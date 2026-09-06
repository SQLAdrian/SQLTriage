/* In the name of God, the Merciful, the Compassionate */

using FluentAssertions;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The PDF header defect (queued 2026-08-06, fixed 2026-08-10): a run that was cancelled, or one
    /// of whose planned targets never reported, produced a PASSED / FINDINGS / ERRORS / SERVERS
    /// header on the client deliverable that reads exactly like a complete assessment's.
    ///
    /// <para><b>Why CategoryExclusionNotice could not cover it.</b> That sentence is <c>null</c>
    /// whenever no category was excluded, so an UNFILTERED run that stopped early said nothing at all
    /// on the PDF. The completeness sentence is measured separately, from the same two counts, and
    /// both travel to the exports through one string.</para>
    ///
    /// <para>This is ruling #4's argument (a degraded run must not produce a header that reads like a
    /// clean one) applied to the run rather than to a single check.</para>
    /// </summary>
    public class RunCompletenessNoticeTests
    {
        // ── The measurement ─────────────────────────────────────────────────

        [Fact]
        public void A_run_that_finished_with_every_target_reporting_says_nothing()
        {
            // Null, not "0 servers missing": a complete run's PDF must be byte-identical to what it
            // always was, with no clause claiming completeness either.
            QuickCheckStateService.DescribeRunCompleteness(cancelled: false, plannedTargets: 3, reportingTargets: 3)
                .Should().BeNull();
        }

        [Fact]
        public void A_plan_with_no_targets_says_nothing()
        {
            // Unticking every category asks nothing of any target, so nothing is silent. That is the
            // legitimate outcome of an empty plan and not a run that failed to finish.
            QuickCheckStateService.DescribeRunCompleteness(cancelled: false, plannedTargets: 0, reportingTargets: 0)
                .Should().BeNull();
        }

        [Fact]
        public void A_cancelled_run_says_it_was_stopped()
        {
            var note = QuickCheckStateService.DescribeRunCompleteness(
                cancelled: true, plannedTargets: 3, reportingTargets: 3);

            note.Should().NotBeNull();
            note.Should().Contain("stopped before it finished");
            note.Should().NotContain("produced no results at all",
                "every planned target did report; only the plan was cut short");
        }

        [Fact]
        public void A_silent_target_is_counted_and_named_against_the_planned_total()
        {
            var note = QuickCheckStateService.DescribeRunCompleteness(
                cancelled: false, plannedTargets: 4, reportingTargets: 1);

            note.Should().NotBeNull();
            note.Should().Contain("3 of the 4 servers in this run produced no results at all");
            note.Should().NotContain("stopped before it finished");
        }

        [Fact]
        public void A_cancelled_run_that_also_lost_targets_states_both()
        {
            var note = QuickCheckStateService.DescribeRunCompleteness(
                cancelled: true, plannedTargets: 5, reportingTargets: 2);

            note.Should().Contain("stopped before it finished");
            note.Should().Contain("3 of the 5 servers");
        }

        [Fact]
        public void The_single_target_run_reads_as_english_and_not_as_a_counter()
        {
            // The MOST LIKELY degraded shape this feature produces: one selected server, no rows.
            // It rendered "1 of the 1 servers in this run produced no results at all ... describes
            // them" on the Findings PDF title band and the Executive Briefing cover until 2026-08-10.
            var note = QuickCheckStateService.DescribeRunCompleteness(
                cancelled: false, plannedTargets: 1, reportingTargets: 0);

            note.Should().Be("The one server in this run produced no results at all, so nothing in "
                           + "this report describes it.");
            note.Should().NotContain("1 of the 1");
            note.Should().NotContain("describes them");
        }

        [Fact]
        public void One_silent_target_among_several_is_described_in_the_singular_too()
        {
            var note = QuickCheckStateService.DescribeRunCompleteness(
                cancelled: false, plannedTargets: 4, reportingTargets: 3);

            note.Should().Be("1 of the 4 servers in this run produced no results at all, so nothing "
                           + "in this report describes it.");
        }

        [Fact]
        public void More_reporting_targets_than_planned_is_not_a_negative_shortfall()
        {
            // Defensive: the two counts come from different measurements, and a negative silent count
            // would render as "-1 of the 2 servers".
            QuickCheckStateService.DescribeRunCompleteness(cancelled: false, plannedTargets: 2, reportingTargets: 3)
                .Should().BeNull();
        }

        // ── What the exports read ───────────────────────────────────────────

        [Fact]
        public void The_report_coverage_note_is_empty_when_neither_sentence_was_measured()
        {
            var state = new QuickCheckStateService();
            state.CoverageNoteForReports.Should().BeEmpty(
                "an unfiltered complete run's PDF must render exactly as it always has");
        }

        [Fact]
        public void The_report_coverage_note_carries_the_completeness_sentence_on_its_own()
        {
            // The defect in one assertion: no category was excluded, so the exclusion sentence is
            // null, and before this change the PDF's coverage line was therefore empty.
            var state = new QuickCheckStateService
            {
                CategoryExclusionNotice = null,
                RunCompletenessNotice = QuickCheckStateService.DescribeRunCompleteness(true, 2, 2),
            };

            state.CoverageNoteForReports.Should().Contain("stopped before it finished");
        }

        [Fact]
        public void The_report_coverage_note_joins_both_sentences_when_both_were_measured()
        {
            var state = new QuickCheckStateService
            {
                CategoryExclusionNotice = "Category filter: Auditing excluded from this run.",
                RunCompletenessNotice = QuickCheckStateService.DescribeRunCompleteness(true, 2, 2),
            };

            state.CoverageNoteForReports.Should().StartWith("Category filter: Auditing excluded from this run.");
            state.CoverageNoteForReports.Should().Contain("stopped before it finished");
        }

        [Fact]
        public void Clearing_the_results_clears_the_completeness_sentence_with_them()
        {
            // Same invariant CategoryExclusionNotice carries: the sentence may stand only while the
            // grid holds exactly the run it describes.
            var state = new QuickCheckStateService
            {
                CategoryExclusionNotice = "Category filter: Auditing excluded from this run.",
                RunCompletenessNotice = "This assessment was stopped before it finished.",
            };

            state.ClearResults();

            state.RunCompletenessNotice.Should().BeNull();
            state.CategoryExclusionNotice.Should().BeNull();
            state.CoverageNoteForReports.Should().BeEmpty();
        }
    }
}
