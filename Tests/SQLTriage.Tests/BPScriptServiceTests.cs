/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

/// <summary>
/// THE INVARIANT: no folder the application writes to at runtime may sit on the update overwrite
/// path. <c>BPScripts\</c> was the last folder in the payload that broke it, and the worst of the
/// three, because the operator does not merely EDIT files there - they NAME them. Two routes, and
/// only the second names anything: <c>Pages\BestPractice.razor:87</c>'s Save Script button EDITS the
/// content of a script already listed (<c>:216</c> passes <c>_editingScript.FileName</c>, assigned
/// only at <c>:209</c> from an existing entry - there is no filename box on that page), while
/// <c>Sync Scripts from Folder</c> at <c>:41</c> adopts any <c>*.sql</c> the operator dropped in by
/// hand. <c>Data\BPScriptService.cs:67-71</c> writes into <c>&lt;install&gt;\BPScripts</c> either way,
/// so no allow-list of filenames could ever have protected work whose names we cannot enumerate.
///
/// <para>WHAT ADRIAN RULED, 2026-09-10 (DECISIONS, widget): SEED ONCE. Keep the button. Ship the
/// stock scripts as pristine defaults, create per FILE when absent, never overwrite.</para>
///
/// <para>⚠ <b>AND WHAT HE RULED ON 2026-09-11, which these tests are NOT the ones that prove.</b> The
/// cold gate measured that seed-once alone freezes all eleven stock scripts on every install that
/// already exists, so the scripts pass now also REFRESHES a file whose bytes it can prove are one of
/// our own shipped revisions. That behaviour lives in
/// <see cref="BPScriptRefreshSeedingTests"/>; this class continues to prove the payload-shape half -
/// that nothing delivers <c>BPScripts\</c> and no update route can reach it. Both halves are needed
/// and neither implies the other.</para>
///
/// <para>THE FIX IS ON THE PAYLOAD, AND THESE TESTS ARE SHAPED BY WHY. Editing a copy list would have
/// fixed the deploy script and nothing else: the applier every shipped install actually runs is
/// <c>Data\AutoUpdateService.cs:740</c> - <c>robocopy %SRC% "&lt;appDir&gt;" /E /IS /IT ...</c>,
/// wholesale, with no <c>/XF</c> and no <c>/XD</c>. So
/// <see cref="An_operator_edit_survives_the_wholesale_robocopy_the_shipped_updater_runs"/> runs the
/// REAL robocopy with the REAL flags, read out of that source file rather than retyped here, and
/// <see cref="The_old_payload_shape_destroys_the_edit_under_the_same_harness"/> is the control that
/// shows the same harness CAN display the loss - because a control that cannot reproduce cannot
/// refute.</para>
///
/// <para>TWO CONSEQUENCES, STATED HERE RATHER THAN DISCOVERED LATER. (1) A customer whose scripts
/// were already destroyed does not get them back; nothing in this lane recovers anything. (2)
/// Seed-once means a customer who has edited a file of a stock name never receives an improved stock
/// version of it - <see cref="A_customer_who_edited_a_stock_script_never_receives_our_newer_one"/>
/// exercises exactly that, as a documented outcome rather than a regression waiting to be
/// "fixed".</para>
/// </summary>
public class BPScriptServiceTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root;

    public BPScriptServiceTests(ITestOutputHelper output)
    {
        _out = output;
        _root = Path.Combine(Path.GetTempPath(), "bpseed-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>A publish payload in the shape this lane ships: stock scripts as defaults, and NO
    /// BPScripts\ folder at all, because nothing may deliver one.</summary>
    private string Payload(params (string Name, string Content)[] stock)
    {
        var baseDir = Path.Combine(_root, "payload-" + Guid.NewGuid().ToString("N")[..8]);
        var defaults = Path.Combine(baseDir, ConfigDefaultsSeeder.BPScriptsDefaultsFolderName);
        Directory.CreateDirectory(defaults);
        foreach (var (name, content) in stock)
            File.WriteAllText(Path.Combine(defaults, name), content);
        return baseDir;
    }

    private static string Scripts(string baseDir, string name) =>
        Path.Combine(ConfigDefaultsSeeder.ResolveTargetFolder(baseDir, ConfigDefaultsSeeder.BPScriptsFolderName), name);

    private static void Author(string baseDir, string name, string content)
    {
        var path = Scripts(baseDir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // ── The premise the whole lane rests on ───────────────────────────────────────────────────

    [Fact]
    public void The_script_service_writes_any_name_into_the_install_folder_and_sync_adopts_it()
    {
        // ⚠ THIS TEST CLAIMS THE SERVICE, NOT THE BUTTON, and its old name claimed the button. The
        // Save Script button CANNOT produce the filename below: SaveScript (Pages\BestPractice.razor
        // :213-217) passes _editingScript.FileName, and _editingScript is assigned in exactly one
        // place, :209, from a script already in _config.Scripts. There is no filename box and no
        // new-script action on that page, so the button only ever EDITS THE CONTENT of a file that is
        // already there. Calling SaveScriptContent directly, as this does, exercises the service's
        // contract - which is the thing the payload split actually depends on.
        //
        // MEASURED, NOT ASSUMED. Everything above and below is built on "the folder ends up holding
        // files under names we did not choose". That premise is real, and the second half of this test
        // shows the route that makes it real: a file dropped in by hand is adopted by
        // SyncScriptsFromFolder (the Sync Scripts from Folder button, BestPractice.razor:41). The 1-arg
        // constructor is what
        // production resolves through (three bare DI registrations: Data/ServiceCollectionExtensions.cs:92,
        // Data/Services/ServerModeService.cs:466, Data/Services/WindowsServiceHost.cs:497), and it
        // takes the same _scriptsPath expression this seam does.
        var install = Path.Combine(_root, "install-" + Guid.NewGuid().ToString("N")[..8]);
        var scripts = Path.Combine(install, "BPScripts");
        var config = Path.Combine(install, "Config", "bp-scripts.json");

        var svc = new BPScriptService(NullLogger<BPScriptService>.Instance, scripts, config);
        svc.SaveScriptContent("my site standard.sql", "-- the operator's own check\r\nSELECT 1;");

        var written = Path.Combine(scripts, "my site standard.sql");
        File.Exists(written).Should().BeTrue(
            "SaveScriptContent must write into the scripts folder it was given. If this ever stops "
            + "being true, the payload split below is protecting a folder nothing writes to.");
        File.ReadAllText(written).Should().Contain("the operator's own check");

        // ...and a file under a name we never shipped is picked up as a script on the next start -
        // this is SyncScriptsFromFolder, the same call the Sync Scripts from Folder button makes, and
        // it is the route by which operator-chosen FILENAMES really do appear in that folder. It is
        // why an allow-list of shipped names could never have protected it.
        var reopened = new BPScriptService(NullLogger<BPScriptService>.Instance, scripts, config);
        reopened.GetConfig().Scripts.Should().Contain(s => s.FileName == "my site standard.sql",
            "SyncScriptsFromFolder enumerates *.sql in the folder, so the operator's own filenames become "
            + "first-class scripts. We cannot enumerate them in advance and must protect the whole folder.");
    }

    // ── Adrian's ruling: seed once, per file ──────────────────────────────────────────────────

    [Fact]
    public void A_first_run_creates_every_stock_script_beside_the_binary()
    {
        var baseDir = Payload(
            ("01. MaintenanceSolution.sql", "-- ola"),
            ("09. Do Stats.sql", "-- stats"),
            ("AddTraceflags.ps1", "# traceflags"));

        var result = ConfigDefaultsSeeder.SeedBPScripts(baseDir);

        foreach (var line in result.Describe()) _out.WriteLine(line);
        result.NoDefaultsShipped.Should().BeFalse();
        result.Seeded.Select(e => e.RelativePath).Should().BeEquivalentTo(new[]
            { "01. MaintenanceSolution.sql", "09. Do Stats.sql", "AddTraceflags.ps1" });
        result.Failed.Should().BeEmpty();

        File.ReadAllText(Scripts(baseDir, "01. MaintenanceSolution.sql")).Should().Be("-- ola");
        File.ReadAllText(Scripts(baseDir, "AddTraceflags.ps1")).Should().Be("# traceflags",
            "the folder ships .ps1 as well as .sql, and a seeder that only carried *.sql across would "
            + "leave a fresh install missing two of the eleven shipped scripts");
    }

    [Fact]
    public void Seeding_is_create_if_absent_per_FILE_not_per_folder()
    {
        // THE PIN THE RULING TURNS ON. A folder-level "does BPScripts\ exist?" check would look right
        // and be wrong: an install where the operator has saved one script would be considered seeded,
        // and would never receive a stock script added in a later release.
        var baseDir = Payload(
            ("01. MaintenanceSolution.sql", "-- ola v2"),
            ("09. Do Stats.sql", "-- stats v2"));
        Author(baseDir, "01. MaintenanceSolution.sql", "-- MY tuned copy, do not touch");

        var result = ConfigDefaultsSeeder.SeedBPScripts(baseDir);

        foreach (var line in result.Describe()) _out.WriteLine(line);
        result.Kept.Select(e => e.RelativePath).Should().BeEquivalentTo(new[] { "01. MaintenanceSolution.sql" });
        result.Seeded.Select(e => e.RelativePath).Should().BeEquivalentTo(new[] { "09. Do Stats.sql" },
            "the folder already existed, but the file that was missing must still be created - that is "
            + "what per-FILE means");

        File.ReadAllText(Scripts(baseDir, "01. MaintenanceSolution.sql")).Should().Be("-- MY tuned copy, do not touch");
        File.ReadAllText(Scripts(baseDir, "09. Do Stats.sql")).Should().Be("-- stats v2");
    }

    [Fact]
    public void A_script_the_operator_authored_is_never_touched()
    {
        // The operator's OWN file - a name we have never shipped and could not have listed. It has no
        // shipped default, so it is outside the seeder's reach by construction: it is never enumerated.
        var baseDir = Payload(("01. MaintenanceSolution.sql", "-- ola"));
        Author(baseDir, "site-standard checks.sql", "-- mine");

        var result = ConfigDefaultsSeeder.SeedBPScripts(baseDir);

        result.Entries.Should().NotContain(e => e.RelativePath.Contains("site-standard"),
            "a file with no shipped default is never enumerated, so the seeder cannot reach it at all");
        File.ReadAllText(Scripts(baseDir, "site-standard checks.sql")).Should().Be("-- mine");
    }

    [Fact]
    public void A_stock_script_the_operator_deleted_comes_back_on_the_next_start()
    {
        var baseDir = Payload(("09. Do Stats.sql", "-- stats"));
        ConfigDefaultsSeeder.SeedBPScripts(baseDir);
        File.Delete(Scripts(baseDir, "09. Do Stats.sql"));

        var second = ConfigDefaultsSeeder.SeedBPScripts(baseDir);

        second.Seeded.Select(e => e.RelativePath).Should().BeEquivalentTo(new[] { "09. Do Stats.sql" });
        File.ReadAllText(Scripts(baseDir, "09. Do Stats.sql")).Should().Be("-- stats");
    }

    [Fact]
    public void An_overwrite_would_be_visible_to_these_tests()
    {
        // The negative above ("never replaced") is worth nothing until the comparison is shown to
        // notice a replacement. This makes one happen deliberately, by the same route the seeder would
        // have taken, and confirms the same assertion catches it.
        var baseDir = Payload(("01. MaintenanceSolution.sql", "-- ours"));
        Author(baseDir, "01. MaintenanceSolution.sql", "-- theirs");

        File.Copy(Path.Combine(baseDir, ConfigDefaultsSeeder.BPScriptsDefaultsFolderName, "01. MaintenanceSolution.sql"),
                  Scripts(baseDir, "01. MaintenanceSolution.sql"), overwrite: true);

        File.ReadAllText(Scripts(baseDir, "01. MaintenanceSolution.sql")).Should().Be("-- ours",
            "the instrument must be able to SEE an overwrite, or every 'was not overwritten' assertion in "
            + "this class is a green light attached to nothing");
    }

    [Fact]
    public void A_customer_who_edited_a_stock_script_never_receives_our_newer_one()
    {
        // A STATED CONSEQUENCE, NOT A DEFECT. Seed-once cuts both ways and Adrian ruled for this trade
        // knowingly: their edit is worth more than our improvement. The current revision stays readable
        // beside it in BPScripts.default\, which is how they merge it if they want it.
        var baseDir = Payload(("09. Do Stats.sql", "-- v1 shipped"));
        ConfigDefaultsSeeder.SeedBPScripts(baseDir);
        Author(baseDir, "09. Do Stats.sql", "-- v1 shipped, plus my WHERE clause");

        // A later release improves the stock script.
        File.WriteAllText(
            Path.Combine(baseDir, ConfigDefaultsSeeder.BPScriptsDefaultsFolderName, "09. Do Stats.sql"),
            "-- v2 shipped, materially better");
        ConfigDefaultsSeeder.SeedBPScripts(baseDir);

        File.ReadAllText(Scripts(baseDir, "09. Do Stats.sql")).Should().Be("-- v1 shipped, plus my WHERE clause",
            "their edit wins - that is the ruling");
        File.ReadAllText(Path.Combine(baseDir, ConfigDefaultsSeeder.BPScriptsDefaultsFolderName, "09. Do Stats.sql"))
            .Should().Be("-- v2 shipped, materially better",
                "and the improvement is still on disk beside it, so they can merge it by hand. If this "
                + "stops being true the ruling loses its escape hatch.");
    }

    [Fact]
    public void An_existing_scripts_folder_is_reused_whatever_its_casing()
    {
        var baseDir = Payload(("09. Do Stats.sql", "-- stats"));
        var lower = Path.Combine(baseDir, "bpscripts");
        Directory.CreateDirectory(lower);
        File.WriteAllText(Path.Combine(lower, "09. Do Stats.sql"), "-- theirs");

        var result = ConfigDefaultsSeeder.SeedBPScripts(baseDir);

        result.Kept.Should().ContainSingle();
        Directory.GetDirectories(baseDir)
            .Select(Path.GetFileName)
            .Where(n => n!.Equals("BPScripts", StringComparison.OrdinalIgnoreCase))
            .Should().ContainSingle("a second folder must not be created beside the operator's");
        File.ReadAllText(Path.Combine(lower, "09. Do Stats.sql")).Should().Be("-- theirs");
    }

    [Fact]
    public void A_build_with_no_shipped_defaults_is_benign_and_says_so()
    {
        var baseDir = Path.Combine(_root, "no-defaults");
        Directory.CreateDirectory(baseDir);

        var result = ConfigDefaultsSeeder.SeedBPScripts(baseDir);

        result.NoDefaultsShipped.Should().BeTrue(
            "a community build removes BPScripts\\** entirely, so no BPScripts.default\\ ships and there is "
            + "simply nothing to seed. That must not read as a failure.");
        result.NeedsAttention.Should().BeFalse();
        Directory.Exists(Path.Combine(baseDir, "BPScripts")).Should().BeFalse(
            "and it must not create an empty folder a community install has no use for");
    }

    [Fact]
    public void An_unseedable_scripts_folder_states_the_SCRIPTS_consequence_not_another_pass()
    {
        // Each pass owns its own consequence sentence, because a generic one would be true of none of
        // them: config\ kills a headless start, docs\ dangles four audit banners, and this one leaves
        // the Best Practice page empty and the Deploy button unable to find its script.
        var baseDir = Payload(("01. MaintenanceSolution.sql", "-- ola"));
        var target = Path.Combine(baseDir, "BPScripts");

        // A FILE where the folder must go: the copy cannot succeed and cannot be mistaken for success.
        File.WriteAllText(target, "not a directory");

        var result = ConfigDefaultsSeeder.SeedBPScripts(baseDir);

        result.NeedsAttention.Should().BeTrue();
        var text = string.Join("\n", result.Describe());
        _out.WriteLine(text);
        text.Should().Contain("Best Practice page", "the operator is told what stops working, in their terms");
        text.Should().Contain("01. MaintenanceSolution.sql");
        text.Should().NotContain("--server",
            "and is NOT told the service will fail to start, which is the config pass's consequence");
        text.Should().NotContain("audit-chain banners",
            "nor the docs pass's");
    }

    // ── The route every shipped install actually runs ─────────────────────────────────────────

    /// <summary>
    /// The applier's own flags, read out of <c>Data\AutoUpdateService.cs</c> rather than retyped, so
    /// that a change to the real applier reaches this harness instead of leaving it rehearsing a shape
    /// production no longer has.
    /// </summary>
    private static string[] ShippedRobocopyFlags()
    {
        var source = File.ReadAllText(Path.Combine(FrkContractTests.RepoRoot(), "Data", "AutoUpdateService.cs"));
        var line = source.Replace("\r\n", "\n").Split('\n')
            .SingleOrDefault(l => l.Contains("robocopy %SRC%", StringComparison.Ordinal));

        line.Should().NotBeNull(
            "Data/AutoUpdateService.cs must still contain exactly one 'robocopy %SRC%' line - it is the "
            + "applier every shipped install runs, and this harness exists to rehearse it. If it moved or "
            + "split, re-derive the shape before editing this helper.");

        var flags = Regex.Matches(line!, @"\s(/[A-Z]+)\b").Select(m => m.Groups[1].Value).ToArray();
        flags.Should().Contain("/E").And.Contain("/IS",
            "/E recurses and /IS copies files that are 'the same' - together they are what makes the copy "
            + "wholesale. If they are gone the applier changed and this rehearsal is stale.");
        return flags;
    }

    private (int Exit, string Output) Robocopy(string source, string destination)
    {
        var psi = new ProcessStartInfo("robocopy")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(source);
        psi.ArgumentList.Add(destination);
        foreach (var f in ShippedRobocopyFlags()) psi.ArgumentList.Add(f);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit(60_000).Should().BeTrue("robocopy must finish inside a minute on a temp tree");

        // robocopy's exit code is a bit field; 0-7 are success, 8+ are failures.
        p.ExitCode.Should().BeLessThan(8, "robocopy failed: " + stdout);
        return (p.ExitCode, stdout);
    }

    [Fact]
    public void An_operator_edit_survives_the_wholesale_robocopy_the_shipped_updater_runs()
    {
        // THE ROUTE THAT MATTERS. Not the deploy script - the .cmd applier AutoUpdateService writes and
        // every shipped install executes: a recursive robocopy of the extracted package over the
        // install directory with no /XF and no /XD. A copy list cannot reach it; only a payload that
        // does not contain BPScripts\ can.
        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(Path.Combine(install, "BPScripts"));
        File.WriteAllText(Path.Combine(install, "BPScripts", "01. MaintenanceSolution.sql"), "-- MY tuned copy");
        File.WriteAllText(Path.Combine(install, "BPScripts", "site-standard checks.sql"), "-- entirely mine");
        File.WriteAllText(Path.Combine(install, "SQLTriage.exe"), "old binary");

        // The payload this lane ships: defaults only, no BPScripts\ anywhere in it.
        var payload = Path.Combine(_root, "payload-new");
        Directory.CreateDirectory(Path.Combine(payload, ConfigDefaultsSeeder.BPScriptsDefaultsFolderName));
        File.WriteAllText(Path.Combine(payload, ConfigDefaultsSeeder.BPScriptsDefaultsFolderName, "01. MaintenanceSolution.sql"), "-- ola v2");
        File.WriteAllText(Path.Combine(payload, ConfigDefaultsSeeder.BPScriptsDefaultsFolderName, "09. Do Stats.sql"), "-- stats v2");
        File.WriteAllText(Path.Combine(payload, "SQLTriage.exe"), "new binary");

        var (_, output) = Robocopy(payload, install);
        _out.WriteLine(output);

        File.ReadAllText(Path.Combine(install, "SQLTriage.exe")).Should().Be("new binary",
            "the update must still actually apply - a harness where nothing was copied proves nothing "
            + "about what survived");

        File.ReadAllText(Path.Combine(install, "BPScripts", "01. MaintenanceSolution.sql")).Should().Be("-- MY tuned copy",
            "the operator's edited copy of a STOCK script must survive the update");
        File.ReadAllText(Path.Combine(install, "BPScripts", "site-standard checks.sql")).Should().Be("-- entirely mine",
            "and so must a script they authored under a name we have never shipped");

        // ...and then the app starts and seeds what is genuinely missing.
        var seeded = ConfigDefaultsSeeder.SeedBPScripts(install);
        _out.WriteLine(string.Join("\n", seeded.Describe()));
        File.ReadAllText(Path.Combine(install, "BPScripts", "09. Do Stats.sql")).Should().Be("-- stats v2",
            "a script added by the new release must reach an existing install on its next start");
        File.ReadAllText(Path.Combine(install, "BPScripts", "01. MaintenanceSolution.sql")).Should().Be("-- MY tuned copy",
            "and seeding must not undo what robocopy just spared");
    }

    // ── The route this lane nearly BROKE ──────────────────────────────────────────────────────

    /// <summary>
    /// The three steps installer\SQLTriage.iss CurStepChanged performs on an upgrade, modelled here
    /// so the sequence can be exercised rather than only read.
    ///
    /// <para>⚠ WHAT THIS IS AND IS NOT. It rehearses the ALGORITHM. It does NOT run the Pascal in
    /// [Code].</para>
    ///
    /// <para>⚠⚠ <b>AN EARLIER DRAFT OF THIS COMMENT SAID "no Inno Setup is installed on this box" AND
    /// THAT WAS FALSE</b>, which is worse than a wrong sentence: it left a working test unarmed and
    /// turned a provable claim into a self-declared UNTESTED. <c>ISCC.exe</c> is present in two places
    /// — <c>C:\Users\afsul\AppData\Local\Programs\Inno Setup 6\ISCC.exe</c> and
    /// <c>C:\GitHub\Inno Setup 6\ISCC.exe</c>, the second being the very path
    /// <c>InstallerCompileLiveTests.cs:55</c> names in its own invocation note. Armed with
    /// <c>INSTALLER_ISCC_EXE</c>, that harness compiles the real installer and passes. The 2026-09-11
    /// cold gate went further and compiled three installers, proving end to end that an operator's
    /// edited script survives an upgrade WITH the two mitigation calls and is DELETED without them. So
    /// the compiled behaviour of BackupOperatorBPScripts / RestoreOperatorBPScripts is PROVED, not
    /// untested. The lesson generalises past this lane: "the tool is not installed" is a claim to
    /// MEASURE, not to assume, and a skipped live test is a question nobody asked.</para>
    ///
    /// <para>What IS pinned elsewhere:
    /// RuntimeWriteFolderCensusTests.The_best_practice_scripts_are_off_the_overwrite_path_on_every_route
    /// asserts both procedures exist in the .iss and that the backup precedes Exec(sUnInstallString)
    /// and the restore follows it. This test is the third leg: that the sequence, performed in that
    /// order with those rules, actually saves the operator's work.</para>
    /// </summary>
    private static void SimulateInnoUpgrade(string install, string backupDir, string[] uninstallLog,
                                            bool withBackup = true)
    {
        // ssInstall, step 1 - BackupOperatorBPScripts: enumerate what is ACTUALLY there. There is no
        // name list, because the operator invents the filenames - by dropping a .sql file into the
        // folder and pressing Sync Scripts from Folder, not through the Save Script button, which can
        // only edit the content of a file already listed.
        var scripts = Path.Combine(install, "BPScripts");
        if (withBackup && Directory.Exists(scripts))
        {
            Directory.CreateDirectory(backupDir);
            foreach (var f in Directory.GetFiles(scripts))
                // ⚠ overwrite:true, NOT false, and the flag is the opposite of what it looks like.
                // The Pascal this mirrors is FileCopy(Src, Dst, False) at installer\SQLTriage.iss:460,
                // and Inno's third argument is FailIfExists - so False means OVERWRITE IF PRESENT.
                // C#'s File.Copy takes the inverse sense, so faithful is overwrite:true. The old
                // overwrite:false threw where the real installer would have replaced. Not load-bearing
                // for these cases (the backup dir is fresh each time), but this is a FIDELITY harness
                // and a harness that quietly diverges from the thing it models is worth nothing.
                File.Copy(f, Path.Combine(backupDir, Path.GetFileName(f)), overwrite: true);
        }

        // step 2 - Exec(sUnInstallString): the PREVIOUS version's uninstaller. It deletes what that
        // version's [Files] laid down, which for any build before this lane includes {app}\BPScripts\*.
        foreach (var name in uninstallLog)
        {
            var victim = Path.Combine(scripts, name);
            if (File.Exists(victim)) File.Delete(victim);
        }

        // step 3 - RestoreOperatorBPScripts: put back only what did not survive. Never clobber a file
        // that is still there.
        if (Directory.Exists(backupDir))
        {
            Directory.CreateDirectory(scripts);
            foreach (var f in Directory.GetFiles(backupDir))
            {
                // FAITHFUL AS IT STANDS: SQLTriage.iss:482-484 guards the same restore with
                // "not FileExists(...)", so the FileCopy there is unreachable when the file survived
                // and its FailIfExists flag never matters. The C# guard mirrors the Pascal guard.
                var dst = Path.Combine(scripts, Path.GetFileName(f));
                if (!File.Exists(dst)) File.Copy(f, dst, overwrite: false);
            }
        }
    }

    [Fact]
    public void The_inno_upgrade_no_longer_destroys_the_operator_scripts_it_used_to_lay_down()
    {
        // THE DATA-LOSS ROUTE THIS LANE NEARLY OPENED. [Files] no longer installs {app}\BPScripts -
        // correct, because anything it lays down enters the uninstall log. But CurStepChanged runs the
        // PREVIOUS version's uninstaller first, and every install made before this commit has
        // {app}\BPScripts\* in its log. That uninstaller deletes the operator's edited stock scripts,
        // and nothing re-lays them now, because the seeder only creates what BPScripts.default\ carries.
        // Without the backup/restore this fix would have closed two routes and opened a third.
        var install = Path.Combine(_root, "inno-install");
        var scripts = Path.Combine(install, "BPScripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "09. Do Stats.sql"), "-- stock, EDITED by me");
        File.WriteAllText(Path.Combine(scripts, "site-standard checks.sql"), "-- entirely mine");

        // The old install's uninstall log: the stock names it laid down. The operator's own filename
        // is NOT in it - it was never installed - which is exactly why a name list could not fix this.
        SimulateInnoUpgrade(install, Path.Combine(_root, "inno-backup"),
            new[] { "09. Do Stats.sql", "01. MaintenanceSolution.sql" });

        File.ReadAllText(Path.Combine(scripts, "09. Do Stats.sql")).Should().Be("-- stock, EDITED by me",
            "the operator's EDITED copy of a stock script is the file the pre-upgrade uninstaller "
            + "deletes, and the one nothing would re-lay. If this goes red the Inno route destroys "
            + "their work and this lane is a net loss on it.");
        File.ReadAllText(Path.Combine(scripts, "site-standard checks.sql")).Should().Be("-- entirely mine");

        // ...and the new payload then seeds only what is genuinely absent.
        var payloadDefaults = Path.Combine(install, ConfigDefaultsSeeder.BPScriptsDefaultsFolderName);
        Directory.CreateDirectory(payloadDefaults);
        File.WriteAllText(Path.Combine(payloadDefaults, "09. Do Stats.sql"), "-- stock v2");
        File.WriteAllText(Path.Combine(payloadDefaults, "01. MaintenanceSolution.sql"), "-- ola v2");

        var seeded = ConfigDefaultsSeeder.SeedBPScripts(install);
        _out.WriteLine(string.Join("\n", seeded.Describe()));

        File.ReadAllText(Path.Combine(scripts, "09. Do Stats.sql")).Should().Be("-- stock, EDITED by me",
            "seeding must not undo what the restore just saved");
        File.ReadAllText(Path.Combine(scripts, "01. MaintenanceSolution.sql")).Should().Be("-- ola v2",
            "and the stock script the uninstaller removed, which the operator had NOT edited, comes back");
    }

    [Fact]
    public void Without_the_backup_the_inno_upgrade_destroys_them_and_the_harness_shows_it()
    {
        // A CONTROL THAT CANNOT REPRODUCE CANNOT REFUTE. The test above passes when a file is
        // unchanged, which is also what a harness whose simulated uninstaller deleted nothing would
        // report. This runs the SAME sequence with the backup step skipped and requires the loss.
        var install = Path.Combine(_root, "inno-install-control");
        var scripts = Path.Combine(install, "BPScripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "09. Do Stats.sql"), "-- stock, EDITED by me");

        // The IDENTICAL sequence with step 1 - BackupOperatorBPScripts - skipped. That is precisely
        // what this lane would have shipped had the mitigation been left out: [Files] stops laying
        // BPScripts\ down, so nothing re-creates it, while the pre-upgrade uninstaller still deletes it.
        SimulateInnoUpgrade(install, Path.Combine(_root, "inno-backup-control"),
            new[] { "09. Do Stats.sql" }, withBackup: false);

        File.Exists(Path.Combine(scripts, "09. Do Stats.sql")).Should().BeFalse(
            "without the backup the operator's edited stock script MUST be gone after the pre-upgrade "
            + "uninstall. If this goes green the simulated uninstaller is deleting nothing and the "
            + "passing test above is a green light attached to nothing.");

        // ...and the seeder cannot bring it back, which is why the backup is the only mitigation.
        var payloadDefaults = Path.Combine(install, ConfigDefaultsSeeder.BPScriptsDefaultsFolderName);
        Directory.CreateDirectory(payloadDefaults);
        File.WriteAllText(Path.Combine(payloadDefaults, "09. Do Stats.sql"), "-- stock v2");
        ConfigDefaultsSeeder.SeedBPScripts(install);

        File.ReadAllText(Path.Combine(scripts, "09. Do Stats.sql")).Should().Be("-- stock v2",
            "the seeder puts back OUR copy, never theirs - it only ever creates what BPScripts.default\\ "
            + "carries. The operator's edit is unrecoverable from the payload, which is exactly why "
            + "BackupOperatorBPScripts has to run before the uninstaller and not after it.");
    }

    [Fact]
    public void The_old_payload_shape_destroys_the_edit_under_the_same_harness()
    {
        // A CONTROL THAT CANNOT REPRODUCE CANNOT REFUTE. The test above passes when a file is
        // unchanged, and "unchanged" is also what a harness that copied nothing would report. This runs
        // the identical robocopy against the PRE-FIX payload - stock scripts shipped as BPScripts\ -
        // and requires the loss to happen. If this ever goes green, the test above has stopped
        // measuring anything.
        var install = Path.Combine(_root, "install-control");
        Directory.CreateDirectory(Path.Combine(install, "BPScripts"));
        File.WriteAllText(Path.Combine(install, "BPScripts", "01. MaintenanceSolution.sql"), "-- MY tuned copy");

        var payload = Path.Combine(_root, "payload-old");
        Directory.CreateDirectory(Path.Combine(payload, "BPScripts"));
        File.WriteAllText(Path.Combine(payload, "BPScripts", "01. MaintenanceSolution.sql"), "-- ola v2");

        var (_, output) = Robocopy(payload, install);
        _out.WriteLine(output);

        File.ReadAllText(Path.Combine(install, "BPScripts", "01. MaintenanceSolution.sql")).Should().Be("-- ola v2",
            "the pre-fix payload shape MUST destroy the operator's edit under this harness. This is the "
            + "defect the lane fixed, reproduced, so that the passing test above is known to be capable of "
            + "failing. Note what the operator loses and does not get back: this is why a customer whose "
            + "scripts were already destroyed cannot be recovered by anything in this lane.");
    }
}
