/* In the name of God, the Merciful, the Compassionate */

// Pages lane, cluster 6 (2026-08-28) - the client-deliverable headline claims on the
// Vulnerability Assessment page and its two PDF paths.
//
//   pages-r2-02  The PDF cover band named every TARGET of a run beside a pass rate measured only
//                on the servers that answered. Proved live the same day: a four-target run with
//                two nonexistent hosts put all four names on the cover and a "4" in the SERVERS
//                chip, while only two servers had produced a single check.
//   pages-r2-03  The multi-server progress toast said "Assessment complete: {server}" from the
//                same code path on success, on failure, and BEFORE a server's assessment was
//                awaited. Proved live: "Assessment complete: ZZHUNTNOSUCHHOST (2/4)" for a host
//                that does not exist, and "Assessment complete: .\OLD2017 (1/4)" before OLD2017
//                had started.
//   pages-r2-04  The green "No vulnerabilities found! Your SQL Server appears to be compliant."
//                line rendered on EVERY zero-result terminal state, including a run whose
//                connection carried a whitespace server name, so nothing was ever contacted.
//   pages-r1-12  RULED refuted-as-filed: the Audit Assessment's coverage sentence already fires
//                whenever a target fails, on the page and both PDF paths. What stood was the
//                WORD: a target that threw was described as "tested". Relabelled, pinned here.
//
// These are unit tests over the measured-prose functions plus LINTS over the shipped markup. The
// live exercise of the same functions, driven by real payloads from a real four-target run, is
// VaCoverageHonestyLiveTests in this same folder (arm-gated, inert unarmed).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

public class VaCoverageHonestyTests
{
    private static readonly string[] ComplianceClaims =
    {
        "compliant", "No vulnerabilities found", "no vulnerabilities", "secure", "passed all",
    };

    // ── pages-r2-02: the coverage sentence ───────────────────────────────────

    [Fact]
    public void A_run_in_which_every_planned_server_reported_says_nothing()
    {
        // Null, not "0 servers missing": a complete run's PDF must be byte-identical to what it
        // always was, with no clause claiming completeness either.
        VulnerabilityAssessmentStateService.DescribeAssessmentCoverage(
            cancelled: false,
            plannedServers: new[] { "A", "B" },
            reportingServers: new[] { "A", "B" })
            .Should().BeNull();
    }

    [Fact]
    public void A_run_with_no_targets_at_all_says_nothing_here()
    {
        // Nothing was planned, so nothing went silent. The zero-target case is stated by
        // DescribeEmptyResult, which knows WHY nothing was contacted; this sentence is about the
        // gap between a plan and its coverage and there is no plan.
        VulnerabilityAssessmentStateService.DescribeAssessmentCoverage(
            cancelled: false, plannedServers: Array.Empty<string>(), reportingServers: Array.Empty<string>())
            .Should().BeNull();
    }

    [Fact]
    public void The_servers_that_produced_nothing_are_counted_AND_named()
    {
        // The defect was a cover band NAMING servers it had not measured. Narrowing the band to
        // the servers that answered cures the false claim; naming the rest here is what stops the
        // cure from being a silent omission. A bare count would leave a reader unable to tell
        // which of their own servers is missing from the report they are holding.
        var note = VulnerabilityAssessmentStateService.DescribeAssessmentCoverage(
            cancelled: false,
            plannedServers: new[] { @".\NEW2022", @".\OLD2017", "ZZHUNTNOSUCHHOST", "ZZHUNTNOSUCHHOST2" },
            reportingServers: new[] { @".\NEW2022", @".\OLD2017" });

        note.Should().NotBeNull();
        note.Should().Contain("2 of the 4 servers in this run produced no assessment results at all");
        note.Should().Contain("ZZHUNTNOSUCHHOST");
        note.Should().Contain("ZZHUNTNOSUCHHOST2");
        note.Should().NotContain("stopped before it finished");
    }

