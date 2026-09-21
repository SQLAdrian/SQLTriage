/* In the name of God, the Merciful, the Compassionate */
/*
 * Reads SQL Agent job definitions from msdb for a chosen instance.
 *
 * Read-only by construction: one parameterless SELECT over msdb.dbo.sysjobs +
 * msdb.dbo.sysjobsteps. Nothing here writes. Job DDL lives in the gated remediation lane
 * (AgPrimaryGuardOpRenderer), never in this service.
 *
 * NOTE: three pages (ScheduledTasks, AgentJobTimeline, DynamicDashboard) still carry their
 * own inline sysjobs SQL. Folding them onto this service is a deliberate follow-up, kept out
 * of this change so the guard feature does not drag a UI refactor along with it.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;
using SQLTriage.Data.Models.Jobs;

namespace SQLTriage.Data.Services.Jobs
{
    public class JobInventoryService
    {
        private readonly ServerConnectionManager _connections;
        private readonly ILogger<JobInventoryService> _logger;

        public JobInventoryService(ServerConnectionManager connections, ILogger<JobInventoryService> logger)
        {
            _connections = connections;
            _logger = logger;
        }

        /// <summary>
        /// Every Agent job on <paramref name="serverName"/>, with its steps in step order, AND
        /// the outcome of the read. Never throws — the callers are UI surfaces that must degrade,
        /// not crash — but a failed, refused or unroutable read is reported as such.
        /// <para>
        /// pages-r1-01/r1-11/r2-01: this used to return <c>Array.Empty</c> for a failure, which is
        /// the same value a genuinely job-free instance returns. Callers could not tell "there are
        /// no jobs" from "we could not look", and three surfaces printed the first while meaning
        /// the second — one of them offering an irreversible delete on the strength of it.
        /// </para>
        /// </summary>
        public async Task<JobInventoryRead> GetJobsAsync(
            string serverName, CancellationToken ct = default)
        {
            var conn = ResolveConnection(serverName);
            if (conn is null)
            {
                _logger.LogWarning("Job inventory: no connection resolves instance {Server}; nothing was read", serverName);
                return JobInventoryRead.NoConnectionFor(serverName);
            }

            var byId = new Dictionary<Guid, AgentJobDefinition>();

            try
            {
                using var sqlConn = new SqlConnection(conn.GetConnectionString(serverName, "msdb"));
                await sqlConn.OpenAsync(ct).ConfigureAwait(false);
                using var cmd = new SqlCommand(JobInventorySql, sqlConn) { CommandTimeout = 30 };
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

                // Result set 1 — jobs + steps.
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var jobId = reader.GetGuid(reader.GetOrdinal("job_id"));
                    if (!byId.TryGetValue(jobId, out var job))
                    {
                        job = new AgentJobDefinition
                        {
                            JobId       = jobId,
                            Name        = reader.GetString(reader.GetOrdinal("job_name")),
                            Enabled     = reader.GetByte(reader.GetOrdinal("job_enabled")) == 1,
                            StartStepId = reader.GetInt32(reader.GetOrdinal("start_step_id")),
                            Description = Str(reader, "job_description"),
                            OwnerLogin  = Str(reader, "owner_login")
                        };
                        byId[jobId] = job;
                    }

                    // LEFT JOIN: a job with no steps yields one row with a null step_id.
                    var stepOrdinal = reader.GetOrdinal("step_id");
                    if (reader.IsDBNull(stepOrdinal)) continue;

                    job.Steps.Add(new AgentJobStep
                    {
                        StepId          = reader.GetInt32(stepOrdinal),
                        StepName        = Str(reader, "step_name") ?? string.Empty,
                        Subsystem       = Str(reader, "subsystem"),
                        DatabaseName    = Str(reader, "database_name"),
                        Command         = Str(reader, "command"),
                        OnSuccessAction = reader.GetByte(reader.GetOrdinal("on_success_action")),
                        OnSuccessStepId = reader.GetInt32(reader.GetOrdinal("on_success_step_id")),
                        OnFailAction    = reader.GetByte(reader.GetOrdinal("on_fail_action")),
                        OnFailStepId    = reader.GetInt32(reader.GetOrdinal("on_fail_step_id"))
                    });
                }

                // Result set 2 — schedules. A second set rather than a third join: joining steps
                // AND schedules in one query multiplies rows (3 steps x 2 schedules = 6) and the
                // de-duplication that needs is a bug factory.
                if (await reader.NextResultAsync(ct).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var jobId = reader.GetGuid(reader.GetOrdinal("job_id"));
                        if (!byId.TryGetValue(jobId, out var job)) continue;

                        job.Schedules.Add(new AgentJobSchedule
                        {
                            Name                 = Str(reader, "schedule_name") ?? string.Empty,
                            Enabled              = reader.GetInt32(reader.GetOrdinal("schedule_enabled")) == 1,
                            FreqType             = reader.GetInt32(reader.GetOrdinal("freq_type")),
                            FreqInterval         = reader.GetInt32(reader.GetOrdinal("freq_interval")),
                            FreqSubdayType       = reader.GetInt32(reader.GetOrdinal("freq_subday_type")),
                            FreqSubdayInterval   = reader.GetInt32(reader.GetOrdinal("freq_subday_interval")),
                            FreqRelativeInterval = reader.GetInt32(reader.GetOrdinal("freq_relative_interval")),
                            FreqRecurrenceFactor = reader.GetInt32(reader.GetOrdinal("freq_recurrence_factor")),
                            ActiveStartDate      = reader.GetInt32(reader.GetOrdinal("active_start_date")),
                            ActiveEndDate        = reader.GetInt32(reader.GetOrdinal("active_end_date")),
                            ActiveStartTime      = reader.GetInt32(reader.GetOrdinal("active_start_time")),
                            ActiveEndTime        = reader.GetInt32(reader.GetOrdinal("active_end_time"))
                        });
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Job inventory read failed for {Server}", serverName);
                return JobInventoryRead.Failure(serverName, ex.Message);
            }

            return JobInventoryRead.Success(serverName, new List<AgentJobDefinition>(byId.Values));
        }

        private static string? Str(SqlDataReader r, string col)
        {
            var i = r.GetOrdinal(col);
            return r.IsDBNull(i) ? null : r.GetString(i);
        }

        private ServerConnection? ResolveConnection(string instance)
        {
            if (string.IsNullOrWhiteSpace(instance)) return null;
            foreach (var c in _connections.GetConnections())
                foreach (var s in c.GetServerList())
                    if (string.Equals(s, instance, StringComparison.OrdinalIgnoreCase))
                        return c;
            return null;
        }

        /// <summary>
        /// TWO result sets: jobs+steps, then jobs+schedules. Both LEFT JOIN so a job with no
        /// steps or no schedule is still inventoried (it is ineligible for a guard, and the UI
        /// should say so rather than hide it).
        /// <para>
        /// Deliberately not one query: joining steps AND schedules together multiplies rows
        /// (3 steps x 2 schedules = 6) and the de-duplication that would need is a bug factory.
        /// </para>
        /// </summary>
        internal const string JobInventorySql = @"
SELECT  j.job_id,
        j.name           AS job_name,
        j.enabled        AS job_enabled,
        j.start_step_id,
        j.description    AS job_description,
        SUSER_SNAME(j.owner_sid) AS owner_login,
        s.step_id,
        s.step_name,
        s.subsystem,
        s.database_name,
        s.command,
        s.on_success_action,
        s.on_success_step_id,
        s.on_fail_action,
        s.on_fail_step_id
FROM    msdb.dbo.sysjobs AS j WITH (NOLOCK)
LEFT JOIN msdb.dbo.sysjobsteps AS s WITH (NOLOCK)
        ON s.job_id = j.job_id
ORDER BY j.name, s.step_id;

SELECT  j.job_id,
        sch.name        AS schedule_name,
        sch.enabled     AS schedule_enabled,
        sch.freq_type,
        sch.freq_interval,
        sch.freq_subday_type,
        sch.freq_subday_interval,
        sch.freq_relative_interval,
        sch.freq_recurrence_factor,
        sch.active_start_date,
        sch.active_end_date,
        sch.active_start_time,
        sch.active_end_time
FROM    msdb.dbo.sysjobs AS j WITH (NOLOCK)
JOIN    msdb.dbo.sysjobschedules AS js WITH (NOLOCK) ON js.job_id = j.job_id
JOIN    msdb.dbo.sysschedules AS sch WITH (NOLOCK) ON sch.schedule_id = js.schedule_id
ORDER BY j.name, sch.name;";
    }
}
