/* In the name of God, the Merciful, the Compassionate */
/*
 * Canonical, connection-agnostic shape of a SQL Agent job as read from msdb.
 *
 * Deliberately a plain data model with no SMO and no live connection: it is read once by
 * JobInventoryService and then reasoned over offline (guard detection, eligibility, and —
 * later — primary/secondary diffing). Anything that needs a server round-trip belongs in
 * the service, not here.
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace SQLTriage.Data.Models.Jobs
{
    public class AgentJobDefinition
    {
        public Guid JobId { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public int StartStepId { get; set; }
        public string? Description { get; set; }
        public List<AgentJobStep> Steps { get; set; } = new();
        public List<AgentJobSchedule> Schedules { get; set; } = new();

        /// <summary>
        /// The job's owner login on the instance it was read from. Informational only: a synced
        /// job is created under whatever context SQLTriage connects as, because the primary's
        /// owner login very often does not exist on the secondary. The diff surfaces a mismatch
        /// rather than failing the sync over it.
        /// </summary>
        public string? OwnerLogin { get; set; }

        /// <summary>
        /// The step name SQLTriage writes when it injects an availability-group primary
        /// guard. Used both to render the guard and to recognise one it already wrote.
        /// </summary>
        public const string GuardStepName = "SQLTriage: AG primary guard";

        /// <summary>
        /// True when step 1 is an AG primary guard. Matches on our own step name OR on the
        /// hadr function appearing in the step body — belt and braces, so a guard a DBA
        /// hand-wrote is respected rather than duplicated.
        /// </summary>
        public bool HasAgPrimaryGuard
        {
            get
            {
                var first = Steps.FirstOrDefault(s => s.StepId == 1);
                if (first is null) return false;

                return string.Equals(first.StepName, GuardStepName, StringComparison.OrdinalIgnoreCase)
                    || (first.Command ?? string.Empty)
                           .Contains("fn_hadr_is_primary_replica", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Whether a guard can be injected at step 1 automatically and safely.
        /// <para>
        /// Inserting a step renumbers the ones after it. msdb renumbers the step rows, but
        /// EXPLICIT flow targets (on_success_action / on_fail_action = 4, "go to step N")
        /// stored on OTHER steps are the classic place where that renumbering does not
        /// follow — a job wired "on failure go to step 3" would silently start pointing at
        /// the wrong step. Rather than rewrite another DBA's control flow, we refuse those
        /// jobs and surface them for manual review. Jobs whose steps only quit or fall
        /// through to the next step have no such references and are safe.
        /// </para>
        /// <para>
        /// Live probe 2026-07-30 (SQL 2025 17.0.1000.7, C:\temp\ag-verify\01-msdb-renumber-probe.sql):
        /// on 2025, explicit go-to targets WERE retargeted by the insert — so this refusal is
        /// provably conservative there, not required. It stays because the estate a guard sweep
        /// runs across is rarely all-2025, and nothing older has been probed; relaxing it means
        /// re-running that probe per supported version and gating on the result, not deleting
        /// this check.
        /// </para>
        /// </summary>
        public bool EligibleForGuard =>
            Steps.Count > 0 &&
            Steps.All(s => s.OnSuccessAction is 1 or 3 && s.OnFailAction is 1 or 2);

        /// <summary>Why the job is not eligible, for the UI. Null when it is.</summary>
        public string? IneligibleReason =>
            Steps.Count == 0
                ? "Job has no steps."
                : EligibleForGuard
                    ? null
                    : "Job uses explicit go-to-step flow targets; inserting a step would need its control flow rewritten. Handle manually.";
    }

    /// <summary>
    /// An msdb schedule attached to a job. Field names mirror sysschedules so the sync renderer
    /// can hand them straight back to sp_add_schedule without a translation layer to get wrong.
    /// </summary>
    public class AgentJobSchedule
    {
        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public int FreqType { get; set; }
        public int FreqInterval { get; set; }
        public int FreqSubdayType { get; set; }
        public int FreqSubdayInterval { get; set; }
        public int FreqRelativeInterval { get; set; }
        public int FreqRecurrenceFactor { get; set; }
        public int ActiveStartDate { get; set; }
        public int ActiveEndDate { get; set; }
        public int ActiveStartTime { get; set; }
        public int ActiveEndTime { get; set; }
    }

    public class AgentJobStep
    {
        public int StepId { get; set; }
        public string StepName { get; set; } = string.Empty;
        public string? Subsystem { get; set; }
        public string? DatabaseName { get; set; }
        public string? Command { get; set; }

        /// <summary>1 = quit success, 2 = quit failure, 3 = go to next step, 4 = go to step.</summary>
        public int OnSuccessAction { get; set; }
        public int OnSuccessStepId { get; set; }
        public int OnFailAction { get; set; }
        public int OnFailStepId { get; set; }
    }
}
