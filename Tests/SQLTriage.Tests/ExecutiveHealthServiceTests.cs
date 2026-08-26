/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
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

        // ── VA-only data must NOT feed Executive Health (2026-07-16 honesty ruling) ────────

        [Fact]
        public async Task GetHealthAsync_VaCriticalFindingsOnly_SecurityStaysNotAssessed()
        {
            // Honesty ruling (2026-07-16, Builder A): Executive Health is corpus-fed ONLY.
            // This used to be "GetHealthAsync_CriticalVulnerabilities_LowersSecurityDimension"
            // and asserted that critical VA findings dragged the Security dimension down via
            // the VA-fallback path. That path is gone — VA now lives only on its own page
            // (and the gated Audit Evidence export) and must never move Executive Health,
            // even when the findings are critical failures.
            var vaState = new VulnerabilityAssessmentStateService();

            // Inject 10 Security findings, all Failed + Critical — this used to drag the
            // Security dimension score down. It must not anymore: with no corpus check
            // results for this server, Security stays honestly "not yet assessed".
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

            var secDim = result.Breakdown.Security;
            Assert.False(secDim.HasData,
                "Security must stay unassessed when only VA data exists (no corpus results) — VA no longer feeds Executive Health.");
            Assert.Equal(100, secDim.Score); // not-assessed default — never a VA-derived number
            Assert.False(result.IsAssessed);
            Assert.Equal(0, result.Score);
        }

        // ── The conditioning sweep, 2026-08-05: the real service, driven ─────
        // EstateHealthPolicyTests exercise the model with hand-built fixtures. These two drive
        // the SHIPPED ExecutiveHealthService, so the fixture cannot be the thing that passes.

        [Fact]
        public async Task GetHealthAsync_NothingMeasured_SeverityIsUnknownNotCritical()
        {
            // Severity is a verdict. With no measured dimension the composite is a forced 0, and
            // ScoreToSeverity(0) used to stamp the server "Critical - requires immediate action"
            // on the strength of nothing measured at all.
            var svc = NewService();
            var result = await svc.GetHealthScoreAsync("cold-server");

            Assert.Equal(HealthAssessmentState.NotAssessed, result.State);
            Assert.Equal(HealthSeverity.Unknown, result.Severity);
            Assert.Null(result.MeasuredScore);
            Assert.Equal("", result.Message);
            Assert.False(EstateHealthPolicy.MayPublish(result),
                "the portal must omit the health block, not publish a placeholder");
        }

        [Fact]
        public async Task GetHealthAsync_ServerDidNotAnswer_ResourceIsUnreachableAndTheServerIsNotAssessed()
        {
            // Drives the ROOT CAUSE branch through the real service: a cached health entry whose
            // poll never came back online. Before the fix, ScoreResource's offline branch omitted
            // the state argument, so this same call returned IsAssessed TRUE carrying a fabricated
            // Resource 0 — into the CIO estate mean, the assessed-server count, the /dba card and
            // the portal daily summary.
            var health = new HealthCheckService(new StubDbConnectionFactory());
            // The stub factory throws, so the poll records the server without ever setting
            // IsOnline true. That is exactly the state the offline branch reads.
            await health.GetHealthStatusAsync("silent-server");
            Assert.NotNull(health.GetCachedHealth("silent-server"));
            Assert.NotEqual(true, health.GetCachedHealth("silent-server")!.IsOnline);

            var dbPath = Path.Combine(_tempDir, "governance-history.db");
            var svc = new ExecutiveHealthService(
                health,
                new GovernanceHistoryService(NullLogger<GovernanceHistoryService>.Instance, dbDir: _tempDir),
                new BlockingHistoryService(NullLogger<BlockingHistoryService>.Instance,
                    dbPath: Path.Combine(_tempDir, "blocking-history.db")),
                new HistoricalPerformanceService(NullLogger<HistoricalPerformanceService>.Instance, dbPath: dbPath),
                new VulnerabilityAssessmentStateService(),
                NullLogger<ExecutiveHealthService>.Instance);

            var result = await svc.GetHealthScoreAsync("silent-server");

            Assert.Equal(DimensionState.Unreachable, result.Breakdown.Resource.State);
            Assert.False(result.Breakdown.Resource.HasData);
            Assert.Null(result.Breakdown.Resource.MeasuredScore);
            Assert.False(result.IsAssessed, "this is the assertion that failed before the fix");
            Assert.Equal(HealthAssessmentState.Unreachable, result.State);
            Assert.Equal(HealthSeverity.Unknown, result.Severity);
            Assert.Null(result.MeasuredScore);
            Assert.False(EstateHealthPolicy.MayPublish(result),
                "the publisher guard read IsAssessed and let this exact object through");

            // And the sentence every surface prints for it.
            Assert.Equal("This server did not answer, so no dimension was measured.",
                EstateHealthPolicy.ServerBasis(result));
            Assert.Equal("n/a", EstateHealthPolicy.DimensionValue(result.Breakdown.Resource));

            // The estate mean is unaffected by a server nothing measured.
            var rollup = EstateHealthPolicy.Summarise(new[] { result });
            Assert.Null(rollup.MeanScore);
            Assert.Equal(1, rollup.UnreachableCount);
            Assert.Equal(0, rollup.AssessedCount);
        }

        [Fact]
        public async Task GetHealthAsync_FirstPollStillInFlight_IsNotAssessedNotUnreachable()
        {
            // Gate fix R11. The offline branch caught IsOnline null as well as false, so a server
            // the app was still mid-handshake with was reported as one that "did not answer".
            // HealthCheckService creates the cache row and sets IsLoading before it opens the
            // connection, which is the window reproduced here.
            var health = new HealthCheckService(new StubDbConnectionFactory());
            await health.GetHealthStatusAsync("in-flight-server");

            var cached = health.GetCachedHealth("in-flight-server")!;
            cached.IsOnline = null;
            cached.IsLoading = true;   // the poll is out and has not come back

            var dbPath = Path.Combine(_tempDir, "governance-history.db");
            var svc = new ExecutiveHealthService(
                health,
                new GovernanceHistoryService(NullLogger<GovernanceHistoryService>.Instance, dbDir: _tempDir),
                new BlockingHistoryService(NullLogger<BlockingHistoryService>.Instance,
                    dbPath: Path.Combine(_tempDir, "blocking-history.db")),
                new HistoricalPerformanceService(NullLogger<HistoricalPerformanceService>.Instance, dbPath: dbPath),
                new VulnerabilityAssessmentStateService(),
                NullLogger<ExecutiveHealthService>.Instance);

            var result = await svc.GetHealthScoreAsync("in-flight-server");

            Assert.Equal(DimensionState.NotAssessed, result.Breakdown.Resource.State);
            Assert.DoesNotContain("did not answer", result.Breakdown.Resource.Summary);
            Assert.DoesNotContain("did not answer", result.Breakdown.Resource.Tooltip);
            Assert.DoesNotContain("did not answer", EstateHealthPolicy.DimensionBasis(result.Breakdown.Resource));
            Assert.Equal(HealthAssessmentState.NotAssessed, result.State);
            Assert.Null(result.MeasuredScore);
        }

        [Fact]
        public async Task GetHealthAsync_PollFinishedWithoutAnAnswer_StillReadsUnreachable()
        {
            // The other half of R11: a poll that has FINISHED with IsOnline still null threw on
            // the way out. That IS a real negative observation and keeps the unreachable wording.
            var health = new HealthCheckService(new StubDbConnectionFactory());
            await health.GetHealthStatusAsync("finished-silent-server");

            var cached = health.GetCachedHealth("finished-silent-server")!;
            Assert.False(cached.IsLoading, "the poll completed");

            var dbPath = Path.Combine(_tempDir, "governance-history.db");
            var svc = new ExecutiveHealthService(
                health,
                new GovernanceHistoryService(NullLogger<GovernanceHistoryService>.Instance, dbDir: _tempDir),
                new BlockingHistoryService(NullLogger<BlockingHistoryService>.Instance,
                    dbPath: Path.Combine(_tempDir, "blocking-history.db")),
                new HistoricalPerformanceService(NullLogger<HistoricalPerformanceService>.Instance, dbPath: dbPath),
                new VulnerabilityAssessmentStateService(),
                NullLogger<ExecutiveHealthService>.Instance);

            var result = await svc.GetHealthScoreAsync("finished-silent-server");

            Assert.Equal(DimensionState.Unreachable, result.Breakdown.Resource.State);
        }

        // ── No yesterday snapshot → flat (stable) trend ──────────────────────

        [Fact]
        public async Task GetHealthAsync_NoYesterdaySnapshot_ReturnsFlatTrend()
        {
            // Fresh DB: no prior snapshots. The DIRECTION field still defaults to Stable — it has to
            // hold one of three values — but SR-11 (2026-08-08) is that the direction was the only
            // thing returned, so every surface printed "Stable" for a server with no yesterday.
            var svc = NewService();
            var result = await svc.GetHealthScoreAsync("trend-server");

            Assert.Equal(HealthTrend.Stable, result.Trend);

            // The part that decides what may be PRINTED.
            Assert.Equal(HealthTrendState.NoPriorSnapshot, result.TrendState);
            Assert.Null(result.MeasuredTrend);
            Assert.Equal("No trend yet", EstateHealthPolicy.TrendValue(result));
        }

        /// <summary>
        /// The self-comparison, found 2026-08-08 while rendering the SR-11 fix. Snapshots are keyed
        /// by DATE and one is written on the first assessment of each UTC day, so an unbounded "on or
        /// after yesterday" query starts returning TODAY'S OWN ROW from that moment — and the trend
        /// compared the score with itself, arithmetic diff 0, "Stable", for the rest of the day.
        /// Fixing only the rendering would have shipped that as a genuine comparison.
        /// </summary>
        [Fact]
        public async Task TodaysOwnSnapshotIsNotAComparison_AndTheUnboundedQueryProvesTheBoundIsDoingIt()
        {
            var history = new GovernanceHistoryService(
                NullLogger<GovernanceHistoryService>.Instance, dbDir: _tempDir);

            history.RecordHealthScore("self-compare", 74, new HealthScoreBreakdown());

            var yesterday = DateTime.UtcNow.Date.AddDays(-1);
            var today = DateTime.UtcNow.Date;

            // THE FIX: a day-over-day window excludes today, so a history containing only today has
            // nothing to compare against.
            Assert.Null(await history.GetLatestHealthScoreAsync("self-compare", yesterday, beforeDate: today));

            // NEGATIVE CONTROL: the unbounded call — the shape the trend used to make, and the shape
            // ResolveComposite still wants — does return it. If this ever stops, the assertion above
            // is passing for the wrong reason (an empty store) and proves nothing.
            Assert.Equal(74, await history.GetLatestHealthScoreAsync("self-compare", yesterday));
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
