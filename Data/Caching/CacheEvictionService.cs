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
    /// </summary>
    public class CacheEvictionService : IDisposable
    {
        private readonly liveQueriesCacheStore _cache;
        private readonly TimeSpan _evictionThreshold;
        private readonly TimeSpan _interval;
        private readonly long _maxCacheSizeBytes;
        private readonly MemoryMonitorService? _memoryMonitor;
        private readonly ILogger<CacheEvictionService> _logger;
        private Timer? _timer;
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
        /// Starts the periodic eviction timer.
        /// </summary>
        public void Start()
        {
            _timer = new Timer(OnTimerTick, null, _interval, _interval);
        }

        /// <summary>
        /// Stops the periodic eviction timer.
        /// </summary>
        public void Stop()
        {
            _timer?.Dispose();
            _timer = null;
        }

        private void OnTimerTick(object? state)
        {
            _ = RunEvictionAsync();
        }

        private async Task RunEvictionAsync()
        {
            try
            {
                // Time-based eviction
                await _cache.EvictOlderThanAsync(_evictionThreshold);

                // Size-based eviction
                await _cache.EnforceSizeLimitAsync(_maxCacheSizeBytes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cache eviction failed");
            }
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
            try
            {
                await _cache.EvictOlderThanAsync(TimeSpan.FromHours(24));
                _logger.LogWarning("Aggressive cache eviction triggered due to memory pressure");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Memory pressure eviction failed");
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                if (_memoryMonitor != null)
                {
                    _memoryMonitor.MemoryPressureChanged -= OnMemoryPressureChanged;
                }
                _timer?.Dispose();
                _disposed = true;
            }
        }
    }
}