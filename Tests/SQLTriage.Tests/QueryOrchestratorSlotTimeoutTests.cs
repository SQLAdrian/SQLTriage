/* In the name of God, the Merciful, the Compassionate */

// INVARIANT B-prime (lane alert-correctness, 2026-09-19): OUR OWN FAULT IS NOT EVIDENCE ABOUT THE
// SERVER. A concurrency slot the query orchestrator could not grant is our resource, not the server's,
// so the alert loop must not count it as a circuit-breaker failure. A COMMAND timeout is the server's
// and must still count. The two are told apart by ORIGIN: QueryResult.FailedBeforeWorkStarted is set
// in QueryOrchestrator, at the only place a request is failed before its work runs.
//
// AND THE SLOT WAIT ALWAYS ENDS. Measured on the build-4082 binary before this lane: with the only slot
// held, a request whose 300 ms timeout expired never completed in 10 of 12 runs, because WaitAsync's
// own token (armed with the same timeout, a moment earlier) usually fired first and the resulting
// OperationCanceledException escaped ExecuteWorkAsync before the request's completion was set. The
// caller's await never returned. The first test below is that measurement, now required to pass every
// time.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;
using SQLTriage.Data.Scheduling;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

public sealed class QueryOrchestratorSlotTimeoutTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;

    public QueryOrchestratorSlotTimeoutTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "slot-timeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private (QueryOrchestrator Orchestrator, QueryRegistry Registry) NewOrchestrator(int defaultTimeoutSeconds = 60)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Orchestrator:GlobalConcurrency"] = "1",
                ["Orchestrator:PerServerConcurrency"] = "5",
                ["Orchestrator:ChannelCapacity"] = "100",
                ["Orchestrator:DefaultTimeoutSeconds"] = defaultTimeoutSeconds.ToString(),
                ["Scheduler:StateFilePath"] = Path.Combine(_dir, Guid.NewGuid().ToString("N") + "-scheduler-state.json"),
            })
            .Build();
        var registry = new QueryRegistry(NullLogger<QueryRegistry>.Instance, config);
        var orchestrator = new QueryOrchestrator(NullLogger<QueryOrchestrator>.Instance, config, registry);
        orchestrator.Start();
        return (orchestrator, registry);
    }

    /// <summary>Enqueues work that holds the only global slot until released, and waits until it does.</summary>
    private static async Task<(Task<QueryResult> Blocker, TaskCompletionSource Release)> HoldTheOnlySlotAsync(QueryOrchestrator orchestrator)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = orchestrator.EnqueueAsync(new QueryRequest
        {
            QueryId = "holder",
            Timeout = TimeSpan.FromSeconds(30),
            Work = async _ => { started.TrySetResult(); await release.Task; },
        }, QueryPriority.P1_Alert);
        var first = await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(first == started.Task, "HARNESS: the holder never got the slot, so no later request can be starved of it");
        return (blocker, release);
    }

    [Fact]
    public async Task A_request_that_cannot_get_a_slot_always_completes_and_is_marked_as_our_failure()
    {
        const int runs = 12;
        var completed = 0;

        for (var i = 0; i < runs; i++)
        {
            var (orchestrator, registry) = NewOrchestrator();
            try
            {
                var (blocker, release) = await HoldTheOnlySlotAsync(orchestrator);

                var workRan = false;
                var starved = orchestrator.EnqueueAsync(new QueryRequest
                {
                    QueryId = "starved",
                    Timeout = TimeSpan.FromMilliseconds(300),
                    Work = _ => { workRan = true; return Task.CompletedTask; },
                }, QueryPriority.P1_Alert);

                var first = await Task.WhenAny(starved, Task.Delay(TimeSpan.FromSeconds(5)));
                release.TrySetResult();
                Assert.True(first == starved,
                    $"run {i + 1}: a request that could not get a slot within 300 ms had not completed after 5 s. "
                    + "WHAT TO CHECK: whether every exit from QueryOrchestrator.ExecuteWorkAsync before the work "
                    + "runs still sets the request's completion (WaitForSlotAsync, CompleteWithoutRunning).");

                var result = await starved;
                Assert.False(result.Success);
                Assert.IsType<TimeoutException>(result.Exception);
                Assert.True(result.FailedBeforeWorkStarted,
                    $"run {i + 1}: a slot we could not grant must be marked as OUR failure at its origin");
                Assert.False(workRan, "the work must not run after its slot timed out");
                completed++;

                await blocker;
            }
            finally
            {
                await orchestrator.StopAsync();
                orchestrator.Dispose();
                registry.Dispose();
            }
        }

        _out.WriteLine($"completed with the slot-timeout result: {completed} of {runs}");
        Assert.Equal(runs, completed);
    }

    [Fact]
    public async Task A_caller_that_cancels_during_the_slot_wait_gets_a_cancellation_not_a_hang()
    {
        var (orchestrator, registry) = NewOrchestrator();
        try
        {
            var (blocker, release) = await HoldTheOnlySlotAsync(orchestrator);

            using var cts = new CancellationTokenSource();
            var waiting = orchestrator.EnqueueAsync(new QueryRequest
            {
                QueryId = "cancelled-while-waiting",
                Timeout = TimeSpan.FromSeconds(20),
                CancellationToken = cts.Token,
                Work = _ => Task.CompletedTask,
            }, QueryPriority.P1_Alert, cts.Token);

            await Task.Delay(200);
            cts.Cancel();

            var first = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(5)));
            release.TrySetResult();
            Assert.True(first == waiting, "a caller's cancellation during the slot wait must complete the request");
            var ex = await Record.ExceptionAsync(async () => await waiting);
            Assert.IsAssignableFrom<OperationCanceledException>(ex);
            await blocker;
        }
        finally
        {
            await orchestrator.StopAsync();
            orchestrator.Dispose();
            registry.Dispose();
        }
    }

    [Fact]
    public async Task A_failure_inside_the_work_is_not_marked_as_ours()
    {
        var (orchestrator, registry) = NewOrchestrator();
        try
        {
            var result = await orchestrator.EnqueueAsync(new QueryRequest
            {
                QueryId = "work-threw",
                Work = _ => throw new InvalidOperationException("thrown by the work"),
            }, QueryPriority.P1_Alert);

            Assert.False(result.Success);
            Assert.False(result.FailedBeforeWorkStarted, "the work ran, so the flag must not claim otherwise");
        }
        finally
        {
            await orchestrator.StopAsync();
            orchestrator.Dispose();
            registry.Dispose();
        }
    }

    // ── Through the alert engine, with the REAL orchestrator and the REAL breaker ─────

    private AlertEvaluationService NewEngine(IQueryOrchestrator orchestrator, ServerCircuitBreakerService breaker)
    {
        var templates = new AlertTemplateService(NullLogger<AlertTemplateService>.Instance);
        var channels = new NotificationChannelService(NullLogger<NotificationChannelService>.Instance, templates);
        var svc = new AlertEvaluationService(
            NullLogger<AlertEvaluationService>.Instance,
            new AlertDefinitionService(NullLogger<AlertDefinitionService>.Instance),
            new AlertHistoryService(NullLogger<AlertHistoryService>.Instance),
            new AlertingService(NullLogger<AlertingService>.Instance),
            new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance),
            new ToastService(),
            channels,
            new liveQueriesCacheStore(),
            orchestrator,
            breaker: breaker,
            evalFailureStorePath: Path.Combine(_dir, Guid.NewGuid().ToString("N") + "-eval-failures.json"));
        svc.DryRun = true;
        return svc;
    }

    private static AlertDefinition StandardAlert() => new()
    {
        Id = "slot_timeout_probe_" + Guid.NewGuid().ToString("N"),
        Name = "Slot timeout probe",
        Query = "SELECT 1",
        QueryMode = "standard",
        Operator = "greater_than",
        Thresholds = new AlertThresholds { Warning = 0 },
    };

    private static ServerConnection Connection(string server) => new()
    {
        Id = Guid.NewGuid().ToString(),
        ServerNames = server,
        UseWindowsAuthentication = true,
        ConnectionTimeout = 2,
        IsEnabled = true,
    };

    [Fact]
    public async Task A_slot_timeout_does_not_open_the_breaker_and_a_command_timeout_still_does()
    {
        // Default request timeout 1 s: the alert engine sets none of its own.
        var (orchestrator, registry) = NewOrchestrator(defaultTimeoutSeconds: 1);
        var breaker = new ServerCircuitBreakerService(NullLogger<ServerCircuitBreakerService>.Instance, audit: null);
        var svc = NewEngine(orchestrator, breaker);
        const string server = "slot-starved-server";
        var alert = StandardAlert();
        var queried = 0;
        svc.StandardQueryOverrideForTests = (_, _) => { Interlocked.Increment(ref queried); return Task.FromResult<object?>(0.0); };

        try
        {
            // B-prime: the only slot is held, so every evaluation fails in the orchestrator, before
            // the server is asked anything. Five of them would open the breaker if they counted.
            var (blocker, release) = await HoldTheOnlySlotAsync(orchestrator);
            for (var i = 0; i < 5; i++)
            {
                var run = svc.ThrottledEvaluateAsync(alert, Connection(server), server, new AlertGlobalDefaults(), CancellationToken.None);
                var first = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));
                Assert.True(first == run, $"evaluation {i + 1} under a held slot never returned");
                await run;
            }
            Assert.Equal(0, queried);
            Assert.True(breaker.ShouldAttempt(server),
                "five slot timeouts are OUR resource running out, not five failures of the server");
            release.TrySetResult();
            await blocker;

            // The control: the slot is free now, and the SERVER times out the command. That is a
            // hung server and it must still be backed off after three.
            svc.StandardQueryOverrideForTests = (_, _) => throw ServerAnswerClassifierTests.MakeSqlException((-2, 11));
            for (var i = 0; i < 3; i++)
                await svc.ThrottledEvaluateAsync(alert, Connection(server), server, new AlertGlobalDefaults(), CancellationToken.None);
            Assert.False(breaker.ShouldAttempt(server), "three command timeouts are three failures of the server");
            Assert.True(svc.HasEvaluationFailure(server));
        }
        finally
        {
            svc.Dispose();
            await orchestrator.StopAsync();
            orchestrator.Dispose();
            registry.Dispose();
        }
    }

    [Fact]
    public async Task A_refusal_from_the_server_does_not_open_the_breaker_but_still_reads_Unknown()
    {
        var (orchestrator, registry) = NewOrchestrator();
        var breaker = new ServerCircuitBreakerService(NullLogger<ServerCircuitBreakerService>.Instance, audit: null);
        var svc = NewEngine(orchestrator, breaker);
        const string server = "refusing-server";
        var alert = StandardAlert();
        // The exact shape measured for a login without VIEW SERVER STATE: 300 then 297, one exception.
        svc.StandardQueryOverrideForTests = (_, _) => throw ServerAnswerClassifierTests.MakeSqlException((300, 14), (297, 16));

        try
        {
            for (var i = 0; i < 6; i++)
                await svc.ThrottledEvaluateAsync(alert, Connection(server), server, new AlertGlobalDefaults(), CancellationToken.None);

            Assert.True(breaker.ShouldAttempt(server), "a server that refused six times answered six times");
            Assert.True(svc.HasEvaluationFailure(server),
                "a refused alert is still an evaluation failure: it reads Unknown, never Ok");
        }
        finally
        {
            svc.Dispose();
            await orchestrator.StopAsync();
            orchestrator.Dispose();
            registry.Dispose();
        }
    }
}
