/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Services.Licensing;
using SQLTriage.Data.Services.Remediation;

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
        private readonly IRemediationCapability _capability;
        private readonly IBundleAccessor _bundle;

        // Marks a GO batch in the .sql as a real server change (currently: the two trailing
        // sp_triage(R) reporting-proc ALTERs). These batches are skipped entirely on a
        // change-control preview so preview can never touch sys.objects — see PREVIEW PURITY
        // comment in ConfigScripts/Server Configuration and Hardening.sql near the proc deploy.
        private const string ApplyOnlyBatchMarker = "SQLTRIAGE_APPLY_ONLY_BATCH";

        // The preview/apply switch is a TEXT substitution over the shipped .sql, and the .sql
        // declares the APPLY side by default (DECLARE @ForChangeControl BIT = 0). Regex.Replace
        // returns its input UNCHANGED when nothing matches, so a reformatted or renamed DECLARE
        // would silently leave the script in APPLY mode and a "preview" would write to the server.
        // The count is therefore checked before a connection is ever opened — see
        // RewriteChangeControlMode. IgnoreCase because T-SQL is not case-sensitive about "BIT".
        private static readonly Regex ForChangeControlDeclare =
            new(@"(@ForChangeControl\s+BIT\s*=\s*)[01]", RegexOptions.IgnoreCase);

        public ServerConfigScriptService(
            SqlServerConnectionFactory factory,
            ILogger<ServerConfigScriptService> logger,
            IRemediationCapability capability,
            IBundleAccessor bundle)
        {
            _factory = factory;
            _logger = logger;
            _capability = capability;
            _bundle = bundle;
        }

        /// <summary>
        /// The licence refusal for this runner, or null when the set is licensed. The VERDICT comes
        /// from <see cref="IRemediationCapability"/> — the same object <c>RemediationRunner</c> asks,
        /// so the --devbridge hatch and the Full-tier + remediation-claim rule are read here exactly
        /// once and from one place. The SENTENCE comes from <see cref="ServerConfigSuiteGate"/>, so a
        /// DBA reads the same words at the nav, the page and here.
        ///
        /// <para>This is the runtime layer that makes /server-configuration's gating more than
        /// markup: <see cref="RunAsync"/> is the only path from that page to a server, and direct
        /// navigation, a stale circuit or a future caller all arrive through it.</para>
        /// </summary>
        private string? LicenceRefusal =>
            _capability.IsGranted ? null : ServerConfigSuiteGate.DescribeRefusal(_bundle);

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
            // Licence gate FIRST — before the script is even read, so an unlicensed install cannot
            // learn anything about the payload and no connection is opened. Reported and returned
            // rather than thrown, matching the ScriptExists refusal below: the page prints the
            // reason into its output pane, and a caller that ignores onMessage still gets no run.
            if (LicenceRefusal is { } refusal)
            {
                onMessage(refusal, true);
                _logger.LogWarning(
                    "Server config script run refused: the Server Configuration feature set is not licensed on this install (apply={Apply}).",
                    apply);
                return;
            }

            if (!ScriptExists)
            {
                onMessage($"Script not found: {ScriptPath}", true);
                return;
            }

            var sql = await File.ReadAllTextAsync(ScriptPath, ct);

            // @ForChangeControl: 1 = preview (logs PLANNED, no server change), 0 = apply.
            sql = RewriteChangeControlMode(sql, apply, onMessage);

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
                if (!apply && batch.Contains(ApplyOnlyBatchMarker, StringComparison.Ordinal))
                {
                    onMessage($"[batch {batchNo}] Skipped (apply-only — no server change on preview).", false);
                    continue;
                }
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

        /// <summary>
        /// One row of the script's structured change-control report (the 8-column result set
        /// near the end of the script: <c>SELECT ID, Captured, Mode, Section, Setting,
        /// CurrentValue, TargetValue, Detail FROM #ChangeControlReport</c>). Mode is one of
        /// PLANNED / SKIPPED / INFO / NOTICE / MANUAL for a preview run — IMPLEMENTING/FAILED
        /// can never appear here because every mutating branch in the script requires
        /// <c>@ForChangeControl = 0</c>, and <see cref="RunPreviewAsync"/> always forces preview.
        /// </summary>
        public sealed record ConfigCheckRow(
            int Id,
            DateTime Captured,
            string Mode,
            string Section,
            string Setting,
            string? CurrentValue,
            string? TargetValue,
            string? Detail);

        /// <summary>
        /// Side-effect-free change-control preview. Always forces <c>@ForChangeControl = 1</c>
        /// (there is no "apply" switch on this method — it cannot be used to apply) and captures
        /// the script's structured #ChangeControlReport result set as typed rows for the
        /// /remediation UI. Every other result set the script returns (the Markdown report,
        /// per-batch diagnostic SELECTs, etc.) still executes on the same single connection —
        /// the script's #temp tables span the whole run, same as <see cref="RunAsync"/> — but is
        /// only forwarded to <paramref name="onMessage"/> as flattened text. The structured
        /// result set is recognised by its exact 8-column shape (ID/Captured/Mode/Section/
        /// Setting/CurrentValue/TargetValue/Detail), not by batch position, so it keeps working
        /// if the script is reordered elsewhere.
        /// </summary>
        public async Task<IReadOnlyList<ConfigCheckRow>> RunPreviewAsync(Action<string, bool>? onMessage = null, CancellationToken ct = default)
        {
            var rows = new List<ConfigCheckRow>();

            // Same gate as RunAsync. Preview makes no server change, but it does open a connection
            // and execute the licensed script's read batches — that is the product, so it is bound
            // to the licence too. /remediation is already refused at its own page level; this makes
            // the runner refuse on its own account rather than by that page's good behaviour.
            if (LicenceRefusal is { } refusal)
            {
                onMessage?.Invoke(refusal, true);
                _logger.LogWarning(
                    "Server config preview refused: the Server Configuration feature set is not licensed on this install.");
                return rows;
            }

            if (!ScriptExists)
            {
                onMessage?.Invoke($"Script not found: {ScriptPath}", true);
                return rows;
            }

            var sql = await File.ReadAllTextAsync(ScriptPath, ct);

            // Preview is ALWAYS change-control — this method has no apply path.
            sql = RewriteChangeControlMode(sql, apply: false, onMessage);

            var batches = SplitOnGo(sql);

            using var conn = (SqlConnection)_factory.CreateConnection("master");
            conn.InfoMessage += (_, e) =>
            {
                foreach (SqlError err in e.Errors)
                    onMessage?.Invoke(err.Message, err.Class > 10);
            };

            await conn.OpenAsync(ct);
            onMessage?.Invoke($"Connected to {conn.DataSource} — running PREVIEW (change-control) …", false);

            var batchNo = 0;
            foreach (var batch in batches)
            {
                batchNo++;
                if (string.IsNullOrWhiteSpace(batch)) continue;
                if (batch.Contains(ApplyOnlyBatchMarker, StringComparison.Ordinal))
                {
                    onMessage?.Invoke($"[batch {batchNo}] Skipped (apply-only — no server change on preview).", false);
                    continue;
                }
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = batch;
                    cmd.CommandTimeout = 600;
                    using var reader = await cmd.ExecuteReaderAsync(ct);
                    do
                    {
                        if (IsChangeControlReportShape(reader))
                        {
                            while (await reader.ReadAsync(ct))
                            {
                                rows.Add(new ConfigCheckRow(
                                    reader.GetInt32(0),
                                    reader.GetDateTime(1),
                                    reader.GetString(2),
                                    reader.GetString(3),
                                    reader.GetString(4),
                                    reader.IsDBNull(5) ? null : reader.GetString(5),
                                    reader.IsDBNull(6) ? null : reader.GetString(6),
                                    reader.IsDBNull(7) ? null : reader.GetString(7)));
                            }
                        }
                        else
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
                                if (line.Length > 0) onMessage?.Invoke(line, false);
                            }
                        }
                    } while (await reader.NextResultAsync(ct));
                }
                catch (SqlException ex)
                {
                    onMessage?.Invoke($"[batch {batchNo}] ERROR {ex.Number}: {ex.Message}", true);
                    _logger.LogWarning(ex, "Server config preview batch {Batch} raised an error", batchNo);
                }
            }

            onMessage?.Invoke($"Done — preview complete ({rows.Count} row(s)).", false);
            return rows;
        }

        /// <summary>
        /// How many <c>@ForChangeControl BIT = 0|1</c> declares the mode substitution would
        /// rewrite in <paramref name="sql"/>. Public so a test can pin it at 1 against the
        /// shipped .sql — the substitution is the only thing standing between a preview and an
        /// apply, and nothing else asserts that the script still matches the pattern.
        /// </summary>
        public static int CountChangeControlDeclares(string sql) =>
            ForChangeControlDeclare.Matches(sql).Count;

        /// <summary>
        /// Rewrites the script's <c>DECLARE @ForChangeControl BIT = 0|1</c> and PROVES the
        /// rewrite landed. Called before any connection is opened, so a substitution that did
        /// not match refuses to run rather than falling through to the script's shipped default
        /// — which is the APPLY side. Exactly one declare is expected; zero means the pattern no
        /// longer matches the .sql, more than one means the mode is ambiguous. Either way the
        /// run is abandoned.
        /// </summary>
        private string RewriteChangeControlMode(string sql, bool apply, Action<string, bool>? onMessage)
        {
            var matchCount = CountChangeControlDeclares(sql);
            if (matchCount != 1)
            {
                var reason =
                    $"Refusing to run: expected exactly one '@ForChangeControl BIT = 0|1' declare to rewrite " +
                    $"in {ScriptPath}, found {matchCount}. The script's own default is APPLY, so it is not " +
                    "run at all rather than run in a mode this app could not set.";
                onMessage?.Invoke(reason, true);
                _logger.LogError("Server config script mode substitution matched {Count} declare(s) — run abandoned", matchCount);
                throw new InvalidOperationException(reason);
            }

            return ForChangeControlDeclare.Replace(sql, "${1}" + (apply ? "0" : "1"));
        }

        // Recognise the script's structured change-control result set by its exact column
        // shape rather than by batch/result-set position, so this keeps working even if the
        // script's SELECTs are reordered around it.
        private static bool IsChangeControlReportShape(SqlDataReader reader) =>
            reader.FieldCount == 8 &&
            string.Equals(reader.GetName(0), "ID", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(reader.GetName(1), "Captured", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(reader.GetName(2), "Mode", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(reader.GetName(3), "Section", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(reader.GetName(4), "Setting", StringComparison.OrdinalIgnoreCase);

        private static string[] SplitOnGo(string sql) => SqlGoBatchSplitter.Split(sql);
    }

    /// <summary>
    /// Splits a T-SQL script on lines that are just GO (the batch separator), keeping statement
    /// text intact. Anchored to the whole line: a bare substring split on "\nGO" also cuts a
    /// line-leading <c>GOTO Label</c> in half, which the SQLWATCH deploy scripts contain.
    ///
    /// The trailing \r? is essential: the scripts ship CRLF, and in .NET multiline mode $ matches
    /// *before* the \n — so without consuming the \r, "GO\r\n" never matches and GO leaks into the
    /// batch ("Incorrect syntax near 'GO'" / "CREATE/ALTER PROCEDURE must be the first statement").
    /// </summary>
    public static class SqlGoBatchSplitter
    {
        private static readonly Regex GoLine =
            new(@"^[ \t]*GO[ \t]*(?:--.*)?\r?$",
                RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

        public static string[] Split(string sql) => GoLine.Split(sql);
    }
}
