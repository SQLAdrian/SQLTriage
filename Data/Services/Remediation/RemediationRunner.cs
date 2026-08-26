/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationRunner — build step 4. The ONLY path that may execute a
 * Remediation-classified statement. UI binds to this facade; it never touches
 * the executor, the template store, or the credit ledger directly.
 *
 * Enforces the 5 gates IN ORDER. Each gate that refuses returns a refusal with
 * a reason and logs nothing executable — the read-only wall stays provable.
 *
 *   1 TEMPLATE   — registered template AND SqlSafetyValidator classifies the
 *                  template's change as Remediation (not Safe-read, not Blocked).
 *   2 CAPABILITY — IRemediationCapability granted (managed-tier only).
 *   3 CREDIT     — reserve credits; refund if we never execute.
 *   4 APPROVAL   — explicit human approval flag (no silent/auto apply). The
 *                  preview is rendered for the human via PreviewAsync first.
 *   5 EXECUTION  — RE-DERIVE the gate on the mutating path, probe audit
 *                  writability, then execute; commit credits + ledger the outcome.
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services.Remediation
{
    public sealed class RemediationRunner
    {
        private readonly RemediationTemplateStore _templates;
        private readonly IRemediationCapability _capability;
        private readonly IRemediationCreditLedger _credits;
        private readonly IRemediationExecutor _executor;
        private readonly AuditLogService _audit;
        private readonly ILogger<RemediationRunner> _logger;

        // Permission back-off: a server that returns a non-transient perms error is parked
        // (so we don't churn the audit with denials) until a TTL expires — then it may be
        // retried (e.g. after the operator fixes credentials). Value = park-until (UTC).
        private static readonly TimeSpan BackoffTtl = TimeSpan.FromMinutes(30);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _permBackoff
            = new(StringComparer.OrdinalIgnoreCase);

        // Per-server apply serialization: two concurrent approvals (double-click / two
        // operators) for the same server must not both run the change + ledger.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.SemaphoreSlim> _applyLocks
            = new(StringComparer.OrdinalIgnoreCase);

        public RemediationRunner(
            RemediationTemplateStore templates,
            IRemediationCapability capability,
            IRemediationCreditLedger credits,
            IRemediationExecutor executor,
            AuditLogService audit,
            ILogger<RemediationRunner> logger)
        {
            _templates = templates;
            _capability = capability;
            _credits = credits;
            _executor = executor;
            _audit = audit;
            _logger = logger;
        }

        /// <summary>
        /// Gate 4 step one: render the -WhatIf preview for a human. Still requires
        /// gates 1–3 to pass first (no point previewing an unauthorised change).
        /// The preview's safety verdict is DISPLAY-ONLY; gate 5 re-checks.
        /// </summary>
        public async Task<RemediationProposal> ProposeAsync(
            string templateKey, string serverName,
            System.Collections.Generic.IReadOnlyDictionary<string, string>? parameters = null,
            CancellationToken ct = default)
        {
            var template = _templates.TryGet(templateKey);

            // Gate 1: template registered AND its rendered change is Remediation-classified.
            if (!GatePassesTemplate(template, parameters, out var classifyError))
                return RemediationProposal.Refused(RemediationRefusal.NotARegisteredTemplate, classifyError);

            // Gate 2: capability.
            if (!_capability.IsGranted)
                return RemediationProposal.Refused(RemediationRefusal.CapabilityDenied,
                    "Build/licence does not carry the remediation capability.");

            var request = new RemediationRequest(template!, serverName, parameters);
            var preview = await _executor.PreviewAsync(request, ct).ConfigureAwait(false);

            _audit.LogRemediationProposed(template!.Key, serverName, preview.WhatIfText);
            return RemediationProposal.Previewed(preview);
        }

        /// <summary>
        /// Gates 1–5. <paramref name="approved"/> is gate 4: it must be an explicit
        /// human decision passed by the caller — the runner never approves itself.
        /// Applies are SERIALIZED per server so two concurrent approvals can't both run.
        /// </summary>
        public async Task<RemediationResult> ApplyAsync(
            string templateKey, string serverName, bool approved, string approvedBy,
            System.Collections.Generic.IReadOnlyDictionary<string, string>? parameters = null,
            int? creditCost = null,
            CancellationToken ct = default)
        {
            var applyGate = _applyLocks.GetOrAdd(serverName ?? string.Empty, _ => new System.Threading.SemaphoreSlim(1, 1));
            await applyGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await ApplyCoreAsync(templateKey, serverName, approved, approvedBy, parameters, creditCost, ct)
                    .ConfigureAwait(false);
            }
            finally { applyGate.Release(); }
        }

        private async Task<RemediationResult> ApplyCoreAsync(
            string templateKey, string serverName, bool approved, string approvedBy,
            System.Collections.Generic.IReadOnlyDictionary<string, string>? parameters,
            int? creditCost,
            CancellationToken ct)
        {
            var template = _templates.TryGet(templateKey);

            // Gate 1: TEMPLATE.
            if (!GatePassesTemplate(template, parameters, out var classifyError))
                return RemediationResult.Refused(RemediationRefusal.NotARegisteredTemplate, classifyError);

            // Gate 2: CAPABILITY.
            if (!_capability.IsGranted)
                return RemediationResult.Refused(RemediationRefusal.CapabilityDenied,
                    "Build/licence does not carry the remediation capability.");

            // Gate 4: APPROVAL (checked before reserving credits so an unapproved
            // request never touches the ledger).
            if (!approved)
                return RemediationResult.Refused(RemediationRefusal.NotApproved,
                    "Remediation requires explicit human approval.");

            // Permission back-off: a server parked (within the TTL) is not retried. Still
            // record the attempt — for a compliance product, a refused/skipped apply must
            // leave an audit trail (silent no-op is the nightmare).
            //
            // Two honesty fixes here (r1-06, 2026-08-25). The ledger detail used to read "retried
            // automatically after the back-off TTL" and the operator was told the apply "will retry
            // after the back-off window": NOTHING retries. The TTL only stops refusing, so the next
            // MANUAL apply after it expires is the retry. And this path returned without ever
            // calling LogRemediationApproved, while the page printed "Recorded in the audit ledger
            // (Proposed / Approved / Applied)" over it — proved live by the hunt, which found one
            // EventType 39 for two EventType 40s. The human approval is a real event (gate 4 above
            // has already required it), so it is now recorded where it happened.
            if (IsParked(serverName, out var parkedUntil))
            {
                _audit.LogRemediationApproved(template!.Key, serverName, approvedBy);
                _audit.LogRemediationApplied(template.Key, serverName,
                    AuditLogService.RemediationOutcomes.CouldNotRun,
                    $"Not attempted. The server is parked after an earlier permissions denial until {parkedUntil:u}. "
                    + "Nothing retries on its own; applying again after that time is the retry.");
                _audit.Flush();
                return RemediationResult.Applied(RemediationOutcome.CouldNotRun,
                    $"Nothing was applied. '{serverName}' is parked after a permissions denial until {parkedUntil:u}. "
                    + "No retry is scheduled. Fix the permissions, then apply again after that time.");
            }

            // Gate 3: CREDIT — reserve against this server's balance; must commit or refund.
            // The RUNNER is the cost authority, not the caller: an omitted creditCost is
            // priced from the template so a UI that forgets to pass one can never underpay.
            // An explicit value is still clamped — no caller can reserve zero or negative.
            var cost = RemediationCreditCost.Clamp(creditCost ?? RemediationCreditCost.For(template));
            var reservation = _credits.Reserve(serverName, cost);
            if (reservation is null)
                return RemediationResult.Refused(RemediationRefusal.InsufficientCredits,
                    $"Insufficient change credits for '{serverName}' (need {cost}, have {_credits.AvailableFor(serverName)}).");

            try
            {
                // Gate 5 pre-flight: AUDIT-WRITABILITY PROBE. Fail LOUD, refund credits.
                if (!_executor.CanWriteAudit())
                {
                    _credits.Refund(reservation);
                    _logger.LogError("Remediation refused: audit ledger not writable for '{Server}'.", serverName);
                    return RemediationResult.Refused(RemediationRefusal.AuditNotWritable,
                        "Audit ledger is not writable; refusing to apply (the audit trail is the deliverable).");
                }

                // Gate 5: RE-DERIVE the gate on the mutating path. The preview's
                // verdict was display-only; authorise again here against the real
                // template change before anything runs.
                if (!GatePassesTemplate(template, parameters, out var reError))
                {
                    _credits.Refund(reservation);
                    return RemediationResult.Refused(RemediationRefusal.NotARegisteredTemplate,
                        $"Re-derived gate failed at execution: {reError}");
                }

                // Rulings 3 and 7: an apply that rode an acknowledged override route records WHAT
                // was acknowledged, and the operator's stated reason, alongside who approved it.
                // Null for an ordinary apply, which logs exactly as before.
                _audit.LogRemediationApproved(template!.Key, serverName, approvedBy,
                    RemediationAcknowledgements.Describe(parameters));

                var request = new RemediationRequest(template, serverName, parameters);
                var execution = await _executor.ExecuteAsync(request, ct).ConfigureAwait(false);

                // Distinct terminal states drive credit + back-off + ledger.
                if (execution.IsPermissionDenied)
                    _permBackoff[serverName] = DateTime.UtcNow + BackoffTtl;

                // A change "stuck" if verified, OR if verify failed and the rollback was not
                // CONFIRMED. Only a confirmed rollback refunds. NoOp/CouldNotRun also refund.
                //
                // RULING 2 (DECISIONS 2026-08-25 17:33) set that test, and it is stricter than what
                // shipped: an inverse action that ran and could NOT be confirmed used to refund.
                // The server's state is genuinely unknown at that point, and issuing a refund for
                // it asserts the change is gone. It keeps the charge instead. Confirmed is the only
                // state that says a server was put back, so it is the only state that pays one back.
                // The rule itself lives on RemediationCreditOutcome so the page's "credit charged /
                // refunded" line reads the SAME predicate this charge is made from.
                bool changeStuck = RemediationCreditOutcome.ChangeStuck(execution.Outcome, execution.RollbackState);
                if (changeStuck)
                    _credits.Commit(reservation);
                else
                    _credits.Refund(reservation);

                // A rollback that could not be attempted writes no RemediationRolledBack entry
                // below, because no inverse ran and there is nothing to attest to. The REASON is
                // still a compliance fact ("this change is stuck and cannot be undone from here"),
                // so it rides the applied entry rather than evaporating.
                var appliedError = execution.RollbackState == RemediationRollbackState.NotAvailable
                                   && !string.IsNullOrWhiteSpace(execution.RollbackError)
                    ? string.IsNullOrWhiteSpace(execution.Error)
                        ? execution.RollbackError
                        : $"{execution.Error} {execution.RollbackError}"
                    : execution.Error;

                _audit.LogRemediationApplied(template.Key, serverName,
                    RemediationOutcomeMap.ToAuditString(execution.Outcome), appliedError,
                    execution.PreChangeValue?.ToString());

                // S1: a template whose EFFECT needs time (e.g. "did the created jobs run?")
                // schedules a follow-up verification. Only a verified apply schedules one, and
                // only when the plan is renderable NOW from these parameters — probing at
                // schedule time guarantees the later re-render cannot fail by construction.
                if (execution.Outcome == RemediationOutcome.AppliedVerified
                    && template.DeferredVerify is { } dv
                    && DeferredVerifyPlan.TryRenderVerifySql(template, parameters, out _, out _))
                {
                    _audit.LogRemediationVerifyScheduled(template.Key, serverName,
                        DateTime.UtcNow.AddDays(dv.WindowDays), dv.Description,
                        DeferredVerificationService.SerializeParams(parameters));
                }

                // The executor performs snapshot-based rollback on a verify failure; the runner owns
                // the audit trail, so it ledgers the rollback outcome here. RolledBack is true ONLY
                // when an inverse action actually ran. A rollback that could not be attempted gets
                // no entry: a "rollback FAILED" line about a statement nobody sent is a fabricated
                // event in an HMAC-chained ledger.
                if (execution.RolledBack)
                    _audit.LogRemediationRolledBack(template.Key, serverName,
                        execution.RollbackState switch
                        {
                            RemediationRollbackState.Confirmed => true,
                            RemediationRollbackState.Failed => false,
                            _ => (bool?)null, // Unconfirmed — the ledger's own third state
                        },
                        execution.RollbackError);

                // The audit trail is the deliverable — flush it to disk synchronously before
                // we report the outcome, so a "done" can never outrun its own ledger entry.
                _audit.Flush();

                return RemediationResult.Applied(execution.Outcome, execution.Error,
                    execution.RollbackState, execution.PreChangeValue, execution.RollbackError,
                    creditsCharged: cost);
            }
            catch (Exception ex)
            {
                // Any unexpected failure: the change did not complete — refund and
                // record as could-not-run. A fix that threw never reads as applied.
                _credits.Refund(reservation);
                _logger.LogError(ex, "Remediation '{Key}' on '{Server}' threw during apply.", templateKey, serverName);
                _audit.LogRemediationApplied(template!.Key, serverName,
                    AuditLogService.RemediationOutcomes.CouldNotRun, ex.Message);
                // Flush on the throw path too — an apply that threw (with the change possibly
                // already live) is the most security-relevant terminal state to persist.
                _audit.Flush();
                // Carry the reserved cost through as creditsCharged. The reservation of `cost`
                // credits was just refunded above, so the operator's credit line must read "N change
                // credits were refunded" for the REAL N. A bare Applied(...) defaults CreditsCharged
                // to 0 and would render "0 change credits were refunded." over a one-to-five refund
                // (DECISIONS 2026-08-25, VOICE-1 exception-path gate defect).
                return RemediationResult.Applied(RemediationOutcome.CouldNotRun, ex.Message,
                    creditsCharged: cost);
            }
        }

        /// <summary>
        /// Gate 1 (and the gate-5 re-derivation). The gate must vet WHAT ACTUALLY RUNS:
        /// it renders the template's structured operation to canonical T-SQL via the SAME
        /// renderer the executor uses, then classifies THAT rendering under the template's
        /// key (using the store as the single registered-key authority, so the store and
        /// validator can never drift). The classification is value-independent, so the
        /// representative render is what's vetted here; the executor renders + runs the
        /// real, bounds-checked value from the same op. A registered template whose
        /// rendered change does not classify as Remediation fails closed.
        ///
        /// MVP ships only Configuration (structured-op) templates. A non-Configuration
        /// template has no change-vetting path yet and fails closed — never authorised by
        /// registered key alone (that key-only promotion was exactly the gap this gate closes).
        /// </summary>
        private bool GatePassesTemplate(
            RemediationTemplate? template,
            System.Collections.Generic.IReadOnlyDictionary<string, string>? parameters,
            out string error)
        {
            error = string.Empty;
            if (template is null || !_templates.IsRegistered(template.Key))
            {
                error = "Not a registered remediation template.";
                return false;
            }

            // Eight op-kinds have a change-vetting path: Configuration (sp_configure or, since
            // corpus check #39, db_set_option — a per-database ALTER DATABASE ... SET whose
            // target list is bounded by offenders_query, same Configuration rollback rationale
            // as sp_configure), and the five Transactable kinds CreateIndex (add-missing-index),
            // AgentAlertPack (the standard SQL Agent alert + operator pack),
            // InstallMaintenanceSolution (Ola Hallengren install + schedule tickboxes),
            // BackupDatabaseNow, and CheckDbNow (lane S5 — gated one-shot live-server BACKUP
            // DATABASE / DBCC CHECKDB, NOT reversible; the resource + confirm-token gates live
            // in BackupCheckDbOpRenderer and the executor, not here — this gate only vets the
            // STATEMENT SHAPE), and AgentJobPrimaryGuard (inject an AG primary-replica guard as
            // step 1 of one named Agent job). Any other kind fails closed — a registered key
            // alone never authorises a write (that key-only promotion was exactly the gap this
            // gate closes). All eight paths render to T-SQL and classify THAT rendering. Pin
            // BOTH Kind AND OpKind so they can never diverge: a Configuration template must
            // carry an SpConfigure or DbSetOption op, and Transactable must carry CreateIndex,
            // AgentAlertPack, InstallMaintenanceSolution, BackupDatabaseNow, CheckDbNow, or
            // AgentJobPrimaryGuard.
            // (Closes the overlay path where kind=Configuration + opKind=CreateIndex would route
            // to the index executor.)
            bool isConfiguration = template.Kind == RemediationKind.Configuration
                && (template.Operation?.OpKind == RemediationOpKind.SpConfigure
                    || template.Operation?.OpKind == RemediationOpKind.DbSetOption);
            bool isCreateIndex = template.Kind == RemediationKind.Transactable
                && template.Operation?.OpKind == RemediationOpKind.CreateIndex;
            bool isAgentAlertPack = template.Kind == RemediationKind.Transactable
                && template.Operation?.OpKind == RemediationOpKind.AgentAlertPack;
            bool isInstallMaintenanceSolution = template.Kind == RemediationKind.Transactable
                && template.Operation?.OpKind == RemediationOpKind.InstallMaintenanceSolution;
            // Lane S5 — gated one-shot Backup-NOW / CHECKDB-NOW. Same Transactable shape:
            // pin BOTH Kind AND OpKind so an overlay can never mix a Configuration Kind
            // with a BackupDatabaseNow/CheckDbNow op (or vice versa) to slip past this gate.
            bool isBackupDatabaseNow = template.Kind == RemediationKind.Transactable
                && template.Operation?.OpKind == RemediationOpKind.BackupDatabaseNow;
            bool isCheckDbNow = template.Kind == RemediationKind.Transactable
                && template.Operation?.OpKind == RemediationOpKind.CheckDbNow;
            // AG primary guard — injects a guard step into ONE named existing Agent job.
            // Same Transactable shape and the same Kind+OpKind pinning as the others, so an
            // overlay can't pair a Configuration Kind with this op to reach the job executor.
            bool isAgPrimaryGuard = template.Kind == RemediationKind.Transactable
                && template.Operation?.OpKind == RemediationOpKind.AgentJobPrimaryGuard;
            // Agent-job sync (recreate one job on the secondary) and its deliberately separate
            // delete-extra sibling. Kept as two kinds so a sync can never delete: the delete
            // path is reachable only under its own registered key.
            bool isAgentJobSync = template.Kind == RemediationKind.Transactable
                && template.Operation?.OpKind == RemediationOpKind.AgentJobSync;
            bool isAgentJobDeleteExtra = template.Kind == RemediationKind.Transactable
                && template.Operation?.OpKind == RemediationOpKind.AgentJobDeleteExtra;
            if (!isConfiguration && !isCreateIndex && !isAgentAlertPack && !isInstallMaintenanceSolution
                && !isBackupDatabaseNow && !isCheckDbNow && !isAgPrimaryGuard
                && !isAgentJobSync && !isAgentJobDeleteExtra)
            {
                error = $"Template '{template.Key}' kind '{template.Kind}' has no change-vetting path yet (fails closed).";
                return false;
            }

            if (template.Operation is null)
            {
                error = $"Template '{template.Key}' carries no structured operation to authorise.";
                return false;
            }

            if (!RemediationOpRenderer.TryRenderForClassification(template.Operation, out var renderedSql, out var renderError))
            {
                error = $"Template '{template.Key}' operation could not be rendered: {renderError}";
                return false;
            }

            var classification = SqlSafetyValidator.Classify(
                renderedSql, new RemediationContext(template.Key), _templates.RegisteredKeys());

            if (classification != SqlClassification.Remediation)
            {
                error = $"Safety validator does not authorise the rendered change for key '{template.Key}' ({classification}).";
                return false;
            }
            return true;
        }

        /// <summary>Test/diagnostic visibility: is a server currently parked by back-off?</summary>
        public bool IsServerParked(string serverName) => IsParked(serverName, out _);

        // Parked iff there's an unexpired park entry. An expired entry is swept and the
        // server is eligible to retry (e.g. after the operator fixes credentials). The expiry is
        // reported out so the refusal can name the real moment instead of describing a retry that
        // does not exist.
        private bool IsParked(string serverName, out DateTime until)
        {
            until = default;
            if (string.IsNullOrEmpty(serverName)) return false;
            if (!_permBackoff.TryGetValue(serverName, out var parkedUntil)) return false;
            if (DateTime.UtcNow >= parkedUntil) { _permBackoff.TryRemove(serverName, out _); return false; }
            until = parkedUntil;
            return true;
        }
    }
}
