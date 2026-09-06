/* In the name of God, the Merciful, the Compassionate */
/*
 * Remediation lane contracts — build step 4.
 *
 * The RemediationRunner depends only on these narrow seams, never on concrete
 * licensing / credit / execution machinery. This keeps the runner unit-testable
 * with no live SQL Server or dbatools, and keeps the privileged executor
 * reachable ONLY through the runner (Erik Darling's "statically checkable"
 * rule). Step 5 supplies the real IRemediationExecutor (dbatools -WhatIf +
 * transacted execute); the real bundle-claim capability and persisted credit
 * ledger are later steps.
 */

using System.Threading;
using System.Threading.Tasks;

namespace SQLTriage.Data.Services.Remediation
{
    // ── Gate 2: capability ──────────────────────────────────────────────────

    /// <summary>
    /// Gate 2. Whether the running build/licence is permitted to apply
    /// remediations at all (managed-tier only). Backed later by IFeatureGate +
    /// the bundle remediation claim; an adapter satisfies it for now.
    /// </summary>
    public interface IRemediationCapability
    {
        bool IsGranted { get; }
    }

    // ── Gate 3: credits ─────────────────────────────────────────────────────

    /// <summary>
    /// Gate 3. The "Change Credits" mechanic that bounds how many fixes may run, scoped
    /// PER SERVER (the MSP per-server allocation): each server has its own balance, seeded
    /// from the signed bundle allocation. Reserve before apply; commit on success; refund
    /// if the apply never ran, or was rolled back AND the rollback was CONFIRMED.
    ///
    /// <para>Ruling 2 (2026-08-25) narrowed the last clause. A rollback that ran and could not be
    /// confirmed leaves the server in an unknown state, so it keeps the charge: a refund is an
    /// assertion that the change is gone, and nobody read the server back.</para>
    /// </summary>
    public interface IRemediationCreditLedger
    {
        /// <summary>Credits currently available to reserve for <paramref name="serverName"/>.</summary>
        int AvailableFor(string serverName);

        /// <summary>
        /// Reserve <paramref name="cost"/> credits against <paramref name="serverName"/>'s
        /// balance. Returns a reservation handle, or null if insufficient credits. A
        /// reservation must be committed or refunded — never left dangling.
        /// </summary>
        CreditReservation? Reserve(string serverName, int cost);

        /// <summary>Consume a reservation (the change ran). Idempotent.</summary>
        void Commit(CreditReservation reservation);

        /// <summary>
        /// Return a reservation's credits (the change did not run, or a CONFIRMED rollback put the
        /// server back). Idempotent.
        /// </summary>
        void Refund(CreditReservation reservation);

        /// <summary>
        /// Read-only breakdown of a server's credit position for display: signed allocation,
        /// committed spend, outstanding (in-flight) reservations, and available.
        /// </summary>
        CreditBreakdown GetBreakdown(string serverName);

        /// <summary>
        /// Give back credits that have already COMMITTED, after a rollback whose confirming read
        /// proved the server is back where it was (Phase 3, batch rollback).
        ///
        /// <para>WHY IT IS NOT <see cref="Refund"/>. Refund cancels an OPEN reservation and is the
        /// right operation for the apply-time rollback: the reservation is still in flight, so
        /// nothing was ever spent. A batch rollback happens AFTER the applies committed — the
        /// reservation handles are gone and the spend is persisted — so there is nothing to cancel
        /// and a second operation is needed to state what happened. Reusing Refund here would have
        /// meant keeping reservations open across an operator's think-time, which is worse.</para>
        ///
        /// <para>The rule is unchanged and this method does not decide it: only a CONFIRMED rollback
        /// credits anything back, because a credit-back asserts the change is gone and only a
        /// confirming read can say that (Ruling 2, 2026-08-25). The caller owns that decision; this
        /// seam only performs it.</para>
        ///
        /// <para>DEFAULT IMPLEMENTATION REFUSES, and returns false rather than throwing or silently
        /// succeeding. A ledger that cannot express a post-hoc credit must say so, so the operator is
        /// told the rollback worked AND that the charge stands, instead of reading a refund that
        /// never happened.</para>
        /// </summary>
        /// <returns>True only if the credit was actually given back.</returns>
        bool TryCreditBack(string serverName, int credits) => false;

