/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Licensing;
using SQLTriage.Data.Services.Remediation;
using SQLTriage.Tests.Licensing;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    /// <summary>
    /// EXERCISE VEHICLE for the remediation-safety lane (honesty hunt, 2026-08-25). INERT in a
    /// normal <c>dotnet test</c> run: every test early-returns green unless
    /// <c>REMSAFE_LIVE_TARGET</c> names a reachable instance. Same pattern and same reasoning as
    /// <see cref="RemediationDbSetOptionLiveSmokeTests"/>: the REAL executor and the REAL service
    /// are driven, and every claim about the server is checked against an INDEPENDENT
    /// <see cref="SqlConnection"/> read rather than the app's own self-report.
    ///
    /// INVOCATION (gate, on a box with the local test instances):
    ///   $env:REMSAFE_LIVE_TARGET = ".\new2022"
    ///   dotnet test Tests/SQLTriage.Tests --filter "FullyQualifiedName~RemediationSafetyLiveSmokeTests"
    ///
    /// <para>Everything it touches is reversible and restored: one bit-valued sp_configure option
    /// put back to the value it was found at, and one scratch database created and dropped.</para>
    /// </summary>
    public class RemediationSafetyLiveSmokeTests
    {
        private readonly ITestOutputHelper _out;
        public RemediationSafetyLiveSmokeTests(ITestOutputHelper output) => _out = output;

        private const string ScratchDb = "_SQLT_TEST_remsafe_maint";
        private const string RollbackScratchDb = "_SQLT_TEST_remsafe_rollback";
        private const string ToggleTemplateKey = "OPTIMIZEFORADHOC";
        private const string ToggleConfigName = "optimize for ad hoc workloads";

        private static string? Target => Environment.GetEnvironmentVariable("REMSAFE_LIVE_TARGET");

        private static string ConnString(string target, string db = "master") =>
            $"Server={target};Database={db};Integrated Security=true;TrustServerCertificate=true;Connection Timeout=15;";

        private static (DbatoolsRemediationExecutor executor, string auditDir) Wire(string target)
        {
            var connections = new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance);
            connections.AddConnection(new ServerConnection
            {
                ServerNames = target,
                UseWindowsAuthentication = true,
                TrustServerCertificate = true,
                IsEnabled = true,
            });
            var auditDir = Path.Combine(Path.GetTempPath(), "remsafe-livesmoke-" + Guid.NewGuid().ToString("N"));
            var audit = new AuditLogService(auditDir, startFlushTimer: false);
            var executor = new DbatoolsRemediationExecutor(
                new PowerShellService(NullLogger<PowerShellService>.Instance),
                connections, audit,
                new DiskIoService(NullLogger<DiskIoService>.Instance),
                NullLogger<DbatoolsRemediationExecutor>.Instance);
            return (executor, auditDir);
        }

        private static int ReadConfigIndependently(string target, string configName)
        {
            using var conn = new SqlConnection(ConnString(target));
            conn.Open();
            using var cmd = new SqlCommand("SELECT value_in_use FROM sys.configurations WHERE name = @n;", conn);
            cmd.Parameters.AddWithValue("@n", configName);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        private static void SetConfigIndependently(string target, string configName, int value)
        {
            using var conn = new SqlConnection(ConnString(target));
            conn.Open();
            using var cmd = new SqlCommand(
                "EXEC sp_configure 'show advanced options', 1; RECONFIGURE; " +
                $"EXEC sp_configure '{configName}', {value}; RECONFIGURE;", conn)
            { CommandTimeout = 30 };
            cmd.ExecuteNonQuery();
        }

        private static void Exec(string target, string sql, string db = "master")
        {
            using var conn = new SqlConnection(ConnString(target, db));
            conn.Open();
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
            cmd.ExecuteNonQuery();
        }

        // ── Cluster 1 (r2-01): a compliant server is not regressed, and is not charged ──

        [Fact]
        public async Task ApplyingTheOldShippedFloorToACompliantServer_IsRefused_AndTheServerIsUnchanged()
        {
            var target = Target;
            if (string.IsNullOrWhiteSpace(target)) { _out.WriteLine("REMSAFE_LIVE_TARGET unset - inert."); return; }

            var original = ReadConfigIndependently(target, ToggleConfigName);
            _out.WriteLine($"Live '{ToggleConfigName}' on {target} before: {original}");

            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var template = store.TryGet(ToggleTemplateKey)!;
            var recommended = template.Operation!.RecommendedValue!.Value;
            var (executor, _) = Wire(target);

            try
            {
                // Put the server in the COMPLIANT state this fix exists to reach.
                if (original != recommended) SetConfigIndependently(target, ToggleConfigName, recommended);
                Assert.Equal(recommended, ReadConfigIndependently(target, ToggleConfigName));

                // The old shipped default was Operation.MinValue. Ask for exactly that.
                var floor = template.Operation.MinValue;
                Assert.NotEqual(recommended, floor); // the defect: the floor is the opposite of the fix

                var request = new RemediationRequest(template, target,
                    new Dictionary<string, string> { [template.Operation.ValueParam] = floor.ToString() });

                var preview = await executor.PreviewAsync(request);
                Assert.False(preview.Succeeded);
                Assert.Contains("already at the recommended value", preview.Error!);
                _out.WriteLine("Preview refused: " + preview.Error);

                var execution = await executor.ExecuteAsync(request);
                Assert.Equal(RemediationOutcome.CouldNotRun, execution.Outcome);
                Assert.Contains("already at the recommended value", execution.Error!);
                _out.WriteLine("Execute refused: " + execution.Error);

                // INDEPENDENT proof the server was not written to.
                Assert.Equal(recommended, ReadConfigIndependently(target, ToggleConfigName));

                // CouldNotRun is a refunding outcome in the runner, so a server that needed nothing
                // is never charged. Pinned here rather than assumed.
                Assert.NotEqual(RemediationOutcome.AppliedVerified, execution.Outcome);
                Assert.NotEqual(RemediationOutcome.AppliedVerifyFailed, execution.Outcome);
            }
            finally
            {
                if (ReadConfigIndependently(target, ToggleConfigName) != original)
                    SetConfigIndependently(target, ToggleConfigName, original);
                _out.WriteLine($"Restored '{ToggleConfigName}' to {original}.");
            }
        }

        [Fact]
        public async Task ADeliberateRevertOffTheRecommendedValue_IsStillAllowed_AndReallyWrites()
        {
            var target = Target;
            if (string.IsNullOrWhiteSpace(target)) { _out.WriteLine("REMSAFE_LIVE_TARGET unset - inert."); return; }

            var original = ReadConfigIndependently(target, ToggleConfigName);
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var template = store.TryGet(ToggleTemplateKey)!;
            var recommended = template.Operation!.RecommendedValue!.Value;
            var floor = template.Operation.MinValue;
            var (executor, _) = Wire(target);

            try
            {
                if (original != recommended) SetConfigIndependently(target, ToggleConfigName, recommended);

                // The Undo / Revert-to-history path passes the acknowledgement. The guard must not
                // block an operator putting a setting back the way it was.
                //
                // RULING 7 (2026-08-25) added the second half: an acknowledgement carries the
                // operator's stated reason. Both revert routes state theirs by construction, and
                // this is the sentence Pages/Remediation.razor sends for the in-session Undo.
                var acked = new RemediationRequest(template, target, new Dictionary<string, string>
                {
                    [template.Operation.ValueParam] = floor.ToString(),
                    [RemediationOpRenderer.AcknowledgeRegressionParam] = "true",
                    [RemediationOpRenderer.RegressionIntentParam] =
                        "Undo of the change SQLTriage applied in this session.",
                });

                var execution = await executor.ExecuteAsync(acked);
                Assert.Equal(RemediationOutcome.AppliedVerified, execution.Outcome);
                Assert.Equal(floor, ReadConfigIndependently(target, ToggleConfigName)); // independent
                _out.WriteLine($"Acknowledged revert wrote {floor} and verified.");
            }
            finally
            {
                if (ReadConfigIndependently(target, ToggleConfigName) != original)
                    SetConfigIndependently(target, ToggleConfigName, original);
                _out.WriteLine($"Restored '{ToggleConfigName}' to {original}.");
            }
        }

        // ── Cluster 2 (r2-03): the confirm read really dies, and the verdict is Unconfirmed ──

        [Fact]
        public async Task AConfirmingReadOnAKilledConnection_ReadsAsUnconfirmed_NotAsASuccess()
        {
            var target = Target;
            if (string.IsNullOrWhiteSpace(target)) { _out.WriteLine("REMSAFE_LIVE_TARGET unset - inert."); return; }

            // The shape the hunt could not arrange: the inverse action completes, then the
            // connection dies BEFORE the confirming read lands. Built here for real.
            //
            // Two details make it a genuine repro rather than a mime, each learned by a failed
            // attempt. ConnectRetryCount=0 turns off SqlClient's connection resiliency, which
            // otherwise REPLACES the dead connection and hides the loss entirely (attempt one came
            // back Confirmed). And the KILL lands while the confirming read is IN FLIGHT (a WAITFOR
            // holds it open), because KILLing an idle session only marks it and the next command
            // quietly re-opens.
            var victimConnString = ConnString(target) + "ConnectRetryCount=0;";
            using var victim = new SqlConnection(victimConnString);
            await victim.OpenAsync();

            int spid;
            using (var spidCmd = new SqlCommand("SELECT @@SPID;", victim))
                spid = Convert.ToInt32(await spidCmd.ExecuteScalarAsync());

            // A third detail, learned by measurement rather than by reading: this raced. Over four
            // runs of this class it came back Confirmed once, because opening the killer's OWN
            // connection was inside the timed window, and a cold connect can eat the whole 6 s the
            // victim's WAITFOR holds open. A probe that reports the honest answer most of the time
            // is not a probe. The connection is opened FIRST, so only the KILL statement is on the
            // clock, and the window is 20 s rather than 6.
            using var killerConn = new SqlConnection(ConnString(target));
            await killerConn.OpenAsync();

            var killer = Task.Run(async () =>
            {
                await Task.Delay(1200);
                using var killCmd = new SqlCommand($"KILL {spid};", killerConn);
                await killCmd.ExecuteNonQueryAsync();
            });

            var (state, error) = await DbatoolsRemediationExecutor.ConfirmRollbackAsync(
                async ct =>
                {
                    // In flight for ~20 s; the KILL arrives about a second in.
                    using var cmd = new SqlCommand("WAITFOR DELAY '00:00:20'; SELECT 1;", victim) { CommandTimeout = 60 };
                    return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1;
                },
                "the rollback did not restore the pre-change state",
                CancellationToken.None);

            await killer;
            _out.WriteLine($"KILLed spid {spid} mid-statement from a second connection.");

            _out.WriteLine($"Confirm read after the kill: state={state}, error={error}");
            Assert.Equal(RemediationRollbackState.Unconfirmed, state);
            Assert.NotEqual(RemediationRollbackState.Confirmed, state);
            Assert.Contains("unknown", error!, StringComparison.OrdinalIgnoreCase);

            // And the derived flag the ledger reads is false, not true.
            var exec = new RemediationExecution { Outcome = RemediationOutcome.AppliedVerifyFailed, RollbackState = state };
            Assert.True(exec.RolledBack);
            Assert.False(exec.RollbackSucceeded);
        }

        [Fact]
        public async Task AnApplyFailureWithNoInvertibleOption_ReportsNoRollbackAttempt_NotAFailedRollback()
        {
            var target = Target;
            if (string.IsNullOrWhiteSpace(target)) { _out.WriteLine("REMSAFE_LIVE_TARGET unset - inert."); return; }

            // The lane's own regression, exercised for real. A db_set_option whose option_sql is
            // not a simple ON/OFF toggle has NO inverse, so when the apply throws, nothing is run
            // to undo it. That used to be reported as RollbackState.Failed, which the runner
            // ledgers as "Rollback of remediation ... FAILED" at Error severity, about a statement
            // nobody sent.
            //
            // Built on a scratch database only. "SET PAGE_VERIFY BOGUS" passes the renderer's
            // charset guard, is not invertible (BOGUS is not ON/OFF), and is rejected by the
            // server's parser, so the apply throws and NOTHING on the instance changes.
            Exec(target, $"IF DB_ID(N'{RollbackScratchDb}') IS NOT NULL BEGIN ALTER DATABASE [{RollbackScratchDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{RollbackScratchDb}]; END; CREATE DATABASE [{RollbackScratchDb}];");
            try
            {
                var before = ReadPageVerifyIndependently(target, RollbackScratchDb);
                _out.WriteLine($"page_verify_option_desc on {RollbackScratchDb} before: {before}");

                var op = new RemediationOperation
                {
                    OpKind = RemediationOpKind.DbSetOption,
                    OptionSql = "SET PAGE_VERIFY BOGUS",
                    OffendersQuery = $"SELECT name FROM sys.databases WHERE name = '{RollbackScratchDb}';",
                };
                Assert.False(RemediationOpRenderer.TryInvertBooleanOptionSql(op.OptionSql, out _));

                var template = new RemediationTemplate
                {
                    Key = "TEST-NONINVERTIBLE-DBSETOPTION-LIVE",
                    DisplayName = "Test non-invertible db_set_option (live smoke)",
                    Kind = RemediationKind.Configuration,
                    Operation = op,
                    Reversible = true,
                };

                var (executor, _) = Wire(target);
                var execution = await executor.ExecuteAsync(new RemediationRequest(template, target));
                _out.WriteLine($"Outcome={execution.Outcome} RollbackState={execution.RollbackState} RolledBack={execution.RolledBack}");
                _out.WriteLine($"RollbackError: {execution.RollbackError}");

                Assert.Equal(RemediationOutcome.CouldNotRun, execution.Outcome);
                Assert.Equal(RemediationRollbackState.NotAvailable, execution.RollbackState);
                Assert.NotEqual(RemediationRollbackState.Failed, execution.RollbackState);
                Assert.False(execution.RolledBack); // no ledger entry is written for this
                Assert.False(execution.RollbackSucceeded);
                Assert.Contains("No rollback was attempted", execution.RollbackError!, StringComparison.Ordinal);

                // INDEPENDENT proof nothing on the server moved.
                Assert.Equal(before, ReadPageVerifyIndependently(target, RollbackScratchDb));
            }
            finally
            {
                Exec(target, $"IF DB_ID(N'{RollbackScratchDb}') IS NOT NULL BEGIN ALTER DATABASE [{RollbackScratchDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{RollbackScratchDb}]; END;");
                _out.WriteLine($"Dropped {RollbackScratchDb}.");
            }
        }

        private static string ReadPageVerifyIndependently(string target, string database)
        {
            using var conn = new SqlConnection(ConnString(target));
            conn.Open();
            using var cmd = new SqlCommand(
                "SELECT page_verify_option_desc FROM sys.databases WHERE name = @n;", conn);
            cmd.Parameters.AddWithValue("@n", database);
            return Convert.ToString(cmd.ExecuteScalar()) ?? string.Empty;
        }

        // ── Cluster 3 (r1-02): preview and apply now read the same five objects ──

        [Fact]
        public async Task AScratchDatabaseWithTheFourProcsAndNoCommandLog_IsPartiallyPresent_NotANoOp()
        {
            var target = Target;
            if (string.IsNullOrWhiteSpace(target)) { _out.WriteLine("REMSAFE_LIVE_TARGET unset - inert."); return; }

            Exec(target, $"IF DB_ID(N'{ScratchDb}') IS NOT NULL BEGIN ALTER DATABASE [{ScratchDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{ScratchDb}]; END; CREATE DATABASE [{ScratchDb}];");
            try
            {
                // The exact live shape the hunt built: the four Ola proc names present, CommandLog absent.
                foreach (var proc in MaintenanceSolutionOpRenderer.CoreProcNames)
                    Exec(target, $"CREATE PROCEDURE dbo.{proc} AS SELECT 1;", ScratchDb);

                // The OLD preview probe: 4 procs present, so it answers "already installed".
                int oldProbe;
                using (var conn = new SqlConnection(ConnString(target, ScratchDb)))
                {
                    await conn.OpenAsync();
                    using var cmd = new SqlCommand(MaintenanceSolutionOpRenderer.ProcsExistProbe, conn);
                    oldProbe = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                }
                Assert.Equal(1, oldProbe); // the divergence, still measurable on a live server
                _out.WriteLine("Old 4-proc preview probe answers 1 (already installed).");

                // The provenance snapshot both sides now read.
                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var conn = new SqlConnection(ConnString(target, ScratchDb)))
                {
                    await conn.OpenAsync();
                    using var cmd = new SqlCommand(MaintenanceSolutionOpRenderer.RenderProcsProvenanceSnapshot(), conn);
                    using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync()) present.Add(reader.GetString(0));
                }
                _out.WriteLine("Provenance snapshot: " + string.Join(", ", present.OrderBy(x => x)));

                Assert.Equal(4, present.Count);
                Assert.DoesNotContain(MaintenanceSolutionOpRenderer.CommandLogTableName, present);

                // One classification, so preview and apply cannot disagree any more.
                Assert.Equal(MaintenanceSolutionOpRenderer.InstallPresence.PartiallyPresent,
                    MaintenanceSolutionOpRenderer.Classify(present));

                // And the uninstall would not clean up an overwrite of these four.
                var uninstall = MaintenanceSolutionOpRenderer.RenderUninstallSql(present);
                foreach (var proc in MaintenanceSolutionOpRenderer.CoreProcNames)
                    Assert.DoesNotContain($"DROP PROCEDURE dbo.{proc}", uninstall);
            }
            finally
            {
                Exec(target, $"IF DB_ID(N'{ScratchDb}') IS NOT NULL BEGIN ALTER DATABASE [{ScratchDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{ScratchDb}]; END;");
                _out.WriteLine($"Dropped {ScratchDb}.");
            }
        }

        // ── Cluster 4 (r1-03/r2-02): a preview whose batches all fail is not a clean preview ──

        [Fact]
        public async Task APreviewWhoseEveryBatchFails_ReportsBatchErrors_AndDoesNotSucceed()
        {
            var target = Target;
            if (string.IsNullOrWhiteSpace(target)) { _out.WriteLine("REMSAFE_LIVE_TARGET unset - inert."); return; }

            var accessor = new FakeBundleAccessor
            {
                Tier = Tier.Full,
                Features = new BundleFeatures(false, false, false, Array.Empty<int>(), Remediation: true),
            };
            var service = new ServerConfigScriptService(
                null!, NullLogger<ServerConfigScriptService>.Instance,
                new BundleBackedRemediationCapability(accessor), accessor);

            // Stand a crafted script in for the shipped one IN THE TEST OUTPUT ONLY, byte-for-byte
            // restored afterwards. Same technique the hunt's reproduce pass used.
            var scriptPath = service.ScriptPath;
            if (!File.Exists(scriptPath)) { _out.WriteLine($"Script missing at {scriptPath} - inert."); return; }
            var originalBytes = await File.ReadAllBytesAsync(scriptPath);

            var outDir = Path.Combine(Path.GetTempPath(), "remsafe-preview-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outDir);
            try
            {
                await File.WriteAllTextAsync(scriptPath,
                    "DECLARE @ForChangeControl BIT = 0;\r\nGO\r\nSELECT * FROM dbo.ZZ_NoSuchTable_RemSafe;\r\nGO\r\nRAISERROR('crafted batch failure', 16, 1);\r\nGO\r\n");

                var result = await service.RunPreviewForInstanceAsync("remsafe-live", ConnString(target), outDir);

                _out.WriteLine($"Refused={result.Refused} Reason={result.RefusalReason} Rows={result.Rows.Count} " +
                               $"BatchErrors={result.BatchErrors?.Count} Succeeded={result.Succeeded}");
                foreach (var e in result.BatchErrors ?? (IReadOnlyList<string>)Array.Empty<string>())
                    _out.WriteLine("  " + e);

                // The old shape: not refused, no reason, zero rows, and reported as a clean preview.
                Assert.False(result.Refused);
                Assert.Empty(result.Rows);
                // The fix: the errors are carried, and the verdict says so.
                Assert.NotNull(result.BatchErrors);
                Assert.NotEmpty(result.BatchErrors!);
                Assert.False(result.Succeeded);

                // The export names them too, rather than printing the benign "no rows captured".
                Assert.NotNull(result.ExportedPath);
                var exported = await File.ReadAllTextAsync(result.ExportedPath!);
                Assert.Contains("ZZ_NoSuchTable_RemSafe", exported);
            }
            finally
            {
                await File.WriteAllBytesAsync(scriptPath, originalBytes);
                try { Directory.Delete(outDir, recursive: true); } catch { /* cleanup */ }
                _out.WriteLine("Restored the shipped script byte-for-byte.");
            }
        }

        // ── Cluster 5 (r1-04): the multi-instance apply keeps its rollback script ──

        [Fact]
        public async Task AMultiInstanceApply_WritesTheRollbackScript_TheSingleInstancePathAlwaysWrote()
        {
            var target = Target;
            if (string.IsNullOrWhiteSpace(target)) { _out.WriteLine("REMSAFE_LIVE_TARGET unset - inert."); return; }

            var accessor = new FakeBundleAccessor
            {
                Tier = Tier.Full,
                Features = new BundleFeatures(false, false, false, Array.Empty<int>(), Remediation: true),
            };
            var service = new ServerConfigScriptService(
                null!, NullLogger<ServerConfigScriptService>.Instance,
                new BundleBackedRemediationCapability(accessor), accessor);

            // Same technique as the r1-03 probe above: a crafted script stands in for the shipped one
            // IN THE TEST OUTPUT ONLY, restored byte-for-byte afterwards. It is deliberately all
            // SELECTs, so an APPLY run of it changes nothing on the server.
            var scriptPath = service.ScriptPath;
            if (!File.Exists(scriptPath)) { _out.WriteLine($"Script missing at {scriptPath} - inert."); return; }
            var originalBytes = await File.ReadAllBytesAsync(scriptPath);

            var outDir = Path.Combine(Path.GetTempPath(), "remsafe-rollback-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outDir);
            try
            {
                await File.WriteAllTextAsync(scriptPath,
                    "DECLARE @ForChangeControl BIT = 0;\r\nGO\r\n" +
                    // The 8-column #ChangeControlReport shape the service captures as rows.
                    "SELECT CAST(1 AS INT) AS ID, CAST(SYSUTCDATETIME() AS DATETIME) AS Captured, " +
                    "CAST('IMPLEMENTING' AS NVARCHAR(30)) AS Mode, CAST('Section' AS NVARCHAR(60)) AS Section, " +
                    "CAST('Setting' AS NVARCHAR(60)) AS Setting, CAST('0' AS NVARCHAR(60)) AS CurrentValue, " +
                    "CAST('1' AS NVARCHAR(60)) AS TargetValue, CAST(NULL AS NVARCHAR(200)) AS Detail;\r\nGO\r\n" +
                    // The 2-column rollback shape the multi-instance lane used to discard.
                    "SELECT CAST(@@SERVERNAME AS NVARCHAR(200)) AS ServerName, " +
                    "CAST('-- ZZ remsafe rollback probe\r\nEXEC sys.sp_configure ''show advanced options'', 1;' AS NVARCHAR(MAX)) AS RollbackScript;\r\nGO\r\n");

                var result = await service.RunApplyForInstanceAsync("remsafe-live", ConnString(target), outDir);

                _out.WriteLine($"Refused={result.Refused} Rows={result.Rows.Count} Succeeded={result.Succeeded} " +
                               $"RollbackPath={result.RollbackScriptPath} RollbackError={result.RollbackScriptError}");
                _out.WriteLine("Note: " + result.RollbackScriptNote);

                Assert.False(result.Refused);
                Assert.True(result.Succeeded);

                // The assertion the hunt's probe PASSED before this fix, meaning nothing was written.
                var rollbackFiles = Directory.GetFiles(outDir, "*_rollback.sql");
                Assert.Single(rollbackFiles);
                Assert.Equal(rollbackFiles[0], result.RollbackScriptPath);
                Assert.Contains("ZZ remsafe rollback probe", await File.ReadAllTextAsync(rollbackFiles[0]));
                Assert.Null(result.RollbackScriptError);

                // And the export names it, so the file is discoverable from the record.
                Assert.NotNull(result.ExportedPath);
                Assert.Contains("_rollback.sql", await File.ReadAllTextAsync(result.ExportedPath!), StringComparison.Ordinal);
            }
            finally
            {
                await File.WriteAllBytesAsync(scriptPath, originalBytes);
                try { Directory.Delete(outDir, recursive: true); } catch { /* cleanup */ }
                _out.WriteLine("Restored the shipped script byte-for-byte.");
            }
        }

        // ── Cluster 11 (r2-06): the Backup-Now refusal names the BACKUP drive's own problem ──

        /// <summary>
        /// The resource-gate line of a preview, or null when the run never reached gate 2.
        /// <para>An earlier gate (the database-state probe, the size estimate) can fail on a cold
        /// or busy instance, and that returns a preview with an Error and NO WhatIfText at all.
        /// That is a transient environment failure, not the property under test, so the caller
        /// treats it as inert rather than reporting a defect it did not observe. A preview that
        /// DOES render and still has no gate line is a real failure and is returned as null.</para>
        /// </summary>
        private bool TryGetResourceGateLine(RemediationPreview preview, out string? gateLine)
        {
            gateLine = null;
            if (string.IsNullOrWhiteSpace(preview.WhatIfText))
            {
                _out.WriteLine("INERT: the preview never reached the resource gate. Error: " + preview.Error);
                return false;
            }
            gateLine = preview.WhatIfText
                .Split('\n')
                .FirstOrDefault(l => l.StartsWith("Resource gate:", StringComparison.Ordinal));
            return true;
        }

        /// <summary>
        /// The finding's own reproduce pass said this branch "needs ... the running app" and was
        /// never exercised. It does not: it needs a REGISTERED CONNECTION and a live DiskIoService
        /// snapshot, which this harness builds directly. Read-only against the target — a preview
        /// runs SELECTs, and the backup directory it names is unreachable by construction, so no
        /// BACKUP statement is ever rendered for apply.
        /// </summary>
        [Fact]
        public async Task BackupPreview_CannotMeasureTheBackupDrive_StatesTheBackupDrivesOwnReason()
        {
            var target = Target;
            if (string.IsNullOrWhiteSpace(target)) { _out.WriteLine("REMSAFE_LIVE_TARGET unset - inert."); return; }

            var (executor, auditDir) = Wire(target);
            try
            {
                var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
                var template = store.TryGet("BACKUPDATABASENOW");
                Assert.NotNull(template);

                // A UNC backup directory: real syntax an operator types, no drive letter to measure.
                // 'master' is ONLINE on any reachable instance, so gate 1 passes and the run reaches
                // the resource gate — the branch under test.
                const string Directory_ = @"\\no-such-backup-host\sqlbackups";
                var request = new RemediationRequest(template!, target, new Dictionary<string, string>
                {
                    [BackupCheckDbOpRenderer.DatabaseNameParam] = "master",
                    [BackupCheckDbOpRenderer.AllowSystemDatabaseParam] = "true",
                    [BackupCheckDbOpRenderer.BackupDirectoryParam] = Directory_,
                    [BackupCheckDbOpRenderer.ConfirmLargeOperationParam] = "true",
                });

                var preview = await executor.PreviewAsync(request, CancellationToken.None);
                _out.WriteLine("Preview.Succeeded=" + preview.Succeeded);
                _out.WriteLine(preview.WhatIfText);

                // Fails closed: an unmeasurable backup drive is a refusal, not an assumption.
                Assert.False(preview.Succeeded);

                if (!TryGetResourceGateLine(preview, out var gateLine)) return;
                Assert.False(string.IsNullOrWhiteSpace(gateLine), "No resource-gate line in the preview.");

                // 1. It names the directory the operator actually gave.
                Assert.Contains(Directory_, gateLine!, StringComparison.Ordinal);
                // 2. It states a reason. Before the fix this ended in a dangling sentence: the
                //    reason slot held ResolveDataDriveAsync's error, and on a box where the data
                //    drive resolves fine that error is null.
                Assert.Contains("Could not determine a drive letter", gateLine!, StringComparison.Ordinal);
                // 3. It is not about the database's data files. That is the wrong volume.
                Assert.DoesNotContain("data-file", gateLine!, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("file inventory", gateLine!, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { Directory.Delete(auditDir, recursive: true); } catch { /* cleanup */ }
            }
        }

        /// <summary>
        /// The same server, a drive letter that is genuinely absent from this instance's disk
        /// inventory. Proves the resolver reaches a LIVE DiskIoService snapshot and refuses on the
        /// evidence, rather than refusing at the string parse in the test above.
        /// </summary>
        [Fact]
        public async Task BackupPreview_DriveLetterNotInTheLiveInventory_IsRefusedByName()
        {
            var target = Target;
            if (string.IsNullOrWhiteSpace(target)) { _out.WriteLine("REMSAFE_LIVE_TARGET unset - inert."); return; }

            var (executor, auditDir) = Wire(target);
            try
            {
                var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
                var template = store.TryGet("BACKUPDATABASENOW");
                Assert.NotNull(template);

                var request = new RemediationRequest(template!, target, new Dictionary<string, string>
                {
                    [BackupCheckDbOpRenderer.DatabaseNameParam] = "master",
                    [BackupCheckDbOpRenderer.AllowSystemDatabaseParam] = "true",
                    [BackupCheckDbOpRenderer.BackupDirectoryParam] = @"Q:\sqlbackups",
                    [BackupCheckDbOpRenderer.ConfirmLargeOperationParam] = "true",
                });

                var preview = await executor.PreviewAsync(request, CancellationToken.None);
                _out.WriteLine("Preview.Succeeded=" + preview.Succeeded);
                _out.WriteLine(preview.WhatIfText);

                Assert.False(preview.Succeeded);
                if (!TryGetResourceGateLine(preview, out var gateLine)) return;
                Assert.False(string.IsNullOrWhiteSpace(gateLine), "No resource-gate line in the preview.");
                Assert.Contains(@"Q:\sqlbackups", gateLine!, StringComparison.Ordinal);
                // The resolver's OWN sentence, not the caller's framing. The pre-fix text said
                // "the target backup directory's drive ('Q:')" and then left the reason slot empty,
                // so this substring is the discriminator between a stated reason and a dangling one.
                Assert.Contains("Could not determine free space for drive 'Q:'", gateLine!, StringComparison.Ordinal);
                Assert.DoesNotContain("data-file", gateLine!, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { Directory.Delete(auditDir, recursive: true); } catch { /* cleanup */ }
            }
        }
    }
}
