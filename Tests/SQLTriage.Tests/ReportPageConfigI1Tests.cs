/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

/// <summary>
/// THE INVARIANT: <b>(I1) a customer's configuration must never be silently replaced by a built-in
/// default.</b> <c>report-pages.json</c> holds the operator's per-page report layout.
///
/// <para><b>WHAT WAS WRONG (lane I1-census, 2026-09-12).</b> <c>ReportPageConfigService.Load</c> ended
/// with an unconditional <c>SaveRoot(BuildDefaults())</c> that THREE arms reached: the file was absent;
/// <c>Deserialize</c> returned null; and the <c>catch</c> — so any read failure at all, a transient IO
/// error or a half-written file included, persisted this build's layout over the operator's. The write
/// was a bare <c>Directory.CreateDirectory</c> + <c>File.WriteAllText</c>: no backup and no
/// <c>.rejected-</c> copy, so the damaged file was not replaced, it was gone. Strictly worse than
/// <c>DashboardConfigService</c>, which copies aside first.</para>
///
/// <para><b>These tests exercise the production method.</b> Before this lane the only constructor
/// hard-coded <c>AppDomain.CurrentDomain.BaseDirectory</c>, so the arms could be asserted only by
/// reading the source — the same gap <c>DashboardConfigService</c> had until 2026-09-11. The internal
/// disk constructor runs exactly what production runs.</para>
/// </summary>
public sealed class ReportPageConfigI1Tests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root;

    public ReportPageConfigI1Tests(ITestOutputHelper output)
    {
        _out = output;
        _root = Path.Combine(Path.GetTempPath(), "sqlt-rpcfg-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string NewConfigPath()
    {
        var dir = Path.Combine(_root, "install-" + Guid.NewGuid().ToString("N")[..8], "Config");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "report-pages.json");
    }

    private static ReportPageConfigService Open(string configPath) =>
        new(NullLogger<ReportPageConfigService>.Instance, configPath);

    // ── ARM (a): absent. Seeding is legitimate here and nowhere else. ─────────────────────────────

    /// <summary>
    /// The one arm that MAY write. No <c>report-pages.json</c> ships in the payload and none is embedded
    /// in the assembly — verified by the shipped-Config listing on 2026-09-12 — so on a fresh install
    /// there is no better default to run from and nothing on disk to lose. This differs from
    /// <c>dashboard-config.json</c>, which IS embedded, and that difference is the whole reason the two
    /// services answer absence differently.
    /// </summary>
    [Fact]
    public void An_absent_file_is_seeded_because_nothing_ships_at_that_path()
    {
        var path = NewConfigPath();

        var svc = Open(path);

        svc.LoadOutcome.Should().Be(ConfigLoadOutcome.Missing);
        File.Exists(path).Should().BeTrue("a fresh install has no layout to lose and none ships");
        svc.Root.Pages.Should().HaveCount(3);
        _out.WriteLine(File.ReadAllText(path)[..Math.Min(200, (int)new FileInfo(path).Length)]);
    }

    // ── ARM (b): the JSON literal `null`. ────────────────────────────────────────────────────────

    /// <summary>
    /// Valid JSON, no object. <c>Deserialize</c> returns null without throwing, so the <c>catch</c>
    /// never runs — and before this lane that arm reached the same unconditional write. Asserted as
    /// "the bytes are unchanged" rather than "the file is not the defaults", because a write that
    /// happened to produce something else would pass the weaker form.
    /// </summary>
    [Fact]
    public void A_file_holding_the_json_literal_null_is_left_exactly_as_it_is()
    {
        var path = NewConfigPath();
        File.WriteAllText(path, "null");
        var before = File.ReadAllBytes(path);

        var svc = Open(path);

        svc.LoadOutcome.Should().Be(ConfigLoadOutcome.Unreadable);
        File.ReadAllBytes(path).Should().Equal(before,
            "the operator's file must be byte-identical after a read that could not use it");
        svc.Root.Pages.Should().HaveCount(3, "defaults are served in memory for this session");
        Directory.GetFiles(Path.GetDirectoryName(path)!, "*.rejected-*").Should().ContainSingle(
            "content that did not yield a config is preserved, so what was in it can still be seen later");
    }

    // ── ARM (c): the catch. The arm that cost the data. ──────────────────────────────────────────

    /// <summary>
    /// A truncated / malformed file — the shape a half-finished save or a full disk leaves behind. This
    /// is the arm the cold gate's report understated: it is not only "corrupt JSON", it is EVERY read
    /// failure, and the old code answered all of them by overwriting the file it had just failed to
    /// read.
    /// </summary>
    [Fact]
    public void A_file_that_does_not_parse_is_left_exactly_as_it_is_and_is_copied_aside()
    {
        var path = NewConfigPath();
        File.WriteAllText(path, "{\"version\":1,\"pages\":[{\"id\":\"qc\",");   // truncated mid-object
        var before = File.ReadAllBytes(path);

        var svc = Open(path);

        svc.LoadOutcome.Should().Be(ConfigLoadOutcome.Unreadable);
        File.ReadAllBytes(path).Should().Equal(before, "nothing may be written over a file we could not read");

        var rejected = Directory.GetFiles(Path.GetDirectoryName(path)!, "*.rejected-*");
        rejected.Should().ContainSingle();
        File.ReadAllBytes(rejected[0]).Should().Equal(before, "the copy is the operator's bytes, not ours");
    }

    /// <summary>An empty file is damage, not "nothing configured yet" — every writer here serialises a
    /// real object, so zero bytes is an interrupted write. It is not quarantined, because a copy of
    /// nothing preserves nothing; see <see cref="ConfigFileHelper"/>.</summary>
    [Fact]
    public void An_empty_file_is_damage_and_is_not_overwritten()
    {
        var path = NewConfigPath();
        File.WriteAllText(path, "   ");

        var svc = Open(path);

        svc.LoadOutcome.Should().Be(ConfigLoadOutcome.Empty);
        File.ReadAllText(path).Should().Be("   ", "an empty store is still the operator's file");
        svc.Root.Pages.Should().HaveCount(3);
    }

    // ── The second half of the fix: the write guard. ─────────────────────────────────────────────

    /// <summary>
    /// WITHOUT THIS THE LOAD FIX LASTS UNTIL THE FIRST EDIT. Load leaves the damaged file alone and
    /// serves defaults; the page then renders those defaults as though they were the operator's, and a
    /// single drag of a single section persists them — the same loss one click later, with the evidence
    /// gone. That is the measured Settings ▸ Access Control defect in a second store, recorded on
    /// <see cref="StoreWriteIntent"/>. The refusal uses the shared predicate
    /// <see cref="ConfigFileHelper.WouldOverwriteUnreadStore"/> rather than a local restatement of it.
    /// </summary>
    [Fact]
    public void An_edit_after_an_unreadable_load_is_refused_rather_than_persisted()
    {
        var path = NewConfigPath();
        File.WriteAllText(path, "null");
        var before = File.ReadAllBytes(path);

        var svc = Open(path);
        svc.LoadOutcome.Should().Be(ConfigLoadOutcome.Unreadable);

        svc.DeleteSection("quick-check", "qc-summary");
        svc.MoveSection("quick-check", "qc-results", -1);
        svc.UpdateRoot(svc.Root);

        File.ReadAllBytes(path).Should().Equal(before,
            "three separate mutators, each of which used to end in a whole-file write, must all refuse "
            + "while the in-memory layout is this build's defaults wearing the operator's name");
    }

    /// <summary>The refusal must not become a product that cannot be configured: a store that LOADED is
    /// still fully writable. A guard that refused everything would pass the test above and break the
    /// feature, which is the failure mode a one-directional pin invites.</summary>
    [Fact]
    public void A_healthy_store_still_saves()
    {
        var path = NewConfigPath();
        var seeded = Open(path);                       // arm (a) writes a real file
        seeded.LoadOutcome.Should().Be(ConfigLoadOutcome.Missing);

        var svc = Open(path);                          // now re-open it: a genuine load
        svc.LoadOutcome.Should().Be(ConfigLoadOutcome.Loaded);

        var before = File.ReadAllBytes(path);
        svc.DeleteSection("quick-check", "qc-summary");

        File.ReadAllBytes(path).Should().NotEqual(before, "an operator's edit on a healthy store persists");
        Open(path).GetSections("/audit").Should().NotContain(s => s.Id == "qc-summary");
    }
}
