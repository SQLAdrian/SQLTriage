/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Data;
using System.Threading.Tasks;
using System.Collections.Generic;
using SQLTriage.Data.Models;
using Microsoft.Data.SqlClient;

namespace SQLTriage.Data
{
    /// <summary>
    /// Service for checking server health status.
    /// Maintains a persistent dictionary of <see cref="ServerHealthStatus"/> objects
    /// so that delta-tracked counters (deadlocks) survive across refresh cycles.
    /// </summary>
    public class HealthCheckService
    {
        private readonly IDbConnectionFactory _connectionFactory;

        /// <summary>
        /// Persistent health state per server name. The same object is reused across
        /// calls so that deadlock delta tracking works correctly.
        /// </summary>
        private readonly ConcurrentDictionary<string, ServerHealthStatus> _healthByServer = new(
            StringComparer.OrdinalIgnoreCase);

        public HealthCheckService(IDbConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        /// <summary>
        /// Returns the persisted health status for a server, or null if never checked.
        /// </summary>
        public ServerHealthStatus? GetCachedHealth(string serverName)
        {
            _healthByServer.TryGetValue(serverName, out var status);
            return status;
        }

        /// <summary>
        /// Returns all currently tracked server health statuses.
        /// </summary>
        public Dictionary<string, ServerHealthStatus> GetAllHealth()
        {
            return new Dictionary<string, ServerHealthStatus>(_healthByServer);
        }

        /// <summary>
        /// Returns a simple health summary suitable for K8s liveness/readiness probes.
        /// </summary>
        public (bool IsHealthy, string Status, Dictionary<string, bool> ServerStatus) GetHealthSummary()
        {
            var serverStatus = new Dictionary<string, bool>();
            var allHealthy = true;

            foreach (var kvp in _healthByServer)
            {
                var isOnline = (kvp.Value.IsOnline ?? false) && !kvp.Value.IsLoading;
                serverStatus[kvp.Key] = isOnline;
                if (!isOnline) allHealthy = false;
            }

            var status = allHealthy ? "Healthy" : "Degraded";
            if (_healthByServer.IsEmpty)
                status = "No servers configured";

            return (allHealthy, status, serverStatus);
        }

        public async Task<ServerHealthStatus> GetHealthStatusAsync(string serverName)
        {
            // Reuse existing object so deadlock delta tracking is preserved
            var health = _healthByServer.GetOrAdd(serverName, name => new ServerHealthStatus
            {
                ServerId = name,
                ServerName = name
            });

            health.IsLoading = true;
            health.LastUpdated = DateTime.Now;

            try
            {
                using var connection = await _connectionFactory.CreateConnectionAsync();
                var sqlConnection = (SqlConnection)connection;

                health.IsOnline = true;

                // Get memory info
                var memoryQuery = @"
                    SELECT
                        (SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name = 'Buffer Pool Size (KB)' AND object_name LIKE '%Buffer Manager%') / 1024 as BufferPoolMB,
                        (SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name = 'Granted Workspace Memory (KB)' AND object_name LIKE '%SQLServer:Memory Manager%') / 1024 as GrantedMemoryMB,
                        (SELECT count(*) FROM sys.dm_os_waiting_tasks WHERE wait_type LIKE 'RESOURCE_SEMAPHORE_QUERY%') as RequestsWaiting";

                // Get blocking info
                var blockingQuery = @"
                    SELECT
                        (SELECT COUNT(*) FROM sys.dm_tran_locks WHERE request_status = 'WAIT') as TotalBlocked,
                        ISNULL((SELECT MAX(wait_time) FROM sys.dm_exec_requests WHERE blocking_session_id > 0) / 1000, 0) as LongestBlockedSeconds";

                // Get thread info
                var threadQuery = @"
                    SELECT
                        (SELECT desired_threads FROM sys.dm_os_schedulers WHERE status = 'VISIBLE ONLINE') as TotalThreads,
                        (SELECT active_worker_count FROM sys.dm_os_schedulers WHERE status = 'VISIBLE ONLINE') as AvailableThreads,
                        (SELECT count(*) FROM sys.dm_os_workers where state = 'RUNNABLE') as ThreadsWaitingForCpu,
                        (SELECT count(*) FROM sys.dm_exec_requests where blocking_session_id = 0 and status = 'suspended') as RequestsWaitingForThreads";

                // Get deadlock count
                var deadlockQuery = @"
                    SELECT ISNULL((
                        SELECT SUM(cntr_value)
                        FROM sys.dm_os_performance_counters
                        WHERE counter_name = 'Number of Deadlocks/sec'
                        AND object_name LIKE '%_Transactions'
                    ), 0)";

                // Get top wait
                var waitQuery = @"
                    SELECT TOP 1
                        wait_type,
                        wait_time / 1000.0 as wait_time_seconds
                    FROM sys.dm_os_wait_stats
                    WHERE wait_type NOT IN ('CLR_SEMAPHORE','LAZYWRITER_SLEEP','RESOURCE_QUEUE','SLEEP_TASK','SLEEP_SYSTEMTASK','SQLTRACE_BUFFER_FLUSH','WAITFOR','DISPATCHER_QUEUE_SEMAPHORE')
                    ORDER BY wait_time DESC";

                // Last blocking event (from SQLWATCH if available)
                var lastBlockingQuery = @"
                    SELECT DATEDIFF(MINUTE, MAX(r.snapshot_time), GETUTCDATE())
                    FROM [dbo].[sqlwatch_logger_perf_os_wait_stats] r
                    WHERE r.wait_type LIKE 'LCK%'
                      AND r.waiting_tasks_count > 0";

                // Last deadlock event (from SQLWATCH if available)
                var lastDeadlockQuery = @"
                    SELECT DATEDIFF(MINUTE, MAX(r.snapshot_time), GETUTCDATE())
                    FROM [dbo].[sqlwatch_logger_xes_deadlock] r";

                // Execute queries and populate health
                using var cmd = (SqlCommand)connection.CreateCommand();
                cmd.CommandTimeout = 10;

                // Memory
                cmd.CommandText = memoryQuery;
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        if (!reader.IsDBNull(0)) health.BufferPoolMb = reader.GetDecimal(0);
                        if (!reader.IsDBNull(1)) health.GrantedMemoryMb = reader.GetDecimal(1);
                        if (!reader.IsDBNull(2)) health.RequestsWaitingForMemory = reader.GetInt32(2);
                    }
                }

                // Blocking
                cmd.CommandText = blockingQuery;
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        if (!reader.IsDBNull(0)) health.TotalBlocked = reader.GetInt64(0);
                        if (!reader.IsDBNull(1)) health.LongestBlockedSeconds = reader.GetDecimal(1);
                    }
                }

