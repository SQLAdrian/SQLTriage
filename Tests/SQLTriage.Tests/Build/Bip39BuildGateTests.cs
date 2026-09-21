/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace SQLTriage.Tests.Build;

/// <summary>
/// The BIP39 wordlist gate is an MSBuild target, so nothing in the test suite can execute it: these
/// facts read SQLTriage.csproj as XML and assert its SHAPE. Precedent for that is ScriptCensusTests,
/// which loads the same csproj with <see cref="XDocument"/> and asserts on its copy rules.
///
/// <para>WHAT THESE PROTECT. Resources\bip39-english.txt is gitignored and fetched by
/// tools\fetch-bip39-wordlist.ps1. From the day it was written until 2026-09-02 the Build-time
/// target only WARNED when it was absent for a non-community profile, while the Publish-time twin
/// ERRORED on the identical condition - so <c>dotnet build -p:SQLTriageProfile=full</c> in a tree
/// that had never run the fetch exited 0 and produced a binary whose license-key surface throws on
/// first use. Fresh-eyes finding premortem-3. A one-word revert (Error back to Warning) restores
/// that hole silently and leaves every other build green, which is exactly the kind of change that
/// needs a test rather than a comment.</para>
///
/// <para>WHAT THEY DO NOT PROVE. A shape, not a behaviour. That the target actually fails a full
/// build was proved live on 2026-09-02: full profile, wordlist absent, exit 1, one error raised at
/// SQLTriage.csproj(545,5), 1 Error(s); community profile with the same absence, exit 0, 0
/// Error(s), which is the condition CI runs under. Re-proving it needs a real non-community
/// build, which CI cannot run - see tools\record-full-profile-build.ps1 and its log for the
/// standing record of those builds.</para>
///
/// <para>WHERE IN THE BUILD IT FIRES, measured rather than assumed. <c>BeforeTargets="Build"</c>
/// does NOT mean before compilation: Build's own dependencies (CoreBuild, CoreCompile) run first,
/// so in the 2026-09-02 log the C# compiler's warnings appear at lines 13-122 and the wordlist
/// error at line 125. The build still exits 1 and produces no successful output, which is the
/// consequence that was ruled; it simply pays for a compile before saying so. An earlier claim
/// that the error was raised "before any compile" was wrong and is corrected here rather than
/// left standing - moving the hook earlier would be a separate change, and this file's facts
/// assert the Build hook as it is.</para>
/// </summary>
public class Bip39BuildGateTests
{
    private const string WordlistPath = @"Resources\bip39-english.txt";

    // RawPassedScan.RepoRoot is the house convention for this (anchored on SQLTriage.sln); the
    // sibling in this folder, ProfileGatedTestSyncTests, resolves the root the same way.
    private static string CsprojPath() =>
        Path.Combine(RawPassedScan.RepoRoot().FullName, "SQLTriage.csproj");

    /// <summary>Every &lt;Target&gt; in the csproj, with its name, hooks and condition.</summary>
    private static IReadOnlyList<XElement> Targets() =>
        XDocument.Load(CsprojPath()).Descendants("Target").ToList();

