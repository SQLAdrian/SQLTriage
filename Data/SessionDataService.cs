/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
// Required for SqlServerConnectionFactory.CreateConnection(initialDatabase)
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;

namespace SQLTriage.Data
{
    /// <summary>
    /// Real-time session data service that queries sys.dm_exec_sessions and
    /// sys.dm_exec_requests directly against the currently selected SQL Server.
    /// This bypasses CachingQueryExecutor — sessions are always live, never cached.
    /// Prefetch cache: unfiltered results are pre-warmed so the Live Monitor page
    /// loads instantly instead of waiting for the first SQL round-trip.
    /// </summary>
    public class SessionDataService
    {
        private readonly IDbConnectionFactory _connectionFactory;
        private readonly BlockingHistoryService? _blockingHistory;

        // Prefetch cache — keyed by server name (empty string = default connection).
        // Stores the last unfiltered result and its age. Max 60s stale before ignored.
        private readonly ConcurrentDictionary<string, (List<SessionInfo> Sessions, DateTime FetchedAt)> _prefetchCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _prefetchLocks = new(StringComparer.OrdinalIgnoreCase);
        private const int PrefetchMaxAgeSeconds = 60;

        private string BuildLiveSessionsQuery(int topCount, bool hideSleeping, bool onlyBlocked, bool hideLowIO, string searchText = "")
        {
            var conditions = new List<string> { "s.is_user_process = 1" };

            if (hideSleeping)
                conditions.Add("s.status <> 'sleeping'");

            if (onlyBlocked)
                conditions.Add("(r.blocking_session_id > 0 OR EXISTS (SELECT 1 FROM sys.dm_exec_requests br WITH (NOLOCK) WHERE br.blocking_session_id = s.session_id))");

            if (!string.IsNullOrWhiteSpace(searchText))
                conditions.Add("(s.login_name LIKE '%' + @SearchText + '%' OR s.host_name LIKE '%' + @SearchText + '%' OR s.program_name LIKE '%' + @SearchText + '%' OR DB_NAME(s.database_id) LIKE '%' + @SearchText + '%')");

            var whereClause = conditions.Any() ? "WHERE " + string.Join(" AND ", conditions) : "";

            // ENHANCEMENT: Added memory_usage and row_count (aliased without brackets to avoid parser confusion)
            return $@"
SELECT TOP ({topCount})
    s.session_id AS SPID,
    s.login_name AS LoginName,
    ISNULL(s.host_name, '') AS HostName,
    ISNULL(DB_NAME(s.database_id), '') AS DatabaseName,
    s.status AS SessionStatus,
    s.cpu_time AS CpuTime,
    s.reads AS LogicalReads,
    s.writes AS Writes,
    s.open_transaction_count AS OpenTransactionCount,
    r.status AS RequestStatus,
    r.command AS Command,
    r.wait_type AS WaitType,
    ISNULL(r.wait_time, 0) AS WaitTime,
    ISNULL(r.blocking_session_id, 0) AS BlockingSessionId,
    ISNULL(r.total_elapsed_time, 0) AS TotalElapsedTime,
    ISNULL(s.program_name, '') AS ProgramName,
    t.text AS QueryText,
    s.memory_usage AS MemoryUsageKB,
    ISNULL(r.row_count, 0) AS [RowCount]
FROM sys.dm_exec_sessions s WITH (NOLOCK)
LEFT JOIN sys.dm_exec_requests r WITH (NOLOCK)
    ON s.session_id = r.session_id
OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
{whereClause}
ORDER BY s.cpu_time + s.reads DESC";
        }

        public SessionDataService(
            IDbConnectionFactory connectionFactory,
            BlockingHistoryService? blockingHistory = null)
        {
            _connectionFactory = connectionFactory;
            _blockingHistory = blockingHistory;
        }