    [Fact]
    public void One_planned_server_that_went_silent_reads_in_the_singular()
    {
        var note = VulnerabilityAssessmentStateService.DescribeAssessmentCoverage(
            cancelled: false, plannedServers: new[] { "PROD01" }, reportingServers: Array.Empty<string>());

        note.Should().Contain("The one server in this run produced no assessment results at all");
        note.Should().Contain("describes it");
        note.Should().Contain("PROD01");
        note.Should().NotContain("1 of the 1");
    }

    [Fact]
    public void A_cancelled_run_says_it_was_stopped_even_when_everyone_reported()
    {
        var note = VulnerabilityAssessmentStateService.DescribeAssessmentCoverage(
            cancelled: true, plannedServers: new[] { "A", "B" }, reportingServers: new[] { "A", "B" });

        note.Should().Contain("stopped before it finished");
        note.Should().NotContain("produced no assessment results");
    }

    [Fact]
    public void A_cancelled_run_that_also_lost_servers_states_both()
    {
        var note = VulnerabilityAssessmentStateService.DescribeAssessmentCoverage(
            cancelled: true, plannedServers: new[] { "A", "B", "C" }, reportingServers: new[] { "A" });

        note.Should().Contain("stopped before it finished");
        note.Should().Contain("2 of the 3 servers in this run produced no assessment results at all");
        note.Should().Contain("B").And.Contain("C");
    }

    [Fact]
    public void The_reporting_set_is_matched_case_insensitively()
    {
        // Instance names round-trip through connection strings and @@SERVERNAME with no case
        // guarantee; a case-sensitive match here would name a server as silent in the same
        // sentence the cover band names it as assessed.
        VulnerabilityAssessmentStateService.DescribeAssessmentCoverage(
            cancelled: false, plannedServers: new[] { @".\NEW2022" }, reportingServers: new[] { @".\new2022" })
            .Should().BeNull();
    }

    // ── pages-r2-02: the reporting set itself, offline ───────────────────────
    //
    // lane9-03. Everything above drives the PROSE. Until 2026-08-28 nothing offline drove the one
    // line the prose is computed from - ServerAssessmentOutcome.Reported - so mutating it from
    // `RowCount > 0` to `RowCount >= 0` reinstated the entire pages-r2-02 defect (every attempted
    // target counted as reporting, ReportingServers becomes the whole target list, SilentServers
    // empties, and the coverage sentence disappears) with all 158 offline lane tests still green.
    // Only the arm-gated live harness caught it, and an arm-gated harness is SKIPPED in CI. These
    // four tests are the offline kill.

    private static MultiServerAssessmentRun RunOver(params (string Server, int Rows)[] outcomes) =>
        new()
        {
            PlannedServers = outcomes.Select(o => o.Server).ToList(),
            Outcomes = outcomes
                .Select(o => new ServerAssessmentOutcome(
                    o.Server, o.Rows, o.Rows > 0 ? null : "nothing came back"))
                .ToList(),
        };

    [Fact]
    public void A_target_that_produced_no_rows_is_not_reported()
    {
        // The mutation target, asserted on the record itself. Zero rows is zero checks: a server
        // that connected and returned nothing has measured nothing.
        new ServerAssessmentOutcome("PROD01", 0, null).Reported.Should().BeFalse();
        new ServerAssessmentOutcome("PROD01", 0, "unreachable").Reported.Should().BeFalse();
        new ServerAssessmentOutcome("PROD01", 1, null).Reported.Should().BeTrue();
        new ServerAssessmentOutcome("PROD01", 158, null).Reported.Should().BeTrue();
    }

    [Fact]
    public void The_reporting_set_holds_only_the_targets_that_produced_rows()
    {
        // The shape the live harness proved: four targets, two of which do not exist.
        var run = RunOver(
            (@".\NEW2022", 79), (@".\OLD2017", 79),
            ("ZZHUNTNOSUCHHOST", 0), ("ZZHUNTNOSUCHHOST2", 0));

        run.ReportingServers.Should().BeEquivalentTo(new[] { @".\NEW2022", @".\OLD2017" });
        run.ReportingServers.Should().NotContain("ZZHUNTNOSUCHHOST");
        run.ReportingServers.Should().NotContain("ZZHUNTNOSUCHHOST2");
        run.SilentServers.Select(o => o.ServerName)
           .Should().BeEquivalentTo(new[] { "ZZHUNTNOSUCHHOST", "ZZHUNTNOSUCHHOST2" });
        run.SilentServers.Should().OnlyContain(o => !string.IsNullOrWhiteSpace(o.Error),
            "a target dropped from the report carries the reason it was dropped");
    }

