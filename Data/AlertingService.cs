/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;

namespace SQLTriage.Data
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

        /// <summary>
        /// The store this service ACTUALLY owns, since 2026-08-04: a JSON ARRAY of
        /// <see cref="AlertThreshold"/>.
        ///
        /// <para><b>It used to be alert-definitions.json, and that was a two-schema collision on one
        /// path.</b> <c>AlertDefinitionService</c> reads the same file name (case-insensitively —
        /// it probes <c>config/</c> then <c>Config/</c>, which on Windows is one directory) as an
        /// OBJECT: <c>{version, globalDefaults, categories, alerts}</c>. That object is what
        /// SQLTriage.csproj SHIPS, 95,970 bytes and 80 curated alerts. So this service read every
        /// stock install's shipped catalogue as a malformed array, reported the install damaged,
        /// evaluated ZERO thresholds, and quarantined a 96 KB copy on each start — and the
        /// announce-tier write then replaced the catalogue with a one-element array, which the
        /// updater PRESERVES rather than repairs.</para>
        ///
        /// <para>Neither tier fixes that. Announcing is what did the damage; refusing would fire on
        /// 100% of stock installs, and a guard that refuses on every install is the guard an
        /// operator disables — taking the intake-SAS and credential refusals with it. The defect was
        /// never the tier, it was two schemas sharing a path, so the path is what changed.</para>
        /// </summary>
        internal const string ThresholdStoreFileName = "alert-thresholds.json";

        /// <summary>
        /// The path this service used to write. Read once, at startup, ONLY when it parses as this
        /// service's array schema — which a shipped catalogue never does. See
        /// <see cref="AdoptLegacyThresholdStore"/>.
        /// </summary>
        internal const string LegacyThresholdStoreFileName = "alert-definitions.json";

        private readonly string _legacyThresholdsFilePath;

        /// <param name="thresholdsFilePath">
        /// Test seam (mirrors ServerConnectionManager's connectionsFilePath and OwnerAssignmentStore's
        /// pathOverride): null ⇒ the real Config/ path, exactly as before.
        /// </param>
        public AlertingService(ILogger<AlertingService> logger,
                               NotificationChannelService? notificationChannels = null,
                               string? thresholdsFilePath = null)
        {
            _logger = logger;
            _notificationChannels = notificationChannels;
            _alertsFilePath = thresholdsFilePath
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", ThresholdStoreFileName);
            _legacyThresholdsFilePath = Path.Combine(
                Path.GetDirectoryName(_alertsFilePath) ?? ".", LegacyThresholdStoreFileName);
            LoadThresholds();
        }

        public List<AlertThreshold> GetThresholds()
        {
            lock (_lock)
            {
                return _thresholds.ToList();
            }
        }

        // AddThreshold has no production call site since the write API was removed (DECISIONS
        // 2026-08-26, ruling 3): the POST/PUT/DELETE /alerts/thresholds routes that reached the
        // store are gone. It is retained because ConfigStoreWriteGuardTests drives the shared
        // config-store write guard through it. UpdateThreshold/RemoveThreshold were removed with
        // the routes — nothing else called them.
        public void AddThreshold(AlertThreshold threshold)
        {
            lock (_lock)
            {
                _thresholds.Add(threshold);
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
                                // This legacy path fires on a fixed configured threshold and nothing
                                // else, so the basis is never in doubt here (C3, 2026-08-05).
                                BasisKind = "FixedThreshold",
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

        /// <summary>
        /// How alert-thresholds.json came off disk.
        ///
        /// <para><b>Warned about, NOT refused, and the asymmetry is the point.</b> This store holds
        /// threshold rules — a metric, a comparison, a number, a severity. Every one of them was
        /// typed by an operator from their own knowledge of the estate, so an operator can type them
        /// again; nothing here is a secret, nothing here is a security control, and the built-in
        /// defaults this file was seeded from are still in <c>GetDefaultThresholds</c>. The worst
        /// case is an afternoon of re-entry, and the <c>.rejected-</c> copy usually spares even
        /// that.</para>
        ///
        /// <para>The credential and control stores in this codebase refuse the write. Applying the
        /// same ceremony to a file whose loss is recoverable is how a guard becomes the thing
        /// everyone works around — and the guard they rip out is the same code protecting the
        /// intake SAS and the channel credentials. So this one says loudly what happened and what
        /// it cost, and lets the operator get on with it.</para>
        /// </summary>
        private ConfigLoadOutcome _load = ConfigLoadOutcome.Missing;

        private string? _quarantine;

        /// <summary>True when alert-thresholds.json exists and did not load, so no threshold is being evaluated.</summary>
        public bool IsStoreDamaged { get { lock (_lock) return _load is ConfigLoadOutcome.Unreadable or ConfigLoadOutcome.Empty; } }

        /// <summary>What to do about it. The ONE register — never a locally written sentence.</summary>
        public string DescribeStoreRecovery()
        {
            lock (_lock) return ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine);
        }

        private void LoadThresholds()
        {
            try
            {
                if (File.Exists(_alertsFilePath))
                {
                    _thresholds = ConfigFileHelper.Load<List<AlertThreshold>>(_alertsFilePath, _jsonOptions, out _load, out _quarantine);
                    _logger.LogInformation("Loaded {Count} alert thresholds (store: {Outcome})", _thresholds.Count, _load);

                    if (IsStoreDamaged)
                        _logger.LogError(
                            "[Alerting] {Path} exists and did not load ({Outcome}). NO threshold is being evaluated "
                            + "by this process — nothing will alert, and the defaults are NOT being substituted. The "
                            + "next threshold you add or change here will replace the file with just that one, so "
                            + "repair it first if you want the rest back. {Recovery}",
                            _alertsFilePath, _load, ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine));
                }
                else
                {
                    _load = ConfigLoadOutcome.Missing;

                    // One-time move off the collided path. Only adopts a legacy file that parses as
                    // THIS service's array schema, so a stock install's shipped object catalogue is
                    // read as "not mine", left untouched, and this install starts on defaults —
                    // which is what it should always have had.
                    if (!AdoptLegacyThresholdStore())
                        _thresholds = GetDefaultThresholds();

                    SaveThresholds();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading alert thresholds");
                _thresholds = new List<AlertThreshold>();
                _load = ConfigLoadOutcome.Unreadable;
            }
        }

        /// <summary>
        /// Migrates thresholds off <c>alert-definitions.json</c> when — and only when — that file is
        /// really this service's old array store. Returns true if thresholds were adopted.
        ///
        /// <para><b>Probed, not loaded.</b> <see cref="ConfigFileHelper.InspectStore{T}"/> takes no
        /// <c>.rejected-</c> copy and writes no log line, so on the overwhelmingly common case — a
        /// stock install whose alert-definitions.json is the shipped 80-alert OBJECT — this method
        /// asks a question, gets "not an array", and leaves no trace. Using <c>Load</c> here would
        /// quarantine 96 KB of somebody else's healthy catalogue on every first start and announce
        /// damage that does not exist.</para>
        ///
        /// <para>The legacy file is never deleted or rewritten. If it held thresholds it also holds
        /// nothing else worth keeping — but it is not this service's file any more, and the service
        /// that does own it gets to decide, having announced what it found.</para>
        /// </summary>
        private bool AdoptLegacyThresholdStore()
        {
            try
            {
                if (!File.Exists(_legacyThresholdsFilePath)) return false;

                if (ConfigFileHelper.InspectStore<List<AlertThreshold>>(_legacyThresholdsFilePath, _jsonOptions)
                    != ConfigLoadOutcome.Loaded)
                    return false;

                var legacy = ConfigFileHelper.Load<List<AlertThreshold>>(
                    _legacyThresholdsFilePath, _jsonOptions, out var legacyOutcome, out _);

                // An empty array is not a migration — it is a file with nothing in it, and adopting
                // it would give this install zero thresholds where defaults are the honest answer.
                if (legacyOutcome != ConfigLoadOutcome.Loaded || legacy.Count == 0) return false;

                _thresholds = legacy;
                _logger.LogWarning(
                    "[Alerting] Adopted {Count} threshold(s) from the legacy store {Legacy} into {Path}. That path "
                    + "is shared with the shipped alert catalogue, which is why this service no longer writes it; "
                    + "the legacy file has been left exactly as it is.",
                    legacy.Count, _legacyThresholdsFilePath, _alertsFilePath);
                return true;
            }
            catch (Exception ex)
            {
                // A failed migration must never stop the service starting on defaults.
                _logger.LogWarning(ex, "[Alerting] Could not read the legacy threshold store {Legacy}",
                    _legacyThresholdsFilePath);
                return false;
            }
        }

        private void SaveThresholds()
        {
            // Announced, not refused — see _load. This fires exactly once per damaged load, because
            // a successful write makes the store Loaded, and it is the only record of the moment the
            // damaged file stopped existing.
            var replacingDamage = _load is ConfigLoadOutcome.Unreadable or ConfigLoadOutcome.Empty;

            try
            {
                ConfigFileHelper.Save(_alertsFilePath, _thresholds, _jsonOptions);
                if (replacingDamage)
                    _logger.LogWarning(
                        "[Alerting] {Path} did not load ({Outcome}) and has now been REPLACED by the {Count} "
                        + "threshold(s) held in memory. Any rule the damaged file contained is gone from the live "
                        + "file. {Recovery}",
                        _alertsFilePath, _load, _thresholds.Count,
                        ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine));

                _load = ConfigLoadOutcome.Loaded;
                _quarantine = null;
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