        /// <summary>
        /// Pre-warms the session cache for the given server so the first page load is instant.
        /// Safe to call fire-and-forget. No-ops if a prefetch is already in progress for this server.
        /// </summary>
        public async Task PrefetchAsync(string serverName = "")
        {
            var lockObj = _prefetchLocks.GetOrAdd(serverName, _ => new SemaphoreSlim(1, 1));
            if (!await lockObj.WaitAsync(0))
                return; // already prefetching — skip

            try
            {
                var sessions = await FetchSessionsAsync(100, false, false, false, "");
                _prefetchCache[serverName] = (sessions, DateTime.UtcNow);
            }
            catch
            {
                // Prefetch failures are silent — page will just do a live fetch
            }
            finally
            {
                lockObj.Release();
            }
        }

        /// <summary>
        /// Fetches all live user sessions from the currently connected SQL Server instance.
        /// Returns prefetched data immediately (if fresh) and kicks off a background refresh.
        /// Uses master database for live session DMVs which are server-scoped.
        /// ENHANCEMENT: Added server-side filtering, memory usage, row count, and search.
        /// </summary>
        public async Task<List<SessionInfo>> GetLiveSessionsAsync(
            int topCount = 100,
            bool hideSleeping = false,
            bool onlyBlocked = false,
            bool hideLowIO = false,
            string searchText = "")
        {
            // For the default unfiltered query, return cached data instantly if fresh,
            // then schedule a background refresh to update the cache for next time.
            bool isDefaultQuery = !hideSleeping && !onlyBlocked && !hideLowIO && string.IsNullOrEmpty(searchText) && topCount == 100;
            if (isDefaultQuery && _prefetchCache.TryGetValue("", out var cached)
                && (DateTime.UtcNow - cached.FetchedAt).TotalSeconds <= PrefetchMaxAgeSeconds)
            {
                // Return cached data immediately; refresh in background for next call
                _ = Task.Run(async () => await PrefetchAsync(""));
                return cached.Sessions;
            }

            return await FetchSessionsAsync(topCount, hideSleeping, onlyBlocked, hideLowIO, searchText);
        }

        private async Task<List<SessionInfo>> FetchSessionsAsync(
            int topCount, bool hideSleeping, bool onlyBlocked, bool hideLowIO, string searchText)
        {
            var sessions = new List<SessionInfo>();

            using var conn = _connectionFactory is SqlServerConnectionFactory sqlFactory
                ? (SqlConnection)sqlFactory.CreateConnection("master")
                : (SqlConnection)_connectionFactory.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = BuildLiveSessionsQuery(topCount, hideSleeping, onlyBlocked, hideLowIO, searchText);
            cmd.CommandTimeout = 30;

            if (!string.IsNullOrWhiteSpace(searchText))
            {
                var searchParam = cmd.CreateParameter();
                searchParam.ParameterName = "@SearchText";
                searchParam.Value = searchText;
                cmd.Parameters.Add(searchParam);
            }

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                sessions.Add(new SessionInfo
                {
                    SPID = reader.GetInt16(reader.GetOrdinal("SPID")),
                    LoginName = reader.GetString(reader.GetOrdinal("LoginName")),
                    HostName = reader.GetString(reader.GetOrdinal("HostName")),
                    DatabaseName = reader.GetString(reader.GetOrdinal("DatabaseName")),
                    SessionStatus = reader.GetString(reader.GetOrdinal("SessionStatus")),
                    CpuTime = reader.GetInt32(reader.GetOrdinal("CpuTime")),
                    LogicalReads = reader.GetInt64(reader.GetOrdinal("LogicalReads")),
                    Writes = reader.GetInt64(reader.GetOrdinal("Writes")),
                    OpenTransactionCount = reader.GetInt32(reader.GetOrdinal("OpenTransactionCount")),
                    RequestStatus = reader.IsDBNull(reader.GetOrdinal("RequestStatus")) ? null : reader.GetString(reader.GetOrdinal("RequestStatus")),
                    Command = reader.IsDBNull(reader.GetOrdinal("Command")) ? null : reader.GetString(reader.GetOrdinal("Command")),
                    WaitType = reader.IsDBNull(reader.GetOrdinal("WaitType")) ? null : reader.GetString(reader.GetOrdinal("WaitType")),
                    WaitTime = reader.GetInt32(reader.GetOrdinal("WaitTime")),
                    BlockingSessionId = reader.GetInt16(reader.GetOrdinal("BlockingSessionId")),
                    TotalElapsedTime = reader.GetInt32(reader.GetOrdinal("TotalElapsedTime")),
                    ProgramName = reader.GetString(reader.GetOrdinal("ProgramName")),
                    QueryText = reader.IsDBNull(reader.GetOrdinal("QueryText")) ? null : reader.GetString(reader.GetOrdinal("QueryText")),
                    MemoryUsageKB = reader.GetInt32(reader.GetOrdinal("MemoryUsageKB")),
                    RowCount = reader.GetInt64(reader.GetOrdinal("RowCount"))
                });
            }

