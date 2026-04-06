/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SqlHealthAssessment.Data.Models;

// Required for SqlServerConnectionFactory.CreateConnection(initialDatabase)
using SqlHealthAssessment.Data;

namespace SqlHealthAssessment.Data
{
    /// <summary>
    /// Real-time session data service that queries sys.dm_exec_sessions and
    /// sys.dm_exec_requests directly against the currently selected SQL Server.
    /// This bypasses CachingQueryExecutor — sessions are always live, never cached.
    /// </summary>
    public class SessionDataService
    {
        private readonly IDbConnectionFactory _connectionFactory;

        private const string LiveSessionsQuery = @"
            SELECT TOP (@TopCount)
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
                t.text AS QueryText
            FROM sys.dm_exec_sessions s WITH (NOLOCK)
            LEFT JOIN sys.dm_exec_requests r WITH (NOLOCK)
                ON s.session_id = r.session_id
            OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
            WHERE s.is_user_process = 1
            ORDER BY s.cpu_time + s.reads DESC";

        public SessionDataService(IDbConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        /// <summary>
        /// Fetches all live user sessions from the currently connected SQL Server instance.
        /// Uses master database for live session DMVs which are server-scoped.
        /// </summary>
        public async Task<List<SessionInfo>> GetLiveSessionsAsync(int topCount = 100)
        {
            var sessions = new List<SessionInfo>();

            // Live session DMVs are server-scoped; use master database directly
            // to avoid dependency on SQLWATCH database being present.
            // Cast to SqlServerConnectionFactory to use the CreateConnection(initialDatabase) overload
            using var conn = _connectionFactory is SqlServerConnectionFactory sqlFactory
                ? (SqlConnection)sqlFactory.CreateConnection("master")
                : (SqlConnection)_connectionFactory.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = LiveSessionsQuery;
            cmd.CommandTimeout = 30;

            var topParam = cmd.CreateParameter();
            topParam.ParameterName = "@TopCount";
            topParam.Value = topCount;
            cmd.Parameters.Add(topParam);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                sessions.Add(new SessionInfo
                {
                    SPID                 = reader.GetInt16(reader.GetOrdinal("SPID")),
                    LoginName            = reader.GetString(reader.GetOrdinal("LoginName")),
                    HostName             = reader.GetString(reader.GetOrdinal("HostName")),
                    DatabaseName         = reader.GetString(reader.GetOrdinal("DatabaseName")),
                    SessionStatus        = reader.GetString(reader.GetOrdinal("SessionStatus")),
                    CpuTime              = reader.GetInt32(reader.GetOrdinal("CpuTime")),
                    LogicalReads         = reader.GetInt64(reader.GetOrdinal("LogicalReads")),
                    Writes               = reader.GetInt64(reader.GetOrdinal("Writes")),
                    OpenTransactionCount = reader.GetInt32(reader.GetOrdinal("OpenTransactionCount")),
                    RequestStatus        = reader.IsDBNull(reader.GetOrdinal("RequestStatus")) ? null : reader.GetString(reader.GetOrdinal("RequestStatus")),
                    Command              = reader.IsDBNull(reader.GetOrdinal("Command"))        ? null : reader.GetString(reader.GetOrdinal("Command")),
                    WaitType             = reader.IsDBNull(reader.GetOrdinal("WaitType"))       ? null : reader.GetString(reader.GetOrdinal("WaitType")),
                    WaitTime             = reader.GetInt32(reader.GetOrdinal("WaitTime")),
                    BlockingSessionId    = reader.GetInt16(reader.GetOrdinal("BlockingSessionId")),
                    TotalElapsedTime     = reader.GetInt32(reader.GetOrdinal("TotalElapsedTime")),
                    ProgramName          = reader.GetString(reader.GetOrdinal("ProgramName")),
                    QueryText            = reader.IsDBNull(reader.GetOrdinal("QueryText")) ? null : reader.GetString(reader.GetOrdinal("QueryText"))
                });
            }

            return sessions;
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
