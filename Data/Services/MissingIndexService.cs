/* In the name of God, the Merciful, the Compassionate */
/*
 * MissingIndexService — read-only sourcing of missing-index CANDIDATES from the
 * sys.dm_db_missing_index_* DMVs. It only READS; it never creates anything. The
 * candidates feed the gated add-missing-index remediation (ADDMISSINGINDEX): the
 * operator picks one, and its exact columns flow into the gated CREATE INDEX.
 *
 * The DMVs report equality/inequality/included columns as bracketed lists; we strip
 * to bare names so the renderer's identifier guard can charset-check them. We do NOT
 * synthesise an index DEFINITION — the columns come verbatim from the DMV; only the
 * index NAME is generated (and sanitised to the guard's charset).
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Renderer = SQLTriage.Data.Services.Remediation.RemediationOpRenderer;

namespace SQLTriage.Data.Services
{
    /// <summary>One missing-index recommendation from the DMVs (read-only; a candidate, not a change).</summary>
    public sealed class MissingIndexCandidate
    {
        public string Database = "";
        public string Schema = "";
        public string Table = "";
        public string SuggestedName = "";
        public List<string> KeyColumns = new();
        public List<string> IncludedColumns = new();
        public long UserSeeks;
        public double AvgImpact;          // avg_user_impact (0-100)
        public double EstimatedBenefit;   // impact-weighted seeks — the ranking score
    }

    public sealed class MissingIndexService
    {
        private readonly IServerConnectionManager _connections;
        private readonly ILogger<MissingIndexService> _logger;

        public MissingIndexService(IServerConnectionManager connections, ILogger<MissingIndexService> logger)
        {
            _connections = connections;
            _logger = logger;
        }

        // Instance-wide missing-index DMVs (database_id resolves the object names). Ranked by the
        // classic impact * (seeks+scans) heuristic. Read-only.
        private const string CandidateSql = @"
SELECT TOP (@top)
    DB_NAME(mid.database_id)                                   AS DatabaseName,
    OBJECT_SCHEMA_NAME(mid.[object_id], mid.database_id)       AS SchemaName,
    OBJECT_NAME(mid.[object_id], mid.database_id)              AS TableName,
    ISNULL(mid.equality_columns, '')                          AS EqualityColumns,
    ISNULL(mid.inequality_columns, '')                        AS InequalityColumns,
    ISNULL(mid.included_columns, '')                          AS IncludedColumns,
    migs.user_seeks                                           AS UserSeeks,
    migs.avg_user_impact                                      AS AvgImpact,
    (migs.avg_user_impact * (migs.user_seeks + migs.user_scans)) AS Benefit
FROM sys.dm_db_missing_index_group_stats migs
JOIN sys.dm_db_missing_index_groups  mig ON migs.group_handle = mig.index_group_handle
JOIN sys.dm_db_missing_index_details mid ON mig.index_handle  = mid.index_handle
WHERE mid.database_id > 4                       -- user databases only
  AND OBJECT_NAME(mid.[object_id], mid.database_id) IS NOT NULL
ORDER BY (migs.avg_user_impact * (migs.user_seeks + migs.user_scans)) DESC;";

        public async Task<List<MissingIndexCandidate>> GetCandidatesAsync(
            string serverNameOrId, int top = 25, CancellationToken ct = default)
        {
            var result = new List<MissingIndexCandidate>();
            var connString = ResolveConnectionString(serverNameOrId);
            if (connString == null) return result;

            try
            {
                using var conn = new SqlConnection(connString);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using var cmd = new SqlCommand(CandidateSql, conn) { CommandTimeout = 30 };
                cmd.Parameters.Add(new SqlParameter("@top", System.Data.SqlDbType.Int) { Value = Math.Clamp(top, 1, 200) });
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var db = reader["DatabaseName"]?.ToString() ?? "";
                    var schema = reader["SchemaName"]?.ToString() ?? "";
                    var table = reader["TableName"]?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(db) || string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(table))
                        continue;

                    var keys = ParseColumns(reader["EqualityColumns"]?.ToString())
                        .Concat(ParseColumns(reader["InequalityColumns"]?.ToString()))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (keys.Count == 0) continue;
                    var includes = ParseColumns(reader["IncludedColumns"]?.ToString())
                        .Where(c => !keys.Contains(c, StringComparer.OrdinalIgnoreCase))
                        .ToList();

                    // Pre-filter to the renderer's identifier charset (the SAME guard the apply
                    // path enforces). Drop the WHOLE candidate if any identifier — db/schema/table
                    // or any column — can't survive it: a comma-bearing or non-ASCII name would
                    // otherwise be silently mangled into a DIFFERENT index, or surfaced as an
                    // always-erroring row. Fail-closed: never offer a candidate we can't faithfully apply.
                    if (!Renderer.IsSafeIdentifier(db) || !Renderer.IsSafeIdentifier(schema) || !Renderer.IsSafeIdentifier(table)
                        || !keys.All(Renderer.IsSafeIdentifier) || !includes.All(Renderer.IsSafeIdentifier))
                    {
                        _logger.LogDebug("Skipping missing-index candidate on [{Schema}].[{Table}] in {Db}: an identifier is outside the safe charset.", schema, table, db);
                        continue;
                    }

                    result.Add(new MissingIndexCandidate
                    {
                        Database = db,
                        Schema = schema,
                        Table = table,
                        KeyColumns = keys,
                        IncludedColumns = includes,
                        SuggestedName = BuildIndexName(table, keys),
                        UserSeeks = ToLong(reader["UserSeeks"]),
                        AvgImpact = ToDouble(reader["AvgImpact"]),
                        EstimatedBenefit = ToDouble(reader["Benefit"]),
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read missing-index candidates for '{Server}'", serverNameOrId);
            }
            return result;
        }

        // DMV column lists look like "[ColA], [ColB]" — strip brackets to bare names. Split is
        // BRACKET-AWARE (commas inside [..] belong to the column name, not the delimiter), so a
        // legally comma-bearing column "[My,Col]" stays one token (and is then dropped by the
        // charset pre-filter rather than silently split into two wrong columns).
        private static List<string> ParseColumns(string? bracketed)
        {
            if (string.IsNullOrWhiteSpace(bracketed)) return new();
            var tokens = new List<string>();
            var sb = new StringBuilder();
            bool inBracket = false;
            for (int i = 0; i < bracketed.Length; i++)
            {
                char ch = bracketed[i];
                if (!inBracket)
                {
                    if (ch == '[') { inBracket = true; continue; }
                    if (ch == ',') { AddToken(tokens, sb); continue; }
                    sb.Append(ch);            // unbracketed (shouldn't occur for DMV output, but be faithful)
                }
                else
                {
                    if (ch == ']')
                    {
                        if (i + 1 < bracketed.Length && bracketed[i + 1] == ']') { sb.Append(']'); i++; continue; } // escaped ]]
                        inBracket = false;     // closing bracket — restores the real name (incl. any inner ',' or ']')
                        continue;
                    }
                    sb.Append(ch);
                }
            }
            AddToken(tokens, sb);
            return tokens;
        }

        private static void AddToken(List<string> tokens, StringBuilder sb)
        {
            var t = sb.ToString().Trim();
            if (t.Length > 0) tokens.Add(t);
            sb.Clear();
        }

        // Generate an index name from the table + first key columns, sanitised to the renderer's
        // identifier charset (letters/digits/underscore). The renderer guards it again at render.
        private static string BuildIndexName(string table, List<string> keys)
        {
            var sb = new StringBuilder("IX_");
            sb.Append(Sanitise(table));
            foreach (var k in keys.Take(3)) { sb.Append('_'); sb.Append(Sanitise(k)); }
            sb.Append("_sqlt");
            var name = sb.ToString();
            return name.Length > 128 ? name.Substring(0, 124) + "sqlt" : name;
        }

        // ASCII-only (NOT char.IsLetterOrDigit, which is Unicode-aware) so the generated name
        // always matches the renderer's ASCII charset guard — never a name that passes here but
        // is rejected at render.
        private static string Sanitise(string s)
        {
            var sb = new StringBuilder();
            foreach (var ch in s)
                if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_')
                    sb.Append(ch);
            return sb.Length == 0 ? "x" : sb.ToString();
        }

        private static long ToLong(object? o) => o != null && long.TryParse(o.ToString(), out var v) ? v : 0;
        private static double ToDouble(object? o) => o != null && double.TryParse(o.ToString(), out var v) ? v : 0;

        private string? ResolveConnectionString(string serverNameOrId)
        {
            var conn = _connections.GetConnection(serverNameOrId)
                       ?? _connections.GetConnections()
                            .Find(c => c.GetServerList().Exists(s =>
                                string.Equals(s, serverNameOrId, StringComparison.OrdinalIgnoreCase)));
            if (conn == null) return null;
            var servers = conn.GetServerList();
            // Prefer the instance that actually matched the requested name (a connection group may
            // hold several); fall back to the first only when matched by connection id.
            var server = servers.FirstOrDefault(s => string.Equals(s, serverNameOrId, StringComparison.OrdinalIgnoreCase))
                         ?? (servers.Count > 0 ? servers[0] : serverNameOrId);
            return conn.GetConnectionString(server, "master");
        }
    }
}
