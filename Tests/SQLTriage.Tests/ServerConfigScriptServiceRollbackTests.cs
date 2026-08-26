/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using SQLTriage.Tests.Licensing;
using Xunit;

namespace SQLTriage.Tests;

/// <summary>
/// Tests for the apply-mode rollback-script feature added to <see cref="ServerConfigScriptService"/>
/// (2026-06-30 handoff spec): the script-side <c>#RollbackScript</c>/<c>#sp_CCRollback</c> wiring
/// writes a reverse-T-SQL file to <c>output/</c> on every APPLY run that changed something.
///
/// <para><b>Not covered here</b> (needs a live server — the verify phase's job, not this
/// builder's): that the reverse statements themselves actually undo the change on a real
/// instance. What IS covered: the pure, DB-independent seams — the result-set shape detector, the
/// file-name sanitizer/builder, and the write-or-skip logic — plus that the shipped .sql actually
/// carries the wiring this class expects AND actually parses (skipped when the .sql is absent,
/// e.g. a community test build, per <c>Tests/SQLTriage.Tests/Gated/README.md</c> — this file
/// itself is NOT in Gated/ because <see cref="ServerConfigScriptService"/> is not a gated symbol,
/// only its .sql payload is Content-Removed under community).</para>
///
/// <para><b>2026-08-20 lesson</b>: the three "ShippedScript_*" substring tests below were green
/// (suite 4413/0) for weeks while the script was 100% uncompilable T-SQL (12x Msg 102, a
/// concatenation expression passed as an EXEC parameter). A substring match cannot see a parse
/// failure. <see cref="ShippedScript_ParsesCleanUnderScriptDom_ZeroErrors"/> is the real gate:
/// it parses the whole shipped file with TransactSql.ScriptDom and requires zero errors.</para>
/// </summary>
public sealed class ServerConfigScriptServiceRollbackTests : IDisposable
{
    private readonly string _tempDir;

    public ServerConfigScriptServiceRollbackTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sqlt-rollback-tests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* test cleanup; ignore */ }
    }

    private static ServerConfigScriptService NewService()
    {
        var accessor = new FakeBundleAccessor();
        return new ServerConfigScriptService(
            null!,
            NullLogger<ServerConfigScriptService>.Instance,
            new BundleBackedRemediationCapability(accessor),
            accessor);
    }

    // ── Shape detector: recognises the rollback result set and NOT the others ────────────────

    [Fact]
    public void IsRollbackScriptShape_RecognisesTheExactTwoColumnShape()
    {
        Assert.True(ServerConfigScriptService.IsRollbackScriptShape(
            2, i => i == 0 ? "ServerName" : "RollbackScript"));
    }

    [Fact]
    public void IsRollbackScriptShape_IsCaseInsensitiveOnColumnNames()
    {
        // T-SQL column aliases aren't case-sensitive on the wire; match IsChangeControlReportShape's
        // OrdinalIgnoreCase convention so a driver/collation quirk can't silently defeat detection.
        Assert.True(ServerConfigScriptService.IsRollbackScriptShape(
            2, i => i == 0 ? "servername" : "rollbackscript"));
    }

    [Fact]
    public void IsRollbackScriptShape_RejectsTheChangeControlReportShape()
    {
        // The 8-column #ChangeControlReport shape (ID, Captured, Mode, Section, Setting,
        // CurrentValue, TargetValue, Detail) must never be mistaken for the rollback result set.
        var names = new[] { "ID", "Captured", "Mode", "Section", "Setting", "CurrentValue", "TargetValue", "Detail" };
        Assert.False(ServerConfigScriptService.IsRollbackScriptShape(names.Length, i => names[i]));
    }

    [Fact]
    public void IsRollbackScriptShape_RejectsAGenericTwoColumnDiagnosticRow()
    {
        // Right column COUNT, wrong names — must not match on count alone.
        Assert.False(ServerConfigScriptService.IsRollbackScriptShape(
            2, i => i == 0 ? "Name" : "Value"));
    }

    [Fact]
    public void IsRollbackScriptShape_RejectsWrongColumnCount()
    {
        Assert.False(ServerConfigScriptService.IsRollbackScriptShape(
            1, _ => "RollbackScript"));
        Assert.False(ServerConfigScriptService.IsRollbackScriptShape(
            3, i => i switch { 0 => "ServerName", 1 => "RollbackScript", _ => "Extra" }));
    }

    // ── File-name sanitizer / builder ────────────────────────────────────────────────────────

    [Fact]
    public void SanitizeInstanceNameForFile_ReplacesInvalidFileNameChars()
    {
        // MSI\OLD2017 -> MSI_OLD2017 (the exact example from the 2026-06-30 handoff spec).
        Assert.Equal("MSI_OLD2017", ServerConfigScriptService.SanitizeInstanceNameForFile(@"MSI\OLD2017"));
    }

    [Fact]
    public void SanitizeInstanceNameForFile_GuardsPathTraversal()
    {
        var sanitized = ServerConfigScriptService.SanitizeInstanceNameForFile("../../etc");
        Assert.DoesNotContain("../", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeInstanceNameForFile_NullOrWhitespace_FallsBackToUnknown()
    {
        Assert.Equal("unknown", ServerConfigScriptService.SanitizeInstanceNameForFile(null));
        Assert.Equal("unknown", ServerConfigScriptService.SanitizeInstanceNameForFile("   "));
    }

    [Fact]
    public void BuildRollbackFileName_MatchesTheInstanceStampConvention()
    {
        var stamp = new DateTime(2026, 8, 20, 14, 30, 5);
        Assert.Equal(
            @"MSI_OLD2017_20260820-143005_rollback.sql",
            ServerConfigScriptService.BuildRollbackFileName(@"MSI\OLD2017", stamp));
    }

    // ── Writer: writes the expected file, and writes NOTHING for a zero-statement run ──────────

    [Fact]
    public async Task WriteRollbackScriptAsync_WritesTheFile_UnderTheExpectedName()
    {
        var path = await ServerConfigScriptService.WriteRollbackScriptAsync(
            _tempDir, @"MSI\OLD2017", "-- reverse statement\r\nEXEC sys.sp_configure ...;");

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.StartsWith("MSI_OLD2017_", Path.GetFileName(path), StringComparison.Ordinal);
        Assert.EndsWith("_rollback.sql", Path.GetFileName(path), StringComparison.Ordinal);
        Assert.Contains("EXEC sys.sp_configure", await File.ReadAllTextAsync(path!));
    }

    [Fact]
    public async Task WriteRollbackScriptAsync_CreatesTheOutputDirectory_WhenAbsent()
    {
        Assert.False(Directory.Exists(_tempDir));
        var path = await ServerConfigScriptService.WriteRollbackScriptAsync(_tempDir, "SRV1", "EXEC sys.sp_configure ...;");
        Assert.True(Directory.Exists(_tempDir));
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WriteRollbackScriptAsync_WritesNothing_WhenNoRollbackStatementsWereProduced(string? emptyScript)
    {
        var path = await ServerConfigScriptService.WriteRollbackScriptAsync(_tempDir, "SRV1", emptyScript);

        Assert.Null(path);
        // The guard must fire before the directory is even touched — an apply run that changed
        // nothing (or a preview, which never reaches this call) must not leave output/ behind.
        Assert.False(Directory.Exists(_tempDir));
    }

    // ── r1-04: the multi-instance apply lane keeps the rollback script the single-instance one keeps ──
    //
    // The defect (honesty hunt 2026-08-25): RunScriptCoreAsync, the body behind the per-instance
    // apply, checked only IsChangeControlReportShape. The script's (ServerName, RollbackScript)
    // result set fell into the flatten-to-onMessage branch, and that lane passes onMessage: null, so
    // the undo script was discarded outright. Proved live by the hunt: Rows=1, Succeeded=True, the
    // applied export written, and Directory.GetFiles(outDir, "*_rollback.sql") EMPTY. The tests that
    // "covered" rollback only ever exercised the pure static helpers, never a call site.
    //
    // The call-site half is proved live in RemediationSafetyLiveSmokeTests (a crafted apply script
    // against .\new2022 that returns the rollback shape). What is pinned here is what a live server
    // cannot pin: the three states the operator must be able to tell apart.

    [Fact]
    public void DescribeRollbackOutcome_NamesTheFile_WhenOneWasWritten()
    {
        var note = ServerConfigScriptService.DescribeRollbackOutcome(@"C:\out\SRV1_20260825-101500_rollback.sql", null);
        Assert.Contains("_rollback.sql", note, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT saved", note, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeRollbackOutcome_SaysNotSaved_WhenOneWasReturnedAndCouldNotBeWritten()
    {
        // The state that must never read like "this run produced none": there IS an undo script and
        // the operator does not have it.
        var note = ServerConfigScriptService.DescribeRollbackOutcome(null, "Writing it to C:\\out failed: access denied.");
        Assert.Contains("NOT saved", note, StringComparison.Ordinal);
        Assert.Contains("access denied", note, StringComparison.Ordinal);
        Assert.Contains("no generated undo script", note, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeRollbackOutcome_SaysNoneWasReturned_WhenTheRunProducedNothingToUndo()
    {
        var note = ServerConfigScriptService.DescribeRollbackOutcome(null, null);
        Assert.Contains("No rollback script was returned", note, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT saved", note, StringComparison.Ordinal);
    }

    [Fact]
    public void TheApplyResult_CarriesTheRollbackOutcome_AndDoesNotFoldItIntoSucceeded()
    {
        var rows = new[] { new ServerConfigScriptService.ConfigCheckRow(1, DateTime.UtcNow, "IMPLEMENTING", "S", "Setting", "0", "1", null) };

        var written = new ServerConfigScriptService.InstanceApplyResult(
            "SRV1", false, null, rows, "C:\\out\\x.txt", null, @"C:\out\SRV1_rollback.sql", null);
        var lost = new ServerConfigScriptService.InstanceApplyResult(
            "SRV1", false, null, rows, "C:\\out\\x.txt", null, null, "disk full.");
        var none = new ServerConfigScriptService.InstanceApplyResult(
            "SRV1", false, null, rows, "C:\\out\\x.txt");

        // Succeeded keeps its one meaning ("did this read/change the server"), word for word with
        // the preview lane. A missing undo file is stated, not folded into the verdict.
        Assert.True(written.Succeeded);
        Assert.True(lost.Succeeded);
        Assert.True(none.Succeeded);

        Assert.Contains("_rollback.sql", written.RollbackScriptNote, StringComparison.Ordinal);
        Assert.Contains("NOT saved", lost.RollbackScriptNote, StringComparison.Ordinal);
        Assert.Contains("No rollback script was returned", none.RollbackScriptNote, StringComparison.Ordinal);
    }

    [Fact]
    public void TheApplyExport_NamesWhatHappenedToTheRollbackScript()
    {
        var rows = Array.Empty<ServerConfigScriptService.ConfigCheckRow>();

        var applied = ServerConfigScriptService.FormatChangeControlExport(
            "SRV1", new DateTime(2026, 8, 25, 10, 15, 0), rows, apply: true,
            rollbackNote: ServerConfigScriptService.DescribeRollbackOutcome(@"C:\out\SRV1_rollback.sql", null));
        Assert.Contains("_rollback.sql", applied, StringComparison.Ordinal);

        // Preview produces no rollback script at all, so its export says nothing about one.
        var preview = ServerConfigScriptService.FormatChangeControlExport(
            "SRV1", new DateTime(2026, 8, 25, 10, 15, 0), rows);
        Assert.DoesNotContain("ollback", preview, StringComparison.Ordinal);
    }

    // ── The shipped .sql actually carries the wiring (skip-if-absent: community build excludes it) ──

    [Fact]
    public void ShippedScript_DeclaresTheRollbackTableAndHelperProc()
    {
        var service = NewService();
        if (!service.ScriptExists) return; // community test build: ConfigScripts\** is Content-Removed

        var sql = File.ReadAllText(service.ScriptPath);

        Assert.Contains("CREATE TABLE #RollbackScript", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE PROCEDURE #sp_CCRollback", sql, StringComparison.Ordinal);
        Assert.Contains("EXEC #sp_CCRollback", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ShippedScript_EmitsTheRollbackResultSet_GatedOnApplyMode()
    {
        var service = NewService();
        if (!service.ScriptExists) return;

        var sql = File.ReadAllText(service.ScriptPath);

        // The third result set: ServerName + RollbackScript, gated on @ForChangeControl = 0
        // (apply mode) and placed after the raw #ChangeControlReport rows.
        Assert.Contains("AS ServerName", sql, StringComparison.Ordinal);
        Assert.Contains("AS RollbackScript", sql, StringComparison.Ordinal);

        var rawRowsIndex = sql.IndexOf(
            "SELECT ID, Captured, Mode, Section, Setting, CurrentValue, TargetValue, Detail",
            StringComparison.Ordinal);
        var rollbackSelectIndex = sql.IndexOf("AS RollbackScript", StringComparison.Ordinal);
        Assert.True(rawRowsIndex >= 0, "raw #ChangeControlReport rows SELECT not found");
        Assert.True(rollbackSelectIndex > rawRowsIndex, "rollback result set must follow the raw rows SELECT");
    }

    // ── Real parse gate (2026-08-20 fix round) ───────────────────────────────────────────────
    //
    // A live verify pass proved the shipped .sql did not compile: 12x T-SQL Msg 102 "Incorrect
    // syntax near '+'" because EXEC #sp_CCRollback was called with @Stmt = <a concatenation
    // expression>, which T-SQL forbids as an EXEC parameter. The three tests above are all
    // Assert.Contains substring checks on the raw text -- they were green (suite 4413/0) the
    // whole time the feature was 100% dead, because a substring match cannot see that the
    // surrounding statement fails to parse. This test closes that gap offline: it feeds the
    // ENTIRE shipped file (the same GO-batched script the service reads) through the real
    // TransactSql.ScriptDom parser used by SSMS/DacFx and asserts there are ZERO parse errors,
    // so any future concatenation-as-EXEC-parameter regression (or any other parse-breaking
    // typo) fails this suite instead of shipping silently dead.

    [Fact]
    public void ShippedScript_ParsesCleanUnderScriptDom_ZeroErrors()
    {
        var service = NewService();
        if (!service.ScriptExists) return; // community test build: ConfigScripts\** is Content-Removed

        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StreamReader(service.ScriptPath);
        parser.Parse(reader, out System.Collections.Generic.IList<ParseError> errors);

        Assert.True(errors.Count == 0, "ScriptDom parse errors in " + service.ScriptPath + ":\n" +
            string.Join("\n", errors.Select(e => $"  Line {e.Line}, Col {e.Column}: {e.Message} ({e.Number})")));
    }
}
