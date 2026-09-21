/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

/// <summary>
/// THE INVARIANT, and it is stated here because this is the file that measures it:
///
/// <para><b>No folder the application writes to at runtime may sit on the update overwrite path.</b></para>
///
/// <para>WHY THIS IS A CENSUS AND NOT A LIST. Three folders have now been fixed one at a time -
/// <c>config\</c> (2026-09-10 round one), <c>docs\</c> (round two, hours later), <c>BPScripts\</c>
/// (2026-09-11) - and each was found by a human noticing, not by an instrument. The guard that
/// already existed,
/// <c>PayloadConfigSplitTests.Every_top_level_payload_item_has_an_update_decision</c>, could not
/// catch the third: <c>BPScripts</c> WAS in <c>$copyItems</c>, so it HAD a recorded decision and the
/// tripwire passed. That guard asserts that a decision was recorded. It cannot assert the decision
/// is RIGHT for a folder the app writes into - the shape named in
/// <c>a-parity-test-validates-agreement-not-completeness</c>, where an instrument reports its own
/// agreement as coverage. This class derives the answer from the code that WRITES.</para>
///
/// <para>⚠ IT DOES NOT PIN A COUNT, deliberately. Prior censuses over this tree returned 37, 48 or
/// 59 sites depending on which base expression was allowed, and four more sites use literal relative
/// path strings that no <c>Path.Combine</c> census can see. A count measures the pattern, not the
/// code. What is pinned is the SET of folders, checked against the SET the deploy script overwrites,
/// with every exception carrying a written reason.</para>
///
/// <para>⚠ IT OVER-APPROXIMATES ON PURPOSE, and the direction matters. A file that resolves
/// <c>&lt;base&gt;\Foo</c> and also contains any file-writing call counts Foo as written-into, even
/// when the write targets somewhere else. Tracking which write goes with which path would need
/// dataflow, and a dataflow census written in regex UNDER-reports - the first draft of this
/// enumerator missed <c>BPScripts</c> itself, the very folder this lane exists for, because
/// <c>_scriptsPath</c> is assigned through a <c>??</c>. Under-reporting is a false green. The
/// over-approximation's only cost is that a read-only folder in a file that writes elsewhere needs a
/// recorded reason - which is the same thing <c>$deliberatelyNotCopied</c> already asks for, and
/// exactly what <c>ConfigScripts</c> carries below.</para>
///
/// <para>⚠⚠ <b>THE BLIND SPOT, NAMED RATHER THAN LEFT TO BE DISCOVERED: this census cannot see a
/// CROSS-FILE writer.</b> <see cref="WriteSites"/> requires the base-rooted resolution and the write
/// call to be in the SAME source file, so a folder resolved in <c>A.cs</c> and written by a helper in
/// <c>B.cs</c> - or resolved into a field, a DI-injected service or a constant that another type
/// consumes - is invisible to it and would pass this class silently. Three payload folders resolve
/// base-rooted in files containing no write call at all and are therefore reported clean on that
/// basis alone: <c>Assets</c>, <c>wwwroot</c> and <c>SQLTriage.exe</c>. The 2026-09-11 cold gate
/// checked all three by hand and found them read-only <b>today</b>, which is a measurement with a
/// date on it and not a property. A future writer reached through a helper in another file would not
/// go red here. Closing it needs dataflow across compilation units, which is a Roslyn analyser rather
/// than a regex; until then this paragraph is the honest statement of what the green means.</para>
/// </summary>
public class RuntimeWriteFolderCensusTests
{
    private readonly ITestOutputHelper _out;

    public RuntimeWriteFolderCensusTests(ITestOutputHelper output) => _out = output;

    // ── The enumerator ────────────────────────────────────────────────────────────────────────

    /// <summary>Production source only. Tests are not shipped and tools\ is not in the solution.</summary>
    private static readonly string[] ExcludedDirectories =
    {
        "bin", "obj", "publish", "release", "Tests", "tools", "corpus", "llmck", "lib",
        "BenchmarkSuite1", "PerformanceMonitor-main", "PerformanceMonitor_db",
        "SQLTriage-RAG-Builder", "node_modules", "evidence", "examples", "templates", "build",
    };

    /// <summary>
    /// Both spellings of the base directory, because the tree uses both and a census over either one
    /// alone is blind to a third of its own sites.
    /// </summary>
    private static readonly Regex BaseRootedResolution = new(
        @"Path\.Combine\(\s*(?:AppContext\.BaseDirectory|AppDomain\.CurrentDomain\.BaseDirectory)\s*,\s*""(?<segment>[^""]+)""",
        RegexOptions.Compiled);

