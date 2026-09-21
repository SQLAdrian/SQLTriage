/* In the name of God, the Merciful, the Compassionate */

// S1 deferred-verify — LIVE end-to-end loop, the honest proof the unit pins cannot give:
//   real runner apply (CheckDbWeekly schedule lane) -> RemediationVerifyScheduled in the real
//   HMAC ledger -> GetPending sees it -> Verify-now BEFORE the job ran = StillPending (the
//   fail-safe pin) -> sp_start_job, job RUNS against the live instance -> Verify-now = Passed
//   -> pending list empties -> RemediationVerifyResolved(Passed) in the ledger.
//
// INERT in a normal `dotnet test` run — early-returns unless DEFERREDVERIFY_LIVE_TARGET names a
// reachable SQL instance with SQL Server Agent RUNNING (the job must actually execute). Same
// pattern as RemediationDbSetOptionLiveSmokeTests: real services, independent SqlConnection
// proof reads, self-cleaning (drops the job it created; uninstalls the Ola procs only if THIS
// run installed them).
//
// INVOCATION (on a box with the local test instances; Agent must be running):
//   $env:DEFERREDVERIFY_LIVE_TARGET = ".\NEW2022"
//   dotnet test Tests/SQLTriage.Tests --filter "FullyQualifiedName~DeferredVerifyLiveSmoke"