    [Fact]
    public void A_run_in_which_every_target_failed_reports_nobody()
    {
        var run = RunOver(("ZZDEADTARGET1", 0), ("ZZDEADTARGET2", 0));

        run.ReportingServers.Should().BeEmpty(
            "this is the cover band's list; a run that measured nothing may name nobody on it");
        run.SilentServers.Should().HaveCount(2);
    }

    [Fact]
    public void The_coverage_sentence_is_computed_from_that_same_set_end_to_end()
    {
        // The two halves wired together, offline. If Reported ever widens to include a target that
        // produced no rows, ReportingServers swallows the whole plan, the silent count goes to zero
        // and this sentence VANISHES - so this assertion fails on the disappearance as well as on
        // the wording, which is the failure mode the mutation actually produces.
        var run = RunOver(
            (@".\NEW2022", 79), (@".\OLD2017", 79),
            ("ZZHUNTNOSUCHHOST", 0), ("ZZHUNTNOSUCHHOST2", 0));

        var note = VulnerabilityAssessmentStateService.DescribeAssessmentCoverage(
            cancelled: false, run.PlannedServers, run.ReportingServers);

        note.Should().NotBeNull("two of the four targets produced nothing");
        note.Should().Contain("2 of the 4 servers in this run produced no assessment results at all");
        note.Should().Contain("ZZHUNTNOSUCHHOST").And.Contain("ZZHUNTNOSUCHHOST2");
    }

    // ── pages-r2-04: the zero-result sentence is never a compliance claim ─────

    [Theory]
    // planned, reporting, cancelled, scopeFilter, reason
    [InlineData(0, 0, false, false, "the selected connection has no server name configured")]
    [InlineData(0, 0, false, false, null)]
    [InlineData(0, 0, false, false, "no connection was selected")]
    [InlineData(3, 0, false, false, null)]
    [InlineData(1, 0, false, false, null)]
    [InlineData(4, 0, true, false, null)]
    [InlineData(2, 2, false, true, null)]
    [InlineData(2, 2, false, false, null)]
    [InlineData(1, 1, false, false, null)]
    public void No_zero_result_state_ever_asserts_compliance(
        int planned, int reporting, bool cancelled, bool scopeFilter, string? reason)
    {
        // The house rule this closes: compliance is never asserted from zero measurement. Every
        // executed check contributes a result row, passed or failed, so a zero-row terminal state
        // is a zero-check state - there is no measurement here to read a security posture from.
        // Driven over every terminal state the page can be left in rather than over the one the
        // finding happened to reproduce.
        var text = VulnerabilityAssessmentStateService.DescribeEmptyResult(
            planned, reporting, cancelled, scopeFilter, reason);

        text.Should().NotBeNullOrWhiteSpace();
        var lowered = text.ToLowerInvariant();
        foreach (var claim in ComplianceClaims)
            lowered.Should().NotContain(claim.ToLowerInvariant(),
                $"a zero-result run may not say \"{claim}\"");
    }

    [Fact]
    public void A_run_that_contacted_nothing_says_so_and_says_why()
    {
        // pages-r2-04 case (b), the case proved live: a connection whose ServerNames is whitespace
        // yields zero targets, so the run contacted nothing at all.
        var text = VulnerabilityAssessmentStateService.DescribeEmptyResult(
            plannedServers: 0, reportingServers: 0, cancelled: false, scopeFilterActive: false,
            nothingContactedReason: "the selected connection has no server name configured");

        text.Should().StartWith("Nothing was assessed:");
        text.Should().Contain("the selected connection has no server name configured");
        text.Should().Contain("no check ran");
    }

