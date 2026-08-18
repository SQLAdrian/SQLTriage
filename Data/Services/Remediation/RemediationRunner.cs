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
            int creditCost = 1,
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
            int creditCost,
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
            if (IsParked(serverName))
            {
                _audit.LogRemediationApplied(template!.Key, serverName,
                    AuditLogService.RemediationOutcomes.CouldNotRun,
                    "Server parked after an earlier permissions denial (retried automatically after the back-off TTL).");
                _audit.Flush();
                return RemediationResult.Applied(RemediationOutcome.CouldNotRun,
                    $"Server '{serverName}' parked after a permissions denial; will retry after the back-off window.");
            }

            // Gate 3: CREDIT — reserve against this server's balance; must commit or refund.
            var reservation = _credits.Reserve(serverName, creditCost);
            if (reservation is null)
                return RemediationResult.Refused(RemediationRefusal.InsufficientCredits,
                    $"Insufficient change credits for '{serverName}' (need {creditCost}, have {_credits.AvailableFor(serverName)}).");

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

                _audit.LogRemediationApproved(template!.Key, serverName, approvedBy);

                var request = new RemediationRequest(template, serverName, parameters);
                var execution = await _executor.ExecuteAsync(request, ct).ConfigureAwait(false);

                // Distinct terminal states drive credit + back-off + ledger.
                if (execution.IsPermissionDenied)
                    _permBackoff[serverName] = DateTime.UtcNow + BackoffTtl;

                // A change "stuck" if verified, OR if verify failed but it was NOT cleanly rolled
                // back. A successfully reverted change consumed nothing — refund it (the ledger
                // contract promises "refund if ... rolled back"). NoOp/CouldNotRun also refund.
                bool changeStuck = execution.Outcome == RemediationOutcome.AppliedVerified
                    || (execution.Outcome == RemediationOutcome.AppliedVerifyFailed
                        && !(execution.RolledBack && execution.RollbackSucceeded));
                if (changeStuck)
                    _credits.Commit(reservation);
                else
                    _credits.Refund(reservation);

                _audit.LogRemediationApplied(template.Key, serverName,
                    RemediationOutcomeMap.ToAuditString(execution.Outcome), execution.Error,
                    execution.PreChangeValue?.ToString());

                // The executor performs snapshot-based rollback on a verify failure; the
                // runner owns the audit trail, so it ledgers the rollback outcome here.
                if (execution.RolledBack)
                    _audit.LogRemediationRolledBack(template.Key, serverName,
                        execution.RollbackSucceeded, execution.RollbackError);

                // The audit trail is the deliverable — flush it to disk synchronously before
                // we report the outcome, so a "done" can never outrun its own ledger entry.
                _audit.Flush();

                return RemediationResult.Applied(execution.Outcome, execution.Error, execution.RolledBack, execution.PreChangeValue);
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
                return RemediationResult.Applied(RemediationOutcome.CouldNotRun, ex.Message);
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

            // Two op-kinds have a change-vetting path: Configuration (sp_configure) and the
            // Transactable CreateIndex (add-missing-index). Any other kind fails closed — a
            // registered key alone never authorises a write (that key-only promotion was exactly
            // the gap this gate closes). Both paths render to T-SQL and classify THAT rendering.
            // Pin BOTH Kind AND OpKind so they can never diverge: a Configuration template must
            // carry an SpConfigure op, and CreateIndex must be Transactable. (Closes the overlay
            // path where kind=Configuration + opKind=CreateIndex would route to the index executor.)
            bool isConfiguration = template.Kind == RemediationKind.Configuration
                && template.Operation?.OpKind == RemediationOpKind.SpConfigure;
            bool isCreateIndex = template.Kind == RemediationKind.Transactable
                && template.Operation?.OpKind == RemediationOpKind.CreateIndex;
            if (!isConfiguration && !isCreateIndex)
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
        public bool IsServerParked(string serverName) => IsParked(serverName);

        // Parked iff there's an unexpired park entry. An expired entry is swept and the
        // server is eligible to retry (e.g. after the operator fixes credentials).
        private bool IsParked(string serverName)
        {
            if (string.IsNullOrEmpty(serverName)) return false;
            if (!_permBackoff.TryGetValue(serverName, out var until)) return false;
            if (DateTime.UtcNow >= until) { _permBackoff.TryRemove(serverName, out _); return false; }
            return true;
        }
    }
}
