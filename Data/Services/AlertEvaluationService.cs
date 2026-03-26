/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlHealthAssessment.Data.Models;

namespace SqlHealthAssessment.Data.Services
{
    /// <summary>
    /// Timer-based engine that periodically evaluates alert definitions against all configured servers.
    /// Runs each alert's T-SQL query, compares the result to thresholds, manages the state machine
    /// (Active → Acknowledged → Resolved), and dispatches notifications via AlertingService.
    /// </summary>
    public class AlertEvaluationService : IDisposable
    {
        private readonly ILogger<AlertEvaluationService> _logger;
        private readonly AlertDefinitionService _definitions;
        private readonly AlertHistoryService _history;
        private readonly AlertingService _alerting;
        private readonly ServerConnectionManager _connections;
        private readonly ToastService _toast;
        private readonly System.Timers.Timer _timer;

        // In-memory state: key = "alertId:serverName"
        private readonly ConcurrentDictionary<string, AlertState> _activeStates = new(StringComparer.OrdinalIgnoreCase);

        // Tracks last evaluation time per alert so we respect individual frequencies
        private readonly ConcurrentDictionary<string, DateTime> _lastEvaluation = new(StringComparer.OrdinalIgnoreCase);

        // Tracks last notification time per alert+server for cooldown
        private readonly ConcurrentDictionary<string, DateTime> _lastNotified = new(StringComparer.OrdinalIgnoreCase);

        private bool _isRunning;
        private readonly SemaphoreSlim _evaluationLock = new(1, 1);

        /// <summary>Max concurrent SQL queries during alert evaluation (prevents connection exhaustion).</summary>
        private const int MaxConcurrentQueries = 5;
        private readonly SemaphoreSlim _querySemaphore = new(MaxConcurrentQueries, MaxConcurrentQueries);

        public event Action? OnAlertsChanged;

        /// <summary>
        /// All currently active/acknowledged alert states (for UI binding).
        /// </summary>
        public IReadOnlyCollection<AlertState> ActiveAlerts =>
            _activeStates.Values.Where(s => s.Status != AlertStatus.Resolved).OrderByDescending(s => s.LastTriggered).ToList();

        public int ActiveCount => _activeStates.Values.Count(s => s.Status == AlertStatus.Active);

        public bool IsRunning => _isRunning;

        public AlertEvaluationService(
            ILogger<AlertEvaluationService> logger,
            AlertDefinitionService definitions,
            AlertHistoryService history,
            AlertingService alerting,
            ServerConnectionManager connections,
            ToastService toast)
        {
            _logger = logger;
            _definitions = definitions;
            _history = history;
            _alerting = alerting;
            _connections = connections;
            _toast = toast;

            // Base timer ticks every 30 seconds; individual alert frequencies are checked inside
            _timer = new System.Timers.Timer(30_000);
            _timer.Elapsed += async (_, _) => await EvaluateAllAsync();
        }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _timer.Start();
            _logger.LogInformation("Alert evaluation engine started (30s tick)");
        }

        public void Stop()
        {
            _isRunning = false;
            _timer.Stop();
            _logger.LogInformation("Alert evaluation engine stopped");
        }

        /// <summary>
        /// Run a single evaluation cycle across all enabled alerts and all enabled servers.
        /// </summary>
        public async Task EvaluateAllAsync()
        {
            if (!await _evaluationLock.WaitAsync(0)) return; // skip if already running

            try
            {
                var globalDefaults = _definitions.GetGlobalDefaults();
                if (!globalDefaults.Enabled) return;

                var alerts = _definitions.GetEnabledAlerts();
                var serverConnections = _connections.GetEnabledConnections();
                if (alerts.Count == 0 || serverConnections.Count == 0) return;

                var now = DateTime.UtcNow;

                // Auto-acknowledge stale alerts
                var autoAcked = _history.AutoAcknowledge(globalDefaults.AutoAcknowledgeHours);
                if (autoAcked > 0)
                {
                    _logger.LogInformation("Auto-acknowledged {Count} stale alerts", autoAcked);
                    // Also update in-memory states
                    foreach (var state in _activeStates.Values.Where(s => s.Status == AlertStatus.Active
                        && (now - s.FirstTriggered).TotalHours >= globalDefaults.AutoAcknowledgeHours))
                    {
                        state.Status = AlertStatus.Acknowledged;
                        state.AcknowledgedAt = now;
                    }
                }

                // Evaluate each alert that is due
                foreach (var alert in alerts)
                {
                    // Skip if not due yet based on frequency
                    var evalKey = alert.Id;
                    if (_lastEvaluation.TryGetValue(evalKey, out var lastEval)
                        && (now - lastEval).TotalSeconds < alert.FrequencySeconds)
                    {
                        continue;
                    }

                    // Skip special query modes for now (error_log_scan, connectivity_check)
                    if (!string.IsNullOrEmpty(alert.QueryMode) && alert.QueryMode != "standard")
                        continue;

                    _lastEvaluation[evalKey] = now;

                    // Run against each server with bounded concurrency
                    var tasks = new List<Task>();
                    foreach (var conn in serverConnections)
                    {
                        foreach (var serverName in conn.GetServerList())
                        {
                            tasks.Add(ThrottledEvaluateAsync(alert, conn, serverName, globalDefaults));
                        }
                    }

                    await Task.WhenAll(tasks);
                }

                // Resolve alerts that are no longer triggering
                ResolveCleared();

                OnAlertsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Alert evaluation cycle failed");
            }
            finally
            {
                _evaluationLock.Release();
            }
        }

