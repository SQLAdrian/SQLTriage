/* In the name of God, the Merciful, the Compassionate */

// ── The conditioning sweep, 2026-08-05 ───────────────────────────────────────────────────────
// "Success" is a verdict, and three paths in ScheduledTaskEngine reached it over zero
// measurements: an assessment that ran no check, a report rendered over no results, and a
// restore-verify that found no target. The first is the one that bites — an unattended overnight
// run on a host where no page had ever loaded the check catalogue came back with nothing and was
// filed as a clean pass, indistinguishable in the history grid from a real 582-check run.
//
// The restore-verify cascade was cited as this file's own correct exemplar. It carried the same
// defect at its head, which is why the exemplar is now tested rather than admired.

using System;
using System.IO;
using FluentAssertions;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

public class ScheduledRunVerdictTests
{
    // ── Assessment ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_assessment_that_ran_no_check_is_not_a_success()
    {
        var (status, message) = ScheduledRunVerdict.Assessment(totalChecks: 0, catalogueLoadError: null);

        status.Should().Be(ScheduledRunVerdict.Warning,
            "nothing was assessed, so there is no pass to report");
        message.Should().Be("No checks ran, so nothing was assessed on this server. "
                          + "The check catalogue loaded, and no check is enabled.");
    }

    [Fact]
    public void An_assessment_blocked_by_an_unloaded_catalogue_names_the_load_error()
    {
        var (status, message) = ScheduledRunVerdict.Assessment(
            totalChecks: 0,
            catalogueLoadError: "No check catalog available - license/bundle not loaded and no source parser configured.");

        status.Should().Be(ScheduledRunVerdict.Warning);
        message.Should().Be("No checks ran, so nothing was assessed on this server. "
                          + "The check catalogue is not loaded: No check catalog available - "
                          + "license/bundle not loaded and no source parser configured.",
            "an operator fixes an unloaded catalogue differently from an all-unticked one, so the "
            + "sentence has to say which state produced the empty run");
    }

    [Fact]
    public void An_assessment_that_ran_checks_is_a_success_and_says_nothing_further()
    {
        var (status, message) = ScheduledRunVerdict.Assessment(totalChecks: 582, catalogueLoadError: null);

        status.Should().Be(ScheduledRunVerdict.Success);
        message.Should().BeNull("a clean run has nothing to qualify");
    }

    [Fact]
    public void One_check_is_still_a_measurement()
        => ScheduledRunVerdict.Assessment(1, null).Status.Should().Be(ScheduledRunVerdict.Success,
            "the boundary is zero, not some minimum sample size we never defined");

    // ── Report ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_report_rendered_over_no_results_is_not_a_success()
    {
        var (status, message) = ScheduledRunVerdict.Report(resultCount: 0);

        status.Should().Be(ScheduledRunVerdict.Warning);
        message.Should().Be("The report rendered over zero check results, so it describes no "
                          + "assessment. Run an audit against these servers first.");
    }

    [Fact]
    public void A_report_with_results_and_zero_findings_stays_a_success()
    {
        // The distinction that keeps this fix from over-firing: zero FINDINGS on a real run is a
        // genuinely good outcome. The measurement that matters is whether any check result existed.
        var (status, message) = ScheduledRunVerdict.Report(resultCount: 582);

        status.Should().Be(ScheduledRunVerdict.Success);
        message.Should().BeNull();
    }

    // ── Restore-verify ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_restore_verify_with_no_target_is_not_a_success()
    {
        var (status, message) = ScheduledRunVerdict.RestoreVerify(
            total: 0, passed: 0, failed: 0, couldNotRun: 0, notSupported: 0);

        status.Should().Be(ScheduledRunVerdict.Warning,
            "the cited exemplar cascade fell through to Success here: nothing failed because "
            + "nothing was tried");
        message.Should().Be("No backup was verified: no eligible target was found on this server. "
                          + "Nothing is claimed about the state of its backups.");
    }

    [Fact]
    public void A_corrupt_backup_is_a_failure_and_outranks_everything_else()
    {
        var (status, message) = ScheduledRunVerdict.RestoreVerify(
            total: 5, passed: 3, failed: 1, couldNotRun: 1, notSupported: 0);

        status.Should().Be(ScheduledRunVerdict.Failed);
        message.Should().Be("Restore-verify: 3 passed, 1 FAILED (corrupt), "
                          + "1 could not be checked, 0 not supported (lite tier).");
    }

    [Fact]
    public void A_skipped_database_is_a_warning_not_a_clean_pass()
        => ScheduledRunVerdict.RestoreVerify(total: 4, passed: 3, failed: 0, couldNotRun: 1, notSupported: 0)
            .Status.Should().Be(ScheduledRunVerdict.Warning);

    [Fact]
    public void A_lite_tier_omission_is_a_warning_not_a_clean_pass()
        => ScheduledRunVerdict.RestoreVerify(total: 4, passed: 3, failed: 0, couldNotRun: 0, notSupported: 1)
            .Status.Should().Be(ScheduledRunVerdict.Warning);

