/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Install-if-absent semantics for Ola Hallengren's Maintenance Solution + the four
    /// independent schedule tickboxes. Pins: (1) the embedded script's checksum guard, (2) the
    /// install-vs-noop decision is driven by the 4-proc snapshot, (3) uninstall drops only the
    /// objects the install created (never a pre-existing install), (4) a backup lane requires a
    /// non-empty, safely-escaped BackupDirectory, (5) Express (no Agent) gates schedule tickboxes
    /// but not the proc install, (6) the gate classifies the rendered writes as Remediation ONLY
    /// under the registered key, (7) the shipped template carries the verified corpus check ids.
    /// </summary>
    public class RemediationMaintenanceSolutionTests
    {
        // ── Checksum guard ───────────────────────────────────────────────────

        [Fact]
        public void LoadVerifiedScriptText_MatchesPinnedChecksum_AndReturnsNonEmptyText()
        {
            var text = MaintenanceSolutionOpRenderer.LoadVerifiedScriptText();
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.Contains("CommandExecute", text);
            Assert.Contains("Ola Hallengren", text);
        }

        [Fact]
        public void ExpectedScriptSha256_IsAWellFormedLowercaseHexHash()
        {
            Assert.Equal(64, MaintenanceSolutionOpRenderer.ExpectedScriptSha256.Length);
            Assert.Matches("^[0-9a-f]{64}$", MaintenanceSolutionOpRenderer.ExpectedScriptSha256);
        }

        /// <summary>
        /// The guard above hashes the EMBEDDED resource. This one hashes the script file exactly as it
        /// sits CHECKED OUT — which is the byte stream MSBuild is about to embed — so a bad checkout
        /// fails here, at build time, with a diagnosis, instead of at install time on a client's SQL
        /// Server. It also says WHICH failure it is, because "checksum mismatch" alone is what sent
        /// this hunt after a script change that had never happened.
        ///
        /// Written 2026-08-03. <c>.gitattributes</c> carried a self-cancelling <c>-text eol=crlf</c>
        /// for three weeks: <c>-text</c> means "check the blob out verbatim", which suppresses the
        /// <c>eol=crlf</c> beside it. Every worktree created after that rule landed checked the script
        /// out LF, hashed dbee9d8a… instead of the pinned 4045a69b…, and the install path failed
        /// closed. It hid for three weeks because worktrees created BEFORE the rule had already been
        /// converted to CRLF by core.autocrlf=true, and git never re-normalises files already on disk
        /// — so the pin held on the old trees and failed on every fresh checkout.
        /// </summary>
        [Fact]
        public void RepoScriptFile_AsCheckedOut_HashesToThePinnedChecksum()
        {
            var path = LocateRepoScript();
            var bytes = File.ReadAllBytes(path);
            var actual = Sha256Hex(bytes);
            var pinned = MaintenanceSolutionOpRenderer.ExpectedScriptSha256;

            if (actual == pinned) return;

            // Latin1 round-trips every byte 0-255 unchanged, so the BOM and any non-UTF8 bytes
            // survive these transforms — we are re-hashing bytes, not re-encoding text.
            var chars = Encoding.Latin1.GetString(bytes);
            var asLf = chars.Replace("\r\n", "\n");
            var asCrlf = asLf.Replace("\n", "\r\n");
            var lfHash = Sha256Hex(Encoding.Latin1.GetBytes(asLf));
            var crlfHash = Sha256Hex(Encoding.Latin1.GetBytes(asCrlf));

            string diagnosis;
            if (crlfHash == pinned)
                diagnosis =
                    "LINE ENDINGS, NOT DRIFT. The script text is correct; this checkout landed LF and the " +
                    "pin is over the CRLF form. Check that .gitattributes reads `text eol=crlf` (NOT `-text " +
                    "eol=crlf`, which is self-cancelling) for \"BPScripts/01. MaintenanceSolution.sql\", then " +
                    "`git add --renormalize` that path and re-checkout the file. Do NOT re-pin the constant: " +
                    "worktrees that predate the fix hold the CRLF bytes and would break.";
            else if (lfHash == pinned)
                diagnosis =
                    "The pin appears to be over the LF form while this checkout is CRLF — the inverse of the " +
                    "2026-08-03 bug. Someone likely re-pinned the constant to the LF hash. Restore the CRLF " +
                    "pin and fix the checkout instead.";
            else
                diagnosis =
                    "GENUINE DRIFT. Neither the LF nor the CRLF form of this file matches the pin, so the " +
                    "script text itself changed. Establish that the change was intended and vendored from a " +
                    "real Ola Hallengren release BEFORE touching the constant.";

            Assert.Fail(
                $"Embedded Maintenance Solution script failed its pinned checksum.{Environment.NewLine}" +
                $"  file      : {path}{Environment.NewLine}" +
                $"  size      : {bytes.Length} bytes{Environment.NewLine}" +
                $"  pinned    : {pinned}{Environment.NewLine}" +
                $"  actual    : {actual}{Environment.NewLine}" +
                $"  as LF     : {lfHash}{Environment.NewLine}" +
                $"  as CRLF   : {crlfHash}{Environment.NewLine}" +
                $"  diagnosis : {diagnosis}");
        }

        private static string Sha256Hex(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        /// <summary>
        /// Walks up from this source file to find the script in the repo. Deliberately THROWS rather
        /// than skipping when it cannot find it — a guard that quietly opts out when it cannot run is
        /// the false-clean this test exists to prevent.
        /// </summary>
        private static string LocateRepoScript([CallerFilePath] string callerFilePath = "")
        {
            var dir = Path.GetDirectoryName(callerFilePath);
            while (!string.IsNullOrEmpty(dir))
            {
                var candidate = Path.Combine(dir, "BPScripts", "01. MaintenanceSolution.sql");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }

            throw new FileNotFoundException(
                "Could not locate BPScripts/01. MaintenanceSolution.sql by walking up from " +
                $"'{callerFilePath}'. This test hashes the file as checked out, so it must run against " +
                "the repo tree. If the layout moved, fix this walk — do not delete or skip the test.");
        }

        // ── Install batches: GO-split, @CreateJobs flipped to 'N' ────────────

        [Fact]
        public void RenderInstallApplyBatches_FlipsCreateJobsToNo()
        {
            var batches = MaintenanceSolutionOpRenderer.RenderInstallApplyBatches();
            Assert.True(batches.Length > 1, "The 22-GO-batch script should split into multiple batches.");
            var joined = string.Join("\n", batches);
            Assert.Contains("@CreateJobs nvarchar(max)          = 'N'", joined);
            Assert.DoesNotContain("@CreateJobs nvarchar(max)          = 'Y'", joined);
        }

        [Fact]
        public void RenderInstallApplyBatches_ContainsTheFourCoreProcCreates()
        {
            var joined = string.Join("\n", MaintenanceSolutionOpRenderer.RenderInstallApplyBatches());
            foreach (var proc in MaintenanceSolutionOpRenderer.CoreProcNames)
                Assert.Contains(proc, joined);
        }

        [Fact]
        public void RenderInstallApplyBatches_NoBatchIsEmptyGoLeakage()
        {
            // A regression guard for the GO-split regex: no batch text should itself start with
            // a bare "GO" (which would mean the separator leaked into the batch instead of being
            // consumed) — this is the exact bug ServerConfigScriptService's SplitOnGo comment warns about.
            foreach (var batch in MaintenanceSolutionOpRenderer.RenderInstallApplyBatches())
                Assert.False(System.Text.RegularExpressions.Regex.IsMatch(batch.TrimStart(), @"^GO\b"),
                    "A batch must not start with a leaked GO separator.");
        }

        // ── Install-vs-noop decision (snapshot probe shape) ──────────────────

        [Fact]
        public void ProcsExistProbe_ChecksAllFourCoreProcNames()
        {
            foreach (var proc in MaintenanceSolutionOpRenderer.CoreProcNames)
                Assert.Contains(proc, MaintenanceSolutionOpRenderer.ProcsExistProbe);
            Assert.Contains("= 4", MaintenanceSolutionOpRenderer.ProcsExistProbe);
        }

        [Fact]
        public void ProcsExistProbe_IsReadOnlySafe()
        {
            Assert.True(SqlSafetyValidator.Validate(MaintenanceSolutionOpRenderer.ProcsExistProbe).IsSafe);
        }

        // ── Uninstall (named, provenance-gated inverse) ───────────────────────

        [Fact]
        public void RenderUninstallSql_NothingPreExisting_DropsAllFourProcsAndCommandLog_EachGuardedIfExists()
        {
            var none = new HashSet<string>();
            var sql = MaintenanceSolutionOpRenderer.RenderUninstallSql(none);
            foreach (var proc in MaintenanceSolutionOpRenderer.CoreProcNames)
                Assert.Contains($"DROP PROCEDURE dbo.{proc}", sql);
            Assert.Contains("DROP TABLE dbo.CommandLog", sql);
            // Every DROP is preceded by an existence guard (idempotent uninstall — never throws
            // on a partially-created or already-absent object).
            var guardCount = sql.Split(new[] { "IF OBJECT_ID" }, StringSplitOptions.None).Length - 1;
            Assert.Equal(5, guardCount); // 4 procs + CommandLog
        }

        [Fact]
        public void RenderUninstallSql_PreExistingName_IsNeverDropped_KillerCase()
        {
            // The data-loss defect: a user's own home-grown dbo.DatabaseBackup proc (a common
            // name) predates this apply. Snapshot correctly records it as pre-existing, so
            // uninstall must NEVER emit a DROP for it — while still dropping the other 3 procs +
            // CommandLog, which this apply DID create.
            var preExisting = new HashSet<string> { "DatabaseBackup" };
            var sql = MaintenanceSolutionOpRenderer.RenderUninstallSql(preExisting);

            Assert.DoesNotContain("DROP PROCEDURE dbo.DatabaseBackup", sql);
            Assert.Contains("DROP PROCEDURE dbo.CommandExecute", sql);
            Assert.Contains("DROP PROCEDURE dbo.DatabaseIntegrityCheck", sql);
            Assert.Contains("DROP PROCEDURE dbo.IndexOptimize", sql);
            Assert.Contains("DROP TABLE dbo.CommandLog", sql);
        }

        [Fact]
        public void RenderUninstallSql_AllPreExisting_DropsNothing()
        {
            var preExisting = new HashSet<string>(MaintenanceSolutionOpRenderer.CoreProcNames)
            {
                MaintenanceSolutionOpRenderer.CommandLogTableName
            };
            var sql = MaintenanceSolutionOpRenderer.RenderUninstallSql(preExisting);
            Assert.DoesNotContain("DROP PROCEDURE", sql);
            Assert.DoesNotContain("DROP TABLE", sql);
        }

        [Fact]
        public void RenderProcsProvenanceSnapshot_IsReadOnlySafe_AndNamesAllFiveObjects()
        {
            var sql = MaintenanceSolutionOpRenderer.RenderProcsProvenanceSnapshot();
            Assert.True(SqlSafetyValidator.Validate(sql).IsSafe);
            foreach (var proc in MaintenanceSolutionOpRenderer.CoreProcNames)
                Assert.Contains(proc, sql);
            Assert.Contains(MaintenanceSolutionOpRenderer.CommandLogTableName, sql);
        }

        // ── Schedule tickboxes: backup lanes require a directory ─────────────

        [Theory]
        [InlineData(MaintenanceSolutionOpRenderer.ScheduleLane.FullBackupDaily)]
        [InlineData(MaintenanceSolutionOpRenderer.ScheduleLane.DiffBackupDaily)]
        [InlineData(MaintenanceSolutionOpRenderer.ScheduleLane.LogBackupEvery15Min)]
        public void TryRenderScheduleCreateSql_BackupLane_RequiresNonEmptyDirectory(MaintenanceSolutionOpRenderer.ScheduleLane lane)
        {
            var empty = new Dictionary<string, string>();
            Assert.False(MaintenanceSolutionOpRenderer.TryRenderScheduleCreateSql(lane, empty, out var sql, out var err));
            Assert.Equal(string.Empty, sql);
            Assert.False(string.IsNullOrEmpty(err));
        }

        [Fact]
        public void TryRenderScheduleCreateSql_CheckDbLane_NeedsNoDirectory_AndCallsIntegrityCheck()
        {
            // The job step must call DatabaseIntegrityCheck (@Databases/@CheckCommands are ITS
            // parameters). The original shipped command called CommandExecute — Ola's low-level
            // runner, which has NO such parameters — so the job failed on its first real run.
            // Caught live 2026-07-29 by the S1 deferred-verify loop; this pin now asserts the
            // proc that actually accepts these arguments, not just a shape.
            var empty = new Dictionary<string, string>();
            Assert.True(MaintenanceSolutionOpRenderer.TryRenderScheduleCreateSql(
                MaintenanceSolutionOpRenderer.ScheduleLane.CheckDbWeekly, empty, out var sql, out var err), err);
            Assert.Contains("DatabaseIntegrityCheck", sql);
            Assert.Contains("@Databases", sql);
            Assert.Contains("CHECKDB", sql);
            Assert.DoesNotContain("CommandExecute", sql);
        }

        [Theory]
        [InlineData(MaintenanceSolutionOpRenderer.ScheduleLane.FullBackupDaily, "DatabaseBackup", "'FULL'")]
        [InlineData(MaintenanceSolutionOpRenderer.ScheduleLane.DiffBackupDaily, "DatabaseBackup", "'DIFF'")]
        [InlineData(MaintenanceSolutionOpRenderer.ScheduleLane.LogBackupEvery15Min, "DatabaseBackup", "'LOG'")]
        public void TryRenderScheduleCreateSql_BackupLane_CallsCorrectBackupType(
            MaintenanceSolutionOpRenderer.ScheduleLane lane, string expectedProc, string expectedType)
        {
            var p = new Dictionary<string, string> { [MaintenanceSolutionOpRenderer.BackupDirectoryParam] = @"D:\Backups" };
            Assert.True(MaintenanceSolutionOpRenderer.TryRenderScheduleCreateSql(lane, p, out var sql, out var err), err);
            Assert.Contains(expectedProc, sql);
            Assert.Contains(expectedType, sql);
            Assert.Contains(@"D:\Backups", sql);
        }

        [Fact]
        public void TryRenderScheduleCreateSql_EscapesEmbeddedQuoteInBackupDirectory_PathInjectionRejectedByEscaping()
        {
            // A single quote in the directory must be escaped, never left able to terminate a
            // string literal early. The directory is quoted TWICE over: once as its own N'...'
            // data literal inside the @command text (QuoteDataLiteral), then the WHOLE @command
            // text is itself quoted as sp_add_jobstep's N'...' argument — so an embedded quote in
            // the directory is doubled twice (once per nesting level), exactly how SQL Server's
            // own nested-literal escaping works (verified live: SELECT un-escapes '''' back to a
            // single quote once per literal level it passes through).
            var p = new Dictionary<string, string> { [MaintenanceSolutionOpRenderer.BackupDirectoryParam] = @"D:\Backups'; DROP TABLE x; --" };
            Assert.True(MaintenanceSolutionOpRenderer.TryRenderScheduleCreateSql(
                MaintenanceSolutionOpRenderer.ScheduleLane.FullBackupDaily, p, out var sql, out _));
            Assert.Contains(@"D:\Backups''''; DROP TABLE x; --", sql);
            // No run of 3-or-fewer quotes appears where the injected text would need only 1 or 3
            // to break out early — the count must be exactly a multiple-of-4 doubling.
            Assert.DoesNotContain("Backups'; DROP TABLE x; --", sql);
            Assert.DoesNotContain("Backups''; DROP TABLE x; --", sql);
        }

        [Fact]
        public void TryRenderScheduleCreateSql_GuardsCreateWithIfNotExistsByJobName()
        {
            var p = new Dictionary<string, string> { [MaintenanceSolutionOpRenderer.BackupDirectoryParam] = @"D:\Backups" };
            Assert.True(MaintenanceSolutionOpRenderer.TryRenderScheduleCreateSql(
                MaintenanceSolutionOpRenderer.ScheduleLane.FullBackupDaily, p, out var sql, out _));
            Assert.Contains("IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'SQLTriage - Full Backup Daily')", sql);
            Assert.Contains("sp_add_job", sql);
            Assert.Contains("sp_add_jobstep", sql);
            Assert.Contains("sp_add_schedule", sql);
            Assert.Contains("sp_attach_schedule", sql);
            Assert.Contains("sp_add_jobserver", sql);
        }

        [Fact]
        public void JobNameFor_IsStableAndDistinctPerLane()
        {
            var names = new HashSet<string>();
            foreach (var lane in MaintenanceSolutionOpRenderer.AllLanes)
            {
                var name = MaintenanceSolutionOpRenderer.JobNameFor(lane);
                Assert.StartsWith("SQLTriage - ", name);
                Assert.True(names.Add(name), $"Duplicate job name for lane {lane}: {name}");
            }
            Assert.Equal(4, names.Count);
        }

        [Fact]
        public void RenderScheduleDropSql_IsGuardedAndCallsSpDeleteJob()
        {
            var sql = MaintenanceSolutionOpRenderer.RenderScheduleDropSql(MaintenanceSolutionOpRenderer.ScheduleLane.CheckDbWeekly);
            Assert.Contains("IF EXISTS", sql);
            Assert.Contains("sp_delete_job", sql);
            Assert.Contains("SQLTriage - CHECKDB Weekly", sql);
        }

        [Fact]
        public void RenderScheduleExistsSql_IsReadOnlySafe()
        {
            var sql = MaintenanceSolutionOpRenderer.RenderScheduleExistsSql(MaintenanceSolutionOpRenderer.ScheduleLane.LogBackupEvery15Min);
            Assert.True(SqlSafetyValidator.Validate(sql).IsSafe);
        }

        // ── Action routing (install vs one of the 4 lanes, one template key) ──

        [Fact]
        public void TryResolveAction_DefaultsToInstall_WhenAbsent()
        {
            Assert.True(MaintenanceSolutionOpRenderer.TryResolveAction(new Dictionary<string, string>(), out var action, out var lane, out _));
            Assert.Equal(MaintenanceSolutionOpRenderer.ActionInstall, action);
            Assert.Null(lane);
        }

        [Theory]
        [InlineData("CheckDbWeekly", MaintenanceSolutionOpRenderer.ScheduleLane.CheckDbWeekly)]
        [InlineData("FullBackupDaily", MaintenanceSolutionOpRenderer.ScheduleLane.FullBackupDaily)]
        [InlineData("DiffBackupDaily", MaintenanceSolutionOpRenderer.ScheduleLane.DiffBackupDaily)]
        [InlineData("LogBackupEvery15Min", MaintenanceSolutionOpRenderer.ScheduleLane.LogBackupEvery15Min)]
        public void TryResolveAction_ParsesEachLane(string raw, MaintenanceSolutionOpRenderer.ScheduleLane expected)
        {
            var p = new Dictionary<string, string> { [MaintenanceSolutionOpRenderer.ActionParam] = raw };
            Assert.True(MaintenanceSolutionOpRenderer.TryResolveAction(p, out _, out var lane, out _));
            Assert.Equal(expected, lane);
        }

        [Fact]
        public void TryResolveAction_UnrecognisedValue_Fails()
        {
            var p = new Dictionary<string, string> { [MaintenanceSolutionOpRenderer.ActionParam] = "NotALane" };
            Assert.False(MaintenanceSolutionOpRenderer.TryResolveAction(p, out _, out _, out var err));
            Assert.False(string.IsNullOrEmpty(err));
        }

        // ── Availability probe ────────────────────────────────────────────────

        [Fact]
        public void AgentAvailabilityProbe_ChecksEngineEditionFour()
        {
            Assert.Contains("EngineEdition", MaintenanceSolutionOpRenderer.AgentAvailabilityProbe);
            Assert.Contains("= 4", MaintenanceSolutionOpRenderer.AgentAvailabilityProbe);
        }

        // ── Safety validator: previously-unguarded CREATE TABLE/PROCEDURE now blocked ──

        [Fact]
        public void Validate_BlocksFreeFormCreateTableAndProcedure()
        {
            Assert.False(SqlSafetyValidator.Validate("CREATE TABLE dbo.Foo (id int);").IsSafe);
            Assert.False(SqlSafetyValidator.Validate("CREATE PROCEDURE dbo.Foo AS SELECT 1;").IsSafe);
            Assert.False(SqlSafetyValidator.Validate("ALTER PROCEDURE dbo.Foo AS SELECT 1;").IsSafe);
            Assert.False(SqlSafetyValidator.Validate("DROP PROCEDURE dbo.Foo;").IsSafe);
        }

        [Fact]
        public void Validate_StillAllowsTempTableCreates()
        {
            // The negative lookahead (?!#) must NOT block #temp table creates — shipped diagnostics
            // (e.g. the CHECKDB-not-run check itself) create #temp tables freely.
            Assert.True(SqlSafetyValidator.Validate("CREATE TABLE #DBCCLastGood (ID INT);").IsSafe);
        }

        [Fact]
        public void Validate_BlocksFreeFormAgentJobProcs()
        {
            Assert.False(SqlSafetyValidator.Validate("EXEC msdb.dbo.sp_add_job @job_name = N'x';").IsSafe);
            Assert.False(SqlSafetyValidator.Validate("EXEC msdb.dbo.sp_add_jobstep @job_name = N'x', @step_name = N'y';").IsSafe);
            Assert.False(SqlSafetyValidator.Validate("EXEC msdb.dbo.sp_add_schedule @schedule_name = N'x';").IsSafe);
            Assert.False(SqlSafetyValidator.Validate("EXEC msdb.dbo.sp_attach_schedule @job_name = N'x', @schedule_name = N'y';").IsSafe);
            Assert.False(SqlSafetyValidator.Validate("EXEC msdb.dbo.sp_add_jobserver @job_name = N'x';").IsSafe);
            Assert.False(SqlSafetyValidator.Validate("EXEC msdb.dbo.sp_delete_job @job_name = N'x';").IsSafe);
        }

        [Fact]
        public void Classify_InstallMaintenanceSolution_IsRemediationOnlyUnderRegisteredKey()
        {
            var sql = MaintenanceSolutionOpRenderer.RenderRepresentativeForClassification();

            Assert.Equal(SqlClassification.Blocked, SqlSafetyValidator.Classify(sql, null));
            Assert.Equal(SqlClassification.Blocked, SqlSafetyValidator.Classify(sql, new RemediationContext("NOTREGISTERED")));
            Assert.Equal(SqlClassification.Remediation, SqlSafetyValidator.Classify(sql, new RemediationContext("INSTALLMAINTENANCESOLUTION")));
        }

        [Fact]
        public void RepresentativeClassificationRender_ClassifiesAsRemediation()
        {
            var op = new RemediationOperation { OpKind = RemediationOpKind.InstallMaintenanceSolution };
            Assert.True(RemediationOpRenderer.TryRenderForClassification(op, out var sql, out var err), err);
            Assert.Equal(SqlClassification.Remediation,
                SqlSafetyValidator.Classify(sql, new RemediationContext("INSTALLMAINTENANCESOLUTION")));
        }

        // ── Shipped template ──────────────────────────────────────────────────

        [Fact]
        public void ShippedTemplate_IsWellFormed()
        {
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var t = store.TryGet("INSTALLMAINTENANCESOLUTION");
            Assert.NotNull(t);
            Assert.Equal(RemediationKind.Transactable, t!.Kind);
            Assert.NotNull(t.Operation);
            Assert.Equal(RemediationOpKind.InstallMaintenanceSolution, t.Operation!.OpKind);
            Assert.Equal(RemediationRiskClass.Standard, t.RiskClass);
            Assert.True(t.Reversible);
            Assert.False(t.ShowPowerBand);
        }

        [Fact]
        public void ShippedTemplate_ResolvesTheVerifiedCorpusCheckIds()
        {
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var t = store.TryGet("INSTALLMAINTENANCESOLUTION")!;
            var expected = new[]
            {
                "SQLT-BLITZ-DBCC-CHECKDB-NOT-PERFORMED-RECENTLY",
                "SQLT-BLITZ-CORRUPTION-CHECKS-NOT-OPTIMAL",
                "SQLT-BPCHK-00580-NO-FULL-BACKUPS",
                "SQLT-BLITZ-BACKUP-RECENCY",
                "SQLT-BLITZ-LOG-BACKUP-RECENCY",
                "SQLT-BLITZ-MSDB-BACKUP-HISTORY-NOT-PURGED",
                "SQLT-VA-MSDB-BACKUP-HISTORY-SIZE",
            };
            Assert.Equal(expected.Length, t.ResolvesCheckIds.Count);
            foreach (var id in expected) Assert.Contains(id, t.ResolvesCheckIds);
        }

        [Fact]
        public void Store_RegistersInstallMaintenanceSolution_AndTheSafetyGateAccepts()
        {
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            Assert.True(store.IsRegistered("INSTALLMAINTENANCESOLUTION"));
            const string aBlockedWrite = "CREATE TABLE dbo.CommandLog (id int);";
            Assert.Equal(SqlClassification.Remediation,
                SqlSafetyValidator.Classify(aBlockedWrite, new RemediationContext("INSTALLMAINTENANCESOLUTION"), store.RegisteredKeys()));
        }
    }
}
