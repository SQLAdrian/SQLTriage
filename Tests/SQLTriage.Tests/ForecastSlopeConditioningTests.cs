/* In the name of God, the Merciful, the Compassionate */

// ── The same over-claim, on the forecast slope (2026-08-09) ───────────────────────────────────
//
// ExecutiveHealthScore.Score holds 0 when nothing was measured; ForecastResult.SlopePerDay holds 0
// when no line was ever fitted. /capacity-planning printed the second one three ways at once, on
// every disk row, always: a "Stable" severity badge, "Trend: +0.00 GB/day", and "R²: 0.000".
//
// The reason it was EVERY row is worth writing down, because it is not the null path a reader would
// look for. LoadDiskForecastsFromServer does not forecast at all — it takes one live
// dm_os_volume_stats reading and hand-builds a ForecastResult with SlopePerDay = 0,
// DataPointCount = 1, RSquared = 0. So DaysUntilThreshold is null for every volume, the `else`
// branch was unconditional, and the verdict rendered was a stability finding for a volume with a
// single observation. The CPU card on the same page already guarded its own "Stable" on the result
// existing — the disk card was the outlier, not the pattern.
//
// The discriminator is ForecastResult.SlopeFitted, false by default so a hand-built result claims
// nothing, and MeasuredSlopePerDay is the conditioned reader. These tests hold both the computation
// and the wiring; the markup lint is a LINT over fixed strings in the shipped razor, not a
// boundary — same framing as ConditioningSweepMarkupTests, and said out loud for the same reason.

using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

public class ForecastSlopeConditioningTests
{
    private static List<TimeSeriesPoint> Series(params (double Hours, double Value)[] points)
    {
        var t0 = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var list = new List<TimeSeriesPoint>();
        foreach (var (hours, value) in points)
            list.Add(new TimeSeriesPoint { Time = t0.AddHours(hours), Series = "s", Value = value });
        return list;
    }

    // ── The computation ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_regression_over_spread_observations_reports_a_fitted_slope()
    {
        var result = ForecastService.CalculateLinearForecast(
            Series((0, 10), (24, 20), (48, 30), (72, 40), (96, 50)), threshold: 90);

        result.SlopeFitted.Should().BeTrue("the X values have spread, so a line was fitted");
        result.MeasuredSlopePerDay.Should().NotBeNull();
        result.MeasuredSlopePerDay!.Value.Should().BeApproximately(10, 0.001);
    }

    /// <summary>
    /// The degenerate return: every observation on one timestamp, so the denominator vanishes and
    /// the method returns a HARDCODED zero slope. Five data points and a plausible-looking row —
    /// and no line fitted anywhere. This is the state the old markup would have rendered as
    /// "Stable" plus "+0.00%/day" on the CPU card.
    /// </summary>
    [Fact]
    public void A_degenerate_regression_reports_no_fitted_slope_but_keeps_the_placeholder()
    {
        var result = ForecastService.CalculateLinearForecast(
            Series((0, 10), (0, 20), (0, 30), (0, 40), (0, 55)), threshold: 90);

        result.SlopeFitted.Should().BeFalse();
        result.MeasuredSlopePerDay.Should().BeNull("nothing was fitted, so there is no rate to print");

        // The placeholder still EXISTS — the field is non-nullable and has to hold something. That
        // is the whole shape of this defect class: the fix is never deleting the placeholder, it is
        // refusing to print it.
        result.SlopePerDay.Should().Be(0);
        result.DataPointCount.Should().Be(5, "a count of observations is honest; a slope is not");
    }

    /// <summary>
    /// NEGATIVE CONTROL, and the exact object /capacity-planning builds for a disk volume. If this
    /// ever starts reporting a slope, the default has been flipped and every hand-built result in
    /// the app silently regains a trend claim.
    /// </summary>
    [Fact]
    public void A_hand_built_result_claims_no_slope_because_the_default_claims_nothing()
    {
        var pointInTime = new ForecastService.ForecastResult
        {
            CurrentValue = 62.5,
            SlopePerDay = 0,
            DataPointCount = 1,
            RSquared = 0
        };

        pointInTime.SlopeFitted.Should().BeFalse();
        pointInTime.MeasuredSlopePerDay.Should().BeNull();
        pointInTime.DaysUntilThreshold.Should().BeNull(
            "which is why the disk card's `else` branch was reached for every volume, every time");
    }

    /// <summary>
    /// Why CapacityCollector was left alone rather than given the new discriminator too: the
    /// condition it already applies cannot be satisfied by an unfitted result in this codebase.
    /// DaysUntilThreshold is only ever set on the fitted path, so non-null implies fitted, and the
    /// portal blob was never carrying this over-claim.
    /// </summary>
    [Fact]
    public void The_portal_condition_is_already_only_satisfiable_by_a_fitted_result()
    {
        var fitted = ForecastService.CalculateLinearForecast(
            Series((0, 10), (24, 20), (48, 30), (72, 40), (96, 50)), threshold: 90);
        fitted.DaysUntilThreshold.Should().NotBeNull();
        fitted.SlopeFitted.Should().BeTrue();

        var degenerate = ForecastService.CalculateLinearForecast(
            Series((0, 10), (0, 20), (0, 30), (0, 40), (0, 55)), threshold: 90);
        degenerate.DaysUntilThreshold.Should().BeNull(
            "the degenerate path never predicts a crossing, so 'has a crossing' is a fitted-only "
            + "state and SQLTriage.Data.Services.Portal.CapacityCollector needs no change");
    }

    // ── The wiring, in the shipped markup ───────────────────────────────────────────────────

    private static string CapacityMarkup()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Markup", "CapacityPlanning.razor");
        File.Exists(path).Should().BeTrue(
            "CapacityPlanning.razor is copied to the test output by SQLTriage.Tests.csproj; if this "
            + "fails every assertion below would vacuously pass");
        return File.ReadAllText(path);
    }

    [Fact]
    public void The_capacity_page_reads_the_conditioned_slope_and_never_the_raw_one()
    {
        var markup = CapacityMarkup();

        markup.Should().Contain("MeasuredSlopePerDay",
            "the page asks whether a line was fitted before printing a rate or a verdict");

        foreach (var raw in new[]
        {
            "f.Result.SlopePerDay",
            "f.Result?.SlopePerDay",
        })
            markup.Should().NotContain(raw,
                $"a raw read of {raw} prints the placeholder 0 as a measured flat trend");
    }

    [Fact]
    public void The_disk_badge_is_no_longer_an_unconditional_stable()
    {
        var markup = CapacityMarkup();

        markup.Should().Contain("else if (f.Result?.MeasuredSlopePerDay != null)",
            "'Stable' is now reached only where a slope was actually fitted");
        markup.Should().Contain("Point-in-time",
            "the state the disk rows are actually in gets its own words");

        // POSITIVE CONTROL on this test's own premise. Every assertion above matters because the
        // disk rows are hand-built and never forecast; if that changes, these guards are reasoning
        // about a page that no longer exists and should be re-anchored deliberately.
        markup.Should().Contain("SlopePerDay = 0, // Point-in-time",
            "the disk path still synthesises its results from one live reading — the moment it runs "
            + "a real regression, revisit which branch each card should reach");
    }
}
