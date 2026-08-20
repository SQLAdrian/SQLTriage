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
}
