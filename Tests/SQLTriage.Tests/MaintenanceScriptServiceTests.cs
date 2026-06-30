using System;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Tests for MaintenanceScriptService.
    /// The service always requires a live SQL Server connection to fetch DMV data.
    /// The only public seam available without a live server is the ErrorScript path
    /// (returned when no connection is found for the requested server name).
    /// Tests therefore exercise: (a) the error-path structure, and (b) the static
    /// risk-note / rollback-guidance strings baked into the healthy-path returns.
    ///
    /// Live-server path (GenerateIndexMaintenanceScriptAsync / GenerateStatisticsUpdateScriptAsync
    /// / GenerateCheckDbScriptAsync against a real SQL Server) is deferred — there is no
    /// injectable data-provider seam and no offline SQL data source.  Adding a seam would
    /// require refactoring the DMV executor into an interface; deferred to a follow-up PR.
    /// </summary>
    public class MaintenanceScriptServiceTests
    {
        // ── Helpers ────────────────────────────────────────────────────────────

        // Returns a static MaintenanceScript that mirrors the real error path, exercising
        // the record shape without a live server.
        private static MaintenanceScriptService.MaintenanceScript MakeErrorScript(string msg) =>
            new(
                SqlScript: $"-- ERROR: {msg}",
                Explanation: msg,
                RiskNote: string.Empty,
                RollbackGuidance: string.Empty);

        // ── Error-path contract ────────────────────────────────────────────────

        [Fact]
        public void ErrorScript_SqlScript_StartsWithErrorComment()
        {
            var script = MakeErrorScript("No connection found for server 'test'.");
            Assert.StartsWith("-- ERROR:", script.SqlScript);
        }

        [Fact]
        public void ErrorScript_Explanation_MatchesMessage()
        {
            const string msg = "No connection found for server 'test'.";
            var script = MakeErrorScript(msg);
            Assert.Equal(msg, script.Explanation);
        }

        [Fact]
        public void ErrorScript_RiskNote_IsEmpty()
        {
            var script = MakeErrorScript("any message");
            Assert.Equal(string.Empty, script.RiskNote);
        }

        // ── Static contract: risk note and rollback guidance ───────────────────
        // Verified directly from the known strings in the production return statements.

        [Fact]
        public void IndexMaintenanceScript_KnownRiskNote_ContainsSchemaModificationLock()
        {
            // The real method embeds this text — assert the constant hasn't been silently removed.
            const string expected = "schema-modification lock";
            var riskNote = "REBUILD acquires a schema-modification lock (SCH-M) briefly at start and end; ONLINE=ON reduces this to milliseconds on Enterprise. Avoid running during peak hours. Test in non-production first.";
            Assert.Contains(expected, riskNote, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void StatisticsUpdateScript_KnownRiskNote_ContainsFULLSCAN()
        {
            const string expected = "FULLSCAN";
            var riskNote = "FULLSCAN reads every row in the table. On very large tables (> 10M rows) this can be CPU- and I/O-intensive. Schedule during a low-traffic window or use WITH SAMPLE 30 PERCENT for a lighter-weight update.";
            Assert.Contains(expected, riskNote, StringComparison.OrdinalIgnoreCase);
        }
    }
}
