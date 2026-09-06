/* In the name of God, the Merciful, the Compassionate */

using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Exhaustive coverage of <see cref="DashboardDatabaseResolver.Resolve"/>, the seam extracted
    /// from a live-verify-refuted defect: <c>QueryExecutor</c> let
    /// <c>ServerConnectionManager.CurrentServer.Database</c> (force-set to a dashboard's
    /// <c>DefaultDatabase</c> on load, e.g. "PerformanceMonitor") override EVERY panel's own
    /// database, including panels that opted out via <c>requiresDatabase: ""</c> specifically so
    /// they would run against their own catalog (e.g. "master") on installs without PerfMon. The
    /// result was a raw SQL login-failure banner on 8 panels that were supposed to render natively.
    ///
    /// <para>This is a config-lint-class instrument: it proves the DECISION function, not a live
    /// render. It cannot see whether <c>QueryExecutor</c> actually calls
    /// <see cref="DashboardDatabaseResolver.Resolve"/> with the right arguments, or whether a panel
    /// renders end to end — those are proven by the QueryExecutor call sites (read) and the live
    /// re-verify (rendered), respectively.</para>
    /// </summary>
    public class DashboardDatabaseResolverTests
    {
        private const string PanelOwnDb = "master";

        // ── Opted out (requiresDatabase == "") — NEVER overridden, regardless of the dashboard
        //    forcing a default or the Query Store selector pointing elsewhere. This is the fix. ──

        [Fact]
        public void OptedOut_dashboard_default_absent_uses_own_database()
        {
            var result = DashboardDatabaseResolver.Resolve(
                panelRequiresDatabase: "",
                panelEffectiveDefaultDatabase: PanelOwnDb,
                currentServerOverrideDatabase: null);

            Assert.Equal(PanelOwnDb, result);
        }

        [Fact]
        public void OptedOut_dashboard_default_present_still_uses_own_database()
        {
            // This is the exact shape the verify pass reproduced: memory/pevents force
            // CurrentServer.Database = "PerformanceMonitor" on load, and the opted-out panel
            // must ignore it.
            var result = DashboardDatabaseResolver.Resolve(
                panelRequiresDatabase: "",
                panelEffectiveDefaultDatabase: PanelOwnDb,
                currentServerOverrideDatabase: "PerformanceMonitor");

            Assert.Equal(PanelOwnDb, result);
        }

        [Fact]
        public void OptedOut_override_exactly_master_still_uses_own_database()
        {
            var result = DashboardDatabaseResolver.Resolve(
                panelRequiresDatabase: "",
                panelEffectiveDefaultDatabase: PanelOwnDb,
                currentServerOverrideDatabase: "master");

            Assert.Equal(PanelOwnDb, result);
        }

        // ── Inherit (requiresDatabase == null) — keeps exactly today's override behaviour. ──

        [Fact]
        public void Inherit_dashboard_default_absent_uses_own_database()
        {
            var result = DashboardDatabaseResolver.Resolve(
                panelRequiresDatabase: null,
                panelEffectiveDefaultDatabase: PanelOwnDb,
                currentServerOverrideDatabase: null);

            Assert.Equal(PanelOwnDb, result);
        }

        [Fact]
        public void Inherit_dashboard_default_present_is_overridden()
        {
            var result = DashboardDatabaseResolver.Resolve(
                panelRequiresDatabase: null,
                panelEffectiveDefaultDatabase: PanelOwnDb,
                currentServerOverrideDatabase: "PerformanceMonitor");

            Assert.Equal("PerformanceMonitor", result);
        }

        [Fact]
        public void Inherit_override_exactly_master_is_not_treated_as_an_override()
        {
            // "master" is the default fallback, not a real Query Store / dashboard-forced
            // selection — matches QueryExecutor's original `currentDb != "master"` guard.
            var result = DashboardDatabaseResolver.Resolve(
                panelRequiresDatabase: null,
                panelEffectiveDefaultDatabase: PanelOwnDb,
                currentServerOverrideDatabase: "master");

            Assert.Equal(PanelOwnDb, result);
        }

        [Fact]
        public void Inherit_override_empty_string_is_not_treated_as_an_override()
        {
            var result = DashboardDatabaseResolver.Resolve(
                panelRequiresDatabase: null,
                panelEffectiveDefaultDatabase: PanelOwnDb,
                currentServerOverrideDatabase: "");

            Assert.Equal(PanelOwnDb, result);
        }

        // ── Own requirement (requiresDatabase == a distinct non-empty value) — per
        //    PanelDefinition.RequiresDatabase's doc comment this is "accepted but not
        //    independently resolved", so it must behave exactly like inherit, not like opt-out. ──

        [Fact]
        public void OwnRequirement_dashboard_default_absent_uses_own_database()
        {
            var result = DashboardDatabaseResolver.Resolve(
                panelRequiresDatabase: "distribution",
                panelEffectiveDefaultDatabase: PanelOwnDb,
                currentServerOverrideDatabase: null);

            Assert.Equal(PanelOwnDb, result);
        }

        [Fact]
        public void OwnRequirement_dashboard_default_present_is_overridden_same_as_inherit()
        {
            var result = DashboardDatabaseResolver.Resolve(
                panelRequiresDatabase: "distribution",
                panelEffectiveDefaultDatabase: PanelOwnDb,
                currentServerOverrideDatabase: "PerformanceMonitor");

            Assert.Equal("PerformanceMonitor", result);
        }

        [Theory]
        [InlineData("QueryStoreDb")] // Query Store's own database-selector case: a real, non-master override
        [InlineData("Northwind")]
        public void Inherit_a_real_query_store_selection_is_honoured(string selectedDb)
        {
            var result = DashboardDatabaseResolver.Resolve(
                panelRequiresDatabase: null,
                panelEffectiveDefaultDatabase: "master",
                currentServerOverrideDatabase: selectedDb);

            Assert.Equal(selectedDb, result);
        }
    }

    /// <summary>
    /// <see cref="QueryExecutor.ScrubExceptionMessage"/> already scrubbed connection-string-style
    /// credentials; the verify pass found a distinct leak the pattern didn't cover — SQL Server's
    /// own "Login failed for user 'X'" text, which on a Windows/AD login embeds the OS account name
    /// (observed: "Login failed for user 'MSI\afsul'") and rendered straight into a dashboard panel
    /// warning. <c>internal</c>, exercised via <c>InternalsVisibleTo</c> (see AssemblyInfo.cs).
    /// </summary>
    public class QueryExecutorScrubTests
    {
        [Fact]
        public void Login_failed_username_is_redacted()
        {
            var message = "Cannot open database \"PerformanceMonitor\" requested by the login. " +
                           "The login failed.\r\nLogin failed for user 'MSI\\afsul'.";

            var scrubbed = QueryExecutor.ScrubExceptionMessage(message);

            Assert.DoesNotContain("MSI\\afsul", scrubbed);
            Assert.Contains("Login failed for user '********'", scrubbed);
            // The rest of the message (which failure this is) survives — only the account name goes.
            Assert.Contains("Cannot open database \"PerformanceMonitor\"", scrubbed);
        }

        [Fact]
        public void Login_failed_message_with_no_username_quote_is_left_alone()
        {
            var message = "A network-related or instance-specific error occurred.";

            var scrubbed = QueryExecutor.ScrubExceptionMessage(message);

            Assert.Equal(message, scrubbed);
        }

        [Fact]
        public void Connection_string_credentials_are_still_scrubbed()
        {
            // Pre-existing coverage gap: this pattern was never under test before this fix round.
            var message = "Connection failed: Server=tcp:x;Password=hunter2;User Id=sa;";

            var scrubbed = QueryExecutor.ScrubExceptionMessage(message);

            Assert.DoesNotContain("hunter2", scrubbed);
            Assert.DoesNotContain("User Id=sa", scrubbed);
        }
    }
}
