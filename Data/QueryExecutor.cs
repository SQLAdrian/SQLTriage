/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using System.Threading.Tasks;
using SqlHealthAssessment.Data.Models;
using Microsoft.Extensions.Configuration;
using Polly;

namespace SqlHealthAssessment.Data
{
    /// <summary>
    /// Executes parameterized queries from the QueryStore against the configured database.
    /// Uses SQL Server via Microsoft.Data.SqlClient with async I/O for optimal performance.
    /// All exceptions are scrubbed to prevent connection string / credential leakage.
    /// </summary>
    public class QueryExecutor
    {
        private readonly int _commandTimeout;
        private readonly int _maxRows;
        private readonly IDbConnectionFactory _connectionFactory;
        private readonly DashboardConfigService _configService;
        private readonly AuditLogService? _auditLog;
        private readonly ServerConnectionManager _connectionManager;
        private readonly SqlConnectionPoolService? _connectionPool;
        private readonly ResilienceService _resilience;

        public QueryExecutor(IDbConnectionFactory connectionFactory, DashboardConfigService configService, IConfiguration configuration, ServerConnectionManager connectionManager, ResilienceService resilience, SqlConnectionPoolService? connectionPool = null, AuditLogService? auditLog = null)
        {
            _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
            _resilience = resilience ?? throw new ArgumentNullException(nameof(resilience));
            _connectionPool = connectionPool;
            _auditLog = auditLog;
            
            _commandTimeout = configuration.GetValue<int>("QueryTimeoutSeconds", 60);
            _maxRows = configuration.GetValue<int>("MaxQueryRows", 10000);
        }

        /// <summary>
        /// Executes a query by ID with the given filter parameters and returns a DataTable.
        /// Uses native async I/O - no Task.Run() wrapper needed for I/O-bound operations.
        /// </summary>
        public async Task<DataTable> ExecuteQueryAsync(
            string queryId,
            DashboardFilter filter,
            Dictionary<string, object>? additionalParams = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            var sql = _configService.GetQuery(queryId, _connectionFactory.DataSourceType);
            var defaultDatabase = _configService.GetEffectiveDefaultDatabase(queryId);
            
            // Override with CurrentServer.Database if set (for Query Store database selection)
            var currentDb = _connectionManager.CurrentServer?.Database;
            if (!string.IsNullOrEmpty(currentDb) && currentDb != "master")
                defaultDatabase = currentDb;
            
            var dt = new DataTable();
            var startTime = DateTime.UtcNow;

            return await _resilience.SqlPipeline.ExecuteAsync(async ct =>
            {
                using var conn = _connectionFactory is SqlServerConnectionFactory sqlFactory 
                    ? (SqlConnection)sqlFactory.CreateConnection(defaultDatabase)
                    : (SqlConnection)_connectionFactory.CreateConnection();
                await conn.OpenAsync(ct);

                using var cmd = (SqlCommand)conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = _commandTimeout;
                cmd.Connection = conn;

                AddFilterParameters(cmd, filter);

                if (additionalParams != null)
                {
                    foreach (var kvp in additionalParams)
                    {
                        AddParameter(cmd, kvp.Key, kvp.Value);
                    }
                }

                using var reader = await cmd.ExecuteReaderAsync(ct);
                int rowCount = 0;
                dt.BeginLoadData();

                var schemaTable = reader.GetSchemaTable();
                if (schemaTable != null)
                {
                    foreach (DataRow schemaRow in schemaTable.Rows)
                    {
                        dt.Columns.Add(schemaRow["ColumnName"].ToString(), (Type)schemaRow["DataType"]);
                    }
                }

                var fieldCount = reader.FieldCount;
                var pool = ArrayPool<object>.Shared;
                var buffer = pool.Rent(fieldCount);
                try
                {
                    while (await reader.ReadAsync(ct))
                    {
                        if (++rowCount > _maxRows)
                        {
                            dt.EndLoadData();
                            _auditLog?.LogQueryExecution(queryId, (_connectionFactory as SqlServerConnectionFactory)?.ServerName ?? "unknown", false, DateTime.UtcNow - startTime, rowCount, $"Exceeded max rows ({_maxRows})");
                            throw new InvalidOperationException(
                                $"Query '{queryId}' returned more than {_maxRows} rows. Consider adding filters or pagination.");
                        }

                        reader.GetValues(buffer);
                        var row = dt.NewRow();
                        for (int i = 0; i < fieldCount; i++)
                        {
                            row[i] = buffer[i] is DBNull ? DBNull.Value : buffer[i];
                        }
                        dt.Rows.Add(row);
                    }
                }
                finally
                {
                    pool.Return(buffer, clearArray: true);
                }

                dt.EndLoadData();
                _auditLog?.LogQueryExecution(queryId, (_connectionFactory as SqlServerConnectionFactory)?.ServerName ?? "unknown", true, DateTime.UtcNow - startTime, rowCount);
                return dt;
            }, cancellationToken);
        }

