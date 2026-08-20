/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Changed-objects detection (client-needs Q3). Per scan, captures a per-database
    /// inventory of user objects (procs, functions, views, triggers, tables) — name,
    /// schema, type, create/modify dates, and a definition hash — then diffs the current
    /// inventory against the stored baseline into Created / Altered / Dropped, dated, and
    /// finally rolls the baseline forward. This is the baseline-diff ENGINE only; there is
    /// deliberately no review/approval workflow in this slice.
    ///
    /// Collection is READ-ONLY: a single set-based query per database (no per-object
    /// round-trips — vendor estates are big). Encrypted / permission-denied module
    /// definitions (OBJECT_DEFINITION returns NULL) are stored with an honest
    /// "<see cref="EncryptedMarker"/>" marker rather than a fabricated hash; the
    /// modify_date still catches alterations to those objects.
    ///
    /// Storage: SQLCipher-encrypted SQLite at Data/changed-objects.db (mirrors
    /// <see cref="ServerConfigBaselineService"/> / <see cref="AcceptedFindingsService"/>),
    /// keyed (server, database, schema, object). All writes are serialised through a
    /// single semaphore so the parallel multi-server scan pipeline can call
    /// <see cref="ScanServerAsync"/> concurrently without corrupting the store.
    ///
    /// First run is honest: the very first scan of a (server, database) captures the
    /// baseline and emits NO change rows (there is nothing to diff against yet). The
    /// UI shows "Baseline captured this scan — differences will appear from the next scan".
    /// </summary>
    public sealed class ChangedObjectsService : IDisposable
    {
        /// <summary>Stored in place of a definition hash when the module definition is not
        /// hashable (WITH ENCRYPTION, or the login lacks VIEW DEFINITION). Never a fake hash.</summary>
        public const string EncryptedMarker = "encrypted, definition not hashable";

        private readonly ILogger<ChangedObjectsService> _logger;
        private readonly string _connectionString;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private bool _disposed;

        public ChangedObjectsService(
            ILogger<ChangedObjectsService> logger,
            string? dbPath = null)
        {
            _logger = logger;

            var path = dbPath ?? Path.Combine(AppContext.BaseDirectory, "Data", "changed-objects.db");
            var dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _connectionString = $"Data Source={path};Mode=ReadWriteCreate;Cache=Shared";
            InitializeSchema();
        }

        // ── Schema ────────────────────────────────────────────────────────

        private void InitializeSchema()
        {
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=NORMAL;

                    -- The rolling baseline: one row per live object, keyed (server, db, schema, object).
                    CREATE TABLE IF NOT EXISTS object_baseline (
                        server_name     TEXT NOT NULL,
                        database_name   TEXT NOT NULL,
                        schema_name     TEXT NOT NULL,
                        object_name     TEXT NOT NULL,
                        object_type     TEXT NOT NULL,
                        create_date     TEXT,
                        modify_date     TEXT,
                        definition_hash TEXT,
                        first_seen_utc  TEXT NOT NULL,
                        last_seen_utc   TEXT NOT NULL,
                        PRIMARY KEY (server_name, database_name, schema_name, object_name)
                    );

                    -- The dated change log: one row per detected Created/Altered/Dropped event.
                    CREATE TABLE IF NOT EXISTS object_change_log (
                        id              INTEGER PRIMARY KEY AUTOINCREMENT,
                        server_name     TEXT NOT NULL,
                        database_name   TEXT NOT NULL,
                        schema_name     TEXT NOT NULL,
                        object_name     TEXT NOT NULL,
                        object_type     TEXT NOT NULL,
                        change_type     TEXT NOT NULL,
                        detected_utc    TEXT NOT NULL,
                        old_modify_date TEXT,
                        new_modify_date TEXT
                    );
                    CREATE INDEX IF NOT EXISTS idx_change_log_server_db
                        ON object_change_log(server_name, database_name, detected_utc DESC);

                    -- Per (server, db) scan bookkeeping — drives the honest first-run message.
                    CREATE TABLE IF NOT EXISTS object_scan_meta (
                        server_name    TEXT NOT NULL,
                        database_name  TEXT NOT NULL,
                        first_scan_utc TEXT NOT NULL,
                        last_scan_utc  TEXT NOT NULL,
                        scan_count     INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY (server_name, database_name)
                    );
                ";
                cmd.ExecuteNonQuery();

                // Additive, idempotent: a per-(server,db) note recording the last scan's honest status
                // (e.g. "inventory unavailable this scan"). Stores predating this column get it added
                // once; SQLite has no ADD COLUMN IF NOT EXISTS, so probe pragma_table_info first.
                using (var colCheck = conn.CreateCommand())
                {
                    colCheck.CommandText =
                        "SELECT COUNT(*) FROM pragma_table_info('object_scan_meta') WHERE name = 'last_scan_note';";
                    var hasNote = Convert.ToInt64(colCheck.ExecuteScalar() ?? 0L) > 0;
                    if (!hasNote)
                    {
                        using var alter = conn.CreateCommand();
                        alter.CommandText = "ALTER TABLE object_scan_meta ADD COLUMN last_scan_note TEXT;";
                        alter.ExecuteNonQuery();
                    }
                }

                _logger.LogInformation("[CHANGED-OBJECTS] Schema initialised");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CHANGED-OBJECTS] Schema initialisation failed");
            }
        }

        // ── Collection SQL (READ-ONLY) ────────────────────────────────────

        // Accessible, ONLINE user databases only. HAS_DBACCESS filters databases the current
        // login cannot use (e.g. a non-readable AG secondary); database snapshots are skipped.
        // NOTE: HASHBYTES over the full nvarchar(max) OBJECT_DEFINITION assumes SQL Server 2016+
        // (pre-2016 HASHBYTES silently truncates its input at 8000 bytes). The app targets modern
        // SQL Server, so large module bodies hash in full.
        private const string DatabaseListSql = @"
