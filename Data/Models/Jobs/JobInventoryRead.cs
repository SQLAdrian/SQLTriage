/* In the name of God, the Merciful, the Compassionate */
/*
 * The OUTCOME of an msdb job read, not just its rows.
 *
 * pages-r1-01 / pages-r1-11 / pages-r2-01 (honesty hunt, 2026-08-28): JobInventoryService
 * returned Array.Empty on a refused, failed or unroutable read, which is the same value it
 * returns for an instance that genuinely has no Agent jobs. Every caller therefore read a
 * failure as a measurement:
 *   - /agent-job-sync diffed an empty primary against a populated secondary and offered the
 *     secondary's entire estate for an IRREVERSIBLE delete (DELETEEXTRAJOB, Reversible=false),
 *     under the sentence "N job(s) compared";
 *   - /agent-job-guard printed "No SQL Agent jobs found on <server>." - a claim about the
 *     server made from a read that never succeeded;
 *   - the sync executor took the same empty list as its ROLLBACK SNAPSHOT, and a null snapshot
 *     means "the job did not exist before this apply", so a failed verify would have DELETED a
 *     pre-existing job on the secondary.
 *
 * The fix is this type: a read carries what happened as well as what it found. There is no
 * implicit conversion to a list on purpose - a caller must look at the outcome to get at the
 * rows.
 */

using System;
using System.Collections.Generic;

namespace SQLTriage.Data.Models.Jobs
{
    /// <summary>What happened when the inventory tried to read msdb.</summary>
    public enum JobReadOutcome
    {
        /// <summary>msdb answered. <see cref="JobInventoryRead.Jobs"/> is what it holds - possibly zero jobs.</summary>
        Read,

        /// <summary>No configured connection covers this instance name, so nothing was contacted.</summary>
        NoConnection,

        /// <summary>The connect or the query threw - unreachable, refused, timed out, no msdb rights.</summary>
        Failed
    }

    /// <summary>
    /// One msdb read: its outcome, its rows when it succeeded, and the reason when it did not.
    /// </summary>
    public sealed class JobInventoryRead
    {
        public string ServerName { get; init; } = string.Empty;
        public JobReadOutcome Outcome { get; init; }
        public IReadOnlyList<AgentJobDefinition> Jobs { get; init; } = Array.Empty<AgentJobDefinition>();

        /// <summary>The exception message (or the routing failure), null on a successful read.</summary>
        public string? FailureDetail { get; init; }

        /// <summary>True only when msdb actually answered.</summary>
        public bool Succeeded => Outcome == JobReadOutcome.Read;

        public static JobInventoryRead Success(string serverName, IReadOnlyList<AgentJobDefinition> jobs) =>
            new() { ServerName = serverName, Outcome = JobReadOutcome.Read, Jobs = jobs };

        public static JobInventoryRead NoConnectionFor(string serverName) =>
            new() { ServerName = serverName, Outcome = JobReadOutcome.NoConnection };

        public static JobInventoryRead Failure(string serverName, string detail) =>
            new() { ServerName = serverName, Outcome = JobReadOutcome.Failed, FailureDetail = detail };

        /// <summary>
        /// One sentence naming the instance and why nothing was read from it. Empty string on a
        /// successful read - a caller that prints this unconditionally prints nothing extra.
        /// </summary>
        public string DescribeFailure() => Outcome switch
        {
            JobReadOutcome.Read => string.Empty,
            JobReadOutcome.NoConnection =>
                $"No configured connection covers '{ServerName}', so its SQL Agent jobs were not read. "
                + "This is not a statement about what jobs exist there.",
            _ =>
                $"Could not read the SQL Agent jobs on '{ServerName}': {FailureDetail} "
                + "Nothing below describes that instance."
        };

        /// <summary>
        /// The refusal a two-sided comparison must print instead of a diff when either side did
        /// not answer. Null when both sides answered and a diff is safe to render.
        /// <para>
        /// This is the seam that stops the delete offer: an unread primary yields no rows, and
        /// rows are what the page offers for deletion.
        /// </para>
        /// </summary>
        public static string? DescribeComparisonBlocked(JobInventoryRead source, JobInventoryRead target)
        {
            if (source.Succeeded && target.Succeeded) return null;

            var which = !source.Succeeded && !target.Succeeded
                ? "Neither instance answered"
                : !source.Succeeded ? "The primary did not answer" : "The secondary did not answer";

            var detail = !source.Succeeded ? source.DescribeFailure() : target.DescribeFailure();
            if (!source.Succeeded && !target.Succeeded)
                detail = source.DescribeFailure() + " " + target.DescribeFailure();

            return $"{which}, so there is no comparison to show. {detail} "
                 + "A job missing from an unread side is not evidence that it is missing from the server.";
        }
    }
}