        /// <summary>
        /// True when the ledger exists and did not load, so recorded spend is UNKNOWN and every
        /// number this interface returns is withheld rather than measured.
        ///
        /// <para>On the seam because the UI had no way to ask. <see cref="AvailableFor"/> and
        /// <see cref="GetBreakdown"/> fail closed to zero on a damaged store, which is right, and
        /// zero is also what a spent-out server returns — so /remediation printed "Out of change
        /// credits" and told the operator to redeem a grant. Redemption then succeeded, burnt the
        /// one-time nonce, and left Available at 0, because the damage short-circuit fires before
        /// allocation is ever consulted. Every sibling store (Alerting, Settings, Onboarding)
        /// already surfaces this; the credit ledger was the exception.</para>
        /// </summary>
        bool IsStoreDamaged { get; }

        /// <summary>
        /// What the operator must do about a damaged ledger. The ONE register, never a sentence
        /// written at the call site. Empty when the store is healthy.
        /// </summary>
        string DescribeStoreRecovery();
    }

    /// <summary>A read-only snapshot of a server's change-credit position (for the consumption panel).</summary>
    public readonly record struct CreditBreakdown(int Allocation, int Committed, int Outstanding, int Available);

    /// <summary>An outstanding credit reservation. Opaque handle, scoped to a server.</summary>
    public sealed class CreditReservation
    {
        public string Id { get; }
        public string ServerName { get; }
        public int Cost { get; }
        public CreditReservation(string id, string serverName, int cost)
        {
            Id = id; ServerName = serverName; Cost = cost;
        }
    }

    // ── Gates 4 & 5: execution boundary (step 5 implements) ─────────────────

    /// <summary>
    /// The privileged execution machinery. Step 5 implements this over
    /// PowerShellService (dbatools -WhatIf) and a transacted SQL connection. The
    /// runner is the ONLY caller — UI binds to the runner facade, never here.
    /// </summary>
    public interface IRemediationExecutor
    {
        /// <summary>
        /// Gate 4 preview: render what the change WOULD do via dbatools -WhatIf.
        /// Pure display; its safety verdict is NOT authoritative (gate 5 re-checks).
        /// </summary>
        Task<RemediationPreview> PreviewAsync(RemediationRequest request, CancellationToken ct = default);

        /// <summary>
        /// Gate 5 execution: capture snapshot, run inside a transaction
        /// (SET XACT_ABORT ON), verify post-state. Returns a distinct terminal state.
        /// </summary>
        Task<RemediationExecution> ExecuteAsync(RemediationRequest request, CancellationToken ct = default);

        /// <summary>
        /// Confirms the audit ledger append will succeed (audit-writability probe).
        /// For a compliance product a silently-not-logging apply is the nightmare,
        /// so the runner refuses to execute when this is false.
        /// </summary>
        bool CanWriteAudit();
    }

    /// <summary>
    /// A DELIBERATE, OPERATOR-REQUESTED undo of a configuration change that has already been applied
    /// and committed — the primitive Phase 3's batch rollback is built from.
    ///
    /// <para>⚠ WHY THIS IS NOT <see cref="IRemediationExecutor.ExecuteAsync"/> WITH THE OLD VALUE.
    /// That was the obvious route and it is wrong in two ways that matter, both proved by reading the
    /// apply path rather than guessed. (1) The apply VERIFIES against <c>value_in_use</c>, which is
    /// correct for "did my change take effect" and wrong for "is the old number back": on an option
    /// SQL Server coerces, re-applying the operator's original value would read a coerced
    /// <c>value_in_use</c>, call the undo a verify FAILURE, and then — (2) — run its own
    /// snapshot rollback, putting the value the operator just asked to remove straight back on the
    /// server, and reporting that as a Confirmed rollback. An undo that re-applies the thing being
    /// undone is not a subtle bug. This path verifies against the CONFIGURED value, the column
    /// <c>sp_configure</c> writes and the P2 leveling made the capture, the inverse target and the
    /// confirming read (spike S2 §3.1).</para>
    ///
    /// <para>⚠ WHY IT IS A SEPARATE, OPTIONAL INTERFACE. Every test fake in the suite implements
    /// <see cref="IRemediationExecutor"/>; widening that interface would have broken all of them and
    /// forced a wave of stub methods whose behaviour nobody chose. An executor that does not
    /// implement this one is reported HONESTLY — "this executor cannot undo changes" — rather than
    /// silently reporting a rollback nobody ran.</para>
    /// </summary>
    public interface IRemediationRollbackExecutor
    {
        /// <summary>
        /// Put one <c>sp_configure</c> option back to <paramref name="toConfiguredValue"/> and READ
        /// THE SERVER BACK to say whether it went. Never throws for a SQL failure: the failure is a
        /// state on the return.
        /// </summary>
        Task<RemediationRollbackExecution> RollBackConfigurationAsync(
            RemediationRequest request, int toConfiguredValue, CancellationToken ct = default);
    }

