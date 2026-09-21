/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using SQLTriage.Data;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Delivery for lane alert-correctness 2 (2026-09-19) to EXISTING installs. Editing
    /// <c>Config/alert-definitions.json</c> reaches fresh installs only, because an upgrade keeps the
    /// operator's copy; these tests drive the real <see cref="AlertDefinitionMigrator"/> over the bodies
    /// this product shipped from build 4082 (f1c821e) through 4088 (5435f4e) against the real shipped
    /// catalogue.
    ///
    /// <para><b>A2</b> (ruled 2026-09-18, "turn it off"): every install holding a body this product shipped
    /// with <c>canBaseline: true</c> for <c>sql_response_time</c> ends at <c>false</c>, and keeps its
    /// 2-minute hold, unless the operator changed <c>canBaseline</c> or a hashed field first.</para>
    ///
    /// <para>The fixture <c>prelane-alert-correctness-l2-alerts-5435f4e.json</c> was extracted by script from
    /// <c>git show 5435f4ea3f2afc73c52f9c65010625f7946d3789:Config/alert-definitions.json</c> and compared back
    /// to that blob for equality. Every signature here is COMPUTED by the real
    /// <see cref="AlertDefinitionMigrator.DefinitionSignature"/>; no hex is pasted into a test.</para>
    /// </summary>
    public class AlertDefinitionMigratorLane2Tests
    {
        private readonly ITestOutputHelper _out;

        public AlertDefinitionMigratorLane2Tests(ITestOutputHelper output) => _out = output;

        private const string CheckTheFixture =
            "CHECK: that alert's body in Config/alert-definitions.json against its 4082 body in "
            + "Tests/SQLTriage.Tests/Fixtures/prelane-alert-correctness-l2-alerts-5435f4e.json, and its entries in "
            + "AlertDefinitionMigrator.SupersededDefinitionSignatures and SupersededDefinitionCarries.";

        private static string RepoRoot() => RawPassedScan.RepoRoot().FullName;

        private static string ShippedJson() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "Config", "alert-definitions.json"));

        private static string Fixture4082Json() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "Tests", "SQLTriage.Tests", "Fixtures",
                "prelane-alert-correctness-l2-alerts-5435f4e.json"));

        private static string Fixture4078Json() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "Tests", "SQLTriage.Tests", "Fixtures",
                "prelane-q14-alerts-11e6f88.json"));

        private static JsonObject AlertOf(string? json, string id, string where)
        {
            Assert.True(json != null,
                $"{id}: the {where} document is null, so there is no alert to read. A null merged document means "
                + "the migrator re-based nothing. " + CheckTheFixture);
            var match = JsonNode.Parse(json!)!["alerts"]!.AsArray()
                .Select(n => n!.AsObject())
                .SingleOrDefault(o => string.Equals(o["id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase));
            Assert.True(match != null, $"{id}: no alert with this id in the {where} document. " + CheckTheFixture);
            return match!;
        }

        private static string? Token(JsonObject alert, string property) =>
            alert.TryGetPropertyValue(property, out var value) ? (value?.ToJsonString() ?? "null") : null;

        private static void AssertToken(JsonObject alert, string id, string property, string? expected, string where) =>
            Assert.True(Token(alert, property) == expected,
                $"{id}.{property} in the {where} document is {Token(alert, property) ?? "(absent)"}, expected {expected ?? "(absent)"}. " + CheckTheFixture);

        /// <summary>An install for ONE alert: the shipped catalogue with only <paramref name="id"/> put back
        /// exactly as it is in <paramref name="fixtureJson"/>, then <paramref name="tweak"/> applied.</summary>
        private static JsonNode InstallOf(string fixtureJson, string id, Action<JsonObject>? tweak = null)
        {
            var root = JsonNode.Parse(ShippedJson())!;
            var alerts = root["alerts"]!.AsArray();
            var index = alerts.Select((n, i) => (n, i))
                .Where(p => string.Equals(p.n!["id"]!.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.i).DefaultIfEmpty(-1).Single();
            Assert.True(index >= 0, $"{id}: the shipped catalogue has no alert with this id. " + CheckTheFixture);
            var old = (JsonObject)AlertOf(fixtureJson, id, "fixture").DeepClone();
            tweak?.Invoke(old);
            alerts[index] = old;
            return root;
        }

        private static string Repaired(JsonNode installed, string id, out IReadOnlyList<string> repaired)
        {
            var ok = AlertDefinitionMigrator.TryRepairSupersededDefinitions(
                installed.ToJsonString(), ShippedJson(), out var merged, out repaired);
            Assert.True(ok && merged != null,
                $"{id}: the install was NOT re-based (TryRepairSupersededDefinitions returned {ok}). " + CheckTheFixture);
            Assert.True(repaired.Count == 1 && string.Equals(repaired[0], id, StringComparison.OrdinalIgnoreCase),
                $"{id}: expected only this alert re-based, got [{string.Join(", ", repaired)}]. " + CheckTheFixture);
            return merged!;
        }

        private static void AssertNotRepaired(JsonNode installed, string id, string why)
        {
            var ok = AlertDefinitionMigrator.TryRepairSupersededDefinitions(
                installed.ToJsonString(), ShippedJson(), out var merged, out var repaired);
            Assert.False(ok, $"{id}: {why}, yet the migrator re-based [{string.Join(", ", repaired)}]. " + CheckTheFixture);
            Assert.Null(merged);
            Assert.Empty(repaired);
        }

        private static void AssertSecondRunIsANoOp(string merged, string id)
        {
            Assert.False(AlertDefinitionMigrator.TryRepairSupersededDefinitions(merged, ShippedJson(), out var again, out var repairedAgain),
                $"{id}: a re-based install was re-based again, so every start would rewrite the file. " + CheckTheFixture);
            Assert.Null(again);
            Assert.Empty(repairedAgain);
        }

        // ── A2: sql_response_time ──────────────────────────────────────────────────────────────────

        [Fact]
        public void The_4082_sql_response_time_body_hashes_to_a_registered_signature()
        {
            var body = AlertOf(Fixture4082Json(), "sql_response_time", "4082 fixture");
            var signature = AlertDefinitionMigrator.DefinitionSignature(body);
            _out.WriteLine("DefinitionSignature(4082 fixture sql_response_time) = " + signature);
            _out.WriteLine("DefinitionSignature(shipped sql_response_time)      = "
                + AlertDefinitionMigrator.DefinitionSignature(AlertOf(ShippedJson(), "sql_response_time", "shipped")));
            AssertToken(body, "sql_response_time", "canBaseline", "true", "4082 fixture");
            Assert.True(AlertDefinitionMigrator.SupersededDefinitionSignatures["sql_response_time"].Contains(signature),
                "sql_response_time as shipped at 4082 hashes to " + signature + ", which is not registered, so the canBaseline "
                + "carry can never reach an install of 4082 through 4088. " + CheckTheFixture);
        }

        [Fact]
        public void The_shipped_sql_response_time_declares_no_learned_baseline_and_keeps_its_hold()
        {
            var shipped = AlertOf(ShippedJson(), "sql_response_time", "shipped");
            AssertToken(shipped, "sql_response_time", "canBaseline", "false", "shipped");
            AssertToken(shipped, "sql_response_time", "holdSeconds", "120", "shipped");
            AssertToken(shipped, "sql_response_time", "queryMode", "\"response_time_probe\"", "shipped");
            var carries = AlertDefinitionMigrator.SupersededDefinitionCarries["sql_response_time"];
            Assert.Contains(carries, c => c.Property == "canBaseline" && c.OldValueJson == "true" && c.NewValueJson == "false");
            Assert.Contains(carries, c => c.Property == "holdSeconds" && c.OldValueJson == null && c.NewValueJson == "120");
        }

        [Fact]
        public void A_4082_install_ends_with_no_learned_baseline_keeps_its_hold_and_nothing_else_moves()
        {
            var installed = InstallOf(Fixture4082Json(), "sql_response_time", a =>
            {
                // Operator SETTINGS must survive the re-base.
                a["enabled"] = false;
                a["sendEmail"] = true;
            });

            var merged = Repaired(installed, "sql_response_time", out _);
            var after = AlertOf(merged, "sql_response_time", "merged");
            AssertToken(after, "sql_response_time", "canBaseline", "false", "merged");
            AssertToken(after, "sql_response_time", "holdSeconds", "120", "merged");
            AssertToken(after, "sql_response_time", "enabled", "false", "merged");
            AssertToken(after, "sql_response_time", "sendEmail", "true", "merged");

            var before = installed["alerts"]!.AsArray().Select(n => n!.AsObject())
                .Where(o => o["id"]!.GetValue<string>() != "sql_response_time")
                .ToDictionary(o => o["id"]!.GetValue<string>(), o => o.ToJsonString());
            var afterOthers = JsonNode.Parse(merged)!["alerts"]!.AsArray().Select(n => n!.AsObject())
                .Where(o => o["id"]!.GetValue<string>() != "sql_response_time")
                .ToDictionary(o => o["id"]!.GetValue<string>(), o => o.ToJsonString());
            Assert.True(before.Count > 0 && before.Count == afterOthers.Count,
                $"expected the same alerts besides sql_response_time before and after the merge, found {before.Count} and {afterOthers.Count}");
            foreach (var kv in before)
                Assert.True(afterOthers[kv.Key] == kv.Value, kv.Key + " changed, but only sql_response_time may");

            AssertSecondRunIsANoOp(merged, "sql_response_time");
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_4078_install_ends_with_no_learned_baseline_and_the_2_minute_hold(bool withOperator)
        {
            var installed = InstallOf(Fixture4078Json(), "sql_response_time", a =>
            {
                if (!withOperator) Assert.True(a.Remove("operator"), "the 4078 fixture carries an operator to remove");
            });

            var merged = Repaired(installed, "sql_response_time", out _);
            var after = AlertOf(merged, "sql_response_time", "merged");
            AssertToken(after, "sql_response_time", "canBaseline", "false", "merged");
            AssertToken(after, "sql_response_time", "holdSeconds", "120", "merged");
            AssertToken(after, "sql_response_time", "queryMode", "\"response_time_probe\"", "merged");
            AssertSecondRunIsANoOp(merged, "sql_response_time");
        }

        /// <summary>An operator who turned the learned baseline off, or removed the field, before the upgrade:
        /// the body already matches what ships, so there is nothing to re-base and nothing is written.</summary>
        [Theory]
        [InlineData("false")]
        [InlineData(null)]
        public void A_4082_install_whose_operator_already_changed_canBaseline_is_untouched(string? theirs)
        {
            var installed = InstallOf(Fixture4082Json(), "sql_response_time", a =>
            {
                if (theirs == null) a.Remove("canBaseline");
                else a["canBaseline"] = JsonNode.Parse(theirs);
            });
            AssertNotRepaired(installed, "sql_response_time", "the operator already changed canBaseline to " + (theirs ?? "(absent)"));
            AssertToken(AlertOf(installed.ToJsonString(), "sql_response_time", "installed"), "sql_response_time", "canBaseline", theirs, "installed");
        }

        [Fact]
        public void A_4082_install_whose_operator_edited_a_hashed_field_keeps_everything()
        {
            var installed = InstallOf(Fixture4082Json(), "sql_response_time", a => a["thresholds"]!["critical"] = 5000);
            AssertNotRepaired(installed, "sql_response_time", "the operator edited the critical threshold");
            AssertToken(AlertOf(installed.ToJsonString(), "sql_response_time", "installed"), "sql_response_time", "canBaseline", "true", "installed");
        }

        // ── C1: integrity_check_overdue ────────────────────────────────────────────────────────────

        [Fact]
        public void The_4082_integrity_check_overdue_body_hashes_to_a_registered_signature_and_the_shipped_one_does_not()
        {
            var body = AlertOf(Fixture4082Json(), "integrity_check_overdue", "4082 fixture");
            var shipped = AlertOf(ShippedJson(), "integrity_check_overdue", "shipped");
            var old = AlertDefinitionMigrator.DefinitionSignature(body);
            var now = AlertDefinitionMigrator.DefinitionSignature(shipped);
            _out.WriteLine("DefinitionSignature(4082 fixture integrity_check_overdue) = " + old);
            _out.WriteLine("DefinitionSignature(shipped integrity_check_overdue)      = " + now);
            var registered = AlertDefinitionMigrator.SupersededDefinitionSignatures["integrity_check_overdue"];
            Assert.True(registered.Contains(old),
                "integrity_check_overdue as shipped at 4082 hashes to " + old + ", which is not registered, so the THROW never reaches "
                + "an install of 4082 through 4088. " + CheckTheFixture);
            Assert.False(registered.Contains(now),
                "the shipped integrity_check_overdue body's own signature " + now + " is registered as superseded. " + CheckTheFixture);
            Assert.NotEqual(old, now);
        }

        [Fact]
        public void A_4082_integrity_install_is_rebased_onto_the_THROW_and_nothing_else_moves()
        {
            var installed = InstallOf(Fixture4082Json(), "integrity_check_overdue", a => a["enabled"] = false);
            var merged = Repaired(installed, "integrity_check_overdue", out _);
            var after = AlertOf(merged, "integrity_check_overdue", "merged");
            var shipped = AlertOf(ShippedJson(), "integrity_check_overdue", "shipped");
            AssertToken(after, "integrity_check_overdue", "query", Token(shipped, "query"), "merged");
            AssertToken(after, "integrity_check_overdue", "description", Token(shipped, "description"), "merged");
            AssertToken(after, "integrity_check_overdue", "enabled", "false", "merged");
            Assert.Contains("THROW 50001", Token(after, "query"));

            var before = installed["alerts"]!.AsArray().Select(n => n!.AsObject())
                .Where(o => o["id"]!.GetValue<string>() != "integrity_check_overdue")
                .ToDictionary(o => o["id"]!.GetValue<string>(), o => o.ToJsonString());
            var afterOthers = JsonNode.Parse(merged)!["alerts"]!.AsArray().Select(n => n!.AsObject())
                .Where(o => o["id"]!.GetValue<string>() != "integrity_check_overdue")
                .ToDictionary(o => o["id"]!.GetValue<string>(), o => o.ToJsonString());
            Assert.True(before.Count > 0 && before.Count == afterOthers.Count, "the alert count changed across the merge");
            foreach (var kv in before)
                Assert.True(afterOthers[kv.Key] == kv.Value, kv.Key + " changed, but only integrity_check_overdue may");

            AssertSecondRunIsANoOp(merged, "integrity_check_overdue");
        }

        [Fact]
        public void A_4082_integrity_install_whose_operator_edited_a_hashed_field_keeps_everything()
        {
            var installed = InstallOf(Fixture4082Json(), "integrity_check_overdue", a => a["thresholds"]!["warning"] = 200);
            AssertNotRepaired(installed, "integrity_check_overdue", "the operator edited the warning threshold");
        }

        /// <summary>An install of BOTH lane-2 alerts as shipped at 4082, the shape of the live service's file:
        /// both re-based in one pass, and a second pass writes nothing.</summary>
        [Fact]
        public void A_4082_install_of_both_alerts_is_rebased_in_one_pass()
        {
            var root = InstallOf(Fixture4082Json(), "sql_response_time");
            var alerts = root["alerts"]!.AsArray();
            var index = alerts.Select((n, i) => (n, i)).Single(p => p.n!["id"]!.GetValue<string>() == "integrity_check_overdue").i;
            alerts[index] = AlertOf(Fixture4082Json(), "integrity_check_overdue", "4082 fixture").DeepClone();

            var ok = AlertDefinitionMigrator.TryRepairSupersededDefinitions(root.ToJsonString(), ShippedJson(), out var merged, out var repaired);
            Assert.True(ok && merged != null, "a 4082 install of both lane-2 alerts was not re-based. " + CheckTheFixture);
            Assert.Equal(new[] { "integrity_check_overdue", "sql_response_time" }, repaired.OrderBy(x => x, StringComparer.Ordinal).ToArray());
            AssertToken(AlertOf(merged, "sql_response_time", "merged"), "sql_response_time", "canBaseline", "false", "merged");
            Assert.Contains("THROW 50001", Token(AlertOf(merged, "integrity_check_overdue", "merged"), "query"));
            AssertSecondRunIsANoOp(merged!, "both lane-2 alerts");
        }
    }
}
