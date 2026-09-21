/* In the name of God, the Merciful, the Compassionate */

// ── The conditioning sweep, 2026-08-05 ───────────────────────────────────────────────────────
// Four different conditions can fire one alert, and only two of them involve a threshold the
// definition carries. The message printed alert.Thresholds regardless, so a trend-only fire on an
// alert with no fixed thresholds rendered "above 0.0" — naming a threshold that exists in no
// definition, in the sentence a DBA reads at 3am.
//
// FormatMessage now takes the basis that actually fired, and each branch prints only numbers its
// own basis measured. These tests read the rendered sentence, not the code path.

using System;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

public class AlertFiringBasisTests
{
    /// <summary>The shape that produced "above 0.0": a baseline-capable alert with NO fixed thresholds.</summary>
    private static AlertDefinition TrendOnlyAlert() => new()
    {
        Id = "wave2-trend",
        Name = "Log growth",
        Unit = "megabytes",
        Operator = "greater_than",
        Thresholds = new AlertThresholds { Warning = null, Critical = null },
    };

    private static AlertDefinition FixedAlert() => new()
    {
        Id = "wave2-fixed",
        Name = "CPU",
        Unit = "percent",
        Operator = "greater_than",
        Thresholds = new AlertThresholds { Warning = 80, Critical = 95 },
    };

    private static string Render(AlertDefinition alert, double value, FiringBasis basis, string severity)
        => AlertEvaluationService.FormatMessage(alert, "SERVER1", value, basis, severity);

    // ── The shipped defect ───────────────────────────────────────────────────────────────────

    [Fact]
    public void A_trend_fire_never_names_a_threshold_because_a_slope_has_none()
    {
        var message = Render(TrendOnlyAlert(), 412.0, FiringBasis.Trend(), "Warning");

        message.Should().NotContain("0.0 MB",
            "this is the shipped defect verbatim: a trend fire on an alert with null thresholds "
            + "printed 'above 0.0 MB warning threshold'");
        message.Should().NotContain("above",
            "nothing was crossed, so nothing is 'above' anything");
        // The word "threshold" survives, in the clause that says none was crossed. What must not
        // survive is a NUMBER presented as one.
        message.Should().Be("412.0 MB (a warning trend over the last 72 h of samples; "
                          + "no fixed threshold was crossed)");
    }

    [Fact]
    public void A_trend_fire_at_critical_reads_critical_and_still_names_no_threshold()
        => Render(TrendOnlyAlert(), 900.0, FiringBasis.Trend(), "Critical")
            .Should().Be("900.0 MB (a critical trend over the last 72 h of samples; "
                       + "no fixed threshold was crossed)");

    // ── The three bases that DO carry numbers ────────────────────────────────────────────────

    [Fact]
    public void A_fixed_fire_names_the_definition_threshold_it_crossed()
        => Render(FixedAlert(), 97.4, FiringBasis.Fixed(95), "Critical")
            .Should().Be("97.4% (above the 95.0% critical threshold)");

    [Fact]
    public void A_learned_fire_names_the_learned_number_and_says_it_was_learned()
    {
        var message = Render(FixedAlert(), 88.0, FiringBasis.Learned(84.2), "Warning");

        message.Should().Be("88.0% (above the learned warning threshold of 84.2%, "
                          + "from this alert's own recent samples)");
        message.Should().NotContain("80.0",
            "the definition's fixed 80 did NOT fire here, and printing it would attribute the "
            + "alert to a threshold that was never crossed");
    }

    [Fact]
    public void A_deviation_fire_names_the_percentage_and_the_average_it_deviated_from()
        => Render(FixedAlert(), 143.0, FiringBasis.Deviation(baselineAverage: 100.0,
                     deviationPercent: 43.0, limitPercent: 25.0), "Warning")
            .Should().Be("143.0% (43.0% above the 100.0% baseline average, "
                       + "over the 25.0% deviation limit)");

