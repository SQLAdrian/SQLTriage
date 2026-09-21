/* In the name of God, the Merciful, the Compassionate */
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services;

/// <summary>
/// Live DMV queries powering the /code-hotspots page.
/// Three drill levels: databases → objects → statements, all sourced from
/// sys.dm_exec_query_stats joined to sys.dm_exec_sql_text.
/// </summary>
public sealed class CodeHotspotsService
{
    private readonly ILogger<CodeHotspotsService>? _log;

    public CodeHotspotsService(ILogger<CodeHotspotsService>? log = null)
    {
        _log = log;
    }

    public sealed class DbRow
    {
        public string Name { get; set; } = "";
        public int Objects { get; set; }
        public long Execs { get; set; }
        public double TotalCpuMs { get; set; }
        public double AvgCpuMs { get; set; }
        public long Reads { get; set; }
        public long Writes { get; set; }
    }

    public sealed class ObjRow
    {
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public int Statements { get; set; }
        public long Execs { get; set; }
        public double TotalCpuMs { get; set; }
        public double AvgCpuMs { get; set; }
        public long Reads { get; set; }
        public long Writes { get; set; }
    }

    public sealed class StatementRow
    {
        public string Name { get; set; } = "";
        public string Snippet { get; set; } = "";
        public long Execs { get; set; }
        public double TotalCpuMs { get; set; }
        public double AvgCpuMs { get; set; }
        public long Reads { get; set; }
        public long Writes { get; set; }
        public double PctOfTotal { get; set; }
    }

    public async Task<List<DbRow>> GetDatabasesAsync(ServerConnection conn, string server, CancellationToken ct = default)
    {
        const string sql = @"
SELECT
    DB_NAME(t.dbid) AS db,
    COUNT(DISTINCT t.objectid) AS objs,
    SUM(qs.execution_count) AS execs,
    SUM(qs.total_worker_time) / 1000.0 AS total_cpu_ms,
    SUM(qs.total_worker_time) / NULLIF(SUM(qs.execution_count), 0) / 1000.0 AS avg_cpu_ms,
    SUM(qs.total_logical_reads) AS reads,
    SUM(qs.total_logical_writes) AS writes
FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) t
WHERE t.dbid IS NOT NULL AND t.dbid > 4
GROUP BY t.dbid
ORDER BY total_cpu_ms DESC;";

        var rows = new List<DbRow>();
        await using var c = new SqlConnection(conn.GetConnectionString(server, "master"));
        await c.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 30 };
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new DbRow
            {
                Name = rdr.IsDBNull(0) ? "(unknown)" : rdr.GetString(0),
                Objects = rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1),
                Execs = rdr.IsDBNull(2) ? 0 : Convert.ToInt64(rdr.GetValue(2)),
                TotalCpuMs = rdr.IsDBNull(3) ? 0 : Convert.ToDouble(rdr.GetValue(3)),
                AvgCpuMs = rdr.IsDBNull(4) ? 0 : Convert.ToDouble(rdr.GetValue(4)),
                Reads = rdr.IsDBNull(5) ? 0 : Convert.ToInt64(rdr.GetValue(5)),
                Writes = rdr.IsDBNull(6) ? 0 : Convert.ToInt64(rdr.GetValue(6))
            });
        }
        return rows;
    }

    public async Task<List<ObjRow>> GetObjectsAsync(ServerConnection conn, string server, string databaseName, CancellationToken ct = default)
    {
        // We resolve object names by switching context per database. Cheaper to query once per DB
        // because OBJECT_NAME / OBJECT_SCHEMA_NAME need to run in the right database to resolve.
        var sql = $@"
USE {QuoteName(databaseName)};
SELECT
    OBJECT_SCHEMA_NAME(t.objectid) + '.' + OBJECT_NAME(t.objectid) AS obj_name,
    COALESCE(o.type, 'X') AS otype,
    COUNT(*) AS stmt_count,
    SUM(qs.execution_count) AS execs,
    SUM(qs.total_worker_time) / 1000.0 AS total_cpu_ms,
    SUM(qs.total_worker_time) / NULLIF(SUM(qs.execution_count), 0) / 1000.0 AS avg_cpu_ms,
    SUM(qs.total_logical_reads) AS reads,
    SUM(qs.total_logical_writes) AS writes
FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) t
LEFT JOIN sys.objects o ON t.objectid = o.object_id
WHERE t.dbid = DB_ID()
  AND t.objectid IS NOT NULL
