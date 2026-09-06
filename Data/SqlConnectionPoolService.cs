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
    /// Implements connection validation, per-server idle queues, session-state reset on
    /// reuse, automatic cleanup, and optimal pool sizing.
    /// </summary>
    public class SqlConnectionPoolService : IDisposable
    {
        // Idle connections segregated per connection-key. A single global FIFO meant one
        // request for server A could dequeue-and-dispose every healthy idle connection
        // for servers B..Z (near-zero reuse in multi-server runs); per-key queues make a
        // cross-server dequeue impossible. Queues are never removed once created — the
        // dict is bounded by the number of distinct servers, same as _connectionCounts.
        private readonly ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>> _idleByKey = new();
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
                // 1. Try this key's own idle queue first. Only same-server connections
                //    live here, so nothing healthy belonging to another server can be
                //    dequeued (let alone disposed) on this path.
                var idleQueue = _idleByKey.GetOrAdd(connectionKey, _ => new ConcurrentQueue<PooledConnection>());
                while (idleQueue.TryDequeue(out var pooled))
                {
                    if (pooled.ConnectionString == connectionString &&
                        pooled.Connection.State == ConnectionState.Open &&
                        DateTime.UtcNow - pooled.LastUsed < _idleTimeout &&
                        await TryResetSessionAsync(pooled, cancellationToken))
                    {
                        pooled.LastUsed = DateTime.UtcNow;
                        return pooled.Connection;
                    }

                    // Stale, closed, differently-configured (same key, different options),
                    // or failed the session reset — dispose it and keep draining this key's
                    // queue. Decrement the counters for the DISPOSED connection's own key,
                    // and the global counter, or it drifts upward on every stale dequeue
                    // until the pool wedges permanently at MaxGlobal.
                    try { pooled.Connection.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "[ConnPool] Dispose stale connection failed"); }
                    ReleaseSlot(GetConnectionKey(pooled.ConnectionString));
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

            _idleByKey.GetOrAdd(connectionKey, _ => new ConcurrentQueue<PooledConnection>())
                .Enqueue(new PooledConnection
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
            var keptCount = 0;
            var connectionsToDispose = new List<PooledConnection>();

            // Drain each key's queue and separate keep vs dispose
            foreach (var queue in _idleByKey.Values)
            {
                var connectionsToKeep = new List<PooledConnection>();
                while (queue.TryDequeue(out var pooled))
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
                    queue.Enqueue(connection);
                }
                keptCount += connectionsToKeep.Count;
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

            _logger.LogDebug("Connection pool cleanup completed: kept {KeptCount}, disposed {DisposedCount}", keptCount, connectionsToDispose.Count);
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

        // Session-state reset issued before a pooled connection is handed to the next
        // borrower. The custom pool bypasses ADO.NET pooling, so sp_reset_connection
        // never runs and any SET state a borrower leaves behind (LOCK_TIMEOUT, isolation
        // level, ROWCOUNT, ANSI options...) would otherwise leak to the next one — the
        // general class behind the SQLT-BPCHK-01470 ANSI_WARNINGS poisoning. Restores
        // login defaults and rolls back any orphaned transaction still holding locks.
        // QUOTED_IDENTIFIER / ANSI_NULLS are parse-time options: a SET here takes effect
        // from the NEXT batch, which is exactly the borrower's first batch. ARITHABORT is
        // deliberately not touched — a fresh connection leaves it to the database-scoped
        // configuration, and an explicit SET here would override that.
        private const string SessionResetSql =
            "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;" +
            "SET TRANSACTION ISOLATION LEVEL READ COMMITTED;" +
            "SET LOCK_TIMEOUT -1;" +
            "SET ROWCOUNT 0;" +
            "SET TEXTSIZE 2147483647;" +
            "SET DEADLOCK_PRIORITY NORMAL;" +
            "SET XACT_ABORT OFF;" +
            "SET NOCOUNT OFF;" +
            "SET ANSI_WARNINGS ON;" +
            "SET ANSI_NULLS ON;" +
            "SET QUOTED_IDENTIFIER ON;";

        /// <summary>
        /// Restores session defaults on an idle connection before it is reused, including
        /// the database context (a borrower may have issued USE). Returns false when the
        /// reset cannot be executed — a connection whose session state cannot be
        /// guaranteed is cheaper to replace than to debug, so the caller disposes it.
        /// </summary>
        private async Task<bool> TryResetSessionAsync(PooledConnection pooled, CancellationToken cancellationToken)
        {
            try
            {
                var sql = SessionResetSql;
                try
                {
                    var db = new SqlConnectionStringBuilder(pooled.ConnectionString).InitialCatalog;
                    if (!string.IsNullOrWhiteSpace(db))
                        sql = $"USE [{db.Replace("]", "]]")}];" + sql;
                }
                catch (Exception ex)
                {
                    // Unparseable connection string — skip the USE, still reset SET state.
                    _logger.LogDebug(ex, "[ConnPool] Could not parse InitialCatalog for session reset");
                }

                using var cmd = pooled.Connection.CreateCommand();
                cmd.CommandText = sql;
                if (cmd is System.Data.Common.DbCommand dbCmd)
                    await dbCmd.ExecuteNonQueryAsync(cancellationToken);
                else
                    cmd.ExecuteNonQuery();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[ConnPool] Session reset on reuse failed; connection will be disposed");
                return false;
            }
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
            foreach (var queue in _idleByKey.Values)
            {
                while (queue.TryDequeue(out var pooled))
                {
                    try { pooled.Connection.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "[ConnPool] Dispose during shutdown failed"); }
                }
            }
            _idleByKey.Clear();

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
