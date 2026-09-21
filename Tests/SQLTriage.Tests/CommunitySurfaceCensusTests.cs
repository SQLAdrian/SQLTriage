/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

/// <summary>
/// THE INVARIANT, stated here because this is the file that measures it:
///
/// <para><b>The community profile boundary is verified by something that measures the artefact that
/// SHIPS, and an artefact that cannot be measured FAILS.</b></para>
///
/// <para>WHY THIS CLASS EXISTS. On 2026-09-11 the guard standing between gated code and the PUBLIC
/// repo, <c>scripts\verify-community-build.ps1</c>, was found pinning its scan target BY NAME - an
/// <c>obj\</c> intermediate it did not build - and FAILING TOWARD CLEAN. A genuine 174 MB full-profile
/// Release build passed as community, exit 0. Three layered mechanisms were ruled that day (18:40);
/// the PRIMARY one is a compile-time source/route census, because it runs before the compiler emits a
/// byte and therefore cannot be blinded by compression, single-file bundling, or a shared
/// <c>obj\</c> that two profiles write to.</para>
///
/// <para>WHAT RUNS THE CENSUS, AND WHAT GOES RED WHEN IT DOES NOT. The census is
/// <c>scripts\census-community-surface.ps1</c>, invoked by <c>buildprofile.targets</c> target
/// <c>SQLTriageCommunitySurfaceCensus</c> at <c>BeforeTargets="CoreCompile"</c> on every community
/// build and publish, with no <c>ContinueOnError</c>. Delete that target and
/// <see cref="The_census_is_wired_into_the_community_build"/> goes red. Weaken the census so it stops
/// reddening on a known-bad tree and
/// <see cref="The_census_reddens_when_a_never_ship_source_is_compiled_in"/> goes red. That pairing is
/// the whole point: the through-line of this arc is guards that exist and are wired to nothing.</para>
///
/// <para>⚠ EVERY GREEN FROM A FAIL-TOWARD-CLEAN GUARD IS INADMISSIBLE UNTIL THE INSTRUMENT IS SHOWN TO
/// GO RED. Most tests below are therefore reddening tests: they build a known-bad synthetic tree and
/// assert a non-zero exit. <see cref="The_census_passes_on_the_real_community_evaluation"/> is the only
/// one that asserts a pass, and it is admissible only because the others exist.</para>
///
/// <para>⚠⚠ THE BLIND SPOT, NAMED RATHER THAN LEFT TO BE DISCOVERED. The never-ship set is the set of
/// source files carrying the gated canary token. A NEW gated page added without that token is invisible
/// to this census - exactly as it is invisible to the byte scan, which hunts the same token. This class
/// does not close that hole. It closes the one where a canary-bearing page is compiled into community
/// anyway, and the one where a never-ship route literal survives into live community code. A census
/// over a marker can only ever be as complete as the marking.</para>
/// </summary>
public class CommunitySurfaceCensusTests
{
    private readonly ITestOutputHelper _out;

    public CommunitySurfaceCensusTests(ITestOutputHelper output) => _out = output;

    private static string RepoRoot() => FrkContractTests.RepoRoot();

    private static string CensusScript() => Path.Combine(RepoRoot(), "scripts", "census-community-surface.ps1");

    private static string EmitScript() => Path.Combine(RepoRoot(), "scripts", "emit-build-surface.ps1");

    /// <summary>The gated marker, assembled from parts: the contiguous token must not live in a test
    /// file either - tests are not published, but the habit is what keeps it out of the ones that are.</summary>
    private const string Canary = "SQLT-GATED-DNA-" + "NEVERSHIP";

    private const string CommunityDefines =
        "TRACE,SQLT_COMMUNITY,SQLT_NO_PREMIUM,SQLT_NO_DEVTOOLS,SQLT_NO_PORTAL,SQLT_NO_OPERATIONS," +
        "SQLT_NO_LIVEMONITORING,DEBUG";

    // ── running the census ────────────────────────────────────────────────────────────────────