using System;
using System.Collections.Generic;
using System.IO;
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
    public class DeferredVerifyLiveSmokeTests
    {
        private readonly ITestOutputHelper _out;
        public DeferredVerifyLiveSmokeTests(ITestOutputHelper output) => _out = output;
        private void Line(string s) => _out.WriteLine(s);

        private const string JobName = "SQLTriage - CHECKDB Weekly"; // MaintenanceSolutionOpRenderer.JobNameFor(CheckDbWeekly)

        private sealed class GrantedCapability : IRemediationCapability { public bool IsGranted => true; }

        private static string MasterConnString(string target) =>
            $"Server={target};Database=master;Integrated Security=true;TrustServerCertificate=true;Connection Timeout=15;";

        private static object? Scalar(string target, string sql)
        {
            using var conn = new SqlConnection(MasterConnString(target));
            conn.Open();
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
            return cmd.ExecuteScalar();
        }

        private static void Exec(string target, string sql)
        {
            using var conn = new SqlConnection(MasterConnString(target));
            conn.Open();
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
            cmd.ExecuteNonQuery();
        }

        private static void DropJobIfExists(string target)
        {
            try
            {
                Exec(target,
                    "IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'SQLTriage - CHECKDB Weekly') " +
                    "EXEC msdb.dbo.sp_delete_job @job_name = N'SQLTriage - CHECKDB Weekly', @delete_unused_schedule = 1;");
            }
            catch { /* best-effort teardown */ }
        }

        [Fact]
        public async Task DeferredVerify_FullLiveLoop_Apply_Pending_JobRuns_Passed()
        {
            var target = Environment.GetEnvironmentVariable("DEFERREDVERIFY_LIVE_TARGET");
            if (string.IsNullOrWhiteSpace(target))
            {
                Line("SKIPPED: set DEFERREDVERIFY_LIVE_TARGET to a SQL instance (e.g. .\\NEW2022) to exercise the live loop.");
                return; // inert no-op in normal runs
            }

            // Agent must be RUNNING or the created job can never execute.
            var agentRunning = Scalar(target,
                "SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.dm_server_services " +
                "WHERE servicename LIKE 'SQL Server Agent%' AND status_desc = 'Running') THEN 1 ELSE 0 END;");
            if (Convert.ToInt32(agentRunning) != 1)
            {
                Line($"SKIPPED: SQL Server Agent is not running on {target} — the job could never run.");
                return;
            }

            // Ola procs must exist in master for the job step to SUCCEED. Install via the real
            // template if absent; uninstall in teardown only if THIS run installed them.
            bool procsPreExisted = Convert.ToInt32(Scalar(target, MaintenanceSolutionOpRenderer.ProcsExistProbe)) == 1;
            Line($"Ola procs pre-existing in master: {procsPreExisted}");

            DropJobIfExists(target);

            var tempAuditDir = Path.Combine(Path.GetTempPath(), "sqlt-defverify-livesmoke-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempAuditDir);
            try
            {
                // ── Real service graph (the same one production DI builds) ──────────
                var connections = new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance);
                connections.AddConnection(new ServerConnection
                {
                    ServerNames = target,
                    UseWindowsAuthentication = true,
                    TrustServerCertificate = true,
                    IsEnabled = true,
                });
                var audit = new AuditLogService(tempAuditDir, startFlushTimer: false);
                var templates = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
                var executor = new DbatoolsRemediationExecutor(
                    new PowerShellService(NullLogger<PowerShellService>.Instance),
                    connections, audit,
                    new DiskIoService(NullLogger<DiskIoService>.Instance),
                    NullLogger<DbatoolsRemediationExecutor>.Instance);
                var runner = new RemediationRunner(templates, new GrantedCapability(),
                    new InMemoryRemediationCreditLedger(initialCreditsPerServer: 10),
                    executor, audit, NullLogger<RemediationRunner>.Instance);
                var deferred = new DeferredVerificationService(audit, templates, connections,
                    NullLogger<DeferredVerificationService>.Instance);

                if (!procsPreExisted)
                {
                    var install = await runner.ApplyAsync("INSTALLMAINTENANCESOLUTION", target, approved: true, "livesmoke");
                    Line($"Ola install outcome: {install.Outcome} — {install.Message}");
                    Assert.Equal(RemediationOutcome.AppliedVerified, install.Outcome);
                }

                // ── 1) APPLY the schedule lane via the REAL runner ──────────────────
                var p = new Dictionary<string, string>
                { [MaintenanceSolutionOpRenderer.ActionParam] = "CheckDbWeekly" };
                var apply = await runner.ApplyAsync("INSTALLMAINTENANCESOLUTION", target, approved: true, "livesmoke", p);
                Line($"Schedule-lane apply outcome: {apply.Outcome} — {apply.Message}");
                Assert.Equal(RemediationOutcome.AppliedVerified, apply.Outcome);

                // Independent proof the job exists.
                Assert.Equal(1, Convert.ToInt32(Scalar(target,
                    $"SELECT CASE WHEN EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'{JobName}') THEN 1 ELSE 0 END;")));

                // ── 2) PENDING: the ledger schedule is visible ──────────────────────
                var pending = deferred.GetPending(target);
                var pv = Assert.Single(pending);
                Assert.Equal("INSTALLMAINTENANCESOLUTION", pv.TemplateKey);
                Line($"Pending verification: verify by {pv.VerifyByUtc:o}");

                // ── 3) FAIL-SAFE: before the job has run, Verify-now stays pending ──
                var early = await deferred.VerifyNowAsync(pv);
                Line($"Verify-now before job ran: {early.Status} — {early.Message}");
                Assert.Equal(DeferredVerifyStatus.StillPending, early.Status);

                // ── 4) RUN the job for real; wait for the outcome row ───────────────
                Exec(target, $"EXEC msdb.dbo.sp_start_job @job_name = N'{JobName}';");
                int runStatus = -1;
                for (int i = 0; i < 60; i++) // up to 5 min: CHECKDB across the dev instance's DBs
                {
                    await Task.Delay(5000);
                    var status = Scalar(target,
                        "SELECT TOP (1) h.run_status FROM msdb.dbo.sysjobhistory h " +
                        "JOIN msdb.dbo.sysjobs j ON j.job_id = h.job_id " +
                        $"WHERE j.name = N'{JobName}' AND h.step_id = 0 ORDER BY h.instance_id DESC;");
                    if (status is not null && status is not DBNull) { runStatus = Convert.ToInt32(status); break; }
                }
                Line($"Job outcome row run_status: {runStatus} (1 = Succeeded)");
                if (runStatus != 1)
                {
                    // Dump the step messages BEFORE teardown deletes the job (and its history) —
                    // the first live run failed here and took its evidence with it.
                    using var conn = new SqlConnection(MasterConnString(target));
                    conn.Open();
                    using var cmd = new SqlCommand(
                        "SELECT h.step_id, h.run_status, h.message FROM msdb.dbo.sysjobhistory h " +
                        "JOIN msdb.dbo.sysjobs j ON j.job_id = h.job_id " +
                        $"WHERE j.name = N'{JobName}' ORDER BY h.instance_id;", conn);
                    using var rd = cmd.ExecuteReader();
                    while (rd.Read())
                        Line($"  step {rd.GetInt32(0)} status {rd.GetInt32(1)}: {rd.GetString(2)}");
                }
                Assert.Equal(1, runStatus); // the job genuinely ran and succeeded

                // ── 5) VERIFY-NOW flips to Passed; pending empties; ledger records it ─
                var final = await deferred.VerifyNowAsync(pv);
                Line($"Verify-now after job ran: {final.Status} — {final.Message}");
                Assert.Equal(DeferredVerifyStatus.Passed, final.Status);
                Assert.Empty(deferred.GetPending(target));

                var resolved = new AuditLogService(tempAuditDir, startFlushTimer: false).GetEntries(
                    DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1),
                    AuditEventType.RemediationVerifyResolved);
                var entry = Assert.Single(resolved);
                Assert.Equal("true", entry.Details["Passed"]);
                Line("Ledger: RemediationVerifyResolved(Passed=true) — full live loop closed.");
            }
            finally
            {
                DropJobIfExists(target);
                if (!procsPreExisted)
                {
                    try
                    {
                        Exec(target,
                            "DROP PROCEDURE IF EXISTS dbo.CommandExecute; DROP PROCEDURE IF EXISTS dbo.DatabaseBackup; " +
                            "DROP PROCEDURE IF EXISTS dbo.DatabaseIntegrityCheck; DROP PROCEDURE IF EXISTS dbo.IndexOptimize; " +
                            "DROP TABLE IF EXISTS dbo.CommandLog;");
                    }
                    catch { /* best-effort teardown */ }
                }
                try { Directory.Delete(tempAuditDir, recursive: true); } catch { }
            }
        }
    }
}
