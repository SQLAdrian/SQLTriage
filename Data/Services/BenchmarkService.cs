/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Threading.Tasks;
using SQLTriage.Data;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// CPU and latency benchmarking service for SQL Server instances.
    /// Runs read-only DMV queries to measure CPU performance, memory latency,
    /// and hypervisor contention indicators.
    /// </summary>
    public class BenchmarkService
    {
        private readonly IDbConnectionFactory _connectionFactory;
        private readonly liveQueriesCacheStore _cacheStore;

        public BenchmarkService(IDbConnectionFactory connectionFactory, liveQueriesCacheStore cacheStore)
        {
            _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _cacheStore = cacheStore ?? throw new ArgumentNullException(nameof(cacheStore));
        }

        /// <summary>
        /// Runs a comprehensive benchmark suite on the specified SQL Server.
        /// Measures CPU arithmetic performance, memory access patterns, and
        /// hypervisor scheduling latency indicators.
        /// </summary>
        public async Task<BenchmarkResult> RunBenchmarkAsync(string serverName, string instanceName = "")
        {
            var result = new BenchmarkResult
            {
                ServerName = serverName,
                InstanceName = instanceName,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                using var conn = (DbConnection)_connectionFactory.CreateConnection();
                await conn.OpenAsync();

                // CPU Integer Arithmetic Benchmark
                result.CpuIntegerBenchmarkMs = await RunCpuIntegerBenchmarkAsync(conn);

                // String Operations Benchmark
                result.StringOpsBenchmarkMs = await RunStringOpsBenchmarkAsync(conn);

                // Memory Access Benchmark (simulated via DMV queries)
                result.MemoryAccessBenchmarkMs = await RunMemoryAccessBenchmarkAsync(conn);

                // Signal Wait Analysis (hypervisor contention indicator)
                var signalWait = await GetSignalWaitPercentageAsync(conn);
                result.SignalWaitPercentage = signalWait;

                // CPU Scheduler Delay Analysis
                result.CpuSchedulerDelayMs = await GetCpuSchedulerDelayAsync(conn);

                // Store results in cache for trending
                await StoreBenchmarkResultsAsync(result);

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        private async Task<double> RunCpuIntegerBenchmarkAsync(DbConnection conn)
        {
            // Simple CPU benchmark using T-SQL arithmetic
            const string sql = @"
                DECLARE @start DATETIME2 = SYSDATETIME();
                DECLARE @i BIGINT = 0;
                DECLARE @sum BIGINT = 0;
                WHILE @i < 1000000
                BEGIN
                    SET @sum = @sum + @i * @i;
                    SET @i = @i + 1;
                END
                SELECT DATEDIFF(MICROSECOND, @start, SYSDATETIME()) / 1000.0 AS DurationMs;
            ";

            using var cmd = (DbCommand)conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 30;

            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToDouble(result ?? 0);
        }

        private async Task<double> RunStringOpsBenchmarkAsync(DbConnection conn)
        {
            // String operations benchmark
            const string sql = @"
                DECLARE @start DATETIME2 = SYSDATETIME();
                DECLARE @i INT = 0;
                DECLARE @str NVARCHAR(MAX) = '';
                WHILE @i < 10000
                BEGIN
                    SET @str = @str + CAST(@i AS NVARCHAR(10)) + ',';
                    SET @i = @i + 1;
                END
                -- Force string processing
                SELECT LEN(REVERSE(@str)) AS Length;
                SELECT DATEDIFF(MICROSECOND, @start, SYSDATETIME()) / 1000.0 AS DurationMs;
            ";

            using var cmd = (DbCommand)conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 30;

            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToDouble(result ?? 0);
        }

        private async Task<double> RunMemoryAccessBenchmarkAsync(DbConnection conn)
        {
            // Memory access pattern simulation via DMV queries
            const string sql = @"
                DECLARE @start DATETIME2 = SYSDATETIME();

                -- Force memory access patterns by querying large DMVs
                SELECT COUNT(*) FROM sys.dm_exec_query_stats WITH (NOLOCK);
                SELECT COUNT(*) FROM sys.dm_os_memory_clerks WITH (NOLOCK);
                SELECT COUNT(*) FROM sys.dm_exec_connections WITH (NOLOCK);

                -- Simple aggregation to ensure processing
                SELECT
                    SUM(execution_count) as total_execs,
                    AVG(total_worker_time) as avg_worker_time,
                    MAX(last_execution_time) as last_exec
                FROM sys.dm_exec_query_stats WITH (NOLOCK)
                WHERE execution_count > 0;

                SELECT DATEDIFF(MICROSECOND, @start, SYSDATETIME()) / 1000.0 AS DurationMs;
            ";

            using var cmd = (DbCommand)conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 30;

            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToDouble(result ?? 0);
        }

        private async Task<double> GetSignalWaitPercentageAsync(DbConnection conn)
        {
            // Signal wait time indicates CPU scheduling delays (hypervisor contention)
            const string sql = @"
                SELECT
                    CASE
                        WHEN SUM(signal_wait_time_ms) > 0 AND SUM(wait_time_ms) > 0
                        THEN (SUM(signal_wait_time_ms) * 100.0) / SUM(wait_time_ms)
                        ELSE 0
                    END AS SignalWaitPercentage
                FROM sys.dm_os_wait_stats WITH (NOLOCK)
                WHERE wait_type NOT IN (
                    'BROKER_EVENTHANDLER', 'BROKER_RECEIVE_WAITFOR',
                    'BROKER_TASK_STOP', 'BROKER_TO_FLUSH',
                    'BROKER_TRANSMITTER', 'CHECKPOINT_QUEUE',
                    'CHKPT', 'CLR_AUTO_EVENT', 'CLR_MANUAL_EVENT',
                    'CLR_SEMAPHORE', 'DBMIRROR_DBM_EVENT',
                    'DBMIRROR_EVENTS_QUEUE', 'DBMIRROR_WORKER_QUEUE',
                    'DBMIRRORING_CMD', 'DIRTY_PAGE_POLL',
                    'DISPATCHER_QUEUE_SEMAPHORE', 'EXECSYNC',
                    'FSAGENT', 'FT_IFTS_SCHEDULER_IDLE_WAIT',
                    'FT_IFTSHC_MUTEX', 'HADRSIMPLIFIEr_STATE_SYNC',
                    'KSOURCE_WAKEUP', 'LAZYWRITER_SLEEP',
                    'LOGMGR_QUEUE', 'MEMORY_ALLOCATION_EXT',
                    'ONDEMAND_TASK_QUEUE', 'PREEMPTIVE_XE_CALLBACKEXECUTE',
                    'PREEMPTIVE_XE_DISPATCHER', 'PREEMPTIVE_XE_GETTARGETSTATE',
                    'PREEMPTIVE_XE_SESSIONCOMMIT', 'PREEMPTIVE_XE_TARGETFINALIZE',
                    'PREEMPTIVE_XE_TARGETINIT', 'PREEMPTIVE_XE_TIMEREVENT',
                    'PWAIT_ALL_COMPONENTS_INITIALIZED', 'PWAIT_DIRECTLOGCONSUMER_GETNEXT',
                    'QDS_PERSIST_TASK_MAIN_LOOP_SLEEP', 'QDS_ASYNC_QUEUE',
                    'QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP',
                    'REQUEST_FOR_DEADLOCK_SEARCH', 'RESOURCE_QUEUE',
                    'SERVER_IDLE_CHECK', 'SLEEP_BPOOL_FLUSH',
                    'SLEEP_DBSTARTUP', 'SLEEP_DCOMSTARTUP',
                    'SLEEP_MASTERDBREADY', 'SLEEP_MASTERMDREADY',
                    'SLEEP_MASTERUPGRADED', 'SLEEP_MSDBSTARTUP',
                    'SLEEP_SYSTEMTASK', 'SLEEP_TASK',
                    'SLEEP_TEMPDBSTARTUP', 'SNIA_EXTERNAL_RPC_CALL',
                    'SP_SERVER_DIAGNOSTICS_SLEEP', 'SQLTRACE_BUFFER_FLUSH',
                    'SQLTRACE_INCREMENTAL_FLUSH_SLEEP', 'SQLTRACE_WAIT_ENTRIES',
                    'WAIT_FOR_RESULTS', 'WAITFOR', 'WAITFOR_TASKSHUTDOWN',
                    'WAIT_XTP_HOST_WAIT', 'WAIT_XTP_RECOVERY',
                    'WAIT_XTP_WAIT', 'XE_DISPATCHER_JOIN',
                    'XE_DISPATCHER_WAIT', 'XE_LIVE_TARGET_TVF',
                    'XE_TIMER_EVENT'
                );
            ";

            using var cmd = (DbCommand)conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 30;

            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToDouble(result ?? 0);
        }

        private async Task<double> GetCpuSchedulerDelayAsync(DbConnection conn)
        {
            // Measure CPU scheduler responsiveness
            const string sql = @"
                SELECT
                    AVG(CASE WHEN sosr.runnable_tasks_count > 0 THEN 1.0 ELSE 0 END) * 100 AS AvgRunnableTasks,
                    AVG(sosr.signal_wait_time_ms) AS AvgSignalWaitMs
                FROM sys.dm_os_schedulers AS sos WITH (NOLOCK)
                LEFT JOIN sys.dm_os_scheduler_runnable_tasks AS sosr WITH (NOLOCK)
                    ON sos.scheduler_id = sosr.scheduler_id
                WHERE sos.status = 'VISIBLE ONLINE';
            ";

            using var cmd = (DbCommand)conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 30;

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var avgRunnableTasks = Convert.ToDouble(reader[0] ?? 0);
                var avgSignalWaitMs = Convert.ToDouble(reader[1] ?? 0);
                return avgSignalWaitMs; // Return signal wait as primary indicator
            }

            return 0;
        }

        private async Task StoreBenchmarkResultsAsync(BenchmarkResult result)
        {
            // Store benchmark results in SQLite cache for historical trending
            var statValues = new Dictionary<string, object>
            {
                ["cpu_integer_benchmark_ms"] = result.CpuIntegerBenchmarkMs,
                ["string_ops_benchmark_ms"] = result.StringOpsBenchmarkMs,
                ["memory_access_benchmark_ms"] = result.MemoryAccessBenchmarkMs,
                ["signal_wait_percentage"] = result.SignalWaitPercentage,
                ["cpu_scheduler_delay_ms"] = result.CpuSchedulerDelayMs
            };

            foreach (var kvp in statValues)
            {
                var statValue = new StatValue
                {
                    Label = kvp.Key,
                    Value = Convert.ToDouble(kvp.Value),
                    Unit = "",
                    Color = "#4caf50",
                    Instance = result.InstanceName
                };
                await _cacheStore.UpsertStatValueAsync(
                    result.ServerName,
                    result.InstanceName,
                    statValue,
                    result.Timestamp);
            }
        }
    }

    /// <summary>
    /// Result of a benchmark run
    /// </summary>
    public class BenchmarkResult
    {
        public string ServerName { get; set; } = "";
        public string InstanceName { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }

        // Benchmark measurements
        public double CpuIntegerBenchmarkMs { get; set; }
        public double StringOpsBenchmarkMs { get; set; }
        public double MemoryAccessBenchmarkMs { get; set; }
        public double SignalWaitPercentage { get; set; }
        public double CpuSchedulerDelayMs { get; set; }

        // Computed ratings
        public string CpuRating => CpuIntegerBenchmarkMs switch
        {
            var x when x < 100 => "Fast",
            var x when x < 500 => "Normal",
            _ => "Degraded"
        };

        public string StringOpsRating => StringOpsBenchmarkMs switch
        {
            var x when x < 200 => "Fast",
            var x when x < 1000 => "Normal",
            _ => "Degraded"
        };

        public string HypervisorRating => SignalWaitPercentage switch
        {
            var x when x < 5 => "Low Contention",
            var x when x < 25 => "Moderate Contention",
            _ => "High Contention (Possible Hypervisor Issue)"
        };
    }
}