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
/// THE PAYLOAD IS THE PROMISE. DECISIONS 2026-09-10 (Adrian): "the customer update path is a SHIPPED
/// SCRIPT plus a HARMLESS ZIP, not an in-app installer." Harmless is a property of the ARTEFACT, not
/// of the procedure applied to it: an operator extracting the release over their install - which is
/// what the update path had always actually been - must not be able to destroy anything they authored.
///
/// <para>THE PROPERTY, stated so it can be checked: extracting the payload over an install overwrites
/// ONLY files nobody authored. Everything operator-editable ships as a DEFAULT in
/// <c>config.default\</c> (and, for the compliance pack, <c>docs.default\</c>; and, from 2026-09-11,
/// <c>BPScripts.default\</c>) and is seeded into <c>config\</c>, <c>docs\</c> or <c>BPScripts\</c> on
/// first run only when absent (ConfigDefaultsSeeder); everything in <c>config\</c> is a build or
/// product artefact that MUST be refreshed by every upgrade.</para>
///
/// <para>THE ONE KNOWN EXCEPTION WAS <c>BPScripts\</c> AND IT IS CLOSED - lane
/// bpscripts-operator-edits-are-lost, 2026-09-11. Until then <c>Pages\BestPractice.razor:87</c> gave
/// the operator a "Save Script" button, <c>Data\BPScriptService.cs:67-71</c> wrote it straight into
/// <c>&lt;install&gt;\BPScripts</c>, and <c>BPScripts</c> sat in the deploy script's
/// <c>$copyItems</c> and in neither <c>$preservedFolders</c> nor <c>$deliberatelyNotCopied</c> - so
/// the edit was destroyed on every update route AND the preservation transcript could not report it.
/// The pins in THIS class still assert the property only for <c>config\</c> and <c>docs\</c>, which is
/// what this lane changed. ⚠ AND THEY COULD NOT HAVE CAUGHT THE THIRD ONE:
/// <see cref="Every_top_level_payload_item_has_an_update_decision"/> asserts that a decision was
/// RECORDED, and <c>BPScripts</c> was in <c>$copyItems</c>, so it HAD one and the tripwire passed.
/// The completeness pin lives in <c>RuntimeWriteFolderCensusTests</c> instead, derived from the code
/// that WRITES rather than from either hand-kept list.</para>
///
/// <para>THE PACK WAS THE SECOND HALF OF THE SAME MISTAKE, and this class asserted it for one round.
/// The first attempt at this lane fixed <c>config\</c> and then ADDED <c>docs</c> to the update copy
/// list, reasoning that the audit banners point at a runbook that must not go stale. Measured by the
/// cold gate on a synthetic install: four filled-in compliance documents destroyed, the signed
/// sign-off log replaced by the blank template, and the deploy's own preservation transcript reporting
/// zero drift because <c>docs\</c> was outside the folders it measured. The documents are operator
/// state - <c>sign-off-log.md</c> says so of itself - so they take the seed-once treatment and the
/// pins below now assert the opposite of what they asserted on 2026-09-10 round one. A pin that
/// asserted a defect is recorded here rather than quietly flipped. The four secrets an install owns - <c>.seat-register-key</c>,
/// <c>.sqlite-cipher-key</c>, <c>portal-settings.json</c>, <c>server-connections.json</c> - are out of
/// reach because they have never been in the payload at all.</para>
///
/// <para>THREE FILES DECIDE THAT SPLIT AND THEY CAN DRIFT: SQLTriage.csproj chooses each file's
/// TargetPath, installer\SQLTriage.iss chooses each file's flag, and tools\Deploy-SQLTriageService.ps1
/// chooses what an in-place service update copies. This class is what makes them agree. The live facts
/// at the end check the built artefact itself when one is available - a tree can be right and a
/// payload still wrong.</para>
/// </summary>
public class PayloadConfigSplitTests
{
    private readonly ITestOutputHelper _out;
    public PayloadConfigSplitTests(ITestOutputHelper output) => _out = output;

