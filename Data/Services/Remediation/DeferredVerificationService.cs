/* In the name of God, the Merciful, the Compassionate */

// S1 deferred-verify state ("resolution applied — verifying by <date>").
//
// Some resolutions cannot be confirmed at apply time: the change applies cleanly (the
// immediate verify passes) but its real-world EFFECT needs time to materialise — e.g. the
// maintenance schedule jobs the S3 lane creates only prove themselves once they have RUN.
// Model:
//   - A template DECLARES a DeferredVerifySpec (description + window days). No SQL rides it.
//   - After an AppliedVerified apply of such a template, RemediationRunner ledgers a
//     RemediationVerifyScheduled entry carrying the verify-by deadline and the apply's
//     parameters (JSON). The HMAC-chained ledger is the single persistence — no new store.
//   - This service reads the ledger to list PENDING verifications (scheduled entries with no
//     later resolution for the same template+server+parameters) and, on "Verify now",
//     RE-RENDERS the scalar verify SQL from the REGISTERED template + the stored parameters
//     via DeferredVerifyPlan — executable SQL never round-trips through a data store, so a
//     tampered ledger line can rename a job but never inject a statement shape.
//   - Resolution is fail-closed and honest: scalar 1 -> Passed; anything else past the
//     deadline -> Failed (ledgered at Error severity); anything else before the deadline ->
//     stays pending (connectivity errors also stay pending — a broken connection must not
//     stamp a verification failed).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services.Remediation
{
    /// <summary>One scheduled-but-unresolved deferred verification, read back from the ledger.</summary>
    public sealed record PendingVerification(
        string TemplateKey,
        string ServerName,
        DateTime ScheduledUtc,
        DateTime VerifyByUtc,
        string Description,
        string ParametersJson)
    {
        public bool IsOverdue(DateTime nowUtc) => nowUtc > VerifyByUtc;
    }

    /// <summary>Terminal-or-pending status of one "Verify now" attempt. Never a bare bool.</summary>
    public enum DeferredVerifyStatus
    {
        /// <summary>The effect is confirmed; resolution ledgered (Info).</summary>
        Passed,
        /// <summary>Past the deadline and still not confirmed; resolution ledgered (Error).</summary>
        FailedOverdue,
        /// <summary>Not confirmed yet, but the deadline has not passed — stays pending.</summary>
        StillPending,
        /// <summary>Could not check (no connection / query error) — stays pending, message says why.</summary>
        CouldNotCheck,
    }

    public sealed record DeferredVerifyRunResult(DeferredVerifyStatus Status, string Message);

    /// <summary>
    /// Renders the scalar (1 = confirmed) verify SQL for a template's deferred verification
    /// from the REGISTERED template + the apply's parameters. Fail-closed: templates without
    /// a registered plan here cannot schedule a deferred verification at all (the runner
    /// probes this at schedule time), so a pending entry can never become unresolvable by
    /// construction — only by a template being unregistered later, which resolves as failure.
    /// </summary>
    public static class DeferredVerifyPlan
    {
        public static bool TryRenderVerifySql(
            RemediationTemplate template,
            IReadOnlyDictionary<string, string>? parameters,
            out string sql,
            out string error)
        {
            sql = string.Empty;
            error = string.Empty;

            switch (template.Key)
            {
                case "INSTALLMAINTENANCESOLUTION":
                    // Only the schedule-lane sub-operation has a time-deferred effect (did the
                    // created job RUN successfully?). The proc install verifies fully at apply
                    // time, so it deliberately renders no deferred plan.
                    if (!MaintenanceSolutionOpRenderer.TryResolveAction(
                            parameters ?? new Dictionary<string, string>(), out _, out var lane, out var actionError))
                    {
                        error = actionError;
                        return false;
                    }
                    if (lane is null)
                    {
                        error = "Install-only apply has no deferred verification (nothing scheduled to run).";
                        return false;
                    }
                    // step_id = 0 is the job-outcome row; run_status = 1 is Succeeded.
                    string jobLiteral = "N'" + MaintenanceSolutionOpRenderer.JobNameFor(lane.Value).Replace("'", "''") + "'";
                    sql = "SELECT CASE WHEN EXISTS (SELECT 1 FROM msdb.dbo.sysjobs j " +
                          "JOIN msdb.dbo.sysjobhistory h ON h.job_id = j.job_id AND h.step_id = 0 AND h.run_status = 1 " +
                          $"WHERE j.name = {jobLiteral}) THEN 1 ELSE 0 END";
                    return true;

                default:
                    error = $"No deferred-verify plan registered for template '{template.Key}'.";
                    return false;
            }
        }
    }

    /// <summary>
    /// Lists pending deferred verifications for a server and runs them on demand.
    /// Persistence is the HMAC-chained audit ledger exclusively.
    /// </summary>
    public sealed class DeferredVerificationService
    {
        // Matches AuditLogService.RemediationHistoryLookbackDays: bounded scan, exceeds retention.
        private const int LookbackDays = 366;
        private const int VerifyTimeoutSeconds = 30;

        private readonly AuditLogService _audit;
        private readonly RemediationTemplateStore _templates;
        private readonly IServerConnectionManager _connections;
        private readonly ILogger<DeferredVerificationService> _logger;

        public DeferredVerificationService(
            AuditLogService audit,
            RemediationTemplateStore templates,
            IServerConnectionManager connections,
            ILogger<DeferredVerificationService> logger)
        {
            _audit = audit;
            _templates = templates;
            _connections = connections;
            _logger = logger;
        }

        /// <summary>
        /// Pending = a RemediationVerifyScheduled entry with no RemediationVerifyResolved entry
        /// at-or-after it for the same (template, server, parameters). Newest schedule per
        /// identity wins (re-applying the same fix restarts its verification window).
        /// </summary>
        public List<PendingVerification> GetPending(string serverName)
        {
            if (string.IsNullOrWhiteSpace(serverName)) return new List<PendingVerification>();
            try
            {
                var from = DateTime.UtcNow.AddDays(-LookbackDays);
                var to = DateTime.UtcNow.AddDays(1);
                return ComputePending(serverName,
                    _audit.GetEntries(from, to, AuditEventType.RemediationVerifyScheduled),
                    _audit.GetEntries(from, to, AuditEventType.RemediationVerifyResolved));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read pending deferred verifications for {Server}", serverName);
                return new List<PendingVerification>();
            }
        }

        /// <summary>
        /// Pure pairing core (unit-tested directly). Both lists are NEWEST-FIRST, matching the
        /// GetEntries contract.
        /// </summary>
        internal static List<PendingVerification> ComputePending(
            string serverName,
            IReadOnlyList<AuditLogEntry> scheduledNewestFirst,
            IReadOnlyList<AuditLogEntry> resolvedNewestFirst)
        {
            var pending = new List<PendingVerification>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in scheduledNewestFirst)
            {
                if (s.Details is null) continue;
                if (!string.Equals(Get(s.Details, "ServerName"), serverName, StringComparison.OrdinalIgnoreCase))
                    continue;

                string key = Get(s.Details, "TemplateKey");
                string paramsJson = Get(s.Details, "ParametersJson");
                string identity = key + "\n" + serverName.ToUpperInvariant() + "\n" + paramsJson;

                // Newest-first: the first schedule seen per identity is the live one; a re-apply
                // of the same fix restarts (supersedes) its verification window.
                if (!seen.Add(identity)) continue;

                bool isResolved = resolvedNewestFirst.Any(r =>
                    r.Details is not null
                    && r.Timestamp >= s.Timestamp
                    && string.Equals(Get(r.Details, "TemplateKey"), key, StringComparison.Ordinal)
                    && string.Equals(Get(r.Details, "ServerName"), serverName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Get(r.Details, "ParametersJson"), paramsJson, StringComparison.Ordinal));
                if (isResolved) continue;

                if (!DateTime.TryParse(Get(s.Details, "VerifyByUtc"), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var verifyBy))
                    continue; // unreadable deadline: skip rather than invent one

                pending.Add(new PendingVerification(
                    key, serverName, s.Timestamp, verifyBy,
                    Get(s.Details, "Description"), paramsJson));
            }
            return pending;
        }

        /// <summary>
        /// Runs one pending verification now. Passed / FailedOverdue write a resolution to the
        /// ledger (and flush); StillPending / CouldNotCheck leave it pending.
        /// </summary>
        public async Task<DeferredVerifyRunResult> VerifyNowAsync(PendingVerification p, CancellationToken ct = default)
        {
            var template = _templates.TryGet(p.TemplateKey);
            if (template is null)
            {
                // The plan cannot be re-rendered without its registered template — resolve as
                // failed (loud) rather than leaving a forever-pending entry nobody can act on.
                _audit.LogRemediationVerifyResolved(p.TemplateKey, p.ServerName, p.ParametersJson,
                    passed: false, $"Template '{p.TemplateKey}' is no longer registered; deferred verification unresolvable.");
                _audit.Flush();
                return new DeferredVerifyRunResult(DeferredVerifyStatus.FailedOverdue,
                    "Template no longer registered — resolved as failed.");
            }

            IReadOnlyDictionary<string, string>? parameters = DeserializeParams(p.ParametersJson);
            if (!DeferredVerifyPlan.TryRenderVerifySql(template, parameters, out var sql, out var renderError))
            {
                _audit.LogRemediationVerifyResolved(p.TemplateKey, p.ServerName, p.ParametersJson,
                    passed: false, $"Deferred verify unrenderable: {renderError}");
                _audit.Flush();
                return new DeferredVerifyRunResult(DeferredVerifyStatus.FailedOverdue,
                    $"Could not re-render the verification — resolved as failed. {renderError}");
            }

            string? connString = ResolveConnectionString(p.ServerName);
            if (connString is null)
                return new DeferredVerifyRunResult(DeferredVerifyStatus.CouldNotCheck,
                    $"No connection registered for '{p.ServerName}' — still pending.");

            string scalar;
            try
            {
                using var conn = new SqlConnection(connString);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using var cmd = new SqlCommand(sql, conn) { CommandTimeout = VerifyTimeoutSeconds };
                var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                scalar = result?.ToString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                // A broken check must never stamp the verification failed (fail-safe pending).
                _logger.LogWarning(ex, "Deferred verify probe failed for {Template} on {Server}", p.TemplateKey, p.ServerName);
                return new DeferredVerifyRunResult(DeferredVerifyStatus.CouldNotCheck,
                    $"Could not run the verification probe ({ex.Message}) — still pending.");
            }

            if (scalar == "1")
            {
                _audit.LogRemediationVerifyResolved(p.TemplateKey, p.ServerName, p.ParametersJson,
                    passed: true, p.Description);
                _audit.Flush();
                return new DeferredVerifyRunResult(DeferredVerifyStatus.Passed, "Verified — effect confirmed.");
            }

            if (p.IsOverdue(DateTime.UtcNow))
            {
                _audit.LogRemediationVerifyResolved(p.TemplateKey, p.ServerName, p.ParametersJson,
                    passed: false, $"Not confirmed by the {p.VerifyByUtc:yyyy-MM-dd} deadline. {p.Description}");
                _audit.Flush();
                return new DeferredVerifyRunResult(DeferredVerifyStatus.FailedOverdue,
                    $"Deadline {p.VerifyByUtc:yyyy-MM-dd} passed without confirmation — resolved as failed.");
            }

            return new DeferredVerifyRunResult(DeferredVerifyStatus.StillPending,
                $"Not confirmed yet; will keep verifying until {p.VerifyByUtc:yyyy-MM-dd}.");
        }

        public static string SerializeParams(IReadOnlyDictionary<string, string>? parameters)
            => parameters is null or { Count: 0 }
                ? string.Empty
                : JsonSerializer.Serialize(parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .ToDictionary(kv => kv.Key, kv => kv.Value));

        private static IReadOnlyDictionary<string, string>? DeserializeParams(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
            catch { return null; }
        }

        private string? ResolveConnectionString(string serverName)
        {
            try
            {
                var conn = _connections.GetConnection(serverName)
                           ?? _connections.GetConnections().Find(c => c.GetServerList().Exists(s =>
                                string.Equals(s, serverName, StringComparison.OrdinalIgnoreCase)));
                return conn?.GetConnectionString(serverName, "master");
            }
            catch { return null; }
        }

        private static string Get(IReadOnlyDictionary<string, string> d, string key)
            => d.TryGetValue(key, out var v) ? v : string.Empty;
    }
}
