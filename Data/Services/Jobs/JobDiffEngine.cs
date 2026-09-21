/* In the name of God, the Merciful, the Compassionate */
/*
 * Compares the Agent-job estate on two replicas and says, per job, what differs.
 *
 * PURE by design — no connections, no logging, no clock. Everything that decides whether a job
 * gets overwritten on a live secondary is decided here, so it must be exhaustively testable
 * without a SQL Server in the room.
 *
 * Equality is a canonical hash rather than field-by-field comparison so that adding a field to
 * the model can't silently drop out of the comparison. What is EXCLUDED from the hash matters
 * as much as what is in it:
 *   - job_id      : always differs across instances; comparing it would mark everything Different
 *   - owner       : the primary's owner login frequently does not exist on the secondary
 *                   (Adrian's ruling: create under the executing context, report the mismatch)
 *   - dates / version_number : churn on every edit, say nothing about equivalence
 * Command text is CRLF- and trailing-whitespace-normalised: the same script pasted through two
 * different editors is the same script, and flagging it as drift trains people to ignore drift.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SQLTriage.Data.Models.Jobs;

namespace SQLTriage.Data.Services.Jobs
{
    public enum JobDiffStatus
    {
        /// <summary>Present on the primary, absent on the secondary.</summary>
        Missing,
        /// <summary>Present on both, but not equivalent.</summary>
        Different,
        /// <summary>Equivalent on both — nothing to do.</summary>
        Match,
        /// <summary>On the secondary only. Reported, never deleted without an explicit opt-in.</summary>
        ExtraOnSecondary
    }

    public sealed class JobDiffRow
    {
        public string JobName { get; init; } = string.Empty;
        public JobDiffStatus Status { get; init; }

        /// <summary>
        /// Which axis differs — "steps differ", "schedule differs", "enabled state differs", or a
        /// combination. Separated so a shop that staggers schedules on purpose can leave those
        /// rows unticked instead of being forced into all-or-nothing.
        /// </summary>
        public string Details { get; init; } = string.Empty;

        /// <summary>True when the two sides' owner logins differ; informational only.</summary>
        public bool OwnerMismatch { get; init; }

        public AgentJobDefinition? Primary { get; init; }
        public AgentJobDefinition? Secondary { get; init; }

        /// <summary>Only Missing and Different are actionable in a primary -> secondary sync.</summary>
        public bool IsActionable => Status is JobDiffStatus.Missing or JobDiffStatus.Different;
    }

    public static class JobDiffEngine
    {
        public static IReadOnlyList<JobDiffRow> Diff(
            IEnumerable<AgentJobDefinition>? primary,
            IEnumerable<AgentJobDefinition>? secondary)
        {
            var p = (primary ?? Enumerable.Empty<AgentJobDefinition>())
                .GroupBy(j => j.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var s = (secondary ?? Enumerable.Empty<AgentJobDefinition>())
                .GroupBy(j => j.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var rows = new List<JobDiffRow>();

            foreach (var (name, pj) in p)
            {
                if (!s.TryGetValue(name, out var sj))
                {
                    rows.Add(new JobDiffRow
                    {
                        JobName = name,
                        Status = JobDiffStatus.Missing,
                        Details = "Not present on the secondary.",
                        Primary = pj
                    });
                    continue;
                }

                var ownerMismatch = !string.Equals(
                    pj.OwnerLogin ?? string.Empty, sj.OwnerLogin ?? string.Empty, StringComparison.OrdinalIgnoreCase);

                var axes = new List<string>();
                if (pj.Enabled != sj.Enabled)
                    axes.Add($"enabled state differs (primary {(pj.Enabled ? "enabled" : "disabled")})");
                if (StepsHash(pj) != StepsHash(sj)) axes.Add("steps differ");
                if (ScheduleHash(pj) != ScheduleHash(sj)) axes.Add("schedule differs");

                if (axes.Count == 0)
                {
                    rows.Add(new JobDiffRow
                    {
                        JobName = name,
                        Status = JobDiffStatus.Match,
                        Details = ownerMismatch ? "Equivalent (owner login differs)." : "Equivalent.",
                        OwnerMismatch = ownerMismatch,
                        Primary = pj,
                        Secondary = sj
                    });
                }
                else
                {
                    rows.Add(new JobDiffRow
                    {
                        JobName = name,
                        Status = JobDiffStatus.Different,
                        Details = string.Join("; ", axes),
                        OwnerMismatch = ownerMismatch,
                        Primary = pj,
                        Secondary = sj
                    });
                }
            }

            foreach (var (name, sj) in s)
            {
                if (p.ContainsKey(name)) continue;
                rows.Add(new JobDiffRow
                {
                    JobName = name,
                    Status = JobDiffStatus.ExtraOnSecondary,
                    Details = "Exists only on the secondary. Not touched by a sync.",
                    Secondary = sj
                });
            }

            return rows.OrderBy(r => r.JobName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Canonical hash of a job's step set, in step order.</summary>
        public static string StepsHash(AgentJobDefinition job)
        {
            var sb = new StringBuilder();
            sb.Append("start=").Append(job.StartStepId).Append('\u001f');
            foreach (var st in job.Steps.OrderBy(x => x.StepId))
            {
                sb.Append(st.StepId).Append('\u001f')
                  .Append(Norm(st.StepName)).Append('\u001f')
                  .Append(Norm(st.Subsystem)).Append('\u001f')
                  .Append(Norm(st.DatabaseName)).Append('\u001f')
                  .Append(NormCommand(st.Command)).Append('\u001f')
                  .Append(st.OnSuccessAction).Append('\u001f')
                  .Append(st.OnSuccessStepId).Append('\u001f')
                  .Append(st.OnFailAction).Append('\u001f')
                  .Append(st.OnFailStepId).Append('\u001e');
            }
            return Sha(sb.ToString());
        }

        /// <summary>Canonical hash of a job's schedules, ordered by name so msdb ordering can't matter.</summary>
        public static string ScheduleHash(AgentJobDefinition job)
        {
            var sb = new StringBuilder();
            foreach (var sc in job.Schedules.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(Norm(sc.Name)).Append('\u001f')
                  .Append(sc.Enabled ? '1' : '0').Append('\u001f')
                  .Append(sc.FreqType).Append('\u001f')
                  .Append(sc.FreqInterval).Append('\u001f')
                  .Append(sc.FreqSubdayType).Append('\u001f')
                  .Append(sc.FreqSubdayInterval).Append('\u001f')
                  .Append(sc.FreqRelativeInterval).Append('\u001f')
                  .Append(sc.FreqRecurrenceFactor).Append('\u001f')
                  .Append(sc.ActiveStartDate).Append('\u001f')
                  .Append(sc.ActiveEndDate).Append('\u001f')
                  .Append(sc.ActiveStartTime).Append('\u001f')
                  .Append(sc.ActiveEndTime).Append('\u001e');
            }
            return Sha(sb.ToString());
        }

        private static string Norm(string? s) => (s ?? string.Empty).Trim();

        /// <summary>
        /// Line-ending and trailing-whitespace insensitive. The same T-SQL saved through two
        /// editors is the same T-SQL; reporting that as drift teaches operators to ignore drift.
        /// </summary>
        private static string NormCommand(string? s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var lines = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            return string.Join("\n", lines.Select(l => l.TrimEnd())).Trim();
        }

        private static string Sha(string text)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(bytes).ToLower(CultureInfo.InvariantCulture);
        }
    }
}
