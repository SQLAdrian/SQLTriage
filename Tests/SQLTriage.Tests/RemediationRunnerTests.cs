/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Build step 4: RemediationRunner gate enforcement. Drives all 5 gates with a
    /// fake executor (no live SQL / dbatools). Pins gate ORDER, the read-only wall
    /// (no executor call when a gate refuses), credit reserve/refund semantics,
    /// the audit-writability probe, permission back-off, distinct terminal states,
    /// and the orphan-key contract test.
    /// </summary>
    public class RemediationRunnerTests : System.IDisposable
    {
        private readonly string _auditDir;

        public RemediationRunnerTests()
        {
            _auditDir = Path.Combine(Path.GetTempPath(), "rem-runner-tests-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_auditDir);
        }

        public void Dispose()
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(_auditDir, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                Directory.Delete(_auditDir, recursive: true);
            }
            catch { }
        }

        // ── Test doubles ────────────────────────────────────────────────────

        private sealed class GrantedCapability : IRemediationCapability { public bool IsGranted => true; }

        private sealed class FakeExecutor : IRemediationExecutor
        {
            public int ExecuteCalls;
            public int PreviewCalls;
            public bool AuditWritable = true;
            public RemediationExecution Result = new() { Outcome = RemediationOutcome.AppliedVerified };

            public Task<RemediationPreview> PreviewAsync(RemediationRequest r, CancellationToken ct = default)
            {
                PreviewCalls++;
                return Task.FromResult(new RemediationPreview { Succeeded = true, WhatIfText = "would set MAXDOP" });
            }
            public Task<RemediationExecution> ExecuteAsync(RemediationRequest r, CancellationToken ct = default)
            {
                ExecuteCalls++;
                return Task.FromResult(Result);
            }
            public bool CanWriteAudit() => AuditWritable;
        }

        private RemediationTemplateStore Templates() => new(NullLogger<RemediationTemplateStore>.Instance);
        private AuditLogService Audit() => new(_auditDir, startFlushTimer: false);

        private RemediationRunner NewRunner(
            FakeExecutor executor,
            IRemediationCapability? capability = null,
            IRemediationCreditLedger? credits = null)
        {
            return new RemediationRunner(
                Templates(),
                capability ?? new GrantedCapability(),
                credits ?? Granted(10),
                executor,
                Audit(),
                NullLogger<RemediationRunner>.Instance);
        }

        // Per-server ledger seeded with n credits for every server (incl. the test "srv1").
        private static InMemoryRemediationCreditLedger Granted(int n)
            => new(initialCreditsPerServer: n);

        // ── Gate 1: TEMPLATE ────────────────────────────────────────────────

        [Fact]
        public async Task Apply_UnregisteredTemplate_RefusedAtGate1_ExecutorNeverCalled()
        {
            var exec = new FakeExecutor();
            var r = await NewRunner(exec).ApplyAsync("NOPE", "srv1", approved: true, "adrian");
            Assert.True(r.IsRefused);
            Assert.Equal(RemediationRefusal.NotARegisteredTemplate, r.Refusal);
            Assert.Equal(0, exec.ExecuteCalls);
        }

        // ── Gate 2: CAPABILITY ──────────────────────────────────────────────

        [Fact]
        public async Task Apply_CapabilityDenied_RefusedAtGate2_ExecutorNeverCalled()
        {
            var exec = new FakeExecutor();
            var runner = NewRunner(exec, capability: new DeniedRemediationCapability());
            var r = await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian");
            Assert.True(r.IsRefused);
            Assert.Equal(RemediationRefusal.CapabilityDenied, r.Refusal);
            Assert.Equal(0, exec.ExecuteCalls);
        }

        // ── Gate 4: APPROVAL (checked before credits are touched) ────────────

        [Fact]
        public async Task Apply_NotApproved_RefusedAtGate4_NoCreditsSpent_ExecutorNeverCalled()
        {
            var exec = new FakeExecutor();
            var credits = Granted(5);
            var runner = NewRunner(exec, credits: credits);
            var r = await runner.ApplyAsync("MAXDOP", "srv1", approved: false, "adrian");
            Assert.True(r.IsRefused);
            Assert.Equal(RemediationRefusal.NotApproved, r.Refusal);
            Assert.Equal(0, exec.ExecuteCalls);
            Assert.Equal(5, credits.AvailableFor("srv1")); // untouched
        }

        // ── Gate 3: CREDIT ──────────────────────────────────────────────────

        [Fact]
        public async Task Apply_InsufficientCredits_RefusedAtGate3_ExecutorNeverCalled()
        {
            var exec = new FakeExecutor();
            var runner = NewRunner(exec, credits: Granted(0));
            var r = await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian");
            Assert.True(r.IsRefused);
            Assert.Equal(RemediationRefusal.InsufficientCredits, r.Refusal);
            Assert.Equal(0, exec.ExecuteCalls);
        }

        // ── Gate 5: AUDIT-WRITABILITY PROBE ─────────────────────────────────

        [Fact]
        public async Task Apply_AuditNotWritable_Refused_CreditsRefunded_ExecutorNeverCalled()
        {
            var exec = new FakeExecutor { AuditWritable = false };
            var credits = Granted(3);
            var runner = NewRunner(exec, credits: credits);
            var r = await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian");
            Assert.True(r.IsRefused);
            Assert.Equal(RemediationRefusal.AuditNotWritable, r.Refusal);
            Assert.Equal(0, exec.ExecuteCalls);
            Assert.Equal(3, credits.AvailableFor("srv1")); // reserved then refunded
        }

        // ── Happy path: all gates pass, change applied + verified ───────────

        [Fact]
        public async Task Apply_AllGatesPass_Executes_CommitsCredit_VerifiedOutcome()
        {
            var exec = new FakeExecutor { Result = new() { Outcome = RemediationOutcome.AppliedVerified } };
            var credits = Granted(2);
            var runner = NewRunner(exec, credits: credits);
            var r = await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian");
            Assert.False(r.IsRefused);
            Assert.Equal(RemediationOutcome.AppliedVerified, r.Outcome);
            Assert.Equal(1, exec.ExecuteCalls);
            Assert.Equal(1, credits.AvailableFor("srv1")); // 2 - 1 committed
        }

        // ── Distinct terminal states: NoOp/CouldNotRun refund the credit ────

        [Theory]
        [InlineData(RemediationOutcome.NoOp)]
        [InlineData(RemediationOutcome.CouldNotRun)]
        public async Task Apply_NothingChanged_RefundsCredit(RemediationOutcome outcome)
        {
            var exec = new FakeExecutor { Result = new() { Outcome = outcome } };
            var credits = Granted(2);
            var runner = NewRunner(exec, credits: credits);
            var r = await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian");
            Assert.Equal(outcome, r.Outcome);
            Assert.Equal(2, credits.AvailableFor("srv1")); // nothing changed → credit returned
        }

        [Fact]
        public async Task Apply_AppliedButVerifyFailed_NotRolledBack_StillCommitsCredit()
        {
            // Verify failed and the change was NOT rolled back → server is half-applied → charge.
            var exec = new FakeExecutor { Result = new() { Outcome = RemediationOutcome.AppliedVerifyFailed } };
            var credits = Granted(2);
            var runner = NewRunner(exec, credits: credits);
            var r = await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian");
            Assert.Equal(RemediationOutcome.AppliedVerifyFailed, r.Outcome);
            Assert.False(r.RolledBack);
            Assert.Equal(1, credits.AvailableFor("srv1")); // change stuck → credit spent
        }

        [Fact]
        public async Task Apply_VerifyFailedButCleanlyRolledBack_RefundsCredit()
        {
            // The change was undone (snapshot rollback restored the old value) → nothing consumed
            // → refund (matches the IRemediationCreditLedger "refund if ... rolled back" contract).
            var exec = new FakeExecutor { Result = new() { Outcome = RemediationOutcome.AppliedVerifyFailed, RolledBack = true, RollbackSucceeded = true } };
            var credits = Granted(2);
            var runner = NewRunner(exec, credits: credits);
            var r = await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian");
            Assert.Equal(RemediationOutcome.AppliedVerifyFailed, r.Outcome);
            Assert.True(r.RolledBack);
            Assert.Equal(2, credits.AvailableFor("srv1")); // cleanly reverted → credit returned
        }

        [Fact]
        public async Task Apply_VerifyFailedAndRollbackFailed_CommitsCredit()
        {
            // Rollback itself failed → indeterminate server state → the credit is consumed.
            var exec = new FakeExecutor { Result = new() { Outcome = RemediationOutcome.AppliedVerifyFailed, RolledBack = true, RollbackSucceeded = false } };
            var credits = Granted(2);
            var runner = NewRunner(exec, credits: credits);
            var r = await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian");
            Assert.Equal(1, credits.AvailableFor("srv1")); // indeterminate → charged
        }

        // ── Pre-change value is surfaced for the session-scoped undo ────────

        [Fact]
        public async Task Apply_VerifiedApply_SurfacesPreChangeValue_ForUndo()
        {
            var exec = new FakeExecutor { Result = new() { Outcome = RemediationOutcome.AppliedVerified, PreChangeValue = 7 } };
            var r = await NewRunner(exec).ApplyAsync("MAXDOP", "srv1", approved: true, "adrian");
            Assert.Equal(RemediationOutcome.AppliedVerified, r.Outcome);
            Assert.Equal(7, r.PreChangeValue); // the value an "Undo" re-applies
        }

        // ── Permission back-off ─────────────────────────────────────────────

        [Fact]
        public async Task Apply_PermissionDenied_ParksServer_SecondAttemptShortCircuits()
        {
            var exec = new FakeExecutor
            {
                Result = new() { Outcome = RemediationOutcome.CouldNotRun, IsPermissionDenied = true }
            };
            var runner = NewRunner(exec, credits: Granted(5));

            var first = await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian");
            Assert.Equal(RemediationOutcome.CouldNotRun, first.Outcome);
            Assert.True(runner.IsServerParked("srv1"));

            var second = await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian");
            Assert.Equal(RemediationOutcome.CouldNotRun, second.Outcome);
            Assert.Equal(1, exec.ExecuteCalls); // second never reached the executor
        }

        // ── The read-only wall holds: a refused apply writes no Applied entry ─

        [Fact]
        public async Task Apply_RefusedAtCapability_LedgerHasNoAppliedEntry()
        {
            var exec = new FakeExecutor();
            var audit = Audit();
            var runner = new RemediationRunner(Templates(), new DeniedRemediationCapability(),
                Granted(5), exec, audit, NullLogger<RemediationRunner>.Instance);

            await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian");
            audit.Flush();

            var file = Directory.GetFiles(_auditDir, "audit-*.jsonl").FirstOrDefault();
            if (file != null)
            {
                var lines = File.ReadAllLines(file);
                Assert.DoesNotContain(lines, l => l.Contains("RemediationApplied"));
            }
        }

        // ── Propose renders a preview only after gates 1–2 ──────────────────

        [Fact]
        public async Task Propose_CapabilityDenied_NoPreview()
        {
            var exec = new FakeExecutor();
            var runner = NewRunner(exec, capability: new DeniedRemediationCapability());
            var p = await runner.ProposeAsync("MAXDOP", "srv1");
            Assert.True(p.IsRefused);
            Assert.Equal(0, exec.PreviewCalls);
        }

        [Fact]
        public async Task Propose_AllGatesPass_RendersPreview()
        {
            var exec = new FakeExecutor();
            var p = await NewRunner(exec).ProposeAsync("MAXDOP", "srv1");
            Assert.False(p.IsRefused);
            Assert.NotNull(p.Preview);
            Assert.Equal(1, exec.PreviewCalls);
        }

        // ── CONTRACT TEST: no orphan keys (shipped-half-built guard) ─────────

        [Fact]
        public async Task ContractTest_EveryRegisteredKey_IsApplyable_NoOrphans()
        {
            // Every registered template key must reach the executor through the
            // runner when all gates pass — an orphan key (registered but not
            // applyable) is a silent no-op and must fail the build.
            var store = Templates();
            foreach (var key in store.RegisteredKeys())
            {
                var exec = new FakeExecutor();
                var runner = new RemediationRunner(store, new GrantedCapability(),
                    Granted(5), exec, Audit(), NullLogger<RemediationRunner>.Instance);

                var r = await runner.ApplyAsync(key, "srv1", approved: true, "adrian");
                Assert.False(r.IsRefused, $"Registered key '{key}' was refused by the runner — orphan key.");
                Assert.Equal(1, exec.ExecuteCalls);
            }
        }
    }
}
