/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace SQLTriage.Data
{
    /// <summary>
    /// Session management service for idle timeout and activity tracking.
    /// Implements 1-hour idle timeout with automatic refresh rate adjustment.
    /// </summary>
    public class SessionManager
    {
        private readonly IConfiguration _configuration;
        private DateTime _lastActivity = DateTime.UtcNow;
        private readonly object _lock = new();

        /// <summary>
        /// Idle timeout in minutes (default 60 minutes)
        /// </summary>
        public int IdleTimeoutMinutes { get; private set; } = 60;

        /// <summary>
        /// Refresh rate when idle (default 30 minutes)
        /// </summary>
        public int IdleRefreshRateMinutes { get; private set; } = 30;

        /// <summary>
        /// Normal refresh rate (restored after idle)
        /// </summary>
        public int NormalRefreshRateMinutes { get; private set; } = 5;

        /// <summary>
        /// Current refresh rate
        /// </summary>
        public int CurrentRefreshRateMinutes { get; private set; } = 5;

        /// <summary>
        /// Whether the session is currently in idle mode
        /// </summary>
        public bool IsIdle { get; private set; }

        /// <summary>
        /// Event raised when session becomes idle or active
        /// </summary>
        public event EventHandler<SessionStateChangedEventArgs>? SessionStateChanged;

        public SessionManager(IConfiguration configuration)
        {
            _configuration = configuration;
            LoadConfiguration();
        }

        private void LoadConfiguration()
        {
            IdleTimeoutMinutes = _configuration.GetValue<int>("Session:IdleTimeoutMinutes", 60);
            IdleRefreshRateMinutes = _configuration.GetValue<int>("Session:IdleRefreshRateMinutes", 30);
            NormalRefreshRateMinutes = _configuration.GetValue<int>("Session:NormalRefreshRateMinutes", 5);
            CurrentRefreshRateMinutes = NormalRefreshRateMinutes;
        }

        /// <summary>
        /// Records user activity and resets the idle timer
        /// </summary>
        public void RecordActivity()
        {
            lock (_lock)
            {
                var wasIdle = IsIdle;
                _lastActivity = DateTime.UtcNow;

                if (IsIdle)
                {
                    // Restore normal refresh rate when becoming active
                    IsIdle = false;
                    CurrentRefreshRateMinutes = NormalRefreshRateMinutes;

                    if (wasIdle)
                    {
                        SessionStateChanged?.Invoke(this, new SessionStateChangedEventArgs
                        {
                            IsIdle = false,
                            RefreshRateMinutes = CurrentRefreshRateMinutes
                        });
                    }
                }
            }
        }

        /// <summary>
        /// Checks if the session has timed out due to inactivity
        /// </summary>
        public void CheckIdleState()
        {
            lock (_lock)
            {
                var idleTime = DateTime.UtcNow - _lastActivity;
                var shouldBeIdle = idleTime.TotalMinutes >= IdleTimeoutMinutes;

                if (shouldBeIdle && !IsIdle)
                {
                    // Enter idle mode
                    IsIdle = true;
                    CurrentRefreshRateMinutes = IdleRefreshRateMinutes;

                    SessionStateChanged?.Invoke(this, new SessionStateChangedEventArgs
                    {
                        IsIdle = true,
                        RefreshRateMinutes = CurrentRefreshRateMinutes
                    });
                }
            }
        }

        /// <summary>
        /// Gets the remaining idle time before timeout
        /// </summary>
        public TimeSpan GetRemainingIdleTime()
        {
            lock (_lock)
            {
                var elapsed = DateTime.UtcNow - _lastActivity;
                var remaining = TimeSpan.FromMinutes(IdleTimeoutMinutes) - elapsed;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        /// <summary>
        /// Resets the session completely
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _lastActivity = DateTime.UtcNow;
                IsIdle = false;
                CurrentRefreshRateMinutes = NormalRefreshRateMinutes;
            }
        }
    }

    public class SessionStateChangedEventArgs : EventArgs
    {
        public bool IsIdle { get; set; }
        public int RefreshRateMinutes { get; set; }
    }
}
