/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The census behind <see cref="ShippedConfig"/>. It exists because the defect it guards was a SET
    /// completed as an INSTANCE: on 2026-09-10 commit <c>3ba2a2b</c> moved the operator-editable configs
    /// from the payload's <c>config\</c> to <c>config.default\</c>, that lane repaired ONE of the seven
    /// test helpers that resolved such a file by hand (<c>ServiceCatalogHonestyTests.cs:49</c>), and the
    /// other six went blind. MEASURED at base <c>c216df6</c> from <c>base.trx</c>, and the scope word
    /// matters in both directions: in a FULL-SUITE run 24 tests threw <c>FileNotFoundException</c> before
    /// reaching an assertion — <c>AlertCumulativeRateTests</c> 15, <c>AlertBaselineFenceDirectionTests</c>
    /// 6, <c>AlertSeverityCollapseTests</c> 3 — while <c>WaitSignalRatioAlertTests</c> was 16 passed / 1
    /// failed, because another class had already seeded a <c>config\</c> for it. Run that class IN
    /// ISOLATION and 16 of its 17 throw instead. The failing SET depends on run order; that is the
    /// defect, and a count quoted without its run shape is not a fact.
    ///
    /// <para>Three invariants, each enumerated FROM THE PAYLOAD OR FROM THE CODE rather than from a
    /// hand-kept list, so that a list going stale cannot make the census read green.</para>
    /// </summary>
    public class ShippedConfigResolutionCensusTests
    {
        private readonly ITestOutputHelper _out;
        public ShippedConfigResolutionCensusTests(ITestOutputHelper output) => _out = output;

        /// <summary>The test sources, enumerated off disk from the repo root. Never a hand-kept list.</summary>
        private static IReadOnlyList<string> TestSourceFiles()
        {
            var root = Path.Combine(RawPassedScan.RepoRoot().FullName, "Tests", "SQLTriage.Tests");
            Directory.Exists(root).Should().BeTrue(
                "the census scans the test sources; without them it would pass over nothing");

            return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                                        StringComparison.OrdinalIgnoreCase)
                         && !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // ── (a) COMPLETENESS. Every file the payload ships as a default must be resolvable ──────────

        /// <summary>
        /// INVARIANT: every file this build places in <c>config.default\</c> is findable through the one
        /// resolver. The enumeration is the PAYLOAD's own directory listing — a consumer-derived oracle,
        /// not a parity check between two hand-kept lists, which is the pairing the house rule requires.
        /// </summary>
        [Fact]
        public void Every_file_the_payload_ships_as_a_default_resolves_through_the_one_resolver()
        {
            var shipped = ShippedConfig.EnumerateShippedDefaults();

            shipped.Should().NotBeEmpty(
                "this build must place the operator-editable defaults in "
                + ShippedConfig.DefaultsFolderName + "\\ beside the test assembly; an empty folder means "
                + "the census is scanning nothing and would pass over any defect at all");

            _out.WriteLine($"{ShippedConfig.DefaultsFolderName}\\ ships {shipped.Count} file(s): "
                           + string.Join(", ", shipped));

            // ⚠ HONEST LIMIT: this assertion is near-vacuous BY CONSTRUCTION and is not the guard here.
            // EnumerateShippedDefaults() lists config.default\ and TryPath() probes config.default\ FIRST,
            // so every name returned resolves unless the two calls race. The LIVE guard in this test is the
            // NotBeEmpty below it, which was mutation-proved RED on 2026-09-11 by hiding the payload folder.
            var unresolved = shipped.Where(n => ShippedConfig.TryPath(n) is null).ToList();
            unresolved.Should().BeEmpty(
                "a file the payload ships must be reachable by the resolver every test uses");
        }

        // ── (b) ONE RESOLVER, NOT SEVEN. No test may hand-roll a lookup that the split would blind ──

        /// <summary>
        /// INVARIANT: no test source resolves a file that ships in <c>config.default\</c> by naming a
        /// RUNTIME config folder itself. The runtime folder is created by
        /// <c>ConfigDefaultsSeeder</c> from <c>Program.Main</c>, which no test process runs, so such a
        /// lookup finds the file only in a tree whose <c>bin\</c> still holds a pre-split leftover.
        ///
        /// <para>Both offending shapes are checked, because the six twins used one and the seventh
        /// used the other: the hand-rolled two-folder array
        /// (<c>new[] { "config", "Config" }</c>) and a direct
        /// <c>Path.Combine(baseDir, "Config", "&lt;shipped file&gt;")</c>. The file names are read off
        /// the payload, so the census widens by itself when a new config joins the split.</para>
        /// </summary>
        [Fact]
        public void No_test_resolves_a_shipped_default_by_naming_a_runtime_config_folder_itself()
        {
            var shippedNames = ShippedConfig.EnumerateShippedDefaults();
            shippedNames.Should().NotBeEmpty("an empty payload listing would make this census vacuous");

            var sources = TestSourceFiles();
            sources.Count.Should().BeGreaterThan(50,
                "the whole test tree must be in scope; a near-empty enumeration is a broken census");

            var offences = new List<string>();

            foreach (var file in sources)
            {
                var name = Path.GetFileName(file);
                if (string.Equals(name, nameof(ShippedConfig) + ".cs", StringComparison.OrdinalIgnoreCase))
                    continue;   // the one resolver is allowed to name the folders; that is its job
                if (string.Equals(name, "ConfigDefaultsSeedingTests.cs", StringComparison.OrdinalIgnoreCase))
                    continue;   // it tests the seeder itself, so the folder names ARE its subject

                // HONEST LIMIT, PROVED 2026-09-11: this is a SINGLE-LINE string scan, so it is BLIND to
                // the same lookup wrapped across lines - a Path.Combine( / baseDir, / "config", ... ) or a
                // new[] / { "config", "Config" } planted deliberately kept all five censuses GREEN. A count
                // measures the pattern, not the code. What actually prevents recurrence is the shared
                // ShippedConfig resolver that all eight call sites use; this census is a tripwire for the
                // easy case, NOT the guarantee. Deriving it from a syntax tree would close the hole.
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];

                    // A comment is not a lookup. Prose in this very file describes both offending
                    // shapes verbatim, and without this the census reports itself.
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal)
                        || trimmed.StartsWith("*", StringComparison.Ordinal)) continue;

                    // Shape 1 — the hand-rolled probe array the six twins each carried a copy of.
                    if ((line.Contains("new[]", StringComparison.Ordinal)
                         || line.Contains("new string[]", StringComparison.Ordinal))
                        && line.Contains("\"config\"", StringComparison.Ordinal)
                        && line.Contains("\"Config\"", StringComparison.Ordinal))
                    {
                        offences.Add($"{name}:{i + 1}: hand-rolled runtime-folder probe — {line.Trim()}");
                        continue;
                    }

                    // Shape 2 — a direct Combine naming a runtime folder AND a file that ships as a
                    // default, anchored on the BUILD OUTPUT. A Combine anchored on the repo root is the
                    // correct way to read the authored file and is deliberately NOT an offence: the
                    // defect is reading the runtime folder, not naming it.
                    if (!line.Contains("Path.Combine", StringComparison.Ordinal)) continue;
                    if (line.Contains("RepoRoot", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!line.Contains("\"config\"", StringComparison.Ordinal)
                        && !line.Contains("\"Config\"", StringComparison.Ordinal)) continue;

                    var anchoredOnOutput =
                        line.Contains("BaseDirectory", StringComparison.Ordinal)
                        || line.Contains("baseDir", StringComparison.Ordinal)
                        // A walk UP from the base directory reaches bin\ before the repo, so the first
                        // hit is a build-output copy. ConfigStoreWriteGuardTests carried this shape.
                        || line.Contains("dir.FullName", StringComparison.Ordinal);
                    if (!anchoredOnOutput) continue;

                    var hit = shippedNames.FirstOrDefault(
                        n => line.Contains("\"" + n + "\"", StringComparison.OrdinalIgnoreCase));
                    if (hit != null)
                        offences.Add($"{name}:{i + 1}: resolves shipped default '{hit}' from the build "
                                     + $"output's runtime folder — {line.Trim()}");
                }
            }

            offences.Should().BeEmpty(
                "these must call " + nameof(ShippedConfig) + ".Path/ReadAllText instead. The runtime "
                + "config\\ folder does not exist in a test process — ConfigDefaultsSeeder creates it "
                + "from Program.Main — so a lookup that names it resolves only against a stale bin\\, "
                + "which is how these lookups resolved at all before 2026-09-10 and why they throw\n"
                + "now. A stale bin\\ is also why such a lookup can READ GREEN on a box that built\n"
                + "earlier. Counts here depend on run order - quote the run shape with the number.\n"
                + "Offences:\n"
                + string.Join("\n", offences));
        }

        // ── (e) THE PROBE ORDER IS THE WHOLE FIX. config.default\ is the payload; config\ is mutable ──
        //
        // ⚠ "config.default\ is immutable" would be the tidy claim and it is FALSE as of this lane, PROVED
        // 2026-09-11: ScriptConfigurationMigratorTests.cs:621-651 resolves through ShippedConfig and then
        // File.WriteAllText's that path, so running it rewrites config.default\script-configurations.json
        // in the build output (mtime moved; bytes restored in its finally, hash unchanged). The suite is
        // serial (xunit.runner.json maxParallelThreads: 1) so the blast radius is bounded, but a kill
        // between the write and the finally leaves the payload short one entry. Giving that test a private
        // copy is the real fix and is NOT done here — it is named in the lane close instead.

        /// <summary>
        /// INVARIANT: a shipped default resolves out of <c>config.default\</c>, never out of the
        /// runtime <c>config\</c> — whenever this build ships a defaults folder at all.
        ///
        /// <para><b>This is the single property that makes the resolver correct, and it is here so that
        /// a later "tidy-up" reordering the probe list goes red with the reason attached.</b> The
        /// runtime <c>config\</c> beside a test assembly is NOT a read-only copy of the payload. It is
        /// written DURING the suite: <c>DashboardConfigService</c>'s disk-backed constructor resolves
        /// <c>&lt;baseDir&gt;\Config\dashboard-config.json</c>, and its <c>Load()</c> calls
        /// <c>Save()</c> when that file is absent — so the two tests that construct it
        /// (<c>CachedStatHonestyTests.cs:253</c>, <c>NoServerIdleTests.cs:116</c>) write
        /// <c>DefaultConfigGenerator</c>'s ~42-panel default over the path every other test reads.
        /// MEASURED 2026-09-11 in this build output: 100,274 bytes where the shipped file is 481,763.</para>
        ///
        /// <para>That is precisely why the brief for this lane named TWO failures with two unrelated
        /// messages. QUOTED FROM <c>base.trx</c> at <c>c216df6</c>, not paraphrased:
        /// <c>livewaits.signal_pct not found in dashboard-config.json</c>, and
        /// <c>the CREATE INDEX display panel must cache (the guard blanks string literals and does not
        /// block CREATE INDEX)</c>. Note what the second one is NOT: the <c>dropped N shipped quer(ies)</c>
        /// assertion above it PASSED, because the 42-panel stub is internally consistent and every panel
        /// it does contain caches. The failure landed on the named regression anchor instead — the panel
        /// was not dropped by a gate, it was absent from the file being read. A reader who quotes the
        /// drop-count message here is describing a failure this tree never produced. Before <c>3ba2a2b</c> the payload put the real file in <c>config\</c>,
        /// so <c>File.Exists</c> was true, <c>Save()</c> never fired and the collision was dormant.</para>
        ///
        /// <para>The readers are fixed by reading the artefact that ships. The WRITERS are untouched and
        /// out of this lane's scope — giving them a path seam changes a product constructor — so the
        /// build output will keep being mutated, and this census is what keeps that from mattering.</para>
        /// </summary>
        [Fact]
        public void A_shipped_default_resolves_out_of_the_payload_folder_not_the_mutable_runtime_folder()
        {
            var shipped = ShippedConfig.EnumerateShippedDefaults();
            shipped.Should().NotBeEmpty("an empty payload listing would make this census vacuous");

            var expectedFolder = Path.Combine(AppContext.BaseDirectory, ShippedConfig.DefaultsFolderName);
            var wrong = shipped
                .Select(n => new { n, resolved = ShippedConfig.TryPath(n) })
                .Where(x => x.resolved is null
                            || !string.Equals(Path.GetDirectoryName(x.resolved),
                                              expectedFolder.TrimEnd(Path.DirectorySeparatorChar),
                                              StringComparison.OrdinalIgnoreCase))
                .Select(x => $"{x.n} -> {x.resolved ?? "(unresolved)"}")
                .ToList();

            wrong.Should().BeEmpty(
                "the runtime config\\ folder is written by the suite itself while the suite runs, so a "
                + "resolver that prefers it reads whatever the last writer left. Probe "
                + ShippedConfig.DefaultsFolderName + "\\ first.\n" + string.Join("\n", wrong));
        }

        // ── (d) THE PAYLOAD IS THE REPO FILE. What ships is what a repo-reading guard asserts about ─

        /// <summary>
        /// INVARIANT: every file the payload places in <c>config.default\</c> is byte-identical to its
        /// source in the repo's <c>Config\</c>.
        ///
        /// <para>This is the half that makes a repo-reading guard mean something about the customer.
        /// <c>ExecSurfaceGuardTests.LoadPath_FullShippedConfig_CachesEveryShippedQuery_NoneDroppedByTheGate</c>
        /// asserts that the gate drops no SHIPPED query, but it reads the REPO file; without this pin,
        /// a <c>Content Update</c> retargeted at the wrong source, or a payload transform, would leave
        /// that test green while the file a customer receives said something else. Measured 2026-09-11:
        /// all seven files matched. Every name here has a <c>Config\</c> source by construction — the
        /// payload folder is produced by <c>TargetPath</c> rewrites in SQLTriage.csproj — so a file with
        /// NO repo source is itself the failure, not a skip.</para>
        /// </summary>
        [Fact]
        public void Every_shipped_default_is_byte_identical_to_its_repo_source()
        {
            var shipped = ShippedConfig.EnumerateShippedDefaults();
            shipped.Should().NotBeEmpty("an empty payload listing would make this census vacuous");

            var repoConfig = Path.Combine(RawPassedScan.RepoRoot().FullName, "Config");
            var mismatches = new List<string>();

            foreach (var fileName in shipped)
            {
                var payload = ShippedConfig.TryPath(fileName);
                var source = Path.Combine(repoConfig, fileName);

                if (payload is null) { mismatches.Add($"{fileName}: not resolvable at all"); continue; }
                if (!File.Exists(source)) { mismatches.Add($"{fileName}: no repo source at {source}"); continue; }

                var a = File.ReadAllBytes(payload);
                var b = File.ReadAllBytes(source);
                if (!a.AsSpan().SequenceEqual(b))
                    mismatches.Add($"{fileName}: payload {a.Length} bytes != repo source {b.Length} bytes");
            }

            _out.WriteLine($"compared {shipped.Count} shipped default(s) against Config\\");
            mismatches.Should().BeEmpty(
                "a guard that reads the repo file only speaks about the customer while the payload "
                + "carries those same bytes.\n" + string.Join("\n", mismatches));
        }

        // ── (c) A REPO READ MUST ANCHOR ON THE REPO ROOT, never on the first hit walking up ─────────

        /// <summary>
        /// INVARIANT: a helper that reads a REPO SOURCE file anchors on the folder holding
        /// <c>SQLTriage.sln</c>. It must never return the first hit while walking UP from the test
        /// assembly, because <c>bin\&lt;cfg&gt;\&lt;tfm&gt;\&lt;rid&gt;\</c> sits between the assembly
        /// and the repo root and can hold a stale copy of the very file under guard.
        ///
        /// <para>PROVED 2026-09-11: planting a mutated <c>dashboard-config.json</c> in the test output
        /// made <c>ExecSurfaceGuardTests.ReadRepoFile("Config/dashboard-config.json")</c> read THAT
        /// file — the test failed naming the panel that had been mutated in the output copy, while the
        /// repo file it claims to read was untouched. Two of the eight <c>ReadRepoFile</c> helpers in
        /// this suite had that shape; six already anchored correctly.</para>
        /// </summary>
        [Fact]
        public void Every_ReadRepoFile_helper_anchors_on_the_repo_root()
        {
            var sources = TestSourceFiles();
            var declarations = 0;
            var offences = new List<string>();

            foreach (var file in sources)
            {
                var name = Path.GetFileName(file);
                if (string.Equals(name, nameof(ShippedConfigResolutionCensusTests) + ".cs",
                                  StringComparison.OrdinalIgnoreCase))
                    continue;   // this file quotes the helper's name in prose, it does not declare one

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].Contains("string ReadRepoFile(", StringComparison.Ordinal)) continue;

                    declarations++;
                    var body = string.Join("\n", lines.Skip(i).Take(25));

                    // (i) it must anchor at all.
                    if (!body.Contains("SQLTriage.sln", StringComparison.Ordinal)
                        && !body.Contains("RepoRoot(", StringComparison.Ordinal))
                    {
                        offences.Add($"{name}:{i + 1}: ReadRepoFile does not anchor on the repo root "
                                     + "(no SQLTriage.sln walk and no RepoRoot() call in its first 25 lines)");
                        continue;
                    }

                    // (ii) and the anchor must not sit BEHIND a first-hit walk that can return earlier.
                    //      Checking only (i) is not enough — measured 2026-09-11 by mutation: a body that
                    //      walks up for the TARGET FILE and falls back to RepoRoot() satisfies (i) while
                    //      still returning the build-output copy. The two shapes are told apart by WHAT
                    //      the walk looks for: the correct one probes for SQLTriage.sln, the defective
                    //      one probes for the caller's own relative path.
                    if (System.Text.RegularExpressions.Regex.IsMatch(
                            body, @"Combine\(\s*dir\.FullName\s*,\s*relative",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        offences.Add($"{name}:{i + 1}: ReadRepoFile walks up looking for the TARGET FILE "
                                     + "(Path.Combine(dir.FullName, relative...)), so it returns a bin\\ "
                                     + "copy before it ever reaches the repo root");
                    }
                }
            }

            _out.WriteLine($"ReadRepoFile declarations censused: {declarations}");
            declarations.Should().BeGreaterThan(0,
                "the census must find the helpers it guards; finding none would pass over everything");

            offences.Should().BeEmpty(
                "use RawPassedScan.RepoRoot() — a first-hit walk up from AppContext.BaseDirectory "
                + "reads bin\\ before it reaches the repo, so the guard silently scans a build output.\n"
                + string.Join("\n", offences));
        }
    }
}
