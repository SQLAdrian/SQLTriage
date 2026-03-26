/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using SqlHealthAssessment.Data.Models;
using SqlHealthAssessment.Data.Services;
using Microsoft.Extensions.Logging;

namespace SqlHealthAssessment.Data
{
    public class AlertingService
    {
        private readonly ILogger<AlertingService> _logger;
        private readonly NotificationChannelService? _notificationChannels;
        private readonly string _alertsFilePath;
        private List<AlertThreshold> _thresholds = new();
        private readonly ConcurrentQueue<AlertNotification> _notifications = new();
        private readonly object _lock = new();
        private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

        /// <summary>
        /// Cooldown tracker: key = "{instanceName}:{metric}" → last alert time.
        /// Prevents the same metric on the same instance from firing more than once
        /// within the cooldown window (default 5 minutes).
        /// </summary>
        private readonly ConcurrentDictionary<string, DateTime> _lastAlertTime = new(
            StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan AlertCooldown = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Set of notification IDs the user has explicitly acknowledged.
        /// Cleared automatically when the corresponding alert no longer fires
        /// (so the badge re-appears if the condition returns).
        /// </summary>
        private readonly HashSet<string> _acknowledgedIds = new(StringComparer.Ordinal);

        public AlertingService(ILogger<AlertingService> logger, NotificationChannelService? notificationChannels = null)
        {
            _logger = logger;
            _notificationChannels = notificationChannels;
            _alertsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "alert-configurations.json");
            LoadThresholds();
        }

        public List<AlertThreshold> GetThresholds()
        {
            lock (_lock)
            {
                return _thresholds.ToList();
            }
        }

        public void AddThreshold(AlertThreshold threshold)
        {
            lock (_lock)
            {
                _thresholds.Add(threshold);
                SaveThresholds();
            }
        }

        public void UpdateThreshold(AlertThreshold threshold)
        {
            lock (_lock)
            {
                var index = _thresholds.FindIndex(t => t.Id == threshold.Id);
                if (index >= 0)
                {
                    _thresholds[index] = threshold;
                    SaveThresholds();
                }
            }
        }

        public void RemoveThreshold(string id)
        {
            lock (_lock)
            {
                _thresholds.RemoveAll(t => t.Id == id);
                SaveThresholds();
            }
        }

        public List<AlertNotification> GetNotifications(int maxCount = 50)
        {
            return _notifications.Take(maxCount).ToList();
        }

        public int GetUnacknowledgedCount()
        {
            lock (_lock)
            {
                return _notifications.Count(n => !_acknowledgedIds.Contains(n.Id));
            }
        }

        /// <summary>
        /// Acknowledges a single notification by its ID.
        /// The notification remains in the list but is excluded from the unacknowledged count
        /// until the alert condition clears and re-triggers.
        /// </summary>
        public void AcknowledgeNotification(string id)
        {
            lock (_lock)
            {
                _acknowledgedIds.Add(id);
                var notification = _notifications.FirstOrDefault(n => n.Id == id);
                if (notification != null)
                    notification.IsAcknowledged = true;
                PruneAcknowledgedIds();
            }
        }

        /// <summary>
        /// Acknowledges all current notifications in one operation.
        /// </summary>
        public void AcknowledgeAll()
        {
            lock (_lock)
            {
                foreach (var n in _notifications)
                {
                    _acknowledgedIds.Add(n.Id);
                    n.IsAcknowledged = true;
                }
                PruneAcknowledgedIds();
            }
        }

        public void ClearNotifications()
        {
            lock (_lock)
            {
                while (_notifications.TryDequeue(out _)) { }
                _acknowledgedIds.Clear();
            }
        }

        /// <summary>
        /// Removes acknowledged IDs that no longer correspond to any queued notification.
        /// Must be called inside _lock.
        /// </summary>
        private void PruneAcknowledgedIds()
        {
            var activeIds = new HashSet<string>(_notifications.Select(n => n.Id));
            _acknowledgedIds.RemoveWhere(id => !activeIds.Contains(id));
        }

        /// <summary>
        /// Evaluates alert thresholds against the supplied metrics (backward-compatible, no instance context).
        /// </summary>
        public List<AlertEvaluationResult> EvaluateAlerts(Dictionary<string, double> metrics)
            => EvaluateAlerts(metrics, instanceName: string.Empty);

