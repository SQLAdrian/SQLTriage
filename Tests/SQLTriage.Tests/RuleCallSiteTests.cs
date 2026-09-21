/* In the name of God, the Merciful, the Compassionate */

// A TESTED FUNCTION IS NOT A USED FUNCTION.
//
// The sp_PerfCheck bundle added two decisions to the app, and extracted both of them out of the
// code that makes them so CI could measure them: DiagnosticScriptRunner.ReadOutputRowsAsync (which
// result set gets exported) and DiagnosticScriptRunner.ShouldRaiseNoRowsIssue (whether zero rows is
// a fault). Both are covered by truth tables in DiagnosticScriptRunnerStaleOutputTests.
//
// Measured 2026-08-24, twice, and it is the same defect both times: a truth table over an extracted
// member proves the member is right, and proves NOTHING about whether the code that used to make
// the decision still asks it.
//
//   * Deleting "result.Results = await ReadOutputRowsAsync(...)" from ExecuteScriptAsync left the
//     whole Debug suite green at 4789 passed. The runner then exported result set 0, which for
//     sp_PerfCheck is a two-column server banner shipped under the name of a performance audit.
//     Only the live harness caught it, and the live harness is skipped in CI.
//   * Reverting the Full Audit call site to its old inline predicate
//     (cfg.ExportToCsv && r.RowsAffected == 0), which ignores EmptyResultIsNormal entirely, left
//     the whole Debug suite green AND was invisible to the live harness, because nothing renders
//     Full Audit. Every clean server would be reported to the operator as a tooling fault, on
//     every audit, with no signal anywhere.
//
// A call-site deletion cannot be caught by a unit test of the thing deleted. It is caught here, by
// reading the source of the two call sites and asserting the rule is still the thing asked. That is
// a weaker instrument than execution and it is named as one: it proves the CALL is written, not
// that it runs. What it closes is the silent-drift half, which is the half that shipped.
//
// This is the same instrument ScriptCensusTests uses for the csproj copy rules and
// DarlingContractTests uses for the csproj literal: read the file, assert the shape, name the
// consequence in the failure message.

using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace SQLTriage.Tests;

public class RuleCallSiteTests
{
    private static string RunnerSource() =>
        ReadRepoFile(Path.Combine("Data", "DiagnosticScriptRunner.cs"));

    private static string FullAuditSource() =>
        ReadRepoFile(Path.Combine("Pages", "FullAudit.razor"));

    private static string ReadRepoFile(string relativePath)
    {
        var path = Path.Combine(FrkContractTests.RepoRoot(), relativePath);
        Assert.True(File.Exists(path),
            "Expected " + relativePath + " at " + path + ". This test reads the call site as text, "
            + "so a moved or renamed file is a failure and not a pass.");
        var text = File.ReadAllText(path);
        Assert.True(text.Length > 1000,
            relativePath + " is " + text.Length + " bytes, which is too small to be the file this "
            + "test thinks it is reading. A tripwire over an empty string passes everything.");
        return text;
    }

    // -----------------------------------------------------------------------------------------
    // (a) THE RUNNER ASKS FOR THE CONFIGURED RESULT SET
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void The_runner_reads_its_rows_through_the_member_that_advances_the_reader()
    {
        var src = RunnerSource();

        // Anti-vacuity: both names must be present at all, or the regexes below are asserting
        // things about a file that no longer contains the code they describe.
        Assert.Contains("ExecuteScriptAsync", src, StringComparison.Ordinal);
        Assert.True(Regex.Matches(src, @"\bReadOutputRowsAsync\b").Count >= 2,
            "ReadOutputRowsAsync appears fewer than twice in DiagnosticScriptRunner.cs, so it is "
            + "either defined and never called, or gone.");

        var throughTheMember = Regex.Matches(src, @"result\.Results\s*=\s*await\s+ReadOutputRowsAsync\s*\(");
        Assert.True(throughTheMember.Count == 1,
            "ExecuteScriptAsync assigns result.Results from ReadOutputRowsAsync "
            + throughTheMember.Count + " times and it must be exactly once. Deleting that call is "
            + "the mutation that stayed green across the entire suite while the runner exported "
            + "result set 0: a two-column server banner written to CSV under the name of a "
            + "performance audit, with Success = true.");

        // "=" and not "==": result.Results is also COMPARED to null in the CSV writer, and a
        // comparison is not a second way to fill the export.
        var allAssignments = Regex.Matches(src, @"result\.Results\s*=(?!=)");
        Assert.True(allAssignments.Count == 1,
            "result.Results is assigned " + allAssignments.Count + " times in "
            + "DiagnosticScriptRunner.cs. A second assignment is a second way to fill the export, "
            + "and the one that does not go through ReadOutputRowsAsync does not advance the reader "
            + "to the configured result set.");

        var handRolledLoops = Regex.Matches(src, @"\breader\.ReadAsync\s*\(");
        Assert.True(handRolledLoops.Count == 1,
            "The output reader is read in " + handRolledLoops.Count + " places. Exactly one is "
            + "right, and it is inside ReadOutputRowsAsync, after the advance. A row loop written "
            + "anywhere else reads whichever set the reader happens to be sitting on.");
    }

