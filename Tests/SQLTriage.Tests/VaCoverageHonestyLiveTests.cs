/* In the name of God, the Merciful, the Compassionate */

// LIVE EXERCISE VEHICLE for the Vulnerability Assessment coverage fixes (pages lane cluster 6,
// 2026-08-28). INERT in a normal `dotnet test` run: LiveFactAttribute reports every test here as
// SKIPPED - not passed - unless the environment names two reachable SQL instances.
//
// WHY IT EXISTS. The prose functions in VaCoverageHonestyTests are driven by payloads a test
// composed. This file drives them with payloads a REAL four-target run produced, through the real
// SqlAssessmentService, against real instances, with two targets that genuinely do not exist. It
// is the same shape as the filed reproduce probe for pages-r2-02 ("an 'All servers' assessment
// over four targets of which two could not be answered").
//
// MEASURED BEFORE THE FIX, 2026-08-28, with this exact shape (.\NEW2022, .\OLD2017,
// ZZHUNTNOSUCHHOST, ZZHUNTNOSUCHHOST2):
//     report server=.\OLD2017          completed=1/4 -> TOAST-INFO: Assessment complete: .\OLD2017 (1/4)
//     report server=ZZHUNTNOSUCHHOST   completed=2/4 -> TOAST-INFO: Assessment complete: ZZHUNTNOSUCHHOST (2/4)
//     report server=ZZHUNTNOSUCHHOST2  completed=4/4 -> TOAST-INFO: Assessment complete: ZZHUNTNOSUCHHOST2 (4/4)
//     summary.TotalChecks=158 Rows=158
//     PAGE State.AssessedServers (cover band + SERVERS chip) = .\NEW2022, .\OLD2017,
//                                                              ZZHUNTNOSUCHHOST, ZZHUNTNOSUCHHOST2
//     PAGE SERVERS chip value = 4
//     servers that actually produced rows (distinct non-empty ThisServer across the merged
//     rows) = <none>
// The last line is why the reporting set is recorded by the runner and not recovered from the
// merged rows: MergeResults composes new rows and does not carry the per-server identity stamp,
// so a cover band rebuilt from the rows would have named nobody.
//
// INVOCATION (gate, on a box with the local test instances). Nothing is minted and nothing is
// planted on the instances; the bundle accessor is the shipped LOCKED state, which is what a
// fresh install has:
//
//     SQLTRIAGE_VA_LIVE_TARGET='.\NEW2022' SQLTRIAGE_VA_LIVE_TARGET2='.\OLD2017' \
//       dotnet test Tests/SQLTriage.Tests/SQLTriage.Tests.csproj \
//       --filter "FullyQualifiedName~VaCoverageHonestyLiveTests"
//
// Both variables must name instances that are RUNNING (check with `sc query`, not with a prior
// session's notes - this box's instance states have reversed twice during this wave). The two
// unreachable targets are nonexistent hostnames, which are reliable regardless of instance state.
//
// SIDE EFFECT: SqlAssessmentService writes its per-server and consolidated CSVs into the test
// binary's own output\ folder. That is the shipped behaviour under exercise, not a fixture; the
// folder is under bin\ and is not committed.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using SQLTriage.Tests.Licensing;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

public class VaCoverageHonestyLiveTests
{
    private const string TargetVar = "SQLTRIAGE_VA_LIVE_TARGET";
    private const string TargetVar2 = "SQLTRIAGE_VA_LIVE_TARGET2";
    private const string DeadA = "ZZHUNTNOSUCHHOST";
    private const string DeadB = "ZZHUNTNOSUCHHOST2";

