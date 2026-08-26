/* In the name of God, the Merciful, the Compassionate */

// Ruling 2 (DECISIONS 2026-08-26 04:20): the alert catalogue advertised FIVE severities
// (Info/Low/Medium/High/Critical) while AlertEvaluationService.RuntimeSeverity routes on TWO — an
// alert declared "Critical" floors at Critical, everything else routes Warning. Adrian ruled the
// catalogue must say what it does, so the three advertised-but-dead levels were mapped to the two
// the engine honours: Critical stays Critical, High/Medium/Low/Info become Warning.
//
// The collapse must not change how a single existing alert is routed. These tests prove it two
// ways: (1) the SHIPPED catalogue now advertises only the two live levels; (2) for EVERY alert in
// the pre-collapse catalogue (the base-SHA image, Fixtures/prelane-alert-catalogue-8873806.json),
// the routed severity through the REAL RuntimeSeverity function is byte-for-byte identical before
// and after, at both criticalThresholdCrossed states. A mis-map — a Critical demoted to Warning,
// say — fails (2) with the offending alert id named.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class AlertSeverityCollapseTests
    {
        private static readonly string[] LiveLevels = { "Warning", "Critical" };
        private static readonly string[] DeadLevels = { "Info", "Low", "Medium", "High" };

        private static string ShippedCataloguePath()
        {
            var baseDir = AppContext.BaseDirectory;
            foreach (var folder in new[] { "config", "Config" })
            {
                var candidate = Path.Combine(baseDir, folder, "alert-definitions.json");
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException(
                "The shipped alert catalogue must be beside the test assembly: " + baseDir);
        }

        private static string PreCollapseCataloguePath()
            => Path.Combine(AppContext.BaseDirectory, "Fixtures", "prelane-alert-catalogue-8873806.json");

        /// <summary>id -> declared severity, read straight from the catalogue bytes.</summary>
        private static Dictionary<string, string> SeverityById(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var alert in doc.RootElement.GetProperty("alerts").EnumerateArray())
            {
                var id = alert.GetProperty("id").GetString()!;
                map[id] = alert.GetProperty("severity").GetString()!;
            }
            map.Should().NotBeEmpty("a catalogue with no alerts is a parse or path failure");
            return map;
        }

        /// <summary>
        /// The shipped catalogue advertises ONLY the two levels the engine routes on. This is red on
        /// the pre-collapse catalogue (which carries Info/Low/Medium/High) and green after the map.
        /// </summary>
        [Fact]
        public void The_catalogue_advertises_only_the_two_levels_the_engine_routes_on()
        {
            var offered = SeverityById(ShippedCataloguePath()).Values
                .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();

            offered.Should().OnlyContain(s => LiveLevels.Contains(s, StringComparer.Ordinal),
                "AlertEvaluationService.RuntimeSeverity honours exactly Warning and Critical; a "
                + "catalogue level outside that set implies a granularity the engine does not have. "
                + "Offered: " + string.Join(", ", offered));
        }

        /// <summary>
        /// The fixture is a genuine BEFORE image. If it no longer carries the five advertised levels
        /// it is not the base-SHA catalogue and the invariance proof below would be vacuous.
        /// </summary>
        [Fact]
        public void The_pre_collapse_baseline_really_carried_the_five_advertised_levels()
        {
            var present = SeverityById(PreCollapseCataloguePath()).Values.ToHashSet(StringComparer.Ordinal);
            foreach (var level in DeadLevels.Concat(new[] { "Critical" }))
                present.Should().Contain(level,
                    "the base-SHA fixture must still advertise all five levels or it is not a valid "
                    + "before-image for the routing-invariance test");
        }

        /// <summary>
        /// THE OUTCOME, PROVED FOR EVERY EXISTING ALERT. For each id in the pre-collapse catalogue,
        /// the routed severity through the real RuntimeSeverity function is identical before and
        /// after the collapse, at both criticalThresholdCrossed states. This is the "preserve each
        /// alert's effective behaviour" the ruling mandates — a currently-Critical alert stays
        /// Critical, a High/Medium/Low/Info one becomes Warning, and both route exactly as before.
        /// </summary>
        [Fact]
        public void Collapsing_severity_preserves_every_existing_alerts_routing_outcome()
        {
            var before = SeverityById(PreCollapseCataloguePath());
            var after = SeverityById(ShippedCataloguePath());

            after.Keys.Should().BeEquivalentTo(before.Keys,
                "the collapse only rewrites the severity label; it must not add or drop an alert");

            foreach (var id in before.Keys)
            {
                foreach (var criticalCrossed in new[] { false, true })
                {
                    var routedBefore = AlertEvaluationService.RuntimeSeverity(
                        new AlertDefinition { Severity = before[id] }, criticalCrossed);
                    var routedAfter = AlertEvaluationService.RuntimeSeverity(
                        new AlertDefinition { Severity = after[id] }, criticalCrossed);

                    routedAfter.Should().Be(routedBefore,
                        $"alert '{id}' declared '{before[id]}' routed '{routedBefore}' before the "
                        + $"collapse and now declares '{after[id]}'; its routed severity must be "
                        + $"unchanged (criticalThresholdCrossed={criticalCrossed})");
                }
            }
        }

        /// <summary>
        /// The honest mapping, stated at the alert level: exactly the alerts that were Critical stay
        /// Critical, and every other declared level becomes Warning. Pins the direction of the map so
        /// a future edit that promotes a Warning to Critical (or the reverse) is caught here.
        /// </summary>
        [Fact]
        public void The_critical_set_is_preserved_and_everything_else_became_warning()
        {
            var before = SeverityById(PreCollapseCataloguePath());
            var after = SeverityById(ShippedCataloguePath());

            var criticalBefore = before.Where(kv => kv.Value == "Critical").Select(kv => kv.Key).OrderBy(x => x);
            var criticalAfter = after.Where(kv => kv.Value == "Critical").Select(kv => kv.Key).OrderBy(x => x);
            criticalAfter.Should().BeEquivalentTo(criticalBefore,
                "a currently-Critical alert must stay Critical and no other alert may be promoted");

            foreach (var kv in after.Where(kv => kv.Value != "Critical"))
                kv.Value.Should().Be("Warning",
                    $"alert '{kv.Key}' was '{before[kv.Key]}' and every non-critical level collapses "
                    + "to Warning");
        }
    }
}
