/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class ServerCircuitBreakerServiceTests : IDisposable
    {
        private readonly string _auditDir;

        public ServerCircuitBreakerServiceTests()
        {
            _auditDir = Path.Combine(Path.GetTempPath(), "cbsvc-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_auditDir);
        }

        public void Dispose()
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(_auditDir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(_auditDir, recursive: true);
            }
            catch { /* test cleanup; ignore */ }
        }

        private AuditLogService NewAudit() => new(_auditDir, startFlushTimer: false);

        private ServerCircuitBreakerService NewBreaker(AuditLogService? audit = null)
            => new(NullLogger<ServerCircuitBreakerService>.Instance, audit);

        // ── T3: after 3 failures, ShouldAttempt returns false ───────────

        [Fact]
        public void AfterThreeFailures_ShouldAttemptReturnsFalse()
        {
            // Arrange
            var svc = NewBreaker();

            // Act: record 3 consecutive failures (threshold for 60-second back-off)
            svc.RecordFailure("srv1");
            svc.RecordFailure("srv1");
            svc.RecordFailure("srv1");

            // Assert: circuit is open — attempt should be suppressed
            Assert.False(svc.ShouldAttempt("srv1"),
                "ShouldAttempt should return false when circuit is open after 3 failures.");
        }

        [Fact]
        public void AfterThreeFailures_NewServer_IsNotAffected()
        {
            // Arrange
            var svc = NewBreaker();
            svc.RecordFailure("srv1");
            svc.RecordFailure("srv1");
            svc.RecordFailure("srv1");

            // Assert: a different server is unaffected
            Assert.True(svc.ShouldAttempt("srv2"),
                "Circuit breaker for srv1 should not block a different server.");
        }

        // ── T4: recovery emits ServerCircuitClosed audit entry ──────────

        [Fact]
        public void RecordSuccess_AfterOpenCircuit_EmitsCircuitClosedAuditEntry()
        {
            // Arrange: drive circuit fully open (threshold == 10 failures for the RecordSuccess
            // "wasOpen" check, which tests ConsecutiveFailures >= BackOffSchedule[^1].Threshold).
            using var audit = NewAudit();
            var svc = NewBreaker(audit);

            // Back-off schedule: threshold 10 → 30 min. Must reach ConsecutiveFailures >= 10
            // for RecordSuccess to treat the circuit as "was open" and emit CircuitClosed.
            for (int i = 0; i < 10; i++)
                svc.RecordFailure("srv1");

            Assert.False(svc.ShouldAttempt("srv1"), "Circuit should be open after 10 failures.");

            // Act: record a success — circuit should close
            svc.RecordSuccess("srv1");

            // Flush audit so entries are on disk
            audit.Flush();

            // Assert: ShouldAttempt is now true (circuit closed, counter reset)
            Assert.True(svc.ShouldAttempt("srv1"), "ShouldAttempt should return true after RecordSuccess.");

            // Assert: exactly one ServerCircuitClosed entry in the audit log
            var entries = audit.GetEntries(
                DateTime.UtcNow.AddMinutes(-5),
                DateTime.UtcNow.AddMinutes(5),
                filterType: AuditEventType.ServerCircuitClosed);

            Assert.Single(entries);
            Assert.Equal("srv1", entries[0].Details["Server"]);
        }
    }
}
