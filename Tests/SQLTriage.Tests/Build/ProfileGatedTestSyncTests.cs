/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SQLTriage.Tests.Build
{
    /// <summary>
    /// The recurrence guard for the 2026-08-04 community-build outage.
    ///
    /// <para><b>What happened.</b> CI on <c>main</c> builds ONE profile — community — and it had not
    /// passed for ~11 days: 15 failures, 5 cancelled, zero successes back to 2026-07-23. It failed at
    /// BUILD, so <c>dotnet test</c> never ran once in that window: <c>main</c>'s suite was unverified
    /// AND the publicly shipped edition did not compile. The cause was one test file binding
    /// <c>RoadmapReport</c>, a type <c>buildprofile.targets</c> Compile-Removes from the community
    /// build. Gating that file uncovered three more, in two further categories.</para>
    ///
    /// <para><b>What this guards.</b> A test file that is still compiled under community must not
    /// bind a symbol the community assembly does not contain. The gated set is DERIVED from
    /// buildprofile.json + buildprofile.targets + the app's own <c>#if</c> fences (see
    /// <see cref="ProfileGateModel"/>), so a newly added gate is covered without anyone updating a
    /// list here.</para>
    ///
    /// <para><b>What this does NOT guard, stated plainly.</b> It is a LINT, not a boundary. The
    /// boundary is the community build, which CI already runs and which already caught this defect
    /// on the very first push — detection was never the failure; response was. This lint moves the
    /// signal to where a developer will actually meet it: the default-profile suite, which stayed
    /// green for the whole outage. It can be walked around by reflection, a type alias, a name it
    /// does not parse as a declaration, or a symbol reached only inside an interpolated string. It
    /// cannot see gates expressed outside buildprofile.targets. Do not treat a green run here as
    /// proof the community build compiles — build it.</para>
    /// </summary>
    public sealed class ProfileGatedTestSyncTests
    {
        private const string TestProjectDir = "Tests/SQLTriage.Tests";

        private static DirectoryInfo Root() => RawPassedScan.RepoRoot();

        private static IEnumerable<string> TestSourceFiles(DirectoryInfo root) =>
            Directory.EnumerateFiles(Path.Combine(root.FullName, "Tests", "SQLTriage.Tests"), "*.cs",
                                     SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                              && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

        // ── 1. THE guard: no still-compiled test binds a community-removed symbol ────────────

        [Fact]
        public void No_test_compiled_under_community_binds_a_symbol_that_build_removes()
        {
            var root = Root();
            var surface = ProfileGateModel.CommunitySurface(root);
            var removals = ProfileGateModel.TestProjectCommunityRemovals(root);

            Assert.True(surface.Files.Count > 0,
                "the community gated-file set came back empty — the model is not reading " +
                "buildprofile.targets, and a guard that scans for nothing must fail, not pass.");

            // Names declared ONLY in gated files. A name also declared in a file that ships (partials
            // such as AssessmentPdf, or a same-named type in another namespace) is not evidence of
            // anything and is subtracted.
            var gatedNames = new HashSet<string>(StringComparer.Ordinal);
            var gatedNamespaces = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rel in surface.Files)
            {
                var text = File.ReadAllText(Path.Combine(root.FullName, rel));
                foreach (var n in ProfileGateModel.TypeNamesIn(text)) gatedNames.Add(n);
                foreach (var ns in ProfileGateModel.NamespacesIn(text)) gatedNamespaces.Add(ns);
            }

            var shippedNames = new HashSet<string>(StringComparer.Ordinal);
            var shippedNamespaces = new HashSet<string>(StringComparer.Ordinal);
            var definedSymbols = new HashSet<string>(surface.Symbols, StringComparer.Ordinal);

            // Members fenced OUT of a file that still compiles, as "DeclaringType.Member".
            var gatedMembers = new HashSet<string>(StringComparer.Ordinal);
            var compiledMembers = new HashSet<string>(StringComparer.Ordinal);

            foreach (var f in Directory.EnumerateFiles(root.FullName, "*.cs", SearchOption.AllDirectories))
            {
                var rel = ProfileGateModel.Rel(root, f);
                if (rel.StartsWith("Tests/", StringComparison.OrdinalIgnoreCase)) continue;
                if (rel.Contains("/obj/") || rel.Contains("/bin/") || rel.StartsWith("obj/") || rel.StartsWith("bin/")) continue;
                if (surface.Files.Contains(rel)) continue;

                var text = File.ReadAllText(f);
                foreach (var n in ProfileGateModel.TypeNamesIn(text)) shippedNames.Add(n);
                foreach (var ns in ProfileGateModel.NamespacesIn(text)) shippedNamespaces.Add(ns);
                foreach (var m in ProfileGateModel.FencedOutMembersIn(text, definedSymbols, compiledMembers))
                    gatedMembers.Add(m);
            }

            gatedNames.ExceptWith(shippedNames);
            gatedNamespaces.ExceptWith(shippedNamespaces);
            // A const declared in BOTH arms of an #if/#else is not gated at all (BuildModules).
            gatedMembers.ExceptWith(compiledMembers);

            // A name the TEST assembly declares for itself is its own, not the app's.
            foreach (var f in TestSourceFiles(root))
                foreach (var n in ProfileGateModel.TypeNamesIn(File.ReadAllText(f)))
                    gatedNames.Remove(n);

            Assert.True(gatedNames.Count > 0 && gatedMembers.Count > 0,
                $"the derived gate set is degenerate (types={gatedNames.Count}, members={gatedMembers.Count}); " +
                "a guard that scans for nothing must fail, not pass.");

            var namePattern = new Regex(@"(?<![\w.])(" + string.Join("|", gatedNames.Select(Regex.Escape)) + @")\b",
                                        RegexOptions.Compiled);
            var memberPattern = new Regex(@"\b(" + string.Join("|", gatedMembers.Select(Regex.Escape)) + @")\b",
                                          RegexOptions.Compiled);
            var qualifiedPattern = new Regex(@"\b(" + string.Join("|", gatedNames.Concat(gatedNamespaces).Select(Regex.Escape)) + @")\b",
                                             RegexOptions.Compiled);

            var offenders = new List<string>();
            foreach (var f in TestSourceFiles(root))
            {
                var relToProject = ProfileGateModel.Rel(root, f)
                    .Substring(TestProjectDir.Length + 1);
                if (!ProfileGateModel.TestFileIsCompiledInCommunity(relToProject, removals)) continue;

                var code = ProfileGateModel.StripCommentsAndStrings(File.ReadAllText(f));

                foreach (var (line, i) in code.Split('\n').Select((l, i) => (l, i)))
                {
                    // 1. A type that exists only in a Compile-Removed file: "RoadmapReport".
                    var m = namePattern.Match(line);
                    // 2. A member #if-fenced out of a file that still ships:
                    //    "AssessmentPdf.BuildFindingsReport".
                    if (!m.Success) m = memberPattern.Match(line);
                    // 3. A whole namespace that only gated files declare:
                    //    "SQLTriage.Data.Services.Portal.CapacityCollector".
                    if (!m.Success)
                    {
                        var q = Regex.Match(line, @"SQLTriage(\.\w+)+");
                        if (!q.Success || !qualifiedPattern.IsMatch(q.Value)) continue;
                        m = q;
                    }
                    offenders.Add($"{relToProject}({i + 1}): binds '{m.Value.Trim()}'");
                }
            }

            Assert.True(offenders.Count == 0,
                "These test files are still compiled in a COMMUNITY build but bind symbols that build " +
                "removes, so `dotnet build -p:SQLTriageProfile=community` will fail — and community is " +
                "the ONLY profile CI builds, so the whole suite stops running.\n" +
                "Move just the offending TEST (not its whole fixture) to Tests/SQLTriage.Tests/Gated/ " +
                "— see Gated/README.md.\n  " + string.Join("\n  ", offenders));
        }

        // ── 2. The gating declarations cannot rot into no-ops ────────────────────────────────

        [Fact]
        public void Every_named_community_removal_still_points_at_a_file_that_exists()
        {
            var root = Root();
            var dir = Path.Combine(root.FullName, "Tests", "SQLTriage.Tests");

            var dead = ProfileGateModel.TestProjectCommunityRemovals(root)
                .Where(p => p.IndexOf('*') < 0)
                .Where(p => !File.Exists(Path.Combine(dir, p.Replace('/', Path.DirectorySeparatorChar))))
                .ToList();

            Assert.True(dead.Count == 0,
                "A <Compile Remove> in SQLTriage.Tests.csproj names a file that no longer exists. MSBuild " +
                "treats that as a silent no-op, so the gate reads as present and enforces nothing — rename " +
                "the entry with the file, or drop it:\n  " + string.Join("\n  ", dead));
        }

        [Fact]
        public void Everything_under_Gated_is_actually_removed_from_the_community_build()
        {
            var root = Root();
            var gatedDir = Path.Combine(root.FullName, "Tests", "SQLTriage.Tests", "Gated");
            Assert.True(Directory.Exists(gatedDir),
                "Tests/SQLTriage.Tests/Gated/ is missing. It is where a test that binds a community-removed " +
                "symbol lives, and the folder glob in the csproj is what keeps it out of the community build.");

            var removals = ProfileGateModel.TestProjectCommunityRemovals(root);
            var leaked = Directory.EnumerateFiles(gatedDir, "*.cs", SearchOption.AllDirectories)
                .Select(f => ProfileGateModel.Rel(root, f).Substring(TestProjectDir.Length + 1))
                .Where(rel => ProfileGateModel.TestFileIsCompiledInCommunity(rel, removals))
                .ToList();

            Assert.True(leaked.Count == 0,
                "These files sit under Gated/ but the csproj's community ItemGroup does not remove them, " +
                "so they would still compile into a community test build:\n  " + string.Join("\n  ", leaked));
        }

        // ── 3. The model is reading the real inputs (a guard on the guard) ───────────────────

        [Fact]
        public void The_model_reproduces_the_gates_the_build_is_known_to_apply()
        {
            var root = Root();
            var surface = ProfileGateModel.CommunitySurface(root);

            // Derived, not asserted from memory: these three are the gates whose removal actually
            // broke the build on 2026-08-04. If the model stops seeing them it has silently stopped
            // modelling anything, and test 1 above would pass over an empty set.
            Assert.Contains("Data/Services/RoadmapPdfBuilder.cs", surface.Files);
            Assert.Contains(surface.Files, f => f.StartsWith("Data/Services/Portal/", StringComparison.Ordinal));
            Assert.Contains("SQLT_NO_REPORT_FINDINGS_PDF", surface.Symbols);
            Assert.Contains("SQLT_NO_REPORT_DBA_HANDOFF", surface.Symbols);
            Assert.Contains("SQLT_COMMUNITY", surface.Symbols);

            // And the keepers are NOT gated: a model that gates everything would also pass test 1
            // vacuously-loudly. Audit Evidence / Risk Register / Risk Acknowledgement have no
            // exclude flag at all, so no SQLT_NO_REPORT_* symbol should exist for them.
            Assert.DoesNotContain(surface.Symbols, s => s.Contains("AUDIT_EVIDENCE"));
            Assert.DoesNotContain(surface.Symbols, s => s.Contains("RISK_REGISTER"));
        }
    }
}
