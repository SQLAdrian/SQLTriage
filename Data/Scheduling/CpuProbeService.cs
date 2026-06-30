/* In the name of God, the Merciful, the Compassionate */
// Bismillah ar-Rahman ar-Raheem

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SQLTriage.Data;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Scheduling
{
    /// <summary>
    /// Probes SQL Server CPU usage to drive adaptive batch sizing.
    /// Uses sys.dm_os_performance_counters for lightweight, non-blocking CPU measurement.
    /// </summary>
    public class CpuProbeService : IDisposable
    {
        private readonly ILogger<CpuProbeService> _logger;
        private readonly IConfiguration _configuration;
        private readonly ServerConnectionManager _connectionManager;
        private string _connectionString = "";
        private readonly object _lock = new();
        private double _lastCpuPercent = 0.0;
        private DateTime _lastSampleTime = DateTime.MinValue;
        private bool _disposed;
        private Timer? _timer;
        private readonly int _probeIntervalSec;

        public CpuProbeService(
            ILogger<CpuProbeService> logger,
            IConfiguration configuration,
            ServerConnectionManager connectionManager)
        {
            _logger = logger;
            _configuration = configuration;
            _connectionManager = connectionManager;

            var currentServer = _connectionManager.CurrentServer;
            if (currentServer != null)
            {
                _connectionString = BuildConnectionString(currentServer);
            }
            else
            {
                _connectionString = configuration.GetConnectionString("SqlServer") ?? "Server=.;Database=master;Integrated Security=true;";
            }

            _connectionManager.OnConnectionChanged += OnConnectionChanged;

            _probeIntervalSec = _configuration.GetValue<int>("LoadBalancing:CpuProbeIntervalSec", 5);
            _timer = new Timer(ProbeCpuAsync, null, 0, _probeIntervalSec * 1000);
        }

        private void OnConnectionChanged()
        {
            var currentServer = _connectionManager.CurrentServer;
            if (currentServer != null)
            {
                lock (_lock)
                {
                    _connectionString = BuildConnectionString(currentServer);
                }
            }
        }

        private string BuildConnectionString(ServerConnection connection)
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = connection.GetServerList().FirstOrDefault() ?? connection.Id,
                InitialCatalog = "master",
                ConnectTimeout = 5
            };

            // EffectiveAuthType returns "Windows", "SqlServer", "EntraMFA"
            var auth = connection.EffectiveAuthType;
            if (auth == AuthenticationTypes.SqlServer)
            {
                builder.IntegratedSecurity = false;
                builder.UserID = connection.Username ?? "";
                builder.Password = connection.GetDecryptedPassword();
            }
            else if (auth == AuthenticationTypes.EntraMFA)
            {
                builder.IntegratedSecurity = false;
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive;
                if (!string.IsNullOrWhiteSpace(connection.Username))
                    builder.UserID = connection.Username;
            }
            else
            {
                builder.IntegratedSecurity = true;
            }

            if (connection.TrustServerCertificate)
                builder.TrustServerCertificate = true;

            return builder.ConnectionString;
        }

        /// <summary>
        /// Returns the most recent CPU usage percentage (0–100). Returns 0 if probe failed or no data yet.
        /// </summary>
        public double GetLatestCpuPercent()
        {
            lock (_lock)
            {
                return _lastCpuPercent;
            }
        }

        /// <summary>
        /// Samples CPU usage via sys.dm_os_performance_counters.
        /// Reads 'Processor Time' counter for '_Total' instance.
        /// </summary>
        private async void ProbeCpuAsync(object? state)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT cntr_value
                    FROM sys.dm_os_performance_counters
                    WHERE object_name = 'Processor'
                      AND counter_name = '% Processor Time'
                      AND instance_name = '_Total'";

                var result = await cmd.ExecuteScalarAsync();
                if (result is double cpuValue)
                {
                    lock (_lock)
                    {
                        _lastCpuPercent = cpuValue;
                        _lastSampleTime = DateTime.UtcNow;
                    }
                    _logger.LogDebug("[CpuProbe] CPU usage: {Cpu:F1}%", cpuValue);
                }
                else if (result is int intValue)
                {
                    lock (_lock)
                    {
                        _lastCpuPercent = Convert.ToDouble(intValue);
                        _lastSampleTime = DateTime.UtcNow;
                    }
                    _logger.LogDebug("[CpuProbe] CPU usage: {Cpu:F1}%", _lastCpuPercent);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CpuProbe] Failed to sample CPU — check connection/permissions (VIEW SERVER STATE required)");
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _timer?.Dispose();
                _disposed = true;
            }
        }
    }
}