        private async Task ThrottledEvaluateAsync(
            AlertDefinition alert,
            ServerConnection connection,
            string serverName,
            AlertGlobalDefaults globalDefaults)
        {
            await _querySemaphore.WaitAsync();
            try
            {
                await EvaluateAlertOnServerAsync(alert, connection, serverName, globalDefaults);
            }
            finally
            {
                _querySemaphore.Release();
            }
        }

        private async Task EvaluateAlertOnServerAsync(
            AlertDefinition alert,
            ServerConnection connection,
            string serverName,
            AlertGlobalDefaults globalDefaults)
        {
            var stateKey = $"{alert.Id}:{serverName}".ToLowerInvariant();

            try
            {
                var value = await ExecuteAlertQueryAsync(alert, connection, serverName);
                if (value == null) return; // query returned no data

                var isWarning = IsThresholdBreached(value.Value, alert.Thresholds.Warning, alert.Operator);
                var isCritical = alert.Thresholds.Critical.HasValue
                    && IsThresholdBreached(value.Value, alert.Thresholds.Critical, alert.Operator);

                if (isWarning || isCritical)
                {
                    var severity = isCritical ? "Critical" : "Warning";
                    var thresholdUsed = isCritical ? alert.Thresholds.Critical!.Value : alert.Thresholds.Warning!.Value;

                    if (_activeStates.TryGetValue(stateKey, out var existing))
                    {
                        // Already active — increment hit count, update value
                        existing.HitCount++;
                        existing.LastValue = value.Value;
                        existing.LastTriggered = DateTime.UtcNow;
                        existing.Severity = severity;
                        existing.Message = FormatMessage(alert, serverName, value.Value, thresholdUsed, severity);

                        // Check cooldown before re-notifying
                        var cooldown = _definitions.GetCooldown(alert);
                        if (!_lastNotified.TryGetValue(stateKey, out var lastNotify)
                            || (DateTime.UtcNow - lastNotify) >= cooldown)
                        {
                            DispatchNotification(alert, existing);
                            _lastNotified[stateKey] = DateTime.UtcNow;
                        }

                        _history.UpsertAlert(existing);
                    }
                    else
                    {
                        // New alert
                        var state = new AlertState
                        {
                            AlertId = alert.Id,
                            AlertName = alert.Name,
                            ServerName = serverName,
                            Severity = severity,
                            Status = AlertStatus.Active,
                            LastValue = value.Value,
                            ThresholdValue = thresholdUsed,
                            HitCount = 1,
                            FirstTriggered = DateTime.UtcNow,
                            LastTriggered = DateTime.UtcNow,
                            Message = FormatMessage(alert, serverName, value.Value, thresholdUsed, severity)
                        };
                        _activeStates[stateKey] = state;
                        _history.UpsertAlert(state);

                        DispatchNotification(alert, state);
                        _lastNotified[stateKey] = DateTime.UtcNow;

                        _logger.LogWarning("Alert fired: {AlertName} on {Server} ({Severity}) — value: {Value}",
                            alert.Name, serverName, severity, value.Value);
                    }
                }
                else
                {
                    // Condition cleared — mark as resolved if was active
                    if (_activeStates.TryRemove(stateKey, out var cleared))
                    {
                        cleared.Status = AlertStatus.Resolved;
                        cleared.ResolvedAt = DateTime.UtcNow;
                        _history.ResolveAlert(alert.Id, serverName);
                        _lastNotified.TryRemove(stateKey, out _);

                        _logger.LogInformation("Alert resolved: {AlertName} on {Server}", alert.Name, serverName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to evaluate alert {AlertId} on {Server}", alert.Id, serverName);
            }
        }

        private async Task<double?> ExecuteAlertQueryAsync(
            AlertDefinition alert,
            ServerConnection connection,
            string serverName)
        {
            var connString = connection.GetConnectionString(serverName, "master");

            using var sqlConn = new SqlConnection(connString);
            await sqlConn.OpenAsync();

            using var cmd = new SqlCommand(alert.Query, sqlConn)
            {
                CommandTimeout = 15
            };

            var result = await cmd.ExecuteScalarAsync();
            if (result == null || result == DBNull.Value) return null;
            return Convert.ToDouble(result);
        }

        private static bool IsThresholdBreached(double value, double? threshold, string op)
        {
            if (!threshold.HasValue) return false;
            return op == "less_than"
                ? value < threshold.Value
                : value > threshold.Value;
        }

        private void DispatchNotification(AlertDefinition alert, AlertState state)
        {
            // Toast notification
            var toastMethod = state.Severity == "Critical"
                ? (Action<string, string, int>)_toast.ShowError
                : _toast.ShowWarning;
            toastMethod($"{alert.Name} — {state.ServerName}",
                state.Message, 6000);

            // Feed into existing AlertingService notification pipeline for email/Teams
            var notification = new AlertNotification
            {
                AlertName = alert.Name,
                Metric = alert.Id,
                CurrentValue = state.LastValue,
                ThresholdValue = state.ThresholdValue,
                Severity = state.Severity.ToLowerInvariant(),
                InstanceName = state.ServerName,
                Message = state.Message,
                TriggeredAt = state.LastTriggered
            };

            // Use reflection-free approach: call EvaluateAlerts with pre-built metrics
            var metrics = new Dictionary<string, double> { [alert.Id] = state.LastValue };
            _alerting.EvaluateAlerts(metrics, state.ServerName);
        }

        /// <summary>
        /// Resolve any in-memory states that haven't been refreshed in 3x their frequency
        /// (the alert condition has likely cleared but was never re-evaluated to confirm).
        /// </summary>
        private void ResolveCleared()
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in _activeStates)
            {
                var state = kvp.Value;
                var alert = _definitions.GetAlert(state.AlertId);
                if (alert == null) continue;

                var staleCutoff = TimeSpan.FromSeconds(alert.FrequencySeconds * 3);
                if ((now - state.LastTriggered) > staleCutoff && state.Status == AlertStatus.Active)
                {
                    state.Status = AlertStatus.Resolved;
                    state.ResolvedAt = now;
                    _activeStates.TryRemove(kvp.Key, out _);
                    _history.ResolveAlert(state.AlertId, state.ServerName);
                    _lastNotified.TryRemove(kvp.Key, out _);
                }
            }
        }

        public void AcknowledgeAlert(string alertId, string serverName)
        {
            var key = $"{alertId}:{serverName}".ToLowerInvariant();
            if (_activeStates.TryGetValue(key, out var state))
            {
                state.Status = AlertStatus.Acknowledged;
                state.AcknowledgedAt = DateTime.UtcNow;
            }
            _history.AcknowledgeAlert(alertId, serverName);
            OnAlertsChanged?.Invoke();
        }

        public void AcknowledgeAll()
        {
            foreach (var state in _activeStates.Values.Where(s => s.Status == AlertStatus.Active))
            {
                state.Status = AlertStatus.Acknowledged;
                state.AcknowledgedAt = DateTime.UtcNow;
            }
            _history.AcknowledgeAll();
            OnAlertsChanged?.Invoke();
        }

        private static string FormatMessage(AlertDefinition alert, string server, double value, double threshold, string severity)
        {
            var direction = alert.Operator == "less_than" ? "below" : "above";
            var unit = alert.Unit switch
            {
                "percent" => "%",
                "seconds" => "s",
                "milliseconds" => "ms",
                "megabytes" => " MB",
                "hours" => " hrs",
                "minutes" => " min",
                "count" => "",
                "per_second" => "/s",
                "per_minute" => "/min",
                _ => ""
            };
            return $"{value:N1}{unit} ({direction} {threshold:N1}{unit} {severity.ToLower()} threshold)";
        }

        public void Dispose()
        {
            Stop();
            _timer?.Dispose();
            _evaluationLock?.Dispose();
            _querySemaphore?.Dispose();
        }
    }
}
