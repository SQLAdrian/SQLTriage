/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using FluentAssertions;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Narration;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>F-VAL value engine — the ranged effort estimate math + determinism.</summary>
    public class ValueNarrativeServiceTests
    {
        private static CheckTransition T(string id, double effort) =>
            new() { CheckId = id, CheckName = id, Category = "Security", Severity = "High", EffortHours = effort };

        private static CheckTransitionResult Result(
            List<CheckTransition> resolved, List<CheckTransition>? regressed = null,
            int baselineScore = 60, int currentScore = 75) => new()
        {
            ServerName = "SRV1",
            BaselineId = 1,
            BaselineCompositeScore = baselineScore,
            CurrentCompositeScore = currentScore,
            Resolved = resolved,
            Regressed = regressed ?? new List<CheckTransition>(),
        };

        [Fact]
        public void Estimate_ranges_at_80_to_120_percent_of_resolved_effort()
        {
            var svc = new ValueNarrativeService();
            var est = svc.Estimate(Result(new() { T("A", 10), T("B", 30) })); // 40 total

            est.ResolvedCount.Should().Be(2);
            est.TotalEffortHours.Should().Be(40);
            est.LowHours.Should().Be(32);   // floor(40 * 0.8)
            est.HighHours.Should().Be(48);  // ceil(40 * 1.2)
            est.HealthDelta.Should().Be(15);
            est.IsIndicative.Should().BeTrue();
        }

        [Fact]
        public void Estimate_backfills_zero_effort_from_resolver()
        {
            // Disk-hydrated transition has 0 effort; resolver supplies the real figure.
            var svc = new ValueNarrativeService(id => id == "A" ? 20 : 0);
            var est = svc.Estimate(Result(new() { T("A", 0) }));

            est.TotalEffortHours.Should().Be(20, "zero-effort transition must be backfilled by checkId");
            est.LowHours.Should().Be(16);
            est.HighHours.Should().Be(24);
        }

        [Fact]
        public void Estimate_prefers_transition_effort_over_resolver_when_present()
        {
            var svc = new ValueNarrativeService(_ => 999);
            var est = svc.Estimate(Result(new() { T("A", 5) }));
            est.TotalEffortHours.Should().Be(5, "a live non-zero effort must not be overridden by the resolver");
        }

        [Fact]
        public void Estimate_empty_when_nothing_resolved()
        {
            var svc = new ValueNarrativeService();
            var est = svc.Estimate(Result(new()));
            est.ResolvedCount.Should().Be(0);
            est.TotalEffortHours.Should().Be(0);
            svc.Narrate(est).Should().Be(ValuePhrasings.NothingResolvedYet);
        }

        [Fact]
        public void Estimate_null_transitions_returns_empty()
        {
            var svc = new ValueNarrativeService();
            svc.Estimate(null!).Should().BeSameAs(ValueEstimate.Empty);
        }

        [Fact]
        public void Narrate_is_deterministic_for_same_estimate()
        {
            var svc = new ValueNarrativeService();
            var est = svc.Estimate(Result(new() { T("A", 12), T("B", 8) }, new() { T("C", 4) }));
            svc.Narrate(est).Should().Be(svc.Narrate(est));
        }

        [Fact]
        public void Narrate_includes_the_ranged_hours_in_text()
        {
            var svc = new ValueNarrativeService();
            var est = svc.Estimate(Result(new() { T("A", 50) })); // 40–60
            var text = svc.Narrate(est);
            text.Should().Contain(est.LowHours.ToString());
            text.Should().Contain(est.HighHours.ToString());
        }
    }
}
