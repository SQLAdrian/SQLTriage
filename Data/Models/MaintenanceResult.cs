/* In the name of God, the Merciful, the Compassionate */

using System;

namespace SQLTriage.Data.Models
{
    public class MaintenanceResult
    {
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public bool Success { get; set; }
        public bool OptimizeCompleted { get; set; }
        public bool VacuumCompleted { get; set; }
        public string? IntegrityCheckResult { get; set; }
        public bool? IntegrityOk { get; set; }
        public long RowsPurged { get; set; }

        public TimeSpan Duration => (CompletedAt ?? DateTime.UtcNow) - StartedAt;
    }
}
