/* In the name of God, the Merciful, the Compassionate */
/*
 * BatchRemediationDriver — the "concentrate" primitive: apply N registered remediation templates
 * to ONE server under ONE operator approval, with a PER-ITEM credit reservation (Adrian's ruling
 * 2026-09-01). Prototyped and proved live on SQL 2022 by spike S1 (2026-09-01); productionised in
 * the Phase-1 build lane.
 *
 * DESIGN CONSTRAINT, deliberate: this class contains NO capability or security gate logic. Every
 * item still goes through RemediationRunner.ApplyAsync, which enforces the same 5 gates in the same
 * order it always did — THE GATE DECISIONS ARE UNTOUCHED: no gate was added, removed, reordered or
 * had its condition changed. The offline arm proves the driver adds no gate of its own by
 * withholding the remediation claim and watching the RUNNER refuse every item.
 *
 * ⚠ THAT IS A CLAIM ABOUT DECISIONS, NOT A LINE COUNT, and this header used to make it as one
 * ("the diff to RemediationRunner's gate core is zero lines"). False as a number since P3's own
 * contract commit 3474f1d, which added a GatePassesTemplate overload emitting the authorised
 * rendering, and false again after fix round 1, which changed what the unpreviewed-apply proposal
 * record attests. Neither touched a gate's verdict. A number nobody re-counts is a claim that goes
 * stale silently, so the honest form is the one above (fix round 1, gate blocker 2).
 *
 * What the driver DOES add is three BATCH-level gates the per-item path cannot do for itself, plus
 * a stop policy (which is behaviour across the loop, not a gate):
 *
 *   GATE A — ONE approval carried to N applies. The operator approves the batch; the driver passes
 *      the same `approved` flag and `approvedBy` string to every ApplyAsync, and refuses an
 *      unapproved batch before the runner is ever reached (the runner would refuse each item
 *      anyway; this keeps N refusals for one decision out of the ledger).
 *   GATE B — every template key must be REGISTERED before anything runs. A batch carrying one bad
 *      key fails whole rather than applying the good ones and then refusing.
 *   GATE C — an AGGREGATE CREDIT PRE-CHECK. Price every item, compare the total against
 *      IRemediationCreditLedger.AvailableFor, refuse the whole batch if it cannot be paid for in
 *      full. This is a READ; it reserves nothing. Measured, not asserted: the contrast arm of spike
 *      S1 §3.6 drove the same three items with no pre-check against a 2-credit allocation on a live
 *      server, and two fixes landed before the third was refused for money — exactly the
 *      half-mutated state a concentrate must not produce.
 *   STOP POLICY — sp_configure/RECONFIGURE cannot ride one transaction, so there is no atomic
 *      all-or-nothing available at any price. Stop-on-first-failure narrows the half-mutated window
 *      instead. A NoOp is NOT a failure: the server was already compliant, the credit refunds, and
 *      the rest of the batch is still worth running.
 *
 * SCOPE — WHAT IS WIRED (rewritten in fix round 1, gate blocker 2; the Phase-1 text below it was
 * false on this tip and the falsehood was load-bearing — it told a reader the mutating path had no
 * caller):
 *   PreviewBatchAsync / PreviewBatchDetailedAsync are the reading surface.
 *   ApplyBatchAsync IS reachable from the UI as of Phase 3: Pages/Remediation.razor calls it from
 *   ApplyTheBatchAsync, confined to the armed approval dialog (.rbatch-armed) and pinned there by
 *   RemediationBatchPreviewUiTests. RollBackBatchAsync is likewise reachable, from the separate
 *   armed rollback confirmation. The Phase-1 header said ApplyBatchAsync was "deliberately NOT
 *   reachable from any UI"; that stopped being true the moment the batch was wired to a button.
 *
 * WHAT IT STILL DOES NOT DO (stated, not papered over):
 *   - It does not reserve the total up front. The ruling is PER-ITEM reservation: each ApplyAsync
 *     reserves, commits or refunds its own slice, exactly as a single apply does today.
 *   - Rollback inside a batch WAS named untested by everyone up to spike S1, and that sentence
 *     survived here into Phase 3, where it is the negation of the lane's own headline. It is
 *     proved: RemediationPhase3LiveSmokeTests applies three real fixes on .\new2022 under one
 *     approval and undoes them in reverse order, each item confirmed by an independent read
 *     (proved), and BatchRollbackTests drives every branch offline (proved). What remains untested
 *     is narrower and named at the bottom of this file's build notes: the Unconfirmed undo state
 *     has never been produced live, and only sp_configure items can be batch-undone at all.
 *
 * PHASE 2 ADDED GATE B2 — the risk-class scope guard (plan item 3.2, pulled forward). This header
 * used to say "it enforces NOTHING about risk class; a caller could batch a Sensitive op today",
 * which was true and was the whole problem: the ruling lived in a comment. A Sensitive item now
 * fails the WHOLE batch before the credit pre-check, with a message naming the fixes and saying to
 * apply them one at a time.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SQLTriage.Data.Services.Remediation
{
    /// <summary>One item in a batch: a registered template key plus its parameters.</summary>
    public sealed class BatchRemediationItem
    {
        public string TemplateKey { get; }
        public IReadOnlyDictionary<string, string>? Parameters { get; }

        public BatchRemediationItem(string templateKey, IReadOnlyDictionary<string, string>? parameters = null)
        {
            TemplateKey = templateKey;
            Parameters = parameters;
        }
    }

    /// <summary>Why a whole batch was refused before any item ran.</summary>
    public enum BatchRefusal
    {
        None = 0,
        /// <summary>No explicit human approval for the batch.</summary>
        NotApproved,
        /// <summary>An item names a key the template store does not carry. Fails the batch closed.</summary>
        UnregisteredTemplate,
        /// <summary>The aggregate price exceeds the server's available credit.</summary>
        InsufficientCreditsForBatch,
        /// <summary>Empty item list.</summary>
        NothingToApply,
        /// <summary>
        /// An item is a Sensitive-class op. The standing ruling is that Sensitive fixes are applied
        /// one at a time, with the operator looking at that one fix; a batch is the opposite of that.
        /// Fails the WHOLE batch rather than skipping the item, so nobody approves a set and gets a
        /// quietly smaller one.
        /// </summary>
        SensitiveOpInBatch,
    }

    /// <summary>What a batch does when one item fails.</summary>
    public enum BatchStopPolicy
    {
        /// <summary>Default. Stop before the next item once one is refused or could not run.</summary>
        StopOnFirstFailure = 0,
        /// <summary>Run every item regardless. For diagnosis only.</summary>
        ContinueOnFailure = 1,
    }

    /// <summary>One item's preview inside a batch, with the fact the operator most needs before approving.</summary>
    public sealed class BatchRemediationPreviewItem
    {
        public string TemplateKey { get; init; } = string.Empty;
        public RemediationProposal Proposal { get; init; } = default!;

        /// <summary>
        /// What this item would cost if applied — the same price GATE C sums, so the per-row number
        /// and the batch total can never be computed two different ways.
        /// </summary>
        public int Price { get; init; }

        /// <summary>
        /// True when the preview shows the server is ALREADY at the requested value, so applying
        /// this item would be a NoOp and its reservation would be refunded.
        ///
        /// <para>This exists because the preview already read everything needed to know it and said
        /// nothing (spike S1 §3.9, proved live): a three-item batch quoted 3 credits while one item
        /// was already compliant, and the ledger took 2. For a single fix that is a shrug; for a
        /// "remediate everything on this server" sweep, where most items are usually already
        /// compliant, the quoted price would routinely exceed the bill.</para>
        ///
        /// <para>Null means UNKNOWN, not false — the preview did not expose a comparable
        /// current-vs-target pair (a refused preview, or a reading the executor could not take, which
        /// renders as "(unread)"). An unknown is never shown as "no change", and it is never quietly
        /// priced at zero: it counts toward <see cref="BatchPreviewResult.ChangingPrice"/>.</para>
        /// </summary>
        public bool? IsNoChange { get; init; }
    }

    /// <summary>A whole batch's combined preview: every item's rendering, priced two ways.</summary>
    public sealed class BatchPreviewResult
    {
        public IReadOnlyList<BatchRemediationPreviewItem> Items { get; init; }
            = Array.Empty<BatchRemediationPreviewItem>();

        /// <summary>The run-id every audit entry this preview wrote carries. See <see cref="BatchApplyResult.RunId"/>.</summary>
        public string RunId { get; init; } = string.Empty;

        /// <summary>What GATE C would price this batch at — the operator's worst-case exposure.</summary>
        public int PricedTotal => Items.Sum(i => i.Price);

        /// <summary>
        /// The price of the items that would actually CHANGE something: the honest quote. Items
        /// proved already-compliant are excluded; items whose state is UNKNOWN are included, because
        /// quoting an unknown at zero is the same over-promise in the other direction.
        /// </summary>
        public int ChangingPrice => Items.Where(i => i.IsNoChange != true).Sum(i => i.Price);

        /// <summary>How many items the preview proved would do nothing.</summary>
        public int NoChangeCount => Items.Count(i => i.IsNoChange == true);
    }

    // The apply and rollback RESULT types live in BatchApplyContracts.cs — the Phase-3 contract
    // file, committed ahead of this implementation so the UI builder could compile against it.
    // BatchApplyResult replaces P1's BatchRemediationResult and BatchApplyItemResult replaces
    // BatchRemediationItemResult; both keep the old member names as projections, so no shipped
    // assertion changed meaning.

    public sealed class BatchRemediationDriver
    {
        private readonly RemediationRunner _runner;
        private readonly RemediationTemplateStore _templates;
        private readonly IRemediationCreditLedger _credits;

        public BatchRemediationDriver(
            RemediationRunner runner,
            RemediationTemplateStore templates,
            IRemediationCreditLedger credits)
        {
            _runner = runner;
            _templates = templates;
            _credits = credits;
        }

        /// <summary>
        /// Whether a template may ride in a batch at all. False for a Sensitive-class fix, which the
        /// standing ruling keeps one-at-a-time. Public so a surface can leave it out of the
        /// selectable list with the same rule the driver refuses on, rather than a second copy of it.
        /// </summary>
        public static bool IsBatchable(RemediationTemplate? template) =>
            template is not null && template.RiskClass != RemediationRiskClass.Sensitive;

        /// <summary>
        /// The refusal an operator reads. Plain words, the consequence stated, and what to do next.
        /// One producer, so the message cannot drift from the guard that raises it.
        /// </summary>
        public static string DescribeSensitiveRefusal(IReadOnlyList<string> sensitiveKeys) =>
            $"These fixes are marked sensitive and are applied one at a time: {string.Join(", ", sensitiveKeys)}. "
            + "Remove them from the batch and apply each one on its own, so you are looking at that fix "
            + "when you approve it.";

        /// <summary>
        /// A fresh run-id for one operator decision. Public because a caller that previews and then
        /// applies the SAME decision must carry ONE id across both calls — the whole point of the
        /// field is that the Proposed, Approved and Applied entries join.
        /// </summary>
        public static string NewRunId() => Guid.NewGuid().ToString("N");

        /// <summary>
        /// Price the batch without touching the ledger or the server. The operator sees this
        /// beside the combined preview, before approving anything.
        /// </summary>
        public int PriceBatch(IReadOnlyList<BatchRemediationItem> items) =>
            items.Sum(i => RemediationCreditCost.Clamp(RemediationCreditCost.For(_templates.TryGet(i.TemplateKey))));

        /// <summary>
        /// Preview every item, in order. Pure read; nothing is reserved and nothing is written to
        /// the server. Each call is the runner's own ProposeAsync, so gates 1 and 2 are enforced
        /// per item exactly as they are for a single preview.
        /// </summary>
        public async Task<IReadOnlyList<(string TemplateKey, RemediationProposal Proposal)>> PreviewBatchAsync(
            string serverName, IReadOnlyList<BatchRemediationItem> items, string? runId = null,
            CancellationToken ct = default)
        {
            var previews = new List<(string, RemediationProposal)>(items.Count);
            foreach (var item in items)
            {
                var p = await _runner.ProposeAsync(item.TemplateKey, serverName, item.Parameters, runId, ct)
                    .ConfigureAwait(false);
                previews.Add((item.TemplateKey, p));
            }
            return previews;
        }

        /// <summary>
        /// The combined preview the operator approves from: every item rendered, each priced, each
        /// flagged when the server is already at the requested value, and the batch quoted BOTH ways
        /// (worst-case exposure and the changing-items price). Pure read.
        /// </summary>
        public async Task<BatchPreviewResult> PreviewBatchDetailedAsync(
            string serverName, IReadOnlyList<BatchRemediationItem> items, string? runId = null,
            CancellationToken ct = default)
        {
            var id = string.IsNullOrWhiteSpace(runId) ? NewRunId() : runId!;
            var rows = new List<BatchRemediationPreviewItem>(items.Count);
            foreach (var item in items)
            {
                var p = await _runner.ProposeAsync(item.TemplateKey, serverName, item.Parameters, id, ct)
                    .ConfigureAwait(false);
                rows.Add(new BatchRemediationPreviewItem
                {
                    TemplateKey = item.TemplateKey,
                    Proposal = p,
                    Price = RemediationCreditCost.Clamp(RemediationCreditCost.For(_templates.TryGet(item.TemplateKey))),
                    IsNoChange = DetectNoChange(p),
                });
            }
            return new BatchPreviewResult { Items = rows, RunId = id };
        }

        /// <summary>
        /// Reads the preview's OWN current-vs-target sentence — the one the executor already emits —
        /// and answers whether applying would change anything. Null when the pair is absent or
        /// unparsable: unknown is a third answer here, never a silent "it will change" or a silent
        /// "it won't".
        ///
        /// <para>⚠ BOUND TO THE PRODUCER BY STRUCTURE (Phase-1 gate, 2026-09-01). The pattern is
        /// <see cref="RemediationPreviewSentence.CurrentVersusTargetPattern"/>, which is BUILT from
        /// the same format string <see cref="DbatoolsRemediationExecutor"/> writes the sentence
        /// with. This method used to carry its own hand-written regular expression, and the offline
        /// tests hand-wrote the sentence a third time — so an executor wording change would have
        /// left every test green while this quietly answered "unknown" for every item, and an
        /// undetected no-change item is priced as a change.</para>
        /// </summary>
        internal static bool? DetectNoChange(RemediationProposal proposal)
        {
            if (proposal is null || proposal.IsRefused) return null;
            var text = proposal.Preview?.WhatIfText;
            if (string.IsNullOrWhiteSpace(text)) return null;

            var m = RemediationPreviewSentence.CurrentVersusTargetPattern.Match(text);
            if (!m.Success) return null;

            // The current slot is captured as raw text, so "(unread)" matches the SHAPE and then
            // fails to parse — which is the difference between "the sentence was never emitted" and
            // "the sentence says we could not read it". Both answer null; both are honest; neither
            // is allowed to become "no change".
            if (!int.TryParse(m.Groups["current"].Value.Trim(), System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out var current))
                return null;
            if (!int.TryParse(m.Groups["target"].Value.Trim(), System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out var target))
                return null;

            return current == target;
        }

        /// <summary>
        /// Apply the batch. ONE approval, N sequential gated applies, PER-ITEM credit reservation,
        /// one run-id on every audit entry.
        ///
        /// <para>⚠ THIS IS A MUTATING PATH WITH A LIVE CALLER. <c>Pages/Remediation.razor</c> reaches
        /// it from <c>ApplyTheBatchAsync</c>, inside the armed approval dialog. The Phase-1 note that
        /// stood here said it was "NOT wired to any UI", which was true then and false on this tip —
        /// the most dangerous shape of stale comment, because it tells the next reader that editing
        /// this method cannot reach an operator (fix round 1, gate blocker 2).</para>
        /// </summary>
        public async Task<BatchApplyResult> ApplyBatchAsync(
            string serverName,
            IReadOnlyList<BatchRemediationItem> items,
            bool approved,
            string approvedBy,
            BatchStopPolicy stopPolicy = BatchStopPolicy.StopOnFirstFailure,
            string? runId = null,
            CancellationToken ct = default)
        {
            if (items is null || items.Count == 0)
                return BatchApplyResult.Refused(BatchRefusal.NothingToApply, "The batch is empty.",
                    serverName: serverName ?? string.Empty);

            // Batch gate A: ONE explicit human approval covers the whole batch. Refused here so
            // an unapproved batch never reaches the runner at all (the runner would refuse each
            // item anyway — this just keeps the audit trail free of N refusals for one decision).
            if (!approved)
                return BatchApplyResult.Refused(BatchRefusal.NotApproved,
                    "A batch remediation requires one explicit human approval covering every item.",
                    serverName: serverName ?? string.Empty);

            // Batch gate B: every key must be registered BEFORE anything runs. A batch containing
            // one bad key fails whole rather than half-applying and then refusing.
            var unknown = items.Where(i => _templates.TryGet(i.TemplateKey) is null)
                               .Select(i => i.TemplateKey).ToList();
            if (unknown.Count > 0)
                return BatchApplyResult.Refused(BatchRefusal.UnregisteredTemplate,
                    $"Batch refused: not a registered remediation template ({string.Join(", ", unknown)}).",
                    serverName: serverName ?? string.Empty);

            // Batch gate B2: RISK-CLASS SCOPE GUARD (plan item 3.2, pulled forward into Phase 2 as
            // defensive plumbing on Adrian's operator-proofing instruction, 2026-09-01).
            //
            // The ruling has always been that a Sensitive op is applied one at a time. Until now
            // NOTHING enforced it: the driver's own header said so in prose, and a caller could
            // batch a Sensitive template today. Prose is not a guard. This is, and it lands before
            // the credit pre-check so a Sensitive item is never masked by a money refusal.
            var sensitive = items
                .Select(i => _templates.TryGet(i.TemplateKey))
                .Where(t => t is not null && t!.RiskClass == RemediationRiskClass.Sensitive)
                .Select(t => t!.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (sensitive.Count > 0)
                return BatchApplyResult.Refused(BatchRefusal.SensitiveOpInBatch,
                    $"Batch refused. Nothing was applied. {DescribeSensitiveRefusal(sensitive)}",
                    serverName: serverName ?? string.Empty);

            // Batch gate C: AGGREGATE CREDIT PRE-CHECK. A read, not a reservation. The per-item
            // reservations still happen inside each ApplyAsync (the ruling), so this only stops a
            // batch that provably cannot be paid for in full from starting.
            int priced = PriceBatch(items);
            int available = _credits.AvailableFor(serverName);
            if (available < priced)
                return BatchApplyResult.Refused(BatchRefusal.InsufficientCreditsForBatch,
                    $"Batch refused: needs {priced} change credits for '{serverName}', {available} available.",
                    priced, available, serverName ?? string.Empty);

            // ONE run-id for the whole decision. A caller that previewed first passes that preview's
            // id in, so the Proposed entries join the Approved/Applied ones; a caller that did not
            // gets a fresh one, and the applies still join each other.
            var id = string.IsNullOrWhiteSpace(runId) ? NewRunId() : runId!;

            // THE PROPOSAL-RECORD DECISION (Phase 3, closing the P1 §3.5 finding). A run-id supplied
            // by the caller means a combined preview already wrote the Proposed entries under it —
            // that is the ONLY reason to carry an id across two calls, and it is what
            // PreviewBatchDetailedAsync returns its id for. No id means nothing was previewed under
            // this decision, so each apply writes its own proposal record from the gate's authorised
            // rendering. Either way the ledger holds a Proposed→Approved→Applied triple per item, and
            // neither way writes two Proposed entries for one item.
            bool previewJoined = !string.IsNullOrWhiteSpace(runId);

            var results = new List<BatchApplyItemResult>(items.Count);
            bool stopped = false;
            string stopReason = string.Empty;
            string stoppedAtKey = string.Empty;

            foreach (var item in items)
            {
                if (stopped)
                {
                    // NOT A REFUSAL (fix round 1, gate blocker 3). This carried
                    // Refused(NotApproved, ...) — a gate-4 verdict on an item that never reached a
                    // gate, and gate 4 is APPROVAL, so the record said "nobody approved this" about
                    // one item of a batch the operator had just approved whole. Nothing was put to
                    // any check here; the batch simply stopped first, and the honest record carries
                    // no refusal at all and names the item that stopped it.
                    results.Add(new BatchApplyItemResult
                    {
                        TemplateKey = item.TemplateKey,
                        Parameters = item.Parameters,
                        Result = RemediationResult.NotAttempted(
                            RemediationRefusalProse.DescribeNotAttempted(stoppedAtKey)),
                        Attempted = false,
                    });
                    continue;
                }

                // The 5-gate core, untouched. Same approval flag, approvedBy and run-id for every item.
                var r = await _runner.ApplyAsync(item.TemplateKey, serverName, approved, approvedBy,
                    item.Parameters, creditCost: null, correlationId: id,
                    recordProposal: !previewJoined, ct: ct).ConfigureAwait(false);

                results.Add(new BatchApplyItemResult
                {
                    TemplateKey = item.TemplateKey,
                    Parameters = item.Parameters,
                    Result = r,
                    Attempted = true,
                    RollbackReason = DescribeItemRollback(r),
                });

                if (IsBatchStoppingFailure(r) && stopPolicy == BatchStopPolicy.StopOnFirstFailure)
                {
                    stopped = true;
                    stoppedAtKey = item.TemplateKey;
                    stopReason = DescribeStop(item.TemplateKey, r);
                }
            }

            int reserved = results.Where(x => x.Attempted).Sum(x => x.Result.CreditsCharged);
            // Sum the field the RUNNER decided, from the same predicate it charged the ledger from.
            // Re-deriving ChangeStuck here would be a second copy of a pricing rule — the exact
            // shape RemediationCreditOutcome's own header warns about.
            int committed = results.Where(x => x.Attempted).Sum(x => x.Result.CreditsCommitted);

            return new BatchApplyResult
            {
                IsRefused = false,
                PerItem = results,
                RunId = id,
                ServerName = serverName,
                ApprovedBy = approvedBy ?? string.Empty,
                PricedTotal = priced,
                AvailableAtPreCheck = available,
                TotalReserved = reserved,
                TotalCommitted = committed,
                Stopped = stopped,
                StopReason = stopReason,
                Message = stopped
                    ? "The batch stopped after an item failed; later items were not attempted."
                    : "Every item in the batch was attempted.",
            };
        }

        /// <summary>
        /// Whether one item's result ends the batch under the default stop policy. ONE predicate,
        /// so the decision and the sentence describing it cannot come from two different rules.
        ///
        /// <para>⚠ WIDER THAN PHASE 1, DELIBERATELY. P1 stopped on a refusal or
        /// <see cref="RemediationOutcome.CouldNotRun"/> only, and let
        /// <see cref="RemediationOutcome.AppliedVerifyFailed"/> through. That is the state where the
        /// statement RAN and the server did not end up where the fix wanted it — a change that
        /// reached a production server and did not take. Carrying on from there means applying the
        /// rest of an approved set onto a server that has just behaved unexpectedly, and if the
        /// item's own rollback was anything other than Confirmed, onto a server whose state nobody
        /// has read back. The operator approved N changes expecting them to work; the first one that
        /// did not is precisely when to stop and let a person look. Phase 3 wires this batch to a
        /// button, so the narrower rule stops being a theoretical preference.</para>
        ///
        /// <para>A NoOp is still NOT a failure: the server was already compliant, the credit refunds,
        /// and the rest of the batch is still worth running.</para>
        /// </summary>
        internal static bool IsBatchStoppingFailure(RemediationResult r) =>
            r is not null
            && (r.IsRefused
                || r.Outcome == RemediationOutcome.CouldNotRun
                || r.Outcome == RemediationOutcome.AppliedVerifyFailed);

        /// <summary>
        /// WHY the batch stopped, in plain words naming the item and what it did. One producer, so
        /// the sentence a surface prints is the sentence a test asserts. The verify-fail arm also
        /// states what is known about that item's OWN rollback, because "we stopped" and "the thing
        /// that made us stop is or is not still on your server" are different facts and the operator
        /// needs both.
        /// </summary>
        internal static string DescribeStop(string templateKey, RemediationResult r)
        {
            string what;
            if (r.IsRefused)
                // ⚠ PLAIN WORDS, NOT THE MEMBER (lane/remediation-enum-prose, 2026-09-02). This read
                // `({r.Refusal})`, and Pages/Remediation.razor renders StopReason verbatim inside the
                // batch section — so a refusal-stopped batch put "NotARegisteredTemplate" in front of
                // an operator from INSIDE the section the P3 enum guard already scanned. It stayed
                // green because that guard's fixture stops the batch on a CouldNotRun, so this arm
                // was never executed. RemediationRefusalProse is the one producer of these words.
                what = $"was refused at a gate: {RemediationRefusalProse.Describe(r.Refusal)}";
            else if (r.Outcome == RemediationOutcome.AppliedVerifyFailed)
                what = "ran, and the server did not end up where the fix wanted it";
            else
                what = "could not run";

            var rollback = r.Outcome == RemediationOutcome.AppliedVerifyFailed
                ? " " + (r.RollbackState == RemediationRollbackState.Confirmed
                    ? "That change was put back and the server was read to confirm it."
                    : "Check that item before doing anything else: "
                      + DescribeItemRollback(r))
                : string.Empty;

            // WHAT TO DO, on the refusal arm only. The gate that stopped the batch is the thing
            // standing between the operator and the rest of their approved set, so its next step is
            // the move this box exists to name. The other two arms deliberately say nothing here:
            // a verify-fail wants a person to look, and its own rollback sentence above says so.
            var nextStep = r.IsRefused
                ? " " + RemediationRefusalProse.DescribeNextStep(r.Refusal)
                : string.Empty;

            var detail = string.IsNullOrWhiteSpace(r.Message) ? string.Empty : $" {r.Message.Trim()}";
            return $"Stopped at '{templateKey}': it {what}. Later items were not attempted, so nothing "
                 + $"after this one was changed.{nextStep}{rollback}{detail}";
        }

        /// <summary>
        /// The plain-word rollback sentence for one applied item, produced by the ONE producer
        /// (<see cref="RemediationRollbackProse"/>) rather than assembled here. Empty when no
        /// rollback was involved, so a surface prints nothing rather than "NotAttempted".
        /// </summary>
        internal static string DescribeItemRollback(RemediationResult r)
        {
            if (r is null || r.IsRefused) return string.Empty;
            if (r.RollbackState == RemediationRollbackState.NotAttempted) return string.Empty;
            return RemediationRollbackProse.DescribeState(
                r.RollbackState, r.RollbackError, "the setting is back where this batch found it");
        }

        /// <summary>
        /// Undo a batch that has already been applied. Captured inverses, in REVERSE apply order,
        /// each through the leveled (configured-value) rollback with its own confirming read, each
        /// reported as an honest 5-state result with a plain-word reason. Items with nothing to undo
        /// are REPORTED with the reason, never silently dropped.
        /// </summary>
        /// <remarks>
        /// <para>ORDER IS REVERSE APPLY ORDER, and it is not cosmetic. Batch items are not
        /// independent: the renderer emits a <c>show advanced options</c> prelude, applies can share
        /// touched options, and later items are applied onto the state earlier ones left. Undoing
        /// forwards would put an early item back into a world its later siblings had already changed.
        /// Reverse order is the only order in which each inverse sees the state its own apply
        /// produced — the same reason <see cref="RenderedConfigurationScan.SideEffectOptions"/>
        /// restores a prelude AFTER the target.</para>
        ///
        /// <para>WHAT IS REPORTED, AND WHAT IS COUNTED INSTEAD. Every item this batch actually
        /// CHANGED appears in <see cref="BatchRollbackResult.PerItem"/>, including the ones nothing
        /// could be done about, each with its reason. Items that changed nothing — a no-op, a
        /// refusal, an item the stop policy never attempted, an apply already put back by its own
        /// confirmed rollback — are NOT reported as failed undos, because there is nothing about them
        /// to undo; they are counted in <see cref="BatchRollbackResult.Message"/> so their absence is
        /// stated rather than silent. Putting them in the list would drag
        /// <see cref="BatchRollbackResult.AllConfirmed"/> to false for a batch that IS fully back,
        /// which is the same over-report this lane has been closing all day, inverted.</para>
        /// </remarks>
        public async Task<BatchRollbackResult> RollBackBatchAsync(
            BatchApplyResult applied, string approvedBy, CancellationToken ct = default)
        {
            if (applied is null)
                return BatchRollbackResult.Refused("There is no batch result to undo.");

            if (applied.IsRefused)
                return BatchRollbackResult.Refused(
                    "That batch was refused before anything ran, so there is nothing to undo.",
                    applied.RunId, applied.ServerName);

            // The operator clicking "undo this batch" IS the approval, and it must be attributable.
            // An unattributed change to a production server is exactly what gate 4 exists to stop,
            // and a batch undo is N changes.
            if (string.IsNullOrWhiteSpace(approvedBy))
                return BatchRollbackResult.Refused(
                    "An undo needs a named approver. Nothing was sent to the server.",
                    applied.RunId, applied.ServerName);

            var toUndo = applied.PerItem.Where(i => i.Mutated).Reverse().ToList();
            int unchanged = applied.PerItem.Count - toUndo.Count;

            if (toUndo.Count == 0)
                return new BatchRollbackResult
                {
                    RunId = applied.RunId,
                    ServerName = applied.ServerName,
                    Message = "Nothing to undo: none of the items in that batch changed this server.",
                };

            var rows = new List<BatchRollbackItemResult>(toUndo.Count);

            foreach (var item in toUndo)
            {
                ct.ThrowIfCancellationRequested();

                // No captured value means no inverse. Reported, with the reason, never dropped:
                // "this one is still in place" is precisely what an operator undoing a batch must be
                // told, and it is the fact a silent skip destroys.
                //
                // ⚠ THE REASON IS CHOSEN BY OP KIND, and it used to be one sentence for all of them
                // (fix round 1, gate blocker 5). "the value ... was never read" is true only for
                // sp_configure, the one kind that HAS a value to read. A created index, a completed
                // backup and a per-database SET capture no before-value at all, so that sentence
                // reported a missed read where none was possible and pointed the operator at a fault
                // that does not exist. RemediationRollbackProse.NoBatchUndoReason owns the split.
                if (item.PreChangeValue is not int restoreTo)
                {
                    rows.Add(new BatchRollbackItemResult
                    {
                        TemplateKey = item.TemplateKey,
                        RollbackState = RemediationRollbackState.NotAvailable,
                        Message = RemediationRollbackProse.NoRollback(
                            RemediationRollbackProse.NoBatchUndoReason(
                                _templates.TryGet(item.TemplateKey), item.TemplateKey)),
                    });
                    continue;
                }

                // The SAME run-id as the apply, so the undo joins the do in the audit chain.
                //
                // ⚠ NO REGRESSION ACKNOWLEDGEMENT IS SYNTHESISED HERE, and the omission is the
                // considered answer rather than an oversight. Putting a server back IS a move away
                // from the recommended value, so the apply path's regression guard would refuse it —
                // but the undo does not travel the apply path. RollBackConfigurationAsync renders the
                // inverse and runs it directly, and never consults that guard. Manufacturing an
                // acknowledgement the code never reads would put a fabricated operator attestation
                // into the parameters of a real change, to satisfy a gate that is not there.
                var undo = await _runner.RollBackAsync(
                    item.TemplateKey, applied.ServerName, restoreTo,
                    approved: true, approvedBy: approvedBy,
                    parameters: item.Parameters, correlationId: applied.RunId,
                    creditsToRefund: item.CreditsCommitted, ct: ct).ConfigureAwait(false);

                rows.Add(new BatchRollbackItemResult
                {
                    TemplateKey = item.TemplateKey,
                    RollbackState = undo.State,
                    Message = undo.Message,
                    RestoredToValue = undo.RestoredToValue,
                    CreditsRefunded = undo.CreditsRefunded,
                });
            }

            int confirmed = rows.Count(r => r.RollbackState == RemediationRollbackState.Confirmed);
            var message = $"Undo of {rows.Count} changed item{(rows.Count == 1 ? "" : "s")}: "
                        + $"{confirmed} confirmed back, {rows.Count - confirmed} not.";
            if (unchanged > 0)
                message += $" {unchanged} item{(unchanged == 1 ? "" : "s")} in that batch changed nothing, "
                         + "so there was nothing to undo for them.";

            return new BatchRollbackResult
            {
                RunId = applied.RunId,
                ServerName = applied.ServerName,
                PerItem = rows,
                Message = message,
            };
        }

    }
}
