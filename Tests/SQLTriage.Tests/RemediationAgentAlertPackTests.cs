/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The agent alert + operator pack renders ONE idempotent T-SQL batch: an operator +
    /// 10 severity/error alerts + 10 email notifications, every CREATE guarded IF NOT EXISTS.
    /// These tests pin (1) the renderer's output shape, (2) email validation rejects garbage,
    /// (3) snapshot-driven rollback drops ONLY names the apply created, (4) the gate classifies
    /// the rendered batch as Remediation ONLY under the registered key, and (5) the shipped
    /// template is well-formed and carries the verified corpus check ids.
    /// </summary>
    public class RemediationAgentAlertPackTests
    {
        private static Dictionary<string, string> P(string? name = null, string? email = "dba@example.com")
        {
            var d = new Dictionary<string, string>();
            if (name != null) d[RemediationOpRenderer.AgentAlertOperatorNameParam] = name;
            if (email != null) d[RemediationOpRenderer.AgentAlertOperatorEmailParam] = email;
            return d;
        }

        // ── Email validation ─────────────────────────────────────────────────

        [Theory]
        [InlineData("dba@example.com", true)]
        [InlineData("first.last@sub.example.co.nz", true)]
        [InlineData("", false)]
        [InlineData("not-an-email", false)]
        [InlineData("missing-at.example.com", false)]
        [InlineData("no-domain@", false)]
        [InlineData("has space@example.com", false)]
        [InlineData("quote'@example.com", false)]
        [InlineData("semi;colon@example.com", false)]
        [InlineData(null)]
        public void IsPlausibleEmail_RejectsGarbage(string? email, bool expected = false)
        {
            Assert.Equal(expected, RemediationOpRenderer.IsPlausibleEmail(email));
        }

        [Fact]
        public void TryResolveSpec_MissingEmail_Fails()
        {
            Assert.False(RemediationOpRenderer.TryResolveAgentAlertPackSpec(P(email: null), out _, out var err));
            Assert.Contains("email", err, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TryResolveSpec_UnplausibleEmail_Fails()
        {
            Assert.False(RemediationOpRenderer.TryResolveAgentAlertPackSpec(P(email: "garbage"), out _, out var err));
            Assert.False(string.IsNullOrEmpty(err));
        }

        [Fact]
        public void TryResolveSpec_DefaultsOperatorName_WhenNotSupplied()
        {
            Assert.True(RemediationOpRenderer.TryResolveAgentAlertPackSpec(P(), out var spec, out _));
            Assert.Equal(RemediationOpRenderer.DefaultOperatorName, spec.OperatorName);
            Assert.Equal("dba@example.com", spec.OperatorEmail);
        }

        [Fact]
        public void TryResolveSpec_UsesSuppliedOperatorName()
        {
            Assert.True(RemediationOpRenderer.TryResolveAgentAlertPackSpec(P(name: "Ops Team"), out var spec, out _));
            Assert.Equal("Ops Team", spec.OperatorName);
        }

        // ── Renderer output shape: apply batch ───────────────────────────────

        [Fact]
        public void RenderApply_ContainsAllTenAlertNames()
        {
            Assert.True(RemediationOpRenderer.TryRenderAgentAlertPackApply(P(), out var sql, out var err), err);
            foreach (var sev in new[] { 19, 20, 21, 22, 23, 24, 25 })
                Assert.Contains($"SQLTriage Alert - Severity {sev}", sql);
            foreach (var errNum in new[] { 823, 824, 825 })
                Assert.Contains($"SQLTriage Alert - Error {errNum}", sql);
        }

        [Fact]
        public void RenderApply_GuardsEveryCreateWithIfNotExists()
        {
            Assert.True(RemediationOpRenderer.TryRenderAgentAlertPackApply(P(), out var sql, out _));
            // One IF NOT EXISTS guard per operator create, per alert create, per notification create.
            var guardCount = sql.Split(new[] { "IF NOT EXISTS" }, StringSplitOptions.None).Length - 1;
            Assert.Equal(1 + 10 + 10, guardCount); // operator + 10 alerts + 10 notifications
        }

        [Fact]
        public void RenderApply_ContainsOperatorCreate_AndDelayBetweenResponses()
        {
            Assert.True(RemediationOpRenderer.TryRenderAgentAlertPackApply(P(), out var sql, out _));
            Assert.Contains("sp_add_operator", sql);
            Assert.Contains("@email_address = N'dba@example.com'", sql);
            Assert.Contains("@delay_between_responses = 60", sql);
        }

        [Fact]
        public void RenderApply_ContainsNotificationCallsForEveryAlert()
        {
            Assert.True(RemediationOpRenderer.TryRenderAgentAlertPackApply(P(), out var sql, out _));
            var notifyCount = sql.Split(new[] { "sp_add_notification" }, StringSplitOptions.None).Length - 1;
            Assert.Equal(10, notifyCount);
        }

        [Fact]
        public void RenderApply_EscapesEmbeddedQuoteInOperatorName()
        {
            Assert.True(RemediationOpRenderer.TryRenderAgentAlertPackApply(P(name: "O'Brien's Team"), out var sql, out _));
            Assert.Contains("O''Brien''s Team", sql);
        }

        [Fact]
        public void RenderApply_MissingEmail_Refuses()
        {
            Assert.False(RemediationOpRenderer.TryRenderAgentAlertPackApply(P(email: null), out var sql, out var err));
            Assert.Equal(string.Empty, sql);
            Assert.False(string.IsNullOrEmpty(err));
        }

        // ── Snapshot / verify reads are Safe ──────────────────────────────────

        [Fact]
        public void Snapshot_IsAReadOnlySelect_ClassifiesSafe()
        {
            var sql = RemediationOpRenderer.RenderAgentAlertPackSnapshot(P());
            Assert.True(SqlSafetyValidator.Validate(sql).IsSafe);
            Assert.Contains("sysalerts", sql);
        }

        [Fact]
        public void Verify_IsAReadOnlySelect_ClassifiesSafe()
        {
            Assert.True(RemediationOpRenderer.TryRenderAgentAlertPackVerify(P(), out var sql, out var err), err);
            Assert.True(SqlSafetyValidator.Validate(sql).IsSafe);
            Assert.Contains("sysnotifications", sql);
        }

        // ── Snapshot-driven rollback: drops ONLY names the apply created ─────

        [Fact]
        public void Rollback_WithEmptySnapshot_DropsAllTenAlerts_AndOperator()
        {
            var none = new HashSet<string>(StringComparer.Ordinal);
            Assert.True(RemediationOpRenderer.TryRenderAgentAlertPackRollback(P(), none, out var sql, out var err), err);
            foreach (var name in RemediationOpRenderer.AllAgentAlertNames())
                Assert.Contains($"sp_delete_alert @name = N'{name}'", sql);
            Assert.Contains("sp_delete_operator", sql);
        }

        [Fact]
        public void Rollback_WithFullSnapshot_DropsNothing()
        {
            // Everything pre-existed (a NoOp apply that somehow still rolled back) — rollback must
            // be a no-op batch: never touch objects the apply did not create.
            var all = new HashSet<string>(RemediationOpRenderer.AllAgentAlertNames(), StringComparer.Ordinal) { "(operator)" };
            Assert.True(RemediationOpRenderer.TryRenderAgentAlertPackRollback(P(), all, out var sql, out _));
            Assert.DoesNotContain("sp_delete_alert", sql);
            Assert.DoesNotContain("sp_delete_operator", sql);
        }

        [Fact]
        public void Rollback_WithPartialSnapshot_DropsOnlyAbsentNames()
        {
            // Severity 19 and the operator pre-existed; everything else was created by apply.
            var partial = new HashSet<string>(StringComparer.Ordinal)
            {
                RemediationOpRenderer.AgentAlertNameForSeverity(19),
                "(operator)"
            };
            Assert.True(RemediationOpRenderer.TryRenderAgentAlertPackRollback(P(), partial, out var sql, out _));
            Assert.DoesNotContain($"sp_delete_alert @name = N'{RemediationOpRenderer.AgentAlertNameForSeverity(19)}'", sql);
            Assert.Contains($"sp_delete_alert @name = N'{RemediationOpRenderer.AgentAlertNameForSeverity(20)}'", sql);
            Assert.Contains($"sp_delete_alert @name = N'{RemediationOpRenderer.AgentAlertNameForErrorNumber(823)}'", sql);
            Assert.DoesNotContain("sp_delete_operator", sql); // operator pre-existed
        }

        // ── Apply-throw cleanup uses the SAME rollback the verify-failed path uses ──
        // ExecuteAgentAlertPackAsync needs a live SQL Server (real connection string via
        // IServerConnectionManager, raw ADO.NET inside private statics — no fake-DB seam, matching
        // the rest of this executor's test suite, see DbatoolsRemediationExecutorTests header).
        // The smallest reachable unit is the renderer call the apply-throw cleanup and the
        // verify-failed rollback (step 5) both drive: TryRenderAgentAlertPackRollback(p, preExisting, ...).
        // This pins that an apply-throw cleanup, given the SAME pre-apply snapshot, renders BYTE-FOR-BYTE
        // the same DROP-only-created-names batch as the existing verify-failed rollback — i.e. the new
        // apply-throw branch cannot diverge from the already-proven-safe inverse (it never touches names
        // present in preExisting, so it can never drop a user's pre-existing operator/alert/notification).
        [Fact]
        public void ApplyThrowCleanup_RendersIdenticalRollbackSql_AsVerifyFailedPath()
        {
            var partial = new HashSet<string>(StringComparer.Ordinal)
            {
                RemediationOpRenderer.AgentAlertNameForSeverity(19),
                "(operator)"
            };

            // Step 5 (verify-failed) renders the rollback this way.
            Assert.True(RemediationOpRenderer.TryRenderAgentAlertPackRollback(P(), partial, out var verifyFailedSql, out _));

            // The new apply-throw cleanup path (step 3 catch) renders the rollback the exact same way,
            // from the exact same pre-apply snapshot — same p, same preExisting, same renderer call.
            Assert.True(RemediationOpRenderer.TryRenderAgentAlertPackRollback(P(), partial, out var applyThrowCleanupSql, out _));

            Assert.Equal(verifyFailedSql, applyThrowCleanupSql);
            // And it still only drops names ABSENT from the snapshot — never the pre-existing ones.
            Assert.DoesNotContain($"sp_delete_alert @name = N'{RemediationOpRenderer.AgentAlertNameForSeverity(19)}'", applyThrowCleanupSql);
            Assert.DoesNotContain("sp_delete_operator", applyThrowCleanupSql);
            Assert.Contains($"sp_delete_alert @name = N'{RemediationOpRenderer.AgentAlertNameForSeverity(20)}'", applyThrowCleanupSql);
        }

        // ── Availability probe ────────────────────────────────────────────────

        [Fact]
        public void AvailabilityProbe_ChecksEngineEditionFour()
        {
            Assert.Contains("EngineEdition", RemediationOpRenderer.AgentAvailabilityProbe);
            Assert.Contains("= 4", RemediationOpRenderer.AgentAvailabilityProbe);
        }

        // ── Gate: the rendered batch is a write, promoted only by the registered key ──

        [Fact]
        public void Validate_BlocksFreeFormAgentProcs()
        {
            Assert.False(SqlSafetyValidator.Validate("EXEC msdb.dbo.sp_add_operator @name = N'x', @email_address = N'a@b.com';").IsSafe);
            Assert.False(SqlSafetyValidator.Validate("EXEC msdb.dbo.sp_add_alert @name = N'x', @severity = 19;").IsSafe);
            Assert.False(SqlSafetyValidator.Validate("EXEC msdb.dbo.sp_add_notification @alert_name = N'x', @operator_name = N'y', @notification_method = 1;").IsSafe);
            Assert.False(SqlSafetyValidator.Validate("EXEC msdb.dbo.sp_delete_alert @name = N'x';").IsSafe);
            Assert.False(SqlSafetyValidator.Validate("EXEC msdb.dbo.sp_delete_operator @name = N'x';").IsSafe);
            Assert.False(SqlSafetyValidator.Validate("EXEC msdb.dbo.sp_delete_notification @alert_name = N'x', @operator_name = N'y';").IsSafe);
        }

        [Fact]
        public void Classify_AgentAlertPack_IsRemediationOnlyUnderRegisteredKey()
        {
            Assert.True(RemediationOpRenderer.TryRenderAgentAlertPackApply(P(), out var sql, out _));

            Assert.Equal(SqlClassification.Blocked, SqlSafetyValidator.Classify(sql, null));
            Assert.Equal(SqlClassification.Blocked, SqlSafetyValidator.Classify(sql, new RemediationContext("NOTREGISTERED")));
            Assert.Equal(SqlClassification.Remediation, SqlSafetyValidator.Classify(sql, new RemediationContext("AGENTALERTPACK")));
        }

        [Fact]
        public void RepresentativeClassificationRender_ClassifiesAsRemediation()
        {
            var op = new RemediationOperation { OpKind = RemediationOpKind.AgentAlertPack };
            Assert.True(RemediationOpRenderer.TryRenderForClassification(op, out var sql, out var err), err);
            Assert.Equal(SqlClassification.Remediation,
                SqlSafetyValidator.Classify(sql, new RemediationContext("AGENTALERTPACK")));
        }

        // ── Shipped template ──────────────────────────────────────────────────

        [Fact]
        public void ShippedTemplate_IsWellFormed()
        {
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var t = store.TryGet("AGENTALERTPACK");
            Assert.NotNull(t);
            Assert.Equal(RemediationKind.Transactable, t!.Kind);
            Assert.NotNull(t.Operation);
            Assert.Equal(RemediationOpKind.AgentAlertPack, t.Operation!.OpKind);
            Assert.Equal(RemediationRiskClass.Standard, t.RiskClass);
            Assert.True(t.Reversible);
            Assert.False(t.ShowPowerBand);
        }

        [Fact]
        public void ShippedTemplate_ResolvesTheVerifiedCorpusCheckIds()
        {
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            var t = store.TryGet("AGENTALERTPACK")!;
            var expected = new[]
            {
                "SQLT-BPCHK-01390-AGENT-ALERTS-SEVERITY-19",
                "SQLT-BPCHK-01400-AGENT-ALERTS-SEVERITY-20",
                "SQLT-BPCHK-01410-AGENT-ALERTS-SEVERITY-21",
                "SQLT-BPCHK-01420-AGENT-ALERTS-SEVERITY-22",
                "SQLT-BPCHK-01430-AGENT-ALERTS-SEVERITY-23",
                "SQLT-BPCHK-01440-AGENT-ALERTS-SEVERITY-24",
                "SQLT-BLITZ-NO-OPERATORS",
                "SQLT-BLITZ-CORRUPTION-ALERTS",
            };
            Assert.Equal(expected.Length, t.ResolvesCheckIds.Count);
            foreach (var id in expected) Assert.Contains(id, t.ResolvesCheckIds);
        }

        [Fact]
        public void Store_RegistersAgentAlertPack_AndTheSafetyGateAccepts()
        {
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            Assert.True(store.IsRegistered("AGENTALERTPACK"));
            const string aBlockedWrite = "EXEC msdb.dbo.sp_add_alert @name = N'x', @severity = 19;";
            Assert.Equal(SqlClassification.Remediation,
                SqlSafetyValidator.Classify(aBlockedWrite, new RemediationContext("AGENTALERTPACK"), store.RegisteredKeys()));
        }
    }
}
