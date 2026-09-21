/* In the name of God, the Merciful, the Compassionate */
/*
 * BatchApplyContracts — THE PHASE-3 CONTRACT. The types the UI builder codes against while the
 * engine builder implements them, committed FIRST and deliberately ahead of the implementation
 * (WORKLIST-2026-09-01-remconc-p3.md, "THE CONTRACT").
 *
 * ⚠ ONE DELIBERATE DEPARTURE FROM THE WORKLIST'S PROSE, stated here because this file is what the
 * UI builder compiles against. The worklist sketches the apply as
 * `ApplyBatchAsync(items, serverName, approved, approvedBy, runId?, ct)`. The SHIPPED method — P1,
 * unchanged through P2, with call sites in three existing test files — is serverName-FIRST, and so
 * are both shipped preview methods, which the same contract pins as "UNCHANGED signature". Flipping
 * the order to match a prose sketch would have silently re-bound three existing positional call
 * sites (`ApplyBatchAsync(Server, items, ...)`) to the wrong parameters. The order is therefore
 * serverName-first, consistent with every other method on the driver. The NAMES are the contract's.
 *
 * WHY THE RESULT TYPES ARE FLAT. The UI consumes only the three driver methods plus
 * RemediationValueBounds and RemediationRollbackProse (contract, file-ownership section). It must be
 * able to render a per-item row without reaching through RemediationResult into executor-shaped
 * fields, and without re-deriving a credit or rollback predicate of its own — every such re-derivation
 * in this lane's history became a number that disagreed with the ledger.
 *
 * WHY THE FLAT FIELDS ARE COMPUTED, NOT COPIED. BatchApplyItemResult stores the underlying
 * RemediationResult ONCE and projects Outcome / RollbackState / CreditsCharged / CreditsCommitted /
 * PreChangeValue / Message off it. A copy would be a second storage of the same fact, free to drift
 * from the runner's answer between the apply and the render; a projection cannot. This is the same
 * structural rule the P1 gate imposed on DetectNoChange and the P2 fix round imposed on the input
 * bounds: one producer, everyone else reads it.
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace SQLTriage.Data.Services.Remediation
{
    /// <summary>
    /// One item's outcome inside a batch apply, flattened for a surface to render and carrying
    /// everything <see cref="BatchRemediationDriver.RollBackBatchAsync"/> needs to invert it later.
    /// </summary>
    public sealed class BatchApplyItemResult
    {
        public string TemplateKey { get; init; } = string.Empty;

        /// <summary>
        /// The parameters this item was applied with. Carried so a later rollback re-renders the
        /// SAME operation rather than a default one — a template whose target is operator-supplied
        /// (MAXDOP, the memory cap) renders differently without them.
        /// </summary>
        public IReadOnlyDictionary<string, string>? Parameters { get; init; }

        /// <summary>False when the stop policy prevented this item from being attempted at all.</summary>
        public bool Attempted { get; init; }

        /// <summary>The runner's own answer. The single storage every flat field below projects from.</summary>
        public RemediationResult Result { get; init; } = default!;

        /// <summary>Terminal state of the attempt. Null when the item was refused at a gate or never attempted.</summary>
        public RemediationOutcome? Outcome => Result?.Outcome;

        /// <summary>Which gate refused it, when one did.</summary>
        public RemediationRefusal? Refusal => Result?.Refusal;

        /// <summary>What is known about any rollback the APPLY itself performed (verify-fail path).</summary>
        public RemediationRollbackState RollbackState =>
            Result?.RollbackState ?? RemediationRollbackState.NotAttempted;

        /// <summary>
        /// The rollback reason in the operator's words, produced by
        /// <see cref="RemediationRollbackProse.DescribeState"/> at the moment the item completed.
        /// Empty when no rollback was involved. Never a bare state name: a state word on its own
        /// tells a person nothing they can act on (P2 fix-round blocker 3).
        /// </summary>
        public string RollbackReason { get; init; } = string.Empty;

        /// <summary>What the runner RESERVED for this item. Exposure, not the bill — see <see cref="CreditsCommitted"/>.</summary>
        public int CreditsCharged => Result?.CreditsCharged ?? 0;

        /// <summary>This item's real bill. The field an aggregate must sum.</summary>
        public int CreditsCommitted => Result?.CreditsCommitted ?? 0;

        /// <summary>The pre-change CONFIGURED value, and the value a batch rollback puts back.</summary>
        public int? PreChangeValue => Result?.PreChangeValue;

        /// <summary>The pre-change EFFECTIVE value, carried beside it for honesty.</summary>
        public int? PreChangeValueInUse => Result?.PreChangeValueInUse;

        public string Message => Result?.Message ?? string.Empty;

        /// <summary>
        /// True when this item is believed to have LEFT A CHANGE on the server — the only items a
        /// batch rollback has anything to undo. It is deliberately conservative: an apply that
        /// verified, and an apply that ran but could not verify AND was not confirmed back, both
        /// count. A NoOp, a refusal, and a confirmed self-rollback do not.
        /// </summary>
        public bool Mutated =>
            Attempted && Result is not null && !Result.IsRefused
            && (Result.Outcome == RemediationOutcome.AppliedVerified
                || (Result.Outcome == RemediationOutcome.AppliedVerifyFailed
                    && Result.RollbackState != RemediationRollbackState.Confirmed));
    }

    /// <summary>
    /// The batch apply's aggregate outcome. One approval, N gated applies, one run-id.
    /// <para>Named per the Phase-3 contract. The P1/P2 member names (<c>Items</c>,
    /// <c>CreditsReserved</c>, <c>CreditsCommitted</c>) are kept as PROJECTIONS of the contract
    /// members rather than as separate storage, so the shipped tests that assert on them and the UI
    /// that renders the contract names can never read two different numbers.</para>
    /// </summary>
    public sealed class BatchApplyResult
    {
        public bool IsRefused { get; init; }
        public BatchRefusal Refusal { get; init; }
        public string Message { get; init; } = string.Empty;

        /// <summary>
        /// The correlation-id every audit entry this batch wrote carries in <c>Details["RunId"]</c> —
        /// the join an auditor uses to ask "show me everything that one approval authorised".
        /// A batch rollback reuses this SAME id, so the undo joins the do.
        /// </summary>
        public string RunId { get; init; } = string.Empty;

        /// <summary>The server every item was applied to. Carried so a rollback needs only this result.</summary>
        public string ServerName { get; init; } = string.Empty;

        /// <summary>Who approved the batch. Carried for the same reason, and re-stated at rollback time.</summary>
        public string ApprovedBy { get; init; } = string.Empty;

        public IReadOnlyList<BatchApplyItemResult> PerItem { get; init; }
            = Array.Empty<BatchApplyItemResult>();

        /// <summary>P1/P2 name for <see cref="PerItem"/>. A projection, never a second list.</summary>
        public IReadOnlyList<BatchApplyItemResult> Items => PerItem;

        /// <summary>Aggregate price computed by gate C, before anything ran.</summary>
        public int PricedTotal { get; init; }

        /// <summary>Credit available on the server at gate-C time.</summary>
        public int AvailableAtPreCheck { get; init; }

        /// <summary>
        /// Sum of the per-item RESERVATIONS over attempted items — the batch's EXPOSURE, not its
        /// bill. It over-reports by every no-op in the batch, deliberately and by the same rule
        /// <see cref="RemediationResult.CreditsCharged"/> states. An operator-facing "you were
        /// charged N" must use <see cref="TotalCommitted"/>.
        /// </summary>
        public int TotalReserved { get; init; }

        /// <summary>
        /// The batch's real bill: the sum of the per-item <see cref="RemediationResult.CreditsCommitted"/>,
        /// each decided by the runner from the same predicate it charged the ledger from.
        /// </summary>
        public int TotalCommitted { get; init; }

        /// <summary>P1/P2 names. Projections of the two above.</summary>
        public int CreditsReserved => TotalReserved;
        public int CreditsCommitted => TotalCommitted;
        public int CreditsRefunded => TotalReserved - TotalCommitted;

        /// <summary>True when the stop policy ended the batch before every item was attempted.</summary>
        public bool Stopped { get; init; }

        /// <summary>
        /// WHY it stopped, in plain words naming the item that stopped it. Empty when it did not
        /// stop. A boolean with no reason is the shape this lane keeps having to fix.
        /// </summary>
        public string StopReason { get; init; } = string.Empty;

        public static BatchApplyResult Refused(BatchRefusal refusal, string message,
            int pricedTotal = 0, int availableAtPreCheck = 0, string serverName = "") =>
            new()
            {
                IsRefused = true,
                Refusal = refusal,
                Message = message,
                PricedTotal = pricedTotal,
                AvailableAtPreCheck = availableAtPreCheck,
                ServerName = serverName,
            };
    }

    /// <summary>One item's outcome inside a batch ROLLBACK.</summary>
    public sealed class BatchRollbackItemResult
    {
        public string TemplateKey { get; init; } = string.Empty;

        /// <summary>The honest 5-state answer. <see cref="RemediationRollbackState.Confirmed"/> is the only success.</summary>
        public RemediationRollbackState RollbackState { get; init; } = RemediationRollbackState.NotAttempted;

        /// <summary>
        /// What happened, in the operator's words. Produced by <see cref="RemediationRollbackProse"/>,
        /// so an item this batch could not undo carries the
        /// <see cref="RemediationRollbackProse.NoRollbackMarker"/> sentence and a REASON — it is
        /// reported, never silently skipped.
        /// </summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>The configured value the inverse targeted, when there was one.</summary>
        public int? RestoredToValue { get; init; }

        /// <summary>
        /// Credits given back for this item. Non-zero only on a CONFIRMED rollback, per the shipped
        /// rule: a refund asserts the change is gone, and only a confirming read can say that.
        /// </summary>
        public int CreditsRefunded { get; init; }

        /// <summary>True only when an inverse statement actually ran (of any outcome).</summary>
        public bool InverseRan => RollbackState.InverseWasAttempted();
    }

    /// <summary>
    /// The batch rollback's aggregate outcome. Items are undone in REVERSE apply order and every
    /// item that was applied appears here — including the ones nothing could be done about.
    /// </summary>
    public sealed class BatchRollbackResult
    {
        public bool IsRefused { get; init; }
        public string Message { get; init; } = string.Empty;

        /// <summary>The SAME run-id as the apply it undoes, so the ledger joins them.</summary>
        public string RunId { get; init; } = string.Empty;

        public string ServerName { get; init; } = string.Empty;

        /// <summary>In the order the rollbacks were ATTEMPTED — reverse of the apply order.</summary>
        public IReadOnlyList<BatchRollbackItemResult> PerItem { get; init; }
            = Array.Empty<BatchRollbackItemResult>();

        /// <summary>
        /// True only when every reported item came back CONFIRMED. An item that could not be rolled
        /// back at all makes this false, which is the point: a batch containing one irreversible fix
        /// was not fully undone, and the operator must not read that as "everything is back".
        /// </summary>
        public bool AllConfirmed =>
            PerItem.Count > 0 && PerItem.All(i => i.RollbackState == RemediationRollbackState.Confirmed);

        /// <summary>How many items had no rollback available at all, each with its reason in <see cref="BatchRollbackItemResult.Message"/>.</summary>
        public int NoRollbackCount =>
            PerItem.Count(i => i.RollbackState == RemediationRollbackState.NotAvailable);

        public int TotalCreditsRefunded => PerItem.Sum(i => i.CreditsRefunded);

        public static BatchRollbackResult Refused(string message, string runId = "", string serverName = "") =>
            new() { IsRefused = true, Message = message, RunId = runId, ServerName = serverName };
    }
}