    private readonly ITestOutputHelper _out;
    public VaCoverageHonestyLiveTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// Synchronous IProgress so every report is captured in order on the reporting thread.
    /// <c>Progress&lt;T&gt;</c> posts to the captured SynchronizationContext, and under xunit that
    /// would let reports land after the awaited run returned - a harness that measured its own
    /// scheduling rather than the runner's behaviour.
    /// </summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _sink;
        public SyncProgress(Action<T> sink) => _sink = sink;
        public void Report(T value) => _sink(value);
    }

    private static string Cs(string server) =>
        $"Server={server};Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=5;";

    private static string RequireTarget(string name)
    {
        // The attribute is not the whole guard: if it is ever weakened or removed, this body must
        // FAIL rather than pass with nothing behind it.
        var value = Environment.GetEnvironmentVariable(name);
        value.Should().NotBeNullOrWhiteSpace(
            $"{name} must name a reachable SQL instance for this harness to mean anything");
        return value!;
    }

    [LiveFact(TargetVar, TargetVar2)]
    public async Task Four_targets_two_dead_name_only_the_servers_that_reported()
    {
        var live1 = RequireTarget(TargetVar);
        var live2 = RequireTarget(TargetVar2);

        var names = new[] { live1, live2, DeadA, DeadB };
        var targets = names.Select(n => (ConnectionString: Cs(n), ServerName: n)).ToList();

        var svc = new SqlAssessmentService(
            NullLogger<SqlAssessmentService>.Instance,
            connectionManager: null!,
            bundle: new FakeBundleAccessor().SetLocked());

        var reports = new List<ServerAssessmentProgress>();
        var gate = new object();
        var progress = new SyncProgress<ServerAssessmentProgress>(p =>
        {
            lock (gate) reports.Add(p);
        });

        var run = await svc.RunMultiServerAssessmentAsync(targets, parallelism: 1, progress: progress);

        _out.WriteLine("=== POST-FIX: 4 targets, 2 dead ===");
        _out.WriteLine("planned: " + string.Join(", ", run.PlannedServers));
        foreach (var p in reports)
        {
            var msg = VulnerabilityAssessmentStateService.DescribeServerProgress(p);
            _out.WriteLine($"  {p.ServerName} phase={p.Phase} {p.Completed}/{p.Total} rows={p.RowCount} "
                           + $"-> {(msg is null ? "(no toast)" : (msg.Value.IsFailure ? "ERROR: " : "INFO: ") + msg.Value.Text)}");
        }
        _out.WriteLine("reporting: " + string.Join(", ", run.ReportingServers));
        foreach (var s in run.SilentServers)
            _out.WriteLine($"  silent: {s.ServerName} -> {s.Error}");
        _out.WriteLine($"summary rows={run.Summary.Results.Count} total={run.Summary.TotalChecks}");

        // ── pages-r2-02: the cover band's source list ────────────────────────
        run.PlannedServers.Should().BeEquivalentTo(names, "the run's intent is still recorded");
        run.ReportingServers.Should().NotContain(DeadA).And.NotContain(DeadB,
            "a target that was never contacted may not be named on a client PDF cover band");
        run.ReportingServers.Should().Contain(live1);
        run.SilentServers.Select(s => s.ServerName).Should().Contain(new[] { DeadA, DeadB });
        run.SilentServers.Where(s => s.ServerName == DeadA || s.ServerName == DeadB)
            .Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.Error),
                "a server dropped from the report has to carry the reason it was dropped");

        // ── pages-r2-03: what the page would have said, from the real payloads ─
        var messages = reports
            .Select(VulnerabilityAssessmentStateService.DescribeServerProgress)
            .Where(m => m is not null)
            .Select(m => m!.Value)
            .ToList();

        messages.Where(m => m.Text.Contains("Assessment complete"))
            .Should().OnlyContain(m => !m.Text.Contains(DeadA) && !m.Text.Contains(DeadB),
                "the pre-fix run said 'Assessment complete: ZZHUNTNOSUCHHOST (2/4)' for a host "
                + "that does not exist");
        messages.Should().Contain(m => m.IsFailure && m.Text.Contains(DeadA),
            "the failure has to be reported, and as an Error so it survives Notifications being off");
        reports.Where(p => p.Phase == ServerAssessmentPhase.Starting)
            .Should().NotBeEmpty("the progress bar still advances before a server is awaited")
            .And.OnlyContain(p => VulnerabilityAssessmentStateService.DescribeServerProgress(p) == null,
                "and that report no longer claims the server is complete before it has begun");

        // ── The coverage sentence, from this run's own measurement ───────────
        var notice = VulnerabilityAssessmentStateService.DescribeAssessmentCoverage(
            cancelled: false, run.PlannedServers, run.ReportingServers);
        _out.WriteLine("coverage notice: " + notice);
        notice.Should().NotBeNull();
        notice.Should().Contain("2 of the 4 servers in this run produced no assessment results at all");
        notice.Should().Contain(DeadA).And.Contain(DeadB);

        // ── Why the reporting set is not recovered from the merged rows ──────
        var fromRows = run.Summary.Results
            .Select(r => r.ThisServer)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _out.WriteLine($"servers recoverable from merged rows: {fromRows.Count}");
        fromRows.Count.Should().BeLessThan(run.ReportingServers.Count,
            "MergeResults composes new rows without the per-server identity stamp, so a cover band "
            + "rebuilt from the rows would understate coverage - measured, not assumed");
    }

    [Fact]
    public void A_whitespace_server_name_yields_no_target_at_all()
    {
        // pages-r2-04 case (b), the filed whitespace-connection probe, at the seam the page uses
        // to build its target list. No instance needed, so this one is not arm-gated.
        var conn = new ServerConnection { ServerNames = "   " };
        var list = conn.GetServerList();

        _out.WriteLine($"ServerNames=\"   \" -> GetServerList().Count = {list.Count}");
        list.Should().BeEmpty();

        // The state the page is then left in, and what it now says about it.
        var state = new VulnerabilityAssessmentStateService
        {
            HasRun = true,
            NothingContactedReason = "the selected connection has no server name configured",
        };
        (!state.Results.Any() && !state.IsRunning && state.HasRun).Should().BeTrue(
            "this is the exact render condition the compliance line used to sit behind");

        var text = VulnerabilityAssessmentStateService.DescribeEmptyResult(
            state.PlannedServers.Count, state.AssessedServers.Count,
            cancelled: false, scopeFilterActive: false,
            nothingContactedReason: state.NothingContactedReason);
        _out.WriteLine("renders: " + text);
        text.Should().Contain("Nothing was assessed");
        text.ToLowerInvariant().Should().NotContain("compliant");
    }
}
