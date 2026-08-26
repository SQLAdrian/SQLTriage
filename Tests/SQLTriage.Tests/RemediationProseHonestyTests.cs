/* In the name of God, the Merciful, the Compassionate */

using System;
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
    /// Cluster 8 of the remediation-safety lane (honesty hunt, 2026-08-25): three places where the
    /// prose claimed something the mechanism does not do.
    ///
    /// <para><b>r1-06</b> — the parked-server path wrote "retried automatically after the back-off
    /// TTL" into the audit ledger and told the operator the apply "will retry after the back-off
    /// window". Nothing retries: the TTL only stops the refusal, so the next MANUAL apply is the
    /// retry. The same path returned Applied WITHOUT calling LogRemediationApproved, while the page
    /// printed "Recorded in the audit ledger (Proposed / Approved / Applied)" over it. The hunt
    /// proved that live through the real AuditLogService: one EventType 39 for two EventType 40s.</para>
    ///
    /// <para><b>r1-05</b> — the deferred-verification panel read as an ongoing process. VerifyNowAsync
    /// has exactly one caller, the button on each row, and the app registers no hosted service.</para>
    ///
    /// <para><b>r1-09</b> — the headline promised every apply was "reversible" while Backup Now and
    /// CHECKDB Now ship on the same page with Reversible = false, and their own confirm boxes make
    /// the operator tick "is NOT reversible" one screen down.</para>
    /// </summary>
    public sealed class RemediationProseHonestyTests : IDisposable
    {
        private readonly string _auditDir;

        public RemediationProseHonestyTests()
        {
            _auditDir = Path.Combine(Path.GetTempPath(), "remsafe-prose-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_auditDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_auditDir, recursive: true); } catch { /* test cleanup */ }
        }

        private sealed class GrantedCapability : IRemediationCapability { public bool IsGranted => true; }

        private sealed class PermissionDeniedExecutor : IRemediationExecutor
        {
            public int ExecuteCalls;
            public Task<RemediationPreview> PreviewAsync(RemediationRequest r, CancellationToken ct = default) =>
                Task.FromResult(new RemediationPreview { Succeeded = true, WhatIfText = "would set MAXDOP" });
            public Task<RemediationExecution> ExecuteAsync(RemediationRequest r, CancellationToken ct = default)
            {
                ExecuteCalls++;
                return Task.FromResult(new RemediationExecution
                {
                    Outcome = RemediationOutcome.CouldNotRun,
                    IsPermissionDenied = true,
                    Error = "permission denied",
                });
            }
            public bool CanWriteAudit() => true;
        }

        private List<AuditLogEntry> ReadAuditEntries()
        {
            var file = Directory.GetFiles(_auditDir, "audit-*.jsonl")
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).First();
            return File.ReadAllLines(file)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => System.Text.Json.JsonSerializer.Deserialize<AuditLogEntry>(l)!)
                .ToList();
        }

        // ── r1-06: the parked path says what really happens, and records the approval ──

        [Fact]
        public async Task AParkedServer_IsNotToldItWillRetryOnItsOwn()
        {
            var exec = new PermissionDeniedExecutor();
            var audit = new AuditLogService(_auditDir, startFlushTimer: false);
            var runner = new RemediationRunner(
                new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance),
                new GrantedCapability(),
                new InMemoryRemediationCreditLedger(initialCreditsPerServer: 5),
                exec, audit, NullLogger<RemediationRunner>.Instance);

            var first = await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian");
            Assert.Equal(RemediationOutcome.CouldNotRun, first.Outcome);
            Assert.True(runner.IsServerParked("srv1"));

            var parked = await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian");
            Assert.Equal(1, exec.ExecuteCalls); // the second never reached the executor

            // The operator-facing sentence.
            Assert.DoesNotContain("will retry", parked.Message!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("automatic", parked.Message!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("No retry is scheduled", parked.Message!, StringComparison.Ordinal);
            Assert.Contains("apply again", parked.Message!, StringComparison.OrdinalIgnoreCase);

            audit.Flush();
            var entries = ReadAuditEntries();

            // The ledger detail.
            var appliedEntries = entries.Where(e => e.EventType == AuditEventType.RemediationApplied).ToList();
            Assert.Equal(2, appliedEntries.Count);
            Assert.DoesNotContain(appliedEntries, e =>
                (e.Details.TryGetValue("Error", out var d) ? d : string.Empty)
                    .Contains("automatically", StringComparison.OrdinalIgnoreCase));

            // The approval the page claims is in the ledger. One per attempt, including the parked
            // one: the human really did approve it, gate 4 required that before this path was
            // reached, and the page prints "Proposed / Approved / Applied" for the result either way.
            Assert.Equal(2, entries.Count(e => e.EventType == AuditEventType.RemediationApproved));
        }

        [Fact]
        public async Task AParkedRefusal_NamesTheMomentTheParkExpires()
        {
            var exec = new PermissionDeniedExecutor();
            var audit = new AuditLogService(_auditDir, startFlushTimer: false);
            var runner = new RemediationRunner(
                new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance),
                new GrantedCapability(),
                new InMemoryRemediationCreditLedger(initialCreditsPerServer: 5),
                exec, audit, NullLogger<RemediationRunner>.Instance);

            await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian");
            var parked = await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian");

            // A time the operator can act on, not "the back-off window". The TTL is 30 minutes, so
            // the stamp is today's date in UTC round-trip form.
            Assert.Contains(DateTime.UtcNow.ToString("yyyy-MM-dd"), parked.Message!, StringComparison.Ordinal);
            Assert.Contains("parked", parked.Message!, StringComparison.OrdinalIgnoreCase);
        }

        // ── r1-05 / r1-09: the page's copy matches the mechanism ──────────────

        private static string RemediationMarkup() =>
            File.ReadAllText(Path.Combine(RawPassedScan.RepoRoot().FullName, "Pages", "Remediation.razor"));

        [Fact]
        public void TheHeadline_DoesNotPromiseThatEveryApplyIsReversible()
        {
            var markup = RemediationMarkup();
            Assert.DoesNotContain("gated, bounded, reversible", markup, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TwoShippedFixesOnThatPage_ReallyAreNotReversible()
        {
            // The measurement the headline now defers to. If either of these ever becomes reversible
            // the page will say so on its own, because it reads the same templates this test does.
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            Assert.False(store.TryGet("BACKUPDATABASENOW")!.Reversible);
            Assert.False(store.TryGet("CHECKDBNOW")!.Reversible);
        }

        [Fact]
        public void TheDeferredVerificationPanel_DoesNotImplyAScheduler()
        {
            var markup = RemediationMarkup();
            Assert.DoesNotContain("verifying by", markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Scheduled verifications", markup, StringComparison.Ordinal);
            Assert.Contains("Nothing checks them on a schedule", markup, StringComparison.Ordinal);
        }

        [Fact]
        public void NothingButThatButton_RunsADeferredVerification()
        {
            // The claim the panel's copy rests on. If a scheduler is ever wired up, a second caller
            // appears here and this test fails, which is the signal to change the copy back.
            var root = RawPassedScan.RepoRoot().FullName;
            var callers = new List<string>();

            foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    && !file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.Contains(Path.DirectorySeparatorChar + "Tests" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;

                var text = File.ReadAllText(file);
                if (text.Contains("VerifyNowAsync(", StringComparison.Ordinal)
                    && !file.EndsWith("DeferredVerificationService.cs", StringComparison.OrdinalIgnoreCase))
                    callers.Add(Path.GetRelativePath(root, file));
            }

            Assert.True(callers.Count == 1,
                "DeferredVerificationService.VerifyNowAsync should have exactly one production caller " +
                "(the per-row Verify now button). Found: " + string.Join(", ", callers) +
                ". If a scheduler was added, the /remediation panel's copy must stop saying nothing runs on a schedule.");
        }
    }
}
