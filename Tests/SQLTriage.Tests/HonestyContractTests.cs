/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Narration;

namespace SQLTriage.Tests;

// ── Honesty-gate tests ────────────────────────────────────────────────────────
// Encodes the Language Contract (PRODUCT-ROADMAP §5b) as enforceable tests so it
// can't silently rot. Every generated sentence must be an OBSERVATION the data
// supports — never a CLAIM about facts we can't see. These guard the narration
// engine, the Risk Register consequence clauses, and the acknowledgement
// instrument. If a future edit reintroduces a forbidden claim, CI fails here.
public class HonestyContractTests
{
    // Phrases that assert what the tool cannot know, or prophesy. The contract
    // forbids these in any generated commentary. (Matched case-insensitively.)
    private static readonly string[] ForbiddenClaims =
    {
        "hours worked",
        "your dba spent",
        "dba spent",
        "will cause",
        "guaranteed",
        "definitely will",
        "you spent",
    };

    // Every captured phrasing across the whole NarrationLibrary, flattened.
    private static IEnumerable<string> AllLibraryPhrasings()
    {
        foreach (var arr in NarrationLibrary.Headline.Values) foreach (var s in arr) yield return s;
        foreach (var s in NarrationLibrary.Weakest) yield return s;
        foreach (var s in NarrationLibrary.Strength) yield return s;
        foreach (var arr in NarrationLibrary.Trend.Values) foreach (var s in arr) yield return s;
        foreach (var s in NarrationLibrary.RegressionRider) yield return s;
        foreach (var s in NarrationLibrary.CoveragePartial) yield return s;
        foreach (var s in NarrationLibrary.CoverageIndicative) yield return s;
    }

    // F-VAL value-narrative phrasings, flattened, for the same forbidden-claim scan.
    private static IEnumerable<string> AllValuePhrasings()
    {
        foreach (var s in ValuePhrasings.Headline) yield return s;
        foreach (var s in ValuePhrasings.RegressionRider) yield return s;
        yield return ValuePhrasings.IndicativeCaveat;
        yield return ValuePhrasings.NothingResolvedYet;
    }

    [Fact]
    public void NarrationLibrary_contains_no_forbidden_claim_phrases()
    {
        foreach (var phrase in AllLibraryPhrasings())
        {
            var lower = phrase.ToLowerInvariant();
            foreach (var bad in ForbiddenClaims)
                lower.Should().NotContain(bad,
                    $"the Language Contract forbids the claim phrase '{bad}' in generated commentary — found in: \"{phrase}\"");
        }
    }

    [Fact]
    public void ValuePhrasings_contain_no_forbidden_claim_phrases()
    {
        foreach (var phrase in AllValuePhrasings())
        {
            var lower = phrase.ToLowerInvariant();
            foreach (var bad in ForbiddenClaims)
                lower.Should().NotContain(bad,
                    $"the Language Contract forbids the claim phrase '{bad}' in the value narrative — found in: \"{phrase}\"");
        }
    }

    [Fact]
    public void ValueHeadlines_are_ranged_and_attributed_to_a_standard()
    {
        // Every headline must hedge precision with a range token AND attribute to a book rate /
        // standard — never assert a timesheet. This is the heart of the F-VAL contract.
        foreach (var h in ValuePhrasings.Headline)
        {
            var lower = h.ToLowerInvariant();
            (h.Contains("{low}") && h.Contains("{high}")).Should().BeTrue(
                $"value headline must present a low–high RANGE — \"{h}\" does not");
            lower.Should().ContainAny(new[] { "typically", "book rate", "usually", "would" },
                $"value headline must attribute to a standard, not a timesheet — \"{h}\"");
        }
    }

    [Fact]
    public void ValueNarrative_surfaces_regressions_whenever_they_exist()
    {
        // Progress must never be told without the regressions alongside it.
        var svc = new ValueNarrativeService(_ => 0);
        var est = new ValueEstimate
        {
            ResolvedCount = 5,
            RegressedCount = 2,
            TotalEffortHours = 40,
            LowHours = 32,
            HighHours = 48,
            HealthDelta = 10,
            IsIndicative = true,
        };
        var text = svc.Narrate(est).ToLowerInvariant();
        text.Should().ContainAny("regress", "the other way",
            "a value narrative with regressions must surface them, even mid-progress");
        text.Should().Contain("indicative",
            "the value figure must declare itself indicative (per-check effort not yet audited)");
    }

