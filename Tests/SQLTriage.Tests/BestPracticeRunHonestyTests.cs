/* In the name of God, the Merciful, the Compassionate */

// Pages lane, cluster 5 (2026-08-28) - the Best Practice script executor, whose completion claims
// outlived the result table they described. These scripts run against a client's live instance and
// the panel is what the operator reads to decide what to change next.
//
//   pages-r1-04  RunSelected appended ("All scripts completed.", false, true) unconditionally
//                after the loop; the third tuple member is IsSuccess, rendered var(--green).
//                ExecuteScript swallowed every exception into a red message and returned normally,
//                so the run never learned of a failure and counted nothing. Proved at hunt time:
//                nine red "Error in <script>" lines against a nonexistent host, then a green
//                "All scripts completed." Zero of nine had run.
//
//   pages-r2-06  One unlabelled grid from a single reused DataTable, not cleared by the catch. On
//                a failure at or before ExecuteReaderAsync the panel showed the PREVIOUS script's
//                rows beneath this script's red error and the green completion line. Proved at
//                hunt time with two probe scripts against .\NEW2022, and refined there: the stale
//                table appears for a syntax/compile or connect failure, not for a runtime error
//                after the fresh assignment.
//
// The tally functions are unit-tested here; which sentence and which caption the panel renders is
// a fact about the .razor, so the markup is linted too.

using System;
using System.IO;
using FluentAssertions;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

public class BestPracticeRunHonestyTests
{
    // ── pages-r1-04: the closing line matches the run ────────────────────────

    [Fact]
    public void A_run_in_which_every_script_threw_does_not_say_completed()
    {
        // The finding's own proved scenario: nine enabled scripts, nonexistent host, zero ran.
        var tally = new BestPracticeRunTally(Attempted: 9, Succeeded: 0, Failed: 9);

        var line = BestPracticeRunReporting.DescribeCompletion(tally);

        line.Should().NotContain("All 9 selected scripts completed");
        line.Should().Contain("FAILED");
        line.Should().Contain("Nothing ran");
    }

    [Fact]
    public void That_run_is_not_painted_green()
    {
        // The colour is the half an operator reads first: the sentence was literally true read as
        // "the loop finished", and it was the most recent line on the panel, in green.
        BestPracticeRunReporting.CompletionIsSuccess(
            new BestPracticeRunTally(9, 0, 9)).Should().BeFalse();
    }

    [Fact]
    public void A_partial_run_states_both_numbers()
    {
        var line = BestPracticeRunReporting.DescribeCompletion(
            new BestPracticeRunTally(Attempted: 9, Succeeded: 7, Failed: 2));

        line.Should().Contain("9").And.Contain("7").And.Contain("2");
        line.Should().Contain("failed");
        BestPracticeRunReporting.CompletionIsSuccess(new BestPracticeRunTally(9, 7, 2))
            .Should().BeFalse("a run with any failure is not a success");
    }

    [Fact]
    public void A_genuinely_clean_run_still_says_so_in_green()
    {
        // Honesty runs both ways. A fix that made every run look doubtful would just move the lie.
        var tally = new BestPracticeRunTally(9, 9, 0);

        BestPracticeRunReporting.DescribeCompletion(tally).Should().Contain("All 9 selected scripts completed");
        BestPracticeRunReporting.CompletionIsSuccess(tally).Should().BeTrue();
    }

    [Fact]
    public void A_single_script_run_reads_naturally_in_both_directions()
    {
        BestPracticeRunReporting.DescribeCompletion(new BestPracticeRunTally(1, 1, 0))
            .Should().Contain("The 1 selected script completed");
        BestPracticeRunReporting.DescribeCompletion(new BestPracticeRunTally(1, 0, 1))
            .Should().Contain("FAILED");
    }

    [Fact]
    public void A_run_with_nothing_enabled_claims_nothing()
    {
        var tally = new BestPracticeRunTally(0, 0, 0);

        BestPracticeRunReporting.DescribeCompletion(tally).Should().Contain("nothing ran");
        BestPracticeRunReporting.CompletionIsSuccess(tally)
            .Should().BeFalse("zero scripts is not a successful run of scripts");
    }

