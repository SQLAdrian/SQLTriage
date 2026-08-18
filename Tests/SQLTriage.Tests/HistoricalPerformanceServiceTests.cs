using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class HistoricalPerformanceServiceTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _dbPath;

        public HistoricalPerformanceServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "histperf-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _dbPath = Path.Combine(_tempDir, "governance-history.db");
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup; ignore */ }
        }

        private HistoricalPerformanceService NewService(
            int rawRetentionDays = 14,
            int hourlyRetentionDays = 90,
            int dailyRetentionDays = 365)
            => new(NullLogger<HistoricalPerformanceService>.Instance,
                   rawRetentionDays: rawRetentionDays,
                   hourlyRetentionDays: hourlyRetentionDays,
                   dailyRetentionDays: dailyRetentionDays,
                   dbPath: _dbPath);

        // Insert raw wait-stats directly into the DB so rollup can aggregate them.
        private void SeedRawHistory(string server, DateTime hourUtc, string waitType, double waitMs, int count)
        {
            var connStr = $"Data Source={_dbPath};Mode=ReadWriteCreate;Cache=Shared";
            using var conn = SqliteCipherHelper.OpenEncrypted(connStr);
            using var cmd = conn.CreateCommand();

            // Ensure raw table exists (schema is created by HistoricalPerformanceService, but
            // this helper may run before the first service creation in some orderings).
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS wait_stats_history (
                    server_name  TEXT    NOT NULL,
                    recorded_at  TEXT    NOT NULL,
                    wait_type    TEXT    NOT NULL,
                    delta_wait_ms REAL   NOT NULL
                );";
            cmd.ExecuteNonQuery();

            for (int i = 0; i < count; i++)
            {
                using var ins = conn.CreateCommand();
                ins.CommandText = @"
                    INSERT INTO wait_stats_history (server_name, recorded_at, wait_type, delta_wait_ms)
                    VALUES (@s, @t, @w, @v)";
                // Stagger timestamps within the hour
                var ts = hourUtc.AddMinutes(i * (60.0 / count));
                ins.Parameters.AddWithValue("@s", server);
                ins.Parameters.AddWithValue("@t", ts.ToString("o"));
                ins.Parameters.AddWithValue("@w", waitType);
                ins.Parameters.AddWithValue("@v", waitMs);
                ins.ExecuteNonQuery();
            }
        }

        // ── Rollup: hourly ────────────────────────────────────────────────────

        [Fact]
        public async Task Rollup_PopulatesHourlyTable()
        {
            // Seed 5 raw rows for 2 hours ago (completed hour — rollup will pick it up).
            var twoHoursAgo = DateTime.UtcNow.Date.Add(
                TimeSpan.FromHours(DateTime.UtcNow.Hour - 2));

            // Create service first so schema is initialised, then seed.
            using var svc = NewService();
            SeedRawHistory("srv-rollup", twoHoursAgo, "PAGEIOLATCH_SH", 100, 5);

            await svc.RunRollupAsync(CancellationToken.None);

            var rows = svc.GetHourlyWaitStats("srv-rollup",
                twoHoursAgo.AddMinutes(-30), twoHoursAgo.AddHours(1).AddMinutes(30));

            Assert.NotEmpty(rows);
            var row = rows.First(r => r.WaitType == "PAGEIOLATCH_SH");
            Assert.Equal("srv-rollup", row.ServerName);
            Assert.InRange(row.AvgWaitMs, 99.0, 101.0);
        }

        // ── Rollup: daily ─────────────────────────────────────────────────────

        [Fact]
        public async Task Rollup_PopulatesDailyTable()
        {
            // Seed hourly rows for yesterday by inserting directly into wait_stats_hourly.
            var yesterday = DateTime.UtcNow.Date.AddDays(-1);
            var connStr = $"Data Source={_dbPath};Mode=ReadWriteCreate;Cache=Shared";

            using var svc = NewService(); // ensures schema

            using (var conn = SqliteCipherHelper.OpenEncrypted(connStr))
            {
                for (int h = 0; h < 3; h++)
                {
                    using var cmd = conn.CreateCommand();
                    var hourKey = yesterday.AddHours(h).ToString("o");
                    cmd.CommandText = @"
                        INSERT OR REPLACE INTO wait_stats_hourly
                            (server_name, hour_utc, wait_type, avg_wait_time_ms, max_wait_time_ms, sample_count)
                        VALUES (@s, @h, @w, @avg, @max, @sc)";
                    cmd.Parameters.AddWithValue("@s", "srv-daily");
                    cmd.Parameters.AddWithValue("@h", hourKey);
                    cmd.Parameters.AddWithValue("@w", "CXPACKET");
                    cmd.Parameters.AddWithValue("@avg", 50.0);
                    cmd.Parameters.AddWithValue("@max", 200.0);
                    cmd.Parameters.AddWithValue("@sc", 10);
                    cmd.ExecuteNonQuery();
                }
            }

            await svc.RunRollupAsync(CancellationToken.None);

            var rows = svc.GetDailyWaitStats("srv-daily",
                yesterday.AddDays(-1), yesterday.AddDays(1));

            Assert.NotEmpty(rows);
            var row = rows.First(r => r.WaitType == "CXPACKET");
            Assert.Equal("srv-daily", row.ServerName);
            Assert.InRange(row.AvgWaitMs, 49.0, 51.0);
        }

        // ── Retention purge ──────────────────────────────────────────────────

        [Fact]
        public async Task RetentionPurge_RemovesBeyondConfiguredDays()
        {
            // Use 1-day hourly retention.  Seed a row 2 days old in the hourly table.
            var connStr = $"Data Source={_dbPath};Mode=ReadWriteCreate;Cache=Shared";

            using var svc = NewService(rawRetentionDays: 1, hourlyRetentionDays: 1, dailyRetentionDays: 1);

            var oldHour = DateTime.UtcNow.AddDays(-2).ToString("o");

            using (var conn = SqliteCipherHelper.OpenEncrypted(connStr))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO wait_stats_hourly
                        (server_name, hour_utc, wait_type, avg_wait_time_ms, max_wait_time_ms, sample_count)
                    VALUES ('srv-purge', @h, 'LCK_M_X', 300.0, 500.0, 5)";
                cmd.Parameters.AddWithValue("@h", oldHour);
                cmd.ExecuteNonQuery();
            }

            // Confirm row is present before purge
            var before = svc.GetHourlyWaitStats("srv-purge",
                DateTime.UtcNow.AddDays(-3), DateTime.UtcNow.AddDays(1));
            Assert.NotEmpty(before);

            // RunRollupAsync calls PurgeAsync internally
            await svc.RunRollupAsync(CancellationToken.None);

            var after = svc.GetHourlyWaitStats("srv-purge",
                DateTime.UtcNow.AddDays(-3), DateTime.UtcNow.AddDays(1));
            Assert.Empty(after);
        }
    }
}
