/* In the name of God, the Merciful, the Compassionate */

// ── Gate fix C2 (2026-08-05) ─────────────────────────────────────────────────────────────────
// The /cio narration headline is composed from the GOVERNANCE score. A governance score with
// nothing evaluated buckets ScoreBucket.Unknown, whose phrasings open "Nothing has been assessed
// yet" — a sentence that rendered directly above an Executive Health card printing a mean across
// N assessed servers, and above the estate caption that names those N by state.
//
// These pin both halves of the fix:
//   1. the headline may not declare the page empty while the same rollup says otherwise;
//   2. the prescription no longer names a Vulnerability Assessment, which has not fed Executive
//      Health since the 2026-07-16 ruling. The identical sentence was deleted from the PDF
//      earlier in this same wave citing that ruling, and this sibling was left standing.

using System;
using FluentAssertions;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Narration;
using Xunit;

namespace SQLTriage.Tests;

public class NarrationHeadlineConditioningTests
{
    /// <summary>A governance score with nothing evaluated: the state that buckets Unknown.</summary>
    private static GovernanceScore NothingEvaluated() => new()
    {
        Overall = 0,
        TotalFindings = 0,
        PassedFindings = 0,
        FailedFindings = 0,
    };

    private static ExecutiveHealthScore Assessed(int score)
    {
        var s = new ExecutiveHealthScore { Score = score, Severity = EstateHealthPolicy.ScoreToSeverity(score) };
        s.Breakdown.Security = new DimensionScore("Security", 0.25, score, "", "",
            state: DimensionState.Measured);
        return s;
    }

    private static ExecutiveHealthScore Offline()
    {
        var s = new ExecutiveHealthScore();
        s.Breakdown.Resource = new DimensionScore("Resource", 0.20, 0, "", "",
            state: DimensionState.Unreachable);
        return s;
    }

    [Fact]
    public void The_headline_does_not_declare_the_page_empty_while_the_rollup_carries_a_mean()
    {
        var rollup = EstateHealthPolicy.Summarise(new[] { Assessed(80), Assessed(60) });
        rollup.AssessedCount.Should().Be(2, "otherwise this test would pass for the wrong reason");
        rollup.MeanScore.Should().Be(70);

        var narration = NarrationService.Narrate(NothingEvaluated(), rollup.MeanScore, rollup);

        narration.Should().NotContain("Nothing has been assessed yet",
            "the Executive Health card on the same screen is printing a mean across 2 servers");
        narration.Should().NotContain("no assessment data to read yet");
        narration.Should().Contain("2 assessed servers",
            "the count comes from the same rollup the caption is composed from");
    }

    [Fact]
    public void The_headline_still_says_nothing_is_assessed_when_nothing_is()
    {
        var rollup = EstateHealthPolicy.Summarise(new[] { Offline(), Offline() });
        rollup.AssessedCount.Should().Be(0);

        var narration = NarrationService.Narrate(NothingEvaluated(), null, rollup);

        narration.Should().Match(n =>
            n.Contains("Nothing has been assessed yet") || n.Contains("no assessment data to read yet"),
            "with no measured mean anywhere on the page the original claim is true");
    }

    [Fact]
    public void A_caller_with_no_rollup_reads_exactly_as_it_did_before()
    {
        // The estate parameter is optional; every caller that has no rollup beside it keeps the
        // pre-fix sentence, so this change cannot alter a surface it was not aimed at.
        NarrationService.Narrate(NothingEvaluated(), null)
            .Should().Be(NarrationService.Narrate(NothingEvaluated(), null, null));
    }

    [Fact]
    public void No_unknown_bucket_phrasing_prescribes_a_vulnerability_assessment()
    {
        foreach (var phrasing in NarrationLibrary.Headline[ScoreBucket.Unknown])
            phrasing.Should().NotContain("Vulnerability Assessment",
                "Executive Health has not been fed by VA since the 2026-07-16 ruling, so running "
                + "one would not move the number this sentence is talking about");

        foreach (var phrasing in NarrationLibrary.HeadlineUnknownBesideAMeasuredMean)
            phrasing.Should().NotContain("Vulnerability Assessment");
    }

    [Fact]
    public void No_unknown_bucket_phrasing_carries_an_em_dash()
    {
        // Voice guide, client-visible copy. Scoped to the phrasings this fix touches: the rest of
        // NarrationLibrary still carries em-dashes and is left for a copy pass of its own.
        foreach (var phrasing in NarrationLibrary.Headline[ScoreBucket.Unknown])
            phrasing.Should().NotContain("—");

        foreach (var phrasing in NarrationLibrary.HeadlineUnknownBesideAMeasuredMean)
            phrasing.Should().NotContain("—");
    }
}
