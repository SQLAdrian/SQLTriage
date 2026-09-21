/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
// SQLTriage.Data declares its own LogLevel, so the logging one is aliased rather than relying on
// which using wins. CS0104 otherwise, and an alias says which one was meant.
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

/// <summary>
/// THE INVARIANT: <b>a customer's dashboard configuration is never silently replaced by a built-in
/// default.</b> A READ must not create the file, and the thing the product runs on when the file is
/// missing must be the layout this build ships, not a stub.
///
/// <para>WHAT WAS WRONG (lane seeder-stub-freeze, 2026-09-11, RULED by Adrian).
/// <c>DashboardConfigService.Load</c> ended with <c>if (!File.Exists(_configPath)) { Save(); }</c> — a
/// persist-on-read. With <c>config.default\dashboard-config.json</c> missing from the payload, the
/// constructor generated <see cref="DefaultConfigGenerator"/>'s stub (measured 100,274 bytes, three
/// dashboards, where the product ships 481,763 bytes and 27) and WROTE it to the operator's path. Two
/// things then made it permanent: <see cref="ConfigDefaultsSeeder"/> is create-if-absent, so every later
/// seeding pass found the stub present and recorded <c>Kept</c>; and <c>Save()</c> copies the previous file
/// to the backup only IF one exists, so on the absent path there was no backup, by construction.</para>
///
/// <para><b>THESE TESTS EXERCISE THE PRODUCTION METHOD, not a copy of it.</b> Before this lane the absent
/// path had no seam at all — the only constructor hard-coded
/// <c>AppDomain.CurrentDomain.BaseDirectory</c> — so the behaviour could only be asserted by reading the
/// source. The internal disk constructor runs the same two steps in the same order as production.</para>
///
/// <para><b>⚠ PROVED IN THIS REPO'S OWN TEST OUTPUT, by a control that reproduces.</b>
/// <c>CachedStatHonestyTests</c> and <c>NoServerIdleTests</c> build the service through the PRODUCTION
/// constructor, which has no path seam. Probe, 2026-09-11: delete <c>config\</c> beside the test assembly,
/// run <c>CachedStatHonestyTests</c> alone. On the base commit a <c>dashboard-config.json</c> of
/// <b>100,274 bytes</b> — the stub, to the byte — appeared in that folder; with this lane's change the
/// folder does not come back at all. Both arms passed their 14 tests, so the write was invisible to every
/// instrument in the suite. ⚠ NOT "on every run": <c>ConfigDefaultsSeedingTests</c> calls
/// <c>ConfigDefaultsSeeder.Run()</c> against <c>AppContext.BaseDirectory</c> and seeds that same
/// <c>config\</c> from <c>config.default\</c>, so whether the absent branch is taken depends on which class
/// runs first. The probe deleted the folder to make it deterministic. That folder is the one whose stale
/// contents turned sixteen genuinely-red tests green on 2026-09-11; see <c>ShippedConfig</c>'s remarks.</para>
/// </summary>
public sealed class DashboardConfigAbsentFileTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root;

    public DashboardConfigAbsentFileTests(ITestOutputHelper output)
    {
        _out = output;
        _root = Path.Combine(Path.GetTempPath(), "sqlt-dashcfg-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string NewInstall(out string configDir)
    {
        var baseDir = Path.Combine(_root, "install-" + Guid.NewGuid().ToString("N")[..8]);
        configDir = Path.Combine(baseDir, ConfigDefaultsSeeder.ConfigFolderName);
        Directory.CreateDirectory(configDir);
        return baseDir;
    }

    /// <summary>The production path pair, so every test binds the same two names production does.</summary>
    private static (string Config, string Backup) Paths(string configDir) =>
        (Path.Combine(configDir, "dashboard-config.json"),
         Path.Combine(configDir, "dashboard-config.backup.json"));

    private static DashboardConfigService Open(string configDir, ILogger<DashboardConfigService> log)
    {
        var (config, backup) = Paths(configDir);
        return new DashboardConfigService(log, config, backup, watchForChanges: false);
    }

    // ── I1: a read never writes ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE CORE PIN. Re-add a <c>Save()</c> on the absent branch of <c>Load</c> and this goes red on the
    /// first assertion. It is deliberately stated as "no file of any kind appeared", not "the file does not
    /// equal the stub": an assertion about CONTENT would pass a write that happened to produce something
    /// else, and the property ruled on is that nothing is written at all.
    /// </summary>
    [Fact]
    public void A_read_of_an_absent_config_creates_no_file_and_takes_no_backup()
    {
        var baseDir = NewInstall(out var configDir);
        var (configPath, backupPath) = Paths(configDir);
        var log = new CapturingLogger();

        var svc = Open(configDir, log);

        File.Exists(configPath).Should().BeFalse(
            "reading an absent dashboard config must not create it. Writing a built-in default to the "
            + "operator's path is the silent replacement this lane closed: ConfigDefaultsSeeder is "
            + "create-if-absent, so a file written here is Kept by every later update and the shipped "
            + "27-dashboard layout never arrives.");
        File.Exists(backupPath).Should().BeFalse(
            "and no backup can exist either — Save() only copies a file that is already there, so the "
            + "absent path produced a replacement with nothing to roll back to");
        Directory.GetFiles(configDir).Should().BeEmpty("nothing of any name was written");

        svc.Origin.Should().Be(DashboardConfigService.ConfigOrigin.ShippedDefaultsFromAssembly);
        _out.WriteLine(string.Join(Environment.NewLine, log.Messages));
    }

    /// <summary>
    /// "Best available default" is the EMBEDDED SHIPPED CONFIG, not the generator. The product already
    /// embedded the real artefact and <c>DashboardConfigMigrator</c> already read it one line before Load
    /// wrote the wrong thing — so this asserts the count is the shipped one and is strictly greater than
    /// the stub's, which is the fact the old behaviour got wrong.
    /// </summary>
    [Fact]
    public void The_fallback_is_the_shipped_layout_and_not_the_generator_stub()
    {
        var baseDir = NewInstall(out var configDir);
        var svc = Open(configDir, NullLogger<DashboardConfigService>.Instance);

        var stubCount = DefaultConfigGenerator.Generate().Dashboards.Count;
        var served = svc.Config.Dashboards.Count;

        _out.WriteLine($"served {served} dashboards; the generator stub has {stubCount}");

        served.Should().BeGreaterThan(stubCount,
            "the fallback must be the shipped layout. DashboardConfigService's own remarks record that the "
            + "generator yields three dashboards where the product ships 27, and serving the stub is what "
            + "made a packaging slip look like a three-dashboard product.");

        var embedded = DashboardConfigService.TryReadShippedConfigFromAssembly(out var detail);
        _out.WriteLine("embedded: " + detail);
        embedded.Should().NotBeNull(
            "the embedded Config\\dashboard-config.json resource must be readable in every profile. ABSENT "
            + "OR EMPTY IS A FAILURE, never a skip: without it the fallback degrades to the stub.");
        served.Should().Be(embedded!.Dashboards.Count, "and it is that resource being served");
    }

    /// <summary>
    /// The embedded resource and the shipped payload file must be the SAME BYTES. They come from one source
    /// file in the csproj (an EmbeddedResource and a Content TargetPath over <c>Config\dashboard-config.json</c>),
    /// so a difference means one of the two stopped tracking that file — and the fallback would then be a
    /// layout this build does not actually ship.
    /// </summary>
    [Fact]
    public void The_embedded_shipped_layout_is_the_same_bytes_the_payload_ships()
    {
        var payloadCopy = ShippedConfig.Path("dashboard-config.json");
        var onDisk = File.ReadAllBytes(payloadCopy);
        var embedded = DashboardConfigMigrator.ReadResourceBytes(DashboardConfigMigrator.ShippedResourceSuffix);

        embedded.Should().NotBeNull("the resource must be embedded");
        _out.WriteLine($"{payloadCopy}: {onDisk.Length} bytes; embedded: {embedded!.Length} bytes");

        // The reader strips a BOM, so compare on the BOM-free view of the file rather than requiring the
        // csproj's two references to agree about a byte nothing reads.
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var onDiskBody = onDisk.Take(3).SequenceEqual(bom) ? onDisk.Skip(3).ToArray() : onDisk;
        var embeddedBody = embedded.Take(3).SequenceEqual(bom) ? embedded.Skip(3).ToArray() : embedded;

        embeddedBody.Length.Should().Be(onDiskBody.Length,
            "the embedded fallback and the shipped default must be the same artefact");
        embeddedBody.Should().Equal(onDiskBody,
            "byte for byte: the fallback an install runs on must be the layout this build ships, not a "
            + "second copy that can drift from it");
    }

    /// <summary>
    /// A file that exists and does not parse is the operator's, damaged. It is copied aside for analysis and
    /// otherwise LEFT EXACTLY AS IT IS — the behaviour before this lane, preserved deliberately, because the
    /// old <c>!File.Exists</c> guard was the only thing stopping a corrupt file being replaced and removing
    /// the guard outright would have opened a worse door than the one being closed.
    /// </summary>
    [Fact]
    public void A_config_that_does_not_parse_is_left_exactly_as_it_is()
    {
        var baseDir = NewInstall(out var configDir);
        var (configPath, backupPath) = Paths(configDir);
        const string damaged = "{ this is not json at all";
        File.WriteAllText(configPath, damaged);

        var svc = Open(configDir, NullLogger<DashboardConfigService>.Instance);

        File.ReadAllText(configPath).Should().Be(damaged,
            "the operator's damaged file is theirs and is not overwritten by a default");
        File.Exists(backupPath).Should().BeFalse("and nothing was saved, so no backup was rotated");
        Directory.GetFiles(configDir, "*.corrupt.*.json").Should().NotBeEmpty(
            "it is copied aside for analysis, which is where the operator's content survives");
        svc.Origin.Should().Be(DashboardConfigService.ConfigOrigin.ShippedDefaultsAfterUnreadableFile);
    }

    /// <summary>
    /// <b>THE ARM THE PREVIOUS LANE LEFT OPEN, and it did not look like damage at all.</b> A file holding
    /// the JSON literal <c>null</c> is VALID JSON: <c>Deserialize</c> returns null and throws nothing, so
    /// the handler above never ran. <c>LoadConfigFromDisk</c> answered the null with
    /// <c>?? DefaultConfigGenerator.Generate()</c> — the three-dashboard stub — and <c>Load</c> then took
    /// its SUCCESS branch and set <c>Origin = ConfigOrigin.OperatorFile</c>. The product served a stub and
    /// reported it as the customer's own file, at Debug, where nothing would ever read it; any later save
    /// wrote that stub over the real layout. Fixed 2026-09-12 (lane I1-census) by throwing instead, so this
    /// arm lands in the same handler every other unreadable file does.
    ///
    /// <para>The Origin assertion is the load-bearing one. A stub served under
    /// <see cref="DashboardConfigService.ConfigOrigin.ShippedDefaultsAfterUnreadableFile"/> is a product
    /// telling the truth about a bad file; the same stub under <c>OperatorFile</c> is a product that has
    /// quietly replaced the operator's configuration and says nothing.</para>
    /// </summary>
    [Fact]
    public void A_config_holding_the_json_literal_null_is_left_alone_and_never_reported_as_the_operators()
    {
        var baseDir = NewInstall(out var configDir);
        var (configPath, backupPath) = Paths(configDir);
        const string literalNull = "null";
        File.WriteAllText(configPath, literalNull);

        var svc = Open(configDir, NullLogger<DashboardConfigService>.Instance);

        svc.Origin.Should().NotBe(DashboardConfigService.ConfigOrigin.OperatorFile,
            "a built-in stub must never be reported as the operator's file — that false origin is what "
            + "made this silent, because every surface that could have warned reads Origin");
        svc.Origin.Should().Be(DashboardConfigService.ConfigOrigin.ShippedDefaultsAfterUnreadableFile);

        File.ReadAllText(configPath).Should().Be(literalNull, "nothing may be written over it");
        File.Exists(backupPath).Should().BeFalse("nothing was saved, so no backup was rotated");
        Directory.GetFiles(configDir, "*.corrupt.*.json").Should().NotBeEmpty(
            "content that yielded no config is still content, and it is preserved for analysis");

        svc.Config.Dashboards.Should().HaveCountGreaterThan(
            DefaultConfigGenerator.Generate().Dashboards.Count,
            "the fallback is the embedded shipped layout, not the generator stub");
    }

    // ── The freeze: what the write used to cost on the NEXT update ────────────────────────────────

    /// <summary>
    /// <b>THE HALF THAT MADE IT PERMANENT, with its own control.</b> The seeder is create-if-absent, so
    /// whether the next update delivers the real 27-dashboard layout depends entirely on whether the read
    /// left a file behind. Both arms run here: with nothing left behind the real default is SEEDED, and with
    /// a stub left behind (the old behaviour, reproduced by hand) it is KEPT and the operator never receives
    /// the shipped layout. The second arm is the control — absence of evidence counts only once the
    /// instrument is proved able to show a positive.
    /// </summary>
    [Fact]
    public void The_next_update_delivers_the_real_layout_only_because_the_read_left_nothing_behind()
    {
        var shippedSource = ShippedConfig.Path("dashboard-config.json");
        var shippedBytes = File.ReadAllBytes(shippedSource);

        // ARM A — today's behaviour: read, then update.
        var armA = NewInstall(out var configDirA);
        Directory.CreateDirectory(Path.Combine(armA, ConfigDefaultsSeeder.DefaultsFolderName));
        _ = Open(configDirA, NullLogger<DashboardConfigService>.Instance);
        File.Copy(shippedSource,
                  Path.Combine(armA, ConfigDefaultsSeeder.DefaultsFolderName, "dashboard-config.json"));

        var seedA = ConfigDefaultsSeeder.Seed(armA, new[] { "dashboard-config.json" });
        foreach (var line in seedA.Describe()) _out.WriteLine("A: " + line);

        seedA.Seeded.Select(e => e.RelativePath).Should().Contain("dashboard-config.json",
            "the read left nothing behind, so the next update creates the real file");
        File.ReadAllBytes(Path.Combine(configDirA, "dashboard-config.json")).Should().Equal(shippedBytes,
            "and what the operator has is the shipped 27-dashboard layout");

        // ARM B — the control: the stub the old code would have written, put there by hand.
        var armB = NewInstall(out var configDirB);
        Directory.CreateDirectory(Path.Combine(armB, ConfigDefaultsSeeder.DefaultsFolderName));
        File.Copy(shippedSource,
                  Path.Combine(armB, ConfigDefaultsSeeder.DefaultsFolderName, "dashboard-config.json"));
        var stubPath = Path.Combine(configDirB, "dashboard-config.json");
        File.WriteAllText(stubPath, System.Text.Json.JsonSerializer.Serialize(DefaultConfigGenerator.Generate()));
        var stubBytes = File.ReadAllBytes(stubPath);

        var seedB = ConfigDefaultsSeeder.Seed(armB, new[] { "dashboard-config.json" });
        foreach (var line in seedB.Describe()) _out.WriteLine("B: " + line);

        seedB.Kept.Select(e => e.RelativePath).Should().Contain("dashboard-config.json",
            "the control: a stub on disk is Kept, for ever, by every update route");
        File.ReadAllBytes(stubPath).Should().Equal(stubBytes,
            "and the shipped layout never reaches that install — which is what the removed Save() cost");
        stubBytes.Length.Should().BeLessThan(shippedBytes.Length,
            "the stub really is the smaller, poorer artefact, so the two arms differ in the direction claimed");
    }

    // ── "Somebody must be told" ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Running on something that is not the operator's file is reported at Error with the consequence
    /// spelled out, and — because a warning that misdescribes what happened is worse than no warning — it
    /// says explicitly that nothing was written, so nobody reads the message as data loss.
    /// </summary>
    [Fact]
    public void The_operator_is_told_loudly_and_told_that_nothing_was_written()
    {
        var baseDir = NewInstall(out var configDir);
        var log = new CapturingLogger();

        var svc = Open(configDir, log);

        var errors = log.Entries.Where(e => e.Level >= MsLogLevel.Error).Select(e => e.Message).ToList();
        foreach (var m in errors) _out.WriteLine(m);

        errors.Should().NotBeEmpty(
            "a process serving dashboards that are not the operator's must say so at Error. Silence here is "
            + "what presented 'running on defaults you never chose' as a normal start.");

        var text = string.Join(Environment.NewLine, errors);
        text.Should().Contain("dashboard-config.json", "naming the file");
        text.Should().Contain("does not exist", "and the state it was found in");
        text.Should().Contain("NOTHING HAS BEEN WRITTEN",
            "and the fact that stops the message reading as data loss");
        text.Should().Contain("NOT your", "and that what is being served is not theirs");
    }

    private sealed record LogLine(MsLogLevel Level, string Message);

    /// <summary>House pattern (see EscalationChannelRoutingTests): the smallest logger that lets an
    /// assertion be about what the operator would actually read.</summary>
    private sealed class CapturingLogger : ILogger<DashboardConfigService>
    {
        public List<LogLine> Entries { get; } = new();
        public IEnumerable<string> Messages => Entries.Select(e => $"[{e.Level}] {e.Message}");

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Scope();
        public bool IsEnabled(MsLogLevel logLevel) => true;

        public void Log<TState>(MsLogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogLine(logLevel, formatter(state, exception)));

        private sealed class Scope : IDisposable { public void Dispose() { } }
    }
}
