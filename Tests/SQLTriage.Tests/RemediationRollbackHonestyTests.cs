/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SQLTriage.Data;
using SQLTriage.Data.Services.Remediation;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// remediation-r2-03 + remediation-r2-08 (honesty hunt, 2026-08-25): five rollback sites in
    /// DbatoolsRemediationExecutor reported <c>RollbackSucceeded = true</c> when the CONFIRMING
    /// READ threw, inferring success from "the inverse action did not throw". The runner wrote that
    /// boolean straight into the HMAC ledger and the audit page rendered it as an unqualified
    /// "rolled back", Success=True, Error="".
    ///
    /// <para>r2-08 is why it stayed green: every test that certified the rollback contract hand-set
    /// the flag on a fake executor, so the RULE was tested and the PRODUCER of its input was not.
    /// These tests drive the producer. <see cref="DbatoolsRemediationExecutor.ConfirmRollbackAsync"/>
    /// is the single decision point all five sites now call, and the first test below feeds it a
    /// confirming read that throws — the exact shape the hunt could not arrange against a live
    /// instance, because it needs a connection to die between a successful inverse and its
    /// verification.</para>
    /// </summary>
    public class RemediationRollbackHonestyTests
    {
        // ── The producer: a confirm read that throws is NOT a success ────────

        [Fact]
        public async Task ConfirmRollback_WhenTheConfirmingReadThrows_IsUnconfirmed_NeverConfirmed()
        {
            var (state, error) = await DbatoolsRemediationExecutor.ConfirmRollbackAsync(
                _ => throw new InvalidOperationException("BeginExecuteReader requires an open and available Connection"),
                "mismatch text that must not be used here",
                CancellationToken.None);

            Assert.Equal(RemediationRollbackState.Unconfirmed, state);
            Assert.NotEqual(RemediationRollbackState.Confirmed, state);
            Assert.NotNull(error);
            Assert.Contains("unknown", error, StringComparison.OrdinalIgnoreCase);
            // The mismatch message describes an OBSERVED wrong state. Nothing was observed here.
            Assert.DoesNotContain("mismatch text", error);
        }

        [Fact]
        public async Task ConfirmRollback_WhenTheConfirmingReadThrowsASqlException_IsUnconfirmed()
        {
            // The realistic shape: the rollback statement committed, then the connection died and
            // the verifying SELECT raised. ADO.NET can surface either exception type; both are
            // "we did not see the server", never "the server is fine".
            var (state, _) = await DbatoolsRemediationExecutor.ConfirmRollbackAsync(
                _ => Task.FromException<bool>(MakeSqlLikeFailure()),
                "mismatch", CancellationToken.None);

            Assert.Equal(RemediationRollbackState.Unconfirmed, state);
        }

        [Fact]
        public async Task ConfirmRollback_WhenTheReadObservesTheRestoredState_IsConfirmed()
        {
            var (state, error) = await DbatoolsRemediationExecutor.ConfirmRollbackAsync(
                _ => Task.FromResult(true), "mismatch", CancellationToken.None);

            Assert.Equal(RemediationRollbackState.Confirmed, state);
            Assert.Null(error);
        }

        [Fact]
        public async Task ConfirmRollback_WhenTheReadObservesTheWrongState_IsFailed_WithTheMismatchReason()
        {
            var (state, error) = await DbatoolsRemediationExecutor.ConfirmRollbackAsync(
                _ => Task.FromResult(false), "Rollback DROP INDEX did not remove the index.", CancellationToken.None);

            Assert.Equal(RemediationRollbackState.Failed, state);
            Assert.Equal("Rollback DROP INDEX did not remove the index.", error);
        }

        [Fact]
        public async Task ConfirmRollback_Cancellation_Propagates_AndIsNotDowngradedToUnconfirmed()
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                DbatoolsRemediationExecutor.ConfirmRollbackAsync(
                    _ => throw new OperationCanceledException(), "mismatch", CancellationToken.None));
        }

        // ── The contract: "succeeded" is derived, so it cannot be asserted ───

        [Fact]
        public void AnUnconfirmedRollback_IsAttempted_ButNeverReadsAsSucceeded()
        {
            var exec = new RemediationExecution
            {
                Outcome = RemediationOutcome.AppliedVerifyFailed,
                RollbackState = RemediationRollbackState.Unconfirmed,
            };

            Assert.True(exec.RolledBack);          // the inverse ran, so the ledger records one
            Assert.False(exec.RollbackSucceeded);  // and it is not a success
        }

        // Enumerated from the ENUM, not from a hand-written list. A hand-written list is a
        // hard-coded census: it went stale the moment a fifth state was added, and the census it
        // was pinning silently stopped covering the new member.
        private static RemediationRollbackState[] AllStates() =>
            Enum.GetValues<RemediationRollbackState>();

        [Fact]
        public void RollbackSucceeded_IsTrueForExactlyOneState()
        {
            var succeeding = AllStates()
                .Where(s => new RemediationExecution { RollbackState = s }.RollbackSucceeded)
                .ToList();

            Assert.Equal(new[] { RemediationRollbackState.Confirmed }, succeeding);
        }

        [Fact]
        public void RolledBack_IsTrueForExactlyTheStatesWhereAnInverseActuallyRan()
        {
            // RolledBack is what makes the runner write a RemediationRolledBack entry into the
            // HMAC-chained ledger. It must therefore mean "an inverse action ran", and nothing
            // else. NotAvailable ran nothing, so it is not a rollback of any kind.
            var attempted = AllStates()
                .Where(s => new RemediationExecution { RollbackState = s }.RolledBack)
                .ToList();

            Assert.Equal(
                new[]
                {
                    RemediationRollbackState.Confirmed,
                    RemediationRollbackState.Unconfirmed,
                    RemediationRollbackState.Failed,
                },
                attempted);
        }

        [Fact]
        public void ARollbackThatCouldNotBeAttempted_IsNeitherARollbackNorAFailedOne()
        {
            var exec = new RemediationExecution
            {
                Outcome = RemediationOutcome.CouldNotRun,
                RollbackState = RemediationRollbackState.NotAvailable,
                RollbackError = "No rollback was attempted. This fix is not reversible.",
            };

            Assert.False(exec.RolledBack);         // nothing ran, so the ledger records nothing
            Assert.False(exec.RollbackSucceeded);  // and it is certainly not a success
            Assert.NotEqual(RemediationRollbackState.Failed, exec.RollbackState);
        }

        [Fact]
        public void TheExecutionAndTheResult_AgreeOnWhetherARollbackHappened_ForEveryState()
        {
            // One definition, two carriers. The page reads RemediationResult and the ledger reads
            // RemediationExecution; a drift between them is how a UI caption and an audit entry end
            // up telling different stories about the same apply.
            foreach (var s in AllStates())
            {
                var fromExecutor = new RemediationExecution { RollbackState = s }.RolledBack;
                var fromResult = RemediationResult
                    .Applied(RemediationOutcome.CouldNotRun, rollbackState: s).RolledBack;
                Assert.Equal(fromExecutor, fromResult);
            }
        }

        // ── The ledger: a third state, not a coerced boolean ─────────────────

        [Fact]
        public void AuditLedger_UnconfirmedRollback_IsItsOwnState_AndIsNotRecordedAsSuccess()
        {
            var dir = Path.Combine(Path.GetTempPath(), "rem-rollback-audit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                using (var svc = new AuditLogService(dir, startFlushTimer: false))
                {
                    svc.LogRemediationRolledBack("MAXDOP", "srv1", success: null, "confirming read failed");
                    svc.Flush();
                }

                var entry = ReadEntries(dir).Single();
                Assert.Equal(AuditEventType.RemediationRolledBack, entry.EventType);
                Assert.Equal(AuditSeverity.Warning, entry.Severity);          // not Info, not Error
                Assert.Equal("Unconfirmed", entry.Details["RollbackState"]);
                Assert.Equal("False", entry.Details["Success"]);              // never True
                Assert.Contains("NOT CONFIRMED", entry.Message);
                Assert.NotEqual(string.Empty, entry.Details["Error"]);        // the reason is carried
            }
            finally { TryDelete(dir); }
        }

        [Fact]
        public void AuditLedger_ConfirmedRollback_StillReadsAsAPlainSuccess()
        {
            var dir = Path.Combine(Path.GetTempPath(), "rem-rollback-audit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                using (var svc = new AuditLogService(dir, startFlushTimer: false))
                {
                    svc.LogRemediationRolledBack("MAXDOP", "srv1", success: true);
                    svc.Flush();
                }

                var entry = ReadEntries(dir).Single();
                Assert.Equal(AuditSeverity.Info, entry.Severity);
                Assert.Equal("Confirmed", entry.Details["RollbackState"]);
                Assert.Equal("True", entry.Details["Success"]);
            }
            finally { TryDelete(dir); }
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static List<AuditLogEntry> ReadEntries(string dir)
        {
            var file = Directory.GetFiles(dir, "audit-*.jsonl")
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).First();
            return File.ReadAllLines(file)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonSerializer.Deserialize<AuditLogEntry>(l)!)
                .ToList();
        }

        private static void TryDelete(string dir)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* test cleanup */ }
        }

        // SqlException cannot be constructed directly. A read failure that is not a SqlException is
        // the shape the apply lane already documents for a killed SPID (InvalidOperationException),
        // so the test uses a distinct one to prove the helper does not special-case a type.
        private static Exception MakeSqlLikeFailure() =>
            new IOException("Unable to read data from the transport connection.");
    }
}
