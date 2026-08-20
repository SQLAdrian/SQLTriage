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
using System.Linq;
using System.Text;
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
        private readonly DiskIoService _diskIo;
        private readonly ILogger<DbatoolsRemediationExecutor> _logger;

        // SQL error numbers that mean "not allowed" (non-transient) — drive back-off.
        private static readonly HashSet<int> PermissionDeniedErrors = new() { 229, 297, 300 };

        // Optional per the project's DI convention: only the Agent-job sync path needs it, and
        // leaving it nullable keeps every existing construction of this executor compiling.
        private readonly Jobs.JobInventoryService? _jobs;

        public DbatoolsRemediationExecutor(
            PowerShellService powerShell,
            IServerConnectionManager connections,
            AuditLogService audit,
            DiskIoService diskIo,
            ILogger<DbatoolsRemediationExecutor> logger,
            Jobs.JobInventoryService? jobs = null)
        {
            _powerShell = powerShell;
            _connections = connections;
            _audit = audit;
            _diskIo = diskIo;
            _logger = logger;
            _jobs = jobs;
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

            // AgentAlertPack: show the exact idempotent batch that would run (+ Agent availability).
            if (request.Template.Operation?.OpKind == RemediationOpKind.AgentAlertPack)
                return await PreviewAgentAlertPackAsync(request, ct).ConfigureAwait(false);

            // InstallMaintenanceSolution: show the install-or-noop decision, or (for a schedule
            // tickbox) the exact idempotent job-create batch.
            if (request.Template.Operation?.OpKind == RemediationOpKind.InstallMaintenanceSolution)
                return await PreviewMaintenanceSolutionAsync(request, ct).ConfigureAwait(false);

            // Lane S5 — Backup-NOW / CHECKDB-NOW: the preview IS the gate report. It renders
            // the exact statement, runs the resource-gate math, and surfaces size + duration
            // hint + confirm-token status so the human approves an INFORMED decision — never a
            // blind "yes". A preview that would refuse at apply time says so here too.
            if (request.Template.Operation?.OpKind == RemediationOpKind.BackupDatabaseNow)
                return await PreviewBackupDatabaseNowAsync(request, ct).ConfigureAwait(false);
            if (request.Template.Operation?.OpKind == RemediationOpKind.CheckDbNow)
                return await PreviewCheckDbNowAsync(request, ct).ConfigureAwait(false);

            // DbSetOption: preview the per-database ALTER DATABASE ... SET batch — the offending
            // database LIST comes from offenders_query (read-only), not the ConfigName/value_param
            // shape PreviewConfigurationAsync below assumes, so it must be checked first.
            if (request.Template.Operation?.OpKind == RemediationOpKind.DbSetOption)
                return await PreviewDbSetOptionAsync(request, ct).ConfigureAwait(false);

            // AgentJobPrimaryGuard: show the exact step-injection batch, and say up front when
            // the job is already guarded (apply would be a no-op) so nobody spends an approval
            // discovering that.
            if (request.Template.Operation?.OpKind == RemediationOpKind.AgentJobPrimaryGuard)
                return await PreviewAgPrimaryGuardAsync(request, ct).ConfigureAwait(false);

            // AgentJobSync / AgentJobDeleteExtra: show the exact batch, plus the drop-and-recreate
            // warning — the lost run history must be known BEFORE approval, not discovered after.
            if (request.Template.Operation?.OpKind == RemediationOpKind.AgentJobSync)
                return await PreviewAgentJobSyncAsync(request, ct).ConfigureAwait(false);
            if (request.Template.Operation?.OpKind == RemediationOpKind.AgentJobDeleteExtra)
            {
                request.Parameters!.TryGetValue(AgentJobSyncOpRenderer.JobNameParam, out var delName);
                return AgentJobSyncOpRenderer.TryRenderDeleteExtraSql(delName, out var delSql, out var delErr)
                    ? new RemediationPreview { Succeeded = true, WhatIfText = "-- NOT REVERSIBLE: msdb keeps no copy of a deleted job.\n\n" + delSql }
                    : new RemediationPreview { Succeeded = false, Error = delErr };
            }

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

        // ── DbSetOption kind: per-database ALTER DATABASE ... SET, target list from offenders_query ──
        private async Task<RemediationPreview> PreviewDbSetOptionAsync(RemediationRequest request, CancellationToken ct)
        {
            var op = request.Template.Operation;
            if (op is null || string.IsNullOrWhiteSpace(op.OptionSql) || string.IsNullOrWhiteSpace(op.OffendersQuery))
                return new RemediationPreview { Succeeded = false, Error = "db_set_option template carries no option_sql/offenders_query to preview." };

            var connString = ResolveConnectionString(request.ServerName);
            if (connString == null)
                return new RemediationPreview { Succeeded = false, Error = $"No connection registered for '{request.ServerName}'." };

            HashSet<string> offenders;
            try { offenders = await RowsAsync(connString, op.OffendersQuery, ct).ConfigureAwait(false); }
            catch (SqlException ex) { return new RemediationPreview { Succeeded = false, Error = $"Offenders read failed: {ex.Message}" }; }

            if (offenders.Count == 0)
                return new RemediationPreview { Succeeded = true, WhatIfText = $"No offending databases on {request.ServerName} for '{op.OptionSql}' — apply would be a no-op." };

            var sb = new StringBuilder();
            sb.AppendLine($"Would run on {request.ServerName} ({offenders.Count} offending database(s)):");
            foreach (var db in offenders.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (RemediationOpRenderer.TryRenderDbSetOption(db, op.OptionSql, out var sql, out var err))
                    sb.AppendLine(sql);
                else
                    sb.AppendLine($"  (skipped — {err})");
            }
            return new RemediationPreview { Succeeded = true, WhatIfText = sb.ToString().TrimEnd() };
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

        private async Task<RemediationPreview> PreviewAgentAlertPackAsync(RemediationRequest request, CancellationToken ct)
        {
            var p = request.Parameters;
            var connString = ResolveConnectionString(request.ServerName);
            if (connString == null)
                return new RemediationPreview { Succeeded = false, Error = $"No connection registered for '{request.ServerName}'." };

            // Express (EngineEdition 4) has no SQL Server Agent — refuse the preview honestly
            // rather than show a batch that would never fire.
            try
            {
                var avail = await ScalarAsync(connString, RemediationOpRenderer.AgentAvailabilityProbe, ct).ConfigureAwait(false);
                if (string.Equals(avail?.Trim(), "0", StringComparison.Ordinal))
                    return new RemediationPreview { Succeeded = false, Error = "SQL Server Agent is not available on this edition (e.g. Express) — the alert pack cannot be applied here." };
            }
            catch { /* best-effort; the apply path re-checks authoritatively */ }

            if (!RemediationOpRenderer.TryRenderAgentAlertPackApply(p, out var applySql, out var err))
                return new RemediationPreview { Succeeded = false, Error = err };

            return new RemediationPreview { Succeeded = true, WhatIfText = $"Would run on {request.ServerName}:\n{applySql}" };
        }

        private async Task<RemediationPreview> PreviewMaintenanceSolutionAsync(RemediationRequest request, CancellationToken ct)
        {
            var p = request.Parameters;
            if (!MaintenanceSolutionOpRenderer.TryResolveAction(p, out _, out var lane, out var actionErr))
                return new RemediationPreview { Succeeded = false, Error = actionErr };

            var connString = ResolveConnectionString(request.ServerName);
            if (connString == null)
                return new RemediationPreview { Succeeded = false, Error = $"No connection registered for '{request.ServerName}'." };

            if (lane is null)
            {
                // Install path: show whether this is a real install or a no-op (already installed).
                string checksumNote;
                try { MaintenanceSolutionOpRenderer.LoadVerifiedScriptText(); checksumNote = "Embedded script checksum verified."; }
                catch (Exception ex) { return new RemediationPreview { Succeeded = false, Error = $"Refusing to preview: {ex.Message}" }; }

                string? alreadyInstalled;
                try { alreadyInstalled = await ScalarAsync(connString, MaintenanceSolutionOpRenderer.ProcsExistProbe, ct).ConfigureAwait(false); }
                catch (SqlException ex) { return new RemediationPreview { Succeeded = false, Error = $"Snapshot read failed: {ex.Message}" }; }

                if (string.Equals(alreadyInstalled?.Trim(), "1", StringComparison.Ordinal))
                    return new RemediationPreview
                    {
                        Succeeded = true,
                        WhatIfText = $"CommandExecute/DatabaseBackup/DatabaseIntegrityCheck/IndexOptimize already exist on {request.ServerName} — " +
                                     "apply would be a no-op (the Maintenance Solution script is never re-run over an existing install)."
                    };

                return new RemediationPreview
                {
                    Succeeded = true,
                    WhatIfText = $"Would install Ola Hallengren's Maintenance Solution on {request.ServerName} (master), @CreateJobs='N' " +
                                 "(no jobs — use the schedule tickboxes separately). " + checksumNote
                };
            }

            // Schedule-lane path: show the exact idempotent job-create batch (+ existence note).
            if (!MaintenanceSolutionOpRenderer.TryRenderScheduleCreateSql(lane.Value, p, out var createSql, out var renderErr))
                return new RemediationPreview { Succeeded = false, Error = renderErr };

            string existsNote = string.Empty;
            try
            {
                var exists = await ScalarAsync(connString, MaintenanceSolutionOpRenderer.RenderScheduleExistsSql(lane.Value), ct).ConfigureAwait(false);
                if (string.Equals(exists?.Trim(), "1", StringComparison.Ordinal))
                    existsNote = "\n\n(This job already exists — apply would be a no-op.)";
            }
            catch { /* preview is best-effort */ }

            return new RemediationPreview { Succeeded = true, WhatIfText = $"Would run on {request.ServerName}:\n{createSql}{existsNote}" };
        }

        // ── Lane S5: Backup-NOW / CHECKDB-NOW — shared resource-gate plumbing ────
        // Both ops share the same "find the DiskIoService drive for this database's data
        // volume" step; factored once so the preview and execute paths can never drift on
        // which drive they measure against.

        /// <summary>
        /// Resolves the DiskIoService drive backing a database's PRIMARY data file (the
        /// volume BACKUP/CHECKDB actually touch). Returns null (with a reason) if the
        /// server/database has no file inventory (e.g. connection failure, or a database
        /// DiskIoService's DMV read didn't cover — fails closed: no drive found means the
        /// resource gate cannot be evaluated, so the caller must refuse rather than assume
        /// "plenty of space").
        /// </summary>
        private async Task<(DiskIoService.DriveRow? drive, string? error)> ResolveDataDriveAsync(
            string serverName, string database, CancellationToken ct)
        {
            var conn = _connections.GetConnection(serverName)
                       ?? _connections.GetConnections().Find(c => c.GetServerList().Exists(s =>
                            string.Equals(s, serverName, StringComparison.OrdinalIgnoreCase)));
            if (conn == null) return (null, $"No connection registered for '{serverName}'.");
            var servers = conn.GetServerList();
            var server = servers.Count > 0 ? servers[0] : serverName;

            DiskIoService.Snapshot snapshot;
            try { snapshot = await _diskIo.GetSnapshotAsync(conn, server, windowSeconds: 1, ct: ct).ConfigureAwait(false); }
            catch (Exception ex) { return (null, $"Could not read disk/file inventory: {ex.Message}"); }

            var file = snapshot.Files.Find(f =>
                string.Equals(f.DatabaseName, database, StringComparison.OrdinalIgnoreCase)
                && string.Equals(f.TypeDesc, "ROWS", StringComparison.OrdinalIgnoreCase));
            if (file is null || string.IsNullOrEmpty(file.DriveLetter))
                return (null, $"Could not resolve the data-file drive for database '{database}' (no ROWS file inventory or non-drive-letter path).");

            var drive = snapshot.Drives.Find(d => string.Equals(d.DriveLetter, file.DriveLetter, StringComparison.OrdinalIgnoreCase));
            if (drive is null)
                return (null, $"Could not resolve free-space for drive '{file.DriveLetter}' (database '{database}').");

            return (drive, null);
        }

        private async Task<RemediationPreview> PreviewBackupDatabaseNowAsync(RemediationRequest request, CancellationToken ct)
        {
            var p = request.Parameters;
            if (!BackupCheckDbOpRenderer.TryResolveBackupSpec(p, out var spec, out var specErr))
                return new RemediationPreview { Succeeded = false, Error = specErr };

            var connString = ResolveConnectionString(request.ServerName, spec.Database);
            if (connString == null)
                return new RemediationPreview { Succeeded = false, Error = $"No connection registered for '{request.ServerName}'." };

            // Gate 1 (state): database must exist and be ONLINE.
            if (!BackupCheckDbOpRenderer.TryRenderDatabaseOnlineProbe(spec.Database, out var onlineSql, out var onlineErr))
                return new RemediationPreview { Succeeded = false, Error = onlineErr };
            string? online;
            try { online = await ScalarAsync(connString, onlineSql, ct).ConfigureAwait(false); }
            catch (SqlException ex) { return new RemediationPreview { Succeeded = false, Error = $"State probe failed: {ex.Message}" }; }
            if (!string.Equals(online?.Trim(), "1", StringComparison.Ordinal))
                return new RemediationPreview { Succeeded = false, Error = $"Database '{spec.Database}' does not exist or is not ONLINE — refusing to preview a backup." };

            // Gate 2 (resource): estimate size (run in the target DB) + the target backup drive's free space.
            long estimatedBytes;
            try { estimatedBytes = await ScalarLongAsync(connString, BackupCheckDbOpRenderer.EstimateBackupSizeBytesQuery, ct).ConfigureAwait(false); }
            catch (SqlException ex) { return new RemediationPreview { Succeeded = false, Error = $"Backup-size estimate failed: {ex.Message}" }; }

            var driveLetter = ExtractDriveLetter(spec.Directory);
            var (drive, driveErr) = await ResolveDataDriveAsync(request.ServerName, spec.Database, ct).ConfigureAwait(false);
            long? backupDriveFreeBytes = null;
            string driveLabel = string.IsNullOrEmpty(driveLetter) ? spec.Directory : driveLetter;
            if (!string.IsNullOrEmpty(driveLetter))
            {
                try
                {
                    var conn = _connections.GetConnection(request.ServerName)
                               ?? _connections.GetConnections().Find(c => c.GetServerList().Exists(s => string.Equals(s, request.ServerName, StringComparison.OrdinalIgnoreCase)));
                    if (conn != null)
                    {
                        var servers = conn.GetServerList();
                        var server = servers.Count > 0 ? servers[0] : request.ServerName;
                        var snap = await _diskIo.GetSnapshotAsync(conn, server, windowSeconds: 1, ct: ct).ConfigureAwait(false);
                        var targetDrive = snap.Drives.Find(d => string.Equals(d.DriveLetter, driveLetter, StringComparison.OrdinalIgnoreCase));
                        if (targetDrive != null) backupDriveFreeBytes = targetDrive.AvailableBytes;
                    }
                }
                catch { /* preview is best-effort on the drive lookup; apply re-evaluates authoritatively */ }
            }

            BackupCheckDbOpRenderer.ResourceGateResult gate = backupDriveFreeBytes.HasValue
                ? BackupCheckDbOpRenderer.EvaluateBackupResourceGate(estimatedBytes, backupDriveFreeBytes.Value, driveLabel)
                : new BackupCheckDbOpRenderer.ResourceGateResult { Allowed = false, Reason = $"Could not determine free space for the target backup directory's drive ('{driveLabel}') — refusing (fail closed). {driveErr}" };

            if (!BackupCheckDbOpRenderer.TryRenderBackupApply(p, out var applySql, out var renderErr))
                return new RemediationPreview { Succeeded = false, Error = renderErr };

            var confirmed = BackupCheckDbOpRenderer.IsConfirmed(p);
            var durationHint = BackupCheckDbOpRenderer.DurationHint(estimatedBytes, isCheckDb: false);
            var gb = estimatedBytes / 1024.0 / 1024.0 / 1024.0;

            var text =
                $"Would run on {request.ServerName}:\n{applySql}\n\n" +
                $"Database '{spec.Database}' is ~{gb:0.0} GB; backup typically {durationHint}; runs against the LIVE server.\n" +
                $"Resource gate: {gate.Reason}\n" +
                (confirmed ? "Confirmation token present." : "NO confirmation token — apply will be refused until BackupCheckDb.ConfirmLargeOperation=true is supplied.");

            // The preview always renders (so the human sees the exact statement + numbers),
            // but is marked failed when a hard gate would refuse the apply — never a silent
            // "looks fine" when it wouldn't actually run.
            bool wouldSucceed = gate.Allowed && confirmed;
            return new RemediationPreview { Succeeded = wouldSucceed, WhatIfText = text, Error = wouldSucceed ? null : "One or more gates would refuse this apply — see the gate detail above." };
        }

        private async Task<RemediationPreview> PreviewCheckDbNowAsync(RemediationRequest request, CancellationToken ct)
        {
            var p = request.Parameters;
            if (!BackupCheckDbOpRenderer.TryResolveCheckDbSpec(p, out var spec, out var specErr))
                return new RemediationPreview { Succeeded = false, Error = specErr };

            var connString = ResolveConnectionString(request.ServerName, spec.Database);
            if (connString == null)
                return new RemediationPreview { Succeeded = false, Error = $"No connection registered for '{request.ServerName}'." };

            if (!BackupCheckDbOpRenderer.TryRenderDatabaseOnlineProbe(spec.Database, out var onlineSql, out var onlineErr))
                return new RemediationPreview { Succeeded = false, Error = onlineErr };
            string? online;
            try { online = await ScalarAsync(connString, onlineSql, ct).ConfigureAwait(false); }
            catch (SqlException ex) { return new RemediationPreview { Succeeded = false, Error = $"State probe failed: {ex.Message}" }; }
            if (!string.Equals(online?.Trim(), "1", StringComparison.Ordinal))
                return new RemediationPreview { Succeeded = false, Error = $"Database '{spec.Database}' does not exist or is not ONLINE — refusing to preview CHECKDB." };

            long totalSizeBytes;
            try { totalSizeBytes = await ScalarLongAsync(connString, BackupCheckDbOpRenderer.RenderDatabaseTotalSizeBytesQuery(spec.Database), ct).ConfigureAwait(false); }
            catch (SqlException ex) { return new RemediationPreview { Succeeded = false, Error = $"Database-size read failed: {ex.Message}" }; }

            var (drive, driveErr) = await ResolveDataDriveAsync(request.ServerName, spec.Database, ct).ConfigureAwait(false);
            BackupCheckDbOpRenderer.ResourceGateResult gate = drive != null
                ? BackupCheckDbOpRenderer.EvaluateCheckDbResourceGate(totalSizeBytes, drive.AvailableBytes, drive.DriveLetter)
                : new BackupCheckDbOpRenderer.ResourceGateResult { Allowed = false, Reason = $"Could not resolve the database's data volume — refusing (fail closed). {driveErr}" };

            if (!BackupCheckDbOpRenderer.TryRenderCheckDbApply(p, out var applySql, out var renderErr))
                return new RemediationPreview { Succeeded = false, Error = renderErr };

            var confirmed = BackupCheckDbOpRenderer.IsConfirmed(p);
            var durationHint = BackupCheckDbOpRenderer.DurationHint(totalSizeBytes, isCheckDb: true);
            var gb = totalSizeBytes / 1024.0 / 1024.0 / 1024.0;

            var text =
                $"Would run on {request.ServerName}:\n{applySql}\n\n" +
                $"Database '{spec.Database}' is ~{gb:0.0} GB; CHECKDB typically {durationHint}; runs against the LIVE server (read-mostly, but shares I/O with the workload).\n" +
                $"Resource gate: {gate.Reason}\n" +
                (gate.Warning ? "WARNING: free space is tight for CHECKDB's internal snapshot (see above).\n" : string.Empty) +
                (confirmed ? "Confirmation token present." : "NO confirmation token — apply will be refused until BackupCheckDb.ConfirmLargeOperation=true is supplied.");

            bool wouldSucceed = gate.Allowed && confirmed;
            return new RemediationPreview { Succeeded = wouldSucceed, WhatIfText = text, Error = wouldSucceed ? null : "One or more gates would refuse this apply — see the gate detail above." };
        }

        // ── Gate 5: snapshot → apply → verify ───────────────────────────────

        public async Task<RemediationExecution> ExecuteAsync(RemediationRequest request, CancellationToken ct = default)
        {
            // CreateIndex resolves its OWN connection (to the target database, not master) and
            // renders fully-guarded DDL from request parameters. The runner's gate has already
            // classified the representative CREATE INDEX as Remediation under ADDMISSINGINDEX.
            if (request.Template.Operation?.OpKind == RemediationOpKind.CreateIndex)
                return await ExecuteCreateIndexAsync(request, ct).ConfigureAwait(false);

            // AgentAlertPack: msdb object creation, gated on Agent availability, snapshot-driven
            // rollback of only the names this apply created.
            if (request.Template.Operation?.OpKind == RemediationOpKind.AgentAlertPack)
                return await ExecuteAgentAlertPackAsync(request, ct).ConfigureAwait(false);

            // InstallMaintenanceSolution: install-if-absent (procs), or one schedule tickbox
            // (a single idempotent Agent job), routed by the Action parameter.
            if (request.Template.Operation?.OpKind == RemediationOpKind.InstallMaintenanceSolution)
                return await ExecuteMaintenanceSolutionAsync(request, ct).ConfigureAwait(false);

            // AgentJobPrimaryGuard: inject step 1 into ONE named existing job, snapshot-driven
            // rollback (delete the injected step, restore the original start step).
            if (request.Template.Operation?.OpKind == RemediationOpKind.AgentJobPrimaryGuard)
                return await ExecuteAgPrimaryGuardAsync(request, ct).ConfigureAwait(false);

            // Agent-job sync: read the definition from the PRIMARY, write it to the SECONDARY
            // (request.ServerName is always the write target).
            if (request.Template.Operation?.OpKind == RemediationOpKind.AgentJobSync)
                return await ExecuteAgentJobSyncAsync(request, ct).ConfigureAwait(false);
            if (request.Template.Operation?.OpKind == RemediationOpKind.AgentJobDeleteExtra)
                return await ExecuteDeleteExtraJobAsync(request, ct).ConfigureAwait(false);

            // Lane S5 — gated one-shot Backup-NOW / CHECKDB-NOW against the LIVE server.
            // Every hard gate (state, resource, confirm-token) is RE-EVALUATED here — the
            // preview's numbers are display-only, exactly like every other op in this file.
            if (request.Template.Operation?.OpKind == RemediationOpKind.BackupDatabaseNow)
                return await ExecuteBackupDatabaseNowAsync(request, ct).ConfigureAwait(false);
            if (request.Template.Operation?.OpKind == RemediationOpKind.CheckDbNow)
                return await ExecuteCheckDbNowAsync(request, ct).ConfigureAwait(false);

            // DbSetOption: per-database apply — resolves its OWN offender list from
            // offenders_query (same query the preview used, for parity) rather than the single
            // scalar ExecuteConfigurationAsync below expects, so it must be checked first.
            if (request.Template.Operation?.OpKind == RemediationOpKind.DbSetOption)
                return await ExecuteDbSetOptionAsync(request, ct).ConfigureAwait(false);

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

        // ── AgentAlertPack kind: idempotent batch, snapshot-driven rollback of created names ──
        // ── AgentJobPrimaryGuard ─────────────────────────────────────────────────
        // Injects a primary-replica guard as step 1 of ONE named, already-existing Agent job.
        // Everything here is scoped to that single job: the snapshot, the verify, and the
        // inverse all name it explicitly, so a failure can never disturb a neighbouring job.

        private async Task<RemediationPreview> PreviewAgPrimaryGuardAsync(RemediationRequest request, CancellationToken ct)
        {
            var p = request.Parameters;
            if (!AgPrimaryGuardOpRenderer.TryRenderApplySql(p, out var applySql, out var applyErr))
                return new RemediationPreview { Succeeded = false, Error = applyErr };

            p!.TryGetValue(AgPrimaryGuardOpRenderer.JobNameParam, out var jobName);
            jobName = (jobName ?? string.Empty).Trim();

            var connString = ResolveConnectionString(request.ServerName);
            if (connString == null)
                return new RemediationPreview { Succeeded = false, Error = $"No connection registered for '{request.ServerName}'." };

            var note = string.Empty;
            try
            {
                var exists = await ScalarAsync(connString, AgPrimaryGuardOpRenderer.RenderGuardExistsSql(jobName), ct)
                                    .ConfigureAwait(false);
                if (string.Equals(exists?.Trim(), "1", StringComparison.Ordinal))
                    note = $"-- NOTE: '{jobName}' already has a guard at step 1. Applying would be a no-op.\n\n";
            }
            catch (SqlException ex)
            {
                note = $"-- NOTE: could not read the job's current state ({ex.Message}).\n\n";
            }

            return new RemediationPreview { Succeeded = true, WhatIfText = note + applySql };
        }

        private async Task<RemediationExecution> ExecuteAgPrimaryGuardAsync(RemediationRequest request, CancellationToken ct)
        {
            var p = request.Parameters;
            var connString = ResolveConnectionString(request.ServerName);
            if (connString == null)
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = $"No connection registered for '{request.ServerName}'." };

            // A guard step on an instance with no Agent would never fire. Fail closed, honestly.
            string? availability;
            try { availability = await ScalarAsync(connString, RemediationOpRenderer.AgentAvailabilityProbe, ct).ConfigureAwait(false); }
            catch (SqlException ex) { return PermsAwareFailure(ex, "Agent availability probe failed"); }
            if (string.Equals(availability?.Trim(), "0", StringComparison.Ordinal))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = "SQL Server Agent is not available on this edition (e.g. Express) — refusing to apply." };

            if (!AgPrimaryGuardOpRenderer.TryRenderApplySql(p, out var applySql, out var applyErr))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = applyErr };

            p!.TryGetValue(AgPrimaryGuardOpRenderer.JobNameParam, out var rawJob);
            var jobName = (rawJob ?? string.Empty).Trim();

            // 1) Snapshot BEFORE apply: is it already guarded, and what is the current start step
            //    (the value the inverse must restore)?
            string? alreadyGuarded, startStepRaw;
            try
            {
                alreadyGuarded = await ScalarAsync(connString, AgPrimaryGuardOpRenderer.RenderGuardExistsSql(jobName), ct).ConfigureAwait(false);
                startStepRaw = await ScalarAsync(connString, AgPrimaryGuardOpRenderer.RenderStartStepIdSql(jobName), ct).ConfigureAwait(false);
            }
            catch (SqlException ex) { return PermsAwareFailure(ex, "AG primary guard snapshot failed"); }

            if (startStepRaw is null)
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = $"Job '{jobName}' does not exist on {request.ServerName}." };

            // 2) Already guarded -> NoOp. The runner refunds the credit; re-running is free.
            if (string.Equals(alreadyGuarded?.Trim(), "1", StringComparison.Ordinal))
                return new RemediationExecution { Outcome = RemediationOutcome.NoOp };

            var originalStartStep = int.TryParse(startStepRaw.Trim(), out var s) ? s : 1;

            // 3) Apply. The batch is IF NOT EXISTS-guarded, so even a racing second apply is safe.
            try { await ExecuteNonQueryAsync(connString, applySql, ct).ConfigureAwait(false); }
            catch (Exception applyEx)
            {
                if (applyEx is OperationCanceledException) throw;

                // A partial apply is possible (step added, start_step_id update denied). Undo it
                // with the same inverse the verify-failure path uses — never leave a job half-wired.
                var (rolledBack, rollbackOk, rollbackError) =
                    await TryRollbackGuardAsync(connString, jobName, originalStartStep, request.Template.Reversible, ct).ConfigureAwait(false);

                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.CouldNotRun,
                    Error = $"AG primary guard apply failed: {applyEx.Message}",
                    IsPermissionDenied = applyEx is SqlException sx && PermissionDeniedErrors.Contains(sx.Number),
                    RolledBack = rolledBack,
                    RollbackSucceeded = rollbackOk,
                    RollbackError = rollbackError
                };
            }

            // 4) Verify: guard is step 1 AND the job starts at step 1.
            string? verifyResult;
            try { verifyResult = await ScalarAsync(connString, AgPrimaryGuardOpRenderer.RenderVerifySql(jobName), ct).ConfigureAwait(false); }
            catch (SqlException ex)
            {
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.AppliedVerifyFailed,
                    Error = $"Verify read failed: {ex.Message}",
                    IsPermissionDenied = PermissionDeniedErrors.Contains(ex.Number)
                };
            }
            if (string.Equals(verifyResult?.Trim(), "1", StringComparison.Ordinal))
                return new RemediationExecution { Outcome = RemediationOutcome.AppliedVerified };

            // 5) Verify failed — undo.
            var verifyError = $"Post-apply verify did not confirm the guard as step 1 of '{jobName}' with start_step_id = 1.";
            var (rb, rbOk, rbErr) =
                await TryRollbackGuardAsync(connString, jobName, originalStartStep, request.Template.Reversible, ct).ConfigureAwait(false);

            return new RemediationExecution
            {
                Outcome = RemediationOutcome.AppliedVerifyFailed,
                Error = verifyError,
                RolledBack = rb,
                RollbackSucceeded = rbOk,
                RollbackError = rbErr
            };
        }

        /// <summary>
        /// Runs the guard's named inverse and CONFIRMS it by re-reading, rather than assuming a
        /// successful execute means a restored state.
        /// </summary>
        private async Task<(bool rolledBack, bool ok, string? error)> TryRollbackGuardAsync(
            string connString, string jobName, int originalStartStepId, bool reversible, CancellationToken ct)
        {
            if (!reversible) return (false, false, null);

            var inverse = AgPrimaryGuardOpRenderer.RenderInverseSql(jobName, originalStartStepId);
            try
            {
                await ExecuteNonQueryAsync(connString, inverse, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return (true, false, $"Rollback failed: {ex.Message}");
            }

            try
            {
                var stillThere = await ScalarAsync(connString, AgPrimaryGuardOpRenderer.RenderGuardExistsSql(jobName), ct).ConfigureAwait(false);
                var ok = string.Equals(stillThere?.Trim(), "0", StringComparison.Ordinal);
                return (true, ok, ok ? null : "Rollback did not remove the injected guard step.");
            }
            catch
            {
                // The rollback action itself succeeded; the confirm read is unavailable.
                return (true, true, null);
            }
        }

        // ── AgentJobSync / AgentJobDeleteExtra ───────────────────────────────────
        // The definition is READ here from the primary rather than passed in as parameters. That
        // is the whole trust story: no T-SQL ever crosses the request-parameter surface, so an
        // operator (or a tampered parameter dictionary) cannot smuggle a batch past the gate.
        // Only two charset-guarded names travel: which job, and which server to read it from.

        private async Task<RemediationPreview> PreviewAgentJobSyncAsync(RemediationRequest request, CancellationToken ct)
        {
            var (job, error) = await ReadPrimaryJobAsync(request, ct).ConfigureAwait(false);
            if (job is null) return new RemediationPreview { Succeeded = false, Error = error };

            if (!AgentJobSyncOpRenderer.TryRenderSyncSql(job, out var sql, out var renderErr))
                return new RemediationPreview { Succeeded = false, Error = renderErr };

            return new RemediationPreview
            {
                Succeeded = true,
                WhatIfText = "-- " + AgentJobSyncOpRenderer.DropRecreateWarning + "\n\n" + sql
            };
        }

        private async Task<RemediationExecution> ExecuteAgentJobSyncAsync(RemediationRequest request, CancellationToken ct)
        {
            var connString = ResolveConnectionString(request.ServerName);
            if (connString == null)
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = $"No connection registered for '{request.ServerName}'." };

            var (job, readError) = await ReadPrimaryJobAsync(request, ct).ConfigureAwait(false);
            if (job is null)
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = readError };

            if (!AgentJobSyncOpRenderer.TryRenderSyncSql(job, out var applySql, out var renderErr))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = renderErr };

            // Snapshot: the secondary's CURRENT definition, so a verify failure can put it back.
            // Read through the inventory service (not a hand-rolled query) so snapshot and diff
            // can never disagree about what a job "is".
            var beforeList = await _jobs!.GetJobsAsync(request.ServerName, ct).ConfigureAwait(false);
            var before = beforeList.FirstOrDefault(j => string.Equals(j.Name, job.Name, StringComparison.OrdinalIgnoreCase));

            try { await ExecuteNonQueryAsync(connString, applySql, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException) throw;

                var (rb1, ok1, err1) = await RestoreJobAsync(connString, request.ServerName, job.Name, before, ct).ConfigureAwait(false);
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.CouldNotRun,
                    Error = $"Agent job sync failed: {ex.Message}",
                    IsPermissionDenied = ex is SqlException sx && PermissionDeniedErrors.Contains(sx.Number),
                    RolledBack = rb1,
                    RollbackSucceeded = ok1,
                    RollbackError = err1
                };
            }

            string? verify;
            try
            {
                verify = await ScalarAsync(connString,
                    AgentJobSyncOpRenderer.RenderVerifySql(job.Name, job.Steps.Count, job.StartStepId), ct).ConfigureAwait(false);
            }
            catch (SqlException ex)
            {
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.AppliedVerifyFailed,
                    Error = $"Verify read failed: {ex.Message}",
                    IsPermissionDenied = PermissionDeniedErrors.Contains(ex.Number)
                };
            }

            if (string.Equals(verify?.Trim(), "1", StringComparison.Ordinal))
                return new RemediationExecution { Outcome = RemediationOutcome.AppliedVerified };

            var (rb, ok, err) = await RestoreJobAsync(connString, request.ServerName, job.Name, before, ct).ConfigureAwait(false);
            return new RemediationExecution
            {
                Outcome = RemediationOutcome.AppliedVerifyFailed,
                Error = $"Post-apply verify did not confirm '{job.Name}' with {job.Steps.Count} step(s) starting at step {job.StartStepId}.",
                RolledBack = rb,
                RollbackSucceeded = ok,
                RollbackError = err
            };
        }

        private async Task<RemediationExecution> ExecuteDeleteExtraJobAsync(RemediationRequest request, CancellationToken ct)
        {
            var connString = ResolveConnectionString(request.ServerName);
            if (connString == null)
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = $"No connection registered for '{request.ServerName}'." };

            request.Parameters!.TryGetValue(AgentJobSyncOpRenderer.JobNameParam, out var rawName);
            var jobName = (rawName ?? string.Empty).Trim();

            if (!AgentJobSyncOpRenderer.TryRenderDeleteExtraSql(jobName, out var sql, out var err))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = err };

            try
            {
                var exists = await ScalarAsync(connString, AgentJobSyncOpRenderer.RenderExistsSql(jobName), ct).ConfigureAwait(false);
                if (string.Equals(exists?.Trim(), "0", StringComparison.Ordinal))
                    return new RemediationExecution { Outcome = RemediationOutcome.NoOp };

                await ExecuteNonQueryAsync(connString, sql, ct).ConfigureAwait(false);

                var after = await ScalarAsync(connString, AgentJobSyncOpRenderer.RenderExistsSql(jobName), ct).ConfigureAwait(false);
                return string.Equals(after?.Trim(), "0", StringComparison.Ordinal)
                    ? new RemediationExecution { Outcome = RemediationOutcome.AppliedVerified }
                    : new RemediationExecution { Outcome = RemediationOutcome.AppliedVerifyFailed, Error = $"'{jobName}' still exists after the delete." };
            }
            catch (SqlException ex) { return PermsAwareFailure(ex, $"Deleting '{jobName}' failed"); }
        }

        /// <summary>Reads the job definition from the source (primary) instance.</summary>
        private async Task<(Models.Jobs.AgentJobDefinition? job, string error)> ReadPrimaryJobAsync(
            RemediationRequest request, CancellationToken ct)
        {
            if (_jobs is null)
                return (null, "Job inventory service is not available in this host; cannot read the primary's job definition.");

            var p = request.Parameters;
            if (p is null) return (null, "Job name and source server are required.");

            p.TryGetValue(AgentJobSyncOpRenderer.JobNameParam, out var rawJob);
            p.TryGetValue(AgentJobSyncOpRenderer.SourceServerParam, out var rawSrc);
            var jobName = (rawJob ?? string.Empty).Trim();
            var source = (rawSrc ?? string.Empty).Trim();

            if (!AgentJobSyncOpRenderer.IsSafeName(jobName)) return (null, $"Job name '{jobName}' is empty or not permitted here.");
            if (string.IsNullOrWhiteSpace(source)) return (null, "No source (primary) server supplied.");
            if (string.Equals(source, request.ServerName, StringComparison.OrdinalIgnoreCase))
                return (null, "Source and target are the same instance; refusing to sync a server to itself.");

            var jobs = await _jobs.GetJobsAsync(source, ct).ConfigureAwait(false);
            var job = jobs.FirstOrDefault(j => string.Equals(j.Name, jobName, StringComparison.OrdinalIgnoreCase));
            return job is null
                ? (null, $"Job '{jobName}' was not found on '{source}'.")
                : (job, string.Empty);
        }

        /// <summary>
        /// Puts the target back to <paramref name="before"/> — recreating its prior definition, or
        /// deleting the job outright when it did not exist before this apply. Confirms by re-reading.
        /// </summary>
        private async Task<(bool rolledBack, bool ok, string? error)> RestoreJobAsync(
            string connString, string serverName, string jobName,
            Models.Jobs.AgentJobDefinition? before, CancellationToken ct)
        {
            try
            {
                string restoreSql;
                if (before is null)
                {
                    // The job did not exist on the secondary before: the clean inverse is removal.
                    if (!AgentJobSyncOpRenderer.TryRenderDeleteExtraSql(jobName, out restoreSql, out var delErr))
                        return (false, false, delErr);
                }
                else if (!AgentJobSyncOpRenderer.TryRenderSyncSql(before, out restoreSql, out var reErr))
                {
                    return (false, false, reErr);
                }

                await ExecuteNonQueryAsync(connString, restoreSql, ct).ConfigureAwait(false);

                var exists = await ScalarAsync(connString, AgentJobSyncOpRenderer.RenderExistsSql(jobName), ct).ConfigureAwait(false);
                var shouldExist = before is not null;
                var ok = string.Equals(exists?.Trim(), shouldExist ? "1" : "0", StringComparison.Ordinal);
                return (true, ok, ok ? null : "Rollback did not restore the secondary's prior state.");
            }
            catch (Exception ex)
            {
                return (true, false, $"Rollback failed: {ex.Message}");
            }
        }

        private async Task<RemediationExecution> ExecuteAgentAlertPackAsync(RemediationRequest request, CancellationToken ct)
        {
            var p = request.Parameters;
            var connString = ResolveConnectionString(request.ServerName);
            if (connString == null)
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = $"No connection registered for '{request.ServerName}'." };

            // Gate: SQL Server Agent must be available (Express/EngineEdition 4 has none). Fail
            // closed with an honest message rather than run a batch that would never fire.
            string? availability;
            try { availability = await ScalarAsync(connString, RemediationOpRenderer.AgentAvailabilityProbe, ct).ConfigureAwait(false); }
            catch (SqlException ex) { return PermsAwareFailure(ex, "Agent availability probe failed"); }
            if (string.Equals(availability?.Trim(), "0", StringComparison.Ordinal))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = "SQL Server Agent is not available on this edition (e.g. Express) — refusing to apply." };

            if (!RemediationOpRenderer.TryResolveAgentAlertPackSpec(p, out _, out var specErr))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = specErr };
            if (!RemediationOpRenderer.TryRenderAgentAlertPackApply(p, out var applySql, out var applyErr))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = applyErr };
            if (!RemediationOpRenderer.TryRenderAgentAlertPackVerify(p, out var verifySql, out var verifyErr))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = verifyErr };

            // 1) Snapshot: which of the 10 names + the operator already exist BEFORE apply.
            //    Rollback below drops ONLY the names absent from this set (apply created them).
            HashSet<string> preExisting;
            try { preExisting = await RowsAsync(connString, RemediationOpRenderer.RenderAgentAlertPackSnapshot(p), ct).ConfigureAwait(false); }
            catch (SqlException ex) { return PermsAwareFailure(ex, "Agent alert pack snapshot failed"); }

            // 2) Already fully configured (operator + all 10 alerts pre-existing)? -> NoOp.
            var allNamesPlusOperator = new List<string>(RemediationOpRenderer.AllAgentAlertNames()) { "(operator)" };
            if (allNamesPlusOperator.TrueForAll(preExisting.Contains))
                return new RemediationExecution { Outcome = RemediationOutcome.NoOp };

            // 3) Apply the idempotent batch (IF NOT EXISTS guards make this safe to run even when
            //    some names already exist — only the missing ones get created). A THROW here can
            //    still be a PARTIAL apply (e.g. permission denied on the 4th alert, or a
            //    cancellation) — some names absent from preExisting may now exist in msdb. Clean
            //    those up with the same snapshot-driven rollback step 5 uses, so a failed apply
            //    never leaves orphaned operator/alert/notification objects behind.
            try { await ExecuteNonQueryAsync(connString, applySql, ct).ConfigureAwait(false); }
            catch (Exception applyEx)
            {
                if (applyEx is OperationCanceledException) throw; // surface cancellation, don't swallow it as a failure

                bool rolledBack = false, rollbackOk = false;
                string? rollbackError = null;
                if (request.Template.Reversible
                    && RemediationOpRenderer.TryRenderAgentAlertPackRollback(p, preExisting, out var cleanupSql, out _))
                {
                    rolledBack = true;
                    try
                    {
                        await ExecuteNonQueryAsync(connString, cleanupSql, ct).ConfigureAwait(false);
                        var after = await RowsAsync(connString, RemediationOpRenderer.RenderAgentAlertPackSnapshot(p), ct).ConfigureAwait(false);
                        rollbackOk = after.SetEquals(preExisting);
                        if (!rollbackOk) rollbackError = "Cleanup after apply failure did not fully restore the pre-apply state.";
                    }
                    catch (Exception cleanupEx)
                    {
                        // The apply failure is the primary error — don't let a cleanup failure mask it.
                        rollbackOk = false;
                        rollbackError = $"Cleanup after apply failure failed: {cleanupEx.Message}";
                    }
                }

                if (applyEx is SqlException sqlEx)
                {
                    return new RemediationExecution
                    {
                        Outcome = RemediationOutcome.CouldNotRun,
                        Error = $"Agent alert pack apply failed: {sqlEx.Message}",
                        IsPermissionDenied = PermissionDeniedErrors.Contains(sqlEx.Number),
                        RolledBack = rolledBack,
                        RollbackSucceeded = rollbackOk,
                        RollbackError = rollbackError
                    };
                }
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.CouldNotRun,
                    Error = $"Agent alert pack apply failed: {applyEx.Message}",
                    RolledBack = rolledBack,
                    RollbackSucceeded = rollbackOk,
                    RollbackError = rollbackError
                };
            }

            // 4) Verify: operator + all 10 alerts + all 10 notifications now exist.
            string? verifyResult;
            try { verifyResult = await ScalarAsync(connString, verifySql, ct).ConfigureAwait(false); }
            catch (SqlException ex)
            {
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.AppliedVerifyFailed,
                    Error = $"Verify read failed: {ex.Message}",
                    IsPermissionDenied = PermissionDeniedErrors.Contains(ex.Number)
                };
            }
            if (string.Equals(verifyResult?.Trim(), "1", StringComparison.Ordinal))
                return new RemediationExecution { Outcome = RemediationOutcome.AppliedVerified };

            // 5) Verify failed — roll back to the snapshot (drop only names THIS apply created).
            var verifyError = "Post-apply verify did not confirm the operator, all 10 alerts, and all 10 notifications.";
            if (request.Template.Reversible
                && RemediationOpRenderer.TryRenderAgentAlertPackRollback(p, preExisting, out var rollbackSql, out _))
            {
                try { await ExecuteNonQueryAsync(connString, rollbackSql, ct).ConfigureAwait(false); }
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
                bool ok = true;
                try
                {
                    var after = await RowsAsync(connString, RemediationOpRenderer.RenderAgentAlertPackSnapshot(p), ct).ConfigureAwait(false);
                    // Rollback is correct iff the post-rollback existing set equals the pre-apply set.
                    ok = after.SetEquals(preExisting);
                }
                catch { /* rollback action succeeded; confirm read unavailable — treat as rolled back */ }
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.AppliedVerifyFailed,
                    Error = verifyError,
                    RolledBack = true,
                    RollbackSucceeded = ok,
                    RollbackError = ok ? null : "Rollback did not fully restore the pre-apply state."
                };
            }
            return new RemediationExecution { Outcome = RemediationOutcome.AppliedVerifyFailed, Error = verifyError };
        }

        // ── InstallMaintenanceSolution kind: install-if-absent, or one schedule tickbox ──
        private async Task<RemediationExecution> ExecuteMaintenanceSolutionAsync(RemediationRequest request, CancellationToken ct)
        {
            var p = request.Parameters;
            if (!MaintenanceSolutionOpRenderer.TryResolveAction(p, out _, out var lane, out var actionErr))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = actionErr };

            var connString = ResolveConnectionString(request.ServerName);
            if (connString == null)
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = $"No connection registered for '{request.ServerName}'." };

            return lane is null
                ? await ExecuteMaintenanceSolutionInstallAsync(connString, request, ct).ConfigureAwait(false)
                : await ExecuteMaintenanceSolutionScheduleAsync(connString, request, lane.Value, ct).ConfigureAwait(false);
        }

        // Install-if-absent: snapshot (do the 4 procs exist?) -> NoOp if so, else run the
        // checksum-verified script (GO-split, one connection) -> verify -> DROP-on-verify-fail
        // (the named uninstall — never a snapshot-value replay, there is no single "old value"
        // for an install). The script is NEVER re-run over an existing install (verified by the
        // snapshot gate below) — this is the "install-if-absent" contract the design requires.
        private async Task<RemediationExecution> ExecuteMaintenanceSolutionInstallAsync(
            string connString, RemediationRequest request, CancellationToken ct)
        {
            // 0) Checksum guard — refuse before touching the server if the embedded resource
            //    doesn't match the pinned hash (tamper/version guard).
            string[] batches;
            try { batches = MaintenanceSolutionOpRenderer.RenderInstallApplyBatches(); }
            catch (Exception ex) { return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = ex.Message }; }

            // 1) Snapshot: PER-NAME provenance — which of the 4 core procs + CommandLog already
            //    exist BEFORE apply (a HashSet<string>, same shape as AgentAlertPack's snapshot).
            //    Captured ONCE here and threaded into every RenderUninstallSql call below so
            //    cleanup/rollback never drops a name that predates this apply (e.g. a user's own
            //    home-grown dbo.DatabaseBackup proc) — this is the data-loss fix: uninstall used
            //    to be name-only (IF OBJECT_ID(...) IS NOT NULL with no provenance check at all).
            HashSet<string> preExisting;
            try { preExisting = await RowsAsync(connString, MaintenanceSolutionOpRenderer.RenderProcsProvenanceSnapshot(), ct).ConfigureAwait(false); }
            catch (SqlException ex) { return PermsAwareFailure(ex, "Maintenance Solution install snapshot failed"); }

            // 2) Already installed? -> NoOp (no credit consumed; the 9515-line script is NEVER
            //    re-run over an existing install). Equivalent to the old "=4" boolean: all 4 proc
            //    names + CommandLog present in the provenance set.
            var allCoreNames = new List<string>(MaintenanceSolutionOpRenderer.CoreProcNames) { MaintenanceSolutionOpRenderer.CommandLogTableName };
            if (allCoreNames.TrueForAll(preExisting.Contains))
                return new RemediationExecution { Outcome = RemediationOutcome.NoOp };

            // 3) Apply: run every GO batch sequentially on ONE connection (the script's session-
            //    scoped #Config temp table must survive across batch boundaries — same requirement
            //    ServerConfigScriptService documents for this exact class of script). Fail closed:
            //    unlike ServerConfigScriptService's continue-on-error preview/apply loop, an install
            //    must not leave a half-created object set — the first batch failure aborts and the
            //    catch below cleans up whatever the partial run created.
            try
            {
                using var conn = new SqlConnection(connString);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                foreach (var batch in batches)
                {
                    if (string.IsNullOrWhiteSpace(batch)) continue;
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = batch;
                    cmd.CommandTimeout = 300;
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }
            catch (Exception applyEx)
            {
                if (applyEx is OperationCanceledException) throw; // surface cancellation, don't swallow it as a failure

                // Partial apply cleanup: the same named, PROVENANCE-GATED uninstall the verify-failed
                // path uses below — it only drops names both (a) existing and (b) absent from
                // preExisting, so it is safe even if nothing was created yet, and never touches a
                // pre-existing object (e.g. a user's own dbo.DatabaseBackup) that this apply didn't create.
                bool rolledBack = false, rollbackOk = false; string? rollbackError = null;
                if (request.Template.Reversible)
                {
                    rolledBack = true;
                    try { await ExecuteNonQueryAsync(connString, MaintenanceSolutionOpRenderer.RenderUninstallSql(preExisting), ct).ConfigureAwait(false); rollbackOk = true; }
                    catch (Exception cleanupEx) { rollbackError = $"Cleanup after apply failure failed: {cleanupEx.Message}"; }
                }
                if (applyEx is SqlException sqlEx)
                    return new RemediationExecution
                    {
                        Outcome = RemediationOutcome.CouldNotRun,
                        Error = $"Maintenance Solution install failed: {sqlEx.Message}",
                        IsPermissionDenied = PermissionDeniedErrors.Contains(sqlEx.Number),
                        RolledBack = rolledBack, RollbackSucceeded = rollbackOk, RollbackError = rollbackError
                    };
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.CouldNotRun,
                    Error = $"Maintenance Solution install failed: {applyEx.Message}",
                    RolledBack = rolledBack, RollbackSucceeded = rollbackOk, RollbackError = rollbackError
                };
            }

            // 4) Verify: all 4 core procs now exist.
            string? after;
            try { after = await ScalarAsync(connString, MaintenanceSolutionOpRenderer.ProcsExistProbe, ct).ConfigureAwait(false); }
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

            // 5) Verify failed — uninstall (named, PROVENANCE-GATED inverse: drop only the procs +
            //    CommandLog THIS apply created; a pre-existing name of the same shape is left alone).
            var verifyError = "Post-install verify did not find all 4 core procedures.";
            if (request.Template.Reversible)
            {
                try { await ExecuteNonQueryAsync(connString, MaintenanceSolutionOpRenderer.RenderUninstallSql(preExisting), ct).ConfigureAwait(false); }
                catch (SqlException ex)
                {
                    return new RemediationExecution
                    {
                        Outcome = RemediationOutcome.AppliedVerifyFailed,
                        Error = verifyError,
                        RolledBack = true, RollbackSucceeded = false,
                        RollbackError = $"Rollback failed: {ex.Message}",
                        IsPermissionDenied = PermissionDeniedErrors.Contains(ex.Number)
                    };
                }
                bool ok = true;
                try
                {
                    // Rollback is correct iff the post-uninstall provenance set equals the pre-apply
                    // set (same "after.SetEquals(preExisting)" check AgentAlertPack's rollback uses) —
                    // NOT "all 4 absent", which would be false whenever a pre-existing name was
                    // correctly left in place.
                    var afterRollback = await RowsAsync(connString, MaintenanceSolutionOpRenderer.RenderProcsProvenanceSnapshot(), ct).ConfigureAwait(false);
                    ok = afterRollback.SetEquals(preExisting);
                }
                catch { /* uninstall action succeeded; confirm read unavailable — treat as rolled back */ }
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.AppliedVerifyFailed,
                    Error = verifyError,
                    RolledBack = true, RollbackSucceeded = ok,
                    RollbackError = ok ? null : "Rollback did not fully restore the pre-apply state."
                };
            }
            return new RemediationExecution { Outcome = RemediationOutcome.AppliedVerifyFailed, Error = verifyError };
        }

        // One schedule tickbox: an independent, idempotent Agent job. Gated on Agent availability
        // (Express has none); snapshot=does the named job already exist -> NoOp if so; apply the
        // IF-NOT-EXISTS-guarded create batch; verify; DROP the job (clean named inverse) on a
        // verify failure. Each lane is its own bounded, reversible unit — dropping one never
        // touches another lane's job or a pre-existing Ola install.
        private async Task<RemediationExecution> ExecuteMaintenanceSolutionScheduleAsync(
            string connString, RemediationRequest request, MaintenanceSolutionOpRenderer.ScheduleLane lane, CancellationToken ct)
        {
            var p = request.Parameters;

            string? availability;
            try { availability = await ScalarAsync(connString, MaintenanceSolutionOpRenderer.AgentAvailabilityProbe, ct).ConfigureAwait(false); }
            catch (SqlException ex) { return PermsAwareFailure(ex, "Agent availability probe failed"); }
            if (string.Equals(availability?.Trim(), "0", StringComparison.Ordinal))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = "SQL Server Agent is not available on this edition (e.g. Express) — schedule tickboxes cannot be applied here." };

            if (!MaintenanceSolutionOpRenderer.TryRenderScheduleCreateSql(lane, p, out var createSql, out var createErr))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = createErr };

            var existsSql = MaintenanceSolutionOpRenderer.RenderScheduleExistsSql(lane);

            // 1) Snapshot: does this lane's job already exist?
            string? before;
            try { before = await ScalarAsync(connString, existsSql, ct).ConfigureAwait(false); }
            catch (SqlException ex) { return PermsAwareFailure(ex, "Schedule job existence snapshot failed"); }

            // 2) Already present? -> NoOp.
            if (string.Equals(before?.Trim(), "1", StringComparison.Ordinal))
                return new RemediationExecution { Outcome = RemediationOutcome.NoOp };

            // 3) Apply the idempotent create batch.
            try { await ExecuteNonQueryAsync(connString, createSql, ct).ConfigureAwait(false); }
            catch (Exception applyEx)
            {
                if (applyEx is OperationCanceledException) throw;
                if (applyEx is SqlException sqlEx) return PermsAwareFailure(sqlEx, "Schedule job create failed");
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = $"Schedule job create failed: {applyEx.Message}" };
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

            // 5) Verify failed — DROP the job we created (clean named inverse).
            var verifyError = "Post-create verify did not find the schedule job.";
            if (request.Template.Reversible)
            {
                try { await ExecuteNonQueryAsync(connString, MaintenanceSolutionOpRenderer.RenderScheduleDropSql(lane), ct).ConfigureAwait(false); }
                catch (SqlException ex)
                {
                    return new RemediationExecution
                    {
                        Outcome = RemediationOutcome.AppliedVerifyFailed,
                        Error = verifyError,
                        RolledBack = true, RollbackSucceeded = false,
                        RollbackError = $"Rollback failed: {ex.Message}",
                        IsPermissionDenied = PermissionDeniedErrors.Contains(ex.Number)
                    };
                }
                bool ok = true;
                try { ok = string.Equals((await ScalarAsync(connString, existsSql, ct).ConfigureAwait(false))?.Trim(), "0", StringComparison.Ordinal); }
                catch { /* DROP succeeded; confirm read unavailable — treat as rolled back */ }
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.AppliedVerifyFailed,
                    Error = verifyError,
                    RolledBack = true, RollbackSucceeded = ok,
                    RollbackError = ok ? null : "Rollback DROP did not remove the job."
                };
            }
            return new RemediationExecution { Outcome = RemediationOutcome.AppliedVerifyFailed, Error = verifyError };
        }

        // ── Lane S5: Backup-NOW — every hard gate RE-EVALUATED on the mutating path ──
        private async Task<RemediationExecution> ExecuteBackupDatabaseNowAsync(RemediationRequest request, CancellationToken ct)
        {
            var p = request.Parameters;

            // Gate 4 FIRST (cheapest, no I/O): no confirm token -> refuse before touching the server.
            if (!BackupCheckDbOpRenderer.IsConfirmed(p))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = "Refused: requires explicit confirmation for a live-server operation (BackupCheckDb.ConfirmLargeOperation=true)." };

            if (!BackupCheckDbOpRenderer.TryResolveBackupSpec(p, out var spec, out var specErr))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = specErr };

            var connString = ResolveConnectionString(request.ServerName, spec.Database);
            if (connString == null)
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = $"No connection registered for '{request.ServerName}'." };

            // Gate 1: state — must exist and be ONLINE (and, for a system DB, explicitly allowed —
            // already enforced by TryResolveBackupSpec's AllowSystemDatabaseParam check above).
            if (!BackupCheckDbOpRenderer.TryRenderDatabaseOnlineProbe(spec.Database, out var onlineSql, out var onlineErr))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = onlineErr };
            string? online;
            try { online = await ScalarAsync(connString, onlineSql, ct).ConfigureAwait(false); }
            catch (SqlException ex) { return PermsAwareFailure(ex, "Database state probe failed"); }
            if (!string.Equals(online?.Trim(), "1", StringComparison.Ordinal))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = $"Database '{spec.Database}' does not exist or is not ONLINE." };

            // Gate 2: resource — estimate size (query run against the target DB) vs the backup
            // directory's drive free space (via DiskIoService). Fails CLOSED if the drive cannot
            // be resolved (no DiskIoService inventory hit) — never assumes space is available.
            long estimatedBytes;
            try { estimatedBytes = await ScalarLongAsync(connString, BackupCheckDbOpRenderer.EstimateBackupSizeBytesQuery, ct).ConfigureAwait(false); }
            catch (SqlException ex) { return PermsAwareFailure(ex, "Backup-size estimate failed"); }

            var driveLetter = ExtractDriveLetter(spec.Directory);
            if (string.IsNullOrEmpty(driveLetter))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = $"Could not determine a drive letter from backup directory '{spec.Directory}' — refusing (fail closed; the resource gate requires a resolvable drive)." };

            var conn = _connections.GetConnection(request.ServerName)
                       ?? _connections.GetConnections().Find(c => c.GetServerList().Exists(s => string.Equals(s, request.ServerName, StringComparison.OrdinalIgnoreCase)));
            if (conn == null)
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = $"No connection registered for '{request.ServerName}'." };
            DiskIoService.Snapshot snap;
            try
            {
                var servers = conn.GetServerList();
                var server = servers.Count > 0 ? servers[0] : request.ServerName;
                snap = await _diskIo.GetSnapshotAsync(conn, server, windowSeconds: 1, ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex) { return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = $"Could not read disk inventory: {ex.Message}" }; }

            var targetDrive = snap.Drives.Find(d => string.Equals(d.DriveLetter, driveLetter, StringComparison.OrdinalIgnoreCase));
            if (targetDrive == null)
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = $"Could not determine free space for drive '{driveLetter}' — refusing (fail closed)." };

            var gate = BackupCheckDbOpRenderer.EvaluateBackupResourceGate(estimatedBytes, targetDrive.AvailableBytes, driveLetter);
            if (!gate.Allowed)
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = gate.Reason };

            // Gate 6: path — render (guards empty/injection; TryResolveBackupSpec already
            // rejected an empty directory, and TryRenderBackupApply single-quote-escapes it).
            if (!BackupCheckDbOpRenderer.TryRenderBackupApply(p, out var applySql, out var renderErr))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = renderErr };

            // Snapshot the "apply start" instant (UTC) — the verify read looks for a backupset
            // row finishing AFTER this instant, so a STALE prior backup can never false-pass.
            var startUtc = DateTime.UtcNow;

            // Gate 5: timeout — generous but BOUNDED (BackupCommandTimeoutSeconds), so a hung
            // backup (dead tape/share) surfaces as a timeout rather than hanging forever.
            try { await ExecuteNonQueryAsync(connString, applySql, BackupCheckDbOpRenderer.BackupCommandTimeoutSeconds, ct).ConfigureAwait(false); }
            catch (Exception applyEx)
            {
                if (applyEx is OperationCanceledException) throw; // surface cancellation, don't downgrade
                // A BACKUP that throws mid-run may still have produced a partial/failed backupset
                // row (SQL Server itself cleans up an aborted backup — there is no partial .bak to
                // roll back). Consult msdb authoritatively rather than assume "nothing happened".
                bool verified = false;
                try
                {
                    if (BackupCheckDbOpRenderer.TryRenderBackupVerifyRead(spec.Database, startUtc, out var vsql, out _))
                        verified = string.Equals((await ScalarAsync(connString, vsql, ct).ConfigureAwait(false))?.Trim(), "1", StringComparison.Ordinal);
                }
                catch { /* couldn't confirm — fall through to the throw-based outcome */ }
                if (verified) return new RemediationExecution { Outcome = RemediationOutcome.AppliedVerified };
                if (applyEx is SqlException sqlEx) return PermsAwareFailure(sqlEx, "Backup failed");
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = $"Backup failed: {applyEx.Message}" };
            }

            // Verify: a backupset row for this database with backup_finish_date >= startUtc.
            if (!BackupCheckDbOpRenderer.TryRenderBackupVerifyRead(spec.Database, startUtc, out var verifySql, out var verifyRenderErr))
                return new RemediationExecution { Outcome = RemediationOutcome.AppliedVerifyFailed, Error = verifyRenderErr };
            string? verifyResult;
            try { verifyResult = await ScalarAsync(connString, verifySql, ct).ConfigureAwait(false); }
            catch (SqlException ex)
            {
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.AppliedVerifyFailed,
                    Error = $"Verify read failed: {ex.Message}",
                    IsPermissionDenied = PermissionDeniedErrors.Contains(ex.Number)
                };
            }
            if (string.Equals(verifyResult?.Trim(), "1", StringComparison.Ordinal))
                return new RemediationExecution { Outcome = RemediationOutcome.AppliedVerified };

            // BACKUP DATABASE returned without throwing but no matching backupset row was found —
            // NOT reversible (there is nothing to roll back: a backup only creates a file; either
            // it exists in msdb or it doesn't) — surfaced as AppliedVerifyFailed for human review.
            return new RemediationExecution { Outcome = RemediationOutcome.AppliedVerifyFailed, Error = "BACKUP DATABASE completed without error but no matching msdb.dbo.backupset row was found." };
        }

        // ── Lane S5: CHECKDB-NOW — every hard gate RE-EVALUATED on the mutating path ──
        private async Task<RemediationExecution> ExecuteCheckDbNowAsync(RemediationRequest request, CancellationToken ct)
        {
            var p = request.Parameters;

            // Gate 4 FIRST: no confirm token -> refuse before touching the server.
            if (!BackupCheckDbOpRenderer.IsConfirmed(p))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = "Refused: requires explicit confirmation for a live-server operation (BackupCheckDb.ConfirmLargeOperation=true)." };

            if (!BackupCheckDbOpRenderer.TryResolveCheckDbSpec(p, out var spec, out var specErr))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = specErr };

            var connString = ResolveConnectionString(request.ServerName, spec.Database);
            if (connString == null)
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = $"No connection registered for '{request.ServerName}'." };

            // Gate 1: state — CHECKDB works on any ONLINE database (no system-DB restriction:
            // CHECKDB against master/msdb is normal, unlike backing them up unasked).
            if (!BackupCheckDbOpRenderer.TryRenderDatabaseOnlineProbe(spec.Database, out var onlineSql, out var onlineErr))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = onlineErr };
            string? online;
            try { online = await ScalarAsync(connString, onlineSql, ct).ConfigureAwait(false); }
            catch (SqlException ex) { return PermsAwareFailure(ex, "Database state probe failed"); }
            if (!string.Equals(online?.Trim(), "1", StringComparison.Ordinal))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = $"Database '{spec.Database}' does not exist or is not ONLINE." };

            // Gate 3: resource — CHECKDB's internal-snapshot heuristic vs free space on the
            // database's OWN data volume (via DiskIoService). Fails CLOSED if the volume cannot
            // be resolved.
            long totalSizeBytes;
            try { totalSizeBytes = await ScalarLongAsync(connString, BackupCheckDbOpRenderer.RenderDatabaseTotalSizeBytesQuery(spec.Database), ct).ConfigureAwait(false); }
            catch (SqlException ex) { return PermsAwareFailure(ex, "Database-size read failed"); }

            var (drive, driveErr) = await ResolveDataDriveAsync(request.ServerName, spec.Database, ct).ConfigureAwait(false);
            if (drive == null)
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = $"Could not resolve the database's data volume — refusing (fail closed). {driveErr}" };

            var gate = BackupCheckDbOpRenderer.EvaluateCheckDbResourceGate(totalSizeBytes, drive.AvailableBytes, drive.DriveLetter);
            if (!gate.Allowed)
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = gate.Reason };

            if (!BackupCheckDbOpRenderer.TryRenderCheckDbApply(p, out var applySql, out var renderErr))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = renderErr };

            // Gate 5: timeout — generous but BOUNDED (CheckDbCommandTimeoutSeconds); a runaway
            // CHECKDB (e.g. on a very large, badly-fragmented database) surfaces as a timeout
            // rather than hanging the connection forever.
            try
            {
                await ExecuteNonQueryAsync(connString, applySql, BackupCheckDbOpRenderer.CheckDbCommandTimeoutSeconds, ct).ConfigureAwait(false);
            }
            catch (Exception applyEx)
            {
                if (applyEx is OperationCanceledException) throw; // surface cancellation, don't downgrade
                // DBCC CHECKDB surfaces corruption findings as INFO/error messages, not always as
                // a thrown SqlException with a distinguishable "corruption found" number — a throw
                // here (including one carrying corruption text) is reported CouldNotRun with the
                // full server message so a human reviews it; NEVER silently reinterpreted as clean.
                if (applyEx is SqlException sqlEx) return PermsAwareFailure(sqlEx, "DBCC CHECKDB failed or reported errors");
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = $"DBCC CHECKDB failed: {applyEx.Message}" };
            }

            // Verify = the command's own success (no exception) — DBCC CHECKDB WITH NO_INFOMSGS
            // returns no result set on a clean pass; ALL_ERRORMSGS + a non-zero severity would
            // have thrown a SqlException above (caught and reported), so reaching here means the
            // engine completed the check with nothing to report. Not reversible: a consistency
            // check makes no changes, so there is nothing to roll back either way.
            return new RemediationExecution { Outcome = RemediationOutcome.AppliedVerified };
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

        // ── DbSetOption kind: per-database ALTER DATABASE ... SET, snapshot/rollback per offender ──
        //
        // Unlike Configuration/SpConfigure's single scalar value, this op fixes a WHOLE LIST of
        // databases in one apply. "Pre-state" is derived, not read: every database returned by
        // offenders_query is, by definition, NOT at the option_sql target (that is what
        // "offender" means) — so its true prior state is the OPPOSITE value. Preview and apply
        // use the SAME offenders_query (parity by construction — see PreviewDbSetOptionAsync).
        private async Task<RemediationExecution> ExecuteDbSetOptionAsync(RemediationRequest request, CancellationToken ct)
        {
            var t = request.Template;
            var op = t.Operation;
            if (op is null || string.IsNullOrWhiteSpace(op.OptionSql) || string.IsNullOrWhiteSpace(op.OffendersQuery))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = "db_set_option template carries no option_sql/offenders_query." };

            var connString = ResolveConnectionString(request.ServerName);
            if (connString == null)
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = $"No connection registered for '{request.ServerName}'." };

            // 1) Snapshot: the SAME offenders_query the preview used. Every name here is, by
            //    definition, currently NOT at the option_sql target (the rollback target).
            List<string> offenders;
            try { offenders = (await RowsAsync(connString, op.OffendersQuery, ct).ConfigureAwait(false)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch (SqlException ex) { return PermsAwareFailure(ex, "Offenders snapshot failed"); }

            // 2) None offending? -> NoOp (nothing to do, no credit consumed).
            if (offenders.Count == 0)
                return new RemediationExecution { Outcome = RemediationOutcome.NoOp };

            bool canInvert = RemediationOpRenderer.TryInvertBooleanOptionSql(op.OptionSql, out var invertedOptionSql);

            // 3) Apply per offender. ALTER DATABASE ... SET auto-commits (cannot run inside a user
            //    transaction — same restriction RECONFIGURE has), so a throw partway through may
            //    have already changed some databases; track exactly which ones we executed so a
            //    rollback never touches a database this apply didn't touch. Names that fail the
            //    identifier guard (hyphens/dots are ordinary in real DB names) are tracked in
            //    `skipped`, NOT silently dropped — folded into the verify/outcome below (step 4/5)
            //    so a database this apply never touched can never read back as AppliedVerified.
            var applied = new List<string>();
            var skipped = new List<(string Database, string Reason)>();
            foreach (var db in offenders)
            {
                if (!RemediationOpRenderer.TryRenderDbSetOption(db, op.OptionSql, out var applySql, out var renderErr))
                {
                    skipped.Add((db, renderErr));
                    continue;
                }
                try { await ExecuteNonQueryAsync(connString, applySql, ct).ConfigureAwait(false); applied.Add(db); }
                catch (Exception applyEx)
                {
                    if (applyEx is OperationCanceledException) throw; // surface cancellation, don't downgrade
                    var (rolledBack, rollbackOk, rollbackErr) = canInvert
                        ? await RollbackDbSetOptionAsync(connString, applied, invertedOptionSql, ct).ConfigureAwait(false)
                        : (false, false, "Rollback not available: option_sql is not a simple ON/OFF toggle.");
                    if (applyEx is SqlException sqlEx)
                        return new RemediationExecution
                        {
                            Outcome = RemediationOutcome.CouldNotRun,
                            Error = $"db_set_option apply failed on '{db}': {sqlEx.Message}",
                            IsPermissionDenied = PermissionDeniedErrors.Contains(sqlEx.Number),
                            RolledBack = rolledBack, RollbackSucceeded = rollbackOk, RollbackError = rollbackErr
                        };
                    return new RemediationExecution
                    {
                        Outcome = RemediationOutcome.CouldNotRun,
                        Error = $"db_set_option apply failed on '{db}': {applyEx.Message}",
                        RolledBack = rolledBack, RollbackSucceeded = rollbackOk, RollbackError = rollbackErr
                    };
                }
            }

            // 3b) Every offender unrenderable? Zero statements executed — this must never read
            //     as an applied/verified change (nothing ran to verify) or commit a credit for
            //     zero work. Fail closed as CouldNotRun (the runner refunds on this outcome) with
            //     the exact per-database reason.
            if (applied.Count == 0)
            {
                var reasons = string.Join("; ", skipped.Select(s => $"{s.Database} ({s.Reason})"));
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.CouldNotRun,
                    Error = $"All {skipped.Count} offending database(s) have unrenderable names — nothing was applied: {reasons}"
                };
            }

            // 4) Verify: re-run the SAME offenders_query (preview==apply==verify parity) and
            //    confirm none of the databases THIS RUN KNEW ABOUT are still offending — both
            //    the ones we applied AND the ones we skipped as unrenderable. Checking only
            //    `applied` here was the defect: a skipped database can never appear in `applied`,
            //    so it could never trip verify and would silently read as AppliedVerified while
            //    still offending.
            HashSet<string> stillOffending;
            try { stillOffending = await RowsAsync(connString, op.OffendersQuery, ct).ConfigureAwait(false); }
            catch (SqlException ex)
            {
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.AppliedVerifyFailed,
                    Error = $"Verify read failed: {ex.Message}",
                    IsPermissionDenied = PermissionDeniedErrors.Contains(ex.Number)
                };
            }

            var stillBad = offenders.Where(stillOffending.Contains).ToList();
            if (stillBad.Count == 0)
                return new RemediationExecution { Outcome = RemediationOutcome.AppliedVerified };

            // 5) Verify failed for one or more databases — roll back ONLY those (re-apply the
            //    inverted clause, restoring the exact pre-apply "offender" state). A stillBad
            //    entry that was skipped as unrenderable will fail to render here too (same
            //    reason) and is honestly reported by RollbackDbSetOptionAsync as a rollback
            //    failure — there is nothing to roll back, and nothing to hide.
            var skippedNames = new HashSet<string>(skipped.Select(s => s.Database), StringComparer.OrdinalIgnoreCase);
            var skippedStillBad = stillBad.Where(skippedNames.Contains).ToList();
            var verifyError = skippedStillBad.Count > 0
                ? $"Post-change verify still finds {stillBad.Count} of {offenders.Count} database(s) offending: {string.Join(", ", stillBad)} " +
                  $"({skippedStillBad.Count} of which were skipped as unrenderable and never attempted: {string.Join(", ", skippedStillBad)})."
                : $"Post-change verify still finds {stillBad.Count} of {applied.Count} database(s) offending: {string.Join(", ", stillBad)}.";
            if (t.Reversible && canInvert)
            {
                var (rolledBack, rollbackOk, rollbackErr) = await RollbackDbSetOptionAsync(connString, stillBad, invertedOptionSql, ct).ConfigureAwait(false);
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.AppliedVerifyFailed,
                    Error = verifyError,
                    RolledBack = rolledBack,
                    RollbackSucceeded = rollbackOk,
                    RollbackError = rollbackErr
                };
            }
            return new RemediationExecution { Outcome = RemediationOutcome.AppliedVerifyFailed, Error = verifyError };
        }

        // Re-applies the inverted option_sql to each named database — DbSetOption's snapshot-based
        // rollback action (mirrors Configuration's re-apply-old-value rollback, generalised to a
        // per-database loop). Best-effort: a throw on one database doesn't stop the others, so a
        // partial rollback is reported honestly (RollbackSucceeded=false) rather than masked.
        private async Task<(bool RolledBack, bool Succeeded, string? Error)> RollbackDbSetOptionAsync(
            string connString, IReadOnlyList<string> databases, string invertedOptionSql, CancellationToken ct)
        {
            if (databases.Count == 0) return (false, true, null);
            var failures = new List<string>();
            foreach (var db in databases)
            {
                if (!RemediationOpRenderer.TryRenderDbSetOption(db, invertedOptionSql, out var sql, out var err))
                {
                    failures.Add($"{db}: {err}");
                    continue;
                }
                try { await ExecuteNonQueryAsync(connString, sql, ct).ConfigureAwait(false); }
                catch (Exception ex) { failures.Add($"{db}: {ex.Message}"); }
            }
            return (true, failures.Count == 0, failures.Count == 0 ? null : "Rollback failed for: " + string.Join("; ", failures));
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

        // Lane S5's resource-gate reads return a BIGINT byte count — parsed straight to long
        // (DBNull/unparseable -> 0, which fails the resource gate closed rather than throwing
        // mid-preview; a genuine read failure is still surfaced via the SqlException the caller
        // already catches around this call).
        private static async Task<long> ScalarLongAsync(string connString, string query, CancellationToken ct)
        {
            var raw = await ScalarAsync(connString, query, ct).ConfigureAwait(false);
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0L;
        }

        // Same drive-letter heuristic as DiskIoService.ExtractDriveLetter (private there) —
        // duplicated rather than exposed, since it's a trivial, stable string parse and lane S5
        // needs it against an operator-typed backup DIRECTORY, not a DMV physical_name.
        private static string ExtractDriveLetter(string path)
        {
            var t = (path ?? string.Empty).Trim();
            if (t.Length < 2) return string.Empty;
            return t[1] == ':' && char.IsLetter(t[0]) ? char.ToUpperInvariant(t[0]) + ":" : string.Empty;
        }

        // Multi-row read: returns the single string column of every row as a set. Used by
        // AgentAlertPack's snapshot (which of the 10 alert names + the operator already exist) —
        // the rollback then drops only the names ABSENT from this set (apply created them).
        private static async Task<HashSet<string>> RowsAsync(string connString, string query, CancellationToken ct)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            using var conn = new SqlConnection(connString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new SqlCommand(query, conn) { CommandTimeout = 30 };
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                if (!await reader.IsDBNullAsync(0, ct).ConfigureAwait(false))
                    result.Add(reader.GetString(0));
            return result;
        }

        // Executes the rendered remediation T-SQL (the exact text the gate classified).
        // Used by the Configuration path only — sp_configure/RECONFIGURE can't run inside a
        // user transaction, so this is a plain batch; rollback is snapshot-based (re-apply).
        private static Task ExecuteNonQueryAsync(string connString, string sql, CancellationToken ct) =>
            ExecuteNonQueryAsync(connString, sql, commandTimeoutSeconds: 30, ct);

        // Overload with an explicit command timeout — lane S5's Backup-NOW/CHECKDB-NOW run for
        // hours on a large database, so they pass BackupCommandTimeoutSeconds/
        // CheckDbCommandTimeoutSeconds here instead of the 30-second default every other
        // remediation apply uses (those are all sub-second config/DDL changes).
        private static async Task ExecuteNonQueryAsync(string connString, string sql, int commandTimeoutSeconds, CancellationToken ct)
        {
            using var conn = new SqlConnection(connString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = commandTimeoutSeconds };
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
