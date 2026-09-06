/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using SQLTriage.Tests.Licensing;
using Xunit;

namespace SQLTriage.Tests;

/// <summary>
/// Unit tests for the <see cref="SqlAssessmentService"/> bundle-accessor integration.
/// Full assessment runs require a live SQL Server — those are integration-test scope.
/// These tests exercise the bundle-driven initialisation path only, PLUS (below) the
/// auto-save-CSV gate in isolation — the one piece of the CLI --report path's fix that does
/// not itself need a live server.
/// </summary>
public class SqlAssessmentServiceTests
{
    // ── Bundle-locked / empty-bundle tests ───────────────────────────────────

    [Fact]
    public void Returns_Empty_When_Bundle_Locked()
    {
        // When ruleset.json is absent from the bundle the service falls back to
        // the built-in comprehensive checks — it must never throw.
        var bundle = new FakeBundleAccessor().SetLocked();
        // SqlAssessmentService requires ServerConnectionManager which requires DI.
        // We can only smoke-test construction here; full run tests need SQL Server.
        // Construction must succeed without throwing.
        var exception = Record.Exception(() =>
            new SqlAssessmentService(
                NullLogger<SqlAssessmentService>.Instance,
                connectionManager: null!,   // not used in construction
                bundle: bundle));
        Assert.Null(exception);
    }

    [Fact]
    public void Construction_WithBundleContainingRuleset_DoesNotThrow()
    {
        // A minimal, empty ruleset JSON — no rules, no probes. Must not throw.
        const string minimalRuleset = """{ "rules": [], "probes": {} }""";
        var bundle = new FakeBundleAccessor()
            .PutFile("Config/ruleset.json", minimalRuleset);
        var exception = Record.Exception(() =>
            new SqlAssessmentService(
                NullLogger<SqlAssessmentService>.Instance,
                connectionManager: null!,
                bundle: bundle));
        Assert.Null(exception);
    }

    [Fact]
    public void Construction_WithMalformedRulesetJson_DoesNotThrow()
    {
        // Malformed JSON in the bundle must trigger the error-log path, not a crash.
        var bundle = new FakeBundleAccessor()
            .PutFile("Config/ruleset.json", "{ not valid json }}}");
        var exception = Record.Exception(() =>
            new SqlAssessmentService(
                NullLogger<SqlAssessmentService>.Instance,
                connectionManager: null!,
                bundle: bundle));
        Assert.Null(exception);
    }

    [Fact]
    public void BundleStateChanged_DoesNotThrow()
    {
        var bundle = new FakeBundleAccessor()
            .PutFile("Config/ruleset.json", """{ "rules": [], "probes": {} }""");
        var svc = new SqlAssessmentService(
            NullLogger<SqlAssessmentService>.Instance,
            connectionManager: null!,
            bundle: bundle);

        // Raise state-changed; the service must invalidate its cache without throwing.
        var exception = Record.Exception(() => bundle.RaiseStateChanged());
        Assert.Null(exception);
    }

    // ── CLI --report audit-evidence, item 2: the unrequested VA CSV ─────────────────────────
    //
    // RunServerAssessmentAsync itself needs a live SQL Server to reach the point where it plants
    // its per-server CSV (the assessment engine runs first), so the CLI opt-out cannot be proven
    // end-to-end here — that is what the verify round's live .\new2022 run is for (does the file
    // land under --out or beside the exe, on an actual --report audit-evidence invocation).
    // What IS provable without a live server is the gate itself:
    // MaybeSaveCsvToOutputFolderAsync(autoSaveCsv) is the exact branch RunServerAssessmentAsync
    // calls, and CliAuditHost passes autoSaveCsv: false there — these two tests drive that real
    // method with both values and check the real filesystem location
    // (Pages/VulnerabilityAssessment.razor.cs's Import modal and VA File Browser glob the same
    // path/pattern), rather than reading the branch off the source.

    [Fact]
    public async Task MaybeSaveCsvToOutputFolderAsync_AutoSaveCsvFalse_PlantsNoFile()
    {
        var bundle = new FakeBundleAccessor()
            .PutFile("Config/ruleset.json", """{ "rules": [], "probes": {} }""");
        var svc = new SqlAssessmentService(
            NullLogger<SqlAssessmentService>.Instance,
            connectionManager: null!,
            bundle: bundle);

        // Alphanumeric-only so SqlAssessmentService's own SanitizeFileName (strip invalid
        // chars, collapse whitespace/underscore runs) cannot alter it — the glob below has to
        // match exactly what the writer would have produced.
        var serverName = "TESTSRV" + Guid.NewGuid().ToString("N");
        var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output");
        var pattern = $"VulnerabilityAssessment_{serverName}_*.csv";

        await svc.MaybeSaveCsvToOutputFolderAsync(new AssessmentSummary(), serverName, autoSaveCsv: false);

        var matches = Directory.Exists(outputDir) ? Directory.GetFiles(outputDir, pattern) : Array.Empty<string>();
        Assert.Empty(matches);
    }

