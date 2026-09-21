/* In the name of God, the Merciful, the Compassionate */

// ── no-server-idle ruling (Adrian fresh-eyes ruling 4, 2026-09-07) ─────────────────────────────
//
// WHY THIS FILE EXISTS. With zero configured servers the app must go idle in an explicit
// "no servers configured" state — there is NO implicit local default. Before this lane the shared
// connection factory returned a hard-coded "Server=.;Database=SQLWATCH;Integrated Security=true;"
// whenever no server was selected, so every background collector silently probed localhost forever.
//
// These tests hold the two halves of the ruling that a unit can hold:
//   1. the factory is Unconfigured (and refuses to hand out a "." connection) when no server and no
//      fallback string are configured;
//   2. a collector that reaches the factory (MetricHistoryCollectorService) is a true no-op in that
//      state rather than opening — and failing — a connection per panel per tick.
//
// The remaining half (exactly ONE idle log line across a live >=4-minute run, then collectors waking
// when a server is added) is a live probe, recorded in the lane evidence, not something a unit can claim.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

public class NoServerIdleTests
{
    // A ServerConnectionManager pointed at a path that does not exist loads zero connections and is
    // NOT damaged (damage is "the file exists and failed to load"), so GetEnabledConnections() is empty.
    private static ServerConnectionManager EmptyManager()
    {
        var missing = Path.Combine(Path.GetTempPath(), "nsi-" + Guid.NewGuid().ToString("N") + ".json");
        var mgr = new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance, connectionsFilePath: missing);
        Assert.False(mgr.IsStoreDamaged);
        Assert.Empty(mgr.GetEnabledConnections());
        return mgr;
    }

    private static GlobalInstanceSelector Selector() =>
        new GlobalInstanceSelector(NullLogger<GlobalInstanceSelector>.Instance);

    [Fact]
    public void Factory_with_no_servers_and_no_fallback_is_unconfigured()
    {
        var factory = new SqlServerConnectionFactory(EmptyManager(), Selector(), fallbackConnectionString: null);
        Assert.True(factory.IsUnconfigured);
    }

    [Fact]
    public void Factory_with_no_servers_and_empty_fallback_is_unconfigured()
    {
        var factory = new SqlServerConnectionFactory(EmptyManager(), Selector(), fallbackConnectionString: "   ");
        Assert.True(factory.IsUnconfigured);
    }

    [Fact]
    public void Backward_compat_ctor_with_null_is_unconfigured()
    {
        var factory = new SqlServerConnectionFactory((string?)null);
        Assert.True(factory.IsUnconfigured);
    }

    [Fact]
    public void Unconfigured_CreateConnection_throws_instead_of_returning_a_local_default()
    {
        var factory = new SqlServerConnectionFactory(EmptyManager(), Selector(), fallbackConnectionString: null);

        // The core of the ruling: NO implicit "Server=." connection. The old code returned a
        // SqlConnection to "." here; the fixed factory refuses before any network attempt.
        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateConnection());
        Assert.Contains("no server configured", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unconfigured_CreateConnectionAsync_throws_before_opening()
    {
        var factory = new SqlServerConnectionFactory(EmptyManager(), Selector(), fallbackConnectionString: null);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await factory.CreateConnectionAsync());
    }

    [Fact]
    public void Factory_with_a_configured_fallback_is_not_unconfigured_and_uses_that_server()
    {
        var factory = new SqlServerConnectionFactory(
            EmptyManager(), Selector(),
            fallbackConnectionString: "Server=configured-host;Database=SQLWATCH;Integrated Security=true;");

        Assert.False(factory.IsUnconfigured);

        using var conn = factory.CreateConnection();
        var dataSource = new SqlConnectionStringBuilder(conn.ConnectionString).DataSource;
        Assert.Equal("configured-host", dataSource);
        Assert.NotEqual(".", dataSource);   // never the removed local default
    }

    [Fact]
    public async Task MetricHistoryCollector_tick_is_a_no_op_when_the_factory_is_unconfigured()
    {
        var connections = EmptyManager();
        var selector = Selector();
        var factory = new SqlServerConnectionFactory(connections, selector, fallbackConnectionString: null);
        Assert.True(factory.IsUnconfigured);

        var configService = new DashboardConfigService(NullLogger<DashboardConfigService>.Instance);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var queryExecutor = new QueryExecutor(factory, configService, configuration, connections, new ResilienceService());
        using var cache = new liveQueriesCacheStore();
        var options = new MetricRetentionOptions();   // Enabled by default

        using var collector = new MetricHistoryCollectorService(
            NullLogger<MetricHistoryCollectorService>.Instance,
            configService, queryExecutor, cache, selector, options, factory);

        // The tick short-circuits: no connection is opened, nothing is written, nothing throws.
        var written = await collector.CollectAllAsync();
        Assert.Equal(0, written);
        Assert.Equal(0, collector.SamplesRetained);
    }

    // ── residual 1, ruled 2026-09-08: the census the two existing instruments could not see ──────
    //
    // WHY THIS EXISTS. Every test above measures SqlServerConnectionFactory, and so does the source
    // pin in ConnectionRetargetChokepointTests. Neither looks for a connection-string LITERAL
    // anywhere else in the tree — which is exactly how Data/Services/LocalInstanceDetector.cs
    // survived the no-server-idle lane: a class nobody called, holding four executable local dials
    // ("Data Source=.", "(local)", "localhost", "127.0.0.1") at Connect Timeout=2. Nothing that
    // measures BEHAVIOUR could see it, because nothing ever ran it — a 512 s live run never touched
    // it. Deleting that file is one fix; this is the one that stops the class of defect coming back
    // under another name.
    //
    // It is a LINT over text, not a boundary — the boundary is the factory refusing to hand out a
    // "." connection at all. Three deliberate exclusions, stated so a later reader does not mistake
    // the scan for something wider than it is:
    //   * COMMENTS are stripped first. SqlServerConnectionFactory.cs:146 documents the REMOVED
    //     "Server=.;Database=SQLWATCH;..." default in prose, and prose naming the old defect is the
    //     opposite of the defect. The ban is on executable literals only.
    //   * A NAMED local instance (".\new2022") is not a hit: that is a server a user configured, not
    //     a default the product invented.
    //   * Tests/ is out of scope for the same reason — test code dials ".\new2022" on purpose.

    private static readonly (Regex Pattern, string Literal)[] LocalDialPatterns =
    {
        // "." as the WHOLE data source: followed by ';', a closing quote, end of line — anything
        // that is neither a backslash (".\instance") nor an identifier character.
        (new Regex(@"Data Source=\.(?![\\\w])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Data Source=."),
        (new Regex(@"Data Source=\(local\)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Data Source=(local)"),
        (new Regex(@"Data Source=localhost", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Data Source=localhost"),
        (new Regex(@"Data Source=127\.0\.0\.1", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Data Source=127.0.0.1"),
        (new Regex(@"Server=\.;", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Server=.;"),
        (new Regex(@"Server=\(local\)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Server=(local)"),
        (new Regex(@"Server=localhost", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Server=localhost"),
        (new Regex(@"Server=127\.0\.0\.1", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Server=127.0.0.1"),
    };

    /// <summary>Directory segments the census never descends into.</summary>
    private static readonly string[] CensusExcludedDirectories =
        { "Tests", "bin", "obj", ".ignore", ".git", "node_modules" };

    [Fact]
    public void No_executable_local_dial_literal_survives_anywhere_in_product_code()
    {
        var root = RawPassedScan.RepoRoot();
        var files = ProductSourceFiles(root);

        // Non-vacuity. A scan that found no files would pass over nothing and say "clean" — the
        // failure mode this whole file exists to refuse. 617 product source files at c82a45f.
        Assert.True(files.Count > 300,
            $"the census found only {files.Count} product source files under {root.FullName} — it is " +
            "scanning nothing and must fail rather than report a clean tree.");

        var hits = new List<string>();
        foreach (var file in files)
        {
            var lines = StripComments(File.ReadAllText(file));
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var (pattern, literal) in LocalDialPatterns)
                {
                    if (!pattern.IsMatch(lines[i])) continue;
                    var relative = Path.GetRelativePath(root.FullName, file).Replace('\\', '/');
                    hits.Add($"{relative}:{i + 1}  [{literal}]  {lines[i].Trim()}");
                }
            }
        }

        Assert.True(hits.Count == 0,
            "Executable local-dial literal(s) in product code. SQLTriage has NO implicit local " +
            "instance (no-server-idle ruling, Adrian fresh-eyes ruling 4): an unconfigured install " +
            "goes idle, it does not guess at \".\" / (local) / localhost / 127.0.0.1. Delete the " +
            "dial, or read the server from configuration:\n  " + string.Join("\n  ", hits));
    }

    /// <summary>
    /// The service host announces the idle posture ITSELF (residual 3, ruled 2026-09-08).
    ///
    /// <para><c>NoServerIdleNotice.AnnounceOnce</c> is process-once, and before this lane the
    /// <c>--server</c> path had no call of its own: the single idle line on the 2026-09-08 live run
    /// was emitted by whichever background collector reached its first tick. This is a source pin,
    /// like the chokepoint lint — its job is to make a silent removal loud. The live proof is the
    /// gate's, recorded in the lane evidence, and it is a DIFFERENTIAL one: no log sink records a
    /// logger category (the Serilog output templates in WindowsServiceHost.cs omit {SourceContext}),
    /// so the emitter was proved by mutating this guard off and watching the idle line move from
    /// before every collector "started" line to after the first collector's tick.</para>
    /// </summary>
    [Fact]
    public void The_service_host_announces_the_idle_posture_itself()
    {
        var host = Path.Combine(RawPassedScan.RepoRoot().FullName, "Data", "Services", "WindowsServiceHost.cs");
        Assert.True(File.Exists(host),
            $"the pin cannot read '{host}' — it must scan real source, not pass over nothing.");

        Assert.Contains("NoServerIdleNotice.AnnounceOnce(", File.ReadAllText(host), StringComparison.Ordinal);
    }

    // ── census helpers ───────────────────────────────────────────────────────────────────────

    /// <summary>Every shipped <c>.cs</c>/<c>.razor</c> file under the repo root, build output and
    /// test code excluded. Excludes on DIRECTORY segments only, so a file named <c>Tests.cs</c>
    /// would still be scanned.</summary>
    private static List<string> ProductSourceFiles(DirectoryInfo root)
    {
        var separator = Path.DirectorySeparatorChar;

        return Directory
            .EnumerateFiles(root.FullName, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(f =>
            {
                var segments = Path.GetRelativePath(root.FullName, f).Replace('/', separator).Split(separator);
                for (var i = 0; i < segments.Length - 1; i++)   // directory segments only
                    if (CensusExcludedDirectories.Contains(segments[i], StringComparer.OrdinalIgnoreCase))
                        return false;
                return true;
            })
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The text with <c>//</c> line comments, <c>///</c> doc lines and <c>/* … */</c> blocks removed,
    /// one entry per source line so a hit can name its line number.
    ///
    /// <para>Deliberately naive about string literals: a <c>//</c> or <c>/*</c> INSIDE a string starts
    /// a strip here. That direction is the safe one for a ban — it can only UNDER-report, never
    /// invent a hit — and no connection-string literal in this repo contains either sequence. Said
    /// out loud so the next reader does not mistake this for a parser.</para>
    /// </summary>
    internal static string[] StripComments(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var stripped = new string[lines.Length];
        var inBlockComment = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var kept = new StringBuilder(line.Length);
            var j = 0;

            while (j < line.Length)
            {
                if (inBlockComment)
                {
                    var close = line.IndexOf("*/", j, StringComparison.Ordinal);
                    if (close < 0) { j = line.Length; break; }
                    inBlockComment = false;
                    j = close + 2;
                    continue;
                }

                if (j + 1 < line.Length && line[j] == '/' && line[j + 1] == '/') break;   // to end of line
                if (j + 1 < line.Length && line[j] == '/' && line[j + 1] == '*') { inBlockComment = true; j += 2; continue; }

                kept.Append(line[j]);
                j++;
            }

            stripped[i] = kept.ToString();
        }

        return stripped;
    }
}
