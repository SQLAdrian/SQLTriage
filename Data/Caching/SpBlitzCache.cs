/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using Microsoft.Data.Sqlite;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Caching;

/// <summary>
/// SQLite-backed cache for sp_BLITZ findings.
/// All writes are wrapped in a transaction; WAL mode allows concurrent reads.
/// Database file is <c>spblitz.db</c> in the same directory as the main cache DB.
/// </summary>
public sealed class SpBlitzCache : IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public SpBlitzCache()
        : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "spblitz.db"))
    {
    }

    /// <summary>Test seam: construct against an explicit database file path.</summary>
    public SpBlitzCache(string dbPath)
    {
        _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate;";
        InitializeSchema();
    }

    // ── Schema ────────────────────────────────────────────────────────────────

    private void InitializeSchema()
    {
        using var conn = OpenConnection();

        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS blitz_findings (
                import_id       TEXT NOT NULL,
                server_label    TEXT NOT NULL,
                priority        INTEGER NOT NULL,
                findings_group  TEXT NOT NULL,
                finding         TEXT NOT NULL,
                database_name   TEXT NOT NULL DEFAULT '',
                details         TEXT NOT NULL,
                url             TEXT NULL,
                imported_utc    TEXT NOT NULL,
                PRIMARY KEY (import_id, priority, finding, server_label, database_name)
            );

            CREATE INDEX IF NOT EXISTS ix_blitz_server_imported
                ON blitz_findings(server_label, imported_utc DESC);
        ";
        cmd.ExecuteNonQuery();
    }

    // ── Write operations ──────────────────────────────────────────────────────

    /// <summary>
    /// Inserts (or ignores duplicates via PRIMARY KEY) all findings in one transaction.
    /// </summary>
    public async Task SaveAsync(IEnumerable<BlitzFinding> findings, CancellationToken ct = default)
    {
        var list = findings as IList<BlitzFinding> ?? findings.ToList();
        if (list.Count == 0) return;

        await _writeLock.WaitAsync(ct);
        try
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT OR IGNORE INTO blitz_findings
                    (import_id, server_label, priority, findings_group, finding,
                     database_name, details, url, imported_utc)
                VALUES
                    (@import_id, @server_label, @priority, @findings_group, @finding,
                     @database_name, @details, @url, @imported_utc)";

            var pImportId = cmd.Parameters.Add("@import_id", SqliteType.Text);
            var pServerLabel = cmd.Parameters.Add("@server_label", SqliteType.Text);
            var pPriority = cmd.Parameters.Add("@priority", SqliteType.Integer);
            var pFindingsGroup = cmd.Parameters.Add("@findings_group", SqliteType.Text);
            var pFinding = cmd.Parameters.Add("@finding", SqliteType.Text);
            var pDatabaseName = cmd.Parameters.Add("@database_name", SqliteType.Text);
            var pDetails = cmd.Parameters.Add("@details", SqliteType.Text);
            var pUrl = cmd.Parameters.Add("@url", SqliteType.Text);
            var pImportedUtc = cmd.Parameters.Add("@imported_utc", SqliteType.Text);

            foreach (var f in list)
            {
                pImportId.Value = f.ImportId.ToString("D");
                pServerLabel.Value = f.ServerLabel;
                pPriority.Value = f.Priority;
                pFindingsGroup.Value = f.FindingsGroup;
                pFinding.Value = f.Finding;
                pDatabaseName.Value = (object?)f.DatabaseName ?? string.Empty;
                pDetails.Value = f.Details;
                pUrl.Value = (object?)f.Url ?? DBNull.Value;
                pImportedUtc.Value = f.ImportedUtc.ToString("o");
                await cmd.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ── Read operations ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns all findings for a specific server label, ordered by imported time descending.
    /// </summary>
    public async Task<IReadOnlyList<BlitzFinding>> LoadByServerAsync(string serverLabel, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT import_id, server_label, priority, findings_group, finding,
                   database_name, details, url, imported_utc
            FROM   blitz_findings
            WHERE  server_label = @server_label
            ORDER  BY imported_utc DESC, priority ASC";
        cmd.Parameters.AddWithValue("@server_label", serverLabel);
        return await ReadFindingsAsync(cmd, ct);
    }

    /// <summary>
    /// Returns up to <paramref name="maxRows"/> most-recently imported findings across all servers.
    /// </summary>
    public async Task<IReadOnlyList<BlitzFinding>> LoadAllRecentAsync(int maxRows = 1000, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT import_id, server_label, priority, findings_group, finding,
                   database_name, details, url, imported_utc
            FROM   blitz_findings
            ORDER  BY imported_utc DESC, priority ASC
            LIMIT  @max_rows";
        cmd.Parameters.AddWithValue("@max_rows", maxRows);
        return await ReadFindingsAsync(cmd, ct);
    }

    // ── Delete / maintenance ──────────────────────────────────────────────────

    /// <summary>Removes all findings that belong to the given import batch.</summary>
    public async Task DeleteImportAsync(Guid importId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM blitz_findings WHERE import_id = @import_id";
            cmd.Parameters.AddWithValue("@import_id", importId.ToString("D"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Removes all findings imported before <paramref name="cutoffUtc"/> and returns the deleted row count.
    /// </summary>
    /// <remarks>
    /// TODO: wire this into the existing <c>CacheEvictionService</c> housekeeping loop once
    /// the eviction service supports pluggable retention callbacks (Phase 8 candidate).
    /// Until then, callers may invoke it manually from a scheduled task or startup hook.
    /// </remarks>
    public async Task<int> PruneOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM blitz_findings WHERE imported_utc < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoffUtc.ToString("o"));
            return await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<BlitzFinding>> ReadFindingsAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var results = new List<BlitzFinding>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new BlitzFinding
            {
                ImportId = Guid.Parse(reader.GetString(0)),
                ServerLabel = reader.GetString(1),
                Priority = reader.GetInt32(2),
                FindingsGroup = reader.GetString(3),
                Finding = reader.GetString(4),
                DatabaseName = reader.IsDBNull(5) ? null : reader.GetString(5),
                Details = reader.GetString(6),
                Url = reader.IsDBNull(7) ? null : reader.GetString(7),
                ImportedUtc = DateTime.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind)
            });
        }
        return results.AsReadOnly();
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _writeLock.Dispose();
            _disposed = true;
        }
    }
}
