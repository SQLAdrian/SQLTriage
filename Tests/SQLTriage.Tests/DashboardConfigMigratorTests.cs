// In the name of God, the Merciful, the Compassionate

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// DashboardConfigMigrator carries a panel this product shipped broken and has since corrected to
    /// an EXISTING install that still holds the stock body, and never touches a panel an operator has
    /// edited. These tests exercise the pure re-base with inline fixtures, then the same mechanism
    /// against the real embedded shipped + superseded catalogues so a drift in either fails loudly.
    /// </summary>
    public class DashboardConfigMigratorTests
    {
        // p1 is the panel the "lane" corrected; p2 is a second panel the lane never touched.
        private const string Shipped =
            "{\"dashboards\":[{\"id\":\"d1\",\"panels\":[" +
            "{\"id\":\"p1\",\"title\":\"Corrected\",\"enabled\":true,\"defaultDatabase\":\"SQLWATCH\",\"query\":{\"sqlServer\":\"SELECT 1 /*fixed*/\"}}," +
            "{\"id\":\"p2\",\"title\":\"Untouched\",\"query\":{\"sqlServer\":\"SELECT 2\"}}" +
            "]}]}";

        private const string Superseded =
            "{\"supersededPanels\":[" +
            "{\"dashboardId\":\"d1\",\"panelId\":\"p1\",\"panel\":" +
            "{\"id\":\"p1\",\"title\":\"Broken\",\"defaultDatabase\":\"master\",\"query\":{\"sqlServer\":\"SELECT 1 /*broken*/\"}}}" +
            "]}";

        private static JsonObject PanelById(string configJson, string panelId)
        {
            var root = JsonNode.Parse(configJson)!.AsObject();
            return root["dashboards"]!.AsArray()
                .SelectMany(d => d!["panels"]!.AsArray())
                .Select(p => p!.AsObject())
                .Single(p => (string)p["id"]! == panelId);
        }

        [Fact]
        public void Rebases_a_stock_panel_and_preserves_the_operator_enabled_toggle()
        {
            // Operator disabled p1 but did not otherwise edit it; p2 they left alone.
            var installed =
                "{\"dashboards\":[{\"id\":\"d1\",\"panels\":[" +
                "{\"id\":\"p1\",\"title\":\"Broken\",\"enabled\":false,\"defaultDatabase\":\"master\",\"query\":{\"sqlServer\":\"SELECT 1 /*broken*/\"}}," +
                "{\"id\":\"p2\",\"title\":\"Untouched\",\"query\":{\"sqlServer\":\"SELECT 2\"}}" +
                "]}]}";

            var ok = DashboardConfigMigrator.TryRepairSupersededPanels(
                installed, Shipped, Superseded, out var merged, out var repaired);

            Assert.True(ok);
            Assert.Equal(new[] { "d1␟p1" }, repaired);
            Assert.NotNull(merged);

            var p1 = PanelById(merged!, "p1");
            Assert.Equal("Corrected", (string)p1["title"]!);                 // shipped body delivered
            Assert.Equal("SQLWATCH", (string)p1["defaultDatabase"]!);        // shipped body delivered
            Assert.Equal("SELECT 1 /*fixed*/", (string)p1["query"]!["sqlServer"]!);
            Assert.False((bool)p1["enabled"]!);                              // operator toggle preserved
        }

        [Fact]
        public void Leaves_an_operator_edited_panel_exactly_as_it_is()
        {
            // Operator edited p1's SQL, so it matches no shipped-and-retired body.
            var installed =
                "{\"dashboards\":[{\"id\":\"d1\",\"panels\":[" +
                "{\"id\":\"p1\",\"title\":\"Broken\",\"defaultDatabase\":\"master\",\"query\":{\"sqlServer\":\"SELECT 1 /*operator tuned*/\"}}" +
                "]}]}";

            var ok = DashboardConfigMigrator.TryRepairSupersededPanels(
                installed, Shipped, Superseded, out var merged, out var repaired);

            Assert.False(ok);
            Assert.Null(merged);
            Assert.Empty(repaired);
        }

        [Fact]
        public void Is_idempotent_once_a_panel_has_been_rebased()
        {
            var installed =
                "{\"dashboards\":[{\"id\":\"d1\",\"panels\":[" +
                "{\"id\":\"p1\",\"title\":\"Broken\",\"query\":{\"sqlServer\":\"SELECT 1 /*broken*/\"},\"defaultDatabase\":\"master\"}" +
                "]}]}";

            Assert.True(DashboardConfigMigrator.TryRepairSupersededPanels(
                installed, Shipped, Superseded, out var merged, out _));

            // The rebased panel now matches the current shipped body, which is not in the superseded set.
            Assert.False(DashboardConfigMigrator.TryRepairSupersededPanels(
                merged!, Shipped, Superseded, out var second, out var repaired2));
            Assert.Null(second);
            Assert.Empty(repaired2);
        }

        [Fact]
        public void Fails_closed_on_an_unparseable_installed_config()
        {
            var ok = DashboardConfigMigrator.TryRepairSupersededPanels(
                "{ this is not json", Shipped, Superseded, out var merged, out var repaired);

            Assert.False(ok);
            Assert.Null(merged);
            Assert.Empty(repaired);
        }

        /// <summary>
        /// A CRLF install (every real Windows install) must be handed back single CRLF line endings, not
        /// doubled CRs (\r\r\n). The serializer emits Environment.NewLine (CRLF on Windows), so the merge
        /// must collapse before it re-applies the installed file's endings. Regression pin for the
        /// line-ending-doubling defect the adversarial gate caught.
        /// </summary>
        [Fact]
        public void Preserves_single_crlf_line_endings_on_a_crlf_install()
        {
            var installed =
                "{\r\n  \"dashboards\": [\r\n    {\r\n      \"id\": \"d1\",\r\n      \"panels\": [\r\n" +
                "        { \"id\": \"p1\", \"title\": \"Broken\", \"defaultDatabase\": \"master\", \"query\": { \"sqlServer\": \"SELECT 1 /*broken*/\" } }\r\n" +
                "      ]\r\n    }\r\n  ]\r\n}\r\n";

            var ok = DashboardConfigMigrator.TryRepairSupersededPanels(
                installed, Shipped, Superseded, out var merged, out _);

            Assert.True(ok);
            Assert.NotNull(merged);
            Assert.DoesNotContain("\r\r\n", merged);                     // no doubled CR
            Assert.Contains("\r\n", merged);                             // CRLF is preserved
            Assert.Equal(merged!.Replace("\r\n", "\n"),                  // every LF is paired with exactly one CR
                merged.Replace("\r", ""));
        }

        /// <summary>
        /// An LF install gets bare LF back, never a stray CR. The migrator promises to match the file's
        /// existing line endings in either direction.
        /// </summary>
        [Fact]
        public void Preserves_bare_lf_line_endings_on_an_lf_install()
        {
            var installed =
                "{\n  \"dashboards\": [\n    {\n      \"id\": \"d1\",\n      \"panels\": [\n" +
                "        { \"id\": \"p1\", \"title\": \"Broken\", \"defaultDatabase\": \"master\", \"query\": { \"sqlServer\": \"SELECT 1 /*broken*/\" } }\n" +
                "      ]\n    }\n  ]\n}\n";

            var ok = DashboardConfigMigrator.TryRepairSupersededPanels(
                installed, Shipped, Superseded, out var merged, out _);

            Assert.True(ok);
            Assert.NotNull(merged);
            Assert.DoesNotContain("\r", merged);                         // no CR introduced on an LF file
        }

        // ── Against the real embedded catalogues ──────────────────────────────────────────

        [Fact]
        public void The_shipped_and_superseded_catalogues_are_embedded()
        {
            Assert.NotNull(DashboardConfigMigrator.ReadShippedDefaults());
            Assert.NotNull(DashboardConfigMigrator.ReadSupersededBodies());
        }

        /// <summary>
        /// THE CHOKEPOINT. Config/dashboard-config.json is what every config lint in this suite reads
        /// (DashboardCounterArithmeticTests, NavVisibilityConfigShapeTests, DashboardPresenceGateTests,
        /// WaitVerdictPanelTests). The copy compiled into SQLTriage.dll is what
        /// <c>DashboardConfigMigrator</c> re-bases installed panels from and what
        /// <c>DashboardFactoryReset</c> restores a dashboard to. Two artifacts, one set of assertions,
        /// and until this test nothing measured that they were the same thing.
        ///
        /// <para>WHY IT REPLACES A PAIR OF LINT LISTS. Until DECISIONS 2026-08-26 18:23 ruling 3, drift
        /// between two config files was guarded by running ~21 hand-paired assertion rows against both.
        /// That is a scan: it covers what somebody remembered to duplicate, and the file it covered was
        /// read by nothing. This is one assertion over the whole document, so a panel, a query, a
        /// threshold, a title or a whole dashboard cannot differ between the linted file and the shipped
        /// artifact without failing here.</para>
        ///
        /// <para>It also makes a STALE BUILD detectable. Editing Config/dashboard-config.json without
        /// rebuilding SQLTriage would leave the lints reading a new file and the migrator and reset
        /// serving an old one. That divergence now fails a test instead of shipping.</para>
        /// </summary>
        [Fact]
        public void The_embedded_catalogue_is_byte_identical_to_the_shipped_config_file()
        {
            var onDisk = System.IO.File.ReadAllText(System.IO.Path.Combine(
                RawPassedScan.RepoRoot().FullName, "Config", "dashboard-config.json"));
            var embedded = DashboardConfigMigrator.ReadShippedDefaults();

            Assert.NotNull(embedded);
            Assert.Equal(onDisk, embedded);
        }

        /// <summary>
        /// Config/dashboard-config.default.json is deleted (ruling 3). A test that only checks today's
        /// behaviour cannot catch somebody restoring the file, so this greps for it by name across the
        /// places that would bring it back: the repo tree, the project file, and the public allow-list.
        /// It was Content-Removed from every build and read by nothing, it drifted to a different
        /// dashboard and panel id set, and ~21 assertion rows were spent keeping a dead file tidy.
        /// </summary>
        [Fact]
        public void The_dead_default_seed_does_not_come_back()
        {
            var root = RawPassedScan.RepoRoot().FullName;

            Assert.False(
                System.IO.File.Exists(System.IO.Path.Combine(root, "Config", "dashboard-config.default.json")),
                "Config/dashboard-config.default.json was deleted by DECISIONS 2026-08-26 18:23 ruling 3. "
                + "Nothing reads it; a copy on disk is drift waiting to be mistaken for what ships.");

            foreach (var relative in new[] { "SQLTriage.csproj", System.IO.Path.Combine(".handoff", ".publicallow") })
            {
                var path = System.IO.Path.Combine(root, relative);
                if (!System.IO.File.Exists(path)) continue;
                Assert.DoesNotContain("dashboard-config.default.json", System.IO.File.ReadAllText(path),
                    System.StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Every superseded body, replayed as an installed panel, re-bases to a current shipped body —
        /// proving each superseded id still has a delivery target and the whole lane's corrections are
        /// reachable. A distinct-key count pins the reviewed set so an accidental addition or loss
        /// fails here.
        /// </summary>
        [Fact]
        public void Every_superseded_panel_rebases_to_a_current_shipped_body()
        {
            var superseded = JsonNode.Parse(DashboardConfigMigrator.ReadSupersededBodies()!)!.AsObject();
            var entries = superseded["supersededPanels"]!.AsArray();

            // Build an installed config from ONE superseded body per (dashboardId, panelId).
            var byDashboard = new Dictionary<string, Dictionary<string, JsonObject>>();
            foreach (var e in entries)
            {
                var did = (string)e!["dashboardId"]!;
                var pid = (string)e["panelId"]!;
                var panel = e["panel"]!.AsObject().DeepClone().AsObject();
                if (!byDashboard.TryGetValue(did, out var panels))
                    byDashboard[did] = panels = new Dictionary<string, JsonObject>();
                panels.TryAdd(pid, panel);
            }

            var dashboards = new JsonArray();
            foreach (var (did, panels) in byDashboard)
            {
                var panelArray = new JsonArray();
                foreach (var p in panels.Values) panelArray.Add(p);
                dashboards.Add(new JsonObject { ["id"] = did, ["panels"] = panelArray });
            }
            var installed = new JsonObject { ["dashboards"] = dashboards }.ToJsonString();

            var distinctKeys = byDashboard.Sum(kv => kv.Value.Count);

            var ok = DashboardConfigMigrator.TryRepairSupersededPanels(
                installed, DashboardConfigMigrator.ReadShippedDefaults()!,
                DashboardConfigMigrator.ReadSupersededBodies()!, out var merged, out var repaired);

            Assert.True(ok);
            // The reviewed set of corrected panels. 37 -> 39 on 2026-08-28 (strings lane cluster 1):
            // livetempdb.used_mb and livetempdb.version_store_mb joined it, both reading the
            // database-scoped sys.dm_db_file_space_usage unqualified against master and so returning
            // NULL on every install. pmemory.buffer_pool gained a THIRD superseded body in the same
            // commit but was already a distinct key, so the count moves by two and not by three.
            //
            // 39 -> 43 on 2026-08-28 (strings lane cluster 3), the six panels whose description
            // asserted a measurement their query did not take: queryperf.query_summary (shipped an
            // internal bug report as its client-facing tooltip), longqueries.querytext (promised
            // execution plan XML it never selected and stamped every row with getdate()),
            // live.sessions (described sys.dm_exec_sessions while querying sys.sysprocesses under
            // three undisclosed filters) and sessions.top (promised session_id and login that no
            // branch of its plan-cache aggregate can return). Four, not six: queryperf.top_queries
            // and waits.details were already distinct keys from this lane's own 2026-08-26 pass, and
            // each merely gained a second superseded body covering the 84beb1c generation - the same
            // shape pmemory.buffer_pool needed above, and the reason this count moves by fewer than
            // the number of panels a lane corrects.
            Assert.Equal(43, distinctKeys);
            Assert.Equal(distinctKeys, repaired.Count);     // every one found a shipped target and re-based
            Assert.NotNull(merged);

            // A second pass is a no-op: the rebased bodies are the current shipped ones.
            Assert.False(DashboardConfigMigrator.TryRepairSupersededPanels(
                merged!, DashboardConfigMigrator.ReadShippedDefaults()!,
                DashboardConfigMigrator.ReadSupersededBodies()!, out _, out _));
        }
    }
}