    // -----------------------------------------------------------------------------------------
    // (b) FULL AUDIT ASKS WHETHER ZERO ROWS MEANS BROKEN
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Full_Audit_raises_a_No_Rows_issue_only_through_the_shared_rule()
    {
        var src = FullAuditSource();

        Assert.Contains("MaybeShowPostRunModal", src, StringComparison.Ordinal);

        const string issueLiteral = "\"No-Rows\"";
        var raised = Regex.Matches(src, Regex.Escape(issueLiteral));
        Assert.True(raised.Count == 1,
            "Pages/FullAudit.razor constructs a \"No-Rows\" issue " + raised.Count + " times. This "
            + "test can only prove the guard on one of them, so a second construction has to be "
            + "brought back here before it is written.");

        // The guard IMMEDIATELY above the construction is the one that decides it. Checking that
        // the file merely mentions ShouldRaiseNoRowsIssue somewhere would pass a file that called
        // the rule, ignored the answer, and raised the issue under a second condition.
        var upToTheIssue = src.Substring(0, raised[0].Index);
        var lastIf = upToTheIssue.LastIndexOf("if (", StringComparison.Ordinal);
        Assert.True(lastIf >= 0, "No 'if (' precedes the \"No-Rows\" issue at all, so it is raised "
            + "unconditionally on every script that succeeds.");

        var guard = upToTheIssue.Substring(lastIf);
        Assert.Contains("DiagnosticScriptRunner.ShouldRaiseNoRowsIssue", guard, StringComparison.Ordinal);

        // The shape that was there before the bundle, and the shape a revert would restore. It
        // ignores EmptyResultIsNormal, so sp_PerfCheck finding nothing wrong with a server is
        // reported to the operator as a broken export.
        var inlineComparison = Regex.Matches(src, @"RowsAffected\s*==\s*0");
        Assert.True(inlineComparison.Count == 0,
            "Pages/FullAudit.razor compares RowsAffected to 0 inline (" + inlineComparison.Count
            + " times). Whether zero rows is a fault depends on the entry's EmptyResultIsNormal "
            + "flag, and an inline comparison cannot see it. The rule is "
            + "DiagnosticScriptRunner.ShouldRaiseNoRowsIssue.");
    }

    // -----------------------------------------------------------------------------------------
    // (c) THE LOADER ASKS FOR THE sp_Blitz OUTPUT-QUERY REPAIR
    // -----------------------------------------------------------------------------------------
    //
    // The same class of gap the exportpack-blitz-header lane's correctness lens found 2026-08-24:
    // ScriptConfigurationRepairTests proves ScriptConfigurationMigrator.RepairSupersededOutputQueries
    // repairs an installed config correctly, by calling it directly. Nothing pinned that
    // LoadScriptConfigurations still CALLS it - deleting the one line at DiagnosticScriptRunner.cs:92
    // left the entire suite green, because every repair test reaches the migrator without going
    // through the loader at all. An install upgraded over the pre-2026-08-24 shipped sp_Blitz query
    // would then keep producing the CSV the Export Pack refuses, forever, with every unit test still
    // passing.

    [Fact]
    public void The_loader_repairs_superseded_output_queries_before_reading_the_config()
    {
        var src = RunnerSource();

        // Anti-vacuity: both members must still exist, or the regex below is asserting something
        // about code that is no longer there.
        Assert.Contains("EnsureShippedEntries", src, StringComparison.Ordinal);
        Assert.Contains("RepairSupersededOutputQueries", src, StringComparison.Ordinal);

        var repairCall = Regex.Matches(src,
            @"ScriptConfigurationMigrator\.RepairSupersededOutputQueries\s*\(\s*configPath\s*,\s*_logger\s*\)");
        Assert.True(repairCall.Count == 1,
            "DiagnosticScriptRunner.cs calls ScriptConfigurationMigrator.RepairSupersededOutputQueries("
            + "configPath, _logger) " + repairCall.Count + " times and it must be exactly once. Deleting "
            + "that call is the mutation that stayed green across the entire suite while an install "
            + "upgraded over the pre-2026-08-24 shipped sp_Blitz query kept the CSV the Export Pack "
            + "refuses, forever - RepairSupersededOutputQueries itself is well covered, but a covered "
            + "function nobody calls fixes nothing.");

        // The repair has to run BEFORE the config is read back into memory, or the freshly-repaired
        // bytes on disk are never the bytes this process actually loads.
        var ensureAt = src.IndexOf("ScriptConfigurationMigrator.EnsureShippedEntries", StringComparison.Ordinal);
        var repairAt = src.IndexOf("ScriptConfigurationMigrator.RepairSupersededOutputQueries", StringComparison.Ordinal);
        var readAt = src.IndexOf("File.ReadAllText(configPath)", StringComparison.Ordinal);
        Assert.True(ensureAt >= 0 && repairAt >= 0 && readAt >= 0,
            "one of EnsureShippedEntries / RepairSupersededOutputQueries / the config read could not "
            + "be located in DiagnosticScriptRunner.cs at all.");
        Assert.True(ensureAt < repairAt && repairAt < readAt,
            "the ordering must be EnsureShippedEntries, then RepairSupersededOutputQueries, then the "
            + "config is read - new shipped entries have to exist before a value on one of them can be "
            + "repaired, and the repair has to land on disk before the file already open for reading "
            + "sees it.");
    }
}
