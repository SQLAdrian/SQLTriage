/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// 2026-08-02, ROUND 8. THE COMBINATION, not the surface.
    /// <para>
    /// <c>Pages/AuditLogViewer.razor</c> renders the three startup findings as three INDEPENDENT
    /// <c>@if</c> blocks, so more than one can render at once. Seven rounds enumerated surfaces;
    /// none enumerated combinations of flags. Two sentences that are true when their own flag is
    /// the only one set are FALSE when another is set beside them, and both were live at 9825fb5:
    /// </para>
    /// <list type="number">
    /// <item>the amber UNVERIFIABLE banner's "No entry with an available signing key was found to
    /// have been altered", printed beneath the red break alert on a chain that had a tampered entry
    /// whose key resolved fine;</item>
    /// <item>the INDETERMINATE banner's "Whether the chain was altered can be neither confirmed nor
    /// ruled out", printed on a launch whose own scan confirmed the alteration.</item>
    /// </list>
    /// <para>
    /// Both are chain-wide NEGATIVES. The per-finding facts beside them (which key id went dark,
    /// which record failed first, what the anchor could not tell us) stay — they are true whatever
    /// else was found. What could not survive a break is the negative.
    /// </para>
    /// </summary>
    public class AuditLogStartupBannerTests : IDisposable
    {
        private readonly List<string> _dirs = new();
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

        /// <summary>
        /// Every chain-wide sentence a banner can print that is only true when its own finding is
        /// the ONLY one. If any of these appears beside a break it is false on those bytes,
        /// whichever banner carried it and whichever END of the banner carried it.
        /// </summary>
        /// <remarks>
        /// ROUND 9 (2026-08-02) added the third entry and moved this list out of a single test
        /// method. Round 8 composed the banners' CLOSINGS and checked exactly this list against
        /// <c>ClosingClaim</c> — so the same defect went on living in the OPENING one sentence
        /// higher up: "Audit chain integrity cannot be determined" was a markup literal gated only
        /// on <c>ChainProvenanceIndeterminate</c>, and it is false on the two constructed cells
        /// where a break was found on the same launch (B and I; B, I and T). It is also the
        /// QUOTABLE half — the sentence an operator reads first and repeats to an auditor. The list
        /// is now checked against <see cref="AuditLogService.StartupChainBanner.AllClaims"/>,
        /// opening and closing together, so it cannot be satisfied by moving a sentence.
        /// </remarks>
        private static readonly string[] ChainWideNegatives =
        {
            "was found to have been altered",
            "Whether the chain was altered can be neither confirmed nor ruled out",
            "not evidence of tampering",
            "Audit chain integrity cannot be determined"
        };

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
        }

        // ── Fixtures ─────────────────────────────────────────────────────

        private string NewDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "audit-banner-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            _dirs.Add(dir);
            return dir;
        }

        private static AuditLogService Open(string dir) => new(dir, startFlushTimer: false);

        private static string LatestLog(string dir) =>
            Directory.GetFiles(dir, "audit-*.jsonl")
                     .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).First();

        /// <summary>N entries, flushed — which also establishes the anchor and its key ledger.</summary>
        private static string Seed(string dir, int entries = 3)
        {
            using var svc = Open(dir);
            for (var i = 0; i < entries; i++) svc.LogConnectionAttempt($"srv{i}", success: true);
            svc.Flush();
            return LatestLog(dir);
        }

        private static string RewriteJson(string line, string field, string jsonValue)
        {
            var needle = "\"" + field + "\":";
            var start = line.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(start >= 0, $"Field {field} not present in the audit line.");
            var valueStart = start + needle.Length;
            Assert.Equal('"', line[valueStart]);
            var valueEnd = valueStart + 1;
            while (line[valueEnd] != '"' || line[valueEnd - 1] == '\\') valueEnd++;
            return line[..valueStart] + jsonValue + line[(valueEnd + 1)..];
        }

        private static byte[] HmacEntropy() =>
            (byte[])typeof(AuditLogService)
                .GetField("HmacKeyEntropy", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null)!;

        /// <summary>A real, well-formed DPAPI blob this box cannot unwrap — what an orphaned
        /// key or anchor looks like on disk after an identity change.</summary>
        private static byte[] ForeignBlob(string payload) =>
            System.Security.Cryptography.ProtectedData.Protect(
                Encoding.UTF8.GetBytes(payload),
                Encoding.UTF8.GetBytes("a-different-entropy"),
                System.Security.Cryptography.DataProtectionScope.LocalMachine);

        private static void Overwrite(string path, byte[] bytes)
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.WriteAllBytes(path, bytes);
        }

        /// <summary>The shipped page, with Razor comments removed — a comment never renders, so
        /// only what survives this strip can appear on an operator's screen.</summary>
        private static string RenderableMarkup()
        {
            var raw = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Markup", "AuditLogViewer.razor"));
            var sb = new StringBuilder();
            var i = 0;
            while (i < raw.Length)
            {
                var open = raw.IndexOf("@*", i, StringComparison.Ordinal);
                if (open < 0) { sb.Append(raw, i, raw.Length - i); break; }
                sb.Append(raw, i, open - i);
                var close = raw.IndexOf("*@", open + 2, StringComparison.Ordinal);
                if (close < 0) break;
                i = close + 2;
            }
            return sb.ToString();
        }

        /// <summary>
        /// <see cref="RenderableMarkup"/> with HTML tags removed and whitespace collapsed — what an
        /// operator actually READS. Needed because the sentences at issue are broken up by markup:
        /// the headline this round fixes was written <c>Audit chain integrity &lt;strong&gt;cannot
        /// be determined&lt;/strong&gt;</c>, so a search of the raw markup for the sentence a person
        /// sees finds nothing and a "the page no longer says this" assertion would pass vacuously.
        /// </summary>
        private static string ReadableText()
        {
            var markup = RenderableMarkup();
            var sb = new StringBuilder(markup.Length);
            var inTag = false;
            foreach (var ch in markup)
            {
                if (ch == '<') { inTag = true; sb.Append(' '); continue; }
                if (ch == '>') { inTag = false; sb.Append(' '); continue; }
                if (!inTag) sb.Append(char.IsWhiteSpace(ch) ? ' ' : ch);
            }
            return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        // ── The constructions ────────────────────────────────────────────

        /// <summary>Entry 0's KeyId is rewritten to an id nothing here resolves, and a
        /// real-but-unreadable DPAPI archive is planted for it — byte-for-byte what genuine key
        /// loss leaves behind — so its run classifies as key loss, i.e. UNVERIFIABLE.</summary>
        private static void PlantBenignKeyGap(string dir, string logFile)
        {
            var lines = File.ReadAllLines(logFile);
            lines[0] = RewriteJson(lines[0], "KeyId", "\"CAFEBABECAFEBABE\"");
            File.WriteAllLines(logFile, lines);
            File.WriteAllBytes(Path.Combine(dir, "hmac.key.CAFEBABECAFEBABE"),
                System.Security.Cryptography.ProtectedData.Protect(
                    new byte[32], Encoding.UTF8.GetBytes("a-different-entropy"),
                    System.Security.Cryptography.DataProtectionScope.LocalMachine));
        }

        /// <summary>Same head-of-chain rewrite, with NO archive planted — with the ledger gone
        /// the classifier can neither corroborate nor refute it, which is INDETERMINATE.</summary>
        private static void PlantUncorroboratedKeyGap(string logFile)
        {
            var lines = File.ReadAllLines(logFile);
            lines[0] = RewriteJson(lines[0], "KeyId", "\"CAFEBABECAFEBABE\"");
            File.WriteAllLines(logFile, lines);
        }

        private static void TamperMessage(string logFile, int index)
        {
            var lines = File.ReadAllLines(logFile);
            Assert.True(lines.Length > index);
            lines[index] = RewriteJson(lines[index], "Message", "\"TAMPERED\"");
            File.WriteAllLines(logFile, lines);
        }

        /// <summary>Deletes the anchored tail line — the surviving prefix re-verifies clean, so the
        /// out-of-band anchor is the only witness that anything was removed.</summary>
        private static void TruncateTail(string logFile)
        {
            var lines = File.ReadAllLines(logFile);
            Assert.True(lines.Length >= 2);
            File.WriteAllLines(logFile, lines.Take(lines.Length - 1));
        }

        /// <summary>Deletes the anchor but KEEPS its regime marker: the anchor regime was active and
        /// the log still has entries, which is the deletion shape, not a first run. It also leaves
        /// the key-provenance ledger unavailable.</summary>
        private static void DeleteAnchorKeepRegime(string dir)
        {
            var anchor = Path.Combine(dir, ".chain-anchor");
            Assert.True(File.Exists(anchor));
            File.SetAttributes(anchor, FileAttributes.Normal);
            File.Delete(anchor);
            Assert.True(File.Exists(Path.Combine(dir, ".chain-anchor.regime")));
        }

        private static void OrphanAnchor(string dir) =>
            Overwrite(Path.Combine(dir, ".chain-anchor"), ForeignBlob("{\"Sig\":\"x\",\"Ts\":\"x\"}"));

        // ── The two reported defects ─────────────────────────────────────

        /// <summary>
        /// DEFECT 1 (BLOCKING). Benign key loss BESIDE a real tamper. Measured:
        /// <c>ChainBroken=True ChainUnverifiable=True ChainProvenanceIndeterminate=False</c> on one
        /// launch, both banners rendering, the amber one exculpating the chain.
        /// <para>
        /// RED at 9825fb5. The flag assertions pass there (that is the reachability half); the
        /// markup assertion fails, because at 9825fb5 the sentence is an ungated literal in the
        /// page — verified independently by stripping Razor comments from
        /// <c>git show 9825fb5:Pages/AuditLogViewer.razor</c>, which still contains it once.
        /// </para>
        /// </summary>
        [Fact]
        public void UnverifiableBanner_DoesNotExculpate_WhenABreakWasAlsoFound()
        {
            if (!OperatingSystem.IsWindows()) return;

            var dir = NewDir();
            var log = Seed(dir);
            PlantBenignKeyGap(dir, log);
            TamperMessage(log, 2);

            using var svc = Open(dir);
            Assert.True(svc.ChainBroken);
            Assert.True(svc.ChainUnverifiable);
            Assert.False(svc.ChainProvenanceIndeterminate);

            // The page has no other source for the sentence…
            Assert.DoesNotContain(
                "No entry with an available signing key was found to have been altered",
                RenderableMarkup());

            // …and the only source it has refuses to produce it on these bytes.
            var claim = svc.StartupBannerFor(AuditLogService.StartupChainFinding.UnverifiableGap)!.ClosingClaim;
            Assert.DoesNotContain("was found to have been altered", claim);
            Assert.Contains("FAILED", claim);
        }

        /// <summary>
        /// DEFECT 2 (BLOCKING). An unreadable anchor BESIDE a real tamper. Measured:
        /// <c>ChainBroken=True ChainProvenanceIndeterminate=True</c>, and the banner denied what the
        /// same scan had just confirmed.
        /// <para>RED at 9825fb5, exactly as above.</para>
        /// </summary>
        [Fact]
        public void IndeterminateBanner_DoesNotDenyTheBreak_WhenABreakWasAlsoFound()
        {
            if (!OperatingSystem.IsWindows()) return;

            var dir = NewDir();
            var log = Seed(dir);
            TamperMessage(log, 1);
            OrphanAnchor(dir);

            using var svc = Open(dir);
            Assert.True(svc.ChainBroken);
            Assert.True(svc.ChainProvenanceIndeterminate);
            Assert.False(svc.ChainUnverifiable);

            Assert.DoesNotContain(
                "Whether the chain was altered can be neither confirmed nor ruled out",
                RenderableMarkup());

            var claim = svc.StartupBannerFor(AuditLogService.StartupChainFinding.ProvenanceIndeterminate)!.ClosingClaim;
            Assert.DoesNotContain("Whether the chain was altered can be neither confirmed nor ruled out", claim);
            Assert.DoesNotContain("not evidence of tampering", claim);
            Assert.Contains("FAILED", claim);
        }

        /// <summary>
        /// THE OVER-CORRECTION GUARD. With no break, both sentences are the honest ones and must
        /// still be printed — the finding-scoped statements are what make an evidence gap readable,
        /// and deleting them would trade a false exculpation for a silent one.
        /// </summary>
        [Fact]
        public void ChainWideNegatives_SurviveWhenTheirFindingIsTheOnlyOne()
        {
            if (!OperatingSystem.IsWindows()) return;

            var gapDir = NewDir();
            PlantBenignKeyGap(gapDir, Seed(gapDir));
            using (var gap = Open(gapDir))
            {
                Assert.False(gap.ChainBroken);
                Assert.True(gap.ChainUnverifiable);
                Assert.Contains(
                    "No entry with an available signing key was found to have been altered",
                    gap.StartupBannerFor(AuditLogService.StartupChainFinding.UnverifiableGap)!.ClosingClaim);
            }

            var anchorDir = NewDir();
            Seed(anchorDir);
            OrphanAnchor(anchorDir);
            using var orphaned = Open(anchorDir);
            Assert.False(orphaned.ChainBroken);
            Assert.True(orphaned.ChainProvenanceIndeterminate);
            var alone = orphaned.StartupBannerFor(AuditLogService.StartupChainFinding.ProvenanceIndeterminate)!;
            Assert.Contains(
                "Whether the chain was altered can be neither confirmed nor ruled out",
                alone.ClosingClaim);

            // ROUND 9: the HEADLINE too. With nothing else found, integrity really is undetermined
            // and the banner must still say so — narrowing it to "provenance" on a launch where
            // provenance is the ONLY thing in doubt would understate the state, which is the same
            // failure in the other direction.
            Assert.Equal("Audit chain integrity cannot be determined.", alone.Headline);
        }

        /// <summary>
        /// ROUND 9, BLOCKING. The INDETERMINATE banner's HEADLINE is the round-8 defect surviving as
        /// the banner's opening instead of its closing — and it is the quotable one. Measured, one
        /// launch, one set of bytes: an ordinary tamper plus an orphaned anchor stacks
        /// <c>Audit chain integrity check failed. First failing record: …</c> directly above
        /// <c>Audit chain integrity cannot be determined.</c> The second sentence is false: this
        /// launch DID determine the integrity, and the sentence above it says what it found.
        /// </summary>
        [Fact]
        public void IndeterminateHeadline_DoesNotSayUndetermined_WhenTheSameLaunchDeterminedABreak()
        {
            if (!OperatingSystem.IsWindows()) return;

            var dir = NewDir();
            var log = Seed(dir);
            TamperMessage(log, 1);
            OrphanAnchor(dir);

            using var svc = Open(dir);
            Assert.True(svc.ChainBroken);
            Assert.True(svc.ChainProvenanceIndeterminate);

            var banner = svc.StartupBannerFor(AuditLogService.StartupChainFinding.ProvenanceIndeterminate)!;
            Assert.DoesNotContain("Audit chain integrity cannot be determined", banner.AllClaims);
            Assert.Equal("Audit chain provenance cannot be determined.", banner.Headline);

            // The break banner beside it still states the finding that makes the above true.
            Assert.Equal(
                "Audit chain integrity check failed.",
                svc.StartupBannerFor(AuditLogService.StartupChainFinding.Break)!.Headline);

            // And the page has no second source for either sentence.
            Assert.DoesNotContain("Audit chain integrity cannot be determined", ReadableText());
        }

        /// <summary>
        /// ROUND 9, the second reachable cell for the same headline: a DELETED anchor (which takes
        /// the key-provenance ledger with it) beside an uncorroborated head-of-chain key rewrite,
        /// which is <c>Broken + Indeterminate + truncation</c>. The composer sees three booleans, so
        /// this cell must be checked and not assumed to follow from the one above.
        /// </summary>
        [Fact]
        public void IndeterminateHeadline_IsScopedOnTheTruncationCellToo()
        {
            if (!OperatingSystem.IsWindows()) return;

            var dir = NewDir();
            PlantUncorroboratedKeyGap(Seed(dir));
            DeleteAnchorKeepRegime(dir);

            using var svc = Open(dir);
            Assert.True(svc.ChainBroken);
            Assert.True(svc.ChainProvenanceIndeterminate);
            Assert.NotNull(svc.ChainTruncationDetail);

            var banner = svc.StartupBannerFor(AuditLogService.StartupChainFinding.ProvenanceIndeterminate)!;
            Assert.Equal("Audit chain provenance cannot be determined.", banner.Headline);
            // It says nothing about COMPLETENESS: on this cell completeness WAS determined — the
            // anchor's regime marker survived the deletion and truncation was detected.
            Assert.DoesNotContain("complete", banner.Headline, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// NON-BLOCKING (item 3). The break's first failing record is the identifier an operator
        /// quotes to an auditor. It was interpolated with the CURRENT CULTURE's default DateTime
        /// format — on this box "1/08/2026 1:51:44 pm_BVAGWnBUVG+KBAa1" — while every other record
        /// id on the same page is round-trip ISO-8601. Ambiguous (d/M vs M/d) and locale-dependent.
        /// </summary>
        [Fact]
        public void BreakRecordId_IsIso8601_LikeEveryOtherRecordIdOnThePage()
        {
            if (!OperatingSystem.IsWindows()) return;

            var dir = NewDir();
            TamperMessage(Seed(dir), 1);

            using var svc = Open(dir);
            Assert.True(svc.ChainBroken);
            var id = svc.ChainBreakFirstRecordId!;

            var stamp = id[..id.LastIndexOf('_')];
            Assert.True(
                DateTime.TryParseExact(stamp, "o", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out _),
                $"Break record id is not ISO-8601 round-trip: '{id}'");
            // The culture-formatted shape this replaced: "1/08/2026 1:51:44 pm_…". The suffix is
            // base64 and may legitimately contain '/', so both checks are scoped to the stamp.
            Assert.DoesNotContain("/", stamp);
            Assert.DoesNotContain(" ", stamp);
        }

        /// <summary>
        /// NON-BLOCKING (item 4). <see cref="AuditLogService.ChainIndeterminateDetail"/> is authored
        /// lower-case because <c>FoldWithAnchorFindings</c> splices it after "In addition, ". The
        /// page prints it as a sentence of its own, where it began lower-case mid-banner. Both
        /// readers now take the form they need — and the page must be the one taking the sentence.
        /// </summary>
        [Fact]
        public void IndeterminateDetail_ReadsAsASentenceOnThePage_AndStaysSplicableInTheBundle()
        {
            if (!OperatingSystem.IsWindows()) return;

            var dir = NewDir();
            Seed(dir);
            OrphanAnchor(dir);

            using var svc = Open(dir);
            Assert.True(svc.ChainProvenanceIndeterminate);

            var stored = svc.ChainIndeterminateDetail!;
            var sentence = svc.ChainIndeterminateDetailSentence!;
            Assert.True(char.IsLower(stored[0]), "the stored form must stay splicable after \"In addition, \".");
            Assert.True(char.IsUpper(sentence[0]));
            Assert.Equal(stored[1..], sentence[1..]);

            Assert.Contains("ChainIndeterminateDetailSentence", RenderableMarkup());
        }

        // ── The cross-product ────────────────────────────────────────────

        /// <summary>One cell of the ChainBroken × ChainUnverifiable × ChainProvenanceIndeterminate
        /// × truncation table. <see cref="Build"/> is null when the cell is unreachable, and
        /// <see cref="Why"/> then says why.</summary>
        private sealed record Cell(
            bool Broken, bool Unverifiable, bool Indeterminate, bool Truncation,
            string Name, Action<string>? Build, string? Why = null);

        private static IEnumerable<Cell> CrossProduct() => new[]
        {
            // ── reachable, constructed from real bytes ────────────────────
            new Cell(false, false, false, false, "clean chain",
                dir => Seed(dir)),

            new Cell(false, true, false, false, "benign key loss",
                dir => PlantBenignKeyGap(dir, Seed(dir))),

            new Cell(false, false, true, false, "orphaned anchor",
                dir => { Seed(dir); OrphanAnchor(dir); }),

            new Cell(true, false, false, false, "ordinary tamper",
                dir => TamperMessage(Seed(dir), 1)),

            new Cell(true, true, false, false, "tamper + benign key gap",
                dir => { var log = Seed(dir); PlantBenignKeyGap(dir, log); TamperMessage(log, 2); }),

            new Cell(true, false, true, false, "tamper + orphaned anchor",
                dir => { TamperMessage(Seed(dir), 1); OrphanAnchor(dir); }),

            new Cell(true, false, false, true, "truncation",
                dir => TruncateTail(Seed(dir))),

            new Cell(true, true, false, true, "truncation + benign key gap",
                dir => { var log = Seed(dir, 4); PlantBenignKeyGap(dir, log); TruncateTail(log); }),

            // The anchor is DELETED rather than orphaned, so the ledger is gone with it: the
            // head-of-chain rewrite can then be neither corroborated nor refuted (INDETERMINATE),
            // and the regime marker left behind makes the missing anchor a truncation finding.
            new Cell(true, false, true, true, "deleted anchor + uncorroborated key gap",
                dir => { PlantUncorroboratedKeyGap(Seed(dir)); DeleteAnchorKeepRegime(dir); }),

            // ── unreachable ───────────────────────────────────────────────
            new Cell(false, false, false, true, "truncation without a break", null,
                "FlagTruncation is the only writer of ChainTruncationDetail and sets ChainBroken in " +
                "the same two lines, so truncation ALWAYS implies a break."),
            new Cell(false, true, false, true, "truncation without a break", null, "as above"),
            new Cell(false, false, true, true, "truncation without a break", null, "as above"),
            new Cell(false, true, true, true, "truncation without a break", null, "as above"),

            new Cell(false, true, true, false, "benign gap + indeterminate together", null,
                "ChainUnverifiable requires ClassifyUnverifiableRun to return KeyLoss, which is only " +
                "reachable with _keyLedgerAvailable true. BOTH routes to ChainProvenanceIndeterminate " +
                "require it false: the run route tests it directly, and the anchor route only fires " +
                "when ReadChainAnchorOrThrow throws, which is the same read that left the ledger " +
                "unloaded. One predicate, one launch, opposite sides."),
            new Cell(true, true, true, false, "benign gap + indeterminate together", null, "as above"),
            new Cell(true, true, true, true, "benign gap + indeterminate together", null, "as above")
        };

        /// <summary>
        /// THE THING NOBODY ENUMERATED. All sixteen cells of the flag cross-product: every reachable
        /// one is CONSTRUCTED from real bytes, its flags measured off a real startup scan, and its
        /// banners composed and read; every unreachable one carries the reason it cannot be built.
        /// <para>
        /// The rule being enforced is the one that governs this whole lane: a sentence printed on an
        /// artifact must be derived from the same evidence as the verdict beside it. Concretely — a
        /// chain-wide NEGATIVE cannot survive a launch on which a break was found.
        /// </para>
        /// </summary>
        [Fact]
        public void CrossProduct_NoBannerPrintsAFalseSentenceForItsState()
        {
            if (!OperatingSystem.IsWindows()) return;

            var chainWideNegatives = ChainWideNegatives;

            var cells = CrossProduct().ToList();
            Assert.Equal(16, cells.Count);
            Assert.Equal(16, cells.Select(c => (c.Broken, c.Unverifiable, c.Indeterminate, c.Truncation)).Distinct().Count());

            foreach (var cell in cells)
            {
                if (cell.Build == null)
                {
                    Assert.False(string.IsNullOrWhiteSpace(cell.Why),
                        $"[{cell.Name}] an unreachable cell must say why.");
                    continue;
                }

                var dir = NewDir();
                cell.Build(dir);
                using var svc = Open(dir);

                var measured = $"B={svc.ChainBroken} U={svc.ChainUnverifiable} " +
                               $"I={svc.ChainProvenanceIndeterminate} T={svc.ChainTruncationDetail != null}";
                Assert.True(cell.Broken == svc.ChainBroken, $"[{cell.Name}] {measured}");
                Assert.True(cell.Unverifiable == svc.ChainUnverifiable, $"[{cell.Name}] {measured}");
                Assert.True(cell.Indeterminate == svc.ChainProvenanceIndeterminate, $"[{cell.Name}] {measured}");
                Assert.True(cell.Truncation == (svc.ChainTruncationDetail != null), $"[{cell.Name}] {measured}");

                var banners = svc.ComposeStartupChainBanners();
                Assert.Equal(
                    (cell.Broken ? 1 : 0) + (cell.Unverifiable ? 1 : 0) + (cell.Indeterminate ? 1 : 0),
                    banners.Count);

                foreach (var banner in banners)
                {
                    if (!cell.Broken) continue;
                    // ROUND 9: AllClaims, not ClosingClaim. Round 8 asserted this list against the
                    // closing only, and the INDETERMINATE banner's OPENING carried
                    // "Audit chain integrity cannot be determined" straight through both of the
                    // constructed cells below that set Broken beside Indeterminate.
                    foreach (var negative in chainWideNegatives)
                        Assert.DoesNotContain(negative, banner.AllClaims);
                }

                // A banner rendered beside a break must say so rather than merely go quiet: a
                // reader who scrolls past the red alert must not take the amber notice as clean.
                foreach (var banner in banners.Where(b => b.Finding != AuditLogService.StartupChainFinding.Break))
                    Assert.Equal(cell.Broken, banner.ClosingClaim.Contains("FAILED", StringComparison.Ordinal));

                // The composed OPENING of the indeterminate banner is the sentence this round
                // exists for: beside a break it must narrow to what is genuinely open.
                var indeterminate = banners.FirstOrDefault(
                    b => b.Finding == AuditLogService.StartupChainFinding.ProvenanceIndeterminate);
                if (indeterminate != null)
                    Assert.Equal(
                        cell.Broken ? "Audit chain provenance cannot be determined."
                                    : "Audit chain integrity cannot be determined.",
                        indeterminate.Headline);
            }
        }

        /// <summary>
        /// The composer is held to the same rule over the FULL flag space, independently of what the
        /// startup scan can currently reach. Reachability is a property of today's classifier — the
        /// wording must not become false if a later round makes another combination reachable.
        /// </summary>
        [Fact]
        public void Composer_IsSafeForEveryFlagCombination_ReachableOrNot()
        {
            // 2026-08-11: widened from 8 to 16 for the RESTART flag. A fourth finding multiplies the
            // combinations the wording has to survive, and the whole point of this test is that the
            // combinations are enumerated rather than the surfaces.
            for (var mask = 0; mask < 16; mask++)
            {
                var broken = (mask & 1) != 0;
                var indeterminate = (mask & 2) != 0;
                var unverifiable = (mask & 4) != 0;
                var restarted = (mask & 8) != 0;

                var banners = AuditLogService.ComposeStartupChainBanners(broken, indeterminate, unverifiable, restarted);
                Assert.Equal(
                    (broken ? 1 : 0) + (indeterminate ? 1 : 0) + (unverifiable ? 1 : 0) + (restarted ? 1 : 0),
                    banners.Count);

                foreach (var banner in banners)
                {
                    // A restart is amber for the same reason a key gap is: every entry it names
                    // verified against its own signing key, so red would be an accusation.
                    Assert.Equal(
                        banner.Finding is AuditLogService.StartupChainFinding.UnverifiableGap
                                       or AuditLogService.StartupChainFinding.Restart
                            ? "warning" : "error",
                        banner.Severity);

                    // ROUND 9. Which openings the composer owns, stated as an assertion rather than
                    // left to prose: the two chain-wide openings come from here, and the
                    // UnverifiableGap opening deliberately does not — it is scoped to the entries it
                    // names and interleaves their ids, and it is held on the page by
                    // ThePage_KeepsOnlyScopedOpenings instead.
                    if (banner.Finding == AuditLogService.StartupChainFinding.UnverifiableGap)
                        Assert.Equal(string.Empty, banner.Headline);
                    else
                        Assert.False(string.IsNullOrWhiteSpace(banner.Headline));

                    if (!broken) continue;
                    // The forbidden shape is the UNSCOPED chain-wide negative. "Neither confirmed
                    // nor ruled out" is still printable beside a break when it is scoped to what
                    // the gap actually leaves open ("whether anything BEYOND that finding…"), and
                    // narrowing this assertion to the whole sentence is what keeps that honest
                    // rather than merely quiet.
                    foreach (var negative in ChainWideNegatives)
                        Assert.DoesNotContain(negative, banner.AllClaims);
                }
            }
        }

        /// <summary>
        /// The page must have NO second source for a chain-wide claim: a literal in the markup is
        /// exactly what survived seven rounds, because a sentence inside a Razor page is not
        /// reachable by a test. Every banner must read its chain-wide claims off the composer.
        /// </summary>
        /// <remarks>
        /// ROUND 9 widened this from the two CLOSINGS to the openings as well, and — the part that
        /// mattered — added the <see cref="ReadableText"/> assertions. Round 8's version only
        /// checked that the composer was CALLED; it never checked that the sentences were gone, so
        /// it passed while "Audit chain integrity cannot be determined" was still sitting in the
        /// markup one line above the composed closing.
        /// </remarks>
        [Fact]
        public void ThePage_SourcesEveryChainWideClaim_FromTheComposer()
        {
            var markup = RenderableMarkup();

            Assert.Contains("StartupBannerFor(AuditLogService.StartupChainFinding.Break)", markup);
            Assert.Contains("StartupBannerFor(AuditLogService.StartupChainFinding.ProvenanceIndeterminate)", markup);
            Assert.Contains("StartupBannerFor(AuditLogService.StartupChainFinding.UnverifiableGap)", markup);
            // FOUR since 2026-08-11: the RESTART banner is the fourth finding, and it reads its
            // headline and its closing off the composer like the other three.
            Assert.Contains("StartupBannerFor(AuditLogService.StartupChainFinding.Restart)", markup);
            Assert.Equal(4, markup.Split("StartupBannerFor").Length - 1);

            // …and none of the composed sentences survives as a literal an operator could read.
            var readable = ReadableText();
            foreach (var negative in ChainWideNegatives)
                Assert.DoesNotContain(negative, readable);
            Assert.DoesNotContain("Audit chain provenance cannot be determined", readable);
            Assert.DoesNotContain("Audit chain integrity check failed", readable);
        }

        /// <summary>
        /// The sibling openings, checked rather than assumed. The UNVERIFIABLE banner's opening is
        /// the one chain-state sentence still written in the markup, and this is why that is sound:
        /// it is SCOPED — the negative attaches to "some audit entries" and to "their" integrity,
        /// not to the chain — so no other finding on the same launch can falsify it. The assertion
        /// pins the scoping words, so a later edit that quietly widens it to the chain fails here.
        /// </summary>
        [Fact]
        public void ThePage_KeepsOnlyScopedOpenings()
        {
            var readable = ReadableText();

            Assert.Contains(
                "Some audit entries are unverifiable : they were signed with key",
                readable);
            Assert.Contains("so their integrity can be neither confirmed nor denied", readable);

            // The unscoped forms of that same claim, which would be false beside a break.
            Assert.DoesNotContain("the chain's integrity can be neither confirmed nor denied", readable);
            Assert.DoesNotContain("Audit chain integrity can be neither confirmed nor denied", readable);
        }

        // ── The sealed export's CurrentUser footnote (non-blocking) ──────

        /// <summary>
        /// NON-BLOCKING TIGHTENING. When the ONLY CurrentUser-wrapped row is <c>.chain-anchor</c>,
        /// the footnote's "entries signed with that key can no longer be checked" is vacuously true
        /// — the anchor signs no entries. The consequence that actually follows is the loss of
        /// TRUNCATION DETECTION, which was unsaid. Measured on a chain whose verdict was INTACT.
        /// </summary>
        [Fact]
        public void SealedExportFootnote_NamesTruncationDetection_WhenOnlyTheAnchorIsCurrentUser()
        {
            if (!OperatingSystem.IsWindows()) return;

            var dir = NewDir();
            Seed(dir);

            // Re-wrap the anchor CurrentUser under the service's OWN entropy: still readable, so the
            // verdict stays INTACT and this test is about the footnote and nothing else.
            var anchorPath = Path.Combine(dir, ".chain-anchor");
            var plaintext = System.Security.Cryptography.ProtectedData.Unprotect(
                File.ReadAllBytes(anchorPath), HmacEntropy(),
                System.Security.Cryptography.DataProtectionScope.LocalMachine);
            Overwrite(anchorPath, System.Security.Cryptography.ProtectedData.Protect(
                plaintext, HmacEntropy(), System.Security.Cryptography.DataProtectionScope.CurrentUser));

            using var svc = Open(dir);
            var scopes = svc.DescribeKeyMaterialScopes();
            Assert.Equal(AuditLogService.CurrentUserScopeText,
                scopes.Single(r => r.FileName == ".chain-anchor").Scope);
            Assert.DoesNotContain(scopes.Where(r => r.FileName != ".chain-anchor"),
                r => r.Scope == AuditLogService.CurrentUserScopeText);

            var statement = svc.DescribeChainForCompliance("test");
            Assert.Equal("INTACT", statement.StatusWord);
            var report = svc.ComposeChainVerificationReport(statement, "test");

            Assert.Contains("TRUNCATION", report);
            Assert.Contains("It signs no", report);
            Assert.DoesNotContain("entries signed with that", report);
        }

        /// <summary>
        /// The converse: a CurrentUser KEY file still gets the key-material consequence, so the
        /// tightening above narrowed a sentence rather than deleting one.
        /// <para>
        /// The CurrentUser row is a prior-key ARCHIVE, not <c>hmac.key</c>: measured here, this
        /// build re-wraps the LIVE key to LocalMachine on the next open, so a CurrentUser hmac.key
        /// does not survive long enough to be read by the report. Archives do survive — which is
        /// the state every pre-2026-08-01 install is actually in.
        /// </para>
        /// </summary>
        [Fact]
        public void SealedExportFootnote_StillNamesKeyMaterial_WhenAKeyArchiveIsCurrentUser()
        {
            if (!OperatingSystem.IsWindows()) return;

            var dir = NewDir();
            Seed(dir);

            File.WriteAllBytes(Path.Combine(dir, "hmac.key.AAAABBBBCCCCDDDD"),
                System.Security.Cryptography.ProtectedData.Protect(
                    new byte[32], HmacEntropy(),
                    System.Security.Cryptography.DataProtectionScope.CurrentUser));

            using var svc = Open(dir);
            Assert.Equal(AuditLogService.CurrentUserScopeText,
                svc.DescribeKeyMaterialScopes().Single(r => r.FileName == "hmac.key.AAAABBBBCCCCDDDD").Scope);

            var report = svc.ComposeChainVerificationReport(svc.DescribeChainForCompliance("test"), "test");
            Assert.Contains("KEY MATERIAL", report);
            Assert.DoesNotContain("It signs no", report);
        }
    }
}
