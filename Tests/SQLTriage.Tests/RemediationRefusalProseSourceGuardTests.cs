/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationRefusalProseSourceGuardTests — the TREE-WIDE guard that no source file anywhere in the
 * app puts a RemediationRefusal enum member into a string an operator reads
 * (lane/remediation-enum-prose-2, 2026-09-02).
 *
 * WHY A SOURCE GUARD AND NOT ANOTHER RENDER GUARD. Gated/RemediationRefusalProseRenderTests is the
 * render guard, and it is a good one — over Pages/Remediation.razor. It renders that page and scans
 * what came out. It cannot see any other page, and the two guards before it could not either: the P3
 * guard scanned one div, the lane-1 guard scanned one page. Each round the catalogue of "the sites
 * that are left" was written by reading, and each round it was wrong — seven cited where fourteen
 * stood, and this round seven cited where SIX stand. A guard whose reach is one page will be
 * re-scoped by hand after every future page, and the hand is what keeps missing.
 *
 * So this one is not scoped to a page at all. It reads every .cs and .razor file in the app tree and
 * fails on the SHAPE, naming file and line. A page added next year is covered the day it is added,
 * by nobody. It is the cheap half of the pair: the render guard proves the words an operator sees,
 * this one proves no site was left behind.
 *
 * ⚠ AND IT IS THE HALF THAT RUNS IN CI. Lane 1 recorded an honest limit: its render guard lives in
 * Gated\, which SQLTriage.Tests.csproj Compile-Removes from a community build, and CI builds the
 * COMMUNITY profile only — so the fourteen sites it fixed are proved locally and by nothing in CI.
 * This file binds only RemediationRefusal and RemediationRefusalProse, neither of which is
 * profile-removed, and it reads SOURCE, which is on disk whatever the profile Content-Removes. It
 * therefore runs on BOTH axes, CI included. It is deliberately NOT in Gated\.
 *
 * ⚠ WHAT IT DOES NOT PROVE. That a site calls the producer is not that the operator can read the
 * result. This file never renders anything. The words themselves are proved by
 * RemediationRefusalProseRenderTests (both producers, every member, distinct) and the rendering of
 * them by that file (Pages/Remediation.razor) and by Gated/RemediationRefusalProsePageRenderTests
 * (Pages/RemediationLab.razor, Pages/AgentJobGuard.razor, Pages/AgentJobSync.razor).
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SQLTriage.Data.Services.Remediation;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    public sealed class RemediationRefusalProseSourceGuardTests
    {
        private readonly ITestOutputHelper _out;
        public RemediationRefusalProseSourceGuardTests(ITestOutputHelper output) => _out = output;

        /// <summary>
        /// The producer every refusal must go through. A shape is forgiven only when this name is in
        /// the same expression, which is the one thing that makes the sentence testable.
        /// </summary>
        private const string Producer = "RemediationRefusalProse";

        /// <summary>
        /// Files the scan MUST have read. Without this a scan that walked the wrong directory, or a
        /// glob that matched nothing, reports a clean tree — which is how a source guard becomes a
        /// formality. Every file here is one that has carried the defect at some point in this arc.
        /// </summary>
        private static readonly string[] MustHaveScanned =
        {
            "Pages/Remediation.razor",
            "Pages/RemediationLab.razor",
            "Pages/AgentJobGuard.razor",
            "Pages/AgentJobSync.razor",
            "Data/Services/Remediation/BatchRemediationDriver.cs",
            "Data/Services/Remediation/RemediationRefusalProse.cs",
        };

        // ── 1. THE SHAPE, ANYWHERE IN THE TREE ───────────────────────────────────

        [Fact]
        public void NoSourceFileEverPutsARefusalEnumMemberIntoAStringAPersonReads()
        {
            var files = AppSourceFiles(out var root);

            // ⚠ THE PRECONDITION, same reasoning as the render guard's shell anchor: a scan that
            // read nothing passes every assertion below.
            Assert.True(files.Count > 200,
                $"the scan found only {files.Count} source files under '{root}', which is too few to "
                + "be the app tree: the guard would report clean without having read anything");

            var scanned = new HashSet<string>(files.Select(f => Relative(root, f)), StringComparer.OrdinalIgnoreCase);
            foreach (var required in MustHaveScanned)
                Assert.True(scanned.Contains(required),
                    $"the scan never read '{required}', so anything in it is UNPROVED by this run. "
                    + "Either the file moved (update MustHaveScanned and say why) or the walk is broken.");

            var failures = new List<string>();
            foreach (var file in files)
                failures.AddRange(RawRefusalRenders(Relative(root, file), File.ReadAllText(file)));

            foreach (var f in failures) _out.WriteLine(f);

            Assert.True(failures.Count == 0,
                $"{failures.Count} source site(s) put a RemediationRefusal enum member into a string a "
                + $"person reads.{Environment.NewLine}"
                + string.Join(Environment.NewLine, failures)
                + Environment.NewLine
                + "An operator reads words, not enum members. Route each through "
                + "RemediationRefusalProse.DescribeGateRefusal(refusal, message), which produces the "
                + "whole line — what happened, that nothing was sent to the server, what to do, and "
                + "the gate's own message.");
        }

        // ── 2. THE CENSUS: the sites are ROUTED, not deleted ─────────────────────

        [Fact]
        public void EveryKnownRefusalSurfaceStillGoesThroughTheProducer()
        {
            // ⚠ WHY A COUNT AND NOT A PRESENCE CHECK. Test 1 passes on a file with zero refusal
            // surfaces just as happily as on a file with fourteen correct ones — deleting the surface
            // satisfies it. The house lesson [[verify-cardinality-not-just-presence]] has now recurred
            // three times in this arc alone (a section-scoped scan, a preview site with an
            // uncatalogued result twin, and a catalogue that said seven where six stand). So the
            // number is asserted, per file.
            //
            // A FAILURE HERE IS NOT NECESSARILY A DEFECT. Adding a refusal surface moves a number.
            // Change it, and say in the commit which surface you added — that is the whole point of
            // making you come here.
            var census = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                // 14 per-operation surfaces (7 preview/result pairs), fixed by lane 1 at d97c251.
                ["Pages/Remediation.razor"] = 14,
                // 2 each (preview + result), fixed by lane 2. The pages are Content-Removed from a
                // community build; the SOURCE is on disk either way, so this census reads the same
                // on both axes.
                ["Pages/RemediationLab.razor"] = 2,
                ["Pages/AgentJobGuard.razor"] = 2,
                ["Pages/AgentJobSync.razor"] = 2,
            };

            var files = AppSourceFiles(out var root);
            var byPath = files.ToDictionary(f => Relative(root, f), f => f, StringComparer.OrdinalIgnoreCase);

            var failures = new List<string>();
            foreach (var (relative, expected) in census)
            {
                if (!byPath.TryGetValue(relative, out var full))
                {
                    failures.Add($"[{relative}] was not found in the app tree at all.");
                    continue;
                }

                var text = Blanked(File.ReadAllText(full));
                var actual = Regex.Matches(text, Regex.Escape(Producer) + @"\.DescribeGateRefusal\s*\(").Count;
                _out.WriteLine($"{relative}: {actual} DescribeGateRefusal call(s)");

                if (actual != expected)
                    failures.Add($"[{relative}] calls {Producer}.DescribeGateRefusal {actual} time(s); "
                        + $"the census says {expected}. If you ADDED a refusal surface, raise the number "
                        + "here and name the surface in the commit. If you REMOVED one, lower it. If you "
                        + "did neither, a surface has been re-written by hand and is no longer routed.");
            }

            Assert.True(failures.Count == 0,
                string.Join(Environment.NewLine, failures));
        }

        // ── 3. THE PRODUCER IS REACHABLE FROM BOTH AXES ──────────────────────────

        [Fact]
        public void TheProducerAnswersEveryMemberOnWhicheverProfileThisRunIs()
        {
            // The render guard makes this assertion too, and it lives in Gated\ — which means on a
            // COMMUNITY build (the only profile CI builds) nobody was asserting it. Repeated here,
            // deliberately, because a duplicated assertion that runs is worth more than a single one
            // that is compiled out of the only build a badge reports on.
            foreach (RemediationRefusal refusal in Enum.GetValues(typeof(RemediationRefusal)))
            {
                var line = RemediationRefusalProse.DescribeGateRefusal(refusal, "The gate said no.");
                _out.WriteLine($"{refusal}: {line}");

                foreach (var name in Enum.GetNames(typeof(RemediationRefusal)))
                    Assert.False(Regex.IsMatch(line, @"\b" + Regex.Escape(name) + @"\b"),
                        $"DescribeGateRefusal({refusal}) carries the enum member '{name}'. Line: {line}");

                Assert.Contains(RemediationRefusalProse.Describe(refusal), line, StringComparison.Ordinal);
                Assert.Contains(RemediationRefusalProse.DescribeNextStep(refusal), line, StringComparison.Ordinal);
                Assert.Contains("The gate said no.", line, StringComparison.Ordinal);
            }
        }

        // ── The detector ─────────────────────────────────────────────────────────

        /// <summary>
        /// The three shapes that put a refusal value into a string, each named so a failure says what
        /// was written rather than only where. Comments are blanked first (see <see cref="Blanked"/>),
        /// so prose ABOUT the defect — of which this arc has written a great deal — is not a defect.
        /// </summary>
        private static readonly (string Name, string Pattern, string Advice)[] Shapes =
        {
            ("razor expression",
             @"@\(?[A-Za-z_][\w\.\?\!]*\.Refusal\b",
             "a Razor expression renders the enum straight into the markup"),

            ("interpolated string",
             @"\{[^{}\r\n]*\.Refusal\b[^{}\r\n]*\}",
             "an interpolation hole calls ToString() on the enum"),

            ("string concatenation",
             @"(?:\+\s*[A-Za-z_][\w\.\?\!]*\.Refusal\b|[A-Za-z_][\w\.\?\!]*\.Refusal\b\s*\+)",
             "the enum is concatenated onto a sentence"),
        };

        private static IEnumerable<string> RawRefusalRenders(string relative, string text)
        {
            var blanked = Blanked(text);
            foreach (var (name, pattern, advice) in Shapes)
            {
                foreach (Match m in Regex.Matches(blanked, pattern))
                {
                    // The producer's own call sites read `...DescribeGateRefusal(x.Refusal, ...)`, so
                    // the value IS mentioned beside a `.Refusal` — and that is the correct shape. The
                    // test is whether the producer is in the same expression.
                    if (Context(blanked, m).Contains(Producer, StringComparison.Ordinal)) continue;

                    var line = blanked.Take(m.Index).Count(c => c == '\n') + 1;
                    yield return $"[{relative}:{line}] {name} — {advice}: "
                        + Original(text, m).Trim();
                }
            }
        }

        /// <summary>The match plus enough either side to see whether it sits inside a producer call.</summary>
        private static string Context(string text, Match m)
        {
            var start = Math.Max(0, m.Index - 80);
            var end = Math.Min(text.Length, m.Index + m.Length + 80);
            return text.Substring(start, end - start);
        }

        /// <summary>The offending text as it is actually written, read back off the UNblanked source.</summary>
        private static string Original(string text, Match m) =>
            text.Substring(m.Index, Math.Min(m.Length + 40, text.Length - m.Index))
                .Split('\n')[0];

        /// <summary>
        /// Comments replaced by spaces — LENGTH AND LINE BREAKS PRESERVED, so every match index still
        /// maps to the real line number. Blanking rather than deleting is the whole trick.
        ///
        /// <para>⚠ WHY COMMENTS MUST GO. Three of the strings this guard forbids are quoted verbatim
        /// in comments explaining why they were removed — RemediationRefusalProse's own header quotes
        /// <c>{item.Refusal}</c> twice, and Pages/Remediation.razor and BatchRemediationDriver.cs each
        /// quote it once. A guard that flagged its own documentation would be deleted within a week.</para>
        ///
        /// <para>⚠ THE ONE KNOWN IMPRECISION, stated rather than hidden: the line-comment rule skips a
        /// <c>//</c> preceded by <c>:</c> so a URL in a string survives, but any OTHER <c>//</c> inside
        /// a string literal blanks the rest of that line. That can only ever HIDE a defect, never
        /// invent one, and no such line exists in the tree today (test 2's census would notice the
        /// site going quiet).</para>
        /// </summary>
        private static string Blanked(string text)
        {
            text = Blank(text, @"@\*.*?\*@", RegexOptions.Singleline);   // razor comments
            text = Blank(text, @"/\*.*?\*/", RegexOptions.Singleline);   // block comments
            text = Blank(text, @"(?<![:/])//[^\r\n]*", RegexOptions.None); // line and /// doc comments
            return text;
        }

        private static string Blank(string text, string pattern, RegexOptions options) =>
            Regex.Replace(text, pattern, m => Regex.Replace(m.Value, @"[^\r\n]", " "), options);

        // ── The tree ─────────────────────────────────────────────────────────────

        private static List<string> AppSourceFiles(out string root)
        {
            root = RepoRoot();
            var r = root;
            return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                .Where(f => !IsExcluded(Relative(r, f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string Relative(string root, string full) =>
            full.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');

        /// <summary>
        /// Tests are excluded because a test may legitimately WRITE the forbidden shape to prove a
        /// producer never emits it — this file does exactly that. Build output is excluded because
        /// obj\ carries generated copies of every .razor, which would report each defect twice.
        /// </summary>
        private static bool IsExcluded(string relative) =>
            relative.StartsWith("Tests/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase);

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SQLTriage.sln")))
                dir = dir.Parent;
            Assert.True(dir is not null, "this guard reads app SOURCE, so it needs the repo root");
            return dir!.FullName;
        }
    }
}
