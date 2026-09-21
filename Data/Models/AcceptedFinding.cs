/* In the name of God, the Merciful, the Compassionate */

using System;

namespace SQLTriage.Data.Models
{
    /// <summary>
    /// A client-configured, per-(instance, check) acceptance of a FAIL/WARN finding.
    /// The check itself still reports truth universally; this is the consumer-side
    /// "yes, we know — that's acceptable here" layer (F6). An accepted finding is
    /// annotated + downgraded (rides the Passed tier for score math) and badged in
    /// the UI, NEVER silently hidden — an auditor can always see what was accepted,
    /// by whom, when, and why.
    ///
    /// Keyed on the stable v2 <see cref="CheckId"/> (corpus-v2 SCHEMA: ids are never
    /// reused), so an acceptance survives corpus rebuilds / bundle upgrades.
    /// <see cref="DatabaseName"/>/<see cref="ObjectName"/> are NULL for instance-wide
    /// acceptance (the MVP); they exist for the Phase-2 per-object contract but are
    /// only populated once the corpus emits offending-item detail rows.
    /// </summary>
    public sealed class AcceptedFinding
    {
        /// <summary>The SQL Server instance this acceptance applies to.</summary>
        public string ServerName { get; set; } = string.Empty;

        /// <summary>The stable v2 check id, e.g. "SQLT-VA-XP-CMDSHELL".</summary>
        public string CheckId { get; set; } = string.Empty;

        /// <summary>NULL = instance-wide (MVP). Phase 2: a specific database.</summary>
        public string? DatabaseName { get; set; }

        /// <summary>NULL = instance-wide (MVP). Phase 2: a specific object.</summary>
        public string? ObjectName { get; set; }

        /// <summary>Required audit narrative — WHY this finding is accepted here.</summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>User identity if available (OS user or null otherwise).</summary>
        public string? AcceptedBy { get; set; }

        /// <summary>When the acceptance was recorded (UTC).</summary>
        public DateTime AcceptedAt { get; set; } = DateTime.UtcNow;

        /// <summary>NULL = never expires; else the finding re-surfaces as live after this (UTC).</summary>
        public DateTime? ExpiresAt { get; set; }

        /// <summary>True when <see cref="ExpiresAt"/> is in the past (finding should re-surface).</summary>
        public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value <= DateTime.UtcNow;
    }
}