    /// <summary>
    /// The targets that hook <paramref name="hook"/> - matched on the whole attribute value split
    /// on ';', so a target that hooks "Build;Publish" is found by either name.
    /// </summary>
    private static IReadOnlyList<XElement> TargetsHooking(string hook) =>
        Targets()
            .Where(t => (t.Attribute("BeforeTargets")?.Value ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Any(h => h.Trim().Equals(hook, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    private static bool MentionsWordlist(XElement e) =>
        (e.Attribute("Condition")?.Value ?? "").Contains("bip39-english.txt", StringComparison.OrdinalIgnoreCase)
        || (e.Attribute("Text")?.Value ?? "").Contains("bip39-english.txt", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void The_csproj_reader_is_not_vacuous()
    {
        // NULL HYPOTHESIS FIRST. Every fact below is "the reader found X, or did not find Y". A
        // reader that silently finds nothing - wrong path, an XML namespace appearing on the
        // project element, a rename of Target - would make all of them pass while asserting
        // nothing. That is the defect class this whole file exists because of, so it gets a
        // known-positive of its own before any of the real assertions.
        Assert.True(File.Exists(CsprojPath()), "SQLTriage.csproj not found at " + CsprojPath());

        var targets = Targets();
        Assert.True(targets.Count >= 8,
            "the csproj reader found only " + targets.Count + " <Target> elements. It found 16 on "
            + "2026-09-02 (counted, not remembered); a reader that finds almost none makes every "
            + "other fact here vacuous.");

        Assert.True(TargetsHooking("Build").Count >= 1,
            "the reader found no target hooking BeforeTargets=\"Build\", so it cannot see the "
            + "target this file is about.");
        Assert.True(TargetsHooking("Publish").Count >= 1,
            "the reader found no target hooking BeforeTargets=\"Publish\".");
    }

    [Fact]
    public void A_build_time_target_errors_when_the_bip39_wordlist_is_absent()
    {
        var guards = TargetsHooking("Build")
            .Where(t => t.Elements().Any(MentionsWordlist))
            .ToList();

        Assert.True(guards.Count == 1,
            "expected exactly ONE BeforeTargets=\"Build\" target guarding " + WordlistPath
            + ", found " + guards.Count + ". Zero means a full/private build no longer fails on a "
            + "missing wordlist and ships a binary whose license-key decode throws at runtime. Two "
            + "means one of them can be relaxed without going red.");

        var guard = guards[0];

        // The whole point of the 2026-09-02 change: this task is Error, not Warning. A Warning is
        // provably ignorable - eight tests in Licensing/Bip39MirrorTests.cs used to early-return to
        // a silent PASS on the same condition, so nothing downstream contradicted it either.
        var errors = guard.Elements("Error").Where(MentionsWordlist).ToList();
        var warnings = guard.Elements("Warning").Where(MentionsWordlist).ToList();

        Assert.True(errors.Count >= 1,
            "the Build-time wordlist guard '" + (guard.Attribute("Name")?.Value ?? "(unnamed)")
            + "' raises no <Error> naming " + WordlistPath + ". If it was changed back to "
            + "<Warning>, a full build with no wordlist exits 0 again and the failure moves to "
            + "publish - or to the client. Fresh-eyes premortem-3.");

        Assert.True(warnings.Count == 0,
            "the Build-time wordlist guard still raises a <Warning> for " + WordlistPath
            + ". A warning beside the error re-opens the soft landing the error exists to close.");

        // The condition that keeps CI green. Community builds never embed the wordlist and must not
        // fail on its absence; the workflow builds community only, so a target that fired for every
        // profile would take the one job CI runs down.
        var condition = guard.Attribute("Condition")?.Value ?? "";
        Assert.True(condition.Contains("community", StringComparison.OrdinalIgnoreCase)
                    && condition.Contains("!="),
            "the Build-time wordlist guard's Condition is '" + condition + "'. It must exclude the "
            + "community profile with a != test: community never embeds the wordlist, and CI builds "
            + "community only, so a guard that fires for every profile takes the one job CI runs "
            + "down for a resource that build does not need.");
    }

    [Fact]
    public void The_publish_time_twin_still_errors()
    {
        // The belt to the Build-time braces, and older than it: an incremental publish over an
        // already-built tree whose wordlist was deleted in between never re-runs Build.
        var guards = TargetsHooking("Publish")
            .Where(t => t.Elements("Error").Any(MentionsWordlist))
            .ToList();

        Assert.True(guards.Count >= 1,
            "no BeforeTargets=\"Publish\" target raises an <Error> naming " + WordlistPath
            + ". The Build-time gate does not cover an incremental publish, so removing this one "
            + "lets a publish produce a bundle with no wordlist.");
    }

    [Fact]
    public void No_target_anywhere_only_warns_about_the_wordlist()
    {
        // Not scoped to Build/Publish on purpose. A third target that merely warns - added later,
        // hooked anywhere - would read as a gate in a log and gate nothing.
        var warners = Targets()
            .SelectMany(t => t.Elements("Warning"))
            .Where(MentionsWordlist)
            .ToList();

        Assert.True(warners.Count == 0,
            "found " + warners.Count + " <Warning> task(s) naming " + WordlistPath
            + " in SQLTriage.csproj. The wordlist gate is an error in every profile that needs the "
            + "wordlist; a warning about it is the shape that was provably ignored for months.");
    }
}
