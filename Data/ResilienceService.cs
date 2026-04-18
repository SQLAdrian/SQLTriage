using Microsoft.Data.SqlClient;
using Polly;
using Polly.Retry;

namespace SQLTriage.Data;

public class ResilienceService
{
    private readonly ResiliencePipeline _sqlPipeline;
    private readonly ResiliencePipeline _cachePipeline;

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
}