            return sessions;
        }

        /// <summary>
        /// Gets detailed blocking information from sys.dm_os_waiting_tasks for more accurate chain.
        /// When <paramref name="serverName"/> is supplied and <see cref="BlockingHistoryService"/>
        /// is registered, each pair is persisted for forensics (noise threshold: 5 s).
        /// </summary>
        public async Task<List<BlockingInfo>> GetBlockingChainAsync(string? serverName = null)
        {
            var blockers = new List<BlockingInfo>();

            // Use SqlConnection for async support
            using var conn = await _connectionFactory.CreateConnectionAsync();
            if (conn is not SqlConnection sqlConn)
                throw new InvalidOperationException("GetBlockingChainAsync requires a SQL Server connection.");

            await sqlConn.OpenAsync();
            sqlConn.ChangeDatabase("master");
            using var cmd = sqlConn.CreateCommand();
            cmd.CommandText = @"
                SELECT
                    wt.blocking_session_id          AS BlockingSPID,
                    wt.session_id                   AS BlockedSPID,
                    wt.wait_duration_ms             AS WaitDurationMs,
                    wt.wait_type                    AS WaitType,
                    s_blocker.login_name            AS BlockerLogin,
                    s_blocked.login_name            AS BlockedLogin,
                    DB_NAME(r_blocker.database_id)  AS BlockerDatabase,
                    DB_NAME(r_blocked.database_id)  AS BlockedDatabase,
                    LEFT(CONVERT(NVARCHAR(MAX), ib_blocker.[event_info]), 2000) AS BlockerSqlText,
                    LEFT(CONVERT(NVARCHAR(MAX), ib_blocked.[event_info]),  2000) AS BlockedSqlText
                FROM sys.dm_os_waiting_tasks wt WITH (NOLOCK)
                -- blocker session info
                LEFT JOIN sys.dm_exec_sessions   s_blocker WITH (NOLOCK) ON s_blocker.session_id = wt.blocking_session_id
                LEFT JOIN sys.dm_exec_requests   r_blocker WITH (NOLOCK) ON r_blocker.session_id = wt.blocking_session_id
                -- blocked session info
                LEFT JOIN sys.dm_exec_sessions   s_blocked WITH (NOLOCK) ON s_blocked.session_id = wt.session_id
                LEFT JOIN sys.dm_exec_requests   r_blocked WITH (NOLOCK) ON r_blocked.session_id = wt.session_id
                -- SQL text via dm_exec_input_buffer (works for sleeping blockers with no active request)
                OUTER APPLY sys.dm_exec_input_buffer(wt.blocking_session_id, NULL) ib_blocker
                OUTER APPLY sys.dm_exec_input_buffer(wt.session_id,          NULL) ib_blocked
                WHERE wt.blocking_session_id IS NOT NULL
                  AND wt.blocking_session_id > 0";
            cmd.CommandTimeout = 15;

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                blockers.Add(new BlockingInfo
                {
                    BlockingSPID = reader.GetInt32(reader.GetOrdinal("BlockingSPID")),
                    BlockedSPID = reader.GetInt32(reader.GetOrdinal("BlockedSPID")),
                    WaitDurationMs = reader.GetInt64(reader.GetOrdinal("WaitDurationMs")),
                    WaitType = reader.IsDBNull(reader.GetOrdinal("WaitType")) ? null : reader.GetString(reader.GetOrdinal("WaitType")),
                    BlockerLogin = reader.IsDBNull(reader.GetOrdinal("BlockerLogin")) ? null : reader.GetString(reader.GetOrdinal("BlockerLogin")),
                    BlockedLogin = reader.IsDBNull(reader.GetOrdinal("BlockedLogin")) ? null : reader.GetString(reader.GetOrdinal("BlockedLogin")),
                    BlockerDatabase = reader.IsDBNull(reader.GetOrdinal("BlockerDatabase")) ? null : reader.GetString(reader.GetOrdinal("BlockerDatabase")),
                    BlockedDatabase = reader.IsDBNull(reader.GetOrdinal("BlockedDatabase")) ? null : reader.GetString(reader.GetOrdinal("BlockedDatabase")),
                    BlockerSqlText = reader.IsDBNull(reader.GetOrdinal("BlockerSqlText")) ? null : reader.GetString(reader.GetOrdinal("BlockerSqlText")),
                    BlockedSqlText = reader.IsDBNull(reader.GetOrdinal("BlockedSqlText")) ? null : reader.GetString(reader.GetOrdinal("BlockedSqlText")),
                });
            }

