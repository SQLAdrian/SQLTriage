/* In the name of God, the Merciful, the Compassionate */

using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services;

/// <summary>
/// Generates ready-to-review T-SQL maintenance scripts based on live DMV data.
/// Scripts are display-only — no auto-execution. DBA reviews and runs manually.
/// </summary>
public class MaintenanceScriptService
{
    private readonly ILogger<MaintenanceScriptService> _logger;
    private readonly ServerConnectionManager _connectionManager;

    public MaintenanceScriptService(
        ILogger<MaintenanceScriptService> logger,
        ServerConnectionManager connectionManager)
    {
        _logger = logger;
        _connectionManager = connectionManager;
    }

    // ── Result record ────────────────────────────────────────────────────────

    /// <summary>
    /// A generated maintenance script with advisory text.
    /// The script is for DBA review only — never auto-executed.
    /// </summary>
    public record MaintenanceScript(
        string SqlScript,
        string Explanation,
        string RiskNote,
        string RollbackGuidance);

    // ── Connection helper ────────────────────────────────────────────────────

    private string? GetConnectionString(string serverName, string database = "master")
    {
        var conn = _connectionManager.GetEnabledConnections()
            .FirstOrDefault(c => c.GetServerList()
                .Any(s => string.Equals(s, serverName, StringComparison.OrdinalIgnoreCase)));

        if (conn is null)
        {
            _logger.LogWarning("MaintenanceScriptService: no enabled connection found for server '{Server}'", serverName);
            return null;
        }

        return conn.GetConnectionString(serverName, database);
    }

    // ── #1  Index maintenance ────────────────────────────────────────────────

