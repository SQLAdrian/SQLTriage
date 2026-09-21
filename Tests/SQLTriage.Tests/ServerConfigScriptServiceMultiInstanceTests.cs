/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Licensing;
using SQLTriage.Data.Services.Remediation;
using SQLTriage.Tests.Licensing;
using Xunit;

namespace SQLTriage.Tests;

/// <summary>
/// The multi-instance change-control preview added to <see cref="ServerConfigScriptService"/>
/// (2026-08-20 item 5, Adrian's accepted spec): one change-control run per selected instance
/// against an EXPLICIT connection string (never the process-wide GlobalInstanceSelector), each
/// exported as its own text file, and refused with a named reason when the install's licence does
/// not carry the Server Configuration feature set (DECISIONS 2026-08-05 item 5).
///
/// <para>Extended (2026-08-20 extension round, Lane H) with <see
/// cref="ServerConfigScriptService.RunApplyForInstanceAsync"/> — the apply counterpart Adrian
/// ruled as its own lane. Same shared core as preview (<c>RunScriptCoreAsync</c>), same licence
/// gate, distinct export suffix (<c>-applied</c>) and audit bundle type
/// (<c>"RemediationApply"</c>).</para>
///
/// <para><b>Not covered here</b> (needs a live server — the verify phase's job, not this
/// builder's): that a run against a REAL reachable instance actually captures rows, applies them
/// and connects. Also not covered here: the <c>/server-configuration</c> page's own sequencing and
/// continue-but-record failure-policy loop (<c>RunMultiInstanceApply</c>) — it lives in a Razor
/// <c>@code</c> block with no component-test harness (bUnit) in this repo, so it is exercised
/// through this class's proxy instead: <see cref="ServerConfigScriptService.RunApplyForInstanceAsync"/>
/// treats each instance independently and never leaks one instance's identity into another's
/// result, which is what the page's loop relies on to keep every instance's outcome separate and
/// non-blocking. What IS covered: the pure seams (export formatter, file-name builder, write-or-
/// skip, <see cref="ServerConfigScriptService.InstanceApplyResult.HasFailedRows"/>) and the refusal
/// path, which never opens a connection at all and is therefore deterministic in every build
/// profile.</para>
/// </summary>
public sealed class ServerConfigScriptServiceMultiInstanceTests : IDisposable
{
    private readonly string _tempDir;

    public ServerConfigScriptServiceMultiInstanceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sqlt-mc-changecontrol-tests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* test cleanup; ignore */ }
    }

    private static BundleFeatures Features(bool remediation) =>
        new(false, false, false, Array.Empty<int>(), Remediation: remediation);

    private static ServerConfigScriptService NewService(FakeBundleAccessor accessor) =>
        new(
            null!,
            NullLogger<ServerConfigScriptService>.Instance,
            new BundleBackedRemediationCapability(accessor),
            accessor);

    // ── RunPreviewForInstanceAsync: refuses BEFORE touching anything, per instance ──────────────

    [Fact]
    public async Task RunPreviewForInstanceAsync_Unlicensed_RefusesWithTheNamedReason_AndNeverOpensAConnection()
    {
        // Deliberately garbage connection string: if the refusal ever stopped short-circuiting,
        // opening it would throw rather than quietly pass.
        var unlicensed = new FakeBundleAccessor { Tier = Tier.Full, Features = Features(false) };
        var service = NewService(unlicensed);

        var result = await service.RunPreviewForInstanceAsync(
            "SQL01", "Server=nope;Database=master;", _tempDir);

        Assert.True(result.Refused);
        Assert.Equal(
            ServerConfigSuiteGate.DescribeRefusal(ServerConfigSuiteState.ClaimNotGranted),
            result.RefusalReason);
        Assert.Empty(result.Rows);
        Assert.Null(result.ExportedPath);
        Assert.Equal("SQL01", result.InstanceName);

        // No export was attempted for a refused instance.
        Assert.False(Directory.Exists(_tempDir));
    }

    [Fact]
    public async Task RunPreviewForInstanceAsync_LockedBundle_RefusesWithTheNoLicenceReason()
    {
        var locked = new FakeBundleAccessor { Tier = Tier.Full, Features = Features(true) };
        locked.SetLocked();
        var service = NewService(locked);

        var result = await service.RunPreviewForInstanceAsync("SQL02", "Server=nope;", _tempDir);

        Assert.True(result.Refused);
        Assert.Equal(
            ServerConfigSuiteGate.DescribeRefusal(ServerConfigSuiteState.NoLicenceUnlocked),
            result.RefusalReason);
    }

    [Fact]
    public async Task RunPreviewForInstanceAsync_FreeTier_RefusesEvenCarryingTheClaim()
    {
        var free = new FakeBundleAccessor { Tier = Tier.Free, Features = Features(true) };
        var service = NewService(free);

        var result = await service.RunPreviewForInstanceAsync("SQL03", "Server=nope;", _tempDir);

        Assert.True(result.Refused);
        Assert.Equal(
            ServerConfigSuiteGate.DescribeRefusal(ServerConfigSuiteState.FreeTier),
            result.RefusalReason);
    }

    [Fact]
    public async Task RunPreviewForInstanceAsync_TwoInstances_AreRefusedIndependently_EachNamingItself()
    {
        // Same install, same (un)licensed bundle -- both instances refuse together, but the
        // result each gets back names ITSELF, never the other instance's identity.
        var unlicensed = new FakeBundleAccessor { Tier = Tier.Full, Features = Features(false) };
        var service = NewService(unlicensed);

        var first = await service.RunPreviewForInstanceAsync("SQL-A", "Server=a;", _tempDir);
        var second = await service.RunPreviewForInstanceAsync("SQL-B", "Server=b;", _tempDir);

        Assert.Equal("SQL-A", first.InstanceName);
        Assert.Equal("SQL-B", second.InstanceName);
        Assert.True(first.Refused);
        Assert.True(second.Refused);
    }

    // ── Export formatter ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void FormatChangeControlExport_NoRows_SaysSo()
    {
        var text = ServerConfigScriptService.FormatChangeControlExport(
            "SQL01", new DateTime(2026, 8, 20, 9, 0, 0), Array.Empty<ServerConfigScriptService.ConfigCheckRow>());

        Assert.Contains("SQL01", text, StringComparison.Ordinal);
        Assert.Contains("No change-control rows were captured.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatChangeControlExport_RendersEveryRow()
    {
        var rows = new[]
        {
            new ServerConfigScriptService.ConfigCheckRow(
                1, new DateTime(2026, 8, 20), "PLANNED", "sp_configure", "max server memory",
                "2147483647", "16384", "cap to 16GB"),
            new ServerConfigScriptService.ConfigCheckRow(
                2, new DateTime(2026, 8, 20), "SKIPPED", "Trace Flags", "3226",
                null, null, "already set"),
        };

        var text = ServerConfigScriptService.FormatChangeControlExport("SQL01", DateTime.Now, rows);

        Assert.Contains("[PLANNED] sp_configure / max server memory: 2147483647 -> 16384 (cap to 16GB)", text, StringComparison.Ordinal);
        Assert.Contains("[SKIPPED] Trace Flags / 3226", text, StringComparison.Ordinal);
    }

    // ── File-name builder (same instance+timestamp convention as the apply-mode rollback export) ──

    [Fact]
    public void BuildChangeControlFileName_MatchesTheInstanceStampConvention()
    {
        var stamp = new DateTime(2026, 8, 20, 14, 30, 5);
        Assert.Equal(
            @"MSI_OLD2017_20260820-143005_changecontrol.txt",
            ServerConfigScriptService.BuildChangeControlFileName(@"MSI\OLD2017", stamp));
    }

    [Fact]
    public void SanitizeInstanceNameForFile_ReplacesInvalidFileNameChars()
    {
        Assert.Equal("MSI_OLD2017", ServerConfigScriptService.SanitizeInstanceNameForFile(@"MSI\OLD2017"));
    }

    // ── Writer: writes the expected file ─────────────────────────────────────────────────────

    [Fact]
    public async Task WriteChangeControlExportAsync_WritesTheFile_UnderTheExpectedName()
    {
        var path = await ServerConfigScriptService.WriteChangeControlExportAsync(
            _tempDir, @"MSI\OLD2017", "Change-control preview -- MSI\\OLD2017\r\nNo change-control rows were captured.\r\n");

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.StartsWith("MSI_OLD2017_", Path.GetFileName(path), StringComparison.Ordinal);
        Assert.EndsWith("_changecontrol.txt", Path.GetFileName(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteChangeControlExportAsync_CreatesTheOutputDirectory_WhenAbsent()
    {
        Assert.False(Directory.Exists(_tempDir));
        var path = await ServerConfigScriptService.WriteChangeControlExportAsync(_tempDir, "SRV1", "some text");
        Assert.True(Directory.Exists(_tempDir));
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WriteChangeControlExportAsync_WritesNothing_WhenThereIsNoText(string? emptyText)
    {
        var path = await ServerConfigScriptService.WriteChangeControlExportAsync(_tempDir, "SRV1", emptyText);

        Assert.Null(path);
        Assert.False(Directory.Exists(_tempDir));
    }

    // ── RunApplyForInstanceAsync: refuses BEFORE touching anything, per instance (Lane H) ──────

    [Fact]
    public async Task RunApplyForInstanceAsync_Unlicensed_RefusesWithTheNamedReason_AndNeverOpensAConnection()
    {
        // Same garbage-connection-string proof as the preview refusal test: if the gate ever
        // stopped short-circuiting BEFORE a connection is opened, this would throw rather than
        // quietly refuse -- and an apply that silently fell through to a connection attempt would
        // be far worse than a preview doing the same.
        var unlicensed = new FakeBundleAccessor { Tier = Tier.Full, Features = Features(false) };
        var service = NewService(unlicensed);

        var result = await service.RunApplyForInstanceAsync(
            "SQL01", "Server=nope;Database=master;", _tempDir);

        Assert.True(result.Refused);
        Assert.Equal(
            ServerConfigSuiteGate.DescribeRefusal(ServerConfigSuiteState.ClaimNotGranted),
            result.RefusalReason);
        Assert.Empty(result.Rows);
        Assert.Null(result.ExportedPath);
        Assert.Equal("SQL01", result.InstanceName);
        Assert.False(result.HasFailedRows);

        // No export was attempted for a refused instance -- apply never even created the
        // output directory, exactly like preview.
        Assert.False(Directory.Exists(_tempDir));
    }

    [Fact]
    public async Task RunApplyForInstanceAsync_LockedBundle_RefusesWithTheNoLicenceReason()
    {
        var locked = new FakeBundleAccessor { Tier = Tier.Full, Features = Features(true) };
        locked.SetLocked();
        var service = NewService(locked);

        var result = await service.RunApplyForInstanceAsync("SQL02", "Server=nope;", _tempDir);

        Assert.True(result.Refused);
        Assert.Equal(
            ServerConfigSuiteGate.DescribeRefusal(ServerConfigSuiteState.NoLicenceUnlocked),
            result.RefusalReason);
    }

    [Fact]
    public async Task RunApplyForInstanceAsync_FreeTier_RefusesEvenCarryingTheClaim()
    {
        var free = new FakeBundleAccessor { Tier = Tier.Free, Features = Features(true) };
        var service = NewService(free);

        var result = await service.RunApplyForInstanceAsync("SQL03", "Server=nope;", _tempDir);

        Assert.True(result.Refused);
        Assert.Equal(
            ServerConfigSuiteGate.DescribeRefusal(ServerConfigSuiteState.FreeTier),
            result.RefusalReason);
    }

    [Fact]
    public async Task RunApplyForInstanceAsync_TwoInstances_AreRefusedIndependently_EachNamingItself()
    {
        // Proxy for the page's sequencing loop (no component-test harness exists for the Razor
        // @code block -- see the class doc): the service treats each call independently, so two
        // instances called back-to-back on the same install never cross-contaminate identity, and
        // a refusal on the first cannot poison or short-circuit the second.
        var unlicensed = new FakeBundleAccessor { Tier = Tier.Full, Features = Features(false) };
        var service = NewService(unlicensed);

        var first = await service.RunApplyForInstanceAsync("SQL-A", "Server=a;", _tempDir);
        var second = await service.RunApplyForInstanceAsync("SQL-B", "Server=b;", _tempDir);

        Assert.Equal("SQL-A", first.InstanceName);
        Assert.Equal("SQL-B", second.InstanceName);
        Assert.True(first.Refused);
        Assert.True(second.Refused);
    }

    // NOTE: a "script missing" refusal test (mirroring the licence refusals above) is not written
    // here -- ScriptPath is computed from AppContext.BaseDirectory with no seam to override it,
    // and this test project ships the real ConfigScripts payload under the full profile, so
    // ScriptExists is genuinely true in every test run. Lane E's preview tests carry the same gap
    // for the same reason. The refusal branch is READ, not run: RunApplyForInstanceAsync reuses
    // the identical `if (!ScriptExists)` shape RunPreviewForInstanceAsync already has.

    // ── Export formatter: apply vs preview header (Lane H) ──────────────────────────────────────

    [Fact]
    public void FormatChangeControlExport_Apply_HeaderSaysApply_NotPreview()
    {
        var text = ServerConfigScriptService.FormatChangeControlExport(
            "SQL01", new DateTime(2026, 8, 20, 9, 0, 0), Array.Empty<ServerConfigScriptService.ConfigCheckRow>(), apply: true);

        Assert.Contains("Change-control apply -- SQL01", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Change-control preview", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatChangeControlExport_DefaultsToPreview_ExistingCallSitesUnchanged()
    {
        // The 3-arg call every existing preview call site uses must still say "preview" -- apply
        // defaulting to false is what keeps FormatChangeControlExport_NoRows_SaysSo /
        // FormatChangeControlExport_RendersEveryRow above passing unmodified.
        var text = ServerConfigScriptService.FormatChangeControlExport(
            "SQL01", new DateTime(2026, 8, 20, 9, 0, 0), Array.Empty<ServerConfigScriptService.ConfigCheckRow>());

        Assert.Contains("Change-control preview -- SQL01", text, StringComparison.Ordinal);
    }

    // ── File-name builder: the -applied suffix distinguishes apply from preview (Lane H) ────────

    [Fact]
    public void BuildChangeControlFileName_Apply_UsesTheAppliedSuffix()
    {
        var stamp = new DateTime(2026, 8, 20, 14, 30, 5);
        Assert.Equal(
            @"MSI_OLD2017_20260820-143005_changecontrol-applied.txt",
            ServerConfigScriptService.BuildChangeControlFileName(@"MSI\OLD2017", stamp, apply: true));
    }

    [Fact]
    public void BuildChangeControlFileName_DefaultsToPreview_ExistingCallSitesUnchanged()
    {
        var stamp = new DateTime(2026, 8, 20, 14, 30, 5);
        Assert.Equal(
            @"MSI_OLD2017_20260820-143005_changecontrol.txt",
            ServerConfigScriptService.BuildChangeControlFileName(@"MSI\OLD2017", stamp));
    }

    [Fact]
    public void BuildChangeControlFileName_PreviewAndApply_NeverCollide_ForTheSameInstanceAndSecond()
    {
        var stamp = new DateTime(2026, 8, 20, 14, 30, 5);
        var preview = ServerConfigScriptService.BuildChangeControlFileName("SQL01", stamp, apply: false);
        var applied = ServerConfigScriptService.BuildChangeControlFileName("SQL01", stamp, apply: true);

        Assert.NotEqual(preview, applied);
    }

    // ── Writer: apply writes under the -applied suffix (Lane H) ─────────────────────────────────

    [Fact]
    public async Task WriteChangeControlExportAsync_Apply_WritesUnderTheAppliedSuffix()
    {
        var path = await ServerConfigScriptService.WriteChangeControlExportAsync(
            _tempDir, @"MSI\OLD2017", "Change-control apply -- MSI\\OLD2017\r\nNo change-control rows were captured.\r\n", apply: true);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.StartsWith("MSI_OLD2017_", Path.GetFileName(path), StringComparison.Ordinal);
        Assert.EndsWith("_changecontrol-applied.txt", Path.GetFileName(path), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WriteChangeControlExportAsync_Apply_WritesNothing_WhenThereIsNoText(string? emptyText)
    {
        var path = await ServerConfigScriptService.WriteChangeControlExportAsync(_tempDir, "SRV1", emptyText, apply: true);

        Assert.Null(path);
        Assert.False(Directory.Exists(_tempDir));
    }

    // ── InstanceApplyResult.HasFailedRows: the Done-vs-Failed signal the page reads (Lane H) ────

    private static ServerConfigScriptService.ConfigCheckRow Row(string mode) =>
        new(1, new DateTime(2026, 8, 20), mode, "sp_configure", "max server memory", "2147483647", "16384", null);

    [Fact]
    public void InstanceApplyResult_HasFailedRows_TrueWhenAnyRowIsFailed()
    {
        var result = new ServerConfigScriptService.InstanceApplyResult(
            "SQL01", false, null, new[] { Row("IMPLEMENTING"), Row("FAILED"), Row("SKIPPED") }, "path");

        Assert.True(result.HasFailedRows);
    }

    [Fact]
    public void InstanceApplyResult_HasFailedRows_FalseWhenNoRowIsFailed()
    {
        var result = new ServerConfigScriptService.InstanceApplyResult(
            "SQL01", false, null, new[] { Row("IMPLEMENTING"), Row("SKIPPED"), Row("INFO") }, "path");

        Assert.False(result.HasFailedRows);
    }

    [Fact]
    public void InstanceApplyResult_HasFailedRows_FalseWhenRowsAreEmpty()
    {
        var result = new ServerConfigScriptService.InstanceApplyResult(
            "SQL01", false, null, Array.Empty<ServerConfigScriptService.ConfigCheckRow>(), "path");

        Assert.False(result.HasFailedRows);
    }

    [Fact]
    public void InstanceApplyResult_HasFailedRows_IsCaseInsensitiveOnMode()
    {
        // The script's own Mode literal is always upper-case ('FAILED'), but the check itself is
        // ordinal-case-insensitive by construction -- pinned so a future refactor cannot quietly
        // narrow it to an exact-case match.
        var result = new ServerConfigScriptService.InstanceApplyResult(
            "SQL01", false, null, new[] { Row("failed") }, "path");

        Assert.True(result.HasFailedRows);
    }

    // ── H-fix D1(a): operator-name injection is closed AT THE SUBSTITUTION SITE (2026-08-20) ──
    //
    // Live-proven blocker: operatorName "DBA\r\nGO\r\nZZ" (the page's own free-text input,
    // substituted BEFORE SplitOnGo) killed every batch, yet the run reported Refused=False/0 rows,
    // an export reading "No change-control rows were captured.", RemediationApply Success:True, and
    // a UI showing Done. These tests exercise the FIX (SanitizeOperatorNameForSubstitution) as a
    // pure seam and re-run its output through the REAL SqlGoBatchSplitter, so the proof is not just
    // "the sanitizer strips characters" but "the exact substitution this class performs can no
    // longer introduce a batch boundary".

    [Fact]
    public void SanitizeOperatorNameForSubstitution_StripsCrAndLf()
    {
        Assert.Equal("DBAGOZZ", ServerConfigScriptService.SanitizeOperatorNameForSubstitution("DBA\r\nGO\r\nZZ"));
    }

    [Fact]
    public void SanitizeOperatorNameForSubstitution_LeavesAnOrdinaryNameUnchanged()
    {
        Assert.Equal("DBA", ServerConfigScriptService.SanitizeOperatorNameForSubstitution("DBA"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SanitizeOperatorNameForSubstitution_PassesThroughNullOrEmptyUnchanged(string? input)
    {
        Assert.Equal(input, ServerConfigScriptService.SanitizeOperatorNameForSubstitution(input));
    }

    [Fact]
    public void SanitizedOperatorName_SplicedIntoTheRealSubstitutionShape_CanNeverIntroduceAnExtraBatch()
    {
        // Reconstructs the EXACT text RunAsync/RunScriptCoreAsync produce: the sanitized name is
        // Regex-escaped for the N'...' literal (single-quote doubling only -- CR/LF is already gone
        // by the time that happens) and spliced into a SET @OperatorName = N'...' line, followed by
        // the script's own real GO separator and one more statement.
        const string maliciousOperatorName = "DBA\r\nGO\r\nZZ";
        var safe = ServerConfigScriptService.SanitizeOperatorNameForSubstitution(maliciousOperatorName)!;
        var sql = "SET @OperatorName = N'" + safe.Replace("'", "''") + "'\r\nGO\r\nSELECT 1\r\n";

        var batches = SqlGoBatchSplitter.Split(sql);

        // Exactly one real GO in the template above -> exactly 2 batches. Before the fix, the
        // embedded "GO" line in the raw operator name would have split this into 4.
        Assert.Equal(2, batches.Length);
        Assert.DoesNotContain(batches, b => b.Trim() == "ZZ'");
    }

    [Fact]
    public void UnsanitizedOperatorName_WouldHaveIntroducedExtraBatches_CharacterisingTheOriginalDefect()
    {
        // Negative control: proves the test above is actually discriminating, by showing what the
        // UNSANITIZED substitution (the pre-fix shape) does to the same template.
        const string maliciousOperatorName = "DBA\r\nGO\r\nZZ";
        var sql = "SET @OperatorName = N'" + maliciousOperatorName.Replace("'", "''") + "'\r\nGO\r\nSELECT 1\r\n";

        var batches = SqlGoBatchSplitter.Split(sql);

        Assert.True(batches.Length > 2, "the unsanitized template was expected to split into extra batches");
    }

    // ── H-fix D1(b)/D2: the pure Succeeded computation the export/audit/UI all key off ──────────
    //
    // LogReportBundle's success ARGUMENT and the export header/UI Done-vs-Failed mapping are all
    // WIRING around this one pure property -- they are gate-verified live (no audit-sink test seam
    // exists in this project; AuditLogService is a concrete class with no fake). What is unit-tested
    // here is the computation itself, which is where the fabricated-success defect actually lived
    // (an unconditional `true` literal, not a wiring bug).

    [Fact]
    public void InstanceApplyResult_Succeeded_FalseWhenBatchErrorsArePresent()
    {
        var result = new ServerConfigScriptService.InstanceApplyResult(
            "SQL01", false, null, new[] { Row("IMPLEMENTING") }, "path",
            new[] { "[batch 3] ERROR 208: Invalid object name" });

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void InstanceApplyResult_Succeeded_FalseWhenZeroRowsCaptured_EvenWithNoBatchErrors()
    {
        // The D1 shape: every batch died silently enough that nothing raised, but the script's own
        // #ChangeControlReport never got a row either (an operator-name-killed run before the fix
        // could land exactly here with an empty BatchErrors list).
        var result = new ServerConfigScriptService.InstanceApplyResult(
            "SQL01", false, null, Array.Empty<ServerConfigScriptService.ConfigCheckRow>(), "path", Array.Empty<string>());

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void InstanceApplyResult_Succeeded_FalseWhenRefused_RegardlessOfRowsOrErrors()
    {
        var result = new ServerConfigScriptService.InstanceApplyResult(
            "SQL01", true, "refused", new[] { Row("IMPLEMENTING") }, null, Array.Empty<string>());

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void InstanceApplyResult_Succeeded_TrueWhenRowsCapturedAndNoBatchErrors()
    {
        var result = new ServerConfigScriptService.InstanceApplyResult(
            "SQL01", false, null, new[] { Row("IMPLEMENTING") }, "path", Array.Empty<string>());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void InstanceApplyResult_Succeeded_TrueWhenBatchErrorsOmitted_BackwardCompatibleDefault()
    {
        // The 5-arg constructor every pre-H-fix call site (and this file's own HasFailedRows tests
        // above) still uses -- BatchErrors defaults to null, which Succeeded treats as "no errors".
        var result = new ServerConfigScriptService.InstanceApplyResult(
            "SQL01", false, null, new[] { Row("IMPLEMENTING") }, "path");

        Assert.True(result.Succeeded);
    }

    // ── H-fix D2: connection-loss shape is a FAILED RESULT, never an unhandled throw ────────────
    //
    // Live proof that ADO.NET actually raises InvalidOperationException("BeginExecuteReader
    // requires an open and available Connection") for a server-side KILL mid-run needs a live SQL
    // Server (PARSEONLY only for this builder) and is the verify/gate phase's job. What is
    // unit-tested here is the catch-CLASSIFICATION's downstream shape: the exact InstanceApplyResult
    // RunApplyForInstanceAsync's `catch (InvalidOperationException)` branch constructs (Refused
    // stays false -- a real attempt happened; whatever rows were captured pre-loss are preserved,
    // never discarded; BatchErrors carries the loss) computes Succeeded=false and constructs without
    // throwing.
    [Fact]
    public void InstanceApplyResult_ConnectionLossShape_IsAFailedResult_PreservesPreLossRows_NeverThrows()
    {
        var preLossRows = new[] { Row("IMPLEMENTING") };

        var result = new ServerConfigScriptService.InstanceApplyResult(
            "SQL01", false, null, preLossRows, "path",
            new[] { "Connection lost mid-run: BeginExecuteReader requires an open and available Connection." });

        Assert.False(result.Refused);
        Assert.False(result.Succeeded);
        Assert.Single(result.Rows);
        Assert.Single(result.BatchErrors!);
    }

    // ── Export formatter: batch errors are named, never hidden behind the benign "no rows" line ──

    [Fact]
    public void FormatChangeControlExport_BatchErrors_AreNamedInTheHeader()
    {
        var text = ServerConfigScriptService.FormatChangeControlExport(
            "SQL01", new DateTime(2026, 8, 20, 9, 0, 0), Array.Empty<ServerConfigScriptService.ConfigCheckRow>(),
            apply: true, batchErrors: new[] { "[batch 3] ERROR 208: Invalid object name" });

        Assert.Contains("1 batch error(s) occurred", text, StringComparison.Ordinal);
        Assert.Contains("[batch 3] ERROR 208: Invalid object name", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatChangeControlExport_ZeroRowsWithBatchErrors_NeverReadsAsBenignEmptiness()
    {
        // The exact D1 shape: zero rows AND at least one batch error. Before the fix this printed
        // only "No change-control rows were captured." -- indistinguishable from a clean run that
        // genuinely needed no changes.
        var text = ServerConfigScriptService.FormatChangeControlExport(
            "SQL01", new DateTime(2026, 8, 20, 9, 0, 0), Array.Empty<ServerConfigScriptService.ConfigCheckRow>(),
            apply: true, batchErrors: new[] { "[batch 1] ERROR 111: Bare GO split the script" });

        Assert.Contains("did not complete cleanly", text, StringComparison.Ordinal);
        Assert.DoesNotContain("No change-control rows were captured.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatChangeControlExport_ZeroRowsNoBatchErrors_StillPrintsTheOriginalBenignLine()
    {
        // Existing call sites (preview, and a genuinely clean apply) never pass batchErrors --
        // pins that the honest-header change is additive, not a rewording of the ordinary case.
        var text = ServerConfigScriptService.FormatChangeControlExport(
            "SQL01", new DateTime(2026, 8, 20, 9, 0, 0), Array.Empty<ServerConfigScriptService.ConfigCheckRow>());

        Assert.Contains("No change-control rows were captured.", text, StringComparison.Ordinal);
    }

    // ── Cosmetic fix: SanitizeInstanceNameForFile never emits a leading dot (2026-08-20 H round) ──
    //
    // ".\new2022" (this box's own local-instance convention) sanitized to a leading-dot filename
    // ("._new2022_...") -- a hidden file under POSIX tooling. The export IS the deliverable, so this
    // was fixed here even though it is cosmetic. Shared by BOTH the preview export (lane E,
    // ancestor commit d3f260d) and the apply export (this lane) -- one function, one fix.

    [Theory]
    [InlineData(@".\new2022", "_new2022")]
    [InlineData(".hidden", "hidden")]
    [InlineData("...", "unknown")]
    [InlineData(@"MSI\OLD2017", "MSI_OLD2017")]
    public void SanitizeInstanceNameForFile_NeverEmitsALeadingDot(string input, string expected)
    {
        Assert.Equal(expected, ServerConfigScriptService.SanitizeInstanceNameForFile(input));
    }

    [Fact]
    public void BuildChangeControlFileName_LocalInstanceConvention_NeverEmitsAHiddenLeadingDotFile()
    {
        var stamp = new DateTime(2026, 8, 20, 14, 30, 5);
        var name = ServerConfigScriptService.BuildChangeControlFileName(@".\new2022", stamp);

        Assert.False(name.StartsWith(".", StringComparison.Ordinal));
    }
}
