/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using FluentAssertions;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

/// <summary>
/// THE INVARIANT: <b>if the shipped payload is incomplete, somebody is TOLD.</b> Every file the payload
/// is supposed to carry in <c>config.default\</c> is accounted for, and one that is absent produces an
/// attention entry rather than silence.
///
/// <para>WHY THIS CLASS EXISTS (measured 2026-09-11, lane seeder-stub-freeze). Nothing in the product
/// knew what the payload was supposed to contain. <see cref="ConfigDefaultsSeeder"/> enumerates what IS
/// in the defaults folder, so <b>it could not fail to copy a file it never looked for</b> — a shipped
/// default missing from the payload produced no entry, no warning and nothing on stderr. The trigger is
/// not hypothetical: a <c>Content Update</c> whose source file is not in the tree matches nothing and
/// contributes nothing, with no build warning, and <c>Config\user-settings.json</c> is in exactly that
/// state on this commit.</para>
///
/// <para><b>BOTH SIDES OF EVERY COMPARISON HERE ARE DERIVED, AND BOTH ARE READ AS STRUCTURE.</b> The
/// expected side is <see cref="ShippedConfigDefaults"/>, generated at build time by an XPath 1.0 query
/// over <c>SQLTriage.csproj</c> as an XML tree; this class re-derives it INDEPENDENTLY with
/// <see cref="XDocument"/> and compares, so a generator that did not re-run is caught. The actual side is
/// <see cref="ShippedConfig.EnumerateShippedDefaults"/>, read off the build output beside this assembly.
/// Neither side is a list anybody keeps by hand — a parity test between two hand-kept lists validates
/// AGREEMENT, not COMPLETENESS, and on 2026-09-10 two such lists were held identical by a good test while
/// both were missing <c>power-pricing.json</c>.</para>
///
/// <para><b>⚠ WHAT THIS CLASS DOES NOT CATCH, stated so nobody reads it as wider than it is.</b> It
/// measures the <c>config.default\</c> folder against the csproj's <c>config.default/</c> TargetPaths, in
/// BOTH directions. It says nothing about a config file that ships to <c>config\</c> with no TargetPath at
/// all — the 2026-09-10 <c>power-pricing.json</c> shape — because nothing promised that file. That
/// direction belongs to <c>PayloadConfigSplitTests.No_shipped_config_file_escapes_the_classification</c>,
/// which is a REGEX over the csproj text and is therefore a tripwire rather than a guarantee.</para>
/// </summary>
public sealed class ShippedConfigDefaultsManifestTests
{
    private readonly ITestOutputHelper _out;
    public ShippedConfigDefaultsManifestTests(ITestOutputHelper output) => _out = output;

    private static string RepoRoot() => FrkContractTests.RepoRoot();

