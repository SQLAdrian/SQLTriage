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
