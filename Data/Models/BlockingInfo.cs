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
    }
}
