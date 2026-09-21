/* In the name of God, the Merciful, the Compassionate */

// Pages lane, cluster 1 (2026-08-28) - an unreadable or refused msdb read rendered as a
// confident claim about a server.
//
//   pages-r1-01  JobInventoryService.GetJobsAsync returned Array.Empty for a failed, refused or
//                unroutable read - the same value an instance with no Agent jobs returns. So a
//                primary whose msdb could not be read diffed as "has no jobs", every job on the
//                secondary rendered as "extra on secondary", and each one was tickable for an
//                IRREVERSIBLE delete (DELETEEXTRAJOB, Reversible = false) under the sentence
//                "N job(s) compared between A and B".
//   pages-r1-11  The same empty list made /agent-job-guard print "No SQL Agent jobs found on
//                <server>." - a claim about the server from a read that never succeeded.
//   pages-r2-01  /agent-timeline swallowed every per-server read in a wholly silent
//                catch (Exception) { }, then computed Total/Failed/Succeeded/Unique over whatever
//                subset answered, so "Failed 0" read as an estate fact.
//
// One fix closes all three: the read carries its outcome. These are unit tests over that type and
// the coverage prose, plus LINTS over the shipped markup, because which branch each page renders
// exists only in the .razor. The live exercise - a real msdb read that succeeds and a real one
// that fails - is AgentJobReadLiveTests in this folder (arm-gated, inert unarmed).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using SQLTriage.Data.Models.Jobs;
using SQLTriage.Data.Services.Jobs;
using Xunit;

namespace SQLTriage.Tests;

public class AgentJobReadHonestyTests
{
    private static AgentJobDefinition Job(string name) => new() { JobId = Guid.NewGuid(), Name = name };

    // ── pages-r1-01/r1-11: the read carries its outcome ──────────────────────

    [Fact]
    public void A_successful_read_of_zero_jobs_is_a_measurement()
    {
        var read = JobInventoryRead.Success("A", Array.Empty<AgentJobDefinition>());

        read.Succeeded.Should().BeTrue();
        read.Jobs.Should().BeEmpty();
        // Nothing extra to say: msdb answered and the answer was "none".
        read.DescribeFailure().Should().BeEmpty();
    }

    [Fact]
    public void A_failed_read_is_not_a_measurement_and_says_so()
    {
        var read = JobInventoryRead.Failure("A", "Login failed for user 'x'.");

        read.Succeeded.Should().BeFalse();
        read.Jobs.Should().BeEmpty();
        read.DescribeFailure()
            .Should().Contain("Could not read the SQL Agent jobs on 'A'")
            .And.Contain("Login failed for user 'x'.")
            .And.Contain("Nothing below describes that instance.");
    }

    [Fact]
    public void An_unroutable_instance_says_nothing_was_contacted()
    {
        var read = JobInventoryRead.NoConnectionFor("ZZNOSUCH");

        read.Succeeded.Should().BeFalse();
        read.DescribeFailure()
            .Should().Contain("No configured connection covers 'ZZNOSUCH'")
            .And.Contain("not a statement about what jobs exist there");
    }

    // ── pages-r1-01: the seam that stops the delete offer ────────────────────

    [Fact]
    public void Two_reads_that_both_answered_do_not_block_the_comparison()
    {
        JobInventoryRead.DescribeComparisonBlocked(
            JobInventoryRead.Success("P", new[] { Job("nightly") }),
            JobInventoryRead.Success("S", Array.Empty<AgentJobDefinition>()))
            .Should().BeNull("both sides were measured, so the diff is real - and a genuinely "
                           + "empty secondary must still diff as 'missing'");
    }

    [Fact]
    public void An_unread_primary_blocks_the_comparison_and_names_the_side()
    {
        var blocked = JobInventoryRead.DescribeComparisonBlocked(
            JobInventoryRead.Failure("P", "A network-related error occurred."),
            JobInventoryRead.Success("S", new[] { Job("nightly"), Job("weekly") }));

        blocked.Should().NotBeNull();
        blocked!.Should().Contain("The primary did not answer")
            .And.Contain("no comparison to show")
            .And.Contain("A network-related error occurred.")
            .And.Contain("not evidence that it is missing from the server");
    }

    [Fact]
    public void An_unread_secondary_blocks_the_comparison_too()
    {
        // The other direction matters as much: an unread SECONDARY would render every primary job
        // as "missing" and offer to create the lot on a server nobody could read.
        var blocked = JobInventoryRead.DescribeComparisonBlocked(
            JobInventoryRead.Success("P", new[] { Job("nightly") }),
            JobInventoryRead.NoConnectionFor("S"));

        blocked.Should().NotBeNull();
        blocked!.Should().Contain("The secondary did not answer").And.Contain("'S'");
    }

    [Fact]
    public void Both_sides_unread_reports_both_reasons()
    {
        var blocked = JobInventoryRead.DescribeComparisonBlocked(
            JobInventoryRead.Failure("P", "primary reason."),
            JobInventoryRead.Failure("S", "secondary reason."));

        blocked.Should().NotBeNull();
        blocked!.Should().Contain("Neither instance answered")
            .And.Contain("primary reason.")
            .And.Contain("secondary reason.");
    }

    // ── pages-r2-01: a sweep's totals carry what they were measured over ─────

    private static JobHistoryServerRead Answered(string name, int rows) => new(name, true, null, rows);
    private static JobHistoryServerRead Silent(string name, string why) => new(name, false, why, 0);