    private (int ExitCode, string Output) RunCensus(string surfaceFile, string repoRoot, string? expectToken = null)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot(),
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(CensusScript());
        psi.ArgumentList.Add("-SurfaceFile");
        psi.ArgumentList.Add(surfaceFile);
        psi.ArgumentList.Add("-RepoRoot");
        psi.ArgumentList.Add(repoRoot);
        if (expectToken is not null)
        {
            psi.ArgumentList.Add("-ExpectToken");
            psi.ArgumentList.Add(expectToken);
        }

        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(180_000);
        _out.WriteLine(text);
        return (p.ExitCode, text);
    }

    /// <summary>
    /// The smallest tree that still carries the real structure: a BuildModules.cs copied from the repo
    /// (so the pruning model is the real one), one never-ship page, one shipping page.
    /// </summary>
    private static string NewSyntheticTree(bool devTree, bool withNeverShipSource, string? liveLiteral = null,
                                           string? prunedLiteral = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sqlt-census-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Data"));
        Directory.CreateDirectory(Path.Combine(dir, "Pages"));
        if (devTree) Directory.CreateDirectory(Path.Combine(dir, ".handoff"));
        File.Copy(Path.Combine(RepoRoot(), "Data", "BuildModules.cs"), Path.Combine(dir, "Data", "BuildModules.cs"));

        if (withNeverShipSource)
        {
            File.WriteAllText(Path.Combine(dir, "Pages", "Gated.razor"),
                "@page \"/synthetic-gated\"\n@code { private const string GatedCanary = \"" + Canary + "\"; }\n");
        }

        var shipped = "@page \"/open\"\n<div>open</div>\n";
        if (liveLiteral is not null) shipped += "<a href=\"" + liveLiteral + "\">x</a>\n";
        if (prunedLiteral is not null) shipped += "@if (BuildModules.Premium) { <a href=\"" + prunedLiteral + "\">x</a> }\n";
        else shipped += "@if (BuildModules.Premium) { <span>pruned</span> }\n";
        File.WriteAllText(Path.Combine(dir, "Pages", "Open.razor"), shipped);
        File.WriteAllText(Path.Combine(dir, "Program.cs"), "class P { static void Main() {} }\n");
        return dir;
    }

    private static string NewSurface(string dir, IEnumerable<string> items, string defines = CommunityDefines,
                                     string profile = "community", string? token = null)
    {
        var path = Path.Combine(dir, "surface-" + Guid.NewGuid().ToString("N") + ".txt");
        var lines = new List<string>
        {
            "# sqltriage-build-surface v1",
            "P|Producer=CommunitySurfaceCensusTests",
            "P|ProjectFullPath=" + Path.Combine(RepoRoot(), "SQLTriage.csproj"),
            "P|SQLTriageProfile=" + profile,
            "P|Configuration=Debug",
            "P|RuntimeIdentifier=win-x64",
            "P|DefineConstants=" + defines,
        };
        if (token is not null) lines.Add("P|BuildToken=" + token);
        lines.AddRange(items);
        File.WriteAllLines(path, lines);
        return path;
    }

    // ── 1. the wiring ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A mechanism nobody invokes is not a mechanism. This asserts the census is invoked by the build
    /// itself - before the compiler runs, on the community profile, and fatally.
    /// </summary>
    [Fact]
    public void The_census_is_wired_into_the_community_build()
    {
        var targets = File.ReadAllText(Path.Combine(RepoRoot(), "buildprofile.targets"));

        var target = Regex.Match(targets,
            "<Target\\s+Name=\"SQLTriageCommunitySurfaceCensus\"[\\s\\S]*?</Target>",
            RegexOptions.None);
        target.Success.Should().BeTrue(
            "the primary gate must be invoked by the build; without this Target the census is a script nobody runs");

        var body = target.Value;
        body.Should().Contain("BeforeTargets=\"CoreCompile\"",
            "the census must run before the compiler emits a byte - that is what compression and single-file bundling cannot blind");
        body.Should().Contain("'$(SQLTriageProfile)' == 'community'",
            "the census applies to the community profile");
        body.Should().Contain("census-community-surface.ps1", "the Target must invoke the census script");
        body.Should().NotContain("ContinueOnError",
            "a gate that continues on error is not a gate");
        body.Should().Contain("-ExpectToken",
            "the census must be handed this build's token, so it reads the surface THIS build wrote rather than a path that merely tends to hold one");
        body.Should().Contain("P|BuildToken=$(_SQLTCensusToken)", "the token must be written into the surface it is checked against");

        File.Exists(CensusScript()).Should().BeTrue("the Target names this script");
        File.Exists(EmitScript()).Should().BeTrue("the standalone emitter is what lets this suite exercise the census");
    }

    // ── 2. the reddening tests: this guard fails toward clean, so prove it fails ──────────────

    [Fact]
    public void The_census_reddens_when_a_never_ship_source_is_compiled_in()
    {
        var tree = NewSyntheticTree(devTree: true, withNeverShipSource: true);

        // Control first: the same tree with the gated page correctly excluded must PASS, or the red
        // below would prove nothing about the mutation.
        var clean = NewSurface(tree, new[] { "C|Pages\\Open.razor", "S|Program.cs" });
        var ok = RunCensus(clean, tree);
        ok.ExitCode.Should().Be(0, "the control arm must pass, or the reddening arm is measuring the tree and not the defect");

        // The mutation: the never-ship page is in the item set, as it would be if its
        // buildprofile.targets Remove were deleted.
        var bad = NewSurface(tree, new[] { "C|Pages\\Open.razor", "C|Pages\\Gated.razor", "S|Program.cs" });
        var red = RunCensus(bad, tree);
        red.ExitCode.Should().Be(1, "a never-ship source compiled into the community profile is the defect this gate exists for");
        red.Output.Should().Contain("never-ship source COMPILED INTO THE COMMUNITY PROFILE");
        red.Output.Should().Contain("Gated.razor", "the failure must name the file, not merely fail");
    }

    [Fact]
    public void The_census_reddens_on_a_never_ship_route_literal_in_live_code()
    {
        var tree = NewSyntheticTree(devTree: true, withNeverShipSource: true, liveLiteral: "/synthetic-gated");
        var surface = NewSurface(tree, new[] { "C|Pages\\Open.razor", "S|Program.cs" });
        var red = RunCensus(surface, tree);
        red.ExitCode.Should().Be(1);
        red.Output.Should().Contain("never-ship route literal");

        // And the discrimination that makes the census usable: the SAME literal inside an
        // @if (BuildModules.Premium) block is removed by the compiler, so it is reported, not failed.
        var pruned = NewSyntheticTree(devTree: true, withNeverShipSource: true, prunedLiteral: "/synthetic-gated");
        var surface2 = NewSurface(pruned, new[] { "C|Pages\\Open.razor", "S|Program.cs" });
        var green = RunCensus(surface2, pruned);
        green.ExitCode.Should().Be(0, "an occurrence the compiler prunes is not a leak; failing on it would make the census unusable and then ignored");
        green.Output.Should().Contain("CENSUS-IMPRECISION(CommunitySurfaceCensusTests)",
            "excused occurrences are reported as a SET, never silently dropped");
    }

    /// <summary>
    /// ABSENT or EMPTY is a FAILURE, never a skip. That behaviour is the one thing explicitly off the
    /// table for this lane: a silent degrade to whatever was scannable is how a full-profile build
    /// passed as community.
    /// </summary>
    [Fact]
    public void The_census_fails_when_its_input_is_absent_or_empty()
    {
        var tree = NewSyntheticTree(devTree: true, withNeverShipSource: true);

        var absent = RunCensus(Path.Combine(tree, "no-such-surface.txt"), tree);
        absent.ExitCode.Should().Be(1, "an absent surface means the census cannot state what is compiled");
        absent.Output.Should().Contain("ABSENT");

        var emptyPath = Path.Combine(tree, "empty.txt");
        File.WriteAllText(emptyPath, "# sqltriage-build-surface v1\n");
        var empty = RunCensus(emptyPath, tree);
        empty.ExitCode.Should().Be(1, "an empty surface must not pass vacuously");

        var noItems = NewSurface(tree, Array.Empty<string>());
        var vacuous = RunCensus(noItems, tree);
        vacuous.ExitCode.Should().Be(1, "zero Compile and zero Content items is an empty census");
        vacuous.Output.Should().Contain("ZERO");
    }

    /// <summary>
    /// Provenance, not name - applied to the census's own input. A surface it cannot tie to this build,
    /// or that is not a community evaluation at all, must not produce a verdict.
    /// </summary>
    [Fact]
    public void The_census_refuses_a_surface_it_cannot_vouch_for()
    {
        var tree = NewSyntheticTree(devTree: true, withNeverShipSource: true);
        var items = new[] { "C|Pages\\Open.razor", "S|Program.cs" };

        var full = NewSurface(tree, items, defines: "TRACE,DEBUG", profile: "full");
        RunCensus(full, tree).ExitCode.Should().Be(1, "a full-profile surface is not a community surface");

        var mislabelled = NewSurface(tree, items, defines: "TRACE,DEBUG", profile: "community");
        var m = RunCensus(mislabelled, tree);
        m.ExitCode.Should().Be(1, "a surface claiming community with no SQLT_COMMUNITY symbol is claiming something it cannot show");
        m.Output.Should().Contain("SQLT_COMMUNITY");

        var tokened = NewSurface(tree, items, token: "aaaaaaaaaaaa");
        var wrongToken = RunCensus(tokened, tree, expectToken: "bbbbbbbbbbbb");
        wrongToken.ExitCode.Should().Be(1, "a surface from another build is a stale artefact, which is exactly today's defect one layer up");
        wrongToken.Output.Should().Contain("BuildToken");
        RunCensus(tokened, tree, expectToken: "aaaaaaaaaaaa").ExitCode.Should().Be(0,
            "the matching token must still pass, or the check is just a break");
    }

    /// <summary>
    /// Non-vacuity is a NAMED guard, not a count. Rename the marker and the census must say so rather
    /// than report a clean tree - a census that finds nothing and passes is the false-green shape this
    /// arc produced five times in one day.
    /// </summary>
    [Fact]
    public void The_census_fails_a_dev_tree_in_which_nothing_is_marked_never_ship()
    {
        var tree = NewSyntheticTree(devTree: true, withNeverShipSource: false);
        var surface = NewSurface(tree, new[] { "C|Pages\\Open.razor", "S|Program.cs" });
        var red = RunCensus(surface, tree);
        red.ExitCode.Should().Be(1);
        red.Output.Should().Contain("NON-VACUOUS CENSUS FAILED");
    }

    /// <summary>
    /// The public path, which is the one where a fail-closed fix breaks strangers. A curated public tree
    /// carries no gated source at all, so the census passes - and says why. A public tree that DOES carry
    /// one is the worst case there is, and fails.
    /// </summary>
    [Fact]
    public void The_census_passes_a_curated_public_tree_and_fails_one_carrying_gated_sources()
    {
        var publicTree = NewSyntheticTree(devTree: false, withNeverShipSource: false);
        var surface = NewSurface(publicTree, new[] { "C|Pages\\Open.razor", "S|Program.cs" });
        var green = RunCensus(surface, publicTree);
        green.ExitCode.Should().Be(0, "a community build from the curated public tree must still work for outside users");
        green.Output.Should().Contain("public tree");

        var leaky = NewSyntheticTree(devTree: false, withNeverShipSource: true);
        var surface2 = NewSurface(leaky, new[] { "C|Pages\\Open.razor", "S|Program.cs" });
        var red = RunCensus(surface2, leaky);
        red.ExitCode.Should().Be(1, "a never-ship source sitting in a public tree is the leak that cannot be walked back");
    }

    // ── 3. the live arm ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The real repo, the real MSBuild evaluation of the community profile, the real census. Admissible
    /// only because every test above shows the same instrument going red.
    /// </summary>
    [Fact]
    public void The_census_passes_on_the_real_community_evaluation()
    {
        var surface = Path.Combine(Path.GetTempPath(), "sqlt-surface-" + Guid.NewGuid().ToString("N") + ".txt");

        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot(),
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(EmitScript());
        psi.ArgumentList.Add("-OutFile");
        psi.ArgumentList.Add(surface);
        using (var p = Process.Start(psi)!)
        {
            _out.WriteLine(p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd());
            p.WaitForExit(300_000);
            p.ExitCode.Should().Be(0, "the community profile must still be evaluable, or nothing downstream means anything");
        }

        File.Exists(surface).Should().BeTrue();
        var lines = File.ReadAllLines(surface);
        lines.Count(l => l.StartsWith("C|", StringComparison.Ordinal)).Should().BeGreaterThan(0,
            "the community evaluation must still compile .razor Content, or the emitter has stopped matching");
        lines.Count(l => l.StartsWith("S|", StringComparison.Ordinal)).Should().BeGreaterThan(0);

        var result = RunCensus(surface, RepoRoot());
        result.ExitCode.Should().Be(0,
            "if this is red, read the failure: a never-ship source is being compiled into community, or a never-ship route literal reaches live community code");
        result.Output.Should().Contain("census-community-surface: OK");
    }

    // ── 4. the completeness oracle over the hand-written byte-scan list ──────────────────────

    /// <summary>
    /// A parity test validates AGREEMENT, not COMPLETENESS. <c>.handoff\gated-routes.txt</c> is the
    /// hand-written needle list the byte scan uses; nothing derived it from the code, so nothing
    /// noticed when it fell behind. This derives the never-ship routes FROM THE SOURCE - the @page
    /// directives of every file carrying the gated marker - and fails when one of them is missing from
    /// the list. On the day it was written it found three: two portal/dev-tools routes and one dev-tools
    /// route whose sibling WAS listed.
    /// </summary>
    [Fact]
    public void Gated_routes_list_covers_every_never_ship_route_the_census_derives()
    {
        var root = RepoRoot();
        var neverShip = NeverShipSources(root);
        neverShip.Should().NotBeEmpty(
            "nothing in this tree carries the gated marker - either the marker was renamed, in which case the byte scan is blind too, or the enumerator is broken");

        var pageRx = new Regex("@page\\s+\"([^\"]+)\"");
        var derived = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in neverShip)
        {
            foreach (Match m in pageRx.Matches(File.ReadAllText(f)))
            {
                var route = m.Groups[1].Value;
                if (!route.Contains('{')) derived.Add(route);   // templated routes are not literal-scannable
            }
        }
        derived.Should().NotBeEmpty("the @page reader must still match, or this oracle is vacuous");

        var listPath = Path.Combine(root, ".handoff", "gated-routes.txt");
        File.Exists(listPath).Should().BeTrue("the dev tree must carry the byte-scan needle list");
        var listed = File.ReadAllLines(listPath)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        listed.Should().NotBeEmpty("an emptied needle list makes the byte scan a no-op");

        // CENSUS-EXCEPTION(<this test>): an accepted REAL violation, declared at the site in the list
        // itself with a human-written reason. Per the convention frozen 2026-09-11 04:47 it is not an
        // exemption from measurement - it must be RE-ASSERTED as still matching, which is why a stale
        // exception (one naming a route the code no longer gates) fails below.
        var exceptionRx = new Regex(
            @"CENSUS-EXCEPTION\(Gated_routes_list_covers_every_never_ship_route_the_census_derives\)\s*:\s*(?<route>\S+)");
        var excepted = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in exceptionRx.Matches(File.ReadAllText(listPath))) excepted.Add(m.Groups["route"].Value);

        // The reader itself must still match, even when nothing is currently excepted. There are
        // zero declared exceptions today - /remediation-tuner was the only one and was measured and
        // listed on 2026-09-11 - so without this the exception path would be a mechanism that runs,
        // finds nothing, and could have stopped working years ago without anyone noticing.
        exceptionRx.IsMatch(
            "# CENSUS-EXCEPTION(Gated_routes_list_covers_every_never_ship_route_the_census_derives): /sample - reason")
            .Should().BeTrue("the CENSUS-EXCEPTION reader must still recognise a declared exception");

        _out.WriteLine($"never-ship sources={neverShip.Count} derived routes={derived.Count} " +
                       $"listed needles={listed.Count} declared exceptions={string.Join(", ", excepted)}");

        var stale = excepted.Where(r => !derived.Contains(r)).ToList();
        stale.Should().BeEmpty(
            "a declared exception must still name a route this census derives, or it is a reason for a violation that no longer exists; stale: " +
            string.Join(", ", stale));

        var missing = derived.Where(r => !listed.Contains(r) && !excepted.Contains(r)).ToList();
        missing.Should().BeEmpty(
            "every route declared by a never-ship page must be a needle in the byte scan's list, or carry a declared " +
            "CENSUS-EXCEPTION naming this test with a written reason; missing: " + string.Join(", ", missing));
    }

    /// <summary>Production sources carrying the gated marker. Mirrors the census's own enumerator.</summary>
    private static List<string> NeverShipSources(string root)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", ".git", ".vs", "node_modules", "Tests", "tools", "publish", "release",
            "evidence", "corpus", "lib", "llmck", "packages", "TestResults",
        };
        var found = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var sub in Directory.GetDirectories(dir))
            {
                if (excluded.Contains(Path.GetFileName(sub))) continue;
                stack.Push(sub);
            }
            foreach (var f in Directory.GetFiles(dir))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".razor" && ext != ".cs") continue;
                if (File.ReadAllText(f).Contains(Canary, StringComparison.Ordinal)) found.Add(f);
            }
        }
        return found;
    }

    // ── 5. defect 4: an emptied needle list must FAIL, not pass vacuously ───────────────────

    /// <summary>
    /// The byte-scan guard's route block used to iterate an empty list, print nothing and pass. Proved
    /// here by running the real <c>verify-community-build.ps1</c> against a COPY of the tree whose needle
    /// list has been commented out - never by editing the real list in place. The control arm uses the
    /// real list, so a red here is the emptiness and not the harness.
    /// </summary>
    [Fact]
    public void An_emptied_gated_routes_list_fails_the_byte_scan_guard()
    {
        var root = RepoRoot();
        var sandbox = ShipArtifactTests.NewSandboxRootFor("sqlt-verify-");
        Directory.CreateDirectory(Path.Combine(sandbox, "scripts"));
        Directory.CreateDirectory(Path.Combine(sandbox, ".handoff"));
        // Both halves of the guard: verify-community-build.ps1 dot-sources shipartifact.ps1 (the
        // bundle reader, ship-manifest reader and multi-pattern scanner) and REFUSES to render a
        // verdict without it. Copying only one half would make this test measure the missing file.
        foreach (var s in new[] { "verify-community-build.ps1", "shipartifact.ps1" })
            File.Copy(Path.Combine(root, "scripts", s), Path.Combine(sandbox, "scripts", s));

        // A stand-in publish directory with a manifest that binds it. -DllPath used to serve here,
        // but community mode now REFUSES it: a verdict about what ships cannot rest on a path typed
        // on a command line (ShipArtifactTests.A_community_verdict_is_refused_when_nothing_binds_the_artefact_to_a_build).
        // The point under test is still the needle list, not the bytes.
        var pub = Path.Combine(sandbox, "pub");
        Directory.CreateDirectory(Path.Combine(pub, "Config"));
        var dll = Path.Combine(pub, "SQLTriage.dll");
        File.WriteAllBytes(dll, new byte[] { 0x4D, 0x5A, 0x00, 0x00 });
        var freeBundle = Path.Combine(pub, "Config", "free-bundle.dat");
        File.WriteAllBytes(freeBundle, new byte[] { 1, 2, 3, 4 });
        var manifest = pub + ".sqltriage-ship-manifest.txt";
        File.WriteAllLines(manifest, new[]
        {
            "# sqltriage-ship-manifest v1",
            "P|ShipToken=tok-routes",
            "P|SQLTriageProfile=community",
            "P|Configuration=Debug",
            "P|PublishSingleFile=false",
            "P|PublishedSingleFileName=",
            "P|PublishDir=" + pub,
            "R|SQLTriage.dll||" + Sha256File(dll) + "|" + dll,
            "R|Config/free-bundle.dat||" + Sha256File(freeBundle) + "|" + freeBundle,
        });

        var realList = File.ReadAllLines(Path.Combine(root, ".handoff", "gated-routes.txt"));
        var listPath = Path.Combine(sandbox, ".handoff", "gated-routes.txt");

        File.WriteAllLines(listPath, realList);
        RunVerify(sandbox, pub, manifest).ExitCode.Should().Be(0,
            "control: the real needle list against a clean stand-in must pass, or the red below proves nothing");

        // Every line commented out: present, parseable, and yielding zero routes.
        File.WriteAllLines(listPath, realList.Select(l => l.TrimStart().StartsWith("#", StringComparison.Ordinal) ? l : "# " + l));
        var red = RunVerify(sandbox, pub, manifest);
        red.ExitCode.Should().Be(1, "a needle list that yields zero routes makes the fingerprint guard a no-op; it must fail closed");
        red.Output.Should().Contain("ZERO effective routes");
    }

    private static string Sha256File(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    private (int ExitCode, string Output) RunVerify(string sandbox, string publishDir, string manifest)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = sandbox,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(Path.Combine(sandbox, "scripts", "verify-community-build.ps1"));
        psi.ArgumentList.Add("-PublishDir");
        psi.ArgumentList.Add(publishDir);
        psi.ArgumentList.Add("-ShipManifest");
        psi.ArgumentList.Add(manifest);
        psi.ArgumentList.Add("-ExpectShipToken");
        psi.ArgumentList.Add("tok-routes");
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(180_000);
        _out.WriteLine(text);
        return (p.ExitCode, text);
    }

    // ── 6. the guard against a guard decaying into prose ─────────────────────────────────────

    /// <summary>
    /// <c>AutoUpdateService.cs:631</c> names a test that does not exist; an unverified test name in a
    /// comment reads like coverage it does not have. Every test of this class cited by the census
    /// sources must actually exist here.
    /// </summary>
    [Fact]
    public void Every_test_name_cited_in_the_census_sources_exists()
    {
        var root = RepoRoot();
        var cited = new SortedSet<string>(StringComparer.Ordinal);
        // A test name in this repo is unmistakable by shape: CamelCase start, then underscore-separated
        // words. Matching the SHAPE rather than the proximity to the class name means a citation that
        // wraps, or sits three lines from the class name, is still read - a proximity parser silently
        // stops matching and this whole guard becomes decoration.
        // The lowercase letter after the leading capital is what separates a test name
        // (The_census_is_wired...) from a preprocessor symbol (SQLT_NO_REPORT_EXEC_SUMMARY), which
        // otherwise has the same underscore shape and would make this test fail on a define.
        var rx = new Regex(@"(?<![A-Za-z0-9_])(?<name>[A-Z][a-z0-9][A-Za-z0-9]*(?:_[A-Za-z0-9]+){3,})");
        foreach (var rel in new[]
                 {
                     Path.Combine("scripts", "census-community-surface.ps1"),
                     Path.Combine("scripts", "emit-build-surface.ps1"),
                     Path.Combine("scripts", "verify-community-build.ps1"),
                     Path.Combine("scripts", "shipartifact.ps1"),
                     "buildprofile.targets",
                     Path.Combine(".handoff", "gated-routes.txt"),
                 })
        {
            var path = Path.Combine(root, rel);
            if (!File.Exists(path)) continue;
            var text = Regex.Replace(File.ReadAllText(path), @"\s+", " ");   // a wrapped citation is still a citation
            foreach (Match m in rx.Matches(text)) cited.Add(m.Groups["name"].Value);
        }

        cited.Should().NotBeEmpty(
            "the census sources must name the tests that enforce them - if this is empty the citation reader has stopped matching, " +
            "and the comments have quietly become prose again");

        // Existence is checked across the WHOLE test assembly, not just this class: buildprofile.targets
        // already cites a test in another class, and a citation is a citation wherever it points.
        var actual = typeof(CommunitySurfaceCensusTests).Assembly
            .GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(m => m.GetCustomAttributes(typeof(FactAttribute), false).Length > 0
                     || m.GetCustomAttributes(typeof(TheoryAttribute), false).Length > 0)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        _out.WriteLine("cited: " + string.Join(", ", cited));
        var ghosts = cited.Where(c => !actual.Contains(c)).ToList();
        ghosts.Should().BeEmpty("a comment naming a test that does not exist is not a guard; ghosts: " + string.Join(", ", ghosts));
    }
}
