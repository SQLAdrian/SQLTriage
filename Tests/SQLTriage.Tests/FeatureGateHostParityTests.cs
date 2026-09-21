/* In the name of God, the Merciful, the Compassionate */

// ── GATE-02: the two hosts answered differently about the same licence ───────────────────────
//
// FeatureRegistrar.RegisterAll was a per-host STARTUP CALL, and exactly one host made it —
// App.xaml.cs:231, the WPF desktop. WindowsServiceHost, which is the host behind BOTH --server and
// --service (the installed live service), never called it. IFeatureGate treats an unregistered
// feature as not licensed, correctly and deliberately, so on that host every gated surface was
// hidden: the nav rendered four sections with ZERO dashboard links while dashboard-config.json held
// 27 dashboards and each /dashboard/{id} route still rendered perfectly by URL.
//
// Why the existing instruments missed it, which is the part worth keeping:
//   • ServiceHostParityTests compares the two hosts by matching `GetService<T>` type arguments.
//     FeatureRegistrar.RegisterAll(Services) is a STATIC call and resolves nothing, so it was
//     invisible to a scan built to enumerate resolutions.
//   • the desktop's own in-app server mode DID work, because ServerModeService forwards the
//     desktop's already-registered IFeatureGate instance into the browser container — so the
//     "server mode" everybody tests by hand was never the lane that was broken.
//
// The fix is structural, not another call: the gate carries its own registration (see
// FeatureGate.EnsureRegistered) and is handed it by AddSharedServices, the composition root every
// host passes through. Resolvable ⇒ registered. These tests hold that seam from three sides —
// the runtime container, a negative control that proves the assertion discriminates, and a source
// lint that fails if registration is ever duplicated back into a host.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

public class FeatureGateHostParityTests
{
    /// <summary>Every id FeatureRegistrar declares. Named, not counted: a count passes while the
    /// wrong six are registered, and DynamicDashboards is the one the defect was reported through.</summary>
    private static readonly string[] AllFeatureIds =
    {
        FeatureRegistrar.Consolidation,
        FeatureRegistrar.DynamicDashboards,
        FeatureRegistrar.DevTools,
        FeatureRegistrar.Remediation,
        FeatureRegistrar.ServerHardening,
        FeatureRegistrar.PlaybookMarkdown,
    };

    // ── 1. The seam, resolved for real ──────────────────────────────────────────────────────

    /// <summary>
    /// The claim that matters: a container composed the way EVERY host composes one hands out a gate
    /// that already knows the module set — with no host startup call anywhere in this test.
    /// </summary>
    [Fact]
    public void A_container_composed_by_the_shared_seam_hands_out_a_registered_gate()
    {
        using var provider = BuildSharedContainer();

        var gate = provider.GetRequiredService<IFeatureGate>();

        // Nothing has called EnsureRegistered. The first READ is the trigger, which is what makes
        // this work on a host that knows nothing about feature registration.
        gate.Features.Should().BeEquivalentTo(AllFeatureIds,
            "the gate registers itself on first read, so every host that can resolve it gets the "
            + "same module set — this is the GATE-02 fix and the assertion it is proved by");

        gate.Describe(FeatureRegistrar.DynamicDashboards).Should().NotBeNull(
            "the descriptor carries the nav metadata; the headless host had none of it");
    }

    /// <summary>
    /// NEGATIVE CONTROL for the test above. A gate built with no deferred registration and never
    /// registered by hand is EXACTLY the object the headless host used to hold. If this ever starts
    /// reporting features, the assertion above has stopped discriminating.
    /// </summary>
    [Fact]
    public void A_gate_with_no_registration_is_empty_which_is_what_the_headless_host_had()
    {
        var bare = new FeatureGate();

        bare.Features.Should().BeEmpty();
        bare.Describe(FeatureRegistrar.DynamicDashboards).Should().BeNull();
    }

    /// <summary>
    /// The behaviour that turned a missing call into a blank nav, pinned so the correction made to
    /// App.xaml.cs's comment is anchored by a test rather than by prose. Unregistered means NOT
    /// licensed — fail-closed — not "ungated". The old comment there claimed the opposite.
    /// </summary>
    [Fact]
    public void An_unregistered_feature_is_not_licensed_and_never_falls_back_to_ungated()
    {
        var bare = new FeatureGate();

        bare.IsLicensed(FeatureRegistrar.DynamicDashboards).Should().BeFalse();
        bare.IsEnabled(FeatureRegistrar.DynamicDashboards).Should().BeFalse();

        // And the soft toggle is not the thing holding it shut: soft defaults ON, so IsEnabled is
        // false purely because the HARD gate is absent.
        bare.IsSoftEnabled(FeatureRegistrar.DynamicDashboards).Should().BeTrue();
    }

