/* In the name of God, the Merciful, the Compassionate */

// ── The conditioning sweep, 2026-08-05 ───────────────────────────────────────────────────────
// THE LAW under test: a printed claim must be conditioned on the same measurement that produced
// the verdict beside it.
//
// The root cause these tests pin: ExecutiveHealthService.ScoreResource's offline branch omitted
// the state argument, so "the server did not answer" was constructed as a MEASURED 0. That single
// missing argument printed four claims nothing had measured — the /cio estate mean and its
// assessed-server caption, /dba's "Resource 0/100", and the portal daily-summary health block,
// whose own guard comment promised it never emits a fabricated value.
//
// Every sentence a surface renders in an unmeasured state is produced by EstateHealthPolicy and
// asserted here character-for-character. That is deliberate: a sentence assembled inside a .razor
// can only be checked as a markup substring, and a markup substring assertion is how a prior round
// of this same work passed vacuously. The live rendered matrix is still the verifier's job; these
// tests pin the text that matrix must contain.

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

public class EstateHealthPolicyTests
{
    // ── Builders: one per state, so every test names the state it is exercising ──────────────

    private static DimensionScore Dim(string name, DimensionState state, int score = 100)
        => new(name, 0.20, score, $"{name} summary", $"{name} tooltip", state: state);

    private static ExecutiveHealthScore Assessed(int score = 80)
        => new()
        {
            Score = score,
            Severity = EstateHealthPolicy.ScoreToSeverity(score),
            Breakdown = new HealthScoreBreakdown
            {
                Security = Dim("Security", DimensionState.Measured, score),
                Compliance = Dim("Compliance", DimensionState.NotAssessed),
                Performance = Dim("Performance", DimensionState.NotAssessed),
                Resource = Dim("Resource", DimensionState.NotAssessed),
                Blocking = Dim("Blocking", DimensionState.NotAssessed),
            }
        };

    /// <summary>Exactly the shape the offline branch produces: Resource unreachable, rest uncollected.</summary>
    private static ExecutiveHealthScore Offline()
        => new()
        {
            Score = 0,
            Severity = HealthSeverity.Unknown,
            Breakdown = new HealthScoreBreakdown
            {
                Resource = Dim("Resource", DimensionState.Unreachable, 0),
            }
        };

    private static ExecutiveHealthScore NotAssessed() => new();

    private static ExecutiveHealthScore Faulted()
        => new() { Severity = HealthSeverity.Unknown, CollectionFailed = true };

    // ── 1. The root cause, at the model ──────────────────────────────────────────────────────

    [Fact]
    public void An_unreachable_dimension_is_not_a_measurement()
    {
        var dim = Dim("Resource", DimensionState.Unreachable, 0);

        dim.HasData.Should().BeFalse(
            "the measurement was 'the server did not answer', which is not a resource-saturation "
            + "score — this is the exact assertion that failed before the fix, because the ctor "
            + "default made the offline branch read as measured");
        dim.MeasuredScore.Should().BeNull("there is no score to print in this state");
        dim.Score.Should().Be(0, "the 0 survives only as a placeholder for the composite to skip");
    }

    [Fact]
    public void An_offline_server_is_not_an_assessed_server()
    {
        var offline = Offline();

        offline.IsAssessed.Should().BeFalse(
            "nothing measured this server, so it must not be counted among the assessed");
        offline.State.Should().Be(HealthAssessmentState.Unreachable);
        offline.MeasuredScore.Should().BeNull();
    }

    [Theory]
    [InlineData(DimensionState.Measured, true)]
    [InlineData(DimensionState.NotAssessed, false)]
    [InlineData(DimensionState.Unreachable, false)]
    [InlineData(DimensionState.CollectionFailed, false)]
    public void Only_a_measured_dimension_reads_as_data(DimensionState state, bool expected)
        => Dim("Any", state).HasData.Should().Be(expected);

