/* In the name of God, the Merciful, the Compassionate */

namespace SQLTriage.Data.Models
{
    /// <summary>
    /// Represents a live SQL Server session from sys.dm_exec_sessions / sys.dm_exec_requests.
    /// Used by the real-time Sessions bubble view.
    /// </summary>
    public class SessionInfo
    {
        public int SPID { get; set; }
        public string LoginName { get; set; } = "";
        public string HostName { get; set; } = "";
        public string DatabaseName { get; set; } = "";
        public string SessionStatus { get; set; } = "";
        public long CpuTime { get; set; }
        public long LogicalReads { get; set; }
        public long Writes { get; set; }
        public int OpenTransactionCount { get; set; }
        public string? RequestStatus { get; set; }
        public string? Command { get; set; }
        public string? WaitType { get; set; }
        public long WaitTime { get; set; }
        public int BlockingSessionId { get; set; }
        public long TotalElapsedTime { get; set; }
        public string? QueryText { get; set; }
        public string? ProgramName { get; set; }

        /// <summary>True if this session is being blocked by another session.</summary>
        public bool IsBlocked => BlockingSessionId > 0;

        /// <summary>True if session is sleeping/idle with an open transaction (dangerous).</summary>
        public bool IsIdleInTransaction =>
            OpenTransactionCount > 0 &&
            (SessionStatus?.Equals("sleeping", System.StringComparison.OrdinalIgnoreCase) == true);

        /// <summary>True if session has an active request running.</summary>
        public bool IsActive =>
            RequestStatus != null &&
            !RequestStatus.Equals("background", System.StringComparison.OrdinalIgnoreCase);

        /// <summary>True if session status is sleeping.</summary>
        public bool IsSleeping =>
            SessionStatus?.Equals("sleeping", System.StringComparison.OrdinalIgnoreCase) == true;

        /// <summary>Activity score used for bubble sizing (higher = bigger).</summary>
        public double ActivityScore => CpuTime + LogicalReads + (Writes * 2);

        /// <summary>Memory usage in KB from sys.dm_exec_sessions.memory_usage.</summary>
        public long MemoryUsageKB { get; set; }

        /// <summary>Row count from sys.dm_exec_requests when active.</summary>
        public long? RowCount { get; set; }
    }
}
