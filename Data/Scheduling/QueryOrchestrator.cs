/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Scheduling
{
    /// <summary>
    /// Unified query orchestrator implementing priority-queue scheduling,
    /// global + per-server concurrency caps, latency-aware metrics,
    /// and graceful backpressure.
    ///
    /// Replaces the fragmented scheduler landscape:
    ///   - QueryScheduler (dashboard queries)
    ///   - AlertEvaluationService._querySemaphore (alert throttling)
    ///   - ScheduledTaskEngine._taskSemaphore (task throttling)
    ///
    /// All work is enqueued through a single dispatcher with 5 priority levels.
    /// </summary>
    public sealed class QueryOrchestrator : IQueryOrchestrator, IDisposable
    {
        private readonly ILogger<QueryOrchestrator> _logger;
        private readonly IConfiguration _configuration;
        private readonly QueryRegistry _registry;

        // ── Configuration ──────────────────────────────────────────────
        private int _globalConcurrency;
        private int _perServerConcurrency;
        private readonly int _channelCapacity;
        private readonly TimeSpan _defaultTimeout;

        // ── Priority channels ──────────────────────────────────────────
        // One bounded channel per priority level. P0 gets smallest capacity
        // but highest drain priority; P4 gets largest capacity.
        private readonly Channel<QueuedRequest>[] _channels;
        private static readonly int PriorityCount = Enum.GetValues<QueryPriority>().Length;

        // ── Concurrency control ────────────────────────────────────────
        private SemaphoreSlim _globalSemaphore;
        private SemaphoreSlim[] _serverSemaphores; // striped locks
        private const int ServerStripeCount = 64;

        // ── Dispatcher lifecycle ───────────────────────────────────────
        private readonly CancellationTokenSource _cts = new();
        private Task? _dispatcherTask;
        private bool _started;
        private bool _disposed;

        // ── Metrics ────────────────────────────────────────────────────
        private readonly ConcurrentQueue<LatencySample> _latencyWindow = new();
        private readonly TimeSpan _metricsWindow = TimeSpan.FromMinutes(1);
        private int _totalEnqueued;
        private int _totalCompleted;
        private int _totalFailed;
        private int _inFlightCount;

        // Track in-flight fire-and-forget tasks so StopAsync can await them
        private readonly ConcurrentDictionary<Task, byte> _inFlightTasks = new();

        // ── Types ──────────────────────────────────────────────────────
        private sealed class QueuedRequest
        {
            public required QueryRequest Request { get; init; }
            public required QueryPriority Priority { get; init; }
            public required TaskCompletionSource<QueryResult> Completion { get; init; }
            public DateTime EnqueuedAt { get; init; }
        }

        private sealed class LatencySample
        {
            public required string QueryId { get; init; }
            public required string ServerName { get; init; }
            public required TimeSpan Latency { get; init; }
            public required bool Success { get; init; }
            public DateTime Timestamp { get; init; }
        }

        public QueryOrchestrator(ILogger<QueryOrchestrator> logger, IConfiguration configuration, QueryRegistry registry)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));

            _globalConcurrency = configuration.GetValue<int>("Orchestrator:GlobalConcurrency", 50);
            _perServerConcurrency = configuration.GetValue<int>("Orchestrator:PerServerConcurrency", 50);
            _channelCapacity = configuration.GetValue<int>("Orchestrator:ChannelCapacity", 1000);
            _defaultTimeout = TimeSpan.FromSeconds(configuration.GetValue<int>("Orchestrator:DefaultTimeoutSeconds", 60));

            _globalSemaphore = new SemaphoreSlim(_globalConcurrency, _globalConcurrency);
            _serverSemaphores = new SemaphoreSlim[ServerStripeCount];
            for (int i = 0; i < ServerStripeCount; i++)
                _serverSemaphores[i] = new SemaphoreSlim(_perServerConcurrency, _perServerConcurrency);

            // P0 channel: smallest capacity, highest priority drain
            // P4 channel: largest capacity, lowest priority drain
            _channels = new Channel<QueuedRequest>[PriorityCount];
            for (int p = 0; p < PriorityCount; p++)
            {
                var capacity = p == 0 ? Math.Max(50, _channelCapacity / 20)
                    : p == (PriorityCount - 1) ? _channelCapacity
                    : _channelCapacity / (PriorityCount - p);

                _channels[p] = Channel.CreateBounded<QueuedRequest>(new BoundedChannelOptions(capacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false
                });
            }

            _logger.LogInformation(
                "QueryOrchestrator configured: globalConcurrency={Global}, perServerConcurrency={PerServer}, channelCapacity={Capacity}",
                _globalConcurrency, _perServerConcurrency, _channelCapacity);
        }

        // ── IQueryOrchestrator ─────────────────────────────────────────

        public Task<QueryResult> EnqueueAsync(QueryRequest request, QueryPriority priority, CancellationToken cancellationToken = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(QueryOrchestrator));
            if (!_started) throw new InvalidOperationException("Orchestrator not started. Call Start() first.");

            var tcs = new TaskCompletionSource<QueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var queued = new QueuedRequest
            {
                Request = request,
                Priority = priority,
                Completion = tcs,
                EnqueuedAt = DateTime.UtcNow
            };

            Interlocked.Increment(ref _totalEnqueued);

            // Write to the appropriate priority channel.
            // BoundedChannelFullMode.Wait means this blocks until space is available (backpressure).
            _ = WriteChannelAsync(priority, queued, cancellationToken);

            return tcs.Task;
        }

        private async Task WriteChannelAsync(QueryPriority priority, QueuedRequest request, CancellationToken cancellationToken)
        {
            try
            {
                await _channels[(int)priority].Writer.WriteAsync(request, cancellationToken);
            }
            catch (ChannelClosedException)
            {
                request.Completion.TrySetException(new InvalidOperationException("Orchestrator is shutting down."));
            }
            catch (OperationCanceledException)
            {
                request.Completion.TrySetCanceled();
            }
        }

        public Task<OrchestratorHealth> GetHealthAsync(CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            TrimLatencyWindow(now);

            var completedLastMinute = _latencyWindow.Count(s => s.Timestamp > now.AddMinutes(-1));
            var failedLastMinute = _latencyWindow.Count(s => !s.Success && s.Timestamp > now.AddMinutes(-1));
            var avgLatency = _latencyWindow.Any()
                ? TimeSpan.FromMilliseconds(_latencyWindow.Average(s => s.Latency.TotalMilliseconds))
                : TimeSpan.Zero;

            var queueDepth = _channels.Sum(c => c.Reader.Count);
            var inFlight = Interlocked.CompareExchange(ref _inFlightCount, 0, 0);

            return Task.FromResult(new OrchestratorHealth
            {
                QueueDepth = queueDepth,
                InFlightCount = inFlight,
                CompletedLastMinute = completedLastMinute,
                FailedLastMinute = failedLastMinute,
                AverageLatencyLastMinute = avgLatency,
                IsHealthy = queueDepth < _channelCapacity && inFlight < _globalConcurrency,
                Timestamp = now
            });
        }

        public Task<OrchestratorMetrics> GetMetricsAsync(CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            TrimLatencyWindow(now);

            var countByPriority = new Dictionary<QueryPriority, int>();
            foreach (QueryPriority p in Enum.GetValues<QueryPriority>())
                countByPriority[p] = _latencyWindow.Count(s => s.QueryId.StartsWith(p.ToString()));

            var avgByServer = _latencyWindow
                .GroupBy(s => s.ServerName)
                .ToDictionary(
                    g => g.Key,
                    g => TimeSpan.FromMilliseconds(g.Average(s => s.Latency.TotalMilliseconds)));

            return Task.FromResult(new OrchestratorMetrics
            {
                TotalEnqueued = Interlocked.CompareExchange(ref _totalEnqueued, 0, 0),
                TotalCompleted = Interlocked.CompareExchange(ref _totalCompleted, 0, 0),
                TotalFailed = Interlocked.CompareExchange(ref _totalFailed, 0, 0),
                CountByPriority = countByPriority,
                AverageLatencyByServer = avgByServer,
                WindowStart = now.AddMinutes(-1),
                WindowEnd = now
            });
        }

        public void Start()
        {
            if (_started) return;
            _started = true;
            _dispatcherTask = Task.Run(DispatcherLoopAsync);
            _logger.LogInformation("QueryOrchestrator dispatcher started");
        }

        private async Task TrackWorkAsync(QueuedRequest request)
        {
            var t = Task.Run(async () => await ExecuteWorkAsync(request), _cts.Token);
            _inFlightTasks.TryAdd(t, 0);
            try { await t; }
            finally { _inFlightTasks.TryRemove(t, out _); }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (!_started) return;
            _cts.Cancel();

            // Signal completion to all channels so waiting writers unblock
            foreach (var ch in _channels)
                ch.Writer.Complete();

            if (_dispatcherTask != null)
            {
                try { await _dispatcherTask.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken); }
                catch (TimeoutException) { _logger.LogWarning("Dispatcher loop did not stop within 30s"); }
            }

            // Wait for in-flight work to complete (up to 10s)
            var inFlight = _inFlightTasks.Keys.ToArray();
            if (inFlight.Length > 0)
            {
                try { await Task.WhenAll(inFlight).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
                catch (TimeoutException) { _logger.LogWarning("{Count} in-flight queries did not complete within 10s", inFlight.Length); }
            }

            _logger.LogInformation("QueryOrchestrator stopped");
        }

        // ── Dispatcher loop ────────────────────────────────────────────

        private async Task DispatcherLoopAsync()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var request = await DequeueNextAsync(_cts.Token);
                    if (request == null)
                    {
                        await Task.Delay(10, _cts.Token);
                        continue;
                    }

                    _ = TrackWorkAsync(request);
                }
                catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Dispatcher loop error");
                    await Task.Delay(100, _cts.Token);
                }
            }
        }

        /// <summary>
        /// Drains channels in strict priority order: P0 first, then P1, etc.
        /// If a higher-priority channel has items, lower-priority channels starve
        /// until the higher channel is empty. This is intentional — dashboard queries
        /// must never wait behind prefetch work.
        /// </summary>
        private async Task<QueuedRequest?> DequeueNextAsync(CancellationToken cancellationToken)
        {
            // Try each priority channel in order
            for (int p = 0; p < PriorityCount; p++)
            {
                if (_channels[p].Reader.TryRead(out var request))
                    return request;
            }

            // Nothing immediately available — wait on the highest-priority channel
            // that might receive work soon. Fall back to lower channels with timeout.
            for (int p = 0; p < PriorityCount; p++)
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                try
                {
                    return await _channels[p].Reader.ReadAsync(linked.Token);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    // Timeout on this channel — try next lower priority
                    continue;
                }
            }

            return null;
        }

        // ── Execution ──────────────────────────────────────────────────

        private async Task ExecuteWorkAsync(QueuedRequest queued)
        {
            var req = queued.Request;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var timeout = req.Timeout ?? _defaultTimeout;

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                req.CancellationToken, timeoutCts.Token, _cts.Token);
            var token = linkedCts.Token;

            // Acquire global concurrency slot
            if (!await _globalSemaphore.WaitAsync(timeout, token))
            {
                var result = CreateResult(queued, sw.Elapsed, new TimeoutException("Global concurrency slot unavailable within timeout."));
                queued.Completion.TrySetResult(result);
                RecordLatency(queued, result);
                return;
            }

            Interlocked.Increment(ref _inFlightCount);

            // Acquire per-server semaphore(s)
            var serverSemaphores = AcquireServerSemaphores(req.ServerNames);
            foreach (var sem in serverSemaphores)
            {
                if (!await sem.WaitAsync(timeout, token))
                {
                    ReleaseServerSemaphores(serverSemaphores);
                    _globalSemaphore.Release();
                    Interlocked.Decrement(ref _inFlightCount);
                    var result = CreateResult(queued, sw.Elapsed, new TimeoutException("Per-server concurrency slot unavailable within timeout."));
                    queued.Completion.TrySetResult(result);
                    RecordLatency(queued, result);
                    return;
                }
            }

            try
            {
                await req.Work(token);
                sw.Stop();
                var result = CreateResult(queued, sw.Elapsed, null);
                queued.Completion.TrySetResult(result);
                RecordLatency(queued, result);
                req.OnComplete?.Invoke(result);
            }
            catch (OperationCanceledException) when (req.CancellationToken.IsCancellationRequested)
            {
                sw.Stop();
                queued.Completion.TrySetCanceled();
                RecordLatency(queued, CreateResult(queued, sw.Elapsed, null, success: false));
            }
            catch (Exception ex)
            {
                sw.Stop();
                var result = CreateResult(queued, sw.Elapsed, ex);
                queued.Completion.TrySetResult(result);
                RecordLatency(queued, result);
                req.OnComplete?.Invoke(result);
            }
            finally
            {
                ReleaseServerSemaphores(serverSemaphores);
                _globalSemaphore.Release();
                Interlocked.Decrement(ref _inFlightCount);
            }
        }

        // ── Helpers ────────────────────────────────────────────────────

        private SemaphoreSlim[] AcquireServerSemaphores(IReadOnlyList<string> serverNames)
        {
            var semaphores = _serverSemaphores; // local snapshot for thread safety during updates
            if (serverNames.Count == 0)
                return new[] { semaphores[0] }; // default stripe

            var seen = new HashSet<int>();
            var list = new List<SemaphoreSlim>();
            foreach (var name in serverNames)
            {
                var idx = GetServerStripeIndex(name);
                if (seen.Add(idx))
                    list.Add(semaphores[idx]);
            }
            return list.ToArray();
        }

        private void ReleaseServerSemaphores(SemaphoreSlim[] semaphores)
        {
            foreach (var sem in semaphores)
            {
                try { sem.Release(); }
                catch (SemaphoreFullException) { /* already at max */ }
            }
        }

        private static int GetServerStripeIndex(string serverName)
        {
            var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(serverName ?? "default");
            return (hash & 0x7FFFFFFF) % ServerStripeCount;
        }

        private static QueryResult CreateResult(QueuedRequest queued, TimeSpan duration, Exception? ex, bool? success = null)
        {
            var isSuccess = success ?? (ex == null);
            return new QueryResult
            {
                QueryId = queued.Request.QueryId,
                Success = isSuccess,
                Duration = duration,
                Exception = ex
            };
        }

        private void RecordLatency(QueuedRequest queued, QueryResult result)
        {
            if (result.Success)
                Interlocked.Increment(ref _totalCompleted);
            else
                Interlocked.Increment(ref _totalFailed);

            var server = queued.Request.ServerNames.FirstOrDefault() ?? "default";
            _latencyWindow.Enqueue(new LatencySample
            {
                QueryId = queued.Request.QueryId,
                ServerName = server,
                Latency = result.Duration,
                Success = result.Success,
                Timestamp = DateTime.UtcNow
            });

            // Feed latency into QueryRegistry for affinity grouping and historical tracking
            try { _registry.RecordExecution(queued.Request.QueryId, result.Duration.TotalMilliseconds, queued.Request.SqlText); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to record execution in QueryRegistry"); }

            TrimLatencyWindow(DateTime.UtcNow);
        }

        private void TrimLatencyWindow(DateTime now)
        {
            var cutoff = now.Add(-_metricsWindow);
            while (_latencyWindow.TryPeek(out var sample) && sample.Timestamp < cutoff)
                _latencyWindow.TryDequeue(out _);
        }

        public void UpdateLimits(int globalConcurrency, int perServerConcurrency)
        {
            if (globalConcurrency <= 0) throw new ArgumentOutOfRangeException(nameof(globalConcurrency));
            if (perServerConcurrency <= 0) throw new ArgumentOutOfRangeException(nameof(perServerConcurrency));

            var newGlobal = new SemaphoreSlim(globalConcurrency, globalConcurrency);
            var newServer = new SemaphoreSlim[ServerStripeCount];
            for (int i = 0; i < ServerStripeCount; i++)
                newServer[i] = new SemaphoreSlim(perServerConcurrency, perServerConcurrency);

            var oldGlobal = Interlocked.Exchange(ref _globalSemaphore, newGlobal);
            var oldServer = Interlocked.Exchange(ref _serverSemaphores, newServer);

            _globalConcurrency = globalConcurrency;
            _perServerConcurrency = perServerConcurrency;

            oldGlobal?.Dispose();
            if (oldServer != null)
            {
                foreach (var sem in oldServer) sem?.Dispose();
            }

            _logger.LogInformation("QueryOrchestrator limits updated: global={Global}, perServer={PerServer}", globalConcurrency, perServerConcurrency);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _cts.Cancel();
            _cts.Dispose();
            _globalSemaphore.Dispose();
            foreach (var sem in _serverSemaphores) sem.Dispose();
        }
    }
}
