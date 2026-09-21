/* In the name of God, the Merciful, the Compassionate */

// INVARIANT A2 (lane alert-correctness 2, 2026-09-19): NO SHIPPED HANDLER-ROUTED ALERT DECLARES A
// LEARNED BASELINE. A routed alert (AlertEvaluationService.IsRoutedToBuiltInHandler) is evaluated by
// its built-in handler against fixed thresholds only, and the baseline seeder leaves it out
// (AlertBaselineService.SeedableAlerts). So canBaseline true on one is inert today, and it is a latent
// re-enable the day someone un-routes the alert: lane Q14's gate 2 PROVED that learned-baseline firing
// on connection_count raised a Critical on +25 connections. Ruled by Adrian 2026-09-18, "turn it off",
// for sql_response_time, the one routed alert that shipped canBaseline true at 5435f4e (80 alerts, 8
// routed).
//
// This is a STRUCTURAL census (generation rule 2b): it parses the shipped JSON into the app's own
// model and applies the evaluator's own routing rule to every alert, so a formatting change cannot
// hide an offender and a new routed alert is enumerated without anyone listing it. Its controls: a
// synthetic routed and baselined specimen must be flagged, and the body that shipped at 5435f4e
// (fixture prelane-alert-correctness-l2-alerts-5435f4e.json) must be flagged too, which is the census
// reproducing the defect it was written for.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    public class AlertRoutedBaselineCensusTests
    {
        private readonly ITestOutputHelper _out;

        public AlertRoutedBaselineCensusTests(ITestOutputHelper output) => _out = output;

        private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

        private const string WhatToCheck =
            "CHECK: the alert's queryMode and canBaseline in Config/alert-definitions.json; whether its handler "
            + "(AlertEvaluationService.EvaluateSpecialAlertAsync) now reads a learned baseline, which would make this rule "
            + "wrong; whether the alert was meant to be routed at all; and, for an existing install, "
            + "AlertDefinitionMigrator.SupersededDefinitionSignatures and SupersededDefinitionCarries.";

        /// <summary>The census rule, in one place: an alert the evaluator routes to a handler, that
        /// declares a learned baseline.</summary>
        internal static List<AlertDefinition> RoutedAndBaselined(IEnumerable<AlertDefinition> alerts) =>
            alerts.Where(a => AlertEvaluationService.IsRoutedToBuiltInHandler(a) && a.CanBaseline).ToList();

        private static List<AlertDefinition> Parse(string json)
        {
            var file = JsonSerializer.Deserialize<AlertDefinitionsFile>(json, Options);
            Assert.True(file?.Alerts != null, "the document did not parse into AlertDefinitionsFile, so nothing was enumerated");
            return file!.Alerts;
        }

        private static List<AlertDefinition> ShippedAlerts() =>
            Parse(File.ReadAllText(Path.Combine(RawPassedScan.RepoRoot().FullName, "Config", "alert-definitions.json")));

        [Fact]
        public void No_shipped_handler_routed_alert_declares_a_learned_baseline()
        {
            var alerts = ShippedAlerts();
            var routed = alerts.Where(AlertEvaluationService.IsRoutedToBuiltInHandler).ToList();
            var baselined = alerts.Count(a => a.CanBaseline);

            // The whole distribution, printed, so a green run says what it measured.
            _out.WriteLine($"shipped alerts={alerts.Count} routed={routed.Count} canBaseline={baselined}");
            foreach (var a in routed)
                _out.WriteLine($"  routed {a.Id} queryMode={a.QueryMode} canBaseline={a.CanBaseline} enabled={a.Enabled}");

            // HAYSTACK before needle: with no routed alert the census would pass on nothing.
            Assert.True(alerts.Count > 0 && routed.Count > 0,
                $"the census enumerated {alerts.Count} shipped alerts and {routed.Count} routed ones, so it measured nothing. "
                + "CHECK: the path to Config/alert-definitions.json and AlertEvaluationService.IsRoutedToBuiltInHandler.");

            var offenders = RoutedAndBaselined(alerts);
            Assert.True(offenders.Count == 0,
                $"{offenders.Count} of {routed.Count} handler-routed shipped alerts declare canBaseline true: "
                + string.Join(", ", offenders.Select(o => o.Id + " (queryMode " + o.QueryMode + ")"))
                + ". A routed alert is evaluated by its handler against fixed thresholds, so its learned baseline is inert "
                + "until someone un-routes it. " + WhatToCheck);
        }

        /// <summary>SPECIMEN CONTROL: the rule flags a synthetic routed and baselined alert, and only that
        /// combination.</summary>
        [Fact]
        public void The_census_rule_flags_a_synthetic_routed_and_baselined_specimen_and_nothing_else()
        {
            var specimen = new AlertDefinition { Id = "specimen_routed_baselined", QueryMode = "response_time_probe", CanBaseline = true, Enabled = true };
            var routedOnly = new AlertDefinition { Id = "specimen_routed_only", QueryMode = "response_time_probe", CanBaseline = false, Enabled = true };
            var standardBaselined = new AlertDefinition { Id = "specimen_standard_baselined", QueryMode = "standard", CanBaseline = true, Enabled = true };
            var noModeBaselined = new AlertDefinition { Id = "specimen_nomode_baselined", QueryMode = null, CanBaseline = true, Enabled = true };

            var flagged = RoutedAndBaselined(new[] { specimen, routedOnly, standardBaselined, noModeBaselined });

            Assert.True(flagged.Count == 1 && flagged[0].Id == specimen.Id,
                "the census rule flagged [" + string.Join(", ", flagged.Select(f => f.Id)) + "], expected only "
                + specimen.Id + ". A rule that cannot flag the specimen is an instrument that cannot fail. "
                + "CHECK: RoutedAndBaselined and AlertEvaluationService.IsRoutedToBuiltInHandler.");
        }

        /// <summary>CONTROL THAT REPRODUCES: the body this product shipped at 5435f4e (build 4088) is flagged,
        /// so the census would have been red on the file this lane changed.</summary>
        [Fact]
        public void The_census_rule_flags_the_body_that_shipped_at_5435f4e()
        {
            var fixture = Path.Combine(RawPassedScan.RepoRoot().FullName, "Tests", "SQLTriage.Tests", "Fixtures",
                "prelane-alert-correctness-l2-alerts-5435f4e.json");
            var old = Parse(File.ReadAllText(fixture));

            var flagged = RoutedAndBaselined(old).Select(a => a.Id).ToList();
            Assert.True(flagged.Count == 1 && flagged[0] == "sql_response_time",
                "the 5435f4e bodies should flag exactly sql_response_time (routed, canBaseline true); flagged ["
                + string.Join(", ", flagged) + "]. CHECK: the fixture prelane-alert-correctness-l2-alerts-5435f4e.json "
                + "and RoutedAndBaselined.");
        }
    }
}
