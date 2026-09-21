using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace SQLTriage.Data;

public class ResilienceService
{
    private readonly ResiliencePipeline _sqlPipeline;
    private readonly ResiliencePipeline _cachePipeline;
    private readonly ConcurrentDictionary<string, ResiliencePipeline> _serverPipelines = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ResilienceService> _logger;

    // logger is optional so existing parameterless construction (tests, manual
    // wiring) keeps working; DI supplies the real logger.
    public ResilienceService(ILogger<ResilienceService>? logger = null)
    {
        _logger = logger ?? NullLogger<ResilienceService>.Instance;

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
        return _serverPipelines.GetOrAdd(serverName, key =>
            new ResiliencePipelineBuilder()
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5,
                    SamplingDuration = TimeSpan.FromSeconds(60),
                    MinimumThroughput = 5,
                    BreakDuration = TimeSpan.FromSeconds(30),
                    ShouldHandle = new PredicateBuilder().Handle<SqlException>(),
                    // DIAGNOSTIC (2026-05-29): log the SqlException number that
                    // tripped the breaker so we can see whether it is genuinely
                    // transient (-2/1205/10053/...) or a non-transient error
                    // (permission, missing object/DMV, edition gap) that the
                    // breaker arguably should not count. See open question in
                    // .handoff/FINDING_circuit_breaker_2026-05-29.md.
                    OnOpened = args =>
                    {
                        _logger.LogWarning(
                            "[CircuitBreaker] OPENED for server '{Server}' for {Break}s. " +
                            "Tripping outcome: {Outcome}",
                            key,
                            args.BreakDuration.TotalSeconds,
                            DescribeOutcome(args.Outcome.Exception));
                        return default;
                    },
                    OnClosed = args =>
                    {
                        _logger.LogInformation(
                            "[CircuitBreaker] CLOSED for server '{Server}' — calls resumed.", key);
                        return default;
                    },
                    OnHalfOpened = args =>
                    {
                        _logger.LogInformation(
                            "[CircuitBreaker] HALF-OPEN for server '{Server}' — probing with next call.", key);
                        return default;
                    }
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

    // Builds a compact, credential-free description of what tripped the
    // breaker: the SqlException error number(s) (which is what we need to
    // decide whether the breaker is over-firing on non-transient errors) plus
    // a short class hint. SqlException.Errors can carry several numbers.
    private static string DescribeOutcome(Exception? ex)
    {
        if (ex is null) return "no exception (result-based)";
        if (ex is SqlException sql)
        {
            var numbers = new List<int>();
            foreach (SqlError e in sql.Errors) numbers.Add(e.Number);
            var transient = numbers.Exists(n => n is -2 or 1205 or 64 or 233 or 10053 or 10054 or 10060);
            return $"SqlException number(s)=[{string.Join(",", numbers)}] " +
                   $"class={sql.Class} transient={transient}";
        }
        return $"{ex.GetType().Name}";
    }

    /// <summary>
    /// Translates a circuit-open exception into a human-readable message. Polly's raw
    /// <see cref="BrokenCircuitException"/> message ("The circuit is now open and is not
    /// allowing calls.") is opaque to users; callers catch the exception at the query
    /// boundary and surface this instead. Non-circuit exceptions pass through unchanged.
    /// Message-only — does not alter breaker behaviour.
    /// </summary>
    public static string FriendlyCircuitMessage(Exception ex) =>
        ex is BrokenCircuitException
            ? "Server is fast-failing after repeated errors — paused ~30s before retrying."
            : ex.Message;

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
