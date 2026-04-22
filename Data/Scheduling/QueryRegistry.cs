/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Scheduling
{
    /// <summary>
    /// Central registry for query metadata, grouping logic, and health tracking.
    /// Persists state to JSON for observability and warm-start.
    /// </summary>
    public class QueryRegistry : IDisposable
    {
        private readonly ILogger<QueryRegistry> _logger;
        private readonly string _stateFilePath;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _persistenceTask;
        private readonly Task _healthCheckTask;
        private readonly object _lock = new();
        private bool _disposed;

        // All queries by queryId
        private readonly ConcurrentDictionary<string, QueryMetadata> _queries = new();

        // Group routing: queryId -> BatchGroupState
        private readonly ConcurrentDictionary<string, BatchGroupState> _queryToGroup = new();

        // All groups by groupId
        private readonly ConcurrentDictionary<string, BatchGroupState> _groups = new();

        // Global tuning multiplier (aggressive: 4.0, idle: 0.25)
        private double _globalMultiplier = 1.0;
        private readonly TimeSpan _persistenceInterval;
        private readonly TimeSpan _healthCheckInterval;

        public QueryRegistry(ILogger<QueryRegistry> logger, IConfiguration configuration)
        {
            _logger = logger;
            var configPath = configuration.GetValue<string>("Scheduler:StateFilePath", "Config/scheduler-state.json");
            _stateFilePath = Path.Combine(AppContext.BaseDirectory, configPath);

            var persistMins = configuration.GetValue<int>("Scheduler:StatePersistenceMinutes", 2);
            _persistenceInterval = TimeSpan.FromMinutes(persistMins > 0 ? persistMins : 2);

            var healthCheckSec = configuration.GetValue<int>("Scheduler:HealthCheckIntervalSec", 30);
            _healthCheckInterval = TimeSpan.FromSeconds(healthCheckSec > 0 ? healthCheckSec : 30);

            // Ensure Config directory exists
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath)!);

            // Load any existing state
            LoadStateAsync().ConfigureAwait(false).GetAwaiter().GetResult();

            // Start periodic persistence loop
            _persistenceTask = Task.Run(PersistenceLoopAsync);

            // Start health check loop
            _healthCheckTask = Task.Run(HealthCheckLoopAsync);
        }

        /// <summary>
        /// Registers or updates a query in the registry.
        /// If sqlHash is provided and query already exists, it will be moved to a hash-based affinity group.
        /// </summary>
        public BatchGroupState RegisterQuery(QueryMetadata metadata, string? sqlHash = null)
        {
            lock (_lock)
            {
                // Get or create canonical metadata
                var existing = _queries.GetOrAdd(metadata.QueryId, _ => metadata);
                if (!ReferenceEquals(existing, metadata))
                {
                    // Merge incoming data into canonical entry
                    if (!string.IsNullOrEmpty(sqlHash))
                        existing.SqlHash = sqlHash;
                    if (!string.IsNullOrEmpty(metadata.ServerName))
                        existing.ServerName = metadata.ServerName;
                }

                // Decide which group this query belongs to
                string groupId;
                var effectiveHash = !string.IsNullOrEmpty(sqlHash) ? sqlHash : existing.SqlHash;
                var serverKey = !string.IsNullOrEmpty(metadata.ServerName) ? $"{metadata.ServerName}_" : "";
                if (!string.IsNullOrEmpty(effectiveHash))
                {
                    groupId = $"hash_{serverKey}{effectiveHash}";
                }
                else if (!string.IsNullOrEmpty(existing.BatchGroupId) && existing.BatchGroupId != "default")
                {
                    groupId = existing.BatchGroupId;
                }
                else
                {
                    groupId = "default";
                }

                // If group changed, migrate query to new group
                if (existing.BatchGroupId != groupId)
                {
                    // Remove from old group
                    if (_queryToGroup.TryGetValue(metadata.QueryId, out var oldGroup))
                    {
                        oldGroup.Queries.TryRemove(metadata.QueryId, out _);
                    }
                    // Assign to new group
                    existing.BatchGroupId = groupId;
                }

                // Get or create target group
                var group = _groups.GetOrAdd(groupId, id => new BatchGroupState(id));
                group.Queries[metadata.QueryId] = existing;
                _queryToGroup[metadata.QueryId] = group;

                return group;
            }
        }

        /// <summary>
        /// Records a completed query execution for adaptive tuning.
        /// </summary>
        public void RecordExecution(string queryId, double roundtripMs, string? sqlHash = null)
        {
            lock (_lock)
            {
                if (_queries.TryGetValue(queryId, out var meta))
                {
                    // Exponential moving average (α = 0.3)
                    var α = 0.3;
                    meta.RoundtripMs = α * roundtripMs + (1 - α) * meta.RoundtripMs;
                    meta.SampleCount++;
                    meta.LastExecuted = DateTime.UtcNow;

                    // Update sqlHash if provided and blank
                    if (!string.IsNullOrEmpty(sqlHash) && string.IsNullOrEmpty(meta.SqlHash))
                    {
                        meta.SqlHash = sqlHash;
                    }
                }
            }
        }

        /// <summary>
        /// Gets the current BatchGroupState for a query, creating if needed.
        /// </summary>
        public BatchGroupState GetGroupForQuery(string queryId, QueryMetadata? meta = null)
        {
            if (_queryToGroup.TryGetValue(queryId, out var existing))
                return existing;

            if (meta != null)
                return RegisterQuery(meta);

            // Create default group
            var defGroup = _groups.GetOrAdd("default", _ => new BatchGroupState("default"));
            _queryToGroup[queryId] = defGroup;
            return defGroup;
        }

        /// <summary>
        /// Returns a snapshot of all queries for health/UI.
        /// </summary>
        public IReadOnlyCollection<QueryMetadata> GetAllQueries()
            => _queries.Values.ToList().AsReadOnly();

        /// <summary>
        /// Returns all batch groups.
        /// </summary>
        public IReadOnlyCollection<BatchGroupState> GetAllGroups()
            => _groups.Values.ToList().AsReadOnly();

        /// <summary>
        /// Set global multiplier (from UI slider).
        /// </summary>
        public void SetGlobalMultiplier(double multiplier)
        {
            lock (_lock)
            {
                _globalMultiplier = Math.Clamp(multiplier, 0.1, 10.0);
                _logger.LogInformation("[QueryRegistry] Global multiplier set to {Multiplier:F2}", _globalMultiplier);
            }
        }

        public double GetGlobalMultiplier()
        {
            lock (_lock) return _globalMultiplier;
        }

        /// <summary>
        /// Attempts to reassign outlier queries to more appropriate groups.
        /// Called periodically by health check.
        /// </summary>
        private void RebalanceOutliers()
        {
            lock (_lock)
            {
                foreach (var group in _groups.Values)
                {
                    var avg = group.AvgRoundtripMs;
                    if (avg <= 0) continue;

                    var outliers = group.Queries.Values
                        .Where(q => q.RoundtripMs > 0 && Math.Abs(q.RoundtripMs - avg) / avg > 0.5) // >50% deviation
                        .ToList();

                    foreach (var outlier in outliers)
                    {
                        _logger.LogDebug("[QueryRegistry] Query {QueryId} is outlier in group {GroupId} (rt={RtMs}, avg={AvgMs})",
                            outlier.QueryId, group.GroupId, outlier.RoundtripMs, avg);

                        // Create or find a better-matched group based on latency
                        var targetGroup = FindOrCreateGroupByLatency(outlier.RoundtripMs);
                        if (targetGroup.GroupId != group.GroupId)
                        {
                            // Move query
                            group.Queries.TryRemove(outlier.QueryId, out _);
                            targetGroup.Queries[outlier.QueryId] = outlier;
                            _queryToGroup[outlier.QueryId] = targetGroup;
                            outlier.BatchGroupId = targetGroup.GroupId;
                            _logger.LogInformation("[QueryRegistry] Reassigned {QueryId} from group {From} to group {To}",
                                outlier.QueryId, group.GroupId, targetGroup.GroupId);
                        }
                    }
                }
            }
        }

        private BatchGroupState FindOrCreateGroupByLatency(double latencyMs)
        {
            // Bucket by observed roundtrip: <100ms fast, 100-500 medium, 500-2000 slow, >2000 very_slow
            string bucket;
            if (latencyMs < 100) bucket = "fast";
            else if (latencyMs < 500) bucket = "medium";
            else if (latencyMs < 2000) bucket = "slow";
            else bucket = "very_slow";

            var groupId = $"latency_{bucket}";
            return _groups.GetOrAdd(groupId, _ => new BatchGroupState(groupId));
        }

        private async Task PersistenceLoopAsync()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_persistenceInterval, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    var snapshot = CreateSnapshot();
                    var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(_stateFilePath, json, _cts.Token);
                    _logger.LogDebug("[QueryRegistry] State saved to {Path}", _stateFilePath);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[QueryRegistry] Failed to save state");
                }
            }
        }

        private async Task HealthCheckLoopAsync()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_healthCheckInterval, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    RebalanceOutliers();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[QueryRegistry] Health check failed");
                }
            }
        }

        private SchedulerState CreateSnapshot()
        {
            lock (_lock)
            {
                var state = new SchedulerState
                {
                    LastUpdated = DateTime.UtcNow,
                    GlobalMultiplier = _globalMultiplier,
                    QueryCount = _queries.Count,
                    GroupCount = _groups.Count,
                    Queries = _queries.Values.ToDictionary(q => q.QueryId, q => q),
                    Groups = _groups.Values.ToDictionary(
                        g => g.GroupId,
                        g => new GroupSnapshot
                        {
                            PeriodSec = g.TargetPeriodSec,
                            QueryIds = g.Queries.Keys.ToList(),
                            AvgRoundtripMs = g.AvgRoundtripMs,
                            InFlight = g.InFlightCount,
                            IsHealthy = g.IsHealthy
                        })
                };
                return state;
            }
        }

        private async Task LoadStateAsync()
        {
            try
            {
                if (!File.Exists(_stateFilePath))
                {
                    _logger.LogInformation("[QueryRegistry] No state file found at {Path}, starting fresh", _stateFilePath);
                    return;
                }

                var json = await File.ReadAllTextAsync(_stateFilePath);
                var state = JsonSerializer.Deserialize<SchedulerState>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (state != null)
                {
                    lock (_lock)
                    {
                        _globalMultiplier = state.GlobalMultiplier;
                        foreach (var q in state.Queries.Values)
                        {
                            _queries[q.QueryId] = q;
                        }
                        foreach (var g in state.Groups.Values)
                        {
                            var groupState = new BatchGroupState(g.QueryIds.FirstOrDefault() ?? "orphaned")
                            {
                                TargetPeriodSec = g.PeriodSec,
                                InFlightCount = g.InFlight
                            };
                            _groups[groupState.GroupId] = groupState;
                        }
                        // Rebuild routing
                        foreach (var q in _queries.Values)
                        {
                            var groupId = q.BatchGroupId;
                            if (_groups.TryGetValue(groupId, out var grp))
                            {
                                grp.Queries[q.QueryId] = q;
                                _queryToGroup[q.QueryId] = grp;
                            }
                        }
                    }
                    _logger.LogInformation("[QueryRegistry] State loaded: {QueryCount} queries, {GroupCount} groups",
                        state.QueryCount, state.GroupCount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[QueryRegistry] Failed to load state");
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _cts.Cancel();
                try { _persistenceTask.Wait(TimeSpan.FromSeconds(5)); } catch { /* best effort */ }
                try { _healthCheckTask.Wait(TimeSpan.FromSeconds(5)); } catch { /* best effort */ }
                _cts.Dispose();
                _disposed = true;
            }
        }
    }
}