    [Fact]
    public void BaselineNarration_steady_state_does_not_assert_a_direction_or_zero_counts()
    {
        // Baseline == current (nothing resolved, nothing regressed): the read must NOT claim
        // the estate "slipped"/"improved" off composite-score noise, and must never render a
        // "0 checks newly failing"-style zero-count slot.
        var t = new CheckTransitionResult
        {
            ServerName = "SRV1",
            BaselineId = 1,
            BaselineCompositeScore = 100,
            CurrentCompositeScore = 95, // score noise — must NOT drive a worsening verdict
            Resolved = new List<CheckTransition>(),
            Regressed = new List<CheckTransition>(),
        };
        var baseline = new BaselineSummary
        {
            BaselineId = 1, ServerName = "SRV1", CapturedAt = "2026-06-15 11:00:00",
            CompositeScore = 100, TotalChecks = 487, PassedChecks = 415, FailedChecks = 72,
        };

        var text = NarrationService.NarrateBaseline(t, baseline).ToLowerInvariant();
        text.Should().NotContainAny("slipped", "newly failing", "regressed", "wrong way",
            "a steady estate must not be narrated as worsening off score noise");
        text.Should().NotContain("0 checks", "no zero-count slot should ever render");
    }

    [Fact]
    public void BaselineNarration_worsening_only_with_real_regressions()
    {
        var t = new CheckTransitionResult
        {
            ServerName = "SRV1", BaselineId = 1, BaselineCompositeScore = 80, CurrentCompositeScore = 70,
            Resolved = new List<CheckTransition>(),
            Regressed = new List<CheckTransition> { new() { CheckId = "A" }, new() { CheckId = "B" } },
        };
        var baseline = new BaselineSummary
        {
            BaselineId = 1, ServerName = "SRV1", CapturedAt = "x",
            CompositeScore = 80, TotalChecks = 100, PassedChecks = 60, FailedChecks = 40,
        };
        var text = NarrationService.NarrateBaseline(t, baseline).ToLowerInvariant();
        text.Should().ContainAny("slipped", "regressed", "wrong way",
            "a genuine 2-regression state should surface the regression honestly");
        text.Should().Contain("2 ", "regression phrasing must carry the real non-zero count (2)");
        text.Should().NotContainAny("0 checks newly failing", "0 have regressed",
            "regression phrasing must never render a zero count");
    }

    [Fact]
    public void ValueNarrative_does_not_invent_value_when_nothing_resolved()
    {
        var svc = new ValueNarrativeService(_ => 0);
        var est = new ValueEstimate { ResolvedCount = 0, TotalEffortHours = 0 };
        var text = svc.Narrate(est).ToLowerInvariant();
        text.Should().ContainAny("nothing has moved", "no remediation effort");
    }

    [Fact]
    public void AcknowledgementStatements_contain_no_forbidden_claim_phrases()
    {
        foreach (var formal in new[] { true, false })
        {
            var lower = ReportBundleService.AcknowledgementStatement(formal).ToLowerInvariant();
            foreach (var bad in ForbiddenClaims)
                lower.Should().NotContain(bad,
                    $"acknowledgement statement (formal={formal}) must not assert un-knowable claims");
        }
    }

    [Fact]
    public void Acknowledgement_both_tones_transfer_accountability_off_the_DBA()
    {
        // The whole point of the instrument: declining funding accepts the risk,
        // and responsibility does NOT rest with the person who raised it.
        var formal = ReportBundleService.AcknowledgementStatement(true).ToLowerInvariant();
        var plain = ReportBundleService.AcknowledgementStatement(false).ToLowerInvariant();

        formal.Should().Contain("accept", "formal tone must state that declining = accepting the risk");
        formal.Should().Contain("not", "formal tone must place responsibility away from the preparer");

        plain.Should().Contain("accept", "plain tone must state management accepts the risk");
        plain.Should().Contain("not", "plain tone must absolve the person who raised it");
    }

