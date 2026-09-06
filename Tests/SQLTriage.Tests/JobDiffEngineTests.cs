/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using System.Linq;
using SQLTriage.Data.Models.Jobs;
using SQLTriage.Data.Services.Jobs;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The diff decides what gets overwritten on a LIVE secondary, so it is pinned hard.
    /// Two classes of mistake matter most:
    ///   - a false Match, which silently leaves the standby out of step after a failover;
    ///   - a false Different, which nags until operators stop reading the diff at all.
    /// </summary>
    public class JobDiffEngineTests
    {
        private static AgentJobStep Step(int id, string cmd = "SELECT 1", string name = "Work") =>
            new() { StepId = id, StepName = name, Subsystem = "TSQL", Command = cmd, OnSuccessAction = 3, OnFailAction = 2 };

        private static AgentJobSchedule Sched(string name = "Nightly", int freqType = 4, int startTime = 20000) =>
            new() { Name = name, Enabled = true, FreqType = freqType, FreqInterval = 1, ActiveStartTime = startTime };

        private static AgentJobDefinition Job(
            string name = "Reindex",
            bool enabled = true,
            string cmd = "SELECT 1",
            string? owner = "sa",
            IEnumerable<AgentJobSchedule>? schedules = null) => new()
            {
                Name = name,
                Enabled = enabled,
                StartStepId = 1,
                OwnerLogin = owner,
                Steps = { Step(1, cmd) },
                Schedules = (schedules ?? new[] { Sched() }).ToList()
            };

        private static JobDiffRow One(IEnumerable<AgentJobDefinition> p, IEnumerable<AgentJobDefinition> s) =>
            JobDiffEngine.Diff(p, s).Single();

        // ── The four statuses ────────────────────────────────────────────────

        [Fact]
        public void Identical_jobs_are_a_match()
        {
            var row = One(new[] { Job() }, new[] { Job() });
            Assert.Equal(JobDiffStatus.Match, row.Status);
            Assert.False(row.IsActionable);
        }

        [Fact]
        public void Job_only_on_primary_is_missing()
        {
            var row = One(new[] { Job() }, System.Array.Empty<AgentJobDefinition>());
            Assert.Equal(JobDiffStatus.Missing, row.Status);
            Assert.True(row.IsActionable);
        }

        [Fact]
        public void Job_only_on_secondary_is_extra_and_not_actionable()
        {
            // Never deleted by a sync — deleting someone's job because it isn't on the other
            // side would be the single most destructive thing this feature could do.
            var row = One(System.Array.Empty<AgentJobDefinition>(), new[] { Job() });
            Assert.Equal(JobDiffStatus.ExtraOnSecondary, row.Status);
            Assert.False(row.IsActionable);
        }

        [Fact]
        public void Different_step_command_is_different()
        {
            var row = One(new[] { Job(cmd: "SELECT 1") }, new[] { Job(cmd: "SELECT 2") });
            Assert.Equal(JobDiffStatus.Different, row.Status);
            Assert.Contains("steps differ", row.Details);
        }

        // ── Axis labelling (Adrian's ruling: distinguish, don't merge) ────────

        [Fact]
        public void Schedule_only_drift_is_flagged_as_schedule_not_steps()
        {
            var row = One(
                new[] { Job(schedules: new[] { Sched(startTime: 20000) }) },
                new[] { Job(schedules: new[] { Sched(startTime: 30000) }) });

            Assert.Equal(JobDiffStatus.Different, row.Status);
            Assert.Contains("schedule differs", row.Details);
            Assert.DoesNotContain("steps differ", row.Details);
        }

        [Fact]
        public void Enabled_state_drift_is_called_out_separately()
        {
            var row = One(new[] { Job(enabled: true) }, new[] { Job(enabled: false) });
            Assert.Contains("enabled state differs", row.Details);
            Assert.DoesNotContain("steps differ", row.Details);
        }

        [Fact]
        public void Multiple_axes_are_all_reported()
        {
            var row = One(
                new[] { Job(enabled: true, cmd: "A", schedules: new[] { Sched(startTime: 1) }) },
                new[] { Job(enabled: false, cmd: "B", schedules: new[] { Sched(startTime: 2) }) });

            Assert.Contains("enabled state differs", row.Details);
            Assert.Contains("steps differ", row.Details);
            Assert.Contains("schedule differs", row.Details);
        }

        // ── What the hash must IGNORE ────────────────────────────────────────

        [Fact]
        public void Line_endings_and_trailing_whitespace_are_not_drift()
        {
            var row = One(
                new[] { Job(cmd: "SELECT 1\r\nSELECT 2   \r\n") },
                new[] { Job(cmd: "SELECT 1\nSELECT 2\n") });
            Assert.Equal(JobDiffStatus.Match, row.Status);
        }

        [Fact]
        public void Owner_login_difference_is_reported_but_is_not_drift()
        {
            // Adrian's ruling: the job is created under the executing context, so a differing
            // owner is expected and must not force a needless overwrite.
            var row = One(new[] { Job(owner: "CONTOSO\\svc_primary") }, new[] { Job(owner: "sa") });
            Assert.Equal(JobDiffStatus.Match, row.Status);
            Assert.True(row.OwnerMismatch);
            Assert.Contains("owner login differs", row.Details);
        }

        [Fact]
        public void Job_id_difference_is_not_drift()
        {
            var p = Job(); p.JobId = System.Guid.NewGuid();
            var s = Job(); s.JobId = System.Guid.NewGuid();
            Assert.Equal(JobDiffStatus.Match, One(new[] { p }, new[] { s }).Status);
        }

        [Fact]
        public void Schedule_ordering_from_msdb_is_not_drift()
        {
            var a = Job(schedules: new[] { Sched("Nightly"), Sched("Weekly") });
            var b = Job(schedules: new[] { Sched("Weekly"), Sched("Nightly") });
            Assert.Equal(JobDiffStatus.Match, One(new[] { a }, new[] { b }).Status);
        }

        // ── What the hash must CATCH ─────────────────────────────────────────

        [Fact]
        public void Flow_action_change_is_caught_even_with_identical_command_text()
        {
            // "on failure quit" vs "on failure carry on" is a behavioural change with no
            // visible difference in the step's SQL — exactly the drift a text diff would miss.
            var p = Job();
            var s = Job();
            s.Steps[0].OnFailAction = 3;
            Assert.Equal(JobDiffStatus.Different, One(new[] { p }, new[] { s }).Status);
        }

        [Fact]
        public void Extra_step_is_caught()
        {
            var s = Job();
            s.Steps.Add(Step(2, "SELECT 99"));
            Assert.Equal(JobDiffStatus.Different, One(new[] { Job() }, new[] { s }).Status);
        }

        [Fact]
        public void Start_step_change_is_caught()
        {
            var s = Job();
            s.StartStepId = 2;
            Assert.Equal(JobDiffStatus.Different, One(new[] { Job() }, new[] { s }).Status);
        }

        [Fact]
        public void Removed_schedule_is_caught()
        {
            var s = Job(schedules: System.Array.Empty<AgentJobSchedule>());
            Assert.Equal(JobDiffStatus.Different, One(new[] { Job() }, new[] { s }).Status);
        }

        // ── Shape ────────────────────────────────────────────────────────────

        [Fact]
        public void Job_names_are_matched_case_insensitively()
        {
            var row = One(new[] { Job("Reindex") }, new[] { Job("REINDEX") });
            Assert.Equal(JobDiffStatus.Match, row.Status);
        }

        [Fact]
        public void Handles_null_inputs()
        {
            Assert.Empty(JobDiffEngine.Diff(null, null));
            Assert.Single(JobDiffEngine.Diff(new[] { Job() }, null));
            Assert.Single(JobDiffEngine.Diff(null, new[] { Job() }));
        }

        [Fact]
        public void Rows_are_ordered_by_name_for_a_stable_view()
        {
            var rows = JobDiffEngine.Diff(
                new[] { Job("Zeta"), Job("Alpha"), Job("Mike") },
                System.Array.Empty<AgentJobDefinition>());
            Assert.Equal(new[] { "Alpha", "Mike", "Zeta" }, rows.Select(r => r.JobName).ToArray());
        }
    }
}
