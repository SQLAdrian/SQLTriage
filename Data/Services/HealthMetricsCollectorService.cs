/* In the name of God, the Merciful, the Compassionate */

// #57 (2026-07-15): the DBA dashboard's "Per-Server Health" cards (CPU / Memory / Blocked /
// Deadlocks / Top-Wait) rendered "--" forever because nothing ever called
// HealthCheckService.GetHealthStatusAsync — it was dead code. This is the background collector
// that calls it, on a timer, across every enabled server, mirroring the AlertEvaluationService /
// BlockingForensicsService lifecycle (PeriodicTimer loop, Start()/Stop(), registered in
// AddSharedServices, started from both App.xaml.cs and WindowsServiceHost.cs).
//
// The one thing this class exists to get right: it ALWAYS resolves the owning ServerConnection
// and passes it into GetHealthStatusAsync, so each server gets its OWN connection — never the
// arg-less single-instance factory (see HealthCheckService.GetHealthStatusAsync's XML doc for why
// that would silently show every server the same numbers under different names).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Timer-based background collector that keeps <see cref="HealthCheckService"/>'s per-server
    /// live metrics fresh across the whole enabled estate.
    /// </summary>
    public class HealthMetricsCollectorService : IDisposable
    {
        private readonly ILogger<HealthMetricsCollectorService> _logger;
        private readonly ServerConnectionManager _connections;
        private readonly HealthCheckService _healthCheck;
        private readonly int _collectSeconds;

        /// <summary>Caps concurrent per-server collections so a large estate can't be stormed
        /// all at once — mirrors CheckExecutionService's per-instance SemaphoreSlim(7) pattern.</summary>
        private const int MaxConcurrentCollections = 7;
        private readonly SemaphoreSlim _throttle = new(MaxConcurrentCollections, MaxConcurrentCollections);

        private readonly CancellationTokenSource _cts = new();
        private Task? _loopTask;
        private bool _isRunning;

        public HealthMetricsCollectorService(
            ILogger<HealthMetricsCollectorService> logger,
            ServerConnectionManager connections,
            HealthCheckService healthCheck,
            IConfiguration? configuration = null)
        {
            _logger = logger;
            _connections = connections;
            _healthCheck = healthCheck;

            var seconds = configuration?.GetValue<int>("HealthMetrics:CollectSeconds", 20) ?? 20;
            _collectSeconds = seconds < 10 ? 20 : seconds;
        }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _loopTask = Task.Run(() => LoopAsync(_cts.Token));
            _logger.LogInformation("Health metrics collector started ({IntervalS}s tick)", _collectSeconds);
        }

        public void Stop()
        {
            _isRunning = false;
            _cts.Cancel();
            _logger.LogInformation("Health metrics collector stopped");
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_collectSeconds));

            // Collect once immediately on startup so the dashboard has real numbers on first
            // load instead of waiting a full tick.
            try { await CollectAllAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _logger.LogError(ex, "Initial health metrics collection failed"); }

            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await CollectAllAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Health metrics collection cycle failed");
                }
            }
        }

        /// <summary>
        /// One full pass across every enabled connection's server list, bounded to
        /// <see cref="MaxConcurrentCollections"/> concurrent per-server polls. An unreachable
        /// server sets IsOnline=false inside HealthCheckService (SqlException caught there) and
        /// does not affect any other server's result.
        /// </summary>
        private async Task CollectAllAsync(CancellationToken ct)
        {
            var tasks = new List<Task>();
            foreach (var conn in _connections.GetEnabledConnections())
            {
                foreach (var serverName in conn.GetServerList())
                {
                    if (ct.IsCancellationRequested) break;
                    tasks.Add(CollectOneThrottledAsync(conn, serverName, ct));
                }
            }

            if (tasks.Count > 0)
                await Task.WhenAll(tasks);
        }

        private async Task CollectOneThrottledAsync(ServerConnection connection, string serverName, CancellationToken ct)
        {
            await _throttle.WaitAsync(ct);
            try
            {
                // GetHealthStatusAsync already catches SqlException/Exception internally and
                // records IsOnline=false + ErrorMessage on the persisted health object, so this
                // try/catch is defensive only.
                await _healthCheck.GetHealthStatusAsync(serverName, connection);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Health metrics collection failed for {Server}", serverName);
            }
            finally
            {
                _throttle.Release();
            }
        }

        /// <summary>
        /// On-demand refresh of a single server, used by the Health page's "Refresh" button so it
        /// triggers a real poll instead of only reading whatever the last timer tick cached.
        /// </summary>
        public async Task CollectServerAsync(string serverName, CancellationToken ct = default)
        {
            var connection = _connections.GetEnabledConnections()
                .FirstOrDefault(c => c.GetServerList().Any(s => string.Equals(s, serverName, StringComparison.OrdinalIgnoreCase)));
            if (connection == null) return;

            await CollectOneThrottledAsync(connection, serverName, ct);
        }

        public void Dispose()
        {
            Stop();
            try { _loopTask?.Wait(TimeSpan.FromSeconds(5)); } catch { /* best effort */ }
            _cts?.Dispose();
            _throttle?.Dispose();
        }
    }
}
