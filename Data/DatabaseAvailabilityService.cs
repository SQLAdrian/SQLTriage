/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SqlHealthAssessment.Data
{
    /// <summary>
    /// Checks database availability on the target SQL Server.
    /// Results are cached to avoid repeated queries.
    /// </summary>
    public class DatabaseAvailabilityService
    {
        private readonly IDbConnectionFactory _connectionFactory;
        private readonly Dictionary<string, bool> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, bool>> _serverCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        public DatabaseAvailabilityService(IDbConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        }

        /// <summary>
        /// Checks if a database exists on the target server.
        /// Results are cached for the lifetime of the service.
        /// </summary>
        public async Task<bool> DatabaseExistsAsync(
            string databaseName,
            CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(databaseName, out var cached))
                    return cached;
            }

            try
            {
                SqlConnection conn;
                // Always use master database to avoid connection failures
                // when the target database doesn't exist yet
                if (_connectionFactory is SqlServerConnectionFactory sqlFactory)
                {
                    conn = (SqlConnection)sqlFactory.CreateConnection("master");
                }
                else
                {
                    conn = (SqlConnection)_connectionFactory.CreateConnection();
                }
                await conn.OpenAsync(cancellationToken);

                using var cmd = new SqlCommand(
                    "SELECT database_id FROM sys.databases WHERE name = @dbName;", conn);
                cmd.Parameters.AddWithValue("@dbName", databaseName);
                cmd.CommandTimeout = 10;

                var result = await cmd.ExecuteScalarAsync(cancellationToken);
                bool exists = result != null && result != DBNull.Value;

                lock (_lock)
                {
                    _cache[databaseName] = exists;
                }

                return exists;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Checks if a database exists on a specific server.
        /// Results are cached per server for the lifetime of the service.
        /// </summary>
        public async Task<bool> DatabaseExistsOnServerAsync(
            string serverName,
            string databaseName,
            CancellationToken cancellationToken = default)
        {
            var cacheKey = $"{serverName}:{databaseName}";
            
            lock (_lock)
            {
                if (_serverCache.TryGetValue(serverName, out var serverDbs) &&
                    serverDbs.TryGetValue(databaseName, out var cached))
                    return cached;
            }

            try
            {
                using var conn = new SqlConnection($"Server={serverName};Database=master;Integrated Security=true;TrustServerCertificate=true;");
                await conn.OpenAsync(cancellationToken);

                using var cmd = new SqlCommand(
                    "SELECT database_id FROM sys.databases WHERE name = @dbName;", conn);
                cmd.Parameters.AddWithValue("@dbName", databaseName);
                cmd.CommandTimeout = 10;

                var result = await cmd.ExecuteScalarAsync(cancellationToken);
                bool exists = result != null && result != DBNull.Value;

                lock (_lock)
                {
                    if (!_serverCache.ContainsKey(serverName))
                        _serverCache[serverName] = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    _serverCache[serverName][databaseName] = exists;
                }

                return exists;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Clears the cache. Call this when connections change.
        /// </summary>
        public void InvalidateCache()
        {
            lock (_lock)
            {
                _cache.Clear();
                _serverCache.Clear();
            }
        }
    }
}