    // ── The classification, with the reason attached to each entry ───────────────────────────────
    // This IS the record. If a file moves between these two dictionaries, the reason must move with it.

    /// <summary>Operator-editable. Shipped as a default; seeded first-run-only; never overwritten.</summary>
    private static readonly IReadOnlyDictionary<string, string> OperatorEditable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["appsettings.json"] = "carries the admin credential (AdminAuth Hash/Salt) and the update kill-switch; AdminAuthService.cs:212 writes it back",
        ["appsettings.Production.json"] = "environment overrides an operator sets for their own deployment",
        ["dashboard-config.json"] = "the operator's dashboard layout; DashboardConfigService.cs:401 writes it back on every save",
        ["alert-definitions.json"] = "operator alert thresholds and recipients; AlertDefinitionMigrator.cs:273,874 write it back",
        ["scheduled-tasks.json"] = "the operator's schedule",
        ["script-configurations.json"] = "per-check configuration; DiagnosticScriptRunner.cs:129 writes it back",
        ["power-pricing.json"] = "$/kWh and grid CO2; PowerEstimateService.cs:44 calls it editable per deployment and :225 reads it off disk",
        ["user-settings.json"] = "per-operator state by name and by tools/SQLTriageUpdater/Program.cs:289, which uses it as its own do-not-clobber fixture. Not in the tree today; classified so that adding one cannot ship it over",
    };

    /// <summary>Build or product artefacts. Ship into config\; refreshed by every upgrade.</summary>
    private static readonly IReadOnlyDictionary<string, string> ProductArtefacts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["version.json"] = "the build stamp. Nothing in the app writes it (whole-repo census 2026-09-10: every production reference is a read). Stale, the service misreports its own build - the 2026-08-03 defect",
        ["free-bundle.dat"] = "the Free check catalogue, regenerated with every build. First-run-only seeding would freeze a customer's catalogue at their install date",
        ["health-benchmark.json"] = "no disk reader exists: HealthBenchmarkService.cs:155 reads it out of the encrypted bundle, so an edit to the disk copy changes nothing and can lose nothing",
        ["dashboard.schema.json"] = "a JSON Schema versioned with the code it describes; whole-repo grep finds no runtime reader at all",
    };

    private const string DefaultsFolder = "config.default";
    private const string RuntimeFolder = "config";

    private static string RepoRoot() => FrkContractTests.RepoRoot();
    private static string CsprojText() => File.ReadAllText(Path.Combine(RepoRoot(), "SQLTriage.csproj"));
    private static string IssText() => File.ReadAllText(Path.Combine(RepoRoot(), "installer", "SQLTriage.iss"));
    private static string DeployScriptText() => File.ReadAllText(Path.Combine(RepoRoot(), "tools", "Deploy-SQLTriageService.ps1"));

    /// <summary>Every Content item in the csproj that declares a TargetPath, as source -> target.</summary>
    private static IReadOnlyDictionary<string, string> CsprojTargetPaths()
    {
        var doc = XDocument.Load(Path.Combine(RepoRoot(), "SQLTriage.csproj"));
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "Content"))
        {
            var source = item.Attribute("Update")?.Value ?? item.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(source)) continue;

            var target = item.Elements().FirstOrDefault(c => c.Name.LocalName == "TargetPath")?.Value
                         ?? item.Attribute("TargetPath")?.Value;
            if (string.IsNullOrWhiteSpace(target)) continue;

            map[source.Replace('\\', '/')] = target.Replace('\\', '/');
        }
        return map;
    }

    private static string FirstSegment(string path) =>
        path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? path;

    /// <summary>The deploy script's $copyItems, as a SET. Every check on that list goes through here,
    /// so no check can be defeated by an element moving to the end and losing its trailing comma.</summary>
    private static ISet<string> CopyItems(string script) => QuotedNames(script, @"\$copyItems\s*=\s*@\((?<body>.*?)\n\)");

    /// <summary>The deploy script's $deliberatelyNotCopied keys, as a SET.</summary>
    private static ISet<string> SkippedItems(string script)
    {
        var body = Regex.Match(script, @"\$deliberatelyNotCopied\s*=\s*@\{(?<body>.*?)\n\}", RegexOptions.Singleline).Groups["body"].Value;
        return new Regex(@"'(?<name>[^']+)'\s*=").Matches(body)
            .Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static ISet<string> QuotedNames(string script, string blockPattern)
    {
        var block = Regex.Match(script, blockPattern, RegexOptions.Singleline);
        block.Success.Should().BeTrue($"the deploy script must declare the list matched by {blockPattern} exactly once");

        // COMMENT LINES ARE NOT LIST ELEMENTS. $copyItems carries a long comment saying why 'docs' is no
        // longer in it, and a bare regex over the block read that explanation as an entry - so the list
        // appeared to contain the very thing the comment records removing. Caught by the PowerShell
        // harness on the first run after the change; the same trap, so the same strip.
        var code = string.Join("\n", block.Groups["body"].Value
            .Split('\n')
            .Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal)));

        return new Regex(@"'(?<name>[^']+)'").Matches(code)
            .Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_operator_editable_config_file_ships_as_a_default_and_not_over_the_operators_copy()
    {
        var targets = CsprojTargetPaths();
        var problems = new List<string>();

        foreach (var (name, why) in OperatorEditable)
        {
            var key = "Config/" + name;
            if (!targets.TryGetValue(key, out var target))
            {
                problems.Add($"{name}: no Content TargetPath in SQLTriage.csproj at all. It is operator state ({why}), "
                             + $"so without a TargetPath it lands in the payload's config folder and an extraction over an install destroys it.");
                continue;
            }
            if (!string.Equals(FirstSegment(target), DefaultsFolder, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{name}: TargetPath is '{target}', expected it under {DefaultsFolder}/. It is operator state - {why}.");
        }

        problems.Should().BeEmpty(because: "the whole ruling rests on operator-editable files shipping as DEFAULTS:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Every_product_artefact_ships_straight_into_the_config_folder_and_is_refreshed()
    {
        var targets = CsprojTargetPaths();
        var problems = new List<string>();

        foreach (var (name, why) in ProductArtefacts)
        {
            var entry = targets.FirstOrDefault(kv =>
                string.Equals(Path.GetFileName(kv.Key), name, StringComparison.OrdinalIgnoreCase));

            if (entry.Key is null)
            {
                problems.Add($"{name}: no Content TargetPath in SQLTriage.csproj. It is a product artefact ({why}), "
                             + "so it must be an explicit decision rather than whichever item happened to create the folder first.");
                continue;
            }
            if (!string.Equals(FirstSegment(entry.Value), RuntimeFolder, StringComparison.Ordinal))
                problems.Add($"{name}: TargetPath is '{entry.Value}', expected it under {RuntimeFolder}/. Seeding it first-run-only would be a NEW defect - {why}.");
        }

        problems.Should().BeEmpty(because: string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void No_shipped_config_file_escapes_the_classification()
    {
        // Exhaustiveness. A .json added to Config\ with no Content Update entry ships to the payload
        // with an undecided folder - which is exactly how power-pricing.json reached config\ by
        // accident and dashboard.schema.json reached config\schemas\ by accident.
        //
        // THIS HALF IS A TRIPWIRE, NOT THE GUARANTEE, and says so because it must. The shipped-file
        // side is structural (Directory.GetFiles below); the removed-set is a REGEX over the csproj
        // TEXT, so a reformat defeats it - attributes reordered (Remove= placed after Update=), a
        // different quote style, or a wrap inside the attribute value all read as "not removed", and
        // the file then counts as shipped when it is not. RULED 2026-09-11 (Adrian, widget; CLAUDE.md
        // generation discipline rule 2): a census counts as the GUARANTEE only when its enumerator
        // reads STRUCTURE - a syntax tree, an XML/MSBuild item tree, or a real enumerator over
        // declarations - and a census that scans CHARACTERS must say so in its own comment. PROVED
        // that formatting alone defeats a character census: 2026-09-11, two line-wrapped offenders
        // kept all five of another lane's censuses green.
        // CsprojTargetPaths() at :98 is the structural form (XDocument). Porting this removed-set onto
        // it is the fix; it is deliberately NOT in the seeder-stub-freeze lane, which owns
        // config.default\ completeness rather than this file's classification.
        var csproj = CsprojText();
        var removed = new Regex(@"<Content\s+Remove=""Config\\(?<name>[^""]+)""", RegexOptions.IgnoreCase)
            .Matches(csproj).Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var configDir = Path.Combine(RepoRoot(), "Config");
        var shipped = Directory.GetFiles(configDir, "*.json", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(configDir, p))
            .Where(rel => !removed.Contains(rel) && !removed.Any(r => r.Contains('*') && MatchesGlob(rel, r)))
            .Where(rel => !rel.StartsWith("Ignore", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var classified = OperatorEditable.Keys.Concat(ProductArtefacts.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var undecided = shipped.Where(rel => !classified.Contains(Path.GetFileName(rel))).ToList();

        _out.WriteLine($"Config\\ ships {shipped.Count} .json file(s) after Content Remove; {classified.Count} names are classified.");
        foreach (var f in shipped) _out.WriteLine("  " + f);

        undecided.Should().BeEmpty(
            "every config file that reaches the payload must be classified as operator-editable (config.default\\, "
            + "seeded first-run-only) or as a product artefact (config\\, refreshed every upgrade). Add it to "
            + "OperatorEditable or ProductArtefacts in this file WITH ITS REASON, and give it a matching "
            + "Content TargetPath in SQLTriage.csproj. Undecided: " + string.Join(", ", undecided));
    }

    private static bool MatchesGlob(string value, string glob)
    {
        var rx = "^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(value, rx, RegexOptions.IgnoreCase);
    }

    [Fact]
    public void The_installer_flags_agree_with_the_csproj_classification()
    {
        // installer\SQLTriage.iss protects the INNO upgrade path (onlyifdoesntexist + the CurStepChanged
        // backup/restore); the seeder protects the ZIP path. Two nets over two routes - but only if they
        // agree about which files are operator state.
        var iss = IssText();
        var onlyIfDoesntExist = new Regex(@"\\config(?:\.default)?\\(?<name>[A-Za-z0-9._-]+\.json)""[^\r\n]*onlyifdoesntexist", RegexOptions.IgnoreCase)
            .Matches(iss).Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ignoreVersion = new Regex(@"\\config(?:\.default)?\\(?<name>[A-Za-z0-9._-]+\.(?:json|dat))""[^\r\n]*ignoreversion", RegexOptions.IgnoreCase)
            .Matches(iss).Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

        onlyIfDoesntExist.Should().NotBeEmpty("the installer must still preserve operator config across an upgrade");

        foreach (var name in onlyIfDoesntExist)
            OperatorEditable.Should().ContainKey(name,
                $"installer\\SQLTriage.iss ships {name} onlyifdoesntexist, which means it is operator state, "
                + "but this file does not classify it as such - so the csproj may be shipping it over their copy in the zip.");

        foreach (var name in ignoreVersion)
            ProductArtefacts.Should().ContainKey(name,
                $"installer\\SQLTriage.iss ships {name} ignoreversion, which OVERWRITES it on every upgrade. "
                + "That is only correct for a product artefact. Either the flag is wrong or the classification is.");

        // And the source folder each one is drawn from matches the bucket.
        foreach (var name in onlyIfDoesntExist)
            iss.Should().MatchRegex(@"(?i)\\config\.default\\" + Regex.Escape(name),
                $"{name} is operator state, so the installer must lay down the SHIPPED DEFAULT from config.default\\");

        _out.WriteLine("onlyifdoesntexist: " + string.Join(", ", onlyIfDoesntExist.OrderBy(s => s)));
        _out.WriteLine("ignoreversion    : " + string.Join(", ", ignoreVersion.OrderBy(s => s)));
    }

    [Fact]
    public void The_service_updater_refreshes_exactly_the_product_artefacts_and_nothing_else_in_config()
    {
        // tools\Deploy-SQLTriageService.ps1 excludes the whole config folder and then copies a named list
        // of exceptions back. Those exceptions must be the product artefacts and only those, or the
        // update either leaves a stale build stamp (the 2026-08-03 defect) or eats operator state.
        var script = DeployScriptText();
        var block = Regex.Match(script, @"\$productConfigArtifacts\s*=\s*@\((?<body>.*?)\n\)", RegexOptions.Singleline);
        block.Success.Should().BeTrue("the deploy script must declare $productConfigArtifacts as one list");

        var refreshed = new Regex(@"Path\s*=\s*'config\\(?<rel>[^']+)'", RegexOptions.IgnoreCase)
            .Matches(block.Groups["body"].Value)
            .Select(m => Path.GetFileName(m.Groups["rel"].Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        refreshed.Should().BeEquivalentTo(ProductArtefacts.Keys,
            "the files the updater copies back into config\\ are exactly the product artefacts. "
            + "Anything more is operator state being overwritten; anything less ships a stale product file.");

        // Every reason is stated in the script, not just in this test.
        foreach (var m in new Regex(@"Path\s*=\s*'config\\[^']+';\s*Why\s*=\s*'(?<why>[^']+)'").Matches(block.Groups["body"].Value).Cast<Match>())
            m.Groups["why"].Value.Length.Should().BeGreaterThan(30, "each exception states why it is one, in the operator's transcript");

        // SET MEMBERSHIP, not a regex with a trailing comma. The pin here read
        // @"\$copyItems\s*=\s*@\([^)]*'config'\s*," until 2026-09-10 round two, which passes the
        // moment 'config' becomes the LAST element of the list and loses its comma - the one edit most
        // likely to reintroduce the defect while looking tidy.
        CopyItems(script).Should().NotContain("config",
            "the config folder itself must never be in the copy list, in any position");
    }

    [Fact]
    public void The_updater_ships_to_customers_under_a_customer_facing_name_and_is_the_tested_file()
    {
        // Half two of the ruling: the update path is a SHIPPED SCRIPT. Until 2026-09-10 the only correct
        // updater lived in tools\, which DefaultItemExcludes keeps out of every build, so a customer had
        // no way to get it and "extract the zip over the install" was the real procedure.
        var targets = CsprojTargetPaths();
        targets.Should().ContainKey("tools/Deploy-SQLTriageService.ps1",
            "the deploy script must ship in the payload, or the customer update path has no script in it");
        targets["tools/Deploy-SQLTriageService.ps1"].Should().Be("Install-SQLTriage.ps1",
            "it ships at the payload root under a name a customer would type");

        CsprojText().Should().MatchRegex(
            @"(?s)<Content\s+Include=""tools\\Deploy-SQLTriageService\.ps1""[^>]*CopyToPublishDirectory=""PreserveNewest""",
            "and it must be copied to PUBLISH, not only to the build output");

        // The shipped file and the tested file are the same file - not a fork.
        var harness = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "tests", "Test-DeployScript.ps1"));
        harness.Should().Contain("'Deploy-SQLTriageService.ps1'",
            "tools\\tests\\Test-DeployScript.ps1 parses and exercises the SHIPPED script; a second customer copy would be untested by construction");

        var script = DeployScriptText();
        script.Should().NotContain("ClaudeBuildFolder", "no development-box path may reach a customer as a default");
        script.Should().NotContain(@"C:\GitHub", "no development-box path may reach a customer as a default");
        script.Should().Contain(".PARAMETER WhatIfOnly", "the dry run is named in the help a customer reads");
    }

    /// <summary>
    /// The csproj copies the deploy script only when it EXISTS: a Condition added 2026-09-20 (lane
    /// <c>public-release</c>) so the curated PUBLIC tree, which strips <c>tools/</c>, still builds -
    /// <c>publish-public.ps1</c> step 4b died on MSB3030 before it. A condition that stops matching drops
    /// <c>Install-SQLTriage.ps1</c> from the payload with NO build error, and the regex in the test above
    /// cannot see it (<c>[^&gt;]*</c> spans the attribute; PROVED by mutation, cold gate 2026-09-20). So pin
    /// the two properties that make the condition safe: the path it names exists, and it is the SAME path
    /// the Include names.
    /// </summary>
    [Fact]
    public void The_conditioned_installer_copy_names_a_path_that_exists()
    {
        var doc = XDocument.Load(Path.Combine(RepoRoot(), "SQLTriage.csproj"));
        var item = doc.Descendants().Single(e => e.Name.LocalName == "Content"
            && (e.Attribute("Include")?.Value ?? string.Empty)
                .EndsWith("Deploy-SQLTriageService.ps1", StringComparison.OrdinalIgnoreCase));

        var include = item.Attribute("Include")!.Value;
        File.Exists(Path.Combine(RepoRoot(), include.Replace('\\', Path.DirectorySeparatorChar)))
            .Should().BeTrue($"the csproj copies '{include}' into the payload only when it exists, so a move "
                + "or rename would drop the installer silently");

        var condition = item.Attribute("Condition")?.Value;
        if (!string.IsNullOrWhiteSpace(condition))
        {
            condition.Should().Be($"Exists('{include}')",
                "the condition must name the SAME path as the Include, or the copy stops happening while "
                + "the build stays green");
        }
    }

    [Fact]
    public void The_docs_folder_has_an_explicit_update_decision()
    {
        // THE HOLE THIS PIN EXISTS FOR. docs\compliance shipped in the payload from 2026-08-28 - the app
        // sends operators to incident-response-runbook.md from four audit-chain banners - but 'docs' was
        // in neither the copy list nor any recorded exclusion, so a service update never refreshed it and
        // nobody had decided that. It was not a wrong decision; it was no decision.
        //
        // ⚠ THIS PIN ASSERTED THE DEFECT FOR ONE ROUND, and is deliberately flipped rather than quietly
        // corrected. On 2026-09-10 round one it read: copied.Should().Contain("docs", "the compliance
        // pack the app tells operators to read must be refreshed by an update"). Refreshing it is
        // exactly what destroys it: the four documents are FILLED IN by the operator, sign-off-log.md
        // calls itself an append-only evidence record auditors read, and the cold gate measured four of
        // them destroyed by a hand extraction over an install. The decision the hole demanded is still
        // recorded - silence is still the one unacceptable answer - but the decision is now NOT COPIED,
        // with the pristine templates delivered beside it in docs.default\ and seeded when absent.
        var script = DeployScriptText();
        var copied = CopyItems(script);
        var skipped = SkippedItems(script);

        copied.Should().NotContain("docs",
            "docs\\compliance is the operator's filled-in compliance pack - a signed evidence record "
            + "auditors read. Copying our copy over theirs destroys it, which is worse than the stale "
            + "runbook it was fixing. It is delivered as docs.default and seeded when absent instead.");
        skipped.Should().Contain("docs",
            "and 'not copied' must be RECORDED as a decision with its reason, not left silent - that "
            + "silence is the original defect this pin exists for");
        copied.Should().Contain("docs.default",
            "the pristine templates must still reach the customer, or the 2026-08-28 delivery fix is "
            + "undone: an operator who deleted a document gets nothing back and nobody can see the "
            + "current revision");
        copied.Should().Contain("config.default", "so must the shipped defaults, or a deleted config file comes back at its install-date value");
        copied.Should().Contain("Install-SQLTriage.ps1", "including the updater itself, or the next update runs the previous version's script");
        skipped.Should().Contain("config", "and the config folder must be recorded as deliberately excluded");
        copied.Should().NotContain("config");
    }

    [Fact]
    public void The_operators_compliance_pack_is_measured_by_the_preservation_report()
    {
        // The aggravating half of the round-one defect, pinned so it cannot come back. The deploy
        // overwrote docs\ AND told the operator "no file was added, removed, or altered" - because
        // $preservedFolders did not include docs, so the measurement could not see the folder the
        // script was writing to. A folder this script writes to and a folder it measures must never
        // disagree: anything not in $copyItems that an operator owns belongs in the signature.
        var script = DeployScriptText();
        var preserved = QuotedNames(script, @"\$preservedFolders\s*=\s*@\((?<body>[^)]*)(?<close>\))");

        preserved.Should().Contain("docs",
            "the compliance pack must be inside the preservation signature, or a deploy that touched it "
            + "would report zero drift - which is what round one printed while destroying four documents");
        preserved.Should().Contain("config");

        foreach (var owned in SkippedItems(script).Where(n => n is "config" or "docs"))
            preserved.Should().Contain(owned,
                $"{owned} is recorded as deliberately not copied because the operator owns it, so the "
                + "transcript must be able to prove it did not change");
    }

    // ── LIVE: the artefact itself ────────────────────────────────────────────────────────────────
    // A tree can be right and a payload still wrong. Point SQLTRIAGE_PAYLOAD_DIR at a publish output
    // (dotnet publish -c Release -r win-x64 -o <dir>) to check the thing that actually ships.

    private static string RequirePayload()
    {
        var dir = Environment.GetEnvironmentVariable("SQLTRIAGE_PAYLOAD_DIR");
        dir.Should().NotBeNullOrWhiteSpace("the LiveFact attribute must not be weakened to a vacuous pass");
        Directory.Exists(dir).Should().BeTrue($"SQLTRIAGE_PAYLOAD_DIR points at {dir}, which does not exist");
        return dir!;
    }

    [LiveFact("SQLTRIAGE_PAYLOAD_DIR")]
    public void The_payload_carries_no_customer_owned_artefact()
    {
        var payload = RequirePayload();
        var offenders = new List<string>();

        foreach (var file in Directory.GetFiles(payload, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(payload, file);
            var name = Path.GetFileName(file);
            var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (name.EndsWith(".db", StringComparison.OrdinalIgnoreCase)) offenders.Add(rel + " (a database)");
            if (name.StartsWith('.')) offenders.Add(rel + " (a dotfile - the install's keys are dotfiles)");
            if (name.Contains("seat-register", StringComparison.OrdinalIgnoreCase)
                || name.Contains("cipher-key", StringComparison.OrdinalIgnoreCase)
                || name.Equals("portal-settings.json", StringComparison.OrdinalIgnoreCase)
                || name.Equals("server-connections.json", StringComparison.OrdinalIgnoreCase))
                offenders.Add(rel + " (install-owned secret or connection state)");

            foreach (var forbidden in new[] { "audit-logs", "Data", "output", "logs" })
                if (segments.Length > 1 && string.Equals(segments[0], forbidden, StringComparison.OrdinalIgnoreCase))
                    offenders.Add(rel + $" (under {forbidden}\\, which is install-owned)");
        }

        _out.WriteLine($"payload: {payload}");
        foreach (var d in Directory.GetFileSystemEntries(payload).OrderBy(x => x)) _out.WriteLine("  " + Path.GetFileName(d));

        offenders.Should().BeEmpty("the release zip must contain nothing a customer owns: " + string.Join("; ", offenders));
    }

    [LiveFact("SQLTRIAGE_PAYLOAD_DIR")]
    public void No_operator_editable_file_sits_directly_in_the_payloads_config_folder()
    {
        var payload = RequirePayload();
        var configDir = Directory.GetDirectories(payload)
            .FirstOrDefault(d => string.Equals(Path.GetFileName(d), RuntimeFolder, StringComparison.OrdinalIgnoreCase));
        configDir.Should().NotBeNull("the payload must still carry a config folder for the product artefacts");

        var inConfig = Directory.GetFiles(configDir!, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName).ToList();
        var leaked = inConfig.Where(n => OperatorEditable.ContainsKey(n!)).ToList();

        _out.WriteLine("config\\ holds: " + string.Join(", ", inConfig.OrderBy(x => x)));
        leaked.Should().BeEmpty(
            "these are operator-editable and are in the folder an extraction writes over. That is the exact "
            + "defect the ruling of 2026-09-10 closes: " + string.Join(", ", leaked));

        var defaultsDir = Path.Combine(payload, DefaultsFolder);
        Directory.Exists(defaultsDir).Should().BeTrue("the payload must carry the shipped defaults for the seeder to work from");
        var defaults = Directory.GetFiles(defaultsDir, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList();
        _out.WriteLine("config.default\\ holds: " + string.Join(", ", defaults.OrderBy(x => x)));
        defaults.Should().NotBeEmpty();
        foreach (var d in defaults) OperatorEditable.Should().ContainKey(d!,
            $"{d} ships as a default, so it must be classified as operator-editable here with its reason");
    }

    [LiveFact("SQLTRIAGE_PAYLOAD_DIR")]
    public void The_compliance_pack_ships_as_a_default_and_never_over_the_operators_copy()
    {
        // The artefact itself, because a tree can be right and a payload still wrong - and here the
        // MSBuild change is a TargetPath on a wildcard Include, which is exactly the kind of rule that
        // can evaluate to nothing without failing a build.
        var payload = RequirePayload();

        var overTheirs = Path.Combine(payload, "docs", "compliance");
        Directory.Exists(overTheirs).Should().BeFalse(
            "the payload must not carry docs\\compliance at all: that path is where the OPERATOR's "
            + "filled-in pack lives on an install, and a hand extraction over it destroys their signed "
            + "sign-off log. Measured 2026-09-10: four documents lost.");

        var asDefaults = Path.Combine(payload, "docs.default", "compliance");
        Directory.Exists(asDefaults).Should().BeTrue(
            "the pristine templates must ship, or the seeder has nothing to seed from and the four "
            + "audit banners point at a file that is never created");

        var shipped = Directory.GetFiles(asDefaults, "*.md").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        _out.WriteLine("docs.default\\compliance\\ holds: " + string.Join(", ", shipped));
        shipped.Should().BeEquivalentTo(new[]
        {
            "access-review-procedure.md",
            "incident-response-runbook.md",
            "sign-off-log.md",
            "vendor-dependency-register.md",
        }, "the wildcard TargetPath must carry the WHOLE pack across, not the first file it matched");
    }

    [LiveFact("SQLTRIAGE_PAYLOAD_DIR")]
    public void Every_top_level_payload_item_has_an_update_decision()
    {
        // The allow-list pin. Adding a shipped top-level folder without deciding whether an update
        // refreshes it FAILS here - which is what would have caught docs\ in August.
        var payload = RequirePayload();
        var script = DeployScriptText();

        var copied = CopyItems(script);
        var skipped = SkippedItems(script);

        var undecided = Directory.GetFileSystemEntries(payload)
            .Select(Path.GetFileName)
            .Where(n => !copied.Contains(n!) && !skipped.Contains(n!))
            .OrderBy(n => n)
            .ToList();

        _out.WriteLine("copied : " + string.Join(", ", copied.OrderBy(x => x)));
        _out.WriteLine("skipped: " + string.Join(", ", skipped.OrderBy(x => x)));

        undecided.Should().BeEmpty(
            "every top-level item in the payload must be named in $copyItems or in $deliberatelyNotCopied in "
            + "tools\\Deploy-SQLTriageService.ps1. Silence is how docs\\ shipped for two months with no update "
            + "rule. Undecided: " + string.Join(", ", undecided));
    }
}
