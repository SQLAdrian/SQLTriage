/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

/// <summary>
/// THE REFRESH RULE for <c>BPScripts\</c>: <b>a stock script the operator has not touched keeps
/// receiving improvements; one they HAVE touched is never overwritten.</b>
///
/// <para><b>WHY THIS CLASS EXISTS AT ALL.</b> Round one of this lane implemented Adrian's SEED ONCE
/// ruling literally, and the cold gate then measured what that does to the population that actually
/// exists: <c>C:\SQLTriage-Service\BPScripts\</c> already holds all eleven stock scripts, so
/// create-if-absent finds every one of them present and Keeps it. Seed-once alone would therefore
/// freeze <b>every existing install</b> at its installed revision — not only the installs where
/// somebody edited something — and improvements demonstrably DO reach installs today, since that same
/// folder's <c>Install-All-Scripts.sql</c> is dated 26 Aug against 10 Jul for the other ten. Adrian
/// ruled on 2026-09-11 to fix it properly before merging.</para>
///
/// <para><b>THESE TESTS USE THE REAL MANIFEST AND REAL FILE BYTES, not injected hashes.</b> Each one
/// seeds a temp tree whose <c>BPScripts\</c> holds a byte-exact copy of a REAL stock script from the
/// repo, so <see cref="ShippedBPScriptRevisions"/> classifies it for real; only the <i>default</i>
/// standing in for "a newer revision" is synthetic. There is no reimplementation of the copy logic and
/// no test-only seam in the production path — <see cref="ConfigDefaultsSeeder.SeedBPScripts"/> is the
/// method under test.</para>
///
/// <para><b>THE NEGATIVES ARE PROVED BY A POSITIVE CONTROL.</b> "An operator's edit is not
/// overwritten" is worth nothing until the same instrument has been shown to display an overwrite, so
/// <see cref="An_untouched_stock_script_of_an_older_revision_is_refreshed"/> makes a refresh happen
/// through the same assertions that
/// <see cref="A_script_the_operator_edited_is_never_refreshed"/> uses to claim one did not.</para>
/// </summary>
public class BPScriptRefreshSeedingTests : IDisposable
{
    /// <summary>A real stock script, small enough to copy freely. Its CURRENT bytes are in the
    /// manifest, which is what makes "this file is stock" true here without faking anything.</summary>
    private const string StockScript = "AddTraceflags.ps1";

    private readonly ITestOutputHelper _out;
    private readonly string _root;

