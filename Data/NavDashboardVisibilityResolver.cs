/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using System.Linq;

namespace SQLTriage.Data
{
    /// <summary>
    /// Pure decision for whether a dashboard gets a nav link on the current server.
    ///
    /// <para><b>The defect this closes.</b> <c>NavMenu.razor</c> gated whole nav SECTIONS
    /// ("SQLWATCH", "Performance Monitor") on a single per-connection presence flag
    /// (<c>_SQLWATCHExists</c> / <c>_performanceMonitorExists</c>, populated by
    /// <c>RefreshDbFlags</c>/<c>DatabaseAvailabilityService.DatabaseExistsAsync</c>), plus
    /// <c>DashboardDefinition.DefaultDatabase</c> grouping. That is a coarser test than the one
    /// <see cref="DashboardDatabaseResolver"/> and <c>DynamicDashboard.razor</c>'s
    /// <c>RequiresExtraDb</c>/<c>PanelDatabaseAvailable</c> already apply at RENDER time: a panel can
    /// opt out of its dashboard's <c>requiresDatabase</c> via its own <c>requiresDatabase == ""</c>
    /// (e.g. memory's native Overview tab, pevents' 5 native panels — see
    /// <c>DashboardConfigService.GetPanelRequiresDatabase</c>), so the dashboard has renderable
    /// content on a server with no PerfMon/SQLWATCH database. Gating the whole section on the flag
    /// made that renderable content URL-only: reachable by typed route, absent from every nav list.
    /// </para>
    ///
    /// <para>This resolver is the single per-dashboard visibility test both nav sections should use
    /// instead: visible when the dashboard has no requirement, OR its required database is present,
    /// OR at least one of its panels opted out of that requirement. A section still renders only when
    /// at least one of its dashboards is visible by this test — the grouping (which section a
    /// dashboard's link lands in) is unchanged; only the per-dashboard membership test changes.</para>
    /// </summary>
    public static class NavDashboardVisibilityResolver
    {
        /// <summary>
        /// Decides whether a dashboard's nav link should render on the current server.
        /// </summary>
        /// <param name="dashboardRequiresDatabase">
        /// The dashboard's own <c>requiresDatabase</c> (see
        /// <see cref="Models.DashboardDefinition.RequiresDatabase"/>). <c>null</c>/empty means
        /// nothing gates the dashboard — always visible.
        /// </param>
        /// <param name="panelRequiresDatabaseList">
        /// The raw <c>requiresDatabase</c> value of every panel the dashboard owns (its flat
        /// <c>Panels</c> plus every <c>Tabs[].Panels</c> entry — see
        /// <see cref="Models.DashboardDefinition.AllPanels"/>). <c>null</c> is treated as "inherits"
        /// (no opt-out); <c>""</c> is an explicit opt-out of the dashboard's requirement.
        /// </param>
        /// <param name="requiredDbPresent">
        /// Whether <paramref name="dashboardRequiresDatabase"/> is actually present on the currently
        /// connected server — the existing live flag/<c>DatabaseExistsAsync</c> answer. Meaningless
        /// (and never consulted) when the dashboard has no requirement.
        /// </param>
        public static bool IsVisible(
            string? dashboardRequiresDatabase,
            IEnumerable<string?>? panelRequiresDatabaseList,
            bool requiredDbPresent)
        {
            if (string.IsNullOrEmpty(dashboardRequiresDatabase))
                return true; // nothing gates it

            if (requiredDbPresent)
                return true; // the requirement is satisfied outright

            return panelRequiresDatabaseList != null &&
                   panelRequiresDatabaseList.Any(p => p == string.Empty);
        }

        /// <summary>
        /// Turns a dashboard's own <c>requiresDatabase</c> into the <c>requiredDbPresent</c> bool
        /// <see cref="IsVisible"/> needs, reading a per-cycle presence cache keyed by database name
        /// (case-insensitive) — the shape <c>NavMenu.RefreshDbFlags</c> populates once per connection
        /// change for every DISTINCT <c>requiresDatabase</c> a master-group dashboard carries (today
        /// only Replication → "distribution"; the mechanism is generic, not hardcoded to that value).
        /// A dashboard with no requirement never consults the cache (<see cref="IsVisible"/>'s
        /// "nothing gates it" branch returns first), so <c>false</c> here for that case is inert, not
        /// a guess. A requirement whose database was never resolved into the cache (should not happen
        /// once <c>RefreshDbFlags</c> has run, but is not assumed here) is treated as absent — the
        /// same fail-closed default the two literal SQLWATCH/PerfMon flags already have before their
        /// first refresh.
        /// </summary>
        public static bool DbPresentFromCache(
            string? requiresDatabase,
            IReadOnlyDictionary<string, bool>? presenceByDatabase)
        {
            return !string.IsNullOrEmpty(requiresDatabase) &&
                   presenceByDatabase != null &&
                   presenceByDatabase.TryGetValue(requiresDatabase, out var present) &&
                   present;
        }
    }
}
