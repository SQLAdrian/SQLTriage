/* In the name of God, the Merciful, the Compassionate */

using System;

namespace SQLTriage.Data
{
    /// <summary>
    /// Pure decision for which database catalog a panel's query executes against.
    ///
    /// <para><b>The defect this closes.</b> <see cref="ServerConnectionManager.CurrentServer"/>'s
    /// Database is a single process-wide value that gets force-set to a dashboard's
    /// <c>DefaultDatabase</c> on load (e.g. "PerformanceMonitor") and re-pointed by the Query Store
    /// database selector. Before this resolver existed, every panel query unconditionally honoured
    /// that override whenever it was set to anything other than "master" — including panels that
    /// had explicitly opted OUT of the dashboard's database requirement via
    /// <c>requiresDatabase: ""</c> (e.g. the memory dashboard's native Overview tab, reading
    /// <c>sys.dm_os_memory_clerks</c> off <c>master</c> — no PerfMon needed). On an install without
    /// PerfMon those "opted-out" panels still connected to the (missing) PerformanceMonitor
    /// database and rendered a raw SQL login failure instead of their own data.</para>
    ///
    /// <para>The fix is this one decision, extracted so it can be tested exhaustively without a
    /// live database or a Blazor render: an opted-out panel (<c>requiresDatabase == ""</c>) always
    /// runs against its own resolved default database; every other panel (requiresDatabase unset —
    /// inherits — or a distinct non-empty value, not independently resolved yet) keeps exactly
    /// today's override behaviour.</para>
    /// </summary>
    public static class DashboardDatabaseResolver
    {
        /// <summary>
        /// Resolves the database a panel's query should connect to.
        /// </summary>
        /// <param name="panelRequiresDatabase">
        /// The panel's own raw <c>requiresDatabase</c> value (see
        /// <see cref="Models.PanelDefinition.RequiresDatabase"/>). <c>null</c> = unset, inherits the
        /// dashboard's gate. <c>""</c> = explicit opt-out. Any other value = a distinct requirement,
        /// not independently resolved — treated the same as inherit.
        /// </param>
        /// <param name="panelEffectiveDefaultDatabase">
        /// The panel's own resolved default database (its own <c>defaultDatabase</c>, falling back
        /// to the owning dashboard's <c>defaultDatabase</c> — see
        /// <see cref="DashboardConfigService.GetEffectiveDefaultDatabase"/>). Used whenever the
        /// panel is opted out, or when there is no override to apply.
        /// </param>
        /// <param name="currentServerOverrideDatabase">
        /// <see cref="ServerConnectionManager.CurrentServer"/>'s Database at call time — set by,
        /// e.g., a dashboard forcing its own DefaultDatabase on load, or the Query Store database
        /// selector. <c>null</c>/empty, or exactly "master", means there is nothing to override with
        /// (the dashboard has no forced default, or the override was never pointed anywhere but the
        /// default catalog).
        /// </param>
        /// <returns>The database name to connect to for this query.</returns>
        public static string Resolve(
            string? panelRequiresDatabase,
            string panelEffectiveDefaultDatabase,
            string? currentServerOverrideDatabase)
        {
            var optedOut = panelRequiresDatabase == string.Empty;

            if (!optedOut &&
                !string.IsNullOrEmpty(currentServerOverrideDatabase) &&
                !string.Equals(currentServerOverrideDatabase, "master", StringComparison.Ordinal))
            {
                return currentServerOverrideDatabase;
            }

            return panelEffectiveDefaultDatabase;
        }
    }
}