    [Fact]
    public void A_run_in_which_every_server_threw_says_nothing_was_assessed()
    {
        // pages-r2-04 case (a): each per-server failure is swallowed by the runner, so the page's
        // outer catch never fires and this is the state the operator is left looking at.
        var text = VulnerabilityAssessmentStateService.DescribeEmptyResult(
            plannedServers: 3, reportingServers: 0, cancelled: false, scopeFilterActive: false,
            nothingContactedReason: null);

        text.Should().Contain("all 3 servers in this run produced no results");
        text.Should().Contain("See the run log");
    }

    [Fact]
    public void A_cancelled_run_that_produced_nothing_says_it_was_stopped()
    {
        // lane9-06. This arm existed and no production caller could reach it: both call sites
        // hard-coded cancelled:false, so an operator who pressed Stop was told their servers had
        // been attempted and had produced nothing, with per-server reasons to go and read.
        var text = VulnerabilityAssessmentStateService.DescribeEmptyResult(
            plannedServers: 1, reportingServers: 0, cancelled: true, scopeFilterActive: false,
            nothingContactedReason: null);

        text.Should().Be("Nothing was assessed: the run was stopped before any server reported, "
                       + "so no check ran.");
        text.Should().NotContain("See the run log",
            "there is no per-server reason to read for a run the operator stopped");
        text.Should().NotContain("produced no results",
            "nothing failed to report - the run did not get that far");
    }

    [Fact]
    public void Both_zero_result_call_sites_read_the_recorded_cancellation()
    {
        // The arm above is only worth having if production reaches it. The page renders the
        // zero-result block long after the run's CancellationTokenSource is disposed, so the flag
        // has to be on the state; these two lints hold both call sites to reading it.
        ReadMarkup("VulnerabilityAssessment.razor")
            .Should().Contain("cancelled: State.RunWasCancelled");
        var source = ReadMarkup("VulnerabilityAssessment.razor.cs");
        source.Should().Contain("cancelled: State.RunWasCancelled");
        source.Should().Contain("State.RunWasCancelled = true;",
            "the OperationCanceledException path is how a stopped single-server run gets here");
        System.Text.RegularExpressions.Regex
            .Matches(source + ReadMarkup("VulnerabilityAssessment.razor"), @"cancelled:\s*false")
            .Count.Should().Be(0, "a hard-coded false is the defect, in either file");
    }

    [Fact]
    public void A_scope_filter_that_excluded_everything_blames_the_filter_not_the_estate()
    {
        var text = VulnerabilityAssessmentStateService.DescribeEmptyResult(
            plannedServers: 2, reportingServers: 2, cancelled: false, scopeFilterActive: true,
            nothingContactedReason: null);

        text.Should().Contain("scope filter");
        text.Should().NotContain("Nothing was assessed");
    }

    // ── pages-r2-03: the progress message says what happened ─────────────────

    [Fact]
    public void A_server_that_has_not_started_produces_no_message_at_all()
    {
        // The runner reports once BEFORE awaiting a server so the progress bar can move. The old
        // page lambda turned that report into "Assessment complete: {server}" as soon as the
        // completed counter was above zero - a completion claim for work not yet begun.
        VulnerabilityAssessmentStateService.DescribeServerProgress(
            new ServerAssessmentProgress(1, 4, @".\OLD2017", ServerAssessmentPhase.Starting))
            .Should().BeNull();
    }

    [Fact]
    public void A_failed_server_is_reported_as_a_failure_with_its_reason()
    {
        var msg = VulnerabilityAssessmentStateService.DescribeServerProgress(
            new ServerAssessmentProgress(2, 4, "ZZHUNTNOSUCHHOST", ServerAssessmentPhase.Failed,
                Error: "A network-related or instance-specific error occurred"));

        msg.Should().NotBeNull();
        msg!.Value.IsFailure.Should().BeTrue(
            "ToastService suppresses every type except Error while Notifications is off, so a "
            + "failure sent as Info is invisible to any operator who has switched the nav bell off");
        msg.Value.Text.Should().Contain("FAILED");
        msg.Value.Text.Should().Contain("ZZHUNTNOSUCHHOST");
        msg.Value.Text.Should().Contain("network-related");
        msg.Value.Text.Should().NotContain("complete");
    }