    [Fact]
    public void An_unrecorded_basis_says_so_rather_than_naming_a_plausible_number()
    {
        var message = Render(FixedAlert(), 91.0, FiringBasis.Unknown(), "Warning");

        message.Should().Be("91.0% (warning; the condition that fired was not recorded, "
                          + "so no threshold is named here)");
        message.Should().NotContain("80.0");
        message.Should().NotContain("95.0");
    }

    // ── Direction still follows the operator, in every basis that prints one ─────────────────

    [Fact]
    public void The_direction_word_follows_the_operator()
    {
        var lessThan = new AlertDefinition
        {
            Id = "wave2-free-space",
            Name = "Free space",
            Unit = "percent",
            Operator = "less_than",
            Thresholds = new AlertThresholds { Warning = 10 },
        };

        Render(lessThan, 4.2, FiringBasis.Fixed(10), "Warning")
            .Should().Be("4.2% (below the 10.0% warning threshold)");
        Render(lessThan, 4.2, FiringBasis.Learned(8.0), "Warning")
            .Should().Be("4.2% (below the learned warning threshold of 8.0%, "
                       + "from this alert's own recent samples)");
    }

    // ── The basis carries only what its own condition measured ──────────────────────────────

    [Fact]
    public void A_trend_basis_carries_no_numbers_at_all()
    {
        var basis = FiringBasis.Trend();

        basis.Kind.Should().Be(AlertBasisKind.TrendAnomaly);
        basis.Threshold.Should().BeNull("substituting 0 here is exactly what printed 'above 0.0'");
        basis.BaselineAverage.Should().BeNull();
        basis.DeviationPercent.Should().BeNull();
        basis.DeviationLimitPercent.Should().BeNull();
    }

    [Fact]
    public void A_fixed_basis_carries_a_threshold_and_nothing_else()
    {
        var basis = FiringBasis.Fixed(95);

        basis.Kind.Should().Be(AlertBasisKind.FixedThreshold);
        basis.Threshold.Should().Be(95);
        basis.BaselineAverage.Should().BeNull();
        basis.DeviationPercent.Should().BeNull();
    }

    [Fact]
    public void A_deviation_basis_carries_the_two_numbers_it_measured_and_no_threshold()
    {
        var basis = FiringBasis.Deviation(baselineAverage: 100.0, deviationPercent: 43.0, limitPercent: 25.0);

        basis.Kind.Should().Be(AlertBasisKind.BaselineDeviation);
        basis.Threshold.Should().BeNull("this rule fires on a percentage off an average, not on a level");
        basis.BaselineAverage.Should().Be(100.0);
        basis.DeviationPercent.Should().Be(43.0);
        basis.DeviationLimitPercent.Should().Be(25.0);
    }

    // ── The stored filler comes back as the null it stood for ───────────────────────────────

    /// <summary>
    /// The history table's threshold column is REAL NOT NULL, so a fire with no threshold stores 0
    /// as a filler. The read-back reconstructed the null for trend and unrecorded rows only, so a
    /// deviation row — which carries no threshold by construction — came back out of the database
    /// as a measured threshold of 0. This goes through the real insert and the real read.
    /// </summary>
    [Fact]
    public void A_deviation_row_comes_back_out_of_history_with_no_threshold_rather_than_a_zero()
    {
        using var history = new AlertHistoryService(NullLogger<AlertHistoryService>.Instance);
        var alertId = $"wave2-deviation-{Guid.NewGuid():N}";

        history.UpsertAlert(new AlertState
        {
            AlertId = alertId,
            AlertName = "Log growth",
            ServerName = "SERVER1",
            Severity = "Warning",
            LastValue = 143.0,
            ThresholdValue = null,              // FiringBasis.Deviation carries none
            BasisKind = nameof(AlertBasisKind.BaselineDeviation),
            Message = "143.0% (43.0% above the 100.0% baseline average, over the 25.0% deviation limit)",
        });

        var record = history.GetHistoryByAlert(alertId).Should().ContainSingle(
            "the row has to come back at all, or the null asserted below proves nothing").Subject;

        record.ThresholdValue.Should().BeNull(
            "the 0 in the column is a filler, and reading it as a measurement is what puts "
            + "'Threshold: 0.00' in front of a DBA for a fire that crossed no threshold");
    }
}
