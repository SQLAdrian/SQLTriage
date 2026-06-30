/* In the name of God, the Merciful, the Compassionate */
/*
 * DbatoolsRemediationExecutor — build step 5. The real IRemediationExecutor:
 * gate-4 preview via dbatools -WhatIf, gate-5 execution via dbatools (the change)
 * plus a SqlConnection for the read-only snapshot + verify.
 *
 * Reachable ONLY through RemediationRunner (the runner is its only caller). This
 * type performs NO gate checks itself — the runner has already cleared all five
 * by the time ExecuteAsync runs. It is the privileged machinery, not a gate.
 *
 * Rollback strategy (per template Kind):
 *   Configuration (MAXDOP): sp_configure/RECONFIGURE cannot run in a user
 *     transaction, so we snapshot the old value first; rollback = re-apply it.
 *   Transactable / CreateIndex (add-missing-index): CREATE INDEX is its own atomic
 *     unit (a failed CREATE leaves no partial index), so this path does NOT wrap in
 *     BEGIN TRAN — rollback is the clean inverse DDL (DROP INDEX the index we created)
 *     on a verify failure. The XACT_ABORT/BEGIN TRAN wrap is reserved for a future
 *     non-atomic Transactable op-kind, should one be added.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services.Remediation
{
    public sealed class DbatoolsRemediationExecutor : IRemediationExecutor
    {
        private readonly PowerShellService _powerShell;
        private readonly IServerConnectionManager _connections;
        private readonly AuditLogService _audit;
        private readonly ILogger<DbatoolsRemediationExecutor> _logger;

        // SQL error numbers that mean "not allowed" (non-transient) — drive back-off.
        private static readonly HashSet<int> PermissionDeniedErrors = new() { 229, 297, 300 };

        public DbatoolsRemediationExecutor(
            PowerShellService powerShell,
            IServerConnectionManager connections,
            AuditLogService audit,
            ILogger<DbatoolsRemediationExecutor> logger)
        {
            _powerShell = powerShell;
            _connections = connections;
            _audit = audit;
            _logger = logger;
        }

        /// <summary>
        /// Audit-writability probe. The runner refuses to execute if this is false:
        /// for a compliance product a silently-not-logging apply is the nightmare.
        /// </summary>
        public bool CanWriteAudit() => _audit.CanWrite;

        // ── Gate 4: preview ─────────────────────────────────────────────────

        public async Task<RemediationPreview> PreviewAsync(RemediationRequest request, CancellationToken ct = default)
        {
            // CreateIndex: show the exact CREATE INDEX DDL that would run (+ whether it already exists).
            if (request.Template.Operation?.OpKind == RemediationOpKind.CreateIndex)
                return await PreviewCreateIndexAsync(request, ct).ConfigureAwait(false);

            // Configuration templates render the EXACT T-SQL that will run and show the
            // current value alongside the target — the preview is the change, not an opaque
            // -WhatIf. This is the same rendering the runner's gate classified.
            if (request.Template.Kind == RemediationKind.Configuration)
                return await PreviewConfigurationAsync(request, ct).ConfigureAwait(false);

            // Other (dbatools-applied) kinds: -WhatIf preview.
            if (!_powerShell.IsDbatoolsAvailable)
                return new RemediationPreview { Succeeded = false, Error = "dbatools module is not available." };

            var command = BuildDbatoolsCommand(request, whatIf: true, out var paramError);
            if (paramError != null)
                return new RemediationPreview { Succeeded = false, Error = paramError };

            var result = await _powerShell.ExecuteAsTextAsync(command, importDbatools: true, cancellationToken: ct)
                                          .ConfigureAwait(false);

            return new RemediationPreview
            {
                Succeeded = result.Success,
                WhatIfText = string.IsNullOrWhiteSpace(result.Output)
                    ? $"Would run: {request.Template.DisplayName} on {request.ServerName}"
                    : result.Output.Trim(),
                Error = result.Success ? null : (result.Error ?? "Preview failed.")
            };
        }

        private async Task<RemediationPreview> PreviewConfigurationAsync(RemediationRequest request, CancellationToken ct)
        {
            var t = request.Template;
            if (t.Operation is null)
                return new RemediationPreview { Succeeded = false, Error = "Configuration template carries no structured operation to preview." };

            if (!RemediationOpRenderer.TryResolveValue(t.Operation, request.Parameters, out var target, out var valueError))
                return new RemediationPreview { Succeeded = false, Error = valueError };
            if (!RemediationOpRenderer.TryRender(t.Operation, target, out var applySql, out var renderError))
                return new RemediationPreview { Succeeded = false, Error = renderError };

            // Best-effort read of the current value so the preview shows "current -> target".
            // The read is DERIVED from the op (single-statement) — never free-form template text.
            string? current = null;
            var connString = ResolveConnectionString(request.ServerName);
            if (connString != null && RemediationOpRenderer.TryRenderRead(t.Operation, out var readSql, out _))
            {
                try { current = await ScalarAsync(connString, readSql, ct).ConfigureAwait(false); }
                catch { /* preview is best-effort; the apply path reports any read failure authoritatively */ }
            }

            var text =
                $"Would run on {request.ServerName}:\n{applySql}\n\n" +
                $"Current '{t.Operation.ConfigName}' = {current ?? "(unread)"}; target = {target}.";
            return new RemediationPreview { Succeeded = true, WhatIfText = text };
        }

        private async Task<RemediationPreview> PreviewCreateIndexAsync(RemediationRequest request, CancellationToken ct)
        {
            var p = request.Parameters;
            if (!RemediationOpRenderer.TryRenderCreateIndex(p, out var createSql, out var err))
                return new RemediationPreview { Succeeded = false, Error = err };

            // Best-effort: note if the index already exists (apply would be a no-op).
            var existsNote = string.Empty;
            if (RemediationOpRenderer.TryResolveIndexSpec(p, out var spec, out _))
            {
                var cs = ResolveConnectionString(request.ServerName, spec.Database);
                if (cs != null && RemediationOpRenderer.TryRenderIndexExistsRead(p, out var existsSql, out _))
                {
                    try
                    {
                        var e = await ScalarAsync(cs, existsSql, ct).ConfigureAwait(false);
                        if (string.Equals(e?.Trim(), "1", StringComparison.Ordinal))
                            existsNote = "\n\n(The index already exists — apply would be a no-op.)";
                    }
                    catch { /* preview is best-effort */ }
                }
            }
            return new RemediationPreview { Succeeded = true, WhatIfText = $"Would run on {request.ServerName}:\n{createSql}{existsNote}" };
        }

        // ── Gate 5: snapshot → apply → verify ───────────────────────────────

        public async Task<RemediationExecution> ExecuteAsync(RemediationRequest request, CancellationToken ct = default)
        {
            // CreateIndex resolves its OWN connection (to the target database, not master) and
            // renders fully-guarded DDL from request parameters. The runner's gate has already
            // classified the representative CREATE INDEX as Remediation under ADDMISSINGINDEX.
            if (request.Template.Operation?.OpKind == RemediationOpKind.CreateIndex)
                return await ExecuteCreateIndexAsync(request, ct).ConfigureAwait(false);

            var connString = ResolveConnectionString(request.ServerName);
            if (connString == null)
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.CouldNotRun,
                    Error = $"No connection registered for '{request.ServerName}'."
                };

            // Configuration (structured-op) templates: the change is rendered T-SQL — the EXACT
            // text the gate classified — and its snapshot/verify reads are DERIVED from the op,
            // never from free-form template text (so a template can't smuggle a write through
            // the read path). Other kinds keep the dbatools apply path.
            return request.Template.Kind == RemediationKind.Configuration
                ? await ExecuteConfigurationAsync(connString, request, ct).ConfigureAwait(false)
                : await ExecuteViaDbatoolsAsync(connString, request, ct).ConfigureAwait(false);
        }

        // ── CreateIndex kind: render guarded DDL, run on the target DB, verify, DROP-on-failure ──
        private async Task<RemediationExecution> ExecuteCreateIndexAsync(RemediationRequest request, CancellationToken ct)
        {
            var p = request.Parameters;
            if (!RemediationOpRenderer.TryResolveIndexSpec(p, out var spec, out var specErr))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = specErr };

            // Index DDL runs in the TARGET database (not master). The database name passed the
            // identifier guard, so it is safe in the connection string's Initial Catalog.
            var connString = ResolveConnectionString(request.ServerName, spec.Database);
            if (connString == null)
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = $"No connection registered for '{request.ServerName}'." };

            if (!RemediationOpRenderer.TryRenderIndexExistsRead(p, out var existsSql, out var readErr))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = readErr };
            if (!RemediationOpRenderer.TryRenderCreateIndex(p, out var createSql, out var createErr))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = createErr };

            // 1) Snapshot: does the index already exist?
            string? before;
            try { before = await ScalarAsync(connString, existsSql, ct).ConfigureAwait(false); }
            catch (SqlException ex) { return PermsAwareFailure(ex, "Index existence snapshot failed"); }

            // 2) Already present? -> NoOp (no credit consumed).
            if (string.Equals(before?.Trim(), "1", StringComparison.Ordinal))
                return new RemediationExecution { Outcome = RemediationOutcome.NoOp };

            // 3) Apply CREATE INDEX. It auto-commits server-side, so a throw in the POST-COMMIT
            //    window (command timeout / network drop / cancel AFTER the server finished a long
            //    build) must NOT be reported as "could not run" while a real index now exists —
            //    that would refund a credit for a stuck change and put a false "nothing changed"
            //    in the audit ledger. On any throw, consult the exists-read to decide the truth.
            try { await ExecuteNonQueryAsync(connString, createSql, ct).ConfigureAwait(false); }
            catch (Exception applyEx)
            {
                bool committed = false;
                try { committed = string.Equals((await ScalarAsync(connString, existsSql, ct).ConfigureAwait(false))?.Trim(), "1", StringComparison.Ordinal); }
                catch { /* couldn't confirm — fall through to the throw-based outcome */ }
                if (committed)
                    return new RemediationExecution { Outcome = RemediationOutcome.AppliedVerified };
                if (applyEx is OperationCanceledException) throw; // surface cancellation, don't downgrade
                if (applyEx is SqlException sqlEx) return PermsAwareFailure(sqlEx, "Create index failed");
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = $"Create index failed: {applyEx.Message}" };
            }

            // 4) Verify it now exists.
            string? after;
            try { after = await ScalarAsync(connString, existsSql, ct).ConfigureAwait(false); }
            catch (SqlException ex)
            {
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.AppliedVerifyFailed,
                    Error = $"Verify read failed: {ex.Message}",
                    IsPermissionDenied = PermissionDeniedErrors.Contains(ex.Number)
                };
            }
            if (string.Equals(after?.Trim(), "1", StringComparison.Ordinal))
                return new RemediationExecution { Outcome = RemediationOutcome.AppliedVerified };

            // 5) Verify failed (created but not found) — DROP the index we created (clean inverse).
            var verifyError = "Post-create verify did not find the index.";
            if (request.Template.Reversible && RemediationOpRenderer.TryRenderDropIndex(p, out var dropSql, out _))
            {
                // The DROP is the rollback ACTION. If IT throws, the rollback genuinely failed.
                try { await ExecuteNonQueryAsync(connString, dropSql, ct).ConfigureAwait(false); }
                catch (SqlException ex)
                {
                    return new RemediationExecution
                    {
                        Outcome = RemediationOutcome.AppliedVerifyFailed,
                        Error = verifyError,
                        RolledBack = true,
                        RollbackSucceeded = false,
                        RollbackError = $"Rollback failed: {ex.Message}",
                        IsPermissionDenied = PermissionDeniedErrors.Contains(ex.Number)
                    };
                }
                // DROP completed. Confirm best-effort — a thrown confirm-read does NOT flip the
                // rollback to "failed" (which would wrongly commit a credit for a reverted change):
                // the rollback action itself succeeded.
                bool ok = true;
                try { ok = string.Equals((await ScalarAsync(connString, existsSql, ct).ConfigureAwait(false))?.Trim(), "0", StringComparison.Ordinal); }
                catch { /* DROP succeeded; confirm read unavailable — treat as rolled back */ }
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.AppliedVerifyFailed,
                    Error = verifyError,
                    RolledBack = true,
                    RollbackSucceeded = ok,
                    RollbackError = ok ? null : "Rollback DROP INDEX did not remove the index."
                };
            }
            return new RemediationExecution { Outcome = RemediationOutcome.AppliedVerifyFailed, Error = verifyError };
        }

        // ── Configuration kind: render the op to T-SQL, run it, verify, roll back ──
        private async Task<RemediationExecution> ExecuteConfigurationAsync(
            string connString, RemediationRequest request, CancellationToken ct)
        {
            var t = request.Template;
            if (t.Operation is null)
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = "Configuration template carries no structured operation." };

            // Resolve + bounds-check the value, then render the apply T-SQL (the exact text
            // the runner's gate classified — same renderer, same op) AND the read query
            // (DERIVED from the op — single-statement, never free-form template text).
            if (!RemediationOpRenderer.TryResolveValue(t.Operation, request.Parameters, out var target, out var valueError))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = valueError };
            if (!RemediationOpRenderer.TryRender(t.Operation, target, out var applySql, out var renderError))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = renderError };
            if (!RemediationOpRenderer.TryRenderRead(t.Operation, out var readSql, out var readError))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = readError };

            // 1) Snapshot pre-change value (read-only; the rollback target).
            string? snapshot;
            try { snapshot = await ScalarAsync(connString, readSql, ct).ConfigureAwait(false); }
            catch (SqlException ex) { return PermsAwareFailure(ex, "Snapshot read failed"); }

            int? oldValue = TryParseInt(snapshot);

            // 2) Already compliant? -> NoOp (nothing to do, no credit consumed).
            if (oldValue == target)
                return new RemediationExecution { Outcome = RemediationOutcome.NoOp };

            // 3) Apply the rendered change (for real).
            try { await ExecuteNonQueryAsync(connString, applySql, ct).ConfigureAwait(false); }
            catch (SqlException ex) { return PermsAwareFailure(ex, "Apply failed"); }

            // 4) Verify post-change state.
            string? post;
            try { post = await ScalarAsync(connString, readSql, ct).ConfigureAwait(false); }
            catch (SqlException ex)
            {
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.AppliedVerifyFailed,
                    Error = $"Verify read failed: {ex.Message}",
                    IsPermissionDenied = PermissionDeniedErrors.Contains(ex.Number)
                };
            }

            if (TryParseInt(post) == target)
                return new RemediationExecution { Outcome = RemediationOutcome.AppliedVerified, PreChangeValue = oldValue };

            // 5) Verify failed: the change ran but didn't take. Snapshot-based rollback —
            //    re-apply the captured pre-change value so the server is never left in a
            //    half-changed state. The runner ledgers the rollback outcome.
            var verifyError = $"Post-change verify expected {target} but read '{post}'.";
            if (t.Reversible && oldValue.HasValue
                && RemediationOpRenderer.TryRender(t.Operation, oldValue.Value, out var rollbackSql, out _))
            {
                try
                {
                    await ExecuteNonQueryAsync(connString, rollbackSql, ct).ConfigureAwait(false);
                    var restored = await ScalarAsync(connString, readSql, ct).ConfigureAwait(false);
                    bool restoredOk = TryParseInt(restored) == oldValue.Value;
                    return new RemediationExecution
                    {
                        Outcome = RemediationOutcome.AppliedVerifyFailed,
                        Error = verifyError,
                        RolledBack = true,
                        RollbackSucceeded = restoredOk,
                        RollbackError = restoredOk ? null : $"Rollback re-read '{restored}', expected {oldValue.Value}."
                    };
                }
                catch (SqlException ex)
                {
                    return new RemediationExecution
                    {
                        Outcome = RemediationOutcome.AppliedVerifyFailed,
                        Error = verifyError,
                        RolledBack = true,
                        RollbackSucceeded = false,
                        RollbackError = $"Rollback failed: {ex.Message}",
                        IsPermissionDenied = PermissionDeniedErrors.Contains(ex.Number)
                    };
                }
            }

            return new RemediationExecution { Outcome = RemediationOutcome.AppliedVerifyFailed, Error = verifyError };
        }

        // ── Other kinds: dbatools apply (retained for future Transactable templates) ──
        private async Task<RemediationExecution> ExecuteViaDbatoolsAsync(
            string connString, RemediationRequest request, CancellationToken ct)
        {
            // This path runs the template's FREE-FORM snapshot/verify SQL (no op to derive
            // from), so validate them read-only here. (The Configuration path derives its reads
            // from the structured op instead — see RemediationOpRenderer.TryRenderRead.)
            foreach (var (label, q) in new[] { ("snapshot", request.Template.SnapshotQuery), ("verify", request.Template.VerifyQuery) })
            {
                if (string.IsNullOrWhiteSpace(q))
                {
                    _logger.LogError("Remediation refused: template '{Key}' has no {Label} query.", request.Template.Key, label);
                    return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = $"Refused: the template has no {label} query." };
                }
                if (!SqlSafetyValidator.Validate(q).IsSafe)
                {
                    _logger.LogError("Remediation refused: template '{Key}' {Label} query is not read-only safe.", request.Template.Key, label);
                    return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = $"Refused: the template's {label} query is not read-only safe." };
                }
            }

            // 1) Snapshot pre-change state (read-only; the rollback target).
            string? snapshot;
            try
            {
                snapshot = await ScalarAsync(connString, request.Template.SnapshotQuery, ct).ConfigureAwait(false);
            }
            catch (SqlException ex)
            {
                return PermsAwareFailure(ex, "Snapshot read failed");
            }

            // 2) Already compliant? -> NoOp (nothing to do, no credit consumed).
            if (TargetMatchesSnapshot(request, snapshot))
                return new RemediationExecution { Outcome = RemediationOutcome.NoOp };

            // 3) Apply the change via dbatools (for real — no -WhatIf).
            var command = BuildDbatoolsCommand(request, whatIf: false, out var paramError);
            if (paramError != null)
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = paramError };

            var applyResult = await _powerShell.ExecuteAsTextAsync(command, importDbatools: true, cancellationToken: ct)
                                               .ConfigureAwait(false);
            if (!applyResult.Success)
            {
                // dbatools surfaces a permissions failure in its error text.
                bool perms = LooksLikePermissionDenied(applyResult.Error);
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.CouldNotRun,
                    Error = applyResult.Error ?? "dbatools change failed.",
                    IsPermissionDenied = perms
                };
            }

            // 4) Verify post-change state.
            try
            {
                var post = await ScalarAsync(connString, request.Template.VerifyQuery, ct).ConfigureAwait(false);
                bool verified = VerifyMatchesTarget(request, post);
                return new RemediationExecution
                {
                    Outcome = verified ? RemediationOutcome.AppliedVerified : RemediationOutcome.AppliedVerifyFailed,
                    Error = verified ? null : $"Post-change verify expected target but read '{post}'."
                };
            }
            catch (SqlException ex)
            {
                // The change ran but we couldn't confirm it — NOT a clean success.
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.AppliedVerifyFailed,
                    Error = $"Verify read failed: {ex.Message}",
                    IsPermissionDenied = PermissionDeniedErrors.Contains(ex.Number)
                };
            }
        }

        private static int? TryParseInt(string? s) =>
            int.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (int?)null;

        // ── Helpers ─────────────────────────────────────────────────────────

        private string? ResolveConnectionString(string serverNameOrId)
        {
            // Accept either a registered connection id or a server name.
            var conn = _connections.GetConnection(serverNameOrId)
                       ?? _connections.GetConnections()
                            .Find(c => c.GetServerList().Exists(s =>
                                string.Equals(s, serverNameOrId, StringComparison.OrdinalIgnoreCase)));
            if (conn == null) return null;

            var servers = conn.GetServerList();
            var server = servers.Count > 0 ? servers[0] : serverNameOrId;
            return conn.GetConnectionString(server, "master");
        }

        // Overload: connect to a SPECIFIC database (CreateIndex runs in the target DB, not master).
        // The database name is a guarded identifier (passed RemediationOpRenderer's SafeIdentifier),
        // so it is safe as the connection string's Initial Catalog.
        private string? ResolveConnectionString(string serverNameOrId, string database)
        {
            var conn = _connections.GetConnection(serverNameOrId)
                       ?? _connections.GetConnections()
                            .Find(c => c.GetServerList().Exists(s =>
                                string.Equals(s, serverNameOrId, StringComparison.OrdinalIgnoreCase)));
            if (conn == null) return null;

            var servers = conn.GetServerList();
            var server = servers.Count > 0 ? servers[0] : serverNameOrId;
            return conn.GetConnectionString(server, string.IsNullOrWhiteSpace(database) ? "master" : database);
        }

        private static async Task<string?> ScalarAsync(string connString, string query, CancellationToken ct)
        {
            using var conn = new SqlConnection(connString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new SqlCommand(query, conn) { CommandTimeout = 30 };
            var val = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return val?.ToString();
        }

        // Executes the rendered remediation T-SQL (the exact text the gate classified).
        // Used by the Configuration path only — sp_configure/RECONFIGURE can't run inside a
        // user transaction, so this is a plain batch; rollback is snapshot-based (re-apply).
        private static async Task ExecuteNonQueryAsync(string connString, string sql, CancellationToken ct)
        {
            using var conn = new SqlConnection(connString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Builds the dbatools command line for a template + params. Internal so
        /// tests can exercise command construction without a live server
        /// (InternalsVisibleTo SQLTriage.Tests).
        /// </summary>
        internal static string BuildDbatoolsCommand(RemediationRequest request, bool whatIf, out string? error)
        {
            error = null;
            var t = request.Template;

            // MAXDOP needs a target value. Generic enough to extend per template.
            if (string.Equals(t.DbatoolsCommand, "Set-DbaMaxDop", StringComparison.OrdinalIgnoreCase))
            {
                if (!request.Parameters.TryGetValue("MaxDop", out var maxDopRaw)
                    || !int.TryParse(maxDopRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxDop))
                {
                    error = "MAXDOP remediation requires an integer 'MaxDop' parameter.";
                    return string.Empty;
                }
                var sb = new System.Text.StringBuilder();
                sb.Append($"{t.DbatoolsCommand} -SqlInstance '{Escape(request.ServerName)}' -MaxDop {maxDop} -EnableException -Confirm:$false");
                if (whatIf) sb.Append(" -WhatIf");
                return sb.ToString();
            }

            // Default shape: command -SqlInstance <server> [-WhatIf]. Extend as
            // more templates land (each with its own validated parameters).
            var generic = $"{t.DbatoolsCommand} -SqlInstance '{Escape(request.ServerName)}' -EnableException -Confirm:$false";
            return whatIf ? generic + " -WhatIf" : generic;
        }

        private static string Escape(string s) => (s ?? string.Empty).Replace("'", "''");

        private static bool TargetMatchesSnapshot(RemediationRequest request, string? snapshot)
        {
            // NOTE: only the dbatools else-branch (ExecuteViaDbatoolsAsync) calls this, and that
            // branch is currently UNREACHABLE through the runner — GatePassesTemplate fails closed
            // on any non-Configuration kind. Retained as scaffolding for a future Transactable
            // template. The Configuration path does its own value_in_use scalar comparison.
            if (!request.Parameters.TryGetValue("MaxDop", out var target)) return false;
            return string.Equals(snapshot?.Trim(), target?.Trim(), StringComparison.Ordinal);
        }

        private static bool VerifyMatchesTarget(RemediationRequest request, string? post)
        {
            if (!request.Parameters.TryGetValue("MaxDop", out var target)) return false;
            return string.Equals(post?.Trim(), target?.Trim(), StringComparison.Ordinal);
        }

        private RemediationExecution PermsAwareFailure(SqlException ex, string stage)
        {
            bool perms = PermissionDeniedErrors.Contains(ex.Number);
            _logger.LogWarning(ex, "{Stage} (SQL {Number})", stage, ex.Number);
            return new RemediationExecution
            {
                Outcome = RemediationOutcome.CouldNotRun,
                Error = $"{stage}: {ex.Message}",
                IsPermissionDenied = perms
            };
        }

        private static bool LooksLikePermissionDenied(string? errorText)
        {
            if (string.IsNullOrEmpty(errorText)) return false;
            return errorText.Contains("permission", StringComparison.OrdinalIgnoreCase)
                || errorText.Contains("denied", StringComparison.OrdinalIgnoreCase)
                || errorText.Contains("Msg 229", StringComparison.OrdinalIgnoreCase)
                || errorText.Contains("Msg 297", StringComparison.OrdinalIgnoreCase)
                || errorText.Contains("Msg 300", StringComparison.OrdinalIgnoreCase);
        }
    }
}
