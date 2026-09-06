/* In the name of God, the Merciful, the Compassionate */

// Pages lane, cluster 7 (2026-08-28) - four single-page fabricated or unconfirmed facts with no
// natural cluster-mate. Grouped because each is one page's own claim about something it did not
// measure, not a shared mechanism.
//
//   pages-r1-07  An alert flood silently muted ALL alert toasts, criticals included, for five
//                minutes. A repo-wide grep for IsMuted/MutedUntil found the definition, one
//                consumer, and a test-registry entry - and NO render site, so the state was
//                invisible while the nav bell went on showing an unrelated flag.
//   pages-r1-08  The Disk IO toolbar pill printed the raw connection GUID where an instance name
//                belongs, because the lookup key was `c.Id + "|" + s` and the value passed in was
//                a bare connection Id. Proved at hunt time by rendering. The page also samples the
//                FIRST instance of a multi-instance profile and said so nowhere.
//   pages-r1-10  "Session N killed." fired as a green success on the line after KILL returned,
//                with no confirming read. Proved live on .\new2022 by both verdict passes: KILL
//                returned in 3-6 ms while the target sat at status='rollback' for minutes.
//   pages-r2-09  The governance card's colour came from `score?.Overall ?? 0` while its text fell
//                back to "--", and ScoreColor maps 0 to var(--red) - so an unmeasured server was
//                painted the same red as a genuine 0-49 score. Proved at hunt time by rendering,
//                with the correctly-fixed health card visible on the same screen.
//
// The kill-outcome prose is unit-tested here; its live half is SessionKillLiveTests. The other
// three defects live in markup, so they are linted against the shipped files.

using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

