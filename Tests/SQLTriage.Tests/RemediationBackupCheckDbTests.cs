/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Lane S5 — gated ONE-SHOT Backup-NOW and CHECKDB-NOW. The highest-blast-radius lane
    /// shipped: the gates ARE the feature. Pins: (1) the resource-gate math (insufficient
    /// free space -> refuse; sufficient -> allow) for both Backup and CHECKDB, (2) the
    /// confirm-token gate is required and exact, (3) the backup directory/database-name
    /// identifier guards reject injection attempts, (4) SqlSafetyValidator blocks free-form
    /// BACKUP DATABASE/LOG and DBCC CHECKDB, promoting them to Remediation ONLY under the two
    /// registered keys, (5) the verify-read shape (msdb.dbo.backupset lookback), (6) the
    /// shipped templates carry NOT-reversible + the verified corpus check ids.
    /// </summary>
    public class RemediationBackupCheckDbTests
    {
        private static Dictionary<string, string> BackupParams(
            string db = "AppDb", string dir = @"C:\Backups", bool confirm = true, string? allowSystem = null, string? checksum = null)
        {
            var p = new Dictionary<string, string>
            {
                [BackupCheckDbOpRenderer.DatabaseNameParam] = db,
                [BackupCheckDbOpRenderer.BackupDirectoryParam] = dir,
            };
            if (confirm) p[BackupCheckDbOpRenderer.ConfirmLargeOperationParam] = "true";
            if (allowSystem != null) p[BackupCheckDbOpRenderer.AllowSystemDatabaseParam] = allowSystem;
            if (checksum != null) p[BackupCheckDbOpRenderer.UseChecksumParam] = checksum;
            return p;
        }

        private static Dictionary<string, string> CheckDbParams(
            string db = "AppDb", bool confirm = true, string? physicalOnly = null)
        {
            var p = new Dictionary<string, string> { [BackupCheckDbOpRenderer.DatabaseNameParam] = db };
            if (confirm) p[BackupCheckDbOpRenderer.ConfirmLargeOperationParam] = "true";
            if (physicalOnly != null) p[BackupCheckDbOpRenderer.PhysicalOnlyParam] = physicalOnly;
            return p;
        }

        // ── Resource-gate math: Backup ──────────────────────────────────────

        [Fact]
        public void EvaluateBackupResourceGate_InsufficientFreeSpace_Refuses()
        {
            // 100 GB estimated, headroom factor 1.2 -> needs 120 GB; drive has only 50 GB.
            long estimated = 100L * 1024 * 1024 * 1024;
            long available = 50L * 1024 * 1024 * 1024;
            var result = BackupCheckDbOpRenderer.EvaluateBackupResourceGate(estimated, available, "D:");
            Assert.False(result.Allowed);
            Assert.Contains("insufficient", result.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void EvaluateBackupResourceGate_SufficientFreeSpace_Allows()
        {
            long estimated = 100L * 1024 * 1024 * 1024;
            long available = 200L * 1024 * 1024 * 1024;
            var result = BackupCheckDbOpRenderer.EvaluateBackupResourceGate(estimated, available, "D:");
            Assert.True(result.Allowed);
        }

        [Fact]
        public void EvaluateBackupResourceGate_ExactlyAtHeadroomBoundary_Allows()
        {
            // available == estimated * 1.2 exactly -> must allow (>=, not >).
            long estimated = 100L * 1024 * 1024 * 1024;
            long available = (long)(estimated * BackupCheckDbOpRenderer.BackupHeadroomFactor);
            var result = BackupCheckDbOpRenderer.EvaluateBackupResourceGate(estimated, available, "D:");
            Assert.True(result.Allowed);
        }

        [Fact]
        public void EvaluateBackupResourceGate_OneByteShortOfHeadroom_Refuses()
        {
            long estimated = 100L * 1024 * 1024 * 1024;
            long required = (long)Math.Ceiling(estimated * BackupCheckDbOpRenderer.BackupHeadroomFactor);
            var result = BackupCheckDbOpRenderer.EvaluateBackupResourceGate(estimated, required - 1, "D:");
            Assert.False(result.Allowed);
        }

        // ── Resource-gate math: CHECKDB ──────────────────────────────────────

        [Fact]
        public void EvaluateCheckDbResourceGate_BelowHardFloor_Refuses()
        {
            // DB is 1000 GB; hard floor = 5% = 50 GB; drive has only 10 GB.
            long dbSize = 1000L * 1024 * 1024 * 1024;
            long available = 10L * 1024 * 1024 * 1024;
            var result = BackupCheckDbOpRenderer.EvaluateCheckDbResourceGate(dbSize, available, "E:");
            Assert.False(result.Allowed);
        }

        [Fact]
        public void EvaluateCheckDbResourceGate_BetweenFloorAndRecommended_AllowsWithWarning()
        {
            // DB is 1000 GB; floor=50GB, recommended=300GB; drive has 100GB (above floor, below recommended).
            long dbSize = 1000L * 1024 * 1024 * 1024;
            long available = 100L * 1024 * 1024 * 1024;
            var result = BackupCheckDbOpRenderer.EvaluateCheckDbResourceGate(dbSize, available, "E:");
            Assert.True(result.Allowed);
            Assert.True(result.Warning);
        }

        [Fact]
        public void EvaluateCheckDbResourceGate_AboveRecommended_AllowsCleanly()
        {
            long dbSize = 1000L * 1024 * 1024 * 1024;
            long available = 500L * 1024 * 1024 * 1024;
            var result = BackupCheckDbOpRenderer.EvaluateCheckDbResourceGate(dbSize, available, "E:");
            Assert.True(result.Allowed);
            Assert.False(result.Warning);
        }

        [Fact]
        public void EvaluateCheckDbResourceGate_ZeroOrNegativeDbSize_FailsClosed()
        {
            // Size could not be determined (a read failure upstream defaulted to 0 — never a real
            // empty online DB). Fail-closed discipline: refuse rather than run without a space estimate.
            var result = BackupCheckDbOpRenderer.EvaluateCheckDbResourceGate(0, 1, "E:");
            Assert.False(result.Allowed);
        }

        [Fact]
        public void EvaluateBackupResourceGate_ZeroEstimate_FailsClosed()
        {
            // A 0 backup-size estimate means the space query returned no usable row; refuse rather
            // than let required=0 pass the comparison and run an unbounded backup.
            var result = BackupCheckDbOpRenderer.EvaluateBackupResourceGate(0, long.MaxValue, "F:");
            Assert.False(result.Allowed);
        }

        // ── Confirm-token gate ───────────────────────────────────────────────

        [Theory]
        [InlineData(null)]
        [InlineData("false")]
        [InlineData("1")]
        [InlineData("yes")]
        [InlineData("")]
        public void IsConfirmed_AnythingButExactTrue_IsFalse(string? value)
        {
            var p = new Dictionary<string, string>();
            if (value != null) p[BackupCheckDbOpRenderer.ConfirmLargeOperationParam] = value;
            Assert.False(BackupCheckDbOpRenderer.IsConfirmed(p));
        }

        [Theory]
        [InlineData("true")]
        [InlineData("TRUE")]
        [InlineData("True")]
        public void IsConfirmed_ExactTrueCaseInsensitive_IsTrue(string value)
        {
            var p = new Dictionary<string, string> { [BackupCheckDbOpRenderer.ConfirmLargeOperationParam] = value };
            Assert.True(BackupCheckDbOpRenderer.IsConfirmed(p));
        }

        [Fact]
        public void IsConfirmed_MissingParameter_IsFalse()
        {
            Assert.False(BackupCheckDbOpRenderer.IsConfirmed(new Dictionary<string, string>()));
        }

        // ── Backup-path / database-name injection guard ─────────────────────

        [Theory]
        [InlineData("Orders]); DROP TABLE Users; --")]
        [InlineData("Orders; DROP TABLE Users")]
        [InlineData("Orders'")]
        [InlineData("dbo.Orders")]
        [InlineData("")]
        public void TryResolveBackupSpec_RejectsUnsafeDatabaseName(string maliciousDb)
        {
            Assert.False(BackupCheckDbOpRenderer.TryResolveBackupSpec(BackupParams(db: maliciousDb), out _, out var err));
            Assert.False(string.IsNullOrEmpty(err));
        }

        [Fact]
        public void TryResolveBackupSpec_RejectsEmptyDirectory()
        {
            Assert.False(BackupCheckDbOpRenderer.TryResolveBackupSpec(BackupParams(dir: ""), out _, out var err));
            Assert.Contains("directory", err, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TryResolveBackupSpec_RejectsWhitespaceOnlyDirectory()
        {
            Assert.False(BackupCheckDbOpRenderer.TryResolveBackupSpec(BackupParams(dir: "   "), out _, out _));
        }

        [Fact]
        public void TryResolveBackupSpec_RejectsTempdb()
        {
            Assert.False(BackupCheckDbOpRenderer.TryResolveBackupSpec(BackupParams(db: "tempdb"), out _, out var err));
            Assert.Contains("tempdb", err, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("master")]
        [InlineData("model")]
        [InlineData("msdb")]
        public void TryResolveBackupSpec_SystemDatabase_RequiresExplicitAllow(string sysDb)
        {
            Assert.False(BackupCheckDbOpRenderer.TryResolveBackupSpec(BackupParams(db: sysDb), out _, out var err));
            Assert.Contains("system database", err, StringComparison.OrdinalIgnoreCase);

            Assert.True(BackupCheckDbOpRenderer.TryResolveBackupSpec(BackupParams(db: sysDb, allowSystem: "true"), out var spec, out var err2), err2);
            Assert.True(spec.AllowSystemDatabase);
        }

        [Fact]
        public void TryRenderBackupApply_QuotesDirectoryAndDatabaseSafely_AndEscapesEmbeddedQuote()
        {
            // A directory containing a single quote must not be able to terminate the N'...' literal early.
            var p = BackupParams(dir: @"C:\Ba'ckups");
            Assert.True(BackupCheckDbOpRenderer.TryRenderBackupApply(p, out var sql, out var err, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)), err);
            Assert.Contains("[AppDb]", sql);
            Assert.Contains("N'C:\\Ba''ckups\\AppDb_20260102_030405.bak'", sql);
            Assert.Contains("CHECKSUM", sql);
            Assert.Contains("BACKUP DATABASE", sql);
        }

        [Fact]
        public void TryRenderBackupApply_ChecksumOptOut_OmitsChecksumClause()
        {
            var p = BackupParams(checksum: "false");
            Assert.True(BackupCheckDbOpRenderer.TryRenderBackupApply(p, out var sql, out _));
            Assert.DoesNotContain("CHECKSUM", sql);
        }

        [Fact]
        public void TryRenderCheckDbApply_NeverEmitsTablockOrRepair()
        {
            Assert.True(BackupCheckDbOpRenderer.TryRenderCheckDbApply(CheckDbParams(), out var sql, out var err), err);
            Assert.Contains("DBCC CHECKDB", sql);
            Assert.DoesNotContain("TABLOCK", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("REPAIR", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[AppDb]", sql);
        }

        [Fact]
        public void TryRenderCheckDbApply_PhysicalOnly_AddsClause()
        {
            Assert.True(BackupCheckDbOpRenderer.TryRenderCheckDbApply(CheckDbParams(physicalOnly: "true"), out var sql, out _));
            Assert.Contains("PHYSICAL_ONLY", sql);
        }

        [Theory]
        [InlineData("Orders]); DROP TABLE Users; --")]
        [InlineData("Orders'")]
        [InlineData("")]
        public void TryResolveCheckDbSpec_RejectsUnsafeDatabaseName(string maliciousDb)
        {
            Assert.False(BackupCheckDbOpRenderer.TryResolveCheckDbSpec(CheckDbParams(db: maliciousDb), out _, out var err));
            Assert.False(string.IsNullOrEmpty(err));
        }

        // ── Verify-read shape ────────────────────────────────────────────────

        [Fact]
        public void TryRenderBackupVerifyRead_IsReadOnlyAndTargetsBackupset()
        {
            Assert.True(BackupCheckDbOpRenderer.TryRenderBackupVerifyRead("AppDb", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), out var sql, out var err), err);
            Assert.Contains("msdb.dbo.backupset", sql);
            Assert.Contains("type = 'D'", sql);
            Assert.Contains("backup_finish_date", sql);
            Assert.True(SqlSafetyValidator.Validate(sql).IsSafe);
        }

        [Fact]
        public void TryRenderDatabaseOnlineProbe_IsReadOnly()
        {
            Assert.True(BackupCheckDbOpRenderer.TryRenderDatabaseOnlineProbe("AppDb", out var sql, out var err), err);
            Assert.True(SqlSafetyValidator.Validate(sql).IsSafe);
            Assert.Contains("state = 0", sql);
        }

        // ── SqlSafetyValidator: free-form blocked, gated-only promotion ──────

        [Fact]
        public void Validate_BlocksFreeFormBackupAndCheckDb()
        {
            Assert.False(SqlSafetyValidator.Validate("BACKUP DATABASE AppDb TO DISK = N'C:\\x.bak';").IsSafe);
            Assert.False(SqlSafetyValidator.Validate("BACKUP LOG AppDb TO DISK = N'C:\\x.trn';").IsSafe);
            Assert.False(SqlSafetyValidator.Validate("DBCC CHECKDB(AppDb) WITH NO_INFOMSGS;").IsSafe);
        }

        [Fact]
        public void Classify_BackupDatabaseNow_IsRemediationOnlyUnderRegisteredKey()
        {
            var sql = BackupCheckDbOpRenderer.RenderRepresentativeBackupForClassification();
            var registered = new HashSet<string> { "BACKUPDATABASENOW" };

            var withKey = SqlSafetyValidator.Classify(sql, new RemediationContext("BACKUPDATABASENOW"), registered);
            Assert.Equal(SqlClassification.Remediation, withKey);

            var withoutContext = SqlSafetyValidator.Classify(sql, null, registered);
            Assert.Equal(SqlClassification.Blocked, withoutContext);

            var wrongKey = SqlSafetyValidator.Classify(sql, new RemediationContext("SOMETHINGELSE"), registered);
            Assert.Equal(SqlClassification.Blocked, wrongKey);
        }

        [Fact]
        public void Classify_CheckDbNow_IsRemediationOnlyUnderRegisteredKey()
        {
            var sql = BackupCheckDbOpRenderer.RenderRepresentativeCheckDbForClassification();
            var registered = new HashSet<string> { "CHECKDBNOW" };

            Assert.Equal(SqlClassification.Remediation,
                SqlSafetyValidator.Classify(sql, new RemediationContext("CHECKDBNOW"), registered));
            Assert.Equal(SqlClassification.Blocked,
                SqlSafetyValidator.Classify(sql, null, registered));
        }

        // ── RemediationOpRenderer.TryRenderForClassification wiring ─────────

        [Fact]
        public void TryRenderForClassification_BackupDatabaseNow_RendersRepresentativeBackup()
        {
            var op = new RemediationOperation { OpKind = RemediationOpKind.BackupDatabaseNow };
            Assert.True(RemediationOpRenderer.TryRenderForClassification(op, out var sql, out var err), err);
            Assert.Contains("BACKUP DATABASE", sql);
        }

        [Fact]
        public void TryRenderForClassification_CheckDbNow_RendersRepresentativeCheckDb()
        {
            var op = new RemediationOperation { OpKind = RemediationOpKind.CheckDbNow };
            Assert.True(RemediationOpRenderer.TryRenderForClassification(op, out var sql, out var err), err);
            Assert.Contains("DBCC CHECKDB", sql);
        }

        // ── Shipped templates: registration, RiskClass, Reversible, corpus ids ──

        [Fact]
        public void ShippedTemplates_BackupAndCheckDbNow_AreRegisteredSensitiveAndNotReversible()
        {
            var store = new RemediationTemplateStore(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RemediationTemplateStore>.Instance,
                overlayPathOverride: System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sqlt-s5-" + Guid.NewGuid().ToString("N") + ".json"));

            var backup = store.TryGet("BACKUPDATABASENOW");
            Assert.NotNull(backup);
            Assert.Equal(RemediationRiskClass.Sensitive, backup!.RiskClass);
            Assert.False(backup.Reversible);
            Assert.Equal(RemediationOpKind.BackupDatabaseNow, backup.Operation?.OpKind);
            Assert.Contains("SQLT-BPCHK-00580-NO-FULL-BACKUPS", backup.ResolvesCheckIds);

            var checkdb = store.TryGet("CHECKDBNOW");
            Assert.NotNull(checkdb);
            Assert.Equal(RemediationRiskClass.Sensitive, checkdb!.RiskClass);
            Assert.False(checkdb.Reversible);
            Assert.Equal(RemediationOpKind.CheckDbNow, checkdb.Operation?.OpKind);
            Assert.Contains("SQLT-BLITZ-DBCC-CHECKDB-NOT-PERFORMED-RECENTLY", checkdb.ResolvesCheckIds);
            // SQLT-BPCHK-DBCC-CHECKDB-STATUS is deliberately NOT claimed here — it's already
            // mapped to the review-only MaintenanceGenerator.CheckDb path; see the NOTE in
            // RemediationTemplateStore.SeedShippedTemplates and CheckResolutionLookupTests.
            Assert.DoesNotContain("SQLT-BPCHK-DBCC-CHECKDB-STATUS", checkdb.ResolvesCheckIds);
        }

        [Fact]
        public void DurationHint_NeverThrows_ForZeroOrHugeSize()
        {
            Assert.False(string.IsNullOrEmpty(BackupCheckDbOpRenderer.DurationHint(0, isCheckDb: false)));
            Assert.False(string.IsNullOrEmpty(BackupCheckDbOpRenderer.DurationHint(long.MaxValue / 2, isCheckDb: true)));
        }
    }
}
