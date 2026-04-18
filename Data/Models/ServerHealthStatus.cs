/* In the name of God, the Merciful, the Compassionate */

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SQLTriage.Data.Models
{
    /// <summary>
    /// Severity levels for health indicators.
    /// </summary>
    public enum HealthSeverity
    {
        Unknown,
        Healthy,
        Warning,
        Critical
    }

    /// <summary>
    /// Represents the real-time health status of a server.
    /// Includes connection status plus all live health metrics.
    /// </summary>
    public class ServerHealthStatus : INotifyPropertyChanged
    {
        private bool _isLoading;
        private bool? _isOnline;
        private string? _errorMessage;
        private DateTime? _lastUpdated;

        // CPU metrics
        private int? _cpuPercent;
        private int? _otherCpuPercent;

        // Memory metrics
        private decimal? _bufferPoolMb;
        private decimal? _grantedMemoryMb;
        private int _requestsWaitingForMemory;

        // Blocking metrics
        private long _totalBlocked;
        private decimal _longestBlockedSeconds;
        private int? _lastBlockingMinutesAgo;

        // Thread metrics
        private int _totalThreads;
        private int _availableThreads;
        private int _threadsWaitingForCpu;
        private int _requestsWaitingForThreads;

        // Deadlocks
        private long _deadlockCount;
        private long _previousDeadlockCount;
        private bool _isFirstDeadlockRead = true;
        private int? _lastDeadlockMinutesAgo;

        // Waits
        private string? _topWaitType;
        private decimal _topWaitDurationSeconds;

        public string ServerId { get; set; } = "";
        public string ServerName { get; set; } = "";

        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        public bool? IsOnline
        {
            get => _isOnline;
            set
            {
                _isOnline = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ConnectionSeverity));
                OnPropertyChanged(nameof(ConnectionStatusText));
            }
        }

        public string? ErrorMessage
        {
            get => _errorMessage;
            set { _errorMessage = value; OnPropertyChanged(); }
        }

        public DateTime? LastUpdated
        {
            get => _lastUpdated;
            set
            {
                _lastUpdated = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LastUpdatedDisplay));
            }
        }

        public string LastUpdatedDisplay
        {
            get
            {
                if (!_lastUpdated.HasValue)
                    return "Never";

                var elapsed = DateTime.Now - _lastUpdated.Value;

                if (elapsed.TotalSeconds < 60)
                    return "Just now";

                if (elapsed.TotalMinutes < 60)
                    return $"{(int)elapsed.TotalMinutes}m ago";

                return $"{(int)elapsed.TotalHours}h ago";
            }
        }

        // CPU
        public int? CpuPercent
        {
            get => _cpuPercent;
            set
            {
                _cpuPercent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TotalCpuPercent));
                OnPropertyChanged(nameof(CpuSeverity));
                OnPropertyChanged(nameof(CpuDisplayText));
            }
        }

        public int? OtherCpuPercent
        {
            get => _otherCpuPercent;
            set
            {
                _otherCpuPercent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TotalCpuPercent));
                OnPropertyChanged(nameof(CpuSeverity));
                OnPropertyChanged(nameof(CpuDisplayText));
            }
        }

        public int? TotalCpuPercent
        {
            get
            {
                if (!_cpuPercent.HasValue && !_otherCpuPercent.HasValue) return null;
                return (_cpuPercent ?? 0) + (_otherCpuPercent ?? 0);
            }
        }

        public HealthSeverity CpuSeverity
        {
            get
            {
                var total = TotalCpuPercent;
                if (!total.HasValue) return HealthSeverity.Unknown;
                if (total >= 95) return HealthSeverity.Critical;
                if (total >= 80) return HealthSeverity.Warning;
                return HealthSeverity.Healthy;
            }
        }

        public string CpuDisplayText => TotalCpuPercent.HasValue ? $"{TotalCpuPercent}%" : "--";

        // Memory
        public decimal? BufferPoolMb
        {
            get => _bufferPoolMb;
            set { _bufferPoolMb = value; OnPropertyChanged(); OnPropertyChanged(nameof(MemoryDisplayText)); }
        }

        public decimal? GrantedMemoryMb
        {
            get => _grantedMemoryMb;
            set { _grantedMemoryMb = value; OnPropertyChanged(); OnPropertyChanged(nameof(MemoryDisplayText)); }
        }

        public int RequestsWaitingForMemory
        {
            get => _requestsWaitingForMemory;
            set
            {
                _requestsWaitingForMemory = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MemorySeverity));
                OnPropertyChanged(nameof(MemoryDisplayText));
            }
        }

        public HealthSeverity MemorySeverity
        {
            get
            {
                if (_requestsWaitingForMemory > 0) return HealthSeverity.Critical;
                return HealthSeverity.Healthy;
            }
        }

        public string MemoryDisplayText
        {
            get
            {
                if (_requestsWaitingForMemory > 0) return $"{_requestsWaitingForMemory} waiting";
                if (_bufferPoolMb.HasValue) return $"{_bufferPoolMb.Value:F0} MB";
                return "--";
            }
        }

        // Blocking
        public long TotalBlocked
        {
            get => _totalBlocked;
            set
            {
                _totalBlocked = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(BlockingSeverity));
                OnPropertyChanged(nameof(BlockingDisplayText));
            }
        }

        public decimal LongestBlockedSeconds
        {
            get => _longestBlockedSeconds;
            set
            {
                _longestBlockedSeconds = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(BlockingSeverity));
                OnPropertyChanged(nameof(BlockingDisplayText));
            }
        }

        public HealthSeverity BlockingSeverity
        {
            get
            {
                if (_longestBlockedSeconds >= 60) return HealthSeverity.Critical;
                if (_totalBlocked >= 5) return HealthSeverity.Critical;
                if (_longestBlockedSeconds >= 10) return HealthSeverity.Warning;
                if (_totalBlocked >= 2) return HealthSeverity.Warning;
                if (_totalBlocked > 0) return HealthSeverity.Warning;
                return HealthSeverity.Healthy;
            }
        }

        public string BlockingDisplayText
        {
            get
            {
                if (_totalBlocked == 0) return "0";
                return $"{_totalBlocked}";
            }
        }

        public int? LastBlockingMinutesAgo
        {
            get => _lastBlockingMinutesAgo;
            set { _lastBlockingMinutesAgo = value; OnPropertyChanged(); }
        }

        // Threads
        public int TotalThreads
        {
            get => _totalThreads;
            set
            {
                _totalThreads = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ThreadsSeverity));
                OnPropertyChanged(nameof(ThreadsDisplayText));
            }
        }

        public int AvailableThreads
        {
            get => _availableThreads;
            set
            {
                _availableThreads = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ThreadsSeverity));
                OnPropertyChanged(nameof(ThreadsDisplayText));
            }
        }

        public int ThreadsWaitingForCpu
        {
            get => _threadsWaitingForCpu;
            set { _threadsWaitingForCpu = value; OnPropertyChanged(); }
        }

        public int RequestsWaitingForThreads
        {
            get => _requestsWaitingForThreads;
            set
            {
                _requestsWaitingForThreads = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ThreadsSeverity));
                OnPropertyChanged(nameof(ThreadsDisplayText));
            }
        }

        public HealthSeverity ThreadsSeverity
        {
            get
            {
                if (_requestsWaitingForThreads > 0) return HealthSeverity.Critical;
                if (_threadsWaitingForCpu >= 20) return HealthSeverity.Warning;
                if (_totalThreads > 0 && _availableThreads < _totalThreads * 0.10) return HealthSeverity.Warning;
                return HealthSeverity.Healthy;
            }
        }

        public string ThreadsDisplayText
        {
            get
            {
                if (_requestsWaitingForThreads > 0) return $"{_requestsWaitingForThreads} starved";
                if (_threadsWaitingForCpu >= 20) return $"{_threadsWaitingForCpu} runnable";
                if (_totalThreads > 0 && _availableThreads < _totalThreads * 0.10) return "Low";
                return "OK";
            }
        }

        // Deadlocks
        public long DeadlockCount
        {
            get => _deadlockCount;
            set
            {
                if (_isFirstDeadlockRead)
                {
                    _previousDeadlockCount = value;
                    _isFirstDeadlockRead = false;
                }
                else
                {
                    _previousDeadlockCount = _deadlockCount;
                }
                _deadlockCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DeadlocksSinceLastCheck));
                OnPropertyChanged(nameof(DeadlockSeverity));
                OnPropertyChanged(nameof(DeadlockDisplayText));
            }
        }

        public long DeadlocksSinceLastCheck => _deadlockCount - _previousDeadlockCount;

        public HealthSeverity DeadlockSeverity
        {
            get
            {
                if (DeadlocksSinceLastCheck > 0) return HealthSeverity.Critical;
                return HealthSeverity.Healthy;
            }
        }

        public string DeadlockDisplayText => DeadlocksSinceLastCheck > 0 ? $"+{DeadlocksSinceLastCheck}" : "0";

        public int? LastDeadlockMinutesAgo
        {
            get => _lastDeadlockMinutesAgo;
            set
            {
                _lastDeadlockMinutesAgo = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DeadlockDetailText));
            }
        }

        public string BlockingDetailText
        {
            get
            {
                if (_totalBlocked > 0) return $"max: {_longestBlockedSeconds:F0}s";
                if (!_lastBlockingMinutesAgo.HasValue) return "Last: Unknown";
                return $"Last: {FormatMinutesAgo(_lastBlockingMinutesAgo.Value)}";
            }
        }

        public string DeadlockDetailText
        {
            get
            {
                if (DeadlocksSinceLastCheck > 0) return $"+{DeadlocksSinceLastCheck} since last check";
                if (!_lastDeadlockMinutesAgo.HasValue) return "Last: Unknown";
                return $"Last: {FormatMinutesAgo(_lastDeadlockMinutesAgo.Value)}";
            }
        }

        private static string FormatMinutesAgo(int minutes)
        {
            if (minutes < 1) return "just now";
            if (minutes < 60) return $"{minutes}m ago";
            if (minutes < 1440) return $"{minutes / 60}h ago";
            if (minutes < 10080) return $"{minutes / 1440}d ago";
            return $"{minutes / 10080}w ago";
        }

        // Top Waits
        public string? TopWaitType
        {
            get => _topWaitType;
            set
            {
                _topWaitType = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(WaitDisplayText));
            }
        }

        public decimal TopWaitDurationSeconds
        {
            get => _topWaitDurationSeconds;
            set
            {
                _topWaitDurationSeconds = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(WaitDisplayText));
                OnPropertyChanged(nameof(WaitSeverity));
            }
        }

        public HealthSeverity WaitSeverity
        {
            get
            {
                if (_topWaitDurationSeconds >= 100) return HealthSeverity.Critical;
                if (_topWaitDurationSeconds >= 50) return HealthSeverity.Warning;
                return HealthSeverity.Healthy;
            }
        }

        public string WaitDisplayText
        {
            get
            {
                if (string.IsNullOrEmpty(_topWaitType)) return "--";
                return $"{_topWaitType}: {_topWaitDurationSeconds:F0}s";
            }
        }

        // Overall Connection Severity
        public HealthSeverity ConnectionSeverity
        {
            get
            {
                if (!_isOnline.HasValue) return HealthSeverity.Unknown;
                if (!_isOnline.Value) return HealthSeverity.Critical;
                return HealthSeverity.Healthy;
            }
        }

        public string ConnectionStatusText
        {
            get
            {
                if (!_isOnline.HasValue) return "Unknown";
                return _isOnline.Value ? "Online" : "Offline";
            }
        }

        // Overall Health
        public HealthSeverity OverallSeverity
        {
            get
            {
                // Return the worst severity
                if (ConnectionSeverity == HealthSeverity.Critical || ConnectionSeverity == HealthSeverity.Unknown) return ConnectionSeverity;
                if (CpuSeverity == HealthSeverity.Critical) return HealthSeverity.Critical;
                if (MemorySeverity == HealthSeverity.Critical) return HealthSeverity.Critical;
                if (BlockingSeverity == HealthSeverity.Critical) return HealthSeverity.Critical;
                if (DeadlockSeverity == HealthSeverity.Critical) return HealthSeverity.Critical;
                if (ThreadsSeverity == HealthSeverity.Critical) return HealthSeverity.Critical;
                if (WaitSeverity == HealthSeverity.Critical) return HealthSeverity.Critical;

                // Check warnings
                if (CpuSeverity == HealthSeverity.Warning) return HealthSeverity.Warning;
                if (MemorySeverity == HealthSeverity.Warning) return HealthSeverity.Warning;
                if (BlockingSeverity == HealthSeverity.Warning) return HealthSeverity.Warning;
                if (ThreadsSeverity == HealthSeverity.Warning) return HealthSeverity.Warning;
                if (WaitSeverity == HealthSeverity.Warning) return HealthSeverity.Warning;

                return HealthSeverity.Healthy;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
