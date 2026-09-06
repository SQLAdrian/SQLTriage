/* In the name of God, the Merciful, the Compassionate */
/*
 * Renders the availability-group primary-replica guard step for a SQL Agent job.
 *
 * The guard BODY is the operator's own T-SQL and ships as authored. The app substitutes
 * exactly one thing — the database name — and it is charset-guarded before substitution and
 * literal-escaped on the way in, so the rendered batch is injection-free by construction.
 *
 * Shape (mirrors MaintenanceSolutionOpRenderer):
 *   apply    IF NOT EXISTS (guard already at step 1) -> sp_add_jobstep @step_id = 1
 *                                                    + sp_update_job  @start_step_id = 1
 *   snapshot read-only: start_step_id, step count, step-1 name  (rollback target + baseline)
 *   inverse  IF EXISTS (guard at step 1) -> sp_delete_jobstep + restore original start_step_id
 *   verify   read-only: is the guard now step 1, and did the step count grow by exactly one
 *
 * The IF NOT EXISTS wrapper is what makes re-apply a strict no-op: idempotency is a property
 * of the rendered batch, not of the caller remembering to check first.
 *
 * WHY sp_update_job @start_step_id IS NOT OPTIONAL: msdb renumbers step rows when a step is
 * inserted at position 1, but the job's own start_step_id is not guaranteed to follow. Without
 * this the job would happily start at what is now step 2 and skip the guard entirely — a
 * silent no-op, which is the exact failure mode this product exists to prevent.
 */

using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using SQLTriage.Data.Models.Jobs;

namespace SQLTriage.Data.Services.Remediation
{
    public static class AgPrimaryGuardOpRenderer
    {
        /// <summary>Request parameter: the Agent job to guard.</summary>
        public const string JobNameParam = "JobName";

        /// <summary>Request parameter: the database whose primary-replica role gates the job.</summary>
        public const string DbNameParam = "DbName";

        /// <summary>
        /// Both parameters are DATA values inside N'...' literals, never identifiers, but they
        /// are still charset-guarded: a job or database name containing a quote, bracket,
        /// semicolon or control character has no legitimate reason to reach this renderer, and
        /// refusing is cheaper to reason about than trusting the escaping alone.
        /// </summary>
        /// Parentheses, commas and brackets-free punctuation are permitted because real Agent
        /// job names routinely carry them ("DBA - IndexOptimize (USER_DATABASES)"); quote,
        /// square bracket, semicolon and control characters are not.
        private static readonly Regex SafeName = new(@"^[A-Za-z0-9_\-\.\(\), \\$#@]{1,128}$", RegexOptions.Compiled);

        public static bool IsSafeName(string? value) => SafeName.IsMatch((value ?? string.Empty).Trim());

        private static string Lit(string raw) => "N'" + (raw ?? string.Empty).Replace("'", "''") + "'";

        /// <summary>
        /// The guard body, as authored by the operator, with the single DBName substitution
        /// applied. Kept as one place so apply and tests can never disagree about what runs.
        /// </summary>
        public static string RenderGuardBody(string dbName)
        {
            // Adrian's T-SQL, verbatim in structure; DBName is the only substitution point.
            var db = dbName.Replace("'", "''");
            return
                "--Check if the Database is the Primary\n" +
                $"IF sys.fn_hadr_is_primary_replica('{db}') = 1\n" +
                "BEGIN\n" +
                "    PRINT 'OK';\n" +
                "END;\n" +
                "ELSE\n" +
                "BEGIN\n" +
                $"    RAISERROR('DB {db} is not primary', 16, -1);\n" +
                "END;";
        }

        /// <summary>
        /// Read-only predicate: is a SQLTriage guard already sitting at step 1 of this job?
        /// Used as the idempotency wrapper for apply and the EXISTS test for the inverse.
        /// </summary>
        private static string GuardExistsPredicate(string jobName) =>
            "EXISTS (SELECT 1 FROM msdb.dbo.sysjobsteps s " +
            "JOIN msdb.dbo.sysjobs j ON j.job_id = s.job_id " +
            $"WHERE j.name = {Lit(jobName)} AND s.step_id = 1 " +
            $"AND s.step_name = {Lit(AgentJobDefinition.GuardStepName)})";

        public static bool TryRenderApplySql(
            IReadOnlyDictionary<string, string>? parameters, out string sql, out string error)
        {
            sql = string.Empty;
            error = string.Empty;

            if (!TryResolve(parameters, out var jobName, out var dbName, out error)) return false;

            var body = RenderGuardBody(dbName).Replace("'", "''");

            var sb = new StringBuilder();
            sb.AppendLine($"IF NOT {GuardExistsPredicate(jobName)}");
            sb.AppendLine("BEGIN");
            sb.AppendLine($"    EXEC msdb.dbo.sp_add_jobstep @job_name = {Lit(jobName)}, @step_id = 1, " +
                          $"@step_name = {Lit(AgentJobDefinition.GuardStepName)}, @subsystem = N'TSQL', " +
                          $"@database_name = N'master', @command = N'{body}', " +
                          "@on_success_action = 3, @on_fail_action = 2;");
            sb.AppendLine($"    EXEC msdb.dbo.sp_update_job @job_name = {Lit(jobName)}, @start_step_id = 1;");
            sb.AppendLine("END");
            sql = sb.ToString();
            return true;
        }

