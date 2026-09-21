/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The four fixes ruled on 2026-08-11, after a read-only forensic pass over the installed
    /// service's own audit-logs directory (frozen copy taken during the build-3499 redeploy).
    /// <para>
    /// WHAT THE FORENSICS FOUND, in the order these tests take it:
    /// </para>
    /// <list type="number">
    /// <item>The BROKEN banner counted a cross-segment LINK break as an entry that "FAILED
    /// verification against its own signing key", and added the unverifiable entries back onto a
    /// total that already contained them. Both halves of the sentence an operator reads in anger
    /// were arithmetically false.</item>
    /// <item>Three entries declaring <c>PreviousHash=""</c> mid-segment, and one segment opening
    /// against a pre-break signature, were all reported as tampering. Every one of them verifies
    /// byte-for-byte against the link it declares: they are chain RESTARTS caused by an earlier
    /// build's verify loop returning without advancing the running signature.</item>
    /// <item>The key-replacement record named the new key and nothing about the old one, so the
    /// account of the incident had to be reconstructed from NTFS timestamps.</item>
    /// <item>The key-age sidecar still described a key destroyed eleven days earlier, and the age
    /// and rotation-due date were computed from it.</item>
    /// </list>
    /// <para>
    /// EVERY FIXTURE BELOW IS WRITTEN BY THE PRODUCTION SIGNER. Nothing here hand-composes a
    /// signature or a JSON entry: the shapes are produced by driving <see cref="AuditLogService"/>
    /// itself and then rearranging whole lines it wrote, which is what the accident did in effect.
    /// </para>
    /// </summary>
    public class AuditChainRestartAndKeyCustodyTests : IDisposable
    {
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
        private readonly List<string> _dirs = new();

        private string NewDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "audit-restart-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            _dirs.Add(dir);
            return dir;
        }

        public void Dispose()
        {
            foreach (var dir in _dirs)
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                    }
                    Directory.Delete(dir, recursive: true);
                }
                catch { /* test cleanup; ignore */ }
            }
            GC.SuppressFinalize(this);
        }

        private static AuditLogService Open(string dir) => new(dir, startFlushTimer: false);

        private static string[] Segments(string dir) =>
            Directory.GetFiles(dir, "audit-*.jsonl")
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();

        private static string LatestSegment(string dir)
        {
            var segs = Segments(dir);
            Assert.NotEmpty(segs);
            return segs[^1];
        }

        private static List<string> Lines(string path) =>
            File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

        private static AuditLogEntry Parse(string line) =>
            JsonSerializer.Deserialize<AuditLogEntry>(line, Json)!;

        /// <summary>
        /// Reproduces the live mid-segment restart with the PRODUCTION writer and no hand-editing of
        /// any entry. The trick is the one the accident played: the writer is started over a
        /// directory whose segment it cannot see, so the tail it has to chain onto is empty, and the
        /// entry it writes declares <c>PreviousHash=""</c>. Its lines are then appended after the
        /// hidden ones, which is exactly the file the old build left behind (audit-2026-07-31.jsonl
        /// line 5, and again at lines 80 and 84).
        /// </summary>
        /// <returns>the segment path, and the ordinal of the first restart entry.</returns>
        private static (string Segment, int RestartOrdinal) PlantMidSegmentRestart(string dir)
        {
            using (var first = Open(dir))
            {
                first.LogConnectionAttempt("srv1", success: true);
                first.LogConnectionAttempt("srv2", success: true);
                first.Flush();
            }

            var segment = LatestSegment(dir);
            var beforeRestart = Lines(segment);

            // Not "audit-*.jsonl", so the next service sees an empty chain and seeds an empty tail.
            var hidden = Path.Combine(dir, "hidden-segment.hold");
            File.Move(segment, hidden);

            using (var second = Open(dir))
            {
                second.LogConnectionAttempt("srv3", success: true);
                second.Flush();
            }

            var afterRestart = Lines(LatestSegment(dir));
            Assert.Equal(string.Empty, Parse(afterRestart[0]).PreviousHash);

            File.WriteAllLines(segment, beforeRestart.Concat(afterRestart));
            File.Delete(hidden);
            return (segment, beforeRestart.Count);
        }

        // ── FIX 2: a verified restart is a restart, not a tamper ─────────────────

        /// <summary>
        /// The live shape, end to end: an entry declaring an empty previous-hash link mid-segment,
        /// whose own signature verifies. It must not be counted as a failed entry, the verdict must
        /// not be BROKEN, and the words the operator reads must not be the tampering playbook.
        /// <para>
        /// RED before this build: BrokenCount 1, Status Broken, StatusDetail "1 of 3 entries FAILED
        /// verification against their own signing key ... Treat as tampering until proven otherwise."
        /// </para>
        /// </summary>
        [Fact]
        public void MidSegmentRestart_IsClassifiedRestarted_NotBroken()
        {
            if (!OperatingSystem.IsWindows()) return;
            var dir = NewDir();
            var (segment, restartOrdinal) = PlantMidSegmentRestart(dir);

            using var verifier = Open(dir);
            var result = verifier.VerifyChain("test");

            Assert.Equal(3, result.EntryCount);
            Assert.Equal(1, result.RestartCount);
            Assert.Equal(0, result.BrokenCount);
            Assert.Equal(0, result.LinkBreakCount);
            Assert.Equal(AuditLogService.ChainVerificationStatus.Restarted, result.Status);
            Assert.Equal("RESTARTED", result.StatusLabel);

            // Not clean, and not an accusation.
            Assert.False(result.Intact);
            Assert.DoesNotContain("Treat as tampering", result.StatusDetail);
            Assert.DoesNotContain("FAILED", result.StatusDetail);
            Assert.Contains("resumed after a break", result.StatusDetail);
            Assert.Contains("the LINK across the gap", result.StatusDetail);

            // The restart is NAMED, with the record an operator would quote.
            Assert.NotNull(result.FirstRestartRecord);
            var restartEntry = Parse(Lines(segment)[restartOrdinal]);
            Assert.Contains(restartEntry.Timestamp.ToString("o"), result.FirstRestartRecord!);

            // The startup scan reaches the same verdict on the same bytes, and the red banner is
            // not the one that fires.
            Assert.True(verifier.ChainRestarted);
            Assert.False(verifier.ChainBroken);
        }

        /// <summary>
        /// The cross-segment half of the live shape: audit-2026-08-03.jsonl opened against the
        /// signature of an entry four lines into the PREVIOUS segment, because that was the last
        /// signature the aborted verify loop had advanced to. The bytes are all present, nothing is
        /// missing, and the opening entry verifies against the link it declares.
        /// <para>RED before this build: BrokenCount 1 (a LINK counted as a failed ENTRY), Status Broken.</para>
        /// </summary>
        [Fact]
        public void SegmentOpeningAgainstAPreBreakSignature_IsARestart_NotALinkBreak()
        {
            if (!OperatingSystem.IsWindows()) return;
            var dir = NewDir();

            using (var first = Open(dir))
            {
                for (int i = 0; i < 4; i++) first.LogConnectionAttempt($"srv{i}", success: true);
                first.Flush();
            }

            var original = LatestSegment(dir);
            var all = Lines(original);
            Assert.Equal(4, all.Count);

            // Move the whole segment to an EARLIER date so the writer opens a new one, and truncate
            // it to its first two entries so the next service seeds its tail from entry 2 — the
            // "pre-break signature" the old build was still holding.
            var earlier = Path.Combine(dir, "audit-2000-01-01.jsonl");
            File.Delete(original);
            File.WriteAllLines(earlier, all.Take(2));

            using (var second = Open(dir))
            {
                second.LogConnectionAttempt("after-restart", success: true);
                second.Flush();
            }

            var newSegment = Segments(dir).Single(s => !s.Equals(earlier, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(Parse(all[1]).Signature, Parse(Lines(newSegment)[0]).PreviousHash);

            // Restore every line the first service wrote. The carry-in is now entry 4's signature
            // while the new segment declares entry 2's, and entry 2's is still on disk.
            File.WriteAllLines(earlier, all);

            using var verifier = Open(dir);
            var result = verifier.VerifyChain("test");

            Assert.Equal(5, result.EntryCount);
            Assert.Equal(1, result.RestartCount);
            Assert.Equal(0, result.BrokenCount);
            Assert.Equal(0, result.LinkBreakCount);
            Assert.Equal(AuditLogService.ChainVerificationStatus.Restarted, result.Status);
            Assert.DoesNotContain("Treat as tampering", result.StatusDetail);
        }

        /// <summary>
        /// THE DISCRIMINATOR, and the reason the restart state cannot be used as a downgrade switch.
        /// A deletion also leaves a following entry whose declared link is not the one the walk
        /// carried in, and that entry also verifies against its own link — the difference is that
        /// the link it names is nowhere in the chain, because the entry that carried it was removed.
        /// That stays BROKEN.
        /// <para>
        /// This is the same shape as the test above, mutated in exactly one way: the entries between
        /// the declared link and the resumption point are deleted instead of restored.
        /// </para>
        /// </summary>
        [Fact]
        public void ResumingFromALinkThatIsNotInTheChain_StaysBroken()
        {
            if (!OperatingSystem.IsWindows()) return;
            var dir = NewDir();

            using (var first = Open(dir))
            {
                for (int i = 0; i < 4; i++) first.LogConnectionAttempt($"srv{i}", success: true);
                first.Flush();
            }

            var original = LatestSegment(dir);
            var all = Lines(original);
            var earlier = Path.Combine(dir, "audit-2000-01-01.jsonl");
            File.Delete(original);
            File.WriteAllLines(earlier, all.Take(2));

            using (var second = Open(dir))
            {
                second.LogConnectionAttempt("after-restart", success: true);
                second.Flush();
            }

            // THE MUTATION: entry 2 — the one whose signature the new segment declares — is removed
            // instead of restored. Nothing else about the fixture changes.
            File.WriteAllLines(earlier, all.Take(1));

            using var verifier = Open(dir);
            var result = verifier.VerifyChain("test");

            Assert.Equal(0, result.RestartCount);
            Assert.Equal(1, result.LinkBreakCount);
            Assert.Equal(AuditLogService.ChainVerificationStatus.Broken, result.Status);
            Assert.Contains("Treat as tampering", result.StatusDetail);
        }

        /// <summary>
        /// An entry that verifies against NEITHER its declared link nor the running one is a tamper,
        /// and the restart state must not reach it. Same fixture as the mid-segment restart, with
        /// the restart entry's message rewritten.
        /// </summary>
        [Fact]
        public void AnEntryVerifyingAgainstNeitherLink_StaysBroken()
        {
            if (!OperatingSystem.IsWindows()) return;
            var dir = NewDir();
            var (segment, restartOrdinal) = PlantMidSegmentRestart(dir);

            var lines = Lines(segment);
            var tampered = Parse(lines[restartOrdinal]);
            tampered.Message = "TAMPERED";
            lines[restartOrdinal] = JsonSerializer.Serialize(tampered, Json);
            File.WriteAllLines(segment, lines);

            using var verifier = Open(dir);
            var result = verifier.VerifyChain("test");

            Assert.Equal(0, result.RestartCount);
            Assert.Equal(1, result.BrokenCount);
            Assert.Equal(AuditLogService.ChainVerificationStatus.Broken, result.Status);
            Assert.Contains("Treat as tampering", result.StatusDetail);
            Assert.True(verifier.ChainBroken);
            Assert.False(verifier.ChainRestarted);
        }

        // ── FIX 1: the banner arithmetic ─────────────────────────────────────────

        /// <summary>
        /// The BROKEN sentence, on bytes that carry a real tamper. Each fact gets its own number and
        /// its own noun, and the numbers close over the entries examined.
        /// <para>
        /// RED before this build: the sentence read "N of M entries FAILED verification against
        /// their own signing key", where N mixed entry mismatches with link breaks and M included
        /// entries that were never checked.
        /// </para>
        /// </summary>
        [Fact]
        public void BrokenDetail_GivesEachFactItsOwnNumber_AndTheNumbersClose()
        {
            if (!OperatingSystem.IsWindows()) return;
            var dir = NewDir();

            using (var svc = Open(dir))
            {
                for (int i = 0; i < 4; i++) svc.LogConnectionAttempt($"srv{i}", success: true);
                svc.Flush();
            }

            var segment = LatestSegment(dir);
            var lines = Lines(segment);
            var tampered = Parse(lines[2]);
            tampered.Message = "TAMPERED";
            lines[2] = JsonSerializer.Serialize(tampered, Json);
            File.WriteAllLines(segment, lines);

            using var verifier = Open(dir);
            var result = verifier.VerifyChain("test");

            Assert.Equal(AuditLogService.ChainVerificationStatus.Broken, result.Status);

            // The closed sum: nothing is counted twice and nothing is unaccounted for.
            Assert.Equal(result.EntryCount,
                result.VerifiedCount + result.BrokenCount + result.UnverifiableCount + result.IndeterminateCount);

            var detail = result.StatusDetail;
            Assert.Contains($"Entries examined: {result.EntryCount}", detail);
            Assert.Contains($"Verified against their own signing key: {result.VerifiedCount}", detail);
            Assert.Contains($"Entry signature mismatches: {result.BrokenCount}", detail);
            Assert.Contains($"Chain link breaks (a break BETWEEN entries, not a failed entry): {result.LinkBreakCount}", detail);
            Assert.Contains($"Entries not checked at all: {result.UncheckedCount}", detail);

            // The two false shapes are gone by construction, not by wording.
            Assert.DoesNotContain("FAILED verification against their own signing key", detail);
            Assert.DoesNotContain("a further", detail);
        }

        /// <summary>
        /// A link break is reported as a link break and NOT as an entry that failed against its own
        /// key. Driven through the deletion fixture, which is the only shape that produces one.
        /// </summary>
        [Fact]
        public void ALinkBreak_IsNeverCountedAsAFailedEntry()
        {
            if (!OperatingSystem.IsWindows()) return;
            var dir = NewDir();

            using (var first = Open(dir))
            {
                for (int i = 0; i < 4; i++) first.LogConnectionAttempt($"srv{i}", success: true);
                first.Flush();
            }

            var original = LatestSegment(dir);
            var all = Lines(original);
            var earlier = Path.Combine(dir, "audit-2000-01-01.jsonl");
            File.Delete(original);
            File.WriteAllLines(earlier, all.Take(2));
            using (var second = Open(dir)) { second.LogConnectionAttempt("resumed", success: true); second.Flush(); }
            File.WriteAllLines(earlier, all.Take(1));

            using var verifier = Open(dir);
            var result = verifier.VerifyChain("test");

            Assert.Equal(1, result.LinkBreakCount);
            Assert.Equal(0, result.BrokenCount);
            // Every entry still present verified against its own key, and the verdict is still
            // BROKEN because of the LINK. Both of those are said, separately.
            Assert.Equal(result.EntryCount, result.VerifiedCount);
            Assert.Contains("Entry signature mismatches: 0", result.StatusDetail);
            Assert.Contains("Chain link breaks", result.StatusDetail);
            Assert.Equal(AuditLogService.ChainVerificationStatus.Broken, result.Status);
        }

        /// <summary>
        /// A REPLAYED entry is not a restart. Verification measured on 2026-08-11 that the restart
        /// discriminator bound an entry to a LINK and not to a POSITION: a byte-identical copy of an
        /// already-signed entry verifies against its own key and its own declared link wherever it
        /// is pasted, and that declared link is by definition one the walk has already read. So the
        /// three duplication shapes below all landed on the benign RESTARTED arm, at Warning
        /// severity, under a detail sentence reading "do not file this as tampering", and the
        /// out-of-band anchor is silent on all three because nothing was removed.
        /// <para>
        /// RED before this build, measured per shape: raw Restarted, RestartCount 1 / 2 / 1,
        /// BrokenCount 0, LinkBreakCount 0, ChainBroken False.
        /// </para>
        /// </summary>
        [Theory]
        // append a copy of entry 1 at the tail
        [InlineData(1, 6, 1)]
        // insert a copy of entry 1 after entry 3
        [InlineData(1, 4, 1)]
        // append a copy of entry 4 at the tail
        [InlineData(4, 6, 1)]
        public void AReplayedEntry_IsTampering_NotARestart(int copyFrom, int insertAt, int expectedDuplicates)
        {
            if (!OperatingSystem.IsWindows()) return;
            var dir = NewDir();

            using (var writer = Open(dir))
            {
                for (int i = 0; i < 6; i++) writer.LogConnectionAttempt($"srv{i}", success: true);
                writer.Flush();
            }

            var segment = LatestSegment(dir);
            var lines = Lines(segment);
            Assert.Equal(6, lines.Count);

            // Whole lines the production writer produced, moved. Nothing is composed or re-signed.
            lines.Insert(insertAt, lines[copyFrom]);
            File.WriteAllLines(segment, lines);

            using var verifier = Open(dir);
            var result = verifier.VerifyChain("test");

            Assert.Equal(expectedDuplicates, result.DuplicateEntryCount);
            Assert.Equal(AuditLogService.ChainVerificationStatus.Broken, result.Status);
            Assert.Equal("BROKEN", result.StatusLabel);
            Assert.False(result.Intact);

            // The duplicate is NAMED, and the exculpation the restart arm carries is absent.
            Assert.NotNull(result.FirstDuplicateRecord);
            var detail = result.StatusDetail;
            Assert.Contains($"Duplicated entries found: {expectedDuplicates}", detail);
            Assert.Contains("present more than once", detail);
            Assert.DoesNotContain("do not file this as tampering", detail);
            Assert.Contains("Treat as tampering until proven otherwise", detail);

            // The closed sum still closes: a replayed record IS an entry and it DID verify against
            // its own key, so it is not moved into a failure bucket to make the verdict work.
            Assert.Equal(result.EntryCount,
                result.VerifiedCount + result.BrokenCount + result.UnverifiableCount + result.IndeterminateCount);

            // The folded, client-facing verdict says the same thing, and the on-screen banner is
            // the red one rather than the amber restart panel.
            var statement = verifier.DescribeChainForCompliance("test");
            Assert.Equal("BROKEN", statement.StatusWord);
            Assert.True(verifier.ChainBroken);
            Assert.False(verifier.ChainRestarted);
        }

        /// <summary>
        /// The unchanged chain, run through the same fixture, so the replay test above is not
        /// passing because everything is BROKEN.
        /// </summary>
        [Fact]
        public void AnUntouchedChain_HasNoDuplicates_AndStaysIntact()
        {
            if (!OperatingSystem.IsWindows()) return;
            var dir = NewDir();

            using (var writer = Open(dir))
            {
                for (int i = 0; i < 6; i++) writer.LogConnectionAttempt($"srv{i}", success: true);
                writer.Flush();
            }

            using var verifier = Open(dir);
            var result = verifier.VerifyChain("test");

            Assert.Equal(0, result.DuplicateEntryCount);
            Assert.Null(result.FirstDuplicateRecord);
            Assert.Equal(AuditLogService.ChainVerificationStatus.Intact, result.Status);
            Assert.Equal(string.Empty, result.ReplayClause);
        }

        /// <summary>
        /// The SEALED, SHA-256-anchored document an auditor is handed must not print BROKEN over a
        /// chain whose own status detail two lines below says not to file it as tampering.
        /// <para>
        /// RED before this build: <c>ComplianceChainStatement.StatusWord</c> was its own fourth-arm
        /// copy of the verdict switch, so a Restarted statement fell through to BROKEN. Measured on
        /// this fixture: <c>Status=Restarted Label=Restarted StatusWord=BROKEN</c>, and the exported
        /// report read "Chain status:     BROKEN" above "do not file this as tampering".
        /// </para>
        /// </summary>
        [Fact]
        public void TheSealedReport_PrintsTheRestartVerdict_NotTheTamperWord()
        {
            if (!OperatingSystem.IsWindows()) return;
            var dir = NewDir();
            PlantMidSegmentRestart(dir);

            using var verifier = Open(dir);
            var statement = verifier.DescribeChainForCompliance("test");

            Assert.Equal(AuditLogService.ChainVerificationStatus.Restarted, statement.Status);
            Assert.Equal("Restarted", statement.Label);
            Assert.Equal("RESTARTED", statement.StatusWord);

            var report = verifier.ComposeChainVerificationReport(statement, "test");
            Assert.Contains("Chain status:     RESTARTED", report);
            Assert.DoesNotContain("Chain status:     BROKEN", report);
            // The verdict word and the sentence beneath it now agree, which is the whole point.
            Assert.Contains("do not file this as tampering", report);
            // And the report states the duplicate count it measured, at zero.
            Assert.Contains("Duplicated:       0", report);
        }

        /// <summary>
        /// No surface may render a non-tamper status as a tamper verdict. The audit page's own
        /// switches default to the error colour and the triangle, and the on-chain record's
        /// severity switch defaults to Critical, so a new status is silently filed as tampering
        /// unless every switch names it.
        /// </summary>
        /// <remarks>
        /// WHAT THIS USED TO BE, and why it was worth replacing (2026-08-11 verification). The
        /// previous version was six <c>Assert.Contains</c> calls over the source text of two files,
        /// under a name and a doc-comment that both claimed to enumerate every surface. It did not
        /// enumerate anything: a hand-written list of substrings cannot notice the arm nobody
        /// thought to add to it. It stayed green while
        /// <c>ComplianceChainStatement.StatusWord</c> — in the very file it read, 130 lines below a
        /// string it asserted — fell through to BROKEN on a Restarted chain, and while the bundle
        /// stylesheet had no rule for the class that verdict emits.
        /// <para>
        /// This version DRIVES the two verdict-word surfaces once per <c>ChainVerificationStatus</c>
        /// member, so a member added tomorrow is covered without anyone editing this test. The two
        /// Razor switches are still a source-text scan (a switch inside markup is not callable from
        /// a test), and that half is stated as a scan rather than as a render: it is enumerated over
        /// the enum's members instead of over a list, which is the property that was missing. The
        /// bundle stylesheet is covered by its own enumerated test beside the artifact it styles,
        /// <c>ReportBundleServiceTests.AuditEvidenceHtml_StylesEveryChainVerdict_NotJustTheCleanOne</c>.
        /// </para>
        /// </remarks>
        [Fact]
        public void EverySurface_NamesEveryChainStatus_RatherThanFallingThroughToBroken()
        {
            var markup = File.ReadAllText(FindRepoFile("Pages/AuditLogViewer.razor"));
            var service = File.ReadAllText(FindRepoFile("Data/AuditLogService.cs"));
            var tamperWord = AuditLogService.StatusWordFor(AuditLogService.ChainVerificationStatus.Broken);

            foreach (AuditLogService.ChainVerificationStatus status in
                     Enum.GetValues<AuditLogService.ChainVerificationStatus>())
            {
                var word = AuditLogService.StatusWordFor(status);
                var label = AuditLogService.StatusLabelFor(status);

                // EXERCISED, not scanned: the raw result and the folded compliance statement are
                // each built with this status and asked for their verdict word.
                var result = new AuditLogService.ChainVerificationResult(
                    Intact: status == AuditLogService.ChainVerificationStatus.Intact,
                    EntryCount: 1, FirstEntry: null, LastEntry: null, VerifiedAt: DateTime.UtcNow,
                    Status: status);
                var statement = new AuditLogService.ComplianceChainStatement(
                    status, label, "text", 1, "detail", result);

                Assert.Equal(word, result.StatusLabel);
                Assert.Equal(word, statement.StatusWord);
                Assert.False(string.IsNullOrWhiteSpace(label), $"{status} has no label.");

                // EXERCISED: the severity the chain's own record carries for this status. The
                // switch behind it defaults to Critical, so a status it does not name is filed as
                // tampering by omission.
                var severity = AuditLogService.SeverityFor(status);

                if (status != AuditLogService.ChainVerificationStatus.Broken)
                {
                    Assert.NotEqual(tamperWord, word);
                    Assert.NotEqual(tamperWord, result.StatusLabel);
                    Assert.NotEqual(tamperWord, statement.StatusWord);
                    Assert.NotEqual(AuditSeverity.Critical, severity);
                }

                // The two Razor switches default to the error colour and the warning triangle,
                // which together are the tamper presentation. Every status the service itself files
                // BELOW Error must therefore be named in both, and that set is DERIVED from the
                // severity above rather than written down here: a benign status added tomorrow
                // requires its arms without anyone editing this list. Indeterminate is deliberately
                // absent from the colour switch (it takes the error colour on purpose, see the
                // comment there) and Broken is the default, so neither is asserted.
                //
                // Stated as what it is: a source-text scan. A switch inside markup is not callable
                // from a test, and no browser rendered these panels in this run.
                if (severity is AuditSeverity.Info or AuditSeverity.Warning)
                {
                    Assert.Contains($"ChainVerificationStatus.{status} => \"", markup);
                    Assert.Equal(2, CountOccurrences(markup, $"ChainVerificationStatus.{status} => \""));
                }
            }
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int n = 0, at = 0;
            while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { n++; at += needle.Length; }
            return n;
        }

        private static string FindRepoFile(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new FileNotFoundException($"Could not locate {relative} above {AppContext.BaseDirectory}");
        }

        // ── FIX 3: the key replacement lands on the chain, naming the transition ──

        /// <summary>
        /// Makes the key file unreadable the way a service-account change did on the live box:
        /// bytes that are neither a DPAPI blob this identity can open nor a raw 32-byte legacy key.
        /// </summary>
        private static void MakeKeyUnreadable(string dir)
        {
            var keyPath = Path.Combine(dir, "hmac.key");
            File.SetAttributes(keyPath, FileAttributes.Normal);
            File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(50));
        }

        [Fact]
        public void KeyReplacement_WritesAnOnChainRecordNamingBothKeysAndBothIdentities()
        {
            if (!OperatingSystem.IsWindows()) return;
            var dir = NewDir();

            using (var first = Open(dir))
            {
                first.LogApplicationStart();
                first.Flush();
            }

            var oldKeyIdOnChain = Parse(Lines(LatestSegment(dir))[0]).KeyId;
            Assert.False(string.IsNullOrEmpty(oldKeyIdOnChain));

            // The sidecar written by the first service names the key it describes, so the
            // replacement has a recorded predecessor to carry forward.
            var metaBefore = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "hmac.key.meta")));
            var recordedOldKeyId = metaBefore.RootElement.GetProperty("KeyId").GetString();
            Assert.Equal(oldKeyIdOnChain, recordedOldKeyId);

            MakeKeyUnreadable(dir);

            string newKeyId;
            using (var second = Open(dir))
            {
                second.Flush();
                newKeyId = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "hmac.key.meta")))
                                       .RootElement.GetProperty("KeyId").GetString()!;
            }

            var replacement = Segments(dir)
                .SelectMany(Lines)
                .Select(Parse)
                .Single(e => e.EventType == AuditEventType.HmacKeyReplacedUnreadable);

            Assert.Equal(newKeyId, replacement.Details!["NewKeyId"]);
            Assert.Equal(oldKeyIdOnChain, replacement.Details["OldKeyIdFromSidecar"]);
            Assert.Equal(oldKeyIdOnChain, replacement.Details["OldKeyIdOnChain"]);
            Assert.False(string.IsNullOrWhiteSpace(replacement.Details["OldKeyWrittenBy"]));
            Assert.False(string.IsNullOrWhiteSpace(replacement.Details["ReplacedByIdentity"]));
            Assert.False(string.IsNullOrWhiteSpace(replacement.Details["Reason"]));
            Assert.False(string.IsNullOrWhiteSpace(replacement.Details["ReplacedAt"]));

            // Signed with the NEW key: the old one is unusable by definition.
            Assert.Equal(newKeyId, replacement.KeyId);

            using var verifier = Open(dir);
            Assert.True(verifier.HasVerifiedKeyReplacementRecord(oldKeyIdOnChain, newKeyId),
                "A verified replacement record must be found for the transition it names.");
            // The lookup is keyed on the transition, not on the mere presence of a record.
            Assert.False(verifier.HasVerifiedKeyReplacementRecord("DEADBEEFDEADBEEF", newKeyId));
            Assert.False(verifier.HasVerifiedKeyReplacementRecord(oldKeyIdOnChain, "DEADBEEFDEADBEEF"));
        }

        /// <summary>
        /// MUTATION: an unsigned edit to the replacement record's key ids must stop it counting as
        /// the explanation. The record is only worth reading because its signature covers it.
        /// </summary>
        [Fact]
        public void AnEditedKeyReplacementRecord_NoLongerExplainsTheTransition()
        {
            if (!OperatingSystem.IsWindows()) return;
            var dir = NewDir();

            using (var first = Open(dir)) { first.LogApplicationStart(); first.Flush(); }
            var oldKeyId = Parse(Lines(LatestSegment(dir))[0]).KeyId!;
            MakeKeyUnreadable(dir);
            string newKeyId;
            using (var second = Open(dir))
            {
                second.Flush();
                newKeyId = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "hmac.key.meta")))
                                       .RootElement.GetProperty("KeyId").GetString()!;
            }

            using (var check = Open(dir))
                Assert.True(check.HasVerifiedKeyReplacementRecord(oldKeyId, newKeyId));

            // Rewrite the reason, leaving the signature as it was.
            var segment = Segments(dir).Single(s => Lines(s).Select(Parse)
                .Any(e => e.EventType == AuditEventType.HmacKeyReplacedUnreadable));
            var lines = Lines(segment);
            for (int i = 0; i < lines.Count; i++)
            {
                var e = Parse(lines[i]);
                if (e.EventType != AuditEventType.HmacKeyReplacedUnreadable) continue;
                e.Details!["Reason"] = "routine maintenance";
                lines[i] = JsonSerializer.Serialize(e, Json);
            }
            File.WriteAllLines(segment, lines);

            using var after = Open(dir);
            Assert.False(after.HasVerifiedKeyReplacementRecord(oldKeyId, newKeyId),
                "An edited record must not be accepted as the chain's own account of the transition.");
        }

        // ── FIX 4: the sidecar tells the truth ───────────────────────────────────

        /// <summary>
        /// The live D5 shape, rebuilt: a sidecar describing a key that is not the one signing
        /// entries. The service must say so and compute nothing from it, rather than reporting an
        /// age and a rotation-due date that belong to a destroyed key.
        /// </summary>
        [Fact]
        public void ASidecarDescribingAnotherKey_IsReportedRatherThanUsed()
        {
            if (!OperatingSystem.IsWindows()) return;
            var dir = NewDir();
            using (var svc = Open(dir)) { svc.LogApplicationStart(); svc.Flush(); }

            var metaPath = Path.Combine(dir, "hmac.key.meta");
            var stale = DateTime.UtcNow.AddDays(-400);
            File.WriteAllText(metaPath,
                "{\"CreatedAt\":\"" + stale.ToString("o") + "\",\"RotationDueAt\":\"" +
                stale.AddDays(365).ToString("o") + "\",\"KeyId\":\"70C26C79FC03E942\"}");

            using var svc2 = Open(dir);
            var (basis, sidecarKeyId, _) = svc2.DescribeHmacKeyAgeBasis(365);

            Assert.Equal(AuditLogService.HmacKeyAgeBasis.NamesAnotherKey, basis);
            Assert.Equal("70C26C79FC03E942", sidecarKeyId);
        }

        /// <summary>
        /// A sidecar written before this build names no key. Its age is real but unattributed, and
        /// it must be reported as unattributed rather than as the age of the key in use.
        /// </summary>
        [Fact]
        public void ASidecarNamingNoKey_IsReportedAsUnattributed()
        {
            if (!OperatingSystem.IsWindows()) return;
            var dir = NewDir();
            using (var svc = Open(dir)) { svc.LogApplicationStart(); svc.Flush(); }

            var metaPath = Path.Combine(dir, "hmac.key.meta");
            var stale = DateTime.UtcNow.AddDays(-400);
            File.WriteAllText(metaPath,
                "{\"CreatedAt\":\"" + stale.ToString("o") + "\",\"RotationDueAt\":\"" +
                stale.AddDays(365).ToString("o") + "\"}");

            using var svc2 = Open(dir);
            var (basis, sidecarKeyId, ageDays) = svc2.DescribeHmacKeyAgeBasis(365);

            Assert.Equal(AuditLogService.HmacKeyAgeBasis.NamesNoKey, basis);
            Assert.Equal(string.Empty, sidecarKeyId);
            // EXACT, not a floor. Found by replaying the installed service's own chain: the stamp
            // was parsed to a LOCAL DateTime and subtracted from DateTime.UtcNow, so every age was
            // short by this machine's UTC offset (the live 10-day record read as 9 at +12:00). A
            // ">= 399" assertion passes on the defect; this one does not.
            Assert.Equal(400, ageDays);

            // And the on-chain notice says which of the two it is, rather than asserting an age of
            // a key it cannot attribute one to.
            svc2.LogHmacKeyAgeExceeded(ageDays, 365, "AAAABBBBCCCCDDDD", ageAttributedToKeyInUse: false);
            svc2.Flush();
            var notice = Segments(dir).SelectMany(Lines).Select(Parse)
                .Last(e => e.EventType == AuditEventType.HmacKeyAgeExceeded);
            Assert.Contains("does not name the key it describes", notice.Message);
            Assert.Equal("False", notice.Details!["AgeAttributedToKeyInUse"]);
        }

        /// <summary>
        /// Every path that writes key material writes the sidecar in the same operation, and the
        /// sidecar names the key it describes. Checked on all three: first mint, replacement of an
        /// unreadable key, and a planned rotation.
        /// </summary>
        [Fact]
        public void EveryKeyWritingPath_LeavesASidecarNamingTheKeyInUse()
        {
            if (!OperatingSystem.IsWindows()) return;
            var dir = NewDir();
            var metaPath = Path.Combine(dir, "hmac.key.meta");

            string mintedKeyId;
            using (var svc = Open(dir))
            {
                svc.LogApplicationStart();
                svc.Flush();
                mintedKeyId = Parse(Lines(LatestSegment(dir))[0]).KeyId!;
                Assert.Equal(AuditLogService.HmacKeyAgeBasis.NamesTheKeyInUse,
                             svc.DescribeHmacKeyAgeBasis(365).Basis);
            }
            Assert.Equal(mintedKeyId, JsonDocument.Parse(File.ReadAllText(metaPath))
                                                  .RootElement.GetProperty("KeyId").GetString());

            MakeKeyUnreadable(dir);
            string replacementKeyId;
            using (var svc2 = Open(dir))
            {
                svc2.Flush();
                Assert.Equal(AuditLogService.HmacKeyAgeBasis.NamesTheKeyInUse,
                             svc2.DescribeHmacKeyAgeBasis(365).Basis);
                var meta = JsonDocument.Parse(File.ReadAllText(metaPath)).RootElement;
                replacementKeyId = meta.GetProperty("KeyId").GetString()!;
                Assert.NotEqual(mintedKeyId, replacementKeyId);
                // The transition is IN the record, not reconstructed from file timestamps later.
                Assert.Equal(mintedKeyId, meta.GetProperty("PreviousKeyId").GetString());
                Assert.False(string.IsNullOrWhiteSpace(meta.GetProperty("PreviousWrittenBy").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(meta.GetProperty("ReplacedAt").GetString()));
            }

            using (var svc3 = Open(dir))
            {
                svc3.RotateHmacKey("test-actor");
                svc3.Flush();
                Assert.Equal(AuditLogService.HmacKeyAgeBasis.NamesTheKeyInUse,
                             svc3.DescribeHmacKeyAgeBasis(365).Basis);
                var meta = JsonDocument.Parse(File.ReadAllText(metaPath)).RootElement;
                Assert.Equal(replacementKeyId, meta.GetProperty("PreviousKeyId").GetString());
                Assert.NotEqual(replacementKeyId, meta.GetProperty("KeyId").GetString());
            }
        }
    }
}