    /// <summary>
    /// What a deliberate undo did. Same 5-state honesty as the apply-time rollback, and the same
    /// rule: <see cref="RemediationRollbackState.Confirmed"/> is the ONLY state that means the
    /// server was read back and found where it should be.
    /// </summary>
    public sealed class RemediationRollbackExecution
    {
        public RemediationRollbackState State { get; init; } = RemediationRollbackState.NotAvailable;

        /// <summary>
        /// The reason, in the operator's words, always populated. A state name on its own tells a
        /// person nothing they can act on (P2 fix-round blocker 3, and the whole point of
        /// <see cref="RemediationRollbackProse"/>).
        /// </summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>The configured value the confirming read actually observed, when it could read one.</summary>
        public int? ObservedConfiguredValue { get; init; }

        /// <summary>The effective value observed alongside it — differs when the engine is coercing.</summary>
        public int? ObservedValueInUse { get; init; }

        /// <summary>True when NOTHING was sent to the server on this path.</summary>
        public bool NothingRan => !State.InverseWasAttempted();

        public bool IsPermissionDenied { get; init; }
    }

    /// <summary>A request to apply one template against one server.</summary>
    public sealed class RemediationRequest
    {
        public RemediationTemplate Template { get; }
        public string ServerName { get; }
        /// <summary>Template parameters (e.g. the target MAXDOP value), supplied by the caller.</summary>
        public System.Collections.Generic.IReadOnlyDictionary<string, string> Parameters { get; }

        public RemediationRequest(
            RemediationTemplate template,
            string serverName,
            System.Collections.Generic.IReadOnlyDictionary<string, string>? parameters = null)
        {
            Template = template;
            ServerName = serverName;
            Parameters = parameters ?? new System.Collections.Generic.Dictionary<string, string>();
        }
    }

    /// <summary>Result of a gate-4 -WhatIf preview.</summary>
    public sealed class RemediationPreview
    {
        public bool Succeeded { get; init; }
        /// <summary>Human-readable description of the proposed change for the approval UI.</summary>
        public string WhatIfText { get; init; } = string.Empty;
        public string? Error { get; init; }

        /// <summary>
        /// Whether this fix, on THIS server, can be put back if it does not verify — known BEFORE
        /// approval (Phase-2 item 6b). Null is "unknown", which is neither a promise nor a refusal.
        ///
        /// <para>It is also carried in <see cref="WhatIfText"/> as a sentence, because that is what
        /// both the single-fix and the batch surface already render. The structured field exists so a
        /// surface can style or sort on it without parsing prose, and so a test can assert the fact
        /// rather than the wording.</para>
        /// </summary>
        public bool? CanRollBack { get; init; }

        /// <summary>
        /// The reversibility sentence the operator reads, produced by
        /// <see cref="RemediationRollbackProse"/>. Empty when the preview never got far enough to
        /// know.
        /// </summary>
        public string ReversibilityNote { get; init; } = string.Empty;
    }

    /// <summary>
    /// What we actually know about a rollback. The boolean pair this replaces could not express
    /// the third state, so every site that ran the inverse action and then failed to CONFIRM it
    /// reported a plain success: the ledger printed "rolled back", Success=True, Error="" for a
    /// post-state nobody had read. Four states now, and neither of the two that ran nothing is
    /// spelled "Failed".
    /// </summary>
    public enum RemediationRollbackState
    {
        /// <summary>Nothing needed rolling back (the change verified, or no statement ran).</summary>
        NotAttempted = 0,

        /// <summary>The inverse action ran AND a confirming read observed the pre-change state. The only success.</summary>
        Confirmed = 1,