    [Fact]
    public void Server_state_ranks_measured_above_unreachable_above_failed_above_absent()
    {
        // One measured dimension makes the server assessed even when another went silent:
        // it really was assessed, on that dimension.
        var mixed = new ExecutiveHealthScore
        {
            Breakdown = new HealthScoreBreakdown
            {
                Security = Dim("Security", DimensionState.Measured, 62),
                Resource = Dim("Resource", DimensionState.Unreachable, 0),
                Blocking = Dim("Blocking", DimensionState.CollectionFailed),
            }
        };
        mixed.State.Should().Be(HealthAssessmentState.Assessed);

        Offline().State.Should().Be(HealthAssessmentState.Unreachable);

        new ExecutiveHealthScore
        {
            Breakdown = new HealthScoreBreakdown { Blocking = Dim("Blocking", DimensionState.CollectionFailed) }
        }.State.Should().Be(HealthAssessmentState.Unknown);

        NotAssessed().State.Should().Be(HealthAssessmentState.NotAssessed);
    }

    [Fact]
    public void A_watched_fault_outranks_every_derived_state()
        => Faulted().State.Should().Be(HealthAssessmentState.Unknown,
            "only the caller that watched collection throw can assert this, and it must win");

    // ── 2. Classification and the publisher guard ────────────────────────────────────────────

    [Theory]
    [InlineData(HealthAssessmentState.Assessed, true)]
    [InlineData(HealthAssessmentState.NotAssessed, false)]
    [InlineData(HealthAssessmentState.Unreachable, false)]
    [InlineData(HealthAssessmentState.Unknown, false)]
    public void MayPublish_is_true_in_exactly_one_state(HealthAssessmentState state, bool expected)
    {
        var score = state switch
        {
            HealthAssessmentState.Assessed => Assessed(),
            HealthAssessmentState.Unreachable => Offline(),
            HealthAssessmentState.Unknown => Faulted(),
            _ => NotAssessed(),
        };
        EstateHealthPolicy.Classify(score).Should().Be(state, "sanity: the fixture is in the state under test");
        EstateHealthPolicy.MayPublish(score).Should().Be(expected);
    }

    [Fact]
    public void MayPublish_refuses_the_offline_server_the_old_guard_let_through()
        => EstateHealthPolicy.MayPublish(Offline()).Should().BeFalse(
            "the publisher's guard read IsAssessed, and an offline server read assessed — the "
            + "portal daily summary published a 0 that nothing measured");

    [Fact]
    public void A_missing_score_classifies_as_not_assessed_not_as_a_fault()
        => EstateHealthPolicy.Classify(null).Should().Be(HealthAssessmentState.NotAssessed);

    // ── 3. The estate rollup: only measured servers reach the mean ───────────────────────────

    [Fact]
    public void The_mean_excludes_the_unreachable_server_that_used_to_drag_it()
    {
        var rollup = EstateHealthPolicy.Summarise(new[] { Assessed(80), Offline() });

        rollup.AssessedCount.Should().Be(1);
        rollup.UnreachableCount.Should().Be(1);
        rollup.MeanScore.Should().Be(80,
            "before the fix this averaged 80 with the offline server's fabricated 0 and printed 40");
    }

    [Fact]
    public void No_measured_server_means_no_mean_and_no_band()
    {
        var rollup = EstateHealthPolicy.Summarise(new[] { Offline(), NotAssessed(), Faulted() });

        rollup.HasMean.Should().BeFalse();
        rollup.MeanScore.Should().BeNull("0 is not the answer to 'we did not measure'");
        rollup.Severity.Should().BeNull("Critical is not the answer either");
        rollup.UnreachableCount.Should().Be(1);
        rollup.NotAssessedCount.Should().Be(1);
        rollup.UnknownCount.Should().Be(1);
        rollup.ServerCount.Should().Be(3);
    }

