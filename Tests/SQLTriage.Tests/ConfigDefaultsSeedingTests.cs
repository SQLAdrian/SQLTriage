/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

/// <summary>
/// First-run seeding of <c>config\</c> from the shipped <c>config.default\</c>.
///
/// <para>WHAT THIS IS FOR. DECISIONS 2026-09-10 (Adrian): "the customer update path is a SHIPPED
/// SCRIPT plus a HARMLESS ZIP". Harmless means the release artefact cannot destroy operator state
/// however it is extracted - including the way it had always actually been used, which is copying the
/// zip over the install directory. The payload therefore ships operator-editable configuration as
/// DEFAULTS, and <see cref="ConfigDefaultsSeeder"/> creates the real file once, per file, when it is
/// absent. Every claim in that sentence is exercised below against a temp tree, using the production
/// method - there is no reimplementation of the copy logic here.</para>
///
/// <para>THE NEGATIVE IS PROVED BY MUTATION, not by assertion. "A second run does not overwrite" is
/// only worth something once the instrument has been shown to notice an overwrite, so
/// <see cref="An_overwrite_would_be_visible_to_these_tests"/> makes one happen deliberately and
/// confirms the same comparison catches it.</para>
/// </summary>
public class ConfigDefaultsSeedingTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root;

    public ConfigDefaultsSeedingTests(ITestOutputHelper output)
    {
        _out = output;
        _root = Path.Combine(Path.GetTempPath(), "cfgseed-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// What each toy payload PROMISES, by base directory. Recorded by <see cref="Payload"/> and handed to
    /// the seeder by <see cref="SeedFixture"/>.
    ///
    /// <para>⚠ WHY THIS EXISTS (2026-09-11, lane seeder-stub-freeze). ConfigDefaultsSeeder.Seed(string)
    /// now measures the payload against the set of shipped defaults THE REAL BUILD promised - seven files,
    /// derived by XPath over SQLTriage.csproj's XML tree - and reports every one it does not find. A toy
    /// payload carrying two files is not pretending to be that payload, so running the production overload
    /// against it would bury each test in five findings about files it never claimed to ship. Each fixture
    /// therefore states its own promise, which also makes every test in this file say out loud what it
    /// thinks the payload is supposed to contain. The production promise is pinned separately, by
    /// ShippedConfigDefaultsManifestTests.</para>
    /// </summary>
    private readonly Dictionary<string, IReadOnlyCollection<string>> _promisedByPayload =
        new(StringComparer.OrdinalIgnoreCase);

    private string Payload(params (string Relative, string Content)[] defaults)
    {
        var baseDir = Path.Combine(_root, "payload-" + Guid.NewGuid().ToString("N")[..8]);
        var defaultsDir = Path.Combine(baseDir, ConfigDefaultsSeeder.DefaultsFolderName);
        Directory.CreateDirectory(defaultsDir);
        foreach (var (relative, content) in defaults)
        {
            var path = Path.Combine(defaultsDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        // The promise is the file NAMES, because that is the grain the census compares at: the seeder
        // matches Path.GetFileName, so a default that moved into a subfolder is still accounted for.
        _promisedByPayload[baseDir] = defaults.Select(d => Path.GetFileName(d.Relative)).ToList();
        return baseDir;
    }

    /// <summary>
    /// Seeds a fixture against the promise that fixture declared. A directory this class never built
    /// through <see cref="Payload"/> promises NOTHING, which is the right reading of a bare temp folder
    /// and is what A_build_with_no_shipped_defaults_is_benign_and_says_so depends on.
    /// </summary>
    private SeedResult SeedFixture(string baseDir) =>
        ConfigDefaultsSeeder.Seed(
            baseDir,
            _promisedByPayload.TryGetValue(baseDir, out var promised) ? promised : Array.Empty<string>());

    private static string ConfigPath(string baseDir, string relative) =>
        Path.Combine(ConfigDefaultsSeeder.ResolveConfigFolder(baseDir), relative);

    [Fact]
    public void A_first_run_creates_every_shipped_default_in_the_config_folder()
    {
        var baseDir = Payload(
            ("appsettings.json", "{\"shipped\":true}"),
            ("alert-definitions.json", "{\"alerts\":[]}"),
            ("schemas/nested.json", "{\"nested\":true}"));

        var result = SeedFixture(baseDir);

        result.NoDefaultsShipped.Should().BeFalse();
        result.Failed.Should().BeEmpty();
        result.Seeded.Select(e => e.RelativePath.Replace('\\', '/'))
              .Should().BeEquivalentTo("appsettings.json", "alert-definitions.json", "schemas/nested.json");

        File.ReadAllText(ConfigPath(baseDir, "appsettings.json")).Should().Be("{\"shipped\":true}");
        File.ReadAllText(ConfigPath(baseDir, Path.Combine("schemas", "nested.json"))).Should().Be("{\"nested\":true}",
            "a default in a subfolder is seeded too, or a schema folder moving in later would be silently skipped");
        _out.WriteLine(string.Join(Environment.NewLine, result.Describe()));
    }

    [Fact]
    public void A_second_run_never_overwrites_an_edit_the_operator_made()
    {
        var baseDir = Payload(("appsettings.json", "{\"shipped\":true}"));
        SeedFixture(baseDir).Failed.Should().BeEmpty();

        // The operator edits the file the first run created. This is the whole point of the design.
        var live = ConfigPath(baseDir, "appsettings.json");
        File.WriteAllText(live, "{\"AdminAuth\":{\"Hash\":\"theirs\"}}");
        var stamp = new FileInfo(live).LastWriteTimeUtc;

        var second = SeedFixture(baseDir);

        second.Seeded.Should().BeEmpty("nothing was missing the second time");
        second.Kept.Select(e => e.RelativePath).Should().Contain("appsettings.json");
        File.ReadAllText(live).Should().Be("{\"AdminAuth\":{\"Hash\":\"theirs\"}}",
            "an upgrade must never replace configuration the operator authored");
        new FileInfo(live).LastWriteTimeUtc.Should().Be(stamp, "the file was not even touched");
    }

    [Fact]
    public void An_overwrite_would_be_visible_to_these_tests()
    {
        // Absence of evidence is evidence of absence only once the instrument is shown to display a
        // positive. Do the clobber by hand and confirm the same comparison the test above relies on
        // reports it. Without this, "the edit survived" could be true because nothing was checked.
        var baseDir = Payload(("appsettings.json", "{\"shipped\":true}"));
        SeedFixture(baseDir);
        var live = ConfigPath(baseDir, "appsettings.json");
        File.WriteAllText(live, "{\"AdminAuth\":{\"Hash\":\"theirs\"}}");

        File.Copy(Path.Combine(baseDir, ConfigDefaultsSeeder.DefaultsFolderName, "appsettings.json"), live, overwrite: true);

        File.ReadAllText(live).Should().Be("{\"shipped\":true}",
            "this is what the DEFECT looks like - if this assertion ever fails, the sibling test proves nothing");
    }

    [Fact]
    public void A_file_the_operator_deleted_comes_back_on_the_next_run()
    {
        var baseDir = Payload(("appsettings.json", "{\"shipped\":true}"), ("power-pricing.json", "{\"kwh\":0.1}"));
        SeedFixture(baseDir);

        var kept = ConfigPath(baseDir, "appsettings.json");
        File.WriteAllText(kept, "{\"AdminAuth\":{\"Hash\":\"theirs\"}}");
        File.Delete(ConfigPath(baseDir, "power-pricing.json"));

        var again = SeedFixture(baseDir);

        again.Seeded.Select(e => e.RelativePath).Should().BeEquivalentTo(
            new[] { "power-pricing.json" },
            "seeding is PER FILE - a deleted file is restored without disturbing the one beside it");
        File.ReadAllText(kept).Should().Be("{\"AdminAuth\":{\"Hash\":\"theirs\"}}");
    }

    [Fact]
    public void Files_the_operator_owns_and_we_never_ship_are_out_of_reach_by_construction()
    {
        // .seat-register-key, .sqlite-cipher-key, portal-settings.json and server-connections.json have
        // never been in the payload, so they have no shipped default and the seeder never enumerates
        // them. This pins that property rather than trusting it.
        var baseDir = Payload(("appsettings.json", "{\"shipped\":true}"));
        var configDir = ConfigDefaultsSeeder.ResolveConfigFolder(baseDir);
        Directory.CreateDirectory(configDir);
        foreach (var name in new[] { ".seat-register-key", ".sqlite-cipher-key", "portal-settings.json", "server-connections.json" })
            File.WriteAllText(Path.Combine(configDir, name), "OPERATOR SECRET " + name);

        var result = SeedFixture(baseDir);

        result.Entries.Select(e => e.RelativePath).Should().NotContain(
            new[] { ".seat-register-key", ".sqlite-cipher-key", "portal-settings.json", "server-connections.json" });
        foreach (var name in new[] { ".seat-register-key", ".sqlite-cipher-key", "portal-settings.json", "server-connections.json" })
            File.ReadAllText(Path.Combine(configDir, name)).Should().Be("OPERATOR SECRET " + name);
    }

    [Fact]
    public void An_existing_config_folder_is_reused_whatever_its_casing()
    {
        var baseDir = Payload(("appsettings.json", "{\"shipped\":true}"));
        var lower = Path.Combine(baseDir, "config");
        Directory.CreateDirectory(lower);

        var resolved = ConfigDefaultsSeeder.ResolveConfigFolder(baseDir);
        Path.GetFileName(resolved).Should().BeOneOf("config", "Config");

        SeedFixture(baseDir).Failed.Should().BeEmpty();
        Directory.GetDirectories(baseDir).Count(d =>
            string.Equals(Path.GetFileName(d), "config", StringComparison.OrdinalIgnoreCase))
            .Should().Be(1, "seeding must not create a second config folder beside the operator's");
    }

    [Fact]
    public void A_build_with_no_shipped_defaults_is_benign_and_says_so()
    {
        var baseDir = Path.Combine(_root, "no-defaults");
        Directory.CreateDirectory(baseDir);

        var result = SeedFixture(baseDir);

        result.NoDefaultsShipped.Should().BeTrue();
        result.NeedsAttention.Should().BeFalse("a tree that ships no defaults is not a fault, it is a tree with nothing to seed");
        result.Describe().Should().ContainSingle().Which.Should().Contain("nothing to seed");
    }

    [Fact]
    public void A_config_folder_that_cannot_be_written_is_reported_loudly_and_never_silently()
    {
        // The dangerous failure. Every config reader in the app falls back to a built-in default when
        // its file is absent, so a seeding failure that stayed quiet would present "running on defaults
        // you never chose" as a normal start.
        var baseDir = Payload(("appsettings.json", "{\"shipped\":true}"));

        // A FILE where the config FOLDER should be: Directory.CreateDirectory and File.Copy both fail,
        // and nothing in the tree has to be chmod-ed for it to be reproducible on any box.
        File.WriteAllText(Path.Combine(baseDir, ConfigDefaultsSeeder.ConfigFolderName), "not a folder");

        var result = SeedFixture(baseDir);

        result.Failed.Should().ContainSingle().Which.RelativePath.Should().Be("appsettings.json");
        result.NeedsAttention.Should().BeTrue();

        var text = string.Join(Environment.NewLine, result.Describe());
        _out.WriteLine(text);
        text.Should().Contain("COULD NOT CREATE");

        // ⚠ THIS ASSERTION CHANGED 2026-09-10 ROUND TWO, and the message changed with it. It used to
        // require the words "built-in defaults", because the warning said "SQLTriage will run on
        // built-in defaults for each of them". The cold gate PROVED that is false on the path that
        // matters: with config\appsettings.json absent, --server does not fall back, it throws
        // FileNotFoundException in Data\Services\WindowsServiceHost.cs before it binds a port. A
        // message that describes a graceful degradation which does not happen points a headless
        // operator away from the only thing that is wrong - the folder's permissions.
        text.Should().Contain("FOLDER PERMISSION",
            "the operator is pointed at the cause, not at a symptom");
        text.Should().Contain("HEADLESS START WILL NOT SURVIVE THIS",
            "and told what actually happens: --server exits rather than running on defaults");
        text.Should().Contain("FileNotFoundException",
            "named, so the operator can match it to what they saw in the service log");
        text.Should().Contain("Nothing already in that folder has been changed.",
            "and told what did NOT happen, so a warning does not read as data loss");
    }

    [Fact]
    public void A_failure_that_is_NOT_appsettings_states_a_different_consequence()
    {
        // The consequence must be measured, not boilerplate. --server only dies when appsettings.json
        // is the missing one; for any other file the readers really do fall back. Saying the strong
        // thing every time would be the same defect in the other direction.
        var baseDir = Payload(("dashboard-config.json", "{\"shipped\":true}"));
        File.WriteAllText(Path.Combine(baseDir, ConfigDefaultsSeeder.ConfigFolderName), "not a folder");

        var text = string.Join(Environment.NewLine, SeedFixture(baseDir).Describe());
        _out.WriteLine(text);

        text.Should().Contain("falls back to a value built into the", "this one really does degrade");
        text.Should().NotContain("HEADLESS START WILL NOT SURVIVE THIS",
            "and the harder claim is not made about a file that does not cause it");
    }

    // ── The compliance pack takes the same treatment, for the same reason ───────────────────────

    private string DocsPayload(params string[] names)
    {
        var baseDir = Path.Combine(_root, "docs-payload-" + Guid.NewGuid().ToString("N")[..8]);
        var defaultsDir = Path.Combine(baseDir, ConfigDefaultsSeeder.DocsDefaultsFolderName, "compliance");
        Directory.CreateDirectory(defaultsDir);
        foreach (var n in names) File.WriteAllText(Path.Combine(defaultsDir, n), "# shipped template\n{{placeholder}}\n");
        return baseDir;
    }

    private static string DocsPath(string baseDir, string relative) =>
        Path.Combine(ConfigDefaultsSeeder.ResolveTargetFolder(baseDir, ConfigDefaultsSeeder.DocsFolderName), relative);

    [Fact]
    public void A_first_run_creates_the_compliance_pack_from_the_shipped_templates()
    {
        // The delivery promise the 2026-08-28 fix was written for: Pages\AuditLogViewer.razor sends the
        // operator to docs/compliance/incident-response-runbook.md from four banners, so the file has to
        // exist beside the binary. Seeding is what keeps that true now the payload no longer ships the
        // pack straight into docs\.
        var baseDir = DocsPayload("incident-response-runbook.md", "sign-off-log.md");

        var result = ConfigDefaultsSeeder.SeedDocs(baseDir);
        _out.WriteLine(string.Join(Environment.NewLine, result.Describe()));

        result.Failed.Should().BeEmpty();
        result.Seeded.Should().HaveCount(2);
        File.Exists(DocsPath(baseDir, Path.Combine("compliance", "incident-response-runbook.md"))).Should().BeTrue(
            "the runbook four audit banners name must be beside the binary after a first run");
        File.Exists(DocsPath(baseDir, Path.Combine("compliance", "sign-off-log.md"))).Should().BeTrue();
    }

    [Fact]
    public void A_signed_sign_off_log_is_never_replaced_by_the_blank_template()
    {
        // THE DEFECT THIS PAIR EXISTS FOR, in one test. Round one of this lane put docs\ on the update
        // copy path; the cold gate measured four operator-authored compliance documents destroyed by a
        // hand extraction, the signature gone, the {{placeholder}} template back in their place.
        var baseDir = DocsPayload("sign-off-log.md");
        var real = DocsPath(baseDir, Path.Combine("compliance", "sign-off-log.md"));
        Directory.CreateDirectory(Path.GetDirectoryName(real)!);

        const string signed = "| 2026-08-14 | J. Patel | Q3 | 11 | 2 | evidence/ar-2026q3.csv | complete |\n";
        File.WriteAllText(real, signed);
        var stamp = File.GetLastWriteTimeUtc(real);

        ConfigDefaultsSeeder.SeedDocs(baseDir);   // first run
        ConfigDefaultsSeeder.SeedDocs(baseDir);   // and an "upgrade"

        File.ReadAllText(real).Should().Be(signed,
            "sign-off-log.md is a running evidence record, filled in append-only, that auditors read - "
            + "it says so of itself. Replacing it destroys a signed compliance artefact.");
        File.ReadAllText(real).Should().NotContain("{{placeholder}}");
        File.GetLastWriteTimeUtc(real).Should().Be(stamp, "and the file was not even rewritten with the same bytes");
        ConfigDefaultsSeeder.SeedDocs(baseDir).Kept.Should().ContainSingle().Which.RelativePath
            .Should().Be(Path.Combine("compliance", "sign-off-log.md"));
    }

    [Fact]
    public void A_deleted_compliance_document_comes_back_at_the_CURRENT_revision()
    {
        // The other half of the promise, and the reason the pristine templates still ship: an operator
        // who deleted a document is not left with a dangling banner, and what returns is the revision
        // in the build they are running rather than the one from their install date.
        var baseDir = DocsPayload("incident-response-runbook.md");
        var real = DocsPath(baseDir, Path.Combine("compliance", "incident-response-runbook.md"));

        ConfigDefaultsSeeder.SeedDocs(baseDir);
        File.Exists(real).Should().BeTrue();
        File.Delete(real);

        ConfigDefaultsSeeder.SeedDocs(baseDir).Seeded.Should().ContainSingle();
        File.Exists(real).Should().BeTrue();
    }

    [Fact]
    public void An_overwrite_of_a_compliance_document_would_be_visible_to_these_tests()
    {
        // The control. "It is never overwritten" is worth nothing until the same comparison has been
        // shown to catch an overwrite that really happened.
        var baseDir = DocsPayload("sign-off-log.md");
        var real = DocsPath(baseDir, Path.Combine("compliance", "sign-off-log.md"));
        Directory.CreateDirectory(Path.GetDirectoryName(real)!);
        File.WriteAllText(real, "signed by J. Patel");

        var template = Path.Combine(baseDir, ConfigDefaultsSeeder.DocsDefaultsFolderName, "compliance", "sign-off-log.md");
        File.Copy(template, real, overwrite: true);   // the deliberate clobber

        File.ReadAllText(real).Should().NotBe("signed by J. Patel");
        File.ReadAllText(real).Should().Contain("{{placeholder}}",
            "the check above can see a replacement, so its clean result on the real seeder means something");
    }

    [Fact]
    public void A_build_with_no_shipped_docs_defaults_is_benign()
    {
        var baseDir = Path.Combine(_root, "no-docs-defaults");
        Directory.CreateDirectory(baseDir);

        var result = ConfigDefaultsSeeder.SeedDocs(baseDir);
        result.NoDefaultsShipped.Should().BeTrue();
        result.NeedsAttention.Should().BeFalse();
    }

    [Fact]
    public void An_unwritable_docs_folder_states_the_DOCS_consequence_not_the_config_one()
    {
        var baseDir = DocsPayload("incident-response-runbook.md");
        File.WriteAllText(Path.Combine(baseDir, ConfigDefaultsSeeder.DocsFolderName), "not a folder");

        var result = ConfigDefaultsSeeder.SeedDocs(baseDir);
        result.NeedsAttention.Should().BeTrue();

        var text = string.Join(Environment.NewLine, result.Describe());
        _out.WriteLine(text);
        text.Should().Contain("four audit-chain banners", "the operator is told what stops working, in their terms");
        text.Should().NotContain("--server", "and is NOT told the service will fail to start, which is a different pass's consequence");
    }

    // ── The runtime promise: Program.Main actually calls it ────────────────────────────────────

    [Fact]
    public void The_seeder_is_still_invoked_from_Program_Main()
    {
        // THE HOLE THIS PIN EXISTS FOR, found by the cold gate 2026-09-10. Every test in this file
        // calls Seed()/SeedDocs()/SeedBPScripts() on a temp tree; not one of them launches
        // SQLTriage.exe. Delete the three lines from Program.Main and this class stays green while
        // every customer gets an unseeded install - and an unseeded config\appsettings.json makes
        // --server die with FileNotFoundException before it binds a port. The whole lane's runtime
        // behaviour rested on unpinned statements. (Three since 2026-09-11: BPScripts joined the
        // seed-once side, and the same hole would swallow it.)
        //
        // It reads the SOURCE, so it is a lint and is named as one. What makes it worth having is that
        // it fails on the exact mutation that matters and on nothing else, and that it is cheap enough
        // to run in every build: PROVED by mutation 2026-09-10 - the call was removed, this test went
        // red, the call was restored, it went green. The live counterpart is
        // The_published_binary_seeds_its_own_payload_on_startup below, which runs the real exe when a
        // payload is available.
        var program = Path.Combine(FrkContractTests.RepoRoot(), "Program.cs");
        File.Exists(program).Should().BeTrue();
        var text = File.ReadAllText(program);

        // ⚠ COMMENTS ARE STRIPPED FIRST, and that is not tidiness. The FIRST version of this pin did a
        // plain IndexOf over the raw source, and the mutation proof it exists for went GREEN: commenting
        // the call out leaves the text "ConfigDefaultsSeeder.Run()" on the line, so the pin saw a call
        // that no longer executes. A pin that cannot tell code from a comment about code is not a pin.
        // Line comments only - Program.cs has no block comments in Main, and a "//" inside a string
        // literal would be a different problem, which is why the markers below are chosen to be
        // identifiers rather than prose.
        var code = string.Join("\n", text.Split('\n').Select(l =>
        {
            var i = l.IndexOf("//", StringComparison.Ordinal);
            return i >= 0 ? l[..i] : l;
        }));

        var body = Regex.Match(code, @"public\s+static\s+void\s+Main\s*\([^)]*\)(?<body>.*)", RegexOptions.Singleline);
        body.Success.Should().BeTrue("Program.Main must still be the entry point this lane hangs off");
        var main = body.Groups["body"].Value;

        var configCall = main.IndexOf("ConfigDefaultsSeeder.Run()", StringComparison.Ordinal);
        var docsCall = main.IndexOf("ConfigDefaultsSeeder.RunDocs()", StringComparison.Ordinal);
        var bpCall = main.IndexOf("ConfigDefaultsSeeder.RunBPScripts()", StringComparison.Ordinal);

        configCall.Should().BeGreaterThan(-1,
            "Program.Main must call ConfigDefaultsSeeder.Run(). Without it the payload ships operator "
            + "configuration in config.default\\ and NOTHING ever creates config\\, so every reader in "
            + "the app runs on a built-in value and --server throws FileNotFoundException on "
            + "config/appsettings.json before it binds a port.");
        docsCall.Should().BeGreaterThan(-1,
            "and ConfigDefaultsSeeder.RunDocs(), or the compliance pack ships only as docs.default\\ "
            + "and the four audit-chain banners point at a file nothing ever creates.");
        bpCall.Should().BeGreaterThan(-1,
            "and ConfigDefaultsSeeder.RunBPScripts() (added 2026-09-11, lane "
            + "bpscripts-operator-edits-are-lost), or the Best Practice scripts ship only as "
            + "BPScripts.default\\ and NOTHING creates BPScripts\\ - so the Best Practice page is empty on "
            + "every fresh install and Pages\\ScheduledTasks.razor:147 reports \"Deployment script not "
            + "found\" instead of deploying Ola Hallengren's maintenance solution. This is the pin for the "
            + "half of the payload split that cannot be seen from the payload.");

        // AHEAD OF THE DISPATCH, not merely present. Seeding after the mode switch would leave
        // --help/--version and every early-return path unseeded, which is the case the comment in
        // Program.cs says this ordering exists for. The marker is the first dispatch STATEMENT, not the
        // comment above it - comments are stripped, and a marker that lives in prose can be reworded
        // without anything noticing.
        var dispatch = main.IndexOf("var hasMode = ModeSwitches.Any(", StringComparison.Ordinal);
        dispatch.Should().BeGreaterThan(-1, "the mode-switch scan is the first dispatch statement and the marker this ordering is measured against");
        configCall.Should().BeLessThan(dispatch, "seeding must run BEFORE any mode is chosen or short-circuits");
        docsCall.Should().BeLessThan(dispatch);
        bpCall.Should().BeLessThan(dispatch);
    }

    /// <summary>
    /// The live counterpart: the REAL binary, started for real, seeding its own payload. Runs only when
    /// SQLTRIAGE_PAYLOAD_DIR points at a publish output. Non-destructive by construction - it adds one
    /// probe file to the shipped defaults and removes it again, and the seeder never overwrites, so it
    /// cannot disturb anything already in the payload.
    /// </summary>
    [LiveFact("SQLTRIAGE_PAYLOAD_DIR")]
    public void The_published_binary_seeds_its_own_payload_on_startup()
    {
        var payload = Environment.GetEnvironmentVariable("SQLTRIAGE_PAYLOAD_DIR")!;
        Directory.Exists(payload).Should().BeTrue($"SQLTRIAGE_PAYLOAD_DIR points at {payload}");
        File.Exists(Path.Combine(payload, "SQLTriage.exe")).Should().BeTrue("the payload must carry the binary this test starts");

        // ⚠ A COPY, NEVER THE PAYLOAD ITSELF, and the first version of this test got that wrong. Seeding
        // is a WRITE: starting the binary inside the payload directory creates config\ and
        // docs\compliance\ THERE, which is exactly right for a customer whose extracted zip IS their
        // install, and exactly wrong for a folder other tests are about to assert the SHAPE of. Measured
        // 2026-09-10: this test ran first, and
        // No_operator_editable_file_sits_directly_in_the_payloads_config_folder then failed on files
        // this test had created - and the hand-extraction control counted four compliance documents
        // destroyed by a payload that had grown a docs\ folder ten seconds earlier. A probe is a write
        // (memory: a-probe-can-be-a-write); measure on a throwaway.
        var work = Path.Combine(_root, "live-payload");
        CopyTree(payload, work);

        // The extract a customer would have: the shipped defaults, and NOTHING in config\ or docs\ that
        // is theirs. Deleting the two runtime folders is what makes this a FIRST run rather than a
        // no-op - the seeder only ever creates what is missing, so a pre-populated copy would pass
        // vacuously.
        foreach (var f in new[] { ConfigDefaultsSeeder.ConfigFolderName, ConfigDefaultsSeeder.DocsFolderName })
        {
            var d = ConfigDefaultsSeeder.ResolveTargetFolder(work, f);
            if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
        }

        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(work, "SQLTriage.exe"), "--version")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = work,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(180_000).Should().BeTrue("SQLTriage.exe --version must return");
        _out.WriteLine($"exit {proc.ExitCode}{Environment.NewLine}{stdout}{stderr}");

        // What the customer must have after one start, on BOTH passes.
        var seededConfig = Path.Combine(ConfigDefaultsSeeder.ResolveConfigFolder(work), "appsettings.json");
        File.Exists(seededConfig).Should().BeTrue(
            "starting the real binary must create config\\appsettings.json from config.default\\. If this "
            + "is red while The_seeder_is_still_invoked_from_Program_Main is green, the call is present in "
            + "the source and is not reached at run time - and --server on a fresh extract throws "
            + "FileNotFoundException before it binds a port.");

        var seededDoc = Path.Combine(ConfigDefaultsSeeder.ResolveTargetFolder(work, ConfigDefaultsSeeder.DocsFolderName),
                                     "compliance", "incident-response-runbook.md");
        File.Exists(seededDoc).Should().BeTrue(
            "and docs\\compliance\\incident-response-runbook.md from docs.default\\, or the four "
            + "audit-chain banners in Pages\\AuditLogViewer.razor point at a file nothing ever creates.");
    }

    /// <summary>
    /// <b>THE END-TO-END PROOF THAT AN INCOMPLETE PAYLOAD IS NOW LOUD</b> (lane seeder-stub-freeze,
    /// 2026-09-11). Every other test of the completeness census calls the seeder in-process. This one takes
    /// a REAL payload, removes one shipped default the build promised, starts the REAL binary, and reads
    /// what a customer would have seen on stderr.
    ///
    /// <para>Before this lane that run was SILENT: ConfigDefaultsSeeder enumerates what IS in
    /// config.default\, so it could not fail to copy a file it never looked for, and nothing knew the file
    /// was supposed to be there. dashboard-config.json is the file chosen deliberately — it is the one whose
    /// consumer used to PERSIST a built-in stub over the operator's path when it was absent.</para>
    ///
    /// <para>INVOCATION: set SQLTRIAGE_PAYLOAD_DIR to a build or publish output carrying SQLTriage.exe and
    /// config.default\. It runs on a COPY, never the payload itself — seeding is a write, and a probe that
    /// writes into the folder other tests assert the shape of has already cost this repo a false finding.</para>
    /// </summary>
    [LiveFact("SQLTRIAGE_PAYLOAD_DIR")]
    public void A_payload_missing_a_promised_default_says_so_on_stderr_from_the_real_binary()
    {
        var payload = Environment.GetEnvironmentVariable("SQLTRIAGE_PAYLOAD_DIR")!;
        Directory.Exists(payload).Should().BeTrue($"SQLTRIAGE_PAYLOAD_DIR points at {payload}");

        var work = Path.Combine(_root, "incomplete-payload");
        CopyTree(payload, work);

        // The damage: one promised shipped default removed, exactly as a csproj slip or a bad zip would.
        var defaults = ConfigDefaultsSeeder.ResolveTargetFolder(work, ConfigDefaultsSeeder.DefaultsFolderName);
        var removed = Path.Combine(defaults, "dashboard-config.json");
        File.Exists(removed).Should().BeTrue(
            "the payload under test must carry the file this test removes, or it proves nothing");
        File.Delete(removed);

        // A FIRST run: the runtime folders go, so the seeder has something to do.
        foreach (var f in new[] { ConfigDefaultsSeeder.ConfigFolderName, ConfigDefaultsSeeder.DocsFolderName })
        {
            var d = ConfigDefaultsSeeder.ResolveTargetFolder(work, f);
            if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
        }

        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(work, "SQLTriage.exe"), "--version")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = work,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(180_000).Should().BeTrue("SQLTriage.exe --version must return");
        _out.WriteLine($"exit {proc.ExitCode}{Environment.NewLine}--- stdout ---{Environment.NewLine}{stdout}"
                       + $"{Environment.NewLine}--- stderr ---{Environment.NewLine}{stderr}");

        stderr.Should().Contain("NOT IN THE PAYLOAD",
            "a shipped default the build promised and the payload does not carry must reach the operator on "
            + "stderr, on every entry path. This run was completely silent before 2026-09-11.");
        stderr.Should().Contain("dashboard-config.json", "naming the file");
        stderr.Should().Contain("THIS INSTALL IS INCOMPLETE", "and the condition");
        stderr.Should().NotContain("FOLDER PERMISSION",
            "and NOT the other block's diagnosis — no ACL change can deliver a file that never shipped");

        // And the other half of the lane: the absent file is not silently created by anything that read it.
        var runtimeConfig = ConfigDefaultsSeeder.ResolveConfigFolder(work);
        File.Exists(Path.Combine(runtimeConfig, "dashboard-config.json")).Should().BeFalse(
            "nothing may create the operator's dashboard config as a side effect of a read. If this is red, "
            + "a built-in default has been persisted and ConfigDefaultsSeeder will Keep it for ever.");

        // The files that WERE in the payload still arrived — the census must not have broken seeding.
        File.Exists(Path.Combine(runtimeConfig, "appsettings.json")).Should().BeTrue(
            "the other promised defaults are still seeded; reporting a gap must not stop the pass");
    }

    /// <summary>A plain recursive copy. Directory.Move will not do: the payload and the test's temp
    /// root can be on different volumes.</summary>
    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }

    [Fact]
    public void The_seeder_records_its_last_result_for_a_host_to_log()
    {
        var baseDir = Payload(("appsettings.json", "{}"));
        var direct = SeedFixture(baseDir);
        direct.Should().NotBeNull();

        // Run() is what Program.Main calls; it seeds AppContext.BaseDirectory and publishes the result.
        var viaRun = ConfigDefaultsSeeder.Run();
        ConfigDefaultsSeeder.LastResult.Should().BeSameAs(viaRun,
            "a host that configures its logger after startup must be able to log what seeding did");

        var viaRunDocs = ConfigDefaultsSeeder.RunDocs();
        ConfigDefaultsSeeder.LastDocsResult.Should().BeSameAs(viaRunDocs,
            "and the docs pass is a separate measurement, recorded separately");
    }
}
