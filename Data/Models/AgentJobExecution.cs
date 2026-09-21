/* In the name of God, the Merciful, the Compassionate */

using System;

namespace SQLTriage.Data.Models
{
    public class AgentJobExecution
    {
        public string ServerName { get; set; } = string.Empty;
        public string JobName { get; set; } = string.Empty;
        
        /// <summary>
        /// 0 = Failed, 1 = Succeeded, 2 = Retry, 3 = Canceled, 4 = In progress
        /// </summary>
        public int Status { get; set; }
        
        public DateTime StartTime { get; set; }
        
        /// <summary>
        /// Duration in seconds.
        /// </summary>
        public int DurationSeconds { get; set; }
        
        public DateTime EndTime => StartTime.AddSeconds(DurationSeconds);
        
        public bool IsEnabled { get; set; }
        
        public string ErrorMessage { get; set; } = string.Empty;

        // Visual properties for Gantt chart
        public double OffsetPercent { get; set; }
        public double WidthPercent { get; set; }
        
        public string StatusColor => Status switch
        {
            1 => "var(--success, #4caf50)", // Success
            0 => "var(--danger, #f44336)",  // Failed
            2 => "var(--warning, #ff9800)", // Retry
            3 => "var(--text-muted, #757575)", // Canceled
            _ => "var(--text-muted, #757575)"
        };

        public string StatusText => Status switch
        {
            1 => "Succeeded",
            0 => "Failed",
            2 => "Retry",
            3 => "Canceled",
            _ => "Unknown"
        };
    }
}
