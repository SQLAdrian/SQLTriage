/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    public class AgentJobControlService
    {
        private readonly ServerConnectionManager _connectionManager;
        private readonly ILogger<AgentJobControlService> _logger;

        public AgentJobControlService(ServerConnectionManager connectionManager, ILogger<AgentJobControlService> logger)
        {
            _connectionManager = connectionManager;
            _logger = logger;
        }

        public async Task StartJobAsync(string serverName, string jobName)
        {
            await ExecuteJobSpAsync(serverName, "sp_start_job", jobName);
        }

        public async Task StopJobAsync(string serverName, string jobName)
        {
            await ExecuteJobSpAsync(serverName, "sp_stop_job", jobName);
        }

        public async Task EnableJobAsync(string serverName, string jobName)
        {
            await UpdateJobEnabledAsync(serverName, jobName, true);
        }

        public async Task DisableJobAsync(string serverName, string jobName)
        {
            await UpdateJobEnabledAsync(serverName, jobName, false);
        }

        private async Task ExecuteJobSpAsync(string serverName, string spName, string jobName)
        {
            _logger.LogWarning("Executing {SpName} for job {JobName} on server {ServerName}", spName, jobName, serverName);
            
            var conn = GetConnectionForServer(serverName);
            if (conn == null) throw new InvalidOperationException($"Server {serverName} not found.");

            var connStr = conn.GetConnectionString(serverName, "msdb");
            using var sqlConn = new SqlConnection(connStr);
            await sqlConn.OpenAsync();

            using var cmd = new SqlCommand($"msdb.dbo.{spName}", sqlConn);
            cmd.CommandType = System.Data.CommandType.StoredProcedure;
            cmd.Parameters.AddWithValue("@job_name", jobName);
            
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task UpdateJobEnabledAsync(string serverName, string jobName, bool enabled)
        {
            _logger.LogWarning("Setting job {JobName} enabled={Enabled} on server {ServerName}", jobName, enabled, serverName);
            
            var conn = GetConnectionForServer(serverName);
            if (conn == null) throw new InvalidOperationException($"Server {serverName} not found.");

            var connStr = conn.GetConnectionString(serverName, "msdb");
            using var sqlConn = new SqlConnection(connStr);
            await sqlConn.OpenAsync();

            using var cmd = new SqlCommand("msdb.dbo.sp_update_job", sqlConn);
            cmd.CommandType = System.Data.CommandType.StoredProcedure;
            cmd.Parameters.AddWithValue("@job_name", jobName);
            cmd.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
            
            await cmd.ExecuteNonQueryAsync();
        }

        private ServerConnection? GetConnectionForServer(string serverName)
        {
            foreach (var conn in _connectionManager.GetEnabledConnections())
            {
                if (System.Linq.Enumerable.Contains(conn.GetServerList(), serverName, StringComparer.OrdinalIgnoreCase))
                {
                    return conn;
                }
            }
            return null;
        }
    }
}