    [Fact]
    public void A_server_that_returned_nothing_is_not_reported_as_complete()
    {
        var msg = VulnerabilityAssessmentStateService.DescribeServerProgress(
            new ServerAssessmentProgress(1, 2, "PROD01", ServerAssessmentPhase.Completed, RowCount: 0));

        msg.Should().NotBeNull();
        msg!.Value.IsFailure.Should().BeTrue();
        msg.Value.Text.Should().Contain("NO results");
        msg.Value.Text.Should().Contain("Nothing was measured");
    }

    [Fact]
    public void A_server_that_produced_results_is_reported_complete_with_its_count()
    {
        var msg = VulnerabilityAssessmentStateService.DescribeServerProgress(
            new ServerAssessmentProgress(1, 2, "PROD01", ServerAssessmentPhase.Completed, RowCount: 158));

        msg.Should().NotBeNull();
        msg!.Value.IsFailure.Should().BeFalse();
        msg.Value.Text.Should().Contain("Assessment complete: PROD01 (1/2)");
        msg.Value.Text.Should().Contain("158 result(s)");
    }

    [Fact]
    public void A_server_the_run_was_stopped_before_is_not_reported_complete()
    {
        var msg = VulnerabilityAssessmentStateService.DescribeServerProgress(
            new ServerAssessmentProgress(1, 4, "PROD02", ServerAssessmentPhase.Cancelled));

        msg.Should().NotBeNull();
        msg!.Value.Text.Should().Contain("stopped before PROD02 produced results");
        msg.Value.Text.Should().NotContain("complete");
    }

    [Fact]
    public void No_phase_can_produce_a_completion_claim_for_a_server_that_measured_nothing()
    {
        // The whole matrix, not just the case the finding reproduced.
        var phases = Enum.GetValues<ServerAssessmentPhase>();
        foreach (var phase in phases)
        {
            var msg = VulnerabilityAssessmentStateService.DescribeServerProgress(
                new ServerAssessmentProgress(1, 4, "SRV", phase, RowCount: 0, Error: "boom"));
            if (msg is null) continue;
            msg.Value.Text.Should().NotContain("Assessment complete",
                $"phase {phase} measured nothing on SRV");
        }
    }

    [Fact]
    public void An_error_toast_survives_an_operator_who_switched_notifications_off()
    {
        // The seam pages-r2-03's reproduce pass could not reach: it observed Info toasts suppressed
        // throughout its session and inferred that Notifications is OFF by default.
        //
        // lane9-02, 2026-08-28: THAT INFERENCE WAS WRONG and it was stated as measured fact in three
        // places. Notifications default to ON - UserSettingsService.Settings.NotificationsEnabled
        // is true, NavMenu syncs ToastService.Enabled from it at startup, and a cold run of the HEAD
        // build against an EMPTY settings directory rendered the enabled bell. What that pass
        // actually observed was this box: a developer had switched notifications off in
        // %APPDATA%\SQLTriage\user-settings.json.
        //
        // The seam is real either way, which is why this test stands: Enabled=false is a state an
        // operator can reach from the nav bell in one click, and in it only Error survives. Asserted
        // on the real ToastService rather than on a claim about it.
        var toast = new ToastService { Enabled = false };
        var seen = new List<ToastNotification>();
        toast.OnShow += t => seen.Add(t);

        toast.ShowInfo("info that should be suppressed");
        toast.ShowError("Assessment FAILED on ZZHUNTNOSUCHHOST (2/4): boom");

        seen.Should().ContainSingle();
        seen[0].Type.Should().Be(ToastType.Error);
        seen[0].Title.Should().Contain("FAILED");
    }

