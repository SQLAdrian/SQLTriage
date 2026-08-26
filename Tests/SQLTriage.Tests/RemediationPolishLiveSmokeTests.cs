/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    /// <summary>
    /// EXERCISE VEHICLE for the polish round (DECISIONS 2026-08-25 17:33). Same contract as
    /// <see cref="RemediationSafetyLiveSmokeTests"/>: INERT in a normal <c>dotnet test</c> run,
    /// every test early-returns green unless <c>REMSAFE_LIVE_TARGET</c> names a reachable instance.
    /// The REAL executor, the REAL runner and the REAL services are driven, and every claim about
    /// the server is checked against an INDEPENDENT <see cref="SqlConnection"/> read.
    ///
    /// INVOCATION:
    ///   $env:REMSAFE_LIVE_TARGET = ".\new2022"
    ///   dotnet test Tests/SQLTriage.Tests --filter "FullyQualifiedName~RemediationPolishLiveSmokeTests"
    ///
    /// <para>Everything it touches is reversible and restored: one bit-valued sp_configure option
    /// put back to the value it was found at, one scratch database created and dropped, and one
    /// stub procedure created in master and dropped.</para>
    /// </summary>
    public class RemediationPolishLiveSmokeTests
    {
        private readonly ITestOutputHelper _out;
        public RemediationPolishLiveSmokeTests(ITestOutputHelper output) => _out = output;

        private const string ScratchDb = "_SQLT_TEST_polish_rollback";
        private const string ToggleTemplateKey = "OPTIMIZEFORADHOC";
        private const string ToggleConfigName = "optimize for ad hoc workloads";

        private static string? Target => Environment.GetEnvironmentVariable("REMSAFE_LIVE_TARGET");

        private static string ConnString(string target, string db = "master") =>
            $"Server={target};Database={db};Integrated Security=true;TrustServerCertificate=true;Connection Timeout=15;";

        private sealed class GrantedCapability : IRemediationCapability { public bool IsGranted => true; }

        private static (DbatoolsRemediationExecutor Executor, AuditLogService Audit, string AuditDir) Wire(string target)
        {
            var connections = new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance);
            connections.AddConnection(new ServerConnection
            {
                ServerNames = target,
                UseWindowsAuthentication = true,
                TrustServerCertificate = true,
                IsEnabled = true,
            });
            var auditDir = Path.Combine(Path.GetTempPath(), "remsafe-polish-live-" + Guid.NewGuid().ToString("N"));
            var audit = new AuditLogService(auditDir, startFlushTimer: false);
            var executor = new DbatoolsRemediationExecutor(
                new PowerShellService(NullLogger<PowerShellService>.Instance),
                connections, audit,
                new DiskIoService(NullLogger<DiskIoService>.Instance),
                NullLogger<DbatoolsRemediationExecutor>.Instance);
            return (executor, audit, auditDir);
        }

        private static ServerSizingService WireSizing(string target)
        {
            var connections = new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance);
            connections.AddConnection(new ServerConnection
            {
                ServerNames = target,
                UseWindowsAuthentication = true,
                TrustServerCertificate = true,
                IsEnabled = true,
            });
            return new ServerSizingService(connections, NullLogger<ServerSizingService>.Instance);
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
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
            cmd.ExecuteNonQuery();
        }

        private static List<AuditLogEntry> ReadAuditEntries(string auditDir) =>
            Directory.GetFiles(auditDir, "audit-*.jsonl")
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .SelectMany(File.ReadAllLines)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => System.Text.Json.JsonSerializer.Deserialize<AuditLogEntry>(l)!)
                .ToList();

        // ── Ruling 1: the pre-fill's host read, against a real server ────────────

        [Fact]
        public async Task TheHostSizingRead_AgreesWithAnIndependentDmvRead_AndProducesThePreFills()
        {
            var target = Target;
            if (string.IsNullOrWhiteSpace(target)) { _out.WriteLine("REMSAFE_LIVE_TARGET unset - inert."); return; }

            var facts = await WireSizing(target).ReadAsync(target);
            _out.WriteLine($"ServerSizingService read {target}: cores={facts.LogicalCpuCount} numa={facts.NumaNodeCount} ramMB={facts.PhysicalMemoryMb}");

            // INDEPENDENT read of the same DMVs through a second connection the service never saw.
            int cores, ramMb;
            using (var conn = new SqlConnection(ConnString(target)))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand(
                    "SELECT cpu_count, physical_memory_kb / 1024 FROM sys.dm_os_sys_info;", conn);
                using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                cores = reader.GetInt32(0);
                ramMb = Convert.ToInt32(reader.GetValue(1));
            }
            _out.WriteLine($"Independent DMV read: cores={cores} ramMB={ramMb}");

            Assert.Equal(cores, facts.LogicalCpuCount);
            Assert.Equal(ramMb, facts.PhysicalMemoryMb);
            Assert.NotNull(facts.NumaNodeCount);

            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var maxDop = RemediationRecommendedValues.For(store.TryGet("MAXDOP"), facts);
            var ctfp = RemediationRecommendedValues.For(store.TryGet("CTFP"), facts);
            var maxMem = RemediationRecommendedValues.For(store.TryGet("MAXSERVERMEMORY"), facts);
            _out.WriteLine($"Pre-fills this server would render: MAXDOP={maxDop} CTFP={ctfp} MAXSERVERMEMORY={maxMem}");

            Assert.Equal(50, ctfp);
            // The three pre-fills, whatever they come out as, must be inside their own template's
            // bounds and must never be the schema floor that was the original defect.
            Assert.NotEqual(store.TryGet("MAXSERVERMEMORY")!.Operation!.MinValue, maxMem);
            if (maxDop is int md)
            {
                Assert.InRange(md, store.TryGet("MAXDOP")!.Operation!.MinValue, store.TryGet("MAXDOP")!.Operation!.MaxValue);
                Assert.Equal(RemediationRecommendedValues.MaxDop(facts), md);
            }
        }

        // ── Ruling 7: the acknowledged off-recommended route, on a live server ───

        [Fact]
        public async Task ATickWithNoStatedReason_IsRefusedLive_AndTheServerIsUnchanged()
        {
            var target = Target;
            if (string.IsNullOrWhiteSpace(target)) { _out.WriteLine("REMSAFE_LIVE_TARGET unset - inert."); return; }

            var original = ReadConfigIndependently(target, ToggleConfigName);
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var template = store.TryGet(ToggleTemplateKey)!;
            var recommended = template.Operation!.RecommendedValue!.Value;
            var floor = template.Operation.MinValue;
            var (executor, _, _) = Wire(target);

            try
            {
                if (original != recommended) SetConfigIndependently(target, ToggleConfigName, recommended);

                // The tick, with no stated reason. Ruling 7 requires both halves; this is the arm
                // that used to be enough on its own.
                var tickOnly = new RemediationRequest(template, target, new Dictionary<string, string>
                {
                    [template.Operation.ValueParam] = floor.ToString(),
                    [RemediationOpRenderer.AcknowledgeRegressionParam] = "true",
                });

                var preview = await executor.PreviewAsync(tickOnly);
                Assert.False(preview.Succeeded);
                Assert.Contains(RemediationOpRenderer.RegressionRefusalMarker, preview.Error!, StringComparison.Ordinal);
                _out.WriteLine("Preview refused a tick with no reason: " + preview.Error);

                var execution = await executor.ExecuteAsync(tickOnly);
                Assert.Equal(RemediationOutcome.CouldNotRun, execution.Outcome);
                _out.WriteLine("Execute refused a tick with no reason: " + execution.Error);

                // INDEPENDENT proof nothing was written, and the r2-01 charge guard is intact:
                // CouldNotRun refunds, so a compliant server is not charged.
                Assert.Equal(recommended, ReadConfigIndependently(target, ToggleConfigName));
                Assert.False(RemediationCreditOutcome.ChangeStuck(execution.Outcome, execution.RollbackState));
            }
            finally
            {
                if (ReadConfigIndependently(target, ToggleConfigName) != original)
                    SetConfigIndependently(target, ToggleConfigName, original);
                _out.WriteLine($"Restored '{ToggleConfigName}' to {original}.");
            }
        }

        [Fact]
        public async Task AnAcknowledgedOffRecommendedApply_WritesLive_AndTheLedgerCarriesTheReason()
        {
            var target = Target;
            if (string.IsNullOrWhiteSpace(target)) { _out.WriteLine("REMSAFE_LIVE_TARGET unset - inert."); return; }

            var original = ReadConfigIndependently(target, ToggleConfigName);
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var template = store.TryGet(ToggleTemplateKey)!;
            var recommended = template.Operation!.RecommendedValue!.Value;
            var floor = template.Operation.MinValue;
            var (executor, audit, auditDir) = Wire(target);
            var credits = new InMemoryRemediationCreditLedger(initialCreditsPerServer: 5);
            var runner = new RemediationRunner(store, new GrantedCapability(), credits, executor, audit,
                NullLogger<RemediationRunner>.Instance);

            try
            {
                if (original != recommended) SetConfigIndependently(target, ToggleConfigName, recommended);

                const string reason = "live smoke: vendor requires the non-recommended value";
                var before = credits.AvailableFor(target);
                var result = await runner.ApplyAsync(template.Key, target, approved: true, "adrian",
                    new Dictionary<string, string>
                    {
                        [template.Operation.ValueParam] = floor.ToString(),
                        [RemediationOpRenderer.AcknowledgeRegressionParam] = "true",
                        [RemediationOpRenderer.RegressionIntentParam] = reason,
                    });

                _out.WriteLine($"Acknowledged off-recommended apply: {result.Outcome} / {result.Message}");
                Assert.Equal(RemediationOutcome.AppliedVerified, result.Outcome);

                // INDEPENDENT proof the write really landed.
                Assert.Equal(floor, ReadConfigIndependently(target, ToggleConfigName));

                // A real change is charged. Ruling 7 kept the r2-01 rule: only the compliant no-op
                // is free.
                Assert.Equal(before - 1, credits.AvailableFor(target));

                audit.Flush();
                var approved = ReadAuditEntries(auditDir).Single(e => e.EventType == AuditEventType.RemediationApproved);
                var details = approved.Details.TryGetValue("Details", out var d) ? d : string.Empty;
                _out.WriteLine("Ledger RemediationApproved details: " + details);
                Assert.Contains("Off-recommended change acknowledged", details, StringComparison.Ordinal);
                Assert.Contains(reason, details, StringComparison.Ordinal);
            }
            finally
            {
                if (ReadConfigIndependently(target, ToggleConfigName) != original)
                    SetConfigIndependently(target, ToggleConfigName, original);
                _out.WriteLine($"Restored '{ToggleConfigName}' to {original}.");
                try { Directory.Delete(auditDir, recursive: true); } catch { /* cleanup */ }
            }
        }

        // ── Ruling 2 + R6: the charge, against rollback states a real server produced ──

        [Fact]
        public async Task AConfirmedRollbackFromARealServer_Refunds_AndAnUnattemptedOneDoesNot()
        {
            var target = Target;
            if (string.IsNullOrWhiteSpace(target)) { _out.WriteLine("REMSAFE_LIVE_TARGET unset - inert."); return; }

            Exec(target, $"IF DB_ID(N'{ScratchDb}') IS NOT NULL BEGIN ALTER DATABASE [{ScratchDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{ScratchDb}]; END; CREATE DATABASE [{ScratchDb}];");
            try
            {
                var (executor, _, auditDir) = Wire(target);

                // ARM 1 — a real CONFIRMED rollback. The offenders query names the scratch database
                // unconditionally, so post-change verify still "finds" it, the inverse runs, and the
                // confirming read observes the pre-apply state. Every statement is real.
                var invertible = new RemediationTemplate
                {
                    Key = "TEST-POLISH-CONFIRMED-ROLLBACK-LIVE",
                    DisplayName = "Test invertible db_set_option (live smoke)",
                    Kind = RemediationKind.Configuration,
                    Reversible = true,
                    Operation = new RemediationOperation
                    {
                        OpKind = RemediationOpKind.DbSetOption,
                        OptionSql = "SET AUTO_CLOSE ON",
                        OffendersQuery = $"SELECT name FROM sys.databases WHERE name = '{ScratchDb}';",
                    },
                };
                Assert.True(RemediationOpRenderer.TryInvertBooleanOptionSql(invertible.Operation.OptionSql!, out _));

                var confirmed = await executor.ExecuteAsync(new RemediationRequest(invertible, target));
                _out.WriteLine($"ARM 1 live: Outcome={confirmed.Outcome} RollbackState={confirmed.RollbackState}");
                _out.WriteLine($"ARM 1 rollback error: {confirmed.RollbackError}");
                Assert.Equal(RemediationOutcome.AppliedVerifyFailed, confirmed.Outcome);
                Assert.Equal(RemediationRollbackState.Confirmed, confirmed.RollbackState);

                // INDEPENDENT proof the inverse really put the database back.
                Assert.False(ReadAutoCloseIndependently(target, ScratchDb));

                // Ruling 2: a CONFIRMED rollback is the one state that refunds.
                Assert.False(RemediationCreditOutcome.ChangeStuck(confirmed.Outcome, confirmed.RollbackState));

                // ARM 2 — a real rollback that was NEVER ATTEMPTED. "SET PAGE_VERIFY BOGUS" passes
                // the renderer's charset guard, has no inverse, and the server's parser rejects it,
                // so the apply throws and nothing on the instance changes.
                var nonInvertible = new RemediationTemplate
                {
                    Key = "TEST-POLISH-NOT-ATTEMPTED-LIVE",
                    DisplayName = "Test non-invertible db_set_option (live smoke)",
                    Kind = RemediationKind.Configuration,
                    Reversible = true,
                    Operation = new RemediationOperation
                    {
                        OpKind = RemediationOpKind.DbSetOption,
                        OptionSql = "SET PAGE_VERIFY BOGUS",
                        OffendersQuery = $"SELECT name FROM sys.databases WHERE name = '{ScratchDb}';",
                    },
                };

                var unattempted = await executor.ExecuteAsync(new RemediationRequest(nonInvertible, target));
                _out.WriteLine($"ARM 2 live: Outcome={unattempted.Outcome} RollbackState={unattempted.RollbackState}");
                _out.WriteLine($"ARM 2 rollback error: {unattempted.RollbackError}");
                Assert.Equal(RemediationRollbackState.NotAvailable, unattempted.RollbackState);
                Assert.False(unattempted.RolledBack);
                // R6's register: no inverse ran, and the sentence says so rather than claiming a
                // failure.
                Assert.Contains("No rollback was attempted", unattempted.RollbackError!, StringComparison.Ordinal);
                Assert.DoesNotContain("Rollback failed for", unattempted.RollbackError!, StringComparison.Ordinal);

                try { Directory.Delete(auditDir, recursive: true); } catch { /* cleanup */ }
            }
            finally
            {
                Exec(target, $"IF DB_ID(N'{ScratchDb}') IS NOT NULL BEGIN ALTER DATABASE [{ScratchDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{ScratchDb}]; END;");
                _out.WriteLine($"Dropped {ScratchDb}.");
            }
        }

        private static bool ReadAutoCloseIndependently(string target, string db)
        {
            using var conn = new SqlConnection(ConnString(target));
            conn.Open();
            using var cmd = new SqlCommand("SELECT is_auto_close_on FROM sys.databases WHERE name = @n;", conn);
            cmd.Parameters.AddWithValue("@n", db);
            return Convert.ToBoolean(cmd.ExecuteScalar());
        }

        // ── Ruling 3: the force-install route, on a live partially-present database ──

        [Fact]
        public async Task APartiallyPresentDatabase_IsRefused_AndTheAcknowledgementFlipsThePreviewToAForcedInstall()
        {
            var target = Target;
            if (string.IsNullOrWhiteSpace(target)) { _out.WriteLine("REMSAFE_LIVE_TARGET unset - inert."); return; }

            // The Maintenance Solution install targets master, so the partially-present shape is
            // built there: ONE of the five object names, created as a stub and dropped in the
            // finally. Nothing else in master is touched, and no install is executed.
            const string stub = "CommandExecute";
            Exec(target, $"IF OBJECT_ID(N'dbo.{stub}') IS NOT NULL DROP PROCEDURE dbo.{stub}; ");
            Exec(target, $"CREATE PROCEDURE dbo.{stub} AS SELECT 1;");
            try
            {
                var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
                var template = store.TryGet("INSTALLMAINTENANCESOLUTION")!;
                var (executor, _, auditDir) = Wire(target);

                // The provenance snapshot the executor reads, read independently first.
                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var conn = new SqlConnection(ConnString(target)))
                {
                    await conn.OpenAsync();
                    using var cmd = new SqlCommand(MaintenanceSolutionOpRenderer.RenderProcsProvenanceSnapshot(), conn);
                    using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync()) present.Add(reader.GetString(0));
                }
                _out.WriteLine("Provenance snapshot on master: " + string.Join(", ", present.OrderBy(x => x)));
                Assert.Equal(MaintenanceSolutionOpRenderer.InstallPresence.PartiallyPresent,
                    MaintenanceSolutionOpRenderer.Classify(present));

                // Default path: refused, with the consequence stated in ruling 3's own words.
                var plain = await executor.PreviewAsync(new RemediationRequest(template, target));
                Assert.False(plain.Succeeded);
                _out.WriteLine("Unacknowledged preview: " + plain.Error);
                Assert.Contains(MaintenanceSolutionOpRenderer.OverwriteConsequence, plain.Error!, StringComparison.Ordinal);
                Assert.Contains(stub, plain.Error!, StringComparison.Ordinal);

                // Acknowledged path: the preview succeeds and repeats the consequence, so the last
                // thing read before approval is what the acknowledgement costs.
                var acked = await executor.PreviewAsync(new RemediationRequest(template, target,
                    new Dictionary<string, string>
                    {
                        [MaintenanceSolutionOpRenderer.AcknowledgeOverwriteParam] = "true",
                    }));
                _out.WriteLine("Acknowledged preview: " + acked.WhatIfText);
                Assert.True(acked.Succeeded);
                Assert.Contains("FORCE-install", acked.WhatIfText, StringComparison.Ordinal);
                Assert.Contains("not reversible", acked.WhatIfText, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(stub, acked.WhatIfText, StringComparison.Ordinal);

                try { Directory.Delete(auditDir, recursive: true); } catch { /* cleanup */ }
            }
            finally
            {
                Exec(target, $"IF OBJECT_ID(N'dbo.{stub}') IS NOT NULL DROP PROCEDURE dbo.{stub};");
                _out.WriteLine($"Dropped the master stub dbo.{stub}.");
            }
        }
    }
}