        /// <summary>
        /// Evaluates alert thresholds against the supplied metrics for a specific instance.
        /// Applies a per-metric, per-instance cooldown to avoid duplicate notifications.
        /// </summary>
        public List<AlertEvaluationResult> EvaluateAlerts(
            Dictionary<string, double> metrics, string instanceName)
        {
            var results = new List<AlertEvaluationResult>();

            lock (_lock)
            {
                foreach (var threshold in _thresholds.Where(t => t.Enabled))
                {
                    if (metrics.TryGetValue(threshold.Metric.ToLower(), out var currentValue))
                    {
                        var isTriggered = threshold.Condition switch
                        {
                            "greater_than" => currentValue > threshold.ThresholdValue,
                            "less_than" => currentValue < threshold.ThresholdValue,
                            "equals" => Math.Abs(currentValue - threshold.ThresholdValue) < 0.01,
                            _ => false
                        };

                        if (isTriggered)
                        {
                            var result = new AlertEvaluationResult
                            {
                                IsTriggered = true,
                                AlertId = threshold.Id,
                                AlertName = threshold.Name,
                                CurrentValue = currentValue,
                                ThresholdValue = threshold.ThresholdValue,
                                Severity = threshold.Severity,
                                InstanceName = instanceName,
                                Message = string.IsNullOrEmpty(instanceName)
                                    ? $"{threshold.Name}: {currentValue} {threshold.Condition.Replace("_", " ")} {threshold.ThresholdValue}"
                                    : $"[{instanceName}] {threshold.Name}: {currentValue} {threshold.Condition.Replace("_", " ")} {threshold.ThresholdValue}"
                            };
                            results.Add(result);

                            // Cooldown check: skip notification if same metric+instance fired recently
                            var cooldownKey = $"{instanceName}:{threshold.Metric}".ToLower();
                            if (_lastAlertTime.TryGetValue(cooldownKey, out var lastTime)
                                && (DateTime.UtcNow - lastTime) < AlertCooldown)
                            {
                                continue; // still in cooldown — skip notification, keep evaluation result
                            }

                            _lastAlertTime[cooldownKey] = DateTime.UtcNow;

                            var notification = new AlertNotification
                            {
                                AlertName = threshold.Name,
                                Metric = threshold.Metric,
                                CurrentValue = currentValue,
                                ThresholdValue = threshold.ThresholdValue,
                                Severity = threshold.Severity,
                                InstanceName = instanceName,
                                Message = result.Message
                            };
                            _notifications.Enqueue(notification);

                            // Dispatch to outbound channels (email, Teams) — fire-and-forget
                            if (_notificationChannels != null)
                            {
                                _ = Task.Run(async () =>
                                {
                                    try { await _notificationChannels.DispatchAsync(notification); }
                                    catch (Exception dispatchEx)
                                    {
                                        _logger.LogError(dispatchEx, "Failed to dispatch notification for {AlertName}", notification.AlertName);
                                    }
                                });
                            }

                            PruneAcknowledgedIds();

                            // Keep only last 100 notifications
                            while (_notifications.Count > 100)
                            {
                                _notifications.TryDequeue(out _);
                            }
                        }
                    }
                }
            }

            if (results.Any(r => r.Severity == "critical"))
            {
                _logger.LogWarning("CRITICAL ALERTS TRIGGERED: {Count}", results.Count(r => r.Severity == "critical"));
            }

            return results;
        }

        private void LoadThresholds()
        {
            try
            {
                if (File.Exists(_alertsFilePath))
                {
                    _thresholds = ConfigFileHelper.Load<List<AlertThreshold>>(_alertsFilePath, _jsonOptions);
                    _logger.LogInformation("Loaded {Count} alert thresholds", _thresholds.Count);
                }
                else
                {
                    // Create default thresholds
                    _thresholds = GetDefaultThresholds();
                    SaveThresholds();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading alert thresholds");
                _thresholds = new List<AlertThreshold>();
            }
        }

        private void SaveThresholds()
        {
            try
            {
                ConfigFileHelper.Save(_alertsFilePath, _thresholds, _jsonOptions);
                _logger.LogInformation("Saved {Count} alert thresholds", _thresholds.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving alert thresholds");
            }
        }

        private List<AlertThreshold> GetDefaultThresholds()
        {
            return new List<AlertThreshold>
            {
                new AlertThreshold
                {
                    Name = "High CPU Usage",
                    Metric = "cpu",
                    Condition = "greater_than",
                    ThresholdValue = 80,
                    Enabled = true,
                    Severity = "warning",
                    Description = "Alert when CPU usage exceeds 80%"
                },
                new AlertThreshold
                {
                    Name = "Critical CPU Usage",
                    Metric = "cpu",
                    Condition = "greater_than",
                    ThresholdValue = 95,
                    Enabled = true,
                    Severity = "critical",
                    Description = "Alert when CPU usage exceeds 95%"
                },
                new AlertThreshold
                {
                    Name = "High Memory Usage",
                    Metric = "memory",
                    Condition = "greater_than",
                    ThresholdValue = 85,
                    Enabled = true,
                    Severity = "warning",
                    Description = "Alert when memory usage exceeds 85%"
                },
                new AlertThreshold
                {
                    Name = "High Connection Count",
                    Metric = "connections",
                    Condition = "greater_than",
                    ThresholdValue = 100,
                    Enabled = true,
                    Severity = "warning",
                    Description = "Alert when connection count exceeds 100"
                },
                new AlertThreshold
                {
                    Name = "Deadlock Detected",
                    Metric = "deadlocks",
                    Condition = "greater_than",
                    ThresholdValue = 0,
                    Enabled = true,
                    Severity = "critical",
                    Description = "Alert when any deadlock is detected"
                }
            };
        }
    }
}
