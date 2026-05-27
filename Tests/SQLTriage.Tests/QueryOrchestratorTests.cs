/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Scheduling;

namespace SQLTriage.Tests;

public class QueryOrchestratorTests : IAsyncLifetime
{
    private QueryOrchestrator _orchestrator = null!;
    private QueryRegistry _registry = null!;

    public async Task InitializeAsync()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Orchestrator:GlobalConcurrency"] = "10",
                ["Orchestrator:PerServerConcurrency"] = "10",
                ["Orchestrator:ChannelCapacity"] = "100",
                ["Orchestrator:DefaultTimeoutSeconds"] = "2"
            })
            .Build();

        _registry = new QueryRegistry(NullLogger<QueryRegistry>.Instance, config);
        _orchestrator = new QueryOrchestrator(NullLogger<QueryOrchestrator>.Instance, config, _registry);
        _orchestrator.Start();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _orchestrator.StopAsync();
        _orchestrator.Dispose();
        _registry.Dispose();
    }

    [Fact]
    public async Task EnqueueAsync_SimpleWork_CompletesSuccessfully()
    {
        var request = new QueryRequest
        {
            QueryId = "test:simple",
            Work = async ct => await Task.Delay(10, ct)
        };

        var result = await _orchestrator.EnqueueAsync(request, QueryPriority.P0_Dashboard);

        Assert.True(result.Success);
        Assert.Null(result.Exception);
    }

    // QUARANTINED 2026-05-16. This test is flaky and CANNOT be made
    // deterministic as a black-box test of EnqueueAsync, because the
    // behaviour it tries to assert is genuinely not observable that way.
    //
    // Root cause (verified by reading QueryOrchestrator.DispatcherLoopAsync):
    // the dispatcher guarantees DEQUEUE order (DequeueNextAsync scans
    // channels P0..P4 in strict priority order — that code is CORRECT). But
    // it dispatches work fire-and-forget: `_ = TrackWorkAsync(request);`
    // then immediately loops. Work execution order is therefore decided by
    // the thread pool scheduling the fired tasks' continuations, NOT by
    // priority. So "P0 Work runs/completes before P4 Work" is not a
    // guarantee the orchestrator makes or can make at the Work level — only
    // dequeue order is. Three rewrites (timing window; gate + IsCompleted;
    // single-concurrency + execution-order log) all failed in CI for this
    // same reason: each tried to observe a property the public API does not
    // expose.
    //
    // The PRODUCTION CODE IS NOT BUGGY — priority dequeue is correct. Only
    // the test's premise is wrong.
    //
    // Correct fix (tracked in memory/worklist, needs orchestrator owner):
    // either (a) add a test seam exposing dequeue order (e.g. an internal
    // hook/event the dispatcher raises per dequeue, asserted directly), or
    // (b) unit-test DequeueNextAsync in isolation with pre-populated
    // channels. Both test the real contract; neither races the thread pool.
    [Fact]
    public async Task DequeueOrder_P0_DrainsBeforeP4()
    {
        // Deterministic. Drives the REAL WriteChannelAsync + DequeueNextAsync
        // via the ProbeDequeueOrderAsync seam — no dispatcher, no thread-pool
        // race. 10 P4 enqueued first, then 1 P0; P0 MUST dequeue before any P4
        // (dashboard queries never wait behind prefetch). This asserts the
        // actual guarantee (dequeue order), not the non-guarantee (Work
        // execution order) the prior 3 black-box rewrites flakily chased.
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Orchestrator:GlobalConcurrency"] = "1",
                ["Orchestrator:ChannelCapacity"] = "100"
            }).Build();
        using var reg = new QueryRegistry(NullLogger<QueryRegistry>.Instance, cfg);
        using var orch = new QueryOrchestrator(NullLogger<QueryOrchestrator>.Instance, cfg, reg);
        // Intentionally NOT Start()ed — drive the dequeue path directly.

        var enq = Enumerable.Repeat(QueryPriority.P4_Prefetch, 10)
            .Append(QueryPriority.P0_Dashboard)
            .ToList();
        var order = await orch.ProbeDequeueOrderAsync(enq);

        Assert.Equal(11, order.Count);
        Assert.Equal(QueryPriority.P0_Dashboard, order[0]);
        Assert.All(order.Skip(1), p => Assert.Equal(QueryPriority.P4_Prefetch, p));
    }

    [Fact]
    public async Task EnqueueAsync_ExceededTimeout_ReturnsFailure()
    {
        var request = new QueryRequest
        {
            QueryId = "test:timeout",
            Timeout = TimeSpan.FromMilliseconds(50),
            Work = async ct => await Task.Delay(500, ct)
        };

        var result = await _orchestrator.EnqueueAsync(request, QueryPriority.P1_Alert);

        Assert.False(result.Success);
        Assert.NotNull(result.Exception);
    }

    [Fact]
    public async Task EnqueueAsync_CancellationToken_RespectsExternalCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var request = new QueryRequest
        {
            QueryId = "test:cancel",
            Work = async ct => await Task.Delay(10, ct)
        };

        var task = _orchestrator.EnqueueAsync(request, QueryPriority.P1_Alert, cts.Token);

        var ex = await Record.ExceptionAsync(async () => await task);
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    [Fact]
    public async Task GetHealthAsync_ReturnsNonNegativeMetrics()
    {
        var health = await _orchestrator.GetHealthAsync();

        Assert.True(health.QueueDepth >= 0);
        Assert.True(health.InFlightCount >= 0);
        Assert.True(health.CompletedLastMinute >= 0);
        Assert.True(health.FailedLastMinute >= 0);
        Assert.True(health.AverageLatencyLastMinute >= TimeSpan.Zero);
    }

    [Fact]
    public async Task GetMetricsAsync_AfterEnqueue_IncrementsTotalEnqueued()
    {
        var before = await _orchestrator.GetMetricsAsync();

        await _orchestrator.EnqueueAsync(new QueryRequest
        {
            QueryId = "test:metrics",
            Work = async ct => await Task.Delay(10, ct)
        }, QueryPriority.P0_Dashboard);

        var after = await _orchestrator.GetMetricsAsync();

        Assert.True(after.TotalEnqueued > before.TotalEnqueued);
    }
}
