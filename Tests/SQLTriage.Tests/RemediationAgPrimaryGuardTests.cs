/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models.Jobs;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The AG primary-replica guard injects step 1 into an EXISTING Agent job. These tests pin
    /// the properties that make that safe to run unattended across an estate:
    ///   (1) the rendered apply is idempotent and sets start_step_id (else the guard is skipped),
    ///   (2) job/database names cannot break out of the literals,
    ///   (3) the inverse actually undoes it,
    ///   (4) the gate classifies the batch as Remediation ONLY under the registered key,
    ///   (5) jobs with explicit go-to-step flow are refused rather than silently rewired.
    /// </summary>
    public class RemediationAgPrimaryGuardTests
    {
        private static Dictionary<string, string> P(string job = "Nightly Reindex", string db = "SalesDb") => new()
        {
            [AgPrimaryGuardOpRenderer.JobNameParam] = job,
            [AgPrimaryGuardOpRenderer.DbNameParam] = db
        };

        // ── Apply shape ──────────────────────────────────────────────────────

        [Fact]
        public void Apply_is_idempotent_and_inserts_at_step_one()
        {
            Assert.True(AgPrimaryGuardOpRenderer.TryRenderApplySql(P(), out var sql, out _));

            Assert.Contains("IF NOT EXISTS", sql);
            Assert.Contains("sp_add_jobstep", sql);
            Assert.Contains("@step_id = 1", sql);
            Assert.Contains("@on_success_action = 3", sql);   // go to next step
            Assert.Contains("@on_fail_action = 2", sql);      // quit reporting failure
        }

        [Fact]
        public void Apply_sets_start_step_id_so_the_guard_cannot_be_skipped()
        {
            // Without this the job would start at what is now step 2 and bypass the guard
            // entirely — a silent no-op, the worst possible outcome for a safety feature.
            Assert.True(AgPrimaryGuardOpRenderer.TryRenderApplySql(P(), out var sql, out _));
            Assert.Contains("sp_update_job", sql);
            Assert.Contains("@start_step_id = 1", sql);
        }

        [Fact]
        public void Apply_carries_the_operators_guard_body_with_the_database_substituted()
        {
            Assert.True(AgPrimaryGuardOpRenderer.TryRenderApplySql(P(db: "SalesDb"), out var sql, out _));
            Assert.Contains("fn_hadr_is_primary_replica", sql);
            Assert.Contains("SalesDb", sql);
            Assert.Contains("RAISERROR", sql);
            Assert.Contains("16, -1", sql);
        }

        [Fact]
        public void Guard_body_is_stable_for_a_given_database()
        {
            var a = AgPrimaryGuardOpRenderer.RenderGuardBody("Db1");
            var b = AgPrimaryGuardOpRenderer.RenderGuardBody("Db1");
            Assert.Equal(a, b);
        }

        // ── Injection ────────────────────────────────────────────────────────

        [Theory]
        [InlineData("O'Brien")]
        [InlineData("job];DROP DATABASE x--")]
        [InlineData("job; EXEC sp_who")]
        [InlineData("")]
        [InlineData("   ")]
        public void Rejects_unsafe_job_names(string job)
        {
            Assert.False(AgPrimaryGuardOpRenderer.TryRenderApplySql(P(job: job), out _, out var err));
            Assert.False(string.IsNullOrWhiteSpace(err));
        }

        [Theory]
        [InlineData("db'; RAISERROR('x',16,-1)--")]
        [InlineData("db]")]
        [InlineData("")]
        public void Rejects_unsafe_database_names(string db)
        {
            Assert.False(AgPrimaryGuardOpRenderer.TryRenderApplySql(P(db: db), out _, out var err));
            Assert.False(string.IsNullOrWhiteSpace(err));
        }

        [Fact]
        public void Accepts_realistic_instance_style_names()
        {
            Assert.True(AgPrimaryGuardOpRenderer.TryRenderApplySql(
                P(job: "DBA - Nightly IndexOptimize (USER_DATABASES)", db: "Sales_DB.2024"), out _, out _));
        }

        // ── Inverse + verify ─────────────────────────────────────────────────

        [Fact]
        public void Inverse_removes_the_step_and_restores_the_original_start_step()
        {
            var sql = AgPrimaryGuardOpRenderer.RenderInverseSql("Nightly Reindex", originalStartStepId: 2);
            Assert.Contains("IF EXISTS", sql);
            Assert.Contains("sp_delete_jobstep", sql);
            Assert.Contains("@step_id = 1", sql);
            Assert.Contains("@start_step_id = 2", sql);
        }

        [Fact]
        public void Snapshot_and_verify_are_read_only()
        {
            foreach (var sql in new[]
            {
                AgPrimaryGuardOpRenderer.RenderSnapshotSql("Nightly Reindex"),
                AgPrimaryGuardOpRenderer.RenderVerifySql("Nightly Reindex")
            })
            {
                Assert.Equal(SqlClassification.Safe, SqlSafetyValidator.Classify(sql, null));
            }
        }

        // ── The gate ─────────────────────────────────────────────────────────

        [Fact]
        public void Rendered_apply_is_blocked_without_an_authorising_context()
        {
            Assert.True(AgPrimaryGuardOpRenderer.TryRenderApplySql(P(), out var sql, out _));
            Assert.Equal(SqlClassification.Blocked, SqlSafetyValidator.Classify(sql, null));
        }

        [Fact]
        public void Rendered_apply_is_remediation_only_under_a_registered_key()
        {
            Assert.True(AgPrimaryGuardOpRenderer.TryRenderApplySql(P(), out var sql, out _));

            Assert.Equal(SqlClassification.Remediation,
                SqlSafetyValidator.Classify(sql, new RemediationContext("AGPRIMARYGUARD")));
            Assert.Equal(SqlClassification.Blocked,
                SqlSafetyValidator.Classify(sql, new RemediationContext("NOTAREALKEY")));
            Assert.Equal(SqlClassification.Blocked,
                SqlSafetyValidator.Classify(sql, new RemediationContext("")));
        }

        /// <summary>
        /// Worth being explicit about, because it is easy to assume otherwise: Classify()
        /// promotes a blocked write under ANY registered key, not specifically this one. The
        /// validator's job is "was this authorised by SOME registered template", and the
        /// per-template wall lives one level up — RemediationRunner resolves a key to that
        /// template's own Operation and renders it through that op's own renderer, so guard
        /// T-SQL can never be executed under, say, MAXDOP (which renders sp_configure).
        /// This test pins the real boundary rather than a stronger one we do not have.
        /// </summary>
        [Fact]
        public void Classification_authority_is_key_registration_not_key_identity()
        {
            Assert.True(AgPrimaryGuardOpRenderer.TryRenderApplySql(P(), out var sql, out _));
            Assert.Equal(SqlClassification.Remediation,
                SqlSafetyValidator.Classify(sql, new RemediationContext("MAXDOP")));
        }

        [Fact]
        public void Representative_render_classifies_under_the_registered_key()
        {
            var sql = AgPrimaryGuardOpRenderer.RenderRepresentativeForClassification();
            Assert.Equal(SqlClassification.Remediation,
                SqlSafetyValidator.Classify(sql, new RemediationContext("AGPRIMARYGUARD")));
        }

        // ── Eligibility + guard detection ────────────────────────────────────

        private static AgentJobStep Step(int id, string name = "Work", int onSuccess = 3, int onFail = 2) =>
            new() { StepId = id, StepName = name, OnSuccessAction = onSuccess, OnFailAction = onFail, Command = "SELECT 1" };

        [Fact]
        public void Job_with_quit_or_next_flow_is_eligible()
        {
            var job = new AgentJobDefinition { Name = "J", Steps = { Step(1), Step(2, onSuccess: 1) } };
            Assert.True(job.EligibleForGuard);
            Assert.Null(job.IneligibleReason);
        }

        [Fact]
        public void Job_with_explicit_goto_step_target_is_refused()
        {
            // on_success_action = 4 is "go to step N" — inserting a step ahead of it would
            // leave that reference pointing at the wrong work.
            var job = new AgentJobDefinition { Name = "J", Steps = { Step(1, onSuccess: 4), Step(2) } };
            Assert.False(job.EligibleForGuard);
            Assert.NotNull(job.IneligibleReason);
        }

        [Fact]
        public void Stepless_job_is_refused()
        {
            var job = new AgentJobDefinition { Name = "J" };
            Assert.False(job.EligibleForGuard);
            Assert.Contains("no steps", job.IneligibleReason!);
        }

        [Fact]
        public void Detects_a_guard_we_wrote_by_name()
        {
            var job = new AgentJobDefinition
            {
                Name = "J",
                Steps = { Step(1, AgentJobDefinition.GuardStepName), Step(2) }
            };
            Assert.True(job.HasAgPrimaryGuard);
        }

        [Fact]
        public void Detects_a_hand_written_guard_by_body_so_we_never_duplicate_one()
        {
            var job = new AgentJobDefinition { Name = "J" };
            job.Steps.Add(new AgentJobStep
            {
                StepId = 1,
                StepName = "Check primary",
                Command = "IF sys.fn_hadr_is_primary_replica('X') = 1 PRINT 'OK';",
                OnSuccessAction = 3,
                OnFailAction = 2
            });
            Assert.True(job.HasAgPrimaryGuard);
        }

        [Fact]
        public void Unguarded_job_is_reported_as_unguarded()
        {
            var job = new AgentJobDefinition { Name = "J", Steps = { Step(1), Step(2) } };
            Assert.False(job.HasAgPrimaryGuard);
        }

        // ── Shipped template ─────────────────────────────────────────────────

        [Fact]
        public void Template_is_registered_reversible_and_priced_at_one_credit()
        {
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var t = store.TryGet("AGPRIMARYGUARD");

            Assert.NotNull(t);
            Assert.True(t!.Reversible);
            // Priced per JOB, not per instance: a 30-job estate should not cost 60 credits
            // to make failover-safe.
            Assert.Equal(1, RemediationCreditCost.For(t));
            Assert.Equal(RemediationOpKind.AgentJobPrimaryGuard, t.Operation!.OpKind);
        }

        /// <summary>
        /// Scalar reads the executor depends on. They must be Safe (they run outside the
        /// remediation context, before and after the gated apply) and single-valued.
        /// </summary>
        [Fact]
        public void Executor_scalar_reads_are_safe_and_single_valued()
        {
            foreach (var sql in new[]
            {
                AgPrimaryGuardOpRenderer.RenderGuardExistsSql("Nightly Reindex"),
                AgPrimaryGuardOpRenderer.RenderStartStepIdSql("Nightly Reindex")
            })
            {
                Assert.Equal(SqlClassification.Safe, SqlSafetyValidator.Classify(sql, null));
                Assert.StartsWith("SELECT", sql);
            }
        }
    }
}
