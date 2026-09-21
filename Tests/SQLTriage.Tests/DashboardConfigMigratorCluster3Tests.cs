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
    /// The delivery half of the strings lane's cluster 3. Editing
    /// <c>Config/dashboard-config.json</c> reaches FRESH installs only: the installer places that
    /// file with <c>onlyifdoesntexist</c>, AutoUpdateService lists it in ProtectedConfigFiles, and
    /// SQLTriageUpdater leaves it alone, all so an operator's own panel edits survive an upgrade.
    /// The cost of that promise is that a CORRECTION never arrives either, which is the gap
    /// strings-r1-11 named and DashboardConfigMigrator now closes - but only for bodies a fixing
    /// lane has registered. Registration is opt-in per fix, so it is the thing worth testing.
    ///
    /// <para>The fixture <c>prelane-cluster3-panels-84beb1c.json</c> holds the six panel bodies as
    /// they shipped at 84beb1c, read out of git rather than hand-copied. These tests drive the real
    /// migrator over that fixture against the real EMBEDDED catalogues, not repo files, so a
    /// registration that is present in the working tree but absent from the shipped binary cannot
    /// pass.</para>
    ///
    /// <para>TWO OF THE SIX ALREADY HAD ENTRIES. The dashboards lane registered
    /// <c>queryperf.top_queries</c> and <c>waits.details</c> when it fixed them in 2026-08-26.
    /// Those entries hold the PRE-dashboards-lane bodies, so an install already re-based onto the
    /// 84beb1c body matched neither them nor the new shipped body, and would have kept the wrong
    /// prose forever. The extra entries this lane adds are what cover that middle generation - the
    /// same shape pmemory.buffer_pool needed, and the reason a superseded set grows rather than
    /// being rewritten.</para>
    /// </summary>
    public class DashboardConfigMigratorCluster3Tests
    {
        private static readonly string[] Cluster3Keys =
        {
            "correlation/queryperf.top_queries",
            "correlation/queryperf.query_summary",
            "longqueries/longqueries.querytext",
            "livewaits/waits.details",
            "live/live.sessions",
            "sessions/sessions.top",
        };

        public static TheoryData<string> Keys()
        {
            var data = new TheoryData<string>();
            foreach (var k in Cluster3Keys) data.Add(k);
            return data;
        }

        private static string RepoRoot() => RawPassedScan.RepoRoot().FullName;

        private static string PreLaneJson() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "Tests", "SQLTriage.Tests", "Fixtures",
                "prelane-cluster3-panels-84beb1c.json"));

        private static string ShippedJson() =>
            DashboardConfigMigrator.ReadShippedDefaults()
            ?? throw new InvalidOperationException("the shipped dashboard catalogue is not embedded");

        private static string SupersededJson() =>
            DashboardConfigMigrator.ReadSupersededBodies()
            ?? throw new InvalidOperationException("the superseded bodies are not embedded");

        private static IReadOnlyDictionary<string, JsonObject> PanelsOf(string json)
        {
            var map = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            var root = JsonNode.Parse(json)!.AsObject();

            foreach (var dashboardNode in root["dashboards"]!.AsArray())
            {
                var dashboard = dashboardNode!.AsObject();
                var dashboardId = dashboard["id"]?.GetValue<string>() ?? "";

                var panels = new List<JsonObject>();
                if (dashboard["panels"] is JsonArray flat)
                    panels.AddRange(flat.Select(p => p!.AsObject()));
                if (dashboard["tabs"] is JsonArray tabs)
                    foreach (var tab in tabs)
                        if (tab!.AsObject()["panels"] is JsonArray tabPanels)
                            panels.AddRange(tabPanels.Select(p => p!.AsObject()));

                foreach (var panel in panels)
                {
                    var id = panel["id"]?.GetValue<string>();
                    if (id is not null) map[dashboardId + "/" + id] = panel;
                }
            }

            return map;
        }

        // ── 1. The fixture is genuinely the OLD text, not a copy of the new ────────────────

        [Theory]
        [MemberData(nameof(Keys))]
        public void The_fixture_holds_the_pre_fix_body_and_the_shipped_file_no_longer_does(string key)
        {
            var before = PanelsOf(PreLaneJson())[key];
            var after = PanelsOf(ShippedJson())[key];

            Assert.NotEqual(
                DashboardConfigMigrator.CanonicalPanel(before),
                DashboardConfigMigrator.CanonicalPanel(after));

            // A fixture that had drifted into agreement with the shipped file would make every
            // other test in this class pass while proving nothing.
            Assert.NotEqual(
                before["description"]?.GetValue<string>(),
                after["description"]?.GetValue<string>());
        }

        // ── 2. The registration matches, exactly ──────────────────────────────────────────

        [Theory]
        [MemberData(nameof(Keys))]
        public void The_prefix_body_is_registered_in_the_embedded_superseded_set(string key)
        {
            var parts = key.Split('/', 2);
            var before = PanelsOf(PreLaneJson())[key];
            var wanted = DashboardConfigMigrator.CanonicalPanel(before);

            var registered = JsonNode.Parse(SupersededJson())!.AsObject()["supersededPanels"]!.AsArray()
                .Select(e => e!.AsObject())
                .Where(e => e["dashboardId"]?.GetValue<string>() == parts[0]
                            && e["panelId"]?.GetValue<string>() == parts[1])
                .Select(e => DashboardConfigMigrator.CanonicalPanel(e["panel"]!.AsObject()))
                .ToList();

            Assert.True(registered.Count > 0,
                key + " has no superseded entry at all, so its correction reaches fresh installs only.");

            Assert.True(registered.Contains(wanted),
                key + " has " + registered.Count + " superseded entr(ies) but none of them is the body "
                    + "this lane replaced. The migrator matches on an exact canonical form and silently "
                    + "skips what it does not recognise, so a near-miss here is indistinguishable from "
                    + "no registration at all.");
        }

        // ── 3. End to end: an install carrying the old body is actually re-based ──────────

        [Fact]
        public void An_install_still_carrying_the_prefix_panels_is_rebased_onto_the_shipped_ones()
        {
            var repairedOk = DashboardConfigMigrator.TryRepairSupersededPanels(
                PreLaneJson(), ShippedJson(), SupersededJson(), out var merged, out var repaired);

            Assert.True(repairedOk, "the migrator repaired nothing at all");
            Assert.NotNull(merged);

            // The migrator reports its repaired keys joined by the UNIT SEPARATOR (U+241F), not by
            // a slash: dashboard and panel ids can each contain dots, so a printable delimiter
            // could be ambiguous. Its own doc comment says "dashboardId/panelId", which is what
            // this test believed on the first run - it went red naming all six keys as repaired
            // while claiming none was found. Matching the real separator rather than the documented
            // one, and saying so here, is the difference between a test that measures the migrator
            // and a test that measures a comment.
            foreach (var key in Cluster3Keys)
                Assert.Contains(key.Replace('/', '␟'), repaired);

            var after = PanelsOf(merged!);
            var shipped = PanelsOf(ShippedJson());

            foreach (var key in Cluster3Keys)
                Assert.Equal(
                    DashboardConfigMigrator.CanonicalPanel(shipped[key]),
                    DashboardConfigMigrator.CanonicalPanel(after[key]));
        }

        // ── 4. The operator's own toggle survives the repair ──────────────────────────────

        [Fact]
        public void A_repair_preserves_the_operators_enable_toggle_in_both_directions()
        {
            var installed = JsonNode.Parse(PreLaneJson())!.AsObject();

            // queryperf.query_summary ships disabled and must STAY disabled: its query returns no
            // Value column, so a card switched on by a prose fix would render empty. Flip the two
            // others to the opposite of what ships, so this proves preservation and not a
            // coincidence of everything already agreeing.
            SetEnabled(installed, "queryperf.top_queries", false);
            SetEnabled(installed, "live.sessions", false);

            var ok = DashboardConfigMigrator.TryRepairSupersededPanels(
                installed.ToJsonString(), ShippedJson(), SupersededJson(), out var merged, out _);

            Assert.True(ok);
            var after = PanelsOf(merged!);

            Assert.False(after["correlation/queryperf.top_queries"]["enabled"]!.GetValue<bool>(),
                "the migrator re-enabled a panel the operator had turned off");
            Assert.False(after["live/live.sessions"]["enabled"]!.GetValue<bool>(),
                "the migrator re-enabled a panel the operator had turned off");
            Assert.False(after["correlation/queryperf.query_summary"]["enabled"]!.GetValue<bool>(),
                "queryperf.query_summary came back enabled; its card still has no Value column to read");

            // ...and the bodies were still corrected around those preserved toggles.
            Assert.DoesNotContain("Real-time",
                after["correlation/queryperf.top_queries"]["description"]!.GetValue<string>(),
                StringComparison.OrdinalIgnoreCase);
        }

        // ── 5. A panel the operator edited is left exactly as it is ───────────────────────

        [Fact]
        public void A_panel_the_operator_edited_is_not_touched()
        {
            var installed = JsonNode.Parse(PreLaneJson())!.AsObject();

            var edited = PanelsOf(installed.ToJsonString())["live/live.sessions"];
            Mutate(installed, "live.sessions", p => p["height"] = 999);

            var ok = DashboardConfigMigrator.TryRepairSupersededPanels(
                installed.ToJsonString(), ShippedJson(), SupersededJson(), out var merged, out var repaired);

            Assert.True(ok);   // the other five still repair
            Assert.DoesNotContain("live␟live.sessions", repaired);

            var after = PanelsOf(merged!)["live/live.sessions"];
            Assert.Equal(999, after["height"]!.GetValue<int>());
            Assert.Equal(
                edited["description"]!.GetValue<string>(),
                after["description"]!.GetValue<string>());
        }

        // ── 6. Running it twice changes nothing the second time ──────────────────────────

        [Fact]
        public void The_repair_is_idempotent()
        {
            var ok = DashboardConfigMigrator.TryRepairSupersededPanels(
                PreLaneJson(), ShippedJson(), SupersededJson(), out var merged, out _);
            Assert.True(ok);

            var again = DashboardConfigMigrator.TryRepairSupersededPanels(
                merged!, ShippedJson(), SupersededJson(), out _, out _);

            Assert.False(again,
                "a second pass repaired something again, so a re-based install is still matching a "
                + "superseded body and would be rewritten on every start.");
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────

        private static void Mutate(JsonObject root, string panelId, Action<JsonObject> change)
        {
            foreach (var dashboardNode in root["dashboards"]!.AsArray())
            foreach (var p in dashboardNode!.AsObject()["panels"]!.AsArray())
                if (p!.AsObject()["id"]?.GetValue<string>() == panelId)
                {
                    change(p.AsObject());
                    return;
                }

            throw new InvalidOperationException(panelId + " is not in the fixture");
        }

        private static void SetEnabled(JsonObject root, string panelId, bool value)
            => Mutate(root, panelId, p => p["enabled"] = value);
    }
}
