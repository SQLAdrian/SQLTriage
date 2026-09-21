/* In the name of God, the Merciful, the Compassionate */

// DOES installer/SQLTriage.iss ACTUALLY COMPILE, AND WHAT DOES IT PUT IN THE PACKAGE.
//
// WHY THIS EXISTS ALONGSIDE ScriptCensusTests. That file's fourth axis reads the .iss as TEXT. It
// is a lint, and it says so. This harness is the measurement: it runs the real Inno Setup compiler
// against the real .iss and reads the file manifest the compiler itself prints, so a Source line
// whose folder is empty, misspelled, or guarded out is visible as an absent file rather than as
// present text.
//
// WHAT IT PROVES WHEN ARMED
//   1. The .iss compiles against a FULL publish shape, and the compiled package contains
//      scripts\sp_Blitz.sql. That folder was absent from [Files] from e6e12e7 until 2026-08-24.
//   2. The .iss compiles against a COMMUNITY publish shape, which has no BPScripts.default\ folder.
//      Unguarded, that line made iscc abort with exit 2 ("No files found matching ...\BPScripts\*")
//      and the public release script degraded the community release to zip-only without failing. Measured
//      2026-08-24: before the guard, exit 2 on line 123. So no community user has ever had an
//      installer, which is exactly why nobody noticed defect 1.
//   3. NON-VACUITY. A decoy file is planted in the stub source tree that no [Files] line names. It
//      must NOT appear in the manifest. Without this arm a manifest reader that matched everything,
//      or a compile that silently swept the whole SourceDir, would report green.
//   4. THE OPERATOR'S OWN SCRIPTS ARE NOT PACKAGED (2026-09-11, lane bpscripts-operator-edits-are-lost).
//      The payload folder is BPScripts.default\ from that lane on, and a BPScripts\ decoy is planted
//      beside it. Anything [Files] lays down enters the uninstall log, and CurStepChanged runs the
//      PREVIOUS version's uninstaller before the new copy - so an .iss line reaching {app}\BPScripts
//      is what would delete the operator's saved scripts on the next upgrade. That decoy is what makes
//      the "does not contain" assertion a measurement rather than a restatement of the stub tree.
//
// WHAT IT IS BLIND TO, AND WHO COVERS THAT. BuildStubPublishTree creates scripts\ in BOTH profiles;
// only BPScripts.default\ is community-conditional. So this harness cannot see the one case the deliberately
// UNGUARDED scripts\ Source line is exposed to: a profile whose publish tree has no scripts\ folder,
// which would abort iscc with exit 2 and degrade that release to zip-only. The stub cannot honestly
// simulate it either, because whether a real community publish produces the folder is a fact about
// the csproj and buildprofile.targets, not about this tree. No real community `dotnet publish` has
// ever been compiled against this .iss, so that remains UNTESTED here. The gap is covered by
// ScriptCensusTests.Nothing_gates_the_scripts_folder_out_of_any_build_profile, which reads the two
// files that decide it and goes red the moment a profile starts removing scripts\ or a copy rule
// gains a Condition.
//
// IT DOES NOT INSTALL ANYTHING. Compiling is inert; running the compiled Setup.exe writes an
// uninstall key, a Start-menu group and possibly an HKCU Run value, and a test suite may not do
// that to a developer's machine unattended. The install half of the measurement is a lane probe,
// run by hand on 2026-08-24 with its evidence under C:\temp\installer-lane: the unmodified .iss
// installed no scripts\ folder from a source tree that had one, and the same .iss with the scripts
// line added installed it.
//
// INERT unless armed. LiveFactAttribute reports SKIPPED (not passed) when INSTALLER_ISCC_EXE is
// unset, and RequireIscc asserts the same variable inside the body so the test FAILS rather than
// passing vacuously if that attribute is ever weakened. CI has no Inno Setup at all - the release
// workflow's installer step points at C:\Program Files (x86)\Inno Setup 6\ISCC.exe and is dead code
// behind an unconditional signing-guard exit - so on CI this harness is a skip and the text census
// in ScriptCensusTests is what stands guard.
//
// INVOCATION (first armed run 2026-08-24, Inno Setup compiler engine 6.7.1):
//   $env:INSTALLER_ISCC_EXE = "C:\GitHub\Inno Setup 6\ISCC.exe"
//   dotnet test SQLTriage.sln -c Debug --no-build --filter "FullyQualifiedName~InstallerCompileLiveTests"

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

public class InstallerCompileLiveTests
{
    private readonly ITestOutputHelper _out;
    public InstallerCompileLiveTests(ITestOutputHelper output) => _out = output;
    private void Line(string s) => _out.WriteLine(s);

