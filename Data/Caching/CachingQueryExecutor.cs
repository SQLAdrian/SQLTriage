/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Caching
{
    /// <summary>
    /// Decorator around <see cref="QueryExecutor"/> that adds local liveQueries caching
    /// with delta-fetch support.
    ///
    /// Caching strategy per panel type:
    ///
    ///   TimeSeries — Delta fetch: on steady-state refresh, only fetch rows newer than
    ///                the last successful fetch, merge into cache, serve full window from cache.
    ///
    ///   StatCard, BarGauge, CheckStatus, DataGrid, TextCard — Always try SQL Server first
    ///                (these are cheap: TOP 1 or small aggregates), cache the result, and
    ///                fall back to the cached value on SQL Server failure.
    /// </summary>
    public class CachingQueryExecutor
    {
        private readonly QueryExecutor _inner;
        private readonly liveQueriesCacheStore _cache;
        private readonly ICacheHotTier _hot;
        private readonly CacheStateTracker _stateTracker;
        private readonly DashboardConfigService _configService;
        private readonly MetricRetentionOptions _retention;
        private readonly TimeSpan _evictionThreshold;
        private readonly SemaphoreSlim _invalidationLock = new(1, 1);
        private readonly ILogger<CachingQueryExecutor> _logger;

        // Single-flight: collapses concurrent identical queries onto one SQL execution (B7).
        // Key = $"{queryId}:{instanceKey}:{shape}". Entries are removed as soon as the
        // underlying task completes, so memory stays bounded by in-flight concurrency.
        private readonly ConcurrentDictionary<string, Task<DataTable>> _inFlightDataTable = new();
        private readonly ConcurrentDictionary<string, Task> _inFlightTyped = new();

        // Last cache tier per (queryId:instanceKey) — read by DynamicDashboard to stamp PanelTrace.
        private readonly ConcurrentDictionary<string, string> _lastTier = new();

        /// <summary>
        /// True when the most recent SQL Server query failed and we are serving stale cached data.
        /// </summary>
        public bool IsServingStaleData => _stateTracker.IsOffline;

        /// <summary>
        /// Timestamp of the last successful SQL Server fetch, displayed when serving stale data.
        /// </summary>
        public DateTime? LastSuccessfulFetch => _stateTracker.LastSuccessfulFetch;

        // ── Telemetry: query source counts (thread-safe) ──────────────────────
        private int _totalQueries;
        private int _freshHits;
        private int _cacheHits;

        /// <summary>Total query executions across all panels since last reset.</summary>
        public int TotalQueries => _totalQueries;

        /// <summary>Number of queries that succeeded against SQL Server (fresh data).</summary>
        public int FreshHits => _freshHits;

        /// <summary>Number of queries served from cache due to SQL failure or delta mode.</summary>
        public int CacheHits => _cacheHits;

        /// <summary>Resets all telemetry counters to zero. Call at start of dashboard load cycle.</summary>
        public void ResetMetrics()
        {
            Interlocked.Exchange(ref _totalQueries, 0);
            Interlocked.Exchange(ref _freshHits, 0);
            Interlocked.Exchange(ref _cacheHits, 0);
        }

        /// <summary>
        /// Returns the cache tier used for the most recent execution of this query+instance.
        /// Values: "Fresh" | "Hot" | "SQLite" | "History" | "None" | "Unknown"
        /// "History" is a retained panel served from metric_history merged with the live cache; it
        /// skips the hot tier deliberately (see ReadRetainedWindowAsync).
        /// Call immediately after ExecuteQueryAsync returns to stamp PanelTrace.CacheHitTier.
        /// </summary>
        public string GetLastTier(string queryId, string instanceKey)
            => _lastTier.TryGetValue($"{queryId}:{instanceKey}", out var t) ? t : "Unknown";

        private void SetTier(string queryId, string instanceKey, string tier)
            => _lastTier[$"{queryId}:{instanceKey}"] = tier;

        /// <summary>
        /// Returns a snapshot of current metrics for logging/display.
        /// </summary>
        public (int total, int fresh, int cached) GetMetrics() =>
            (Interlocked.CompareExchange(ref _totalQueries, 0, 0),
             Interlocked.CompareExchange(ref _freshHits, 0, 0),
             Interlocked.CompareExchange(ref _cacheHits, 0, 0));

        public CachingQueryExecutor(
            QueryExecutor inner,
            liveQueriesCacheStore cache,
            ICacheHotTier hot,
            CacheStateTracker stateTracker,
            DashboardConfigService configService,
            IConfiguration configuration,
            ILogger<CachingQueryExecutor> logger,
            MetricRetentionOptions? retention = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _hot = hot ?? throw new ArgumentNullException(nameof(hot));
            _stateTracker = stateTracker ?? throw new ArgumentNullException(nameof(stateTracker));
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _retention = retention ?? MetricRetentionOptions.FromConfiguration(configuration);

            var hours = configuration.GetValue<int>("CacheEvictionHours", 24);
            _evictionThreshold = TimeSpan.FromHours(hours);
        }

        /// <summary>
        /// True when this panel's samples are written to durable retained history and its window is
        /// served from it. Retention is per-panel opt-in — see <c>MetricRetentionOptions</c>.
        /// </summary>
        public bool RetainsHistory(string queryId)
            => _retention.Enabled && _configService.RetainsHistory(queryId);

        /// <summary>The retention settings in force, so a caller can report the window it promises.</summary>
        public MetricRetentionOptions RetentionOptions => _retention;

        /// <summary>
        /// Writes a fresh batch of TimeSeries points to retained history when the panel opted in.
        /// A retention write failure never fails the panel: the live value is still correct, and the
        /// honesty notice will say the window is short rather than pretending it is whole.
        /// </summary>
        private async Task RetainIfOptedInAsync(string queryId, string instanceKey, List<TimeSeriesPoint> points)
        {
            if (points.Count == 0 || !RetainsHistory(queryId)) return;
            try
            {
                await _cache.AppendMetricHistoryAsync(queryId, instanceKey, points, _retention.Bucket);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not retain history for panel {PanelId} on {InstanceKey}. The panel still shows live values; its trend will have a gap for this interval",
                    queryId, instanceKey);
            }
        }

        /// <summary>
        /// Merges retained history with the volatile cache for one window. History is the record up to
        /// its newest bucket; the cache owns everything from that bucket forward, so the panel shows
        /// the latest reading immediately instead of waiting for the next bucket boundary.
        ///
        /// <para>⚠ THE TWO SIDES ARE ON DIFFERENT TIME BASES, so a de-duplication on (series, instant)
        /// does nothing. Retained rows are floored to a bucket; live rows carry the raw
        /// <c>GETDATE()</c> instant they were read at. They collide only by coincidence. Collapsing
        /// exact instants was therefore the whole guard, and it let every open bucket be plotted twice:
        /// once at the bucket start and again at the raw instant of the same reading, so twelve
        /// measurements rendered as fourteen points.</para>
        ///
        /// <para>The boundary is the cut instead. Per series, the newest retained bucket is where the
        /// cache takes over: retained points before it are the record, live points at or after it are
        /// the fresher view of the same interval, and the retained bucket itself is dropped when live
        /// has anything inside it, because that bucket is still forming and holds a copy of one of
        /// those very samples. A series the cache alone has seen is passed through whole, which is what
        /// a panel looks like in the minute before its first bucket closes.</para>
        ///
        /// <para>A live sample older than the cut is dropped even where retained history has a gap.
        /// The record is what survives a restart, and a line whose density depended on how long the
        /// browser had been open would not be one.</para>
        /// </summary>
        internal static List<TimeSeriesPoint> MergeRetainedWithLive(
            List<TimeSeriesPoint> retained, List<TimeSeriesPoint> live)
        {
            if (live == null || live.Count == 0) return retained ?? new List<TimeSeriesPoint>();
            if (retained == null || retained.Count == 0) return live;

            var cut = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            foreach (var p in retained)
            {
                var key = p.Series ?? "";
                if (!cut.TryGetValue(key, out var newest) || p.Time > newest) cut[key] = p.Time;
            }

            var liveReaches = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in live)
            {
                var key = p.Series ?? "";
                if (cut.TryGetValue(key, out var boundary) && p.Time >= boundary) liveReaches.Add(key);
            }

            var merged = new List<TimeSeriesPoint>(retained.Count + live.Count);
            foreach (var p in retained)
            {
                var key = p.Series ?? "";
                var boundary = cut[key];
                if (p.Time < boundary || !liveReaches.Contains(key)) merged.Add(p);
            }
            foreach (var p in live)
            {
                var key = p.Series ?? "";
                if (!cut.TryGetValue(key, out var boundary) || p.Time >= boundary) merged.Add(p);
            }

            merged.Sort((a, b) => a.Time.CompareTo(b.Time));
            return merged;
        }

        /// <summary>
        /// Serves a retained panel's window from durable history merged with the live cache.
        /// Returns null when nothing at all is available, so the caller falls through to its own
        /// empty-result path.
        ///
        /// <para>⚠ THIS DELIBERATELY SKIPS THE HOT TIER. The in-memory hot entry holds whatever the
        /// last fetch returned — for a delta fetch that is the delta alone, a handful of points — and
        /// the ordinary read path returns it before SQLite is ever consulted. That short-circuit is why
        /// the AG queue panels could never show a trend no matter how much history existed underneath
        /// (dashboards-r2-05). A retained panel reads the durable record instead.</para>
        /// </summary>
        private async Task<List<TimeSeriesPoint>?> ReadRetainedWindowAsync(
            string queryId, string instanceKey, DashboardFilter filter)
        {
            try
            {
                var history = await _cache.GetMetricHistoryAsync(queryId, instanceKey, filter.TimeFrom, filter.TimeTo);

                // The live half is still asked for the whole window. GetTimeSeriesAsync keeps the
                // OLDEST rows when a window overflows the chart cap, which on a long range at a fast
                // refresh returns only samples older than the cut, and the merge then discards them.
                // The retained line is what renders, up to one bucket short of now. That is stale, not
                // wrong, and narrowing this read to the cut would risk shortening a series the cache
                // has seen and history has not. The cap behaviour in that read is base behaviour, not
                // this lane's, and changing it moves all 35 TimeSeries panels.
                var live = await _cache.GetTimeSeriesAsync(queryId, instanceKey, filter.TimeFrom, filter.TimeTo);
                var merged = MergeRetainedWithLive(history, live);
                return merged.Count > 0 ? merged : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read retained history for panel {PanelId}", queryId);
                return null;
            }
        }

        /// <summary>
        /// How much retained history actually backs a panel's requested window. The dashboard turns
        /// this into the notice that keeps the honesty bar: retention makes a trend real over time, it
        /// does not backfill the past.
        /// </summary>
        public Task<RetentionCoverage> GetRetentionCoverageAsync(string queryId, DashboardFilter filter)
            => GetRetentionCoverageAsync(queryId, filter, filter.TimeFrom, filter.TimeTo);

        /// <summary>
        /// The same measurement for an EXPLICIT window instead of the toolbar's. The Baseline overlay
        /// reads a window shifted 7 days back, so coverage of <c>filter.TimeFrom..TimeTo</c> describes a
        /// window the baseline never opened. The gate proved what that costs: a comparison drawn from 23
        /// hours of a 7-day window while every honesty surface stayed silent. The filter still supplies
        /// the instance key, because history is filed per server.
        /// </summary>
        public Task<RetentionCoverage> GetRetentionCoverageAsync(
            string queryId, DashboardFilter filter, DateTime from, DateTime to)
        {
            if (!RetainsHistory(queryId))
                return Task.FromResult(new RetentionCoverage(0, null));
            return _cache.GetMetricHistoryCoverageAsync(queryId, BuildInstanceKey(filter), from, to);
        }

        // ──────────────────────── Refresh Cycle Preparation ─────────────

        /// <summary>
        /// Called once per LoadData() cycle, before any panel queries.
        /// Handles:
        ///   1. Detecting filter changes (time range, instance, or timezone) that require full invalidation.
        ///   2. Periodic cache eviction of very old data.
        /// </summary>
        public async Task PrepareRefreshCycle(string dashboardId, int timeRangeMinutes, string selectedInstance, double timezoneOffsetHours = 0)
        {
            await _invalidationLock.WaitAsync();
            try
            {
                if (_stateTracker.RequiresFullReload(dashboardId, timeRangeMinutes, selectedInstance, timezoneOffsetHours))
                {
                    await _cache.InvalidateAllAsync();
                    _hot.InvalidateAll();
                }
                _stateTracker.RecordFilterState(dashboardId, timeRangeMinutes, selectedInstance, timezoneOffsetHours);
            }
            finally
            {
                _invalidationLock.Release();
            }
        }

        /// <summary>
        /// Runs periodic eviction of cached data older than the configured threshold.
        /// Called by CacheEvictionService on a timer.
        /// </summary>
        public Task EvictStaleDataAsync() => _cache.EvictOlderThanAsync(_evictionThreshold);

        // ──────────────────────── ExecuteQueryAsync (DataTable) ──────────

        /// <summary>
        /// Cached version of <see cref="QueryExecutor.ExecuteQueryAsync(string, DashboardFilter, Dictionary{string, object}?, CancellationToken)"/>.
        /// Used by StatCard, DataGrid, and TextCard panels.
        /// Strategy: try SQL Server, cache result, fall back to cached value on SQL Server failure.
        /// </summary>
        public async Task<DataTable> ExecuteQueryAsync(
            string queryId,
            DashboardFilter filter,
            Dictionary<string, object>? additionalParams = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _totalQueries);
            var instanceKey = BuildInstanceKey(filter);

            // Single-flight: if another caller is already fetching this exact (queryId, instanceKey),
            // wait for its result instead of launching a parallel SQL round-trip. additionalParams
            // participates in the key so distinct parameter sets are not collapsed.
            var flightKey = $"dt:{queryId}:{instanceKey}:{BuildParamKey(additionalParams)}";
            var flight = _inFlightDataTable.GetOrAdd(flightKey,
                _ => ExecuteQueryInternalAsync(queryId, filter, instanceKey, additionalParams, cancellationToken));
            try { return await flight; }
            finally { _inFlightDataTable.TryRemove(flightKey, out _); }
        }

        private async Task<DataTable> ExecuteQueryInternalAsync(
            string queryId,
            DashboardFilter filter,
            string instanceKey,
            Dictionary<string, object>? additionalParams,
            CancellationToken cancellationToken)
        {
            try
            {
                // Always try SQL Server first for DataTable queries (StatCard, DataGrid, TextCard)
                var result = await _inner.ExecuteQueryAsync(queryId, filter, additionalParams, cancellationToken);
                _stateTracker.RecordSuccess();
                Interlocked.Increment(ref _freshHits);
                SetTier(queryId, instanceKey, "Fresh");

                // Cache the result for offline fallback
                await _cache.UpsertDataTableAsync(queryId, instanceKey, result, DateTime.UtcNow);
                await _cache.SetLastFetchTimeAsync(queryId, instanceKey, DateTime.UtcNow);
                await _hot.SetDataTableAsync(queryId, instanceKey, result);
                await _hot.SetLastFetchTimeAsync(queryId, instanceKey, DateTime.UtcNow);

                return result;
            }
            catch (OperationCanceledException)
            {
                throw; // Don't cache cancellation as offline
            }
            catch (Exception ex)
            {
                // SQL Server failed — try serving from cache (hot tier first, then SQLite)
                _stateTracker.RecordFailure();

                var hot = await _hot.GetDataTableAsync(queryId, instanceKey);
                if (hot != null)
                {
                    Interlocked.Increment(ref _cacheHits);
                    SetTier(queryId, instanceKey, "Hot");
                    return hot;
                }

                var cached = await _cache.GetDataTableAsync(queryId, instanceKey);
                if (cached != null)
                {
                    Interlocked.Increment(ref _cacheHits);
                    SetTier(queryId, instanceKey, "SQLite");
                    await _hot.SetDataTableAsync(queryId, instanceKey, cached);
                    return cached;
                }

                SetTier(queryId, instanceKey, "None");
                throw QueryExecutor.ScrubException(ex);
            }
        }

        // ──────────────────────── ExecuteQueryAsync<T> (typed) ──────────

        /// <summary>
        /// Cached version of <see cref="QueryExecutor.ExecuteQueryAsync{T}(string, DashboardFilter, Func{IDataReader, T}, Dictionary{string, object}?, CancellationToken)"/>.
        /// Used by TimeSeries, BarGauge, and CheckStatus panels.
        /// Strategy depends on the panel type (delta for TimeSeries, full-replace for others).
        /// </summary>
        public async Task<List<T>> ExecuteQueryAsync<T>(
            string queryId,
            DashboardFilter filter,
            Func<IDataReader, T> mapper,
            Dictionary<string, object>? additionalParams = null,
            CancellationToken cancellationToken = default)
        {
            var panelType = GetPanelType(queryId);
            var instanceKey = BuildInstanceKey(filter);

            // Single-flight (extends B7 to typed queries) — multiple dashboard
            // tabs requesting the same TimeSeries / BarGauge / CheckStatus
            // panel concurrently used to all fire SQL in parallel on a cold
            // cache. We collapse them onto one in-flight task here.
            //
            // Type-erasure: _inFlightTyped is `ConcurrentDictionary<string,
            // Task>` because we can't key by both a string and an open
            // generic type. The shared task carries the result as object
            // (cast to/from List<T>); the cast is safe because the flight
            // key includes typeof(T).Name, so two callers with different T
            // for the same queryId+instance never share a slot.
            var flightKey = $"typed:{typeof(T).Name}:{queryId}:{instanceKey}:{BuildParamKey(additionalParams)}";

            var flight = (Task<List<T>>)_inFlightTyped.GetOrAdd(flightKey,
                _ => DispatchTypedAsync(queryId, filter, panelType, instanceKey, mapper, additionalParams, cancellationToken));
            try { return await flight; }
            finally { _inFlightTyped.TryRemove(flightKey, out _); }
        }

        private Task<List<T>> DispatchTypedAsync<T>(
            string queryId,
            DashboardFilter filter,
            string panelType,
            string instanceKey,
            Func<IDataReader, T> mapper,
            Dictionary<string, object>? additionalParams,
            CancellationToken cancellationToken) => panelType switch
            {
                "TimeSeries" => DeltaFetchTimeSeriesAsync(queryId, filter, instanceKey, mapper, cancellationToken),
                "BarGauge" => FetchWithFallbackBarGaugeAsync(queryId, filter, instanceKey, mapper, cancellationToken),
                "CheckStatus" => FetchWithFallbackCheckStatusAsync(queryId, filter, instanceKey, mapper, cancellationToken),
                _ => FetchDirectAsync(queryId, filter, mapper, additionalParams, cancellationToken)
            };

        /// <summary>
        /// Cached version of <see cref="QueryExecutor.ExecuteScalarAsync{T}"/>.
        /// Falls back to default(T) on failure if no cache exists.
        /// </summary>
        public async Task<T?> ExecuteScalarAsync<T>(
            string queryId,
            DashboardFilter filter,
            Dictionary<string, object>? additionalParams = null,
            CancellationToken cancellationToken = default)
        {
            // Scalar queries are simple — no caching, just pass through
            return await _inner.ExecuteScalarAsync<T>(queryId, filter, additionalParams, cancellationToken);
        }

        // ──────────────────────── Delta Fetch (TimeSeries) ──────────────

        /// <summary>
        /// Core delta-fetch algorithm for TimeSeries panels:
        ///
        ///   1. Look up last_fetch from cache_metadata.
        ///   2. If no prior fetch → full load from SQL Server, write to cache.
        ///   3. If prior fetch → modify filter.TimeFrom to last_fetch, fetch delta only.
        ///   4. Upsert delta rows into liveQueries.
        ///   5. Trim cache rows older than filter.TimeFrom.
        ///   6. Read full window from liveQueries and return.
        ///   7. On SQL Server failure → serve from cache (stale data).
        /// </summary>
        private async Task<List<T>> DeltaFetchTimeSeriesAsync<T>(
            string queryId,
            DashboardFilter filter,
            string instanceKey,
            Func<IDataReader, T> mapper,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _totalQueries);
            var lastFetch = await _hot.GetLastFetchTimeAsync(queryId, instanceKey)
                ?? await _cache.GetLastFetchTimeAsync(queryId, instanceKey);

            if (lastFetch == null)
            {
                // First fetch ever for this query+instance — full load (fresh)
                Interlocked.Increment(ref _freshHits);
                return await FullFetchTimeSeriesAsync(queryId, filter, instanceKey, mapper, cancellationToken);
            }

            // Delta fetch: only get rows newer than last fetch
            try
            {
                var deltaFilter = new DashboardFilter
                {
                    TimeFrom = lastFetch.Value,
                    TimeTo = filter.TimeTo,
                    Instances = filter.Instances,
                    Database = filter.Database,
                    WaitGrouping = filter.WaitGrouping,
                    AggregationMinutes = filter.AggregationMinutes
                };

                var deltaRows = await _inner.ExecuteQueryAsync(queryId, deltaFilter, mapper, null, cancellationToken);
                // A NULL measurement maps to NaN (see TimeSeriesValueMapper). Drop those points so a
                // null never becomes a fabricated 0, and so NaN never reaches SQLite, whose reader
                // GetDouble would throw on the NULL that a NaN persists as.
                if (deltaRows is List<TimeSeriesPoint> deltaTs) deltaTs.RemoveAll(p => double.IsNaN(p.Value));
                _stateTracker.RecordSuccess();
                Interlocked.Increment(ref _freshHits);
                SetTier(queryId, instanceKey, "Fresh");

                // Convert to TimeSeriesPoint for cache storage
                if (deltaRows.Count > 0 && deltaRows is List<TimeSeriesPoint> tsPoints)
                {
                    await _cache.UpsertTimeSeriesAsync(queryId, instanceKey, tsPoints, DateTime.UtcNow);
                    await _hot.SetTimeSeriesAsync(queryId, instanceKey, tsPoints);
                    // Durable copy for panels that opted into retention. Written BEFORE the trim below,
                    // which is exactly the point: the trim keeps the cache tight to the toolbar window
                    // and would otherwise be the thing that makes a trend impossible.
                    await RetainIfOptedInAsync(queryId, instanceKey, tsPoints);
                }

                await _cache.SetLastFetchTimeAsync(queryId, instanceKey, DateTime.UtcNow);
                await _hot.SetLastFetchTimeAsync(queryId, instanceKey, DateTime.UtcNow);

                // Trim old data outside the current time window
                await _cache.TrimTimeSeriesAsync(queryId, instanceKey, filter.TimeFrom);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // SQL Server failed — fall through to serve from cache
                _stateTracker.RecordFailure();
            }

            // A retained panel serves its window from the durable record, not from the hot tier —
            // see ReadRetainedWindowAsync for why the hot short-circuit made a trend impossible.
            if (typeof(T) == typeof(TimeSeriesPoint) && RetainsHistory(queryId))
            {
                var retainedWindow = await ReadRetainedWindowAsync(queryId, instanceKey, filter);
                if (retainedWindow != null)
                {
                    Interlocked.Increment(ref _cacheHits);
                    SetTier(queryId, instanceKey, "History");
                    return (List<T>)(object)retainedWindow;
                }
            }

            // Serve full window from cache (hot tier first, then SQLite)
            var hotRows = await _hot.GetTimeSeriesAsync(queryId, instanceKey);
            if (hotRows != null && hotRows.Count > 0 && typeof(T) == typeof(TimeSeriesPoint))
            {
                Interlocked.Increment(ref _cacheHits);
                SetTier(queryId, instanceKey, "Hot");
                return (List<T>)(object)hotRows;
            }

            var cachedRows = await _cache.GetTimeSeriesAsync(queryId, instanceKey, filter.TimeFrom, filter.TimeTo);
            if (cachedRows.Count > 0 && typeof(T) == typeof(TimeSeriesPoint))
            {
                Interlocked.Increment(ref _cacheHits);
                SetTier(queryId, instanceKey, "SQLite");
                await _hot.SetTimeSeriesAsync(queryId, instanceKey, cachedRows);
                return (List<T>)(object)cachedRows;
            }

            SetTier(queryId, instanceKey, "None");
            return new List<T>();
        }

        /// <summary>
        /// Full initial fetch for a TimeSeries query. Writes all results to cache.
        /// </summary>
        private async Task<List<T>> FullFetchTimeSeriesAsync<T>(
            string queryId,
            DashboardFilter filter,
            string instanceKey,
            Func<IDataReader, T> mapper,
            CancellationToken cancellationToken)
        {
            try
            {
                var rows = await _inner.ExecuteQueryAsync(queryId, filter, mapper, null, cancellationToken);
                // Drop NaN (NULL-measurement) points before caching/returning, same reasoning as
                // the delta path: a null must not plot as a fabricated 0, and NaN must not reach SQLite.
                if (rows is List<TimeSeriesPoint> fullTs) fullTs.RemoveAll(p => double.IsNaN(p.Value));
                _stateTracker.RecordSuccess();
                Interlocked.Increment(ref _freshHits);
                SetTier(queryId, instanceKey, "Fresh");

                // Cache the results
                if (rows is List<TimeSeriesPoint> tsPoints && tsPoints.Count > 0)
                {
                    await _cache.UpsertTimeSeriesAsync(queryId, instanceKey, tsPoints, DateTime.UtcNow);
                    await _hot.SetTimeSeriesAsync(queryId, instanceKey, tsPoints);
                    await RetainIfOptedInAsync(queryId, instanceKey, tsPoints);
                }
                await _cache.SetLastFetchTimeAsync(queryId, instanceKey, DateTime.UtcNow);
                await _hot.SetLastFetchTimeAsync(queryId, instanceKey, DateTime.UtcNow);

                // A retained panel returns the durable window, not just this cycle's rows. The AG queue
                // queries return ONE sample per call (their Time column is GETDATE()), so returning the
                // fetch verbatim is exactly the thing that made a "trend" a single point.
                if (typeof(T) == typeof(TimeSeriesPoint) && RetainsHistory(queryId))
                {
                    var retainedWindow = await ReadRetainedWindowAsync(queryId, instanceKey, filter);
                    if (retainedWindow != null)
                    {
                        SetTier(queryId, instanceKey, "History");
                        return (List<T>)(object)retainedWindow;
                    }
                }

                return rows;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // SQL Server failed on initial load — check if cache has any data (hot tier first)
                _stateTracker.RecordFailure();

                if (typeof(T) == typeof(TimeSeriesPoint) && RetainsHistory(queryId))
                {
                    var retainedWindow = await ReadRetainedWindowAsync(queryId, instanceKey, filter);
                    if (retainedWindow != null)
                    {
                        Interlocked.Increment(ref _cacheHits);
                        SetTier(queryId, instanceKey, "History");
                        return (List<T>)(object)retainedWindow;
                    }
                }

                var hot = await _hot.GetTimeSeriesAsync(queryId, instanceKey);
                if (hot != null && hot.Count > 0 && typeof(T) == typeof(TimeSeriesPoint))
                {
                    Interlocked.Increment(ref _cacheHits);
                    SetTier(queryId, instanceKey, "Hot");
                    return (List<T>)(object)hot;
                }

                var cached = await _cache.GetTimeSeriesAsync(queryId, instanceKey, filter.TimeFrom, filter.TimeTo);
                if (cached.Count > 0 && typeof(T) == typeof(TimeSeriesPoint))
                {
                    Interlocked.Increment(ref _cacheHits);
                    SetTier(queryId, instanceKey, "SQLite");
                    await _hot.SetTimeSeriesAsync(queryId, instanceKey, cached);
                    return (List<T>)(object)cached;
                }

                SetTier(queryId, instanceKey, "None");
                throw QueryExecutor.ScrubException(ex); // Scrub credentials before propagating
            }
        }

        // ──────────────────────── Fetch-with-Fallback (BarGauge) ────────

        private async Task<List<T>> FetchWithFallbackBarGaugeAsync<T>(
            string queryId,
            DashboardFilter filter,
            string instanceKey,
            Func<IDataReader, T> mapper,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _totalQueries);
            try
            {
                var rows = await _inner.ExecuteQueryAsync(queryId, filter, mapper, null, cancellationToken);
                _stateTracker.RecordSuccess();
                Interlocked.Increment(ref _freshHits);
                SetTier(queryId, instanceKey, "Fresh");

                // Cache for offline fallback
                if (rows is List<StatValue> statRows)
                {
                    await _cache.UpsertBarGaugeAsync(queryId, instanceKey, statRows, DateTime.UtcNow);
                    await _cache.SetLastFetchTimeAsync(queryId, instanceKey, DateTime.UtcNow);
                    await _hot.SetBarGaugeAsync(queryId, instanceKey, statRows);
                    await _hot.SetLastFetchTimeAsync(queryId, instanceKey, DateTime.UtcNow);
                }

                return rows;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // SQL Server failed — try serving from cache (hot tier first)
                _stateTracker.RecordFailure();

                var hot = await _hot.GetBarGaugeAsync(queryId, instanceKey);
                if (hot != null)
                {
                    Interlocked.Increment(ref _cacheHits);
                    SetTier(queryId, instanceKey, "Hot");
                    return (List<T>)(object)hot;
                }

                var cached = await _cache.GetBarGaugeAsync(queryId, instanceKey);
                if (cached != null)
                {
                    Interlocked.Increment(ref _cacheHits);
                    SetTier(queryId, instanceKey, "SQLite");
                    await _hot.SetBarGaugeAsync(queryId, instanceKey, cached);
                    return (List<T>)(object)cached;
                }

                SetTier(queryId, instanceKey, "None");
                throw;
            }
        }

        // ──────────────────────── Fetch-with-Fallback (CheckStatus) ─────

        private async Task<List<T>> FetchWithFallbackCheckStatusAsync<T>(
            string queryId,
            DashboardFilter filter,
            string instanceKey,
            Func<IDataReader, T> mapper,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _totalQueries);
            try
            {
                var rows = await _inner.ExecuteQueryAsync(queryId, filter, mapper, null, cancellationToken);
                _stateTracker.RecordSuccess();
                Interlocked.Increment(ref _freshHits);
                SetTier(queryId, instanceKey, "Fresh");

                // Cache for offline fallback
                if (rows is List<CheckStatus> checkRows)
                {
                    await _cache.UpsertCheckStatusAsync(queryId, instanceKey, checkRows, DateTime.UtcNow);
                    await _cache.SetLastFetchTimeAsync(queryId, instanceKey, DateTime.UtcNow);
                    await _hot.SetCheckStatusAsync(queryId, instanceKey, checkRows);
                    await _hot.SetLastFetchTimeAsync(queryId, instanceKey, DateTime.UtcNow);
                }

                return rows;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // SQL Server failed — try serving from cache (hot tier first)
                _stateTracker.RecordFailure();

                var hot = await _hot.GetCheckStatusAsync(queryId, instanceKey);
                if (hot != null && hot.Count > 0 && typeof(T) == typeof(CheckStatus))
                {
                    Interlocked.Increment(ref _cacheHits);
                    SetTier(queryId, instanceKey, "Hot");
                    return (List<T>)(object)hot;
                }

                var cached = await _cache.GetCheckStatusAsync(queryId, instanceKey);
                if (cached.Count > 0 && typeof(T) == typeof(CheckStatus))
                {
                    Interlocked.Increment(ref _cacheHits);
                    SetTier(queryId, instanceKey, "SQLite");
                    await _hot.SetCheckStatusAsync(queryId, instanceKey, cached);
                    return (List<T>)(object)cached;
                }

                SetTier(queryId, instanceKey, "None");
                throw QueryExecutor.ScrubException(ex);
            }
        }

        // ──────────────────────── Direct Passthrough ────────────────────

        /// <summary>
        /// Passthrough for unknown panel types — no caching.
        /// </summary>
        private async Task<List<T>> FetchDirectAsync<T>(
            string queryId,
            DashboardFilter filter,
            Func<IDataReader, T> mapper,
            Dictionary<string, object>? additionalParams,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _totalQueries);
            Interlocked.Increment(ref _freshHits);
            var instanceKey = BuildInstanceKey(filter);
            SetTier(queryId, instanceKey, "Fresh");
            return await _inner.ExecuteQueryAsync(queryId, filter, mapper, additionalParams, cancellationToken);
        }

        // ──────────────────────── Cache pre-load ────────────────────────

        /// <summary>
        /// Reads whatever is already in SQLite for the given panels — no SQL Server roundtrip.
        /// Returns immediately with stale data so the dashboard can render while a fresh fetch runs.
        /// Any panel with no cached data is simply absent from the returned dictionaries.
        /// </summary>
        public async Task PreloadFromCacheAsync(
            IEnumerable<SQLTriage.Data.Models.PanelDefinition> panels,
            DashboardFilter filter,
            ConcurrentDictionary<string, List<TimeSeriesPoint>> tsResults,
            ConcurrentDictionary<string, StatValue> statResults,
            ConcurrentDictionary<string, List<StatValue>> bgResults,
            ConcurrentDictionary<string, DataTable> gridResults,
            ConcurrentDictionary<string, List<CheckStatus>> checkResults)
        {
            var instanceKey = BuildInstanceKey(filter);

            var tasks = panels.Select(async panel =>
            {
                try
                {
                    switch (panel.PanelType)
                    {
                        case "TimeSeries":
                            {
                                var from = filter.TimeFrom == default ? DateTime.UtcNow.AddHours(-1) : filter.TimeFrom;
                                var to = filter.TimeTo == default ? DateTime.UtcNow : filter.TimeTo;
                                // A retained panel preloads its durable window, and skips the hot tier
                                // for the same reason the live read does — a delta-sized hot entry
                                // would render as the whole trend.
                                List<TimeSeriesPoint>? pts;
                                if (RetainsHistory(panel.Id))
                                {
                                    pts = await ReadRetainedWindowAsync(panel.Id, instanceKey,
                                        new DashboardFilter { TimeFrom = from, TimeTo = to, Instances = filter.Instances });
                                }
                                else
                                {
                                    pts = await _hot.GetTimeSeriesAsync(panel.Id, instanceKey)
                                        ?? await _cache.GetTimeSeriesAsync(panel.Id, instanceKey, from, to);
                                }
                                if (pts?.Count > 0) tsResults[panel.Id] = pts;
                                break;
                            }
                        case "StatCard":
                        case "DeltaStatCard":
                            {
                                var dt = await _hot.GetDataTableAsync(panel.Id, instanceKey)
                                    ?? await _cache.GetDataTableAsync(panel.Id, instanceKey);
                                if (dt != null && dt.Rows.Count > 0)
                                {
                                    var row = dt.Rows[0];
                                    double val = 0;
                                    if (dt.Columns.Count > 0 && row[0] != DBNull.Value)
                                        double.TryParse(row[0]?.ToString(), out val);
                                    statResults[panel.Id] = new StatValue { Value = val };
                                }
                                break;
                            }
                        case "BarGauge":
                            {
                                var bg = await _hot.GetBarGaugeAsync(panel.Id, instanceKey)
                                    ?? await _cache.GetBarGaugeAsync(panel.Id, instanceKey);
                                if (bg?.Count > 0) bgResults[panel.Id] = bg;
                                break;
                            }
                        case "DataGrid":
                            {
                                var dt = await _hot.GetDataTableAsync(panel.Id, instanceKey)
                                    ?? await _cache.GetDataTableAsync(panel.Id, instanceKey);
                                if (dt != null) gridResults[panel.Id] = dt;
                                break;
                            }
                        case "CheckStatus":
                            {
                                var cs = await _hot.GetCheckStatusAsync(panel.Id, instanceKey)
                                    ?? await _cache.GetCheckStatusAsync(panel.Id, instanceKey);
                                if (cs?.Count > 0) checkResults[panel.Id] = cs;
                                break;
                            }
                    }
                }
                catch { /* non-fatal — panel stays empty until fresh fetch */ }
            });

            await Task.WhenAll(tasks);
        }

        // ──────────────────────── Helpers ───────────────────────────────

        /// <summary>
        /// Determines the panel type for a given queryId using the O(1) cache in DashboardConfigService.
        /// </summary>
        private string GetPanelType(string queryId) => _configService.GetPanelType(queryId);

        /// <summary>
        /// Builds a consistent cache key from the instance selection in the filter.
        /// Sorts instance names alphabetically to ensure the same set always maps
        /// to the same key regardless of ordering.
        /// </summary>
        public static string BuildInstanceKey(DashboardFilter filter)
        {
            if (filter.Instances == null || filter.Instances.Length == 0)
                return "__all__";

            var sorted = filter.Instances
                .OrderBy(i => i, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            // 2026-07-21: joined with ',' until now, which made the key ambiguous the moment any
            // instance carried a port — {"A", "B,1433"} and {"A", "B", "1433"} produced the SAME
            // key, and CapacityCollector.InstanceKeyMatchesServer (its only parser) could not tell
            // them apart. ServerAddress.KeySeparator ('|') cannot occur in a validated server name,
            // so the key round-trips exactly.
            return SQLTriage.Data.Services.ServerAddress.JoinKey(sorted);
        }

        /// <summary>
        /// Stable key for additionalParams so single-flight does not collapse
        /// queries that differ only in parameter values.
        /// </summary>
        private static string BuildParamKey(Dictionary<string, object>? additionalParams)
        {
            if (additionalParams == null || additionalParams.Count == 0) return "-";
            var sb = new System.Text.StringBuilder();
            foreach (var kv in additionalParams.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
            }
            return sb.ToString();
        }
    }
}