        /// <summary>
        /// Executes a query by ID and maps each row to a typed object using the provided mapper function.
        /// </summary>
        public async Task<List<T>> ExecuteQueryAsync<T>(
            string queryId,
            DashboardFilter filter,
            Func<IDataReader, T> mapper,
            Dictionary<string, object>? additionalParams = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            var sql = _configService.GetQuery(queryId, _connectionFactory.DataSourceType);
            var defaultDatabase = _configService.GetEffectiveDefaultDatabase(queryId);
            
            // Override with CurrentServer.Database if set (for Query Store database selection)
            var currentDb = _connectionManager.CurrentServer?.Database;
            if (!string.IsNullOrEmpty(currentDb) && currentDb != "master")
                defaultDatabase = currentDb;
            
            var results = new List<T>();

            using var conn = _connectionFactory is SqlServerConnectionFactory sqlFactory 
                ? (SqlConnection)sqlFactory.CreateConnection(defaultDatabase)
                : (SqlConnection)_connectionFactory.CreateConnection();
            await conn.OpenAsync(cancellationToken);

            using var cmd = (SqlCommand)conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = _commandTimeout;
            cmd.Connection = conn;

            AddFilterParameters(cmd, filter);

            if (additionalParams != null)
            {
                foreach (var kvp in additionalParams)
                {
                    AddParameter(cmd, kvp.Key, kvp.Value);
                }
            }

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(mapper(reader));
            }

            return results;
        }

        /// <summary>
        /// Executes a query and returns a single scalar value.
        /// </summary>
        public async Task<T?> ExecuteScalarAsync<T>(
            string queryId,
            DashboardFilter filter,
            Dictionary<string, object>? additionalParams = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            var sql = _configService.GetQuery(queryId, _connectionFactory.DataSourceType);
            var defaultDatabase = _configService.GetEffectiveDefaultDatabase(queryId);
            
            // Override with CurrentServer.Database if set (for Query Store database selection)
            var currentDb = _connectionManager.CurrentServer?.Database;
            if (!string.IsNullOrEmpty(currentDb) && currentDb != "master")
                defaultDatabase = currentDb;

            using var conn = _connectionFactory is SqlServerConnectionFactory sqlFactory 
                ? (SqlConnection)sqlFactory.CreateConnection(defaultDatabase)
                : (SqlConnection)_connectionFactory.CreateConnection();
            await conn.OpenAsync(cancellationToken);

            using var cmd = (SqlCommand)conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = _commandTimeout;
            cmd.Connection = conn;

            AddFilterParameters(cmd, filter);

            if (additionalParams != null)
            {
                foreach (var kvp in additionalParams)
                {
                    AddParameter(cmd, kvp.Key, kvp.Value);
                }
            }

            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            if (result == null || result == DBNull.Value)
                return default;

            return (T)Convert.ChangeType(result, typeof(T));
        }

        /// <summary>
        /// Returns the distinct sql_instance values recorded in the SQLWATCH snapshot header table.
        /// These are the real @@SERVERNAME values SQLWATCH stored — they may differ from the
        /// user-configured connection names (IP addresses, aliases, FQDNs).
        /// Returns an empty list on any error so callers can fall back to user-configured names.
        /// </summary>
        public async Task<List<string>> GetSqlWatchInstanceNamesAsync(CancellationToken cancellationToken = default)
        {
            const string sql = "SELECT DISTINCT sql_instance FROM dbo.sqlwatch_logger_snapshot_header ORDER BY sql_instance";
            var results = new List<string>();
            try
            {
                using var conn = (SqlConnection)_connectionFactory.CreateConnection();
                await conn.OpenAsync(cancellationToken);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = 10;
                cmd.Connection = conn;
                using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (!reader.IsDBNull(0))
                        results.Add(reader.GetString(0));
                }
            }
            catch (Exception ex)
            {
                // Debug.WriteLine removed - use _auditLog?.LogQueryExecution() for proper logging
            }
            return results;
        }

        /// <summary>
        /// Adds the standard dashboard filter parameters to the command.
        /// @SqlInstance is a comma-separated string used with STRING_SPLIT in SQL Server queries.
        /// </summary>
        private void AddFilterParameters(IDbCommand cmd, DashboardFilter filter)
        {
            AddParameter(cmd, "@TimeFrom", filter.TimeFrom.ToString("yyyy-MM-dd HH:mm:ss"));
            AddParameter(cmd, "@TimeTo", filter.TimeTo.ToString("yyyy-MM-dd HH:mm:ss"));

            // SQL Server queries use STRING_SPLIT(@SqlInstance, ',')
            var joined = filter.Instances.Length > 0 ? string.Join(",", filter.Instances) : "";
            AddParameter(cmd, "@SqlInstance", joined);

            AddParameter(cmd, "@AggMin", filter.AggregationMinutes);
            
            // Add @TopRows parameter for queries that limit results
            AddParameter(cmd, "@TopRows", _maxRows);
        }

        /// <summary>
        /// Adds a parameter to the command in a provider-agnostic way.
        /// </summary>
        private static void AddParameter(IDbCommand cmd, string name, object value)
        {
            var param = cmd.CreateParameter();
            param.ParameterName = name;
            param.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(param);
        }

        // ──────────────── Exception Scrubbing ──────────────

        private static readonly Regex ConnStrPattern = new(
            @"(Password|Pwd|User Id|UID)\s*=\s*[^;]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Scrubs connection strings, passwords, and credentials from exception messages
        /// before they propagate to the UI or log output.
        /// </summary>
        internal static string ScrubExceptionMessage(string? message)
        {
            if (string.IsNullOrEmpty(message)) return message ?? "";
            return ConnStrPattern.Replace(message, "$1=********");
        }

        /// <summary>
        /// Wraps an exception with scrubbed message if it contains credential information.
        /// </summary>
        internal static Exception ScrubException(Exception ex)
        {
            var scrubbed = ScrubExceptionMessage(ex.Message);
            if (scrubbed == ex.Message) return ex;
            return new InvalidOperationException(scrubbed, ex.InnerException);
        }
    }
}