    private static string? IsccExe => Environment.GetEnvironmentVariable("INSTALLER_ISCC_EXE");

    /// <summary>
    /// The guard behind <see cref="LiveFactAttribute"/>. If that attribute is ever weakened or
    /// removed, the body must FAIL rather than pass vacuously.
    /// </summary>
    private static string RequireIscc()
    {
        Assert.False(string.IsNullOrWhiteSpace(IsccExe),
            "INSTALLER_ISCC_EXE is not set, so this test has no compiler to run and nothing to "
            + "assert. It should have been SKIPPED by LiveFactAttribute; if it ran, that attribute "
            + "is no longer doing its job.");
        Assert.True(File.Exists(IsccExe),
            "INSTALLER_ISCC_EXE points at " + IsccExe + ", which does not exist.");
        return IsccExe!;
    }

    /// <summary>The name of the decoy: present in the stub source tree, named by no [Files] line.</summary>
    private const string DecoyFileName = "not-shipped-by-any-files-line.txt";

    /// <summary>
    /// A minimal stand-in for a publish tree: one stub file per [Files] Source line, plus a decoy.
    /// Contents are irrelevant; only presence is measured. Built from scratch rather than copied
    /// from a real publish tree, because a configured publish tree carries live config, output and
    /// logs and copying one is the house's "sanitize copies of configured installs" hazard.
    /// </summary>
    private static string BuildStubPublishTree(string root, bool community)
    {
        var src = Path.Combine(root, community ? "stub-community" : "stub-full");
        foreach (var d in new[]
                 {
                     src, Path.Combine(src, "config"), Path.Combine(src, "config.default"),
                     Path.Combine(src, "wwwroot"),
                     Path.Combine(src, "scripts"), Path.Combine(src, "Assets", "brand"),
                     Path.Combine(src, "docs.default", "compliance"),
                 })
            Directory.CreateDirectory(d);
        // 2026-09-11 (lane bpscripts-operator-edits-are-lost): the stock scripts publish to
        // BPScripts.default\, and BPScripts\ is now a DECOY in this tree rather than a payload folder.
        // A real publish tree has no BPScripts\ at all; planting one here is how the "the installer
        // never lays down {app}\BPScripts" assertion below becomes a measurement instead of a
        // restatement of what the stub happens to contain.
        if (!community)
        {
            Directory.CreateDirectory(Path.Combine(src, "BPScripts.default"));
            Directory.CreateDirectory(Path.Combine(src, "BPScripts"));
        }

        void Stub(string relative, string content) =>
            File.WriteAllText(Path.Combine(src, relative), content, new UTF8Encoding(false));

        Stub("SQLTriage.exe", "stub");
        Stub("LICENSE.txt", "stub");

        // The payload config split, 2026-09-10 (lane customer-update-path). The stub tree mirrors the
        // real one because that is the whole point of this test: if the .iss and the csproj disagree
        // about which folder a config file ships from, iscc aborts with "No files found matching ..."
        // and this goes red. Operator-editable files ship as DEFAULTS in config.default\; build and
        // product artefacts ship straight into config\.
        foreach (var name in new[]
                 {
                     "appsettings.json", "appsettings.Production.json",
                     "dashboard-config.json", "alert-definitions.json",
                     "scheduled-tasks.json", "script-configurations.json",
                     "power-pricing.json",
                 })
        {
            Stub(Path.Combine("config.default", name), "{}");
        }
        Stub(Path.Combine("config", "version.json"), "{}");
        Stub(Path.Combine("config", "free-bundle.dat"), "stub");
        Stub(Path.Combine("wwwroot", "index.html"), "stub");
        Stub(Path.Combine("scripts", "sp_Blitz.sql"), "SELECT 1");
        Stub(Path.Combine("scripts", "sp_PerfCheck.sql"), "SELECT 1");
        // Brand mark for generated PDFs — present in every profile (SQLTriage.csproj:406, no
        // Condition). platform-r2-07: its [Files] entry was missing, so Inno clients got unbranded PDFs.
        Stub(Path.Combine("Assets", "brand", "sqltriage-mark.png"), "stub-png");
        // Compliance pack - present in every profile (SQLTriage.csproj Content Include on
        // docs\compliance\*.md, no Condition; buildprofile.targets removes nothing under docs\).
        // strings-r1-13: Pages\AuditLogViewer.razor sends the operator here from four audit-chain
        // banners and installer\SQLTriage.iss had no docs entry at all, so the file they were told
        // to read was never on their machine. This stub folder is why the unguarded Source line can
        // compile at all; when it was absent, iscc aborted the whole build with "No files found
        // matching ..." and exit 2 - measured 2026-08-28, and exactly the loud failure that line
        // exists to produce.
        //
        // IT IS docs.default\ FROM 2026-09-10 (round two of lane customer-update-path), and the
        // rename is the point of this stub rather than an incidental follow-on. The four documents
        // are FILLED IN by the operator - sign-off-log.md calls itself an append-only evidence record
        // auditors read - so shipping them into {app}\docs\compliance with ignoreversion replaced a
        // customer's signed compliance record on every upgrade. The installer now lays down the
        // pristine templates in {app}\docs.default\compliance and the app seeds the operator's copy
        // when it is missing. If the .iss and the csproj ever disagree about which of those two
        // folders the pack comes from, iscc aborts and this test names it.
        Stub(Path.Combine("docs.default", "compliance", "incident-response-runbook.md"), "# stub runbook");
        if (!community)
        {
            Stub(Path.Combine("BPScripts.default", "stub.sql"), "SELECT 1");
            Stub(Path.Combine("BPScripts", "operator-authored.sql"), "SELECT 'mine'");
        }

        // The non-vacuity decoy. No [Files] line names it, so it must not reach the package.
        Stub(DecoyFileName, "stub");

        return src;
    }

