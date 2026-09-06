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
    // BM:AlertBaselineService.Class — collects alert samples and computes IQR-based dynamic thresholds
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
    /// Thresholds (Median + IQR), computed in BOTH directions and selected by the alert's own
    /// operator — see <see cref="SelectFences"/> and the note on <see cref="ComputeStats"/>:
    ///   greater_than: Warning  = max(P75 + 1.5 × IQR, P95); Critical = max(P75 + 3.0 × IQR, Warning)
    ///   less_than:    Warning  = min(P25 - 1.5 × IQR, P05); Critical = min(P25 - 3.0 × IQR, Warning)
    ///   Minimum 10 samples required before thresholds are trusted.
    ///   IQR of zero produces NO fence in either direction (ruling 2026-08-23 #2) - only the
    ///   alert's fixed thresholds apply. See <see cref="SelectFences"/>.
    /// </summary>
    public class AlertBaselineService : IDisposable
    {
        // ── Constants ────────────────────────────────────────────────────────

        private const int MinSeedSamples = 10;
        private const int SeedIntervalSeconds = 15;    // aggressive seed cadence
        private const int NormalIntervalSeconds = 3600;  // recompute stats hourly
        private const int RetentionDays = 30;
        private const double WarnMultiplier = 1.5;
        private const double CritMultiplier = 3.0;

        // Trend detection: minimum samples needed for a meaningful slope, and the
        // window of recent samples to regress over (avoids ancient data skewing slope).
        private const int TrendMinSamples = 20;
        private const int TrendWindowHours = 72;   // look back 3 days for slope
        // A slope is "trending" when it exceeds this fraction of the median per hour.
        // e.g. 0.005 = alert if rising > 0.5% of median per hour = ~12% per day.
        private const double TrendWarnSlopeRatio = 0.005;
        private const double TrendCritSlopeRatio = 0.015;
        // alerts-r2-13: a slope alone is a fabricated shape when the points do not actually lie on a
        // line — noise around a flat mean produces a non-zero OLS slope that fits nothing. The trend
        // fire is the one basis that prints no threshold a reader could sanity-check, so it must also
        // require the regression to explain the data. Same reliability floor ForecastService uses
        // (ForecastService.ForecastResult.IsReliable gates on RSquared >= 0.3).
        private const double TrendMinRSquared = 0.3;

        // ── Fields ───────────────────────────────────────────────────────────

        private readonly ILogger<AlertBaselineService> _logger;
        private readonly AlertDefinitionService _definitions;
        private readonly ServerConnectionManager _connections;
        private readonly liveQueriesCacheStore _cache;
        private readonly IUserSettingsService _settings;

        private readonly System.Timers.Timer _seedTimer;
        private readonly System.Timers.Timer _computeTimer;
        private readonly SemaphoreSlim _lock = new(1, 1);

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
        /// alerts-r1-11: whether the learned-baseline feature is switched on at all. When it is off,
        /// <see cref="StartAsync"/> returns before loading sample counts, so <see cref="SeedingComplete"/>
        /// never flips and a naive "show the banner until seeding is complete" renders a seeding
        /// progress bar forever for a feature nobody turned on. The banner reads this so it does not
        /// claim an ongoing measurement that is not happening.
        /// </summary>
        public bool IsBaselineEnabled => _settings.GetAlertBaselineEnabled();

        /// <summary>
        /// Progress 0.0–1.0 across all alert/server pairs.
        /// Useful for showing a progress bar during initial seeding.
        /// </summary>
        public double SeedingProgress
        {
            get
            {
                if (_sampleCounts.IsEmpty) return 0;
                var total = _sampleCounts.Count * MinSeedSamples;
                var current = _sampleCounts.Values.Sum(v => Math.Min(v, MinSeedSamples));
                return total == 0 ? 0 : Math.Min(1.0, (double)current / total);
            }
        }

        /// <summary>Number of alert/server pairs that have reached the minimum sample threshold.</summary>
        public int SeededCount => _sampleCounts.Values.Count(v => v >= MinSeedSamples);
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
            _logger = logger;
            _definitions = definitions;
            _connections = connections;
            _cache = cache;
            _settings = settings;

            _seedTimer = new System.Timers.Timer(SeedIntervalSeconds * 1000) { AutoReset = true };
            _seedTimer.Elapsed += async (_, _) => await RunSeedCycleAsync();

            _computeTimer = new System.Timers.Timer(NormalIntervalSeconds * 1000) { AutoReset = true };
            _computeTimer.Elapsed += async (_, _) => await RecomputeAllStatsAsync();
        }

        // ── Startup ──────────────────────────────────────────────────────────

        /// <summary>
        /// Marker for the one-shot purge below. Dated, so a later repair of the same table gets
        /// its own marker instead of being swallowed by this one.
        /// </summary>
        internal const string CumulativePurgeMarker = "purge-cumulative-baselines-2026-08-23";

        /// <summary>
        /// Throws away every learned sample and fence belonging to an alert that has just been
        /// reclassified as a cumulative counter, exactly once per database.
        ///
        /// <para>Those rows are measurements of a DIFFERENT quantity. A page_splits sample of
        /// 1,554,744 was a since-startup total; from now on the same alert records a rate of a
        /// few per second. Keeping them would poison the baseline two ways. The fences computed
        /// from totals sit millions above any real rate, so a genuine breach would never fire -
        /// stale-quiet. Worse, a series of running totals only ever climbs, which is a textbook
        /// rising trend: <c>is_trend_critical</c> is persisted in alert_baseline_stats and the
        /// evaluator promotes it to Critical without consulting the value at all, so the first
        /// honest rate sample after the upgrade would fire a Critical off a slope measured on
        /// totals. Purging is the only way the first post-upgrade cycle is quiet AND honest.</para>
        ///
        /// <para>Cost of the purge: these alerts re-seed from scratch, so their learned fences are
        /// unavailable for roughly the seeding window. Their fixed thresholds keep working
        /// throughout - and those are now compared to a real rate.</para>
        /// </summary>
        internal async Task PurgeStaleCumulativeBaselinesAsync()
        {
            try
            {
                var cumulative = _definitions.GetAllAlerts()
                    .Where(a => a.IsCumulativeCounter)
                    .Select(a => a.Id)
                    .ToList();
                if (cumulative.Count == 0) return;

                if (!await _cache.TryClaimSchemaMarkerAsync(CumulativePurgeMarker)) return;

                foreach (var id in cumulative)
                    await ResetBaselineAsync(id);

                _logger.LogInformation(
                    "Discarded the learned baseline for {N} alerts now measured as rates: their stored samples were since-startup totals, a different quantity. These alerts re-seed from scratch; their fixed thresholds are unaffected",
                    cumulative.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not purge the stale cumulative baselines");
            }
        }

        public async Task StartAsync()
        {
            // Runs before the enabled check: the stale rows are wrong whether or not the learned
            // baseline is switched on today, and switching it on later must not resurrect them.
            await PurgeStaleCumulativeBaselinesAsync();

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
                return SelectFences(s, alert.Operator);

            return (null, null);
        }

        /// <summary>
        /// Picks the fence pair that sits on the side of the data the alert calls bad: the LOWER
        /// pair for a <c>less_than</c> alert, the UPPER pair for everything else -- and returns NO
        /// fence at all when the window has no spread.
        ///
        /// <para>The whole B defect (2026-08-22) was that this decision did not exist. The
        /// evaluator applies whichever number it is handed through
        /// <c>AlertEvaluationService.IsThresholdBreached</c>, which reads the same operator, so a
        /// fence computed on the wrong side is not a mis-tuned threshold -- it is a fence that
        /// almost every sample is already past.</para>
        ///
        /// <para>ZERO SPREAD PRODUCES NO FENCE. Adrian's ruling, DECISIONS 2026-08-23 05:00 #2:
        /// when the sample spread is zero the learned path produces no fence in either direction
        /// and only the alert's fixed thresholds apply. The measured case is buffer_cache_hit on
        /// server ".": 67 of 68 readings exactly 100, so the iqr is 0, p05 is 100, both lower
        /// fences land on 100, and a reading of 99.99096983926314 read Critical.</para>
        ///
        /// <para>ZERO SPREAD MEANS <c>Iqr &lt;= 0</c>, the ruling's literal (p25 == p75), and that
        /// is also the exact condition under which this method cannot return a fence PAIR at all.
        /// With iqr 0 the multiplier term vanishes from every one of the four fences, so
        /// warn and crit both collapse onto <c>max(p75, p95)</c> upward and onto
        /// <c>min(p25, p05)</c> downward: warn == crit, there is no warning band, and the first
        /// breach of any size is Critical. Two narrower readings were considered and rejected
        /// because each leaves a degenerate pair live: <c>min == max</c> does not even cover the
        /// ruled case (that window has a sample at 99.9909, so min != max), and <c>p05 == p95</c>
        /// misses a 95/5 split whose iqr is 0 while p95 is far from p75 -- warn and crit are still
        /// equal there. Both rejections are pinned in AlertBaselineFenceDirectionTests tier 6.
        /// A spread that is tiny but non-zero still produces a fence: the ruling says zero, and a
        /// tolerance would need a magic number chosen per metric scale.</para>
        ///
        /// <para>The gate is here and not in <see cref="ComputeStats"/> for three reasons. This is
        /// the only place a nullable fence can be expressed -- <see cref="BaselineStats"/> holds
        /// the four fences as non-nullable doubles in NOT NULL columns, so "no fence" cannot be
        /// stored without a schema change. Being on the READ side it also neutralises the rows an
        /// installed database has ALREADY persisted with iqr 0, at the next read rather than at
        /// the next nightly recompute. And the arithmetic in ComputeStats stays honest: the
        /// percentiles and both fence pairs are still measured and still recorded, because the
        /// ruling is a policy about whether to APPLY a learned fence, not a claim that the
        /// numbers are wrong. <see cref="ApplyTrendDirection"/> is the same shape for the same
        /// reason.</para>
        /// </summary>
        internal static (double? warn, double? crit) SelectFences(BaselineStats s, string op)
        {
            // Ruling 2026-08-23 #2. No spread, no learned fence, either direction.
            if (s.Iqr <= 0) return (null, null);

            return op == "less_than"
                ? (s.ThresholdWarnLower, s.ThresholdCritLower)
                : (s.ThresholdWarn, s.ThresholdCrit);
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

            if (_stats.TryGetValue(lookupKey, out var s) && IsTrendCurrent(s, DateTime.UtcNow))
                return ApplyTrendDirection(s, alert.Operator);

            return (false, false);
        }

        /// <summary>
        /// alerts-r1-12: whether a stored trend signal still describes the present. GetThresholds
        /// gates on <c>SampleCount &gt;= MinSeedSamples</c>; GetTrendSignal had no such gate, so a
        /// stale <c>is_trend_critical</c> could survive a data gap and fire Critical on the first new
        /// sample, naming "the last 72 h of samples" when that window held zero. RecomputeAllStatsAsync
        /// only touches pairs that HAVE rows in the retention window — a pair with none is never
        /// revisited, so its stat (and its LastComputed) freeze at the last time it had data. A trend
        /// summarises <see cref="TrendWindowHours"/> of samples, so a stat computed longer ago than
        /// that window no longer overlaps now and must not fire. This is the read-side equivalent of
        /// the retention purge the finding names as missing, and it also neutralises a stale row
        /// reloaded from the database at startup before the first recompute.
        /// </summary>
        internal static bool IsTrendCurrent(BaselineStats s, DateTime nowUtc)
            => s.TrendSampleCount >= TrendMinSamples
               && (nowUtc - s.LastComputed) <= TimeSpan.FromHours(TrendWindowHours);

        /// <summary>
        /// Suppresses a trend signal whose slope points the way the alert calls GOOD.
        ///
        /// <para>B-trend, 2026-08-22. <see cref="ComputeStats"/> measures trend strength as
        /// <c>Math.Abs(slope) / p50</c>, so a metric falling fast and a metric rising fast produce
        /// the same signal, and <c>AlertEvaluationService</c> then sets Critical from it without
        /// consulting the direction or the value. A page life expectancy climbing steadily -- the
        /// best thing that metric can do -- fired a critical trend alert on a <c>greater_than</c>
        /// definition. The magnitude is still computed and stored honestly; this is the read-side
        /// gate that decides whether the shape is bad NEWS, and it is the one thing the stored
        /// booleans cannot know because they never saw the alert's operator.</para>
        ///
        /// <para>A slope of exactly zero points nowhere and fires nothing.</para>
        /// </summary>
        internal static (bool trendWarn, bool trendCrit) ApplyTrendDirection(BaselineStats s, string op)
        {
            var badDirection = op == "less_than"
                ? s.TrendSlopePerHour < 0     // this alert fears the metric FALLING
                : s.TrendSlopePerHour > 0;    // every other operator fears it RISING

            return badDirection
                ? (s.IsTrendWarning, s.IsTrendCritical)
                : (false, false);
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

        /// <summary>
        /// Turns one raw seed reading into a stored baseline sample, or into nothing.
        ///
        /// <para>Internal, and the substitution happens on the way in rather than inside
        /// PersistSampleAsync, so a test can drive the real path from a raw counter reading:
        /// storing the reading instead of the rate here teaches the baseline a quantity the
        /// evaluator will never produce again, and the test that reads the stored sample back
        /// goes red.</para>
        /// </summary>
        internal async Task RecordSeedObservationAsync(
            AlertDefinition alert, string serverName, double raw, DateTime nowUtc)
        {
            var observed = await ObserveForBaselineAsync(alert, serverName, raw, nowUtc);
            if (observed == null) return;   // no rate yet: seed nothing rather than seed a total

            await PersistSampleAsync(alert.Id, serverName, observed.Value, nowUtc);
            _sampleCounts.AddOrUpdate(Key(alert.Id, serverName), 1, (_, old) => old + 1);
        }

        /// <summary>
        /// The rate a raw reading represents, or null when this cycle cannot produce one (no
        /// previous sample, a counter reset, or no elapsed time). Non-cumulative alerts pass
        /// straight through. The arithmetic and the three not-measured cases are
        /// <see cref="AlertEvaluationService.ObserveValue"/>, so the seeder and the evaluator
        /// cannot drift apart on what a rate is.
        /// </summary>
        private async Task<double?> ObserveForBaselineAsync(
            AlertDefinition alert, string serverName, double raw, DateTime nowUtc)
        {
            if (!alert.IsCumulativeCounter) return raw;

            var previous = await _cache.GetLastRawSampleAsync(alert.Id, serverName);
            var observed = AlertEvaluationService.ObserveValue(
                alert, raw, previous?.Raw, previous?.SampledAtUtc, nowUtc);
            await _cache.SaveLastRawSampleAsync(alert.Id, serverName, raw, nowUtc);

            if (observed.Value == null)
                _logger.LogDebug("Baseline seed for {AlertId}/{Server} produced no sample: {Reason}",
                    alert.Id, serverName, observed.NotMeasuredReason);

            return observed.Value;
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

                var raw = Convert.ToDouble(result);
                var now = DateTime.UtcNow;

                // The seeder is a SECOND path into the learned baseline, independent of the
                // evaluator, and it runs the same query. A cumulative counter has to be
                // differenced here too, or the fences this baseline learns are fences over
                // since-startup totals and every later rate sits absurdly below them. It shares
                // the evaluator's stored previous sample deliberately: both read the same counter
                // on the same server, so whichever sampled last is the correct thing to difference
                // against, and the shorter of the two cadences (15 s here, 60-300 s there) only
                // makes the interval tighter.
                await RecordSeedObservationAsync(alert, serverName, raw, now);
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
                var cutoff = DateTime.UtcNow.AddDays(-RetentionDays).ToString("o");
                var rows = await LoadAllSamplesAsync(cutoff);
                var trendCutoff = DateTime.UtcNow.AddHours(-TrendWindowHours);

                // Per-server stats
                var perServer = rows
                    .GroupBy(r => Key(r.AlertId, r.ServerName))
                    .ToList();

                foreach (var g in perServer)
                {
                    var sorted = g.Select(r => r.Value).OrderBy(v => v).ToList();
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
                    var sorted = g.Select(r => r.Value).OrderBy(v => v).ToList();
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

        /// <summary>
        /// Computes the IQR fence pair, in BOTH directions, for one alert/server pair.
        ///
        /// <para>⚠ B, 2026-08-22. This used to compute the UPPER fences only. The evaluator then
        /// applied them through the alert definition's own operator, so for every <c>less_than</c>
        /// alert the learned fence sat ABOVE essentially every sample and <c>value &lt; fence</c>
        /// was true almost always. Worse, the critical fence was further from the data than the
        /// warning fence, so under <c>less_than</c> the critical band was a strict SUPERSET of the
        /// warning band and the computed level was Critical every single time. That is what put
        /// "Buffer Cache Hit Ratio on .\NEW2022 (Critical) - value: 100" in the service log at
        /// 2026-08-22 18:17:39, alongside Page Life Expectancy 410 and Disk Space Low 549762 in
        /// the same second: three less_than alerts, none of them a fixed-threshold breach.</para>
        ///
        /// <para>Both pairs are computed and persisted; <see cref="SelectFences"/> picks the pair
        /// that matches the alert's operator, so the fence is always on the side the alert calls
        /// bad. The upper critical fence is additionally clamped at or beyond the upper warning
        /// fence: warn carries a P95 floor and crit did not, so on a skewed distribution
        /// (P95 above P75 + 3 x IQR) crit landed INSIDE warn and inverted the bands for
        /// greater_than alerts too. Not observed live, asserted anyway.</para>
        /// </summary>
        internal static BaselineStats ComputeStats(
            string alertId,
            string serverName,
            List<double> sorted,
            List<(string AlertId, string ServerName, double Value, DateTime SampledAt)> trendRows)
        {
            var n = sorted.Count;
            var p05 = Percentile(sorted, 0.05);
            var p25 = Percentile(sorted, 0.25);
            var p50 = Percentile(sorted, 0.50);
            var p75 = Percentile(sorted, 0.75);
            var p95 = Percentile(sorted, 0.95);
            var iqr = p75 - p25;

            // Upper fences, for a greater_than alert: Warn = P75 + 1.5 x IQR, Crit = P75 + 3.0 x IQR.
            // Floor warn at P95 so thresholds are never lower than the 95th percentile, then clamp
            // crit at warn so the critical band can never be the wider of the two.
            var warn = Math.Max(p75 + WarnMultiplier * iqr, p95);
            var crit = Math.Max(p75 + CritMultiplier * iqr, warn);

            // Lower fences, for a less_than alert: the mirror image. Cap warn at P05 so it is never
            // higher than the 5th percentile, then clamp crit at or below warn.
            var warnLower = Math.Min(p25 - WarnMultiplier * iqr, p05);
            var critLower = Math.Min(p25 - CritMultiplier * iqr, warnLower);

            // OLS linear regression on recent trend window
            var (slope, rSquared) = ComputeTrend(trendRows);
            var trendCount = trendRows.Count;

            // Slope ratio relative to median (0 guard)
            var slopeRatio = p50 > 0 ? Math.Abs(slope) / p50 : 0;
            // alerts-r2-13: gate on the regression fit as well as the slope magnitude and sample
            // count. Without the rSquared floor a noisy series with no real trend still fires, and a
            // trend fire carries no threshold to sanity-check against.
            var hasTrendShape = trendCount >= TrendMinSamples && rSquared >= TrendMinRSquared;
            var trendWarn = hasTrendShape && slopeRatio >= TrendWarnSlopeRatio;
            var trendCrit = hasTrendShape && slopeRatio >= TrendCritSlopeRatio;

            return new BaselineStats
            {
                AlertId = alertId,
                ServerName = serverName,
                SampleCount = n,
                P05 = p05,
                P25 = p25,
                P50 = p50,
                P75 = p75,
                P95 = p95,
                Iqr = iqr,
                ThresholdWarn = warn,
                ThresholdCrit = crit,
                ThresholdWarnLower = warnLower,
                ThresholdCritLower = critLower,
                LastComputed = DateTime.UtcNow,
                TrendSlopePerHour = slope,
                TrendRSquared = rSquared,
                TrendSampleCount = trendCount,
                IsTrendWarning = trendWarn,
                IsTrendCritical = trendCrit,
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
                sumX += x;
                sumY += y;
                sumXY += x * y;
                sumX2 += x * x;
            }

            var denom = n * sumX2 - sumX * sumX;
            if (Math.Abs(denom) < 1e-12) return (0, 0);

            var slope = (n * sumXY - sumX * sumY) / denom;
            var intercept = (sumY - slope * sumX) / n;

            // R² = 1 - SS_res / SS_tot
            var yMean = sumY / n;
            double ssTot = 0, ssRes = 0;
            foreach (var r in rows)
            {
                var x = (r.SampledAt - origin).TotalHours;
                var yHat = slope * x + intercept;
                ssTot += (r.Value - yMean) * (r.Value - yMean);
                ssRes += (r.Value - yHat) * (r.Value - yHat);
            }

            var rSquared = ssTot < 1e-12 ? 0 : Math.Max(0, 1.0 - ssRes / ssTot);
            return (slope, rSquared);
        }

        private static double Percentile(List<double> sorted, double p)
        {
            if (sorted.Count == 0) return 0;
            if (sorted.Count == 1) return sorted[0];
            var idx = p * (sorted.Count - 1);
            var lo = (int)Math.Floor(idx);
            var hi = (int)Math.Ceiling(idx);
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
                         is_trend_warning, is_trend_critical,
                         p05, threshold_warn_lower, threshold_crit_lower)
                    VALUES
                        (@aid, @srv, @sc, @p25, @p50, @p75, @p95,
                         @iqr, @tw, @tc, @lc,
                         COALESCE((SELECT baseline_locked FROM alert_baseline_stats
                                   WHERE alert_id=@aid AND server_name=@srv), 0),
                         @tslope, @trsq, @tsc, @tiw, @tic,
                         @p05, @twl, @tcl)";
                cmd.Parameters.AddWithValue("@aid", s.AlertId);
                cmd.Parameters.AddWithValue("@srv", s.ServerName);
                cmd.Parameters.AddWithValue("@sc", s.SampleCount);
                cmd.Parameters.AddWithValue("@p05", s.P05);
                cmd.Parameters.AddWithValue("@twl", s.ThresholdWarnLower);
                cmd.Parameters.AddWithValue("@tcl", s.ThresholdCritLower);
                cmd.Parameters.AddWithValue("@p25", s.P25);
                cmd.Parameters.AddWithValue("@p50", s.P50);
                cmd.Parameters.AddWithValue("@p75", s.P75);
                cmd.Parameters.AddWithValue("@p95", s.P95);
                cmd.Parameters.AddWithValue("@iqr", s.Iqr);
                cmd.Parameters.AddWithValue("@tw", s.ThresholdWarn);
                cmd.Parameters.AddWithValue("@tc", s.ThresholdCrit);
                cmd.Parameters.AddWithValue("@lc", s.LastComputed.ToString("o"));
                cmd.Parameters.AddWithValue("@tslope", s.TrendSlopePerHour);
                cmd.Parameters.AddWithValue("@trsq", s.TrendRSquared);
                cmd.Parameters.AddWithValue("@tsc", s.TrendSampleCount);
                cmd.Parameters.AddWithValue("@tiw", s.IsTrendWarning ? 1 : 0);
                cmd.Parameters.AddWithValue("@tic", s.IsTrendCritical ? 1 : 0);
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
                           is_trend_warning, is_trend_critical,
                           p05, threshold_warn_lower, threshold_crit_lower
                    FROM alert_baseline_stats";
                using var r2 = await cmd2.ExecuteReaderAsync();
                while (await r2.ReadAsync())
                {
                    var s = new BaselineStats
                    {
                        AlertId = r2.GetString(0),
                        ServerName = r2.GetString(1),
                        SampleCount = r2.GetInt32(2),
                        P25 = r2.GetDouble(3),
                        P50 = r2.GetDouble(4),
                        P75 = r2.GetDouble(5),
                        P95 = r2.GetDouble(6),
                        Iqr = r2.GetDouble(7),
                        ThresholdWarn = r2.GetDouble(8),
                        ThresholdCrit = r2.GetDouble(9),
                        LastComputed = DateTime.Parse(r2.GetString(10)),
                        TrendSlopePerHour = r2.IsDBNull(11) ? 0 : r2.GetDouble(11),
                        TrendRSquared = r2.IsDBNull(12) ? 0 : r2.GetDouble(12),
                        TrendSampleCount = r2.IsDBNull(13) ? 0 : r2.GetInt32(13),
                        IsTrendWarning = !r2.IsDBNull(14) && r2.GetInt32(14) == 1,
                        IsTrendCritical = !r2.IsDBNull(15) && r2.GetInt32(15) == 1,
                        // B (2026-08-22). A row written before the lower-fence migration carries
                        // the column default 0, and a lower fence of 0 fires nothing until the
                        // next hourly recompute replaces it with a measured one.
                        P05 = r2.IsDBNull(16) ? 0 : r2.GetDouble(16),
                        ThresholdWarnLower = r2.IsDBNull(17) ? 0 : r2.GetDouble(17),
                        ThresholdCritLower = r2.IsDBNull(18) ? 0 : r2.GetDouble(18),
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
        public string AlertId { get; init; } = "";
        public string ServerName { get; init; } = "";
        public int SampleCount { get; init; }
        public double P05 { get; init; }
        public double P25 { get; init; }
        public double P50 { get; init; }
        public double P75 { get; init; }
        public double P95 { get; init; }
        public double Iqr { get; init; }
        /// <summary>UPPER warning fence, for a greater_than alert. See <see cref="AlertBaselineService.SelectFences"/>.</summary>
        public double ThresholdWarn { get; init; }
        /// <summary>UPPER critical fence, clamped at or beyond <see cref="ThresholdWarn"/>.</summary>
        public double ThresholdCrit { get; init; }
        /// <summary>LOWER warning fence, for a less_than alert (B, 2026-08-22).</summary>
        public double ThresholdWarnLower { get; init; }
        /// <summary>LOWER critical fence, clamped at or below <see cref="ThresholdWarnLower"/>.</summary>
        public double ThresholdCritLower { get; init; }
        public DateTime LastComputed { get; init; }
        public bool BaselineLocked { get; init; }
        // Trend detection (OLS linear regression over rolling TrendWindowHours)
        public double TrendSlopePerHour { get; init; }   // units/hour
        public double TrendRSquared { get; init; }   // 0–1 fit quality
        public int TrendSampleCount { get; init; }   // samples used for slope
        public bool IsTrendWarning { get; init; }
        public bool IsTrendCritical { get; init; }
    }
}