    [Fact]
    public void Every_server_lands_in_exactly_one_state()
    {
        var servers = new[] { Assessed(90), Assessed(50), Offline(), NotAssessed(), Faulted() };
        var rollup = EstateHealthPolicy.Summarise(servers);

        (rollup.AssessedCount + rollup.NotAssessedCount + rollup.UnreachableCount + rollup.UnknownCount)
            .Should().Be(rollup.ServerCount, "a server counted twice, or not at all, breaks every caption");
        rollup.ServerCount.Should().Be(servers.Length);
    }

    [Fact]
    public void The_rollup_bands_the_mean_the_same_way_a_single_server_is_banded()
    {
        // Pinned by test rather than by a comment claiming the thresholds match.
        EstateHealthPolicy.ScoreToSeverity(71).Should().Be(HealthSeverity.Healthy);
        EstateHealthPolicy.ScoreToSeverity(70).Should().Be(HealthSeverity.Warning);
        EstateHealthPolicy.ScoreToSeverity(51).Should().Be(HealthSeverity.Warning);
        EstateHealthPolicy.ScoreToSeverity(50).Should().Be(HealthSeverity.Critical);

        EstateHealthPolicy.Summarise(new[] { Assessed(72) }).Severity.Should().Be(HealthSeverity.Healthy);
    }

    // ── 4. The sentences: /cio caption, every state ──────────────────────────────────────────

    [Fact]
    public void EstateBasis_names_the_assessed_count_and_every_server_it_excludes()
    {
        var basis = EstateHealthPolicy.EstateBasis(
            EstateHealthPolicy.Summarise(new[] { Assessed(80), Offline(), NotAssessed(), Faulted() }));

        basis.Should().Be(
            "Equal-weight mean across 1 assessed server. "
            + "1 server did not answer and is not in the mean. "
            + "Health collection failed for 1 server, so nothing is claimed about it. "
            + "1 server has not been assessed yet.");
    }

    [Fact]
    public void EstateBasis_with_nothing_observed_does_not_claim_a_mean()
        => EstateHealthPolicy.EstateBasis(EstateHealthPolicy.Summarise(new[] { NotAssessed(), NotAssessed() }))
            .Should().Be("Nothing has been assessed yet, so no score is shown.");

    [Fact]
    public void EstateBasis_with_every_server_silent_says_silent_not_unassessed()
        => EstateHealthPolicy.EstateBasis(EstateHealthPolicy.Summarise(new[] { Offline(), Offline() }))
            .Should().Be("No score is shown because no server was measured. "
                       + "2 servers did not answer and are not in the mean.");

    [Fact]
    public void EstateBasis_with_no_servers_says_so()
        => EstateHealthPolicy.EstateBasis(EstateHealthPolicy.Summarise(Array.Empty<ExecutiveHealthScore>()))
            .Should().Be("No servers are configured, so nothing has been measured.");

    [Fact]
    public void EstateBasis_separates_a_failed_count_from_a_count_of_zero()
    {
        // R4. A null rollup reaches this method when the surrounding pass threw before Summarise
        // ran. Sharing the empty-estate sentence told the reader their configuration was empty
        // when the count had failed, and the two remedies point in opposite directions.
        var noCount = EstateHealthPolicy.EstateBasis(null);
        var zeroServers = EstateHealthPolicy.EstateBasis(
            EstateHealthPolicy.Summarise(Array.Empty<ExecutiveHealthScore>()));

        noCount.Should().NotBe(zeroServers);
        noCount.Should().Be("The estate was not summarised on this pass, so nothing can be said "
                          + "about its coverage. This is a failure to count, not a count of zero.");
        noCount.Should().NotContain("No servers are configured");
    }

    [Fact]
    public void EstateBasis_singular_and_plural_agree_with_the_counts()
    {
        EstateHealthPolicy.EstateBasis(EstateHealthPolicy.Summarise(new[] { Assessed(60) }))
            .Should().Be("Equal-weight mean across 1 assessed server.");

        EstateHealthPolicy.EstateBasis(EstateHealthPolicy.Summarise(new[] { Assessed(60), Assessed(80) }))
            .Should().Be("Equal-weight mean across 2 assessed servers.");

        EstateHealthPolicy.EstateBasis(EstateHealthPolicy.Summarise(new[] { Assessed(60), Faulted(), Faulted() }))
            .Should().Be("Equal-weight mean across 1 assessed server. "
                       + "Health collection failed for 2 servers, so nothing is claimed about them.");
    }

