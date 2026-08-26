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
        /// The pre-change value captured at snapshot (Configuration ops). Surfaced so the UI
        /// can offer a session-scoped "undo" (re-apply this value) after a verified apply.
        /// </summary>
        public int? PreChangeValue { get; init; }
    }
}
