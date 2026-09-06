/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// The single place app-issued DDL is journaled. Ruling R1 (2026-09-01) requires BOTH registers
    /// for every DDL statement this app sends to a monitored server: the tamper-evident
    /// <see cref="AuditLogService"/> chain AND a <see cref="ChangeItemService"/> Change Ledger row.
    ///
    /// <para><b>Not a new mechanism.</b> Everything here is a call to the typed
    /// <c>AuditLogService.LogDdl*</c> methods plus <c>ChangeItemService.LogChange</c> — the same
    /// pattern <see cref="Remediation.RemediationRunner"/> established with <c>LogRemediation*</c> +
    /// <c>Flush</c>. What this class adds is that the two-register rule lives in ONE place instead
    /// of three. Three surfaces each writing their own pair is three chances to write one and call
    /// it journaled, and a per-surface test can only prove what that surface remembered to do.</para>
    ///
    /// <para><b>The ordering rule.</b> <see cref="RecordAttempt"/> flushes before returning, so the
    /// statement is on disk before it is sent. Enqueue-only would not be journaling: the buffer is
    /// in-process, and a crash mid-DDL would lose exactly the record that mattered.
    /// <see cref="RecordOutcomeAsync"/> is called on the success path AND on every failure path, so
    /// a DDL that threw is journaled as failed rather than as absent.</para>
    ///
    /// <para><b>Journaling never breaks the surface it journals.</b> The ledger write touches an
    /// encrypted SQLite file that can be locked or missing; the audit flush can fail over. Both are
    /// caught here and reported through the logger. A journaling fault must not turn a completed
    /// server-side change into an exception the operator reads as "it did not run" — that would
    /// trade a record-keeping problem for a false statement about the estate. The audit chain is
    /// where the honest trace lives either way, and its own failover path records its trouble.</para>
    ///
    /// <para><b>Nullable dependencies are deliberate.</b> Both registers are resolved with
    /// <c>GetService</c> rather than <c>GetRequiredService</c> at every DI site, matching how
    /// <see cref="ChangeItemService"/> itself takes its audit dependency. A host that has not
    /// registered one of them still gets the other, and <see cref="Describe"/> reports which
    /// registers are live so no caller can claim a journal it does not have.</para>
    /// </summary>
    public sealed class DdlJournal
    {
        private readonly AuditLogService? _audit;
        private readonly ChangeItemService? _ledger;
        private readonly ILogger<DdlJournal>? _logger;

        public DdlJournal(AuditLogService? audit, ChangeItemService? ledger, ILogger<DdlJournal>? logger = null)
        {
            _audit = audit;
            _ledger = ledger;
            _logger = logger;
        }

        /// <summary>True when the tamper-evident chain is available in this host.</summary>
        public bool HasAuditRegister => _audit is not null;

        /// <summary>True when the Change Ledger is available in this host.</summary>
        public bool HasLedgerRegister => _ledger is not null;

        /// <summary>
        /// Which registers this instance can actually write, for a log line or a diagnostic page.
        /// Exists so "journaled to both registers" is a checkable statement rather than an
        /// assumption about DI.
        /// </summary>
        public string Describe() =>
            (HasAuditRegister, HasLedgerRegister) switch
            {
                (true, true) => "audit chain + change ledger",
                (true, false) => "audit chain only (change ledger not registered)",
                (false, true) => "change ledger only (audit chain not registered)",
                _ => "no registers available"
            };

        /// <summary>
        /// Records that a DDL statement is about to be sent, and FLUSHES before returning. Call
        /// this immediately before <c>ExecuteNonQueryAsync</c>, on every DDL path.
        /// </summary>
        /// <param name="surface">One of <see cref="AuditLogService.DdlSurfaces"/>.</param>
        /// <param name="operation">The act in the surface's vocabulary ("create-index", "start", "drop").</param>
        /// <param name="serverName">The instance the statement is being sent to.</param>
        /// <param name="statement">The exact statement text, in full. Never truncate it.</param>
        /// <param name="details">Optional context (database, panel id, whether the text was server-authored).</param>
        public void RecordAttempt(string surface, string operation, string serverName, string statement,
            string? details = null)
        {
            try
            {
                _audit?.LogDdlAttempted(surface, operation, serverName, statement, details);
                _audit?.Flush();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[DDL-JOURNAL] Failed to record the attempt for {Operation} on {Server}",
                    operation, serverName);
            }
        }

        /// <summary>
        /// Records that a DDL statement was refused before reaching the server — a
        /// <see cref="DangerousExecGuard"/> verdict, a permission check, or a rejected shape.
        /// Flushes, because a refusal is often the only trace that anything was tried.
        /// </summary>
        public void RecordBlocked(string surface, string operation, string serverName, string statement, string reason)
        {
            try
            {
                _audit?.LogDdlBlocked(surface, operation, serverName, statement, reason);
                _audit?.Flush();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[DDL-JOURNAL] Failed to record the block for {Operation} on {Server}",
                    operation, serverName);
            }
        }

        /// <summary>
        /// Records the terminal state of a DDL statement in BOTH registers. Call on the success path
        /// and on every failure path — including cancellation, where the server-side effect is
        /// genuinely unknown and the outcome string says so.
        /// </summary>
        /// <param name="outcome">One of <see cref="AuditLogService.DdlOutcomes"/>.</param>
        public async Task RecordOutcomeAsync(string surface, string operation, string serverName, string statement,
            string outcome, string? errorMessage = null)
        {
            try
            {
                _audit?.LogDdlCompleted(surface, operation, serverName, statement, outcome, errorMessage);
                _audit?.Flush();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[DDL-JOURNAL] Failed to record the outcome for {Operation} on {Server}",
                    operation, serverName);
            }

            if (_ledger is null) return;

            try
            {
                // The ledger's check-id IS the surface id, so an operator can ask the ledger "what
                // has the plan viewer created on this server" without matching on prose. The full
                // statement rides remediation_script — the column that already exists for "the SQL
                // behind this row" — so the ledger and the chain carry the same text.
                await _ledger.LogChange(
                    serverName: serverName,
                    checkId: surface,
                    checkName: DescribeSurface(surface, operation),
                    rationale: BuildRationale(operation, outcome, errorMessage),
                    remediationScript: statement);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[DDL-JOURNAL] Failed to write the change-ledger row for {Operation} on {Server}",
                    operation, serverName);
            }
        }

        // The ledger's rationale column is NOT NULL by design — a logged change with no stated
        // reason is worthless for audit. For DDL the reason is what the operator did and what came
        // of it, stated plainly and without promising an outcome the row cannot evidence.
        internal static string BuildRationale(string operation, string outcome, string? errorMessage)
        {
            var head = outcome switch
            {
                AuditLogService.DdlOutcomes.Succeeded =>
                    $"Operator ran '{operation}' from the app; the server returned no error.",
                AuditLogService.DdlOutcomes.Failed =>
                    $"Operator ran '{operation}' from the app; it failed.",
                AuditLogService.DdlOutcomes.Cancelled =>
                    $"Operator ran '{operation}' from the app and cancelled it; the server-side effect is unknown.",
                _ =>
                    $"Operator ran '{operation}' from the app; outcome recorded as '{outcome}'.",
            };

            return string.IsNullOrWhiteSpace(errorMessage) ? head : head + " " + errorMessage;
        }

        internal static string DescribeSurface(string surface, string operation) => surface switch
        {
            AuditLogService.DdlSurfaces.IndexCreate => "Plan viewer: index create",
            AuditLogService.DdlSurfaces.DashboardAction => $"Dashboard action: {operation}",
            AuditLogService.DdlSurfaces.XEventLifecycle => $"Extended Events: {operation}",
            _ => $"{surface}: {operation}",
        };
    }
}
