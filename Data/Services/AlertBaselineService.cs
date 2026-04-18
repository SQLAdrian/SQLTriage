/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Collects alert metric samples and computes per-server IQR-based dynamic thresholds.
    ///
    /// Lifecycle:
    ///   1. On startup — aggressive seeding mode: runs all baselineable alerts every 15s
    ///      until each alert/server pair has MinSeedSamples (10) within ~5 minutes.
    ///   2. Normal mode — collects one sample per alert evaluation (piggybacked from
    ///      AlertEvaluationService, no extra SQL queries).
    ///   3. Nightly — recomputes IQR stats (P25/P50/P75/P95) from rolling 30-day window.
    ///
    /// Thresholds (Median + IQR):
    ///   Warning  = P75 + WarnMultiplier  × IQR   (default 1.5)
    ///   Critical = P75 + CritMultiplier  × IQR   (default 3.0)
    ///   Minimum 10 samples required before thresholds are trusted.
    /// </summary>
    public class AlertBaselineService : IDisposable
    {
        // ── Constants ────────────────────────────────────────────────────────

        private const int    MinSeedSamples         = 10;
        private const int    SeedIntervalSeconds    = 15;    // aggressive seed cadence
        private const int    NormalIntervalSeconds  = 3600;  // recompute stats hourly
        private const int    RetentionDays          = 30;
        private const double WarnMultiplier         = 1.5;
        private const double CritMultiplier         = 3.0;

        // Trend detection: minimum samples needed for a meaningful slope, and the
        // window of recent samples to regress over (avoids ancient data skewing slope).
        private const int    TrendMinSamples        = 20;
        private const int    TrendWindowHours       = 72;   // look back 3 days for slope
        // A slope is "trending" when it exceeds this fraction of the median per hour.
        // e.g. 0.005 = alert if rising > 0.5% of median per hour = ~12% per day.
        private const double TrendWarnSlopeRatio    = 0.005;
        private const double TrendCritSlopeRatio    = 0.015;

        // ── Fields ───────────────────────────────────────────────────────────

        private readonly ILogger<AlertBaselineService>  _logger;
        private readonly AlertDefinitionService         _definitions;
        private readonly ServerConnectionManager        _connections;
        private readonly liveQueriesCacheStore          _cache;
        private readonly IUserSettingsService           _settings;

        private readonly System.Timers.Timer            _seedTimer;
        private readonly System.Timers.Timer            _computeTimer;
        private readonly SemaphoreSlim                  _lock = new(1, 1);

        // alert_id:server_name -> sample count (for progress)
        private readonly ConcurrentDictionary<string, int> _sampleCounts = new(StringComparer.OrdinalIgnoreCase);

        // alert_id:server_name -> computed stats (in-memory cache, refreshed hourly)
        private readonly ConcurrentDictionary<string, BaselineStats> _stats = new(StringComparer.OrdinalIgnoreCase);

        private bool _seedingComplete;
        private bool _disposed;

        // ── Public surface ───────────────────────────────────────────────────

        /// <summary>True once all alert/server pairs have reached MinSeedSamples.</summary>
        public bool SeedingComplete => _seedingComplete;

        /// <summary>
        /// Progress 0.0–1.0 across all alert/server pairs.
        /// Useful for showing a progress bar during initial seeding.
        /// </summary>
        public double SeedingProgress
        {
            get
            {
                if (_sampleCounts.IsEmpty) return 0;
                var total   = _sampleCounts.Count * MinSeedSamples;
                var current = _sampleCounts.Values.Sum(v => Math.Min(v, MinSeedSamples));
                return total == 0 ? 0 : Math.Min(1.0, (double)current / total);
            }
        }

        /// <summary>Number of alert/server pairs that have reached the minimum sample threshold.</summary>
        public int SeededCount   => _sampleCounts.Values.Count(v => v >= MinSeedSamples);
        /// <summary>Total alert/server pairs being tracked.</summary>
        public int TotalPairCount => _sampleCounts.Count;

        public event Action? OnProgressChanged;

        // ── Constructor ──────────────────────────────────────────────────────

        public AlertBaselineService(
            ILogger<AlertBaselineService> logger,
            AlertDefinitionService definitions,
            ServerConnectionManager connections,
            liveQueriesCacheStore cache,
            IUserSettingsService settings)
        {
            _logger      = logger;
            _definitions = definitions;
            _connections = connections;
            _cache       = cache;
            _settings    = settings;

            _seedTimer = new System.Timers.Timer(SeedIntervalSeconds * 1000) { AutoReset = true };
            _seedTimer.Elapsed += async (_, _) => await RunSeedCycleAsync();

            _computeTimer = new System.Timers.Timer(NormalIntervalSeconds * 1000) { AutoReset = true };
            _computeTimer.Elapsed += async (_, _) => await RecomputeAllStatsAsync();
        }

        // ── Startup ──────────────────────────────────────────────────────────

        public async Task StartAsync()
        {
            if (!_settings.GetAlertBaselineEnabled()) return;

            // Load existing sample counts from DB so we know where we left off
            await LoadSampleCountsAsync();

            // Initial stat computation from any existing data
            await RecomputeAllStatsAsync();

            // Start aggressive seeding if not yet complete
            CheckSeedingComplete();
            if (!_seedingComplete)
            {
                _logger.LogInformation("Alert baseline seeding started — target {N} samples per alert/server pair", MinSeedSamples);
                _seedTimer.Start();
                // Run one cycle immediately
                _ = Task.Run(RunSeedCycleAsync);
            }
            else
            {
                _logger.LogInformation("Alert baseline already seeded — {N} pairs ready", SeededCount);
            }

            _computeTimer.Start();
        }

        // ── Sample recording (called by AlertEvaluationService) ──────────────

        /// <summary>
        /// Record a raw metric value for the given alert/server.
        /// Called after every successful alert query execution — no extra SQL needed.
        /// </summary>
        public void RecordSample(string alertId, string serverName, double value)
        {
            if (!_settings.GetAlertBaselineEnabled()) return;

            var alert = _definitions.GetAlert(alertId);
            if (alert == null || !alert.CanBaseline) return;

            var key = Key(alertId, serverName);
            var now = DateTime.UtcNow;

            // Fire-and-forget persist — don't block the evaluation thread
            _ = Task.Run(() => PersistSampleAsync(alertId, serverName, value, now));

            // Update in-memory count
            _sampleCounts.AddOrUpdate(key, 1, (_, old) => old + 1);

            if (!_seedingComplete)
            {
                CheckSeedingComplete();
                OnProgressChanged?.Invoke();
            }
        }

        // ── Threshold lookup (called by AlertEvaluationService) ──────────────

        /// <summary>
        /// Returns computed warning/critical thresholds for this alert/server, or null if
        /// insufficient samples exist. Respects per-server vs global setting.
        /// </summary>
        public (double? warn, double? crit) GetThresholds(string alertId, string serverName)
        {
            if (!_settings.GetAlertBaselineEnabled()) return (null, null);

            var alert = _definitions.GetAlert(alertId);
            if (alert == null || !alert.CanBaseline) return (null, null);

            var lookupKey = _settings.GetAlertBaselinePerServer()
                ? Key(alertId, serverName)
                : Key(alertId, "__global__");

            if (_stats.TryGetValue(lookupKey, out var s) && s.SampleCount >= MinSeedSamples)
                return (s.ThresholdWarn, s.ThresholdCrit);

            return (null, null);
        }

        /// <summary>
        /// Returns trend signals (warn/crit) for this alert/server based on OLS slope detection.
        /// Both can be false when insufficient samples exist or the slope is within normal bounds.
        /// </summary>
        public (bool trendWarn, bool trendCrit) GetTrendSignal(string alertId, string serverName)
        {
            if (!_settings.GetAlertBaselineEnabled()) return (false, false);

            var alert = _definitions.GetAlert(alertId);
            if (alert == null || !alert.CanBaseline) return (false, false);

            var lookupKey = _settings.GetAlertBaselinePerServer()
                ? Key(alertId, serverName)
                : Key(alertId, "__global__");

            if (_stats.TryGetValue(lookupKey, out var s))
                return (s.IsTrendWarning, s.IsTrendCritical);

            return (false, false);
        }

        // ── Seeding cycle ────────────────────────────────────────────────────

        private async Task RunSeedCycleAsync()
        {
            if (_seedingComplete || !_settings.GetAlertBaselineEnabled()) return;

            await _lock.WaitAsync();
            try
            {
                var connections = _connections.GetConnections()
                    .Where(c => c.IsEnabled)
                    .ToList();

                // Flatten to list of (conn, serverName) value tuples
                var serverPairs = new List<(ServerConnection Conn, string Server)>();
                foreach (var c in connections)
                    foreach (var s in c.GetServerList())
                        serverPairs.Add((c, s));

                var alerts = _definitions.GetAllAlerts()
                    .Where(a => a.Enabled && a.CanBaseline)
                    .ToList();

                if (alerts.Count == 0 || serverPairs.Count == 0)
                {
                    _seedingComplete = true;
                    _seedTimer.Stop();
                    return;
                }

                // Initialise tracking keys
                foreach (var a in alerts)
                    foreach (var pair in serverPairs)
                    {
                        var k = Key(a.Id, pair.Server);
                        _sampleCounts.TryAdd(k, 0);
                    }

                // Only seed pairs that still need samples
                var tasks = new List<Task>();
                foreach (var alert in alerts)
                {
                    foreach (var pair in serverPairs)
                    {
                        var k = Key(alert.Id, pair.Server);
                        if (_sampleCounts.GetValueOrDefault(k, 0) >= MinSeedSamples) continue;

                        tasks.Add(SeedOnePairAsync(alert, pair.Conn, pair.Server));
                    }
                }

                await Task.WhenAll(tasks);
                await RecomputeAllStatsAsync();

                CheckSeedingComplete();
                OnProgressChanged?.Invoke();

                if (_seedingComplete)
                {
                    _seedTimer.Stop();
                    _logger.LogInformation("Alert baseline seeding complete — {N} pairs seeded", SeededCount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Baseline seed cycle failed");
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task SeedOnePairAsync(AlertDefinition alert, ServerConnection conn, string serverName)
        {
            try
            {
                var connString = conn.GetConnectionString(serverName, "master");
                using var sqlConn = new SqlConnection(connString);
                await sqlConn.OpenAsync();

                using var cmd = new SqlCommand(alert.Query, sqlConn) { CommandTimeout = 15 };
                var result = await cmd.ExecuteScalarAsync();
                if (result == null || result == DBNull.Value) return;

                var value = Convert.ToDouble(result);
                var now   = DateTime.UtcNow;
                var key   = Key(alert.Id, serverName);

                await PersistSampleAsync(alert.Id, serverName, value, now);
                _sampleCounts.AddOrUpdate(key, 1, (_, old) => old + 1);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Seed failed: alert={A} server={S}", alert.Id, serverName);
            }
        }

        // ── Stats computation ────────────────────────────────────────────────

        /// <summary>
        /// Recomputes IQR-based thresholds from the last 30 days of samples.
        /// Also computes OLS trend slope over the last TrendWindowHours.
        /// Runs hourly in normal operation; also called after each seed cycle.
        /// </summary>
        public async Task RecomputeAllStatsAsync()
        {
            if (!_settings.GetAlertBaselineEnabled()) return;
            try
            {
                var cutoff    = DateTime.UtcNow.AddDays(-RetentionDays).ToString("o");
                var rows      = await LoadAllSamplesAsync(cutoff);
                var trendCutoff = DateTime.UtcNow.AddHours(-TrendWindowHours);

                // Per-server stats
                var perServer = rows
                    .GroupBy(r => Key(r.AlertId, r.ServerName))
                    .ToList();

                foreach (var g in perServer)
                {
                    var sorted    = g.Select(r => r.Value).OrderBy(v => v).ToList();
                    var trendRows = g.Where(r => r.SampledAt >= trendCutoff)
                                    .OrderBy(r => r.SampledAt).ToList();
                    var stats = ComputeStats(g.First().AlertId, g.First().ServerName, sorted, trendRows);
                    _stats[g.Key] = stats;
                    await PersistStatsAsync(stats);
                }

                // Global (cross-server) for non-per-server mode
                var byAlert = rows.GroupBy(r => r.AlertId).ToList();

                foreach (var g in byAlert)
                {
                    var sorted    = g.Select(r => r.Value).OrderBy(v => v).ToList();
                    var trendRows = g.Where(r => r.SampledAt >= trendCutoff)
                                    .OrderBy(r => r.SampledAt).ToList();
                    var stats = ComputeStats(g.Key, "__global__", sorted, trendRows);
                    _stats[Key(g.Key, "__global__")] = stats;
                    await PersistStatsAsync(stats);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Baseline stats recomputation failed");
            }
        }

        private static BaselineStats ComputeStats(
            string alertId,
            string serverName,
            List<double> sorted,
            List<(string AlertId, string ServerName, double Value, DateTime SampledAt)> trendRows)
        {
            var n    = sorted.Count;
            var p25  = Percentile(sorted, 0.25);
            var p50  = Percentile(sorted, 0.50);
            var p75  = Percentile(sorted, 0.75);
            var p95  = Percentile(sorted, 0.95);
            var iqr  = p75 - p25;

            // Warn = P75 + 1.5 * IQR, Crit = P75 + 3.0 * IQR
            // Floor at P95 so thresholds are never lower than the 95th percentile
            var warn = Math.Max(p75 + WarnMultiplier * iqr, p95);
            var crit = p75 + CritMultiplier * iqr;

            // OLS linear regression on recent trend window
            var (slope, rSquared) = ComputeTrend(trendRows);
            var trendCount        = trendRows.Count;

            // Slope ratio relative to median (0 guard)
            var slopeRatio = p50 > 0 ? Math.Abs(slope) / p50 : 0;
            var trendWarn  = trendCount >= TrendMinSamples && slopeRatio >= TrendWarnSlopeRatio;
            var trendCrit  = trendCount >= TrendMinSamples && slopeRatio >= TrendCritSlopeRatio;

            return new BaselineStats
            {
                AlertId          = alertId,
                ServerName       = serverName,
                SampleCount      = n,
                P25              = p25,
                P50              = p50,
                P75              = p75,
                P95              = p95,
                Iqr              = iqr,
                ThresholdWarn    = warn,
                ThresholdCrit    = crit,
                LastComputed     = DateTime.UtcNow,
                TrendSlopePerHour = slope,
                TrendRSquared    = rSquared,
                TrendSampleCount = trendCount,
                IsTrendWarning   = trendWarn,
                IsTrendCritical  = trendCrit,
            };
        }

        /// <summary>
        /// OLS linear regression: x = hours since first sample, y = metric value.
        /// Returns (slope units/hour, R²). Returns (0, 0) if fewer than 2 points.
        /// </summary>
        private static (double Slope, double RSquared) ComputeTrend(
            List<(string AlertId, string ServerName, double Value, DateTime SampledAt)> rows)
        {
            if (rows.Count < 2) return (0, 0);

            var origin = rows[0].SampledAt;
            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
            var n = rows.Count;

            foreach (var r in rows)
            {
                var x = (r.SampledAt - origin).TotalHours;
                var y = r.Value;
                sumX  += x;
                sumY  += y;
                sumXY += x * y;
                sumX2 += x * x;
            }

            var denom = n * sumX2 - sumX * sumX;
            if (Math.Abs(denom) < 1e-12) return (0, 0);

            var slope     = (n * sumXY - sumX * sumY) / denom;
            var intercept = (sumY - slope * sumX) / n;

            // R² = 1 - SS_res / SS_tot
            var yMean  = sumY / n;
            double ssTot = 0, ssRes = 0;
            foreach (var r in rows)
            {
                var x    = (r.SampledAt - origin).TotalHours;
                var yHat = slope * x + intercept;
                ssTot += (r.Value - yMean)  * (r.Value - yMean);
                ssRes += (r.Value - yHat)   * (r.Value - yHat);
            }

            var rSquared = ssTot < 1e-12 ? 0 : Math.Max(0, 1.0 - ssRes / ssTot);
            return (slope, rSquared);
        }

        private static double Percentile(List<double> sorted, double p)
        {
            if (sorted.Count == 0) return 0;
            if (sorted.Count == 1) return sorted[0];
            var idx  = p * (sorted.Count - 1);
            var lo   = (int)Math.Floor(idx);
            var hi   = (int)Math.Ceiling(idx);
            var frac = idx - lo;
            return sorted[lo] + frac * (sorted[hi] - sorted[lo]);
        }

        // ── SQLite persistence ───────────────────────────────────────────────

        private async Task PersistSampleAsync(string alertId, string serverName, double value, DateTime sampledAt)
        {
            try
            {
                using var conn = _cache.CreateExternalConnection();
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO alert_baseline_samples
                        (alert_id, server_name, sampled_at, value, hour_of_day, day_of_week, fetched_at)
                    VALUES
                        (@aid, @srv, @sat, @val, @hod, @dow, @fat)";
                cmd.Parameters.AddWithValue("@aid", alertId);
                cmd.Parameters.AddWithValue("@srv", serverName);
                cmd.Parameters.AddWithValue("@sat", sampledAt.ToString("o"));
                cmd.Parameters.AddWithValue("@val", value);
                cmd.Parameters.AddWithValue("@hod", sampledAt.Hour);
                cmd.Parameters.AddWithValue("@dow", (int)sampledAt.DayOfWeek);
                cmd.Parameters.AddWithValue("@fat", sampledAt.ToString("o"));
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to persist baseline sample {AlertId}/{Server}", alertId, serverName);
            }
        }

        private async Task PersistStatsAsync(BaselineStats s)
        {
            try
            {
                using var conn = _cache.CreateExternalConnection();
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO alert_baseline_stats
                        (alert_id, server_name, sample_count, p25, p50, p75, p95,
                         iqr, threshold_warn, threshold_crit, last_computed, baseline_locked,
                         trend_slope, trend_r_squared, trend_sample_count,
                         is_trend_warning, is_trend_critical)
                    VALUES
                        (@aid, @srv, @sc, @p25, @p50, @p75, @p95,
                         @iqr, @tw, @tc, @lc,
                         COALESCE((SELECT baseline_locked FROM alert_baseline_stats
                                   WHERE alert_id=@aid AND server_name=@srv), 0),
                         @tslope, @trsq, @tsc, @tiw, @tic)";
                cmd.Parameters.AddWithValue("@aid",   s.AlertId);
                cmd.Parameters.AddWithValue("@srv",   s.ServerName);
                cmd.Parameters.AddWithValue("@sc",    s.SampleCount);
                cmd.Parameters.AddWithValue("@p25",   s.P25);
                cmd.Parameters.AddWithValue("@p50",   s.P50);
                cmd.Parameters.AddWithValue("@p75",   s.P75);
                cmd.Parameters.AddWithValue("@p95",   s.P95);
                cmd.Parameters.AddWithValue("@iqr",   s.Iqr);
                cmd.Parameters.AddWithValue("@tw",    s.ThresholdWarn);
                cmd.Parameters.AddWithValue("@tc",    s.ThresholdCrit);
                cmd.Parameters.AddWithValue("@lc",    s.LastComputed.ToString("o"));
                cmd.Parameters.AddWithValue("@tslope",s.TrendSlopePerHour);
                cmd.Parameters.AddWithValue("@trsq",  s.TrendRSquared);
                cmd.Parameters.AddWithValue("@tsc",   s.TrendSampleCount);
                cmd.Parameters.AddWithValue("@tiw",   s.IsTrendWarning  ? 1 : 0);
                cmd.Parameters.AddWithValue("@tic",   s.IsTrendCritical ? 1 : 0);
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to persist baseline stats {AlertId}/{Server}", s.AlertId, s.ServerName);
            }
        }

        private async Task<List<(string AlertId, string ServerName, double Value, DateTime SampledAt)>> LoadAllSamplesAsync(string cutoff)
        {
            var result = new List<(string, string, double, DateTime)>();
            try
            {
                using var conn = _cache.CreateExternalConnection();
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT alert_id, server_name, value, sampled_at
                    FROM alert_baseline_samples
                    WHERE sampled_at >= @cutoff
                    ORDER BY alert_id, server_name, sampled_at";
                cmd.Parameters.AddWithValue("@cutoff", cutoff);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var sampledAt = DateTime.Parse(reader.GetString(3), null,
                        System.Globalization.DateTimeStyles.RoundtripKind);
                    result.Add((reader.GetString(0), reader.GetString(1), reader.GetDouble(2), sampledAt));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to load baseline samples");
            }
            return result;
        }

        private async Task LoadSampleCountsAsync()
        {
            try
            {
                using var conn = _cache.CreateExternalConnection();
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT alert_id, server_name, COUNT(*) AS cnt
                    FROM alert_baseline_samples
                    GROUP BY alert_id, server_name";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var key = Key(reader.GetString(0), reader.GetString(1));
                    _sampleCounts[key] = reader.GetInt32(2);
                }

                // Also load persisted stats into memory
                using var cmd2 = conn.CreateCommand();
                cmd2.CommandText = @"
                    SELECT alert_id, server_name, sample_count, p25, p50, p75, p95,
                           iqr, threshold_warn, threshold_crit, last_computed,
                           trend_slope, trend_r_squared, trend_sample_count,
                           is_trend_warning, is_trend_critical
                    FROM alert_baseline_stats";
                using var r2 = await cmd2.ExecuteReaderAsync();
                while (await r2.ReadAsync())
                {
                    var s = new BaselineStats
                    {
                        AlertId           = r2.GetString(0),
                        ServerName        = r2.GetString(1),
                        SampleCount       = r2.GetInt32(2),
                        P25               = r2.GetDouble(3),
                        P50               = r2.GetDouble(4),
                        P75               = r2.GetDouble(5),
                        P95               = r2.GetDouble(6),
                        Iqr               = r2.GetDouble(7),
                        ThresholdWarn     = r2.GetDouble(8),
                        ThresholdCrit     = r2.GetDouble(9),
                        LastComputed      = DateTime.Parse(r2.GetString(10)),
                        TrendSlopePerHour = r2.IsDBNull(11) ? 0 : r2.GetDouble(11),
                        TrendRSquared     = r2.IsDBNull(12) ? 0 : r2.GetDouble(12),
                        TrendSampleCount  = r2.IsDBNull(13) ? 0 : r2.GetInt32(13),
                        IsTrendWarning    = !r2.IsDBNull(14) && r2.GetInt32(14) == 1,
                        IsTrendCritical   = !r2.IsDBNull(15) && r2.GetInt32(15) == 1,
                    };
                    _stats[Key(s.AlertId, s.ServerName)] = s;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to load baseline counts from DB");
            }
        }

        /// <summary>
        /// Reset baseline for a specific alert/server pair (or all servers if serverName is null).
        /// Wipes samples and stats so re-seeding begins fresh.
        /// </summary>
        public async Task ResetBaselineAsync(string alertId, string? serverName = null)
        {
            try
            {
                using var conn = _cache.CreateExternalConnection();
                await conn.OpenAsync();

                using var cmd1 = conn.CreateCommand();
                cmd1.CommandText = serverName == null
                    ? "DELETE FROM alert_baseline_samples WHERE alert_id = @aid"
                    : "DELETE FROM alert_baseline_samples WHERE alert_id = @aid AND server_name = @srv";
                cmd1.Parameters.AddWithValue("@aid", alertId);
                if (serverName != null) cmd1.Parameters.AddWithValue("@srv", serverName);
                await cmd1.ExecuteNonQueryAsync();

                using var cmd2 = conn.CreateCommand();
                cmd2.CommandText = serverName == null
                    ? "DELETE FROM alert_baseline_stats WHERE alert_id = @aid"
                    : "DELETE FROM alert_baseline_stats WHERE alert_id = @aid AND server_name = @srv";
                cmd2.Parameters.AddWithValue("@aid", alertId);
                if (serverName != null) cmd2.Parameters.AddWithValue("@srv", serverName);
                await cmd2.ExecuteNonQueryAsync();

                // Clear in-memory
                var prefix = serverName == null ? $"{alertId}:" : Key(alertId, serverName);
                foreach (var k in _sampleCounts.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                    _sampleCounts.TryRemove(k, out _);
                foreach (var k in _stats.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                    _stats.TryRemove(k, out _);

                _seedingComplete = false;
                if (!_seedTimer.Enabled) _seedTimer.Start();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ResetBaseline failed for {AlertId}", alertId);
            }
        }

        /// <summary>Returns all computed stats (for UI display).</summary>
        public IReadOnlyDictionary<string, BaselineStats> GetAllStats() => _stats;

        // ── Helpers ──────────────────────────────────────────────────────────

        private static string Key(string alertId, string serverName)
            => $"{alertId}:{serverName}".ToLowerInvariant();

        private void CheckSeedingComplete()
        {
            if (_sampleCounts.Count == 0) return;
            _seedingComplete = _sampleCounts.Values.All(v => v >= MinSeedSamples);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _seedTimer.Dispose();
            _computeTimer.Dispose();
        }
    }

    /// <summary>Computed IQR-based baseline statistics for one alert/server pair.</summary>
    public class BaselineStats
    {
        public string   AlertId          { get; init; } = "";
        public string   ServerName       { get; init; } = "";
        public int      SampleCount      { get; init; }
        public double   P25              { get; init; }
        public double   P50              { get; init; }
        public double   P75              { get; init; }
        public double   P95              { get; init; }
        public double   Iqr              { get; init; }
        public double   ThresholdWarn    { get; init; }
        public double   ThresholdCrit    { get; init; }
        public DateTime LastComputed     { get; init; }
        public bool     BaselineLocked   { get; init; }
        // Trend detection (OLS linear regression over rolling TrendWindowHours)
        public double   TrendSlopePerHour  { get; init; }   // units/hour
        public double   TrendRSquared      { get; init; }   // 0–1 fit quality
        public int      TrendSampleCount   { get; init; }   // samples used for slope
        public bool     IsTrendWarning     { get; init; }
        public bool     IsTrendCritical    { get; init; }
    }
}
