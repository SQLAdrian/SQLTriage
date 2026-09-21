/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
// ⚠ Deliberately NO `using SQLTriage.Data;`. This namespace nests inside it, so QueryExecutor,
// DashboardConfigService, GlobalInstanceSelector and TimeSeriesValueMapper already resolve without
// one. Note that SQLTriage.Data also declares its own LogLevel, and a type reached by namespace
// nesting BEATS one reached by a using — so every LogLevel below is fully qualified.
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// The UI-independent half of retention (DECISIONS 2026-08-26 18:23 ruling 4). Samples every panel
    /// that opted into <c>retainHistory</c> on a timer and prunes retained history to its bounds.
    ///
    /// <para><b>Why a collector at all.</b> The other writer is
    /// <see cref="CachingQueryExecutor"/>, which retains a sample every time a dashboard fetches one.
    /// That alone would make retention a function of who happened to have a browser tab open: a service
    /// install with nobody watching would accrue nothing, and the 7-day baseline would only ever cover
    /// the hours somebody was looking. Both writers target the same bucketed primary key, so they are
    /// idempotent with respect to each other — two writers, no double counting.</para>
    ///
    /// <para><b>Honest scope.</b> This samples the CURRENTLY SELECTED instance, because that is the
    /// server the shared connection factory resolves and the instance key retained history is filed
    /// under. It is not an estate-wide collector. On a multi-server install, history accrues for the
    /// selected server, plus whatever any open dashboard fetches. Widening it to every enabled
    /// connection needs a per-server connection factory and a ruling on the disk that would cost.</para>
    ///
    /// <para>Lifecycle mirrors <see cref="HealthMetricsCollectorService"/>: PeriodicTimer loop,
    /// Start()/Stop(), registered in AddSharedServices and started from BOTH App.xaml.cs and
    /// WindowsServiceHost.cs. Both hosts, or the service install silently has no retention.</para>
    ///
    /// <para><b>Never throws out of the loop, never blocks startup.</b> A collection failure is a debug
    /// line and a gap in the trend, which the panel's own coverage notice then reports honestly. The
    /// prune runs every tick regardless of whether collection succeeded, so a store that is already
    /// over its bounds shrinks even while SQL is unreachable.</para>
    /// </summary>
    public class MetricHistoryCollectorService : IDisposable
    {
        private readonly ILogger<MetricHistoryCollectorService> _logger;
        private readonly DashboardConfigService _configService;
        private readonly QueryExecutor _queryExecutor;
        private readonly liveQueriesCacheStore _cache;
        private readonly GlobalInstanceSelector _instanceSelector;
        private readonly SqlServerConnectionFactory? _connectionFactory;
        private readonly MetricRetentionOptions _options;

        private readonly CancellationTokenSource _cts = new();
        private Task? _loopTask;
        private bool _isRunning;
        private bool _disposed;

        /// <summary>Samples written since start. Read by tests and by the live proof.</summary>
        public int SamplesRetained => _samplesRetained;
        private int _samplesRetained;

        /// <summary>Completed collect+prune cycles since start.</summary>
        public int CyclesCompleted => _cyclesCompleted;
        private int _cyclesCompleted;

        public MetricHistoryCollectorService(
            ILogger<MetricHistoryCollectorService> logger,
            DashboardConfigService configService,
            QueryExecutor queryExecutor,
            liveQueriesCacheStore cache,
            GlobalInstanceSelector instanceSelector,
            MetricRetentionOptions options,
            SqlServerConnectionFactory? connectionFactory = null)
        {
            _logger = logger;
            _configService = configService;
            _queryExecutor = queryExecutor;
            _cache = cache;
            _instanceSelector = instanceSelector;
            _options = options;
            _connectionFactory = connectionFactory;
        }

        public void Start()
        {
            if (_isRunning) return;
            if (!_options.Enabled)
            {
                _logger.LogInformation("Metric history retention is disabled by configuration. No history is retained, and the dashboards will say so rather than draw a trend they do not have");
                return;
            }
            _isRunning = true;
            _loopTask = Task.Run(() => LoopAsync(_cts.Token));
            _logger.LogInformation(
                "Metric history collector started: {PanelCount} panel(s) opted in, {IntervalS}s tick, {BucketS}s buckets, {WindowD}d window, {MaxRows} row cap",
                _configService.GetHistoryRetainedPanelIds().Count,
                (int)_options.CollectInterval.TotalSeconds,
                (int)_options.Bucket.TotalSeconds,
                (int)_options.Window.TotalDays,
                _options.MaxRows);
        }

        public void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;
            _cts.Cancel();
            _logger.LogInformation("Metric history collector stopped");
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(_options.CollectInterval);

            // Prune first. A process that was off for a fortnight comes back with a store full of
            // out-of-window rows, and it should shed them before it adds more.
            try { await PruneAsync(); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _logger.LogWarning(ex, "Initial retained-history prune failed"); }

            // What this process INHERITED, said once, before it has written anything. Retention that
            // does not survive a restart is not retention, so the number that proves it is worth an
            // operator-visible line rather than a debug one.
            try
            {
                var inherited = await _cache.GetMetricHistoryRowCountAsync();
                _logger.LogInformation(
                    "Retained history carried over from previous runs: {Rows} row(s) before this process collected anything",
                    inherited);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not read the inherited retained-history row count"); }

            try { await CollectAllAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _logger.LogDebug(ex, "Initial retained-history collection failed"); }

            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await CollectAllAsync(ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogDebug(ex, "Retained-history collection cycle failed"); }

                try
                {
                    await PruneAsync();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogWarning(ex, "Retained-history prune cycle failed"); }

                Interlocked.Increment(ref _cyclesCompleted);

                // Fully qualified: SQLTriage.Data declares its own LogLevel, and this namespace nests
                // inside it, so the nested type would win over the logging one.
                if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
                {
                    try
                    {
                        _logger.LogDebug(
                            "Retained history after cycle {Cycle}: {Rows} row(s) total, {Samples} sample(s) written by this process",
                            _cyclesCompleted, await _cache.GetMetricHistoryRowCountAsync(), _samplesRetained);
                    }
                    catch { /* telemetry only */ }
                }
            }
        }

        /// <summary>
        /// One sampling pass over every opted-in panel. Public so a test, or the live proof, can drive
        /// a cycle without waiting on a timer.
        /// </summary>
        public async Task<int> CollectAllAsync(CancellationToken ct = default)
        {
            // no-server-idle ruling: with zero servers configured the shared connection factory is
            // Unconfigured, so a sampling pass would only open (and fail) a connection per panel per
            // cycle. Short-circuit to a true no-op instead, and announce the idle posture once.
            if (_connectionFactory?.IsUnconfigured == true)
            {
                SQLTriage.Data.NoServerIdleNotice.AnnounceOnce(_logger);
                return 0;
            }

            var panelIds = _configService.GetHistoryRetainedPanelIds();
            if (panelIds.Count == 0) return 0;

            var instanceKey = CurrentInstanceKey();
            var written = 0;
            foreach (var panelId in panelIds)
            {
                if (ct.IsCancellationRequested) break;
                written += await CollectOneAsync(panelId, instanceKey, ct);
            }
            return written;
        }

        private async Task<int> CollectOneAsync(string panelId, string instanceKey, CancellationToken ct)
        {
            try
            {
                var now = DateTime.Now;
                var filter = new DashboardFilter
                {
                    // A window of two buckets: enough for a query that filters on the range, harmless
                    // for the snapshot queries (their Time column is GETDATE()) that ignore it.
                    TimeFrom = now - _options.Bucket - _options.Bucket,
                    TimeTo = now,
                    Instances = InstanceNames()
                };

                var points = await _queryExecutor.ExecuteQueryAsync(panelId, filter, MapPoint, null, ct);
                points.RemoveAll(p => double.IsNaN(p.Value));
                if (points.Count == 0) return 0;

                var written = await _cache.AppendMetricHistoryAsync(panelId, instanceKey, points, _options.Bucket);
                Interlocked.Add(ref _samplesRetained, written);
                return written;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Unreachable server, missing DMV, no connection configured yet. A gap, not a fault:
                // the panel's coverage notice reports the shortfall rather than hiding it.
                _logger.LogDebug(ex, "Retained-history sample failed for panel {PanelId}", panelId);
                return 0;
            }
        }

        /// <summary>
        /// Prunes to BOTH bounds and logs only when something was actually removed by the row cap —
        /// hitting the cap is the operator-visible fact (history is now shorter than the window
        /// promises), whereas ordinary age pruning is the design working.
        /// </summary>
        public async Task<(int ByAge, int ByCap)> PruneAsync()
        {
            var cutoff = DateTime.UtcNow - _options.Window;
            var result = await _cache.PruneMetricHistoryAsync(cutoff, _options.MaxRows);
            if (result.ByCap > 0)
            {
                _logger.LogWarning(
                    "Retained history hit its {MaxRows}-row cap: {Removed} of the oldest rows were dropped. Retained history is now shorter than the configured {WindowD}-day window. Reduce the number of panels with retainHistory, widen MetricRetention:BucketSeconds, or raise MetricRetention:MaxRows",
                    _options.MaxRows, result.ByCap, (int)_options.Window.TotalDays);
            }
            return result;
        }

        private static TimeSeriesPoint MapPoint(IDataReader reader) => new()
        {
            Time = reader["Time"] as DateTime? ?? DateTime.MinValue,
            Series = reader["Series"]?.ToString() ?? "",
            // Same rule as the dashboard's mapper: a NULL measurement is an unmeasured instant, never
            // a fabricated zero. NaN points are dropped before anything is retained.
            Value = TimeSeriesValueMapper.MapValue(reader["Value"])
        };

        private string[] InstanceNames() => ResolveInstanceNames(
            _instanceSelector.SelectedInstance, _connectionFactory?.ServerName);

        /// <summary>
        /// Which instance name this collector files its samples under.
        ///
        /// <para>⚠ THIS IS THE JOIN. Retained history is keyed by instance, and the dashboard reads it
        /// with <c>BuildInstanceKey(filter)</c> where the filter carries the SELECTED instance. If the
        /// collector picks a different name, every row it writes is filed where nothing looks. The live
        /// proof on 2026-08-26 caught exactly that: headless, nothing had ever set the selector, so the
        /// collector keyed <c>__all__</c> while the dashboard read <c>.</c> — 16 real retained rows and
        /// a panel notice reading "nothing retained yet".</para>
        ///
        /// <para>So: the operator's selection when there is one, and otherwise the server the shared
        /// connection factory is actually about to query, which is the same server the dashboard falls
        /// back to on a single-connection install. The factory's "Unknown" placeholder means it does
        /// not know, and filing rows under a literal called Unknown would be inventing an instance
        /// name, so that keys <c>__all__</c> — what the dashboard builds from an empty instance list.
        /// </para>
        ///
        /// <para><b>"ALL" is not an instance, and this collector cannot key like one.</b> With ALL
        /// selected the dashboard keys on the JOINED list of discovered SQLWATCH instances, a set this
        /// service does not discover and could not reproduce. It samples exactly ONE server, so it
        /// files under that server: the rows then say truthfully which instance they measure. The
        /// consequence is real and is stated on the page rather than hidden — a dashboard left on ALL
        /// finds no retained history under its combined key, and the retention notice says retention is
        /// recorded per server and to select one. Widening the collector to the whole estate is a
        /// ruling, not a builder's call: it needs a per-server connection factory and it multiplies
        /// disk by the size of the estate.</para>
        /// </summary>
        internal static string[] ResolveInstanceNames(string? selected, string? factoryServerName)
        {
            if (!string.IsNullOrWhiteSpace(selected) &&
                !string.Equals(selected, "ALL", StringComparison.OrdinalIgnoreCase))
                return new[] { selected };

            // "ALL" falls through on purpose: it names no instance, so the answer is the server this
            // collector is actually about to query. See the ALL paragraph above.

            if (!string.IsNullOrWhiteSpace(factoryServerName) &&
                !string.Equals(factoryServerName, "Unknown", StringComparison.OrdinalIgnoreCase))
                return new[] { factoryServerName };

            return Array.Empty<string>();
        }

        /// <summary>
        /// The key retained rows are filed under. Built through the SAME
        /// <see cref="CachingQueryExecutor.BuildInstanceKey"/> the dashboards read with — two different
        /// key builders would file history nothing could ever find.
        /// </summary>
        internal string CurrentInstanceKey()
            => CachingQueryExecutor.BuildInstanceKey(new DashboardFilter { Instances = InstanceNames() });

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
