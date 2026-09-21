/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Exhaustive coverage of <see cref="NavDashboardVisibilityResolver.IsVisible"/> — the seam
    /// extracted from Adrian's 2026-08-20 ruling: NavMenu gated whole nav sections ("SQLWATCH",
    /// "Performance Monitor") on a single per-connection presence flag, so a dashboard with
    /// opted-out panels (e.g. memory's native Overview tab, pevents' 5 native panels — renderable per
    /// <see cref="DashboardDatabaseResolver"/>/<c>DynamicDashboard.razor</c>'s
    /// <c>RequiresExtraDb</c>/<c>PanelDatabaseAvailable</c>) still lost its nav link entirely on an
    /// install without that collector database.
    ///
    /// <para>Cross-product: dashboard requiresDatabase (null / empty / set) × required-db presence
    /// (present / absent) × panel opt-out shape (none opted, some opted, all inherit). This is a
    /// config-lint/pure-decision-class instrument: it proves the DECISION function only, not that
    /// NavMenu calls it correctly (that is <see cref="NavVisibilityConfigShapeTests"/>, config-shape
    /// only) or that a link actually renders (live re-verify).</para>
    /// </summary>
    public class NavDashboardVisibilityResolverTests
    {
        // ── dashboardRequiresDatabase null/empty: nothing gates it — always visible,
        //    regardless of db presence or panel shape. ──

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void No_requirement_is_always_visible_db_absent_no_panels_opted(string? requirement)
        {
            Assert.True(NavDashboardVisibilityResolver.IsVisible(
                requirement, new List<string?> { null, null }, requiredDbPresent: false));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void No_requirement_is_always_visible_db_present(string? requirement)
        {
            Assert.True(NavDashboardVisibilityResolver.IsVisible(
                requirement, new List<string?> { null }, requiredDbPresent: true));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void No_requirement_is_always_visible_no_panels_at_all(string? requirement)
        {
            Assert.True(NavDashboardVisibilityResolver.IsVisible(
                requirement, null, requiredDbPresent: false));
        }

        // ── dashboardRequiresDatabase set, required db PRESENT: always visible,
        //    regardless of panel shape (case b). ──

        [Fact]
        public void Requirement_set_db_present_no_panels_opted_is_visible()
        {
            Assert.True(NavDashboardVisibilityResolver.IsVisible(
                "PerformanceMonitor", new List<string?> { null, null }, requiredDbPresent: true));
        }

        [Fact]
        public void Requirement_set_db_present_some_panels_opted_is_visible()
        {
            Assert.True(NavDashboardVisibilityResolver.IsVisible(
                "PerformanceMonitor", new List<string?> { "", null }, requiredDbPresent: true));
        }

        [Fact]
        public void Requirement_set_db_present_all_panels_inherit_is_visible()
        {
            Assert.True(NavDashboardVisibilityResolver.IsVisible(
                "PerformanceMonitor", new List<string?> { null, null, null }, requiredDbPresent: true));
        }

        [Fact]
        public void Requirement_set_db_present_no_panel_list_is_visible()
        {
            Assert.True(NavDashboardVisibilityResolver.IsVisible(
                "PerformanceMonitor", null, requiredDbPresent: true));
        }

        // ── dashboardRequiresDatabase set, required db ABSENT: visible only via an opted-out
        //    panel (case c) — this is the defect the wave's ruling closes. ──

        [Fact]
        public void Requirement_set_db_absent_some_panels_opted_is_visible()
        {
            // memory / pevents shape: at least one panel carries requiresDatabase == "".
            Assert.True(NavDashboardVisibilityResolver.IsVisible(
                "PerformanceMonitor", new List<string?> { "", null, "pmemory.memory_stats" }, requiredDbPresent: false));
        }

        [Fact]
        public void Requirement_set_db_absent_all_panels_opted_is_visible()
        {
            Assert.True(NavDashboardVisibilityResolver.IsVisible(
                "PerformanceMonitor", new List<string?> { "", "" }, requiredDbPresent: false));
        }

        [Fact]
        public void Requirement_set_db_absent_no_panels_opted_is_hidden()
        {
            // pm / pmhealth / pquery shape: every panel inherits, none opt out.
            Assert.False(NavDashboardVisibilityResolver.IsVisible(
                "PerformanceMonitor", new List<string?> { null, null, null }, requiredDbPresent: false));
        }

        [Fact]
        public void Requirement_set_db_absent_all_panels_inherit_is_hidden()
        {
            // instance / longqueries shape (SQLWATCH), same as above under a different db name.
            Assert.False(NavDashboardVisibilityResolver.IsVisible(
                "SQLWATCH", new List<string?> { null }, requiredDbPresent: false));
        }

        [Fact]
        public void Requirement_set_db_absent_no_panel_list_is_hidden()
        {
            Assert.False(NavDashboardVisibilityResolver.IsVisible(
                "PerformanceMonitor", null, requiredDbPresent: false));
        }

        [Fact]
        public void Requirement_set_db_absent_empty_panel_list_is_hidden()
        {
            Assert.False(NavDashboardVisibilityResolver.IsVisible(
                "PerformanceMonitor", new List<string?>(), requiredDbPresent: false));
        }

        [Fact]
        public void Requirement_set_db_absent_a_panel_with_a_distinct_non_empty_value_does_not_opt_out()
        {
            // A distinct explicit db name is accepted by the schema but is NOT an opt-out — only
            // exactly "" counts (mirrors DashboardDatabaseResolver's own "own requirement" case).
            Assert.False(NavDashboardVisibilityResolver.IsVisible(
                "PerformanceMonitor", new List<string?> { "distribution" }, requiredDbPresent: false));
        }

        [Fact]
        public void Wholly_gated_dashboard_stays_hidden_current_behaviour_preserved()
        {
            // pm's actual shape: 6 panels, all null (inherit), no opt-out.
            var panels = new List<string?> { null, null, null, null, null, null };
            Assert.False(NavDashboardVisibilityResolver.IsVisible("PerformanceMonitor", panels, requiredDbPresent: false));
            Assert.True(NavDashboardVisibilityResolver.IsVisible("PerformanceMonitor", panels, requiredDbPresent: true));
        }

        // ── DbPresentFromCache — NavMenu's per-cycle master-group presence lookup. Cross-product:
        //    requirement (null / empty / set) × cache state (present=true / present=false / absent
        //    from cache entirely) × case (the cache is keyed case-insensitively, same as
        //    DatabaseAvailabilityService's own cache). ──

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void DbPresentFromCache_no_requirement_is_false_regardless_of_cache_contents(string? requirement)
        {
            var cache = new Dictionary<string, bool> { ["distribution"] = true };
            Assert.False(NavDashboardVisibilityResolver.DbPresentFromCache(requirement, cache));
        }

        [Fact]
        public void DbPresentFromCache_requirement_set_true_in_cache_is_true()
        {
            var cache = new Dictionary<string, bool> { ["distribution"] = true };
            Assert.True(NavDashboardVisibilityResolver.DbPresentFromCache("distribution", cache));
        }

        [Fact]
        public void DbPresentFromCache_requirement_set_false_in_cache_is_false()
        {
            var cache = new Dictionary<string, bool> { ["distribution"] = false };
            Assert.False(NavDashboardVisibilityResolver.DbPresentFromCache("distribution", cache));
        }

        [Fact]
        public void DbPresentFromCache_requirement_set_absent_from_cache_is_false()
        {
            // Should not happen once RefreshDbFlags has run for a config-declared requirement, but
            // the lookup fails closed rather than assumed-present.
            var cache = new Dictionary<string, bool> { ["distribution"] = true };
            Assert.False(NavDashboardVisibilityResolver.DbPresentFromCache("SomeOtherDb", cache));
        }

        [Fact]
        public void DbPresentFromCache_null_cache_is_false()
        {
            Assert.False(NavDashboardVisibilityResolver.DbPresentFromCache("distribution", null));
        }

        [Fact]
        public void DbPresentFromCache_lookup_is_case_insensitive()
        {
            var cache = new Dictionary<string, bool>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["distribution"] = true
            };
            Assert.True(NavDashboardVisibilityResolver.DbPresentFromCache("DISTRIBUTION", cache));
        }
    }
}
