/* In the name of God, the Merciful, the Compassionate */
/*
 * WaitStatsService — orchestrator for the wait-stats workbench (WAIT_STATS_DESIGN.md).
 * P1 surface: GetSnapshotAsync only. Other methods (trend, scheduler pressure,
 * session waits, query waits) land in P2-P4.
 *
 * Persistence delegated to WaitStatsHistoryService. Background snapshot loop
 * runs every 30s per connected server (configurable). User owns the SQL —
 * the DMV query is a default that can be overridden via SqlQueryRepository.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services;

public class WaitStatsService : IDisposable
{
    private readonly int SnapshotIntervalSeconds;

    // Default DMV query. User can override by replacing this with a config-driven
    // query later (per CLAUDE.md "User owns SQL"). Read-only system catalog access.
    private const string DefaultWaitStatsQuery = @"
        SELECT wait_type, waiting_tasks_count, wait_time_ms, signal_wait_time_ms
        FROM sys.dm_os_wait_stats
        WHERE wait_time_ms > 0;
    ";

    private readonly ILogger<WaitStatsService> _logger;
    private readonly ServerConnectionManager _connections;
    private readonly WaitStatsHistoryService _history;
    private readonly SqlConnectionPoolService? _pool;
    private readonly ServerCircuitBreakerService? _breaker;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, Dictionary<string, (long WaitMs, long Tasks, long SignalMs)>> _lastCumulative
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateLock = new();
    private bool _started;

    public WaitStatsService(
        ILogger<WaitStatsService> logger,
        ServerConnectionManager connections,
        WaitStatsHistoryService history,
        IConfiguration? configuration = null,
        SqlConnectionPoolService? pool = null,
        ServerCircuitBreakerService? breaker = null)
    {
        _logger = logger;
        _connections = connections;
        _history = history;
        _pool = pool;
        _breaker = breaker;
        SnapshotIntervalSeconds = configuration?.GetValue<int>("WaitStats:SnapshotIntervalSeconds", 30) ?? 30;
    }

    /// <summary>
    /// Kicks off the background snapshot loop. Idempotent. Call once at app start.
    /// </summary>
    public void StartBackgroundLoop()
    {
        lock (_stateLock)
        {
            if (_started) return;
            _started = true;
        }
        _ = Task.Run(async () => await LoopAsync(_cts.Token));
        _logger.LogInformation("WaitStats background loop started ({Interval}s cadence)", SnapshotIntervalSeconds);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(SnapshotIntervalSeconds));
        while (await timer.WaitForNextTickAsync(ct))
        {
            var tickStart = DateTime.UtcNow;
            try
            {
                var conns = _connections.GetEnabledConnections();
                foreach (var c in conns)
                {
                    foreach (var srv in c.GetServerList())
                    {
                        if (_breaker != null && !_breaker.ShouldAttempt(srv))
                            continue;

                        // Invariant B-prime (2026-09-19): our own fault is not evidence about the
                        // server. This used to be ONE catch (Exception) that recorded a breaker
                        // failure for anything, so an exception from our delta code, our SQLite
                        // history write or our own connection pool (a TimeoutException when every
                        // pooled connection is in use) counted against a healthy server. Only a
                        // SqlException comes from the server round trip, and it goes to the breaker
                        // with its cause; the shared classifier then counts a refusal (no VIEW SERVER
                        // STATE) as the answer it is. That split is sound HERE because nothing on this
                        // path turns a hung server into a non-SQL exception: there is no CancelAfter,
                        // the token is only the loop's shutdown token, and the connect and command
                        // timeouts both surface as SqlException. Add a CancelAfter and it stops being
                        // sound (ConnectionHealthService is the counter-example).
                        WaitStatsSnapshot snap;
                        try
                        {
                            snap = await GetSnapshotAsync(c, srv, ct);
                        }
                        catch (Exception) when (ct.IsCancellationRequested)
                        {
                            return;   // shutting down: nothing was learned about the server
                        }
                        catch (SqlException ex)
                        {
                            _breaker?.RecordFailure(srv, ex);
                            _logger.LogDebug(ex, "WaitStats snapshot failed for {Server}", srv);
                            continue;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "WaitStats snapshot could not be taken for {Server}, for a reason on our side; the circuit breaker is not told", srv);
                            continue;
                        }

                        // The server answered the read. What follows is ours.
                        _breaker?.RecordSuccess(srv);
                        try
                        {
                            if (snap.Deltas.Count > 0)
                                await _history.RecordSnapshotAsync(srv, snap, ct);
                        }
                        catch (Exception) when (ct.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "WaitStats history write failed for {Server}; the circuit breaker is not told", srv);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WaitStats loop iteration failed");
            }
            var elapsed = DateTime.UtcNow - tickStart;
            if (elapsed.TotalSeconds > SnapshotIntervalSeconds)
                _logger.LogWarning("WaitStats tick overran interval ({ElapsedMs}ms > {IntervalS}s); next tick dropped", (int)elapsed.TotalMilliseconds, SnapshotIntervalSeconds);
        }
    }

    /// <summary>
    /// Read sys.dm_os_wait_stats once, compute deltas vs the previous reading
    /// for this server, classify each wait, and return a snapshot ready for
    /// persistence. Returns empty Deltas on first call (need two samples to compute delta).
    /// </summary>
    public async Task<WaitStatsSnapshot> GetSnapshotAsync(
        SQLTriage.Data.Models.ServerConnection conn,
        string serverName,
        CancellationToken ct = default)
    {
        var snap = new WaitStatsSnapshot();
        var current = new Dictionary<string, (long WaitMs, long Tasks, long SignalMs)>(StringComparer.OrdinalIgnoreCase);

        var connStr = conn.GetConnectionString(serverName, "master");
        System.Data.IDbConnection? rentedConn = null;
        bool pooled = false;
        if (_pool != null)
        {
            rentedConn = await _pool.GetConnectionAsync(connStr, ct);
            pooled = true;
        }
        else
        {
            var direct = new SqlConnection(connStr);
            await direct.OpenAsync(ct);
            rentedConn = direct;
        }
        try
        {
            var sqlConn = (SqlConnection)rentedConn;
            using var cmd = sqlConn.CreateCommand();
            cmd.CommandText = DefaultWaitStatsQuery;
            cmd.CommandTimeout = 10;
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var wt = r.GetString(0);
                if (WaitCategoryClassifier.IsIdleBenign(wt)) continue;
                current[wt] = (
                    r.IsDBNull(2) ? 0 : Convert.ToInt64(r.GetValue(2)),
                    r.IsDBNull(1) ? 0 : Convert.ToInt64(r.GetValue(1)),
                    r.IsDBNull(3) ? 0 : Convert.ToInt64(r.GetValue(3))
                );
            }
        }
        finally
        {
            if (pooled && _pool != null)
                _pool.ReturnConnection(rentedConn!, connStr);
            else
                try { rentedConn?.Dispose(); } catch { /* best effort */ }
        }

        Dictionary<string, (long WaitMs, long Tasks, long SignalMs)>? previous;
        lock (_stateLock)
        {
            _lastCumulative.TryGetValue(serverName, out previous);
            _lastCumulative[serverName] = current;
        }

        if (previous == null) return snap; // first reading — no delta yet

        foreach (var (wt, cur) in current)
        {
            if (!previous.TryGetValue(wt, out var prev)) continue;
            var dWait = cur.WaitMs - prev.WaitMs;
            var dTasks = cur.Tasks - prev.Tasks;
            var dSignal = cur.SignalMs - prev.SignalMs;
            if (dWait <= 0 && dTasks <= 0) continue;
            // Negative deltas can occur if DBCC SQLPERF cleared the stats — skip.
            if (dWait < 0 || dTasks < 0 || dSignal < 0) continue;
            snap.Deltas.Add(new WaitDelta
            {
                WaitType = wt,
                Category = WaitCategoryClassifier.Classify(wt),
                DeltaWaitMs = dWait,
                DeltaTasks = dTasks,
                DeltaSignalMs = dSignal,
            });
        }
        return snap;
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* best effort */ }
        _cts.Dispose();
    }
}
