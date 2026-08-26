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

        // ── S1: deferred-verify scheduling ──────────────────────────────────

        [Fact]
        public async Task Apply_DeferredVerifyTemplate_ScheduleLane_LedgersVerifyScheduled()
        {
            var exec = new FakeExecutor(); // Outcome = AppliedVerified
            var runner = NewRunner(exec);
            var p = new Dictionary<string, string>
            { [MaintenanceSolutionOpRenderer.ActionParam] = "CheckDbWeekly" };

            var r = await runner.ApplyAsync("INSTALLMAINTENANCESOLUTION", "srv1", approved: true, "adrian", p);
            Assert.False(r.IsRefused);
            Assert.Equal(RemediationOutcome.AppliedVerified, r.Outcome);

            var scheduled = Audit().GetEntries(
                System.DateTime.UtcNow.AddDays(-1), System.DateTime.UtcNow.AddDays(1),
                AuditEventType.RemediationVerifyScheduled);
            var entry = Assert.Single(scheduled, e =>
                e.Details["TemplateKey"] == "INSTALLMAINTENANCESOLUTION" && e.Details["ServerName"] == "srv1");
            Assert.Contains("CheckDbWeekly", entry.Details["ParametersJson"]);
            Assert.True(System.DateTime.TryParse(entry.Details["VerifyByUtc"], null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var verifyBy));
            Assert.True(verifyBy > System.DateTime.UtcNow.AddDays(7)); // 8-day window declared
        }

        [Fact]
        public async Task Apply_DeferredVerifyTemplate_InstallOnly_SchedulesNothing()
        {
            // Install-only renders no deferred plan (nothing scheduled to run), so a verified
            // install must NOT create a pending verification.
            var exec = new FakeExecutor();
            var runner = NewRunner(exec);
            var r = await runner.ApplyAsync("INSTALLMAINTENANCESOLUTION", "srv1", approved: true, "adrian");
            Assert.False(r.IsRefused);

            var scheduled = Audit().GetEntries(
                System.DateTime.UtcNow.AddDays(-1), System.DateTime.UtcNow.AddDays(1),
                AuditEventType.RemediationVerifyScheduled);
            Assert.DoesNotContain(scheduled, e => e.Details["ServerName"] == "srv1");
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
            var exec = new FakeExecutor { Result = new() { Outcome = RemediationOutcome.AppliedVerifyFailed, RollbackState = RemediationRollbackState.Confirmed } };
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
            var exec = new FakeExecutor { Result = new() { Outcome = RemediationOutcome.AppliedVerifyFailed, RollbackState = RemediationRollbackState.Failed } };
            var credits = Granted(2);
            var runner = NewRunner(exec, credits: credits);
            var r = await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian");
            Assert.Equal(1, credits.AvailableFor("srv1")); // indeterminate → charged
        }

        [Fact]
        public async Task Apply_VerifyFailedAndRollbackUnconfirmed_KeepsTheCharge_AndLedgersTheThirdState()
        {
            // remediation-r2-03: the inverse action completed, the confirming read did not. The
            // ledger must NOT record a success for a post-state nobody observed.
            //
            // RULING 2 (DECISIONS 2026-08-25 17:33) then changed the CREDIT decision this test used
            // to pin the other way. An unconfirmed rollback used to refund, on the test "did the
            // inverse action complete". It keeps the charge now: nobody read the server, so a
            // refund would assert the change is gone. Refunds are confirmed-only.
            var exec = new FakeExecutor
            {
                Result = new()
                {
                    Outcome = RemediationOutcome.AppliedVerifyFailed,
                    RollbackState = RemediationRollbackState.Unconfirmed,
                    RollbackError = "confirming read failed",
                }
            };
            var credits = Granted(2);
            var audit = Audit();
            var runner = new RemediationRunner(
                Templates(), new GrantedCapability(), credits, exec, audit,
                NullLogger<RemediationRunner>.Instance);

            var r = await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian");

            Assert.True(r.RolledBack);
            Assert.Equal(RemediationRollbackState.Unconfirmed, r.RollbackState);
            Assert.Equal(1, credits.AvailableFor("srv1")); // ruling 2: unconfirmed keeps the charge

            audit.Flush();
            var rolledBack = ReadAuditEntries()
                .Single(e => e.EventType == AuditEventType.RemediationRolledBack);
            Assert.Equal("Unconfirmed", rolledBack.Details["RollbackState"]);
            Assert.Equal("False", rolledBack.Details["Success"]);
            Assert.Equal(AuditSeverity.Warning, rolledBack.Severity);
        }

        [Fact]
        public async Task Apply_RollbackCouldNotBeAttempted_LedgersNoRollbackEvent_AndRecordsWhy()
        {
            // The lane's own regression, caught at gate: NotAvailable means the inverse action was
            // never executed (no invertible option_sql, or the restore statement would not render).
            // Reporting that as Failed put "Rollback of remediation 'X' on 'Y' FAILED" into the
            // HMAC-chained ledger, at Error severity, for a statement nobody sent. No event is the
            // honest record. The REASON is still a compliance fact, so it rides the applied entry.
            var exec = new FakeExecutor
            {
                Result = new()
                {
                    Outcome = RemediationOutcome.CouldNotRun,
                    Error = "db_set_option apply failed on 'tempdb': Option 'RECOVERY' cannot be set in database 'tempdb'.",
                    RollbackState = RemediationRollbackState.NotAvailable,
                    RollbackError = "No rollback was attempted. This fix's option_sql is not a simple ON/OFF toggle, so there is no inverse to run.",
                }
            };
            var audit = Audit();
            var runner = new RemediationRunner(
                Templates(), new GrantedCapability(), Granted(2), exec, audit,
                NullLogger<RemediationRunner>.Instance);

            var r = await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian");

            Assert.False(r.RolledBack);
            Assert.Equal(RemediationRollbackState.NotAvailable, r.RollbackState);

            audit.Flush();
            var entries = ReadAuditEntries();
            Assert.DoesNotContain(entries, e => e.EventType == AuditEventType.RemediationRolledBack);

            var applied = entries.Single(e => e.EventType == AuditEventType.RemediationApplied);
            Assert.Contains("apply failed on 'tempdb'", applied.Details["Error"]);
            Assert.Contains("No rollback was attempted", applied.Details["Error"]);
        }

        [Fact]
        public async Task Apply_RollbackThatReallyRanAndFailed_StillLedgersTheFailure()
        {
            // The counter-test, so the fix above cannot be "stop writing rollback events". An
            // inverse action that RAN and did not restore the server is a real failure and keeps
            // its Error-severity ledger entry.
            var exec = new FakeExecutor
            {
                Result = new()
                {
                    Outcome = RemediationOutcome.AppliedVerifyFailed,
                    RollbackState = RemediationRollbackState.Failed,
                    RollbackError = "Rollback DROP INDEX did not remove the index.",
                }
            };
            var audit = Audit();
            var runner = new RemediationRunner(
                Templates(), new GrantedCapability(), Granted(2), exec, audit,
                NullLogger<RemediationRunner>.Instance);

            var r = await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian");

            Assert.True(r.RolledBack);
            audit.Flush();
            var rolledBack = ReadAuditEntries()
                .Single(e => e.EventType == AuditEventType.RemediationRolledBack);
            Assert.Equal("Failed", rolledBack.Details["RollbackState"]);
            Assert.Equal(AuditSeverity.Error, rolledBack.Severity);
        }

        // ── VOICE-1 exception path: a throw refunds, and the credit line names the real count ──

        // An executor that throws mid-apply, to reach the runner's catch block. CanWriteAudit is
        // true so the gate-5 pre-flight passes and execution is actually attempted.
        private sealed class ThrowingExecutor : IRemediationExecutor
        {
            public Task<RemediationPreview> PreviewAsync(RemediationRequest r, CancellationToken ct = default) =>
                Task.FromResult(new RemediationPreview { Succeeded = true, WhatIfText = "would set MAXDOP" });
            public Task<RemediationExecution> ExecuteAsync(RemediationRequest r, CancellationToken ct = default) =>
                throw new System.InvalidOperationException("dbatools blew up mid-apply");
            public bool CanWriteAudit() => true;
        }

        [Theory]
        [InlineData("MAXDOP")]           // Standard + reversible -> 1 credit (singular grammar)
        [InlineData("ADDMISSINGINDEX")]  // Sensitive + reversible -> 2 credits (plural grammar)
        public async Task Apply_ExecutorThrows_Refunds_AndTheCreditLineNamesTheRealCount(string key)
        {
            // VOICE-1 exception-path defect (gate FAIL, DECISIONS 2026-08-25): the catch block
            // refunded the reservation but returned a bare Applied(...), so CreditsCharged defaulted
            // to 0 and the operator read "0 change credits were refunded." while the ledger refunded
            // one to five. The result must carry the REAL reserved count, read from the same cost
            // source the runner charges from — never hard-coded here.
            var cost = RemediationCreditCost.For(Templates().TryGet(key));
            var credits = Granted(5);
            var runner = new RemediationRunner(
                Templates(), new GrantedCapability(), credits, new ThrowingExecutor(), Audit(),
                NullLogger<RemediationRunner>.Instance);

            var r = await runner.ApplyAsync(key, "srv1", approved: true, "adrian");

            Assert.Equal(RemediationOutcome.CouldNotRun, r.Outcome);
            Assert.Equal(cost, r.CreditsCharged);          // the fix: carries the reservation, not 0
            Assert.Equal(5, credits.AvailableFor("srv1")); // reserved then refunded on the throw

            // The operator's money line, from the same predicate the runner charges from, names the
            // real refunded count with grammar to match, and can never read "0" over a real refund.
            var line = RemediationCreditOutcome.DescribeCharge(r.Outcome!.Value, r.RollbackState, r.CreditsCharged);
            var noun = RemediationCreditCost.Noun(cost);
            var verb = cost == 1 ? "was" : "were";
            Assert.Equal($"{cost} change {noun} {verb} refunded.", line);
            Assert.DoesNotContain("0 change", line, System.StringComparison.Ordinal);
        }

        [Fact]
        public async Task Apply_ParkedServer_ReportsNoChargeRatherThanAVacuousRefund()
        {
            // VOICE-1 parked-path ruling (DECISIONS 2026-08-25): the parked path returns an Applied
            // CouldNotRun without ever reserving a credit, so its CreditsCharged is 0. The credit
            // line must say "No change credit was charged." rather than "0 change credits were
            // refunded." — a refund claim about a refund that never happened. First park the server.
            var exec = new FakeExecutor
            {
                Result = new() { Outcome = RemediationOutcome.CouldNotRun, IsPermissionDenied = true }
            };
            var runner = NewRunner(exec, credits: Granted(5));
            await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian");
            Assert.True(runner.IsServerParked("srv1"));

            var parked = await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian");
            Assert.Equal(RemediationOutcome.CouldNotRun, parked.Outcome);
            Assert.Equal(0, parked.CreditsCharged); // never reserved

            var line = RemediationCreditOutcome.DescribeCharge(
                parked.Outcome!.Value, parked.RollbackState, parked.CreditsCharged);
            Assert.Equal("No change credit was charged.", line);
            Assert.DoesNotContain("refunded", line, System.StringComparison.Ordinal);
        }

        private List<AuditLogEntry> ReadAuditEntries()
        {
            var file = Directory.GetFiles(_auditDir, "audit-*.jsonl")
                .OrderByDescending(f => f, System.StringComparer.OrdinalIgnoreCase).First();
            return File.ReadAllLines(file)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => System.Text.Json.JsonSerializer.Deserialize<AuditLogEntry>(l)!)
                .ToList();
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
