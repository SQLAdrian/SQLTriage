/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// A2 (Adrian ruled it in, 2026-07-20) — <b>make a new raw <c>.Passed</c> read impossible to add
    /// silently.</b>
    ///
    /// <para>WARN, SKIP and INFO all ride <c>CheckResult.Passed == true</c>. Any consumer that reads
    /// that bit without going through <see cref="SQLTriage.Data.Services.CheckClassification"/>
    /// therefore reports a non-assertion as a pass. Four consecutive cold gates each found a NEW
    /// instance of exactly that — score, then narrative, then baseline transitions, then the trend
    /// sparkline. Fixing them one at a time has failed four times running, so this fails the suite
    /// when an unlisted one APPEARS.</para>
    ///
    /// <para><b>Why the suite and not the build.</b> The honest place for a check is where it can be
    /// exercised, and where its own failure is visible. An MSBuild task that scans source would run
    /// inside every build of every consumer of this repo, would have no natural way to prove it can
    /// still fail, and would be silently skipped on an incremental build with no source changes —
    /// which is the exact failure mode ("a check that cannot fail") this guard exists to prevent.
    /// The suite runs it every time, and <see cref="Guard_CanActuallyFail_OnASyntheticUnlistedRead"/>
    /// exercises the failing path on every run rather than asserting it in a comment.</para>
    ///
    /// <para><b>WHAT IT READS.</b> Two textual channels, both in
    /// <see cref="RawPassedScan"/>: a member access (<c>.Passed</c>), and a property-pattern
    /// subpattern (<c>Passed:</c> whose innermost unclosed bracket on the line is a brace) — which
    /// covers the property, list and switch forms, <c>r is { Passed: true }</c>,
    /// <c>rows is [{ Passed: true }, ..]</c> and <c>case { Passed: false }:</c>. The pattern channel
    /// was added 2026-07-20 after the round-4 gate proved all three compiled against the real
    /// <c>CheckResult</c>, promoted WARN, and were invisible to the member-access channel. Pattern
    /// syntax is idiomatic in the scanned roots (62 uses across the tree at the time of writing);
    /// zero are on <c>Passed</c> today, so the channel is empty — it exists to stay that way.</para>
    ///
    /// <para><b>WHAT THIS GUARD DOES NOT CATCH.</b> It is textual. It reads source, not a semantic
    /// model, so it is blind to:</para>
    /// <list type="bullet">
    ///   <item><description><b>Identifier escapes — NOT CAUGHT, by decision.</b>
    ///   Writing any letter of the name as its unicode escape — <c>r.P</c> + a backslash-u-0061
    ///   escape + <c>ssed</c> — is legal C# and binds to exactly the same member, but neither
    ///   channel matches it: both look for the literal text. The round-4 gate compiled that form.
    ///   Catching it
    ///   properly means decoding <c>\uXXXX</c> and <c>\UXXXXXXXX</c> in every identifier, i.e. a
    ///   lexer, which is the semantic model this guard deliberately is not. It is written down here
    ///   rather than half-handled in the regex, because a limit a reader can see is worth more than
    ///   one the regex silently has. Anyone escaping an identifier is evading the guard on purpose,
    ///   and no textual guard survives that.</description></item>
    ///   <item><description><b>Patterns split across lines.</b> The pattern channel decides using
    ///   the innermost unclosed bracket to the LEFT ON THE SAME LINE, which is what separates a
    ///   real subpattern from a named tuple element (<c>(Area: x, Passed: y)</c>) or a format
    ///   string (<c>$"Passed: {n}"</c>) without a parser. So <c>{ Passed: true }</c> on one line is
    ///   seen, but a subpattern wrapped so <c>Passed:</c> begins a line with no opener of its own
    ///   is not.</description></item>
    ///   <item><description><b>Aliasing.</b> <c>var ok = r.Passed;</c> is caught at that line, but
    ///   every later use of <c>ok</c> is invisible. Copy the bit into a local, a tuple, a DTO or an
    ///   anonymous type and the guard sees one allowlisted read, not the consumer downstream of
    ///   it.</description></item>
    ///   <item><description><b>Indirection.</b> A property or method that returns the raw bit
    ///   (<c>bool IsOk =&gt; _r.Passed;</c>) launders it: callers of <c>IsOk</c> are unguarded.
    ///   The same applies to any wrapper type that surfaces it under another name.</description></item>
    ///   <item><description><b>Reflection and serialisation.</b> <c>GetProperty("Passed")</c>,
    ///   <c>JsonSerializer</c> emitting the property, data binding by name, and any dynamic
    ///   dispatch are all invisible.</description></item>
    ///   <item><description><b>Type blindness.</b> It matches the TEXT <c>.Passed</c>, so it also
    ///   lists reads on unrelated types (<c>HealthSummary.Passed</c>, <c>RestoreVerifyOutcome.Passed</c>).
    ///   Those are allowlisted with that as the reason. Conversely, if a future type names its
    ///   field something other than <c>Passed</c> while carrying the same meaning, this guard says
    ///   nothing about it.</description></item>
    ///   <item><description><b>Replacement in place.</b> The allowlist is keyed on file plus the
    ///   normalised code text, with a count. Deleting an allowlisted read and adding a DIFFERENT
    ///   read whose normalised line text is byte-identical would net out. Any other edit shows
    ///   up.</description></item>
    ///   <item><description><b>Scope.</b> Only <c>Data/</c>, <c>Pages/</c>, <c>Cli/</c>,
    ///   <c>Components/</c> and <c>Program.cs</c> are scanned. <c>Mcp/</c> and the test project are
    ///   not.</description></item>
    /// </list>
    ///
    /// <para>It catches the thing that has actually bitten four times: someone writing a fresh
    /// <c>r.Passed</c> in a consumer. That is worth having. It is not a proof of absence, and no
    /// test name here claims one.</para>
    /// </summary>
    public class RawPassedGuardTests
    {
        private const string AllowlistRelativePath = "Tests/SQLTriage.Tests/raw-passed-allowlist.tsv";

        /// <summary>One checked-in ruling: this many reads of this exact line, for this reason.</summary>
        private sealed record AllowEntry(string Path, int Count, string Code, string Reason)
        {
            public string Key => Path + "\t" + Code;
        }

        // ── the guard ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void NoUnlistedRawPassedRead()
        {
            var root = RawPassedScan.RepoRoot();
            var actual = RawPassedScan.ScanReads(root);
            var allowed = ReadAllowlist(root);

            var actualByKey = actual.GroupBy(o => o.Key)
                                    .ToDictionary(g => g.Key, g => g.ToList());
            var allowedByKey = allowed.ToDictionary(a => a.Key, a => a);

            var problems = new List<string>();

            // A read nobody has ruled on, or more copies of a ruled one than were ruled in.
            foreach (var (key, occurrences) in actualByKey.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (!allowedByKey.TryGetValue(key, out var entry))
                {
                    foreach (var o in occurrences)
                        problems.Add($"  NEW raw read  {o.RelativePath}:{o.Line}\n      {o.Code}");
                }
                else if (occurrences.Count > entry.Count)
                {
                    problems.Add(
                        $"  MORE raw reads than ruled in  {occurrences[0].RelativePath} " +
                        $"(allowlist says {entry.Count}, found {occurrences.Count}) at lines " +
                        $"{string.Join(", ", occurrences.Select(o => o.Line))}\n      {occurrences[0].Code}");
                }
            }

            // An allowlist entry that no longer matches anything is a mute suppression waiting to
            // cover a future read. It must be deleted, so it fails too.
            foreach (var entry in allowed.OrderBy(a => a.Path, StringComparer.Ordinal))
            {
                if (!actualByKey.TryGetValue(entry.Key, out var occurrences))
                    problems.Add($"  STALE allowlist entry (no longer present)  {entry.Path}\n      {entry.Code}");
                else if (occurrences.Count < entry.Count)
                    problems.Add(
                        $"  STALE count  {entry.Path} (allowlist says {entry.Count}, found {occurrences.Count})" +
                        $"\n      {entry.Code}");
            }

            if (problems.Count > 0)
            {
                DumpActual(actual);
                throw new Xunit.Sdk.XunitException(BuildMessage(problems));
            }
        }

        private static string BuildMessage(List<string> problems)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Raw CheckResult.Passed guard failed (A2, ruled 2026-07-20).");
            sb.AppendLine();
            sb.AppendLine("WARN, SKIP and INFO all carry Passed=true. A consumer that reads .Passed raw");
            sb.AppendLine("therefore reports \"the check could not assess this\" as \"the check passed\" —");
            sb.AppendLine("the false-clean that four consecutive cold gates each found a fresh instance of.");
            sb.AppendLine();
            sb.AppendLine("WHAT TO DO INSTEAD — Data/Services/CheckClassification.cs:");
            sb.AppendLine("  • counting or scoring?          filter with CheckClassification.IsScorable(r) first");
            sb.AppendLine("  • rendering a pass/fail badge?  test IsWarn(r) BEFORE the Passed arm, never after");
            sb.AppendLine("  • is this genuinely legitimate? add it to " + AllowlistRelativePath);
            sb.AppendLine("    WITH A REASON. An allowlist entry without a reason rots into a mute suppression.");
            sb.AppendLine();
            sb.AppendLine($"{problems.Count} problem(s):");
            foreach (var p in problems) sb.AppendLine(p);
            sb.AppendLine();
            sb.AppendLine("The full current scan was written to raw-passed-actual.tsv in the test output");
            sb.AppendLine("directory, in allowlist format — paste the line you need and write its reason.");
            return sb.ToString();
        }

        /// <summary>Writes the whole current scan in allowlist format, so a developer who trips the
        /// guard has the exact line to paste rather than having to hand-build it.</summary>
        private static void DumpActual(List<RawPassedScan.Occurrence> actual)
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "raw-passed-actual.tsv");
                var lines = actual
                    .GroupBy(o => o.Key)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => $"{g.First().RelativePath}\t{g.Count()}\t{g.First().Code}\tTODO: why is this read legitimate?");
                File.WriteAllLines(path, lines);
            }
            catch { /* diagnostic only — never mask the real failure */ }
        }

        // ── the allowlist must not rot ─────────────────────────────────────────────────────────

        [Fact]
        public void EveryAllowlistEntryCarriesAReason()
        {
            var allowed = ReadAllowlist(RawPassedScan.RepoRoot());

            allowed.Should().NotBeEmpty("an empty allowlist would make the guard's own parsing untested");

            var reasonless = allowed
                .Where(a => string.IsNullOrWhiteSpace(a.Reason) || a.Reason.Trim().Length < 15)
                .Select(a => $"{a.Path}: {a.Code}")
                .ToList();

            reasonless.Should().BeEmpty(
                "an allowlist without reasons is a mute suppression list — the next reader cannot " +
                "tell a ruled-legitimate read from one somebody silenced to get a build green");
        }

        // ── non-vacuity: a guard that scans nothing passes everything ─────────────────────────

        [Fact]
        public void TheScanActuallyReachesTheShippedSource()
        {
            var root = RawPassedScan.RepoRoot();

            var files = RawPassedScan.SourceFiles(root);
            files.Count.Should().BeGreaterThan(200,
                "if the roots resolved to nothing the guard would pass over an empty set");

            files.Should().Contain(f => f.EndsWith("CheckClassification.cs", StringComparison.OrdinalIgnoreCase));
            files.Should().Contain(f => f.EndsWith("CheckTrend.razor", StringComparison.OrdinalIgnoreCase),
                ".razor must be scanned — the sparkline defect that motivated this guard was in one");

            var all = RawPassedScan.ScanAll(root);
            all.Count.Should().BeGreaterThan(50, "the scan must find the occurrences that exist");
            all.Should().Contain(o => o.IsWrite, "CheckExecutionService assigns Passed — writes must be seen");
            all.Should().Contain(o => !o.IsWrite, "and reads must be distinguished from them");
        }

        // ── PROOF THE GUARD CAN FAIL — exercised, not asserted ────────────────────────────────

        [Fact]
        public void Guard_CanActuallyFail_OnASyntheticUnlistedRead()
        {
            // The guard's whole value is that it goes red. A guard that cannot fail is precisely the
            // defect class this repo keeps being burned by, so the failing path is EXERCISED on every
            // run — not claimed in a comment. This drives the real scanner over a real temporary
            // file inside the scanned tree, then asserts the classification the guard consumes.
            //
            // The live end-to-end proof (a raw read added to a real shipped file, guard red, read
            // removed, guard green) is recorded in the round's report; this keeps that property
            // under test afterwards.
            // The probe tree is a REAL directory laid out like the repo (Data/Services/...) in a temp
            // location, not the working tree: a test must not write .cs files into a source folder
            // the next build would compile. The scanner under test is the shipped one, walking real
            // files off disk.
            var probeRoot = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "rawpassed-guard-" + Guid.NewGuid().ToString("N")));
            var probeDir = Path.Combine(probeRoot.FullName, "Data", "Services");
            var probeFile = Path.Combine(probeDir, "GuardProbe.cs");

            try
            {
                Directory.CreateDirectory(probeDir);
                File.WriteAllText(probeFile, string.Join(Environment.NewLine, new[]
                {
                    "namespace SQLTriage.Data.Services {",
                    "  internal static class GuardProbe {",
                    "    // r.Passed here is a comment and must NOT count",
                    "    internal static bool Consume(CheckResult r) => r.Passed;",
                    "  }",
                    "}",
                }));

                var reads = RawPassedScan.ScanReads(probeRoot);

                reads.Should().ContainSingle("the scanner must SEE a newly added raw read, and must " +
                                             "not be fooled by the commented one above it");
                reads[0].RelativePath.Should().Be("Data/Services/GuardProbe.cs");
                reads[0].Line.Should().Be(4);
                reads[0].Code.Should().Contain("r.Passed");

                // And it must be unlisted — nothing in the checked-in allowlist can cover it.
                var allowed = ReadAllowlist(RawPassedScan.RepoRoot()).Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
                allowed.Should().NotContain(reads[0].Key,
                    "an unlisted read is what the guard reports; if the allowlist covered it the " +
                    "guard would stay green over a brand-new consumer");

                // Now take the read away and confirm the guard does not latch: same tree, no finding.
                File.WriteAllText(probeFile, "namespace SQLTriage.Data.Services { internal static class GuardProbe { } }");
                RawPassedScan.ScanReads(probeRoot).Should().BeEmpty(
                    "removing the read must clear the finding — a guard that stays red regardless " +
                    "is as useless as one that stays green");
            }
            finally
            {
                try { Directory.Delete(probeRoot.FullName, recursive: true); } catch { }
            }
        }

        [Fact]
        public void TheScanner_SeesPatternMatchesOnPassed_AndNotTuplesOrFormatText()
        {
            // The round-4 gate's finding: 'if (r is { Passed: true }) n++;' compiled against the real
            // CheckResult, promoted WARN to a pass, and the guard suite stayed 5/5 GREEN because the
            // scanner only looked for the text '.Passed'. Two siblings did the same — a list pattern
            // and a switch arm. None of the three lines below contains a '.Passed' anywhere.
            //
            // The negatives matter as much: the scanned tree really does contain named tuple
            // elements and log/format strings that read 'Passed:', and a channel that reported them
            // would put five noise entries in the allowlist. Noise in an allowlist is how an
            // allowlist stops being read. Both directions are pinned here, over a real temp tree
            // walked by the shipped scanner.
            var probeRoot = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "rawpassed-pattern-" + Guid.NewGuid().ToString("N")));
            var probeDir = Path.Combine(probeRoot.FullName, "Data", "Services");

            try
            {
                Directory.CreateDirectory(probeDir);
                File.WriteAllText(Path.Combine(probeDir, "PatternProbe.cs"), string.Join(Environment.NewLine, new[]
                {
                    "namespace SQLTriage.Data.Services {",                                              // 1
                    "  internal static class PatternProbe {",                                           // 2
                    "    static int A(CheckResult r) { int n = 0; if (r is { Passed: true }) n++; return n; }",   // 3 property
                    "    static bool B(CheckResult[] rows) => rows is [{ Passed: true }, ..];",                   // 4 list
                    "    static int C(CheckResult r) { switch (r) { case { Passed: false }: return 1; } return 0; }", // 5 switch
                    "    static bool M(CheckResult r) => r is { Severity: \"High\", Passed: true };",             // 6 second subpattern
                    "    static string D() => \"Passed: nothing to see\";",                                       // 7 format text — NOT a read
                    "    static (string Area, int Passed) E() => (Area: \"x\", Passed: 1);",                      // 8 tuple — NOT a read
                    "    // if (r is { Passed: true }) — a comment must not count",                               // 9
                    "  }",
                    "}",
                }));

                var reads = RawPassedScan.ScanReads(probeRoot);

                reads.Select(o => o.Line).Should().Equal(new[] { 3, 4, 5, 6 },
                    "the property, list and switch forms are all reads of the bit, a second " +
                    "subpattern after a comma is still a subpattern, and NOTHING else on this " +
                    "probe is — a format string and a named tuple element are not consumers");

                reads.Should().OnlyContain(o => !o.IsWrite, "you cannot assign through a pattern");
                reads.Should().OnlyContain(o => !o.Code.Contains(".Passed", StringComparison.Ordinal),
                    "if any of these were caught by the member-access channel the pattern channel " +
                    "would be untested by this fixture");
            }
            finally
            {
                try { Directory.Delete(probeRoot.FullName, recursive: true); } catch { }
            }
        }

        [Fact]
        public void TheScanner_TellsWritesFromReads_AndIgnoresComments()
        {
            // The classifier is the guard's load-bearing judgement. If it called everything a write,
            // NoUnlistedRawPassedRead would scan a real tree and find nothing — a green guard over a
            // broken scanner. Exercised on fixture text with a known answer.
            var lines = new[]
            {
                "result.Passed = true;",                       // write
                "summary.Passed++;",                           // write
                "if (r.Passed && x) { }",                      // read
                "var y = a.Passed == b.Passed;",               // two reads
                "// r.Passed in a line comment",               // none — comment
                "/// <see cref=\"CheckResult.Passed\"/>",      // none — doc comment
                "/* block r.Passed */ var z = q.Passed;",      // one read (block stripped)
            };

            var code = RawPassedScan.StripComments(lines, isRazor: false);

            code[4].Should().NotContain("Passed", "a line comment must be stripped");
            code[5].Should().NotContain("Passed", "a doc comment must be stripped");
            code[6].Should().NotContain("block r.Passed", "a block comment must be stripped");
            code[6].Should().Contain("q.Passed", "but the code after it must survive");

            // Razor comments too — the sparkline defect lived in a .razor file.
            var razor = RawPassedScan.StripComments(new[] { "@* note: p.Passed here *@ <b>@x.Passed</b>" }, isRazor: true);
            razor[0].Should().NotContain("p.Passed");
            razor[0].Should().Contain("x.Passed");
        }

        // ── allowlist parsing ─────────────────────────────────────────────────────────────────

        private static List<AllowEntry> ReadAllowlist(DirectoryInfo root)
        {
            var path = Path.Combine(root.FullName, AllowlistRelativePath.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(path).Should().BeTrue(
                $"the allowlist must be checked in at {AllowlistRelativePath}; without it the guard " +
                "would either pass over nothing or fail for the wrong reason");

            var entries = new List<AllowEntry>();
            var lineNo = 0;
            foreach (var raw in File.ReadAllLines(path))
            {
                lineNo++;
                var line = raw.TrimEnd();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                var parts = line.Split('\t');
                parts.Length.Should().Be(4,
                    $"{AllowlistRelativePath}:{lineNo} must be TAB-separated as " +
                    $"path<TAB>count<TAB>code<TAB>reason, but had {parts.Length} field(s): {line}");

                int.TryParse(parts[1], out var count).Should().BeTrue(
                    $"{AllowlistRelativePath}:{lineNo} field 2 must be a count");

                entries.Add(new AllowEntry(parts[0].Trim(), count, parts[2].Trim(), parts[3].Trim()));
            }

            entries.Select(e => e.Key).Should().OnlyHaveUniqueItems(
                "a duplicated key would make the count column ambiguous");

            return entries;
        }
    }
}
