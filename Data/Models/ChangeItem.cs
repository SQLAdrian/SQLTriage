/* In the name of God, the Merciful, the Compassionate */

using System;

namespace SQLTriage.Data.Models
{
    /// <summary>
    /// The status lifecycle of a change item (#88 — the LOG branch of the accept-or-log
    /// decision). Flow: <see cref="Logged"/> -> <see cref="HandedToVendor"/> ->
    /// (<see cref="ConfirmedFixed"/> | <see cref="StillFailing"/>). Confirmed is a human
    /// attestation; StillFailing can be set manually OR raised automatically when a fresh
    /// scan lands and the handed-off check still fails past its due date (see
    /// <see cref="SQLTriage.Data.Services.ChangeItemService.EvaluateFollowUpsAsync"/>).
    /// </summary>
    public enum ChangeItemStatus
    {
        /// <summary>Logged as a change to make; not yet handed to anyone.</summary>
        Logged = 0,
        /// <summary>Handed to the vendor/DBA team for remediation (with a handed date + optional due date).</summary>
        HandedToVendor = 1,
        /// <summary>A human confirmed the change was made and the check now passes.</summary>
        ConfirmedFixed = 2,
        /// <summary>
        /// The handed-off change was NOT confirmed delivered: the check still fails (manually
        /// recorded, or auto-flagged past its due date), OR — 2026-07-20 — the due date passed and
        /// the latest run could not fully assess the check (WARN/SKIP/INFO), so the remediation is
        /// unconfirmed. Both need a human; only the first is a confirmed failure, and
        /// <see cref="ChangeItem.ResolutionNote"/> says which one this is. Read the note before
        /// reporting "still failing" to anyone.
        /// </summary>
        StillFailing = 3,
    }

    /// <summary>
    /// #88 — a tracked change-control item: the operator decided NOT to accept a failed
    /// finding but to CHANGE it, logging that decision with a required rationale and
    /// (optionally) handing the remediation to a vendor with a due date. This is the LOG
    /// branch that sits beside <see cref="AcceptedFinding"/> (the ACCEPT branch): together
    /// they make "change-control-with-auditing" true rather than aspirational.
    ///
    /// Like <see cref="AcceptedFinding"/>, the item keys on the stable v2 <see cref="CheckId"/>
    /// so it survives corpus rebuilds/bundle upgrades, and every state transition is written
    /// to the HMAC-chained audit log — never a silent status change.
    /// </summary>
    public sealed class ChangeItem
    {
        /// <summary>Store primary key (assigned by the SQLite store; 0 for an unsaved item).</summary>
        public long Id { get; set; }

        /// <summary>The SQL Server instance this change item applies to.</summary>
        public string ServerName { get; set; } = string.Empty;

        /// <summary>The stable v2 check id this change targets, e.g. "SQLT-VA-XP-CMDSHELL".</summary>
        public string CheckId { get; set; } = string.Empty;

        /// <summary>Human-readable check name snapshotted at log time (survives corpus renames).</summary>
        public string CheckName { get; set; } = string.Empty;

        /// <summary>Required audit narrative — WHY this finding is being changed rather than accepted.</summary>
        public string Rationale { get; set; } = string.Empty;

        /// <summary>
        /// The remediation script / guidance text captured at log time, so the handoff to the
        /// vendor is reproducible even if the underlying corpus later changes. NULL when the
        /// finding carried no remediation text.
        /// </summary>
        public string? RemediationScript { get; set; }

        /// <summary>Current lifecycle status.</summary>
        public ChangeItemStatus Status { get; set; } = ChangeItemStatus.Logged;

        /// <summary>When the change was decided/logged (UTC).</summary>
        public DateTime DecidedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Who logged the change (OS user + role if available).</summary>
        public string? DecidedBy { get; set; }

        /// <summary>When the item was handed to the vendor (UTC); null until handed.</summary>
        public DateTime? HandedAt { get; set; }

        /// <summary>Who/which vendor the change was handed to; null until handed.</summary>
        public string? HandedTo { get; set; }

        /// <summary>Optional due date the vendor was asked to complete by (UTC); null = no deadline.</summary>
        public DateTime? DueAt { get; set; }

        /// <summary>When the item reached a terminal state (ConfirmedFixed/StillFailing), UTC; null while open.</summary>
        public DateTime? ResolvedAt { get; set; }

        /// <summary>Who recorded the terminal state (or "system" for an auto-raised StillFailing).</summary>
        public string? ResolvedBy { get; set; }

        /// <summary>Optional note recorded with the terminal state.</summary>
        public string? ResolutionNote { get; set; }

        /// <summary>Last time any field on this item changed (UTC).</summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>True while the item is not in a terminal state.</summary>
        public bool IsOpen => Status is ChangeItemStatus.Logged or ChangeItemStatus.HandedToVendor;

        /// <summary>
        /// True when the item is handed to a vendor, has a due date, and that date has passed
        /// while the item is still open. Used to tint the ledger row — the follow-up sweep is
        /// what actually flips such an item to <see cref="ChangeItemStatus.StillFailing"/>.
        /// </summary>
        public bool IsOverdue =>
            Status == ChangeItemStatus.HandedToVendor
            && DueAt.HasValue
            && DueAt.Value <= DateTime.UtcNow;
    }
}