    /// <summary>
    /// Queries sys.dm_db_index_physical_stats across all user databases and
    /// emits REBUILD (Enterprise, avg_fragmentation > threshold) or REORGANIZE
    /// (Standard / fragmentation between 10 % and threshold).
    /// READ-ONLY DMV access — no writes to the monitored server.
    /// </summary>
    public async Task<MaintenanceScript> GenerateIndexMaintenanceScriptAsync(
        string serverName,
        int fragmentationThreshold = 30,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Generating index maintenance script for '{Server}' (threshold {T}%)", serverName, fragmentationThreshold);

        var connStr = GetConnectionString(serverName);
        if (connStr is null)
            return ErrorScript($"No connection found for server '{serverName}'.");

        var sb = new StringBuilder();
        sb.AppendLine("/* ============================================================");
        sb.AppendLine($"   Index Maintenance Script — {serverName}");
        sb.AppendLine($"   Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine("   Review carefully before executing. Do NOT run in production");
        sb.AppendLine("   without first testing in a non-production environment.");
        sb.AppendLine("   ============================================================ */");
        sb.AppendLine();

        // Detect edition so we know whether ONLINE=ON is safe
        bool isEnterprise = false;
        int indexCount = 0;

        try
        {
            await using var masterConn = new SqlConnection(connStr);
            await masterConn.OpenAsync(ct);

            // Edition check
            await using (var cmd = masterConn.CreateCommand())
            {
                cmd.CommandText = "SELECT SERVERPROPERTY('EngineEdition');";
                var edition = await cmd.ExecuteScalarAsync(ct);
                // EngineEdition 3 = Enterprise; 8 = Managed Instance (also supports ONLINE)
                isEnterprise = edition is int e && (e == 3 || e == 8);
            }

            // Gather user databases
            var userDbs = new List<string>();
            await using (var cmd = masterConn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT name
FROM   sys.databases
WHERE  database_id > 4          -- exclude system databases
  AND  state_desc = N'ONLINE'
  AND  is_read_only = 0
ORDER  BY name;";
                await using var rdr = await cmd.ExecuteReaderAsync(ct);
                while (await rdr.ReadAsync(ct))
                    userDbs.Add(rdr.GetString(0));
            }

            if (userDbs.Count == 0)
            {
                sb.AppendLine("-- No online, writable user databases found.");
            }

            foreach (var db in userDbs)
            {
                sb.AppendLine($"-- ── Database: [{db}] ──");
                sb.AppendLine($"USE [{db}];");
                sb.AppendLine("GO");
                sb.AppendLine();

                var dbConnStr = connStr.Replace($"Database=master", $"Database={db}", StringComparison.OrdinalIgnoreCase);
                // Build a fresh connection string pointing at this database
                var dbConnStrBuilder = new SqlConnectionStringBuilder(connStr)
                {
                    InitialCatalog = db
                };

                await using var dbConn = new SqlConnection(dbConnStrBuilder.ConnectionString);
                await dbConn.OpenAsync(ct);

                await using var cmd = dbConn.CreateCommand();
                // Use LIMITED mode for performance; sufficient for fragmentation detection.
                // page_count > 100 filters out tiny indexes where rebuild overhead exceeds benefit.
                cmd.CommandText = @"
SELECT
    QUOTENAME(s.name)  AS schema_name,
    QUOTENAME(t.name)  AS table_name,
    QUOTENAME(i.name)  AS index_name,
    ps.avg_fragmentation_in_percent,
    ps.page_count
FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, N'LIMITED') ps
JOIN sys.indexes      i ON i.object_id = ps.object_id AND i.index_id = ps.index_id
JOIN sys.tables       t ON t.object_id = i.object_id
JOIN sys.schemas      s ON s.schema_id = t.schema_id
WHERE ps.index_id > 0                          -- skip heap scans
  AND ps.page_count > 100                      -- ignore trivially small indexes
  AND ps.avg_fragmentation_in_percent >= 10    -- only fragmented
ORDER BY ps.avg_fragmentation_in_percent DESC;";

                cmd.CommandTimeout = 120;

                bool anyInDb = false;
                await using var rdr = await cmd.ExecuteReaderAsync(ct);
                while (await rdr.ReadAsync(ct))
                {
                    var schema = rdr.GetString(0);
                    var table = rdr.GetString(1);
                    var index = rdr.GetString(2);
                    var frag = rdr.GetDouble(3);

                    anyInDb = true;
                    indexCount++;

                    if (frag >= fragmentationThreshold && isEnterprise)
                    {
                        sb.AppendLine($"-- Fragmentation: {frag:F1}% — REBUILD (Enterprise ONLINE)");
                        sb.AppendLine($"ALTER INDEX {index} ON {schema}.{table} REBUILD WITH (ONLINE = ON, SORT_IN_TEMPDB = ON);");
                    }
                    else if (frag >= fragmentationThreshold)
                    {
                        sb.AppendLine($"-- Fragmentation: {frag:F1}% — REBUILD (Standard edition, ONLINE not available)");
                        sb.AppendLine($"ALTER INDEX {index} ON {schema}.{table} REBUILD WITH (SORT_IN_TEMPDB = ON);");
                    }
                    else
                    {
                        sb.AppendLine($"-- Fragmentation: {frag:F1}% — REORGANIZE");
                        sb.AppendLine($"ALTER INDEX {index} ON {schema}.{table} REORGANIZE;");
                        sb.AppendLine($"UPDATE STATISTICS {schema}.{table} {index};");
                    }
                    sb.AppendLine("GO");
                    sb.AppendLine();
                }

                if (!anyInDb)
                    sb.AppendLine($"-- No indexes in [{db}] exceed 10% fragmentation with > 100 pages.");

                sb.AppendLine();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to generate index maintenance script for '{Server}'", serverName);
            return ErrorScript($"Query failed: {ex.Message}");
        }

        if (indexCount == 0)
            sb.AppendLine("-- No actionable index fragmentation found across all user databases.");

        string editionNote = isEnterprise ? "Enterprise detected — REBUILD WITH (ONLINE = ON) emitted." : "Standard/Express detected — ONLINE = ON omitted (requires Enterprise).";

        return new MaintenanceScript(
            SqlScript: sb.ToString(),
            Explanation: $"Found {indexCount} index(es) with >= 10% fragmentation on {serverName}. {editionNote} REBUILD emitted for >= {fragmentationThreshold}%, REORGANIZE for 10–{fragmentationThreshold - 1}%.",
            RiskNote: "REBUILD acquires a schema-modification lock (SCH-M) briefly at start and end; ONLINE=ON reduces this to milliseconds on Enterprise. Avoid running during peak hours. Test in non-production first.",
            RollbackGuidance: "Index rebuild is not directly reversible. For critical tables, take a pre-maintenance snapshot or backup before executing. Statistics are automatically updated by REBUILD.");
    }

    // ── #2  UPDATE STATISTICS ────────────────────────────────────────────────

    /// <summary>
    /// Finds statistics older than 7 days on tables with > 1000 rows and emits
    /// UPDATE STATISTICS … WITH FULLSCAN statements.
    /// READ-ONLY DMV access — no writes to the monitored server.
    /// </summary>
    public async Task<MaintenanceScript> GenerateStatisticsUpdateScriptAsync(
        string serverName,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Generating statistics update script for '{Server}'", serverName);

        var connStr = GetConnectionString(serverName);
        if (connStr is null)
            return ErrorScript($"No connection found for server '{serverName}'.");

        var sb = new StringBuilder();
        sb.AppendLine("/* ============================================================");
        sb.AppendLine($"   UPDATE STATISTICS Script — {serverName}");
        sb.AppendLine($"   Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine("   Statistics older than 7 days on tables with > 1000 rows.");
        sb.AppendLine("   Review carefully before executing.");
        sb.AppendLine("   ============================================================ */");
        sb.AppendLine();

        int statCount = 0;

        try
        {
            await using var masterConn = new SqlConnection(connStr);
            await masterConn.OpenAsync(ct);

            var userDbs = new List<string>();
            await using (var cmd = masterConn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT name FROM sys.databases
WHERE  database_id > 4
  AND  state_desc = N'ONLINE'
  AND  is_read_only = 0
ORDER  BY name;";
                await using var rdr = await cmd.ExecuteReaderAsync(ct);
                while (await rdr.ReadAsync(ct))
                    userDbs.Add(rdr.GetString(0));
            }

            foreach (var db in userDbs)
            {
                sb.AppendLine($"-- ── Database: [{db}] ──");
                sb.AppendLine($"USE [{db}];");
                sb.AppendLine("GO");
                sb.AppendLine();

                var dbConnStrBuilder = new SqlConnectionStringBuilder(connStr)
                {
                    InitialCatalog = db
                };

                await using var dbConn = new SqlConnection(dbConnStrBuilder.ConnectionString);
                await dbConn.OpenAsync(ct);

                await using var cmd = dbConn.CreateCommand();
                cmd.CommandText = @"
SELECT
    QUOTENAME(s.name)  AS schema_name,
    QUOTENAME(t.name)  AS table_name,
    QUOTENAME(st.name) AS stat_name,
    STATS_DATE(st.object_id, st.stats_id) AS last_updated,
    p.rows
FROM sys.stats st
JOIN sys.tables  t ON t.object_id = st.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN (
    SELECT object_id, SUM(rows) AS rows
    FROM   sys.partitions
    WHERE  index_id IN (0, 1)
    GROUP  BY object_id
) p ON p.object_id = t.object_id
WHERE p.rows > 1000
  AND (
        STATS_DATE(st.object_id, st.stats_id) IS NULL
     OR STATS_DATE(st.object_id, st.stats_id) < DATEADD(day, -7, GETDATE())
  )
ORDER BY STATS_DATE(st.object_id, st.stats_id) ASC;";
                cmd.CommandTimeout = 120;

                bool anyInDb = false;
                await using var rdr = await cmd.ExecuteReaderAsync(ct);
                while (await rdr.ReadAsync(ct))
                {
                    var schema = rdr.GetString(0);
                    var table = rdr.GetString(1);
                    var stat = rdr.GetString(2);
                    var lastUpd = rdr.IsDBNull(3) ? "never" : rdr.GetDateTime(3).ToString("yyyy-MM-dd");
                    var rows = rdr.GetInt64(4);

                    anyInDb = true;
                    statCount++;

                    string hint = rows > 10_000_000 ? " -- large table: consider SAMPLE 30 PERCENT instead of FULLSCAN" : string.Empty;
                    sb.AppendLine($"-- Last updated: {lastUpd} | Rows: {rows:N0}{hint}");
                    sb.AppendLine($"UPDATE STATISTICS {schema}.{table} {stat} WITH FULLSCAN;");
                    sb.AppendLine("GO");
                    sb.AppendLine();
                }

                if (!anyInDb)
                    sb.AppendLine($"-- No stale statistics in [{db}] (all up-to-date or table < 1000 rows).");

                sb.AppendLine();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to generate statistics update script for '{Server}'", serverName);
            return ErrorScript($"Query failed: {ex.Message}");
        }

        if (statCount == 0)
            sb.AppendLine("-- No stale statistics found across all user databases.");

        return new MaintenanceScript(
            SqlScript: sb.ToString(),
            Explanation: $"Found {statCount} statistic(s) older than 7 days on tables with > 1000 rows on {serverName}. Tables > 10M rows are annotated — consider SAMPLE instead of FULLSCAN for those.",
            RiskNote: "FULLSCAN reads every row in the table. On very large tables (> 10M rows) this can be CPU- and I/O-intensive. Schedule during a low-traffic window or use WITH SAMPLE 30 PERCENT for a lighter-weight update.",
            RollbackGuidance: "Statistics updates are not reversible. SQL Server will use the new stats immediately after the UPDATE statement completes. To roll back, restore from backup or use DBCC UPDATEUSAGE with a prior value — generally not required.");
    }

    // ── #3  DBCC CHECKDB schedule ────────────────────────────────────────────

    /// <summary>
    /// Emits one DBCC CHECKDB … WITH PHYSICAL_ONLY statement per user database.
    /// READ-ONLY DMV access — no writes to the monitored server.
    /// </summary>
    public async Task<MaintenanceScript> GenerateCheckDbScriptAsync(
        string serverName,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Generating DBCC CHECKDB script for '{Server}'", serverName);

        var connStr = GetConnectionString(serverName);
        if (connStr is null)
            return ErrorScript($"No connection found for server '{serverName}'.");

        var sb = new StringBuilder();
        sb.AppendLine("/* ============================================================");
        sb.AppendLine($"   DBCC CHECKDB Script — {serverName}");
        sb.AppendLine($"   Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine("   Run during a scheduled maintenance window.");
        sb.AppendLine("   PHYSICAL_ONLY is fast; add full CHECKDB monthly.");
        sb.AppendLine("   ============================================================ */");
        sb.AppendLine();

        int dbCount = 0;

        try
        {
            await using var masterConn = new SqlConnection(connStr);
            await masterConn.OpenAsync(ct);

            await using var cmd = masterConn.CreateCommand();
            cmd.CommandText = @"
SELECT name FROM sys.databases
WHERE  database_id > 4
  AND  state_desc = N'ONLINE'
ORDER  BY name;";

            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var db = rdr.GetString(0);
                dbCount++;
                sb.AppendLine($"-- [{db}]: physical integrity check");
                sb.AppendLine($"DBCC CHECKDB ([{db}]) WITH PHYSICAL_ONLY, NO_INFOMSGS;");
                sb.AppendLine("GO");
                sb.AppendLine();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to generate CHECKDB script for '{Server}'", serverName);
            return ErrorScript($"Query failed: {ex.Message}");
        }

        if (dbCount == 0)
            sb.AppendLine("-- No online user databases found.");

        return new MaintenanceScript(
            SqlScript: sb.ToString(),
            Explanation: $"Generated DBCC CHECKDB WITH PHYSICAL_ONLY for {dbCount} user database(s) on {serverName}.",
            RiskNote: "PHYSICAL_ONLY validates page structures and record headers but skips logical consistency checks. Run a full DBCC CHECKDB (without PHYSICAL_ONLY) at least once per month during a maintenance window. CHECKDB may increase I/O load significantly on large databases.",
            RollbackGuidance: "DBCC CHECKDB is read-only and non-destructive. It will not modify any data. If errors are reported, consult the SQL Server error log and plan a restore from a known-good backup before any repair attempt.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static MaintenanceScript ErrorScript(string message) =>
        new(
            SqlScript: $"-- ERROR: {message}",
            Explanation: message,
            RiskNote: string.Empty,
            RollbackGuidance: string.Empty);
}