public class PagesSinglePageHonestyTests
{
    private static string Markup(string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Markup", file));

    /// <summary>
    /// The markup with its commentary removed - razor <c>@* *@</c> blocks and <c>//</c> line
    /// comments.
    ///
    /// <para>Needed because the fixes in this lane document the defect they closed by QUOTING the
    /// old line, which is exactly what a naive "the old literal is gone" lint searches for. The
    /// first run of these tests failed on their own explanatory comments. The choice was to delete
    /// the commentary or to measure the right thing; a lint about what a page RENDERS has no
    /// business reading its comments, so this strips them.</para>
    /// </summary>
    private static string CodeOnly(string file)
    {
        var markup = Markup(file);

        // Razor comment blocks first: they can contain // and would otherwise leak fragments.
        markup = System.Text.RegularExpressions.Regex.Replace(
            markup, @"@\*.*?\*@", " ", System.Text.RegularExpressions.RegexOptions.Singleline);

        // Then // line comments. Not string-literal aware, which is safe here: every assertion
        // below looks for code that would live outside a string anyway.
        markup = System.Text.RegularExpressions.Regex.Replace(
            markup, @"^\s*//.*$", " ", System.Text.RegularExpressions.RegexOptions.Multiline);

        return markup;
    }

    // ── pages-r1-10: the KILL reports what a re-read found ───────────────────

    [Fact]
    public void A_session_still_rolling_back_is_not_reported_as_killed()
    {
        // The proved live state: KILL returns in milliseconds, the session sits in rollback.
        var outcome = new SessionKillOutcome(71, StillPresent: true, "rollback", 28.0, 1000);

        var text = SessionKillReporting.Describe(outcome);

        text.Should().NotContain("Session 71 killed");
        text.Should().Contain("NOT gone");
        text.Should().Contain("28%");
        SessionKillReporting.IsSuccess(outcome).Should().BeFalse();
    }

    [Fact]
    public void The_rollback_estimate_is_carried_in_human_units()
    {
        // 1000 seconds is the reproduce pass's own measured estimated_completion_time. An
        // operator deciding whether to wait needs the magnitude, not a raw millisecond count.
        SessionKillReporting.Describe(new SessionKillOutcome(71, true, "rollback", 0.0, 1000))
            .Should().Contain("16m 40s");
    }

    [Fact]
    public void A_session_confirmed_gone_is_still_reported_as_killed()
    {
        // Honesty runs both ways: the common case must keep its plain, green success.
        var outcome = new SessionKillOutcome(71, StillPresent: false, null, null, null);

        SessionKillReporting.Describe(outcome).Should().Be("Session 71 killed.");
        SessionKillReporting.IsSuccess(outcome).Should().BeTrue();
    }

    [Fact]
    public void A_session_still_present_without_a_request_is_not_reported_as_killed()
    {
        // The LEFT JOIN case: still connected, no active request. Not gone.
        var outcome = new SessionKillOutcome(71, StillPresent: true, null, null, null);

        SessionKillReporting.Describe(outcome).Should().Contain("still present");
        SessionKillReporting.IsSuccess(outcome).Should().BeFalse();
    }

    [Fact]
    public void A_kill_whose_outcome_could_not_be_confirmed_says_so_and_is_not_a_success()
    {
        // The floor: a refused or failed confirming read renders as unknown WITH its reason,
        // never as the old confident claim.
        var outcome = new SessionKillOutcome(
            71, true, null, null, null, ConfirmationError: "Timeout expired.");

        var text = SessionKillReporting.Describe(outcome);

        text.Should().Contain("could not be confirmed");
        text.Should().Contain("Timeout expired.");
        text.Should().NotContain("Session 71 killed");
        SessionKillReporting.IsSuccess(outcome).Should().BeFalse();
        outcome.Confirmed.Should().BeFalse();
    }

    [Fact]
    public void GoneConfirmed_requires_both_a_successful_read_and_an_absent_session()
    {
        // An unconfirmed outcome must never satisfy the success predicate by accident.
        new SessionKillOutcome(1, false, null, null, null, "read failed")
            .GoneConfirmed.Should().BeFalse();
        new SessionKillOutcome(1, false, null, null, null).GoneConfirmed.Should().BeTrue();
    }

    [Fact]
    public void Rollback_status_matching_is_case_insensitive()
    {
        new SessionKillOutcome(1, true, "ROLLBACK", null, null).RollingBack.Should().BeTrue();
        new SessionKillOutcome(1, true, "running", null, null).RollingBack.Should().BeFalse();
    }

    [Fact]
    public void The_page_renders_the_measured_outcome_not_the_old_literal()
    {
        var markup = CodeOnly("Sessions.razor");

        markup.Should().NotContain("Toast.ShowSuccess($\"Session {spid} killed.\")",
            "the unconditional claim was one line and can come back in one line");
        markup.Should().Contain("SessionKillReporting.IsSuccess");
        markup.Should().Contain("SessionKillReporting.Describe");
    }

    [Fact]
    public void A_kill_that_did_not_finish_is_reported_on_the_class_that_survives_the_bell()
    {
        // lane9-02's consequence. This page routed the honest "KILL accepted ... NOT gone yet"
        // message through ShowWarning while the VA page routed its equivalent through ShowError -
        // two clusters making opposite calls from the same (wrong) premise about the notifications
        // default. ToastService.Show drops every type except Error when Enabled is false, which is
        // one click on the nav bell, so a destructive action that has NOT completed must go out as
        // Error or the operator can be left with a silent, unfinished KILL.
        var markup = CodeOnly("Sessions.razor");

        markup.Should().Contain("Toast.ShowError(SessionKillReporting.Describe(outcome))");
        markup.Should().NotContain("Toast.ShowWarning(SessionKillReporting.Describe(outcome))");

        // And the seam itself, on the real service rather than on a claim about it.
        var svc = new ToastService { Enabled = false };
        var seen = new List<ToastNotification>();
        svc.OnShow += t => seen.Add(t);
        svc.ShowWarning("KILL accepted for session 58, but the session is still present.");
        seen.Should().BeEmpty("this is why the warning class was the wrong choice");
        svc.ShowError("KILL accepted for session 58, but the session is still present.");
        seen.Should().ContainSingle();
    }

    [Fact]
    public void The_service_re_reads_the_session_after_issuing_the_kill()
    {
        // Without the re-read there is nothing for the prose to describe, and every test above
        // would still pass against a service that returned a hard-coded "gone".
        SessionDataService.SessionAfterKillSql.Should().Contain("sys.dm_exec_sessions");
        SessionDataService.SessionAfterKillSql.Should().Contain("sys.dm_exec_requests");
        SessionDataService.SessionAfterKillSql.Should().Contain("LEFT JOIN",
            "an INNER JOIN reports a still-connected session with no active request as gone");
    }

    // ── pages-r2-09: an unmeasured server is not a low score ─────────────────

    [Fact]
    public void The_governance_card_no_longer_substitutes_a_zero_into_its_colour()
    {
        // The defect, verbatim. The refute pass proved by grep that this was the only such hit in
        // Pages/ and Components/, so the assertion can be exact.
        CodeOnly("ServerComparison.razor")
            .Should().NotContain("ScoreColor(score?.Overall ?? 0)");
    }

    [Fact]
    public void The_governance_card_colours_from_the_measured_value_or_from_nothing()
    {
        var markup = Markup("ServerComparison.razor");

        markup.Should().Contain("MeasuredScoreColor(score?.Overall)");
        markup.Should().Contain("var(--text-muted)",
            "an unmeasured server gets the same muted treatment the health card below already uses");
        markup.Should().Contain("Not measured");
    }

    [Fact]
    public void The_unmeasured_governance_card_explains_itself()
    {
        // The health card one section down carries EstateHealthPolicy.ServerBasis for exactly this
        // reason. A muted "--" with no explanation trades a false claim for a silent one.
        Markup("ServerComparison.razor").Should().Contain("GovernanceBasis(server, score)");
    }

    // ── pages-r1-08: the pill names what was sampled ─────────────────────────

    [Fact]
    public void The_disk_io_pill_no_longer_resolves_through_the_mismatched_key()
    {
        // ServerDisplayName matched `o.Key == key` where Key was `c.Id + "|" + s`, called with a
        // bare connection Id. Never a match, so `?? key` printed the GUID on every visit.
        var markup = CodeOnly("DiskIo.razor");

        markup.Should().NotContain("ServerDisplayName(_selectedServer)");
        markup.Should().NotContain("o.Key == key");
    }

    [Fact]
    public void The_pill_and_the_sampler_resolve_through_one_lookup()
    {
        // The root of the defect was two different resolutions of "which server is this page
        // about". One lookup means the label cannot name something the sampler did not measure.
        var markup = Markup("DiskIo.razor");

        markup.Should().Contain("SampledInstanceLabel()");
        markup.Should().Contain("private ServerOpt? SelectedOpt()");
        markup.Should().Contain("var opt = SelectedOpt();",
            "ReloadAsync must sample through the same lookup the pill labels through");
    }

    [Fact]
    public void An_unresolvable_connection_says_so_rather_than_printing_its_id()
    {
        // The fallback must not reintroduce the defect in a quieter form. A GUID in a slot that
        // looks like an instance name reads as an identity.
        var markup = CodeOnly("DiskIo.razor");

        markup.Should().Contain("\"Instance not resolved\"");
        markup.Should().NotContain("?.Server ?? key");
    }

    [Fact]
    public void A_multi_instance_profile_discloses_what_was_not_sampled()
    {
        Markup("DiskIo.razor").Should().Contain("UnsampledInstanceNote()");
    }

    // ── pages-r1-07: the mute has a render site ──────────────────────────────

    [Fact]
    public void A_muted_alert_is_counted_so_the_badge_can_state_the_size_of_the_silence()
    {
        var svc = new ToastService();
        ToastNotification? shown = null;
        svc.OnShow += t => shown = t;

        svc.MuteFor(5);
        svc.ShowAlert("Disk full", "critical", critical: true);

        shown.Should().BeNull("the mute drops criticals too - that is the finding, not a bug here");
        svc.SuppressedAlertCount.Should().Be(1);
    }

    [Fact]
    public void The_suppression_notice_survives_the_mute_that_it_describes()
    {
        // A notice that toasts are being withheld cannot be delivered through the mechanism doing
        // the withholding, or it is unreachable in exactly the state it exists to describe.
        var svc = new ToastService();
        ToastNotification? shown = null;
        svc.OnShow += t => shown = t;

        svc.MuteFor(5);
        svc.ShowSuppressionNotice("Alert toasts muted", "5 alerts in 5 seconds.");

        shown.Should().NotBeNull();
        shown!.IsAlert.Should().BeFalse("an IsAlert notice would re-trigger flood detection");
    }

    [Fact]
    public void The_suppression_notice_survives_the_notifications_switch_too()
    {
        // Criticals survive Enabled=false (Type=Error) but are still dropped by the mute, so the
        // disclosure has to reach the screen with notifications off as well.
        var svc = new ToastService { Enabled = false };
        ToastNotification? shown = null;
        svc.OnShow += t => shown = t;

        svc.ShowSuppressionNotice("Alert toasts muted", "flood");

        shown.Should().NotBeNull();
    }

    [Fact]
    public void Unmuting_clears_the_withheld_count()
    {
        var svc = new ToastService();
        svc.MuteFor(5);
        svc.ShowAlert("a", critical: true);
        svc.SuppressedAlertCount.Should().Be(1);

        svc.Unmute();

        svc.IsMuted.Should().BeFalse();
        svc.SuppressedAlertCount.Should().Be(0);
    }

    [Fact]
    public void An_unmuted_service_still_delivers_alerts()
    {
        var svc = new ToastService();
        ToastNotification? shown = null;
        svc.OnShow += t => shown = t;

        svc.ShowAlert("Disk full", "critical", critical: true);

        shown.Should().NotBeNull();
        svc.SuppressedAlertCount.Should().Be(0);
    }

    [Fact]
    public void The_container_renders_the_mute_and_announces_it()
    {
        // The whole of pages-r1-07's mechanism half: there WAS no render site. The grep the
        // verdict passes ran would now find one.
        var markup = Markup("ToastContainer.razor");

        markup.Should().Contain("ToastService.IsMuted");
        markup.Should().Contain("Alert toasts muted");
        markup.Should().Contain("ShowSuppressionNotice");
        markup.Should().Contain("MuteBadgeMessage()");
    }

    [Fact]
    public void The_mute_badge_is_a_disclosure_and_carries_no_control()
    {
        // lane9-01, found by the gate on the first cut of this fix: the badge shipped with a close
        // button wired to ToastService.Unmute(). ToastService is AddSingleton and this container is
        // in the always-rendered shell, so that button cleared the flood mute and zeroed the
        // withheld count for EVERY open circuit, from every route including AccessDenied - an
        // ungated, process-wide write. RbacShellBoundaryCensusTests is the decider and named it;
        // this pins the shape here too, beside the badge's own tests, so the reason travels with
        // the feature rather than only with the census.
        //
        // CodeOnly, not Markup: the fix documents itself by describing the control it removed, and
        // a naive search for the old name finds that explanation. Same trap the helper's own doc
        // records, and the same trap the shell edge scanner fell into on the first cut of this fix.
        var markup = CodeOnly("ToastContainer.razor");

        markup.Should().NotContain("ToastService.Unmute",
            "no control in the always-rendered shell may clear a process-wide mute");
        markup.Should().NotContain("UnmuteNow",
            "the handler went with the button; a dead handler is the next reviewer's invitation");
        markup.Should().Contain("MuteBadgeMessage()",
            "the disclosure itself stays - it is the whole point of pages-r1-07");
    }

    [Fact]
    public void Every_alert_the_mute_swallows_announces_the_new_count()
    {
        // lane9-09. MuteFor resets the count to zero and a suppressed alert returns from Show
        // BEFORE OnShow is raised, so nothing re-rendered the badge while the flood was being
        // dropped: it painted "No alerts have been withheld yet." for the whole five minutes it was
        // withholding them, criticals included. The container repaints on this event; without it
        // the badge states the opposite of what it has measured.
        var svc = new ToastService();
        var announcements = 0;
        svc.MuteStateChanged += () => announcements++;

        svc.MuteFor(5);
        announcements.Should().Be(1, "engaging the mute is itself a state the badge renders");

        svc.ShowAlert("Disk full", "critical", critical: true);
        svc.ShowAlert("Log full", "critical", critical: true);

        svc.SuppressedAlertCount.Should().Be(2);
        announcements.Should().Be(3,
            "every dropped alert moves the number the badge prints, so every one has to be "
            + "announced - a count that only changes on the next unrelated toast is a frozen count");

        svc.Unmute();
        announcements.Should().Be(4, "clearing the mute clears the badge");
    }

    [Fact]
    public void A_toast_that_is_not_suppressed_does_not_announce_a_mute_change()
    {
        // The control. An event raised on every toast would repaint the badge for the wrong reason
        // and would make the test above pass without measuring the suppression path at all.
        var svc = new ToastService();
        var announcements = 0;
        svc.MuteStateChanged += () => announcements++;

        svc.ShowAlert("Disk full", "critical", critical: true);
        svc.ShowInfo("nothing to do with the mute");

        announcements.Should().Be(0);
    }

    [Fact]
    public void The_mute_notice_names_the_channels_that_are_still_working()
    {
        // The refute pass downgraded this to low precisely because three channels survive the
        // mute. Naming them is what turns that mitigation into something the operator can act on.
        var markup = Markup("ToastContainer.razor");

        markup.Should().Contain("Alerts page");
        markup.Should().Contain("Teams");
        markup.Should().Contain("criticals included",
            "the mute drops critical alerts and the operator has to be told that specifically");
    }
}
