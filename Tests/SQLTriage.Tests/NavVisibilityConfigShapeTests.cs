// In the name of God, the Merciful, the Compassionate

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Locks down NavMenu's per-dashboard nav-visibility outcome on the SHIPPED configs, extending
    /// <see cref="DashboardPresenceGateTests"/>'s config-lint class to the ruling this wave closed:
    /// NavMenu used to gate whole nav sections on a single per-connection presence flag
    /// (<c>_SQLWATCHExists</c> / <c>_performanceMonitorExists</c>), so a dashboard whose panels
    /// opted out of the dashboard-level requirement (memory's native Overview tab, pevents' 5 native
    /// panels) still lost its nav link on an install without that collector database.
    ///
    /// <para>This file reads the shipped config text (same instrument class as
    /// <see cref="DashboardPresenceGateTests"/>), extracts each dashboard's
    /// <c>requiresDatabase</c> and every one of its panels' <c>requiresDatabase</c> (flat
    /// <c>panels</c> AND every <c>tabs[].panels</c> entry), and feeds them through the real
    /// <see cref="NavDashboardVisibilityResolver.IsVisible"/> — proving the CONFIG SHAPE plus the
    /// RESOLVER'S decision on that shape, not that NavMenu wires the resolver correctly (that is
    /// unit-tested indirectly by <see cref="NavDashboardVisibilityResolverTests"/>'s exhaustive cases
    /// and confirmed live by the re-verify) and not that a link actually renders on screen.</para>
    /// </summary>
    public class NavVisibilityConfigShapeTests
    {
        private static string ConfigPath(string fileName) =>
            Path.Combine(RawPassedScan.RepoRoot().FullName, "Config", fileName);

        private static JsonElement FindDashboard(string fileName, string dashboardId)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath(fileName)));
            foreach (var d in doc.RootElement.GetProperty("dashboards").EnumerateArray())
            {
                if (d.TryGetProperty("id", out var id) && id.GetString() == dashboardId)
                    return d.Clone();
            }
            throw new InvalidOperationException($"{fileName}: dashboard '{dashboardId}' not found.");
        }

        private static string? RequiresDatabaseOf(JsonElement el) =>
            el.TryGetProperty("requiresDatabase", out var rd) && rd.ValueKind == JsonValueKind.String
                ? rd.GetString()
                : null;

        /// <summary>
        /// Every panel this dashboard owns — its flat <c>panels</c> array plus every <c>tabs[].panels</c>
        /// entry — mirroring <see cref="Data.Models.DashboardDefinition.AllPanels"/> at the JSON layer.
        /// </summary>
        private static List<string?> AllPanelRequiresDatabase(JsonElement dashboard)
        {
            var result = new List<string?>();

            if (dashboard.TryGetProperty("panels", out var panels) && panels.ValueKind == JsonValueKind.Array)
                foreach (var p in panels.EnumerateArray())
                    result.Add(RequiresDatabaseOf(p));

            if (dashboard.TryGetProperty("tabs", out var tabs) && tabs.ValueKind == JsonValueKind.Array)
                foreach (var tab in tabs.EnumerateArray())
                    if (tab.TryGetProperty("panels", out var tabPanels) && tabPanels.ValueKind == JsonValueKind.Array)
                        foreach (var p in tabPanels.EnumerateArray())
                            result.Add(RequiresDatabaseOf(p));

            return result;
        }

        private static bool IsNavVisible(string fileName, string dashboardId, bool requiredDbPresent)
        {
            var dashboard = FindDashboard(fileName, dashboardId);
            return NavDashboardVisibilityResolver.IsVisible(
                RequiresDatabaseOf(dashboard),
                AllPanelRequiresDatabase(dashboard),
                requiredDbPresent);
        }

        // ── PerfMon-gated dashboards, PerfMon ABSENT ──────────────────────────────────────────

        [Fact]
        public void Memory_is_nav_visible_with_PerfMon_absent_via_its_opted_out_Overview_panels()
        {
            Assert.True(IsNavVisible("dashboard-config.json", "memory", requiredDbPresent: false));
        }

        [Theory]
        [InlineData("dashboard-config.json")]
        public void Pevents_is_nav_visible_with_PerfMon_absent_via_its_5_opted_out_native_panels(string fileName)
        {
            Assert.True(IsNavVisible(fileName, "pevents", requiredDbPresent: false));
        }

        [Theory]
        [InlineData("dashboard-config.json")]
        public void Pm_is_nav_hidden_with_PerfMon_absent_no_panel_opts_out(string fileName)
        {
            Assert.False(IsNavVisible(fileName, "pm", requiredDbPresent: false));
        }

        [Theory]
        [InlineData("dashboard-config.json")]
        public void Pmhealth_is_nav_hidden_with_PerfMon_absent_no_panel_opts_out(string fileName)
        {
            Assert.False(IsNavVisible(fileName, "pmhealth", requiredDbPresent: false));
        }

        [Theory]
        [InlineData("dashboard-config.json")]
        public void Pquery_is_nav_hidden_with_PerfMon_absent_no_panel_opts_out(string fileName)
        {
            Assert.False(IsNavVisible(fileName, "pquery", requiredDbPresent: false));
        }

        // ── PerfMon PRESENT: every PerfMon-group dashboard is visible regardless of panel shape ──

        [Theory]
        [InlineData("dashboard-config.json", "pm")]
        [InlineData("dashboard-config.json", "pevents")]
        [InlineData("dashboard-config.json", "pquery")]
        [InlineData("dashboard-config.json", "pmhealth")]
        [InlineData("dashboard-config.json", "memory")]
        public void PerfMon_group_dashboards_are_nav_visible_with_PerfMon_present(string fileName, string dashboardId)
        {
            Assert.True(IsNavVisible(fileName, dashboardId, requiredDbPresent: true));
        }

        // ── SQLWATCH-gated dashboards, SQLWATCH ABSENT: neither has an opted-out panel ────────

        [Theory]
        [InlineData("dashboard-config.json", "instance")]
        [InlineData("dashboard-config.json", "longqueries")]
        public void Instance_and_longqueries_are_nav_hidden_with_SQLWATCH_absent(string fileName, string dashboardId)
        {
            Assert.False(IsNavVisible(fileName, dashboardId, requiredDbPresent: false));
        }

        [Theory]
        [InlineData("dashboard-config.json", "instance")]
        [InlineData("dashboard-config.json", "longqueries")]
        public void Instance_and_longqueries_are_nav_visible_with_SQLWATCH_present(string fileName, string dashboardId)
        {
            Assert.True(IsNavVisible(fileName, dashboardId, requiredDbPresent: true));
        }

        // ── querystore: no requiresDatabase at all — always visible, independent of both flags ──

        [Theory]
        [InlineData("dashboard-config.json")]
        public void Querystore_is_always_nav_visible(string fileName)
        {
            Assert.True(IsNavVisible(fileName, "querystore", requiredDbPresent: false));
            Assert.True(IsNavVisible(fileName, "querystore", requiredDbPresent: true));
        }

        /// <summary>
        /// querystore's <c>defaultDatabase</c> is "master" (lane A retag), which is what puts it in
        /// NavMenu's master-backed "Live Dashboards" section. querystore itself carries no
        /// <c>requiresDatabase</c>, so it stays unconditionally visible even now that the section
        /// runs every member through <see cref="NavDashboardVisibilityResolver.IsVisible"/> — a
        /// member WITH a requirement (replication → "distribution", below) is no longer
        /// unconditional. This pins the fact the wave's fix note asked to state explicitly.
        /// </summary>
        [Theory]
        [InlineData("dashboard-config.json")]
        public void Querystore_lands_in_the_master_backed_group(string fileName)
        {
            var dashboard = FindDashboard(fileName, "querystore");
            Assert.Equal("master", dashboard.GetProperty("defaultDatabase").GetString());
            Assert.Equal("Dashboards", dashboard.GetProperty("navCategory").GetString());
            Assert.Null(RequiresDatabaseOf(dashboard));
        }

        // ── replication: the master-group dashboard that carries a requiresDatabase (the RULED
        //    defect). No panel opts out, so — unlike memory/pevents — it has no escape hatch: it is
        //    strictly hidden when its required database is absent, visible when present. This test
        //    always read only the SHIPPED config: the deleted dashboard-config.default.json (ruling 3,
        //    DECISIONS 2026-08-26 18:23) had no requiresDatabase on its replication entry at all, which
        //    was one of the drifts that made a never-read file worth deleting rather than lint-herding. ──

        [Fact]
        public void Replication_carries_distribution_as_its_requiresDatabase()
        {
            var dashboard = FindDashboard("dashboard-config.json", "replication");
            Assert.Equal("distribution", RequiresDatabaseOf(dashboard));
            Assert.Equal("master", dashboard.GetProperty("defaultDatabase").GetString());
            Assert.Equal("Dashboards", dashboard.GetProperty("navCategory").GetString());
        }

        [Fact]
        public void Replication_is_nav_hidden_with_distribution_absent_no_panel_opts_out()
        {
            Assert.False(IsNavVisible("dashboard-config.json", "replication", requiredDbPresent: false));
        }

        [Fact]
        public void Replication_is_nav_visible_with_distribution_present()
        {
            Assert.True(IsNavVisible("dashboard-config.json", "replication", requiredDbPresent: true));
        }
    }
}
