/* In the name of God, the Merciful, the Compassionate */

// ── B-trend: a trend alert fired on the shape, never on the direction, 2026-08-22 ─────────────
//
// WHY THIS FILE EXISTS. AlertBaselineService.ComputeStats measures trend strength as
//     var slopeRatio = p50 > 0 ? Math.Abs(slope) / p50 : 0;
// and AlertEvaluationService then does
//     if (trendCrit) isCritical = true;
// with no reference to the direction the metric moved or to the alert's operator. So a metric
// falling and a metric rising produced the same signal, and either one could set Critical. Page
// Life Expectancy climbing steadily -- the best thing that metric can do -- fired a critical
// trend alert. It is the most likely explanation for two more lines in the same 2026-08-22 burst:
// "Long-Running Query on . (Critical) - value: 0" and "Version Store Size ... (Critical) - value: 3",
// neither of which is anywhere near its fixed threshold.
//
// WHAT IS REAL HERE. These drive the REAL AlertBaselineService.ComputeStats to produce the stats
// (including the real OLS slope over real timestamps) and the REAL
// AlertBaselineService.ApplyTrendDirection to decide. Nothing is re-implemented.
//
// HONEST LIMIT. AlertBaselineService itself is not constructed -- its four collaborators touch the
// shared test-output Config/ and cache DB. The call site GetTrendSignal -> ApplyTrendDirection is
// one line, read and not run: BELIEVE, not proved.
//
// MUTATION THAT MUST FAIL: make ApplyTrendDirection return (s.IsTrendWarning, s.IsTrendCritical)
// unconditionally. The falling-metric-on-a-greater_than-alert test goes red.

using System;
using System.Collections.Generic;
using FluentAssertions;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

public sealed class AlertTrendDirectionTests
{
    /// <summary>
    /// Thirty hourly samples moving at a fixed rate. Enough points to clear TrendMinSamples (20)
    /// and steep enough to clear TrendCritSlopeRatio (1.5 % of the median per hour).
    /// </summary>
    private static BaselineStats StatsForRamp(double start, double stepPerHour)
    {
        var origin = new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc);
        var rows = new List<(string AlertId, string ServerName, double Value, DateTime SampledAt)>();
        var values = new List<double>();

        for (var i = 0; i < 30; i++)
        {
            var v = start + stepPerHour * i;
            rows.Add(("page_life_expectancy", "SRV1", v, origin.AddHours(i)));
            values.Add(v);
        }

        values.Sort();
        return AlertBaselineService.ComputeStats("page_life_expectancy", "SRV1", values, rows);
    }

    [Fact]
    public void A_rising_metric_is_a_trend_signal_for_a_greater_than_alert()
    {
        var rising = StatsForRamp(start: 1000, stepPerHour: 40);

        rising.TrendSlopePerHour.Should().BeGreaterThan(0);
        rising.IsTrendCritical.Should().BeTrue("the fixture must actually be a strong trend");

        var (warn, crit) = AlertBaselineService.ApplyTrendDirection(rising, "greater_than");
        crit.Should().BeTrue();
        warn.Should().BeTrue();
    }

    [Fact]
    public void A_falling_metric_does_NOT_fire_a_trend_alert_defined_as_greater_than()
    {
        var falling = StatsForRamp(start: 3000, stepPerHour: -40);

        // The magnitude signal is still computed and stored -- that part was never wrong.
        falling.TrendSlopePerHour.Should().BeLessThan(0);
        falling.IsTrendCritical.Should().BeTrue(
            "ComputeStats measures |slope|, so the raw signal is the same as for the rising case");

        // The read-side gate is what the defect was missing.
        var (warn, crit) = AlertBaselineService.ApplyTrendDirection(falling, "greater_than");
        crit.Should().BeFalse(
            "a greater_than alert fears the metric RISING; a metric falling off a cliff is not "
            + "that alert's bad news, and this is what set Critical with no reference to direction");
        warn.Should().BeFalse();
    }

    [Fact]
    public void A_falling_metric_IS_a_trend_signal_for_a_less_than_alert()
    {
        var falling = StatsForRamp(start: 3000, stepPerHour: -40);

        var (warn, crit) = AlertBaselineService.ApplyTrendDirection(falling, "less_than");
        crit.Should().BeTrue();
        warn.Should().BeTrue();
    }

    [Fact]
    public void A_rising_metric_does_NOT_fire_a_trend_alert_defined_as_less_than()
    {
        var rising = StatsForRamp(start: 1000, stepPerHour: 40);

        var (warn, crit) = AlertBaselineService.ApplyTrendDirection(rising, "less_than");
        crit.Should().BeFalse();
        warn.Should().BeFalse();
    }

    [Fact]
    public void A_flat_metric_points_nowhere_and_fires_nothing()
    {
        var flat = StatsForRamp(start: 2000, stepPerHour: 0);

        flat.TrendSlopePerHour.Should().Be(0);
        AlertBaselineService.ApplyTrendDirection(flat, "greater_than").Should().Be((false, false));
        AlertBaselineService.ApplyTrendDirection(flat, "less_than").Should().Be((false, false));
    }
}
