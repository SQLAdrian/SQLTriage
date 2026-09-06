/* In the name of God, the Merciful, the Compassionate */

using System.Data.Common;
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
        private readonly AuditLogService? _auditLog;

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
            IBundleAccessor bundle,
            AuditLogService? auditLog = null)
        {
            _factory = factory;
            _logger = logger;
            _capability = capability;
            _bundle = bundle;
            _auditLog = auditLog;
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
        /// TEST SEAM (InternalsVisibleTo SQLTriage.Tests), house pattern — the same shape
        /// <c>LogCleanupService</c> and <c>AdminAuthService</c> use. Null in every shipped path, so
        /// production creates a real <see cref="SqlConnection"/> and nothing about this class's
        /// behaviour depends on the seam existing.
        ///
        /// <para><b>Why it is here at all</b> (R2/R3, gate residuals 2026-08-25). The two per-instance
        /// lanes below own the accumulators the run fills — rows, batch errors, the rollback-script
        /// capture — and thread them into the record they return. That WIRING is the thing the lane
        /// fixed, and it had no offline pin: the tests that came with the fix hand-built the result
        /// records and asserted on their own construction, which is the test-side shape this whole
        /// wave exists to stop. A connection this class cannot tell apart from a real one lets a CI
        /// test drive the REAL producer (<see cref="RunScriptCoreAsync"/>, the real reader loop, the
        /// real per-batch catch) and then read the record the real lane built from it.</para>
        /// </summary>
        internal Func<string, DbConnection>? ConnectionFactoryOverride { get; set; }

        private DbConnection CreateConnection(string connectionString) =>
            ConnectionFactoryOverride?.Invoke(connectionString) ?? new SqlConnection(connectionString);

        /// <summary>
        /// Strips CR/LF from the operator-name override BEFORE it is substituted into the script's
        /// <c>SET @OperatorName = N'...'</c> literal. The substitution is plain text splicing into
        /// a .sql file that is then split on line-leading <c>GO</c> — a name containing a bare
        /// <c>GO</c> line (the page's free-text operator-name input has no seam preventing that)
        /// would inject an extra batch boundary and silently kill every batch after it. An Agent
        /// operator name legitimately has no business containing a newline, so stripping CR/LF at
        /// the substitution site closes the hole for every caller (single- and multi-instance) that
        /// shares this substitution, rather than trying to validate the input at each UI edge.
        /// Public so a test can pin the exact sanitized output and re-run it through the real batch
        /// splitter without a live connection.
        /// </summary>
        public static string? SanitizeOperatorNameForSubstitution(string? operatorName) =>
            string.IsNullOrEmpty(operatorName) ? operatorName : operatorName.Replace("\r", "").Replace("\n", "");

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

            // Optional operator-name override (escaped for the N'...' literal). Sanitized FIRST —
            // see SanitizeOperatorNameForSubstitution's remarks: an unsanitized CR/LF here can
            // inject a bare GO line into the substituted script and split it into extra batches.
            var safeOperatorName = SanitizeOperatorNameForSubstitution(operatorName);
            if (!string.IsNullOrWhiteSpace(safeOperatorName))
                sql = Regex.Replace(sql, @"(SET\s+@OperatorName\s*=\s*N')[^']*(')",
                    "$1" + safeOperatorName.Replace("'", "''") + "$2");

            var batches = SplitOnGo(sql);

            using var conn = (SqlConnection)_factory.CreateConnection("master");
            conn.InfoMessage += (_, e) =>
            {
                foreach (SqlError err in e.Errors)
                    onMessage(err.Message, err.Class > 10);
            };

            await conn.OpenAsync(ct);
            onMessage($"Connected to {conn.DataSource} — running {(apply ? "APPLY" : "PREVIEW (change-control)")} …", false);

            // apply==true only: the script's third result set (ServerName, RollbackScript) is
            // recognised by column shape and captured here instead of being flattened to
            // onMessage text, then written to output/ once the run is done — see the write below.
            string? rollbackServerName = null;
            string? rollbackScript = null;

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
                        if (apply && IsRollbackScriptShape(reader))
                        {
                            if (await reader.ReadAsync(ct))
                            {
                                rollbackServerName = reader.IsDBNull(0) ? null : reader.GetString(0);
                                rollbackScript = reader.IsDBNull(1) ? null : reader.GetString(1);
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
                                if (line.Length > 0) onMessage(line, false);
                            }
                        }
                    } while (await reader.NextResultAsync(ct));
                }
                catch (SqlException ex)
                {
                    onMessage($"[batch {batchNo}] ERROR {ex.Number}: {ex.Message}", true);
                    _logger.LogWarning(ex, "Server config script batch {Batch} raised an error", batchNo);
                }
            }

            // Write the rollback script (skips when null/whitespace: preview never reaches this
            // result set, and an apply run that made zero changes emits RollbackScript = NULL).
            if (apply && !string.IsNullOrWhiteSpace(rollbackScript))
            {
                var outputDir = Path.Combine(AppContext.BaseDirectory, "output");
                var path = await WriteRollbackScriptAsync(outputDir, rollbackServerName ?? conn.DataSource, rollbackScript, ct);
                if (path != null)
                {
                    onMessage($"Rollback script written: {path}", false);
                    _auditLog?.LogReportBundle("ConfigRollback", rollbackServerName ?? conn.DataSource, true, path);
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
        /// <param name="batchErrors">
        /// Optional accumulator for per-batch execution errors. Supply one and the caller can tell
        /// "this server has no setting gaps" apart from "every batch failed and captured nothing" —
        /// two states that used to render identically, the second as a green tick. The /remediation
        /// hardening panel passes one; callers that only want rows may still omit it.
        /// </param>
        public async Task<IReadOnlyList<ConfigCheckRow>> RunPreviewAsync(
            Action<string, bool>? onMessage = null, CancellationToken ct = default, List<string>? batchErrors = null)
        {
            // Same gate as RunAsync. Preview makes no server change, but it does open a connection
            // and execute the licensed script's read batches — that is the product, so it is bound
            // to the licence too. /remediation is already refused at its own page level; this makes
            // the runner refuse on its own account rather than by that page's good behaviour.
            if (LicenceRefusal is { } refusal)
            {
                onMessage?.Invoke(refusal, true);
                batchErrors?.Add(refusal);
                _logger.LogWarning(
                    "Server config preview refused: the Server Configuration feature set is not licensed on this install.");
                return Array.Empty<ConfigCheckRow>();
            }

            if (!ScriptExists)
            {
                var notFound = $"Script not found: {ScriptPath}";
                onMessage?.Invoke(notFound, true);
                batchErrors?.Add(notFound);
                return Array.Empty<ConfigCheckRow>();
            }

            using var conn = (SqlConnection)_factory.CreateConnection("master");
            return await RunPreviewCoreAsync(conn, onMessage, ct, rowsAccumulator: null, batchErrors: batchErrors);
        }

        /// <summary>
        /// One instance's result from <see cref="RunPreviewForInstanceAsync"/>: either a refusal
        /// (licence, missing script, or unreachable — <see cref="RefusalReason"/> is always
        /// populated when <see cref="Refused"/> is true, never a silent no-op) or the captured
        /// change-control rows plus the path the export was written to (null only when the run
        /// itself did not reach the write, which the refusal states already cover).
        /// </summary>
        /// <param name="BatchErrors">
        /// Per-batch execution errors captured during the run. Preview had no such field: every
        /// per-batch SqlException went to a logger warning and vanished, the export printed the
        /// benign "No change-control rows were captured.", and the audit ledger recorded a
        /// hardcoded true. A preview where every batch failed was indistinguishable from a preview
        /// of a server with nothing to change. The apply lane was fixed for this exact shape in the
        /// 2026-08-20 D1/D2 round; this is preview catching up.
        /// </param>
        public sealed record InstanceChangeControlResult(
            string InstanceName,
            bool Refused,
            string? RefusalReason,
            IReadOnlyList<ConfigCheckRow> Rows,
            string? ExportedPath,
            IReadOnlyList<string>? BatchErrors = null)
        {
            /// <summary>
            /// The honest "did this preview actually read the server" signal. False whenever the run
            /// was refused, any batch raised an error (including a mid-run connection loss), or the
            /// run reached the server yet captured zero rows. Same shape and same reasoning as
            /// <see cref="InstanceApplyResult.Succeeded"/>, deliberately identical so a caller
            /// cannot treat the two lanes differently by accident. A pure function of the record's
            /// own fields, so it is testable without I/O.
            /// </summary>
            public bool Succeeded => !Refused && PreviewIsReadable(Rows, BatchErrors);
        }

        /// <summary>
        /// Whether a run produced something a verdict can rest on. False when any batch raised an
        /// error, and false when the run captured zero rows: the script reports SOMETHING for every
        /// server it can read, so an empty result means the read did not happen, not that the
        /// server is clean.
        ///
        /// <para>Public and static so the SERVICE and the /remediation hardening panel share ONE
        /// definition. They did not share one: the panel had no way to see batch errors at all, so
        /// a run where every batch threw returned an empty row list and the panel rendered "Already
        /// at the conservative hardening baseline" over the top of those errors.</para>
        /// </summary>
        public static bool PreviewIsReadable(IReadOnlyList<ConfigCheckRow>? rows, IReadOnlyCollection<string>? batchErrors) =>
            (batchErrors is null || batchErrors.Count == 0) && rows is { Count: > 0 };

        /// <summary>
        /// The headline sentence a caller prints when <see cref="PreviewIsReadable"/> says no.
        /// THREE shapes, because "this preview did not read X" is itself a false statement about a
        /// run that captured rows AND hit a batch error: that run read the server, partially.
        /// Withholding the verdict is right in all three; overclaiming why is not.
        ///
        /// <para>Shared and pure so the sentence is testable and so a second caller cannot invent
        /// its own wording for the same three facts.</para>
        /// </summary>
        public static string DescribeUnreadablePreview(
            IReadOnlyList<ConfigCheckRow>? rows, IReadOnlyCollection<string>? batchErrors, string serverLabel)
        {
            var rowCount = rows?.Count ?? 0;
            var errorCount = batchErrors?.Count ?? 0;
            var failed = errorCount == 1 ? "One batch failed" : $"{errorCount} batches failed";

            if (errorCount > 0 && rowCount > 0)
                return $"This preview read {serverLabel} and did not finish. {failed}, so no hardening verdict is shown.";
            if (errorCount > 0)
                return $"This preview captured nothing from {serverLabel}. {failed}, so no hardening verdict is shown.";
            return $"This preview reached {serverLabel} and captured no settings. No hardening verdict is shown, because there is nothing to base one on.";
        }

        /// <summary>
        /// Runs a change-control preview against ONE named instance, using an explicit connection
        /// string rather than <see cref="SqlServerConnectionFactory"/>'s process-wide "currently
        /// selected server" — the multi-instance /server-configuration flow calls this once per
        /// selected instance, sequentially, and a shared global target would make the second call
        /// silently retarget (or race) the first. Every selected instance therefore gets its own,
        /// independent change-control run, exactly as the single-instance runner does with the
        /// GlobalInstanceSelector.
        ///
        /// <para>Licensed exactly the same way as <see cref="RunPreviewAsync"/> — one process-wide
        /// bundle, so every instance is refused together when the install itself is unlicensed. The
        /// gate is still asked per call (not hoisted to the caller) so a future caller of this
        /// method on its own gets the refusal, not a silent run.</para>
        ///
        /// <para>On success, the captured rows are formatted and written to
        /// <c>{outputDir}\{instance}_{yyyyMMdd-HHmmss}_changecontrol.txt</c> and logged through the
        /// existing <see cref="AuditLogService.LogReportBundle"/> hook under the
        /// <c>"RemediationChangeControl"</c> bundle type — the same audit surface lane C's
        /// apply-mode rollback export uses under <c>"ConfigRollback"</c>.</para>
        /// </summary>
        public async Task<InstanceChangeControlResult> RunPreviewForInstanceAsync(
            string instanceName, string connectionString, string outputDir, CancellationToken ct = default)
        {
            if (LicenceRefusal is { } refusal)
            {
                _logger.LogWarning(
                    "Server config preview refused for {Instance}: the Server Configuration feature set is not licensed on this install.",
                    instanceName);
                return new InstanceChangeControlResult(instanceName, true, refusal, Array.Empty<ConfigCheckRow>(), null);
            }

            if (!ScriptExists)
            {
                var reason = $"Script not found: {ScriptPath}";
                return new InstanceChangeControlResult(instanceName, true, reason, Array.Empty<ConfigCheckRow>(), null);
            }

            // Owned by THIS call for the same reason the apply lane owns its own: partial progress
            // survives a mid-run throw, because the accumulators already hold what was captured.
            var rows = new List<ConfigCheckRow>();
            var batchErrors = new List<string>();
            try
            {
                using var conn = CreateConnection(connectionString);
                await RunPreviewCoreAsync(conn, onMessage: null, ct, rows, batchErrors);
            }
            catch (SqlException ex)
            {
                var reason = $"Could not connect to {instanceName}: {ex.Message}";
                _logger.LogWarning(ex, "Server config preview could not reach {Instance}", instanceName);
                return new InstanceChangeControlResult(instanceName, true, reason, Array.Empty<ConfigCheckRow>(), null);
            }
            catch (InvalidOperationException ex)
            {
                // The connection died mid-run (e.g. a server-side KILL of the SPID) — ADO.NET
                // surfaces this as "BeginExecuteReader requires an open and available Connection",
                // not a SqlException. Same handling as the apply lane: not a refusal, because a
                // real attempt happened, and the export still lands with whatever was captured.
                var lossMessage = $"Connection lost mid-run: {ex.Message}";
                batchErrors.Add(lossMessage);
                _logger.LogWarning(ex, "Server config preview lost its connection to {Instance} mid-run", instanceName);
            }

            var result = new InstanceChangeControlResult(instanceName, false, null, rows, null, batchErrors);
            var text = FormatChangeControlExport(instanceName, DateTime.Now, rows, batchErrors: batchErrors);
            var path = await WriteChangeControlExportAsync(outputDir, instanceName, text, apply: false, ct: ct);
            if (path != null)
                _auditLog?.LogReportBundle("RemediationChangeControl", instanceName, result.Succeeded, path);

            return result with { ExportedPath = path };
        }

        /// <summary>
        /// One instance's result from <see cref="RunApplyForInstanceAsync"/>: same shape as
        /// <see cref="InstanceChangeControlResult"/> (refusal-or-rows-and-export), but the rows come
        /// from a run that made real changes. <see cref="ConfigCheckRow.Mode"/> can therefore be
        /// IMPLEMENTING or FAILED here, which preview's rows can never carry — <see
        /// cref="HasFailedRows"/> is the honest per-instance "did anything fail" signal the UI reads
        /// to distinguish Done from Failed (a FAILED row is a script-level failure captured by the
        /// script's own TRY/CATCH; it is not an unhandled exception, so it would not otherwise be
        /// visible without reading the rows).
        /// </summary>
        /// <param name="BatchErrors">
        /// Per-batch execution errors captured during the run (defaults to null/empty for the
        /// refusal constructors above, which never reach the batch loop at all). Distinct from
        /// <see cref="HasFailedRows"/>: a FAILED row is the SCRIPT's own TRY/CATCH recording a
        /// failure it survived; a batch error is this RUNNER catching an exception the batch itself
        /// raised (a T-SQL error, or the connection dying mid-run) — the script's report can be
        /// silent about exactly the batches that never got to record anything.
        /// </param>
        /// <param name="RollbackScriptPath">
        /// Where this run's generated undo script was written, or null when none was written. The
        /// multi-instance apply lane used to DISCARD it: <see cref="RunScriptCoreAsync"/> looked only
        /// for the change-control shape, so the script's (ServerName, RollbackScript) result set fell
        /// into the flatten-to-onMessage branch with onMessage null, and the operator was left with an
        /// applied change and no generated way back. The single-instance <see cref="RunAsync"/> had
        /// captured and written it since 2026-06-30.
        /// </param>
        /// <param name="RollbackScriptError">
        /// Set when a rollback script WAS returned and could not be written to disk. Distinguishes
        /// "this run produced no undo script" (both fields null) from "there is an undo script and you
        /// do not have it" — two states that must never read the same.
        /// </param>
        public sealed record InstanceApplyResult(
            string InstanceName,
            bool Refused,
            string? RefusalReason,
            IReadOnlyList<ConfigCheckRow> Rows,
            string? ExportedPath,
            IReadOnlyList<string>? BatchErrors = null,
            string? RollbackScriptPath = null,
            string? RollbackScriptError = null)
        {
            public bool HasFailedRows =>
                Rows.Any(r => string.Equals(r.Mode, "FAILED", StringComparison.OrdinalIgnoreCase));

            /// <summary>
            /// The honest "did this actually work" signal a fabricated LogReportBundle(..., true, ...)
            /// used to skip: false whenever the run was refused, any batch raised an error (including
            /// a mid-run connection loss), or the run reached the server yet captured zero rows (the
            /// D1 shape — every batch died before the script's #ChangeControlReport got a row). A
            /// pure function of the record's own fields so it is testable without any I/O or a live
            /// server — the wiring that reads it (export header, LogReportBundle, the UI's Done/
            /// Failed mapping) is proved live, not here.
            /// </summary>
            public bool Succeeded =>
                !Refused && (BatchErrors is null || BatchErrors.Count == 0) && Rows.Count > 0;

            /// <summary>
            /// One sentence naming what happened to this run's undo script. Deliberately NOT folded
            /// into <see cref="Succeeded"/>: the apply itself can succeed while the undo script is
            /// missing, and <see cref="Succeeded"/> means "did this read/change the server", shared
            /// word for word with the preview lane. Whether a missing undo file should downgrade an
            /// instance to Failed is a product call, so this states the fact and leaves the verdict.
            /// </summary>
            public string RollbackScriptNote => DescribeRollbackOutcome(RollbackScriptPath, RollbackScriptError);
        }

        /// <summary>
        /// The operator-facing sentence for an apply run's undo script: written (and where), returned
        /// but not written (and why), or not produced at all. Pure and public so the service, the
        /// export and the page all say the same thing.
        /// </summary>
        public static string DescribeRollbackOutcome(string? path, string? error) =>
            error is not null
                ? $"Rollback script NOT saved. {error} This apply has no generated undo script."
                : path is not null
                    ? $"Rollback script saved: {path}"
                    : "No rollback script was returned. The script emits one only for changes it actually made.";

        /// <summary>
        /// Capture slot for the script's apply-mode (ServerName, RollbackScript) result set. Owned by
        /// the caller and passed IN, for the same reason the row and batch-error accumulators are:
        /// what the run captured before a mid-run connection loss survives the throw.
        /// </summary>
        public sealed class RollbackScriptCapture
        {
            public string? ServerName { get; set; }
            public string? Script { get; set; }
            public bool Captured => !string.IsNullOrWhiteSpace(Script);
        }

        /// <summary>
        /// Runs the configuration script in APPLY mode (<c>@ForChangeControl = 0</c>, real server
        /// changes) against ONE named instance, using an explicit connection string — the apply
        /// counterpart to <see cref="RunPreviewForInstanceAsync"/>, same explicit-connection
        /// reasoning (never the process-wide "currently selected server", so a second instance in
        /// the same run can never retarget or race the first).
        ///
        /// <para>Licence-gated FIRST, identically to every other entry point on this class — a
        /// refused instance never opens a connection, and the refusal names its own reason. There is
        /// no separate arm/confirm step in this method: that discipline lives in the caller (the
        /// page), same as the single-instance <c>Apply</c> path's <c>_armApply</c> checkbox — this
        /// method assumes the caller already obtained operator consent for the exact instance it is
        /// called with.</para>
        ///
        /// <para>On success, the run's captured <see cref="ConfigCheckRow"/> rows (whatever Mode the
        /// script recorded — IMPLEMENTING, FAILED, SKIPPED, INFO, NOTICE) are exported to
        /// <c>{outputDir}\{instance}_{yyyyMMdd-HHmmss}_changecontrol-applied.txt</c> — the "-applied"
        /// suffix is the only difference from the preview export's name, so the two can never
        /// collide or be mistaken for each other in the output folder — and logged through <see
        /// cref="AuditLogService.LogReportBundle"/> under the distinct <c>"RemediationApply"</c>
        /// bundle type (preview logs under <c>"RemediationChangeControl"</c>).</para>
        ///
        /// <para><b>Error channel (D1/D2 fix, 2026-08-20 H fix round):</b> per-batch exceptions no
        /// longer vanish behind <c>onMessage: null</c>. They are collected into <c>batchErrors</c>
        /// and land in the returned <see cref="InstanceApplyResult.BatchErrors"/>, which <see
        /// cref="InstanceApplyResult.Succeeded"/> folds together with "reached the server but
        /// captured zero rows" — both shapes a killed run can produce. A server-side KILL of the
        /// running SPID surfaces as <see cref="InvalidOperationException"/> ("BeginExecuteReader
        /// requires an open and available Connection") rather than a <see cref="SqlException"/>,
        /// and is caught here rather than left to escape unhandled: the server may already be
        /// partially mutated by whatever batches completed, so this method does NOT rethrow — it
        /// records the loss as a batch error and falls through to the SAME export+audit path a
        /// clean run takes, with whatever rows were captured before the connection died. That is
        /// the point: the record exists precisely because the server may have changed.</para>
        /// </summary>
        public async Task<InstanceApplyResult> RunApplyForInstanceAsync(
            string instanceName, string connectionString, string outputDir, string? operatorName = null, CancellationToken ct = default)
        {
            if (LicenceRefusal is { } refusal)
            {
                _logger.LogWarning(
                    "Server config apply refused for {Instance}: the Server Configuration feature set is not licensed on this install.",
                    instanceName);
                return new InstanceApplyResult(instanceName, true, refusal, Array.Empty<ConfigCheckRow>(), null);
            }

            if (!ScriptExists)
            {
                var reason = $"Script not found: {ScriptPath}";
                return new InstanceApplyResult(instanceName, true, reason, Array.Empty<ConfigCheckRow>(), null);
            }

            // Owned by THIS call, passed into the core so partial progress survives even if the
            // core throws (a mid-run connection loss propagates as an exception rather than a
            // normal return — see the InvalidOperationException catch below) — the accumulator
            // lists are already populated with whatever the run captured before the throw.
            var rows = new List<ConfigCheckRow>();
            var batchErrors = new List<string>();
            var rollback = new RollbackScriptCapture();
            try
            {
                using var conn = CreateConnection(connectionString);
                await RunApplyCoreAsync(conn, operatorName, onMessage: null, ct, rows, batchErrors, rollback);
            }
            catch (SqlException ex)
            {
                // Reached only for a connect-time failure: a batch-level SqlException raised once
                // the connection is open is already caught inside RunScriptCoreAsync's own per-batch
                // try/catch and lands in batchErrors instead, never here.
                var reason = $"Could not connect to {instanceName}: {ex.Message}";
                _logger.LogWarning(ex, "Server config apply could not reach {Instance}", instanceName);
                return new InstanceApplyResult(instanceName, true, reason, Array.Empty<ConfigCheckRow>(), null);
            }
            catch (InvalidOperationException ex)
            {
                // The connection died mid-run (e.g. a server-side KILL of the SPID) — ADO.NET
                // surfaces this as "BeginExecuteReader requires an open and available Connection",
                // not a SqlException. Deliberately NOT a refusal (Refused stays false below): a
                // real attempt happened and the server may already be half-mutated by whatever
                // batches ran before the loss, so this falls through to the normal export+audit
                // path with whatever rows were captured pre-loss, exactly like a batch-error run.
                var lossMessage = $"Connection lost mid-run: {ex.Message}";
                batchErrors.Add(lossMessage);
                _logger.LogWarning(ex, "Server config apply lost its connection to {Instance} mid-run", instanceName);
            }

            // The undo script, written BEFORE the export so the export can name it. A write failure
            // here is recorded and reported, never swallowed: an apply that changed the server and
            // silently lost its way back is exactly the shape this lane exists to close.
            string? rollbackPath = null;
            string? rollbackError = null;
            if (rollback.Captured)
            {
                try
                {
                    rollbackPath = await WriteRollbackScriptAsync(
                        outputDir, rollback.ServerName ?? instanceName, rollback.Script, ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    rollbackError = $"Writing it to {outputDir} failed: {ex.Message}";
                    _logger.LogError(ex, "Could not write the rollback script for {Instance} to {Dir}", instanceName, outputDir);
                }
            }

            var result = new InstanceApplyResult(
                instanceName, false, null, rows, null, batchErrors, rollbackPath, rollbackError);
            var text = FormatChangeControlExport(instanceName, DateTime.Now, rows, apply: true,
                batchErrors: batchErrors, rollbackNote: result.RollbackScriptNote);
            var path = await WriteChangeControlExportAsync(outputDir, instanceName, text, apply: true, ct: ct);
            if (path != null)
                _auditLog?.LogReportBundle("RemediationApply", instanceName, result.Succeeded, path);
            if (rollbackPath != null)
                _auditLog?.LogReportBundle("ConfigRollback", rollback.ServerName ?? instanceName, true, rollbackPath);

            return result with { ExportedPath = path };
        }

        /// <summary>
        /// The read-and-execute core shared by <see cref="RunPreviewAsync"/> (process-wide "current
        /// server") and <see cref="RunPreviewForInstanceAsync"/> (an explicit per-instance
        /// connection) — same script read, same mode rewrite, same batch loop and same
        /// #ChangeControlReport shape detection either way, so the two callers can never observe
        /// different preview behaviour. Opening and disposing <paramref name="conn"/> is the
        /// CALLER's responsibility (both callers already hold it in a <c>using</c>).
        /// </summary>
        private Task<IReadOnlyList<ConfigCheckRow>> RunPreviewCoreAsync(
            DbConnection conn, Action<string, bool>? onMessage, CancellationToken ct,
            List<ConfigCheckRow>? rowsAccumulator = null, List<string>? batchErrors = null) =>
            RunScriptCoreAsync(conn, apply: false, operatorName: null, onMessage, ct, rowsAccumulator, batchErrors);

        /// <summary>
        /// The apply-mode counterpart to <see cref="RunPreviewCoreAsync"/>, used only by <see
        /// cref="RunApplyForInstanceAsync"/> — same shared <see cref="RunScriptCoreAsync"/> body, so
        /// preview and apply can never diverge on the read/parse/batch-loop mechanics, only on the
        /// <c>apply</c> flag <see cref="RunScriptCoreAsync"/> already threads through <see
        /// cref="RewriteChangeControlMode"/> and the apply-only-batch skip.
        /// <paramref name="rowsAccumulator"/>/<paramref name="batchErrors"/> are OWNED by the caller
        /// (see <see cref="RunApplyForInstanceAsync"/>) so partial progress survives a mid-run throw
        /// — D2's connection-loss catch lives one level up, at the per-instance boundary.
        /// </summary>
        private Task<IReadOnlyList<ConfigCheckRow>> RunApplyCoreAsync(
            DbConnection conn, string? operatorName, Action<string, bool>? onMessage, CancellationToken ct,
            List<ConfigCheckRow>? rowsAccumulator = null, List<string>? batchErrors = null,
            RollbackScriptCapture? rollback = null) =>
            RunScriptCoreAsync(conn, apply: true, operatorName, onMessage, ct, rowsAccumulator, batchErrors, rollback);

        /// <summary>
        /// Shared read-parse-execute body behind <see cref="RunPreviewCoreAsync"/> and <see
        /// cref="RunApplyCoreAsync"/>: reads the script, rewrites <c>@ForChangeControl</c> via <see
        /// cref="RewriteChangeControlMode"/> (which refuses the whole run rather than silently
        /// falling through to the script's own APPLY default — see that method's remarks), applies
        /// the optional operator-name substitution <see cref="RunAsync"/> also performs, splits on
        /// GO, and runs every batch on <paramref name="conn"/> — skipping apply-only batches only
        /// when <paramref name="apply"/> is false, identically to <see cref="RunAsync"/>. The
        /// structured #ChangeControlReport rows are captured by column shape regardless of mode: in
        /// preview they can only be PLANNED/SKIPPED/INFO/NOTICE, in apply they can also be
        /// IMPLEMENTING/FAILED (the script's own comment at the top of this file).
        /// <paramref name="rowsAccumulator"/> lets a caller supply the list to append to (rather
        /// than only get one back on a clean return) so rows captured before a mid-run exception
        /// are not lost with the stack frame; <paramref name="batchErrors"/> collects each per-batch
        /// exception's message (D1 fix) — both default to null/discarded for <see
        /// cref="RunPreviewCoreAsync"/>'s preview callers, which do not read either.
        ///
        /// <para><paramref name="rollback"/> (r1-04 fix, 2026-08-25 honesty hunt): apply mode also
        /// captures the script's (ServerName, RollbackScript) result set, the same way <see
        /// cref="RunAsync"/> always has. Without it that result set fell into the flatten-to-text
        /// branch below and, with <paramref name="onMessage"/> null on the multi-instance lane, was
        /// discarded outright — the operator got the change and no generated way back.</para>
        /// </summary>
        private async Task<IReadOnlyList<ConfigCheckRow>> RunScriptCoreAsync(
            DbConnection conn, bool apply, string? operatorName, Action<string, bool>? onMessage, CancellationToken ct,
            List<ConfigCheckRow>? rowsAccumulator = null, List<string>? batchErrors = null,
            RollbackScriptCapture? rollback = null)
        {
            var rows = rowsAccumulator ?? new List<ConfigCheckRow>();

            var sql = await File.ReadAllTextAsync(ScriptPath, ct);

            sql = RewriteChangeControlMode(sql, apply, onMessage);

            var safeOperatorName = SanitizeOperatorNameForSubstitution(operatorName);
            if (!string.IsNullOrWhiteSpace(safeOperatorName))
                sql = Regex.Replace(sql, @"(SET\s+@OperatorName\s*=\s*N')[^']*(')",
                    "$1" + safeOperatorName.Replace("'", "''") + "$2");

            var batches = SplitOnGo(sql);

            // The PRINT/RAISERROR relay is SqlClient-specific. Typed rather than assumed, because
            // the parameter is DbConnection: the per-instance lanes create theirs through
            // CreateConnection, whose test seam hands back an in-memory DbConnection so the offline
            // pins can drive this loop for real (R2/R3, 2026-08-25). A connection with no
            // InfoMessage relays nothing; every other behaviour below is unchanged.
            if (conn is SqlConnection infoConn)
            {
                infoConn.InfoMessage += (_, e) =>
                {
                    foreach (SqlError err in e.Errors)
                        onMessage?.Invoke(err.Message, err.Class > 10);
                };
            }

            await conn.OpenAsync(ct);
            onMessage?.Invoke($"Connected to {conn.DataSource} -- running {(apply ? "APPLY" : "PREVIEW (change-control)")}...", false);

            var batchNo = 0;
            foreach (var batch in batches)
            {
                batchNo++;
                if (string.IsNullOrWhiteSpace(batch)) continue;
                if (!apply && batch.Contains(ApplyOnlyBatchMarker, StringComparison.Ordinal))
                {
                    onMessage?.Invoke($"[batch {batchNo}] Skipped (apply-only, no server change on preview).", false);
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
                        if (apply && rollback is not null && IsRollbackScriptShape(reader))
                        {
                            if (await reader.ReadAsync(ct))
                            {
                                rollback.ServerName = reader.IsDBNull(0) ? null : reader.GetString(0);
                                rollback.Script = reader.IsDBNull(1) ? null : reader.GetString(1);
                            }
                        }
                        else if (IsChangeControlReportShape(reader))
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
                catch (DbException ex)
                {
                    // DbException, not SqlException: SqlException IS a DbException, so a real
                    // server's batch failure lands here exactly as before and prints the same
                    // "ERROR <number>" line. Widening it is what lets the offline pin drive this
                    // arm with a real failure instead of hand-building the result record after it.
                    var number = ex is SqlException sqlEx ? sqlEx.Number : ex.ErrorCode;
                    var msg = $"[batch {batchNo}] ERROR {number}: {ex.Message}";
                    onMessage?.Invoke(msg, true);
                    batchErrors?.Add(msg);
                    _logger.LogWarning(ex, "Server config script batch {Batch} raised an error ({Mode})", batchNo, apply ? "apply" : "preview");
                }
            }

            onMessage?.Invoke($"Done -- {(apply ? "apply" : "preview")} complete ({rows.Count} row(s)).", false);
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
        private static bool IsChangeControlReportShape(DbDataReader reader) =>
            reader.FieldCount == 8 &&
            string.Equals(reader.GetName(0), "ID", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(reader.GetName(1), "Captured", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(reader.GetName(2), "Mode", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(reader.GetName(3), "Section", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(reader.GetName(4), "Setting", StringComparison.OrdinalIgnoreCase);

        // Recognise the script's rollback-script result set (apply mode only) by its exact
        // 2-column shape (ServerName, RollbackScript) — same idiom as IsChangeControlReportShape
        // above, so it keeps working if the script's result sets are reordered. The plain-name
        // overload is public so a test can pin it without a live SqlDataReader (SqlDataReader has
        // no in-memory fake); the private one adapts a real reader onto it.
        public static bool IsRollbackScriptShape(int fieldCount, Func<int, string> getName) =>
            fieldCount == 2 &&
            string.Equals(getName(0), "ServerName", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(getName(1), "RollbackScript", StringComparison.OrdinalIgnoreCase);

        private static bool IsRollbackScriptShape(DbDataReader reader) =>
            IsRollbackScriptShape(reader.FieldCount, reader.GetName);

        /// <summary>
        /// Sanitizes a server/instance name for use in an export file name (mirrors
        /// <c>DiagnosticScriptRunner.SanitizeFileName</c>'s idiom: every <see
        /// cref="Path.GetInvalidFileNameChars"/> becomes '_', plus a path-traversal guard). Shared
        /// by the change-control export below and the apply-mode rollback export.
        ///
        /// <para>Never emits a leading dot (cosmetic fix, 2026-08-20 H round): a local instance
        /// name like <c>.\new2022</c> sanitizes to <c>._new2022</c> before this guard, which POSIX
        /// tooling reads as a hidden file. Leading dots are trimmed AFTER the invalid-char and
        /// traversal replacement, so a name that becomes all dots still falls back to
        /// <c>"unknown"</c> rather than an empty string.</para>
        /// </summary>
        public static string SanitizeInstanceNameForFile(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "unknown";

            var sanitized = name;
            foreach (var c in Path.GetInvalidFileNameChars())
                sanitized = sanitized.Replace(c, '_');
            sanitized = sanitized.Replace("../", "_").Replace("..\\", "_");

            sanitized = sanitized.TrimStart('.');
            if (string.IsNullOrEmpty(sanitized)) return "unknown";

            if (sanitized.Length > 100) sanitized = sanitized.Substring(0, 100);
            return sanitized;
        }

        /// <summary>
        /// Builds the rollback script's file name: <c>{instance}_{yyyyMMdd-HHmmss}_rollback.sql</c>.
        /// Public so a test can pin the exact naming convention.
        /// </summary>
        public static string BuildRollbackFileName(string? instanceName, DateTime timestamp) =>
            $"{SanitizeInstanceNameForFile(instanceName)}_{timestamp:yyyyMMdd-HHmmss}_rollback.sql";

        /// <summary>
        /// Writes the rollback script text to <paramref name="outputDir"/> under the naming
        /// convention above and returns the path, or null when there is nothing to write (no
        /// rollback statements were produced this run). Kept separate from the DB-reading loop in
        /// <see cref="RunAsync"/> so it is testable without a live connection.
        /// </summary>
        public static async Task<string?> WriteRollbackScriptAsync(
            string outputDir, string? instanceName, string? script, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(script))
                return null;

            Directory.CreateDirectory(outputDir);
            var path = Path.Combine(outputDir, BuildRollbackFileName(instanceName, DateTime.Now));
            await File.WriteAllTextAsync(path, script, ct);
            return path;
        }

        /// <summary>
        /// Renders a change-control run's captured rows as plain text for the per-instance export —
        /// a header line naming the instance, the moment captured and whether the run was a preview
        /// or an apply, then one line per row. <paramref name="apply"/> defaults to false so every
        /// existing preview call site is unchanged. Public so a test can pin the exact shape without
        /// a live SqlDataReader.
        ///
        /// <para><paramref name="batchErrors"/> (D1 fix, 2026-08-20 H round): when any batch raised
        /// an error, they are named up front and the "no rows" sentence — which used to print
        /// unconditionally and read as a benign "nothing needed doing" even when every batch had
        /// just failed — is never shown as if the run were clean.</para>
        ///
        /// <para><paramref name="rollbackNote"/> (r1-04 fix, 2026-08-25): an apply export names what
        /// happened to the undo script. Optional so preview call sites are unchanged.</para>
        /// </summary>
        public static string FormatChangeControlExport(string instanceName, DateTime capturedAt, IReadOnlyList<ConfigCheckRow> rows, bool apply = false, IReadOnlyList<string>? batchErrors = null, string? rollbackNote = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Change-control {(apply ? "apply" : "preview")} -- {instanceName} -- {capturedAt:yyyy-MM-dd HH:mm:ss}");
            if (!string.IsNullOrWhiteSpace(rollbackNote))
                sb.AppendLine(rollbackNote);
            sb.AppendLine(new string('-', 72));

            var hasErrors = batchErrors is { Count: > 0 };
            if (hasErrors)
            {
                sb.AppendLine($"{batchErrors!.Count} batch error(s) occurred during this run:");
                foreach (var err in batchErrors)
                    sb.AppendLine($"  ! {err}");
                sb.AppendLine();
            }

            if (rows.Count == 0)
            {
                sb.AppendLine(hasErrors
                    ? "No change-control rows were captured -- the run did not complete cleanly (see the batch error(s) above)."
                    : "No change-control rows were captured.");
            }
            else
            {
                foreach (var r in rows)
                {
                    sb.Append('[').Append(r.Mode).Append("] ")
                      .Append(r.Section).Append(" / ").Append(r.Setting)
                      .Append(": ").Append(r.CurrentValue).Append(" -> ").Append(r.TargetValue);
                    if (!string.IsNullOrEmpty(r.Detail))
                        sb.Append(" (").Append(r.Detail).Append(')');
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Builds the change-control export's file name:
        /// <c>{instance}_{yyyyMMdd-HHmmss}_changecontrol.txt</c> for a preview, or
        /// <c>{instance}_{yyyyMMdd-HHmmss}_changecontrol-applied.txt</c> when <paramref
        /// name="apply"/> is true — the "-applied" suffix is the only difference, so a preview and
        /// an apply export for the same instance and the same second can never collide in
        /// <c>.\output</c>. <paramref name="apply"/> defaults to false so every existing preview
        /// call site is unchanged. Public so a test can pin the exact naming convention.
        /// </summary>
        public static string BuildChangeControlFileName(string? instanceName, DateTime timestamp, bool apply = false) =>
            $"{SanitizeInstanceNameForFile(instanceName)}_{timestamp:yyyyMMdd-HHmmss}_changecontrol{(apply ? "-applied" : "")}.txt";

        /// <summary>
        /// Writes a change-control export's text to <paramref name="outputDir"/> under the naming
        /// convention above and returns the path, or null when there is no text to write. Kept
        /// separate from the DB-reading loop so it is testable without a live connection. <paramref
        /// name="apply"/> defaults to false so every existing preview call site is unchanged.
        /// </summary>
        public static async Task<string?> WriteChangeControlExportAsync(
            string outputDir, string? instanceName, string? text, bool apply = false, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            Directory.CreateDirectory(outputDir);
            var path = Path.Combine(outputDir, BuildChangeControlFileName(instanceName, DateTime.Now, apply));
            await File.WriteAllTextAsync(path, text, ct);
            return path;
        }

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