        /// <summary>
        /// Read-only scalar: 1 when a guard is already at step 1. Drives the NoOp decision, so
        /// a re-run costs nothing and changes nothing.
        /// </summary>
        public static string RenderGuardExistsSql(string jobName) =>
            $"SELECT CASE WHEN {GuardExistsPredicate(jobName)} THEN 1 ELSE 0 END;";

        /// <summary>
        /// Read-only scalar: the job's current start_step_id. Captured BEFORE apply because it
        /// is the value the inverse must restore. Returns nothing when the job does not exist.
        /// </summary>
        public static string RenderStartStepIdSql(string jobName) =>
            $"SELECT j.start_step_id FROM msdb.dbo.sysjobs j WHERE j.name = {Lit(jobName)};";

        /// <summary>Read-only pre-change state: the rollback target and the verify baseline.</summary>
        public static string RenderSnapshotSql(string jobName) =>
            "SELECT j.start_step_id AS StartStepId, " +
            "       (SELECT COUNT(*) FROM msdb.dbo.sysjobsteps x WHERE x.job_id = j.job_id) AS StepCount, " +
            "       (SELECT TOP 1 s.step_name FROM msdb.dbo.sysjobsteps s WHERE s.job_id = j.job_id AND s.step_id = 1) AS Step1Name " +
            $"FROM msdb.dbo.sysjobs j WHERE j.name = {Lit(jobName)};";

        /// <summary>
        /// The named inverse: remove the step this apply added and put start_step_id back where
        /// the snapshot found it. Guarded IF EXISTS so a rollback that runs twice is harmless.
        /// </summary>
        public static string RenderInverseSql(string jobName, int originalStartStepId)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"IF {GuardExistsPredicate(jobName)}");
            sb.AppendLine("BEGIN");
            sb.AppendLine($"    EXEC msdb.dbo.sp_delete_jobstep @job_name = {Lit(jobName)}, @step_id = 1;");
            sb.AppendLine($"    EXEC msdb.dbo.sp_update_job @job_name = {Lit(jobName)}, @start_step_id = {originalStartStepId};");
            sb.AppendLine("END");
            return sb.ToString();
        }

        /// <summary>Read-only: did the guard land at step 1 and become the job's start step?</summary>
        public static string RenderVerifySql(string jobName) =>
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM msdb.dbo.sysjobsteps s " +
            "JOIN msdb.dbo.sysjobs j ON j.job_id = s.job_id " +
            $"WHERE j.name = {Lit(jobName)} AND s.step_id = 1 " +
            $"AND s.step_name = {Lit(AgentJobDefinition.GuardStepName)} AND j.start_step_id = 1) " +
            "THEN 1 ELSE 0 END AS GuardInPlace;";

        /// <summary>
        /// Value-independent representative form of the apply, for the safety gate's
        /// classification pass — same pattern as the other renderers. The gate vets the SHAPE
        /// (sp_add_jobstep + sp_update_job under an IF NOT EXISTS guard), not real parameters.
        /// </summary>
        public static string RenderRepresentativeForClassification()
        {
            var p = new Dictionary<string, string>
            {
                [JobNameParam] = "SQLTriage Representative Job",
                [DbNameParam] = "RepresentativeDb"
            };
            TryRenderApplySql(p, out var apply, out _);
            return apply + "\n" + RenderInverseSql("SQLTriage Representative Job", 1);
        }

        private static bool TryResolve(
            IReadOnlyDictionary<string, string>? parameters,
            out string jobName, out string dbName, out string error)
        {
            jobName = string.Empty;
            dbName = string.Empty;
            error = string.Empty;

            if (parameters is null)
            {
                error = "Job name and database name are required.";
                return false;
            }

            parameters.TryGetValue(JobNameParam, out var rawJob);
            parameters.TryGetValue(DbNameParam, out var rawDb);

            jobName = (rawJob ?? string.Empty).Trim();
            dbName = (rawDb ?? string.Empty).Trim();

            if (!IsSafeName(jobName))
            {
                error = $"Job name '{jobName}' is empty or contains characters that are not permitted here.";
                return false;
            }

            if (!IsSafeName(dbName))
            {
                error = $"Database name '{dbName}' is empty or contains characters that are not permitted here.";
                return false;
            }

            return true;
        }
    }
}
