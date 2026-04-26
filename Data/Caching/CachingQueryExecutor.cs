/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SQLTriage.Data.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

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
        private readonly TimeSpan _evictionThreshold;
        private readonly SemaphoreSlim _invalidationLock = new(1, 1);
        private readonly ILogger<CachingQueryExecutor> _logger;
        private readonly long _memoryThresholdBytes;

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
        /// Values: "Fresh" | "Hot" | "SQLite" | "None" | "Unknown"
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
            ILogger<CachingQueryExecutor> logger)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _hot = hot ?? throw new ArgumentNullException(nameof(hot));
            _stateTracker = stateTracker ?? throw new ArgumentNullException(nameof(stateTracker));
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            var hours = configuration.GetValue<int>("CacheEvictionHours", 24);
            _evictionThreshold = TimeSpan.FromHours(hours);

            // Memory threshold: default 10% of MaxCacheSizeMB, or 50MB minimum
            var maxCacheBytes = configuration.GetValue<long>("MaxCacheSizeMB", 100) * 1024 * 1024;
            _memoryThresholdBytes = Math.Max(maxCacheBytes / 10, 50 * 1024 * 1024); // 10% or 50MB
        }

        /// <summary>
        /// Checks if memory pressure is high and evicts stale data proactively
        /// </summary>
        private async Task CheckMemoryPressureAsync()
        {
            var workingSet = GC.GetTotalMemory(false);
            if (workingSet > _memoryThresholdBytes)
            {
                _logger.LogWarning("Memory pressure detected ({WorkingSet:N0} bytes). Triggering aggressive cache eviction.", workingSet);
                await _cache.EvictOlderThanAsync(TimeSpan.FromHours(6)); // Keep last 6 hours under memory pressure
                GC.Collect(2, GCCollectionMode.Aggressive, true);
            }
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
                // Check memory pressure before refresh cycle
                await CheckMemoryPressureAsync();

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
                _stateTracker.RecordSuccess();
                Interlocked.Increment(ref _freshHits);
                SetTier(queryId, instanceKey, "Fresh");

                // Convert to TimeSeriesPoint for cache storage
                if (deltaRows.Count > 0 && deltaRows is List<TimeSeriesPoint> tsPoints)
                {
                    await _cache.UpsertTimeSeriesAsync(queryId, instanceKey, tsPoints, DateTime.UtcNow);
                    await _hot.SetTimeSeriesAsync(queryId, instanceKey, tsPoints);
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
                _stateTracker.RecordSuccess();
                Interlocked.Increment(ref _freshHits);
                SetTier(queryId, instanceKey, "Fresh");

                // Cache the results
                if (rows is List<TimeSeriesPoint> tsPoints && tsPoints.Count > 0)
                {
                    await _cache.UpsertTimeSeriesAsync(queryId, instanceKey, tsPoints, DateTime.UtcNow);
                    await _hot.SetTimeSeriesAsync(queryId, instanceKey, tsPoints);
                }
                await _cache.SetLastFetchTimeAsync(queryId, instanceKey, DateTime.UtcNow);
                await _hot.SetLastFetchTimeAsync(queryId, instanceKey, DateTime.UtcNow);

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
                            var to   = filter.TimeTo   == default ? DateTime.UtcNow               : filter.TimeTo;
                            var pts = await _hot.GetTimeSeriesAsync(panel.Id, instanceKey)
                                ?? await _cache.GetTimeSeriesAsync(panel.Id, instanceKey, from, to);
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
            return string.Join(",", sorted);
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
