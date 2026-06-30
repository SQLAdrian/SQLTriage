/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Build step 2 of the gated remediation lane: the LogRemediation* ledger
    /// methods. These prove each lifecycle method enqueues a correctly-typed,
    /// HMAC-chained entry, and that LogRemediationApplied derives severity from
    /// the DISTINCT TERMINAL STATE (never a bare success boolean) — with an
    /// unrecognised outcome treated as suspect.
    /// </summary>
    public class AuditLogRemediationTests : IDisposable
    {
        private readonly string _tempDir;
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

        public AuditLogRemediationTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "audit-remediation-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(_tempDir, recursive: true);
            }
            catch { /* test cleanup; ignore */ }
        }

        private AuditLogService NewService() => new(_tempDir, startFlushTimer: false);

        private List<AuditLogEntry> ReadAll()
        {
            var file = Directory.GetFiles(_tempDir, "audit-*.jsonl")
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).First();
            return File.ReadAllLines(file)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonSerializer.Deserialize<AuditLogEntry>(l, Json)!)
                .ToList();
        }

        // ── Each lifecycle method enqueues its typed entry ──────────────────

        [Fact]
        public void LogRemediationProposed_WritesProposedEntry()
        {
            using var svc = NewService();
            svc.LogRemediationProposed("MAXDOP", "srv1", "set to 4");
            svc.Flush();

            var e = ReadAll().Single();
            Assert.Equal(AuditEventType.RemediationProposed, e.EventType);
            Assert.Equal(AuditSeverity.Info, e.Severity);
            Assert.Equal("MAXDOP", e.Details["TemplateKey"]);
            Assert.Equal("srv1", e.Details["ServerName"]);
        }

        [Fact]
        public void LogRemediationApproved_RecordsApprover()
        {
            using var svc = NewService();
            svc.LogRemediationApproved("MAXDOP", "srv1", "adrian");
            svc.Flush();

            var e = ReadAll().Single();
            Assert.Equal(AuditEventType.RemediationApproved, e.EventType);
            Assert.Equal("adrian", e.Details["ApprovedBy"]);
            Assert.Contains("adrian", e.Message);
        }

        [Fact]
        public void LogRemediationRolledBack_FailureIsError()
        {
            using var svc = NewService();
            svc.LogRemediationRolledBack("MAXDOP", "srv1", success: false, "lock timeout");
            svc.Flush();

            var e = ReadAll().Single();
            Assert.Equal(AuditEventType.RemediationRolledBack, e.EventType);
            Assert.Equal(AuditSeverity.Error, e.Severity);
            Assert.Equal("False", e.Details["Success"]);
            Assert.Contains("lock timeout", e.Message);
        }

        // ── Applied: severity is derived from the terminal state ────────────

        [Theory]
        [InlineData(AuditLogService.RemediationOutcomes.AppliedVerified, AuditSeverity.Info)]
        [InlineData(AuditLogService.RemediationOutcomes.NoOp, AuditSeverity.Info)]
        [InlineData(AuditLogService.RemediationOutcomes.AppliedVerifyFailed, AuditSeverity.Error)]
        [InlineData(AuditLogService.RemediationOutcomes.CouldNotRun, AuditSeverity.Warning)]
        public void LogRemediationApplied_MapsOutcomeToSeverity(string outcome, AuditSeverity expected)
        {
            using var svc = NewService();
            svc.LogRemediationApplied("MAXDOP", "srv1", outcome);
            svc.Flush();

            var e = ReadAll().Single();
            Assert.Equal(AuditEventType.RemediationApplied, e.EventType);
            Assert.Equal(expected, e.Severity);
            Assert.Equal(outcome, e.Details["Outcome"]);
        }

        [Fact]
        public void LogRemediationApplied_UnknownOutcome_IsTreatedAsSuspect()
        {
            // An outcome the audit layer does not recognise must NOT read as benign
            // success — it logs at Warning so it surfaces in the ledger.
            using var svc = NewService();
            svc.LogRemediationApplied("MAXDOP", "srv1", "garbage-from-a-typo");
            svc.Flush();

            var e = ReadAll().Single();
            Assert.Equal(AuditSeverity.Warning, e.Severity);
            Assert.NotEqual(AuditSeverity.Info, e.Severity);
        }

        // ── Audit-writability probe (gate-5 pre-flight) ─────────────────────

        [Fact]
        public void CanWrite_OnWritableDirectory_IsTrue_AndLeavesNoProbeFile()
        {
            using var svc = NewService();
            Assert.True(svc.CanWrite);
            Assert.True(svc.CanWrite); // idempotent
            // The probe cleans up after itself — no stray probe files left behind.
            Assert.Empty(Directory.GetFiles(_tempDir, ".write-probe-*"));
        }

        // ── The lifecycle is HMAC-chained like every other event ────────────

        [Fact]
        public void RemediationLifecycle_FormsContiguousHmacChain()
        {
            using var svc = NewService();
            svc.LogRemediationProposed("MAXDOP", "srv1");
            svc.LogRemediationApproved("MAXDOP", "srv1", "adrian");
            svc.LogRemediationApplied("MAXDOP", "srv1", AuditLogService.RemediationOutcomes.AppliedVerified);
            svc.Flush();

            var entries = ReadAll();
            Assert.Equal(3, entries.Count);
            Assert.Equal(string.Empty, entries[0].PreviousHash);
            for (int i = 1; i < entries.Count; i++)
                Assert.Equal(entries[i - 1].Signature, entries[i].PreviousHash);
        }

        // ── Cross-session rollback: pre-change value rides the ledger ───────

        [Fact]
        public void LogRemediationApplied_PersistsPreChangeValue()
        {
            using var svc = NewService();
            svc.LogRemediationApplied("MAXDOP", "srv1",
                AuditLogService.RemediationOutcomes.AppliedVerified, errorMessage: null, preChangeValue: "0");
            svc.Flush();

            var e = ReadAll().Single();
            Assert.Equal("0", e.Details["PreChangeValue"]);
        }

        [Fact]
        public void GetLatestRemediationPreChange_ReturnsMostRecentVerifiedValue()
        {
            using var svc = NewService();
            svc.LogRemediationApplied("MAXDOP", "srv1",
                AuditLogService.RemediationOutcomes.AppliedVerified, preChangeValue: "0");
            // Distinct timestamp so "most recent" is unambiguous (entries are ordered by Timestamp).
            System.Threading.Thread.Sleep(30);
            svc.LogRemediationApplied("MAXDOP", "srv1",
                AuditLogService.RemediationOutcomes.AppliedVerified, preChangeValue: "4");
            svc.Flush();

            // Most recent verified apply on this (template, server) wins.
            Assert.Equal("4", svc.GetLatestRemediationPreChange("MAXDOP", "srv1"));
            // No record for a different server or template.
            Assert.Null(svc.GetLatestRemediationPreChange("MAXDOP", "other"));
            Assert.Null(svc.GetLatestRemediationPreChange("CTFP", "srv1"));
        }

        [Fact]
        public void GetLatestRemediationPreChange_IgnoresNonVerifiedAndEmptyValues()
        {
            using var svc = NewService();
            // A verified apply with a recoverable value …
            svc.LogRemediationApplied("MAXDOP", "srv1",
                AuditLogService.RemediationOutcomes.AppliedVerified, preChangeValue: "2");
            // … then a later FAILED apply (no recoverable value) must NOT shadow it.
            svc.LogRemediationApplied("MAXDOP", "srv1",
                AuditLogService.RemediationOutcomes.AppliedVerifyFailed, errorMessage: "verify failed");
            svc.Flush();

            Assert.Equal("2", svc.GetLatestRemediationPreChange("MAXDOP", "srv1"));
        }
    }
}
