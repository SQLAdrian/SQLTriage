/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

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
        private readonly ILogger<HealthCheckService>? _logger;

        /// <summary>
        /// Persistent health state per server name. The same object is reused across
        /// calls so that deadlock delta tracking works correctly.
        /// </summary>
        private readonly ConcurrentDictionary<string, ServerHealthStatus> _healthByServer = new(
            StringComparer.OrdinalIgnoreCase);

        public HealthCheckService(IDbConnectionFactory connectionFactory, ILogger<HealthCheckService>? logger = null)
        {
            _connectionFactory = connectionFactory;
            _logger = logger;
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

        /// <summary>
        /// Polls live DMVs for one server and updates its persisted <see cref="ServerHealthStatus"/>.
        /// </summary>
        /// <param name="serverName">The server/instance name to poll (used both as the connection's
        /// data source and as the key into the persisted health dictionary).</param>
        /// <param name="connection">
        /// The <see cref="ServerConnection"/> that owns <paramref name="serverName"/> (per-server
        /// credentials/auth). REQUIRED for a multi-server estate — the arg-less
        /// <see cref="_connectionFactory"/> fallback below has no server argument, so calling it
        /// for two different servers would silently query the SAME connection twice and only
        /// label the results differently (#57, 2026-07-15: this was the dashboard's dead-and-wrong
        /// bug). Pass <paramref name="connection"/> whenever the caller knows which
        /// <see cref="ServerConnection"/> owns the server; the parameterless single-instance
        /// fallback exists only for callers with no multi-server topology configured.
        /// </param>
        public async Task<ServerHealthStatus> GetHealthStatusAsync(string serverName, ServerConnection? connection = null)
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
                using IDbConnection conn = connection != null
                    ? await OpenPerServerConnectionAsync(connection, serverName)
                    : await _connectionFactory.CreateConnectionAsync();

                health.IsOnline = true;

                // Get memory info
                // #57 live self-check (2026-07-15) found 'Buffer Pool Size (KB)' does not exist as a
                // perf counter on SQL 2017/2022/2025 (verified live against all three test instances —
                // it returned NULL on every one, so BufferPoolMb silently never populated). Replaced
                // with the standard modern substitute: 'Database pages' (8KB each) from the same
                // Buffer Manager object — this is the documented technique for buffer-pool size once
                // the old counter was retired, not an invented number.
                // ALSO: sys.dm_os_performance_counters.cntr_value is bigint, so the raw arithmetic
                // below stayed bigint — reader.GetDecimal() below then threw "Unable to cast object of
                // type 'System.Int64' to type 'System.Decimal'" on every single live call (caught by
                // the generic catch further down, aborting the ENTIRE batch — blocking/threads/
                // deadlocks/wait/CPU never ran either). This was invisible before #57 because
                // GetHealthStatusAsync had zero callers; live-probing surfaced it immediately. Fixed
                // by explicitly converting to decimal in T-SQL to match what the reader expects.
                var memoryQuery = @"
                    SELECT
                        CONVERT(decimal(18,2), (SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name = 'Database pages' AND object_name LIKE '%Buffer Manager%') * 8 / 1024) as BufferPoolMB,
                        CONVERT(decimal(18,2), (SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name = 'Granted Workspace Memory (KB)' AND object_name LIKE '%SQLServer:Memory Manager%') / 1024) as GrantedMemoryMB,
                        (SELECT count(*) FROM sys.dm_os_waiting_tasks WHERE wait_type LIKE 'RESOURCE_SEMAPHORE_QUERY%') as RequestsWaiting";

                // Get blocking info
                // #57 live self-check: the same bigint/decimal mismatch as above hits LongestBlockedSeconds
                // (reader.GetDecimal) and the same int/bigint mismatch hits TotalBlocked (reader.GetInt64
                // against a plain COUNT(*), which is int) — COUNT_BIG(*) and "/ 1000.0" (decimal literal,
                // not the int literal 1000) fix both without touching the C# reader code.
                var blockingQuery = @"
                    SELECT
                        (SELECT COUNT_BIG(*) FROM sys.dm_tran_locks WHERE request_status = 'WAIT') as TotalBlocked,
                        ISNULL((SELECT MAX(wait_time) FROM sys.dm_exec_requests WHERE blocking_session_id > 0) / 1000.0, 0) as LongestBlockedSeconds";

                // Get thread info
                // #57 live self-check (2026-07-15): 'desired_threads' and 'active_worker_count' are
                // NOT real sys.dm_os_schedulers columns on any of the 3 test instances (2017/2022/2025)
                // — SqlException "Invalid column name" on every one, which used to abort this whole
                // batch partway through and, worse, get caught by the outer catch(SqlException) and
                // falsely flip IsOnline=false for a perfectly healthy, reachable server. Real columns:
                // max_workers_count (server-wide worker ceiling, sys.dm_os_sys_info) for TotalThreads,
                // and current_workers_count - active_workers_count (idle/free workers per scheduler)
                // for AvailableThreads — verified live to return real, differing values per server.
                var threadQuery = @"
                    SELECT
                        (SELECT max_workers_count FROM sys.dm_os_sys_info) as TotalThreads,
                        (SELECT SUM(current_workers_count - active_workers_count) FROM sys.dm_os_schedulers WHERE status = 'VISIBLE ONLINE') as AvailableThreads,
                        (SELECT count(*) FROM sys.dm_os_workers where state = 'RUNNABLE') as ThreadsWaitingForCpu,
                        (SELECT count(*) FROM sys.dm_exec_requests where blocking_session_id = 0 and status = 'suspended') as RequestsWaitingForThreads";

                // Get deadlock count
                // #57 gate fix (2026-07-15): 'Number of Deadlocks/sec' does not exist under any
                // '%_Transactions' object on SQL 2017/2022/2025 (verified live: forced a real
                // deadlock on .\OLD2017, Msg 1205 victim; sqlcmd confirmed the counter exists only
                // under the Locks object). The old filter matched zero rows, so SUM returned NULL,
                // ISNULL coerced to 0, and DeadlockCount could never move even with a live deadlock
                // sitting in the DMV. The counter lives under object_name '...:Locks', instance_name
                // '_Total' — read that single row's cntr_value directly. Do NOT SUM across the Locks
                // object's rows: it also has per-lock-type rows (RID, Key, Page, Object, Extent,
                // Database, ...) alongside _Total, so a SUM would double-count.
                // NOTE: object_name is a fixed-width nchar column padded with trailing spaces, and
                // LIKE (unlike =) does NOT ignore trailing padding — '%:Locks' with no trailing
                // wildcard matched zero rows live (re-gate caught this). Trailing '%' is required.
                var deadlockQuery = @"
                    SELECT ISNULL((
                        SELECT cntr_value
                        FROM sys.dm_os_performance_counters
                        WHERE counter_name = 'Number of Deadlocks/sec'
                        AND object_name LIKE '%:Locks%'
                        AND instance_name = '_Total'
                    ), 0)";

                // Get top wait
                // #57 live self-check (2026-07-15): sys.dm_os_wait_stats has no 'wait_time' column —
                // the real column is 'wait_time_ms' (confirmed live on all 3 test instances; the
                // original also threw "Invalid column name", aborting the batch the same way the
                // thread query did above).
                var waitQuery = @"
                    SELECT TOP 1
                        wait_type,
                        wait_time_ms / 1000.0 as wait_time_seconds
                    FROM sys.dm_os_wait_stats
                    WHERE wait_type NOT IN ('CLR_SEMAPHORE','LAZYWRITER_SLEEP','RESOURCE_QUEUE','SLEEP_TASK','SLEEP_SYSTEMTASK','SQLTRACE_BUFFER_FLUSH','WAITFOR','DISPATCHER_QUEUE_SEMAPHORE')
                    ORDER BY wait_time_ms DESC";

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
                using var cmd = (SqlCommand)conn.CreateCommand();
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

                // CPU — REAL per-server SQL Server process CPU%, read from the
                // SQLProcessUtilization ring buffer (same technique as PowerEstimateService.
                // ProbeAsync). This REPLACES a fabricated `requestCount * 5` proxy that used to
                // sit here (#57, 2026-07-15, DD-sacred: a DBA must never be shown an invented
                // CPU number). Requires VIEW SERVER STATE. Best-effort: if the ring buffer has
                // no data yet (e.g. instance just started) or the read fails (permissions), the
                // existing value is left untouched — null on a first read renders "--" via
                // CpuDisplayText; NEVER a fake fallback.
                var cpuRingBufferQuery = @"
                    ;WITH rb AS (
                        SELECT CONVERT(xml, record) AS rec, [timestamp]
                        FROM sys.dm_os_ring_buffers
                        WHERE ring_buffer_type = N'RING_BUFFER_SCHEDULER_MONITOR'
                    )
                    SELECT TOP 1
                        rec.value('(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]', 'int') AS sql_pct
                    FROM rb
                    ORDER BY [timestamp] DESC";
                try
                {
                    cmd.CommandText = cpuRingBufferQuery;
                    var cpuResult = await cmd.ExecuteScalarAsync();
                    if (cpuResult != null && cpuResult != DBNull.Value)
                        health.CpuPercent = Convert.ToInt32(cpuResult);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "CPU ring-buffer read failed for {Server} — leaving CpuPercent as last-known/unset, never fabricated", serverName);
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
        /// Opens a connection to <paramref name="serverName"/> using <paramref name="connection"/>'s
        /// own auth/credentials — the same per-server pattern <c>AlertEvaluationService</c> and
        /// <c>BlockingForensicsService</c> use (<c>connection.GetConnectionString(serverName, db)</c>),
        /// never the arg-less single-server factory.
        /// </summary>
        private static async Task<IDbConnection> OpenPerServerConnectionAsync(ServerConnection connection, string serverName)
        {
            var connStr = connection.GetConnectionString(serverName, "master");
            var sqlConn = new SqlConnection(connStr);
            await sqlConn.OpenAsync();
            return sqlConn;
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