    [Fact]
    public void EstateBasis_never_describes_a_population_it_did_not_count()
    {
        var basis = EstateHealthPolicy.EstateBasis(EstateHealthPolicy.Summarise(new[] { Assessed(80) }));

        basis.Should().NotContain("did not answer", "no server was observed silent");
        basis.Should().NotContain("collection failed", "no collection was observed to fail");
        basis.Should().NotContain("not been assessed", "every server was assessed");
    }

    // ── 5. The sentences: /dba card and /health hero, every state ────────────────────────────

    [Fact]
    public void ServerBasis_says_which_kind_of_nothing_it_is()
    {
        EstateHealthPolicy.ServerBasis(Assessed()).Should().BeEmpty(
            "an assessed server renders its dimensions, not an explanation");
        EstateHealthPolicy.ServerBasis(Offline())
            .Should().Be("This server did not answer, so no dimension was measured.");
        EstateHealthPolicy.ServerBasis(Faulted())
            .Should().Be("Health collection failed for this server, so no dimension was measured.");
        EstateHealthPolicy.ServerBasis(NotAssessed())
            .Should().Be("No dimension has been collected for this server yet.");
        EstateHealthPolicy.ServerBasis(null)
            .Should().Be("No dimension has been collected for this server yet.");
    }

    [Fact]
    public void ServerCoverage_names_the_unmeasured_dimensions_by_their_reason()
    {
        var mixed = new ExecutiveHealthScore
        {
            Breakdown = new HealthScoreBreakdown
            {
                Security = Dim("Security", DimensionState.Measured, 62),
                Compliance = Dim("Compliance", DimensionState.NotAssessed),
                Performance = Dim("Performance", DimensionState.NotAssessed),
                Resource = Dim("Resource", DimensionState.Unreachable, 0),
                Blocking = Dim("Blocking", DimensionState.CollectionFailed),
            }
        };

        EstateHealthPolicy.ServerCoverage(mixed).Should().Be(
            "Indicative: 1 of 5 dimensions measured. "
            + "Resource could not be measured because the server did not answer. "
            + "Collecting Blocking failed. "
            + "Compliance and Performance have no data collected yet. "
            + "Unmeasured dimensions are excluded from the score, not assumed healthy.");
    }

    [Fact]
    public void ServerCoverage_is_silent_when_all_five_were_measured()
    {
        var full = new ExecutiveHealthScore
        {
            Breakdown = new HealthScoreBreakdown
            {
                Security = Dim("Security", DimensionState.Measured, 80),
                Compliance = Dim("Compliance", DimensionState.Measured, 80),
                Performance = Dim("Performance", DimensionState.Measured, 80),
                Resource = Dim("Resource", DimensionState.Measured, 80),
                Blocking = Dim("Blocking", DimensionState.Measured, 80),
            }
        };
        EstateHealthPolicy.ServerCoverage(full).Should().BeEmpty("there is nothing left to qualify");
    }

    [Fact]
    public void ServerCoverage_for_an_unmeasured_server_falls_back_to_the_card_sentence()
    {
        EstateHealthPolicy.ServerCoverage(Offline())
            .Should().Be("This server did not answer, so no dimension was measured.",
                "a server with no measured dimension has no coverage fraction to state");
        EstateHealthPolicy.ServerCoverage(Faulted())
            .Should().Be("Health collection failed for this server, so no dimension was measured.");
        EstateHealthPolicy.ServerCoverage(NotAssessed())
            .Should().Be("No dimension has been collected for this server yet.");
    }

    // ── 6. The sentences: dimension rows, every state ────────────────────────────────────────