    [Fact]
    public void A_complete_sweep_states_its_scope_without_a_gap_note()
    {
        var reads = new[] { Answered("A", 12), Answered("B", 0) };

        JobHistoryCoverage.DescribeScope(reads).Should().Be("Measured across all 2 configured instances.");
        JobHistoryCoverage.DescribeGap(reads).Should().BeNull();
    }

    [Fact]
    public void A_partial_sweep_names_what_it_could_not_read()
    {
        var reads = new[]
        {
            Answered(@".\NEW2022", 5072),
            Silent("ZZHUNTNOSUCHHOST", "A network-related or instance-specific error occurred."),
            Silent("   ", "Invalid server name."),
        };

        JobHistoryCoverage.DescribeScope(reads).Should().Be("Measured across 1 of 3 configured instances.");

        var gap = JobHistoryCoverage.DescribeGap(reads);
        gap.Should().NotBeNull();
        gap!.Should().Contain("2 instances did not answer")
            .And.Contain("ZZHUNTNOSUCHHOST")
            .And.Contain("(unnamed instance)")
            .And.Contain("nothing above describes them")
            .And.Contain("A network-related or instance-specific error occurred.");
    }

    [Fact]
    public void An_empty_result_from_a_sweep_that_read_nothing_is_not_an_estate_fact()
    {
        var reads = new[] { Silent("A", "down."), Silent("B", "down.") };

        var empty = JobHistoryCoverage.DescribeEmptyState(reads);
        empty.Should().Contain("None of the 2 configured instance(s) answered")
             .And.Contain("This is not a report that no jobs ran.");
        empty.Should().NotContain("No job history in the last 24 hours");
    }

    [Fact]
    public void An_empty_result_from_a_partial_sweep_scopes_its_claim()
    {
        var reads = new[] { Answered("A", 0), Silent("B", "down.") };

        JobHistoryCoverage.DescribeEmptyState(reads)
            .Should().Contain("No job history in the last 24 hours on the 1 of 2 instance(s) that answered")
            .And.Contain("B");
    }

    [Fact]
    public void No_configured_connections_says_nothing_was_contacted()
    {
        JobHistoryCoverage.DescribeEmptyState(Array.Empty<JobHistoryServerRead>())
            .Should().Contain("No enabled connections are configured");
        JobHistoryCoverage.DescribeScope(Array.Empty<JobHistoryServerRead>())
            .Should().Contain("nothing was read");
    }

    // ── Markup lints: the shipped .razor, not a claim about it ───────────────

    private static string ReadMarkup(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Markup", file);
        File.Exists(path).Should().BeTrue($"{file} is copied into the test output by the .csproj");
        return File.ReadAllText(path);
    }

    [Fact]
    public void AgentJobSync_blocks_the_diff_before_it_builds_rows()
    {
        var markup = ReadMarkup("AgentJobSync.razor");

        // The rows ARE the delete offer: JobDiffEngine.Diff must not be reached from a read that
        // did not answer.
        markup.Should().Contain("JobInventoryRead.DescribeComparisonBlocked");

        var blockedAt = markup.IndexOf("DescribeComparisonBlocked", StringComparison.Ordinal);
        var diffAt = markup.IndexOf("JobDiffEngine.Diff", StringComparison.Ordinal);
        blockedAt.Should().BeGreaterThan(0);
        diffAt.Should().BeGreaterThan(blockedAt, "the block is only a block if it is decided first");
    }

    [Fact]
    public void AgentJobSync_does_not_claim_a_verified_convergence_after_a_failed_re_read()
    {
        ReadMarkup("AgentJobSync.razor")
            .Should().Contain("The confirming re-read did not ")
            .And.Contain("NOT verified against the servers");
    }

    [Fact]
    public void AgentJobGuard_gates_its_empty_sentence_on_a_read_that_answered()
    {
        var markup = ReadMarkup("AgentJobGuard.razor");

        markup.Should().Contain("No SQL Agent jobs found on @_selectedServer.");

        // The sentence survives - it is correct for a server that answered with nothing. What it
        // may never again be reachable from is a read that failed.
        // The rendered sentence, not the comment above it that quotes the old defect.
        var sentenceAt = markup.IndexOf("No SQL Agent jobs found on @_selectedServer.", StringComparison.Ordinal);
        var branch = markup.LastIndexOf("else if", sentenceAt, StringComparison.Ordinal);
        markup.Substring(branch, sentenceAt - branch)
            .Should().Contain("_jobsWereRead", "the empty state must be gated on the read having answered");

        markup.Should().Contain("_readFailure", "the failure has its own rendered branch");
    }

    [Fact]
    public void AgentJobTimeline_has_no_silent_catch_and_renders_its_coverage()
    {
        var markup = ReadMarkup("AgentJobTimeline.razor");

        markup.Should().NotContain("// Silently skip servers");
        markup.Should().Contain("Logger.LogWarning", "a per-server read failure must leave a trace");
        markup.Should().Contain("JobHistoryCoverage.DescribeScope")
              .And.Contain("JobHistoryCoverage.DescribeGap")
              .And.Contain("JobHistoryCoverage.DescribeEmptyState");

        // The old unconditional claim is gone from the empty state.
        markup.Should().NotContain("No job history found in the last 24 hours on the connected instances.");
    }
}
