/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SQLTriage.Data;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services;

/// <summary>
/// D3 small build — captures statement-grain snapshots of sys.dm_exec_query_stats
/// into a local SQLite cache so the /code-hotspots page can render deltas
/// ("what executed in the last hour") instead of cumulative-since-plan-cache.
///
/// This slice ships ONLY the snapshot writer + schema. The timer, retention
/// prune, delta math, and UI toggle land in the big build after the writer is
/// verified end-to-end via the DevBridge hand-trigger endpoint.
///
/// Mirrors WaitStatsHistoryService for cipher/journal pragmas + write-lock
/// pattern (see Data/Services/WaitStatsHistoryService.cs).
/// </summary>
public sealed class CodeHotspotsCacheService : IDisposable
{
    private readonly ILogger<CodeHotspotsCacheService> _logger;
    private readonly string _connectionString;
    private readonly object _writeLock = new();
    private readonly TimeSpan _captureInterval;
    private readonly TimeSpan _retention;
    private readonly ServerConnectionManager? _connections;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;
    private bool _disposed;

    public CodeHotspotsCacheService(
        ILogger<CodeHotspotsCacheService> logger,
        ServerConnectionManager? connections = null,
        IConfiguration? configuration = null)
        : this(
            logger,
            Path.Combine(AppContext.BaseDirectory, "code-hotspots-cache.db"),
            connections,
            configuration)
    { }

    /// <summary>Test-seam constructor — lets unit tests point at a temp DB file
    /// so the production cache isn't polluted (per feedback_test_seam_pattern).</summary>
    public CodeHotspotsCacheService(
        ILogger<CodeHotspotsCacheService> logger,
        string dbPath,
        ServerConnectionManager? connections = null,
        IConfiguration? configuration = null)
    {
        _logger = logger;
        _connections = connections;
        _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared";

        var captureMins = configuration?.GetValue("CodeHotspots:CaptureIntervalMinutes", 5) ?? 5;
        var retentionHrs = configuration?.GetValue("CodeHotspots:RetentionHours", 24) ?? 24;
        _captureInterval = TimeSpan.FromMinutes(captureMins > 0 ? captureMins : 5);
        _retention = TimeSpan.FromHours(retentionHrs > 0 ? retentionHrs : 24);

        InitializeSchema();
    }

    /// <summary>Start the background capture loop. Idempotent; safe to call twice.
    /// Called from App startup after DI is ready.</summary>
    public void Start()
    {
        if (_loopTask != null) return;
        if (_connections == null)
        {
            _logger.LogWarning("CodeHotspots cache loop NOT started — no ServerConnectionManager in DI");
            return;
        }
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
        _logger.LogInformation("CodeHotspots cache loop started (every {Mins}m, retention {Hrs}h)",
            _captureInterval.TotalMinutes, _retention.TotalHours);
    }

    /// <summary>Stop the background loop. Called from App shutdown.</summary>
    public void Stop()
    {
        try { _cts.Cancel(); } catch { }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // Immediate first cycle so a delta becomes available after one interval
        // instead of waiting two.
        try { await CaptureAllAsync(ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _logger.LogError(ex, "CodeHotspots initial capture cycle failed"); }

        using var timer = new PeriodicTimer(_captureInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { await CaptureAllAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogError(ex, "CodeHotspots capture cycle failed"); }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task CaptureAllAsync(CancellationToken ct)
    {
        if (_connections == null) return;

        // Retention prune first — cheap and keeps the file bounded even if a
        // later capture fails per-server.
        PruneOld();

        foreach (var conn in _connections.GetEnabledConnections())
        {
            foreach (var server in conn.GetServerList())
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    await CaptureSnapshotAsync(conn, server, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "CodeHotspots capture failed for {Server} — cycle continues",
                        server);
                }
            }
        }
    }

    private void PruneOld()
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(_retention).ToUnixTimeMilliseconds();
        try
        {
            lock (_writeLock)
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM snapshots WHERE captured_at < $cutoff;";
                cmd.Parameters.AddWithValue("$cutoff", cutoff);
                var deleted = cmd.ExecuteNonQuery();
                if (deleted > 0)
                    _logger.LogInformation("CodeHotspots retention prune: removed {N} rows older than {Cutoff}",
                        deleted, cutoff);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CodeHotspots retention prune failed");
        }
    }

