/* In the name of God, the Merciful, the Compassionate */

using System.Text.Json.Serialization;

namespace SqlHealthAssessment.Data.Models
{
    /// <summary>
    /// Represents a query performance metric data point for time-series visualization.
    /// </summary>
    public class QueryPerformancePoint : TimeSeriesPoint
    {
        /// <summary>
        /// Optional additional metadata about the query (e.g., query hash, plan handle).
        /// </summary>
        [JsonPropertyName("metadata")]
        public string? Metadata { get; set; }
    }

    /// <summary>
    /// Represents a top N query by execution time or resource consumption.
    /// </summary>
    public class TopQuery
    {
        [JsonPropertyName("query_hash")]
        public string QueryHash { get; set; } = "";

        [JsonPropertyName("execution_count")]
        public long ExecutionCount { get; set; }

        [JsonPropertyName("total_elapsed_time_ms")]
        public long TotalElapsedTimeMs { get; set; }

        [JsonPropertyName("avg_elapsed_time_ms")]
        public double AvgElapsedTimeMs { get; set; }

        [JsonPropertyName("min_elapsed_time_ms")]
        public long MinElapsedTimeMs { get; set; }

        [JsonPropertyName("max_elapsed_time_ms")]
        public long MaxElapsedTimeMs { get; set; }

        [JsonPropertyName("last_execution_time")]
        public DateTime LastExecutionTime { get; set; }

        [JsonPropertyName("query_plan_hash")]
        public string? QueryPlanHash { get; set; }

        [JsonPropertyName("sql_text_truncated")]
        public string SqlTextTruncated { get; set; } = "";
    }

    /// <summary>
    /// Represents a query plan cache statistics data point.
    /// </summary>
    public class PlanCachePoint : TimeSeriesPoint
    {
        [JsonPropertyName("series")]
        public string Series { get; set; } = "Cached Plans";

        [JsonPropertyName("value")]
        public long Value { get; set; }
    }

    /// <summary>
    /// Represents a batch request per second metric.
    /// </summary>
    public class BatchRequestPoint : TimeSeriesPoint
    {
        [JsonPropertyName("series")]
        public string Series { get; set; } = "Batch Requests/sec";

        [JsonPropertyName("value")]
        public double Value { get; set; }
    }

    /// <summary>
    /// Represents a compilation per second metric.
    /// </summary>
    public class CompilationPoint : TimeSeriesPoint
    {
        [JsonPropertyName("series")]
        public string Series { get; set; } = "Compilations/sec";

        [JsonPropertyName("value")]
        public double Value { get; set; }
    }

    /// <summary>
    /// Represents a query performance summary with aggregated metrics.
    /// </summary>
    public class QueryPerformanceSummary
    {
        [JsonPropertyName("total_queries_executed")]
        public long TotalQueriesExecuted { get; set; }

        [JsonPropertyName("avg_query_duration_ms")]
        public double AvgQueryDurationMs { get; set; }

        [JsonPropertyName("max_query_duration_ms")]
        public long MaxQueryDurationMs { get; set; }

        [JsonPropertyName("min_query_duration_ms")]
        public long MinQueryDurationMs { get; set; }

        [JsonPropertyName("total_logical_reads_mb")]
        public double TotalLogicalReadsMb { get; set; }

        [JsonPropertyName("total_physical_reads_mb")]
        public double TotalPhysicalReadsMb { get; set; }

        [JsonPropertyName("total_writes_mb")]
        public double TotalWritesMb { get; set; }

        [JsonPropertyName("avg_cpu_time_ms")]
        public double AvgCpuTimeMs { get; set; }

        [JsonPropertyName("max_cpu_time_ms")]
        public long MaxCpuTimeMs { get; set; }

        [JsonPropertyName("blocked_queries")]
        public int BlockedQueries { get; set; }

        [JsonPropertyName("recompilations")]
        public int Recompilations { get; set; }
    }
}
