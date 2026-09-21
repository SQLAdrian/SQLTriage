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
    /// Outcome of <see cref="RemediationRunner.RollBackAsync"/> — a DELIBERATE undo of a change that
    /// already applied and committed (Phase 3).
    ///
    /// <para>A GATE REFUSAL LANDS HERE AS <see cref="RemediationRollbackState.NotAvailable"/>, not as
    /// a separate refusal axis. That is deliberate: from the operator's side "the capability gate
    /// says no" and "this template has no inverse" are the same fact — this change cannot be put back
    /// from here — and both must read as NOTHING RAN. Collapsing them onto the honest 5-state enum
    /// keeps one vocabulary for undo across the whole surface, and <see cref="Refusal"/> still
    /// carries which gate said so for anyone auditing it.</para>
    /// </summary>
    public sealed class RemediationUndoResult
    {
        public RemediationRollbackState State { get; init; } = RemediationRollbackState.NotAvailable;

        /// <summary>Always populated, always in plain words, never a bare state name.</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>The configured value the inverse targeted.</summary>
        public int? RestoredToValue { get; init; }

        /// <summary>What the confirming read actually observed, when it could read one.</summary>
        public int? ObservedConfiguredValue { get; init; }

        /// <summary>
        /// Credits given back. Non-zero only on <see cref="RemediationRollbackState.Confirmed"/> AND
        /// only when the ledger actually performed it — a ledger that cannot credit back reports
        /// zero here and says so in <see cref="Message"/>, rather than showing a refund nobody made.
        /// </summary>
        public int CreditsRefunded { get; init; }

        /// <summary>Which gate refused, when a gate is why nothing ran. Null otherwise.</summary>
        public RemediationRefusal? Refusal { get; init; }

        /// <summary>True only when an inverse statement actually ran.</summary>
        public bool InverseRan => State.InverseWasAttempted();
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

        /// <summary>
        /// Pre-change CONFIGURED value (Configuration ops) — drives the session-scoped "undo".
        /// Read from <c>sys.configurations.value</c> since Phase-2 item 2.1; it used to be
        /// <c>value_in_use</c>, which on a coercing option is not the value the operator had.
        /// </summary>
        public int? PreChangeValue { get; private init; }

        /// <summary>
        /// The pre-change EFFECTIVE value (<c>value_in_use</c>), carried beside
        /// <see cref="PreChangeValue"/> for honesty. When the two differ, SQL Server is coercing the
        /// setting or waiting for a restart.
        /// </summary>
        public int? PreChangeValueInUse { get; private init; }

        /// <summary>
        /// The change-credit count the runner RESERVED for this apply, carried through so the page's
        /// "charged / refunded" line states the REAL number rather than a hard-coded singular. It is
        /// the amount RemediationCreditCost priced, which is the amount the ledger reserves and then
        /// either commits or refunds. Zero for a refusal (never executed).
        ///
        /// <para>⚠ THIS IS THE RESERVATION, NOT THE BILL. It is populated identically whether the
        /// reservation committed or was refunded — a NoOp apply still reports the reserved N here,
        /// deliberately, so "N change credits were refunded" names the real N
        /// (<see cref="RemediationCreditOutcome.DescribeCharge"/> reads the outcome alongside it and
        /// says which happened). For a SINGLE apply that pairing is honest. SUMMING this field across
        /// several applies is not: the total over-reports by every no-op in the set. Proved live
        /// 2026-09-01 (spike S1 §3.4) — a three-item batch reported 3 while the ledger took 2. Any
        /// aggregate must sum <see cref="CreditsCommitted"/>.</para>
        /// </summary>
        public int CreditsCharged { get; private init; }

        /// <summary>
        /// The part of <see cref="CreditsCharged"/> that actually COMMITTED — this apply's real bill,
        /// and the field an aggregate over several applies must sum.
        ///
        /// <para>It exists so the over-report above is closed BY STRUCTURE rather than by a comment
        /// telling a future caller to be careful: a caller that wants "what was charged" now has a
        /// field that means exactly that, and cannot reach the wrong number by picking the
        /// obvious-looking one. The runner sets it from the SAME
        /// <see cref="RemediationCreditOutcome.ChangeStuck"/> decision it charges the ledger from, at
        /// the same moment, so this number can never disagree with the balance. Zero whenever the
        /// reservation was refunded (NoOp, CouldNotRun, a confirmed rollback, the throw path), and
        /// zero on a refusal and on the parked path, which never reserve anything at all.</para>
        /// </summary>
        public int CreditsCommitted { get; private init; }

        public static RemediationResult Refused(RemediationRefusal refusal, string message) =>
            new() { IsRefused = true, Refusal = refusal, Message = message };

        /// <summary>
        /// NOTHING HAPPENED AND NO GATE WAS CONSULTED. The third shape, added in fix round 1 (gate
        /// blocker 3) for the item a batch's stop policy never reached.
        ///
        /// <para>⚠ WHY IT IS NOT A REFUSAL. That item used to be carried as
        /// <c>Refused(RemediationRefusal.NotApproved, ...)</c>, which puts a gate-4 verdict on a
        /// change nobody ever put to gate 4 — and gate 4 is APPROVAL, so the record accused the
        /// operator of not approving the very batch they had just approved. A refusal enum is a
        /// statement that a named check said no. When no check ran, the honest answer carries no
        /// refusal at all: <see cref="IsRefused"/> false, <see cref="Refusal"/> null,
        /// <see cref="Outcome"/> null, and a sentence saying what stopped short of it.</para>
        /// </summary>
        public static RemediationResult NotAttempted(string message) =>
            new() { IsRefused = false, Outcome = null, Message = message };

        public static RemediationResult Applied(
            RemediationOutcome outcome, string? message = null,
            RemediationRollbackState rollbackState = RemediationRollbackState.NotAttempted,
            int? preChangeValue = null, string? rollbackError = null, int creditsCharged = 0,
            int creditsCommitted = 0, int? preChangeValueInUse = null) =>
            new()
            {
                IsRefused = false,
                Outcome = outcome,
                Message = message ?? string.Empty,
                RollbackState = rollbackState,
                RollbackError = rollbackError,
                PreChangeValue = preChangeValue,
                PreChangeValueInUse = preChangeValueInUse,
                CreditsCharged = creditsCharged,
                CreditsCommitted = creditsCommitted
            };
    }
}