    [Fact]
    public void Notifications_are_ON_by_default_and_this_lane_may_not_say_otherwise()
    {
        // lane9-02, the DD half. The claim "Notifications is off by default on a fresh install" was
        // written into shipped source twice and into a test's stated reason once, on the strength of
        // one machine's user-settings.json. Pinned here so the false premise cannot be restated: a
        // fresh install has NO settings file, so these are the values it runs on.
        new UserSettingsService.UserSettings().NotificationsEnabled.Should().BeTrue(
            "a fresh install has no user-settings.json, so this default IS the shipped behaviour");
        new ToastService().Enabled.Should().BeTrue(
            "NavMenu syncs ToastService.Enabled from the setting above at startup");

        // And the consequence that made the false premise load-bearing: with notifications ON, an
        // Info-class message is delivered, so nothing about routing may be justified by claiming it
        // would otherwise be swallowed on a fresh install.
        var toast = new ToastService();
        var seen = new List<ToastNotification>();
        toast.OnShow += t => seen.Add(t);
        toast.ShowInfo("delivered on a fresh install");
        seen.Should().ContainSingle();
    }

    // ── Markup lints: the shipped .razor, not a claim about it ───────────────

    private static string ReadMarkup(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Markup", file);
        File.Exists(path).Should().BeTrue(
            $"{file} is copied to the test output by SQLTriage.Tests.csproj; "
            + "if this fails every assertion below would vacuously pass");
        return File.ReadAllText(path);
    }

    [Fact]
    public void The_VA_page_no_longer_ships_the_compliance_claim()
    {
        var markup = ReadMarkup("VulnerabilityAssessment.razor");

        // The exact sentence, and the class of sentence. The comment block above the fixed markup
        // quotes the old line to explain itself, so match on the rendered form (a <p> body), not
        // on the bare words.
        markup.Should().NotContain("<p><i class=\"fa-solid fa-check\"></i> No vulnerabilities found!",
            "the green compliance line was rendered from zero measurement on every terminal state");
        markup.Should().Contain("VulnerabilityAssessmentStateService.DescribeEmptyResult(",
            "the zero-result state is described by the measured function, not by a literal");
    }

    [Fact]
    public void The_VA_cover_band_names_the_assessed_set_and_carries_the_coverage_sentence()
    {
        var markup = ReadMarkup("VulnerabilityAssessment.razor");

        // Matched without the separator glyph on purpose: this file stays ASCII so the assertion
        // cannot fail on an encoding round-trip rather than on the markup.
        markup.Should().Contain(", State.AssessedServers)</div>",
            "the band names the servers that reported; the page assigns that list from the run's "
            + "reporting set, never from its target list");
        markup.Should().Contain("State.RunCoverageNotice",
            "and the same cover states what the report does not cover");
        markup.Should().Contain("Servers Assessed",
            "the stat chip counts assessed servers, so it must say so");
    }

    [Fact]
    public void The_VA_page_assigns_the_assessed_set_from_the_run_not_from_its_targets()
    {
        // The entire pages-r2-02 defect was one assignment. It is private to a Blazor component,
        // so this is asserted against the real shipped code-behind.
        var source = ReadMarkup("VulnerabilityAssessment.razor.cs");

        source.Should().Contain("State.AssessedServers = run.ReportingServers",
            "the cover band's list must come from the servers that produced results");
        source.Should().NotContain("State.AssessedServers = serverTargets.Select",
            "that was the defect: the run's INTENT printed as its coverage");
        source.Should().Contain("State.SilentServers = run.SilentServers",
            "and the servers dropped from the report are kept, with their reasons");
        source.Should().Contain("VulnerabilityAssessmentStateService.DescribeAssessmentCoverage(",
            "the coverage sentence is written once per run from the same measurement");
        System.Text.RegularExpressions.Regex
            .Matches(source, @"CoverageNote\s*=\s*State\.RunCoverageNotice")
            .Count.Should().Be(2, "the Findings PDF and the Executive Briefing both carry it");
        source.Should().Contain("VulnerabilityAssessmentStateService.DescribeServerProgress(",
            "and the per-server toast is conditioned on the outcome, not on the counter");
    }

    [Fact]
    public void The_VA_page_does_not_auto_export_a_report_for_a_run_that_measured_nothing()
    {
        // "Never attest nothing" (2026-07-16), already enforced on the CLI attestation path. The
        // page auto-exported a CSV and a PDF regardless of whether anything had been measured, so
        // a run that contacted nothing still produced documents a client could read as a report.
        var source = ReadMarkup("VulnerabilityAssessment.razor.cs");

        var guardIndex = source.IndexOf("else if (State.Results.Count == 0)", StringComparison.Ordinal);
        guardIndex.Should().BeGreaterThan(0, "the empty run is refused before the export branch");
        var exportIndex = source.IndexOf("await AutoExportVaResults();", StringComparison.Ordinal);
        exportIndex.Should().BeGreaterThan(guardIndex,
            "the guard has to precede the export, not follow it");
    }

