/* In the name of God, the Merciful, the Compassionate */

// THE FOUR-WAY CENSUS: scripts/ on disk, Config/script-configurations.json, the csproj copy rules,
// and installer/SQLTriage.iss must all name the same set of scripts.
//
// WHY. Adding a diagnostic script to this product is three edits in three files, and NOTHING
// checked that all three happened. Each pair of the three fails differently and none of them fails
// loudly:
//
//   * file + config, no csproj rule  -> the script is never copied beside the binary, so every run
//     of that entry throws "Script not found" on the client's server. Nothing catches it before the
//     client does.
//   * file + csproj rule, no config entry -> the script ships in every build and installer and is
//     never offered, never installed, never run. That is scripts/SqlServerVersions.sql today.
//   * config + csproj rule, no file -> the build's copy step fails, which is the one shape that is
//     at least loud.
//
// The half-wired case is the dangerous one and it is exactly the shape the sp_PerfCheck bundle
// could have shipped in: six files touched, and forgetting any one of them leaves a feature that
// looks present in the repo and is absent, or broken, on a client machine. This file is the
// tripwire for the class rather than for one script.
//
// THE FOURTH AXIS, ADDED 2026-08-24, IS THE INSTALLER. The first three axes all stop at the publish
// tree and none of them asks what the installer does with it. installer/SQLTriage.iss had NO
// scripts\ entry from the day it was written (e6e12e7) to 2026-08-24, so every one of those three
// axes was green while an installed build had no diagnostic scripts on disk at all. Proved by
// compiling the real .iss against a stub source tree that DID contain scripts\sp_Blitz.sql and
// finding no scripts\ folder in the installed image.
//
// WHAT THIS AXIS IS AND IS NOT. It reads the .iss AS TEXT. It is a lint. It does not prove iscc
// compiled, it does not prove the compiled installer put the folder on disk, and it cannot see a
// Source line whose {#SourceDir} subtree happens to be empty at compile time. The measurement is
// the compile-install-list probe (2026-08-24, evidence under C:\temp\installer-lane) and the live
// harness InstallerScriptResolutionLiveTests. Do not let a green lint here be read as either.
//
// ONE OF THIS AXIS'S TESTS IS NOT A LINT ON THE .iss AT ALL. The scripts\ Source line is
// deliberately unguarded, and that decision rests on a claim read from source - that every build
// profile produces a non-empty scripts\ folder. The live compile harness is structurally blind to
// that claim (its community stub always creates the folder), so
// Nothing_gates_the_scripts_folder_out_of_any_build_profile reads buildprofile.targets and the
// csproj instead, and goes red when the MECHANISM that could falsify the claim appears.
//
// EXEMPTIONS ARE NAMED, ONE BY ONE, WITH A REASON. A census with a pattern-shaped exemption stops
// being a census: the next orphan matches the pattern and nobody hears about it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace SQLTriage.Tests;