    // ── 2. The registration's own properties ────────────────────────────────────────────────

    [Fact]
    public void The_deferred_registration_runs_exactly_once_however_many_reads_arrive()
    {
        var runs = 0;
        var gate = new FeatureGate(g => { Interlocked.Increment(ref runs); g.Register("x", () => true); });

        gate.IsEnabled("x");
        gate.IsLicensed("x");
        _ = gate.Features;
        _ = gate.Descriptors;
        gate.EnsureRegistered();

        runs.Should().Be(1);
    }

    [Fact]
    public void Concurrent_first_reads_neither_double_register_nor_see_an_empty_gate()
    {
        // The window the lock exists for: with registration marked done BEFORE it finished, a second
        // circuit rendering at the same moment would read a half-filled gate and hide a licensed
        // surface for that one render.
        var runs = 0;
        var gate = new FeatureGate(g =>
        {
            Interlocked.Increment(ref runs);
            Thread.Sleep(30);                       // widen the window on purpose
            g.Register("x", () => true);
        });

        var results = new bool[16];
        Parallel.For(0, results.Length, i => results[i] = gate.IsEnabled("x"));

        runs.Should().Be(1);
        results.Should().AllBeEquivalentTo(true,
            "a reader that arrives during registration must wait for it, not race past it");
    }

    /// <summary>
    /// ⚠ THE LIVE SERVICE. A throw here used to be caught by App.xaml.cs's try/catch; now the gate
    /// owns it. If it escaped, it would escape on the service's startup path, and the SCM's
    /// configured restart actions would make that a loop.
    /// </summary>
    [Fact]
    public void A_registration_that_throws_neither_rethrows_nor_retries_and_leaves_the_gate_closed()
    {
        var runs = 0;
        var gate = new FeatureGate(_ =>
        {
            Interlocked.Increment(ref runs);
            throw new InvalidOperationException("bundle unreadable");
        });

        gate.Invoking(g => g.EnsureRegistered()).Should().NotThrow();
        gate.IsEnabled(FeatureRegistrar.DynamicDashboards).Should().BeFalse("fail closed");
        gate.Features.Should().BeEmpty();

        runs.Should().Be(1, "a failed registration must not be re-attempted on every read — that "
                          + "would log once per render for the life of the process");
    }

    // ── 3. Source lints: the duplication that caused this must not come back ─────────────────

    /// <summary>
    /// ONE call site, and it is the composition root. This is the lint that fails the day somebody
    /// "helpfully" adds the registration back into a host — which is the shape of the original
    /// defect, since a second call site is a second thing to forget in a third host.
    /// </summary>
    [Fact]
    public void Feature_registration_has_exactly_one_call_site_and_it_is_the_composition_root()
    {
        var root = RepoRoot();

        var callers = SourceFiles(root)
            .Where(f => CodeLines(f).Any(l => l.Contains("FeatureRegistrar.RegisterAll(", StringComparison.Ordinal)))
            .Select(f => Rel(root, f))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        callers.Should().BeEquivalentTo(new[] { "Data/ServiceCollectionExtensions.cs" },
            "registration belongs in the one place every host composes. It used to be a startup "
            + "call in App.xaml.cs and nowhere else, and the headless host — the installed live "
            + "service — therefore ran with an empty gate. If a host needs the work done earlier, "
            + "call IFeatureGate.EnsureRegistered (idempotent), do not register again.");
    }

    /// <summary>
    /// The other half of the same claim: the seam only reaches a host that composes it. Both hosts
    /// do, and this fails if either stops — which would put that host back on an empty gate without
    /// any test above noticing, since they all build the container the same way.
    /// </summary>
    [Theory]
    [InlineData("App.xaml.cs")]
    [InlineData("Data/Services/WindowsServiceHost.cs")]
    public void Both_hosts_compose_the_registration_seam(string hostFile)
    {
        var path = Path.Combine(RepoRoot(), hostFile.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).Should().BeTrue($"the lint must read real source, not pass over {hostFile}");

        CodeLines(new FileInfo(path))
            .Any(l => l.Contains("AddSharedServices(", StringComparison.Ordinal))
            .Should().BeTrue(
                $"{hostFile} no longer composes AddSharedServices, so it no longer gets the "
                + "self-registering IFeatureGate — the exact state the headless host was in.");
    }

