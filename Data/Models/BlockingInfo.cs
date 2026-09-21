/* In the name of God, the Merciful, the Compassionate */

namespace SQLTriage.Data.Models
{
    /// <summary>
    /// Represents a blocking relationship from sys.dm_os_waiting_tasks.
    /// Used for accurate blocking chain visualization in Live Sessions.
    /// </summary>
    public class BlockingInfo
    {
        public int BlockingSPID { get; set; }
        public int BlockedSPID { get; set; }
        public long WaitDurationMs { get; set; }
        public string? WaitType { get; set; }

        // Enriched from sys.dm_exec_sessions / sys.dm_exec_sql_text
        public string? BlockerLogin { get; set; }
        public string? BlockedLogin { get; set; }
        public string? BlockerDatabase { get; set; }
        public string? BlockedDatabase { get; set; }
        /// <summary>Blocker SQL text, truncated to 2 000 chars.</summary>
        public string? BlockerSqlText { get; set; }
        /// <summary>Blocked SQL text, truncated to 2 000 chars.</summary>
        public string? BlockedSqlText { get; set; }
    }
}
