/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data
{
    /// <summary>
    /// High-performance connection pool that reuses SQL connections to reduce overhead.
    /// Implements connection validation, automatic cleanup, and optimal pool sizing.
    /// </summary>
    public class SqlConnectionPoolService : IDisposable
    {
        private readonly ConcurrentQueue<PooledConnection> _availableConnections = new();
        private readonly ConcurrentDictionary<string, int> _connectionCounts = new();
        private readonly Timer _cleanupTimer;
        private readonly int _maxPerServer;
        private readonly int _maxGlobal;
        private readonly int _minPoolSize;
        private readonly TimeSpan _connectionTimeout;
        private readonly TimeSpan _idleTimeout;
        private readonly ILogger<SqlConnectionPoolService> _logger;
        private volatile bool _disposed;

        // Atomic total-connection counter across all servers.
        private int _totalActiveConnections;

        /// <summary>
        /// Total live connection slots reserved across all servers. Read-only test/diagnostic
        /// seam — exposes the global counter so leak regressions (the counter drifting up to
        /// MaxGlobal until the pool wedges) can be asserted without a live SQL Server.
        /// </summary>
        public int TotalActiveConnections => Volatile.Read(ref _totalActiveConnections);

        /// <summary>Per-server reserved slot count for the given connection string (diagnostic seam).</summary>
        public int ActiveCountForServer(string connectionString)
            => _connectionCounts.TryGetValue(GetConnectionKey(connectionString), out var n) ? n : 0;

        // Global lock used to make the count-check → increment sequence atomic,
        // preventing TOCTOU races when multiple callers acquire simultaneously.
        private readonly object _countLock = new();

        // Signals waiting callers when a connection is returned to the pool.
        private readonly SemaphoreSlim _returnSignal = new(0);

        public SqlConnectionPoolService(IConfiguration configuration, ILogger<SqlConnectionPoolService> logger)
        {
            _logger = logger;
            _maxPerServer = configuration.GetValue<int>("ConnectionPool:MaxPerServer",
                              configuration.GetValue<int>("ConnectionPool:MaxSize", 13));
            _maxGlobal = configuration.GetValue<int>("ConnectionPool:MaxGlobal", 100);
            _minPoolSize = configuration.GetValue<int>("ConnectionPool:MinSize", 2);
            _connectionTimeout = TimeSpan.FromSeconds(configuration.GetValue<int>("ConnectionPool:TimeoutSeconds", 30));
            _idleTimeout = TimeSpan.FromMinutes(configuration.GetValue<int>("ConnectionPool:IdleTimeoutMinutes", 5));

            // Cleanup timer runs every 2 minutes
            _cleanupTimer = new Timer(CleanupIdleConnections, null,
                TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
        }

        /// <summary>
        /// Gets a connection from the pool or creates a new one.
        /// When the pool is saturated, blocks up to <see cref="_connectionTimeout"/> waiting
        /// for a connection to be returned. Throws <see cref="TimeoutException"/> if the
        /// wait expires — the caller gets a clean, retryable failure instead of a leaked
        /// untracked connection.
        /// </summary>
        public async Task<IDbConnection> GetConnectionAsync(string connectionString, CancellationToken cancellationToken = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SqlConnectionPoolService));

            var connectionKey = GetConnectionKey(connectionString);

            while (true)
            {
                // 1. Try to get from pool first
                while (_availableConnections.TryDequeue(out var pooled))
                {
                    if (pooled.ConnectionString == connectionString &&
                        pooled.Connection.State == ConnectionState.Open &&
                        DateTime.UtcNow - pooled.LastUsed < _idleTimeout)
                    {
                        pooled.LastUsed = DateTime.UtcNow;
                        return pooled.Connection;
                    }
                    else
                    {
                        // Connection is stale, closed, or for a different server — dispose it.
                        // Decrement the counters for the DISPOSED connection's own key, not the
                        // key the caller is asking for (they may differ when the queue holds
                        // connections for multiple servers). Also drop the global counter, or it
                        // drifts upward on every mismatched/stale dequeue until the pool wedges
                        // permanently at MaxGlobal.
                        try { pooled.Connection.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "[ConnPool] Dispose stale connection failed"); }
                        ReleaseSlot(GetConnectionKey(pooled.ConnectionString));
                    }
                }

                // 2. Atomically check-and-increment the count to avoid TOCTOU race.
                //    Rent fails if EITHER per-server OR global cap is hit.
                bool slotAcquired = false;
                bool perServerSaturated = false;
                lock (_countLock)
                {
                    var perServerCount = _connectionCounts.GetOrAdd(connectionKey, 0);
                    var totalCount = _totalActiveConnections;
                    if (perServerCount >= _maxPerServer)
                    {
                        perServerSaturated = true;
                    }
                    else if (totalCount >= _maxGlobal)
                    {
                        // globalSaturated — handled by the else branch in the warning below
                    }
                    else
                    {
                        _connectionCounts.AddOrUpdate(connectionKey, 1, (k, v) => v + 1);
                        Interlocked.Increment(ref _totalActiveConnections);
                        slotAcquired = true;
                    }
                }

                if (slotAcquired)
                {
                    try
                    {
                        var connection = new SqlConnection(connectionString);
                        await connection.OpenAsync(cancellationToken);
                        return connection;
                    }
                    catch
                    {
                        // Failed to open — release the slot we reserved at line ~123.
                        ReleaseSlot(connectionKey);
                        throw;
                    }
                }

                // 3. Pool saturated — block until a connection is returned.
                if (perServerSaturated)
                    _logger.LogWarning("[POOL] Per-server cap reached ({PerServer}) for key={Key}; waiting for return (global={Total}/{Max})",
                        _maxPerServer, connectionKey, _totalActiveConnections, _maxGlobal);
                else
                    _logger.LogWarning("[POOL] Global cap reached ({Global}); waiting for connection return (key={Key}, per-server={PerServer}/{Max})",
                        _maxGlobal, connectionKey, _connectionCounts.GetOrAdd(connectionKey, 0), _maxPerServer);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var acquired = await _returnSignal.WaitAsync(_connectionTimeout, cancellationToken);
                if (!acquired)
                    throw new TimeoutException(
                        $"[POOL] No connection available for key '{connectionKey}' after {_connectionTimeout.TotalSeconds:F0}s. " +
                        $"Per-server cap: {_maxPerServer}, global cap: {_maxGlobal}, total active: {_totalActiveConnections}.");

                _logger.LogInformation("[POOL] Connection available after {Ms}ms wait (key={Key})", sw.ElapsedMilliseconds, connectionKey);
                // Loop back and try to dequeue or create now that signal was received.
            }
        }

        /// <summary>
        /// Returns a connection to the pool for reuse and signals any waiting callers.
        /// </summary>
        public void ReturnConnection(IDbConnection connection, string connectionString)
        {
            if (_disposed || connection == null || connection.State != ConnectionState.Open)
            {
                try { connection?.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "[ConnPool] Dispose returned connection failed"); }
                if (connection != null)
                    ReleaseSlot(GetConnectionKey(connectionString));
                // Signal even on failed return so waiters on either cap can attempt a new create.
                if (!_disposed) _returnSignal.Release();
                return;
            }

            var connectionKey = GetConnectionKey(connectionString);

            _availableConnections.Enqueue(new PooledConnection
            {
                Connection = connection,
                ConnectionString = connectionString,
                LastUsed = DateTime.UtcNow
            });

            // Signal exactly one waiting caller that a connection is available.
            if (!_disposed) _returnSignal.Release();
        }

        /// <summary>
        /// Cleanup idle connections periodically
        /// </summary>
        private void CleanupIdleConnections(object? state)
        {
            if (_disposed) return;

            var cutoff = DateTime.UtcNow - _idleTimeout;
            var connectionsToKeep = new List<PooledConnection>();
            var connectionsToDispose = new List<PooledConnection>();

            // Drain the queue and separate keep vs dispose
            while (_availableConnections.TryDequeue(out var pooled))
            {
                if (pooled.LastUsed > cutoff && pooled.Connection.State == ConnectionState.Open)
                {
                    connectionsToKeep.Add(pooled);
                }
                else
                {
                    connectionsToDispose.Add(pooled);
                }
            }

            // Re-enqueue connections to keep
            foreach (var connection in connectionsToKeep)
            {
                _availableConnections.Enqueue(connection);
            }

            // Dispose idle connections
            foreach (var connection in connectionsToDispose)
            {
                try
                {
                    connection.Connection.Dispose();
                    ReleaseSlot(GetConnectionKey(connection.ConnectionString));
                }
                catch (Exception ex) { _logger.LogDebug(ex, "[ConnPool] Dispose during cleanup failed"); }
            }

            _logger.LogDebug("Connection pool cleanup completed: kept {KeptCount}, disposed {DisposedCount}", connectionsToKeep.Count, connectionsToDispose.Count);
        }

        /// <summary>
        /// Releases one reserved slot for <paramref name="connectionKey"/>: decrements the
        /// per-server count and, only if a slot was actually held, the global counter too.
        /// The single source of truth for slot release — every dispose/return path routes
        /// through here so the per-server and global counters can never diverge, and neither
        /// can go negative when a connection the pool never counted is handed back.
        /// </summary>
        private void ReleaseSlot(string connectionKey)
        {
            bool held;
            lock (_countLock)
            {
                if (_connectionCounts.TryGetValue(connectionKey, out var v) && v > 0)
                {
                    _connectionCounts[connectionKey] = v - 1;
                    held = true;
                }
                else
                {
                    held = false;
                }
            }
            if (held) Interlocked.Decrement(ref _totalActiveConnections);
        }

        /// <summary>
        /// Creates a consistent key from connection string for pooling
        /// </summary>
        private static string GetConnectionKey(string connectionString)
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(connectionString);
                return $"{builder.DataSource}|{builder.InitialCatalog}|{builder.UserID}";
            }
            catch
            {
                return connectionString.GetHashCode().ToString();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _cleanupTimer?.Dispose();
            _returnSignal.Dispose();

            // Dispose all pooled connections
            while (_availableConnections.TryDequeue(out var pooled))
            {
                try { pooled.Connection.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "[ConnPool] Dispose during shutdown failed"); }
            }

            _connectionCounts.Clear();
            Interlocked.Exchange(ref _totalActiveConnections, 0);
        }

        private class PooledConnection
        {
            public IDbConnection Connection { get; set; } = null!;
            public string ConnectionString { get; set; } = "";
            public DateTime LastUsed { get; set; }
        }
    }
}