    private sealed record CompileResult(int ExitCode, string Output, IReadOnlyList<string> PackagedFiles);

    /// <summary>
    /// Runs iscc without /Q and reads the "Compressing: &lt;path&gt;" lines it prints, which are the
    /// compiler's own manifest of what went into the package.
    /// </summary>
    private CompileResult Compile(string iscc, string issPath, string sourceDir, string outputDir, string suffix)
    {
        Directory.CreateDirectory(outputDir);

        var psi = new ProcessStartInfo(iscc)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(issPath)!,
        };
        psi.ArgumentList.Add("/DSourceDir=" + sourceDir);
        psi.ArgumentList.Add("/DOutputDir=" + outputDir);
        psi.ArgumentList.Add("/DEditionSuffix=" + suffix);
        psi.ArgumentList.Add(issPath);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(300_000);

        var text = stdout + stderr;
        var packaged = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("Compressing:", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Substring("Compressing:".Length).Trim())
            .Where(l => l.StartsWith(sourceDir, StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Substring(sourceDir.Length).TrimStart('\\', '/'))
            .ToList();

        return new CompileResult(p.ExitCode, text, packaged);
    }

    /// <summary>
    /// installer/version.iss is generated by the csproj BuildInstaller target from
    /// Config/version.json and is gitignored, so a fresh checkout does not have it and the
    /// #include on .iss line 35 would fail. Create it if it is missing, exactly as
    /// installer/write-version-iss.ps1 does, and say whether we made it so the caller can undo it.
    /// </summary>
    private static (string Path, bool Created) EnsureVersionIss(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "installer", "version.iss");
        if (File.Exists(path)) return (path, false);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, "Config", "version.json")));
        var version = doc.RootElement.GetProperty("version").GetString();
        var build = doc.RootElement.GetProperty("buildNumber").ToString();
        File.WriteAllText(path,
            "#define AppVersion \"" + version + "\"\r\n#define BuildNumber \"" + build + "\"\r\n",
            new UTF8Encoding(false));
        return (path, true);
    }

    [LiveFact("INSTALLER_ISCC_EXE")]
    public void The_installer_compiles_in_both_profiles_and_packages_the_diagnostic_scripts()
    {
        var iscc = RequireIscc();
        var repoRoot = FrkContractTests.RepoRoot();
        var iss = Path.Combine(repoRoot, "installer", "SQLTriage.iss");
        Assert.True(File.Exists(iss), "installer/SQLTriage.iss not found at " + iss);

        var (versionIss, createdVersionIss) = EnsureVersionIss(repoRoot);
        var work = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "sqlt-iscc-probe-" + Guid.NewGuid().ToString("N"))).FullName;

        try
        {
            // ---- FULL profile ----------------------------------------------------------------
            var fullSrc = BuildStubPublishTree(work, community: false);
            var full = Compile(iscc, iss, fullSrc, Path.Combine(work, "out-full"), "-probe-full");
            Line("full compile   : exit=" + full.ExitCode + " packaged=" + full.PackagedFiles.Count);
            foreach (var f in full.PackagedFiles) Line("  packaged: " + f);

            Assert.True(full.ExitCode == 0,
                "iscc could not compile installer/SQLTriage.iss against a full publish shape (exit "
                + full.ExitCode + "). Output:\n" + full.Output);

            Assert.Contains(Path.Combine("scripts", "sp_Blitz.sql"), full.PackagedFiles,
                StringComparer.OrdinalIgnoreCase);
            Assert.Contains(Path.Combine("scripts", "sp_PerfCheck.sql"), full.PackagedFiles,
                StringComparer.OrdinalIgnoreCase);
            // 2026-09-11, lane bpscripts-operator-edits-are-lost. The stock scripts are packaged as
            // DEFAULTS and ConfigDefaultsSeeder creates {app}\BPScripts from them on first run.
            Assert.Contains(Path.Combine("BPScripts.default", "stub.sql"), full.PackagedFiles,
                StringComparer.OrdinalIgnoreCase);
            // ...and NOT over the operator's own folder, which is the whole lane. Both the CONTENT and
            // the FILENAMES in {app}\BPScripts are theirs, by two different routes: Pages\BestPractice
            // .razor:87's Save Script button only EDITS a file already there, while the FILENAMES arrive
            // by hand-dropping a .sql file and running Sync Scripts from Folder (:41). Either way, a
            // file [Files] lays down there enters the uninstall log, and CurStepChanged runs the previous
            // version's uninstaller before the new copy - so this line is what would delete their work.
            // The decoy in BuildStubPublishTree is why this assertion can fail.
            Assert.DoesNotContain(Path.Combine("BPScripts", "operator-authored.sql"), full.PackagedFiles,
                StringComparer.OrdinalIgnoreCase);
            // platform-r2-07: the brand mark must be packaged, or every generated PDF is unbranded.
            Assert.Contains(Path.Combine("Assets", "brand", "sqltriage-mark.png"), full.PackagedFiles,
                StringComparer.OrdinalIgnoreCase);
            // strings-r1-13: the incident-response runbook must be packaged, or the four audit-chain
            // banners in Pages\AuditLogViewer.razor name a file the client does not have - at the
            // one moment the product has just told them their audit chain is broken. This is the
            // MEASUREMENT behind that fix; DocPointerDeliveryTests is only the lint beside it.
            //
            // It is packaged as the DEFAULT from 2026-09-10 round two, and the app creates the
            // operator's copy from it (ConfigDefaultsSeeder). Delivery is unchanged; what changed is
            // that delivery no longer overwrites their filled-in copy.
            Assert.Contains(Path.Combine("docs.default", "compliance", "incident-response-runbook.md"),
                full.PackagedFiles, StringComparer.OrdinalIgnoreCase);
            // ...and NOT over the operator's own folder. An .iss line that put it back at
            // {app}\docs\compliance would destroy a signed sign-off log on every upgrade.
            Assert.DoesNotContain(Path.Combine("docs", "compliance", "incident-response-runbook.md"),
                full.PackagedFiles, StringComparer.OrdinalIgnoreCase);

            // NON-VACUITY. The reader is only worth anything if it can report absence.
            Assert.DoesNotContain(DecoyFileName, full.PackagedFiles, StringComparer.OrdinalIgnoreCase);
            Assert.True(full.PackagedFiles.Count >= 12,
                "the compiler manifest reader found only " + full.PackagedFiles.Count
                + " packaged files. Either iscc changed its output wording or the reader broke.");

            // ---- COMMUNITY profile -----------------------------------------------------------
            var communitySrc = BuildStubPublishTree(work, community: true);
            var community = Compile(iscc, iss, communitySrc, Path.Combine(work, "out-community"), "-probe-community");
            Line("community      : exit=" + community.ExitCode + " packaged=" + community.PackagedFiles.Count);

            Assert.True(community.ExitCode == 0,
                "iscc could not compile installer/SQLTriage.iss against a COMMUNITY publish shape, "
                + "which has no BPScripts.default folder (exit " + community.ExitCode + "). That is the "
                + "defect that left the community channel with no installer at all: "
                + "the public release script catches the non-zero exit and degrades to zip-only without "
                + "failing the release. Output:\n" + community.Output);

            Assert.Contains(Path.Combine("scripts", "sp_Blitz.sql"), community.PackagedFiles,
                StringComparer.OrdinalIgnoreCase);
            // Assets ship in the community profile too (no profile removes them).
            Assert.Contains(Path.Combine("Assets", "brand", "sqltriage-mark.png"), community.PackagedFiles,
                StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.Combine("BPScripts.default", "stub.sql"), community.PackagedFiles,
                StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(DecoyFileName, community.PackagedFiles, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            if (createdVersionIss) { try { File.Delete(versionIss); } catch (IOException) { } }
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
    }
}