    /// <summary>
    /// Anything that creates, replaces, moves or removes a file or directory. <c>Directory.CreateDirectory</c>
    /// is in the list: a folder the app CREATES is a folder the app populates, and it is the call that
    /// makes <c>BPScripts\</c> visible at all (<c>Data\BPScriptService.cs:29</c>).
    /// </summary>
    private static readonly Regex WriteCall = new(
        @"(?<call>File\.(?:WriteAllText|WriteAllTextAsync|WriteAllBytes|WriteAllBytesAsync|WriteAllLines|WriteAllLinesAsync|AppendAllText|AppendAllTextAsync|AppendAllLines|Copy|Move|Replace|Delete|Create|OpenWrite|AppendText|CreateText)"
        + @"|Directory\.(?:CreateDirectory|Delete|Move)"
        + @"|new\s+StreamWriter|new\s+FileStream)\s*\(",
        RegexOptions.Compiled);

    private sealed record WriteSite(string Folder, string File, int ResolveLine, int WriteLine, string Call);

    /// <summary>
    /// Walks explicitly and PRUNES rather than enumerating everything and filtering after, because
    /// <c>bin\</c>, <c>obj\</c> and <c>.git\</c> hold tens of thousands of files on a built tree and a
    /// filter-afterwards enumeration walks all of them on every run.
    /// </summary>
    private static IEnumerable<string> ProductionSourceFiles()
    {
        var pending = new Stack<string>();
        pending.Push(FrkContractTests.RepoRoot());

        while (pending.Count > 0)
        {
            var dir = pending.Pop();

            string[] subdirs;
            string[] files;
            try
            {
                subdirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.')) continue;
                if (ExcludedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                pending.Push(sub);
            }

            foreach (var f in files)
            {
                if (f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                    yield return f;
            }
        }
    }

    /// <summary>
    /// Every top-level folder under the install directory that production code both RESOLVES and, in
    /// the same file, WRITES. One entry per (folder, file); the line numbers name the owner so a
    /// failure is actionable rather than a folder name on its own.
    /// </summary>
    private static IReadOnlyList<WriteSite> WriteSites()
    {
        var root = FrkContractTests.RepoRoot();
        var sites = new List<WriteSite>();

        foreach (var path in ProductionSourceFiles())
        {
            string text;
            try { text = File.ReadAllText(path); }
            catch (IOException) { continue; }

            if (!text.Contains("BaseDirectory", StringComparison.Ordinal)) continue;

            var lines = text.Replace("\r\n", "\n").Split('\n');

            // ⚠ EVERY SITE, NOT THE FIRST PER FILE. This was a Dictionary<string,int> that kept only
            // the first resolution of each folder in each file, which is the exact defect the lane
            // brief's C7 rule 1 names: enumerate MEMBERS/SITES, never types or files. A census keyed
            // per file under-counts by construction and keeps reporting "found it" while a second
            // copy inside the same file drifts - WaitSignalRatioAlertTests carries two copies of one
            // resolver, at :103 and :229, and is the worked example. A List per folder costs nothing
            // and makes the report name every line a reader would have to go and fix.
            var resolved = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var writes = new List<(int Line, string Call)>();

            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match m in BaseRootedResolution.Matches(lines[i]))
                {
                    var top = m.Groups["segment"].Value.Replace('/', '\\').Split('\\')[0];
                    if (!resolved.TryGetValue(top, out var at)) resolved[top] = at = new List<int>();
                    at.Add(i + 1);
                }

                foreach (Match m in WriteCall.Matches(lines[i]))
                    writes.Add((i + 1, m.Groups["call"].Value));
            }

            if (writes.Count == 0) continue;

            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            foreach (var (folder, at) in resolved)
                foreach (var line in at)
                    sites.Add(new WriteSite(folder, rel, line, writes[0].Line, writes[0].Call));
        }

        return sites;
    }

    // ── The deploy script's overwrite path, read from the script ──────────────────────────────

    private static string DeployScriptText() =>
        File.ReadAllText(Path.Combine(FrkContractTests.RepoRoot(), "tools", "Deploy-SQLTriageService.ps1"));