public class ScriptCensusTests
{
    /// <summary>
    /// Files under scripts/ that are deliberately NOT wired into the app, each with the reason it
    /// is there at all. Anything else in that folder is either offered to the operator or a bug.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> KnownUnwiredScripts =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SqlServerVersions.sql"] =
                "A live orphan, PROVED so on 2026-08-24: it is in scripts/, it has no config entry "
                + "and no csproj copy rule, and it predates this census. It is the seed data for the "
                + "sp_Blitz 'Cumulative Update Available' check (CheckID 217), which reads a "
                + "dbo.SqlServerVersions TABLE if an operator has planted one. Listed rather than "
                + "deleted because whether to wire it up or drop it is Adrian's call, not this "
                + "test's. It is named here so it stops hiding the NEXT orphan.",
        };

    /// <summary>
    /// Scripts the build copies into scripts/ from somewhere else in the tree, so they have a
    /// csproj rule and no file under scripts/. These are excluded from the file side of the census
    /// and asserted separately by their own owners.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ScriptsBuiltFromElsewhere =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["identity_manifest.sql"] =
                "Lives under Data/Services/Portal/Export/ so the portal denies cover it, and is "
                + "Content-Included with TargetPath scripts\\identity_manifest.sql. It is not a "
                + "DiagnosticScriptRunner entry and never appears in script-configurations.json.",
        };

    private static string RepoRoot() => FrkContractTests.RepoRoot();

    /// <summary>Every .sql file physically in scripts/.</summary>
    private static IReadOnlyList<string> ScriptFilesOnDisk() =>
        Directory.GetFiles(Path.Combine(RepoRoot(), "scripts"), "*.sql", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Every ScriptPath the shipped configuration names.</summary>
    private static IReadOnlyList<string> ScriptPathsInConfig()
    {
        using var doc = JsonDocument.Parse(
            File.ReadAllText(FrkContractTests.ConfigPath("script-configurations.json")));

        return doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("ScriptPath").GetString() ?? "")
            .Where(s => s.Length > 0)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Every scripts\*.sql the csproj promises to copy beside the binary.</summary>
    private static IReadOnlyList<string> ScriptsCopiedByTheCsproj()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "SQLTriage.csproj"));

        // Both shapes that put a file into the output scripts\ folder: the None Update rules for
        // files that already live in scripts/, and any Content Include that retargets one there.
        var updates = Regex.Matches(csproj, @"<None\s+Update=""scripts\\(?<name>[^""]+\.sql)""")
            .Select(m => m.Groups["name"].Value);

        var targeted = Regex.Matches(csproj, @"<TargetPath>scripts\\(?<name>[^<]+\.sql)</TargetPath>")
            .Select(m => m.Groups["name"].Value);

        return updates.Concat(targeted)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---- AXIS 4: installer/SQLTriage.iss ------------------------------------------------------

    /// <summary>
    /// platform-r1-09 (honesty-hunt 2026-08-25). AppSupportURL is published by Windows as the
    /// OS-level Support link. It pointed at the pre-rename repo (SqlHealthAssessment), which returns
    /// HTTP 404, so a client using the Support link landed on a dead page. It must point at the
    /// SQLTriage repo, and the dead name must not survive anywhere in the installer script.
    /// </summary>
    [Fact]
    public void The_installer_support_url_points_at_the_current_repo()
    {
        var iss = File.ReadAllText(Path.Combine(RepoRoot(), "installer", "SQLTriage.iss"));

        Assert.DoesNotContain("SqlHealthAssessment", iss);
        Assert.Contains("AppSupportURL=https://github.com/SQLAdrian/SQLTriage/issues", iss);
    }

    /// <summary>One [Files] entry of installer/SQLTriage.iss, and whether a preprocessor guard
    /// stands between it and the compiler.</summary>
    private sealed record InstallerFileEntry(
        int Line, string Source, string DestDir, string Folder, string? Guard);

    /// <summary>
    /// Every Source/DestDir pair in installer/SQLTriage.iss, with the innermost #if guard that
    /// encloses it. Line continuations do not matter here: every entry in this file carries its
    /// Source and its DestDir on the same physical line, and the reader asserts that it found the
    /// number of entries it expects rather than trusting that to stay true.
    /// </summary>
    private static IReadOnlyList<InstallerFileEntry> InstallerFileEntries()
    {
        var path = Path.Combine(RepoRoot(), "installer", "SQLTriage.iss");
        var lines = File.ReadAllLines(path);

        var entries = new List<InstallerFileEntry>();
        var guards = new Stack<string>();
        var inFiles = false;

        var sectionRx = new Regex(@"^\s*\[(?<name>[A-Za-z]+)\]\s*$");
        var entryRx = new Regex(
            "^\\s*Source:\\s*\"(?<source>[^\"]+)\"\\s*;\\s*DestDir:\\s*\"(?<dest>[^\"]+)\"");

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            var section = sectionRx.Match(line);
            if (section.Success)
            {
                inFiles = section.Groups["name"].Value.Equals("Files", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("#if", StringComparison.Ordinal))
            {
                guards.Push(trimmed);
                continue;
            }
            if (trimmed.StartsWith("#endif", StringComparison.Ordinal))
            {
                if (guards.Count > 0) guards.Pop();
                continue;
            }

            if (!inFiles) continue;

            var m = entryRx.Match(line);
            if (!m.Success) continue;

            var dest = m.Groups["dest"].Value;
            var folder = dest.StartsWith("{app}", StringComparison.OrdinalIgnoreCase)
                ? dest.Substring("{app}".Length).TrimStart('\\')
                : dest;

            entries.Add(new InstallerFileEntry(
                i + 1, m.Groups["source"].Value, dest, folder,
                guards.Count > 0 ? guards.Peek() : null));
        }

        return entries;
    }

    /// <summary>Folders the installer puts under {app}, from its [Files] section. "" is {app}
    /// itself.</summary>
    private static IReadOnlySet<string> FoldersShippedByTheInstaller() =>
        InstallerFileEntries()
            .Select(e => e.Folder)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Folders whose contents the RUNTIME resolves for itself, with the file:line that resolves
    /// each one, so a failure names its own owner instead of just a folder.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> FoldersTheRuntimeResolves =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["scripts"] =
                "Data/DiagnosticScriptRunner.cs (every Full Audit entry) and "
                + "Data/Services/Portal/Export/ExportPackRunner.cs (identity_manifest.sql). "
                + "AutoUpdateService.cs:791/:842 also writes into it.",
            ["Config"] =
                "Data/BPScriptService.cs (bp-scripts.json), Data/ConnectionManager.cs, "
                + "Cli/CliAuditHost.cs, Data/AutoUpdateService.cs:165 (version.json).",
            ["wwwroot"] =
                "WindowsServiceHost / ServerModeService web root for the Blazor UI.",
            ["Assets"] =
                "Data/Services/AssessmentPdf.cs:107 loads Assets\\brand\\sqltriage-mark.png at PDF "
                + "render time. Copied to publish by SQLTriage.csproj:406 with no Condition (present "
                + "in every profile). Missing from [Files] until platform-r2-07: every Inno-installed "
                + "client got unbranded PDFs while the in-app UI stayed branded off wwwroot\\images.",
            ["BPScripts"] =
                "Data/BPScriptService.cs:27. Full profile only - buildprofile.targets:158-167 "
                + "removes the folder from a community publish, so its entry is #if-guarded.",
        };

    /// <summary>
    /// Folders that are legitimately ABSENT from some profile's publish tree, so their Source line
    /// must be #if DirExists-guarded or iscc aborts the whole compile. Everything else in [Files]
    /// must be UNGUARDED, so that a folder going missing is loud instead of silent.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> FoldersAbsentInSomeProfile =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Deploy"] =
                "UNBUNDLED 2026-07-21 for client safety: SQLTriage.csproj excludes the "
                + "PerformanceMonitor_db / SQLWATCH_db payloads, so publish\\...\\Deploy is normally "
                + "absent in every profile.",
            ["BPScripts"] =
                "buildprofile.targets:158-167 removes BPScripts\\** from a COMMUNITY publish. "
                + "Unguarded, this line aborted the community installer with iscc exit 2 and "
                + "the public release script degraded the release to zip-only without failing. Measured "
                + "2026-08-24 against a community stub tree.",
        };

    [Fact]
    public void The_installer_axis_reader_is_not_vacuous()
    {
        // A regex that stops matching would turn every assertion below green. This is the same
        // guard the three original axes carry, applied to the fourth.
        var entries = InstallerFileEntries();

        Assert.True(entries.Count >= 12,
            "the installer [Files] reader found only " + entries.Count + " Source lines in "
            + "installer/SQLTriage.iss. It used to find 16. Either the file changed shape or the "
            + "regex stopped matching; a reader that finds nothing makes this whole axis vacuous.");

        Assert.True(FoldersShippedByTheInstaller().Count >= 5,
            "the installer [Files] reader found fewer than five distinct DestDir folders");

        // The guard reader has to be able to see BOTH states, or the guard-shape test below cannot
        // fail in either direction.
        Assert.Contains(entries, e => e.Guard is not null);
        Assert.Contains(entries, e => e.Guard is null);

        // And the reader must really be scoped to [Files] and to this installer's conventions:
        // every entry comes out of the publish tree and lands under {app}. An entry that did
        // neither would mean the parser has wandered into another section.
        Assert.All(entries, e =>
        {
            Assert.StartsWith("{#SourceDir}", e.Source, StringComparison.Ordinal);
            Assert.StartsWith("{app}", e.DestDir, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Every_folder_the_runtime_resolves_is_shipped_by_the_installer()
    {
        // THE DEFECT THIS AXIS EXISTS FOR. installer/SQLTriage.iss shipped no scripts\ entry from
        // e6e12e7 until 2026-08-24, so an installed build had no diagnostic scripts on disk and
        // every Full Audit entry failed with "Script not found". Axes 1-3 were green throughout.
        var shipped = FoldersShippedByTheInstaller();

        var missing = FoldersTheRuntimeResolves
            .Where(kv => !shipped.Contains(kv.Key))
            .ToList();

        Assert.True(missing.Count == 0,
            "installer/SQLTriage.iss has no [Files] entry for folders the app resolves at run time: "
            + string.Join("; ", missing.Select(kv => kv.Key + " (read by " + kv.Value + ")"))
            + ". Without the entry the folder is simply not on an installed machine.");
    }

    [Fact]
    public void The_installer_ships_the_scripts_folder_the_other_axes_fill()
    {
        // NAMED FOR WHAT IT ASSERTS. This is a FOLDER-level check, not a per-script one: the two
        // other axes are non-empty, and [Files] carries a scripts\ DestDir. Per-script reach is
        // delivered by Every_configured_script_is_copied_beside_the_binary plus the scripts\*
        // wildcard on that [Files] line, not here. An earlier name claimed every configured script
        // reaches an installed build, which is more than these three assertions measure.
        //
        // The composition is still the point of the fourth axis: axis 3 proves the csproj copies
        // the script into the publish tree, axis 4 proves the installer ships that tree's scripts\
        // folder, and either half alone is green while a client's run dies.
        Assert.NotEmpty(ScriptPathsInConfig());
        Assert.NotEmpty(ScriptsCopiedByTheCsproj());
        Assert.Contains("scripts", FoldersShippedByTheInstaller(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Profile_conditional_folders_are_guarded_and_nothing_else_is()
    {
        // THE SECOND DEFECT, and it is why the first one survived so long. The BPScripts\ line was
        // UNGUARDED while a community publish legitimately has no BPScripts folder, so iscc aborted
        // with exit 2 and the public release script quietly degraded the community release to zip-only.
        // No community user has ever had an installer, so nobody was ever in a position to notice
        // that the installer was missing scripts\.
        //
        // The rule this test pins is a two-way one, and the second direction matters as much as the
        // first: a folder that is present in EVERY profile must stay UNGUARDED, because then a
        // publish tree that loses it aborts the compile loudly instead of silently producing the
        // broken installer this whole file is about.
        var entries = InstallerFileEntries();

        var unguardedButOptional = entries
            .Where(e => FoldersAbsentInSomeProfile.ContainsKey(e.Folder) && e.Guard is null)
            .ToList();

        Assert.True(unguardedButOptional.Count == 0,
            "these installer/SQLTriage.iss [Files] entries name a folder that some build profile "
            + "does not produce, and they are not #if DirExists-guarded, so iscc aborts the whole "
            + "compile for that profile: "
            + string.Join("; ", unguardedButOptional.Select(
                e => "line " + e.Line + " " + e.Source + " (" + FoldersAbsentInSomeProfile[e.Folder] + ")")));

        var guardedButAlwaysPresent = entries
            .Where(e => e.Guard is not null && !FoldersAbsentInSomeProfile.ContainsKey(e.Folder))
            .ToList();

        Assert.True(guardedButAlwaysPresent.Count == 0,
            "these installer/SQLTriage.iss [Files] entries are #if-guarded but their folder is "
            + "produced by every build profile: "
            + string.Join("; ", guardedButAlwaysPresent.Select(e => "line " + e.Line + " " + e.Source))
            + ". A guard on an always-present folder converts a loud compile failure into a "
            + "silently incomplete installer. Either remove the guard, or add the folder to "
            + "FoldersAbsentInSomeProfile with the reason it can be missing.");

        // The guard has to be the DirExists shape. A guard on some other condition would satisfy
        // the first assertion above while still aborting when the folder is absent.
        foreach (var e in entries.Where(x => x.Guard is not null))
        {
            Assert.True(e.Guard!.Contains("DirExists", StringComparison.Ordinal),
                "line " + e.Line + " of installer/SQLTriage.iss is guarded by " + e.Guard
                + ", which does not test whether the folder exists. Only #if DirExists keeps iscc "
                + "from aborting on an absent folder.");
        }
    }

    // ---- platform-r1-02: operator config must survive the pre-upgrade uninstall ----------------

    /// <summary>The bare filenames of Config\*.json [Files] entries flagged onlyifdoesntexist —
    /// the operator-owned files whose edits the 2026-08-23 #4 ruling says must survive an upgrade.</summary>
    private static IReadOnlyList<string> OnlyIfDoesntExistConfigFiles(string iss)
    {
        var rx = new Regex(@"\\Config\\(?<name>[A-Za-z0-9._-]+\.json)""[^\r\n]*onlyifdoesntexist",
            RegexOptions.IgnoreCase);
        return rx.Matches(iss)
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The filenames listed in OperatorConfigNames() in the .iss [Code] section — the set
    /// the CurStepChanged backup/restore actually preserves.</summary>
    private static IReadOnlyList<string> OperatorConfigNamesFromIss(string iss)
    {
        var start = iss.IndexOf("function OperatorConfigNames", StringComparison.Ordinal);
        Assert.True(start >= 0, "OperatorConfigNames() not found in installer/SQLTriage.iss");
        var end = iss.IndexOf("end;", start, StringComparison.Ordinal);
        var body = iss.Substring(start, end - start);
        var rx = new Regex(@"Result\[\d+\]\s*:=\s*'(?<name>[^']+)'");
        return rx.Matches(body)
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [Fact]
    public void Operator_owned_config_survives_the_pre_upgrade_uninstall()
    {
        // platform-r1-02, #2 in the wave top-ten. CurStepChanged runs the PREVIOUS version's
        // uninstaller silently at ssInstall, before the new [Files] copy. Every Config\*.json shipped
        // onlyifdoesntexist is in that uninstaller's log, so it is deleted first; the onlyifdoesntexist
        // guard then finds the file absent and re-lays the shipped default, wiping the operator's
        // AdminAuth credential and Updates:Enabled kill-switch on EVERY upgrade. The fix backs those
        // files up before the uninstall and restores them after. This pins that the mechanism exists
        // in the right order AND that the preserved list equals the actual onlyifdoesntexist Config
        // entries, so a newly-added operator config cannot silently escape preservation.
        var iss = File.ReadAllText(Path.Combine(RepoRoot(), "installer", "SQLTriage.iss"));

        var backupIdx = iss.IndexOf("BackupOperatorConfig(BackupDir)", StringComparison.Ordinal);
        var execIdx = iss.IndexOf("Exec(sUnInstallString", StringComparison.Ordinal);
        var restoreIdx = iss.IndexOf("RestoreOperatorConfig(BackupDir)", StringComparison.Ordinal);

        Assert.True(execIdx >= 0,
            "installer/SQLTriage.iss no longer runs the previous version's uninstaller in "
            + "CurStepChanged. If that upgrade sequence changed, re-derive whether operator config is "
            + "still at risk before deleting this test.");
        Assert.True(backupIdx >= 0 && restoreIdx >= 0,
            "installer/SQLTriage.iss does not back up and restore operator config around the "
            + "pre-upgrade uninstall. Without it every upgrade re-lays the shipped Config defaults over "
            + "the operator's AdminAuth credential and Updates:Enabled kill-switch (platform-r1-02).");
        Assert.True(backupIdx < execIdx,
            "operator config must be backed up BEFORE the pre-upgrade uninstaller runs, not after.");
        Assert.True(execIdx < restoreIdx,
            "operator config must be restored AFTER the pre-upgrade uninstaller runs, not before.");

        var onlyIfDoesntExist = OnlyIfDoesntExistConfigFiles(iss);
        var preserved = OperatorConfigNamesFromIss(iss);

        Assert.NotEmpty(onlyIfDoesntExist);
        Assert.NotEmpty(preserved);

        var notPreserved = onlyIfDoesntExist.Except(preserved, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(notPreserved.Count == 0,
            "these Config files ship onlyifdoesntexist (operator data) but are NOT in "
            + "OperatorConfigNames() in installer/SQLTriage.iss, so the pre-upgrade uninstall wipes "
            + "them: " + string.Join(", ", notPreserved));

        var preservedButNotShipped = preserved.Except(onlyIfDoesntExist, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(preservedButNotShipped.Count == 0,
            "OperatorConfigNames() in installer/SQLTriage.iss names files that are not shipped "
            + "onlyifdoesntexist: " + string.Join(", ", preservedButNotShipped) + ". Keep the two lists in step.");
    }

    /// <summary>
    /// Every item in buildprofile.targets that REMOVES something from a build, as
    /// (element, removed path). These are what make a folder absent from one profile's publish
    /// tree, which is the only thing that can make an unguarded [Files] line abort the compile.
    /// </summary>
    private static IReadOnlyList<(string Element, string Removed)> ProfileGatedRemovals()
    {
        var doc = XDocument.Load(Path.Combine(RepoRoot(), "buildprofile.targets"));

        return doc.Descendants()
            .Where(e => e.Attribute("Remove") is not null)
            .Select(e => (e.Name.LocalName, e.Attribute("Remove")!.Value))
            .ToList();
    }

    /// <summary>
    /// Every &lt;None Update="scripts\NAME.sql"&gt; copy rule in the csproj, with the Condition on
    /// the item and on its enclosing ItemGroup. A Condition on either is what would make a script
    /// stop being copied in some profile.
    /// </summary>
    private static IReadOnlyList<(string Name, string? ItemCondition, string? GroupCondition)>
        ScriptCopyRulesWithConditions()
    {
        var doc = XDocument.Load(Path.Combine(RepoRoot(), "SQLTriage.csproj"));

        return doc.Descendants("None")
            .Where(e => (e.Attribute("Update")?.Value ?? "")
                .StartsWith("scripts\\", StringComparison.OrdinalIgnoreCase))
            .Select(e => (
                e.Attribute("Update")!.Value,
                e.Attribute("Condition")?.Value,
                e.Parent?.Attribute("Condition")?.Value))
            .ToList();
    }

    [Fact]
    public void Nothing_gates_the_scripts_folder_out_of_any_build_profile()
    {
        // THE BELIEF THIS TEST TURNS INTO A MEASUREMENT.
        //
        // installer/SQLTriage.iss ships scripts\ with a DELIBERATELY UNGUARDED Source line, and the
        // test above enforces that it stays unguarded. That decision rests entirely on one claim
        // read from source: every build profile produces a non-empty scripts\ folder. If the claim
        // is ever false, iscc aborts the whole compile with "No files found matching", the public
        // release script catches the non-zero exit, and the release quietly degrades to zip-only -
        // which is exactly the failure the BPScripts guard was added on 2026-08-24 to end.
        //
        // The live compile harness (InstallerCompileLiveTests) CANNOT catch that. Its community stub
        // tree creates scripts\ unconditionally, so it is structurally blind to the case this line
        // is exposed to, and no real community publish has ever been compiled against this .iss.
        // This test is what stands in that gap: it goes red when the MECHANISM that could empty the
        // folder appears, rather than waiting for a release to degrade.
        var removals = ProfileGatedRemovals();

        Assert.True(removals.Count >= 20,
            "the buildprofile.targets reader found only " + removals.Count + " Remove items. It "
            + "used to find 78; a reader that finds nothing makes this test vacuous.");

        // Non-vacuity in the other direction too: the reader must be able to SEE a folder-level
        // removal, because that is the exact shape it is looking for. BPScripts\** is one.
        Assert.Contains(removals, r =>
            r.Removed.StartsWith("BPScripts\\", StringComparison.OrdinalIgnoreCase));

        var scriptRemovals = removals
            .Where(r => r.Removed.StartsWith("scripts\\", StringComparison.OrdinalIgnoreCase)
                        || r.Removed.Equals("scripts", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(scriptRemovals.Count == 0,
            "buildprofile.targets now removes items under scripts\\: "
            + string.Join("; ", scriptRemovals.Select(r => "<" + r.Element + " Remove=\"" + r.Removed + "\">"))
            + ". A profile that produces no scripts\\ folder makes the UNGUARDED Source line in "
            + "installer/SQLTriage.iss abort iscc for that profile, and the release degrades to "
            + "zip-only without failing. Either keep the folder in every profile, or add the "
            + "#if DirExists guard to that line AND add \"scripts\" to FoldersAbsentInSomeProfile "
            + "with the reason - but understand that the guard trades a loud compile failure for a "
            + "silently incomplete installer, which is the defect this whole axis exists for.");

        var copyRules = ScriptCopyRulesWithConditions();

        Assert.True(copyRules.Count >= 6,
            "the csproj reader found only " + copyRules.Count + " <None Update=\"scripts\\...\"> "
            + "copy rules. It used to find 6.");

        var conditional = copyRules
            .Where(r => r.ItemCondition is not null || r.GroupCondition is not null)
            .ToList();

        Assert.True(conditional.Count == 0,
            "these scripts\\ copy rules in SQLTriage.csproj are now conditional: "
            + string.Join("; ", conditional.Select(
                r => r.Name + " (item: " + (r.ItemCondition ?? "none")
                     + ", group: " + (r.GroupCondition ?? "none") + ")"))
            + ". Same consequence as a Remove above: a profile that evaluates them away can leave "
            + "scripts\\ empty, and an empty folder aborts the unguarded [Files] line just as a "
            + "missing one does.");

        // scripts\identity_manifest.sql IS conditional (Content Include, SQLTExcludePortal, csproj
        // :433) and that is deliberate and harmless: it is one file among seven and the other six
        // are unconditional, so the folder is never empty because of it. It is not a <None Update>
        // rule, so the reader above does not see it, and this comment is why that is correct rather
        // than an oversight.
    }

    [Fact]
    public void The_census_itself_is_not_vacuous()
    {
        // If any of the three readers silently returns nothing, every assertion below passes.
        Assert.True(ScriptFilesOnDisk().Count >= 6, "scripts/ reader found almost nothing");
        Assert.True(ScriptPathsInConfig().Count >= 5, "config reader found almost nothing");
        Assert.True(ScriptsCopiedByTheCsproj().Count >= 5, "csproj reader found almost nothing");

        // And the exemption really is an exemption: a file that has quietly been wired up would
        // otherwise sit in the list forever, hiding the next orphan behind it.
        foreach (var exempt in KnownUnwiredScripts.Keys)
        {
            Assert.True(File.Exists(Path.Combine(RepoRoot(), "scripts", exempt)),
                exempt + " is listed as a known-unwired script and is not in scripts/ at all. "
                + "Remove it from KnownUnwiredScripts.");
            Assert.DoesNotContain(exempt, ScriptPathsInConfig(), StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(exempt, ScriptsCopiedByTheCsproj(), StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Every_configured_script_exists_on_disk()
    {
        var onDisk = ScriptFilesOnDisk();

        var missing = ScriptPathsInConfig()
            .Where(s => !onDisk.Contains(s, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Assert.True(missing.Count == 0,
            "Config/script-configurations.json names scripts that are not in scripts/: "
            + string.Join(", ", missing)
            + ". DiagnosticScriptRunner throws FileNotFoundException on the client's server.");
    }

    [Fact]
    public void Every_configured_script_is_copied_beside_the_binary()
    {
        // THE HALF-WIRED CASE. Without a copy rule the file is in the repo, the entry is offered on
        // Full Audit, and the run dies with "Script not found" the first time a client ticks it,
        // because the runner does Path.Combine(AppContext.BaseDirectory, "scripts", ScriptPath) and
        // nothing put the file there.
        var copied = ScriptsCopiedByTheCsproj();

        var uncopied = ScriptPathsInConfig()
            .Where(s => !copied.Contains(s, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Assert.True(uncopied.Count == 0,
            "these scripts have a configuration entry and no csproj copy rule: "
            + string.Join(", ", uncopied)
            + ". Add <None Update=\"scripts\\NAME.sql\"><CopyToOutputDirectory>PreserveNewest"
            + "</CopyToOutputDirectory></None> beside the others in SQLTriage.csproj.");
    }

    [Fact]
    public void Every_copied_script_is_a_file_that_exists()
    {
        var onDisk = ScriptFilesOnDisk();

        var phantom = ScriptsCopiedByTheCsproj()
            .Where(s => !onDisk.Contains(s, StringComparer.OrdinalIgnoreCase))
            .Where(s => !ScriptsBuiltFromElsewhere.ContainsKey(s))
            .ToList();

        Assert.True(phantom.Count == 0,
            "SQLTriage.csproj promises to copy scripts that are not in scripts/ and are not built "
            + "from elsewhere: " + string.Join(", ", phantom));
    }

    [Fact]
    public void No_script_is_shipped_without_being_offered()
    {
        // The quiet failure: a .sql file that ships in every build and installer and that nothing
        // ever runs. Dead weight in the public mirror at best, and at worst a script somebody
        // believes is part of the audit.
        var configured = ScriptPathsInConfig();

        var orphans = ScriptFilesOnDisk()
            .Where(s => !configured.Contains(s, StringComparer.OrdinalIgnoreCase))
            .Where(s => !KnownUnwiredScripts.ContainsKey(s))
            .ToList();

        Assert.True(orphans.Count == 0,
            "these scripts are in scripts/ with no entry in Config/script-configurations.json: "
            + string.Join(", ", orphans)
            + ". Either add the entry, or add the file to KnownUnwiredScripts with the reason it "
            + "is there. An unexplained orphan is how a half-wired script hides.");
    }

    [Fact]
    public void The_shipped_configuration_names_each_script_once()
    {
        // Two entries pointing at one file would install it twice and run it twice, and for an
        // entry with an EXEC in SqlQueryForOutput that is two full audits per tick.
        var paths = ScriptPathsInConfig();

        var duplicated = paths
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicated.Count == 0,
            "Config/script-configurations.json names these scripts more than once: "
            + string.Join(", ", duplicated));
    }
}
