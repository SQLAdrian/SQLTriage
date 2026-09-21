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
    public sealed class DbatoolsRemediationExecutor : IRemediationExecutor, IRemediationRollbackExecutor
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

            var connString = ResolveConnectionString(request.ServerName);

            // Phase-2 item 6a, ORDER-CORRECTED at the fix-round gate (blocker 1). The host ceiling
            // for 'max server memory (MB)' needs a read. That read used to happen FIRST, so a
            // preview of "banana" refused with a sentence claiming nothing had reached the server
            // while sys.dm_os_sys_info had already been queried. The staged resolution parses and
            // range-checks on text alone, and only a value that survives that can cause the
            // read-only host probe to run.
            var staged = await RemediationOpRenderer.ResolveValueStagedAsync(
                t.Operation, request.Parameters,
                token => TryReadPhysicalMemoryMbAsync(connString, t.Operation, token), ct).ConfigureAwait(false);
            if (!staged.Ok)
                return new RemediationPreview { Succeeded = false, Error = staged.Error };
            var target = staged.Value;
            if (!RemediationOpRenderer.TryRender(t.Operation, target, out var applySql, out var renderError))
                return new RemediationPreview { Succeeded = false, Error = renderError };

            // ROUTE A (Phase-2 item 2.4): the rendered fix passes the exec-surface guard before an
            // operator is ever shown it. A preview that renders something the apply path would
            // refuse is a preview that wastes an approval.
            if (!RemediationExecGuard.Allows(applySql, RemediationExecGuard.RenderedFix, out var guardError))
                return new RemediationPreview { Succeeded = false, Error = guardError };

            // Best-effort read of the current value so the preview shows "current -> target".
            // The read is DERIVED from the op (single-statement) — never free-form template text.
            //
            // ⚠ THE CONFIGURED VALUE, not value_in_use (Phase-2 item 2.1). This is the number the
            // operator set and the number a rollback would put back, so it is the number the preview
            // must show. When the engine is using something else (a coerced floor, a pending
            // restart) the sentence below says so rather than leaving the two silently conflated.
            string? current = null;
            string? effective = null;
            if (connString != null && RemediationOpRenderer.TryRenderConfiguredRead(t.Operation, out var cfgReadSql, out _))
            {
                try { current = await ScalarAsync(connString, cfgReadSql, ct).ConfigureAwait(false); }
                catch { /* preview is best-effort; the apply path reports any read failure authoritatively */ }
            }
            if (connString != null && RemediationOpRenderer.TryRenderRead(t.Operation, out var useReadSql, out _))
            {
                try { effective = await ScalarAsync(connString, useReadSql, ct).ConfigureAwait(false); }
                catch { /* same */ }
            }

            // The preview must state the same verdict the apply path will reach. If the server is
            // already at the recommended value and this request would move it off, apply refuses
            // (see ExecuteConfigurationAsync step 2b) — so preview says so here rather than
            // rendering T-SQL that will not be allowed to run.
            if (RemediationOpRenderer.IsRegressionFromRecommended(t.Operation, TryParseInt(current), target)
                && !RemediationOpRenderer.IsRegressionAcknowledged(request.Parameters))
            {
                return new RemediationPreview
                {
                    Succeeded = false,
                    Error = RemediationOpRenderer.DescribeRegressionRefusal(t.Operation, target)
                };
            }

            // ⚠ THE SENTENCE BELOW IS PARSED BACK. BatchRemediationDriver.DetectNoChange reads this
            // exact text to answer "would applying this change anything?", and the batch surface
            // prices an item on that answer. The format therefore lives in ONE place —
            // RemediationPreviewSentence — with the parser derived from it, so a wording change here
            // cannot silently stop the detection. It used to be an interpolated literal, with the
            // regex restating it in another file (Phase-1 gate, 2026-09-01).
            var text = RemediationPreviewSentence.ConfigurationPreview(
                request.ServerName, applySql, t.Operation.ConfigName, current, target);

            // Phase-2 item 2.1 honesty: when the engine's effective value differs from the configured
            // one, the operator is told BOTH before approving. That difference is exactly the
            // condition the old rollback could not see.
            var configured = TryParseInt(current);
            var inUse = TryParseInt(effective);
            if (configured is int c && inUse is int u && c != u)
                text += $"\nThe engine is currently using {u}, not {c}. "
                      + "SQL Server either coerces this setting or is waiting for a restart, "
                      + "so the change may not take effect until then.";

            // The host WAS asked and could not answer. Said plainly, because the alternative is a
            // cap accepted against a ceiling nobody checked while the operator assumes one was.
            if (staged.HostCheckFailed)
                text += "\nThis host's installed RAM could not be read, so this cap was NOT checked "
                      + "against it. The number was checked against the range for the setting only.";

            // Phase-2 item 6b: reversibility BEFORE approval, in the preview text every surface
            // already renders, and as a structured field a surface can act on.
            var rev = RemediationRollbackProse.ForConfiguration(t, t.Operation.ConfigName, configured);
            text += "\n" + rev.Sentence;

            return new RemediationPreview
            {
                Succeeded = true,
                WhatIfText = text,
                CanRollBack = rev.CanRollBack,
                ReversibilityNote = rev.Sentence,
            };
        }

        /// <summary>
        /// Reads the host's installed RAM, for the max-server-memory ceiling only (Phase-2 item 6a).
        /// Read-only, one statement, and it fails to null rather than throwing: an unreadable host
        /// must leave the bound where the template put it, never manufacture one.
        /// </summary>
        private static async Task<int?> TryReadPhysicalMemoryMbAsync(
            string? connString, RemediationOperation? op, CancellationToken ct)
        {
            if (connString is null || op is null) return null;
            if (!RemediationValueBounds.IsMaxServerMemory(op.ConfigName)) return null;
            try
            {
                var raw = await ScalarAsync(connString, RemediationValueBounds.PhysicalMemoryMbQuery, ct).ConfigureAwait(false);
                var mb = TryParseInt(raw);
                return mb is int v && v > 0 ? v : null;
            }
            catch { return null; }
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
            catch (SqlException ex) when (AvailabilityGroupReadability.IsNotReadableOnThisReplica(ex)) { return NotReadableSkip(ex); }
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

            // Phase-2 item 6b: reversibility BEFORE approval. A compound option clause has no
            // derivable inverse, and the operator learns that here rather than after the apply.
            var dbRev = RemediationRollbackProse.ForDbSetOption(request.Template, op.OptionSql);
            sb.AppendLine();
            sb.Append(dbRev.Sentence);

            return new RemediationPreview
            {
                Succeeded = true,
                WhatIfText = sb.ToString().TrimEnd(),
                CanRollBack = dbRev.CanRollBack,
                ReversibilityNote = dbRev.Sentence,
            };
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

                // Preview reads the SAME provenance snapshot the apply gate reads, over the SAME 5
                // objects. It used to ask a 4-proc probe while apply required 5, so an install that
                // had the procs but no CommandLog previewed "no-op" and then re-ran the whole
                // 490 KB script over the operator's existing procedures.
                HashSet<string> preExisting;
                try { preExisting = await RowsAsync(connString, MaintenanceSolutionOpRenderer.RenderProcsProvenanceSnapshot(), ct).ConfigureAwait(false); }
                catch (SqlException ex) when (AvailabilityGroupReadability.IsNotReadableOnThisReplica(ex)) { return NotReadableSkip(ex); }
                catch (SqlException ex) { return new RemediationPreview { Succeeded = false, Error = $"Snapshot read failed: {ex.Message}" }; }

                switch (MaintenanceSolutionOpRenderer.Classify(preExisting))
                {
                    case MaintenanceSolutionOpRenderer.InstallPresence.FullyPresent:
                        return new RemediationPreview
                        {
                            Succeeded = true,
                            WhatIfText = string.Join(", ", MaintenanceSolutionOpRenderer.AllInstalledObjectNames) +
                                         $" all already exist on {request.ServerName}. Apply would be a no-op. " +
                                         "The Maintenance Solution script is never re-run over an existing install."
                        };

                    case MaintenanceSolutionOpRenderer.InstallPresence.PartiallyPresent:
                        // Preview states the truth apply will act on, rather than promising a no-op
                        // apply does not honour. Ruling 3: with the overwrite acknowledged, preview
                        // shows the forced install AND repeats the consequence, so the last thing
                        // read before approval is what the acknowledgement costs.
                        if (!MaintenanceSolutionOpRenderer.IsOverwriteAcknowledged(request.Parameters))
                            return new RemediationPreview
                            {
                                Succeeded = false,
                                Error = MaintenanceSolutionOpRenderer.DescribePartialInstallRefusal(preExisting)
                            };
                        return new RemediationPreview
                        {
                            Succeeded = true,
                            WhatIfText = $"Would FORCE-install Ola Hallengren's Maintenance Solution on {request.ServerName} (master), " +
                                         "@CreateJobs='N' (no jobs; use the schedule tickboxes separately). " + checksumNote + "\n\n" +
                                         MaintenanceSolutionOpRenderer.DescribeForcedInstallConsequence(preExisting)
                        };
                }

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

        /// <summary>
        /// Resolves the DiskIoService drive backing a BACKUP DIRECTORY — a different volume from
        /// the database's data files, and the one BACKUP actually writes to.
        /// <para>One decision point for preview and apply. The preview used to do its own inline
        /// lookup, swallow that lookup's exception, and then print the error text from
        /// <see cref="ResolveDataDriveAsync"/> instead — so an operator hitting a backup-directory
        /// problem was shown a data-file error, or a dangling sentence with no reason at all when
        /// the data drive resolved fine (honesty hunt r2-06). Sharing this method means the two
        /// paths cannot drift on which drive they measure or what they say when they cannot.</para>
        /// <para>Every failure returns a null drive with a reason. The callers fail CLOSED on a
        /// null drive: no free-space reading means the resource gate cannot be evaluated, so the
        /// operation is refused rather than assumed safe.</para>
        /// <para>Internal (InternalsVisibleTo SQLTriage.Tests) so each refusal reason is measured
        /// on its own rather than only through a live backup.</para>
        /// </summary>
        internal async Task<(DiskIoService.DriveRow? drive, string? error)> ResolveBackupDirectoryDriveAsync(
            string serverName, string directory, CancellationToken ct)
        {
            var driveLetter = ExtractDriveLetter(directory);
            if (string.IsNullOrEmpty(driveLetter))
                return (null, $"Could not determine a drive letter from backup directory '{directory}'. The resource gate needs a drive it can measure.");

            var conn = _connections.GetConnection(serverName)
                       ?? _connections.GetConnections().Find(c => c.GetServerList().Exists(s =>
                            string.Equals(s, serverName, StringComparison.OrdinalIgnoreCase)));
            if (conn == null)
                return (null, $"No connection registered for '{serverName}'.");

            DiskIoService.Snapshot snap;
            try
            {
                var servers = conn.GetServerList();
                var server = servers.Count > 0 ? servers[0] : serverName;
                snap = await _diskIo.GetSnapshotAsync(conn, server, windowSeconds: 1, ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex) { return (null, $"Could not read disk inventory: {ex.Message}"); }

            var targetDrive = snap.Drives.Find(d => string.Equals(d.DriveLetter, driveLetter, StringComparison.OrdinalIgnoreCase));
            if (targetDrive == null)
                return (null, $"Could not determine free space for drive '{driveLetter}'. It is not in this server's disk inventory.");

            return (targetDrive, null);
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
            catch (SqlException ex) when (AvailabilityGroupReadability.IsNotReadableOnThisReplica(ex)) { return NotReadableSkip(ex); }
            catch (SqlException ex) { return new RemediationPreview { Succeeded = false, Error = $"State probe failed: {ex.Message}" }; }
            if (!string.Equals(online?.Trim(), "1", StringComparison.Ordinal))
                return new RemediationPreview { Succeeded = false, Error = $"Database '{spec.Database}' does not exist or is not ONLINE — refusing to preview a backup." };

            // Gate 2 (resource): estimate size (run in the target DB) + the target backup drive's free space.
            long estimatedBytes;
            try { estimatedBytes = await ScalarLongAsync(connString, BackupCheckDbOpRenderer.EstimateBackupSizeBytesQuery, ct).ConfigureAwait(false); }
            catch (SqlException ex) when (AvailabilityGroupReadability.IsNotReadableOnThisReplica(ex)) { return NotReadableSkip(ex); }
            catch (SqlException ex) { return new RemediationPreview { Succeeded = false, Error = $"Backup-size estimate failed: {ex.Message}" }; }

            // The BACKUP DIRECTORY's drive, resolved by the same method apply uses, so preview and
            // apply measure the same volume and give the same reason when they cannot. The old code
            // here looked up the backup drive inline, swallowed that lookup's exception, and then
            // printed ResolveDataDriveAsync's error — a different volume's problem, or nothing at
            // all when the data drive resolved fine (honesty hunt r2-06).
            var (backupDrive, backupDriveErr) = await ResolveBackupDirectoryDriveAsync(request.ServerName, spec.Directory, ct).ConfigureAwait(false);
            string driveLabel = backupDrive?.DriveLetter ?? spec.Directory;

            BackupCheckDbOpRenderer.ResourceGateResult gate = backupDrive is not null
                ? BackupCheckDbOpRenderer.EvaluateBackupResourceGate(estimatedBytes, backupDrive.AvailableBytes, driveLabel)
                : new BackupCheckDbOpRenderer.ResourceGateResult
                {
                    Allowed = false,
                    Reason = $"Could not measure free space on the drive holding backup directory '{spec.Directory}'. This apply is refused. {backupDriveErr}"
                };

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
            catch (SqlException ex) when (AvailabilityGroupReadability.IsNotReadableOnThisReplica(ex)) { return NotReadableSkip(ex); }
            catch (SqlException ex) { return new RemediationPreview { Succeeded = false, Error = $"State probe failed: {ex.Message}" }; }
            if (!string.Equals(online?.Trim(), "1", StringComparison.Ordinal))
                return new RemediationPreview { Succeeded = false, Error = $"Database '{spec.Database}' does not exist or is not ONLINE — refusing to preview CHECKDB." };

            long totalSizeBytes;
            try { totalSizeBytes = await ScalarLongAsync(connString, BackupCheckDbOpRenderer.RenderDatabaseTotalSizeBytesQuery(spec.Database), ct).ConfigureAwait(false); }
            catch (SqlException ex) when (AvailabilityGroupReadability.IsNotReadableOnThisReplica(ex)) { return NotReadableSkip(ex); }
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
                        RollbackState = RemediationRollbackState.Failed,
                        RollbackError = $"Rollback failed: {ex.Message}",
                        IsPermissionDenied = PermissionDeniedErrors.Contains(ex.Number)
                    };
                }
                // DROP completed. Now READ the server back — the action completing is not the
                // post-state. A confirming read that throws yields Unconfirmed, not success.
                var (dropState, dropError) = await ConfirmRollbackAsync(
                    async token => string.Equals((await ScalarAsync(connString, existsSql, token).ConfigureAwait(false))?.Trim(), "0", StringComparison.Ordinal),
                    "Rollback DROP INDEX did not remove the index.", ct).ConfigureAwait(false);
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.AppliedVerifyFailed,
                    Error = verifyError,
                    RollbackState = dropState,
                    RollbackError = dropError
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
                var (guardState, guardError) =
                    await TryRollbackGuardAsync(connString, jobName, originalStartStep, request.Template.Reversible, ct).ConfigureAwait(false);

                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.CouldNotRun,
                    Error = $"AG primary guard apply failed: {applyEx.Message}",
                    IsPermissionDenied = applyEx is SqlException sx && PermissionDeniedErrors.Contains(sx.Number),
                    RollbackState = guardState,
                    RollbackError = guardError
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
            var (guardVfState, guardVfError) =
                await TryRollbackGuardAsync(connString, jobName, originalStartStep, request.Template.Reversible, ct).ConfigureAwait(false);

            return new RemediationExecution
            {
                Outcome = RemediationOutcome.AppliedVerifyFailed,
                Error = verifyError,
                RollbackState = guardVfState,
                RollbackError = guardVfError
            };
        }

        /// <summary>
        /// Runs the guard's named inverse and CONFIRMS it by re-reading, rather than assuming a
        /// successful execute means a restored state.
        /// </summary>
        private async Task<(RemediationRollbackState State, string? Error)> TryRollbackGuardAsync(
            string connString, string jobName, int originalStartStepId, bool reversible, CancellationToken ct)
        {
            // Not reversible: a rollback IS warranted here (the caller only asks after a failed
            // apply or a failed verify) and there is no inverse to run. That is NotAvailable, and
            // the operator is told plainly, rather than NotAttempted's silence.
            if (!reversible)
                return (RemediationRollbackState.NotAvailable,
                        "No rollback was attempted. This fix is not reversible, so the change may still be in place. "
                        + "Check the job.");

            var inverse = AgPrimaryGuardOpRenderer.RenderInverseSql(jobName, originalStartStepId);
            try
            {
                await ExecuteNonQueryAsync(connString, inverse, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return (RemediationRollbackState.Failed, $"Rollback failed: {ex.Message}");
            }

            return await ConfirmRollbackAsync(
                async token => string.Equals((await ScalarAsync(connString, AgPrimaryGuardOpRenderer.RenderGuardExistsSql(jobName), token).ConfigureAwait(false))?.Trim(), "0", StringComparison.Ordinal),
                "Rollback did not remove the injected guard step.", ct).ConfigureAwait(false);
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
            //
            // pages-r1-01: this read used to return an empty list on failure, and a null snapshot
            // means "the job did not exist before this apply" — which is what RestoreJobAsync
            // inverts by DELETING it. An unreadable msdb would therefore have armed a rollback
            // that destroys a pre-existing job on the secondary. No snapshot, no apply.
            var beforeRead = await _jobs!.GetJobsAsync(request.ServerName, ct).ConfigureAwait(false);
            if (!beforeRead.Succeeded)
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.CouldNotRun,
                    Error = "Refusing to sync: the target's current job definitions could not be read, so there is "
                          + $"no snapshot to roll back to. {beforeRead.DescribeFailure()}"
                };

            var before = beforeRead.Jobs.FirstOrDefault(j => string.Equals(j.Name, job.Name, StringComparison.OrdinalIgnoreCase));

            try { await ExecuteNonQueryAsync(connString, applySql, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException) throw;

                var (syncFailState, syncFailError) = await RestoreJobAsync(connString, request.ServerName, job.Name, before, ct).ConfigureAwait(false);
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.CouldNotRun,
                    Error = $"Agent job sync failed: {ex.Message}",
                    IsPermissionDenied = ex is SqlException sx && PermissionDeniedErrors.Contains(sx.Number),
                    RollbackState = syncFailState,
                    RollbackError = syncFailError
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

            var (syncVfState, syncVfError) = await RestoreJobAsync(connString, request.ServerName, job.Name, before, ct).ConfigureAwait(false);
            return new RemediationExecution
            {
                Outcome = RemediationOutcome.AppliedVerifyFailed,
                Error = $"Post-apply verify did not confirm '{job.Name}' with {job.Steps.Count} step(s) starting at step {job.StartStepId}.",
                RollbackState = syncVfState,
                RollbackError = syncVfError
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

            // pages-r1-01: a failed read used to come back as an empty list, so this reported
            // "Job 'X' was not found on 'source'" — a statement about the primary's contents made
            // from a read that never answered.
            var read = await _jobs.GetJobsAsync(source, ct).ConfigureAwait(false);
            if (!read.Succeeded) return (null, read.DescribeFailure());

            var job = read.Jobs.FirstOrDefault(j => string.Equals(j.Name, jobName, StringComparison.OrdinalIgnoreCase));
            return job is null
                ? (null, $"Job '{jobName}' was not found on '{source}'.")
                : (job, string.Empty);
        }

        /// <summary>
        /// Puts the target back to <paramref name="before"/> — recreating its prior definition, or
        /// deleting the job outright when it did not exist before this apply. Confirms by re-reading.
        /// </summary>
        private async Task<(RemediationRollbackState State, string? Error)> RestoreJobAsync(
            string connString, string serverName, string jobName,
            Models.Jobs.AgentJobDefinition? before, CancellationToken ct)
        {
            string restoreSql;
            try
            {
                // A render failure means the restore statement was never built, so no inverse ran.
                // NotAvailable, not Failed: the runner writes no "rollback FAILED" attestation for
                // a statement that was never sent, and the reason still reaches the operator.
                if (before is null)
                {
                    // The job did not exist on the secondary before: the clean inverse is removal.
                    if (!AgentJobSyncOpRenderer.TryRenderDeleteExtraSql(jobName, out restoreSql, out var delErr))
                        return (RemediationRollbackState.NotAvailable,
                                "No rollback was attempted. The removal statement could not be built, so the job may still be in place. "
                                + $"Reason: {delErr}");
                }
                else if (!AgentJobSyncOpRenderer.TryRenderSyncSql(before, out restoreSql, out var reErr))
                {
                    return (RemediationRollbackState.NotAvailable,
                            "No rollback was attempted. The restore statement could not be built, so the job may still be changed. "
                            + $"Reason: {reErr}");
                }

                await ExecuteNonQueryAsync(connString, restoreSql, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return (RemediationRollbackState.Failed, $"Rollback failed: {ex.Message}");
            }

            var shouldExist = before is not null;
            return await ConfirmRollbackAsync(
                async token => string.Equals((await ScalarAsync(connString, AgentJobSyncOpRenderer.RenderExistsSql(jobName), token).ConfigureAwait(false))?.Trim(), shouldExist ? "1" : "0", StringComparison.Ordinal),
                "Rollback did not restore the secondary's prior state.", ct).ConfigureAwait(false);
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

                var cleanupState = RemediationRollbackState.NotAttempted;
                string? cleanupError = null;
                if (request.Template.Reversible
                    && RemediationOpRenderer.TryRenderAgentAlertPackRollback(p, preExisting, out var cleanupSql, out _))
                {
                    try
                    {
                        await ExecuteNonQueryAsync(connString, cleanupSql, ct).ConfigureAwait(false);
                        (cleanupState, cleanupError) = await ConfirmRollbackAsync(
                            async token => (await RowsAsync(connString, RemediationOpRenderer.RenderAgentAlertPackSnapshot(p), token).ConfigureAwait(false)).SetEquals(preExisting),
                            "Cleanup after apply failure did not fully restore the pre-apply state.", ct).ConfigureAwait(false);
                    }
                    catch (Exception cleanupEx)
                    {
                        // The apply failure is the primary error — don't let a cleanup failure mask it.
                        cleanupState = RemediationRollbackState.Failed;
                        cleanupError = $"Cleanup after apply failure failed: {cleanupEx.Message}";
                    }
                }

                if (applyEx is SqlException sqlEx)
                {
                    return new RemediationExecution
                    {
                        Outcome = RemediationOutcome.CouldNotRun,
                        Error = $"Agent alert pack apply failed: {sqlEx.Message}",
                        IsPermissionDenied = PermissionDeniedErrors.Contains(sqlEx.Number),
                        RollbackState = cleanupState,
                        RollbackError = cleanupError
                    };
                }
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.CouldNotRun,
                    Error = $"Agent alert pack apply failed: {applyEx.Message}",
                    RollbackState = cleanupState,
                    RollbackError = cleanupError
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
                        RollbackState = RemediationRollbackState.Failed,
                        RollbackError = $"Rollback failed: {ex.Message}",
                        IsPermissionDenied = PermissionDeniedErrors.Contains(ex.Number)
                    };
                }
                // Rollback is correct iff the post-rollback existing set equals the pre-apply set.
                // A snapshot read that throws leaves that unknown, not proven.
                var (packState, packError) = await ConfirmRollbackAsync(
                    async token => (await RowsAsync(connString, RemediationOpRenderer.RenderAgentAlertPackSnapshot(p), token).ConfigureAwait(false)).SetEquals(preExisting),
                    "Rollback did not fully restore the pre-apply state.", ct).ConfigureAwait(false);
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.AppliedVerifyFailed,
                    Error = verifyError,
                    RollbackState = packState,
                    RollbackError = packError
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
            //    re-run over an existing install). Classify reads the same 5 objects the preview
            //    now reads, from the same snapshot query, so the two cannot disagree.
            var presence = MaintenanceSolutionOpRenderer.Classify(preExisting);
            if (presence == MaintenanceSolutionOpRenderer.InstallPresence.FullyPresent)
                return new RemediationExecution { Outcome = RemediationOutcome.NoOp };

            // 2b) SOME of the 5 objects already exist. The script creates a stub and then ALTERs
            //     unconditionally, so running it overwrites whatever is there, and the uninstall
            //     deliberately never drops a name that predates this apply. Overwriting an
            //     operator's own dbo.DatabaseBackup with no way back is not a fix. Refuse and name
            //     what is in the way. CouldNotRun refunds the credit.
            //
            //     Ruling 3 (2026-08-25) opened ONE way past it: an explicit, per-request
            //     acknowledgement of an overwrite that is stated as not reversible. Refusal remains
            //     the default — the parameter is absent unless an operator ticked the box on this
            //     apply — and the acknowledgement itself is written to the audit ledger by the
            //     runner, so the record says a human took this decision.
            if (presence == MaintenanceSolutionOpRenderer.InstallPresence.PartiallyPresent
                && !MaintenanceSolutionOpRenderer.IsOverwriteAcknowledged(request.Parameters))
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.CouldNotRun,
                    Error = MaintenanceSolutionOpRenderer.DescribePartialInstallRefusal(preExisting)
                };

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
                var cleanupState = RemediationRollbackState.NotAttempted;
                string? cleanupError = null;
                if (request.Template.Reversible)
                {
                    try
                    {
                        await ExecuteNonQueryAsync(connString, MaintenanceSolutionOpRenderer.RenderUninstallSql(preExisting), ct).ConfigureAwait(false);
                        // The uninstall running is not proof it worked. Read the provenance set back:
                        // cleanup is correct iff the objects present are exactly the pre-apply ones.
                        (cleanupState, cleanupError) = await ConfirmRollbackAsync(
                            async token => (await RowsAsync(connString, MaintenanceSolutionOpRenderer.RenderProcsProvenanceSnapshot(), token).ConfigureAwait(false)).SetEquals(preExisting),
                            "Cleanup after apply failure did not restore the pre-apply object set.", ct).ConfigureAwait(false);
                    }
                    catch (Exception cleanupEx)
                    {
                        cleanupState = RemediationRollbackState.Failed;
                        cleanupError = $"Cleanup after apply failure failed: {cleanupEx.Message}";
                    }
                }
                if (applyEx is SqlException sqlEx)
                    return new RemediationExecution
                    {
                        Outcome = RemediationOutcome.CouldNotRun,
                        Error = $"Maintenance Solution install failed: {sqlEx.Message}",
                        IsPermissionDenied = PermissionDeniedErrors.Contains(sqlEx.Number),
                        RollbackState = cleanupState, RollbackError = cleanupError
                    };
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.CouldNotRun,
                    Error = $"Maintenance Solution install failed: {applyEx.Message}",
                    RollbackState = cleanupState, RollbackError = cleanupError
                };
            }

            // 4) Verify: all 5 objects the install creates now exist. This used to ask the 4-proc
            //    probe, so an install that produced the procs but not CommandLog verified clean and
            //    was charged for, then failed later when a maintenance run tried to log to a table
            //    that was never created. Verify now measures the same set the NoOp gate does.
            HashSet<string> after;
            try { after = await RowsAsync(connString, MaintenanceSolutionOpRenderer.RenderProcsProvenanceSnapshot(), ct).ConfigureAwait(false); }
            catch (SqlException ex)
            {
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.AppliedVerifyFailed,
                    Error = $"Verify read failed: {ex.Message}",
                    IsPermissionDenied = PermissionDeniedErrors.Contains(ex.Number)
                };
            }
            if (MaintenanceSolutionOpRenderer.Classify(after) == MaintenanceSolutionOpRenderer.InstallPresence.FullyPresent)
                return new RemediationExecution { Outcome = RemediationOutcome.AppliedVerified };

            // 5) Verify failed — uninstall (named, PROVENANCE-GATED inverse: drop only the procs +
            //    CommandLog THIS apply created; a pre-existing name of the same shape is left alone).
            var missing = MaintenanceSolutionOpRenderer.AllInstalledObjectNames.Where(n => !after.Contains(n)).ToList();
            var verifyError = $"Post-install verify did not find {string.Join(", ", missing)}.";
            if (request.Template.Reversible)
            {
                try { await ExecuteNonQueryAsync(connString, MaintenanceSolutionOpRenderer.RenderUninstallSql(preExisting), ct).ConfigureAwait(false); }
                catch (SqlException ex)
                {
                    return new RemediationExecution
                    {
                        Outcome = RemediationOutcome.AppliedVerifyFailed,
                        Error = verifyError,
                        RollbackState = RemediationRollbackState.Failed,
                        RollbackError = $"Rollback failed: {ex.Message}",
                        IsPermissionDenied = PermissionDeniedErrors.Contains(ex.Number)
                    };
                }
                // Rollback is correct iff the post-uninstall provenance set equals the pre-apply
                // set (same "after.SetEquals(preExisting)" check AgentAlertPack's rollback uses) —
                // NOT "all 4 absent", which would be false whenever a pre-existing name was
                // correctly left in place. A read that throws proves nothing either way.
                var (uninstallState, uninstallError) = await ConfirmRollbackAsync(
                    async token => (await RowsAsync(connString, MaintenanceSolutionOpRenderer.RenderProcsProvenanceSnapshot(), token).ConfigureAwait(false)).SetEquals(preExisting),
                    "Rollback did not fully restore the pre-apply state.", ct).ConfigureAwait(false);
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.AppliedVerifyFailed,
                    Error = verifyError,
                    RollbackState = uninstallState,
                    RollbackError = uninstallError
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
                        RollbackState = RemediationRollbackState.Failed,
                        RollbackError = $"Rollback failed: {ex.Message}",
                        IsPermissionDenied = PermissionDeniedErrors.Contains(ex.Number)
                    };
                }
                var (jobState, jobError) = await ConfirmRollbackAsync(
                    async token => string.Equals((await ScalarAsync(connString, existsSql, token).ConfigureAwait(false))?.Trim(), "0", StringComparison.Ordinal),
                    "Rollback DROP did not remove the job.", ct).ConfigureAwait(false);
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.AppliedVerifyFailed,
                    Error = verifyError,
                    RollbackState = jobState,
                    RollbackError = jobError
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

            // Same resolver the preview calls, so the two paths can never disagree about which
            // drive they measure or why they could not measure it.
            var (targetDrive, backupDriveErr) = await ResolveBackupDirectoryDriveAsync(request.ServerName, spec.Directory, ct).ConfigureAwait(false);
            if (targetDrive == null)
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.CouldNotRun,
                    // The resolver states the fact; the caller states the consequence. Fail closed:
                    // no free-space reading means the gate cannot be evaluated, so nothing runs.
                    Error = $"{backupDriveErr} This apply is refused."
                };

            var gate = BackupCheckDbOpRenderer.EvaluateBackupResourceGate(estimatedBytes, targetDrive.AvailableBytes, targetDrive.DriveLetter);
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
            // the runner's gate classified — same renderer, same op) AND the read queries
            // (DERIVED from the op — single-statement, never free-form template text).
            //
            // ⚠ TWO READS, NOT ONE (Phase-2 item 2.1). `readSql` reads value_in_use, the value the
            // ENGINE is using: that is the honest post-change VERIFY, because a setting SQL Server
            // coerced or is holding until a restart has not taken effect. `configuredReadSql` reads
            // sys.configurations.value, the column sp_configure actually writes: that is what the
            // rollback captures, re-applies, and confirms against. Using value_in_use for both was
            // the proved defect (spike S2 section 3.1) - the confirming read compared a pinned 16
            // against a pinned 16, so "Confirmed" was decided before the inverse ran, and the inverse
            // re-applied 16 over a configured value of 0.
            //
            // ⚠ AND THE VALUE IS RESOLVED IN TWO STAGES, IN ORDER (fix round, gate blocker 1). The
            // host-RAM read that bounds 'max server memory (MB)' used to run BEFORE the parse, so a
            // refusal of "banana" printed "Nothing was sent to the server" over a completed query.
            // Stage 1 is pure; only a value that parses and passes the static range can cause the
            // read-only host probe to run, and the sentence each stage prints says which one refused.
            var staged = await RemediationOpRenderer.ResolveValueStagedAsync(
                t.Operation, request.Parameters,
                token => TryReadPhysicalMemoryMbAsync(connString, t.Operation, token), ct).ConfigureAwait(false);
            if (!staged.Ok)
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = staged.Error };
            var target = staged.Value;
            if (!RemediationOpRenderer.TryRender(t.Operation, target, out var applySql, out var renderError))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = renderError };
            if (!RemediationOpRenderer.TryRenderRead(t.Operation, out var readSql, out var readError))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = readError };
            if (!RemediationOpRenderer.TryRenderConfiguredRead(t.Operation, out var configuredReadSql, out var cfgReadError))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = cfgReadError };

            // ROUTE A (Phase-2 item 2.4): the exact text about to run passes the exec-surface guard
            // before it runs. Registry reads are allowed; registry writes, deletes and enumerations
            // are blocked per statement, so no leading benign statement can shield one.
            if (!RemediationExecGuard.Allows(applySql, RemediationExecGuard.RenderedFix, out var applyGuardError))
                return new RemediationExecution { Outcome = RemediationOutcome.CouldNotRun, Error = applyGuardError };

            // 1) Snapshot pre-change state (read-only; the rollback target).
            //
            // ⚠ CAPTURE DERIVES FROM THE RENDERED STATEMENT (Phase-2 item 2.3), not from the
            // template's target field. The renderer emits `sp_configure 'show advanced options', 1`
            // ahead of every advanced option, and nothing used to capture or restore it: on a server
            // where it starts at 0, applying any advanced fix flipped a second setting and left it
            // flipped (spike S1 section 3.10). Every option the batch WRITES is captured here.
            var touched = RenderedConfigurationScan.OptionNamesTouched(applySql);
            var configuredBefore = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in touched)
            {
                if (!RemediationOpRenderer.TryRenderConfiguredRead(name, out var oneRead, out _)) continue;
                try { configuredBefore[name] = TryParseInt(await ScalarAsync(connString, oneRead, ct).ConfigureAwait(false)); }
                catch (SqlException ex) { return PermsAwareFailure(ex, $"Snapshot read failed for '{name}'"); }
            }

            string? snapshot;
            try { snapshot = await ScalarAsync(connString, configuredReadSql, ct).ConfigureAwait(false); }
            catch (SqlException ex) { return PermsAwareFailure(ex, "Snapshot read failed"); }

            int? oldValue = TryParseInt(snapshot);

            // The effective value beside it, recorded for honesty. When the two differ the engine is
            // coercing this setting or waiting for a restart, and the record should say so rather
            // than leave a later reader to infer it from a verify failure.
            int? oldValueInUse = null;
            try { oldValueInUse = TryParseInt(await ScalarAsync(connString, readSql, ct).ConfigureAwait(false)); }
            catch (SqlException) { /* honesty field only; a failure here must not fail the apply */ }

            // 2) Already compliant? -> NoOp (nothing to do, no credit consumed).
            //
            // Measured on the CONFIGURED value (Phase-2 item 2.1). Under the old value_in_use test a
            // server whose configured value already WAS the target still applied, because the engine
            // reported a coerced value_in_use, and then "failed verify" over a change that was never
            // needed. The configured value is what sp_configure would write, so it is what decides
            // whether writing it does anything.
            if (oldValue == target)
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.NoOp,
                    PreChangeValue = oldValue,
                    PreChangeValueInUse = oldValueInUse,
                };

            // 2b) REGRESSION GUARD. The server is already at the value this fix exists to reach,
            //     and the request asks for a different one. Writing it would leave the server
            //     worse than we found it, verify clean (post == target), and commit a credit for
            //     the damage. Refuse instead. CouldNotRun refunds the reservation in the runner,
            //     so a server that needed nothing is never charged. A deliberate revert (Undo /
            //     Revert to history) passes AcknowledgeRegressionParam and is allowed through.
            if (RemediationOpRenderer.IsRegressionFromRecommended(t.Operation, oldValue, target)
                && !RemediationOpRenderer.IsRegressionAcknowledged(request.Parameters))
            {
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.CouldNotRun,
                    Error = RemediationOpRenderer.DescribeRegressionRefusal(t.Operation, target),
                    PreChangeValue = oldValue,
                    PreChangeValueInUse = oldValueInUse,
                };
            }

            // ── APPLY BOUNDARY ──────────────────────────────────────────────────────────────────
            // ⚠ EVERY RETURN BELOW THIS LINE MUST RESTORE THE RENDER'S SIDE EFFECTS FIRST.
            // The rendered batch is `sp_configure 'show advanced options', 1; RECONFIGURE;` and then
            // the target; the statements are not in a transaction, so the PRELUDE COMMITS EVEN WHEN
            // THE TARGET STATEMENT FAILS. The fix-round gate proved the hole live: on a server with
            // 'show advanced options' = 0, an apply that threw returned CouldNotRun and an
            // independent read afterwards said 1 — a second server setting changed by a fix that
            // did not even land, with no note and no PreChangeValue. The invariant below is enforced
            // by RemediationSideEffectRestoreCoverageTests, which reads this method and requires a
            // RestoreSideEffectOptionsAsync call between the boundary and every return after it.
            // ────────────────────────────────────────────────────────────────────────────────────

            // 3) Apply the rendered change (for real).
            try { await ExecuteNonQueryAsync(connString, applySql, ct).ConfigureAwait(false); }
            catch (SqlException ex)
            {
                var (sideNoteApply, _) = await RestoreSideEffectOptionsAsync(connString, applySql, t.Operation.ConfigName, configuredBefore, ct).ConfigureAwait(false);
                return PermsAwareFailure(ex, "Apply failed", sideNoteApply, oldValue, oldValueInUse);
            }

            // 4) Verify post-change state — against value_in_use, the value the ENGINE is using.
            //    A setting the engine coerced or is holding for a restart has NOT taken effect, and
            //    verifying it on the configured column would report success for a change that did
            //    nothing. This read stays exactly what it was.
            string? post;
            try { post = await ScalarAsync(connString, readSql, ct).ConfigureAwait(false); }
            catch (SqlException ex)
            {
                var (sideNoteA, _) = await RestoreSideEffectOptionsAsync(connString, applySql, t.Operation.ConfigName, configuredBefore, ct).ConfigureAwait(false);
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.AppliedVerifyFailed,
                    Error = $"Verify read failed: {ex.Message}{sideNoteA}",
                    PreChangeValue = oldValue,
                    PreChangeValueInUse = oldValueInUse,
                    IsPermissionDenied = PermissionDeniedErrors.Contains(ex.Number)
                };
            }

            if (TryParseInt(post) == target)
            {
                // The change took. Put back anything the RENDER touched on the way (item 2.3): the
                // 'show advanced options' prelude is a second server setting, and leaving it flipped
                // is a change nobody approved.
                var (sideNote, _) = await RestoreSideEffectOptionsAsync(connString, applySql, t.Operation.ConfigName, configuredBefore, ct).ConfigureAwait(false);
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.AppliedVerified,
                    Error = string.IsNullOrEmpty(sideNote) ? null : sideNote.Trim(),
                    PreChangeValue = oldValue,
                    PreChangeValueInUse = oldValueInUse,
                };
            }

            // 5) Verify failed: the change ran but didn't take. Snapshot-based rollback —
            //    re-apply the captured pre-change CONFIGURED value so the server is never left in a
            //    half-changed state. The runner ledgers the rollback outcome.
            var verifyError = $"Post-change verify expected {target} but read '{post}'.";

            // NO ROLLBACK, stated rather than silent (Phase-2 item 2.1). Two honest reasons: the
            // template declares itself irreversible, or the configured value could not be read
            // before the change, in which case there is genuinely nothing to put back. Either way
            // the reason rides the result, is surfaced by the page, and is never a bare state name.
            if (!t.Reversible || !oldValue.HasValue)
            {
                var (sideNoteB, _) = await RestoreSideEffectOptionsAsync(connString, applySql, t.Operation.ConfigName, configuredBefore, ct).ConfigureAwait(false);
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.AppliedVerifyFailed,
                    Error = verifyError + sideNoteB,
                    PreChangeValue = oldValue,
                    PreChangeValueInUse = oldValueInUse,
                    RollbackState = RemediationRollbackState.NotAvailable,
                    RollbackError = RemediationRollbackProse.NoRollbackMarker + " " + (!t.Reversible
                        ? $"this fix declares itself not reversible, so '{t.Operation.ConfigName}' stays as this apply left it."
                        : $"the configured value of '{t.Operation.ConfigName}' could not be read before the change, so there is nothing to put back."),
                };
            }

            if (!RemediationOpRenderer.TryRender(t.Operation, oldValue.Value, out var rollbackSql, out var rollbackRenderError))
            {
                var (sideNoteC, _) = await RestoreSideEffectOptionsAsync(connString, applySql, t.Operation.ConfigName, configuredBefore, ct).ConfigureAwait(false);
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.AppliedVerifyFailed,
                    Error = verifyError + sideNoteC,
                    PreChangeValue = oldValue,
                    PreChangeValueInUse = oldValueInUse,
                    RollbackState = RemediationRollbackState.NotAvailable,
                    RollbackError = RemediationRollbackProse.NoRollbackMarker
                        + $" the inverse statement could not be rendered ({rollbackRenderError}), so nothing was run to put this back.",
                };
            }

            // ROUTE A: the INVERSE passes the same wall the fix did, before it runs.
            if (!RemediationExecGuard.Allows(rollbackSql, RemediationExecGuard.RenderedInverse, out var invGuardError))
            {
                var (sideNoteG, _) = await RestoreSideEffectOptionsAsync(connString, applySql, t.Operation.ConfigName, configuredBefore, ct).ConfigureAwait(false);
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.AppliedVerifyFailed,
                    Error = verifyError + sideNoteG,
                    PreChangeValue = oldValue,
                    PreChangeValueInUse = oldValueInUse,
                    RollbackState = RemediationRollbackState.NotAvailable,
                    RollbackError = RemediationRollbackProse.NoRollbackMarker + " " + invGuardError,
                };
            }

            try
            {
                await ExecuteNonQueryAsync(connString, rollbackSql, ct).ConfigureAwait(false);
            }
            catch (SqlException ex)
            {
                var (sideNoteF, _) = await RestoreSideEffectOptionsAsync(connString, applySql, t.Operation.ConfigName, configuredBefore, ct).ConfigureAwait(false);
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.AppliedVerifyFailed,
                    Error = verifyError + sideNoteF,
                    PreChangeValue = oldValue,
                    PreChangeValueInUse = oldValueInUse,
                    RollbackState = RemediationRollbackState.Failed,
                    RollbackError = $"Rollback failed: {ex.Message}",
                    IsPermissionDenied = PermissionDeniedErrors.Contains(ex.Number)
                };
            }

            // ⚠ THE CONFIRMING READ IS DISCRIMINATING (Phase-2 item 2.1). It reads the CONFIGURED
            // value — the column the inverse above actually sets — and compares it against the
            // configured value captured before the change. The old form read value_in_use and
            // compared it against a value_in_use snapshot, which on a coercing option is the same
            // pinned number on both sides: it could not tell a restored server from an unrestored
            // one, and it answered "Confirmed" either way.
            var (cfgState, cfgError) = await ConfirmRollbackAsync(
                async token => TryParseInt(await ScalarAsync(connString, configuredReadSql, token).ConfigureAwait(false)) == oldValue.Value,
                $"Rollback did not restore the configured value of '{t.Operation.ConfigName}' to {oldValue.Value}.",
                ct).ConfigureAwait(false);

            var (sideNoteD, _) = await RestoreSideEffectOptionsAsync(connString, applySql, t.Operation.ConfigName, configuredBefore, ct).ConfigureAwait(false);

            // Honesty note on a CONFIRMED rollback of a coerced or restart-pending option: the
            // configured value is back, and the engine may still be running the other one. Say it,
            // rather than letting "Confirmed" imply more than was read.
            var stillCoerced = string.Empty;
            if (cfgState == RemediationRollbackState.Confirmed)
            {
                try
                {
                    var inUseAfter = TryParseInt(await ScalarAsync(connString, readSql, ct).ConfigureAwait(false));
                    if (inUseAfter is int a && a != oldValue.Value)
                        stillCoerced = $" The configured value is back at {oldValue.Value}. "
                                     + $"The engine is still using {a}, which it was before this change as well "
                                     + "(SQL Server coerces this setting or needs a restart).";
                }
                catch (SqlException) { /* the confirming read already decided the state; this is a note */ }
            }

            return new RemediationExecution
            {
                Outcome = RemediationOutcome.AppliedVerifyFailed,
                Error = verifyError + sideNoteD,
                PreChangeValue = oldValue,
                PreChangeValueInUse = oldValueInUse,
                RollbackState = cfgState,
                RollbackError = string.IsNullOrEmpty(stillCoerced) ? cfgError : (cfgError ?? string.Empty) + stillCoerced,
            };
        }

        // ── PHASE 3: the DELIBERATE undo (IRemediationRollbackExecutor) ─────────────────────────
        //
        // The batch rollback's per-item primitive. It is the P2-leveled rollback lifted out of the
        // verify-fail path and made callable on its own: capture from sys.configurations.value,
        // inverse targets that column, confirming read reads that column. Everything the apply path
        // learned the hard way is carried over deliberately, and each carry is named where it happens:
        // Route A on the inverse, the side-effect restore on every exit after the write, the
        // discriminating confirming read, and the coercion honesty note on a Confirmed result.
        //
        // WHAT IT REFUSES, LOUDLY AND WITHOUT SENDING ANYTHING: a non-sp_configure op (there is no
        // captured inverse for it here), an irreversible template, an unregistered connection, an
        // unrenderable inverse, an unbuildable confirming read, and a guard refusal. Every one of
        // those returns NotAvailable with a NO ROLLBACK sentence naming the reason — the operator is
        // told what could not be undone and why, which is the whole ruling.

        public async Task<RemediationRollbackExecution> RollBackConfigurationAsync(
            RemediationRequest request, int toConfiguredValue, CancellationToken ct = default)
        {
            var t = request.Template;
            var name = t.Operation?.ConfigName;
            var shown = string.IsNullOrWhiteSpace(name) ? "this setting" : $"'{name}'";

            if (t.Operation is null || t.Operation.OpKind != RemediationOpKind.SpConfigure)
                return NoUndo($"'{t.Key}' is not a server configuration setting, so this batch holds no "
                            + "captured value to put back for it. Undo it from its own page.");

            if (!t.Reversible)
                return NoUndo($"'{t.Key}' declares itself not reversible, so {shown} stays as the batch left it.");

            var connString = ResolveConnectionString(request.ServerName);
            if (connString is null)
                return NoUndo($"no connection is registered for '{request.ServerName}', so nothing was sent "
                            + "and nothing was changed.");

            if (!RemediationOpRenderer.TryRender(t.Operation, toConfiguredValue, out var inverseSql, out var renderError))
                return NoUndo($"the statement to put {shown} back to {toConfiguredValue} could not be built "
                            + $"({renderError}), so nothing was sent to the server.");

            // The confirming read is built BEFORE the write, and a failure to build it refuses the
            // whole undo. This path never changes a setting it cannot then read back: an inverse with
            // no confirming read can only ever report Unconfirmed, which is the state this lane exists
            // to stop being the normal answer.
            if (!RemediationOpRenderer.TryRenderConfiguredRead(t.Operation, out var configuredReadSql, out var cfgReadError))
                return NoUndo($"the read that would confirm {shown} went back could not be built "
                            + $"({cfgReadError}), so nothing was sent to the server.");

            RemediationOpRenderer.TryRenderRead(t.Operation, out var inUseReadSql, out _);

            // ROUTE A: the inverse passes the exec-surface wall before it runs, exactly as the
            // apply-time inverse does.
            if (!RemediationExecGuard.Allows(inverseSql, RemediationExecGuard.RenderedInverse, out var guardError))
                return NoUndo(guardError);

            // The inverse's OWN render carries the same 'show advanced options' prelude the apply's
            // did, so it has the same side effect and needs the same capture-and-put-back. Skipping
            // this would leave the undo doing what the P2 fix round proved the apply must not.
            var configuredBefore = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
            foreach (var touched in RenderedConfigurationScan.OptionNamesTouched(inverseSql))
            {
                if (!RemediationOpRenderer.TryRenderConfiguredRead(touched, out var oneRead, out _)) continue;
                try { configuredBefore[touched] = TryParseInt(await ScalarAsync(connString, oneRead, ct).ConfigureAwait(false)); }
                catch (SqlException ex)
                {
                    return NoUndo($"the pre-undo read of '{touched}' failed ({ex.Message}), so nothing was "
                                + "sent to the server.", PermissionDeniedErrors.Contains(ex.Number));
                }
            }

            // ── UNDO BOUNDARY: every return below has sent something and must restore side effects ──
            try
            {
                await ExecuteNonQueryAsync(connString, inverseSql, ct).ConfigureAwait(false);
            }
            catch (SqlException ex)
            {
                var (failNote, _) = await RestoreSideEffectOptionsAsync(
                    connString, inverseSql, t.Operation.ConfigName, configuredBefore, ct).ConfigureAwait(false);
                return new RemediationRollbackExecution
                {
                    State = RemediationRollbackState.Failed,
                    Reason = $"The undo of {shown} ran and FAILED: {ex.Message} The change may still be in "
                           + $"place. Check the server.{failNote}",
                    IsPermissionDenied = PermissionDeniedErrors.Contains(ex.Number),
                };
            }

            // ONE confirming read, on the CONFIGURED column the inverse just wrote, and the value it
            // observed escapes so the result can report what was actually seen rather than restating
            // what was asked for.
            int? observedConfigured = null;
            var (state, mismatch) = await ConfirmRollbackAsync(
                async token =>
                {
                    observedConfigured = TryParseInt(
                        await ScalarAsync(connString, configuredReadSql, token).ConfigureAwait(false));
                    return observedConfigured == toConfiguredValue;
                },
                // ⚠ PLACEHOLDER, REPLACED BELOW. ConfirmRollbackAsync takes the mismatch text as a
                // STRING, so it is built before the read runs — interpolating the observed value
                // here would print the value it had beforehand (null) on every mismatch, which is a
                // sentence that describes nothing. The real one is composed after the read.
                "mismatch",
                ct).ConfigureAwait(false);

            int? observedInUse = null;
            if (!string.IsNullOrWhiteSpace(inUseReadSql))
            {
                try { observedInUse = TryParseInt(await ScalarAsync(connString, inUseReadSql!, ct).ConfigureAwait(false)); }
                catch (SqlException) { /* an honesty field: its absence must not change the verdict */ }
            }

            var (sideNote, _) = await RestoreSideEffectOptionsAsync(
                connString, inverseSql, t.Operation.ConfigName, configuredBefore, ct).ConfigureAwait(false);

            // The coercion note, on the one state that would otherwise over-promise. "Put back" means
            // the configured value is back; if the engine is still running a different number, say so
            // rather than let the word Confirmed imply more than was read (P2 fix-round blocker 3).
            var coercion = string.Empty;
            if (state == RemediationRollbackState.Confirmed && observedInUse is int inUse && inUse != toConfiguredValue)
                coercion = $" The configured value is back at {toConfiguredValue}. The engine is still using "
                         + $"{inUse} (SQL Server coerces this setting or needs a restart).";

            var observedText = observedConfigured.HasValue
                ? observedConfigured.Value.ToString(CultureInfo.InvariantCulture)
                : "no value at all";

            var reason = state switch
            {
                RemediationRollbackState.Confirmed =>
                    $"{shown} was put back to {toConfiguredValue}, and a read of the server confirmed it."
                    + coercion + sideNote,
                // Composed HERE, after the read, so it can name what was actually observed.
                RemediationRollbackState.Failed =>
                    $"The undo of {shown} ran, and a read of the server afterwards showed {observedText} "
                    + $"rather than {toConfiguredValue}. The change may still be in place. Check the server."
                    + sideNote,
                // Unconfirmed keeps ConfirmRollbackAsync's own sentence, which names the read error
                // that produced it — the one thing this method does not know.
                _ => (mismatch ?? string.Empty) + sideNote,
            };

            return new RemediationRollbackExecution
            {
                State = state,
                Reason = reason.Trim(),
                ObservedConfiguredValue = observedConfigured,
                ObservedValueInUse = observedInUse,
            };
        }

        /// <summary>
        /// The ONE way this file reports "there was nothing to run". <see cref="RemediationRollbackState.NotAvailable"/>
        /// plus the marker sentence, so a surface can recognise the state as well as print it, and so
        /// a refusal can never be mistaken for a failed attempt in the audit chain.
        /// </summary>
        private static RemediationRollbackExecution NoUndo(string reason, bool permissionDenied = false) =>
            new()
            {
                State = RemediationRollbackState.NotAvailable,
                Reason = RemediationRollbackProse.NoRollback(reason),
                IsPermissionDenied = permissionDenied,
            };

        /// <summary>
        /// Puts back every option the RENDERED batch wrote other than the template's own target
        /// (Phase-2 item 2.3). Today that is the <c>show advanced options</c> prelude the renderer
        /// emits ahead of any advanced option; the scan is over the rendered text, so a future render
        /// that touches a third option is covered without anybody editing this method.
        ///
        /// <para>Order matters and is not accidental: the prelude is restored AFTER the target,
        /// because turning <c>show advanced options</c> off first would make the target unsettable.
        /// <see cref="RenderedConfigurationScan.SideEffectOptions"/> returns reverse render order for
        /// exactly that reason.</para>
        ///
        /// <para>Never fails the apply. A restore that could not run is reported as a sentence
        /// appended to the outcome, because a side effect left behind is a fact the operator needs,
        /// and turning it into a failed remediation would misreport the fix itself.</para>
        /// </summary>
        /// <returns>A note to append to the outcome message (empty when nothing needed doing), and
        /// the number of options actually restored.</returns>
        private static async Task<(string Note, int Restored)> RestoreSideEffectOptionsAsync(
            string connString, string applySql, string? targetOption,
            IReadOnlyDictionary<string, int?> configuredBefore, CancellationToken ct)
        {
            var note = new StringBuilder();
            int restored = 0;

            foreach (var name in RenderedConfigurationScan.SideEffectOptions(applySql, targetOption))
            {
                if (!configuredBefore.TryGetValue(name, out var before) || before is not int value)
                {
                    note.Append($" The app could not read '{name}' before this change, so it could not put it back. ")
                        .Append("The rendered fix sets it to 1. Check it on the server.");
                    continue;
                }

                int? now;
                try { now = TryParseInt(await ScalarAsync(connString, RenderConfiguredReadOrEmpty(name), ct).ConfigureAwait(false)); }
                catch (SqlException ex)
                {
                    note.Append($" '{name}' could not be re-read after this change, so it was left as the fix set it ({ex.Message}).");
                    continue;
                }

                if (now == value) continue; // the render did not move it (it was already there)

                if (!RemediationOpRenderer.TryRenderConfigureWrite(name, value, out var restoreSql, out var renderErr))
                {
                    note.Append($" '{name}' was changed by this fix and could not be put back ({renderErr}).");
                    continue;
                }
                if (!RemediationExecGuard.Allows(restoreSql, RemediationExecGuard.RenderedSideEffectRestore, out var guardErr))
                {
                    note.Append($" '{name}' was changed by this fix and was not put back. {guardErr}");
                    continue;
                }

                try
                {
                    await ExecuteNonQueryAsync(connString, restoreSql, ct).ConfigureAwait(false);
                    restored++;
                    // ⚠ A SUCCESSFUL RESTORE IS ALSO REPORTED (fix round, gate blocker 2). It used to
                    // be silent, so the only evidence a second server setting had been flipped and
                    // put back was its absence from a failure note. An operator reading "the fix
                    // could not run" is entitled to read, in the same message, which other option
                    // this app touched on their server and where it left it.
                    note.Append($" '{name}' was switched on to make this change possible and has been put back to {value}.");
                }
                catch (SqlException ex)
                {
                    note.Append($" '{name}' was changed by this fix to make the change possible, and putting it back to {value} failed ({ex.Message}).");
                }
            }

            return (note.ToString(), restored);
        }

        // The configured read for a bare option name, or an empty string when the name fails the
        // charset guard. An empty query would throw at the server; the guard has already passed for
        // every name this method sees, because they came out of SQL this app rendered itself.
        private static string RenderConfiguredReadOrEmpty(string name) =>
            RemediationOpRenderer.TryRenderConfiguredRead(name, out var sql, out _) ? sql : string.Empty;

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
                // ROUTE A (Phase-2 item 2.4): every rendered statement passes the exec-surface guard
                // before it runs. A refusal here is a skip with a stated reason, never a silent drop.
                if (!RemediationExecGuard.Allows(applySql, RemediationExecGuard.RenderedFix, out var dbGuardErr))
                {
                    skipped.Add((db, dbGuardErr));
                    continue;
                }
                try { await ExecuteNonQueryAsync(connString, applySql, ct).ConfigureAwait(false); applied.Add(db); }
                catch (Exception applyEx)
                {
                    if (applyEx is OperationCanceledException) throw; // surface cancellation, don't downgrade
                    // No inverse exists for a non-toggle option_sql, so NOTHING runs on this arm.
                    // That is NotAvailable, not Failed: Failed means an inverse action was executed
                    // and did not restore the server, which the runner ledgers as "the rollback
                    // FAILED". Writing that for a statement nobody sent is a fabricated event.
                    var (rbState, rbError) = canInvert
                        ? await RollbackDbSetOptionAsync(connString, applied, invertedOptionSql, op.OffendersQuery, ct).ConfigureAwait(false)
                        : (RemediationRollbackState.NotAvailable,
                           "No rollback was attempted. This fix's database option is not a simple ON/OFF toggle, so there is no inverse to run. "
                           + (applied.Count == 0
                               ? "Nothing had been changed on this run yet."
                               : $"These databases stay changed: {string.Join(", ", applied)}."));
                    if (applyEx is SqlException sqlEx)
                        return new RemediationExecution
                        {
                            Outcome = RemediationOutcome.CouldNotRun,
                            Error = $"db_set_option apply failed on '{db}': {sqlEx.Message}",
                            IsPermissionDenied = PermissionDeniedErrors.Contains(sqlEx.Number),
                            RollbackState = rbState, RollbackError = rbError
                        };
                    return new RemediationExecution
                    {
                        Outcome = RemediationOutcome.CouldNotRun,
                        Error = $"db_set_option apply failed on '{db}': {applyEx.Message}",
                        RollbackState = rbState, RollbackError = rbError
                    };
                }
            }

            // 3b) Every offender skipped? Zero statements executed — this must never read
            //     as an applied/verified change (nothing ran to verify) or commit a credit for
            //     zero work. Fail closed as CouldNotRun (the runner refunds on this outcome) with
            //     the exact per-database reason.
            //
            //     ⚠ THE SENTENCE USED TO NAME THE WRONG CAUSE (lane/remediation-enum-prose-2,
            //     2026-09-02). It said the databases "have unrenderable names", and `skipped` is
            //     filled on TWO paths above, neither of which is only about a name: a
            //     TryRenderDbSetOption failure (the name OR the option clause), and a
            //     RemediationExecGuard refusal of the fully rendered statement, which is about the
            //     statement and not the database at all. An operator told their database names are
            //     the problem goes and looks at their database names. The per-database `reasons`
            //     beside it always carried the truth, so the fix is to stop the lead-in contradicting
            //     it: state the outcome, then hand over to the reasons.
            if (applied.Count == 0)
            {
                var reasons = string.Join("; ", skipped.Select(s => $"{s.Database} ({s.Reason})"));
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.CouldNotRun,
                    Error = $"Nothing was applied. None of the {skipped.Count} offending database(s) could be changed. "
                          + $"Each one is listed here with the reason it was skipped: {reasons}"
                };
            }

            // 4) Verify: re-run the SAME offenders_query (preview==apply==verify parity) and
            //    confirm none of the databases THIS RUN KNEW ABOUT are still offending — both
            //    the ones we applied AND the ones we skipped. Checking only
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
            //    entry that was skipped at step 3 will fail for the same reason here too, and is
            //    honestly reported by RollbackDbSetOptionAsync as a rollback failure — there is
            //    nothing to roll back, and nothing to hide.
            var skippedNames = new HashSet<string>(skipped.Select(s => s.Database), StringComparer.OrdinalIgnoreCase);
            var skippedStillBad = stillBad.Where(skippedNames.Contains).ToList();
            var verifyError = skippedStillBad.Count > 0
                ? $"Post-change verify still finds {stillBad.Count} of {offenders.Count} database(s) offending: {string.Join(", ", stillBad)} " +
                  $"({skippedStillBad.Count} of which were skipped before anything was sent and never attempted: {string.Join(", ", skippedStillBad)})."
                : $"Post-change verify still finds {stillBad.Count} of {applied.Count} database(s) offending: {string.Join(", ", stillBad)}.";
            if (t.Reversible && canInvert)
            {
                var (rbState, rbError) = await RollbackDbSetOptionAsync(connString, stillBad, invertedOptionSql, op.OffendersQuery, ct).ConfigureAwait(false);
                return new RemediationExecution
                {
                    Outcome = RemediationOutcome.AppliedVerifyFailed,
                    Error = verifyError,
                    RollbackState = rbState,
                    RollbackError = rbError
                };
            }
            return new RemediationExecution { Outcome = RemediationOutcome.AppliedVerifyFailed, Error = verifyError };
        }

        // Re-applies the inverted option_sql to each named database — DbSetOption's snapshot-based
        // rollback action (mirrors Configuration's re-apply-old-value rollback, generalised to a
        // per-database loop). Best-effort: a throw on one database doesn't stop the others, so a
        // partial rollback is reported honestly rather than masked.
        //
        // The action completing was previously the whole test, so a clean loop returned "succeeded"
        // with nothing read back. It now re-runs the SAME offenders_query the apply used: every
        // database this method reverted must be OFFENDING again, because "offender" is exactly the
        // pre-apply state (see ExecuteDbSetOptionAsync step 1). A confirm read that throws returns
        // Unconfirmed, never a success.
        //
        // R6 (gate residual, 2026-08-25): a database whose inverse statement could not be RENDERED
        // used to be reported in the same breath as one whose inverse ran and threw — "Rollback
        // failed for: X". Nothing was ever sent for X. That is the fabrication the four-state enum
        // exists to stop, in miniature, and it survived inside this loop because both arms fed one
        // list. The two are separated now and named separately, and where NOTHING rendered the
        // state is NotAvailable (nothing ran) rather than Failed (something ran and did not work).
        // Fail-closed either way: neither state refunds, and both say the change may still be live.
        private async Task<(RemediationRollbackState State, string? Error)> RollbackDbSetOptionAsync(
            string connString, IReadOnlyList<string> databases, string invertedOptionSql,
            string offendersQuery, CancellationToken ct)
        {
            if (databases.Count == 0) return (RemediationRollbackState.NotAttempted, null);
            var neverAttempted = new List<string>();   // the inverse could not be built: nothing was sent
            var ranAndFailed = new List<string>();     // the inverse was sent and threw
            var sent = new List<string>();             // the inverse was sent and did not throw
            foreach (var db in databases)
            {
                if (!RemediationOpRenderer.TryRenderDbSetOption(db, invertedOptionSql, out var sql, out var err))
                {
                    neverAttempted.Add($"{db}: {err}");
                    continue;
                }
                // ROUTE A (Phase-2 item 2.4): the INVERSE passes the same wall the fix did. A
                // refusal here is "nothing was sent for this database", which is neverAttempted —
                // not a failure, because no statement ran.
                if (!RemediationExecGuard.Allows(sql, RemediationExecGuard.RenderedInverse, out var invGuardErr))
                {
                    neverAttempted.Add($"{db}: {invGuardErr}");
                    continue;
                }
                try
                {
                    await ExecuteNonQueryAsync(connString, sql, ct).ConfigureAwait(false);
                    sent.Add(db);
                }
                catch (Exception ex) { ranAndFailed.Add($"{db}: {ex.Message}"); }
            }

            if (DescribeDbSetOptionRollback(neverAttempted, ranAndFailed, sent) is { } incomplete)
                return incomplete;

            return await ConfirmRollbackAsync(
                async token =>
                {
                    var offendingNow = await RowsAsync(connString, offendersQuery, token).ConfigureAwait(false);
                    return sent.All(offendingNow.Contains);
                },
                "Rollback ran, but the reverted database(s) do not read back as pre-apply state.",
                ct).ConfigureAwait(false);
        }

        /// <summary>
        /// The R6 verdict, as a pure function of what the loop above actually did, so it can be
        /// pinned without a server and so the words and the STATE cannot drift apart. Returns null
        /// when every inverse was sent and none threw, which is the only case with anything left to
        /// confirm; the caller then does the confirming read.
        ///
        /// <para>Internal (InternalsVisibleTo SQLTriage.Tests). The three lists are the three facts
        /// that matter and they are kept apart on purpose: a database whose inverse could not be
        /// BUILT had nothing sent for it, and calling that "the rollback failed" is the same
        /// fabrication the four-state enum exists to stop.</para>
        /// </summary>
        internal static (RemediationRollbackState State, string? Error)? DescribeDbSetOptionRollback(
            IReadOnlyList<string> neverAttempted, IReadOnlyList<string> ranAndFailed, IReadOnlyList<string> sent)
        {
            // Nothing was sent at all. No inverse ran, so there is no attempt to report as failed.
            if (sent.Count == 0 && ranAndFailed.Count == 0)
            {
                if (neverAttempted.Count == 0) return null; // nothing to do; the caller confirms nothing
                return (RemediationRollbackState.NotAvailable,
                        $"No rollback was attempted for {DescribeCount(neverAttempted.Count, "database")}. "
                        + "The inverse statement could not be built, so nothing was sent and those databases stay changed. "
                        + "Reason: " + string.Join("; ", neverAttempted));
            }

            if (neverAttempted.Count > 0 || ranAndFailed.Count > 0)
            {
                var parts = new List<string>();
                if (ranAndFailed.Count > 0)
                    parts.Add("The inverse ran and failed for: " + string.Join("; ", ranAndFailed) + ".");
                if (neverAttempted.Count > 0)
                    parts.Add("Nothing was sent for: " + string.Join("; ", neverAttempted)
                              + ". The inverse statement could not be built, so those databases were never attempted.");
                if (sent.Count > 0)
                    parts.Add($"The inverse was sent for: {string.Join(", ", sent)}.");
                return (RemediationRollbackState.Failed, "Rollback is incomplete. " + string.Join(" ", parts));
            }

            return null;
        }

        private static string DescribeCount(int count, string singular) =>
            count == 1 ? $"1 {singular}" : $"{count} {singular}s";

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

        /// <summary>
        /// The single place a rollback's outcome is decided, shared by every rollback site in this
        /// class. The inverse ACTION has already run without throwing when this is called; all
        /// that is left is to read the server back and say what we saw.
        ///
        /// <para>Each site used to open with <c>bool ok = true;</c> and then try the confirming
        /// read inside a <c>catch { }</c> — so a confirm read that THREW left the literal true in
        /// place and the ledger recorded an unqualified "rolled back", Success=True, Error="". The
        /// action completing is not the post-state; only a read is. A throw now yields
        /// <see cref="RemediationRollbackState.Unconfirmed"/>, which is neither a success nor a
        /// failure, and which the audit ledger renders as its own third state.</para>
        ///
        /// <para>Internal (InternalsVisibleTo SQLTriage.Tests) because it is the PRODUCER of the
        /// flag the runner ledgers. The rule that consumes the flag was already tested against a
        /// fake executor that hand-set it; nothing exercised the code that computes it, so the
        /// fail-open default was green and structurally always would be. A test can now drive this
        /// with a confirming read that throws — the exact shape nobody could arrange live, because
        /// it needs a connection to die between a successful inverse and its verification.</para>
        /// </summary>
        /// <param name="confirm">
        /// Reads the server back and returns true iff the pre-change state is restored. May throw;
        /// that is the whole point of this helper.
        /// </param>
        /// <param name="mismatchError">Reported when the read succeeds but shows the wrong state.</param>
        internal static async Task<(RemediationRollbackState State, string? Error)> ConfirmRollbackAsync(
            Func<CancellationToken, Task<bool>> confirm, string mismatchError, CancellationToken ct)
        {
            try
            {
                return await confirm(ct).ConfigureAwait(false)
                    ? (RemediationRollbackState.Confirmed, null)
                    : (RemediationRollbackState.Failed, mismatchError);
            }
            catch (OperationCanceledException) { throw; } // cancellation is not an unconfirmed rollback
            catch (Exception ex)
            {
                return (RemediationRollbackState.Unconfirmed,
                    "The rollback ran without error. The confirming read then failed, so this server's " +
                    "current state is unknown. Check the server before relying on this record. " +
                    $"Read error: {ex.Message}");
            }
        }

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

        /// <param name="sideEffectNote">
        /// What the render's own side effects did, when this failure happened AFTER the batch could
        /// have committed its prelude (fix round, gate blocker 2). A failed apply still leaves
        /// 'show advanced options' flipped unless it is put back, and the operator is entitled to
        /// read which option that was in the same message that tells them the fix did not run.
        /// </param>
        /// <param name="preChangeValue">
        /// The configured value captured before the change, where one was captured. It used to be
        /// dropped on this path, so a failed apply reported no before-state at all.
        /// </param>
        private RemediationExecution PermsAwareFailure(
            SqlException ex, string stage, string? sideEffectNote = null,
            int? preChangeValue = null, int? preChangeValueInUse = null)
        {
            bool perms = PermissionDeniedErrors.Contains(ex.Number);
            _logger.LogWarning(ex, "{Stage} (SQL {Number})", stage, ex.Number);
            return new RemediationExecution
            {
                Outcome = RemediationOutcome.CouldNotRun,
                Error = $"{stage}: {ex.Message}{sideEffectNote}",
                PreChangeValue = preChangeValue,
                PreChangeValueInUse = preChangeValueInUse,
                IsPermissionDenied = perms
            };
        }

        /// <summary>
        /// The ONE shape a preview takes when the target database is an availability-group replica
        /// that is not readable through this connection.
        ///
        /// <para><b>THE INVARIANT: a non-readable AG replica is a SKIP, never a FAILURE.</b> It is a
        /// permanent fact about WHERE WE ARE CONNECTED, identical on every retry, and it must not be
        /// reported as a fault the operator could act on. The skip is still REPORTED -- the reason
        /// sentence names the SQL error number and says what to do instead -- because an unreported
        /// skip is its own defect.</para>
        ///
        /// <para><b>Every preview catch that can see one of these errors returns THIS, not its own
        /// object.</b> The numbers live in <see cref="AvailabilityGroupReadability"/> and the wording
        /// lives in its DescribeSkip; nothing here restates either. The set of catch blocks that must
        /// route through it is enumerated FROM THE SOURCE by
        /// <c>Tests/SQLTriage.Tests/RemediationAgSkipTests.cs</c>
        /// (EveryPreviewSqlExceptionCatchHasAnAvailabilityGroupSkipAheadOfIt), which goes red when a
        /// seventh catch is added without one.</para>
        /// </summary>
        private static RemediationPreview NotReadableSkip(SqlException ex) =>
            new()
            {
                Succeeded = false,
                NotApplicable = true,
                Error = AvailabilityGroupReadability.DescribeSkip(ex),
            };

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
