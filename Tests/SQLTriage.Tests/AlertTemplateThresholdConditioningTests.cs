/* In the name of God, the Merciful, the Compassionate */

// ── Gate fix C3 (2026-08-05) ─────────────────────────────────────────────────────────────────
// Four conditions can fire one alert and only two of them carry a threshold the definition
// holds. AlertEvaluationService coerced the other two to 0 at two sites, so a TREND fire on an
// alert with no fixed thresholds configured reached every notification channel as
// "Threshold: 0.00" — a number that exists in no alert definition, in the row a DBA reads at 3am.
//
// These assert on RENDERED template output, not on the model, because the model was never the
// thing a human saw. Every one of the six shipped default templates is rendered for both bases:
// a fixed-threshold fire (a number is right there) and a trend fire (no number exists).

using System.Collections.Generic;
using FluentAssertions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

public class AlertTemplateThresholdConditioningTests
{
    /// <summary>Every default template that ships, by the name the config block uses.</summary>
    private static IEnumerable<(string Channel, ChannelTemplate Template)> AllDefaults()
    {
        var cfg = new AlertTemplateConfig();
        yield return ("email", cfg.Email);
        yield return ("teams", cfg.Teams);
        yield return ("slack", cfg.Slack);
        yield return ("webhook", cfg.Webhook);
        yield return ("pagerduty", cfg.PagerDuty);
        yield return ("servicenow", cfg.ServiceNow);
    }

    private static AlertNotification FixedFire() => new()
    {
        AlertName = "Log growth",
        Metric = "log-growth",
        CurrentValue = 812.5,
        ThresholdValue = 500,
        BasisKind = "FixedThreshold",
        Severity = "warning",
        InstanceName = "SERVER-A",
        Message = "812.5 MB (above the 500.0 MB warning threshold)",
    };

    private static AlertNotification TrendFire() => new()
    {
        AlertName = "Log growth",
        Metric = "log-growth",
        CurrentValue = 812.5,
        ThresholdValue = null,        // a slope has no threshold
        BasisKind = "TrendAnomaly",
        Severity = "warning",
        InstanceName = "SERVER-A",
        Message = "812.5 MB (a warning trend over the last 72 h of samples; no fixed threshold was crossed)",
    };

    private static AlertNotification UnrecordedFire() => new()
    {
        AlertName = "Log growth",
        Metric = "log-growth",
        CurrentValue = 812.5,
        ThresholdValue = null,
        BasisKind = "",               // the condition was not recorded
        Severity = "warning",
        InstanceName = "SERVER-A",
        Message = "812.5 MB (warning; the condition that fired was not recorded)",
    };

    [Fact]
    public void Every_default_template_prints_the_real_number_for_a_fixed_threshold_fire()
    {
        foreach (var (channel, t) in AllDefaults())
        {
            var body = AlertTemplateService.Render(t.Body, FixedFire());
            var subject = AlertTemplateService.Render(t.Subject, FixedFire());

            body.Should().Contain("500.00", $"{channel} fired on a threshold of 500 and must say so");
            body.Should().NotContain("{{threshold}}", $"{channel}: the token must be substituted");
            subject.Should().NotContain("{{threshold}}");
        }
    }

    [Fact]
    public void No_default_template_prints_a_threshold_of_zero_for_a_trend_fire()
    {
        foreach (var (channel, t) in AllDefaults())
        {
            var body = AlertTemplateService.Render(t.Body, TrendFire());

            body.Should().NotContain("0.00",
                $"{channel}: no threshold of 0 exists in any alert definition; that figure was "
                + "manufactured by coercing a null basis threshold");
            body.Should().NotContain("{{threshold}}", $"{channel}: the token must be substituted");
            body.Should().Contain("not applicable",
                $"{channel}: the row is rendered with the basis-conditioned text rather than a "
                + "number, so the field is never silently blank either");
            body.Should().Contain("trend",
                $"{channel}: it says WHICH condition fired instead of leaving a gap");
        }
    }

    [Fact]
    public void No_default_template_prints_a_threshold_of_zero_for_an_unrecorded_fire()
    {
        foreach (var (channel, t) in AllDefaults())
        {
            var body = AlertTemplateService.Render(t.Body, UnrecordedFire());

            body.Should().NotContain("0.00", $"{channel}: nothing measured a threshold here either");
            body.Should().Contain("not recorded",
                $"{channel}: an unrecorded condition says so rather than printing a plausible number");
        }
    }

    [Fact]
    public void The_threshold_text_is_decided_in_exactly_one_place()
    {
        // Six templates and a dozen hard-coded channel payloads all read ThresholdText, so they
        // cannot disagree about the same fire. These are the four answers it can give.
        FixedFire().ThresholdText.Should().Be("500.00");
        TrendFire().ThresholdText.Should().Contain("not applicable").And.Contain("trend");
        UnrecordedFire().ThresholdText.Should().Be("not recorded");

        new AlertNotification { ThresholdValue = null, BasisKind = "BaselineDeviation" }
            .ThresholdText.Should().Contain("not applicable").And.Contain("baseline average");
    }

    [Fact]
    public void A_learned_baseline_threshold_is_a_real_measurement_and_still_prints()
    {
        // The IQR path measures a threshold from the alert's own samples. It is not a definition
        // value, and it is still a number that was crossed, so withholding it would be the
        // opposite error.
        var learned = new AlertNotification
        {
            AlertName = "Log growth",
            Metric = "log-growth",
            CurrentValue = 812.5,
            ThresholdValue = 640,
            BasisKind = "LearnedBaseline",
            Severity = "warning",
            InstanceName = "SERVER-A",
        };

        foreach (var (channel, t) in AllDefaults())
            AlertTemplateService.Render(t.Body, learned)
                .Should().Contain("640.00", $"{channel}: a learned threshold was genuinely crossed");
    }
}