    private void InitializeSchema()
    {
        try
        {
            using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA auto_vacuum=INCREMENTAL;

                CREATE TABLE IF NOT EXISTS snapshots (
                    server                  TEXT    NOT NULL,
                    captured_at             INTEGER NOT NULL,
                    sql_handle_hex          TEXT    NOT NULL,
                    statement_start_offset  INTEGER NOT NULL,
                    statement_end_offset    INTEGER NOT NULL,
                    db_id                   INTEGER,
                    db_name                 TEXT,
                    object_id               INTEGER,
                    object_kind             TEXT,
                    schema_qualified        TEXT,
                    snippet_first200        TEXT,
                    execution_count         INTEGER NOT NULL,
                    total_worker_time_us    INTEGER NOT NULL,
                    total_logical_reads     INTEGER NOT NULL,
                    total_logical_writes    INTEGER NOT NULL,
                    PRIMARY KEY (server, captured_at, sql_handle_hex, statement_start_offset)
                ) WITHOUT ROWID;

                CREATE INDEX IF NOT EXISTS idx_snapshots_server_time
                    ON snapshots (server, captured_at);
            ";
            cmd.ExecuteNonQuery();
            _logger.LogInformation("CodeHotspots cache schema initialised");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialise code-hotspots cache schema");
        }
    }

    /// <summary>
    /// Captures one snapshot of the statement-grain DMV view into local SQLite.
    /// Returns the number of rows written. Single-statement DMV query filters
    /// system DBs (dbid > 4) and ad-hoc/cross-DB plans (dbid IS NOT NULL) —
    /// matches CodeHotspotsService.GetDatabasesAsync behaviour. Object name
    /// resolution uses the 2-arg OBJECT_SCHEMA_NAME / OBJECT_NAME overloads
    /// (SQL Server 2012+; project already targets 2014+).
    /// </summary>
    public async Task<int> CaptureSnapshotAsync(ServerConnection conn, string server, CancellationToken ct = default)
    {
        const string dmv = @"
SELECT
    CONVERT(VARCHAR(MAX), qs.sql_handle, 1)    AS sql_handle_hex,
    qs.statement_start_offset,
    qs.statement_end_offset,
    t.dbid                                     AS db_id,
    DB_NAME(t.dbid)                            AS db_name,
    t.objectid                                 AS object_id,
    CASE WHEN t.objectid IS NOT NULL AND t.dbid > 4
         THEN OBJECT_SCHEMA_NAME(t.objectid, t.dbid) + N'.' + OBJECT_NAME(t.objectid, t.dbid)
         ELSE NULL END                         AS schema_qualified,
    LEFT(SUBSTRING(t.text,
                   (qs.statement_start_offset/2)+1,
                   ((CASE qs.statement_end_offset
                         WHEN -1 THEN DATALENGTH(t.text)
                         ELSE qs.statement_end_offset
                     END - qs.statement_start_offset)/2)+1), 200) AS snippet_first200,
    qs.execution_count,
    qs.total_worker_time                       AS total_worker_time_us,
    qs.total_logical_reads,
    qs.total_logical_writes
FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) t
WHERE t.dbid IS NOT NULL AND t.dbid > 4;";

        var capturedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var rows = new List<SnapshotRow>();