SELECT name
FROM sys.databases
WHERE database_id > 4
  AND state = 0
  AND source_database_id IS NULL
  AND HAS_DBACCESS(name) = 1
ORDER BY name;";

        // One set-based pass over sys.objects for a single database. Module objects hash their
        // OBJECT_DEFINITION; tables (type 'U') hash their ordered column shape (name/type/size/
        // nullability/identity/computed) since they have no textual definition. FOR XML PATH keeps
        // this compatible back to SQL 2016 (STRING_AGG would raise the floor to 2017).
        private const string InventorySql = @"
SELECT
    s.name AS SchemaName,
    o.name AS ObjectName,
    o.type_desc AS ObjectType,
    o.create_date AS CreateDate,
    o.modify_date AS ModifyDate,
    CASE
        WHEN o.type = N'U' THEN
            CONVERT(char(64), HASHBYTES('SHA2_256', CONVERT(nvarchar(max), ISNULL(tc.shape, N''))), 2)
        WHEN OBJECT_DEFINITION(o.object_id) IS NULL THEN NULL
        ELSE CONVERT(char(64), HASHBYTES('SHA2_256', OBJECT_DEFINITION(o.object_id)), 2)
    END AS DefinitionHash
FROM sys.objects o
JOIN sys.schemas s ON o.schema_id = s.schema_id
OUTER APPLY (
    SELECT (
        SELECT CONVERT(nvarchar(max),
                 CONCAT(c.column_id, N':', c.name, N':', TYPE_NAME(c.user_type_id), N':',
                        c.max_length, N':', c.precision, N':', c.scale, N':',
                        c.is_nullable, N':', c.is_identity, N':', c.is_computed, N'|'))
        FROM sys.columns c
        WHERE c.object_id = o.object_id AND o.type = N'U'
        ORDER BY c.column_id
        FOR XML PATH(''), TYPE
    ).value('.', 'nvarchar(max)') AS shape
) tc
WHERE o.is_ms_shipped = 0
  AND o.type IN (N'U', N'V', N'P', N'FN', N'IF', N'TF', N'TR')
