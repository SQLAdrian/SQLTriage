/* In the name of God, the Merciful, the Compassionate */
/*
 * AuditLogCorrelationIdTests — plan item 1.5, at the AUDIT layer.
 *
 * The correlation-id (Details["RunId"]) is an ADDITIVE key on NEW remediation entries. The whole
 * safety argument for it is that Details is serialized INSIDE both signed canonical forms, that
 * signatures are computed at write time, and that stored entries are never retrofitted — so a new
 * key changes what NEW entries sign over and changes NOTHING about entries already on disk.
 *
 * That argument was a READ (scout Q6 / spike S1 §5 item 3, tagged "believe"). This file EXERCISES
 * it, in both directions:
 *   - a chain of entries carrying RunId verifies Intact;
 *   - a chain of entries WITHOUT RunId (the shape every entry written before this build has) still
 *     verifies Intact, and gains no key;
 *   - the two shapes INTERLEAVED in one chain verify Intact together, which is the real-world case
 *     on an install that upgrades mid-life;
 *   - a decision's entries can be selected by the key alone, and only those.
 *
 * The negative control matters as much as the positive: MutatingAStoredRunId_BreaksThatEntry proves
 * VerifyChain would actually notice a retro-edit, so "never retro-edit stored entries" is a rule the
 * ledger enforces rather than a rule we merely wrote down.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SQLTriage.Data;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    public class AuditLogCorrelationIdTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _tempDir;
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

        public AuditLogCorrelationIdTests(ITestOutputHelper output)
        {
            _out = output;
            _tempDir = Path.Combine(Path.GetTempPath(), "audit-runid-tests-" + Guid.NewGuid().ToString("N"));
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

        private string NewDir(string tag)
        {
            var d = Path.Combine(_tempDir, tag);
            Directory.CreateDirectory(d);
            return d;
        }

        private static List<AuditLogEntry> ReadAll(string dir) =>
            Directory.GetFiles(dir, "audit-*.jsonl")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .SelectMany(File.ReadAllLines)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonSerializer.Deserialize<AuditLogEntry>(l, Json)!)
                .ToList();

        // ── The four lifecycle methods all carry it ────────────────────────────────

        [Fact]
        public void AllFourRemediationEntries_CarryTheRunId_WhenOneIsSupplied()
        {
            var dir = NewDir("all-four");
            using (var svc = new AuditLogService(dir, startFlushTimer: false))
            {
                svc.LogRemediationProposed("MAXDOP", "srv1", "set to 4", "run-alpha");
                svc.LogRemediationApproved("MAXDOP", "srv1", "adrian", null, "run-alpha");
                svc.LogRemediationApplied("MAXDOP", "srv1",
                    AuditLogService.RemediationOutcomes.AppliedVerified, null, "0", "run-alpha");
                svc.LogRemediationRolledBack("MAXDOP", "srv1", success: true, null, "run-alpha");
                svc.Flush();
            }

            var entries = ReadAll(dir);
            Assert.Equal(4, entries.Count);
            Assert.All(entries, e =>
                Assert.Equal("run-alpha", e.Details[AuditLogService.CorrelationDetailKey]));

            // And the key is the one the code names, not a string this test invented.
            Assert.Equal("RunId", AuditLogService.CorrelationDetailKey);
        }

        [Fact]
        public void AChainCarryingRunIds_VerifiesIntact()
        {
            var dir = NewDir("intact-with");
            using var svc = new AuditLogService(dir, startFlushTimer: false);
            for (int i = 0; i < 5; i++)
            {
                svc.LogRemediationProposed("MAXDOP", "srv1", $"preview {i}", "run-beta");
                svc.LogRemediationApproved("MAXDOP", "srv1", "adrian", null, "run-beta");
                svc.LogRemediationApplied("MAXDOP", "srv1",
                    AuditLogService.RemediationOutcomes.AppliedVerified, null, i.ToString(), "run-beta");
            }
            svc.Flush();

            var v = svc.VerifyChain("runid-intact");
            _out.WriteLine($"intact={v.Intact} status={v.Status} entries={v.EntryCount} broken={v.BrokenCount} "
                         + $"linkBreaks={v.LinkBreakCount} unverifiable={v.UnverifiableCount}");
            Assert.True(v.Intact);
            Assert.Equal(AuditLogService.ChainVerificationStatus.Intact, v.Status);
            Assert.Equal(0, v.BrokenCount);
            Assert.Equal(0, v.LinkBreakCount);
            Assert.Equal(15, ReadAll(dir).Count);
        }

        // ── Entries WITHOUT a run-id — every entry written before this build ───────

        [Fact]
        public void EntriesWrittenWithoutARunId_StillVerify_AndGainNoKey()
        {
            var dir = NewDir("legacy-shape");
            using var svc = new AuditLogService(dir, startFlushTimer: false);

            // Exactly the call shape every existing caller uses — no correlationId argument at all.
            svc.LogRemediationProposed("MAXDOP", "srv1", "set to 4");
            svc.LogRemediationApproved("MAXDOP", "srv1", "adrian");
            svc.LogRemediationApplied("MAXDOP", "srv1", AuditLogService.RemediationOutcomes.NoOp);
            svc.LogRemediationRolledBack("MAXDOP", "srv1", success: null, "could not confirm");
            svc.Flush();

            var v = svc.VerifyChain("runid-absent");
            Assert.True(v.Intact);
            Assert.Equal(AuditLogService.ChainVerificationStatus.Intact, v.Status);

            var entries = ReadAll(dir);
            Assert.Equal(4, entries.Count);
            // The key is ABSENT, not present-and-empty: an empty RunId on every legacy entry would
            // make "everything before the upgrade" look like one enormous batch.
            Assert.All(entries, e =>
                Assert.False(e.Details.ContainsKey(AuditLogService.CorrelationDetailKey)));

            // A whitespace-only id is treated as no id, for the same reason.
            svc.LogRemediationApplied("MAXDOP", "srv1", AuditLogService.RemediationOutcomes.NoOp, null, null, "   ");
            svc.Flush();
            Assert.False(ReadAll(dir).Last().Details.ContainsKey(AuditLogService.CorrelationDetailKey));
        }

        [Fact]
        public void ChainsMixingBothShapes_VerifyIntactTogether()
        {
            // The real upgrade case: an install with history, then a build that adds the key.
            var dir = NewDir("mixed");
            using var svc = new AuditLogService(dir, startFlushTimer: false);

            svc.LogRemediationProposed("MAXDOP", "srv1", "pre-upgrade");
            svc.LogRemediationApplied("MAXDOP", "srv1", AuditLogService.RemediationOutcomes.AppliedVerified);
            svc.LogRemediationProposed("CTFP", "srv1", "post-upgrade", "run-gamma");
            svc.LogRemediationApplied("CTFP", "srv1",
                AuditLogService.RemediationOutcomes.AppliedVerified, null, "50", "run-gamma");
            svc.LogRemediationApplied("MAXDOP", "srv1", AuditLogService.RemediationOutcomes.NoOp);
            svc.Flush();

            var v = svc.VerifyChain("runid-mixed");
            _out.WriteLine($"mixed chain: intact={v.Intact} status={v.Status} entries={v.EntryCount}");
            Assert.True(v.Intact);
            Assert.Equal(0, v.BrokenCount);

            var entries = ReadAll(dir);
            Assert.Equal(2, entries.Count(e => e.Details.ContainsKey(AuditLogService.CorrelationDetailKey)));
            Assert.Equal(3, entries.Count(e => !e.Details.ContainsKey(AuditLogService.CorrelationDetailKey)));
        }

        // ── The join itself ────────────────────────────────────────────────────────

        [Fact]
        public void TheRunIdSelectsOneDecisionsEntries_AndOnlyThose()
        {
            var dir = NewDir("join");
            using var svc = new AuditLogService(dir, startFlushTimer: false);

            foreach (var key in new[] { "MAXDOP", "CTFP", "OPTIMIZEFORADHOC" })
            {
                svc.LogRemediationApproved(key, "srv1", "adrian", null, "decision-1");
                svc.LogRemediationApplied(key, "srv1", AuditLogService.RemediationOutcomes.AppliedVerified,
                    null, null, "decision-1");
            }
            svc.LogRemediationApproved("MAXDOP", "srv1", "adrian", null, "decision-2");
            svc.LogRemediationApplied("MAXDOP", "srv1", AuditLogService.RemediationOutcomes.NoOp,
                null, null, "decision-2");
            svc.LogRemediationApplied("BACKUPCOMPRESSION", "srv1", AuditLogService.RemediationOutcomes.NoOp);
            svc.Flush();

            var all = ReadAll(dir);
            var decision1 = all.Where(e =>
                e.Details.TryGetValue(AuditLogService.CorrelationDetailKey, out var id) && id == "decision-1").ToList();

            Assert.Equal(6, decision1.Count);
            Assert.Equal(new[] { "CTFP", "MAXDOP", "OPTIMIZEFORADHOC" },
                decision1.Select(e => e.Details["TemplateKey"]).Distinct().OrderBy(k => k, StringComparer.Ordinal));
            // Same approver, same server, DIFFERENT decision — the join must not pull decision-2 in.
            Assert.DoesNotContain(decision1, e => e.Details["TemplateKey"] == "BACKUPCOMPRESSION");
            Assert.Equal(2, all.Count(e =>
                e.Details.TryGetValue(AuditLogService.CorrelationDetailKey, out var id) && id == "decision-2"));
        }

        // ── Negative control: the ledger really would catch a retro-edit ───────────

        [Fact]
        public void MutatingAStoredRunId_BreaksThatEntry_WhichIsWhyEntriesAreNeverRetrofitted()
        {
            var dir = NewDir("retro-edit");
            using (var svc = new AuditLogService(dir, startFlushTimer: false))
            {
                svc.LogRemediationApplied("MAXDOP", "srv1",
                    AuditLogService.RemediationOutcomes.AppliedVerified, null, "0", "run-original");
                svc.LogRemediationApplied("CTFP", "srv1",
                    AuditLogService.RemediationOutcomes.AppliedVerified, null, "50", "run-original");
                svc.Flush();
                Assert.True(svc.VerifyChain("pre-edit").Intact);
            }

            var file = Directory.GetFiles(dir, "audit-*.jsonl")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).First();
            var lines = File.ReadAllLines(file);

            // Rewrite a stored RunId in place — precisely what the doc comment forbids.
            lines[0] = lines[0].Replace("run-original", "run-forged", StringComparison.Ordinal);
            File.WriteAllLines(file, lines);

            using var after = new AuditLogService(dir, startFlushTimer: false);
            var v = after.VerifyChain("post-edit");
            _out.WriteLine($"after retro-edit: intact={v.Intact} status={v.Status} broken={v.BrokenCount}");
            Assert.False(v.Intact);
            Assert.True(v.BrokenCount >= 1,
                $"expected at least one broken entry, got {v.BrokenCount} (status {v.Status})");
        }
    }
}