    [Fact]
    public void DimensionValue_prints_a_number_only_where_one_was_measured()
    {
        EstateHealthPolicy.DimensionValue(Dim("Resource", DimensionState.Measured, 84)).Should().Be("84/100");
        EstateHealthPolicy.DimensionValue(Dim("Resource", DimensionState.Unreachable, 0)).Should().Be("n/a");
        EstateHealthPolicy.DimensionValue(Dim("Resource", DimensionState.NotAssessed)).Should().Be("n/a");
        EstateHealthPolicy.DimensionValue(Dim("Resource", DimensionState.CollectionFailed)).Should().Be("n/a");
        EstateHealthPolicy.DimensionValue(null).Should().Be("n/a");
    }

    [Fact]
    public void DimensionValue_never_prints_the_placeholder_of_an_unmeasured_dimension()
    {
        // The two placeholders are the two numbers that shipped as findings: 0 for offline,
        // 100 for uncollected.
        EstateHealthPolicy.DimensionValue(Dim("Resource", DimensionState.Unreachable, 0))
            .Should().NotContain("0/100");
        EstateHealthPolicy.DimensionValue(Dim("Security", DimensionState.NotAssessed, 100))
            .Should().NotContain("100/100");
    }

    [Fact]
    public void DimensionBasis_never_offers_a_remedy_for_a_state_the_remedy_does_not_fit()
    {
        var unreachable = EstateHealthPolicy.DimensionBasis(Dim("Resource", DimensionState.Unreachable, 0));
        unreachable.Should().Be("Resource: the server did not answer, so this was not measured. "
                              + "Excluded from the score, not assumed healthy.");
        unreachable.Should().NotContain("no data collected yet",
            "that sentence invites the reader to fix it by running checks against a silent server");

        EstateHealthPolicy.DimensionBasis(Dim("Blocking", DimensionState.CollectionFailed))
            .Should().Be("Blocking: collection failed, so this was not measured. "
                       + "Excluded from the score, not assumed healthy.");

        EstateHealthPolicy.DimensionBasis(Dim("Compliance", DimensionState.NotAssessed))
            .Should().Be("Compliance: no data collected yet. "
                       + "Excluded from the score, not assumed healthy.");

        EstateHealthPolicy.DimensionBasis(Dim("Security", DimensionState.Measured, 90))
            .Should().Be("Security tooltip", "a measured dimension keeps its own explanation");
    }

    // ── 7. The cross-product, swept ──────────────────────────────────────────────────────────

    [Fact]
    public void No_surface_sentence_prints_a_score_in_any_unmeasured_state()
    {
        var unmeasured = new (string Label, ExecutiveHealthScore Score)[]
        {
            ("unassessed", NotAssessed()),
            ("offline", Offline()),
            ("collection failed", Faulted()),
        };

        foreach (var (label, score) in unmeasured)
        {
            var sentences = new[]
            {
                EstateHealthPolicy.ServerBasis(score),
                EstateHealthPolicy.ServerCoverage(score),
                EstateHealthPolicy.EstateBasis(EstateHealthPolicy.Summarise(new[] { score })),
            };

            foreach (var sentence in sentences)
            {
                sentence.Should().NotContain("/100",
                    $"the {label} state has no score, so no surface may print one");
                System.Text.RegularExpressions.Regex.IsMatch(sentence, @"\b0\b")
                    .Should().BeFalse(
                        $"the {label} state has no score, so neither placeholder (0 for offline, "
                        + $"100 for uncollected) may surface — the sentence was: \"{sentence}\"");
            }

            score.MeasuredScore.Should().BeNull($"the {label} state has no score to read");
            EstateHealthPolicy.MayPublish(score).Should().BeFalse($"the {label} state has nothing to publish");

            foreach (var dim in score.Breakdown.Dimensions)
            {
                EstateHealthPolicy.DimensionValue(dim).Should().Be("n/a",
                    $"no dimension of a {label} server was measured");
            }
        }
    }

