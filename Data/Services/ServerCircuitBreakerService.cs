/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SQLTriage.Data;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Singleton circuit breaker for per-server polling suppression.
    /// Prevents dead servers from generating thousands of errors per hour by
    /// applying exponential back-off after consecutive failures:
    ///   3 failures  → skip for 60 s
    ///   6 failures  → skip for 5 min
    ///  10 failures  → skip for 30 min (cap)
    /// Emits <see cref="AuditEventType.ServerCircuitOpened"/> and
    /// <see cref="AuditEventType.ServerCircuitClosed"/> once per state transition.
    /// </summary>
    public class ServerCircuitBreakerService
    {
        private readonly ILogger<ServerCircuitBreakerService> _logger;
        private readonly AuditLogService? _audit;

        // State is a struct; access is guarded by per-key locks stored in _locks.
        private readonly ConcurrentDictionary<string, ServerState> _states
            = new(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, object> _locks
            = new(StringComparer.OrdinalIgnoreCase);

        // Back-off schedule: (minConsecutiveFailures, retryDelay)
        private static readonly (int Threshold, TimeSpan Delay)[] BackOffSchedule =
        {
            (10, TimeSpan.FromMinutes(30)),
            ( 6, TimeSpan.FromMinutes(5)),
            ( 3, TimeSpan.FromSeconds(60)),
        };

        public ServerCircuitBreakerService(
            ILogger<ServerCircuitBreakerService> logger,
            AuditLogService? audit = null)
        {
            _logger = logger;
            _audit = audit;
        }

        /// <summary>
        /// Returns true if polling should proceed for the given server.
        /// Returns false (and logs at most once) when in back-off.
        /// </summary>
        public bool ShouldAttempt(string serverName)
        {
            var state = _states.GetOrAdd(serverName, _ => new ServerState());
            lock (GetLock(serverName))
            {
                if (state.ConsecutiveFailures == 0) return true;
                if (DateTime.UtcNow >= state.NextRetryAt) return true;

                // Still in back-off — no log spam; callers just get false.
                return false;
            }
        }

        /// <summary>
        /// Records a successful connection; resets the failure counter and
        /// emits <see cref="AuditEventType.ServerCircuitClosed"/> if the circuit was open.
        /// </summary>
        public void RecordSuccess(string serverName)
        {
            var state = _states.GetOrAdd(serverName, _ => new ServerState());
            bool wasOpen;
            lock (GetLock(serverName))
            {
                wasOpen = state.ConsecutiveFailures >= BackOffSchedule[^1].Threshold;
                state.ConsecutiveFailures = 0;
                state.NextRetryAt = DateTime.MinValue;
            }

            if (wasOpen)
            {
                _logger.LogInformation(
                    "[CircuitBreaker] Server {Server} circuit CLOSED — polling resumed", serverName);
                _audit?.Enqueue(AuditEventType.ServerCircuitClosed, AuditSeverity.Info,
                    $"Server circuit closed for '{serverName}'; polling resumed.",
                    new Dictionary<string, string> { ["Server"] = serverName });
            }
        }

        /// <summary>
        /// Records a failed connection. Applies back-off and emits
        /// <see cref="AuditEventType.ServerCircuitOpened"/> on the transition tick.
        /// </summary>
        public void RecordFailure(string serverName)
        {
            var state = _states.GetOrAdd(serverName, _ => new ServerState());
            bool justOpened = false;
            TimeSpan backOffDelay = TimeSpan.Zero;

            lock (GetLock(serverName))
            {
                state.ConsecutiveFailures++;
                var prevNextRetry = state.NextRetryAt;

                // Determine applicable back-off tier.
                TimeSpan newDelay = TimeSpan.Zero;
                foreach (var (threshold, delay) in BackOffSchedule)
                {
                    if (state.ConsecutiveFailures >= threshold)
                    {
                        newDelay = delay;
                        break;
                    }
                }

                if (newDelay > TimeSpan.Zero)
                {
                    state.NextRetryAt = DateTime.UtcNow.Add(newDelay);
                    backOffDelay = newDelay;
                    // "Just opened" = first tick that puts us into back-off
                    justOpened = prevNextRetry == DateTime.MinValue || prevNextRetry < DateTime.UtcNow;
                }
            }

            if (justOpened && backOffDelay > TimeSpan.Zero)
            {
                var nextRetry = state.NextRetryAt;
                _logger.LogInformation(
                    "[CircuitBreaker] Server {Server} circuit OPENED after {N} failures. " +
                    "Next retry at {NextRetry} (back-off {Delay})",
                    serverName, state.ConsecutiveFailures,
                    nextRetry.ToString("HH:mm:ss"), backOffDelay);

                _audit?.Enqueue(AuditEventType.ServerCircuitOpened, AuditSeverity.Warning,
                    $"Server circuit opened for '{serverName}' after {state.ConsecutiveFailures} consecutive failures.",
                    new Dictionary<string, string>
                    {
                        ["Server"] = serverName,
                        ["ConsecutiveFailures"] = state.ConsecutiveFailures.ToString(),
                        ["BackOffSeconds"] = ((int)backOffDelay.TotalSeconds).ToString(),
                        ["NextRetryAt"] = nextRetry.ToString("o")
                    });
            }
        }

        private object GetLock(string serverName)
            => _locks.GetOrAdd(serverName, _ => new object());

        private sealed class ServerState
        {
            public int ConsecutiveFailures { get; set; }
            public DateTime NextRetryAt { get; set; } = DateTime.MinValue;
        }
    }
}
