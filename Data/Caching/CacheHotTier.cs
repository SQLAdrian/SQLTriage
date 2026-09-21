/* In the name of God, the Merciful, the Compassionate */

using System.Data;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Caching
{
    /// <summary>
    /// In-memory hot tier fronting the SQLite cache store.
    /// Provides sub-millisecond reads for dashboard reloads with automatic expiration.
    /// </summary>
    public interface ICacheHotTier
    {
        Task<List<TimeSeriesPoint>?> GetTimeSeriesAsync(string queryId, string instanceKey);
        Task SetTimeSeriesAsync(string queryId, string instanceKey, List<TimeSeriesPoint> data);

        Task<StatValue?> GetStatValueAsync(string queryId, string instanceKey);
        Task SetStatValueAsync(string queryId, string instanceKey, StatValue data);

        Task<List<StatValue>?> GetBarGaugeAsync(string queryId, string instanceKey);
        Task SetBarGaugeAsync(string queryId, string instanceKey, List<StatValue> data);

        Task<DataTable?> GetDataTableAsync(string queryId, string instanceKey);
        Task SetDataTableAsync(string queryId, string instanceKey, DataTable data);

        Task<List<CheckStatus>?> GetCheckStatusAsync(string queryId, string instanceKey);
        Task SetCheckStatusAsync(string queryId, string instanceKey, List<CheckStatus> data);

        Task<DateTime?> GetLastFetchTimeAsync(string queryId, string instanceKey);
        Task SetLastFetchTimeAsync(string queryId, string instanceKey, DateTime time);

        void Invalidate(string queryId, string instanceKey);
        void InvalidateAll();

        /// <summary>Drop a fraction of cached entries (e.g. 0.25 = 25%) — used under memory pressure.</summary>
        void Compact(double fraction);
    }

    /// <summary>
    /// <see cref="ICacheHotTier"/> implementation using <see cref="IMemoryCache"/>.
    /// All items have a 90-second sliding expiration to keep memory bounded
    /// while surviving rapid dashboard refreshes.
    /// </summary>
    public sealed class CacheHotTier : ICacheHotTier
    {
        private readonly IMemoryCache _cache;
        private readonly ILogger<CacheHotTier> _logger;
        private static readonly TimeSpan SlidingExpiration = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan AbsoluteExpiration = TimeSpan.FromMinutes(5);

        public CacheHotTier(IMemoryCache cache, ILogger<CacheHotTier> logger)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        private static string Key(string category, string queryId, string instanceKey)
            => $"hc:{category}:{queryId}:{instanceKey}";

        private MemoryCacheEntryOptions EntryOptions => new MemoryCacheEntryOptions()
            .SetSlidingExpiration(SlidingExpiration)
            .SetAbsoluteExpiration(AbsoluteExpiration)
            .RegisterPostEvictionCallback((key, value, reason, state) =>
            {
                if (reason is EvictionReason.Capacity or EvictionReason.Expired)
                {
                    // Optional: telemetry hook
                }
            });

        public Task<List<TimeSeriesPoint>?> GetTimeSeriesAsync(string queryId, string instanceKey)
        {
            _cache.TryGetValue(Key("ts", queryId, instanceKey), out List<TimeSeriesPoint>? value);
            return Task.FromResult(value);
        }

        public Task SetTimeSeriesAsync(string queryId, string instanceKey, List<TimeSeriesPoint> data)
        {
            _cache.Set(Key("ts", queryId, instanceKey), data, EntryOptions);
            return Task.CompletedTask;
        }

        public Task<StatValue?> GetStatValueAsync(string queryId, string instanceKey)
        {
            _cache.TryGetValue(Key("stat", queryId, instanceKey), out StatValue? value);
            return Task.FromResult(value);
        }

        public Task SetStatValueAsync(string queryId, string instanceKey, StatValue data)
        {
            _cache.Set(Key("stat", queryId, instanceKey), data, EntryOptions);
            return Task.CompletedTask;
        }

        public Task<List<StatValue>?> GetBarGaugeAsync(string queryId, string instanceKey)
        {
            _cache.TryGetValue(Key("bg", queryId, instanceKey), out List<StatValue>? value);
            return Task.FromResult(value);
        }

        public Task SetBarGaugeAsync(string queryId, string instanceKey, List<StatValue> data)
        {
            _cache.Set(Key("bg", queryId, instanceKey), data, EntryOptions);
            return Task.CompletedTask;
        }

        public Task<DataTable?> GetDataTableAsync(string queryId, string instanceKey)
        {
            _cache.TryGetValue(Key("dt", queryId, instanceKey), out DataTable? value);
            return Task.FromResult(value);
        }

        public Task SetDataTableAsync(string queryId, string instanceKey, DataTable data)
        {
            _cache.Set(Key("dt", queryId, instanceKey), data, EntryOptions);
            return Task.CompletedTask;
        }

        public Task<List<CheckStatus>?> GetCheckStatusAsync(string queryId, string instanceKey)
        {
            _cache.TryGetValue(Key("chk", queryId, instanceKey), out List<CheckStatus>? value);
            return Task.FromResult(value);
        }

        public Task SetCheckStatusAsync(string queryId, string instanceKey, List<CheckStatus> data)
        {
            _cache.Set(Key("chk", queryId, instanceKey), data, EntryOptions);
            return Task.CompletedTask;
        }

        public Task<DateTime?> GetLastFetchTimeAsync(string queryId, string instanceKey)
        {
            _cache.TryGetValue(Key("meta:lastfetch", queryId, instanceKey), out DateTime? value);
            return Task.FromResult(value);
        }

        public Task SetLastFetchTimeAsync(string queryId, string instanceKey, DateTime time)
        {
            _cache.Set(Key("meta:lastfetch", queryId, instanceKey), time, EntryOptions);
            return Task.CompletedTask;
        }

        public void Invalidate(string queryId, string instanceKey)
        {
            _cache.Remove(Key("ts", queryId, instanceKey));
            _cache.Remove(Key("stat", queryId, instanceKey));
            _cache.Remove(Key("bg", queryId, instanceKey));
            _cache.Remove(Key("dt", queryId, instanceKey));
            _cache.Remove(Key("chk", queryId, instanceKey));
            _cache.Remove(Key("meta:lastfetch", queryId, instanceKey));
        }

        public void InvalidateAll()
        {
            if (_cache is MemoryCache mc)
            {
                mc.Clear();
                _logger.LogInformation("CacheHotTier cleared");
            }
        }

        public void Compact(double fraction)
        {
            if (fraction <= 0 || fraction > 1) return;
            if (_cache is MemoryCache mc)
            {
                mc.Compact(fraction);
                _logger.LogInformation("CacheHotTier compacted by {Fraction:P0}", fraction);
            }
        }
    }
}
