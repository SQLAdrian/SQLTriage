/* In the name of God, the Merciful, the Compassionate */
/*
 * Scripts ONE Agent job from the primary and recreates it on the secondary.
 *
 * Shape: drop-if-exists, then sp_add_job -> steps -> a second pass to wire real flow targets ->
 * start_step_id -> schedules -> sp_add_jobserver.
 *
 * WHY TWO PASSES OVER THE STEPS: a step's on_success_step_id / on_fail_step_id can point FORWARD
 * to a step that does not exist yet at creation time, and sp_add_jobstep validates the target.
 * So every step is created with plain quit/next actions first, and the real flow is applied by
 * sp_update_jobstep once all steps exist. That is the only reason sp_update_jobstep is needed
 * here — and why it had to be added to SqlSafetyValidator's blocked list to reach the gated lane.
 *
 * WHY DROP-AND-RECREATE rather than a reconciling ALTER: an in-place edit has to diff and mutate
 * steps, schedules and flow wiring in the right order, and a failure part-way leaves a job that
 * is neither the old one nor the new one. Drop-and-recreate has exactly two outcomes, and the
 * gated pipeline captures the secondary's prior definition as the snapshot so the inverse can
 * put it back. The cost is honest and stated in the preview: the job's RUN HISTORY on the
 * secondary is lost. Adrian's ruling for v1.
 *
 * OWNER: not scripted. The primary's owner login very often does not exist on the secondary, and
 * failing a sync over that would be worse than the mismatch. The job is created under the
 * executing context and JobDiffEngine reports the difference (Adrian's ruling, 2026-07-29).
 *
 * TRUST BOUNDARY: step command bodies are arbitrary T-SQL read from an already-connected
 * server's own msdb. They are never interpreted here, only re-emitted as escaped literals, and
 * they never originate from operator input. The safety gate classifies a REPRESENTATIVE render
 * (fixed names, trivial body) — it vets the sp_add_job/jobstep/schedule SHAPE, not tenant SQL.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SQLTriage.Data.Models.Jobs;

namespace SQLTriage.Data.Services.Remediation
{
    public static class AgentJobSyncOpRenderer
    {
        /// <summary>Request parameter: the job to sync (must exist on the primary).</summary>
        public const string JobNameParam = "JobName";

        /// <summary>Request parameter: the instance to READ the definition from.</summary>
        public const string SourceServerParam = "SourceServer";

        private static readonly Regex SafeName =
            new(@"^[A-Za-z0-9_\-\.\(\), \\$#@]{1,128}$", RegexOptions.Compiled);

        public static bool IsSafeName(string? value) => SafeName.IsMatch((value ?? string.Empty).Trim());

        private static string Lit(string? raw) => "N'" + (raw ?? string.Empty).Replace("'", "''") + "'";

        /// <summary>
        /// The full recreate batch for one job. <paramref name="job"/> is the definition as read
        /// from the primary.
        /// </summary>
        public static bool TryRenderSyncSql(AgentJobDefinition? job, out string sql, out string error)
        {
            sql = string.Empty;
            error = string.Empty;

            if (job is null) { error = "No job definition to script."; return false; }
            if (!IsSafeName(job.Name))
            {
                error = $"Job name '{job.Name}' contains characters that are not permitted here.";
                return false;
            }
            if (job.Steps.Count == 0)
            {
                error = $"Job '{job.Name}' has no steps; refusing to create an empty job on the secondary.";
                return false;
            }
            foreach (var sc in job.Schedules)
            {
                if (!IsSafeName(sc.Name))
                {
                    error = $"Schedule name '{sc.Name}' contains characters that are not permitted here.";
                    return false;
                }
            }

            var n = Lit(job.Name);
            var sb = new StringBuilder();

            sb.AppendLine($"IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = {n})");
            sb.AppendLine($"    EXEC msdb.dbo.sp_delete_job @job_name = {n}, @delete_unused_schedule = 1;");
            sb.AppendLine();
            sb.AppendLine($"EXEC msdb.dbo.sp_add_job @job_name = {n}, @enabled = {(job.Enabled ? 1 : 0)}, " +
                          $"@description = {Lit(job.Description ?? "Synced from the availability-group primary by SQLTriage.")};");
            sb.AppendLine();

            // Pass 1 — create every step with neutral flow so no forward reference can fail.
            foreach (var st in job.Steps.OrderBy(s => s.StepId))
            {
                sb.AppendLine($"EXEC msdb.dbo.sp_add_jobstep @job_name = {n}, @step_id = {st.StepId}, " +
                              $"@step_name = {Lit(st.StepName)}, @subsystem = {Lit(string.IsNullOrWhiteSpace(st.Subsystem) ? "TSQL" : st.Subsystem)}, " +
                              $"@database_name = {Lit(string.IsNullOrWhiteSpace(st.DatabaseName) ? "master" : st.DatabaseName)}, " +
                              $"@command = {Lit(st.Command)}, @on_success_action = 1, @on_fail_action = 2;");
            }
            sb.AppendLine();

            // Pass 2 — now that every step exists, apply the real flow wiring.
            foreach (var st in job.Steps.OrderBy(s => s.StepId))
            {
                sb.AppendLine($"EXEC msdb.dbo.sp_update_jobstep @job_name = {n}, @step_id = {st.StepId}, " +
                              $"@on_success_action = {st.OnSuccessAction}, @on_success_step_id = {st.OnSuccessStepId}, " +
                              $"@on_fail_action = {st.OnFailAction}, @on_fail_step_id = {st.OnFailStepId};");
            }
            sb.AppendLine();
            sb.AppendLine($"EXEC msdb.dbo.sp_update_job @job_name = {n}, @start_step_id = {job.StartStepId};");
            sb.AppendLine();

            foreach (var sc in job.Schedules.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"EXEC msdb.dbo.sp_add_schedule @schedule_name = {Lit(sc.Name)}, " +
                              $"@enabled = {(sc.Enabled ? 1 : 0)}, @freq_type = {sc.FreqType}, " +
                              $"@freq_interval = {sc.FreqInterval}, @freq_subday_type = {sc.FreqSubdayType}, " +
                              $"@freq_subday_interval = {sc.FreqSubdayInterval}, " +
                              $"@freq_relative_interval = {sc.FreqRelativeInterval}, " +
                              $"@freq_recurrence_factor = {sc.FreqRecurrenceFactor}, " +
                              $"@active_start_date = {sc.ActiveStartDate}, @active_end_date = {sc.ActiveEndDate}, " +
                              $"@active_start_time = {sc.ActiveStartTime}, @active_end_time = {sc.ActiveEndTime};");
                sb.AppendLine($"EXEC msdb.dbo.sp_attach_schedule @job_name = {n}, @schedule_name = {Lit(sc.Name)};");
            }
            sb.AppendLine();
            sb.AppendLine($"EXEC msdb.dbo.sp_add_jobserver @job_name = {n}, @server_name = N'(LOCAL)';");

            sql = sb.ToString();
            return true;
        }

        /// <summary>
        /// Human-facing warning shown in the preview. The lost run history is the one genuinely
        /// irreversible consequence of drop-and-recreate, so it is stated before approval, not
        /// discovered afterwards.
        /// </summary>
        public const string DropRecreateWarning =
            "This DROPS and recreates the job on the secondary. Its run history there is lost. " +
            "The job's current definition is captured first and can be restored by rolling back.";

        /// <summary>Read-only: does this job exist on the target, and what does it look like?</summary>
        public static string RenderExistsSql(string jobName) =>
            $"SELECT CASE WHEN EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = {Lit(jobName)}) THEN 1 ELSE 0 END;";

        /// <summary>
        /// Read-only verify: the recreated job has the expected step count and start step.
        /// The authoritative check is a full re-diff by the caller; this is the in-pipeline gate.
        /// </summary>
        public static string RenderVerifySql(string jobName, int expectedStepCount, int expectedStartStep) =>
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM msdb.dbo.sysjobs j " +
            $"WHERE j.name = {Lit(jobName)} AND j.start_step_id = {expectedStartStep} " +
            $"AND (SELECT COUNT(*) FROM msdb.dbo.sysjobsteps s WHERE s.job_id = j.job_id) = {expectedStepCount}) " +
            "THEN 1 ELSE 0 END;";

        /// <summary>
        /// Deleting a job that exists ONLY on the secondary. Its own template key and its own
        /// renderer so it can never be reached by the sync path: removing a job nobody asked to
        /// remove is the most destructive thing in this feature, and it stays opt-in per row.
        /// </summary>
        public static bool TryRenderDeleteExtraSql(string? jobName, out string sql, out string error)
        {
            sql = string.Empty;
            error = string.Empty;
            var name = (jobName ?? string.Empty).Trim();
            if (!IsSafeName(name))
            {
                error = $"Job name '{jobName}' is empty or contains characters that are not permitted here.";
                return false;
            }
            sql = $"IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = {Lit(name)})\n" +
                  $"    EXEC msdb.dbo.sp_delete_job @job_name = {Lit(name)}, @delete_unused_schedule = 1;";
            return true;
        }

        /// <summary>
        /// Value-independent representative render for the safety gate — a fixed one-step job.
        /// The gate vets the statement SHAPE; real step bodies are never classified (see the
        /// trust-boundary note in this file's header).
        /// </summary>
        public static string RenderRepresentativeForClassification()
        {
            var job = new AgentJobDefinition
            {
                Name = "SQLTriage Representative Sync Job",
                Enabled = true,
                StartStepId = 1,
                Description = "Representative",
                Steps =
                {
                    new AgentJobStep
                    {
                        StepId = 1, StepName = "Work", Subsystem = "TSQL",
                        DatabaseName = "master", Command = "SELECT 1;",
                        OnSuccessAction = 1, OnFailAction = 2
                    }
                },
                Schedules =
                {
                    new AgentJobSchedule { Name = "Representative Schedule", Enabled = true, FreqType = 4, FreqInterval = 1 }
                }
            };
            TryRenderSyncSql(job, out var sync, out _);
            TryRenderDeleteExtraSql("SQLTriage Representative Sync Job", out var del, out _);
            return sync + "\n" + del;
        }
    }
}