    [Fact]
    public void The_audit_page_calls_its_server_count_an_attempt_not_a_test()
    {
        // pages-r1-12, ruled 2026-08-28: the count itself stands (it is disclosed by the coverage
        // sentence measured from the same run), the LABEL did not.
        var markup = ReadMarkup("QuickCheck.razor");

        markup.Should().Contain("@State.ServersTested server(s) attempted");
        markup.Should().NotContain("server(s) tested",
            "the counter increments outside both catch blocks, so a target that threw is in it");
        markup.Should().Contain("Label = \"SERVERS ATTEMPTED\"");
        markup.Should().NotContain("Label = \"SERVERS\",",
            "the PDF chip carried the same mislabel as the page");
    }

    [Fact]
    public void Every_place_the_audit_page_states_that_count_calls_it_an_attempt()
    {
        // lane9-08. The relabel reached two of the four render sites. The run's own completion and
        // cancellation status lines were untouched and still read as coverage - "errors across N
        // server(s)" and "partial result(s) from N server(s)" - for a counter that increments
        // outside both catch blocks. QuickCheckStateService's contract, written by the same commit,
        // says "Anything that renders this number must describe it as an attempt", so this asserts
        // that contract over the whole file instead of over the two sites that were remembered.
        var markup = ReadMarkup("QuickCheck.razor");

        markup.Should().NotContain("across {State.ServersTested} server(s)",
            "\"across N servers\" is a coverage claim; the counter includes targets that threw");
        markup.Should().NotContain("from {State.ServersTested} server(s).",
            "the cancelled line made the same claim in the other direction");

        // Every interpolation of the counter into a sentence carries the word.
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex
                     .Matches(markup, @"\{State\.ServersTested\}[^""]*"))
            m.Value.Should().Contain("attempted",
                $"this sentence states the count without calling it an attempt: ...{m.Value}");
    }

    [Fact]
    public void The_audit_pages_existing_coverage_sentence_is_untouched()
    {
        // The ruling was explicit that the coverage disclosure is already right and must not be
        // duplicated or rephrased. This pins that the relabel did not disturb it: one sentence,
        // rendered on the page and passed to BOTH PDF paths.
        var markup = ReadMarkup("QuickCheck.razor");

        markup.Should().Contain("QuickCheckStateService.DescribeRunCompleteness(");
        markup.Should().Contain("@State.CoverageNoteForReports");
        System.Text.RegularExpressions.Regex
            .Matches(markup, @"CoverageNote\s*=\s*State\.CoverageNoteForReports")
            .Count.Should().Be(2, "the Findings PDF and the Executive Briefing both carry it");
    }

    [Fact]
    public void The_audit_pages_coverage_sentence_stays_coupled_to_the_same_measurement()
    {
        // The pages-r1-12 ruling rests on ONE coupling: a target that throws contributes nothing
        // to allResults (AddRange runs only on the success path), so targetsWithRows always falls
        // short of the planned total and the sentence always fires. That is what makes the count
        // a labelling question rather than a fabricated value. This is a source LINT, not an
        // exercise - it fails loudly if a later edit decouples the two, which is all a lint can
        // do; the live drive of the disclosure belongs to the gate.
        var markup = ReadMarkup("QuickCheck.razor");

        markup.Should().Contain("allResults.AddRange(serverResults);",
            "results are accumulated only on the success path");
        markup.Should().Contain("var targetsWithRows = allResults",
            "and the coverage measurement is taken from that same accumulator");
        System.Text.RegularExpressions.Regex.IsMatch(
                markup,
                @"DescribeRunCompleteness\(\s*ct\.IsCancellationRequested,\s*plannedTargets,\s*targetsWithRows\s*\)")
            .Should().BeTrue("the sentence is computed from the planned total and the reporting total");
    }
}
