/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Background singleton that pings each enabled server connection every 30 seconds
    /// and exposes a live up/down status dictionary for use in NavMenu and Dashboard.
    /// </summary>
    public class ConnectionHealthService : IDisposable
    {
        public enum ServerStatus { Unknown, Online, Offline }

        public record HealthEntry(ServerStatus Status, DateTime LastChecked, string? Error);

        private readonly ServerConnectionManager _connections;
        private readonly ILogger<ConnectionHealthService> _logger;
        private readonly ConcurrentDictionary<string, HealthEntry> _status = new(StringComparer.OrdinalIgnoreCase);

        // EngineEdition 5 = Azure SQL DB, 8 = Azure SQL Managed Instance
        // Populated during each health check cycle; used to skip unsupported DMV checks on Azure.
        private readonly ConcurrentDictionary<string, bool> _isAzure = new(StringComparer.OrdinalIgnoreCase);

        private readonly System.Timers.Timer _timer;
        private bool _disposed;

        public event Action? OnStatusChanged;

        /// <summary>Returns true if the server is Azure SQL DB or Azure SQL MI.</summary>
        public bool IsAzureSql(string serverName)
            => _isAzure.TryGetValue(serverName, out var v) && v;

        public ConnectionHealthService(ServerConnectionManager connections, ILogger<ConnectionHealthService> logger)
        {
            _connections = connections;
            _logger = logger;

            _timer = new System.Timers.Timer(30_000);
            _timer.Elapsed += async (_, _) => await CheckAllAsync();
            _timer.AutoReset = true;
        }

        /// <summary>Start polling. Called once from App startup after DI is ready.</summary>
        public void Start()
        {
            _ = Task.Run(CheckAllAsync); // immediate first check
            _timer.Start();
        }

        /// <summary>Returns the current health status for a server name (or Unknown if not yet checked).</summary>
        public HealthEntry GetStatus(string serverName)
            => _status.TryGetValue(serverName, out var e) ? e : new HealthEntry(ServerStatus.Unknown, DateTime.MinValue, null);

        /// <summary>Snapshot of all known statuses.</summary>
        public IReadOnlyDictionary<string, HealthEntry> AllStatuses => _status;

        /// <summary>Count of servers currently online.</summary>
        public int OnlineCount => CountByStatus(ServerStatus.Online);

        /// <summary>Count of servers currently offline.</summary>
        public int OfflineCount => CountByStatus(ServerStatus.Offline);

        private int CountByStatus(ServerStatus s)
        {
            int n = 0;
            foreach (var e in _status.Values) if (e.Status == s) n++;
            return n;
        }

        private async Task CheckAllAsync()
        {
            var enabled = _connections.GetEnabledConnections();
            var tasks = new List<Task>();

            foreach (var conn in enabled)
            {
                foreach (var server in conn.GetServerList())
                {
                    var s = server;
                    var c = conn;
                    tasks.Add(CheckServerAsync(c, s));
                }
            }

            await Task.WhenAll(tasks);
            OnStatusChanged?.Invoke();
        }

        private async Task CheckServerAsync(ServerConnection conn, string serverName)
        {
            try
            {
                var connStr = conn.GetConnectionString(serverName, "master") +
                              ";Connect Timeout=5;Application Name=SQLTriage-HealthCheck";

                using var sql = new SqlConnection(connStr);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                await sql.OpenAsync(cts.Token);

                // Probe EngineEdition to detect Azure SQL DB (5) or Azure SQL MI (8)
                // Only probe once per session — skip if already cached
                if (!_isAzure.ContainsKey(serverName))
                {
                    try
                    {
                        using var cmd = sql.CreateCommand();
                        cmd.CommandText = "SELECT SERVERPROPERTY('EngineEdition')";
                        cmd.CommandTimeout = 4;
                        var edition = await cmd.ExecuteScalarAsync(cts.Token);
                        var ee = edition is DBNull || edition == null ? 0 : Convert.ToInt32(edition);
                        _isAzure[serverName] = ee == 5 || ee == 8;
                        if (_isAzure[serverName])
                            _logger.LogInformation("Azure SQL detected: {Server} (EngineEdition={E})", serverName, ee);
                    }
                    catch { _isAzure[serverName] = false; }
                }

                _status[serverName] = new HealthEntry(ServerStatus.Online, DateTime.UtcNow, null);
                _logger.LogDebug("Health check OK: {Server}", serverName);
            }
            catch (Exception ex)
            {
                var prev = GetStatus(serverName);
                _status[serverName] = new HealthEntry(ServerStatus.Offline, DateTime.UtcNow, ex.Message);

                // Only log transition to offline, not every failed ping
                if (prev.Status != ServerStatus.Offline)
                    _logger.LogWarning("Server went offline: {Server} — {Error}", serverName, ex.Message);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            _timer.Dispose();
        }
    }
}
