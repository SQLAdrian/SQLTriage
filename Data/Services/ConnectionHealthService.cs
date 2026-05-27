/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    // BM:ConnectionHealthService.Class — background pinger for server connection health status
    /// <summary>
    /// Background singleton that pings each enabled server connection every 30 seconds
    /// and exposes a live up/down status dictionary for use in NavMenu and Dashboard.
    /// Uses PeriodicTimer + cancellation-driven loop; exceptions are observed per cycle.
    /// </summary>
    public class ConnectionHealthService : IDisposable
    {
        public enum ServerStatus { Unknown, Online, Offline }

        public record HealthEntry(ServerStatus Status, DateTime LastChecked, string? Error);

        private readonly TimeSpan PollInterval;

        private readonly ServerConnectionManager _connections;
        private readonly ILogger<ConnectionHealthService> _logger;
        private readonly ConcurrentDictionary<string, HealthEntry> _status = new(StringComparer.OrdinalIgnoreCase);

        // EngineEdition 5 = Azure SQL DB, 8 = Azure SQL Managed Instance
        private readonly ConcurrentDictionary<string, bool> _isAzure = new(StringComparer.OrdinalIgnoreCase);

        // Cached SQL Server major version + edition string per server.
        // Probed once on the first successful health-check; consumers (Query
        // Plan modal, NoPants index-create UI) read these to gate features
        // like ONLINE / RESUMABLE that have version/edition requirements.
        private readonly ConcurrentDictionary<string, ServerCapabilities> _caps =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>SQL Server version + edition info exposed for feature-gating.</summary>
        /// <param name="MajorVersion">SQL Server major version (e.g. 14 = 2017, 15 = 2019, 16 = 2022). 0 if unknown.</param>
        /// <param name="Edition">Full edition string (e.g. "Enterprise Edition (64-bit)"). null if unknown.</param>
        /// <param name="IsEnterpriseClass">True for Enterprise / Developer / Evaluation editions, which support online/resumable index ops.</param>
        public record ServerCapabilities(int MajorVersion, string? Edition, bool IsEnterpriseClass);

        /// <summary>Returns capabilities for a server, or null if not yet probed.</summary>
        public ServerCapabilities? GetCapabilities(string serverName)
            => _caps.TryGetValue(serverName, out var v) ? v : null;

        private readonly CancellationTokenSource _cts = new();
        private readonly ServerCircuitBreakerService? _breaker;
        private Task? _loopTask;
        private bool _disposed;

        public event Action? OnStatusChanged;

        /// <summary>Returns true if the server is Azure SQL DB or Azure SQL MI.</summary>
        public bool IsAzureSql(string serverName)
            => _isAzure.TryGetValue(serverName, out var v) && v;

        public ConnectionHealthService(ServerConnectionManager connections, ILogger<ConnectionHealthService> logger,
            IConfiguration? configuration = null,
            ServerCircuitBreakerService? breaker = null)
        {
            _connections = connections;
            _logger = logger;
            _breaker = breaker;
            var intervalSeconds = configuration?.GetValue<int>("ConnectionHealth:PollIntervalSeconds", 30) ?? 30;
            PollInterval = TimeSpan.FromSeconds(intervalSeconds > 0 ? intervalSeconds : 30);
        }

        /// <summary>Start polling. Called once from App startup after DI is ready.</summary>
        public void Start()
        {
            if (_loopTask != null) return;
            _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
        }

        private async Task RunLoopAsync(CancellationToken ct)
        {
            // Immediate first check
            try { await CheckAllAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _logger.LogError(ex, "Initial health check cycle failed"); }

            using var timer = new PeriodicTimer(PollInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    try { await CheckAllAsync(ct); }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { _logger.LogError(ex, "Health check cycle failed"); }
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
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

        private async Task CheckAllAsync(CancellationToken ct = default)
        {
            var enabled = _connections.GetEnabledConnections();
            var tasks = new List<Task>();

            foreach (var conn in enabled)
            {
                foreach (var server in conn.GetServerList())
                {
                    var s = server;
                    var c = conn;
                    tasks.Add(CheckServerAsync(c, s, ct));
                }
            }

            await Task.WhenAll(tasks);
            OnStatusChanged?.Invoke();
        }

        private async Task CheckServerAsync(ServerConnection conn, string serverName, CancellationToken outerCt)
        {
            if (_breaker != null && !_breaker.ShouldAttempt(serverName))
                return;

            try
            {
                var connStr = conn.GetConnectionString(serverName, "master") +
                              ";Connect Timeout=5;Application Name=SQLTriage-HealthCheck";

                using var sql = new SqlConnection(connStr);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
                cts.CancelAfter(TimeSpan.FromSeconds(6));
                await sql.OpenAsync(cts.Token);

                // Probe SERVERPROPERTY values once per session: EngineEdition
                // (Azure detection), ProductMajorVersion, and Edition.
                // EngineEdition: 3 = Enterprise, 5 = Azure SQL DB, 6 = DataWarehouse,
                //                8 = Azure SQL MI; Enterprise/Developer/Evaluation
                //                support online + resumable index operations.
                if (!_isAzure.ContainsKey(serverName))
                {
                    try
                    {
                        using var cmd = sql.CreateCommand();
                        cmd.CommandText = @"
                            SELECT
                                CAST(SERVERPROPERTY('EngineEdition') AS int)        AS EngineEdition,
                                CAST(SERVERPROPERTY('ProductMajorVersion') AS int)  AS ProductMajorVersion,
                                CAST(SERVERPROPERTY('Edition') AS nvarchar(256))    AS Edition";
                        cmd.CommandTimeout = 4;
                        using var rdr = await cmd.ExecuteReaderAsync(cts.Token);
                        int ee = 0, pmv = 0;
                        string? ed = null;
                        if (await rdr.ReadAsync(cts.Token))
                        {
                            ee = rdr.IsDBNull(0) ? 0 : rdr.GetInt32(0);
                            pmv = rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1);
                            ed = rdr.IsDBNull(2) ? null : rdr.GetString(2);
                        }
                        _isAzure[serverName] = ee == 5 || ee == 8;

                        // Enterprise-class engines: 3 = Enterprise (incl. Developer/Evaluation),
                        // 5 = Azure SQL DB Premium tiers also expose ONLINE; 8 = Azure MI Business Critical.
                        // Conservative gate: anything except Standard/Web/Express.
                        bool isEnterpriseClass = ee == 3 || ee == 5 || ee == 8 ||
                            (ed != null && (ed.Contains("Enterprise", StringComparison.OrdinalIgnoreCase)
                                         || ed.Contains("Developer", StringComparison.OrdinalIgnoreCase)
                                         || ed.Contains("Evaluation", StringComparison.OrdinalIgnoreCase)));

                        _caps[serverName] = new ServerCapabilities(pmv, ed, isEnterpriseClass);

                        if (_isAzure[serverName])
                            _logger.LogInformation("Azure SQL detected: {Server} (EngineEdition={E})", serverName, ee);
                        _logger.LogDebug("Server caps probed: {Server} v{Ver} edition='{Ed}' enterpriseClass={Ent}",
                            serverName, pmv, ed, isEnterpriseClass);
                    }
                    catch (Exception ex)
                    {
                        _isAzure[serverName] = false;
                        _logger.LogDebug(ex, "Server capability probe failed for {Server}", serverName);
                    }
                }

                _status[serverName] = new HealthEntry(ServerStatus.Online, DateTime.UtcNow, null);
                _breaker?.RecordSuccess(serverName);
                _logger.LogDebug("Health check OK: {Server}", serverName);
            }
            catch (OperationCanceledException) when (outerCt.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _breaker?.RecordFailure(serverName);
                var prev = GetStatus(serverName);
                _status[serverName] = new HealthEntry(ServerStatus.Offline, DateTime.UtcNow, ex.Message);

                if (prev.Status != ServerStatus.Offline)
                    _logger.LogWarning("Server went offline: {Server} — {Error}", serverName, ex.Message);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _cts.Cancel(); } catch { }

            try
            {
                if (_loopTask != null && !_loopTask.Wait(TimeSpan.FromSeconds(2)))
                    _logger.LogWarning("ConnectionHealthService loop did not exit within 2s");
            }
            catch { }

            _cts.Dispose();
        }
    }
}
