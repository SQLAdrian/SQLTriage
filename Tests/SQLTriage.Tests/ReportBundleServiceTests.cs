using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Tests for ReportBundleService HTML composition.
    /// ReportBundleService depends on ExecutiveHealthService, HealthCheckService,
    /// VulnerabilityAssessmentStateService, and UserSettingsService.
    /// ExecutiveHealthService in turn pulls from GovernanceHistoryService,
    /// BlockingHistoryService, HistoricalPerformanceService, and VulnerabilityAssessmentStateService.
    /// All are constructed with empty/default state and temp directories.
    ///
    /// HTML generation is pure (no DB writes from the bundle service itself), so tests
    /// assert structure (tag/keyword presence) not exact content.
    /// </summary>
    public class ReportBundleServiceTests : IDisposable
    {
        private readonly string _tempDir;

        public ReportBundleServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "reportbundle-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup; ignore */ }
        }

        private ReportBundleService NewService()
        {
            var dbPath = Path.Combine(_tempDir, "governance-history.db");

            var vaState = new VulnerabilityAssessmentStateService();
            var userSettings = new UserSettingsService();
            var blockingHistory = new BlockingHistoryService(
                NullLogger<BlockingHistoryService>.Instance,
                retentionDays: 30,
                dbPath: Path.Combine(_tempDir, "blocking-history.db"));
            var perfHistory = new HistoricalPerformanceService(
                NullLogger<HistoricalPerformanceService>.Instance,
                dbPath: dbPath);
            var govHistory = new GovernanceHistoryService(
                NullLogger<GovernanceHistoryService>.Instance,
                retentionDays: 365,
                dbDir: _tempDir);
            var connectionFactory = new NullDbConnectionFactory();
            var healthCheckSvc = new HealthCheckService(connectionFactory);

            var executiveHealth = new ExecutiveHealthService(
                healthCheckSvc,
                govHistory,
                blockingHistory,
                perfHistory,
                vaState,
                NullLogger<ExecutiveHealthService>.Instance);

            return new ReportBundleService(
                executiveHealth,
                healthCheckSvc,
                vaState,
                userSettings,
                NullLogger<ReportBundleService>.Instance,
                auditLog: null);
        }

        // ── PrepareExecutiveSummaryHtml ───────────────────────────────────────

        [Fact]
        public async Task PrepareExecutiveSummaryHtml_ContainsHealthSection()
        {
            var svc = NewService();
            var html = await svc.PrepareExecutiveSummaryHtmlAsync("test-server");

            Assert.Contains("Overall Health Score", html, StringComparison.OrdinalIgnoreCase);
            // Ensure the HTML is non-trivially populated
            Assert.True(html.Length > 200, $"HTML too short ({html.Length} chars)");
        }

        // ── PrepareDbaHandoffHtml ─────────────────────────────────────────────

        [Fact]
        public async Task PrepareDbaHandoffHtml_IncludesAllSections()
        {
            var svc = NewService();
            var html = await svc.PrepareDbaHandoffHtmlAsync("test-server");

            // All four mandated sections
            Assert.Contains("Server Inventory", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("All Vulnerability Assessment Findings", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Known Issues", html, StringComparison.OrdinalIgnoreCase);
        }

        // ── PrepareAuditEvidenceHtml ──────────────────────────────────────────

        [Fact]
        public async Task PrepareAuditEvidenceHtml_IncludesChainStatus()
        {
            var svc = NewService();
            var html = await svc.PrepareAuditEvidenceHtmlAsync("test-server");

            // Chain status is always rendered (N/A when no AuditLogService provided)
            Assert.Contains("HMAC Chain Status", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Audit Evidence", html, StringComparison.OrdinalIgnoreCase);
        }

        // ── PendingHtml is stored ─────────────────────────────────────────────

        [Fact]
        public async Task PrepareExecutiveSummaryHtml_StoresInPendingHtml()
        {
            var svc = NewService();
            var html = await svc.PrepareExecutiveSummaryHtmlAsync("test-server");

            Assert.True(svc.PendingHtml.ContainsKey("test-server|ExecutiveSummary"),
                "HTML should be stored under 'server|ExecutiveSummary' key.");
            Assert.Equal(html, svc.PendingHtml["test-server|ExecutiveSummary"]);
        }
    }

    /// <summary>
    /// Minimal IDbConnectionFactory that always throws (no live connections needed in these tests).
    /// </summary>
    internal sealed class NullDbConnectionFactory : IDbConnectionFactory
    {
        public string DataSourceType => "none";

        public System.Data.IDbConnection CreateConnection()
            => throw new InvalidOperationException("No SQL connection available in unit tests.");

        public System.Threading.Tasks.Task<System.Data.IDbConnection> CreateConnectionAsync()
            => throw new InvalidOperationException("No SQL connection available in unit tests.");
    }
}
