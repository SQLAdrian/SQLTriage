/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

/// <summary>
/// THE INVARIANT, stated here because this is the file that measures it:
///
/// <para><b>A guard must scan the artefact that SHIPS, prove that artefact is the one this publish
/// produced, and FAIL LOUDLY when it cannot be scanned at all. ABSENT, EMPTY and UNPARSEABLE are
/// failures - never skips.</b></para>
///
/// <para>WHY THIS CLASS EXISTS. <c>scripts\verify-community-build.ps1</c> is what stands between gated
/// code and the PUBLIC repo. Until 2026-09-11 it chose its scan target BY NAME - an <c>obj\</c>
/// intermediate it did not build - re-read each path per check, and treated a missing published copy as
/// a silent degrade. Three consequences were measured that day: a concurrent full-profile build
/// reddened a clean community publish with 61 route hits and ZERO canary hits (two reads of one path,
/// two files, one verdict); the <c>-ExpectFull</c> positive control was satisfiable by the WRONG file;
/// and a genuine 174 MB FULL-profile Release single-file build passed as community, exit 0, because in
/// the ship configuration there is no loose DLL to scan at all.</para>
///
/// <para>THE TWO ARMS THIS CLASS HOLDS. Three layered mechanisms were ruled (2026-09-11 18:40).
/// <see cref="CommunitySurfaceCensusTests"/> holds the primary one, the compile-time census. This class
/// holds the other two: the HASH BINDING (every file the build produced must be found inside the
/// artefact that ships, by SHA-256, or the publish fails) and the EXTRACTION arm (a compressed
/// single-file bundle is inflated and scanned entry by entry, because a raw byte scan of it returns the
/// same answer for a clean build and a gated one).</para>
///
/// <para>WHAT RUNS THEM, AND WHAT GOES RED WHEN IT DOES NOT. <c>buildprofile.targets</c> target
/// <c>SQLTriageVerifyCommunityPublish</c>, <c>AfterTargets="Publish"</c>, community only, no
/// <c>ContinueOnError</c>: it emits the ship manifest from <c>@(FilesToBundle)</c> and
/// <c>@(ResolvedFileToPublish)</c> - MSBuild's own item lists, hashed by the <c>GetFileHash</c> task in
/// the same build - then invokes the guard with that manifest and a per-invocation token. Delete the
/// emission and <see cref="The_ship_manifest_is_emitted_by_the_community_publish_target"/> goes red.
/// Weaken any fail-closed arm and the reddening tests below go red.</para>
///
/// <para>EVERY GREEN FROM A FAIL-TOWARD-CLEAN GUARD IS INADMISSIBLE UNTIL THE INSTRUMENT IS SHOWN TO GO
/// RED. Every reddening test here carries a paired CONTROL arm on the same fixture, so a red proves the
/// mutation and not the harness.</para>
///
/// <para>THE NAME TRAP, encoded by
/// <see cref="Every_bundle_entry_is_scanned_not_only_the_one_that_carries_the_expected_name"/>. The
/// shipped bundle carries TWO images of this application - a 14 MB <c>SQLTriage.dll</c> and a 240 MB
/// <c>SQLTriage.r2r.dll</c>, measured 2026-09-11 - and the canary lives in exactly one of them. An
/// extraction arm that picked its entry by filename would be one SDK change away from being the very
/// defect this lane exists to fix. So no entry is picked: every entry is scanned, and the entries that
/// matter are identified by the SHA-256 the build recorded, never by a name.</para>
///
/// <para>This file ships to the public repo (<c>.handoff\.publicallow</c> allows <c>Tests/**</c>), so
/// the canary token is assembled from parts here exactly as it is in the guard, and no gated route
/// literal appears.</para>
/// </summary>
public class ShipArtifactTests
{
    private readonly ITestOutputHelper _out;
    public ShipArtifactTests(ITestOutputHelper output) => _out = output;

    // Assembled from parts: this source ships public, and a contiguous copy of the token would itself
    // be the fingerprint the guard exists to keep out.
    private static readonly string Canary = "SQLT-GATED-DNA-" + "NEVERSHIP";