    public BPScriptRefreshSeedingTests(ITestOutputHelper output)
    {
        _out = output;
        _root = Path.Combine(Path.GetTempPath(), "bpsrefresh-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static byte[] RealStockBytes() =>
        File.ReadAllBytes(Path.Combine(FrkContractTests.RepoRoot(), "BPScripts", StockScript));

    /// <summary>An install tree: <c>BPScripts.default\</c> holding the given default content, and
    /// <c>BPScripts\</c> holding whatever the operator's install already had.</summary>
    private string Tree(byte[]? shippedDefault, byte[]? alreadyInstalled, string name = StockScript)
    {
        var baseDir = Path.Combine(_root, "install-" + Guid.NewGuid().ToString("N")[..8]);

        var defaults = Path.Combine(baseDir, ConfigDefaultsSeeder.BPScriptsDefaultsFolderName);
        Directory.CreateDirectory(defaults);
        if (shippedDefault is not null) File.WriteAllBytes(Path.Combine(defaults, name), shippedDefault);

        if (alreadyInstalled is not null)
        {
            var scripts = Path.Combine(baseDir, ConfigDefaultsSeeder.BPScriptsFolderName);
            Directory.CreateDirectory(scripts);
            File.WriteAllBytes(Path.Combine(scripts, name), alreadyInstalled);
        }

        return baseDir;
    }

    private static string InstalledPath(string baseDir, string name = StockScript) =>
        Path.Combine(ConfigDefaultsSeeder.ResolveTargetFolder(baseDir, ConfigDefaultsSeeder.BPScriptsFolderName), name);

    // ── The positive: an untouched stock file of an older revision gets the improvement ────────────

    /// <summary>
    /// THE DEFECT THIS LANE'S ROUND TWO EXISTS TO FIX. The install holds byte-exact stock content, so
    /// the manifest vouches for it; the payload carries different bytes, so those bytes are a newer
    /// revision. The operator receives it. Under seed-once alone this file was Kept forever.
    ///
    /// <para>This is also the POSITIVE CONTROL for every "was not overwritten" claim below: it proves
    /// these assertions can display a refresh when one happens.</para>
    /// </summary>
    [Fact]
    public void An_untouched_stock_script_of_an_older_revision_is_refreshed()
    {
        var newer = System.Text.Encoding.UTF8.GetBytes("# a newer revision of the stock script\r\n");
        var baseDir = Tree(shippedDefault: newer, alreadyInstalled: RealStockBytes());

        var result = ConfigDefaultsSeeder.SeedBPScripts(baseDir);

        foreach (var line in result.Describe()) _out.WriteLine(line);

        result.Refreshed.Should().ContainSingle(e => e.RelativePath == StockScript,
            "the installed bytes are a revision this product shipped, so they are ours to improve");
        result.Kept.Should().BeEmpty("nothing here was the operator's");

        File.ReadAllBytes(InstalledPath(baseDir)).Should().Equal(newer,
            "the operator must actually end up with the newer revision, not merely be told they did");
    }

    // ── The negative it protects: the operator's own work ─────────────────────────────────────────

    /// <summary>
    /// The bytes on disk are not any revision we shipped, so they are the operator's and are kept
    /// whatever the payload carries. This is the trade Adrian ruled for on 2026-09-10 and round two
    /// does not revisit it.
    /// </summary>
    [Fact]
    public void A_script_the_operator_edited_is_never_refreshed()
    {
        var theirs = System.Text.Encoding.UTF8.GetBytes("# the operator's own edit, hard-won\r\n");
        var newer = System.Text.Encoding.UTF8.GetBytes("# a newer revision of the stock script\r\n");
        var baseDir = Tree(shippedDefault: newer, alreadyInstalled: theirs);

        var result = ConfigDefaultsSeeder.SeedBPScripts(baseDir);

        foreach (var line in result.Describe()) _out.WriteLine(line);

        result.Refreshed.Should().BeEmpty("nothing here was stock");
        result.Kept.Should().ContainSingle(e => e.RelativePath == StockScript && e.Reason == "modified by the operator");

        File.ReadAllBytes(InstalledPath(baseDir)).Should().Equal(theirs,
            "their work must survive byte for byte");
    }

    /// <summary>
    /// A file the payload has never heard of — the case that matters most, because
    /// <c>Sync Scripts from Folder</c> lets the operator put files there under names we do not choose.
    /// The seeder enumerates the DEFAULTS, so such a file is never even considered.
    /// </summary>
    [Fact]
    public void A_script_the_operator_named_themselves_is_not_even_considered()
    {
        var baseDir = Tree(shippedDefault: RealStockBytes(), alreadyInstalled: null);
        var scripts = Path.Combine(baseDir, ConfigDefaultsSeeder.BPScriptsFolderName);
        Directory.CreateDirectory(scripts);
        var mine = Path.Combine(scripts, "our nightly index check.sql");
        File.WriteAllText(mine, "-- ours");

        var result = ConfigDefaultsSeeder.SeedBPScripts(baseDir);

        foreach (var line in result.Describe()) _out.WriteLine(line);

        result.Entries.Should().NotContain(e => e.RelativePath.Contains("nightly index check"),
            "the seeder walks the shipped defaults, so a file we never shipped is out of its reach by "
            + "construction rather than by an exclusion somebody has to maintain");
        File.ReadAllText(mine).Should().Be("-- ours");
    }

    // ── The idle case, so a refresh is never a gratuitous write ────────────────────────────────────

    /// <summary>
    /// Stock and already current: classified, found identical, and left alone. Proved by mtime as well
    /// as by content, because a copy that rewrites identical bytes still churns the file and would make
    /// every update look like a change to anything watching the folder.
    /// </summary>
    [Fact]
    public void A_stock_script_already_at_the_current_revision_is_not_rewritten()
    {
        var real = RealStockBytes();
        var baseDir = Tree(shippedDefault: real, alreadyInstalled: real);
        var path = InstalledPath(baseDir);
        var before = File.GetLastWriteTimeUtc(path);

        var result = ConfigDefaultsSeeder.SeedBPScripts(baseDir);

        foreach (var line in result.Describe()) _out.WriteLine(line);

        result.Refreshed.Should().BeEmpty("there was nothing newer to give them");
        result.Kept.Should().ContainSingle(e => e.RelativePath == StockScript && e.Reason == "stock, already current");
        File.GetLastWriteTimeUtc(path).Should().Be(before, "an identical file must not be rewritten");
    }

    /// <summary>Unchanged from round one, restated here because the refresh must not have cost it: a
    /// stock script the operator deleted still comes back.</summary>
    [Fact]
    public void A_stock_script_the_operator_deleted_still_comes_back()
    {
        var real = RealStockBytes();
        var baseDir = Tree(shippedDefault: real, alreadyInstalled: null);

        var result = ConfigDefaultsSeeder.SeedBPScripts(baseDir);

        result.Seeded.Should().ContainSingle(e => e.RelativePath == StockScript);
        File.ReadAllBytes(InstalledPath(baseDir)).Should().Equal(real);
    }

    /// <summary>
    /// ⚠ THE COMMUNITY SHAPE, measured 2026-09-11 rather than assumed. A community build removes all
    /// eleven scripts (<c>buildprofile.targets:170-171</c>) but leaves an EMPTY
    /// <c>BPScripts.default\</c> directory behind, which is neither of the two cases the seeder's
    /// remarks describe: the folder exists, so <c>NoDefaultsShipped</c> is false, yet there is nothing
    /// in it. It must be inert - no entries, no failures, nothing said to the operator about a folder
    /// they were never meant to have. Found by this lane's own source-vs-output test going red on the
    /// community axis, which is the sort of thing a full-profile-only run never sees.
    /// </summary>
    [Fact]
    public void An_empty_defaults_folder_is_inert_which_is_the_community_build_shape()
    {
        var baseDir = Path.Combine(_root, "community-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(baseDir, ConfigDefaultsSeeder.BPScriptsDefaultsFolderName));

        var result = ConfigDefaultsSeeder.SeedBPScripts(baseDir);

        foreach (var line in result.Describe()) _out.WriteLine(line);

        result.Entries.Should().BeEmpty("there is nothing to seed, refresh or keep");
        result.Failed.Should().BeEmpty("nothing was attempted, so nothing can have failed");
        result.NeedsAttention.Should().BeFalse("the operator must not be warned about a folder a "
            + "community build is not supposed to carry");
    }

    // ── The blast radius: the other two passes must be untouched ───────────────────────────────────

    /// <summary>
    /// ⚠ THE REGRESSION GUARD FOR THE OTHER TWO PAIRS. <c>config\</c> and <c>docs\</c> pass no oracle
    /// and must stay create-if-absent exactly as they were: an operator's <c>appsettings.json</c> is
    /// never replaced even when its bytes happen to equal something we once shipped. If a later change
    /// makes the refresh unconditional, this goes red rather than a customer's configuration going
    /// quietly back to default.
    /// </summary>
    [Fact]
    public void The_config_and_docs_passes_still_never_overwrite_anything()
    {
        var baseDir = Path.Combine(_root, "other-passes-" + Guid.NewGuid().ToString("N")[..8]);

        var cfgDefaults = Path.Combine(baseDir, ConfigDefaultsSeeder.DefaultsFolderName);
        Directory.CreateDirectory(cfgDefaults);
        File.WriteAllText(Path.Combine(cfgDefaults, "appsettings.json"), "{\"shipped\":true}");

        var cfg = Path.Combine(baseDir, ConfigDefaultsSeeder.ConfigFolderName);
        Directory.CreateDirectory(cfg);
        File.WriteAllText(Path.Combine(cfg, "appsettings.json"), "{\"theirs\":true}");

        var docsDefaults = Path.Combine(baseDir, ConfigDefaultsSeeder.DocsDefaultsFolderName);
        Directory.CreateDirectory(docsDefaults);
        File.WriteAllText(Path.Combine(docsDefaults, "sign-off-log.md"), "# blank template");

        var docs = Path.Combine(baseDir, ConfigDefaultsSeeder.DocsFolderName);
        Directory.CreateDirectory(docs);
        File.WriteAllText(Path.Combine(docs, "sign-off-log.md"), "# signed by the auditor");

        // This toy payload promises exactly the one file it carries. From 2026-09-11 Seed(string) measures
        // the payload against the set of shipped defaults the REAL build promises, so the production
        // overload here would report six files this fixture never claimed to ship and say nothing useful
        // about the blast radius this test is actually guarding.
        var configResult = ConfigDefaultsSeeder.Seed(baseDir, new[] { "appsettings.json" });
        var docsResult = ConfigDefaultsSeeder.SeedDocs(baseDir);

        configResult.Refreshed.Should().BeEmpty("the config pass is given no oracle and must not refresh");
        docsResult.Refreshed.Should().BeEmpty("the docs pass is given no oracle and must not refresh");

        configResult.Kept.Should().ContainSingle(e => e.RelativePath == "appsettings.json" && e.Reason == null,
            "a pass with no oracle records a plain Kept, with no hashing and therefore no reason");
        docsResult.Kept.Should().ContainSingle(e => e.RelativePath == "sign-off-log.md" && e.Reason == null);

        File.ReadAllText(Path.Combine(cfg, "appsettings.json")).Should().Be("{\"theirs\":true}");
        File.ReadAllText(Path.Combine(docs, "sign-off-log.md")).Should().Be("# signed by the auditor");
    }

    /// <summary>
    /// The operator-facing account must match what happened. Round one's failure text said flatly
    /// "Nothing already in that folder has been changed", which a refresh makes false. Reported
    /// wrongly is the same class of defect as done wrongly, and this lane's gate found five of them.
    /// </summary>
    [Fact]
    public void The_transcript_reports_a_refresh_as_a_refresh()
    {
        var newer = System.Text.Encoding.UTF8.GetBytes("# a newer revision of the stock script\r\n");
        var baseDir = Tree(shippedDefault: newer, alreadyInstalled: RealStockBytes());

        var lines = ConfigDefaultsSeeder.SeedBPScripts(baseDir).Describe();
        foreach (var line in lines) _out.WriteLine(line);

        lines.Should().Contain(l => l.Contains("refreshed (was stock)") && l.Contains(StockScript),
            "a replaced file is a different fact from a created one and the operator is owed both");
        lines.Should().NotContain(l => l.Contains("created (was absent)") && l.Contains(StockScript),
            "it was not absent");
    }
}
