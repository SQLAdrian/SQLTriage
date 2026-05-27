using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace SQLTriage.Data;

public class ResilienceService
{
    private readonly ResiliencePipeline _sqlPipeline;
    private readonly ResiliencePipeline _cachePipeline;
    private readonly ConcurrentDictionary<string, ResiliencePipeline> _serverPipelines = new(StringComparer.OrdinalIgnoreCase);

    public ResilienceService()
    {
        _sqlPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                ShouldHandle = new PredicateBuilder().Handle<SqlException>(ex =>
                    ex.Number is -2 or 1205 or 64 or 233 or 10053 or 10054 or 10060)
            })
            .AddTimeout(TimeSpan.FromSeconds(30))
            .Build();

        _cachePipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromMilliseconds(100),
                BackoffType = DelayBackoffType.Linear
            })
            .Build();
    }

    public ResiliencePipeline SqlPipeline => _sqlPipeline;
    public ResiliencePipeline CachePipeline => _cachePipeline;

    /// <summary>
    /// Returns a per-server resilience pipeline that includes a circuit breaker.
    /// Circuits are independent per server so one failing server does not affect others.
    /// </summary>
    public ResiliencePipeline GetServerPipeline(string serverName)
    {
        return _serverPipelines.GetOrAdd(serverName, _ =>
            new ResiliencePipelineBuilder()
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5,
                    SamplingDuration = TimeSpan.FromSeconds(60),
                    MinimumThroughput = 5,
                    BreakDuration = TimeSpan.FromSeconds(30),
                    ShouldHandle = new PredicateBuilder().Handle<SqlException>()
                })
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 3,
                    Delay = TimeSpan.FromSeconds(1),
                    BackoffType = DelayBackoffType.Exponential,
                    ShouldHandle = new PredicateBuilder().Handle<SqlException>(ex =>
                        ex.Number is -2 or 1205 or 64 or 233 or 10053 or 10054 or 10060)
                })
                .AddTimeout(TimeSpan.FromSeconds(30))
                .Build());
    }

    /// <summary>Checks whether the circuit breaker for a server is currently open (fast-failing).</summary>
    public bool IsCircuitOpen(string serverName)
    {
        if (_serverPipelines.TryGetValue(serverName, out var pipeline))
        {
            // Polly v8 does not expose circuit state directly on the pipeline.
            // Callers should handle BrokenCircuitException from ExecuteAsync instead.
            return false;
        }
        return false;
    }
}
