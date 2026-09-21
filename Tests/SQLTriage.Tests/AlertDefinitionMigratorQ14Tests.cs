/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Delivery for lane Q14 alerts-that-cannot-fire (2026-09-18). INVARIANT: every ENABLED shipped
    /// alert must be able to produce a value that can cross its own threshold. Three could not:
    /// <c>connection_count</c> divided by <c>SERVERPROPERTY('MaxConnections')</c>, which is not a
    /// property, so its value was NULL on every server; <c>sql_response_time</c> returned a constant
    /// <c>SELECT 1</c> as its FIRST result set, which is the only one the evaluator reads; and
    /// <c>integrity_check_overdue</c> read only a database property that is NULL on SQL Server
    /// 14.0.2130.4, and answered 0 there.
    ///
    /// <para>Editing <c>Config/alert-definitions.json</c> reaches fresh installs only: the file is
    /// protected against overwrite on upgrade. These tests drive the real migrator over the pre-lane
    /// bodies against the real shipped catalogue, so a registration that is present but does not match
    /// cannot pass.</para>
    ///
    /// <para><b>EACH ALERT'S CASES STAND ALONE</b> (fix round 2, from gate 2's mutation mut-A). An install
    /// is built per alert: the shipped catalogue with ONLY the alert under test put back as it shipped.
    /// So a change to one alert's shipped body fails that alert's cases, by name, and none of the
    /// others. Every lookup asserts what it found and names the alert and what to check, instead of
    /// failing on a bare NullReferenceException.</para>
    ///
    /// <para>The fixture <c>prelane-q14-alerts-11e6f88.json</c> was extracted by script from
    /// <c>git show 11e6f88ce3773dd1819c1d4c9c01c0ee084aba6b:Config/alert-definitions.json</c> and
    /// compared back to that blob for equality, not hand-typed. Its
    /// <c>olderIntegrityCheckOverdueBodies</c> hold the two earlier bodies that alert shipped with,
    /// captured the same way from a0c9566 and 6f15efe.</para>
    /// </summary>
    public class AlertDefinitionMigratorQ14Tests
    {
        private static readonly string[] Ids = { "sql_response_time", "connection_count", "integrity_check_overdue" };

        public static IEnumerable<object[]> IdRows() => Ids.Select(i => new object[] { i });

        private const string CheckTheFixture =
            "CHECK: that alert's body in Config/alert-definitions.json against its pre-lane body in "
            + "Tests/SQLTriage.Tests/Fixtures/prelane-q14-alerts-11e6f88.json, and its entries in "
            + "AlertDefinitionMigrator.SupersededDefinitionSignatures and SupersededDefinitionCarries.";

        private static string RepoRoot() => RawPassedScan.RepoRoot().FullName;

        private static string ShippedJson() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "Config", "alert-definitions.json"));

        private static string PreLaneJson() =>
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

        /// <summary>A property's raw JSON token must be exactly <paramref name="expected"/> (null = absent); the
        /// failure names the alert, the property, both values and what to check.</summary>
        private static void AssertToken(JsonObject alert, string id, string property, string? expected, string where) =>
            Assert.True(Token(alert, property) == expected,
                $"{id}.{property} in the {where} document is {Token(alert, property) ?? "(absent)"}, expected {expected ?? "(absent)"}. " + CheckTheFixture);

        private static void AssertHas(string? text, string needle, string id, string property, bool present = true) =>
            Assert.True(text != null && text.Contains(needle, StringComparison.Ordinal) == present,
                $"{id}.{property} {(present ? "does not contain" : "still contains")} '{needle}'. " + CheckTheFixture);

        private static void AssertRepairedSet(IReadOnlyList<string> repaired, IEnumerable<string> expected)
        {
            var want = expected.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            var got = repaired.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            Assert.True(want.SequenceEqual(got, StringComparer.OrdinalIgnoreCase),
                $"re-based [{string.Join(", ", got)}], expected [{string.Join(", ", want)}]. Not re-based: [{string.Join(", ", want.Except(got, StringComparer.OrdinalIgnoreCase))}]. " + CheckTheFixture);
        }

        /// <summary>
        /// An install of build 4078 for ONE alert: the shipped catalogue with only <paramref name="id"/>
        /// put back exactly as it shipped at 11e6f88. Every other alert is the shipped bytes, so nothing
        /// but this alert can be re-based.
        /// </summary>
        private static JsonNode InstallOf4078(string id, Action<JsonObject>? tweak = null) =>
            InstallOf4078(new[] { id }, tweak);

        private static JsonNode InstallOf4078(IEnumerable<string> ids, Action<JsonObject>? tweak = null)
        {
            var root = JsonNode.Parse(ShippedJson())!;
            var alerts = root["alerts"]!.AsArray();
            foreach (var id in ids)
            {
                var index = alerts.Select((n, i) => (n, i))
                    .Where(p => string.Equals(p.n!["id"]!.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.i).DefaultIfEmpty(-1).Single();
                Assert.True(index >= 0, $"{id}: the shipped catalogue has no alert with this id. " + CheckTheFixture);
                var old = (JsonObject)AlertOf(PreLaneJson(), id, "pre-lane fixture").DeepClone();
                tweak?.Invoke(old);
                alerts[index] = old;
            }
            return root;
        }

        private static string Repaired(JsonNode installed, string id, out IReadOnlyList<string> repaired)
        {
            var ok = AlertDefinitionMigrator.TryRepairSupersededDefinitions(
                installed.ToJsonString(), ShippedJson(), out var merged, out repaired);
            Assert.True(ok && merged != null,
                $"{id}: an unedited 4078 install of this alert was NOT re-based (TryRepairSupersededDefinitions returned {ok}). " + CheckTheFixture);
            return merged!;
        }

        // ── registrations ─────────────────────────────────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(IdRows))]
        public void The_prelane_body_hashes_to_a_registered_superseded_signature(string id)
        {
            Assert.True(AlertDefinitionMigrator.SupersededDefinitionSignatures.TryGetValue(id, out var known),
                id + " has no superseded signature registered, so its fix reaches fresh installs only");

            var signature = AlertDefinitionMigrator.DefinitionSignature(AlertOf(PreLaneJson(), id, "pre-lane fixture"));
            Assert.True(known!.Contains(signature),
                id + " ships a fix but its pre-lane body hashes to " + signature + ", which is not registered");
        }

        /// <summary>v0.80.0 shipped the same bodies with no <c>operator</c> property. An install that was
        /// never re-saved by the app still carries that shape, and it measures exactly the same thing
        /// (the model reads a missing operator as greater_than).</summary>
        [Theory]
        [InlineData("sql_response_time")]
        [InlineData("connection_count")]
        public void The_v080_body_without_an_operator_is_registered_too(string id)
        {
            var old = (JsonObject)AlertOf(PreLaneJson(), id, "pre-lane fixture").DeepClone();
            Assert.True(old.Remove("operator"), id + ": the fixture carries an operator to remove. " + CheckTheFixture);

            var signature = AlertDefinitionMigrator.DefinitionSignature(old);
            Assert.True(AlertDefinitionMigrator.SupersededDefinitionSignatures[id].Contains(signature),
                id + ": its v0.80.0 shape (no operator) hashes to " + signature + ", which is not registered. " + CheckTheFixture);
        }

        /// <summary>integrity_check_overdue changed its QUERY between releases, not just its operator, so
        /// its two older bodies are captured whole in the fixture and each must hash to a registration.</summary>
        [Fact]
        public void Every_older_integrity_check_overdue_body_is_registered()
        {
            var older = JsonNode.Parse(PreLaneJson())!["olderIntegrityCheckOverdueBodies"]?.AsObject();
            Assert.True(older != null && older.Count == 2,
                "the fixture must carry the two older integrity_check_overdue bodies (a0c9566 and 6f15efe). " + CheckTheFixture);

            var registered = AlertDefinitionMigrator.SupersededDefinitionSignatures["integrity_check_overdue"];
            foreach (var (label, body) in older!)
            {
                var signature = AlertDefinitionMigrator.DefinitionSignature(body!.AsObject());
                Assert.True(registered.Contains(signature),
                    $"integrity_check_overdue as shipped at {label} hashes to {signature}, which is not registered. " + CheckTheFixture);
            }

            // Lane alert-correctness 2 (C1): the body shipped 4082 through 4088 is the fourth, read from that
            // lane's own fixture; this class's pre-lane fixture is unchanged.
            var body4082 = AlertOf(Lane2FixtureJson(), "integrity_check_overdue", "lane-2 fixture (4082)");
            var signature4082 = AlertDefinitionMigrator.DefinitionSignature(body4082);
            Assert.True(registered.Contains(signature4082),
                $"integrity_check_overdue as shipped at 4082 hashes to {signature4082}, which is not registered. " + CheckTheFixture);
            Assert.Equal(4, registered.Count);
        }

        private static string Lane2FixtureJson() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "Tests", "SQLTriage.Tests", "Fixtures",
                "prelane-alert-correctness-l2-alerts-5435f4e.json"));

        // ── the re-base ───────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void An_unedited_install_is_rebased_and_nothing_else_moves()
        {
            var installed = InstallOf4078(Ids, a =>
            {
                // Operator SETTINGS are not measurement facts: they must survive the re-base.
                if (a["id"]!.GetValue<string>() == "sql_response_time")
                {
                    a["enabled"] = false;
                    a["sendEmail"] = true;
                }
            });

            var merged = Repaired(installed, "all three", out var repaired);
            AssertRepairedSet(repaired, Ids);

            foreach (var id in Ids)
            {
                var after = AlertOf(merged, id, "merged");
                var shipped = AlertOf(ShippedJson(), id, "shipped");
                foreach (var property in AlertDefinitionMigrator.CatalogueFactProperties)
                {
                    var a = Token(after, property);
                    var s = Token(shipped, property);
                    Assert.True(s == a, id + "." + property + " was not re-based: installed " + a + ", shipped " + s);
                }
            }

            var srt = AlertOf(merged, "sql_response_time", "merged");
            AssertToken(srt, "sql_response_time", "queryMode", "\"response_time_probe\"", "merged");
            AssertToken(srt, "sql_response_time", "query", "\"\"", "merged");
            AssertToken(srt, "sql_response_time", "enabled", "false", "merged");
            AssertToken(srt, "sql_response_time", "sendEmail", "true", "merged");
            AssertHas(Token(AlertOf(merged, "connection_count", "merged"), "query"), "MaxConnections", "connection_count", "query", present: false);
            AssertHas(Token(AlertOf(merged, "integrity_check_overdue", "merged"), "query"), "DBCC DBINFO", "integrity_check_overdue", "query");

            // Everything else: byte-for-byte the same JSON after the merge as before it.
            var before = installed["alerts"]!.AsArray().Select(n => n!.AsObject())
                .Where(o => !Ids.Contains(o["id"]!.GetValue<string>()))
                .ToDictionary(o => o["id"]!.GetValue<string>(), o => o.ToJsonString());
            var afterOthers = JsonNode.Parse(merged)!["alerts"]!.AsArray().Select(n => n!.AsObject())
                .Where(o => !Ids.Contains(o["id"]!.GetValue<string>()))
                .ToDictionary(o => o["id"]!.GetValue<string>(), o => o.ToJsonString());
            Assert.True(before.Count == 77 && afterOthers.Count == 77,
                $"expected 77 alerts besides the three Q14 alerts before and after the merge, found {before.Count} and {afterOthers.Count}. " + CheckTheFixture);
            foreach (var kv in before)
                Assert.True(afterOthers[kv.Key] == kv.Value, kv.Key + " changed, but only the three Q14 alerts may");
        }

        [Theory]
        [MemberData(nameof(IdRows))]
        public void An_unedited_install_of_one_alert_is_rebased_alone(string id)
        {
            var merged = Repaired(InstallOf4078(id), id, out var repaired);
            Assert.True(repaired.Count == 1 && string.Equals(repaired[0], id, StringComparison.OrdinalIgnoreCase),
                $"{id}: expected only this alert re-based, got [{string.Join(", ", repaired)}]. " + CheckTheFixture);
            Assert.True(
                AlertDefinitionMigrator.DefinitionSignature(AlertOf(PreLaneJson(), id, "pre-lane fixture"))
                != AlertDefinitionMigrator.DefinitionSignature(AlertOf(merged, id, "merged")),
                $"{id}: the re-based body still hashes to its pre-lane signature, so nothing that measures changed. " + CheckTheFixture);
        }

        [Theory]
        [InlineData("sql_response_time")]
        [InlineData("connection_count")]
        public void A_v080_install_without_an_operator_is_rebased_too(string id)
        {
            var merged = Repaired(InstallOf4078(id, a => a.Remove("operator")), id, out var repaired);

            AssertRepairedSet(repaired, new[] { id });
            var after = AlertOf(merged, id, "merged");
            AssertToken(after, id, "operator", Token(AlertOf(ShippedJson(), id, "shipped"), "operator"), "merged");
            AssertToken(after, id, "query", Token(AlertOf(ShippedJson(), id, "shipped"), "query"), "merged");
        }

        [Fact]
        public void The_older_integrity_check_overdue_bodies_are_rebased()
        {
            var older = JsonNode.Parse(PreLaneJson())!["olderIntegrityCheckOverdueBodies"]!.AsObject()
                .Select(kv => (kv.Key, kv.Value))
                // Lane alert-correctness 2 (C1): the 4082 body re-bases straight onto the new shipped body too.
                .Append(("4082 f1c821e through 4088 5435f4e (lane-2 fixture)",
                    (JsonNode?)AlertOf(Lane2FixtureJson(), "integrity_check_overdue", "lane-2 fixture (4082)")))
                .ToList();
            Assert.Equal(3, older.Count);
            foreach (var (label, body) in older)
            {
                var merged = Repaired(InstallOf4078("integrity_check_overdue", a =>
                {
                    foreach (var property in AlertDefinitionMigrator.CatalogueFactProperties)
                    {
                        a.Remove(property);
                        if (body!.AsObject().TryGetPropertyValue(property, out var v) && v != null) a[property] = v.DeepClone();
                    }
                }), "integrity_check_overdue as shipped at " + label, out _);

                AssertToken(AlertOf(merged, "integrity_check_overdue", "merged"), "integrity_check_overdue", "query",
                    Token(AlertOf(ShippedJson(), "integrity_check_overdue", "shipped"), "query"), "merged (from the body shipped at " + label + ")");
            }
        }

        [Theory]
        [MemberData(nameof(IdRows))]
        public void An_operator_who_changed_a_threshold_keeps_their_alert(string id)
        {
            var installed = InstallOf4078(id, a => a["thresholds"]!["warning"] = 70);

            var ok = AlertDefinitionMigrator.TryRepairSupersededDefinitions(
                installed.ToJsonString(), ShippedJson(), out var merged, out var repaired);

            // Only this alert differs from what ships, and it matches no signature: nothing to write.
            Assert.False(ok, $"{id}: an operator-edited threshold was re-based, or another alert was. repaired=[{string.Join(", ", repaired)}]. " + CheckTheFixture);
            Assert.Null(merged);
            Assert.Empty(repaired);
        }

        [Theory]
        [MemberData(nameof(IdRows))]
        public void An_operator_who_changed_the_query_keeps_their_alert(string id)
        {
            const string theirs = "SELECT 42 AS value -- an operator's own measurement";
            var installed = InstallOf4078(id, a => a["query"] = theirs);

            var ok = AlertDefinitionMigrator.TryRepairSupersededDefinitions(
                installed.ToJsonString(), ShippedJson(), out var merged, out var repaired);

            Assert.False(ok, $"{id}: an operator's own query was re-based. repaired=[{string.Join(", ", repaired)}]. " + CheckTheFixture);
            Assert.Null(merged);
            Assert.Empty(repaired);
        }

        [Fact]
        public void A_second_run_is_a_byte_identical_no_op()
        {
            var merged = Repaired(InstallOf4078(Ids), "all three", out _);

            Assert.False(AlertDefinitionMigrator.TryRepairSupersededDefinitions(
                merged, ShippedJson(), out var again, out var repairedAgain),
                "a re-based install was re-based again, so every start would rewrite the file. " + CheckTheFixture);
            Assert.Null(again);
            Assert.Empty(repairedAgain);
        }

        // ── the carry rule: canBaseline and holdSeconds ─────────────────────────────────────────────────

        /// <summary>The carry table is enumerated, not trusted: every entry names a registered alert, moves
        /// to exactly what ships, and moves somewhere.</summary>
        [Fact]
        public void Every_declared_carry_is_registered_and_names_the_shipped_value()
        {
            var carries = AlertDefinitionMigrator.SupersededDefinitionCarries;
            Assert.True(carries.ContainsKey("connection_count") && carries.ContainsKey("sql_response_time"),
                "the carry table no longer declares the two Q14 carries (ruling 1 canBaseline, ruling 2 holdSeconds, DECISIONS 2026-09-18 04:21)");

            foreach (var (id, list) in carries)
            {
                Assert.True(AlertDefinitionMigrator.SupersededDefinitionSignatures.ContainsKey(id),
                    $"{id} declares a carry but has no superseded signature, so the carry can never run");
                Assert.NotEmpty(list);
                var shipped = AlertOf(ShippedJson(), id, "shipped");
                foreach (var carry in list)
                {
                    Assert.True(carry.OldValueJson != carry.NewValueJson, $"{id}.{carry.Property}: a carry to the same value moves nothing");
                    Assert.True(Token(shipped, carry.Property) == carry.NewValueJson,
                        $"{id}.{carry.Property}: declared new value {carry.NewValueJson}, but the shipped alert holds {Token(shipped, carry.Property) ?? "(absent)"}. "
                        + "A stale declaration moves nothing at runtime; fix the table or the shipped file.");
                }
            }
        }

        [Fact]
        public void An_unedited_install_gets_both_carries()
        {
            var merged = Repaired(InstallOf4078(Ids), "all three", out _);

            AssertToken(AlertOf(PreLaneJson(), "connection_count", "pre-lane fixture"), "connection_count", "canBaseline", "true", "pre-lane fixture");
            AssertToken(AlertOf(merged, "connection_count", "merged"), "connection_count", "canBaseline", "false", "merged");

            AssertToken(AlertOf(PreLaneJson(), "sql_response_time", "pre-lane fixture"), "sql_response_time", "holdSeconds", null, "pre-lane fixture");
            AssertToken(AlertOf(merged, "sql_response_time", "merged"), "sql_response_time", "holdSeconds", "120", "merged");

            // Lane alert-correctness 2 (A2): the same re-base also turns sql_response_time's learned baseline
            // off, through the second carry in its ONE SupersededDefinitionCarries array.
            AssertToken(AlertOf(PreLaneJson(), "sql_response_time", "pre-lane fixture"), "sql_response_time", "canBaseline", "true", "pre-lane fixture");
            AssertToken(AlertOf(merged, "sql_response_time", "merged"), "sql_response_time", "canBaseline", "false", "merged");

            // integrity_check_overdue declares no carry: its operator settings are exactly what it had.
            AssertToken(AlertOf(merged, "integrity_check_overdue", "merged"), "integrity_check_overdue", "canBaseline",
                Token(AlertOf(PreLaneJson(), "integrity_check_overdue", "pre-lane fixture"), "canBaseline"), "merged");
        }

        [Fact]
        public void A_v080_connection_count_with_no_canBaseline_is_left_without_one()
        {
            // v0.80.0 shipped no canBaseline at all, which the model reads as false: already the ruled value.
            var merged = Repaired(InstallOf4078("connection_count", a => a.Remove("canBaseline")), "connection_count", out _);
            AssertToken(AlertOf(merged, "connection_count", "merged"), "connection_count", "canBaseline", null, "merged");
        }

        [Theory]
        [InlineData("sql_response_time", "holdSeconds", "300")]
        [InlineData("sql_response_time", "holdSeconds", "0")]
        [InlineData("connection_count", "canBaseline", "false")]
        public void An_operator_who_set_the_carried_field_keeps_their_value(string id, string property, string theirs)
        {
            var merged = Repaired(InstallOf4078(id, a => a[property] = JsonNode.Parse(theirs)), id, out var repaired);

            AssertRepairedSet(repaired, new[] { id });   // the body is still re-based
            AssertToken(AlertOf(merged, id, "merged"), id, property, theirs, "merged");
        }

        [Theory]
        [InlineData("sql_response_time", "holdSeconds", null)]
        [InlineData("sql_response_time", "canBaseline", "true")]
        [InlineData("connection_count", "canBaseline", "true")]
        public void An_operator_edited_body_gets_no_carry(string id, string property, string? unchanged)
        {
            var installed = InstallOf4078(id, a => a["thresholds"]!["critical"] = 5000);
            Assert.False(AlertDefinitionMigrator.TryRepairSupersededDefinitions(
                installed.ToJsonString(), ShippedJson(), out _, out _));

            // Nothing is written, so the installed alert is what runs: its carried field is untouched.
            AssertToken(AlertOf(installed.ToJsonString(), id, "installed"), id, property, unchanged, "installed");
        }

        // ── the shipped bodies ────────────────────────────────────────────────────────────────────────

        /// <summary>Each registered alert really did change, and the change is the one ruled: a signature
        /// for a body identical to what ships would be a no-op dressed as a fix.</summary>
        [Theory]
        [MemberData(nameof(IdRows))]
        public void The_shipped_body_carries_the_ruled_fix(string id)
        {
            var shipped = AlertOf(ShippedJson(), id, "shipped");
            string Text(string property)
            {
                var v = Token(shipped, property);
                Assert.True(v != null, $"{id}.{property} is absent from the shipped body. " + CheckTheFixture);
                return v!;
            }

            var thresholds = shipped["thresholds"] as JsonObject;
            Assert.True(thresholds != null, $"{id}.thresholds is absent from the shipped body. " + CheckTheFixture);
            switch (id)
            {
                case "sql_response_time":
                    AssertToken(shipped, id, "queryMode", "\"response_time_probe\"", "shipped");
                    AssertToken(thresholds!, id, "warning", "1000", "shipped");
                    AssertToken(thresholds!, id, "critical", "2000", "shipped");
                    AssertToken(shipped, id, "unit", "\"milliseconds\"", "shipped");
                    AssertToken(shipped, id, "holdSeconds", "120", "shipped");
                    AssertHas(Text("description"), "2 minutes", id, "description");
                    AssertHas(Text("description"), "stalled SQLTriage", id, "description");
                    AssertHas(Text("description"), "login time", id, "description");
                    break;
                case "connection_count":
                    AssertToken(thresholds!, id, "warning", "80", "shipped");
                    AssertToken(thresholds!, id, "critical", "90", "shipped");
                    AssertHas(Text("query"), "32767", id, "query");
                    AssertHas(Text("query"), "sys.dm_exec_connections", id, "query");
                    AssertHas(Text("description"), "32,767", id, "description");
                    AssertToken(shipped, id, "canBaseline", "false", "shipped");
                    AssertHas(Text("description"), "never on a learned baseline or trend", id, "description");
                    break;
                case "integrity_check_overdue":
                    AssertHas(Text("query"), "LastGoodCheckDbTime", id, "query");
                    AssertHas(Text("query"), "DBCC DBINFO", id, "query");
                    AssertHas(Text("query"), "dbi_dbccLastKnownGood", id, "query");
                    AssertHas(Text("query"), "ISNULL(MAX", id, "query", present: false);
                    // Lane alert-correctness 2 (C1): with no date readable it THROWs (Unknown), never NULL.
                    AssertHas(Text("query"), "NOT EXISTS (SELECT 1 FROM @dbs WHERE last_good IS NOT NULL) THROW 50001", id, "query");
                    AssertHas(Text("description"), "with none readable the alert reports that it cannot be evaluated", id, "description");
                    AssertHas(Text("description"), "shows Unknown", id, "description", present: false);
                    AssertHas(Text("description"), "gives no reading", id, "description", present: false);
                    AssertToken(thresholds!, id, "warning", "336", "shipped");
                    AssertToken(thresholds!, id, "critical", "672", "shipped");
                    break;
                default:
                    Assert.Fail("no ruled fix is described for " + id);
                    break;
            }

            Assert.True(
                AlertDefinitionMigrator.DefinitionSignature(AlertOf(PreLaneJson(), id, "pre-lane fixture"))
                != AlertDefinitionMigrator.DefinitionSignature(shipped),
                $"{id}: the shipped body hashes to its own pre-lane signature, so the registered re-base would be a no-op. " + CheckTheFixture);
        }
    }
}
