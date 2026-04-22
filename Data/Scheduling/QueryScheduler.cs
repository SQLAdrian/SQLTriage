/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SQLTriage.Data;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Scheduling
{
    /// <summary>
    /// Adaptive query scheduler with affinity-aware grouping, latency-based pacing,
    /// per-server concurrency caps, and state persistence. Replaces hard throttling with intelligent batching.
    /// </summary>
    public class QueryScheduler : IDisposable
    {
        private readonly ILogger<QueryScheduler> _logger;
        private readonly IConfiguration _configuration;
        private readonly QueryRegistry _registry;
        private readonly Channel<QueuedQuery> _channel;
        private readonly CancellationTokenSource _cts;
        private readonly Task _processorTask;

        // Global concurrency limit (sum across all servers)
        private int _maxConcurrentQueries;
        private int _configuredLimit;
        private double _minPeriodSec;
        private double _maxPeriodSec;
        private int _maxBatchWaitMs;

        // Per-server concurrency
        private int _serverBaseMax;
        private double _serverBurstMultiplier = 1.0;
        private int _serverCurrentMax;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _serverSemaphores = new();

        // Burst mode support
        private Timer? _burstTimer;
        private readonly object _burstLock = new();

        private volatile bool _disposed;

        public QueryScheduler(
            ILogger<QueryScheduler> logger,
            IConfiguration configuration,
            QueryRegistry registry,
            UserSettingsService userSettings)
        {
            _logger = logger;
            _configuration = configuration;
            _registry = registry;

            LoadConfiguration();

            // Global limit from user settings (heavy + light)
            var savedHeavy = userSettings.GetMaxHeavyConcurrent();
            var savedLight = userSettings.GetMaxLightConcurrent();
            _configuredLimit = savedHeavy + savedLight;
            _maxConcurrentQueries = Math.Max(_maxConcurrentQueries, _configuredLimit);

            // Per-server limit from user settings
            _serverBaseMax = userSettings.GetMaxConcurrentPerServer();
            _serverCurrentMax = _serverBaseMax;

            _channel = Channel.CreateUnbounded<QueuedQuery>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
            _cts = new CancellationTokenSource();
            _processorTask = Task.Run(ProcessingLoopAsync);

            // Apply burst mode on startup if enabled
            if (userSettings.GetEnableBurstMode())
            {
                var mult = userSettings.GetBurstConcurrencyMultiplier();
                var dur = userSettings.GetBurstDurationSec();
                StartBurst(mult, dur);
            }
        }

        private void LoadConfiguration()
        {
            _maxConcurrentQueries = _configuration.GetValue<int>("LoadBalancing:MaxConcurrentBatches", 50);
            _minPeriodSec = _configuration.GetValue<double>("LoadBalancing:MinPeriodSec", 0.25);
            _maxPeriodSec = _configuration.GetValue<double>("LoadBalancing:MaxPeriodSec", 60.0);
            _maxBatchWaitMs = _configuration.GetValue<int>("LoadBalancing:MaxBatchWaitMs", 100);
        }

        // --- Global concurrency control ---

        public void UpdateQueryLimit(int newMax)
        {
            if (newMax <= 0) throw new ArgumentOutOfRangeException(nameof(newMax));

            lock (_burstLock)
            {
                _configuredLimit = newMax;
                CancelBurstTimer();
                ApplyGlobalLimit(newMax);
            }

            _logger.LogInformation("[QueryScheduler] Global concurrency limit updated to {Limit}", newMax);
        }

        private void ApplyGlobalLimit(int limit)
        {
            _maxConcurrentQueries = limit;
            // No need to replace semaphore if we're not storing it in field? Actually we store _querySemaphore.
            // We'll replace the semaphore instance atomically.
            var newSem = new SemaphoreSlim(limit, limit);
            Interlocked.Exchange(ref _querySemaphore, newSem);
        }

        // --- Per-server concurrency control ---

        private SemaphoreSlim GetOrCreateServerSemaphore(string serverName)
        {
            return _serverSemaphores.GetOrAdd(serverName, _ => new SemaphoreSlim(_serverCurrentMax, _serverCurrentMax));
        }

        private void RefreshServerSemaphoreCaps()
        {
            var newCap = _serverCurrentMax;
            foreach (var key in _serverSemaphores.Keys.ToList())
            {
                _serverSemaphores[key] = new SemaphoreSlim(newCap, newCap);
            }
        }

        public void UpdateServerBaseMax(int newBase)
        {
            if (newBase <= 0) throw new ArgumentOutOfRangeException(nameof(newBase));
            lock (_burstLock)
            {
                _serverBaseMax = newBase;
                var effective = (int)Math.Round(_serverBaseMax * _serverBurstMultiplier);
                _serverCurrentMax = effective;
                RefreshServerSemaphoreCaps();
            }
            _logger.LogInformation("[QueryScheduler] Per-server base cap updated to {Base}, effective {Effective}", newBase, _serverCurrentMax);
        }

        // --- Burst mode ---

        public void StartBurst(double multiplier, int durationSec)
        {
            if (multiplier < 1.0) throw new ArgumentOutOfRangeException(nameof(multiplier));
            if (durationSec <= 0) throw new ArgumentOutOfRangeException(nameof(durationSec));

            lock (_burstLock)
            {
                _serverBurstMultiplier = multiplier;
                var burstServerCap = (int)Math.Round(_serverBaseMax * multiplier);
                _serverCurrentMax = burstServerCap;
                RefreshServerSemaphoreCaps();

                var burstGlobal = (int)Math.Round(_configuredLimit * multiplier);
                ApplyGlobalLimit(burstGlobal);

                _logger.LogInformation("[QueryScheduler] Burst started: Global={Global}, PerServer={Base}×{Mult}={Server} for {Duration}s",
                    burstGlobal, _serverBaseMax, multiplier, burstServerCap, durationSec);

                CancelBurstTimer();
                _burstTimer = new Timer(_ =>
                {
                    ExitBurst();
                }, null, durationSec * 1000, Timeout.Infinite);
            }
        }

        private void CancelBurstTimer()
        {
            _burstTimer?.Dispose();
            _burstTimer = null;
        }

        private void ExitBurst()
        {
            lock (_burstLock)
            {
                CancelBurstTimer();
                _serverBurstMultiplier = 1.0;
                _serverCurrentMax = _serverBaseMax;
                RefreshServerSemaphoreCaps();
                ApplyGlobalLimit(_configuredLimit);
                _logger.LogInformation("[QueryScheduler] Burst ended, reverted to Global={Global}, PerServer={Server}",
                    _configuredLimit, _serverBaseMax);
            }
        }

        // --- Public accessors ---

        public int GetCurrentConcurrencyLimit() => _maxConcurrentQueries;
        public int GetCurrentPerServerLimit() => _serverCurrentMax;

        public void SetGlobalMultiplier(double multiplier) => _registry.SetGlobalMultiplier(multiplier);
        public double GetGlobalMultiplier() => _registry.GetGlobalMultiplier();

        // --- Public execute methods ---

        public async Task ExecuteAsync(Func<Task> operation, string queryId, string? serverName = null, CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var work = new QueuedQuery
            {
                Query = async () =>
                {
                    try
                    {
                        await operation().WaitAsync(cancellationToken);
                        tcs.SetResult(null);
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                        throw;
                    }
                },
                QueryId = queryId,
                ServerName = serverName,
                EnqueuedAt = DateTime.UtcNow
            };

            _channel.Writer.TryWrite(work);
            await tcs.Task.ConfigureAwait(false);
        }

        public async Task ExecuteAsync(Func<Task> operation, string queryId, string? sqlHash = null, string? serverName = null, CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var work = new QueuedQuery
            {
                Query = async () =>
                {
                    try
                    {
                        await operation().WaitAsync(cancellationToken);
                        tcs.SetResult(null);
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                        throw;
                    }
                },
                QueryId = queryId,
                SqlHash = sqlHash,
                ServerName = serverName,
                EnqueuedAt = DateTime.UtcNow
            };

            _channel.Writer.TryWrite(work);
            await tcs.Task.ConfigureAwait(false);
        }

        public Task ExecuteAsync(Func<Task> operation, bool isHeavy, CancellationToken cancellationToken = default)
            => ExecuteAsync(operation, queryId: "unknown", sqlHash: null, serverName: null, cancellationToken: cancellationToken);

        public async Task<T> ExecuteAsync<T>(Func<Task<T>> query, string queryId, string? sqlHash = null, string? serverName = null, CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var work = new QueuedQuery
            {
                Query = async () =>
                {
                    try
                    {
                        var result = await query().WaitAsync(cancellationToken);
                        tcs.SetResult(result);
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                        throw;
                    }
                },
                QueryId = queryId,
                SqlHash = sqlHash,
                ServerName = serverName,
                EnqueuedAt = DateTime.UtcNow
            };

            _channel.Writer.TryWrite(work);
            return await tcs.Task.ConfigureAwait(false);
        }

        // --- Processing loop ---

        private async Task ProcessingLoopAsync()
        {
            var pending = new List<QueuedQuery>();

            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    // Gather pending queries
                    pending.Clear();
                    while (pending.Count < 100 && _channel.Reader.TryRead(out var item))
                    {
                        pending.Add(item);
                    }

                    if (pending.Count == 0)
                    {
                            await Task.Delay(10, _cts.Token);
                        continue;
                    }

                    // Assign groups and per-server semaphores
                    foreach (var q in pending)
                    {
                        var meta = new QueryMetadata
                        {
                            QueryId = q.QueryId,
                            ServerName = q.ServerName
                        };
                        var group = _registry.RegisterQuery(meta, q.SqlHash);
                        q.AssignedGroup = group;
                        q.ServerSemaphore = GetOrCreateServerSemaphore(q.ServerName ?? "default");
                    }

                    // Find due groups
                    var now = DateTime.UtcNow;
                    var readyGroups = pending
                        .GroupBy(q => q.AssignedGroup!.GroupId)
                        .Select(g => new
                        {
                            Group = g.First().AssignedGroup!,
                            Queries = g.ToList(),
                            IsDue = g.First().AssignedGroup!.NextReleaseTime <= now
                        })
                        .OrderByDescending(x => x.IsDue ? 1 : 0)
                        .ThenBy(x => x.Group.NextReleaseTime)
                        .ToList();

                    // Select all due groups
                    var toRelease = new List<QueuedQuery>();
                    foreach (var rg in readyGroups)
                    {
                        if (!rg.IsDue) continue;
                        toRelease.AddRange(rg.Queries);

                        // Schedule next release for this group
                        var effectivePeriod = Math.Max(_minPeriodSec,
                            Math.Min(_maxPeriodSec, rg.Group.TargetPeriodSec * _registry.GetGlobalMultiplier()));
                        rg.Group.NextReleaseTime = now.AddSeconds(effectivePeriod);
                        rg.Group.InFlightCount += rg.Queries.Count;
                        rg.Group.TotalExecutions += rg.Queries.Count;
                        rg.Group.LastUpdated = DateTime.UtcNow;
                    }

                    // If nothing ready, wait until next group due
                    if (toRelease.Count == 0)
                    {
                        var nextDue = readyGroups.FirstOrDefault(r => r.IsDue) ?? readyGroups.OrderBy(r => r.Group.NextReleaseTime).FirstOrDefault();
                        if (nextDue != null)
                        {
                            var waitMs = (int)((nextDue.Group.NextReleaseTime - now).TotalMilliseconds);
                            waitMs = Math.Min(waitMs, _maxBatchWaitMs);
                            if (waitMs > 0) await Task.Delay(waitMs, _cts.Token);
                        }
                        else
                        {
                        await Task.Delay(10, _cts.Token);
                        }
                        continue;
                    }

                    // Fire queries asynchronously; each acquires semaphores (server + global) individually
                    foreach (var q in toRelease)
                    {
                        _ = ExecuteWithTrackingAsync(q);
                    }
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[QueryScheduler] Processing loop error");
                    await Task.Delay(100, _cts.Token);
                }
            }
        }

        private async Task ExecuteWithTrackingAsync(QueuedQuery q)
        {
            var serverSem = q.ServerSemaphore ?? throw new InvalidOperationException("ServerSemaphore not set");

            // Wait for a per-server slot first
            await serverSem.WaitAsync(_cts.Token);
            try
            {
                // Then wait for global slot
                await _querySemaphore.WaitAsync(_cts.Token);
                var start = DateTime.UtcNow;
                try
                {
                    await q.Query();
                    var elapsed = DateTime.UtcNow - start;
                    _registry.RecordExecution(q.QueryId, elapsed.TotalMilliseconds, q.SqlHash);
                }
                finally
                {
                    _querySemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[QueryScheduler] Query {QueryId} failed", q.QueryId);
            }
            finally
            {
                if (q.AssignedGroup != null)
                {
                    Interlocked.Decrement(ref q.AssignedGroup.InFlightCount);
                }
                serverSem.Release();
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _cts.Cancel();
                _cts.Dispose();
                _querySemaphore?.Dispose();
                foreach (var sem in _serverSemaphores.Values)
                {
                    sem?.Dispose();
                }
                _disposed = true;
            }
        }

        // Backing field for global semaphore
        private SemaphoreSlim _querySemaphore = null!;

        private class QueuedQuery
        {
            public Func<Task> Query = null!;
            public string QueryId { get; set; } = "";
            public string? SqlHash;
            public string? ServerName;
            public DateTime EnqueuedAt;
            public BatchGroupState? AssignedGroup;
            public SemaphoreSlim? ServerSemaphore;
        }
    }
}
