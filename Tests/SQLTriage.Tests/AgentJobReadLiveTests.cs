/* In the name of God, the Merciful, the Compassionate */

// Pages lane, cluster 1 (2026-08-28) - the LIVE half of AgentJobReadHonestyTests.
//
// The unit tests pin the wording and the branch. What they cannot give is the fact the whole
// finding rests on: that a real msdb read which FAILS is now distinguishable from a real msdb read
// that returns nothing. This drives SQLTriage.Data.Services.Jobs.JobInventoryService against a
// real instance and a real dead host through a real ServerConnectionManager, and feeds the real
// outcomes to the real prose function the page calls.
//
// INERT in a normal `dotnet test` run: LiveFactAttribute computes Skip at discovery time, so this
// test reports SKIPPED - not passed - unless PAGESJOBREAD_LIVE_TARGET names a reachable SQL
// instance. It shipped with a plain Fact attribute and an arming early-return, which reports
// PASSED for a run that measured nothing (lane9-04, 2026-08-28).
//
// INVOCATION (first live run 2026-08-28 against .\NEW2022):
//   $env:PAGESJOBREAD_LIVE_TARGET = ".\NEW2022"
//   dotnet test Tests/SQLTriage.Tests --filter "FullyQualifiedName~AgentJobReadLive"
//
// Reads only: one parameterless SELECT over msdb.dbo.sysjobs/sysjobsteps/sysjobschedules. It
// writes nothing to any instance, and its connection store is a throwaway file under the temp
// directory - the installed service's own store is never opened.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Models.Jobs;
using SQLTriage.Data.Services.Jobs;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

public class AgentJobReadLiveTests : IDisposable
{
    private const string DeadHost = "ZZHUNTNOSUCHHOST";
    private const string UnconfiguredInstance = "ZZNOTCONFIGUREDANYWHERE";

    private readonly ITestOutputHelper _out;
    private readonly string _dir;

    public AgentJobReadLiveTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "sqltriage-pages-jobread-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private const string TargetVar = "PAGESJOBREAD_LIVE_TARGET";
    private static string? Target => Environment.GetEnvironmentVariable(TargetVar);

    /// <summary>
    /// The attribute is not the whole guard. If <see cref="LiveFactAttribute"/> is ever weakened or
    /// removed, an unarmed body must FAIL here rather than run past an empty target and pass.
    /// </summary>
    private static string RequireTarget()
    {
        var value = Environment.GetEnvironmentVariable(TargetVar);
        value.Should().NotBeNullOrWhiteSpace(
            $"{TargetVar} must name a reachable SQL instance for this harness to mean anything");
        return value!;
    }

    private JobInventoryService BuildService(string liveTarget)
    {
        var store = Path.Combine(_dir, "server-connections.json");
        var connections = new List<ServerConnection>
        {
            // TrustServerCertificate: these local instances carry self-signed certs. Without it the
            // "live" leg fails on the SSL chain and proves nothing about msdb.
            new() { Id = "11111111-0000-0000-0000-000000000001", ServerNames = liveTarget, Database = "master", TrustServerCertificate = true },
            new() { Id = "11111111-0000-0000-0000-000000000002", ServerNames = DeadHost,   Database = "master", TrustServerCertificate = true },
        };
        File.WriteAllText(store, JsonSerializer.Serialize(connections));

        var manager = new ServerConnectionManager(
            NullLogger<ServerConnectionManager>.Instance, seats: null, connectionsFilePath: store);

        return new JobInventoryService(manager, NullLogger<JobInventoryService>.Instance);
    }

    [LiveFact(TargetVar)]
    public async Task Live_a_failed_msdb_read_is_distinguishable_from_an_empty_one()
    {
        RequireTarget();

        var jobs = BuildService(Target!);

        // 1. The instance that is really there.
        var good = await jobs.GetJobsAsync(Target!);
        _out.WriteLine($"[{Target}] outcome={good.Outcome} jobs={good.Jobs.Count} detail={good.FailureDetail ?? "(none)"}");
        good.Outcome.Should().Be(JobReadOutcome.Read);
        good.DescribeFailure().Should().BeEmpty();

        // 2. The host that is not. Configured, so the routing is fine - the READ is what fails.
        var dead = await jobs.GetJobsAsync(DeadHost);
        _out.WriteLine($"[{DeadHost}] outcome={dead.Outcome} jobs={dead.Jobs.Count}");
        _out.WriteLine($"[{DeadHost}] detail={dead.FailureDetail}");
        dead.Outcome.Should().Be(JobReadOutcome.Failed);
        dead.FailureDetail.Should().NotBeNullOrWhiteSpace();
        dead.Jobs.Should().BeEmpty();

        // THE FINDING, in one line: before this change both of the above were Array.Empty, and
        // nothing downstream could tell them apart.
        dead.Jobs.Count.Should().Be(0);
        good.Succeeded.Should().BeTrue();
        dead.Succeeded.Should().BeFalse();

        // 3. An instance no connection covers - nothing was contacted at all.
        var unrouted = await jobs.GetJobsAsync(UnconfiguredInstance);
        _out.WriteLine($"[{UnconfiguredInstance}] outcome={unrouted.Outcome}");
        unrouted.Outcome.Should().Be(JobReadOutcome.NoConnection);

        // 4. The real outcomes through the real prose the page renders.
        var blocked = JobInventoryRead.DescribeComparisonBlocked(dead, good);
        _out.WriteLine("BLOCKED: " + blocked);
        blocked.Should().NotBeNull();
        blocked!.Should().Contain("The primary did not answer").And.Contain(DeadHost);

        JobInventoryRead.DescribeComparisonBlocked(good, good)
            .Should().BeNull("a live instance that answered twice is a comparison the page may render");

        // 5. And the guard page's sentence, from the same live failure.
        _out.WriteLine("GUARD: " + dead.DescribeFailure());
        dead.DescribeFailure().Should().Contain($"Could not read the SQL Agent jobs on '{DeadHost}'");
    }
}
