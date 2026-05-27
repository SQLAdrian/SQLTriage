/* In the name of God, the Merciful, the Compassionate */

using System.Threading.Channels;
using System.Threading.Tasks;

namespace SQLTriage.Data.Scheduling
{
    /// <summary>
    /// Priority levels for the unified query orchestrator.
    /// Lower numeric value = higher priority.
    /// </summary>
    public enum QueryPriority
    {
        /// <summary>Dashboard panel queries — user-facing, must be fastest.</summary>
        P0_Dashboard = 0,

        /// <summary>Alert evaluation queries — time-sensitive, fire within seconds.</summary>
        P1_Alert = 1,

        /// <summary>Scheduled task queries — background but time-bound.</summary>
        P2_ScheduledTask = 2,

        /// <summary>Audit / compliance queries — can tolerate minutes of delay.</summary>
        P3_Audit = 3,

        /// <summary>Prefetch / warm-up queries — lowest priority, best-effort.</summary>
        P4_Prefetch = 4
    }

    /// <summary>
    /// A single unit of work enqueued with the orchestrator.
    /// </summary>
    public sealed class QueryRequest
    {
        /// <summary>Unique identifier for this request (used for metrics/deduplication).</summary>
        public required string QueryId { get; init; }

        /// <summary>Target server name(s). Empty = all enabled servers.</summary>
        public IReadOnlyList<string> ServerNames { get; init; } = Array.Empty<string>();

        /// <summary>The actual work to execute. Receives a per-query CancellationToken.</summary>
        public required Func<CancellationToken, Task> Work { get; init; }

        /// <summary>SQL text or description for logging/metrics.</summary>
        public string? SqlText { get; init; }

        /// <summary>Per-query timeout. Null = use orchestrator default.</summary>
        public TimeSpan? Timeout { get; init; }

        /// <summary>External cancellation token (e.g. from Blazor circuit dispose).</summary>
        public CancellationToken CancellationToken { get; init; }

        /// <summary>Optional callback invoked on completion (success or failure).</summary>
        public Action<QueryResult>? OnComplete { get; init; }
    }

    /// <summary>
    /// Result of a completed <see cref="QueryRequest"/>.
    /// </summary>
    public sealed class QueryResult
    {
        public required string QueryId { get; init; }
        public required bool Success { get; init; }
        public TimeSpan Duration { get; init; }
        public Exception? Exception { get; init; }
    }

    /// <summary>
    /// Real-time health snapshot of the orchestrator.
    /// </summary>
    public sealed class OrchestratorHealth
    {
        public int QueueDepth { get; init; }
        public int InFlightCount { get; init; }
        public int CompletedLastMinute { get; init; }
        public int FailedLastMinute { get; init; }
        public TimeSpan AverageLatencyLastMinute { get; init; }
        public bool IsHealthy { get; init; }
        public DateTime Timestamp { get; init; }
    }

    /// <summary>
    /// Aggregated metrics for a time window.
    /// </summary>
    public sealed class OrchestratorMetrics
    {
        public int TotalEnqueued { get; init; }
        public int TotalCompleted { get; init; }
        public int TotalFailed { get; init; }
        public Dictionary<QueryPriority, int> CountByPriority { get; init; } = new();
        public Dictionary<string, TimeSpan> AverageLatencyByServer { get; init; } = new();
        public DateTime WindowStart { get; init; }
        public DateTime WindowEnd { get; init; }
    }

    /// <summary>
    /// Unified query scheduling and concurrency orchestrator.
    /// Replaces the fragmented scheduler/throttle landscape with a single
    /// priority-queue-based work dispatcher.
    /// </summary>
    public interface IQueryOrchestrator
    {
        /// <summary>
        /// Enqueues a query for execution at the given priority.
        /// Returns a Task that completes when the work finishes.
        /// If the queue is full, the caller blocks until space is available (backpressure).
        /// </summary>
        Task<QueryResult> EnqueueAsync(QueryRequest request, QueryPriority priority, CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns a real-time health snapshot.
        /// </summary>
        Task<OrchestratorHealth> GetHealthAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns aggregated metrics for the current sliding window.
        /// </summary>
        Task<OrchestratorMetrics> GetMetricsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates global and per-server concurrency limits at runtime.
        /// Existing in-flight queries are not affected; new acquisitions use the updated limits.
        /// </summary>
        void UpdateLimits(int globalConcurrency, int perServerConcurrency);

        /// <summary>
        /// Starts the background dispatcher loop. Idempotent.
        /// </summary>
        void Start();

        /// <summary>
        /// Stops accepting new work and drains in-flight queries gracefully.
        /// </summary>
        Task StopAsync(CancellationToken cancellationToken = default);
    }
}
