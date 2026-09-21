/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
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

            // The park is stamped inside the FIRST apply, from DateTime.UtcNow + the back-off TTL.
            // Bracket that apply so the moment the refusal names can be checked against a window
            // this test actually observed, rather than against a re-formatting of "now".
            var before = DateTime.UtcNow;
            await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian");
            var after = DateTime.UtcNow;

            var parked = await runner.ApplyAsync("MAXDOP", "srv1", approved: true, "adrian");

            // A time the operator can act on, not "the back-off window".
            //
            // WHY THIS IS NOT ASSERTED AGAINST TODAY'S DATE (board #13, proved on main CI run
            // 33571170970 attempt 1, created 2026-09-01T23:28Z and red inside the last 30 UTC
            // minutes of that day). The old form asserted the message contained
            // DateTime.UtcNow.ToString("yyyy-MM-dd"). The stamp is now + a 30-minute TTL, so for the
            // last 30 minutes of every UTC day the stamp carries TOMORROW's date and the cell went
            // red on a clock, not on a defect. The fix reads the stamp the message itself printed,
            // parses it back through the same "u" round-trip format the runner formats with, and
            // checks it lands where the observed clock and the PRODUCTION TTL say it must. Nothing
            // here formats the current date, so no date boundary can reach it.
            var expiry = ParkExpiryStampIn(parked.Message!);
            var ttl = ProductionBackoffTtl();

            // "u" truncates to whole seconds, so the printed stamp can sit up to one second before
            // the instant the runner computed. That second is the only slack in the window.
            Assert.InRange(expiry, before + ttl - TimeSpan.FromSeconds(1), after + ttl);
            Assert.Contains("parked", parked.Message!, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The boundary probe the shipped cell above cannot run on its own: the parked sentence stamped at a
        /// PINNED instant inside the last 30 UTC minutes of a day, read back through the same reader and held
        /// to the same window. The retained form of the scratch proof made at 47cf8ce. The stamp is derived
        /// from the production TTL, so a TTL change fails these rows loudly instead of passing them blind.
        /// </summary>
        [Theory]
        // before (what the first apply observed)   stamp the runner prints   skew  inside the window
        [InlineData("2026-09-03T23:45:00Z", "2026-09-04 00:15:00Z", 0, true)]    // the CI-red shape: last 30 UTC minutes, the stamp is TOMORROW
        [InlineData("2026-09-03T23:59:59Z", "2026-09-04 00:29:59Z", 0, true)]    // the final second of the day
        [InlineData("2026-09-03T23:45:00Z", "2026-09-04 00:30:00Z", 15, false)]  // TIGHTNESS: a stamp 15 minutes off the TTL is OUTSIDE the window
        public void AParkedRefusalStampedInsideTheLast30UtcMinutes_StillNamesTheMomentTheParkExpires(
            string beforeUtc, string expectedStamp, int skewMinutes, bool insideWindow)
        {
            var before = DateTime.ParseExact(
                beforeUtc,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            Assert.Equal(DateTimeKind.Utc, before.Kind);

            var after = before.AddMilliseconds(400);      // the bracket the shipped cell observes around the first apply
            var ttl = ProductionBackoffTtl();             // the production field, read by the shipped helper
            var runnerNow = before.AddMilliseconds(120);  // the instant the runner read, inside that bracket
            var parkedUntil = runnerNow + ttl + TimeSpan.FromMinutes(skewMinutes);

            // The row proves a date boundary only while the production TTL still carries this park past
            // midnight. Shorten the TTL and this fails here, loudly, instead of passing on a moved boundary.
            Assert.True(
                (before + ttl).Date > before.Date,
                "this row only proves the date boundary while the production TTL carries a " + beforeUtc + " park past midnight; re-pin the rows");

            // Copied from Data/Services/Remediation/RemediationRunner.cs line 182. The shipped cell above
            // reads the real runner's sentence; this cell only needs the same {parkedUntil:u} stamp shape
            // at a pinned instant.
            var message = $"Not attempted. The server is parked after an earlier permissions denial until {parkedUntil:u}. "
                + "Nothing retries on its own; applying again after that time is the retry.";

            var expiry = ParkExpiryStampIn(message);

            // The literal row value documents the exact CI-red shape.
            Assert.Equal(
                DateTime.ParseExact(
                    expectedStamp,
                    "u",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                expiry);
            Assert.Equal(before.Date.AddDays(1), expiry.Date);   // it lands on the NEXT UTC day

            // The shipped window, verbatim.
            var lo = before + ttl - TimeSpan.FromSeconds(1);
            var hi = after + ttl;
            if (insideWindow)
            {
                Assert.InRange(expiry, lo, hi);
            }
            else
            {
                Assert.NotInRange(expiry, lo, hi);
            }

            // What 47cf8ce removed, kept here as a negative so the old shape cannot creep back.
            Assert.False(
                message.Contains(before.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal),
                "the pre-47cf8ce assertion shape (the message contains today's date) is exactly what went red at 23:34Z on attempt 1 of main CI run 33571170970; attempt 2, after midnight, is the green one a cold query returns");
            Assert.Contains(expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The moment named by a parked refusal, read back out of the operator's own sentence.
        /// <para>The runner formats the park expiry with <c>{parkedUntil:u}</c>, so the sentence
        /// carries exactly one <c>yyyy-MM-dd HH:mm:ssZ</c> stamp. Parsing that back is what makes
        /// the assertion above independent of the wall clock's date.</para>
        /// <para><c>internal</c> so a boundary probe can exercise this exact reader against a
        /// sentence stamped at an arbitrary pinned instant.</para>
        /// </summary>
        internal static DateTime ParkExpiryStampIn(string message)
        {
            var match = ParkStamp.Match(message);
            Assert.True(match.Success,
                "a parked refusal must name the moment the park expires, and this one does not: " + message);

            return DateTime.ParseExact(
                match.Value, "u", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        }

        /// <summary>The shape DateTime's "u" round-trip format prints: 2026-09-03 23:45:00Z.</summary>
        private static readonly Regex ParkStamp =
            new(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}Z", RegexOptions.Compiled);

        /// <summary>
        /// The back-off TTL the runner actually parks for, read off the production field rather than
        /// re-typed here. A test that hard-codes 30 minutes keeps passing after production changes
        /// the window, which is the same class of blindness the date assertion had; a rename fails
        /// this loudly instead. (The sibling harness in MetricRetentionTests repoints private fields
        /// the same way.)
        /// </summary>
        internal static TimeSpan ProductionBackoffTtl()
        {
            var field = typeof(RemediationRunner).GetField(
                "BackoffTtl", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.True(field != null,
                "RemediationRunner.BackoffTtl was renamed; the parked-refusal assertion is derived from it");
            return (TimeSpan)field!.GetValue(null)!;
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
