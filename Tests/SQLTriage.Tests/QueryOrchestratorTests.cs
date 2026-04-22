/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Scheduling;

namespace SQLTriage.Tests;

public class QueryOrchestratorTests : IDisposable
{
    private readonly QueryOrchestrator _orchestrator;
    private readonly QueryRegistry _registry;

    public QueryOrchestratorTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Orchestrator:GlobalConcurrency"] = "10",
                ["Orchestrator:PerServerConcurrency"] = "10",
                ["Orchestrator:ChannelCapacity"] = "100",
                ["Orchestrator:DefaultTimeoutSeconds"] = "5"
            })
            .Build();

        _registry = new QueryRegistry(NullLogger<QueryRegistry>.Instance, config);
        _orchestrator = new QueryOrchestrator(NullLogger<QueryOrchestrator>.Instance, config, _registry);
        _orchestrator.Start();
    }

    public void Dispose()
    {
        // Run shutdown off the xUnit sync context to avoid deadlocks
        Task.Run(async () =>
        {
            await _orchestrator.StopAsync();
            _orchestrator.Dispose();
            _registry.Dispose();
        }).Wait(TimeSpan.FromSeconds(10));
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

    [Fact]
    public async Task EnqueueAsync_PriorityOrdering_P0CompletesBeforeP4()
    {
        var p0Completed = false;
        var p4Completed = false;

        // Enqueue 10 P4 tasks that each take 50ms
        var p4Tasks = Enumerable.Range(0, 10)
            .Select(i => _orchestrator.EnqueueAsync(new QueryRequest
            {
                QueryId = $"test:p4:{i}",
                Work = async ct =>
                {
                    await Task.Delay(50, ct);
                    p4Completed = true;
                }
            }, QueryPriority.P4_Prefetch))
            .ToList();

        // Immediately enqueue a P0 task
        var p0Task = _orchestrator.EnqueueAsync(new QueryRequest
        {
            QueryId = "test:p0",
            Work = async ct =>
            {
                await Task.Delay(10, ct);
                p0Completed = true;
            }
        }, QueryPriority.P0_Dashboard);

        await p0Task;

        // P0 should have completed while P4 tasks are still running
        Assert.True(p0Completed);
        Assert.False(p4Tasks.All(t => t.IsCompleted));

        await Task.WhenAll(p4Tasks);
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
        var request = new QueryRequest
        {
            QueryId = "test:cancel",
            CancellationToken = cts.Token,
            Work = async ct =>
            {
                await Task.Delay(5000, ct);
            }
        };

        var task = _orchestrator.EnqueueAsync(request, QueryPriority.P1_Alert);
        cts.CancelAfter(50);

        await Assert.ThrowsAsync<OperationCanceledException>(() => task);
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
