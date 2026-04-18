/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Threading;
using System.Threading.Tasks;

namespace SQLTriage.Data
{
    /// <summary>
    /// Throttles concurrent SQL query execution to prevent connection pool exhaustion.
    /// Uses two tiers: heavy queries (TimeSeries — full table scans) are limited to
    /// <see cref="MaxHeavyConcurrent"/> slots; lightweight queries (StatCard, BarGauge,
    /// CheckStatus, DataGrid) share a separate higher-concurrency pool.
    /// </summary>
    public class QueryThrottleService : IDisposable
    {
        // TimeSeries queries touch large time-range scans — keep constrained.
        private const int MaxHeavyConcurrent = 5;
        // Aggregate/snapshot queries are cheap; allow more in parallel.
        private const int MaxLightConcurrent = 10;

        private readonly SemaphoreSlim _heavySemaphore = new(MaxHeavyConcurrent, MaxHeavyConcurrent);
        private readonly SemaphoreSlim _lightSemaphore = new(MaxLightConcurrent, MaxLightConcurrent);
        private bool _disposed;

        /// <summary>
        /// Executes an async operation with throttling.
        /// Pass <paramref name="isHeavy"/> = true for TimeSeries panels,
        /// false (default) for StatCard, BarGauge, CheckStatus, and DataGrid.
        /// </summary>
        public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, bool isHeavy, CancellationToken cancellationToken = default)
        {
            var sem = isHeavy ? _heavySemaphore : _lightSemaphore;
            await sem.WaitAsync(cancellationToken);
            try
            {
                return await operation();
            }
            finally
            {
                sem.Release();
            }
        }

        /// <summary>
        /// Overload that treats the query as lightweight (non-TimeSeries).
        /// Kept for backward compatibility with call sites that don't specify panel type.
        /// </summary>
        public Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
            => ExecuteAsync(operation, isHeavy: false, cancellationToken);

        public void Dispose()
        {
            if (!_disposed)
            {
                _heavySemaphore.Dispose();
                _lightSemaphore.Dispose();
                _disposed = true;
            }
        }
    }
}
