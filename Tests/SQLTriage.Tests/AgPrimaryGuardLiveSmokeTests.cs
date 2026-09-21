/* In the name of God, the Merciful, the Compassionate */

// F3 AG primary guard — LIVE end-to-end against a real msdb, the proof the unit pins cannot
// give: the RENDERED apply batch really injects step 1 and renumbers, really re-applies as a
// no-op, the guarded job REALLY refuses to do its work when the database is not primary (on a
// non-AG instance fn_hadr_is_primary_replica returns NULL, so the guard quits-with-failure —
// the exact behaviour that makes a job safe to exist on a secondary), and the inverse really
// restores the original shape.
//
// INERT in a normal `dotnet test` run — early-returns unless AGPRIMARYGUARD_LIVE_TARGET names a
// reachable SQL instance with SQL Server Agent RUNNING. Same pattern as
// DeferredVerifyLiveSmokeTests: raw SqlConnection proof reads, self-cleaning (drops the probe
// job it created).
//
// INVOCATION (first live run 2026-07-30 against MSI\AG2, SQL 2025):
//   $env:AGPRIMARYGUARD_LIVE_TARGET = "localhost\ag2"
//   dotnet test Tests/SQLTriage.Tests --filter "FullyQualifiedName~AgPrimaryGuardLiveSmoke"

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SQLTriage.Data.Services.Remediation;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    public class AgPrimaryGuardLiveSmokeTests
    {
        private readonly ITestOutputHelper _out;
        public AgPrimaryGuardLiveSmokeTests(ITestOutputHelper output) => _out = output;
        private void Line(string s) => _out.WriteLine(s);

        private const string JobName = "SQLTriage-LIVE-GUARD-PROBE-DELETE-ME";
        private const string DbName = "AgTestDb";

        private static string? Target => Environment.GetEnvironmentVariable("AGPRIMARYGUARD_LIVE_TARGET");

        private static string ConnString => $"Server={Target};Database=msdb;Integrated Security=true;TrustServerCertificate=true;Connection Timeout=15;";

        private static object? Scalar(string sql)
        {
            using var conn = new SqlConnection(ConnString);
            conn.Open();
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
            return cmd.ExecuteScalar();
        }

        private static void Exec(string sql)
        {
            using var conn = new SqlConnection(ConnString);
            conn.Open();
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
            cmd.ExecuteNonQuery();
        }

        private static int StepCount() => (int)Scalar(
            "SELECT COUNT(*) FROM msdb.dbo.sysjobsteps s JOIN msdb.dbo.sysjobs j ON j.job_id = s.job_id " +
            $"WHERE j.name = N'{JobName}'")!;

        private static string Step1Name() => (string)Scalar(
            "SELECT s.step_name FROM msdb.dbo.sysjobsteps s JOIN msdb.dbo.sysjobs j ON j.job_id = s.job_id " +
            $"WHERE j.name = N'{JobName}' AND s.step_id = 1")!;

        [Fact]
        public async Task Live_guard_injects_blocks_nonprimary_work_and_reverses()
        {
            if (string.IsNullOrWhiteSpace(Target))
            {
                Line("AGPRIMARYGUARD_LIVE_TARGET not set — live smoke inert.");
                return;
            }

            var p = new Dictionary<string, string>
            {
                [AgPrimaryGuardOpRenderer.JobNameParam] = JobName,
                [AgPrimaryGuardOpRenderer.DbNameParam] = DbName
            };

            try
            {
                // ── Arrange: a 2-step job with quit/next flow (guard-eligible shape) ──
                Exec($@"
IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'{JobName}')
    EXEC msdb.dbo.sp_delete_job @job_name = N'{JobName}';
EXEC msdb.dbo.sp_add_job @job_name = N'{JobName}', @enabled = 1,
     @description = N'F3 live smoke - safe to delete.';
EXEC msdb.dbo.sp_add_jobstep @job_name = N'{JobName}', @step_id = 1, @step_name = N'real-work-A',
     @subsystem = N'TSQL', @database_name = N'master',
     @command = N'SELECT 1;', @on_success_action = 3, @on_fail_action = 2;
EXEC msdb.dbo.sp_add_jobstep @job_name = N'{JobName}', @step_id = 2, @step_name = N'real-work-B',
     @subsystem = N'TSQL', @database_name = N'master',
     @command = N'SELECT 2;', @on_success_action = 1, @on_fail_action = 2;
EXEC msdb.dbo.sp_add_jobserver @job_name = N'{JobName}', @server_name = N'(LOCAL)';");
                Assert.Equal(2, StepCount());
                Line("arranged: 2-step probe job");

                // ── Act 1: the rendered apply, exactly as the executor runs it ──
                Assert.True(AgPrimaryGuardOpRenderer.TryRenderApplySql(p, out var applySql, out var err), err);
                Exec(applySql);

                Assert.Equal(3, StepCount());
                Assert.Equal("SQLTriage: AG primary guard", Step1Name());
                Assert.Equal("1", Scalar(AgPrimaryGuardOpRenderer.RenderVerifySql(JobName))!.ToString());
                Line("apply verified: guard is step 1, steps renumbered, start_step_id = 1");

                // ── Act 2: idempotency — the same batch again must change nothing ──
                Exec(applySql);
                Assert.Equal(3, StepCount());
                Line("re-apply verified: strict no-op");

                // ── Act 3: the behavioural proof. Start the job; on this instance the DB is
                //    not in an AG, fn_hadr_is_primary_replica returns NULL, the guard fails
                //    the job at step 1 — and the real work must never have run. ──
                Exec($"EXEC msdb.dbo.sp_start_job @job_name = N'{JobName}';");

                int? outcome = null;
                for (var i = 0; i < 30 && outcome is null; i++)
                {
                    await Task.Delay(1000);
                    outcome = Scalar(
                        "SELECT TOP 1 h.run_status FROM msdb.dbo.sysjobhistory h " +
                        "JOIN msdb.dbo.sysjobs j ON j.job_id = h.job_id " +
                        $"WHERE j.name = N'{JobName}' AND h.step_id = 0 ORDER BY h.instance_id DESC") as int?;
                }

                Assert.NotNull(outcome);
                Assert.Equal(0, outcome); // 0 = Failed: the guard quit the job, as designed off-primary

                var realWorkRan = (int)Scalar(
                    "SELECT COUNT(*) FROM msdb.dbo.sysjobhistory h " +
                    "JOIN msdb.dbo.sysjobs j ON j.job_id = h.job_id " +
                    $"WHERE j.name = N'{JobName}' AND h.step_id >= 2")!;
                Assert.Equal(0, realWorkRan);
                Line("behaviour verified: job failed at the guard; the real work never executed");

                // ── Act 4: the inverse restores the original shape ──
                Exec(AgPrimaryGuardOpRenderer.RenderInverseSql(JobName, originalStartStepId: 1));
                Assert.Equal(2, StepCount());
                Assert.Equal("real-work-A", Step1Name());
                Line("inverse verified: guard removed, original step 1 restored");
            }
            finally
            {
                try { Exec($"IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'{JobName}') EXEC msdb.dbo.sp_delete_job @job_name = N'{JobName}';"); }
                catch { /* cleanup best-effort */ }
            }
        }
    }
}