    // ── 6. The trend, which needs TWO measurements (SR-11, 2026-08-08) ───────────────────────
    //
    // ExecutiveHealthScore.Trend holds one of three directions whatever happened, and
    // GetTrendAsync answered Stable for "no earlier snapshot" and for "the history read threw"
    // alike. /health printed that as the literal word "Stable" under an equals icon in a box
    // titled "Trend vs yesterday", for a server whose yesterday did not exist.

    [Fact]
    public void A_measured_score_with_no_earlier_snapshot_makes_no_trend_claim()
    {
        var score = Assessed();
        score.TrendState = HealthTrendState.NoPriorSnapshot;
        score.Trend = HealthTrend.Stable;      // the placeholder the old code returned here

        score.MeasuredTrend.Should().BeNull();
        EstateHealthPolicy.TrendValue(score).Should().Be("No trend yet");
        EstateHealthPolicy.TrendBasis(score).Should().Be(
            "No earlier snapshot for this server yet — a trend needs a second measurement to compare against.");
    }

    [Fact]
    public void A_failed_history_read_is_not_described_as_an_absent_snapshot()
    {
        var score = Assessed();
        score.TrendState = HealthTrendState.LookupFailed;

        score.MeasuredTrend.Should().BeNull();
        EstateHealthPolicy.TrendBasis(score).Should().Be(
            "Reading this server's score history failed, so nothing has been compared.");
    }

    [Fact]
    public void A_real_comparison_prints_its_direction_and_says_what_it_compared()
    {
        var score = Assessed();
        score.TrendState = HealthTrendState.Compared;
        score.Trend = HealthTrend.Degrading;

        score.MeasuredTrend.Should().Be(HealthTrend.Degrading);
        EstateHealthPolicy.TrendValue(score).Should().Be("Degrading");
        EstateHealthPolicy.TrendBasis(score).Should().Be(
            "Compared with yesterday's snapshot for this server.");
    }

    /// <summary>
    /// The other end of the comparison. A snapshot exists and TODAY does not: the composite is a
    /// forced 0, so the diff against a real earlier score would have read "Degrading" — a confident
    /// decline for a server nobody assessed.
    /// </summary>
    [Fact]
    public void An_unmeasured_today_makes_no_trend_claim_even_against_a_real_snapshot()
    {
        foreach (var (label, score) in new[]
                 {
                     ("offline", Offline()),
                     ("never assessed", NotAssessed()),
                     ("collection failed", Faulted()),
                 })
        {
            score.TrendState = HealthTrendState.Compared;
            score.Trend = HealthTrend.Degrading;

            score.MeasuredTrend.Should().BeNull(
                $"a {label} server has no score today, so there is nothing to compare FROM");
            EstateHealthPolicy.TrendValue(score).Should().Be("No trend yet");
            EstateHealthPolicy.TrendBasis(score).Should().NotContain("Compared with",
                $"the {label} state must not claim a comparison happened");
        }
    }

    [Fact]
    public void The_default_state_of_a_hand_built_score_claims_no_trend()
    {
        // /cio builds ExecutiveHealthScore instances by hand (the estate-mean carrier and the
        // collection-failed carrier). Whatever they set, the trend defaults to claiming nothing.
        new ExecutiveHealthScore().TrendState.Should().Be(HealthTrendState.NoPriorSnapshot);
        new ExecutiveHealthScore().MeasuredTrend.Should().BeNull();
    }

    [Fact]
    public void No_trend_sentence_names_yesterday_unless_something_was_compared()
    {
        foreach (var state in new[] { HealthTrendState.NoPriorSnapshot, HealthTrendState.LookupFailed })
        {
            var score = Assessed();
            score.TrendState = state;

            var sentence = EstateHealthPolicy.TrendBasis(score);
            sentence.Should().NotContain("yesterday",
                $"the {state} state has no yesterday to be 'vs' — that tooltip was the claim");
            sentence.Should().NotContain("Stable",
                $"the {state} state is not a stability verdict");
        }

        EstateHealthPolicy.TrendBasis(null).Should().Be(
            "No score has been collected for this server, so nothing has been compared.");
    }
}
