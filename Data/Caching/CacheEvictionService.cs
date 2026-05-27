/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Caching
{
    /// <summary>
    /// Runs periodic cache eviction on a timer (default: every 5 minutes).
    /// Removes rows from all liveQueries cache tables where fetched_at is older
    /// than the configured threshold (default: 24 hours).
    /// Uses a SemaphoreSlim to prevent overlapping executions.
    /// </summary>
    public class CacheEvictionService : IDisposable
    {
        private readonly liveQueriesCacheStore _cache;
        private readonly TimeSpan _evictionThreshold;
        private readonly TimeSpan _interval;
        private readonly long _maxCacheSizeBytes;
        private readonly MemoryMonitorService? _memoryMonitor;
        private readonly ILogger<CacheEvictionService> _logger;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _evictionLock = new(1, 1);
        private Task? _loopTask;
        private bool _disposed;

        public CacheEvictionService(liveQueriesCacheStore cache, IConfiguration config, ILogger<CacheEvictionService> logger, MemoryMonitorService? memoryMonitor = null)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger;
            _memoryMonitor = memoryMonitor;

            var hours = 24;
            if (int.TryParse(config["CacheEvictionHours"], out var h) && h > 0)
                hours = h;
            _evictionThreshold = TimeSpan.FromHours(hours);

            // Default max cache size: 500MB (config value is in MB)
            _maxCacheSizeBytes = config.GetValue<long>("MaxCacheSizeMB", 500) * 1024 * 1024;

            // Run eviction every 5 minutes
            _interval = TimeSpan.FromMinutes(5);

            if (_memoryMonitor != null)
            {
                _memoryMonitor.MemoryPressureChanged += OnMemoryPressureChanged;
            }
        }

        /// <summary>
        /// Starts the periodic eviction loop.
        /// </summary>
        public void Start()
        {
            _loopTask = Task.Run(EvictionLoopAsync);
        }

        /// <summary>
        /// Stops the periodic eviction loop gracefully.
        /// </summary>
        public void Stop()
        {
            _cts.Cancel();
        }

        private async Task EvictionLoopAsync()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_interval, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // Skip if previous eviction is still running
                if (!await _evictionLock.WaitAsync(0))
                {
                    _logger.LogWarning("Previous cache eviction still running; skipping this cycle to prevent overlap");
                    continue;
                }

                try
                {
                    await RunEvictionAsync(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Cache eviction failed");
                }
                finally
                {
                    _evictionLock.Release();
                }
            }
        }

        private async Task RunEvictionAsync(CancellationToken cancellationToken)
        {
            // Time-based eviction
            await _cache.EvictOlderThanAsync(_evictionThreshold);

            // Size-based eviction
            await _cache.EnforceSizeLimitAsync(_maxCacheSizeBytes);
        }

        private void OnMemoryPressureChanged(object? sender, MemoryPressureEventArgs e)
        {
            if (e.IsUnderPressure)
            {
                _ = RunPressureEvictionAsync();
            }
        }

        private async Task RunPressureEvictionAsync()
        {
            if (!await _evictionLock.WaitAsync(0)) return; // skip if eviction running
            try
            {
                await _cache.EvictOlderThanAsync(TimeSpan.FromHours(24));
                _logger.LogWarning("Aggressive cache eviction triggered due to memory pressure");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Memory pressure eviction failed");
            }
            finally
            {
                _evictionLock.Release();
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _cts.Cancel();
                if (_memoryMonitor != null)
                {
                    _memoryMonitor.MemoryPressureChanged -= OnMemoryPressureChanged;
                }
                _cts.Dispose();
                _evictionLock.Dispose();
                _disposed = true;
            }
        }
    }
}
