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
    /// Tests for ExecutiveHealthService.
    /// Uses empty in-process state (no live SQL Server, no historical data).
    /// When a dimension has no data, the service awards full points by default —
    /// so a fresh instance with no connections and no VA results must return 100.
    /// </summary>
    public class ExecutiveHealthServiceTests : IDisposable
    {
        private readonly string _tempDir;

        public ExecutiveHealthServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "exechealth-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup; ignore */ }
        }

        private ExecutiveHealthService NewService(VulnerabilityAssessmentStateService? vaState = null)
        {
            var dbPath = Path.Combine(_tempDir, "governance-history.db");
            var vaStateSvc = vaState ?? new VulnerabilityAssessmentStateService();
            var blockingHistory = new BlockingHistoryService(
                NullLogger<BlockingHistoryService>.Instance,
                dbPath: Path.Combine(_tempDir, "blocking-history.db"));
            var perfHistory = new HistoricalPerformanceService(
                NullLogger<HistoricalPerformanceService>.Instance,
                dbPath: dbPath);
            var govHistory = new GovernanceHistoryService(
                NullLogger<GovernanceHistoryService>.Instance,
                dbDir: _tempDir);
            var healthCheckSvc = new HealthCheckService(new StubDbConnectionFactory());

            return new ExecutiveHealthService(
                healthCheckSvc,
                govHistory,
                blockingHistory,
                perfHistory,
                vaStateSvc,
                NullLogger<ExecutiveHealthService>.Instance);
        }

        // ── No data → NOT assessed (not a fake 100) ──────────────────────────

        [Fact]
        public async Task GetHealthAsync_NoDatabaseNoVaResults_IsNotAssessed()
        {
            // Corrected contract (2026-05-18): with no historical data and no VA
            // results, NO dimension has real data — composite must NOT read as a
            // perfect 100 (that overclaimed health for unmonitored servers, the
            // reported bug). It is unassessed: IsAssessed false, score 0 sentinel.
            var svc = NewService();
            var result = await svc.GetHealthScoreAsync("test-server");

            Assert.False(result.IsAssessed);
            Assert.Equal(0, result.Score);
        }

        // ── Security dimension drops when critical VA findings present ────────

        [Fact]
        public async Task GetHealthAsync_CriticalVulnerabilities_LowersSecurityDimension()
        {
            var vaState = new VulnerabilityAssessmentStateService();

            // Inject 10 Security findings, all Failed + Critical
            for (int i = 0; i < 10; i++)
            {
                vaState.Results.Add(new AssessmentResult
                {
                    CheckId = $"SEC-{i:000}",
                    DisplayName = $"Security check {i}",
                    Category = "Security",
                    Status = "Failed",
                    Severity = "Critical",
                    // Security/Compliance dims are per-server (2026-06-11): results
                    // must carry the server identity or they're filtered out.
                    ThisServer = "test-server",
                });
            }

            var svc = NewService(vaState);
            var result = await svc.GetHealthScoreAsync("test-server");

            // Security dimension raw pass rate = 0%, minus 3 pts per critical (10 criticals → −30).
            // Clamped to 0. Composite score < 100.
            Assert.True(result.Score < 100,
                $"Expected score < 100 with critical VA findings, got {result.Score}.");

            var secDim = result.Breakdown.Security;
            Assert.True(secDim.Score < 100,
                $"Security dimension score should be < 100 but got {secDim.Score}.");
        }

        // ── No yesterday snapshot → flat (stable) trend ──────────────────────

        [Fact]
        public async Task GetHealthAsync_NoYesterdaySnapshot_ReturnsFlatTrend()
        {
            // Fresh DB: no prior snapshots → trend defaults to Stable.
            var svc = NewService();
            var result = await svc.GetHealthScoreAsync("trend-server");

            Assert.Equal(HealthTrend.Stable, result.Trend);
        }
    }

    /// <summary>
    /// Stub IDbConnectionFactory — no real connections, health cache starts empty.
    /// </summary>
    internal sealed class StubDbConnectionFactory : IDbConnectionFactory
    {
        public string DataSourceType => "stub";

        public System.Data.IDbConnection CreateConnection()
            => throw new InvalidOperationException("No SQL connection in unit tests.");

        public System.Threading.Tasks.Task<System.Data.IDbConnection> CreateConnectionAsync()
            => throw new InvalidOperationException("No SQL connection in unit tests.");
    }
}
