/* In the name of God, the Merciful, the Compassionate */

namespace SQLTriage.Data.Services.Remediation
{
    /// <summary>
    /// Outcome of <see cref="RemediationRunner.ProposeAsync"/>: either a refusal at
    /// a gate, or a rendered preview awaiting human approval.
    /// </summary>
    public sealed class RemediationProposal
    {
        public bool IsRefused { get; private init; }
        public RemediationRefusal? Refusal { get; private init; }
        public string Message { get; private init; } = string.Empty;
        public RemediationPreview? Preview { get; private init; }

        public static RemediationProposal Refused(RemediationRefusal refusal, string message) =>
            new() { IsRefused = true, Refusal = refusal, Message = message };

        public static RemediationProposal Previewed(RemediationPreview preview) =>
            new() { IsRefused = false, Preview = preview };
    }

    /// <summary>
    /// Outcome of <see cref="RemediationRunner.ApplyAsync"/>. Either refused at a
    /// gate (never executed) or applied (reached the executor — carries a distinct
    /// terminal state). The two are not collapsed into a boolean.
    /// </summary>
    public sealed class RemediationResult
    {
        public bool IsRefused { get; private init; }
        public RemediationRefusal? Refusal { get; private init; }

        /// <summary>Set only when the attempt reached the executor.</summary>
        public RemediationOutcome? Outcome { get; private init; }
        public string Message { get; private init; } = string.Empty;

        /// <summary>
        /// True when the executor performed a snapshot-based rollback (verify failed and the
        /// pre-change value was re-applied). Lets the UI report the ledger truthfully rather
        /// than inferring a rollback from the outcome enum.
        /// </summary>
        public bool RolledBack { get; private init; }

        /// <summary>Pre-change value (Configuration ops) — drives the session-scoped "undo".</summary>
        public int? PreChangeValue { get; private init; }

        public static RemediationResult Refused(RemediationRefusal refusal, string message) =>
            new() { IsRefused = true, Refusal = refusal, Message = message };

        public static RemediationResult Applied(RemediationOutcome outcome, string? message = null, bool rolledBack = false, int? preChangeValue = null) =>
            new() { IsRefused = false, Outcome = outcome, Message = message ?? string.Empty, RolledBack = rolledBack, PreChangeValue = preChangeValue };
    }
}
