/* In the name of God, the Merciful, the Compassionate */

using System;

namespace SQLTriage.Data.Models
{
    /// <summary>
    /// Result of executing a SQL check against a specific instance.
    /// </summary>
    public class CheckResult
    {
        public string CheckId { get; set; } = string.Empty;
        public string CheckName { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public bool Passed { get; set; }
        public int ActualValue { get; set; }
        public int ExpectedValue { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// The SQL Server instance this check was executed against.
        /// </summary>
        public string InstanceName { get; set; } = string.Empty;

        /// <summary>
        /// Duration of the check execution in milliseconds.
        /// </summary>
        public long DurationMs { get; set; }

        /// <summary>
        /// Recommended action from the check definition, shown when a check fails.
        /// </summary>
        public string? RecommendedAction { get; set; }

        /// <summary>
        /// Description from the check definition.
        /// </summary>
        public string? Description { get; set; }
    }

    /// <summary>
    /// Summary of a check execution run across one or more instances.
    /// </summary>
    public class CheckExecutionSummary
    {
        public DateTime StartedAt { get; set; }
        public DateTime CompletedAt { get; set; }
        public string InstanceName { get; set; } = string.Empty;
        public int TotalChecks { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }
        public int Errors { get; set; }
        public TimeSpan Duration => CompletedAt - StartedAt;
    }
}