GROUP BY t.objectid, o.type
ORDER BY total_cpu_ms DESC;";

        var rows = new List<ObjRow>();
        await using var c = new SqlConnection(conn.GetConnectionString(server, "master"));
        await c.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 30 };
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var name = rdr.IsDBNull(0) ? "(unknown)" : rdr.GetString(0);
            var otype = rdr.IsDBNull(1) ? "X" : rdr.GetString(1).Trim();
            rows.Add(new ObjRow
            {
                Name = name,
                Kind = MapObjectKind(otype),
                Statements = rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2),
                Execs = rdr.IsDBNull(3) ? 0 : Convert.ToInt64(rdr.GetValue(3)),
                TotalCpuMs = rdr.IsDBNull(4) ? 0 : Convert.ToDouble(rdr.GetValue(4)),
                AvgCpuMs = rdr.IsDBNull(5) ? 0 : Convert.ToDouble(rdr.GetValue(5)),
                Reads = rdr.IsDBNull(6) ? 0 : Convert.ToInt64(rdr.GetValue(6)),
                Writes = rdr.IsDBNull(7) ? 0 : Convert.ToInt64(rdr.GetValue(7))
            });
        }
        return rows;
    }

    public async Task<List<StatementRow>> GetStatementsAsync(ServerConnection conn, string server, string databaseName, string schemaQualifiedObject, CancellationToken ct = default)
    {
        // Split "schema.object" into parts for OBJECT_ID lookup.
        var parts = schemaQualifiedObject.Split('.', 2);
        var schema = parts.Length == 2 ? parts[0] : "dbo";
        var objName = parts.Length == 2 ? parts[1] : schemaQualifiedObject;

        var sql = $@"
USE {QuoteName(databaseName)};
DECLARE @oid INT = OBJECT_ID(@qualified);
IF @oid IS NULL
BEGIN
    SELECT 0 AS dummy WHERE 1=0;
    RETURN;
END;

WITH stmts AS (
    SELECT
        SUBSTRING(t.text,
                  (qs.statement_start_offset/2)+1,
                  ((CASE qs.statement_end_offset
                        WHEN -1 THEN DATALENGTH(t.text)
                        ELSE qs.statement_end_offset
                    END - qs.statement_start_offset)/2) + 1) AS stmt_text,
        qs.execution_count,
        qs.total_worker_time / 1000.0 AS total_cpu_ms,
        (qs.total_worker_time + 0.0) / NULLIF(qs.execution_count, 0) / 1000.0 AS avg_cpu_ms,
        qs.total_logical_reads AS reads,
        qs.total_logical_writes AS writes
    FROM sys.dm_exec_query_stats qs
    CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) t
    WHERE t.objectid = @oid
)
SELECT
    stmt_text, execution_count, total_cpu_ms, avg_cpu_ms, reads, writes,
    100.0 * total_cpu_ms / NULLIF(SUM(total_cpu_ms) OVER (), 0) AS pct_of_total
FROM stmts
ORDER BY total_cpu_ms DESC;";

        var rows = new List<StatementRow>();
        await using var c = new SqlConnection(conn.GetConnectionString(server, "master"));
        await c.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@qualified", $"[{schema}].[{objName}]");
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            // empty result-set short-circuit returns a single dummy=0 column row from the IF branch — guard
            if (rdr.FieldCount < 7) continue;
            var snippet = rdr.IsDBNull(0) ? "" : rdr.GetString(0);
            rows.Add(new StatementRow
            {
                Name = ShortName(snippet),
                Snippet = snippet,
                Execs = rdr.IsDBNull(1) ? 0 : Convert.ToInt64(rdr.GetValue(1)),
                TotalCpuMs = rdr.IsDBNull(2) ? 0 : Convert.ToDouble(rdr.GetValue(2)),
                AvgCpuMs = rdr.IsDBNull(3) ? 0 : Convert.ToDouble(rdr.GetValue(3)),
                Reads = rdr.IsDBNull(4) ? 0 : Convert.ToInt64(rdr.GetValue(4)),
                Writes = rdr.IsDBNull(5) ? 0 : Convert.ToInt64(rdr.GetValue(5)),
                PctOfTotal = rdr.IsDBNull(6) ? 0 : Convert.ToDouble(rdr.GetValue(6))
            });
        }
        return rows;
    }

    private static string MapObjectKind(string sysObjectsType) => sysObjectsType switch
    {
        "P" => "StoredProc",
        "FN" => "ScalarFn",
        "TF" => "TableFn",
        "IF" => "InlineFn",
        "TR" => "Trigger",
        "V" => "View",
        _ => "Other"
    };

    // Single-line, ~60-char preview of the statement, used as the tile label.
    private static string ShortName(string snippet) => ShortNameForDelta(snippet);

    /// <summary>Same single-line preview rule, exposed for the cache delta adapter
    /// in <c>CodeHotspots.razor</c>. Kept internal so it doesn't widen the public API.</summary>
    internal static string ShortNameForDelta(string snippet)
    {
        if (string.IsNullOrWhiteSpace(snippet)) return "(unnamed)";
        var oneLine = System.Text.RegularExpressions.Regex.Replace(snippet, @"\s+", " ").Trim();
        return oneLine.Length <= 80 ? oneLine : oneLine[..77] + "...";
    }

    // Bracket-quote a database name for a USE clause. Reject anything with a `]`
    // because there's no SqlParameter binding for object names.
    private static string QuoteName(string dbName)
    {
        if (string.IsNullOrWhiteSpace(dbName)) throw new ArgumentException("Database name required");
        if (dbName.Contains(']')) throw new ArgumentException("Invalid database name");
        return "[" + dbName + "]";
    }
}
