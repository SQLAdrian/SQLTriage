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
        /// What is known about the rollback, carried through from the executor so the page can
        /// report the ledger truthfully rather than inferring a rollback from the outcome enum.
        /// A rollback that ran but could not be confirmed reads as Unconfirmed here, and the page
        /// must not describe it as done.
        /// </summary>
        public RemediationRollbackState RollbackState { get; private init; } = RemediationRollbackState.NotAttempted;

        /// <summary>
        /// True when the executor RAN an inverse action, of any outcome. Same single definition the
        /// execution uses, so the page and the ledger cannot disagree about whether a rollback
        /// happened.
        /// </summary>
        public bool RolledBack => RollbackState.InverseWasAttempted();

        /// <summary>Set when the rollback failed or could not be confirmed. Shown to the operator.</summary>
        public string? RollbackError { get; private init; }

        /// <summary>Pre-change value (Configuration ops) — drives the session-scoped "undo".</summary>
        public int? PreChangeValue { get; private init; }

        /// <summary>
        /// The change-credit count the runner reserved for this apply, carried through so the page's
        /// "charged / refunded" line states the REAL number rather than a hard-coded singular. It is
        /// the amount RemediationCreditCost priced, which is the amount the ledger commits or refunds,
        /// so the sentence can never disagree with the balance. Zero for a refusal (never executed).
        /// </summary>
        public int CreditsCharged { get; private init; }

        public static RemediationResult Refused(RemediationRefusal refusal, string message) =>
            new() { IsRefused = true, Refusal = refusal, Message = message };

        public static RemediationResult Applied(
            RemediationOutcome outcome, string? message = null,
            RemediationRollbackState rollbackState = RemediationRollbackState.NotAttempted,
            int? preChangeValue = null, string? rollbackError = null, int creditsCharged = 0) =>
            new()
            {
                IsRefused = false,
                Outcome = outcome,
                Message = message ?? string.Empty,
                RollbackState = rollbackState,
                RollbackError = rollbackError,
                PreChangeValue = preChangeValue,
                CreditsCharged = creditsCharged
            };
    }
}
