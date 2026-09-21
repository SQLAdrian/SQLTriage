/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models.Jobs;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The sync renderer writes to a LIVE availability-group secondary, so these tests pin the
    /// properties that keep that survivable: the two-pass step wiring (a forward flow reference
    /// cannot be set before its target step exists), drop-before-create, injection refusal, and
    /// the hard separation between "sync a job" and "delete a job".
    /// </summary>
    public class RemediationAgentJobSyncTests
    {
        private static AgentJobDefinition Job(string name = "Nightly Reindex", int steps = 3)
        {
            var j = new AgentJobDefinition { Name = name, Enabled = true, StartStepId = 1, Description = "d" };
            for (var i = 1; i <= steps; i++)
                j.Steps.Add(new AgentJobStep
                {
                    StepId = i,
                    StepName = $"Step {i}",
                    Subsystem = "TSQL",
                    DatabaseName = "master",
                    Command = $"SELECT {i};",
                    // Last step quits; earlier ones jump forward to step 3 — the case that forces
                    // the second pass (you cannot target step 3 while creating step 1).
                    OnSuccessAction = i == steps ? 1 : 4,
                    OnSuccessStepId = i == steps ? 0 : steps,
                    OnFailAction = 2,
                    OnFailStepId = 0
                });
            j.Schedules.Add(new AgentJobSchedule
            {
                Name = "Nightly", Enabled = true, FreqType = 4, FreqInterval = 1, ActiveStartTime = 20000
            });
            return j;
        }

        private static string Render(AgentJobDefinition j)
        {
            Assert.True(AgentJobSyncOpRenderer.TryRenderSyncSql(j, out var sql, out var err), err);
            return sql;
        }

        // ── Shape ────────────────────────────────────────────────────────────

        [Fact]
        public void Drops_before_recreating()
        {
            var sql = Render(Job());
            Assert.Contains("sp_delete_job", sql);
            Assert.True(sql.IndexOf("sp_delete_job", StringComparison.Ordinal)
                      < sql.IndexOf("sp_add_job", StringComparison.Ordinal),
                "the drop must precede the create, or sp_add_job fails on a duplicate name");
        }

        [Fact]
        public void Creates_every_step_then_wires_flow_in_a_second_pass()
        {
            var sql = Render(Job(steps: 3));

            // Pass 1: three sp_add_jobstep calls, EVERY one with neutral flow. Asserted per
            // creation line rather than by counting "@on_success_action = 1" across the batch —
            // a step whose real action is also 1 would inflate that count from pass 2.
            var addLines = sql.Split('\n').Where(l => l.Contains("sp_add_jobstep")).ToList();
            Assert.Equal(3, addLines.Count);
            Assert.All(addLines, l =>
            {
                Assert.Contains("@on_success_action = 1", l);
                Assert.Contains("@on_fail_action = 2", l);
                Assert.DoesNotContain("@on_success_step_id", l);
            });

            // Pass 2: three sp_update_jobstep calls carrying the REAL targets.
            Assert.Equal(3, Count(sql, "sp_update_jobstep"));
            Assert.Contains("@on_success_action = 4", sql);
            Assert.Contains("@on_success_step_id = 3", sql);

            // Ordering is the whole point: a forward target cannot be set at creation time.
            Assert.True(sql.LastIndexOf("sp_add_jobstep", StringComparison.Ordinal)
                      < sql.IndexOf("sp_update_jobstep", StringComparison.Ordinal),
                "all steps must exist before any flow target is applied");
        }

        [Fact]
        public void Sets_start_step_and_attaches_schedules_and_jobserver()
        {
            var sql = Render(Job());
            Assert.Contains("sp_update_job", sql);
            Assert.Contains("@start_step_id = 1", sql);
            Assert.Contains("sp_add_schedule", sql);
            Assert.Contains("sp_attach_schedule", sql);
            Assert.Contains("sp_add_jobserver", sql);
            Assert.Contains("N'(LOCAL)'", sql);
        }

        [Fact]
        public void Carries_the_enabled_state_across()
        {
            var j = Job();
            j.Enabled = false;
            Assert.Contains("@enabled = 0", Render(j));
        }

        [Fact]
        public void Does_not_script_the_owner()
        {
            // The primary's owner login routinely does not exist on the secondary; the job is
            // created under the executing context and the diff reports the mismatch instead.
            Assert.DoesNotContain("@owner_login_name", Render(Job()));
        }

        // ── Refusals ─────────────────────────────────────────────────────────

        [Fact]
        public void Refuses_a_job_with_no_steps()
        {
            var j = Job(steps: 0);
            Assert.False(AgentJobSyncOpRenderer.TryRenderSyncSql(j, out _, out var err));
            Assert.Contains("no steps", err);
        }

        [Fact]
        public void Refuses_a_null_job()
        {
            Assert.False(AgentJobSyncOpRenderer.TryRenderSyncSql(null, out _, out _));
        }

        [Theory]
        [InlineData("O'Brien's job")]
        [InlineData("job]; DROP DATABASE x--")]
        [InlineData("")]
        public void Refuses_unsafe_job_names(string name)
        {
            Assert.False(AgentJobSyncOpRenderer.TryRenderSyncSql(Job(name), out _, out var err));
            Assert.False(string.IsNullOrWhiteSpace(err));
        }

        [Fact]
        public void Refuses_an_unsafe_schedule_name()
        {
            var j = Job();
            j.Schedules[0].Name = "sched'; EXEC sp_who--";
            Assert.False(AgentJobSyncOpRenderer.TryRenderSyncSql(j, out _, out var err));
            Assert.Contains("Schedule name", err);
        }

        [Fact]
        public void Escapes_quotes_inside_a_step_body_rather_than_refusing()
        {
            // Step bodies are tenant T-SQL from a connected server's msdb — quotes are normal
            // there and must survive as escaped literals, not cause a refusal.
            var j = Job(steps: 1);
            j.Steps[0].Command = "PRINT 'it''s fine'; SELECT 1;";
            var sql = Render(j);
            Assert.Contains("PRINT ''it''''s fine''", sql);
        }

        // ── The gate ─────────────────────────────────────────────────────────

        [Fact]
        public void Rendered_sync_is_blocked_without_a_registered_context()
        {
            Assert.Equal(SqlClassification.Blocked, SqlSafetyValidator.Classify(Render(Job()), null));
        }

        [Fact]
        public void Rendered_sync_is_remediation_under_the_registered_key()
        {
            Assert.Equal(SqlClassification.Remediation,
                SqlSafetyValidator.Classify(Render(Job()), new RemediationContext("SYNCAGENTJOB")));
        }

        [Fact]
        public void Representative_render_classifies()
        {
            var sql = AgentJobSyncOpRenderer.RenderRepresentativeForClassification();
            Assert.Equal(SqlClassification.Remediation,
                SqlSafetyValidator.Classify(sql, new RemediationContext("SYNCAGENTJOB")));
        }

        [Fact]
        public void Verify_and_exists_reads_are_safe()
        {
            foreach (var sql in new[]
            {
                AgentJobSyncOpRenderer.RenderExistsSql("Nightly Reindex"),
                AgentJobSyncOpRenderer.RenderVerifySql("Nightly Reindex", 3, 1)
            })
            {
                Assert.Equal(SqlClassification.Safe, SqlSafetyValidator.Classify(sql, null));
            }
        }

        // ── Delete-extra is a separate, opt-in path ──────────────────────────

        [Fact]
        public void Sync_never_deletes_anything_other_than_the_job_it_recreates()
        {
            var sql = Render(Job("Only This Job"));
            // Exactly one sp_delete_job, and it names the job being recreated.
            Assert.Equal(1, Count(sql, "sp_delete_job"));
            Assert.Contains("N'Only This Job'", sql);
        }

        [Fact]
        public void Delete_extra_is_guarded_and_refuses_unsafe_names()
        {
            Assert.True(AgentJobSyncOpRenderer.TryRenderDeleteExtraSql("Stale Job", out var sql, out _));
            Assert.Contains("IF EXISTS", sql);
            Assert.Contains("sp_delete_job", sql);

            Assert.False(AgentJobSyncOpRenderer.TryRenderDeleteExtraSql("bad'; DROP DATABASE x--", out _, out var err));
            Assert.False(string.IsNullOrWhiteSpace(err));
        }

        // ── Shipped templates ────────────────────────────────────────────────

        [Fact]
        public void Sync_template_is_registered_reversible_and_one_credit()
        {
            var t = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance).TryGet("SYNCAGENTJOB");
            Assert.NotNull(t);
            Assert.True(t!.Reversible);
            Assert.Equal(1, RemediationCreditCost.For(t));
            Assert.Equal(RemediationOpKind.AgentJobSync, t.Operation!.OpKind);
        }

        [Fact]
        public void Delete_template_is_sensitive_irreversible_and_priced_accordingly()
        {
            var t = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance).TryGet("DELETEEXTRAJOB");
            Assert.NotNull(t);
            Assert.False(t!.Reversible);            // msdb keeps no copy of a dropped job
            Assert.Equal(RemediationRiskClass.Sensitive, t.RiskClass);
            Assert.Equal(3, RemediationCreditCost.For(t));   // Sensitive 2 + irreversible 1
            Assert.Equal(RemediationOpKind.AgentJobDeleteExtra, t.Operation!.OpKind);
        }

        [Fact]
        public void The_two_paths_are_distinct_op_kinds_so_a_sync_can_never_delete()
        {
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            Assert.NotEqual(store.TryGet("SYNCAGENTJOB")!.Operation!.OpKind,
                            store.TryGet("DELETEEXTRAJOB")!.Operation!.OpKind);
        }

        private static int Count(string haystack, string needle)
        {
            var n = 0;
            var i = haystack.IndexOf(needle, StringComparison.Ordinal);
            while (i >= 0) { n++; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal); }
            return n;
        }
    }
}
