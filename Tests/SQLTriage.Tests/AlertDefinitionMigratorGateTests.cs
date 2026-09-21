/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Delivery for the strings lane's FIX ROUND (2026-08-28): four alerts whose never-fires
    /// arithmetic the lane's own cluster-1 lint could not see, because its shape reader answered
    /// Unknown for every queryMode alert and matched only the literal <c>THEN 1 ELSE 0 END</c>.
    ///
    /// <para>Editing <c>Config/alert-definitions.json</c> alone reaches FRESH INSTALLS ONLY - the
    /// file is triple-protected against overwrite (installer <c>onlyifdoesntexist</c>,
    /// <c>AutoUpdateService.ProtectedConfigFiles</c>, <c>SQLTriageUpdater</c>), which is the gap
    /// strings-r1-11 named. These tests drive the real migrator over the four pre-fix bodies against
    /// the real shipped catalogue, so a registration that is present but does not match cannot
    /// pass.</para>
    ///
    /// <para>The fixture <c>prelane-gate-alerts-84beb1c.json</c> was extracted by
    /// <c>git show 84beb1c:Config/alert-definitions.json</c>, not hand-typed, and the same script
    /// verified each of the four is byte-identical at 84beb1c and at the lane HEAD that preceded this
    /// commit - so these four are untouched by clusters 1 to 5 and the pre-fix body really is the
    /// shipped one.</para>
    /// </summary>
    public class AlertDefinitionMigratorGateTests
    {
        private static readonly string[] Ids =
        {
            "instance_unreachable", "machine_unreachable", "deadlock", "failed_login_xevent_session"
        };

        public static IEnumerable<object[]> IdRows() => Ids.Select(i => new object[] { i });

        private static string RepoRoot() => RawPassedScan.RepoRoot().FullName;

        private static string ShippedJson() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "Config", "alert-definitions.json"));

        private static string PreFixJson() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "Tests", "SQLTriage.Tests", "Fixtures",
                "prelane-gate-alerts-84beb1c.json"));

        private static JsonObject AlertOf(string json, string id) =>
            JsonNode.Parse(json)!["alerts"]!.AsArray()
                .Select(n => n!.AsObject())
                .Single(o => string.Equals(o["id"]!.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase));

        [Theory]
        [MemberData(nameof(IdRows))]
        public void The_prefix_body_hashes_to_a_registered_superseded_signature(string id)
        {
            Assert.True(AlertDefinitionMigrator.SupersededDefinitionSignatures.TryGetValue(id, out var known),
                id + " has no superseded signature registered, so its fix reaches fresh installs only");

            var signature = AlertDefinitionMigrator.DefinitionSignature(AlertOf(PreFixJson(), id));
            Assert.True(known!.Contains(signature),
                id + " ships a fix but its pre-fix body hashes to " + signature + ", which is not registered");
        }

        /// <summary>
        /// The two connectivity alerts hash to the SAME signature, and that is a fact worth pinning
        /// rather than a coincidence to trip over later: at 84beb1c their measurement facts were
        /// byte-identical (the same dead <c>SELECT 1 AS value</c>, unit event, greater_than, warning
        /// 1, no critical). A future reader who assumes signatures are unique per alert would draw
        /// the wrong conclusion from one of them.
        /// </summary>
        [Fact]
        public void The_two_connectivity_alerts_shipped_identical_measurement_facts()
        {
            Assert.Equal(
                AlertDefinitionMigrator.DefinitionSignature(AlertOf(PreFixJson(), "instance_unreachable")),
                AlertDefinitionMigrator.DefinitionSignature(AlertOf(PreFixJson(), "machine_unreachable")));
        }

        /// <summary>
        /// The repair is what makes a silent alert speak on an EXISTING install, asserted through the
        /// real predicate rather than by reading a threshold.
        /// </summary>
        [Fact]
        public void After_the_repair_the_condition_fires_on_an_upgraded_install()
        {
            var beforeSilent = Ids.Where(id => !Fires(AlertOf(PreFixJson(), id))).ToList();
            Assert.Equal(4, beforeSilent.Count);   // all four were unreachable at 1

            Assert.True(AlertDefinitionMigrator.TryRepairSupersededDefinitions(
                PreFixJson(), ShippedJson(), out var merged, out var repaired));

            Assert.Equal(4, repaired.Count);

            var stillSilent = Ids.Where(id => !Fires(AlertOf(merged!, id))).ToList();
            Assert.Empty(stillSilent);
        }

        /// <summary>
        /// machine_unreachable's re-base carries PROSE, not just a threshold: the alert promised a
        /// host check and runs the same SQL connection test as its sibling. An install that keeps the
        /// old sentence keeps a false claim, so the description has to travel with the fix.
        /// </summary>
        [Fact]
        public void The_host_check_prose_is_corrected_on_an_upgraded_install_too()
        {
            Assert.True(AlertDefinitionMigrator.TryRepairSupersededDefinitions(
                PreFixJson(), ShippedJson(), out var merged, out _));

            var before = AlertOf(PreFixJson(), "machine_unreachable")["description"]!.GetValue<string>();
            var after = AlertOf(merged!, "machine_unreachable")["description"]!.GetValue<string>();

            Assert.Equal("The host machine does not respond to monitoring requests", before);
            Assert.Contains("same connection test as Instance Unreachable", after, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>An operator who tuned a threshold keeps their edit: the re-base is gated on an
        /// EXACT match of a body this product itself shipped, never a blanket overwrite.</summary>
        [Fact]
        public void An_operator_edited_alert_is_left_exactly_as_they_left_it()
        {
            var root = JsonNode.Parse(PreFixJson())!;
            var edited = root["alerts"]!.AsArray().Select(n => n!.AsObject())
                .Single(o => o["id"]!.GetValue<string>() == "deadlock");
            edited["thresholds"]!["warning"] = 3;   // an operator who decided three is their bar

            Assert.True(AlertDefinitionMigrator.TryRepairSupersededDefinitions(
                root.ToJsonString(), ShippedJson(), out var merged, out var repaired));

            Assert.DoesNotContain("deadlock", repaired, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(3, AlertOf(merged!, "deadlock")["thresholds"]!["warning"]!.GetValue<int>());
        }

        /// <summary>Every registered alert really did change: a signature for a body byte-identical
        /// to what ships would be a no-op dressed as a fix.</summary>
        [Theory]
        [MemberData(nameof(IdRows))]
        public void The_shipped_body_differs_from_the_prefix_body(string id)
        {
            var before = AlertOf(PreFixJson(), id);
            var after = AlertOf(ShippedJson(), id);

            var differing = AlertDefinitionMigrator.CatalogueFactProperties
                .Where(p =>
                {
                    var a = before.TryGetPropertyValue(p, out var av) && av is not null ? av.ToJsonString() : null;
                    var b = after.TryGetPropertyValue(p, out var bv) && bv is not null ? bv.ToJsonString() : null;
                    return a != b;
                })
                .ToList();

            Assert.True(differing.Count > 0, id + " registers a superseded signature but nothing about it changed");
        }

        private static bool Fires(JsonObject alert) => Breaches(alert, 1.0);

        private static bool Breaches(JsonObject alert, double value)
        {
            var op = alert["operator"]!.GetValue<string>();
            var th = alert["thresholds"]!.AsObject();
            double? warn = th.TryGetPropertyValue("warning", out var w) && w is not null ? w.GetValue<double>() : null;
            double? crit = th.TryGetPropertyValue("critical", out var c) && c is not null ? c.GetValue<double>() : null;
            return SQLTriage.Data.Services.AlertEvaluationService.IsThresholdBreached(value, warn, op)
                || SQLTriage.Data.Services.AlertEvaluationService.IsThresholdBreached(value, crit, op);
        }
    }
}
