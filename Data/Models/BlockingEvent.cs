/* In the name of God, the Merciful, the Compassionate */

namespace SQLTriage.Data.Models
{
    /// <summary>
    /// A single persisted blocking pair: one blocker SPID holding another SPID waiting.
    /// Stored in <c>blocking-history.db</c> via <see cref="SQLTriage.Data.Services.BlockingHistoryService"/>.
    /// </summary>
    public class BlockingEvent
    {
        public long Id { get; set; }
        public string ServerName { get; set; } = "";
        public DateTime CapturedUtc { get; set; }
        public int BlockerSpid { get; set; }
        public int BlockedSpid { get; set; }
        public string? BlockerLogin { get; set; }
        public string? BlockedLogin { get; set; }
        public string? BlockerDatabase { get; set; }
        public string? BlockedDatabase { get; set; }
        public string? WaitType { get; set; }
        public string? WaitResource { get; set; }
        /// <summary>Blocker SQL text, truncated to 2 000 chars.</summary>
        public string? BlockerSqlText { get; set; }
        /// <summary>Blocked SQL text, truncated to 2 000 chars.</summary>
        public string? BlockedSqlText { get; set; }
        public int DurationSeconds { get; set; }
    }

    /// <summary>
    /// Aggregated row for the "Top Blocking Offenders" panel.
    /// </summary>
    public class BlockingOffender
    {
        public string? BlockerLogin { get; set; }
        public string? SqlExcerpt { get; set; }
        public int EventCount { get; set; }
        public int TotalDurationSeconds { get; set; }
    }
}
