/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Caching;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Tracks cache performance metrics over time (hourly/daily aggregates).
    /// Stores metrics in the SQLite cache database for historical analysis.
    /// </summary>
    public class CacheMetricsService : IDisposable
    {
        private readonly liveQueriesCacheStore _cache;
        private readonly ILogger<CacheMetricsService> _logger;
        private readonly ConcurrentDictionary<string, SessionMetrics> _sessionMetrics = new();
        private bool _disposed;

        // Current session identifier (process start time)
        private readonly string _sessionId;
        private readonly Task _initTask;

        public CacheMetricsService(liveQueriesCacheStore cache, ILogger<CacheMetricsService> logger)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            // Fire-and-forget initialization — first write will await it
            _initTask = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            try
            {
                await EnsureTableExists();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CacheMetricsService initialization failed");
            }
        }

        private async Task EnsureTableExists()
        {
            using var conn = _cache.CreateExternalConnection();
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS cache_metrics (
                    session_id       TEXT NOT NULL,
                    recorded_at      TEXT NOT NULL,
                    time_period      TEXT NOT NULL, -- 'current', 'hourly', 'daily'
                    total_queries    INTEGER NOT NULL,
                    fresh_hits       INTEGER NOT NULL,
                    cache_hits       INTEGER NOT NULL,
                    PRIMARY KEY (session_id, recorded_at, time_period)
                );

                CREATE INDEX IF NOT EXISTS idx_cache_metrics_time ON cache_metrics(recorded_at);
            ";
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Records metrics from CachingQueryExecutor for the current session.
        /// Call this at the end of each dashboard load cycle.
        /// </summary>
        public async Task RecordSnapshotAsync(string dashboardId, int total, int fresh, int cached)
        {
            if (_disposed) return;
            await _initTask;

            var key = $"current_{dashboardId}";
            var session = _sessionMetrics.GetOrAdd(key, _ => new SessionMetrics());

            session.TotalQueries += total;
            session.FreshHits += fresh;
            session.CacheHits += cached;
            session.LastRecorded = DateTime.UtcNow;

            // Also persist to SQLite for long-term history
            await PersistToStorageAsync(_sessionId, DateTime.UtcNow, "current", total, fresh, cached);

            _logger.LogDebug("[CacheMetrics] Dashboard {DashboardId}: {Total} queries, {Fresh} fresh, {Cached} cached",
                dashboardId, total, fresh, cached);
        }

        /// <summary>
        /// Gets aggregated metrics for the current application session.
        /// </summary>
        public SessionMetrics GetCurrentSessionMetrics()
        {
            var total = new SessionMetrics();
            foreach (var kvp in _sessionMetrics)
            {
                total.TotalQueries += kvp.Value.TotalQueries;
                total.FreshHits += kvp.Value.FreshHits;
                total.CacheHits += kvp.Value.CacheHits;
                if (kvp.Value.LastRecorded > total.LastRecorded)
                    total.LastRecorded = kvp.Value.LastRecorded;
            }
            return total;
        }

        /// <summary>
        /// Gets hourly aggregated metrics for the last 24 hours.
        /// </summary>
        public async Task<List<TimeSeriesMetrics>> GetHourlyMetricsAsync(int hours = 24)
        {
            var result = new List<TimeSeriesMetrics>();
            if (_disposed) return result;

            try
            {
                using var conn = _cache.CreateExternalConnection();
                await conn.OpenAsync();

                var cutoff = DateTime.UtcNow.AddHours(-hours).ToString("o");
                var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT 
                        strftime('%Y-%m-%d %H:00:00', recorded_at) as hour_bucket,
                        SUM(total_queries) as total_queries,
                        SUM(fresh_hits) as fresh_hits,
                        SUM(cache_hits) as cache_hits
                    FROM cache_metrics
                    WHERE time_period = 'current' AND recorded_at > @cutoff
                    GROUP BY hour_bucket
                    ORDER BY hour_bucket ASC
                ";
                cmd.Parameters.AddWithValue("@cutoff", cutoff);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var bucket = DateTime.Parse(reader.GetString(0));
                    var total = reader.GetInt32(1);
                    var fresh = reader.GetInt32(2);
                    var cached = reader.GetInt32(3);

                    result.Add(new TimeSeriesMetrics
                    {
                        Timestamp = bucket,
                        TotalQueries = total,
                        FreshHits = fresh,
                        CacheHits = cached
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query hourly metrics");
            }

            return result;
        }

        /// <summary>
        /// Gets daily aggregated metrics for the last 7 days.
        /// </summary>
        public async Task<List<TimeSeriesMetrics>> GetDailyMetricsAsync(int days = 7)
        {
            var result = new List<TimeSeriesMetrics>();
            if (_disposed) return result;

            try
            {
                using var conn = _cache.CreateExternalConnection();
                await conn.OpenAsync();

                var cutoff = DateTime.UtcNow.AddDays(-days).ToString("o");
                var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT 
                        strftime('%Y-%m-%d', recorded_at) as day_bucket,
                        SUM(total_queries) as total_queries,
                        SUM(fresh_hits) as fresh_hits,
                        SUM(cache_hits) as cache_hits
                    FROM cache_metrics
                    WHERE time_period = 'current' AND recorded_at > @cutoff
                    GROUP BY day_bucket
                    ORDER BY day_bucket ASC
                ";
                cmd.Parameters.AddWithValue("@cutoff", cutoff);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var bucket = DateTime.Parse(reader.GetString(0));
                    var total = reader.GetInt32(1);
                    var fresh = reader.GetInt32(2);
                    var cached = reader.GetInt32(3);

                    result.Add(new TimeSeriesMetrics
                    {
                        Timestamp = bucket,
                        TotalQueries = total,
                        FreshHits = fresh,
                        CacheHits = cached
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query daily metrics");
            }

            return result;
        }

        private async Task PersistToStorageAsync(string sessionId, DateTime recordedAt, string timePeriod, int total, int fresh, int cached)
        {
            try
            {
                using var conn = _cache.CreateExternalConnection();
                await conn.OpenAsync();

                var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO cache_metrics 
                    (session_id, recorded_at, time_period, total_queries, fresh_hits, cache_hits)
                    VALUES (@sessionId, @recordedAt, @timePeriod, @total, @fresh, @cached)
                ";
                cmd.Parameters.AddWithValue("@sessionId", sessionId);
                cmd.Parameters.AddWithValue("@recordedAt", recordedAt.ToString("o"));
                cmd.Parameters.AddWithValue("@timePeriod", timePeriod);
                cmd.Parameters.AddWithValue("@total", total);
                cmd.Parameters.AddWithValue("@fresh", fresh);
                cmd.Parameters.AddWithValue("@cached", cached);

                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist cache metrics");
                // Don't throw — metrics are best-effort
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _sessionMetrics.Clear();
                _disposed = true;
            }
        }

        public class SessionMetrics
        {
            public int TotalQueries { get; set; }
            public int FreshHits { get; set; }
            public int CacheHits { get; set; }
            public DateTime LastRecorded { get; set; } = DateTime.MinValue;

            public double GetFreshPercentage() => TotalQueries > 0 ? (FreshHits * 100.0 / TotalQueries) : 0;
            public double GetCachePercentage() => TotalQueries > 0 ? (CacheHits * 100.0 / TotalQueries) : 0;
        }

        public class TimeSeriesMetrics
        {
            public DateTime Timestamp { get; set; }
            public int TotalQueries { get; set; }
            public int FreshHits { get; set; }
            public int CacheHits { get; set; }
            public double GetFreshPercentage() => TotalQueries > 0 ? (FreshHits * 100.0 / TotalQueries) : 0;
            public double GetCachePercentage() => TotalQueries > 0 ? (CacheHits * 100.0 / TotalQueries) : 0;
        }
    }
}