    /// <summary>
    /// The csproj's <c>config.default/</c> promises, re-derived here from the XML TREE — deliberately a
    /// second implementation rather than a call into the product, so that a generator which silently did
    /// not re-run, or an XPath that stopped matching, shows up as a DISAGREEMENT instead of two views of
    /// the same stale answer.
    /// </summary>
    private static (List<string> Promised, List<(string Path, string Reason)> DeclaredAbsent) CsprojPromises()
    {
        var doc = XDocument.Load(Path.Combine(RepoRoot(), "SQLTriage.csproj"));
        var promised = new List<string>();
        var absent = new List<(string, string)>();

        foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "Content"))
        {
            var target = item.Elements().FirstOrDefault(c => c.Name.LocalName == "TargetPath")?.Value;
            if (string.IsNullOrWhiteSpace(target)) continue;

            var normalised = target.Trim().Replace('\\', '/');
            if (!normalised.StartsWith(ShippedConfigDefaults.TargetPathPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var reason = item.Elements().FirstOrDefault(c => c.Name.LocalName == "PayloadDefaultAbsentReason")?.Value;
            if (reason is null) promised.Add(normalised);
            else absent.Add((normalised, reason));
        }
        return (promised, absent);
    }

    /// <summary>
    /// ABSENT OR EMPTY IS A FAILURE, NEVER A SKIP. A completeness census whose expected set is empty
    /// passes everything, which is the fail-toward-clean direction that let a 174 MB full build through an
    /// IP boundary as community with exit 0 on 2026-09-11. The build errors before generating an empty
    /// promise; this is the runtime half of that guard, and it fails rather than shrugging.
    /// </summary>
    [Fact]
    public void The_builds_promise_is_derivable_and_is_not_empty()
    {
        ShippedConfigDefaults.IsDerivable.Should().BeTrue(
            "the generated half of ShippedConfigDefaults came through empty. That is a BROKEN GUARD, not "
            + "an empty promise: GenerateShippedConfigDefaultsManifest in SQLTriage.csproj is supposed to "
            + "error before it can produce this, so the target did not run or its XPath stopped matching.");

        _out.WriteLine("promised: " + string.Join(", ", ShippedConfigDefaults.PromisedTargetPaths));
        _out.WriteLine("declared absent: " + string.Join(", ", ShippedConfigDefaults.DeclaredAbsentTargetPaths));
    }

    [Fact]
    public void The_generated_promise_is_the_csproj_read_as_an_xml_tree()
    {
        var (promised, absent) = CsprojPromises();

        ShippedConfigDefaults.PromisedTargetPaths.Should().BeEquivalentTo(promised,
            "the generated promise must equal what the csproj's XML tree says right now. A disagreement "
            + "means GenerateShippedConfigDefaultsManifest did not re-run for this build, or its XPath no "
            + "longer selects what this test selects — and a stale promise is worse than none, because it "
            + "reports confidently about a payload it is not describing.");

        ShippedConfigDefaults.DeclaredAbsentTargetPaths.Should().BeEquivalentTo(absent.Select(a => a.Item1));
    }

    /// <summary>
    /// The partition. A new <c>config.default/</c> entry is in the promise or in the declared-absent set,
    /// and there is no third place for it to be — so nobody can add a shipped default that this census is
    /// simply blind to.
    /// </summary>
    [Fact]
    public void No_config_default_target_path_escapes_the_promise_or_the_declaration()
    {
        var (promised, absent) = CsprojPromises();
        var all = promised.Concat(absent.Select(a => a.Item1)).ToList();

        all.Should().NotBeEmpty("the csproj must declare at least one config.default entry");
        all.Should().OnlyHaveUniqueItems("one TargetPath, one decision");

        var accounted = ShippedConfigDefaults.PromisedTargetPaths
            .Concat(ShippedConfigDefaults.DeclaredAbsentTargetPaths)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        all.Where(p => !accounted.Contains(p)).Should().BeEmpty(
            "every config.default TargetPath in the csproj must reach the runtime as either a promise or a "
            + "declared absence. One in neither is a shipped default nothing accounts for.");
    }

    /// <summary>
    /// <b>THE PIN THAT WOULD HAVE CAUGHT IT.</b> Measured against the build output beside this assembly,
    /// which is the artefact the payload is assembled from. If a source file is deleted, renamed, or moved
    /// out from under a <c>Content Update</c> — all silent in MSBuild — this goes red naming the file.
    /// </summary>
    [Fact]
    public void Every_promised_default_is_in_the_payload_beside_this_assembly()
    {
        var shipped = ShippedConfig.EnumerateShippedDefaults();
        shipped.Should().NotBeEmpty(
            "this build placed no " + ShippedConfig.DefaultsFolderName + "\\ folder beside the test "
            + "assembly. ABSENT IS A FAILURE, never a skip: a census with nothing to measure cannot pass.");

        _out.WriteLine($"{ShippedConfig.DefaultsFolderName}\\ carries: " + string.Join(", ", shipped));

        var present = shipped.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = ShippedConfigDefaults.PromisedFileNames.Where(n => !present.Contains(n)).ToList();

        missing.Should().BeEmpty(
            "SQLTriage.csproj promises these files in " + ShippedConfig.DefaultsFolderName
            + "\\ and this build did not produce them: " + string.Join(", ", missing)
            + ". A Content Update whose source file is not in the tree matches nothing and warns about "
            + "nothing, so the build is the only place this can be seen. Either restore the source file, or "
            + "— if the absence is deliberate — give the csproj item a PayloadDefaultAbsentReason saying "
            + "why, which is reviewable and is reported in the seeding transcript.");
    }

    /// <summary>
    /// The other direction, which makes the census a SET EQUALITY rather than a one-way containment. A file
    /// that arrives in <c>config.default\</c> without a TargetPath naming it — by an automatic SDK glob,
    /// say, which is how <c>power-pricing.json</c> reached <c>config\</c> by accident on 2026-09-10 — is a
    /// shipped default nobody decided to ship.
    /// </summary>
    [Fact]
    public void Every_file_in_the_payloads_defaults_folder_is_promised_by_the_build()
    {
        var shipped = ShippedConfig.EnumerateShippedDefaults();
        shipped.Should().NotBeEmpty("there must be a defaults folder to measure");

        var accounted = ShippedConfigDefaults.PromisedFileNames
            .Concat(ShippedConfigDefaults.DeclaredAbsentFileNames)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unexpected = shipped.Where(n => !accounted.Contains(n)).ToList();

        unexpected.Should().BeEmpty(
            "these files are in " + ShippedConfig.DefaultsFolderName + "\\ and no csproj TargetPath puts "
            + "them there: " + string.Join(", ", unexpected) + ". A shipped default that arrived by an "
            + "automatic glob rather than a decision is seeded into the operator's config\\ folder on "
            + "first run, which is a decision about their data that nobody made.");
    }

    /// <summary>
    /// An exemption is a DECLARED, REVIEWABLE escape hatch, so it must carry substance and must describe a
    /// real state of the tree. The day the source file appears, the declaration is stale and this goes red
    /// — which is what stops the exemption outliving its reason and silencing a genuine loss later.
    /// </summary>
    [Fact]
    public void Every_declared_absent_default_has_a_reason_and_is_genuinely_absent_from_the_tree()
    {
        var (_, absent) = CsprojPromises();

        foreach (var (target, reason) in absent)
        {
            _out.WriteLine($"{target}: {reason}");

            reason.Trim().Length.Should().BeGreaterThan(40,
                $"{target} declares its absence deliberate and must say WHY in its own words. A bare "
                + "PayloadDefaultAbsentReason is a silencer, not a decision.");

            var source = Path.Combine(RepoRoot(), "Config", ShippedConfigDefaults.NameOf(target));
            File.Exists(source).Should().BeFalse(
                $"{source} exists, so the declared absence at {target} is STALE: the file is in the tree "
                + "and will ship. Remove the PayloadDefaultAbsentReason element so the promise census "
                + "covers it, or this entry permanently hides a real loss of that file.");
        }
    }

    // ── The runtime half: the seeder actually reports it ─────────────────────────────────────────

    private static string TempTree(out string defaultsDir)
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "sqlt-promise-" + Guid.NewGuid().ToString("N")[..10]);
        defaultsDir = Path.Combine(baseDir, ConfigDefaultsSeeder.DefaultsFolderName);
        Directory.CreateDirectory(defaultsDir);
        return baseDir;
    }

    /// <summary>
    /// <b>THE WIRING PIN: what RUNS this?</b> The public, production entry point — the one
    /// <c>Program.Main</c> calls — is handed a payload missing ONE of the files the REAL build promises,
    /// and must name it. A guard whose expected set arrives only through a test-only overload is not
    /// wired; this is the test that goes red if <see cref="ConfigDefaultsSeeder.Seed(string)"/> stops
    /// passing <see cref="ShippedConfigDefaults"/>.
    /// </summary>
    [Fact]
    public void The_production_seed_entry_point_carries_the_builds_own_promise()
    {
        ShippedConfigDefaults.PromisedFileNames.Count.Should().BeGreaterThan(1,
            "this test omits one promised file and needs at least one other to stay present");

        var omitted = ShippedConfigDefaults.PromisedFileNames
            .First(n => n.Equals("dashboard-config.json", StringComparison.OrdinalIgnoreCase));

        var baseDir = TempTree(out var defaultsDir);
        try
        {
            foreach (var name in ShippedConfigDefaults.PromisedFileNames.Where(n => n != omitted))
                File.WriteAllText(Path.Combine(defaultsDir, name), "{}");

            // No promise argument: this is the overload production uses.
            var result = ConfigDefaultsSeeder.Seed(baseDir);
            foreach (var line in result.Describe()) _out.WriteLine(line);

            result.MissingFromPayload.Select(e => e.RelativePath).Should().BeEquivalentTo(new[] { omitted },
                "the production overload must measure the payload against the promise this build generated "
                + "from its own csproj, and report exactly the file that is not there");
            result.NeedsAttention.Should().BeTrue("an incomplete install is not a cosmetic failure");
            result.Failed.Should().BeEmpty("nothing was attempted, so nothing can have FAILED — the "
                + "remedies differ and so must the two classes");
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The loudest shape: the whole defaults folder is gone. Until this lane the seeder returned an empty
    /// result and <c>Describe()</c> said "nothing to seed", which is the friendliest possible way to hide a
    /// broken payload. A tree that promises NOTHING still gets exactly that answer, and the test below this
    /// one in <c>ConfigDefaultsSeedingTests</c> holds it.
    /// </summary>
    [Fact]
    public void A_payload_that_promises_defaults_and_ships_no_defaults_folder_is_loud()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "sqlt-nofolder-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(baseDir);
        try
        {
            var result = ConfigDefaultsSeeder.Seed(baseDir);
            var text = string.Join(Environment.NewLine, result.Describe());
            _out.WriteLine(text);

            result.NoDefaultsShipped.Should().BeTrue("there is genuinely no defaults folder");
            result.NeedsAttention.Should().BeTrue(
                "but this build promised " + ShippedConfigDefaults.PromisedFileNames.Count
                + " files in it, so 'nothing to seed' is not the whole truth");
            result.MissingFromPayload.Select(e => e.RelativePath)
                  .Should().BeEquivalentTo(ShippedConfigDefaults.PromisedFileNames);
            text.Should().Contain("THE WHOLE SHIPPED-DEFAULTS FOLDER IS ABSENT");
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// A warning that misdescribes what happened is worse than no warning. The <c>Failed</c> block tells
    /// the operator "This is a FOLDER PERMISSION problem, not a corrupt install" — which is exactly wrong
    /// here, and is why MissingFromPayload is a separate action rather than a reuse of Failed.
    /// </summary>
    [Fact]
    public void The_incomplete_payload_transcript_names_the_right_cause_and_not_permissions()
    {
        var baseDir = TempTree(out var defaultsDir);
        try
        {
            foreach (var name in ShippedConfigDefaults.PromisedFileNames
                                                      .Where(n => n != "dashboard-config.json"))
                File.WriteAllText(Path.Combine(defaultsDir, name), "{}");

            var text = string.Join(Environment.NewLine, ConfigDefaultsSeeder.Seed(baseDir).Describe());
            _out.WriteLine(text);

            text.Should().Contain("NOT IN THE PAYLOAD", "the entry names the file");
            text.Should().Contain("THIS INSTALL IS INCOMPLETE", "and the transcript names the condition");
            text.Should().Contain("NOT A PERMISSION PROBLEM",
                "and rules out the cause the other block would have sent them to");
            text.Should().Contain("re-extract the release archive", "and states the actual remedy");
            text.Should().NotContain("FOLDER PERMISSION",
                "the Failed block's diagnosis must not appear for a file that was never delivered — an ACL "
                + "change cannot fix an undelivered file and the operator must not be sent to try");
            text.Should().Contain("Nothing you authored has been changed",
                "so a warning does not read as data loss");
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// A declared absence is ACCOUNTED FOR, which is the point: it appears in the record and does not cry
    /// wolf. The declaration is what makes the difference, and the test above keeps the declaration honest.
    /// </summary>
    [Fact]
    public void A_declared_absent_default_is_accounted_for_and_is_not_an_attention_entry()
    {
        var baseDir = TempTree(out var defaultsDir);
        try
        {
            File.WriteAllText(Path.Combine(defaultsDir, "appsettings.json"), "{}");

            var result = ConfigDefaultsSeeder.Seed(baseDir,
                promisedFileNames: new[] { "appsettings.json" },
                declaredAbsentFileNames: new[] { "user-settings.json" });

            var text = string.Join(Environment.NewLine, result.Describe());
            _out.WriteLine(text);

            result.MissingFromPayload.Should().BeEmpty("the only promised file is there");
            result.NeedsAttention.Should().BeFalse(
                "a declared absence is a decision somebody made on purpose, not a fault");
            result.DeclaredAbsentDefaults.Should().BeEquivalentTo(new[] { "user-settings.json" });
            text.Should().Contain("declared absent by the build, not a fault: user-settings.json",
                "accounting has to be visible in the record, not only in the object");
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { }
        }
    }
}