            // Persist for forensics history — fire-and-forget, never blocks the caller.
            if (_blockingHistory != null && !string.IsNullOrEmpty(serverName) && blockers.Count > 0)
            {
                var capturedUtc = DateTime.UtcNow;
                _ = Task.Run(async () =>
                {
                    foreach (var b in blockers)
                    {
                        await _blockingHistory.RecordBlockingEventAsync(new BlockingEvent
                        {
                            ServerName = serverName,
                            CapturedUtc = capturedUtc,
                            BlockerSpid = b.BlockingSPID,
                            BlockedSpid = b.BlockedSPID,
                            WaitType = b.WaitType,
                            DurationSeconds = (int)(b.WaitDurationMs / 1000),
                            BlockerLogin = b.BlockerLogin,
                            BlockedLogin = b.BlockedLogin,
                            BlockerDatabase = b.BlockerDatabase,
                            BlockedDatabase = b.BlockedDatabase,
                            BlockerSqlText = b.BlockerSqlText,
                            BlockedSqlText = b.BlockedSqlText,
                        });
                    }
                });
            }

            return blockers;
        }

        /// <summary>
        /// Returns the XML query execution plan for a session that currently has an active request.
        /// Returns null if the session is sleeping or has no plan handle (plan not yet compiled).
        /// </summary>
        public async Task<string?> GetQueryPlanAsync(int spid)
        {
            using var conn = await _connectionFactory.CreateConnectionAsync();
            conn.ChangeDatabase("master");
            using var cmd = ((SqlConnection)conn).CreateCommand();
            cmd.CommandText = @"
                SELECT CONVERT(NVARCHAR(MAX), qp.query_plan) AS PlanXml
                FROM sys.dm_exec_requests r WITH (NOLOCK)
                CROSS APPLY sys.dm_exec_query_plan(r.plan_handle) qp
                WHERE r.session_id = @Spid
                  AND qp.query_plan IS NOT NULL";
            cmd.CommandTimeout = 10;

            var p = cmd.CreateParameter();
            p.ParameterName = "@Spid";
            p.Value = spid;
            cmd.Parameters.Add(p);

            var result = await cmd.ExecuteScalarAsync();
            return result as string;
        }

        /// <summary>
        /// Kill a session by SPID
        /// </summary>
        public async Task KillSessionAsync(int spid)
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();
            if (connection is not SqlConnection sqlConn)
                throw new InvalidOperationException("KillSession requires a SQL Server connection");
            using var cmd = new SqlCommand($"KILL {spid}", sqlConn) { CommandTimeout = 30 };
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