ORDER BY s.name, o.name;";

        // ── Public API ────────────────────────────────────────────────────

        /// <summary>
        /// Collects the object inventory for every accessible user database on
        /// <paramref name="serverName"/>, diffs it against the stored baseline, records the
        /// dated Created/Altered/Dropped changes, and rolls the baseline forward.
        ///
        /// NEVER throws — a fault on one database is logged and skipped so the wider scan
        /// pipeline is never interrupted. <paramref name="masterConnectionString"/> is a
        /// connection string pointed at any accessible database (typically master); the
        /// per-database catalog is swapped via <see cref="SqlConnectionStringBuilder"/>.
        /// </summary>
        public async Task ScanServerAsync(
            string serverName,
            string masterConnectionString,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(masterConnectionString))
                return;

            List<string> databases;
            try
            {
                databases = await ListAccessibleDatabasesAsync(masterConnectionString, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CHANGED-OBJECTS] Could not enumerate databases for {Server}; skipping", serverName);
                return;
            }

            var changedTotal = 0;
            foreach (var db in databases)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var current = await CollectDatabaseInventoryAsync(masterConnectionString, db, ct);
                    var changes = await DiffAndPersistAsync(serverName, db, current);
                    changedTotal += changes;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // One inaccessible/odd database must not sink the rest of the server.
                    _logger.LogWarning(ex, "[CHANGED-OBJECTS] Inventory failed for {Server}/{Db}; skipping database", serverName, db);
                }
            }

            _logger.LogInformation("[CHANGED-OBJECTS] Scanned {DbCount} database(s) on {Server}; {Changes} object change(s) recorded",
                databases.Count, serverName, changedTotal);
        }

        /// <summary>
        /// Returns the per-database change views for the UI, one per scanned (server, database),
        /// newest changes first, capped at <paramref name="maxPerDatabase"/> change rows each.
        /// Databases scanned but with no recorded changes are still returned (empty change list)
        /// so the page can show their honest state.
        /// </summary>
        public async Task<IReadOnlyList<ChangedObjectsDbView>> GetChangesAsync(int maxPerDatabase = 500)
        {
            var views = new List<ChangedObjectsDbView>();
            try
            {
                using var conn = await SqliteCipherHelper.OpenEncryptedAsync(_connectionString);

                // 1. Scan bookkeeping — one entry per scanned (server, db).
                var meta = new List<(string Server, string Db, DateTime First, DateTime Last, long Count, string? Note)>();
                using (var metaCmd = conn.CreateCommand())
                {
                    metaCmd.CommandText = @"
                        SELECT server_name, database_name, first_scan_utc, last_scan_utc, scan_count, last_scan_note
                        FROM object_scan_meta
                        ORDER BY server_name, database_name;";
                    using var reader = await metaCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        meta.Add((
                            reader.GetString(0),
                            reader.GetString(1),
                            ParseUtc(reader.GetString(2)),
                            ParseUtc(reader.GetString(3)),
                            reader.GetInt64(4),
                            reader.IsDBNull(5) ? null : reader.GetString(5)));
                    }
                }

                // 2. Change log grouped by (server, db), newest first.
                var byDb = new Dictionary<string, List<ObjectChange>>(StringComparer.OrdinalIgnoreCase);
                using (var logCmd = conn.CreateCommand())
                {
                    logCmd.CommandText = @"
                        SELECT server_name, database_name, schema_name, object_name, object_type,
                               change_type, detected_utc, old_modify_date, new_modify_date
                        FROM object_change_log
                        ORDER BY detected_utc DESC, id DESC;";
                    using var reader = await logCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var server = reader.GetString(0);
                        var db = reader.GetString(1);
                        var key = GroupKey(server, db);
                        if (!byDb.TryGetValue(key, out var list))
                        {
                            list = new List<ObjectChange>();
                            byDb[key] = list;
                        }
                        if (list.Count >= maxPerDatabase) continue; // already newest-first; cap per db
                        list.Add(new ObjectChange(
                            SchemaName: reader.GetString(2),
                            ObjectName: reader.GetString(3),
                            ObjectType: reader.GetString(4),
                            ChangeType: reader.GetString(5),
                            DetectedUtc: ParseUtc(reader.GetString(6)),
                            OldModifyDate: reader.IsDBNull(7) ? null : reader.GetString(7),
                            NewModifyDate: reader.IsDBNull(8) ? null : reader.GetString(8)));
                    }
                }

                foreach (var m in meta)
                {
                    byDb.TryGetValue(GroupKey(m.Server, m.Db), out var changes);
                    views.Add(new ChangedObjectsDbView(
                        ServerName: m.Server,
                        DatabaseName: m.Db,
                        FirstScanUtc: m.First,
                        LastScanUtc: m.Last,
                        ScanCount: m.Count,
                        Changes: changes ?? new List<ObjectChange>(),
                        ScanNote: m.Note));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CHANGED-OBJECTS] GetChangesAsync failed");
            }
            return views;
        }

        // ── Collection helpers ────────────────────────────────────────────

        private static async Task<List<string>> ListAccessibleDatabasesAsync(
            string masterConnectionString, CancellationToken ct)
        {
            var names = new List<string>();
            using var conn = new SqlConnection(masterConnectionString);
            await conn.OpenAsync(ct);
            using var cmd = new SqlCommand(DatabaseListSql, conn) { CommandTimeout = 30 };
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                names.Add(reader.GetString(0));
            return names;
        }

        private static async Task<List<ObjectInventoryRow>> CollectDatabaseInventoryAsync(
            string masterConnectionString, string databaseName, CancellationToken ct)
        {
            // Re-point the connection string at the target database — the inventory query is
            // catalog-scoped (sys.objects / OBJECT_DEFINITION resolve in the current database).
            var builder = new SqlConnectionStringBuilder(masterConnectionString)
            {
                InitialCatalog = databaseName
            };

            var rows = new List<ObjectInventoryRow>();
            using var conn = new SqlConnection(builder.ConnectionString);
            await conn.OpenAsync(ct);
            using var cmd = new SqlCommand(InventorySql, conn) { CommandTimeout = 120 };
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var schema = reader.GetString(0);
                var name = reader.GetString(1);
                var typeDesc = reader.GetString(2);
                var createDate = reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3);
                var modifyDate = reader.IsDBNull(4) ? (DateTime?)null : reader.GetDateTime(4);
                // A NULL hash on a module object means "not hashable" (encrypted / no VIEW DEFINITION):
                // store the honest marker, never a fabricated value. Tables always produce a shape hash.
                var hash = reader.IsDBNull(5) ? EncryptedMarker : reader.GetString(5);

                rows.Add(new ObjectInventoryRow(schema, name, typeDesc, createDate, modifyDate, hash));
            }
            return rows;
        }

        // ── Diff + persist ────────────────────────────────────────────────

        /// <summary>
        /// Diffs <paramref name="current"/> against the stored baseline for (server, db) and, when
        /// this is not the first scan, records the dated Created/Altered/Dropped changes. Always
        /// rolls the baseline forward and bumps the scan bookkeeping. Returns the change count.
        /// </summary>
        private async Task<int> DiffAndPersistAsync(
            string serverName, string databaseName, List<ObjectInventoryRow> current)
        {
            var nowUtc = DateTime.UtcNow;
            var nowIso = nowUtc.ToString("o");
            var changeCount = 0;

            await _writeLock.WaitAsync();
            try
            {
                using var conn = await SqliteCipherHelper.OpenEncryptedAsync(_connectionString);
                using var tx = conn.BeginTransaction();

                // Is this the first time we have scanned this (server, db)?
                bool firstScan;
                using (var metaProbe = conn.CreateCommand())
                {
                    metaProbe.Transaction = tx;
                    metaProbe.CommandText = "SELECT 1 FROM object_scan_meta WHERE server_name = $s AND database_name = $d LIMIT 1;";
                    metaProbe.Parameters.AddWithValue("$s", serverName);
                    metaProbe.Parameters.AddWithValue("$d", databaseName);
                    firstScan = await metaProbe.ExecuteScalarAsync() == null;
                }

                // Load the existing baseline into memory, keyed (schema|object).
                var baseline = new Dictionary<string, ObjectInventoryRow>(StringComparer.OrdinalIgnoreCase);
                using (var loadCmd = conn.CreateCommand())
                {
                    loadCmd.Transaction = tx;
                    loadCmd.CommandText = @"
                        SELECT schema_name, object_name, object_type, create_date, modify_date, definition_hash
                        FROM object_baseline
                        WHERE server_name = $s AND database_name = $d;";
                    loadCmd.Parameters.AddWithValue("$s", serverName);
                    loadCmd.Parameters.AddWithValue("$d", databaseName);
                    using var reader = await loadCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var row = new ObjectInventoryRow(
                            reader.GetString(0),
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.IsDBNull(3) ? null : ParseUtcNullable(reader.GetString(3)),
                            reader.IsDBNull(4) ? null : ParseUtcNullable(reader.GetString(4)),
                            reader.IsDBNull(5) ? null : reader.GetString(5));
                        baseline[ObjectKey(row.SchemaName, row.ObjectName)] = row;
                    }
                }

                // Guard against a metadata-visibility loss being misread as a mass drop. If the current
                // inventory has collapsed to empty (or a tiny fraction) while the baseline is populated,
                // the login almost certainly lost catalog / VIEW DEFINITION visibility this scan — NOT
                // that (nearly) every object was dropped. Wiping the baseline and firing a Dropped event
                // per object would be destructive and dishonest, so on this signal we skip the diff AND
                // the baseline roll-forward entirely, preserve the store untouched, and record an honest
                // "inventory unavailable" note. A genuine mass-drop is at worst delayed one scan and
                // surfaced for verification — recoverable — whereas a wiped baseline is not.
                bool inventoryUnavailable = !firstScan
                    && baseline.Count >= 5
                    && current.Count * 4 < baseline.Count;   // collapsed below 25% of a populated baseline

                string? scanNote = null;

                // On subsequent scans, compute the three-bucket diff and log each change.
                if (!firstScan && !inventoryUnavailable)
                {
                    var currentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var cur in current)
                    {
                        var key = ObjectKey(cur.SchemaName, cur.ObjectName);
                        currentKeys.Add(key);

                        if (!baseline.TryGetValue(key, out var prev))
                        {
                            changeCount += await LogChangeAsync(conn, tx, serverName, databaseName, cur,
                                "Created", nowIso, oldModify: null, newModify: IsoOrNull(cur.ModifyDate));
                        }
                        else if (HasChanged(prev, cur))
                        {
                            changeCount += await LogChangeAsync(conn, tx, serverName, databaseName, cur,
                                "Altered", nowIso, oldModify: IsoOrNull(prev.ModifyDate), newModify: IsoOrNull(cur.ModifyDate));
                        }
                    }

                    foreach (var prev in baseline.Values)
                    {
                        if (!currentKeys.Contains(ObjectKey(prev.SchemaName, prev.ObjectName)))
                        {
                            changeCount += await LogChangeAsync(conn, tx, serverName, databaseName, prev,
                                "Dropped", nowIso, oldModify: IsoOrNull(prev.ModifyDate), newModify: null);
                        }
                    }
                }

                if (inventoryUnavailable)
                {
                    // Preserve the baseline exactly as-is (no roll-forward, no drops). Record why.
                    scanNote = $"Inventory unavailable this scan — only {current.Count} of {baseline.Count} baselined " +
                               "object(s) were visible (the login likely lost catalog / VIEW DEFINITION access). " +
                               "Baseline preserved and no changes recorded; re-scan once access is restored.";
                    _logger.LogWarning(
                        "[CHANGED-OBJECTS] {Server}/{Db}: inventory collapsed to {Cur}/{Base} objects — skipping drop pass, preserving baseline",
                        serverName, databaseName, current.Count, baseline.Count);
                }
                else
                {
                    // Roll the baseline forward: upsert every current object, drop the vanished ones.
                    await ReplaceBaselineAsync(conn, tx, serverName, databaseName, current, baseline, nowIso);
                }

                // Bump the scan bookkeeping (first_scan_utc set once; last_scan_utc + count always) and
                // set/clear the honest last-scan note ($note is NULL on a normal scan, clearing a stale
                // "unavailable" warning once access is restored).
                using (var metaCmd = conn.CreateCommand())
                {
                    metaCmd.Transaction = tx;
                    metaCmd.CommandText = @"
                        INSERT INTO object_scan_meta (server_name, database_name, first_scan_utc, last_scan_utc, scan_count, last_scan_note)
                        VALUES ($s, $d, $now, $now, 1, $note)
                        ON CONFLICT(server_name, database_name)
                        DO UPDATE SET last_scan_utc = $now, scan_count = scan_count + 1, last_scan_note = $note;";
                    metaCmd.Parameters.AddWithValue("$s", serverName);
                    metaCmd.Parameters.AddWithValue("$d", databaseName);
                    metaCmd.Parameters.AddWithValue("$now", nowIso);
                    metaCmd.Parameters.AddWithValue("$note", (object?)scanNote ?? DBNull.Value);
                    await metaCmd.ExecuteNonQueryAsync();
                }

                tx.Commit();
            }
            finally
            {
                _writeLock.Release();
            }

            return changeCount;
        }

        private static async Task<int> LogChangeAsync(
            SqliteConnection conn, SqliteTransaction tx,
            string serverName, string databaseName, ObjectInventoryRow obj,
            string changeType, string detectedUtc, string? oldModify, string? newModify)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO object_change_log
                    (server_name, database_name, schema_name, object_name, object_type,
                     change_type, detected_utc, old_modify_date, new_modify_date)
                VALUES ($s, $d, $sc, $o, $t, $ct, $at, $om, $nm);";
            cmd.Parameters.AddWithValue("$s", serverName);
            cmd.Parameters.AddWithValue("$d", databaseName);
            cmd.Parameters.AddWithValue("$sc", obj.SchemaName);
            cmd.Parameters.AddWithValue("$o", obj.ObjectName);
            cmd.Parameters.AddWithValue("$t", obj.ObjectType);
            cmd.Parameters.AddWithValue("$ct", changeType);
            cmd.Parameters.AddWithValue("$at", detectedUtc);
            cmd.Parameters.AddWithValue("$om", (object?)oldModify ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$nm", (object?)newModify ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
            return 1;
        }

        private static async Task ReplaceBaselineAsync(
            SqliteConnection conn, SqliteTransaction tx,
            string serverName, string databaseName,
            List<ObjectInventoryRow> current,
            Dictionary<string, ObjectInventoryRow> baseline,
            string nowIso)
        {
            var currentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Upsert every current object. first_seen_utc is preserved on conflict; last_seen bumps.
            using (var upsert = conn.CreateCommand())
            {
                upsert.Transaction = tx;
                upsert.CommandText = @"
                    INSERT INTO object_baseline
                        (server_name, database_name, schema_name, object_name, object_type,
                         create_date, modify_date, definition_hash, first_seen_utc, last_seen_utc)
                    VALUES ($s, $d, $sc, $o, $t, $cd, $md, $h, $now, $now)
                    ON CONFLICT(server_name, database_name, schema_name, object_name)
                    DO UPDATE SET object_type = $t, create_date = $cd, modify_date = $md,
                                  definition_hash = $h, last_seen_utc = $now;";
                // Parameters declared once; values re-bound per row.
                var pS = upsert.Parameters.Add("$s", SqliteType.Text);
                var pD = upsert.Parameters.Add("$d", SqliteType.Text);
                var pSc = upsert.Parameters.Add("$sc", SqliteType.Text);
                var pO = upsert.Parameters.Add("$o", SqliteType.Text);
                var pT = upsert.Parameters.Add("$t", SqliteType.Text);
                var pCd = upsert.Parameters.Add("$cd", SqliteType.Text);
                var pMd = upsert.Parameters.Add("$md", SqliteType.Text);
                var pH = upsert.Parameters.Add("$h", SqliteType.Text);
                upsert.Parameters.AddWithValue("$now", nowIso);

                foreach (var cur in current)
                {
                    currentKeys.Add(ObjectKey(cur.SchemaName, cur.ObjectName));
                    pS.Value = serverName;
                    pD.Value = databaseName;
                    pSc.Value = cur.SchemaName;
                    pO.Value = cur.ObjectName;
                    pT.Value = cur.ObjectType;
                    pCd.Value = (object?)IsoOrNull(cur.CreateDate) ?? DBNull.Value;
                    pMd.Value = (object?)IsoOrNull(cur.ModifyDate) ?? DBNull.Value;
                    pH.Value = (object?)cur.DefinitionHash ?? DBNull.Value;
                    await upsert.ExecuteNonQueryAsync();
                }
            }

            // Delete baseline rows no longer present (dropped objects).
            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = @"
                    DELETE FROM object_baseline
                    WHERE server_name = $s AND database_name = $d
                      AND schema_name = $sc AND object_name = $o;";
                var pS = del.Parameters.Add("$s", SqliteType.Text);
                var pD = del.Parameters.Add("$d", SqliteType.Text);
                var pSc = del.Parameters.Add("$sc", SqliteType.Text);
                var pO = del.Parameters.Add("$o", SqliteType.Text);

                foreach (var prev in baseline.Values)
                {
                    if (currentKeys.Contains(ObjectKey(prev.SchemaName, prev.ObjectName))) continue;
                    pS.Value = serverName;
                    pD.Value = databaseName;
                    pSc.Value = prev.SchemaName;
                    pO.Value = prev.ObjectName;
                    await del.ExecuteNonQueryAsync();
                }
            }
        }

        // ── Small helpers ─────────────────────────────────────────────────

        /// <summary>An object is Altered when its modify_date OR its definition hash moved.</summary>
        private static bool HasChanged(ObjectInventoryRow prev, ObjectInventoryRow cur)
        {
            var modifyChanged = !string.Equals(IsoOrNull(prev.ModifyDate), IsoOrNull(cur.ModifyDate), StringComparison.Ordinal);
            var hashChanged = !string.Equals(prev.DefinitionHash, cur.DefinitionHash, StringComparison.OrdinalIgnoreCase);
            return modifyChanged || hashChanged;
        }

        // SQL object names are case-insensitive under the default collation; key accordingly so a
        // case-only rename is not misread as drop+create.
        private static string ObjectKey(string schema, string name) => schema + "." + name;

        private static string GroupKey(string server, string db) => string.Join((char)0x1F, server, db);

        private static string? IsoOrNull(DateTime? dt) => dt?.ToString("o");

        private static DateTime ParseUtc(string iso) =>
            DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
                ? dt : DateTime.MinValue;

        private static DateTime? ParseUtcNullable(string iso) =>
            DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
                ? dt : (DateTime?)null;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _writeLock.Dispose();
        }
    }

    // ── DTOs ──────────────────────────────────────────────────────────────

    /// <summary>One row of the per-database object inventory (collection + baseline).</summary>
    public sealed record ObjectInventoryRow(
        string SchemaName,
        string ObjectName,
        string ObjectType,
        DateTime? CreateDate,
        DateTime? ModifyDate,
        string? DefinitionHash);

    /// <summary>One dated Created / Altered / Dropped change for the UI.</summary>
    public sealed record ObjectChange(
        string SchemaName,
        string ObjectName,
        string ObjectType,
        string ChangeType,
        DateTime DetectedUtc,
        string? OldModifyDate,
        string? NewModifyDate);

    /// <summary>Per-(server, database) change view for the /changed-objects page.</summary>
    public sealed record ChangedObjectsDbView(
        string ServerName,
        string DatabaseName,
        DateTime FirstScanUtc,
        DateTime LastScanUtc,
        long ScanCount,
        IReadOnlyList<ObjectChange> Changes,
        string? ScanNote = null)
    {
        /// <summary>True when this database has only ever been scanned once — the baseline was just
        /// captured and there is nothing to diff against yet (honest first-run state).</summary>
        public bool IsBaselineOnly => ScanCount <= 1;

        /// <summary>Honest status from the most recent scan — non-null only when that scan could not
        /// enumerate the inventory (e.g. lost catalog / VIEW DEFINITION access), so the baseline was
        /// preserved and no changes were recorded rather than firing a false mass-drop.</summary>
        public bool HasScanNote => !string.IsNullOrWhiteSpace(ScanNote);
    }
}
