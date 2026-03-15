/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace SqlHealthAssessment.Data
{
    /// <summary>
    /// Rate limiter to prevent brute-force attacks.
    /// Limits to a maximum number of SQL queries per minute and connection attempts per minute.
    /// </summary>
    public class RateLimiter
    {
        private readonly IConfiguration _configuration;
        
        /// <summary>
        /// Maximum queries per minute (default 50)
        /// </summary>
        public int MaxQueriesPerMinute { get; private set; } = 50;
        
        /// <summary>
        /// Maximum connection attempts per minute (default 10)
        /// </summary>
        public int MaxConnectionAttemptsPerMinute { get; private set; } = 10;
        
        /// <summary>
        /// Current query count in the sliding window
        /// </summary>
        public int CurrentQueryCount { get; private set; }
        
        /// <summary>
        /// Current connection attempt count in the sliding window
        /// </summary>
        public int CurrentConnectionAttemptCount { get; private set; }
        
        /// <summary>
        /// Whether the rate limit has been exceeded
        /// </summary>
        public bool IsRateLimited => CurrentQueryCount >= MaxQueriesPerMinute;
        
        /// <summary>
        /// Whether the connection attempt rate limit has been exceeded
        /// </summary>
        public bool IsConnectionRateLimited => CurrentConnectionAttemptCount >= MaxConnectionAttemptsPerMinute;
        
        /// <summary>
        /// Event raised when rate limit is exceeded
        /// </summary>
        public event EventHandler<RateLimitExceededEventArgs>? RateLimitExceeded;

        /// <summary>
        /// Event raised when connection attempt rate limit is exceeded
        /// </summary>
        public event EventHandler<RateLimitExceededEventArgs>? ConnectionRateLimitExceeded;

        /// <summary>
        /// Queue of query timestamps for sliding window
        /// </summary>
        private readonly ConcurrentQueue<DateTime> _queryTimestamps = new();
        
        /// <summary>
        /// Queue of connection attempt timestamps for sliding window
        /// </summary>
        private readonly ConcurrentQueue<DateTime> _connectionTimestamps = new();
        
        /// <summary>
        /// Lock for thread safety
        /// </summary>
        private readonly object _lock = new();

        public RateLimiter(IConfiguration configuration)
        {
            _configuration = configuration;
            LoadConfiguration();
            
            // Start cleanup timer
            var timer = new Timer(CleanupOldTimestamps, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        }

        private void LoadConfiguration()
        {
            MaxQueriesPerMinute = _configuration.GetValue<int>("RateLimiting:MaxQueriesPerMinute", 50);
            MaxConnectionAttemptsPerMinute = _configuration.GetValue<int>("RateLimiting:MaxConnectionAttemptsPerMinute", 10);
        }

        /// <summary>
        /// Attempts to execute a query. Returns true if allowed, false if rate limited.
        /// </summary>
        public bool TryAcquire()
        {
            CleanupOldTimestamps(null);
            
            lock (_lock)
            {
                if (CurrentQueryCount >= MaxQueriesPerMinute)
                {
                    // Rate limit exceeded
                    RateLimitExceeded?.Invoke(this, new RateLimitExceededEventArgs
                    {
                        CurrentCount = CurrentQueryCount,
                        MaxAllowed = MaxQueriesPerMinute,
                        TimeToReset = GetTimeToReset()
                    });
                    return false;
                }
                
                // Record this query
                _queryTimestamps.Enqueue(DateTime.UtcNow);
                CurrentQueryCount = _queryTimestamps.Count;
                return true;
            }
        }

        /// <summary>
        /// Gets the remaining queries available in the current window
        /// </summary>
        public int GetRemainingQueries()
        {
            CleanupOldTimestamps(null);
            return Math.Max(0, MaxQueriesPerMinute - CurrentQueryCount);
        }

        /// <summary>
        /// Attempts to acquire for a connection attempt. Returns true if allowed, false if rate limited.
        /// </summary>
        public bool TryAcquireForConnection()
        {
            CleanupOldConnectionTimestamps(null);
            
            lock (_lock)
            {
                if (CurrentConnectionAttemptCount >= MaxConnectionAttemptsPerMinute)
                {
                    // Connection rate limit exceeded
                    ConnectionRateLimitExceeded?.Invoke(this, new RateLimitExceededEventArgs
                    {
                        CurrentCount = CurrentConnectionAttemptCount,
                        MaxAllowed = MaxConnectionAttemptsPerMinute,
                        TimeToReset = GetConnectionTimeToReset()
                    });
                    return false;
                }
                
                // Record this connection attempt
                _connectionTimestamps.Enqueue(DateTime.UtcNow);
                CurrentConnectionAttemptCount = _connectionTimestamps.Count;
                return true;
            }
        }

        /// <summary>
        /// Gets the remaining connection attempts available in the current window
        /// </summary>
        public int GetRemainingConnectionAttempts()
        {
            CleanupOldConnectionTimestamps(null);
            return Math.Max(0, MaxConnectionAttemptsPerMinute - CurrentConnectionAttemptCount);
        }

        /// <summary>
        /// Gets the time until the connection rate limit resets
        /// </summary>
        public TimeSpan GetConnectionTimeToReset()
        {
            if (_connectionTimestamps.IsEmpty)
                return TimeSpan.Zero;
            
            if (_connectionTimestamps.TryPeek(out var oldest))
            {
                var expires = oldest.AddMinutes(1);
                var remaining = expires - DateTime.UtcNow;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
            
            return TimeSpan.Zero;
        }

        /// <summary>
        /// Gets the time until the rate limit resets (oldest query expires)
        /// </summary>
        public TimeSpan GetTimeToReset()
        {
            if (_queryTimestamps.IsEmpty)
                return TimeSpan.Zero;
            
            if (_queryTimestamps.TryPeek(out var oldest))
            {
                var expires = oldest.AddMinutes(1);
                var remaining = expires - DateTime.UtcNow;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
            
            return TimeSpan.Zero;
        }

        /// <summary>
        /// Cleans up timestamps older than 1 minute (sliding window)
        /// </summary>
        private void CleanupOldTimestamps(object? state)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-1);
            
            while (_queryTimestamps.TryPeek(out var timestamp))
            {
                if (timestamp < cutoff)
                {
                    _queryTimestamps.TryDequeue(out _);
                }
                else
                {
                    break;
                }
            }
            
            CurrentQueryCount = _queryTimestamps.Count;
        }

        /// <summary>
        /// Cleans up connection timestamps older than 1 minute (sliding window)
        /// </summary>
        private void CleanupOldConnectionTimestamps(object? state)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-1);
            
            while (_connectionTimestamps.TryPeek(out var timestamp))
            {
                if (timestamp < cutoff)
                {
                    _connectionTimestamps.TryDequeue(out _);
                }
                else
                {
                    break;
                }
            }
            
            CurrentConnectionAttemptCount = _connectionTimestamps.Count;
        }

        /// <summary>
        /// Resets the rate limiter (for testing or admin override)
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                while (_queryTimestamps.TryDequeue(out _)) { }
                CurrentQueryCount = 0;
                while (_connectionTimestamps.TryDequeue(out _)) { }
                CurrentConnectionAttemptCount = 0;
            }
        }
    }

    public class RateLimitExceededEventArgs : EventArgs
    {
        public int CurrentCount { get; set; }
        public int MaxAllowed { get; set; }
        public TimeSpan TimeToReset { get; set; }
    }
}
