using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class PerformanceBaselineServiceTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _dbPath;

        public PerformanceBaselineServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "perfbaseline-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _dbPath = Path.Combine(_tempDir, "governance-history.db");
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup; ignore */ }
        }

        private HistoricalPerformanceService NewHistorical() =>
            new(NullLogger<HistoricalPerformanceService>.Instance, dbPath: _dbPath);

        private PerformanceBaselineService NewBaseline(HistoricalPerformanceService historical) =>
            new(NullLogger<PerformanceBaselineService>.Instance, historical, dbPath: _dbPath);

        /// <summary>
        /// Seeds hourly rows directly into wait_stats_hourly so LearnBaselineAsync has data.
        /// </summary>
        private void SeedHourlyRows(string server, string waitType, int samples, double avgMs)
        {
            var connStr = $"Data Source={_dbPath};Mode=ReadWriteCreate;Cache=Shared";
            using var conn = SqliteCipherHelper.OpenEncrypted(connStr);

            // Schema may not exist yet if HistoricalPerformanceService hasn't been called.
            using var schema = conn.CreateCommand();
            schema.CommandText = @"
                CREATE TABLE IF NOT EXISTS wait_stats_hourly (
                    server_name      TEXT    NOT NULL,
                    hour_utc         TEXT    NOT NULL,
                    wait_type        TEXT    NOT NULL,
                    avg_wait_time_ms REAL    NOT NULL,
                    max_wait_time_ms REAL    NOT NULL,
                    sample_count     INTEGER NOT NULL,
                    PRIMARY KEY (server_name, hour_utc, wait_type)
                );";
            schema.ExecuteNonQuery();

            // Anchor to the current truncated hour, then step back in 7-day strides so
            // every seeded row shares the SAME (day-of-week, hour-of-day) bucket and
            // accumulates >= MinBucketSamples (=3) in that single bucket — which is what
            // LearnBaselineAsync actually requires. Staying inside the 30-day lookback
            // (i=1..4 → −7..−28 days) keeps >=4 rows in range. The shared bucket key
            // also equals the outlier's current-hour key used by the anomaly test.
            var anchorHour = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
            for (int i = 0; i < samples; i++)
            {
                using var cmd = conn.CreateCommand();
                var hourUtc = anchorHour.AddDays(-7 * (i + 1));   // same weekday + hour, weekly back
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO wait_stats_hourly
                        (server_name, hour_utc, wait_type, avg_wait_time_ms, max_wait_time_ms, sample_count)
                    VALUES (@s, @h, @w, @avg, @max, @sc)";
                cmd.Parameters.AddWithValue("@s", server);
                cmd.Parameters.AddWithValue("@h", hourUtc.ToString("o"));
                cmd.Parameters.AddWithValue("@w", waitType);
                cmd.Parameters.AddWithValue("@avg", avgMs);
                cmd.Parameters.AddWithValue("@max", avgMs * 2);
                cmd.Parameters.AddWithValue("@sc", 3);
                cmd.ExecuteNonQuery();
            }
        }

        // ── DetectAnomalies_NoBaseline_ReturnsEmpty ──────────────────────────

        [Fact]
        public async Task DetectAnomalies_NoBaselineReturnsEmpty()
        {
            using var hist = NewHistorical();
            using var svc = NewBaseline(hist);

            var anomalies = await svc.DetectAnomaliesAsync(
                "srv-nobaseline", "PAGEIOLATCH_SH",
                DateTime.UtcNow.AddHours(-24), DateTime.UtcNow);

            Assert.Empty(anomalies);
        }

        // ── LearnBaseline_ComputesMeanAndStdDev ──────────────────────────────

        [Fact]
        public async Task LearnBaseline_ComputesMeanAndStdDev()
        {
            // Seed 7 hourly rows at 100 ms each — same wait type, one per day (same hour-of-day).
            // After learning, the bucket for that (day-of-week, hour-of-day) should have mean ~100,
            // stddev ~0 (all values identical).
            const double expectedAvg = 100.0;
            const string server = "srv-learn";
            const string waitType = "SOS_SCHEDULER_YIELD";

            using var hist = NewHistorical();
            SeedHourlyRows(server, waitType, samples: 7, avgMs: expectedAvg);

            using var svc = NewBaseline(hist);
            var bucketsWritten = await svc.LearnBaselineAsync(server, waitType, lookbackDays: 30);

            Assert.True(bucketsWritten > 0, "Expected at least one bucket to be learned.");

            var baseline = svc.GetBaseline(server, waitType);
            Assert.NotEmpty(baseline);

            foreach (var b in baseline)
            {
                Assert.InRange(b.MeanWaitMs, 99.0, 101.0);
                // stddev should be near 0 when all samples are identical
                Assert.True(b.StdDevWaitMs < 1.0,
                    $"Expected near-zero stddev but got {b.StdDevWaitMs}");
            }
        }

        // ── DetectAnomalies_FlagsOutlier ─────────────────────────────────────

        [Fact]
        public async Task DetectAnomalies_FlagsZScoreAboveThreshold()
        {
            // Seed a baseline of 100 ms for each of 7 days (same hour slot).
            // Then add one outlier at 5000 ms in the hourly table — z-score will be huge.
            const string server = "srv-anomaly";
            const string waitType = "CXPACKET";

            using var hist = NewHistorical();
            SeedHourlyRows(server, waitType, samples: 7, avgMs: 100.0);

            // Learn baseline first
            using var svc = NewBaseline(hist);
            await svc.LearnBaselineAsync(server, waitType, lookbackDays: 30);

            // Inject a massive outlier into the hourly table for today's current hour
            var connStr = $"Data Source={_dbPath};Mode=ReadWriteCreate;Cache=Shared";
            var outlierHour = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
            using (var conn = SqliteCipherHelper.OpenEncrypted(connStr))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO wait_stats_hourly
                        (server_name, hour_utc, wait_type, avg_wait_time_ms, max_wait_time_ms, sample_count)
                    VALUES (@s, @h, @w, 5000.0, 5000.0, 1)";
                cmd.Parameters.AddWithValue("@s", server);
                cmd.Parameters.AddWithValue("@h", outlierHour.ToString("o"));
                cmd.Parameters.AddWithValue("@w", waitType);
                cmd.ExecuteNonQuery();
            }

            var anomalies = await svc.DetectAnomaliesAsync(
                server, waitType,
                outlierHour.AddMinutes(-30), outlierHour.AddHours(2),
                zScoreThreshold: 2.0);

            // At least one anomaly should be returned with z-score well above 2
            Assert.NotEmpty(anomalies);
            Assert.All(anomalies, a => Assert.True(Math.Abs(a.ZScore) >= 2.0));
        }
    }
}
