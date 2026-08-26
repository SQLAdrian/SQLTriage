/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// THE RED HALF, written so it can actually be run at the parent commit.
    /// <para>
    /// Round 8 (<c>9a2fcb5</c>) reported its two banner defects as "red at <c>9825fb5</c>" and
    /// pasted a <c>Failed: 2, Passed: 0</c> run. That claim was FALSE AS STATED: the test file it
    /// shipped, <c>AuditLogStartupBannerTests.cs</c>, is written against API that round 8 itself
    /// introduced (<c>StartupBannerFor</c>, <c>StartupChainFinding</c>,
    /// <c>ComposeStartupChainBanners</c>), so at <c>9825fb5</c> it does not compile — 9 errors,
    /// CS1061/CS0117. Whatever ran red there was a different file from the one shipped. The defects
    /// were real; the provenance was not, and an unverifiable red half is exactly what this
    /// project's evidence rule exists to prevent.
    /// </para>
    /// <para>
    /// This file is that half, done properly. It uses ONLY API that predates round 8 — the
    /// constructor, the three public flags, <c>LogConnectionAttempt</c>, <c>Flush</c> — and it reads
    /// the shipped <c>Pages/AuditLogViewer.razor</c> straight out of the source tree, so it needs no
    /// project-file change either. Drop it into a worktree at <c>9825fb5</c> and it compiles and
    /// fails. It stays in the suite alongside the composer-based tests because it is the assertion
    /// that does not care HOW the sentences are produced: whatever machinery a later round chooses,
    /// these sentences must not be readable on the page in these states.
    /// </para>
    /// <para>
    /// Round 9 added the third sentence, the INDETERMINATE banner's HEADLINE — round 8 fixed the
    /// closing of that banner and left its opening, which is the same unscoped chain-wide claim one
    /// sentence higher up and the one an operator actually quotes.
    /// </para>
    /// </summary>
    public class AuditLogStartupBannerPreComposerTests : IDisposable
    {
        private readonly List<string> _dirs = new();

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

        // ── Fixtures: pre-round-8 API only ───────────────────────────────

        private string NewDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "audit-precomposer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            _dirs.Add(dir);
            return dir;
        }

        private static string Seed(string dir, int entries = 3)
        {
            using var svc = new AuditLogService(dir, startFlushTimer: false);
            for (var i = 0; i < entries; i++) svc.LogConnectionAttempt($"srv{i}", success: true);
            svc.Flush();
            return Directory.GetFiles(dir, "audit-*.jsonl")
                            .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).First();
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

        /// <summary>Head-of-chain KeyId rewritten to an id nothing resolves, WITH a real-but-
        /// unreadable DPAPI archive planted for it — byte-for-byte what genuine key loss leaves
        /// behind, so the run classifies benign and sets ChainUnverifiable.</summary>
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

        private static void TamperMessage(string logFile, int index)
        {
            var lines = File.ReadAllLines(logFile);
            Assert.True(lines.Length > index);
            lines[index] = RewriteJson(lines[index], "Message", "\"TAMPERED\"");
            File.WriteAllLines(logFile, lines);
        }

        /// <summary>The out-of-band anchor replaced by a well-formed DPAPI blob this box cannot
        /// unwrap — what a service-account change leaves behind.</summary>
        private static void OrphanAnchor(string dir)
        {
            var path = Path.Combine(dir, ".chain-anchor");
            File.SetAttributes(path, FileAttributes.Normal);
            File.WriteAllBytes(path, System.Security.Cryptography.ProtectedData.Protect(
                Encoding.UTF8.GetBytes("{\"Sig\":\"x\",\"Ts\":\"x\"}"),
                Encoding.UTF8.GetBytes("a-different-entropy"),
                System.Security.Cryptography.DataProtectionScope.LocalMachine));
        }

        // ── The page, as an operator reads it ────────────────────────────

        /// <summary>
        /// The shipped <c>Pages/AuditLogViewer.razor</c>, located from the source tree so this file
        /// is droppable into any checkout without a project-file change; Razor comments stripped
        /// (a comment never renders), then HTML tags stripped and whitespace collapsed, because the
        /// sentences at issue are split by markup — the headline is written
        /// <c>Audit chain integrity &lt;strong&gt;cannot be determined&lt;/strong&gt;</c>, so a
        /// search of the raw file for what a person reads would find nothing and pass vacuously.
        /// </summary>
        private static string ReadablePageText()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string? found = null;
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Pages", "AuditLogViewer.razor");
                if (File.Exists(candidate)) { found = candidate; break; }
                dir = dir.Parent;
            }
            found ??= Path.Combine(AppContext.BaseDirectory, "Markup", "AuditLogViewer.razor");
            Assert.True(File.Exists(found), $"Could not locate Pages/AuditLogViewer.razor from {AppContext.BaseDirectory}.");

            var raw = File.ReadAllText(found);

            var stripped = new StringBuilder(raw.Length);
            var i = 0;
            while (i < raw.Length)
            {
                var open = raw.IndexOf("@*", i, StringComparison.Ordinal);
                if (open < 0) { stripped.Append(raw, i, raw.Length - i); break; }
                stripped.Append(raw, i, open - i);
                var close = raw.IndexOf("*@", open + 2, StringComparison.Ordinal);
                if (close < 0) break;
                i = close + 2;
            }

            var text = new StringBuilder(stripped.Length);
            var inTag = false;
            foreach (var ch in stripped.ToString())
            {
                if (ch == '<') { inTag = true; text.Append(' '); continue; }
                if (ch == '>') { inTag = false; text.Append(' '); continue; }
                if (!inTag) text.Append(char.IsWhiteSpace(ch) ? ' ' : ch);
            }
            return string.Join(' ', text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        // ── The two constructions, and what the page may not say on them ──

        /// <summary>
        /// Benign key loss BESIDE a real tamper: <c>ChainBroken=True ChainUnverifiable=True</c> on
        /// one launch, both banners rendering. The amber banner's chain-wide exculpation is false on
        /// these bytes — the tampered entry's key resolved fine and its signature did not recompute.
        /// <para>RED at 9825fb5 on the markup assertion; the flag assertions pass there.</para>
        /// </summary>
        [Fact]
        public void ThePage_CannotExculpateTheChain_OnABreakBesideABenignKeyGap()
        {
            if (!OperatingSystem.IsWindows()) return;

            var dir = NewDir();
            var log = Seed(dir);
            PlantBenignKeyGap(dir, log);
            TamperMessage(log, 2);

            using var svc = new AuditLogService(dir, startFlushTimer: false);
            Assert.True(svc.ChainBroken);
            Assert.True(svc.ChainUnverifiable);
            Assert.False(svc.ChainProvenanceIndeterminate);

            Assert.DoesNotContain(
                "No entry with an available signing key was found to have been altered",
                ReadablePageText());
        }

        /// <summary>
        /// An unreadable anchor BESIDE a real tamper: <c>ChainBroken=True
        /// ChainProvenanceIndeterminate=True</c>. Two sentences of that banner are false on these
        /// bytes — the closing (round 8's finding) and the HEADLINE (round 9's), which is the one an
        /// operator reads first and quotes: this launch DID determine the integrity, and the red
        /// alert stacked directly above says what it found.
        /// <para>RED at 9825fb5 on all three markup assertions.</para>
        /// </summary>
        [Fact]
        public void ThePage_CannotSayIntegrityIsUndetermined_OnABreakBesideAnUnreadableAnchor()
        {
            if (!OperatingSystem.IsWindows()) return;

            var dir = NewDir();
            var log = Seed(dir);
            TamperMessage(log, 1);
            OrphanAnchor(dir);

            using var svc = new AuditLogService(dir, startFlushTimer: false);
            Assert.True(svc.ChainBroken);
            Assert.True(svc.ChainProvenanceIndeterminate);
            Assert.False(svc.ChainUnverifiable);

            var readable = ReadablePageText();

            // ROUND 9 — the headline. Reachable here and on the deleted-anchor cell below.
            Assert.DoesNotContain("Audit chain integrity cannot be determined", readable);

            // ROUND 8 — the closing.
            Assert.DoesNotContain("Whether the chain was altered can be neither confirmed nor ruled out", readable);
            Assert.DoesNotContain("not evidence of tampering", readable);
        }
    }
}
