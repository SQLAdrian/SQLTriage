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
    /// if the apply never ran (or rolled back).
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

        /// <summary>Return a reservation's credits (the change did not run). Idempotent.</summary>
        void Refund(CreditReservation reservation);

        /// <summary>
        /// Read-only breakdown of a server's credit position for display: signed allocation,
        /// committed spend, outstanding (in-flight) reservations, and available.
        /// </summary>
        CreditBreakdown GetBreakdown(string serverName);
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

    /// <summary>Result of a gate-5 execution. Carries a DISTINCT TERMINAL STATE.</summary>
    public sealed class RemediationExecution
    {
        public RemediationOutcome Outcome { get; init; }
        public string? Error { get; init; }
        /// <summary>True only when the perms error is non-transient (drives back-off).</summary>
        public bool IsPermissionDenied { get; init; }

        /// <summary>
        /// True when the executor attempted a snapshot-based rollback (the change ran but
        /// post-verify failed, so the captured pre-change value was re-applied). The
        /// runner ledgers this via LogRemediationRolledBack.
        /// </summary>
        public bool RolledBack { get; init; }

        /// <summary>When <see cref="RolledBack"/>, whether the rollback restored the pre-change value.</summary>
        public bool RollbackSucceeded { get; init; }

        /// <summary>When <see cref="RolledBack"/> and it failed, the reason.</summary>
        public string? RollbackError { get; init; }

        /// <summary>
        /// The pre-change value captured at snapshot (Configuration ops). Surfaced so the UI
        /// can offer a session-scoped "undo" (re-apply this value) after a verified apply.
        /// </summary>
        public int? PreChangeValue { get; init; }
    }
}
