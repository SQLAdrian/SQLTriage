/* In the name of God, the Merciful, the Compassionate */

using System.Text.Json.Serialization;

namespace SqlHealthAssessment.Data.Models
{
    public class AlertThreshold
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("metric")]
        public string Metric { get; set; } = string.Empty; // e.g., "cpu", "memory", "connections", "deadlocks"

        [JsonPropertyName("condition")]
        public string Condition { get; set; } = "greater_than"; // greater_than, less_than, equals

        [JsonPropertyName("thresholdValue")]
        public double ThresholdValue { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("severity")]
        public string Severity { get; set; } = "warning"; // info, warning, critical

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;
    }

    public class AlertNotification
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string AlertName { get; set; } = string.Empty;
        public string Metric { get; set; } = string.Empty;
        public double CurrentValue { get; set; }
        public double ThresholdValue { get; set; }
        public string Severity { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public DateTime TriggeredAt { get; set; } = DateTime.Now;
        public bool IsAcknowledged { get; set; }

        /// <summary>
        /// The instance this notification was triggered for (empty = global).
        /// </summary>
        public string InstanceName { get; set; } = string.Empty;
    }

    public class AlertEvaluationResult
    {
        public bool IsTriggered { get; set; }
        public string AlertId { get; set; } = string.Empty;
        public string AlertName { get; set; } = string.Empty;
        public double CurrentValue { get; set; }
        public double ThresholdValue { get; set; }
        public string Severity { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// The instance this result relates to (empty = global).
        /// </summary>
        public string InstanceName { get; set; } = string.Empty;
    }

    // ── Alert Definitions File (JSON root) ──────────────────────────────

    public class AlertDefinitionsFile
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0";

        [JsonPropertyName("globalDefaults")]
        public AlertGlobalDefaults GlobalDefaults { get; set; } = new();

        [JsonPropertyName("categories")]
        public List<AlertCategory> Categories { get; set; } = new();

        [JsonPropertyName("alerts")]
        public List<AlertDefinition> Alerts { get; set; } = new();
    }

    public class AlertGlobalDefaults
    {
        [JsonPropertyName("cooldownMinutes")]
        public int CooldownMinutes { get; set; } = 5;

        [JsonPropertyName("autoAcknowledgeHours")]
        public int AutoAcknowledgeHours { get; set; } = 24;

        [JsonPropertyName("retentionDays")]
        public int RetentionDays { get; set; } = 365;

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;
    }

    public class AlertCategory
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("icon")]
        public string Icon { get; set; } = string.Empty;
    }

    public class AlertDefinition
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("category")]
        public string Category { get; set; } = string.Empty;

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("severity")]
        public string Severity { get; set; } = "Medium";

        [JsonPropertyName("thresholds")]
        public AlertThresholds Thresholds { get; set; } = new();

        [JsonPropertyName("unit")]
        public string Unit { get; set; } = string.Empty;

        [JsonPropertyName("operator")]
        public string Operator { get; set; } = "greater_than";

        [JsonPropertyName("durationSeconds")]
        public int DurationSeconds { get; set; } = 300;

        [JsonPropertyName("frequencySeconds")]
        public int FrequencySeconds { get; set; } = 60;

        [JsonPropertyName("cooldownMinutes")]
        public int? CooldownMinutes { get; set; }

        [JsonPropertyName("query")]
        public string Query { get; set; } = string.Empty;

        [JsonPropertyName("queryMode")]
        public string? QueryMode { get; set; }

        [JsonPropertyName("remediation")]
        public string? Remediation { get; set; }

        /// <summary>
        /// When true, this alert fires even outside the operational window (e.g. connectivity checks, disk full).
        /// Still suppressed during an active maintenance window.
        /// </summary>
        [JsonPropertyName("alwaysAlert")]
        public bool AlwaysAlert { get; set; }

        // ── Routing ────────────────────────────────────────────────────

        /// <summary>Channel ID to use for this alert (overrides global default). Empty = use global.</summary>
        [JsonPropertyName("primaryChannel")]
        public string? PrimaryChannel { get; set; }

        /// <summary>When true, send an email notification via the configured SMTP channel.</summary>
        [JsonPropertyName("sendEmail")]
        public bool SendEmail { get; set; }

        /// <summary>
        /// Override the global cooldown for repeat notifications on this alert.
        /// How many minutes to wait before sending another notification for the same active alert.
        /// </summary>
        [JsonPropertyName("nextAlertDelayMinutes")]
        public int? NextAlertDelayMinutes { get; set; }

        // ── Escalation ─────────────────────────────────────────────────

        /// <summary>When true, escalate to critical severity + escalation channel if unacknowledged.</summary>
        [JsonPropertyName("escalate")]
        public bool Escalate { get; set; }

        /// <summary>
        /// Trigger escalation after this many events within <see cref="EscalationWindowMinutes"/>.
        /// 0 = escalate on any single event.
        /// </summary>
        [JsonPropertyName("escalationThresholdEvents")]
        public int EscalationThresholdEvents { get; set; } = 0;

        /// <summary>Rolling window (minutes) for counting escalation events. 0 = any single event.</summary>
        [JsonPropertyName("escalationWindowMinutes")]
        public int EscalationWindowMinutes { get; set; } = 0;

        /// <summary>Channel ID to notify on escalation. Empty = same as primary.</summary>
        [JsonPropertyName("escalationChannel")]
        public string? EscalationChannel { get; set; }

        /// <summary>
        /// Minutes an alert must be unacknowledged before escalating.
        /// Only used when EscalationThresholdEvents == 0 (time-based escalation).
        /// </summary>
        [JsonPropertyName("escalationAfterMinutes")]
        public int EscalationAfterMinutes { get; set; } = 30;

        // ── Baseline deviation ─────────────────────────────────────────

        /// <summary>
        /// When > 0, also fire this alert when the current value is this many percent above the 7-day average
        /// from the cache. Requires a matching queryId in the time-series cache. 0 = disabled.
        /// </summary>
        [JsonPropertyName("baselineDeviationPercent")]
        public double BaselineDeviationPercent { get; set; } = 0;

        /// <summary>
        /// The dashboard queryId to pull 7-day baseline data from (e.g. "live.cpu").
        /// Required when BaselineDeviationPercent > 0.
        /// </summary>
        [JsonPropertyName("baselineQueryId")]
        public string? BaselineQueryId { get; set; }

        /// <summary>The series name within that query to average (e.g. "CPU %"). Empty = average all series.</summary>
        [JsonPropertyName("baselineSeries")]
        public string? BaselineSeries { get; set; }

        // ── Reporting ─────────────────────────────────────────────────

        /// <summary>
        /// When true, events for this alert are included in the scheduled daily summary email.
        /// The summary is sent once per day to the configured SMTP recipients.
        /// </summary>
        [JsonPropertyName("includeInDailySummary")]
        public bool IncludeInDailySummary { get; set; }
    }

    // ── Operational / Maintenance Windows ──────────────────────────────

    /// <summary>Days of the week on which a window is active (flags enum for multi-select).</summary>
    [Flags]
    public enum WindowDays
    {
        None      = 0,
        Monday    = 1,
        Tuesday   = 2,
        Wednesday = 4,
        Thursday  = 8,
        Friday    = 16,
        Saturday  = 32,
        Sunday    = 64,
        Weekdays  = Monday | Tuesday | Wednesday | Thursday | Friday,
        Weekend   = Saturday | Sunday,
        All       = Weekdays | Weekend
    }

    /// <summary>
    /// A time-range window with a day-of-week mask.
    /// Times are stored as HH:mm (local server time).
    /// </summary>
    public class AlertTimeWindow
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = false;

        /// <summary>HH:mm — start of the window (inclusive).</summary>
        [JsonPropertyName("startTime")]
        public string StartTime { get; set; } = "08:00";

        /// <summary>HH:mm — end of the window (inclusive). If less than StartTime, wraps midnight.</summary>
        [JsonPropertyName("endTime")]
        public string EndTime { get; set; } = "18:00";

        /// <summary>Bitmask of active days.</summary>
        [JsonPropertyName("days")]
        public WindowDays Days { get; set; } = WindowDays.Weekdays;

        /// <summary>Returns true if right now falls within this window.</summary>
        public bool IsActive()
        {
            if (!Enabled) return false;
            var now = DateTime.Now;
            var dayFlag = now.DayOfWeek switch
            {
                DayOfWeek.Monday    => WindowDays.Monday,
                DayOfWeek.Tuesday   => WindowDays.Tuesday,
                DayOfWeek.Wednesday => WindowDays.Wednesday,
                DayOfWeek.Thursday  => WindowDays.Thursday,
                DayOfWeek.Friday    => WindowDays.Friday,
                DayOfWeek.Saturday  => WindowDays.Saturday,
                DayOfWeek.Sunday    => WindowDays.Sunday,
                _                   => WindowDays.None
            };
            if ((Days & dayFlag) == WindowDays.None) return false;

            if (!TimeSpan.TryParse(StartTime, out var start)) return false;
            if (!TimeSpan.TryParse(EndTime, out var end)) return false;
            var nowTs = now.TimeOfDay;
            return start <= end
                ? nowTs >= start && nowTs <= end
                : nowTs >= start || nowTs <= end; // overnight wrap
        }
    }

    /// <summary>
    /// Root config for operational and maintenance windows.
    /// Persisted inside notification-channels.json under "alertWindows".
    /// </summary>
    public class AlertWindowConfig
    {
        /// <summary>
        /// Operational window — all alerts fire during this window.
        /// Outside this window only AlwaysAlert alerts fire.
        /// Disabled = no restriction (all alerts fire at all times).
        /// </summary>
        [JsonPropertyName("operationalWindow")]
        public AlertTimeWindow OperationalWindow { get; set; } = new();

        /// <summary>
        /// Maintenance window — ALL alerts are suppressed, including AlwaysAlert ones.
        /// Can also be activated on demand via <see cref="MaintenanceActiveUntil"/>.
        /// </summary>
        [JsonPropertyName("maintenanceWindow")]
        public AlertTimeWindow MaintenanceWindow { get; set; } = new();

        /// <summary>
        /// If set and in the future, maintenance mode is active regardless of the scheduled window.
        /// Set by "Start maintenance now for X minutes".
        /// </summary>
        [JsonPropertyName("maintenanceActiveUntil")]
        public DateTime? MaintenanceActiveUntil { get; set; }

        /// <summary>True if a manual maintenance period is currently running.</summary>
        [JsonIgnore]
        public bool IsManualMaintenanceActive =>
            MaintenanceActiveUntil.HasValue && MaintenanceActiveUntil.Value > DateTime.Now;

        /// <summary>True if any form of maintenance is currently active.</summary>
        [JsonIgnore]
        public bool IsMaintenanceActive => IsManualMaintenanceActive || MaintenanceWindow.IsActive();

        /// <summary>True if an alert should fire, given window config and the alert's AlwaysAlert flag.</summary>
        public bool ShouldFire(bool alwaysAlert)
        {
            if (IsMaintenanceActive) return false;
            if (!OperationalWindow.Enabled) return true;   // no restriction configured
            if (OperationalWindow.IsActive()) return true; // inside operational hours
            return alwaysAlert;                            // outside hours — only if alwaysAlert
        }
    }

    public class AlertThresholds
    {
        [JsonPropertyName("warning")]
        public double? Warning { get; set; }

        [JsonPropertyName("critical")]
        public double? Critical { get; set; }
    }

    // ── Runtime Alert State ─────────────────────────────────────────────

    public enum AlertStatus
    {
        Active,
        Acknowledged,
        Resolved
    }

    public class AlertState
    {
        public string AlertId { get; set; } = string.Empty;
        public string AlertName { get; set; } = string.Empty;
        public string ServerName { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public AlertStatus Status { get; set; } = AlertStatus.Active;
        public double LastValue { get; set; }
        public double ThresholdValue { get; set; }
        public int HitCount { get; set; } = 1;
        public DateTime FirstTriggered { get; set; } = DateTime.UtcNow;
        public DateTime LastTriggered { get; set; } = DateTime.UtcNow;
        public string Message { get; set; } = string.Empty;
        public DateTime? AcknowledgedAt { get; set; }
        public DateTime? ResolvedAt { get; set; }

        /// <summary>Set when this alert has been escalated to the escalation channel.</summary>
        public DateTime? EscalatedAt { get; set; }
        public bool IsEscalated => EscalatedAt.HasValue;
    }

    public class AlertHistoryRecord
    {
        public long Id { get; set; }
        public string AlertId { get; set; } = string.Empty;
        public string AlertName { get; set; } = string.Empty;
        public string ServerName { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public double Value { get; set; }
        public double ThresholdValue { get; set; }
        public string Status { get; set; } = "Active";
        public int HitCount { get; set; } = 1;
        public DateTime FirstTriggered { get; set; }
        public DateTime LastTriggered { get; set; }
        public DateTime? AcknowledgedAt { get; set; }
        public DateTime? ResolvedAt { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}