                // Threads
                cmd.CommandText = threadQuery;
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        if (!reader.IsDBNull(0)) health.TotalThreads = reader.GetInt32(0);
                        if (!reader.IsDBNull(1)) health.AvailableThreads = reader.GetInt32(1);
                        if (!reader.IsDBNull(2)) health.ThreadsWaitingForCpu = reader.GetInt32(2);
                        if (!reader.IsDBNull(3)) health.RequestsWaitingForThreads = reader.GetInt32(3);
                    }
                }

                // Deadlocks - cumulative counter; delta tracked by ServerHealthStatus
                cmd.CommandText = deadlockQuery;
                var deadlockResult = await cmd.ExecuteScalarAsync();
                if (deadlockResult != null && deadlockResult != DBNull.Value)
                {
                    health.DeadlockCount = Convert.ToInt64(deadlockResult);
                }

                // Top Wait
                cmd.CommandText = waitQuery;
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        if (!reader.IsDBNull(0)) health.TopWaitType = reader.GetString(0);
                        if (!reader.IsDBNull(1)) health.TopWaitDurationSeconds = reader.GetDecimal(1);
                    }
                }

                // CPU
                var cpuSimpleQuery = @"
                    SELECT
                        (SELECT SUM(cpu_time) FROM sys.dm_exec_requests) / 1000 as CpuSeconds,
                        (SELECT count(*) FROM sys.dm_exec_requests) as RequestCount";
                cmd.CommandText = cpuSimpleQuery;
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        var requestCount = !reader.IsDBNull(1) ? reader.GetInt32(1) : 0;
                        health.CpuPercent = Math.Min(100, requestCount * 5);
                    }
                }

                // Last blocking / deadlock from SQLWATCH tables (best-effort)
                await PopulateLastSeenAsync(cmd, health, lastBlockingQuery, lastDeadlockQuery);

                health.IsOnline = true;
                health.ErrorMessage = null;
            }
            catch (SqlException ex)
            {
                health.IsOnline = false;
                health.ErrorMessage = ex.Message;
            }
            catch (Exception ex)
            {
                health.ErrorMessage = ex.Message;
            }
            finally
            {
                health.IsLoading = false;
                health.LastUpdated = DateTime.Now;
            }

            return health;
        }

        /// <summary>
        /// Queries SQLWATCH tables for the "last seen" timestamps for blocking and deadlocks.
        /// These are best-effort: if the SQLWATCH tables don't exist, we silently skip.
        /// </summary>
        private static async Task PopulateLastSeenAsync(
            SqlCommand cmd, ServerHealthStatus health,
            string lastBlockingQuery, string lastDeadlockQuery)
        {
            // Last blocking
            try
            {
                cmd.CommandText = lastBlockingQuery;
                var result = await cmd.ExecuteScalarAsync();
                if (result != null && result != DBNull.Value)
                    health.LastBlockingMinutesAgo = Convert.ToInt32(result);
            }
            catch
            {
                // SQLWATCH tables may not exist - silently skip
            }

            // Last deadlock
            try
            {
                cmd.CommandText = lastDeadlockQuery;
                var result = await cmd.ExecuteScalarAsync();
                if (result != null && result != DBNull.Value)
                    health.LastDeadlockMinutesAgo = Convert.ToInt32(result);
            }
            catch
            {
                // SQLWATCH tables may not exist - silently skip
            }
        }
    }
}
