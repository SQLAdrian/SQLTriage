// In the name of God, the Merciful, the Compassionate

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Locks down the dashboard database-presence gate: WHICH dashboards get a "this dashboard
    /// requires X" banner, and that there is exactly ONE mechanism deciding it.
    ///
    /// <para><b>The defect this closes.</b> 27 shipped dashboards, 9 of them tagged (in the recon
    /// list this test's data is drawn from) as reading a database that is not always present
    /// (SQLWATCH or PerformanceMonitor), and NONE of the 9 had <c>requiresDatabase</c> armed in
    /// config — so an install without the collector got either a silently blank dashboard or a
    /// dashboard that queried a database that was never there, with no banner explaining why. A
    /// SECOND, unrelated presence check also existed — <c>DynamicDashboard.razor</c>'s
    /// <c>SqlWatchRequiredDashboards</c>, a hardcoded 2-literal <c>HashSet</c> — duplicating (and
    /// silently DISAGREEING with) the config-driven mechanism for two of the nine dashboards.</para>
    ///
    /// <para>This file asserts the config side of the fix; the runtime side (that
    /// <c>RequiresExtraDb</c>/<c>RequiredDbMissing</c> actually read these values, and that a
    /// panel can opt out of its dashboard's requirement) lives in
    /// <c>Components/Shared/DynamicDashboard.razor</c> and is exercised by hand/live-probe, not by
    /// this file — this is a config lint, same class of instrument as
    /// <see cref="DashboardCounterArithmeticTests"/>, reading the SHIPPED config text directly so a
    /// future edit cannot silently drop a tag without a build ever knowing.</para>
    /// </summary>
    public class DashboardPresenceGateTests
    {
        private static string ConfigPath(string fileName) =>
            Path.Combine(RawPassedScan.RepoRoot().FullName, "Config", fileName);

        private static JsonElement? FindDashboard(string fileName, string dashboardId)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath(fileName)));
            foreach (var d in doc.RootElement.GetProperty("dashboards").EnumerateArray())
            {
                if (d.TryGetProperty("id", out var id) && id.GetString() == dashboardId)
                    return d.Clone();
            }
            return null;
        }

        private static string? RequiresDatabaseOf(JsonElement dashboard) =>
            dashboard.TryGetProperty("requiresDatabase", out var rd) && rd.ValueKind == JsonValueKind.String
                ? rd.GetString()
                : null;

        /// <summary>
        /// The old hardcoded gate (<c>DynamicDashboard.razor</c>'s <c>SqlWatchRequiredDashboards</c>
        /// HashSet, and its <c>RequiresSqlWatch</c> property) must never come back — it duplicated
        /// the config-driven <c>requiresDatabase</c> mechanism and could silently disagree with it.
        /// A test that only checks TODAY'S behaviour cannot catch a regression that re-adds the
        /// literal set with the SAME two names it had before; this greps for the identifier itself.
        /// </summary>
        [Fact]
        public void The_old_two_literal_SqlWatch_set_does_not_come_back()
        {
            var text = File.ReadAllText(Path.Combine(
                RawPassedScan.RepoRoot().FullName, "Components", "Shared", "DynamicDashboard.razor"));

            Assert.DoesNotContain("SqlWatchRequiredDashboards", text, StringComparison.Ordinal);
            Assert.DoesNotContain("RequiresSqlWatch", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// Every dashboard tagged in the 2026-08-20 recon as reading a database that is not always
        /// present must carry its <c>requiresDatabase</c> value in the config this product ships.
        ///
        /// <para>These rows used to run twice, once against dashboard-config.default.json, which is
        /// DELETED (DECISIONS 2026-08-26 18:23, ruling 3) — nothing read it and it had drifted to a
        /// different dashboard id set. The ninth dashboard is the visible casualty: "memory" (tabbed,
        /// merged) is the shipped one, and its row survives; the seed's pre-merge "pmemory_analysis"
        /// equivalent existed ONLY in that file, so its row goes with the file rather than being
        /// pointed at a dashboard this product does not ship. See
        /// <see cref="DashboardCounterArithmeticTests.The_per_second_grid_divides_the_cumulative_counter_by_instance_uptime"/>
        /// for the chokepoint that now carries these assertions to the embedded catalogue.</para>
        /// </summary>
        public static IEnumerable<object[]> NineDashboardsData()
        {
            var sqlwatch = new[] { "instance", "repository", "longqueries" };
            var perfmon = new[] { "pm", "pevents", "pquery", "pmhealth" };

            const string file = "dashboard-config.json";
            foreach (var id in sqlwatch) yield return new object[] { file, id, "SQLWATCH" };
            foreach (var id in perfmon) yield return new object[] { file, id, "PerformanceMonitor" };

            // The ninth: "memory", the tabbed dashboard the pre-merge "pmemory_analysis" became.
            yield return new object[] { file, "memory", "PerformanceMonitor" };
        }

        [Theory]
        [MemberData(nameof(NineDashboardsData))]
        public void The_nine_dashboards_have_their_expected_requiresDatabase(string fileName, string dashboardId, string expected)
        {
            var dashboard = FindDashboard(fileName, dashboardId);
            Assert.True(dashboard.HasValue, $"{fileName}: dashboard '{dashboardId}' not found.");
            Assert.Equal(expected, RequiresDatabaseOf(dashboard!.Value));
        }

        /// <summary>
        /// The memory dashboard's native Overview tab (3 panels reading sys.dm_os_memory_clerks
        /// directly) must NOT inherit the dashboard's PerformanceMonitor requirement — each of its
        /// panels opts out with its own <c>requiresDatabase: ""</c>. Its Analysis tab genuinely needs
        /// PerfMon and is left to inherit (panel-level field absent).
        /// </summary>
        [Fact]
        public void Memory_overview_panels_opt_out_of_the_dashboard_requirement()
        {
            var dashboard = FindDashboard("dashboard-config.json", "memory");
            Assert.True(dashboard.HasValue);

            var overview = dashboard!.Value.GetProperty("tabs").EnumerateArray()
                .Single(t => t.GetProperty("id").GetString() == "overview");
            foreach (var panel in overview.GetProperty("panels").EnumerateArray())
            {
                Assert.True(panel.TryGetProperty("requiresDatabase", out var rd) && rd.GetString() == "",
                    $"pmemory panel '{panel.GetProperty("id").GetString()}' config must declare requiresDatabase=\"\" (opt-out). " +
                    "This is a config-shape check only. It does not prove the panel renders. See DashboardDatabaseResolverTests " +
                    "for the runtime decision and the live re-verify for the render.");
            }

            var analysis = dashboard.Value.GetProperty("tabs").EnumerateArray()
                .Single(t => t.GetProperty("id").GetString() == "analysis");
            foreach (var panel in analysis.GetProperty("panels").EnumerateArray())
            {
                Assert.False(panel.TryGetProperty("requiresDatabase", out var rd) && rd.ValueKind == JsonValueKind.String,
                    $"pmemory analysis panel '{panel.GetProperty("id").GetString()}' should inherit the dashboard's PerformanceMonitor requirement, not carry its own override.");
            }
        }

        /// <summary>
        /// pevents' 5 natively-derivable panels (msdb.dbo.suspect_pages, sys.server_event_sessions /
        /// sys.dm_xe_session_targets — the system_health XEvent session) opt out of the dashboard's
        /// PerformanceMonitor requirement the same way; its 4 genuinely PerfMon-backed panels
        /// (collect.*/report.* schema) inherit it.
        /// </summary>
        [Theory]
        [InlineData("dashboard-config.json")]
        public void Pevents_native_panels_opt_out_of_the_dashboard_requirement(string fileName)
        {
            var native = new[] { "pevents.bad_pages", "pevents.io_issues", "pevents.nonyyielding", "pevents.memory_oom", "pevents.memory_broker" };
            var perfMonBacked = new[] { "pevents.default_trace", "pevents.system_health", "pevents.severe_errors", "pevents.config_changes" };

            var dashboard = FindDashboard(fileName, "pevents");
            Assert.True(dashboard.HasValue);
            var panels = dashboard!.Value.GetProperty("panels").EnumerateArray().ToList();

            foreach (var id in native)
            {
                var panel = panels.Single(p => p.GetProperty("id").GetString() == id);
                Assert.True(panel.TryGetProperty("requiresDatabase", out var rd) && rd.GetString() == "",
                    $"{fileName}: native panel '{id}' config must declare requiresDatabase=\"\" (opt-out). " +
                    "This is a config-shape check only. It does not prove the panel renders. See DashboardDatabaseResolverTests " +
                    "for the runtime decision and the live re-verify for the render.");
            }

            foreach (var id in perfMonBacked)
            {
                var panel = panels.Single(p => p.GetProperty("id").GetString() == id);
                Assert.False(panel.TryGetProperty("requiresDatabase", out var rd) && rd.ValueKind == JsonValueKind.String,
                    $"{fileName}: PerfMon-backed panel '{id}' should inherit the dashboard's requirement, not carry its own override.");
            }
        }

        /// <summary>
        /// querystore's panels are pure sys.query_store_*/sys.databases — it needs neither PerfMon
        /// nor SQLWATCH. It shipped tagged source="PerformanceMonitor" / defaultDatabase="SQLWATCH"
        /// (json) or defaultDatabase="PerformanceMonitor" (default), which put it in the wrong nav
        /// section and, once requiresDatabase started being honoured, would have hidden it behind a
        /// collector that its own queries never touch.
        /// </summary>
        [Theory]
        [InlineData("dashboard-config.json")]
        public void Querystore_needs_no_collector_and_lands_in_the_master_nav_section(string fileName)
        {
            var dashboard = FindDashboard(fileName, "querystore");
            Assert.True(dashboard.HasValue);

            Assert.Equal("master", dashboard!.Value.GetProperty("defaultDatabase").GetString());
            Assert.Null(RequiresDatabaseOf(dashboard.Value));

            var source = dashboard.Value.TryGetProperty("source", out var s) ? s.GetString() : "";
            Assert.NotEqual("PerformanceMonitor", source);
            Assert.NotEqual("SQLWATCH", source, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// qs.regressed and qs.regressed_queries (Query Store "regressed" panels) must floor on a
        /// minimum recent execution count — otherwise a query run once, slowly, reads as "regressed"
        /// off pure noise. Text lint over the shipped query, same class of instrument as
        /// <see cref="DashboardCounterArithmeticTests"/>: the fix WAS proven by hand against a live
        /// instance; this only stops the shipped config drifting back to the unfloored query.
        /// </summary>
        [Theory]
        [InlineData("dashboard-config.json", "qs.regressed")]
        [InlineData("dashboard-config.json", "qs.regressed_queries")]
        public void Regressed_query_panels_floor_on_a_minimum_execution_count(string fileName, string panelId)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath(fileName)));
            var panel = FindPanelAnywhere(doc.RootElement, panelId);
            Assert.True(panel.HasValue, $"{fileName}: panel '{panelId}' not found.");

            var sql = panel!.Value.GetProperty("query").GetProperty("sqlServer").GetString() ?? "";
            Assert.Contains("count_executions", sql, StringComparison.Ordinal);
            Assert.Contains("5", sql.Substring(sql.IndexOf("count_executions", StringComparison.Ordinal)), StringComparison.Ordinal);
        }

        private static JsonElement? FindPanelAnywhere(JsonElement el, string panelId)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    if (el.TryGetProperty("id", out var id) && id.GetString() == panelId &&
                        el.TryGetProperty("query", out _))
                        return el;
                    foreach (var p in el.EnumerateObject())
                    {
                        var found = FindPanelAnywhere(p.Value, panelId);
                        if (found.HasValue) return found;
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in el.EnumerateArray())
                    {
                        var found = FindPanelAnywhere(item, panelId);
                        if (found.HasValue) return found;
                    }
                    break;
            }
            return null;
        }
    }
}
