/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Threading;
using SQLTriage.Data.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Caching
{
    /// <summary>
    /// Runs periodic liveQueries database maintenance on a timer.
    /// Each cycle: purges data older than the retention period, runs
    /// PRAGMA optimize and VACUUM, and optionally PRAGMA integrity_check.
    /// </summary>
    public class liveQueriesMaintenanceService : IDisposable
    {
        private readonly liveQueriesCacheStore _cache;
        private readonly TimeSpan _interval;
        private readonly TimeSpan _retentionPeriod;
        private readonly int _integrityCheckEveryNRuns;
        private readonly ILogger<liveQueriesMaintenanceService> _logger;
        private Timer? _timer;
        private int _runCount;
        private bool _disposed;

        /// <summary>
        /// Raised after each maintenance run completes (success or failure).
        /// </summary>
        public event Action<MaintenanceResult>? OnMaintenanceCompleted;

        public MaintenanceResult? LastResult { get; private set; }

        public liveQueriesMaintenanceService(liveQueriesCacheStore cache, IConfiguration config, ILogger<liveQueriesMaintenanceService> logger)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger;

            // Default: run maintenance every 4 hours
            var intervalHours = 4;
            if (int.TryParse(config["liveQueriesMaintenanceIntervalHours"], out var h) && h > 0)
                intervalHours = h;
            _interval = TimeSpan.FromHours(intervalHours);

            // Default: purge data older than 30 days
            var retentionDays = 30;
            if (int.TryParse(config["DataRetentionDays"], out var d) && d > 0)
                retentionDays = d;
            _retentionPeriod = TimeSpan.FromDays(retentionDays);

            // Default: run integrity check every 6th maintenance run (i.e. once per day at 4h intervals)
            _integrityCheckEveryNRuns = 6;
            if (int.TryParse(config["liveQueriesIntegrityCheckEveryNRuns"], out var n) && n > 0)
                _integrityCheckEveryNRuns = n;
        }

        /// <summary>
        /// Starts the periodic maintenance timer.
        /// </summary>
        public void Start()
        {
            // First run after one interval; then repeat at the configured interval
            _timer = new Timer(_ => OnTimerTickAsync(), null, _interval, _interval);
        }

        /// <summary>
        /// Stops the periodic maintenance timer.
        /// </summary>
        public void Stop()
        {
            _timer?.Dispose();
            _timer = null;
        }

        private async void OnTimerTick(object? state)
        {
            try
            {
                await OnTimerTickAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Timer tick failed");
            }
        }

        private async Task OnTimerTickAsync()
        {
            _runCount++;
            var includeIntegrity = (_runCount % _integrityCheckEveryNRuns) == 0;

            // 1. Purge data beyond the retention period (default 30 days)
            var rowsPurged = await _cache.PurgeOlderThanAsync(_retentionPeriod);

            // 2. Run optimize + vacuum (+ optional integrity check)
            var result = await _cache.RunMaintenanceAsync(includeIntegrity);
            result.RowsPurged = rowsPurged;
            LastResult = result;

            _logger.LogInformation(
                "Maintenance completed in {DurationSeconds}s (purged={RowsPurged}, optimize={OptimizeCompleted}, vacuum={VacuumCompleted}, integrity={IntegrityCheckResult})",
                result.Duration.TotalSeconds.ToString("F1"),
                rowsPurged,
                result.OptimizeCompleted,
                result.VacuumCompleted,
                includeIntegrity ? result.IntegrityCheckResult : "skipped");

            OnMaintenanceCompleted?.Invoke(result);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _timer?.Dispose();
                _disposed = true;
            }
        }
    }
}