        await using (var sql = new SqlConnection(conn.GetConnectionString(server, "master")))
        {
            await sql.OpenAsync(ct);
            await using var cmd = new SqlCommand(dmv, sql) { CommandTimeout = 60 };
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                rows.Add(new SnapshotRow
                {
                    SqlHandleHex = rdr.IsDBNull(0) ? "" : rdr.GetString(0),
                    StatementStartOffset = rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1),
                    StatementEndOffset = rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2),
                    DbId = rdr.IsDBNull(3) ? (int?)null : rdr.GetInt16(3),
                    DbName = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    ObjectId = rdr.IsDBNull(5) ? (int?)null : rdr.GetInt32(5),
                    SchemaQualified = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                    SnippetFirst200 = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                    ExecutionCount = rdr.IsDBNull(8) ? 0 : Convert.ToInt64(rdr.GetValue(8)),
                    TotalWorkerTimeUs = rdr.IsDBNull(9) ? 0 : Convert.ToInt64(rdr.GetValue(9)),
                    TotalLogicalReads = rdr.IsDBNull(10) ? 0 : Convert.ToInt64(rdr.GetValue(10)),
                    TotalLogicalWrites = rdr.IsDBNull(11) ? 0 : Convert.ToInt64(rdr.GetValue(11)),
                });
            }
        }

        if (rows.Count == 0)
        {
            _logger.LogInformation("CodeHotspots snapshot for {Server}: 0 rows (DMV returned empty)", server);
            return 0;
        }

        return WriteSnapshot(server, capturedAt, rows);
    }

    private int WriteSnapshot(string server, long capturedAt, List<SnapshotRow> rows)
    {
        lock (_writeLock)
        {
            using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
            using var tx = conn.BeginTransaction();
            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = @"
                INSERT OR REPLACE INTO snapshots
                  (server, captured_at, sql_handle_hex, statement_start_offset, statement_end_offset,
                   db_id, db_name, object_id, object_kind, schema_qualified, snippet_first200,
                   execution_count, total_worker_time_us, total_logical_reads, total_logical_writes)
                VALUES
                  ($server, $captured_at, $sql_handle_hex, $start_offset, $end_offset,
                   $db_id, $db_name, $object_id, NULL, $schema_qualified, $snippet,
                   $exec_count, $worker_us, $reads, $writes);";

            var pServer = insert.Parameters.Add("$server", SqliteType.Text);
            var pCaptured = insert.Parameters.Add("$captured_at", SqliteType.Integer);
            var pSqlHandle = insert.Parameters.Add("$sql_handle_hex", SqliteType.Text);
            var pStart = insert.Parameters.Add("$start_offset", SqliteType.Integer);
            var pEnd = insert.Parameters.Add("$end_offset", SqliteType.Integer);
            var pDbId = insert.Parameters.Add("$db_id", SqliteType.Integer);
            var pDbName = insert.Parameters.Add("$db_name", SqliteType.Text);
            var pObjectId = insert.Parameters.Add("$object_id", SqliteType.Integer);
            var pSchemaQ = insert.Parameters.Add("$schema_qualified", SqliteType.Text);
            var pSnippet = insert.Parameters.Add("$snippet", SqliteType.Text);
            var pExec = insert.Parameters.Add("$exec_count", SqliteType.Integer);
            var pWorker = insert.Parameters.Add("$worker_us", SqliteType.Integer);
            var pReads = insert.Parameters.Add("$reads", SqliteType.Integer);
            var pWrites = insert.Parameters.Add("$writes", SqliteType.Integer);

            pServer.Value = server;
            pCaptured.Value = capturedAt;

            int written = 0;
            foreach (var r in rows)
            {
                pSqlHandle.Value = r.SqlHandleHex;
                pStart.Value = r.StatementStartOffset;
                pEnd.Value = r.StatementEndOffset;
                pDbId.Value = (object?)r.DbId ?? DBNull.Value;
                pDbName.Value = (object?)r.DbName ?? DBNull.Value;
                pObjectId.Value = (object?)r.ObjectId ?? DBNull.Value;
                pSchemaQ.Value = (object?)r.SchemaQualified ?? DBNull.Value;
                pSnippet.Value = (object?)r.SnippetFirst200 ?? DBNull.Value;
                pExec.Value = r.ExecutionCount;
                pWorker.Value = r.TotalWorkerTimeUs;
                pReads.Value = r.TotalLogicalReads;
                pWrites.Value = r.TotalLogicalWrites;
                written += insert.ExecuteNonQuery();
            }

            tx.Commit();
            _logger.LogInformation("CodeHotspots snapshot for {Server}: wrote {Rows} rows at {Captured}",
                server, written, capturedAt);
            return written;
        }
    }

    // ── Delta query API (consumed by /code-hotspots when ViewMode = "delta") ──

    /// <summary>True when at least two snapshots exist for <paramref name="server"/>
    /// within the retention window — required for a delta computation.</summary>
    public bool HasDelta(string server)
    {
        try
        {
            using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(DISTINCT captured_at) FROM snapshots WHERE server = $server;";
            cmd.Parameters.AddWithValue("$server", server);
            var count = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            return count >= 2;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HasDelta failed for {Server}", server);
            return false;
        }
    }

    /// <summary>Delta row used by the /code-hotspots Delta view. Same shape
    /// regardless of grain (db / object / statement).</summary>
    public sealed record DeltaRow(
        string Key,
        long DeltaExecs,
        double DeltaTotalCpuMs,
        double DeltaAvgCpuMs,
        long DeltaReads,
        long DeltaWrites,
        string? DisplayName,
        string? Kind);

    /// <summary>Delta aggregated to the database grain. Uses the two newest
    /// snapshots in the retention window. Returns empty list if no delta.</summary>
    public List<DeltaRow> ComputeDeltaDatabases(string server)
        => ComputeDelta(server, grain: DeltaGrain.Database, dbFilter: null, objFilter: null);

    /// <summary>Delta aggregated to the object grain, filtered to one database.</summary>
    public List<DeltaRow> ComputeDeltaObjects(string server, string databaseName)
        => ComputeDelta(server, grain: DeltaGrain.Object, dbFilter: databaseName, objFilter: null);

    /// <summary>Delta at the statement grain, filtered to one object inside one database.</summary>
    public List<DeltaRow> ComputeDeltaStatements(string server, string databaseName, string schemaQualifiedObject)
        => ComputeDelta(server, grain: DeltaGrain.Statement, dbFilter: databaseName, objFilter: schemaQualifiedObject);

    private enum DeltaGrain { Database, Object, Statement }

    private List<DeltaRow> ComputeDelta(string server, DeltaGrain grain, string? dbFilter, string? objFilter)
    {
        var pair = GetLatestTwo(server);
        if (pair == null) return new List<DeltaRow>();
        var (s1Time, s2Time) = pair.Value;

        var s1 = LoadSnapshotRows(server, s1Time, dbFilter, objFilter);
        var s2 = LoadSnapshotRows(server, s2Time, dbFilter, objFilter);

        // Skip-first-seen semantics (Adrian 2026-05-26): a key that appears in
        // S2 but not S1 represents a plan compiled between captures. We wait
        // one cycle before showing it so the delta truly means "moved between
        // snapshots". Plan evictions (S1 only) drop silently.
        var s1Lookup = s1.ToDictionary(r => StatementKey(r));
        var deltas = new List<(SnapshotRow Row, long DeltaExecs, double DeltaCpuMs, long DeltaReads, long DeltaWrites)>();
        foreach (var r in s2)
        {
            if (!s1Lookup.TryGetValue(StatementKey(r), out var prev)) continue;
            var dExecs = r.ExecutionCount - prev.ExecutionCount;
            var dCpuMs = (r.TotalWorkerTimeUs - prev.TotalWorkerTimeUs) / 1000.0;
            var dReads = r.TotalLogicalReads - prev.TotalLogicalReads;
            var dWrites = r.TotalLogicalWrites - prev.TotalLogicalWrites;
            // Counter resets (plan recompile under same key) show as negative
            // deltas — drop them; treat as no-activity rather than guessing.
            if (dExecs <= 0 && dCpuMs <= 0 && dReads <= 0 && dWrites <= 0) continue;
            if (dExecs < 0 || dCpuMs < 0 || dReads < 0 || dWrites < 0) continue;
            deltas.Add((r, dExecs, dCpuMs, dReads, dWrites));
        }

        // Aggregate up to the requested grain.
        return grain switch
        {
            DeltaGrain.Database => deltas
                .GroupBy(d => d.Row.DbId ?? -1)
                .Select(g =>
                {
                    var name = g.First().Row.DbName ?? "(unknown)";
                    var totalCpu = g.Sum(x => x.DeltaCpuMs);
                    var totalExecs = g.Sum(x => x.DeltaExecs);
                    return new DeltaRow(
                        Key: name,
                        DeltaExecs: totalExecs,
                        DeltaTotalCpuMs: totalCpu,
                        DeltaAvgCpuMs: totalExecs > 0 ? totalCpu / totalExecs : 0,
                        DeltaReads: g.Sum(x => x.DeltaReads),
                        DeltaWrites: g.Sum(x => x.DeltaWrites),
                        DisplayName: name,
                        Kind: null);
                })
                .OrderByDescending(r => r.DeltaTotalCpuMs)
                .ToList(),

            DeltaGrain.Object => deltas
                .Where(d => d.Row.SchemaQualified != null)
                .GroupBy(d => d.Row.SchemaQualified!)
                .Select(g =>
                {
                    var totalCpu = g.Sum(x => x.DeltaCpuMs);
                    var totalExecs = g.Sum(x => x.DeltaExecs);
                    return new DeltaRow(
                        Key: g.Key,
                        DeltaExecs: totalExecs,
                        DeltaTotalCpuMs: totalCpu,
                        DeltaAvgCpuMs: totalExecs > 0 ? totalCpu / totalExecs : 0,
                        DeltaReads: g.Sum(x => x.DeltaReads),
                        DeltaWrites: g.Sum(x => x.DeltaWrites),
                        DisplayName: g.Key,
                        Kind: null);
                })
                .OrderByDescending(r => r.DeltaTotalCpuMs)
                .ToList(),

            DeltaGrain.Statement => deltas
                .Select(d => new DeltaRow(
                    Key: $"{d.Row.SqlHandleHex}:{d.Row.StatementStartOffset}",
                    DeltaExecs: d.DeltaExecs,
                    DeltaTotalCpuMs: d.DeltaCpuMs,
                    DeltaAvgCpuMs: d.DeltaExecs > 0 ? d.DeltaCpuMs / d.DeltaExecs : 0,
                    DeltaReads: d.DeltaReads,
                    DeltaWrites: d.DeltaWrites,
                    DisplayName: d.Row.SnippetFirst200,
                    Kind: null))
                .OrderByDescending(r => r.DeltaTotalCpuMs)
                .ToList(),

            _ => new List<DeltaRow>()
        };
    }

    private (long S1, long S2)? GetLatestTwo(string server)
    {
        try
        {
            using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT DISTINCT captured_at FROM snapshots
                                WHERE server = $server
                                ORDER BY captured_at DESC LIMIT 2;";
            cmd.Parameters.AddWithValue("$server", server);
            using var rdr = cmd.ExecuteReader();
            long? newest = null, older = null;
            if (rdr.Read()) newest = rdr.GetInt64(0);
            if (rdr.Read()) older = rdr.GetInt64(0);
            return (newest is null || older is null) ? null : (older.Value, newest.Value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetLatestTwo failed for {Server}", server);
            return null;
        }
    }

    private static string StatementKey(SnapshotRow r) =>
        r.SqlHandleHex + "|" + r.StatementStartOffset;

    private List<SnapshotRow> LoadSnapshotRows(string server, long capturedAt, string? dbFilter, string? objFilter)
    {
        var rows = new List<SnapshotRow>();
        using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
        using var cmd = conn.CreateCommand();
        var sql = @"SELECT sql_handle_hex, statement_start_offset, statement_end_offset,
                           db_id, db_name, object_id, schema_qualified, snippet_first200,
                           execution_count, total_worker_time_us, total_logical_reads, total_logical_writes
                    FROM snapshots
                    WHERE server = $server AND captured_at = $captured_at";
        if (dbFilter != null) sql += " AND db_name = $db_name";
        if (objFilter != null) sql += " AND schema_qualified = $schema_qualified";
        sql += ";";
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$server", server);
        cmd.Parameters.AddWithValue("$captured_at", capturedAt);
        if (dbFilter != null) cmd.Parameters.AddWithValue("$db_name", dbFilter);
        if (objFilter != null) cmd.Parameters.AddWithValue("$schema_qualified", objFilter);
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            rows.Add(new SnapshotRow
            {
                SqlHandleHex = rdr.IsDBNull(0) ? "" : rdr.GetString(0),
                StatementStartOffset = rdr.IsDBNull(1) ? 0 : (int)rdr.GetInt64(1),
                StatementEndOffset = rdr.IsDBNull(2) ? 0 : (int)rdr.GetInt64(2),
                DbId = rdr.IsDBNull(3) ? (int?)null : (int)rdr.GetInt64(3),
                DbName = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                ObjectId = rdr.IsDBNull(5) ? (int?)null : (int)rdr.GetInt64(5),
                SchemaQualified = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                SnippetFirst200 = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                ExecutionCount = rdr.IsDBNull(8) ? 0 : rdr.GetInt64(8),
                TotalWorkerTimeUs = rdr.IsDBNull(9) ? 0 : rdr.GetInt64(9),
                TotalLogicalReads = rdr.IsDBNull(10) ? 0 : rdr.GetInt64(10),
                TotalLogicalWrites = rdr.IsDBNull(11) ? 0 : rdr.GetInt64(11)
            });
        }
        return rows;
    }

    /// <summary>Test seam — insert one snapshot row directly into the cache,
    /// bypassing the live DMV. ONLY for unit tests of the delta math. Public so
    /// tests don't need InternalsVisibleTo (per feedback_test_seam_pattern).</summary>
    public void InsertTestRow(string server, long capturedAt, string sqlHandleHex, int startOffset,
        int? dbId, string? dbName, int? objectId, string? schemaQualified, string? snippet,
        long execCount, long workerUs, long reads, long writes)
    {
        WriteSnapshot(server, capturedAt, new List<SnapshotRow>
        {
            new()
            {
                SqlHandleHex = sqlHandleHex,
                StatementStartOffset = startOffset,
                StatementEndOffset = startOffset + 100,
                DbId = dbId,
                DbName = dbName,
                ObjectId = objectId,
                SchemaQualified = schemaQualified,
                SnippetFirst200 = snippet,
                ExecutionCount = execCount,
                TotalWorkerTimeUs = workerUs,
                TotalLogicalReads = reads,
                TotalLogicalWrites = writes
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
        GC.SuppressFinalize(this);
    }

    private sealed class SnapshotRow
    {
        public string SqlHandleHex { get; set; } = "";
        public int StatementStartOffset { get; set; }
        public int StatementEndOffset { get; set; }
        public int? DbId { get; set; }
        public string? DbName { get; set; }
        public int? ObjectId { get; set; }
        public string? SchemaQualified { get; set; }
        public string? SnippetFirst200 { get; set; }
        public long ExecutionCount { get; set; }
        public long TotalWorkerTimeUs { get; set; }
        public long TotalLogicalReads { get; set; }
        public long TotalLogicalWrites { get; set; }
    }
}
