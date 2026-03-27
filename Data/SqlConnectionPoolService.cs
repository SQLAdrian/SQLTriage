/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SqlHealthAssessment.Data
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
        private readonly int _maxPoolSize;
        private readonly int _minPoolSize;
        private readonly TimeSpan _connectionTimeout;
        private readonly TimeSpan _idleTimeout;
        private readonly ILogger<SqlConnectionPoolService> _logger;
        private volatile bool _disposed;

        public SqlConnectionPoolService(IConfiguration configuration, ILogger<SqlConnectionPoolService> logger)
        {
            _logger = logger;
            _maxPoolSize = configuration.GetValue<int>("ConnectionPool:MaxSize", 50);
            _minPoolSize = configuration.GetValue<int>("ConnectionPool:MinSize", 2);
            _connectionTimeout = TimeSpan.FromSeconds(configuration.GetValue<int>("ConnectionPool:TimeoutSeconds", 30));
            _idleTimeout = TimeSpan.FromMinutes(configuration.GetValue<int>("ConnectionPool:IdleTimeoutMinutes", 5));

            // Cleanup timer runs every 2 minutes
            _cleanupTimer = new Timer(CleanupIdleConnections, null, 
                TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
        }

        /// <summary>
        /// Gets a connection from the pool or creates a new one
        /// </summary>
        public async Task<IDbConnection> GetConnectionAsync(string connectionString, CancellationToken cancellationToken = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SqlConnectionPoolService));

            var connectionKey = GetConnectionKey(connectionString);

            // Try to get from pool first
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
                    // Connection is stale or closed, dispose it
                    try { pooled.Connection.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "[ConnPool] Dispose stale connection failed"); }
                    _connectionCounts.AddOrUpdate(connectionKey, 0, (k, v) => Math.Max(0, v - 1));
                }
            }

            // Create new connection if under limit
            var currentCount = _connectionCounts.GetOrAdd(connectionKey, 0);
            if (currentCount < _maxPoolSize)
            {
                var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                
                _connectionCounts.AddOrUpdate(connectionKey, 1, (k, v) => v + 1);
                return connection;
            }

            // Pool is full, create temporary connection
            var tempConnection = new SqlConnection(connectionString);
            await tempConnection.OpenAsync(cancellationToken);
            return tempConnection;
        }

        /// <summary>
        /// Returns a connection to the pool for reuse
        /// </summary>
        public void ReturnConnection(IDbConnection connection, string connectionString)
        {
            if (_disposed || connection == null || connection.State != ConnectionState.Open)
            {
                try { connection?.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "[ConnPool] Dispose returned connection failed"); }
                return;
            }

            var connectionKey = GetConnectionKey(connectionString);
            var currentCount = _connectionCounts.GetOrAdd(connectionKey, 0);

            // Only pool if under max size
            if (currentCount <= _maxPoolSize)
            {
                _availableConnections.Enqueue(new PooledConnection
                {
                    Connection = connection,
                    ConnectionString = connectionString,
                    LastUsed = DateTime.UtcNow
                });
            }
            else
            {
                try { connection.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "[ConnPool] Dispose excess connection failed"); }
                _connectionCounts.AddOrUpdate(connectionKey, 0, (k, v) => Math.Max(0, v - 1));
            }
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
                    var key = GetConnectionKey(connection.ConnectionString);
                    _connectionCounts.AddOrUpdate(key, 0, (k, v) => Math.Max(0, v - 1));
                } 
                catch (Exception ex) { _logger.LogDebug(ex, "[ConnPool] Dispose during cleanup failed"); }
            }

            _logger.LogDebug("Connection pool cleanup completed: kept {KeptCount}, disposed {DisposedCount}", connectionsToKeep.Count, connectionsToDispose.Count);
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

            // Dispose all pooled connections
            while (_availableConnections.TryDequeue(out var pooled))
            {
                try { pooled.Connection.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "[ConnPool] Dispose during shutdown failed"); }
            }

            _connectionCounts.Clear();
        }

        private class PooledConnection
        {
            public IDbConnection Connection { get; set; } = null!;
            public string ConnectionString { get; set; } = "";
            public DateTime LastUsed { get; set; }
        }
    }
}