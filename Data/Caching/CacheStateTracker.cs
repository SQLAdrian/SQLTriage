/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;

namespace SQLTriage.Data.Caching
{
    /// <summary>
    /// Tracks per-dashboard filter state to detect when a full cache invalidation
    /// is needed (time range or instance selection changed) versus a delta refresh
    /// (steady-state sliding window).
    ///
    /// Also tracks SQL Server connection health for the offline resilience indicator.
    /// </summary>
    public class CacheStateTracker
    {
        private readonly ConcurrentDictionary<string, FilterState> _lastFilterState = new();

        /// <summary>
        /// True when the most recent SQL Server query failed due to connectivity issues.
        /// The DynamicDashboard renders a stale-data banner when this is set.
        /// </summary>
        public bool IsOffline { get; set; }

        /// <summary>
        /// The timestamp of the last successful SQL Server fetch.
        /// Displayed in the stale-data banner so the user knows how old the data is.
        /// </summary>
        public DateTime? LastSuccessfulFetch { get; set; }

        /// <summary>
        /// Returns true if the filter has changed in a way that requires a full cache
        /// invalidation and reload. This occurs when:
        ///   - The time range changed (e.g., user switched from 1hr to 6hr)
        ///   - The selected instance changed
        ///   - The timezone offset changed (cached rows have timestamps mapped at the old offset)
        ///   - This is the very first call for this dashboard (no previous state)
        ///
        /// The sliding window moving forward by 5 seconds on each auto-refresh
        /// does NOT constitute a change — that is normal delta-fetch behavior.
        /// </summary>
        public bool RequiresFullReload(string dashboardId, int currentTimeRangeMinutes, string currentInstance, double currentTimezoneOffsetHours)
        {
            if (!_lastFilterState.TryGetValue(dashboardId, out var previous))
            {
                // First time seeing this dashboard — not a "change", just uninitialized.
                // We return false so the first load goes through normal delta path
                // (which will detect no lastFetch and do a full load anyway).
                return false;
            }

            return previous.TimeRangeMinutes != currentTimeRangeMinutes
                || !string.Equals(previous.Instance, currentInstance, StringComparison.OrdinalIgnoreCase)
                || previous.TimezoneOffsetHours != currentTimezoneOffsetHours;
        }

        /// <summary>
        /// Records the current filter state so subsequent calls to RequiresFullReload
        /// can detect changes.
        /// </summary>
        public void RecordFilterState(string dashboardId, int timeRangeMinutes, string selectedInstance, double timezoneOffsetHours)
        {
            _lastFilterState[dashboardId] = new FilterState
            {
                TimeRangeMinutes = timeRangeMinutes,
                Instance = selectedInstance,
                TimezoneOffsetHours = timezoneOffsetHours,
                LastUpdated = DateTime.UtcNow
            };

            EvictStaleFilterStates();
        }

        /// <summary>
        /// Removes filter state entries for dashboards not visited in the last hour,
        /// preventing the dictionary from growing unbounded across long sessions.
        /// </summary>
        private void EvictStaleFilterStates()
        {
            if (_lastFilterState.Count <= 10) return;

            var cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (var kvp in _lastFilterState)
            {
                if (kvp.Value.LastUpdated < cutoff)
                    _lastFilterState.TryRemove(kvp.Key, out _);
            }
        }

        /// <summary>
        /// Records a successful SQL Server fetch, clearing the offline flag.
        /// </summary>
        public void RecordSuccess()
        {
            IsOffline = false;
            LastSuccessfulFetch = DateTime.Now;
        }

        /// <summary>
        /// Records a SQL Server connectivity failure, setting the offline flag.
        /// </summary>
        public void RecordFailure()
        {
            IsOffline = true;
        }

        private class FilterState
        {
            public int TimeRangeMinutes { get; set; }
            public string Instance { get; set; } = "";
            public double TimezoneOffsetHours { get; set; }
            public DateTime LastUpdated { get; set; }
        }
    }
}