        /// <summary>
        /// The inverse action ran without throwing, but the confirming read threw or was not
        /// attempted. The server's real state is UNKNOWN. It may or may not be reverted. Never
        /// report this as a successful rollback.
        /// </summary>
        Unconfirmed = 2,

        /// <summary>The inverse action threw, or the confirming read observed the wrong state. A real failure.</summary>
        Failed = 3,

        /// <summary>
        /// A rollback was warranted and could not be attempted: the template is not reversible, or
        /// the inverse statement could not be rendered. NOTHING RAN. This is its own fact, not a
        /// failed attempt. Reporting it as <see cref="Failed"/> put a "the rollback FAILED" line in
        /// the HMAC audit ledger for an inverse action that was never executed, which is the
        /// fabrication this enum exists to stop, pointing the other way.
        /// </summary>
        NotAvailable = 4,
    }

    /// <summary>
    /// The ONE definition of "a rollback happened", shared by the execution and the result so the
    /// two cannot drift. True only when the inverse action actually ran, or threw while running.
    /// <see cref="RemediationRollbackState.NotAttempted"/> and
    /// <see cref="RemediationRollbackState.NotAvailable"/> both return false: no statement was
    /// executed, so there is no rollback to attest to and no ledger entry to write.
    /// </summary>
    public static class RemediationRollbackStates
    {
        public static bool InverseWasAttempted(this RemediationRollbackState state) =>
            state is RemediationRollbackState.Confirmed
                  or RemediationRollbackState.Unconfirmed
                  or RemediationRollbackState.Failed;
    }

    /// <summary>Result of a gate-5 execution. Carries a DISTINCT TERMINAL STATE.</summary>
    public sealed class RemediationExecution
    {
        public RemediationOutcome Outcome { get; init; }
        public string? Error { get; init; }
        /// <summary>True only when the perms error is non-transient (drives back-off).</summary>
        public bool IsPermissionDenied { get; init; }

        /// <summary>
        /// What is known about the rollback. This is the ONE field a call site sets; the two
        /// booleans below are derived from it, so "confirmed" cannot be asserted by writing a
        /// literal true next to a confirming read that never ran.
        /// </summary>
        public RemediationRollbackState RollbackState { get; init; } = RemediationRollbackState.NotAttempted;

        /// <summary>
        /// True when the executor RAN an inverse action (the change ran but post-verify failed, so
        /// the captured pre-change value was re-applied). The runner ledgers this via
        /// LogRemediationRolledBack, so it must never be true for a path that executed nothing.
        /// </summary>
        public bool RolledBack => RollbackState.InverseWasAttempted();

        /// <summary>
        /// Whether the rollback is CONFIRMED to have restored the pre-change state. False for both
        /// a failed rollback and an unconfirmed one — an unread post-state is not a success.
        /// </summary>
        public bool RollbackSucceeded => RollbackState == RemediationRollbackState.Confirmed;

        /// <summary>When the rollback failed or could not be confirmed, the reason.</summary>
        public string? RollbackError { get; init; }

        /// <summary>
        /// The pre-change CONFIGURED value captured at snapshot (Configuration ops) — read from
        /// <c>sys.configurations.value</c>, the column <c>sp_configure</c> writes. Surfaced so the UI
        /// can offer a session-scoped "undo" (re-apply this value) after a verified apply.
        ///
        /// <para>Phase-2 item 2.1 changed WHICH column this is. It used to be <c>value_in_use</c>,
        /// which on a coercing option is not the value the operator had: spike S2 section 3.1 proved
        /// a "Confirmed" rollback that re-applied 16 where the configured value was 0.</para>
        ///
        /// <para>Phase-2 item 2.2: it is now set on the verify-fail-and-rollback return as well as
        /// the verified one. It used to be dropped on exactly the path where a per-item before-state
        /// ledger needs it most.</para>
        /// </summary>
        public int? PreChangeValue { get; init; }

        /// <summary>
        /// The pre-change EFFECTIVE value (<c>sys.configurations.value_in_use</c>) captured beside
        /// <see cref="PreChangeValue"/>. Recorded for honesty rather than for control flow: when the
        /// two differ, the engine is coercing the setting or is waiting for a restart, and an
        /// operator reading the record afterwards can see that rather than inferring it.
        /// </summary>
        public int? PreChangeValueInUse { get; init; }
    }
}