    // ── pages-r2-06: the grid says whose rows it holds ───────────────────────

    [Fact]
    public void The_grid_caption_names_the_script_and_its_row_count()
    {
        BestPracticeRunReporting.DescribeResultsOwner("ZZHuntA_read", 11)
            .Should().Contain("ZZHuntA_read").And.Contain("11");
    }

    [Fact]
    public void One_script_with_rows_withholds_nothing_and_says_nothing()
    {
        BestPracticeRunReporting.DescribeRetainedResults(1).Should().BeNull();
        BestPracticeRunReporting.DescribeRetainedResults(0).Should().BeNull();
    }

    [Fact]
    public void Earlier_result_sets_that_were_counted_but_not_kept_are_disclosed()
    {
        // The refute pass's separate point: on a clean multi-script run the panel asserts every
        // script's row count while rendering only the last script's table. The counts are honest;
        // their evidence was simply absent, with nothing saying so.
        var note = BestPracticeRunReporting.DescribeRetainedResults(3);

        note.Should().NotBeNull();
        note!.Should().Contain("2 earlier scripts");
        note.Should().Contain("not shown");
    }

    [Fact]
    public void The_singular_case_reads_as_one_script_not_one_scripts()
    {
        BestPracticeRunReporting.DescribeRetainedResults(2)
            .Should().StartWith("1 earlier script in this run");
    }

    // ── Markup lints: both defects were literals in the .razor ───────────────

    private static string Markup() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Markup", "BestPractice.razor"));

    [Fact]
    public void The_unconditional_completion_literal_is_gone_from_the_page()
    {
        // `_executionMessages.Add(("All scripts completed.", false, true));` was the defect, in
        // full, on one line. If it comes back every tally test above stays green.
        Markup().Should().NotContain("\"All scripts completed.\"");
    }

    [Fact]
    public void The_page_computes_its_closing_line_from_the_tally()
    {
        var markup = Markup();

        markup.Should().Contain("BestPracticeRunReporting.DescribeCompletion");
        markup.Should().Contain("BestPracticeRunReporting.CompletionIsSuccess",
            "the colour must be computed from the same tally as the sentence");
        markup.Should().Contain("new BestPracticeRunTally(scripts.Count, succeeded, failed)");
    }

    [Fact]
    public void The_result_grid_carries_its_owning_script()
    {
        Markup().Should().Contain("BestPracticeRunReporting.DescribeResultsOwner",
            "an unlabelled table under another script's error was the whole of pages-r2-06");
    }

    [Fact]
    public void The_grid_is_cleared_before_each_script_runs()
    {
        // The stale-table half. ExecuteScript must null the table on entry AND in its catch;
        // either alone leaves a window where a previous script's rows render under this one's
        // error.
        var markup = Markup();

        var executeAt = markup.IndexOf("private async Task<bool> ExecuteScript", StringComparison.Ordinal);
        executeAt.Should().BeGreaterThan(0);

        var tryAt = markup.IndexOf("try", executeAt, StringComparison.Ordinal);
        var preamble = markup.Substring(executeAt, tryAt - executeAt);

        preamble.Should().Contain("_currentResults = null",
            "the grid must be cleared BEFORE the script runs, not only after it fails");
    }

    [Fact]
    public void A_failing_script_leaves_no_table_behind()
    {
        var markup = Markup();

        var catchAt = markup.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        catchAt.Should().BeGreaterThan(0);
        var catchBlock = markup.Substring(catchAt, Math.Min(500, markup.Length - catchAt));

        catchBlock.Should().Contain("_currentResults = null");
    }

    [Fact]
    public void ExecuteScript_reports_its_outcome_to_the_caller()
    {
        // The root cause of pages-r1-04: ExecuteScript returned void, so RunSelected could not
        // have counted failures even if it had wanted to.
        Markup().Should().Contain("private async Task<bool> ExecuteScript");
    }
}
