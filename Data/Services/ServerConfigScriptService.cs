/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Locates and runs the full-edition "Server Configuration &amp; Hardening" T-SQL script
    /// (ConfigScripts/). It splits the script on GO batches and runs them on ONE
    /// connection so the script's session-scoped temp tables (#ChangeControlReport / #CCFlags)
    /// survive across the GO boundaries. Preview (@ForChangeControl = 1) makes NO server change
    /// and only logs PLANNED/SKIPPED rows; Apply (@ForChangeControl = 0) applies the changes.
    /// Server-modifying — surfaced under the "Apply" nav, full-edition only.
    /// </summary>
    public class ServerConfigScriptService
    {
        private readonly SqlServerConnectionFactory _factory;
        private readonly ILogger<ServerConfigScriptService> _logger;

        public ServerConfigScriptService(SqlServerConnectionFactory factory, ILogger<ServerConfigScriptService> logger)
        {
            _factory = factory;
            _logger = logger;
        }

        /// <summary>Resolved path of the script in the build output.</summary>
        public string ScriptPath =>
            Path.Combine(AppContext.BaseDirectory, "ConfigScripts", "Server Configuration and Hardening.sql");

        public bool ScriptExists => File.Exists(ScriptPath);

        /// <summary>
        /// Runs the configuration script against the currently-selected server's master DB.
        /// </summary>
        /// <param name="apply">false = change-control preview (no changes); true = apply.</param>
        /// <param name="operatorName">optional override for the SQL Agent operator name (@OperatorName).</param>
        /// <param name="onMessage">callback(message, isError) for streaming output to the UI.</param>
        public async Task RunAsync(bool apply, string? operatorName, Action<string, bool> onMessage, CancellationToken ct = default)
        {
            if (!ScriptExists)
            {
                onMessage($"Script not found: {ScriptPath}", true);
                return;
            }

            var sql = await File.ReadAllTextAsync(ScriptPath, ct);

            // @ForChangeControl: 1 = preview (logs PLANNED, no server change), 0 = apply.
            sql = Regex.Replace(sql, @"(@ForChangeControl\s+BIT\s*=\s*)[01]", "${1}" + (apply ? "0" : "1"));

            // Optional operator-name override (escaped for the N'...' literal).
            if (!string.IsNullOrWhiteSpace(operatorName))
                sql = Regex.Replace(sql, @"(SET\s+@OperatorName\s*=\s*N')[^']*(')",
                    "$1" + operatorName.Replace("'", "''") + "$2");

            var batches = SplitOnGo(sql);

            using var conn = (SqlConnection)_factory.CreateConnection("master");
            conn.InfoMessage += (_, e) =>
            {
                foreach (SqlError err in e.Errors)
                    onMessage(err.Message, err.Class > 10);
            };

            await conn.OpenAsync(ct);
            onMessage($"Connected to {conn.DataSource} — running {(apply ? "APPLY" : "PREVIEW (change-control)")} …", false);

            var batchNo = 0;
            foreach (var batch in batches)
            {
                batchNo++;
                if (string.IsNullOrWhiteSpace(batch)) continue;
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = batch;
                    cmd.CommandTimeout = 600;
                    using var reader = await cmd.ExecuteReaderAsync(ct);
                    do
                    {
                        while (await reader.ReadAsync(ct))
                        {
                            var sb = new StringBuilder();
                            for (var c = 0; c < reader.FieldCount; c++)
                            {
                                if (c > 0) sb.Append("  |  ");
                                sb.Append(reader.IsDBNull(c) ? string.Empty : reader.GetValue(c)?.ToString());
                            }
                            var line = sb.ToString();
                            if (line.Length > 0) onMessage(line, false);
                        }
                    } while (await reader.NextResultAsync(ct));
                }
                catch (SqlException ex)
                {
                    onMessage($"[batch {batchNo}] ERROR {ex.Number}: {ex.Message}", true);
                    _logger.LogWarning(ex, "Server config script batch {Batch} raised an error", batchNo);
                }
            }

            onMessage($"Done — {(apply ? "apply" : "preview")} complete.", false);
        }

        // Split on lines that are just GO (the T-SQL batch separator), keeping statement text intact.
        // The trailing \r? is essential: the script ships CRLF, and in .NET multiline mode $ matches
        // *before* the \n — so without consuming the \r, "GO\r\n" never matches and GO leaks into the
        // batch ("Incorrect syntax near 'GO'" / "CREATE/ALTER PROCEDURE must be the first statement").
        private static string[] SplitOnGo(string sql) =>
            Regex.Split(sql, @"(?im)^[ \t]*GO[ \t]*(?:--.*)?\r?$");
    }
}