    [Fact]
    public void Narration_headline_escalates_only_with_the_data_never_above_it()
    {
        // A clean estate must NOT read as alarming; a critical one MUST read as urgent.
        // This guards against headlines that assert severity the score doesn't support.
        // A 95-score, zero-failure estate must not be narrated as critical.
        var strong = NarrationComposer.Compose(Ctx(score: 95, failed: 0, passed: 100, total: 100));
        strong.ToLowerInvariant().Should().NotContainAny("serious trouble", "stop and fix", "critical");

        // Every critical-bucket phrasing (not just the seed-selected one) must read as urgent —
        // asserting the invariant at the library level, independent of which variant is picked.
        var urgencyMarkers = new[] { "serious", "stop and fix", "immediate", "critical", "hands-on-now", "bad way", "trouble" };
        foreach (var phrasing in NarrationLibrary.Headline[ScoreBucket.Critical])
        {
            var lower = phrasing.ToLowerInvariant();
            urgencyMarkers.Any(m => lower.Contains(m)).Should().BeTrue(
                $"every critical-bucket phrasing must convey urgency — \"{phrasing}\" does not");
        }
    }

    [Fact]
    public void Narration_strong_score_with_failures_does_not_claim_nothing_failing()
    {
        // A weighted-strong score (>=85) can still carry failing low-weight checks. The headline
        // must NOT assert "nothing is actively failing" when failures exist (seen live: a 99-score
        // estate with 72 fails read as "nothing is actively failing").
        var text = NarrationComposer.Compose(Ctx(score: 99, failed: 72, passed: 415, total: 487)).ToLowerInvariant();
        text.Should().NotContain("nothing is actively failing",
            "a strong score with real failures must not claim nothing fails");
        text.Should().NotContain("every check is passing",
            "must not claim a clean sweep when checks fail");
    }

    [Fact]
    public void Narration_strong_and_clean_reads_as_flawless()
    {
        // The genuinely-clean case (Strong + 0 fail) SHOULD still read as nothing failing,
        // and must never render a "0 to tidy up"-style zero count.
        var text = NarrationComposer.Compose(Ctx(score: 95, failed: 0, passed: 100, total: 100)).ToLowerInvariant();
        text.Should().ContainAny("nothing is actively failing", "every check is passing", "very little to chase");
        text.Should().NotContainAny("0 to tidy", "0 outstanding", "0 left to chase",
            "a clean estate must not render a zero-count tidy-up");
    }

    [Fact]
    public void Narration_surfaces_partial_coverage_when_indicative()
    {
        // An indicative (partial) read must never pose as complete.
        var ctx = Ctx(score: 70, failed: 20, passed: 30, total: 100, indicative: true);
        var text = NarrationComposer.Compose(ctx).ToLowerInvariant();
        text.Should().ContainAny("partial", "indicative", "haven't run", "outstanding", "full audit");
    }

    [Fact]
    public void Narration_with_no_assessment_does_not_invent_a_verdict()
    {
        var text = NarrationComposer.Compose(Ctx(score: -1, failed: 0, passed: 0, total: 100));
        text.ToLowerInvariant().Should().ContainAny("nothing has been assessed", "no assessment");
    }

    [Fact]
    public void Narration_is_deterministic_for_the_same_estate_state()
    {
        // A CFO seeing different words on each refresh would distrust it.
        var ctx = Ctx(score: 62, failed: 40, passed: 60, total: 120, indicative: true);
        var a = NarrationComposer.Compose(ctx);
        var b = NarrationComposer.Compose(ctx);
        a.Should().Be(b, "the same estate state must always read the same words");
    }

    private static NarrationContext Ctx(double score, int failed, int passed, int total, bool indicative = false) => new()
    {
        OverallScore = score,
        FailedCount = failed,
        PassedCount = passed,
        TotalChecks = total,
        IsIndicative = indicative,
        Weakest = new DimensionFact { Label = "Configuration", Passed = passed / 2, Total = Math.Max(1, total / 2) },
    };
}
