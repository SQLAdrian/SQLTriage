/* In the name of God, the Merciful, the Compassionate */
/*
 * MaintenanceSolutionOpRenderer — install/uninstall of Ola Hallengren's Maintenance
 * Solution (BPScripts\01. MaintenanceSolution.sql) + the four independent schedule
 * tickboxes (CHECKDB weekly, full backup daily, diff backup, log backup every 15 min).
 *
 * Ola Hallengren's Maintenance Solution — https://ola.hallengren.com /
 * https://github.com/olahallengren/sql-server-maintenance-solution — MIT License.
 * Copyright (c) Ola Hallengren. Full license text: BPScripts\LICENSE (if present) or
 * https://ola.hallengren.com/license.html. Embedded verbatim, unmodified except for the
 * @CreateJobs default flip (see RenderInstallApply) — the checksum below pins the
 * UNMODIFIED source text; the flip happens in code, never in the embedded file.
 *
 * Design mirrors RemediationOpRenderer's AgentAlertPack / CreateIndex ops exactly:
 *   - SNAPSHOT (read-only): PER-NAME provenance — which of the 4 core procs + CommandLog
 *     already exist in the target DB, BEFORE apply (a HashSet<string>, same shape as
 *     AgentAlertPack's RenderAgentAlertPackSnapshot) — not just a boolean "all 4 present?".
 *   - APPLY: run the (checksum-verified) embedded script with @CreateJobs='N' — jobs are
 *     created separately, per-lane, by the schedule tickboxes below.
 *   - VERIFY: the 4 procs now exist.
 *   - ROLLBACK (uninstall): a NAMED inverse, PROVENANCE-GATED like AgentAlertPack's
 *     TryRenderAgentAlertPackRollback — DROP a proc/table ONLY if its name is ABSENT from
 *     the pre-apply snapshot set (this apply created it); a name present before apply
 *     (e.g. a user's own dbo.DatabaseBackup) is never touched. Same for the jobs THIS
 *     apply's tickboxes created (never a pre-existing Ola install).
 * Schedule tickboxes are a SEPARATE, independently reversible op each: one msdb job per
 * lane, guarded IF NOT EXISTS by job name, calling the matching Ola proc on its schedule.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SQLTriage.Data.Services.Remediation
{
    public static class MaintenanceSolutionOpRenderer
    {
        // ── Embedded, checksum-pinned script ────────────────────────────────────

        /// <summary>
        /// SHA-256 of the embedded BPScripts\01. MaintenanceSolution.sql, computed over the exact
        /// bytes shipped (UTF-8 with BOM, CRLF, as embedded by MSBuild) — Ola Hallengren's official
        /// v2025-12-20 release, MIT-licensed (github.com/olahallengren/sql-server-maintenance-solution).
        /// A mismatch here (tampered resource, or a future version dropped in without updating this
        /// const) refuses to install rather than silently run unverified script text (doctrine
        /// principle 13 — determinism/reproducibility). Recompute via:
        ///   [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([System.IO.File]::ReadAllBytes($path))).ToLower()
        /// </summary>
        public const string ExpectedScriptSha256 = "4045a69b50371371c0a6b06b4d97ce36cd82cf763dc49d2557e818d1ab7fac5d";

        private static string? _cachedScriptText;
        private static readonly object _loadLock = new();

        /// <summary>
        /// Loads the embedded script, verifies its SHA-256 against <see cref="ExpectedScriptSha256"/>,
        /// and caches the decoded text (BOM-stripped) for reuse. Fails closed (throws) on a checksum
        /// mismatch or a missing resource — never runs unverified/absent script text.
        /// </summary>
        public static string LoadVerifiedScriptText()
        {
            if (_cachedScriptText is not null) return _cachedScriptText;
            lock (_loadLock)
            {
                if (_cachedScriptText is not null) return _cachedScriptText;

                var asm = Assembly.GetExecutingAssembly();
                // Resolve by suffix rather than a hardcoded logical name: MSBuild's name-mangling of
                // "BPScripts\01. MaintenanceSolution.sql" (dots/spaces/leading-digit escaping) is an
                // implementation detail: matching by suffix is robust to it without guessing exactly.
                var resourceName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("MaintenanceSolution.sql", StringComparison.OrdinalIgnoreCase));
                if (resourceName is null)
                    throw new InvalidOperationException(
                        "Embedded resource 'BPScripts\\01. MaintenanceSolution.sql' was not found in the assembly manifest.");

                using var stream = asm.GetManifestResourceStream(resourceName)
                    ?? throw new InvalidOperationException($"Could not open embedded resource '{resourceName}'.");
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var bytes = ms.ToArray();

                var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!string.Equals(actualHash, ExpectedScriptSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Embedded Maintenance Solution script failed its checksum guard (expected {ExpectedScriptSha256}, got {actualHash}). " +
                        "Refusing to install unverified script text.");

                // Decode as UTF-8 and strip a leading BOM if present (the shipped file carries one);
                // the hash above is computed over the RAW bytes, so this decode happens only AFTER
                // the guard passes — the guard vets exactly what's embedded, not a post-processed copy.
                var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes);
                if (text.Length > 0 && text[0] == '﻿') text = text[1..];

                _cachedScriptText = text;
                return _cachedScriptText;
            }
        }

        /// <summary>Test seam: forces the next <see cref="LoadVerifiedScriptText"/> to re-verify (not used in production).</summary>
        internal static void ResetCacheForTests() { lock (_loadLock) { _cachedScriptText = null; } }

        // Split on lines that are just GO (the T-SQL batch separator) — identical regex to
        // ServerConfigScriptService.SplitOnGo (proven against this exact class of multi-batch
        // script). The trailing \r? matters: without consuming it, "GO\r\n" doesn't match in
        // .NET multiline mode and GO leaks into the batch.
        private static readonly Regex GoSplit = new(@"(?im)^[ \t]*GO[ \t]*(?:--.*)?\r?$", RegexOptions.Compiled);

        /// <summary>
        /// Renders the install APPLY: the verified script text with the top-of-script
        /// @CreateJobs default flipped 'Y'→'N' (jobs are created separately by the schedule
        /// tickboxes so the operator controls which lanes run), split into GO batches for
        /// sequential execution on one connection (temp tables/#Config must survive across
        /// batch boundaries, same requirement ServerConfigScriptService documents).
        /// </summary>
        public static string[] RenderInstallApplyBatches()
        {
            var sql = LoadVerifiedScriptText();
            // Only the @CreateJobs declaration line's literal is touched; @BackupDirectory/
            // @LogToTable etc. are left at their shipped defaults (NULL / 'Y' — logging to
            // CommandLog is wanted; no backup directory is set here — the schedule tickboxes
            // pass their own @BackupDirectory per-job-step, not via this install-time default).
            sql = Regex.Replace(sql,
                @"(DECLARE\s+@CreateJobs\s+nvarchar\(max\)\s*=\s*)'Y'",
                "$1'N'", RegexOptions.IgnoreCase);
            return GoSplit.Split(sql);
        }

        /// <summary>The 4 core procs the install creates (also used by the snapshot/verify reads).</summary>
        public static readonly IReadOnlyList<string> CoreProcNames = new[]
        {
            "CommandExecute", "DatabaseBackup", "DatabaseIntegrityCheck", "IndexOptimize"
        };

        /// <summary>The CommandLog table name, alongside the 4 core procs, as one provenance unit.</summary>
        public const string CommandLogTableName = "CommandLog";

        /// <summary>
        /// Read-only snapshot/verify: 1 iff ALL 4 core procs exist in the target database.
        /// Used post-apply (verify) and by the install-if-absent NoOp decision (equivalent to
        /// "all 4 names present in <see cref="RenderProcsProvenanceSnapshot"/>'s result set").
        /// </summary>
        public const string ProcsExistProbe =
            "SELECT CASE WHEN " +
            "(SELECT COUNT(*) FROM sys.objects WHERE type IN ('P','PC') AND name IN " +
            "('CommandExecute','DatabaseBackup','DatabaseIntegrityCheck','IndexOptimize')) = 4 " +
            "THEN 1 ELSE 0 END;";

        /// <summary>
        /// Renders the read-only PROVENANCE snapshot: one row per one of the 4 core proc names +
        /// CommandLog that ALREADY exists in the target database BEFORE apply — same shape as
        /// AgentAlertPack's RenderAgentAlertPackSnapshot. The executor captures this as a
        /// <c>HashSet&lt;string&gt;</c> once, before running the install script, and threads it into
        /// <see cref="RenderUninstallSql"/> so rollback/cleanup never drops a name that predates
        /// this apply (e.g. a user's own home-grown dbo.DatabaseBackup proc) — the fix for the
        /// data-loss defect where a name-only DROP could destroy an unrelated pre-existing object.
        /// </summary>
        public static string RenderProcsProvenanceSnapshot()
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (var proc in CoreProcNames)
            {
                if (!first) sb.Append(" UNION ALL ");
                first = false;
                sb.Append("SELECT ").Append(QuoteDataLiteral(proc))
                  .Append(" AS existing_name WHERE EXISTS (SELECT 1 FROM sys.objects WHERE type IN ('P','PC') AND name = ")
                  .Append(QuoteDataLiteral(proc)).Append(')');
            }
            sb.Append(" UNION ALL SELECT ").Append(QuoteDataLiteral(CommandLogTableName))
              .Append(" WHERE EXISTS (SELECT 1 FROM sys.objects WHERE type = 'U' AND name = ")
              .Append(QuoteDataLiteral(CommandLogTableName)).Append(')').Append(';');
            return sb.ToString();
        }

        // Reuses this file's existing QuoteDataLiteral (below, near TryRenderScheduleCreateSql) —
        // same single-quote-doubling a data literal needs, already proven for the backup-directory
        // escaping path; the proc/table names above are compile-time constants, never user input,
        // but the same helper keeps the quoting identical to the sibling pattern this fix mirrors.

        /// <summary>
        /// Renders the uninstall (rollback) batch: drops each of the 4 core procs + CommandLog
        /// table that is ABSENT from <paramref name="preExisting"/> (this apply created it),
        /// still guarded IF EXISTS (idempotent) underneath. A name PRESENT in <paramref
        /// name="preExisting"/> (it existed before this apply — whether Ola's own prior install or
        /// an unrelated user object sharing the name) is NEVER dropped. This is the install's
        /// reversibility story — a NAMED, PROVENANCE-GATED inverse, not a snapshot-value replay
        /// (there is no single "old value" for an install) and not a blind name-only DROP (that
        /// was the data-loss defect this parameter fixes). Job cleanup is handled separately by
        /// <see cref="RenderScheduleDropSql"/> (each schedule tickbox is its own independently
        /// reversible unit).
        /// </summary>
        public static string RenderUninstallSql(IReadOnlySet<string> preExisting)
        {
            var sb = new StringBuilder();
            foreach (var proc in CoreProcNames)
            {
                if (preExisting.Contains(proc)) continue; // existed before this apply — never drop it
                sb.AppendLine($"IF OBJECT_ID(N'dbo.{proc}', 'P') IS NOT NULL OR OBJECT_ID(N'dbo.{proc}', 'PC') IS NOT NULL");
                sb.AppendLine($"    DROP PROCEDURE dbo.{proc};");
            }
            if (!preExisting.Contains(CommandLogTableName))
            {
                sb.AppendLine("IF OBJECT_ID(N'dbo.CommandLog', 'U') IS NOT NULL");
                sb.AppendLine("    DROP TABLE dbo.CommandLog;");
            }
            return sb.ToString();
        }

        // ── Schedule tickboxes: one idempotent Agent job per lane ───────────────

        /// <summary>The 4 independent schedule lanes a tickbox can create.</summary>
        public enum ScheduleLane { CheckDbWeekly, FullBackupDaily, DiffBackupDaily, LogBackupEvery15Min }

        /// <summary>All 4 lanes, in a fixed, stable order (matches the UI tickbox order).</summary>
        public static readonly IReadOnlyList<ScheduleLane> AllLanes = new[]
        {
            ScheduleLane.CheckDbWeekly, ScheduleLane.FullBackupDaily,
            ScheduleLane.DiffBackupDaily, ScheduleLane.LogBackupEvery15Min
        };

        public const string BackupDirectoryParam = "MaintenanceSolution.BackupDirectory";

        /// <summary>
        /// The request parameter selecting WHICH sub-operation a single INSTALLMAINTENANCESOLUTION
        /// apply performs: absent/empty = install the procs; one of <see cref="ScheduleLane"/>'s
        /// names = create (or, on rollback, drop) that one schedule job. One template key, many
        /// bounded operations — the same model CreateIndex uses (one ADDMISSINGINDEX key, many
        /// index specs) rather than minting 5 separate registered keys for one feature.
        /// </summary>
        public const string ActionParam = "MaintenanceSolution.Action";
        public const string ActionInstall = "Install";

        /// <summary>Resolves the requested action from parameters; defaults to Install when absent.</summary>
        public static bool TryResolveAction(IReadOnlyDictionary<string, string> parameters, out string action, out ScheduleLane? lane, out string error)
        {
            action = ActionInstall; lane = null; error = string.Empty;
            if (parameters is null || !parameters.TryGetValue(ActionParam, out var raw) || string.IsNullOrWhiteSpace(raw)
                || string.Equals(raw, ActionInstall, StringComparison.OrdinalIgnoreCase))
                return true;

            if (Enum.TryParse<ScheduleLane>(raw, ignoreCase: true, out var parsedLane))
            {
                action = raw;
                lane = parsedLane;
                return true;
            }
            error = $"Unrecognised MaintenanceSolution action '{raw}'.";
            return false;
        }

        /// <summary>The exact, stable job name for a lane — the idempotency key (IF NOT EXISTS by name).</summary>
        public static string JobNameFor(ScheduleLane lane) => lane switch
        {
            ScheduleLane.CheckDbWeekly => "SQLTriage - CHECKDB Weekly",
            ScheduleLane.FullBackupDaily => "SQLTriage - Full Backup Daily",
            ScheduleLane.DiffBackupDaily => "SQLTriage - Diff Backup Daily",
            ScheduleLane.LogBackupEvery15Min => "SQLTriage - Log Backup Every 15 Min",
            _ => throw new ArgumentOutOfRangeException(nameof(lane))
        };

        // A backup lane's directory rides a request parameter — semi-trusted (operator-typed) text
        // that becomes a T-SQL literal. Single-quote-escape into the N'...' literal EXACTLY like
        // RemediationOpRenderer's operator name/email (QuoteDataLiteral) — never an identifier, so
        // no charset restriction is needed, but the embedded quote must be escaped so it cannot
        // terminate the literal early (the path-injection surface this guards against).
        private static string QuoteDataLiteral(string raw) => "N'" + (raw ?? string.Empty).Replace("'", "''") + "'";

        /// <summary>
        /// Resolves + validates the backup directory for the two backup lanes. Fails closed on an
        /// empty value; the only injection defence needed is single-quote escaping (done at render
        /// time by <see cref="QuoteDataLiteral"/>) since this rides a data literal, never an
        /// identifier or a shell command.
        /// </summary>
        public static bool TryResolveBackupDirectory(IReadOnlyDictionary<string, string> parameters, out string directory, out string error)
        {
            directory = string.Empty; error = string.Empty;
            if (parameters is null || !parameters.TryGetValue(BackupDirectoryParam, out var dir) || string.IsNullOrWhiteSpace(dir))
            {
                error = "A backup directory is required for this schedule lane.";
                return false;
            }
            directory = dir.Trim();
            return true;
        }

        /// <summary>
        /// Renders the idempotent CREATE batch for one schedule lane: sp_add_job (IF NOT EXISTS by
        /// name) → sp_add_jobstep (calls the matching Ola proc) → sp_add_schedule + sp_attach via
        /// sp_add_jobserver-style attach (@server_name local). Guarded end-to-end by job name — a
        /// second apply with the same lane is a no-op (the executor checks existence first, same
        /// install-if-absent shape as the proc install).
        /// </summary>
        public static bool TryRenderScheduleCreateSql(
            ScheduleLane lane, IReadOnlyDictionary<string, string> parameters, out string sql, out string error)
        {
            sql = string.Empty; error = string.Empty;
            var jobName = QuoteDataLiteral(JobNameFor(lane));
            string command;
            string schedule; // sp_add_schedule params fragment

            switch (lane)
            {
                case ScheduleLane.CheckDbWeekly:
                    // Weekly, Sunday 01:00 — matches the corpus's 14-day CHECKDB-recency window
                    // with comfortable headroom. PHYSICAL_ONLY off (full logical+physical check);
                    // the operator can tune via SSMS afterwards — this ships the sane default.
                    // DatabaseIntegrityCheck is the correct entry point: the original shipped
                    // command called CommandExecute (Ola's low-level runner, which has no
                    // @Databases/@CheckCommands parameters), so the job failed on its very first
                    // run — caught 2026-07-29 by the S1 deferred-verify live loop, the exact gap
                    // ("a created job proves nothing until it has run") S1 exists to close.
                    command = "EXEC dbo.DatabaseIntegrityCheck @Databases = 'ALL_DATABASES', @CheckCommands = 'CHECKDB', " +
                              "@LogToTable = 'Y', @Execute = 'Y'";
                    schedule = "@freq_type = 8, @freq_interval = 1, @freq_recurrence_factor = 1, @active_start_time = 010000"; // weekly, Sunday, 01:00
                    break;
                case ScheduleLane.FullBackupDaily:
                    if (!TryResolveBackupDirectory(parameters, out var fullDir, out error)) return false;
                    command = "EXEC dbo.DatabaseBackup @Databases = 'USER_DATABASES', @BackupType = 'FULL', " +
                              $"@Directory = {QuoteDataLiteral(fullDir)}, @Verify = 'Y', @CleanupTime = 720, @LogToTable = 'Y'";
                    schedule = "@freq_type = 4, @freq_interval = 1, @active_start_time = 020000"; // daily, 02:00
                    break;
                case ScheduleLane.DiffBackupDaily:
                    if (!TryResolveBackupDirectory(parameters, out var diffDir, out error)) return false;
                    command = "EXEC dbo.DatabaseBackup @Databases = 'USER_DATABASES', @BackupType = 'DIFF', " +
                              $"@Directory = {QuoteDataLiteral(diffDir)}, @Verify = 'Y', @CleanupTime = 168, @LogToTable = 'Y', " +
                              "@ChangeBackupType = 'Y'"; // auto-upgrades to FULL if no prior full exists
                    schedule = "@freq_type = 4, @freq_interval = 1, @active_start_time = 100000"; // daily, 10:00 (offset from full)
                    break;
                case ScheduleLane.LogBackupEvery15Min:
                    if (!TryResolveBackupDirectory(parameters, out var logDir, out error)) return false;
                    command = "EXEC dbo.DatabaseBackup @Databases = 'USER_DATABASES', @BackupType = 'LOG', " +
                              $"@Directory = {QuoteDataLiteral(logDir)}, @Verify = 'Y', @CleanupTime = 72, @LogToTable = 'Y'";
                    // Minutes-based recurrence: freq_type=4 (daily), freq_subday_type=4 (minutes), freq_subday_interval=15.
                    schedule = "@freq_type = 4, @freq_interval = 1, @freq_subday_type = 4, @freq_subday_interval = 15";
                    break;
                default:
                    error = $"Unsupported schedule lane '{lane}'.";
                    return false;
            }

            var scheduleName = QuoteDataLiteral(JobNameFor(lane) + " Schedule");
            var sb = new StringBuilder();
            sb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = {jobName})");
            sb.AppendLine("BEGIN");
            sb.AppendLine($"    EXEC msdb.dbo.sp_add_job @job_name = {jobName}, @enabled = 1, @description = N'Created by SQLTriage (Ola Hallengren Maintenance Solution schedule tickbox).';");
            sb.AppendLine($"    EXEC msdb.dbo.sp_add_jobstep @job_name = {jobName}, @step_name = N'Run', @subsystem = N'TSQL', " +
                          $"@database_name = N'master', @command = N'{command.Replace("'", "''")}', @on_success_action = 1, @on_fail_action = 2;");
            sb.AppendLine($"    EXEC msdb.dbo.sp_add_schedule @schedule_name = {scheduleName}, {schedule};");
            sb.AppendLine($"    EXEC msdb.dbo.sp_attach_schedule @job_name = {jobName}, @schedule_name = {scheduleName};");
            sb.AppendLine($"    EXEC msdb.dbo.sp_add_jobserver @job_name = {jobName}, @server_name = N'(LOCAL)';");
            sb.AppendLine("END");
            sql = sb.ToString();
            return true;
        }

        /// <summary>Read-only: does this lane's job already exist (by name)?</summary>
        public static string RenderScheduleExistsSql(ScheduleLane lane) =>
            $"SELECT CASE WHEN EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = {QuoteDataLiteral(JobNameFor(lane))}) THEN 1 ELSE 0 END;";

        /// <summary>
        /// Renders the DROP for one lane's job — the clean, named inverse (drop the job; SQL
        /// Server cascades its jobsteps/schedule/jobserver rows). Guarded IF EXISTS.
        /// </summary>
        public static string RenderScheduleDropSql(ScheduleLane lane) =>
            $"IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = {QuoteDataLiteral(JobNameFor(lane))})\n" +
            $"    EXEC msdb.dbo.sp_delete_job @job_name = {QuoteDataLiteral(JobNameFor(lane))};";

        /// <summary>
        /// Renders a value-independent representative form of the FULL apply (install + one of
        /// each schedule lane) for the safety gate's classification — same pattern as
        /// RemediationOpRenderer.TryRenderForClassification. The gate vets the SHAPE (CREATE
        /// TABLE/PROCEDURE + sp_add_job/jobstep/schedule/jobserver), not real request parameters.
        /// </summary>
        public static string RenderRepresentativeForClassification()
        {
            var sb = new StringBuilder();
            // Representative install shape (first + last batch is enough to exercise CREATE TABLE
            // and CREATE/ALTER PROCEDURE — classifying the full 22-batch script adds no signal).
            sb.AppendLine("CREATE TABLE [dbo].[CommandLog] ([ID] [int] IDENTITY(1,1) NOT NULL);");
            sb.AppendLine("EXEC dbo.sp_executesql @statement = N'CREATE PROCEDURE [dbo].[CommandExecute] AS';");
            sb.AppendLine("ALTER PROCEDURE [dbo].[CommandExecute] AS SELECT 1;");
            // Representative schedule-tickbox shape (CHECKDB lane; no operator-supplied directory needed).
            TryRenderScheduleCreateSql(ScheduleLane.CheckDbWeekly, new Dictionary<string, string>(), out var scheduleSql, out _);
            sb.Append(scheduleSql);
            return sb.ToString();
        }

        // ── Express/no-Agent gate probe (identical semantics to RemediationOpRenderer's) ──
        /// <summary>
        /// 1 = SQL Server Agent available (schedule tickboxes may render/apply), 0 = unavailable
        /// (e.g. Express/EngineEdition 4) — the PROC INSTALL may still proceed (the procs work
        /// without Agent); only the schedule tickboxes are gated off.
        /// </summary>
        public const string AgentAvailabilityProbe =
            "SELECT CASE WHEN CAST(SERVERPROPERTY('EngineEdition') AS int) = 4 THEN 0 ELSE 1 END;";
    }
}