    /// <summary>
    /// The <c>$copyItems</c> array, as a set. Parsed as the ASSIGNMENT rather than by scanning the
    /// whole script, so a name that appears in a comment is not mistaken for an entry.
    /// </summary>
    private static IReadOnlySet<string> CopyItems()
    {
        var script = DeployScriptText();
        var start = script.IndexOf("$copyItems = @(", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1,
            "tools\\Deploy-SQLTriageService.ps1 must still assign $copyItems as an array literal, or this "
            + "census has nothing to check the writers against and would go silently green");

        var end = script.IndexOf("\n)", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "the $copyItems array literal must still be closed by a lone ')'");

        return Regex.Matches(script[start..end], @"^\s*'(?<name>[^']+)'", RegexOptions.Multiline)
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The <c>$deliberatelyNotCopied</c> keys, as a set. Same parsing discipline.</summary>
    private static IReadOnlySet<string> SkippedItems()
    {
        var script = DeployScriptText();
        var start = script.IndexOf("$deliberatelyNotCopied = @{", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "tools\\Deploy-SQLTriageService.ps1 must still assign $deliberatelyNotCopied");

        var end = script.IndexOf("\n}", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);

        return Regex.Matches(script[start..end], @"^\s*'(?<name>[^']+)'\s*=", RegexOptions.Multiline)
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // ── The recorded exceptions, each with the reason it is one ───────────────────────────────

    /// <summary>
    /// Folders that the enumerator reports as written-into AND that the update DOES overwrite, each
    /// with the decision that makes it acceptable. An entry here is a RECORDED decision, in the same
    /// spirit as <c>$deliberatelyNotCopied</c>: a folder that turns up later with no entry goes red.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> RecordedOverwriteExceptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["scripts"] =
                "CLOSED BY ADRIAN 2026-09-11: 'it can download the latest versions of things from the "
                + "official sources, and if no outside source exists then keep the current.' "
                + "Data/AutoUpdateService.cs:885 resolves <base>\\scripts and :896 writes into it in "
                + "DownloadScriptUpdatesAsync; the second path resolves the same folder at :834 and "
                + "creates it at :835 - so it violates the invariant literally. ⚠ CORRECTED 2026-09-11 "
                + "(round two): an earlier draft cited ':834/:847' as though :847 were a second write. It "
                + "is not. :847 is a Path.Combine and :848-849 are File.Exists/ComputeGitBlobSha, all "
                + "read-only; the ONLY file write on either path is :896. The extension filter that keeps "
                + "this folder in its safe class is the if at :840 with its continue at :841. It keeps "
                + "today's "
                + "behaviour because the exposure is a DIFFERENT ONE: the content comes from the same "
                + "canonical GitHub source the release itself does, nothing there is operator-authored, and "
                + "an overwrite is SELF-HEALING - the next update check re-downloads. That is staleness for "
                + "one check interval, not data loss, and freezing it would stop customers receiving "
                + "improved diagnostic scripts. identity_manifest.sql has no upstream, but it is a "
                + "generated product artefact every payload carries, so an overwrite always replaces it "
                + "with a current one - it can never be LOST, which is what Adrian's second clause "
                + "protects. THE OTHER TWO FILES THE CENSUS REPORTS UNDER THIS FOLDER ARE READ-ONLY, and "
                + "are named here so nobody has to re-derive them: Data/DiagnosticScriptRunner.cs:236 "
                + "resolves <base>\\scripts\\<ScriptPath> and only File.Exists/reads it (its write at :129 "
                + "targets Config\\script-configurations.json), and "
                + "Data/Services/Portal/Export/ExportPackRunner.cs:716 resolves "
                + "<base>\\scripts\\identity_manifest.sql read-only (its Directory.CreateDirectory at :680 "
                + "targets a caller-supplied run folder). Verified 2026-09-11. The offline half of this "
                + "reason is pinned by "
                + "Every_file_the_payload_delivers_into_the_scripts_folder_is_a_dot_sql_file.",

            ["ConfigScripts"] =
                "NOT ACTUALLY A WRITER - this is the over-approximation working as designed, recorded rather "
                + "than silently excluded. Data/Services/ServerConfigScriptService.cs:75 resolves "
                + "<base>\\ConfigScripts\\Server Configuration and Hardening.sql and only ever READS it "
                + "(:77 File.Exists, :141 and :711 File.ReadAllTextAsync). The four write calls in that file "
                + "- :919, :921, :1003, :1005 - target the caller-supplied outputDir, which is the output\\ "
                + "folder, not ConfigScripts\\. Verified 2026-09-11: `\"ConfigScripts\"` appears as a literal "
                + "in exactly one place in production source, that one resolution.",
        };

    // ── The pins ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_folder_the_app_writes_into_is_on_the_update_overwrite_path()
    {
        // THE INVARIANT. Three folders were fixed one at a time by somebody noticing; this is the
        // instrument that notices the fourth.
        var copied = CopyItems();
        var sites = WriteSites();

        var violations = sites
            .Where(s => copied.Contains(s.Folder))
            .Where(s => !RecordedOverwriteExceptions.ContainsKey(s.Folder))
            .OrderBy(s => s.Folder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.File, StringComparer.Ordinal)
            .ToList();

        foreach (var s in sites.Where(s => copied.Contains(s.Folder)).OrderBy(s => s.Folder))
            _out.WriteLine($"on the overwrite path : {s.Folder,-16} {s.File}:{s.ResolveLine} (+ {s.Call} at :{s.WriteLine})"
                           + (RecordedOverwriteExceptions.ContainsKey(s.Folder) ? "  [RECORDED]" : "  ** VIOLATION **"));

        violations.Should().BeEmpty(
            "a folder production code writes into at runtime must not be in $copyItems in "
            + "tools\\Deploy-SQLTriageService.ps1, because an update copies over it and destroys whatever "
            + "the app - or the operator through the app - put there. Ship the shipped copy as a "
            + "<name>.default\\ sibling and seed it with ConfigDefaultsSeeder.SeedPair instead, the way "
            + "config\\, docs\\ and BPScripts\\ are done. If the overwrite really is correct, add the "
            + "folder to RecordedOverwriteExceptions with the reason. Violations: "
            + string.Join("; ", violations.Select(v => $"{v.Folder} ({v.File}:{v.ResolveLine})")));
    }

    [Fact]
    public void The_write_census_can_see_the_two_folders_it_was_built_from()
    {
        // A CONTROL THAT CANNOT REPRODUCE CANNOT REFUTE. The test above is a negative: it passes when
        // it finds nothing. That is worth nothing until the enumerator is shown to find the two
        // folders that are KNOWN to be written into. The first draft of this enumerator did not - it
        // tracked variables through assignments and lost _scriptsPath at the ?? in
        // BPScriptService.cs:27, so it reported BPScripts clean while the lane fixing BPScripts was
        // being written. This is the guard against that recurring.
        var sites = WriteSites();
        var byFolder = sites.GroupBy(s => s.Folder, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        _out.WriteLine("folders with a runtime writer: "
                       + string.Join(", ", byFolder.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)));

        byFolder.Should().ContainKey("BPScripts",
            "Data/BPScriptService.cs:27 resolves <base>\\BPScripts and :29/:70 write there. If the "
            + "enumerator cannot see this one it cannot see any of them, and the invariant test above is "
            + "a green light attached to nothing.");
        byFolder["BPScripts"].Should().Contain(s => s.File.EndsWith("Data/BPScriptService.cs", StringComparison.Ordinal));

        byFolder.Should().ContainKey("scripts",
            "Data/AutoUpdateService.cs:885 resolves <base>\\scripts and :896 writes there");
        byFolder["scripts"].Should().Contain(s => s.File.EndsWith("Data/AutoUpdateService.cs", StringComparison.Ordinal));

        byFolder.Should().ContainKey("Config",
            "the config folder has the most writers of any folder in the tree; an enumerator that misses "
            + "it has stopped matching");
    }

    [Fact]
    public void Every_recorded_overwrite_exception_is_still_a_real_finding()
    {
        // An exception list rots the moment the thing it excuses goes away, and a stale entry is a
        // hole: it would silence a NEW writer that happened to reuse the name.
        var sites = WriteSites();
        var copied = CopyItems();
        var seen = sites.Select(s => s.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (folder, reason) in RecordedOverwriteExceptions)
        {
            seen.Should().Contain(folder,
                $"RecordedOverwriteExceptions still excuses '{folder}', but the census no longer finds any "
                + "production file that both resolves it under the base directory and writes. Either the "
                + "writer was removed - delete the entry - or the enumerator stopped seeing it, which is "
                + "worse. Recorded reason: " + reason);

            copied.Should().Contain(folder,
                $"RecordedOverwriteExceptions excuses '{folder}' being overwritten by an update, but it is "
                + "no longer in $copyItems, so there is nothing left to excuse. Delete the entry rather "
                + "than leave a standing permission nobody needs.");

            reason.Should().NotBeNullOrWhiteSpace();
            reason.Length.Should().BeGreaterThan(80,
                $"the reason recorded for '{folder}' must say what makes the overwrite acceptable, not just "
                + "that somebody decided it was");
        }
    }

    [Fact]
    public void Every_file_the_payload_delivers_into_the_scripts_folder_is_a_dot_sql_file()
    {
        // THE OFFLINE HALF OF THE scripts\ EXCEPTION, and the reason it is a test rather than a
        // sentence. That exception rests on a PROPERTY - every file in scripts\ is either re-served by
        // the update source or a generated payload artefact - and a property can rot the moment
        // somebody adds a file. Most of it cannot be checked offline: whether GitHub still serves a
        // given name is a live fact. THIS part can be, and it is the part that decides:
        // Data/AutoUpdateService.cs:841 skips every extension except .sql, so the downloader cannot
        // reach a non-.sql file AT ALL. Such a file silently leaves the "re-served" class, and if it is
        // ever operator-writable the folder becomes a second BPScripts - overwritten by every route,
        // with a recorded reason that no longer describes it.
        //
        // So: a new .sql file inherits the exception legitimately; anything else must go RED here and
        // be decided on its own terms. Note this deliberately does NOT pin a count - seven files today,
        // and a count would measure the census rather than the code.
        var csproj = XDocument.Load(Path.Combine(FrkContractTests.RepoRoot(), "SQLTriage.csproj"));

        static bool IntoScripts(string value) =>
            value.StartsWith(@"scripts\", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase);

        // BOTH shapes that put a file into the payload's scripts\ folder, because either alone is
        // blind to the other: a None Update on a file that already lives in scripts\, and any item
        // retargeted there by TargetPath (which is how identity_manifest.sql arrives, from
        // Data\Services\Portal\Export\).
        var delivered = new List<string>();

        foreach (var e in csproj.Descendants())
        {
            var targetPath = e.Elements().FirstOrDefault(c => c.Name.LocalName == "TargetPath")?.Value;
            if (targetPath is not null && IntoScripts(targetPath))
            {
                delivered.Add(targetPath);
                continue;
            }

            var path = e.Attribute("Update")?.Value ?? e.Attribute("Include")?.Value;
            if (path is null || !IntoScripts(path)) continue;
            if (!e.Elements().Any(c => c.Name.LocalName is "CopyToOutputDirectory" or "CopyToPublishDirectory"))
                continue;

            delivered.Add(path);
        }

        foreach (var d in delivered.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            _out.WriteLine("payload delivers into scripts\\ : " + d);

        delivered.Should().HaveCountGreaterThan(5,
            "the csproj reader found almost nothing, so this pin would pass on a tree that ships "
            + "anything at all. The rules it is looking for are <None Update=\"scripts\\...\"> with a "
            + "CopyToOutputDirectory, and any <TargetPath>scripts\\...</TargetPath>. If those shapes "
            + "changed, fix the reader - do not delete the pin.");

        var notSql = delivered
            .Where(d => !d.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .ToList();

        notSql.Should().BeEmpty(
            "the payload delivers a non-.sql file into scripts\\: " + string.Join("; ", notSql)
            + ". Data/AutoUpdateService.cs:841 filters the re-download to *.sql, so that file can never "
            + "be refreshed from the update source - it silently leaves the class that makes "
            + "RecordedOverwriteExceptions[\"scripts\"] true, and every update route overwrites it. "
            + "Decide it on its own terms: if an operator can ever write it, it needs the "
            + "<name>.default\\ + ConfigDefaultsSeeder treatment that config\\, docs\\ and BPScripts\\ "
            + "have; if it is a generated payload artefact like identity_manifest.sql, extend the "
            + "recorded reason to say so.");
    }

    [Fact]
    public void The_best_practice_scripts_are_off_the_overwrite_path_on_every_route()
    {
        // The lane's own claim, checked on all three delivery routes at once rather than on the one
        // that happened to be edited. The route that matters most is the third: the applier every
        // shipped install runs is a wholesale robocopy that honours no list at all, so only the
        // PAYLOAD not containing BPScripts\ can protect it.
        var copied = CopyItems();
        var skipped = SkippedItems();
        var root = FrkContractTests.RepoRoot();

        copied.Should().NotContain("BPScripts",
            "an update must not copy over <install>\\BPScripts: Pages\\BestPractice.razor:87 lets the "
            + "operator save a script straight into it");
        copied.Should().Contain("BPScripts.default",
            "the stock scripts must still be delivered, as defaults, or a fresh install has no Best "
            + "Practice scripts at all");
        skipped.Should().Contain("BPScripts",
            "and the decision must be RECORDED, not a silence - silence is how docs\\ shipped for two "
            + "months with no update rule");

        // Route two: the installer. It must ship the defaults and never lay down {app}\BPScripts,
        // because a file the installer lays down is a file the pre-upgrade uninstall deletes.
        var iss = File.ReadAllText(Path.Combine(root, "installer", "SQLTriage.iss"));
        var fileEntries = Regex.Matches(iss, @"^Source:\s*""(?<source>[^""]+)"";\s*DestDir:\s*""(?<dest>[^""]+)""",
                                        RegexOptions.Multiline)
                               .Select(m => m.Groups["dest"].Value)
                               .ToList();
        fileEntries.Should().NotBeEmpty("the [Files] reader must still match, or this route is unchecked");
        fileEntries.Should().NotContain(d => d.Equals(@"{app}\BPScripts", StringComparison.OrdinalIgnoreCase),
            "installer/SQLTriage.iss must not install into {app}\\BPScripts. Anything it lays down there "
            + "goes into the uninstall log, and CurStepChanged runs the previous version's uninstaller "
            + "before the new [Files] copy - so it would be deleted on the very next upgrade.");
        fileEntries.Should().Contain(d => d.Equals(@"{app}\BPScripts.default", StringComparison.OrdinalIgnoreCase),
            "installer/SQLTriage.iss must install the stock scripts as {app}\\BPScripts.default");

        // ...and it must carry the operator's scripts across that uninstall, for installs made by any
        // build before this one, whose uninstall log still names {app}\BPScripts\*.
        var backupIdx = iss.IndexOf("BackupOperatorBPScripts(BPScriptsBackupDir)", StringComparison.Ordinal);
        var execIdx = iss.IndexOf("Exec(sUnInstallString", StringComparison.Ordinal);
        var restoreIdx = iss.IndexOf("RestoreOperatorBPScripts(BPScriptsBackupDir)", StringComparison.Ordinal);
        backupIdx.Should().BeGreaterThan(-1,
            "upgrading from a build that predates this lane runs an uninstaller whose log still names "
            + "{app}\\BPScripts\\*, so it deletes the operator's scripts - and nothing re-lays them now, "
            + "because [Files] no longer ships the folder. Without this backup the fix is worse than the "
            + "defect on the Inno route.");
        restoreIdx.Should().BeGreaterThan(-1);
        backupIdx.Should().BeLessThan(execIdx, "the scripts must be backed up BEFORE the pre-upgrade uninstaller runs");
        execIdx.Should().BeLessThan(restoreIdx, "and restored AFTER it");

        // Route three: the applier every shipped install actually runs. It is not list-driven, so the
        // only protection is that the payload has no BPScripts\ in it - which is the csproj's job.
        var applier = File.ReadAllText(Path.Combine(root, "Data", "AutoUpdateService.cs"));
        applier.Should().Contain("robocopy %SRC%",
            "Data/AutoUpdateService.cs must still generate the wholesale robocopy applier this design is "
            + "built around. If it grew /XF or /XD exclusions, re-derive whether the payload split is "
            + "still the right shape before weakening it.");

        var csproj = XDocument.Load(Path.Combine(root, "SQLTriage.csproj"));
        var targets = csproj.Descendants()
            .Where(e => (e.Attribute("Update")?.Value ?? "").StartsWith(@"BPScripts\", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Elements().FirstOrDefault(c => c.Name.LocalName == "TargetPath")?.Value)
            .ToList();

        targets.Should().NotBeEmpty("the csproj must still carry the BPScripts copy rules");
        var misdirected = targets
            .Where(v => v is null || !v.StartsWith("BPScripts.default/", StringComparison.Ordinal))
            .ToList();
        misdirected.Should().BeEmpty(
            "every BPScripts copy rule must retarget into BPScripts.default/. One rule without a TargetPath "
            + "puts that file back into the payload's BPScripts\\ folder, and the wholesale robocopy then "
            + "overwrites the operator's copy of exactly that script - silently, because $preservedFolders "
            + "would report the folder unchanged for every OTHER file in it.");
    }

    [Fact]
    public void Nothing_in_the_payload_lands_under_the_operator_scripts_folder()
    {
        // MEASURED 2026-09-11, AFTER the retarget above was believed complete, and that is why this is
        // a test rather than a sentence. A full Debug publish produced BPScripts.default\ with all
        // eleven scripts - and ALSO a payload BPScripts\ruleset.json, 771 KB. Nothing in
        // SQLTriage.csproj asked for it: the Web SDK's IMPLICIT Content globs (**\*.config and
        // **\*.json) carry CopyToPublishDirectory=PreserveNewest, so a .json left in the source
        // BPScripts\ folder publishes into a payload BPScripts\ folder. The wholesale robocopy at
        // Data\AutoUpdateService.cs:740 then creates <install>\BPScripts and writes into it - the exact
        // property this lane exists to remove, surviving by a route no copy list can express.
        //
        // NO OPERATOR FILE WAS AT RISK: Pages\BestPractice.razor:216 saves to _editingScript.FileName
        // and that list comes from BPScriptService.SyncScriptsFromFolder (Data\BPScriptService.cs:75),
        // which enumerates "*.sql" only, so ruleset.json is not a name any operator can produce. It was
        // a COMPLETENESS defect, not a data-loss one - and the completeness half is what the rest of
        // this class is for.
        //
        // ⚠ WHY IT STARTS FROM THE FILES AND NOT FROM THE FIX. The fix is one <Content Remove>.
        // Asserting that the Remove is present would be an agreement check of exactly the shape named
        // in a-parity-test-validates-agreement-not-completeness: green while a new stray sat beside it
        // uncovered. So this enumerates the FILES ON DISK in the source folder and asks, per file,
        // whether anything can carry it into the payload's BPScripts\.
        //
        // ⚠ WHAT IT CANNOT SEE, STATED: it does not run MSBuild. It carries the SDK's implicit publish
        // globs as a WRITTEN fact (**\*.config, **\*.json). If a future SDK widens that set this test
        // will not notice. The instrument that would is a publish measurement, and the standing one is
        // PayloadConfigSplitTests.Every_top_level_payload_item_has_an_update_decision - a
        // [LiveFact("SQLTRIAGE_PAYLOAD_DIR")], so it runs only when a built payload is pointed at it.
        var root = FrkContractTests.RepoRoot();
        var source = Path.Combine(root, ConfigDefaultsSeeder_BPScriptsSourceFolder);

        Directory.Exists(source).Should().BeTrue(
            "the stock scripts still LIVE in BPScripts\\ in the tree - only their payload path moved. If "
            + "this folder is gone the enumerator below is looking at nothing and every assertion in this "
            + "test is a green light attached to nothing.");
        Directory.GetFiles(source, "*.sql").Should().NotBeEmpty(
            "and it must still hold the stock .sql scripts, for the same reason");

        var csproj = XDocument.Load(Path.Combine(root, "SQLTriage.csproj"));

        // (1) No EXPLICIT rule may retarget anything into the payload's BPScripts\. Note that
        //     "BPScripts.default/..." does not match either prefix, so the eleven correct rules are not
        //     mistaken for violations.
        var retargetedIn = csproj.Descendants()
            .Select(e => (Item: e.Attribute("Update")?.Value ?? e.Attribute("Include")?.Value ?? e.Name.LocalName,
                          Target: e.Elements().FirstOrDefault(c => c.Name.LocalName == "TargetPath")?.Value))
            .Where(t => t.Target is not null)
            .Where(t => t.Target!.StartsWith(@"BPScripts\", StringComparison.OrdinalIgnoreCase)
                        || t.Target!.StartsWith("BPScripts/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        retargetedIn.Should().BeEmpty(
            "a csproj rule delivers a file into the payload's BPScripts\\ folder: "
            + string.Join("; ", retargetedIn.Select(t => t.Item + " -> " + t.Target))
            + ". That folder is created and written by the app at runtime (Data\\BPScriptService.cs:27,29) "
            + "and the shipped applier is a wholesale robocopy with no exclusions, so anything delivered "
            + "there is on the overwrite path. Retarget it into BPScripts.default\\ and let "
            + "ConfigDefaultsSeeder seed it.");

        // (2) ...and no IMPLICIT one may either. These are the extensions the Web SDK publishes on its
        //     own; a file of one of these types sitting in the source folder reaches the payload unless
        //     a Content Remove covers it.
        var implicitlyPublished = new[] { ".json", ".config" };
        var strays = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
            .Where(f => implicitlyPublished.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(root, f).Replace('/', '\\'))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var s in strays)
            _out.WriteLine("implicitly publishable by the SDK, so it needs a Content Remove : " + s);
        _out.WriteLine($"({strays.Count} such file(s) in {ConfigDefaultsSeeder_BPScriptsSourceFolder}\\)");

        if (strays.Count == 0) return;

        var contentRemoves = csproj.Descendants()
            .Where(e => e.Name.LocalName == "Content")
            .Select(e => e.Attribute("Remove")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .SelectMany(v => v!.Split(';'))
            // The file uses a doubled-backslash spelling in places (BPScripts\\Ignore\\*\\*), so both
            // spellings are normalised before comparison rather than one being assumed.
            .Select(v => v.Trim().Replace('/', '\\').Replace(@"\\", @"\"))
            .ToList();

        contentRemoves.Should().Contain(
            r => r.Equals(@"BPScripts\**", StringComparison.OrdinalIgnoreCase),
            "SQLTriage.csproj must carry <Content Remove=\"BPScripts\\**\" />. Without it the Web SDK's "
            + "implicit **\\*.json and **\\*.config globs publish these files into a payload BPScripts\\ "
            + "folder: " + string.Join("; ", strays)
            + ". MEASURED 2026-09-11 before that line existed: the payload carried BPScripts\\ruleset.json "
            + "(771 KB) beside a correct BPScripts.default\\, so the folder the operator authors in was "
            + "still on the wholesale-robocopy overwrite path. A per-file Remove is not enough - the next "
            + "stray would not be covered, which is the whole reason this pin reads the folder.");
    }

    /// <summary>
    /// The SOURCE folder name, which is deliberately NOT
    /// <c>ConfigDefaultsSeeder.BPScriptsDefaultsFolderName</c>: the files still live in
    /// <c>BPScripts\</c> in the tree and only their PAYLOAD path moved to <c>BPScripts.default\</c>.
    /// Named here so the two are never confused at a call site.
    /// </summary>
    private const string ConfigDefaultsSeeder_BPScriptsSourceFolder = "BPScripts";

    [Fact]
    public void The_community_profile_still_excludes_the_script_payload_under_its_new_name()
    {
        // The trap this pin exists for: the payload folder is now BPScripts.default\ but the profile
        // removal must keep naming the SOURCE glob BPScripts\**, because the files still live in
        // BPScripts\ in the tree. "Tidying" the removal to match the new payload name would match
        // nothing and the COMMUNITY build would quietly start shipping the gated scripts. So the
        // source folder is DERIVED from the csproj rules rather than typed here.
        var root = FrkContractTests.RepoRoot();
        var csproj = XDocument.Load(Path.Combine(root, "SQLTriage.csproj"));

        var sourceFolders = csproj.Descendants()
            .Where(e => e.Elements().Any(c => c.Name.LocalName == "TargetPath"
                                              && c.Value.StartsWith("BPScripts.default/", StringComparison.Ordinal)))
            .Select(e => (e.Attribute("Update")?.Value ?? "").Split('\\')[0])
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        sourceFolders.Should().ContainSingle(
            "every file that publishes into BPScripts.default/ must come from one source folder, or the "
            + "profile removal below cannot be expressed as one glob");
        var source = sourceFolders[0];
        _out.WriteLine($"source folder feeding BPScripts.default/ : {source}");

        var removals = XDocument.Load(Path.Combine(root, "buildprofile.targets"))
            .Descendants()
            .Where(e => e.Attribute("Remove") is not null)
            .Select(e => (Element: e.Name.LocalName,
                          Removed: e.Attribute("Remove")!.Value,
                          Group: e.Parent?.Attribute("Condition")?.Value ?? ""))
            .ToList();

        var premium = removals
            .Where(r => r.Group.Contains("SQLTExcludePremium", StringComparison.OrdinalIgnoreCase))
            .ToList();
        premium.Should().NotBeEmpty("the premium exclusion ItemGroup must still exist");

        foreach (var element in new[] { "None", "Content" })
        {
            premium.Should().Contain(
                r => r.Element == element
                     && r.Removed.Equals(source + @"\**", StringComparison.OrdinalIgnoreCase),
                $"buildprofile.targets must still carry <{element} Remove=\"{source}\\**\" /> under "
                + "SQLTExcludePremium. The scripts are proprietary and must never reach a community or "
                + "public build; the payload rename to BPScripts.default\\ moved the TARGET path only, so "
                + "a removal naming the new name would match nothing and ship them.");
        }
    }
}