    /// <summary>
    /// The warm-up lives in the one method both hosts already call after licence initialisation, so
    /// the registered count is in the boot log per lane and a registration fault is visible at
    /// startup. Not the seam — an observability line, asserted so it does not quietly disappear.
    /// </summary>
    [Fact]
    public void The_shared_host_diagnostics_warms_the_gate_for_both_lanes()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "Data", "Services", "WindowsServiceHost.cs"));

        var start = source.IndexOf("internal static void InitializeHostDiagnostics", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "InitializeHostDiagnostics is gone — re-anchor this guard");

        var end = source.IndexOf("internal static IReadOnlyList<BackgroundStop> InitializeBackgroundServices",
            start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "re-anchor: the method's end marker moved");

        var body = source.Substring(start, end - start);
        body.Should().Contain("EnsureRegistered()",
            "both hosts pass through here; warming the gate puts the count and any fault in the "
            + "boot log instead of in somebody's first render");
    }

    // ── Mechanics ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A container built the way every host builds one: AddSharedServices over a real
    /// ServiceCollection. The two profile services are re-registered onto a temp directory
    /// afterwards (last registration wins) for the reason DiLifetimeCensusTests records — the
    /// defaults bind to the operator's real %APPDATA%\SQLTriage.
    /// </summary>
    private static ServiceProvider BuildSharedContainer()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSharedServices(configuration);

        var profileDir = Path.Combine(Path.GetTempPath(), "sqlt-featuregate-" + Guid.NewGuid().ToString("N"));
        services.AddSingleton(new UserSettingsService(Path.Combine(profileDir, "user-settings.json")));
        services.AddSingleton(new InstallProvenanceService(profileDir));

        return services.BuildServiceProvider();
    }

    private static string RepoRoot() => RawPassedScan.RepoRoot().FullName;

    private static string Rel(string root, FileInfo file)
        => Path.GetRelativePath(root, file.FullName).Replace('\\', '/');

    /// <summary>
    /// App source only — the test tree is allowed to call the registrar directly. The repo root's own
    /// files are included non-recursively because App.xaml.cs, the file the old call site lived in,
    /// sits there.
    /// </summary>
    private static IEnumerable<FileInfo> SourceFiles(string root)
    {
        var files = new List<FileInfo>(new DirectoryInfo(root).GetFiles("*.cs", SearchOption.TopDirectoryOnly));

        foreach (var sub in new[] { "Data", "Pages", "Components", "Cli", "Mcp" })
        {
            var dir = new DirectoryInfo(Path.Combine(root, sub));
            if (!dir.Exists) continue;
            files.AddRange(dir.GetFiles("*.cs", SearchOption.AllDirectories));
            files.AddRange(dir.GetFiles("*.razor", SearchOption.AllDirectories));
        }

        return files.Where(f =>
            !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
            && !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    /// <summary>
    /// Lines that are code, not commentary. Needed because App.xaml.cs's corrected comment NAMES
    /// the call it no longer makes — a plain Contains would read that as a call site and the lint
    /// would fail on its own explanation.
    /// </summary>
    private static IEnumerable<string> CodeLines(FileInfo file)
    {
        var inBlock = false;
        var inRazor = false;
        foreach (var raw in File.ReadAllLines(file.FullName))
        {
            var line = raw.Trim();
            if (inRazor) { if (line.Contains("*@")) inRazor = false; continue; }
            if (inBlock) { if (line.Contains("*/")) inBlock = false; continue; }
            if (line.StartsWith("@*", StringComparison.Ordinal))
            {
                if (!line.Contains("*@")) inRazor = true;
                continue;
            }
            if (line.StartsWith("/*", StringComparison.Ordinal))
            {
                if (!line.Contains("*/")) inBlock = true;
                continue;
            }
            if (line.StartsWith("//", StringComparison.Ordinal)
                || line.StartsWith("*", StringComparison.Ordinal)
                || line.StartsWith("<!--", StringComparison.Ordinal)) continue;
            yield return line;
        }
    }
}
