/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;

namespace SQLTriage.Data.Models
{
    /// <summary>
    /// Represents a SQLWATCH check configuration that can be edited in bulk.
    /// </summary>
    public class CheckConfiguration
    {
        public string CheckId { get; set; } = string.Empty;
        public string CheckName { get; set; } = string.Empty;
        public string? CheckDescription { get; set; }
        public bool IsEnabled { get; set; }
        public int AlertLevel { get; set; } = 1;
        public string AlertType { get; set; } = "Warning";
        public int ThresholdWarning { get; set; }
        public int ThresholdCritical { get; set; }
        public int FrequencySeconds { get; set; } = 60;
        public string? Tags { get; set; }
        public string CheckCategory { get; set; } = "Custom";
        public string? SqlQuery { get; set; }

        // For UI selection
        public bool IsSelected { get; set; }
    }

    /// <summary>
    /// Bulk edit operation types.
    /// </summary>
    public enum BulkEditOperation
    {
        Enable,
        Disable,
        SetAlertLevel,
        SetFrequency,
        SetThresholdWarning,
        SetThresholdCritical,
        Delete
    }

    /// <summary>
    /// Represents a bulk edit action to apply to selected checks.
    /// </summary>
    public class BulkEditAction
    {
        public BulkEditOperation Operation { get; set; }
        public int? NewValue { get; set; }
    }
}
