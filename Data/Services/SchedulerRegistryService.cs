/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Scheduling;

namespace SQLTriage.Data.Services
{
    public class SchedulerRegistryService : IDisposable
    {
        private readonly ILogger<SchedulerRegistryService> _logger;
        private readonly QueryRegistry _queryRegistry;
        private readonly QueryScheduler _queryScheduler;
        private readonly List<SchedulerServiceInfo> _services = new();
        private Timer _updateTimer;

        public SchedulerRegistryService(
            ILogger<SchedulerRegistryService> logger,
            IConfiguration configuration,
            QueryRegistry queryRegistry,
            QueryScheduler queryScheduler)
        {
            _logger = logger;
            _queryRegistry = queryRegistry;
            _queryScheduler = queryScheduler;

            DiscoverServices();
            _updateTimer = new Timer(UpdateMetrics, null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        }

        private void DiscoverServices()
        {
            AddService(new SchedulerServiceInfo
            {
                Name = "QueryScheduler",
                Type = "query_scheduler",
                Status = "running",
                Enabled = true,
                GlobalMultiplier = _queryScheduler.GetGlobalMultiplier(),
                LastUpdated = DateTime.UtcNow
            });

            AddService(new SchedulerServiceInfo
            {
                Name = "AutoRefreshService",
                Type = "timer",
                Status = "running",
                Enabled = true,
                IntervalSeconds = 15,
                LastUpdated = DateTime.UtcNow
            });

            AddService(new SchedulerServiceInfo
            {
                Name = "ScheduledTaskEngine",
                Type = "cron",
                Status = "running",
                Enabled = true,
                TaskCount = 12,
                LastUpdated = DateTime.UtcNow
            });

            AddService(new SchedulerServiceInfo
            {
                Name = "AlertEvaluationService",
                Type = "polling",
                Status = "running",
                Enabled = true,
                IntervalSeconds = 30,
                LastUpdated = DateTime.UtcNow
            });

            AddService(new SchedulerServiceInfo
            {
                Name = "CacheEvictionService",
                Type = "background",
                Status = "running",
                Enabled = true,
                IntervalSeconds = 300,
                LastUpdated = DateTime.UtcNow
            });

            AddService(new SchedulerServiceInfo
            {
                Name = "HealthCheckService",
                Type = "background",
                Status = "running",
                Enabled = true,
                IntervalSeconds = 60,
                LastUpdated = DateTime.UtcNow
            });
        }

        private void AddService(SchedulerServiceInfo info)
        {
            lock (_services)
            {
                var existing = _services.FirstOrDefault(s => s.Name == info.Name);
                if (existing != null) _services.Remove(existing);
                _services.Add(info);
            }
        }

        private void UpdateMetrics(object? state)
        {
            try
            {
                var queryCount = _queryRegistry.GetAllQueries().Count;
                var groupCount = _queryRegistry.GetAllGroups().Count;
                var multiplier = _queryScheduler.GetGlobalMultiplier();
                var concurrencyLimit = _queryScheduler.GetCurrentConcurrencyLimit();

                lock (_services)
                {
                    var qs = _services.FirstOrDefault(s => s.Name == "QueryScheduler");
                    if (qs != null)
                    {
                        qs.QueryCount = queryCount;
                        qs.ActiveGroups = groupCount;
                        qs.GlobalMultiplier = multiplier;
                        qs.ConcurrencyLimit = concurrencyLimit;
                        qs.LastUpdated = DateTime.UtcNow;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to update scheduler metrics");
            }
        }

        public IReadOnlyList<SchedulerServiceInfo> GetAllServices()
        {
            lock (_services) { return _services.ToList().AsReadOnly(); }
        }

        public SchedulerServiceInfo? GetService(string name)
        {
            lock (_services) { return _services.FirstOrDefault(s => s.Name == name); }
        }

        public void Dispose() => _updateTimer?.Dispose();
    }

    public class SchedulerServiceInfo
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("status")] public string Status { get; set; } = "running";
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
        [JsonPropertyName("intervalSeconds")] public int? IntervalSeconds { get; set; }
        [JsonPropertyName("taskCount")] public int? TaskCount { get; set; }
        [JsonPropertyName("queryCount")] public int QueryCount { get; set; }
        [JsonPropertyName("activeGroups")] public int ActiveGroups { get; set; }
        [JsonPropertyName("globalMultiplier")] public double? GlobalMultiplier { get; set; }
        [JsonPropertyName("concurrencyLimit")] public int ConcurrencyLimit { get; set; }
        [JsonPropertyName("lastUpdated")] public DateTime LastUpdated { get; set; }
    }
}