    [Fact]
    public void All_verified_is_a_success_and_says_nothing_further()
    {
        var (status, message) = ScheduledRunVerdict.RestoreVerify(
            total: 4, passed: 4, failed: 0, couldNotRun: 0, notSupported: 0);

        status.Should().Be(ScheduledRunVerdict.Success);
        message.Should().BeNull("this is the only genuinely clean outcome, and the only silent one");
    }

    // ── The class, swept ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_path_reports_success_over_zero_measurements()
    {
        ScheduledRunVerdict.Assessment(0, null).Status.Should().NotBe(ScheduledRunVerdict.Success);
        ScheduledRunVerdict.Assessment(0, "anything").Status.Should().NotBe(ScheduledRunVerdict.Success);
        ScheduledRunVerdict.Report(0).Status.Should().NotBe(ScheduledRunVerdict.Success);
        ScheduledRunVerdict.RestoreVerify(0, 0, 0, 0, 0).Status.Should().NotBe(ScheduledRunVerdict.Success);
    }

    [Fact]
    public void Every_non_success_verdict_carries_a_sentence_saying_what_was_measured()
    {
        var verdicts = new[]
        {
            ScheduledRunVerdict.Assessment(0, null),
            ScheduledRunVerdict.Assessment(0, "catalogue missing"),
            ScheduledRunVerdict.Report(0),
            ScheduledRunVerdict.RestoreVerify(0, 0, 0, 0, 0),
            ScheduledRunVerdict.RestoreVerify(2, 1, 1, 0, 0),
        };

        foreach (var (status, message) in verdicts)
        {
            status.Should().NotBe(ScheduledRunVerdict.Success);
            message.Should().NotBeNullOrWhiteSpace(
                "a verdict that is not a clean pass, with no sentence beside it, leaves the "
                + "reader to guess which of several states produced it");
        }
    }

    // ── A REFUSED run is a verdict too (2026-09-07, lane exec-guard-ddl-class) ────────────────
    //
    // This file's founding complaint was a run that reached "success" over zero measurements. A
    // guard-blocked tick is the same shape of hazard read from the other end: the task did not run,
    // and the history row is the only thing an operator will ever look at. It must say Failed, and
    // it must carry the reason — a bare "Failed" with no sentence is the defect this file exists to
    // catch. Evidence class: SOURCE STRUCTURE (believe). The guard's decision itself is proved by
    // execution in DangerousExecGuardTests; what is pinned here is the VERDICT the engine writes.

    [Fact]
    public void A_run_refused_by_the_exec_guard_is_recorded_as_Failed_with_the_reason()
    {
        var src = ReadRepoFile("Data/Services/ScheduledTaskEngine.cs");

        var guardAt = src.IndexOf("DangerousExecGuard.Inspect(task.Query, ExecSurfacePolicy.Unattended)",
                                  StringComparison.Ordinal);
        guardAt.Should().BeGreaterThanOrEqualTo(0,
            "the unattended tick must judge the task under the unattended policy");

        var block = src.Substring(guardAt, Math.Min(2000, src.Length - guardAt));

        block.Should().Contain("exec.Status = \"Failed\"",
            "a task that never reached the server did not succeed");
        block.Should().NotContain("exec.Status = \"Success\"");
        block.Should().Contain("exec.ErrorMessage = $\"Blocked by dangerous-exec guard: {guard.Reason}\"",
            "the history row is the only durable per-task trace, so it has to name WHY");
        block.Should().Contain("exec.CompletedAt = DateTime.UtcNow",
            "a refused run is finished, not left open");
        block.Should().Contain("return;",
            "the refusal must return before the connection string is built");
    }

    [Fact]
    public void A_refused_run_is_announced_three_ways_not_only_as_a_history_row()
    {
        // The estimate's whole warning about this class was that blocks surface "as a row in a
        // table, not an alert". Three sinks, and the audit entry is the one added by this lane.
        var src = ReadRepoFile("Data/Services/ScheduledTaskEngine.cs");

        src.Should().Contain("_history.UpdateExecution(exec)");
        src.Should().Contain("_toast.ShowError(task.Name");
        src.Should().Contain("_audit?.LogUnattendedExecBlocked(");
    }

    /// <summary>
    /// The REPO file of that name, anchored on the folder holding SQLTriage.sln. It used to return the
    /// first hit while walking up from <c>AppContext.BaseDirectory</c>, which reaches
    /// <c>bin\&lt;cfg&gt;\&lt;tfm&gt;\&lt;rid&gt;\</c> — a build output, possibly stale — before it
    /// reaches the repo. Fixed 2026-09-11 with the identical helper in <c>ExecSurfaceGuardTests</c>,
    /// where the wrong-file read was proved by planting a mutated copy in the output; see that
    /// helper's header. The guard here reads .cs source, which the output does not carry, so no
    /// assertion in this class is known to have been affected — but the shape is the same defect and
    /// is now censused by
    /// <see cref="ShippedConfigResolutionCensusTests.Every_ReadRepoFile_helper_anchors_on_the_repo_root"/>.
    /// </summary>
    private static string ReadRepoFile(string relativePath)
    {
        var path = Path.Combine(RawPassedScan.RepoRoot().FullName,
                                relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"{relativePath} must exist at {path} to be asserted against. A missing file under "
                + "guard is a failure, never a pass.", path);
        return File.ReadAllText(path);
    }
}