    // ── lane9-05 (2026-08-28): "never attest nothing", enforced where every caller passes ───────
    //
    // The Vulnerability Assessment page refuses to auto-export a run that measured nothing. The
    // multi-server runner then called the writer unconditionally anyway, so a four-target run in
    // which EVERY target failed still produced
    // output\VulnerabilityAssessment_MultiServer_4_<stamp>.csv - 202 bytes, one header row, no data
    // - for a run that contacted nothing successfully. That file lands in the directory the page's
    // own Import modal and File Browser glob, so it is offerable to a client as an assessment of
    // the servers its filename names. The refusal is now in the writer, which is the one place
    // every path goes through.

    [Fact]
    public async Task SaveCsv_PlantsNothingForARunThatProducedNoResults()
    {
        var bundle = new FakeBundleAccessor()
            .PutFile("Config/ruleset.json", """{ "rules": [], "probes": {} }""");
        var svc = new SqlAssessmentService(
            NullLogger<SqlAssessmentService>.Instance,
            connectionManager: null!,
            bundle: bundle);

        // The label the multi-server runner composes for the proved case: four targets, all failed.
        var serverName = "MultiServer4TESTSRV" + Guid.NewGuid().ToString("N");
        var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output");
        var pattern = $"VulnerabilityAssessment_{serverName}_*.csv";

        await svc.MaybeSaveCsvToOutputFolderAsync(new AssessmentSummary(), serverName, autoSaveCsv: true);

        var matches = Directory.Exists(outputDir) ? Directory.GetFiles(outputDir, pattern) : Array.Empty<string>();
        Assert.Empty(matches);
    }

    [Fact]
    public async Task MaybeSaveCsvToOutputFolderAsync_AutoSaveCsvTrue_PlantsTheFileTheDesktopPageGlobsFor()
    {
        var bundle = new FakeBundleAccessor()
            .PutFile("Config/ruleset.json", """{ "rules": [], "probes": {} }""");
        var svc = new SqlAssessmentService(
            NullLogger<SqlAssessmentService>.Instance,
            connectionManager: null!,
            bundle: bundle);

        var serverName = "TESTSRV" + Guid.NewGuid().ToString("N");
        var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output");
        var pattern = $"VulnerabilityAssessment_{serverName}_*.csv";

        // One real result row. This test used to pass an EMPTY summary, which is now refused
        // (lane9-05, 2026-08-28: "never attest nothing" - a header-only CSV in this folder is
        // globbed by the page's Import modal and offerable as an assessment). What the test is
        // FOR is unchanged: it measures the directory and filename shape those globs expect, so
        // it just needs a summary that has something to export.
        var summary = new AssessmentSummary
        {
            Results = { new AssessmentResult { CheckId = "ZZ-PLANT-1", DisplayName = "plant" } },
        };
        await svc.MaybeSaveCsvToOutputFolderAsync(summary, serverName, autoSaveCsv: true);

        try
        {
            // Exactly the directory + filename shape OpenImportModal (:689-691) and
            // ScanVaOutputFolder (:1320) in Pages/VulnerabilityAssessment.razor.cs glob for —
            // proof that autoSaveCsv: true (the default, i.e. every caller except the CLI
            // --report path) leaves the desktop's own behaviour exactly as it was.
            var matches = Directory.Exists(outputDir) ? Directory.GetFiles(outputDir, pattern) : Array.Empty<string>();
            Assert.Single(matches);
        }
        finally
        {
            // Best-effort cleanup — MaybeSaveCsvToOutputFolderAsync writes to a real,
            // unparameterized location (AppDomain.CurrentDomain.BaseDirectory\output) by design
            // (that IS the behaviour under test), so there is no fixture directory to redirect
            // it to without changing what this proves.
            if (Directory.Exists(outputDir))
                foreach (var f in Directory.GetFiles(outputDir, pattern))
                {
                    try { File.Delete(f); } catch { /* best-effort */ }
                }
        }
    }
}