    /// <summary>
    /// Sandboxes live under the repo's <c>obj\</c>, NOT under <c>%TEMP%</c>.
    /// MEASURED 2026-09-11: an anti-malware AMSI hook can refuse to parse
    /// <c>scripts\shipartifact.ps1</c> from <c>%TEMP%</c> and from <c>C:\temp</c>
    /// ("This script contains malicious content and has been blocked by your antivirus
    /// software") - the C# the fast scanner compiles at runtime looks like a loader -
    /// while the identical bytes load fine from the repo. A harness that copies the guard
    /// into a blocked directory measures the anti-malware product, not the guard.
    /// <c>obj\</c> is gitignored (<c>.gitignore:85</c>) and MSBuild does not glob it.
    /// </summary>
    private static string NewSandboxRoot(string prefix)
    {
        var dir = Path.Combine(RepoRoot(), "obj", "testsandbox", prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Shared with <see cref="CommunitySurfaceCensusTests"/>, which has the same AV constraint.</summary>
    internal static string NewSandboxRootFor(string prefix) => NewSandboxRoot(prefix);

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "SQLTriage.csproj"))) d = d.Parent;
        d.Should().NotBeNull("the tests must run from inside the repo");
        return d!.FullName;
    }

    // ── 1. the mechanism is WIRED ────────────────────────────────────────────────────────────

    /// <summary>
    /// A guard nobody invokes is not a guard. This asserts the publish target still emits the ship
    /// manifest from MSBuild's OWN item lists and still hands it to the guard with a token - and that
    /// it no longer hands over the <c>obj\</c> intermediate path that started all of this.
    /// </summary>
    [Fact]
    public void The_ship_manifest_is_emitted_by_the_community_publish_target()
    {
        var targets = File.ReadAllText(Path.Combine(RepoRoot(), "buildprofile.targets"));
        // XML COMMENTS ARE STRIPPED FIRST. The target's own comment explains that it carries no
        // ContinueOnError and no longer passes -DllPath, so a raw string match reads the prose and
        // reports the opposite of the truth. A string match is not evidence about the world: the
        // assertions below must measure the ELEMENTS, not the paragraph next to them. (This test
        // failed exactly that way on its first run, 2026-09-11.)
        var stripped = Regex.Replace(targets, "<!--.*?-->", " ", RegexOptions.Singleline);
        var flat = Regex.Replace(stripped, @"\s+", " ");

        var i = flat.IndexOf("Name=\"SQLTriageVerifyCommunityPublish\"", StringComparison.Ordinal);
        i.Should().BeGreaterThan(-1, "the publish-time gate must still exist");
        var end = flat.IndexOf("</Target>", i, StringComparison.Ordinal);
        end.Should().BeGreaterThan(i);
        var body = flat.Substring(i, end - i);

        body.Should().Contain("AfterTargets=\"Publish\"", "the gate must fire on every publish");
        body.Should().Contain("'$(SQLTriageProfile)' == 'community'", "and only for the community profile");
        body.Should().NotContain("ContinueOnError", "a violation must FAIL the publish, not warn");

        body.Should().Contain("GetFileHash", "the binding is a hash taken in this build, not a path");
        // BOTH lists are needed, and the reason is measured rather than assumed: @(FilesToBundle) is
        // the CANDIDATE list, not the ingested one - the bundler hands some back
        // (@(_FilesExcludedFromBundle)) to be published loose, and those reappear in
        // @(ResolvedFileToPublish). On a real Release publish 2026-09-11: 39 produced files, 5 inside
        // the bundle, 34 loose. Emitting only one of the two lists cannot bind what actually ships.
        body.Should().Contain("@(FilesToBundle)", "the files offered to the bundler are what bind the single-file case");
        body.Should().Contain("@(ResolvedFileToPublish)", "the files copied into PublishDir are what bind everything the bundler did not take");
        body.Should().Contain("%(NuGetPackageId)", "ownership comes from MSBuild's own provenance metadata, never from a filename");
        body.Should().Contain("WriteLinesToFile", "the manifest must actually be written");
        body.Should().Contain("-ShipManifest", "the guard must receive the manifest");
        body.Should().Contain("-ExpectShipToken", "and the per-invocation token, so a stale manifest is refused");

        body.Should().NotContain("-DllPath",
            "the gate used to hand the guard obj\\<cfg>\\<tfm>\\<rid>\\SQLTriage.dll - shared mutable state between " +
            "the community and full profiles, and the file a concurrent full build rewrote underneath a clean publish");
        body.Should().NotContain("$(IntermediateOutputPath)",
            "no part of the verdict may be pinned to the intermediate any more");
    }

    // ── 2. the binding: ABSENT, STALE and UNMATCHED are failures ─────────────────────────────

    /// <summary>
    /// "There was no manifest, so we scanned whatever DLL was lying around" is the behaviour that let a
    /// full-profile Release build pass as community. A community publish must declare what it produced.
    /// Control arm first, on the same fixture, so the red below is the missing manifest and not the tree.
    /// </summary>
    [Fact]
    public void The_guard_refuses_a_publish_dir_with_no_ship_manifest()
    {
        var sb = NewSandbox();
        var pub = MakeLoosePublishDir(sb, canaryInAssembly: false);
        var manifest = WriteLooseManifest(sb, pub, token: "tok-aaaa");

        var ok = RunVerify(sb, "-PublishDir", pub, "-ShipManifest", manifest, "-ExpectShipToken", "tok-aaaa");
        ok.ExitCode.Should().Be(0, "control: a clean publish with its own manifest must pass, or the red below proves nothing. " + ok.Output);

        File.Delete(manifest);
        var red = RunVerify(sb, "-PublishDir", pub, "-ShipManifest", manifest, "-ExpectShipToken", "tok-aaaa");
        red.ExitCode.Should().Be(1, "a publish that declares nothing cannot be certified");
        red.Output.Should().Contain("ABSENT");

        // ...and the same is true when no manifest argument is passed at all: the guard resolves the
        // sibling location itself rather than falling back to scanning a file it cannot vouch for.
        var red2 = RunVerify(sb, "-PublishDir", pub);
        red2.ExitCode.Should().Be(1, "an unbound publish-dir verdict is exactly the false green this lane removed");
    }

    /// <summary>
    /// A manifest is only evidence about the build that minted its token. Two publishes writing to one
    /// directory, or a manifest left over from an earlier run, must produce a loud red rather than a
    /// verdict about a publish the manifest does not describe.
    /// </summary>
    [Fact]
    public void The_guard_refuses_a_manifest_whose_token_does_not_match()
    {
        var sb = NewSandbox();
        var pub = MakeLoosePublishDir(sb, canaryInAssembly: false);
        var manifest = WriteLooseManifest(sb, pub, token: "tok-right");

        RunVerify(sb, "-PublishDir", pub, "-ShipManifest", manifest, "-ExpectShipToken", "tok-right")
            .ExitCode.Should().Be(0, "control: the token the publish minted must be accepted");

        var red = RunVerify(sb, "-PublishDir", pub, "-ShipManifest", manifest, "-ExpectShipToken", "tok-other");
        red.ExitCode.Should().Be(1);
        red.Output.Should().Contain("token mismatch");

        // A manifest with no token at all is not a weaker manifest - it is no binding.
        var untokened = Path.Combine(sb, "untokened.txt");
        File.WriteAllLines(untokened, File.ReadAllLines(manifest).Where(l => !l.StartsWith("P|ShipToken=", StringComparison.Ordinal)));
        var red2 = RunVerify(sb, "-PublishDir", pub, "-ShipManifest", untokened);
        red2.ExitCode.Should().Be(1);
        red2.Output.Should().Contain("no ShipToken");
    }

    /// <summary>
    /// THE BINDING IS THE WHOLE CONTROL. Three different <c>SQLTriage.dll</c> of three different sizes
    /// can exist at any time (14,462,976 inside the shipped bundle; 14,624,256 in bin;
    /// 10,903,040 loose in a lane's publish folder, measured 2026-09-11). A binding that accepts "the
    /// DLL nearby" recreates the defect with more ceremony, so the file in the publish directory must
    /// hash to what THIS build produced.
    /// </summary>
    [Fact]
    public void The_guard_reddens_when_a_produced_file_is_not_inside_the_shipped_artefact()
    {
        var sb = NewSandbox();
        var pub = MakeLoosePublishDir(sb, canaryInAssembly: false);
        var manifest = WriteLooseManifest(sb, pub, token: "tok-bind");

        RunVerify(sb, "-PublishDir", pub, "-ShipManifest", manifest, "-ExpectShipToken", "tok-bind")
            .ExitCode.Should().Be(0, "control: an unmutated publish must bind");

        // Swap the shipped assembly for different bytes - the shape of "a different build's output
        // turned up in the publish directory". Note it carries NO canary: the point under test is the
        // binding, not the scan, so a red here cannot be the canary check firing instead.
        File.WriteAllBytes(Path.Combine(pub, "SQLTriage.dll"), FakeAssembly("a different build entirely"));
        var red = RunVerify(sb, "-PublishDir", pub, "-ShipManifest", manifest, "-ExpectShipToken", "tok-bind");
        red.ExitCode.Should().Be(1);
        red.Output.Should().Contain("BINDING FAILED");

        // ...and an absent produced file is the same failure, not a skip.
        File.Delete(Path.Combine(pub, "SQLTriage.dll"));
        var red2 = RunVerify(sb, "-PublishDir", pub, "-ShipManifest", manifest, "-ExpectShipToken", "tok-bind");
        red2.ExitCode.Should().Be(1);
        red2.Output.Should().Contain("BINDING FAILED");
    }

    // ── 3. the extraction arm ────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE CORE OF THE RELEASE ARM. Every Release build of this project is a COMPRESSED single-file
    /// bundle (<c>SQLTriage.csproj</c> sets <c>PublishSingleFile</c> and
    /// <c>EnableCompressionInSingleFile</c> for every Release configuration), so a raw byte scan of the
    /// shipped exe scores zero whether the build is clean or gated - measured on three real artefacts
    /// 2026-09-11, raw hits 0/0/0 while extraction gave 1/0/1. This fixture reproduces that in
    /// miniature: the canary exists ONLY inside a deflated payload, so a guard that scans the file as
    /// it sits cannot see it and a guard that inflates can.
    /// </summary>
    [Fact]
    public void The_guard_scans_the_bundle_payload_not_the_raw_exe()
    {
        var sb = NewSandbox();

        var cleanPayload = Encoding.Unicode.GetBytes("nothing to see here, this is an ordinary assembly body");
        var clean = MakeBundlePublishDir(sb, new[] { ("SQLTriage.dll", cleanPayload, true) });
        RunVerify(sb, "-PublishDir", clean.PublishDir, "-ShipManifest", clean.Manifest, "-ExpectShipToken", clean.Token)
            .ExitCode.Should().Be(0, "control: a clean bundle must pass, or the red below proves nothing");

        var dirtyPayload = Encoding.Unicode.GetBytes("harmless prefix " + Canary + " harmless suffix");
        var dirty = MakeBundlePublishDir(sb, new[] { ("SQLTriage.dll", dirtyPayload, true) });

        // The premise of the whole arm, asserted rather than assumed: the token is NOT visible in the
        // file as it sits on disk. If this ever fails the fixture has stopped compressing and the test
        // below would be proving nothing.
        var raw = File.ReadAllBytes(Path.Combine(dirty.PublishDir, "SQLTriage.exe"));
        Contains(raw, Encoding.Unicode.GetBytes(Canary)).Should().BeFalse("the payload must be deflated, or this fixture is not the ship shape");
        Contains(raw, Encoding.UTF8.GetBytes(Canary)).Should().BeFalse();

        var red = RunVerify(sb, "-PublishDir", dirty.PublishDir, "-ShipManifest", dirty.Manifest, "-ExpectShipToken", dirty.Token);
        red.ExitCode.Should().Be(1, "the shipped bytes carry the canary; a guard that cannot see it is the defect");
        red.Output.Should().Contain("canary");
    }

    /// <summary>
    /// THE NAME TRAP. The real bundle carries <c>SQLTriage.dll</c> (14,462,976 bytes, one canary hit)
    /// and <c>SQLTriage.r2r.dll</c> (240,381,952 bytes, zero hits). An implementation that extracted
    /// "the SQLTriage.dll entry" would have been correct on today's SDK and blind on the next one. Here
    /// the entry the manifest binds is CLEAN and a differently-named sibling carries the token: a
    /// name-pinned scanner passes, a scan-everything one reddens.
    /// </summary>
    [Fact]
    public void Every_bundle_entry_is_scanned_not_only_the_one_that_carries_the_expected_name()
    {
        var sb = NewSandbox();
        var ownedClean = Encoding.Unicode.GetBytes("the bound entry is perfectly clean");
        var sibling = Encoding.Unicode.GetBytes("sibling image carrying " + Canary + " where nobody looks");

        var fx = MakeBundlePublishDir(sb, new[]
        {
            ("SQLTriage.dll", ownedClean, true),
            ("SQLTriage.r2r.dll", sibling, true),
        });

        var red = RunVerify(sb, "-PublishDir", fx.PublishDir, "-ShipManifest", fx.Manifest, "-ExpectShipToken", fx.Token);
        red.ExitCode.Should().Be(1, "the canary ships inside the artefact; which entry carries it is not the guard's business");
        red.Output.Should().Contain("canary");
        red.Output.Should().Contain("r2r", "the failure must name where it was found, or nobody can act on it");
    }

    /// <summary>
    /// FAIL CLOSED ON A FORMAT YOU DO NOT UNDERSTAND. Both artefacts measured so far report bundle
    /// major 6; the per-entry compressed-size field exists only from major 6. A future SDK emitting
    /// major 7 shifts every field, and a parser that carries on produces GARBAGE, NOT AN ERROR - a
    /// plausible-looking list of entries that were never really there, scanned clean. Nobody has a
    /// major-7 artefact, so one is synthesised.
    /// </summary>
    [Fact]
    public void The_guard_reddens_on_an_unrecognised_bundle_major()
    {
        var sb = NewSandbox();
        var payload = Encoding.Unicode.GetBytes("ordinary body");

        var six = MakeBundlePublishDir(sb, new[] { ("SQLTriage.dll", payload, true) }, major: 6);
        RunVerify(sb, "-PublishDir", six.PublishDir, "-ShipManifest", six.Manifest, "-ExpectShipToken", six.Token)
            .ExitCode.Should().Be(0, "control: the major this SDK emits must parse");

        var seven = MakeBundlePublishDir(sb, new[] { ("SQLTriage.dll", payload, true) }, major: 7);
        var red = RunVerify(sb, "-PublishDir", seven.PublishDir, "-ShipManifest", seven.Manifest, "-ExpectShipToken", seven.Token);
        red.ExitCode.Should().Be(1);
        red.Output.Should().Contain("unrecognised bundle major");
    }

    /// <summary>
    /// Bounds are checked BEFORE any inflate, so a corrupt or hostile entry table is a named failure
    /// rather than an exception thrown halfway through a scan whose partial result nobody reads.
    /// </summary>
    [Fact]
    public void The_guard_reddens_on_an_entry_that_claims_bytes_beyond_the_file()
    {
        var sb = NewSandbox();
        var payload = Encoding.Unicode.GetBytes("ordinary body");

        var good = MakeBundlePublishDir(sb, new[] { ("SQLTriage.dll", payload, true) });
        RunVerify(sb, "-PublishDir", good.PublishDir, "-ShipManifest", good.Manifest, "-ExpectShipToken", good.Token)
            .ExitCode.Should().Be(0, "control: an in-bounds entry table must parse");

        var bad = MakeBundlePublishDir(sb, new[] { ("SQLTriage.dll", payload, true) }, corruptFirstEntryOffset: true);
        var red = RunVerify(sb, "-PublishDir", bad.PublishDir, "-ShipManifest", bad.Manifest, "-ExpectShipToken", bad.Token);
        red.ExitCode.Should().Be(1);
        red.Output.Should().Contain("beyond the file length");
    }

    // ── 4. the positive control that used to be a false green ────────────────────────────────

    /// <summary>
    /// <c>-ExpectFull</c> is the control that validates the canary instrument. It used to ask "does ANY
    /// of these targets contain the token" and stop at the first hit, so a canary-free PUBLISHED
    /// assembly sitting beside a canary-bearing <c>obj\</c> intermediate satisfied it and the script
    /// printed OK. A positive control satisfiable by the wrong file validates nothing. It now names ONE
    /// artefact.
    /// </summary>
    [Fact]
    public void The_expect_full_control_names_one_artefact_and_cannot_be_satisfied_by_another()
    {
        var sb = NewSandbox();
        var intermediate = Path.Combine(sb, "obj-SQLTriage.dll");
        File.WriteAllBytes(intermediate, FakeAssembly("a full build " + Canary + " with the token"));

        // Control: named directly, the instrument finds the token and the control passes.
        RunVerify(sb, "-ExpectFull", "-DllPath", intermediate)
            .ExitCode.Should().Be(0, "control: the canary IS in the named artefact");

        // The Run Y pairing: a canary-free published artefact, a canary-bearing intermediate beside it.
        var pub = MakeLoosePublishDir(sb, canaryInAssembly: false);
        var manifest = WriteLooseManifest(sb, pub, token: "tok-full", profile: "full");
        var red = RunVerify(sb, "-ExpectFull", "-PublishDir", pub, "-DllPath", intermediate, "-ShipManifest", manifest, "-ExpectShipToken", "tok-full");
        red.ExitCode.Should().Be(1,
            "the published artefact has no canary; the control must not be rescued by the intermediate it was also handed");
        red.Output.Should().Contain("NOT found");

        // And a control with no artefact at all is not a weaker control - it is no control.
        RunVerify(sb, "-ExpectFull").ExitCode.Should().Be(1, "a control with no target is not a control");
    }

    // ── 5. the instrument itself ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A control that cannot reproduce cannot refute: every "not found" this guard prints is only
    /// meaningful once the scanner has been shown, IN THAT RUN, able to display a positive with THOSE
    /// needles - which are read from a file that may have been edited since anyone last exercised them.
    /// The third arm is the reddening one: handed a scanner that genuinely cannot see a needle, the
    /// self-control must say so rather than report a comfortable zero.
    /// </summary>
    [Fact]
    public void The_instrument_self_control_must_be_able_to_display_a_positive()
    {
        var result = RunHarness(@"
$set = New-SqltNeedleSet @(@{ Text = 'SQLT-GATED-DNA-' + 'NEVERSHIP'; Standalone = $false }, @{ Text = '/a-gated-looking-route'; Standalone = $true })
$fault = Test-SqltScannerInstrument $set 'harness'
if ($fault) { Write-Host ""ARM1 FAIL: $fault"" } else { Write-Host 'ARM1 OK' }

# A needle set whose declared Owner does not correspond to its bytes: the scanner is now genuinely
# unable to find what the control asks for, which is exactly the blindness the control exists to catch.
$blind = @{
  # ,$nb - the unary comma keeps this a ONE-element array OF byte[]. Without it
  # PowerShell enumerates the byte array into 21 separate bytes and the scanner is
  # handed 21 one-byte needles, which is a different bug from the one under test.
  Needles    = [byte[][]]@(, [System.Text.Encoding]::Unicode.GetBytes('zzzz-not-present-zzzz'))
  Strides    = @(2)
  Standalone = @($false)
  Owner      = @('a-needle-the-scanner-cannot-see')
}
$fault2 = Test-SqltScannerInstrument $blind 'harness-blind'
if ($fault2) { Write-Host 'ARM2 REDDENS' } else { Write-Host 'ARM2 FAIL: a blind scanner passed its own self-control' }
");
        result.Output.Should().Contain("ARM1 OK", "the control must pass on a working scanner: " + result.Output);
        result.Output.Should().Contain("ARM2 REDDENS", "the control must fail on a scanner that cannot display a positive");
    }

    /// <summary>
    /// The fast scanner exists because the pure-PowerShell one took 13m17s over a single 8.7 MB
    /// assembly, which made scanning the ~400 MB the shipped bundle inflates to unaffordable - and
    /// "unaffordable" is how "we do not scan it" survived. Speed is only allowed to buy coverage if the
    /// two implementations answer identically, including the boundary-aware rule and the chunk-edge
    /// cases, so they are compared on adversarial buffers rather than trusted.
    /// </summary>
    [Fact]
    public void The_fast_scanner_agrees_with_the_reference_scanner()
    {
        var result = RunHarness(@"
$texts = @('SQLT-GATED-DNA-' + 'NEVERSHIP', '/alpha', '/alpha-beta', '/a', 'zz')
$set = New-SqltNeedleSet @($texts | ForEach-Object { @{ Text = $_; Standalone = $true } })
$rand = New-Object System.Random 20260911
$bad = 0; $cases = 0
foreach ($n in @(0, 1, 2, 3, 64, 5000)) {
    $buf = New-Object byte[] $n
    $rand.NextBytes($buf)
    $cases++
    $fast = Measure-SqltNeedles -Data $buf -Length $n -NeedleSet $set
    $ref  = Measure-SqltNeedles -Data $buf -Length $n -NeedleSet $set -ForceReference
    foreach ($t in $texts) { if ($fast[$t] -ne $ref[$t]) { $bad++; Write-Host ""MISMATCH random n=$n '$t' fast=$($fast[$t]) ref=$($ref[$t])"" } }
}
# Adversarial: every needle at a buffer edge, adjacent to itself, and followed by a token character
# (which must SUPPRESS the hit) and by a non-token character (which must not).
foreach ($t in $texts) {
    foreach ($tail in @('', 'x', ' ', '-', '/', '_9')) {
        foreach ($enc in @('u16', 'u8')) {
            $s = ""$t$tail""
            $buf = if ($enc -eq 'u16') { [System.Text.Encoding]::Unicode.GetBytes($s) } else { [System.Text.Encoding]::UTF8.GetBytes($s) }
            $cases++
            $fast = Measure-SqltNeedles -Data $buf -Length $buf.Length -NeedleSet $set
            $ref  = Measure-SqltNeedles -Data $buf -Length $buf.Length -NeedleSet $set -ForceReference
            foreach ($k in $texts) { if ($fast[$k] -ne $ref[$k]) { $bad++; Write-Host ""MISMATCH '$s' $enc '$k' fast=$($fast[$k]) ref=$($ref[$k])"" } }
        }
    }
}
# The signature search is the other fast path: same answer as the reference walk.
$needle = [byte[]]@(1,2,3,4)
$buf = New-Object byte[] 4096
$rand.NextBytes($buf)
foreach ($at in @(0, 17, 2048, 4092)) { $buf[$at]=1; $buf[$at+1]=2; $buf[$at+2]=3; $buf[$at+3]=4 }
$f = @(Find-SqltByteOffsets $buf $needle)
$r = @(Find-SqltByteOffsets $buf $needle -ForceReference)
$cases++
if (($f -join ',') -ne ($r -join ',')) { $bad++; Write-Host ""MISMATCH FindAll fast=$($f -join ',') ref=$($r -join ',')"" }
Write-Host ""CASES=$cases MISMATCHES=$bad""
");
        result.Output.Should().Contain("MISMATCHES=0", "a faster scanner that answers differently is a different guard: " + result.Output);
        result.Output.Should().NotContain("CASES=0");
    }

    // ── 6. the guard must SAY what it scanned ────────────────────────────────────────────────

    /// <summary>
    /// For eight weeks this guard printed neither what it scanned nor why, and nobody could tell from
    /// the output that it had been reading an <c>obj\</c> intermediate all along. A green line that
    /// names the artefact and its provenance is the difference between "the guard passed" and "the
    /// guard ran".
    /// </summary>
    [Fact]
    public void The_guard_reports_what_it_scanned_and_its_provenance_on_success()
    {
        var sb = NewSandbox();
        var pub = MakeLoosePublishDir(sb, canaryInAssembly: false);
        var manifest = WriteLooseManifest(sb, pub, token: "tok-say");

        var ok = RunVerify(sb, "-PublishDir", pub, "-ShipManifest", manifest, "-ExpectShipToken", "tok-say");
        ok.ExitCode.Should().Be(0, ok.Output);
        ok.Output.Should().Contain("PROVENANCE BOUND");
        ok.Output.Should().Contain("byte(s)");
        ok.Output.Should().Contain("found in the shipped artefact by SHA-256");
        ok.Output.Should().Contain("route needles");
        ok.Output.Should().Contain("OK - community");
    }

    /// <summary>
    /// A guard that scanned nothing must never report a pass. This is a NAMED condition with its own
    /// message, not a threshold on a count - a manifest listing only third-party files says nothing
    /// about the profile boundary however many bytes it moves.
    /// </summary>
    [Fact]
    public void A_manifest_that_binds_nothing_this_build_produced_cannot_pass()
    {
        var sb = NewSandbox();
        var pub = MakeLoosePublishDir(sb, canaryInAssembly: false);
        var manifest = Path.Combine(sb, "third-party-only.txt");
        var dll = Path.Combine(pub, "SQLTriage.dll");
        File.WriteAllLines(manifest, new[]
        {
            "# sqltriage-ship-manifest v1",
            "P|ShipToken=tok-empty",
            "P|SQLTriageProfile=community",
            "P|Configuration=Debug",
            "P|PublishSingleFile=false",
            "P|PublishDir=" + pub,
            // Every row carries a NuGetPackageId, i.e. MSBuild says a package produced it, not us.
            "R|SQLTriage.dll|Some.Package|" + Sha256File(dll) + "|" + dll,
        });

        var red = RunVerify(sb, "-PublishDir", pub, "-ShipManifest", manifest, "-ExpectShipToken", "tok-empty");
        red.ExitCode.Should().Be(1);
        red.Output.Should().Contain("NO file that this build produced");
    }

    // -- 5. the three conditions the cold gate returned SHIP WITH CONDITIONS on ---------------

    /// <summary>
    /// B2. The guard used to accept a bare <c>-DllPath</c> in community mode, print PROVENANCE
    /// UNBOUND, and then render the ordinary green verdict anyway. MEASURED 2026-09-11 by the cold
    /// gate: <c>-DllPath full-r2r.dll</c> - the 240 MB ReadyToRun image extracted from a genuine
    /// 174 MB FULL-profile exe - produced "OK - community", exit 0. A community verdict is a claim
    /// about what SHIPPED; a path typed on a command line is bound to no build, so community mode
    /// refuses it. The control arm runs FIRST on the same fixture, so the two reds below are the
    /// refusal and not the sandbox.
    /// </summary>
    [Fact]
    public void A_community_verdict_is_refused_when_nothing_binds_the_artefact_to_a_build()
    {
        var sb = NewSandbox();
        var pub = MakeLoosePublishDir(sb, canaryInAssembly: false);
        var manifest = WriteLooseManifest(sb, pub, token: "tok-bind");
        var dll = Path.Combine(pub, "SQLTriage.dll");

        var control = RunVerify(sb, "-PublishDir", pub, "-ShipManifest", manifest, "-ExpectShipToken", "tok-bind");
        control.ExitCode.Should().Be(0, "control: " + control.Output);

        // Exactly the gate's invocation shape: one named file, no publish directory.
        var byPath = RunVerify(sb, "-DllPath", dll);
        byPath.ExitCode.Should().Be(1, byPath.Output);
        byPath.Output.Should().Contain("-DllPath is REFUSED in community mode");
        byPath.Output.Should().NotContain("OK - community", "the whole defect was that this path rendered a green community verdict");

        // A manifest alone names what was produced but gives nothing to scan it in.
        var byManifest = RunVerify(sb, "-ShipManifest", manifest);
        byManifest.ExitCode.Should().Be(1, byManifest.Output);
        byManifest.Output.Should().Contain("REFUSED in community mode");
        byManifest.Output.Should().NotContain("OK - community");
    }

    /// <summary>
    /// B2, second half. The verdict line said "no canary, no gated assets, free bundle present" on
    /// every green run - including runs where no publish directory existed and checks 2-4 never
    /// executed. A verdict must never assert a check it did not run, so the clauses are now
    /// assembled from what actually ran and name the directory they ran against.
    /// </summary>
    [Fact]
    public void The_verdict_names_only_the_checks_that_ran()
    {
        var sb = NewSandbox();
        var pub = MakeLoosePublishDir(sb, canaryInAssembly: false);
        var manifest = WriteLooseManifest(sb, pub, token: "tok-verdict");

        var ok = RunVerify(sb, "-PublishDir", pub, "-ShipManifest", manifest, "-ExpectShipToken", "tok-verdict");
        ok.ExitCode.Should().Be(0, ok.Output);
        ok.Output.Should().NotContain("OK - community (no canary, no gated assets, free bundle present)",
            "that sentence was printed whether or not the publish-tree checks ran");
        ok.Output.Should().Contain("no canary in", "the canary clause must say what it was measured over");
        ok.Output.Should().Contain("free bundle present in " + pub,
            "checks 2-4 ran against THIS directory, and the verdict must name it rather than assert them in the abstract");
    }

    /// <summary>
    /// B3. The forward binding - every file this build produced is inside what ships - says nothing
    /// about a file that is in the publish directory and in NO manifest row. MEASURED 2026-09-11 by
    /// the cold gate: a canary-bearing SQLTriage.dll dropped beside a clean exe was never a scan unit
    /// in either branch, and the guard reported 524 units, binding 48/48, OK - community, exit 0.
    /// Unprovenanced executable bytes in a directory we are asked to certify are now refused.
    /// </summary>
    [Fact]
    public void The_guard_reddens_on_an_undeclared_executable_in_the_publish_directory()
    {
        var sb = NewSandbox();
        var pub = MakeLoosePublishDir(sb, canaryInAssembly: false);
        var manifest = WriteLooseManifest(sb, pub, token: "tok-undeclared");

        var control = RunVerify(sb, "-PublishDir", pub, "-ShipManifest", manifest, "-ExpectShipToken", "tok-undeclared");
        control.ExitCode.Should().Be(0, "control: " + control.Output);

        // No canary in it at all: the point is provenance, not content. A file the manifest never
        // mentioned is code nobody can say this build produced.
        File.WriteAllBytes(Path.Combine(pub, "Extra.dll"), FakeAssembly("an ordinary body with no token"));

        var red = RunVerify(sb, "-PublishDir", pub, "-ShipManifest", manifest, "-ExpectShipToken", "tok-undeclared");
        red.ExitCode.Should().Be(1, red.Output);
        red.Output.Should().Contain("UNDECLARED EXECUTABLE IN THE PUBLISH DIRECTORY");
        red.Output.Should().Contain("Extra.dll");
    }

    /// <summary>
    /// B3, the other half - and the one that makes "the token is nowhere in what ships" a measurement
    /// rather than a scope. The payload here is NOT code-shaped, so the extension rule above cannot
    /// catch it: only scanning every file in the publish directory can. Three arms, so neither red is
    /// ambiguous: clean, then a benign undeclared file (still green, but reported as undeclared),
    /// then the same file carrying the canary.
    /// </summary>
    [Fact]
    public void An_undeclared_file_in_the_publish_directory_is_still_scanned()
    {
        var sb = NewSandbox();
        var pub = MakeLoosePublishDir(sb, canaryInAssembly: false);
        var manifest = WriteLooseManifest(sb, pub, token: "tok-scanall");

        var control = RunVerify(sb, "-PublishDir", pub, "-ShipManifest", manifest, "-ExpectShipToken", "tok-scanall");
        control.ExitCode.Should().Be(0, "control: " + control.Output);
        control.Output.Should().NotContain("not declared by the ship manifest",
            "the clean fixture declares everything it ships, so nothing must be reported undeclared");

        var payload = Path.Combine(pub, "assets", "payload.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
        File.WriteAllBytes(payload, Encoding.Unicode.GetBytes("a harmless undeclared blob"));

        var stillGreen = RunVerify(sb, "-PublishDir", pub, "-ShipManifest", manifest, "-ExpectShipToken", "tok-scanall");
        stillGreen.ExitCode.Should().Be(0, stillGreen.Output);
        stillGreen.Output.Should().Contain("not declared by the ship manifest",
            "an undeclared non-code file is reported and scanned, not silently ignored");

        File.WriteAllBytes(payload, Encoding.Unicode.GetBytes("a blob carrying " + Canary + " in it"));

        var red = RunVerify(sb, "-PublishDir", pub, "-ShipManifest", manifest, "-ExpectShipToken", "tok-scanall");
        red.ExitCode.Should().Be(1, red.Output);
        red.Output.Should().Contain("canary");
        red.Output.Should().Contain("publish-undeclared:", "the hit must be attributed to the undeclared unit that carried it");
    }

    /// <summary>
    /// B1. The lane hard-broke publish-public.ps1 under its own default -WorkRoot: the guard
    /// dot-sources scripts\shipartifact.ps1, which compiles C# at runtime, and an anti-malware AMSI
    /// hook refuses to PARSE that file out of %TEMP% or C:\temp. MEASURED 2026-09-11: the cold gate
    /// got "VERIFY FAIL: could not load ...shipartifact.ps1 ... blocked by your antivirus software",
    /// exit 1, from a real curated tree, while the BASE guard from the same directory said
    /// "OK - community", exit 0 - so it was new with the lane, not with the box.
    ///
    /// Two arms. POSITIVE: the script's own -PreflightOnly must succeed under the DEFAULT -WorkRoot
    /// on this machine, which is the claim that matters. NEGATIVE: the pre-flight must be able to
    /// display a failure at all - exercised portably by running the same script from a tree whose
    /// scripts\shipartifact.ps1 is absent, rather than by depending on any particular AV product.
    /// </summary>
    [Fact]
    public void The_publish_work_root_default_can_load_the_artefact_resolver()
    {
        var root = RepoRoot();
        var script = Path.Combine(root, "publish-public.ps1");
        // COMMENT LINES ARE STRIPPED FIRST. The parameter's own comment explains at length why the
        // default is no longer $env:TEMP - and so contains the exact string a raw match reads as the
        // defect still being present. This test failed that way on its first run (2026-09-11), the
        // same shape as The_ship_manifest_is_emitted_by_the_community_publish_target. A string match
        // is not evidence about the world: measure the DECLARATION, not the paragraph beside it.
        var code = string.Join("\n", File.ReadAllLines(script)
            .Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal)));

        code.Should().NotContain("$WorkRoot = $env:TEMP",
            "a shared temporary directory is the one family an AMSI hook has been observed to refuse the artefact resolver out of");
        code.Should().Contain("$WorkRoot = (Join-Path (Split-Path -Parent $PSScriptRoot)",
            "the default work root must be a folder beside the repo, where the resolver is known to load");
        code.Should().Contain("$PreflightOnly", "the pre-flight must be runnable on its own, before any build");

        var ok = RunPublishScript(root, script, "-PreflightOnly");
        ok.ExitCode.Should().Be(0,
            "publish-public.ps1's default -WorkRoot must be able to host the community guard: " + ok.Output);
        ok.Output.Should().Contain("PREFLIGHT OK");

        // The instrument must be able to show a positive. A tree with no artefact resolver in it is a
        // failure every box agrees on.
        var sb = NewSandboxRoot("sqlt-preflight-");
        Directory.CreateDirectory(Path.Combine(sb, "scripts"));
        Directory.CreateDirectory(Path.Combine(sb, "tools"));
        File.Copy(script, Path.Combine(sb, "publish-public.ps1"));
        File.Copy(Path.Combine(root, "tools", "release-scrub.ps1"), Path.Combine(sb, "tools", "release-scrub.ps1"));

        var red = RunPublishScript(sb, Path.Combine(sb, "publish-public.ps1"), "-PreflightOnly");
        red.ExitCode.Should().Be(1, red.Output);
        red.Output.Should().Contain("PRE-FLIGHT FAILED");
        red.Output.Should().NotContain("PREFLIGHT OK");
    }

    private (int ExitCode, string Output) RunPublishScript(string workingDir, string script, params string[] args)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(script);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(300_000);
        _out.WriteLine(script + " " + string.Join(" ", args));
        _out.WriteLine(text);
        return (p.ExitCode, text);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────

    private string NewSandbox()
    {
        var root = RepoRoot();
        var sb = NewSandboxRoot("sqlt-ship-");
        Directory.CreateDirectory(Path.Combine(sb, "scripts"));
        foreach (var s in new[] { "verify-community-build.ps1", "shipartifact.ps1" })
            File.Copy(Path.Combine(root, "scripts", s), Path.Combine(sb, "scripts", s));
        // The real needle list, copied - never edited in place.
        var handoff = Path.Combine(root, ".handoff", "gated-routes.txt");
        if (File.Exists(handoff))
        {
            Directory.CreateDirectory(Path.Combine(sb, ".handoff"));
            File.Copy(handoff, Path.Combine(sb, ".handoff", "gated-routes.txt"));
        }
        return sb;
    }

    /// <summary>A stand-in for a compiled assembly: a PE-ish header and a body.</summary>
    private static byte[] FakeAssembly(string body)
    {
        var head = new byte[] { 0x4D, 0x5A, 0x90, 0x00 };
        return head.Concat(Encoding.Unicode.GetBytes(body)).ToArray();
    }

    /// <summary>A loose (non-bundled) publish directory: what a Debug community publish produces.</summary>
    private static string MakeLoosePublishDir(string sandbox, bool canaryInAssembly)
    {
        var pub = Path.Combine(sandbox, "pub-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(Path.Combine(pub, "Config"));
        File.WriteAllBytes(Path.Combine(pub, "SQLTriage.dll"),
            FakeAssembly(canaryInAssembly ? "body with " + Canary + " inside" : "an ordinary community body"));
        // Check 4 of the guard: the community catalog must ship.
        File.WriteAllBytes(Path.Combine(pub, "Config", "free-bundle.dat"), new byte[] { 1, 2, 3, 4 });
        return pub;
    }

    private static string WriteLooseManifest(string sandbox, string publishDir, string token, string profile = "community")
    {
        var manifest = publishDir.TrimEnd('\\', '/') + ".sqltriage-ship-manifest.txt";
        var rows = new List<string>
        {
            "# sqltriage-ship-manifest v1",
            "P|ShipToken=" + token,
            "P|Producer=test fixture",
            "P|SQLTriageProfile=" + profile,
            "P|Configuration=Debug",
            "P|PublishSingleFile=false",
            "P|PublishedSingleFileName=",
            "P|PublishDir=" + publishDir,
        };
        foreach (var rel in new[] { "SQLTriage.dll", Path.Combine("Config", "free-bundle.dat") })
        {
            var full = Path.Combine(publishDir, rel);
            rows.Add("R|" + rel.Replace('\\', '/') + "||" + Sha256File(full) + "|" + full);
        }
        File.WriteAllLines(manifest, rows);
        return manifest;
    }

    private sealed record BundleFixture(string PublishDir, string Manifest, string Token);

    /// <summary>
    /// Builds a real .NET single-file bundle: payloads (deflated), then the v6 header, with the 32-byte
    /// marker and the int64 header offset planted in a stand-in host region. The marker is the constant
    /// every single-file host embeds - MEASURED out of a real 174 MB artefact on 2026-09-11, because the
    /// host's own source comments it as SHA-256(".net core bundle") and it is not.
    /// </summary>
    private static BundleFixture MakeBundlePublishDir(
        string sandbox,
        (string Rel, byte[] Content, bool Compress)[] entries,
        int major = 6,
        bool corruptFirstEntryOffset = false)
    {
        var pub = Path.Combine(sandbox, "bundle-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(Path.Combine(pub, "Config"));
        File.WriteAllBytes(Path.Combine(pub, "Config", "free-bundle.dat"), new byte[] { 1, 2, 3, 4 });

        var marker = new byte[]
        {
            0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
            0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
            0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
            0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae,
        };

        using var ms = new MemoryStream();
        // Stand-in host region. The 8 bytes before the marker hold the header offset; patched below.
        ms.Write(new byte[512], 0, 512);
        var offsetFieldAt = (int)ms.Position;
        ms.Write(new byte[8], 0, 8);
        ms.Write(marker, 0, marker.Length);
        ms.Write(new byte[512], 0, 512);

        var placed = new List<(string Rel, long Offset, long Size, long Compressed)>();
        foreach (var e in entries)
        {
            var at = ms.Position;
            byte[] stored;
            long compressed;
            if (e.Compress)
            {
                using var comp = new MemoryStream();
                using (var ds = new DeflateStream(comp, CompressionLevel.Optimal, true)) ds.Write(e.Content, 0, e.Content.Length);
                stored = comp.ToArray();
                compressed = stored.Length;
            }
            else { stored = e.Content; compressed = 0; }
            ms.Write(stored, 0, stored.Length);
            placed.Add((e.Rel, at, e.Content.Length, compressed));
        }

        var headerAt = ms.Position;
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            bw.Write((uint)major);
            bw.Write((uint)0);
            bw.Write(placed.Count);
            bw.Write("test-bundle-id");
            for (int k = 0; k < 5; k++) bw.Write((long)0);   // deps + runtimeconfig locations, flags
            for (int k = 0; k < placed.Count; k++)
            {
                var p = placed[k];
                bw.Write(k == 0 && corruptFirstEntryOffset ? long.MaxValue / 4 : p.Offset);
                bw.Write(p.Size);
                bw.Write(p.Compressed);
                bw.Write((byte)1);
                bw.Write(p.Rel);
            }
        }

        var bytes = ms.ToArray();
        BitConverter.GetBytes(headerAt).CopyTo(bytes, offsetFieldAt);
        var exe = Path.Combine(pub, "SQLTriage.exe");
        File.WriteAllBytes(exe, bytes);

        var token = "tok-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var manifest = pub.TrimEnd('\\', '/') + ".sqltriage-ship-manifest.txt";
        var rows = new List<string>
        {
            "# sqltriage-ship-manifest v1",
            "P|ShipToken=" + token,
            "P|SQLTriageProfile=community",
            "P|Configuration=Release",
            "P|PublishSingleFile=true",
            "P|EnableCompressionInSingleFile=true",
            "P|PublishedSingleFileName=SQLTriage.exe",
            "P|PublishDir=" + pub,
        };
        // Only the FIRST entry is declared as ours, deliberately: the r2r test needs the canary to live
        // in an entry the manifest never mentions, so that only "scan everything" can find it.
        var first = entries[0];
        rows.Add("B|" + first.Rel + "||" + Sha256Bytes(first.Content) + "|" + Path.Combine(sandbox, first.Rel));
        var freeBundle = Path.Combine(pub, "Config", "free-bundle.dat");
        rows.Add("R|Config/free-bundle.dat||" + Sha256File(freeBundle) + "|" + freeBundle);
        File.WriteAllLines(manifest, rows);

        return new BundleFixture(pub, manifest, token);
    }

    private static string Sha256File(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
    }

    private static string Sha256Bytes(byte[] data)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return true;
        }
        return false;
    }

    private (int ExitCode, string Output) RunVerify(string sandbox, params string[] args)
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
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(300_000);
        _out.WriteLine(string.Join(" ", args));
        _out.WriteLine(text);
        return (p.ExitCode, text);
    }

    /// <summary>Runs a snippet against the REAL library, so the harness proves the shipped code.</summary>
    private (int ExitCode, string Output) RunHarness(string body)
    {
        var root = RepoRoot();
        var script = Path.Combine(NewSandboxRoot("sqlt-harness-"), "harness.ps1");
        File.WriteAllText(script,
            "$ErrorActionPreference = 'Stop'" + Environment.NewLine +
            ". '" + Path.Combine(root, "scripts", "shipartifact.ps1") + "'" + Environment.NewLine +
            body, new UTF8Encoding(false));
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(script);
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(300_000);
        _out.WriteLine(text);
        return (p.ExitCode, text);
    }
}
