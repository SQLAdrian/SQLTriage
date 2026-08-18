/* In the name of God, the Merciful, the Compassionate */

// Active blocking/deadlock forensics: queries a chosen SQL Server directly (read-only) for
// deadlocks (system_health ring buffer), the live blocking chain, and Query Store lock-wait
// history — and runs a background collector that captures blocking + deadlocks across all enabled
// instances on a timer so the forensics page has data even when no live view is open.
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>One query's accumulated lock-wait time from Query Store (survives restarts).</summary>
    public sealed class QueryStoreLockWait
    {
        public string Database { get; set; } = "";
        public long QueryId { get; set; }
        public string? SqlText { get; set; }
        public double TotalLockWaitMs { get; set; }
        public double AvgLockWaitMs { get; set; }
    }

    public sealed class BlockingForensicsService : IDisposable
    {
        private readonly ILogger<BlockingForensicsService> _logger;
        private readonly ServerConnectionManager _connections;
        private readonly BlockingHistoryService _history;
        private readonly int _collectSeconds;

        private readonly CancellationTokenSource _cts = new();
        private Task? _loopTask;
        private bool _isRunning;

        public BlockingForensicsService(
            ILogger<BlockingForensicsService> logger,
            ServerConnectionManager connections,
            BlockingHistoryService history,
            IConfiguration? configuration = null)
        {
            _logger = logger;
            _connections = connections;
            _history = history;
            var s = configuration?.GetValue<int>("Blocking:CollectSeconds", 60) ?? 60;
            _collectSeconds = s < 15 ? 60 : s;
        }

        // ── Background collector ────────────────────────────────────────────────────

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _loopTask = Task.Run(() => LoopAsync(_cts.Token));
            _logger.LogInformation("Blocking forensics collector started ({IntervalS}s tick)", _collectSeconds);
        }

        public void Stop()
        {
            _isRunning = false;
            _cts.Cancel();
            _logger.LogInformation("Blocking forensics collector stopped");
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_collectSeconds));
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { await CollectAllAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogError(ex, "Blocking forensics collection cycle failed"); }
            }
        }

        private async Task CollectAllAsync(CancellationToken ct)
        {
            foreach (var conn in _connections.GetEnabledConnections())
            {
                foreach (var serverName in conn.GetServerList())
                {
                    if (ct.IsCancellationRequested) return;
                    // Each source is independently guarded so one bad server never stalls the sweep.
                    try { await CaptureBlockingAsync(conn, serverName, ct); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Blocking capture failed for {Server}", serverName); }
                    try { await CaptureDeadlocksAsync(conn, serverName, ct); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Deadlock capture failed for {Server}", serverName); }
                }
            }
        }

        // ── On-demand pulls (used by the page's "Query server now") ──────────────────

        /// <summary>Live blocking chain for a chosen instance; also persists pairs to history.</summary>
        public async Task<List<BlockingInfo>> GetLiveBlockingAsync(string serverName, CancellationToken ct = default)
        {
            var resolved = Resolve(serverName);
            if (resolved == null) return new List<BlockingInfo>();
            return await CaptureBlockingAsync(resolved.Value.conn, resolved.Value.server, ct);
        }

        /// <summary>
        /// Pull deadlocks from the chosen instance's system_health ring buffer and persist any new
        /// ones. Returns the number of new deadlocks stored.
        /// </summary>
        public async Task<int> PullDeadlocksAsync(string serverName, CancellationToken ct = default)
        {
            var resolved = Resolve(serverName);
            if (resolved == null) return 0;
            return await CaptureDeadlocksAsync(resolved.Value.conn, resolved.Value.server, ct);
        }

        /// <summary>Top Query-Store queries by accumulated Lock wait, across QS-enabled databases.</summary>
        public async Task<List<QueryStoreLockWait>> GetQueryStoreLockWaitsAsync(
            string serverName, int topPerDb = 20, CancellationToken ct = default)
        {
            var results = new List<QueryStoreLockWait>();
            var resolved = Resolve(serverName);
            if (resolved == null) return results;
            var conn = resolved.Value.conn;
            var server = resolved.Value.server;

            // Enumerate user databases with Query Store on.
            var dbs = new List<string>();
            try
            {
                using var c = new SqlConnection(conn.GetConnectionString(server, "master"));
                await c.OpenAsync(ct);
                using var cmd = new SqlCommand(
                    @"SELECT name FROM sys.databases
                      WHERE is_query_store_on = 1 AND state_desc = 'ONLINE' AND database_id > 4", c)
                    { CommandTimeout = 15 };
                using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) dbs.Add(r.GetString(0));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "QS database enumeration failed for {Server}", serverName);
                return results;
            }

            const string qsSql = @"
                SELECT TOP (@top)
                       q.query_id,
                       SUBSTRING(qt.query_sql_text, 1, 200)   AS sql_text,
                       SUM(ws.total_query_wait_time_ms)        AS total_lock_wait_ms,
                       AVG(ws.avg_query_wait_time_ms)          AS avg_lock_wait_ms
                FROM sys.query_store_wait_stats ws
                JOIN sys.query_store_plan p        ON p.plan_id      = ws.plan_id
                JOIN sys.query_store_query q       ON q.query_id     = p.query_id
                JOIN sys.query_store_query_text qt ON qt.query_text_id = q.query_text_id
                WHERE ws.wait_category = 3   -- Lock
                GROUP BY q.query_id, SUBSTRING(qt.query_sql_text, 1, 200)
                ORDER BY total_lock_wait_ms DESC";

            foreach (var db in dbs)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    using var c = new SqlConnection(conn.GetConnectionString(server, db));
                    await c.OpenAsync(ct);
                    using var cmd = new SqlCommand(qsSql, c) { CommandTimeout = 20 };
                    cmd.Parameters.AddWithValue("@top", topPerDb);
                    using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        results.Add(new QueryStoreLockWait
                        {
                            Database = db,
                            QueryId = r.IsDBNull(0) ? 0 : Convert.ToInt64(r.GetValue(0)),
                            SqlText = r.IsDBNull(1) ? null : r.GetString(1),
                            TotalLockWaitMs = r.IsDBNull(2) ? 0 : Convert.ToDouble(r.GetValue(2)),
                            AvgLockWaitMs = r.IsDBNull(3) ? 0 : Convert.ToDouble(r.GetValue(3)),
                        });
                    }
                }
                catch (Exception ex)
                {
                    // QS off / pre-2017 / no permission on this DB — skip it.
                    _logger.LogDebug(ex, "QS lock-wait query failed for {Server}.{Db}", serverName, db);
                }
            }

            results.Sort((a, b) => b.TotalLockWaitMs.CompareTo(a.TotalLockWaitMs));
            return results;
        }

        // ── Capture helpers (shared by collector + on-demand) ────────────────────────

        private async Task<List<BlockingInfo>> CaptureBlockingAsync(
            ServerConnection conn, string serverName, CancellationToken ct)
        {
            var blockers = new List<BlockingInfo>();
            using var sqlConn = new SqlConnection(conn.GetConnectionString(serverName, "master"));
            await sqlConn.OpenAsync(ct);
            using (var cmd = new SqlCommand(BlockingChainSql, sqlConn) { CommandTimeout = 15 })
            using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
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
            }

            if (blockers.Count > 0)
            {
                var capturedUtc = DateTime.UtcNow;
                foreach (var b in blockers)
                {
                    await _history.RecordBlockingEventAsync(new BlockingEvent
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
            }
            return blockers;
        }

        private async Task<int> CaptureDeadlocksAsync(
            ServerConnection conn, string serverName, CancellationToken ct)
        {
            int inserted = 0;
            using var sqlConn = new SqlConnection(conn.GetConnectionString(serverName, "master"));
            await sqlConn.OpenAsync(ct);
            using var cmd = new SqlCommand(DeadlockRingBufferSql, sqlConn) { CommandTimeout = 30 };
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                // Store event_time as ISO-8601 UTC so the history range filter (string compare on
                // the same format as blocking_events.captured_utc) and the dedup key are consistent.
                var raw = reader.GetValue(0);
                var eventTime = raw is DateTime dt
                    ? DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToString("o")
                    : raw?.ToString();
                var xml = reader.GetString(1);
                if (string.IsNullOrWhiteSpace(eventTime) || string.IsNullOrWhiteSpace(xml)) continue;
                if (await _history.RecordDeadlockAsync(serverName, eventTime!, xml!)) inserted++;
            }
            return inserted;
        }

        // ── Resolve an instance name back to its ServerConnection ────────────────────

        private (ServerConnection conn, string server)? Resolve(string instance)
        {
            if (string.IsNullOrWhiteSpace(instance)) return null;
            foreach (var c in _connections.GetConnections())
                foreach (var s in c.GetServerList())
                    if (string.Equals(s, instance, StringComparison.OrdinalIgnoreCase))
                        return (c, s);
            return null;
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            _cts.Dispose();
        }

        // ── SQL ──────────────────────────────────────────────────────────────────────

        // Same shape as SessionDataService.GetBlockingChainAsync (read-only, NOLOCK).
        private const string BlockingChainSql = @"
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
            LEFT JOIN sys.dm_exec_sessions   s_blocker WITH (NOLOCK) ON s_blocker.session_id = wt.blocking_session_id
            LEFT JOIN sys.dm_exec_requests   r_blocker WITH (NOLOCK) ON r_blocker.session_id = wt.blocking_session_id
            LEFT JOIN sys.dm_exec_sessions   s_blocked WITH (NOLOCK) ON s_blocked.session_id = wt.session_id
            LEFT JOIN sys.dm_exec_requests   r_blocked WITH (NOLOCK) ON r_blocked.session_id = wt.session_id
            OUTER APPLY sys.dm_exec_input_buffer(wt.blocking_session_id, NULL) ib_blocker
            OUTER APPLY sys.dm_exec_input_buffer(wt.session_id,          NULL) ib_blocked
            WHERE wt.blocking_session_id IS NOT NULL
              AND wt.blocking_session_id > 0";

        // Deadlocks from the system_health ring buffer. Fast-path: skip the XML cast entirely when
        // the buffer contains no deadlock reports. Returns (event_time, deadlock_xml-as-text).
        private const string DeadlockRingBufferSql = @"
            IF EXISTS (
                SELECT 1
                FROM sys.dm_xe_session_targets st WITH (NOLOCK)
                JOIN sys.dm_xe_sessions s WITH (NOLOCK) ON s.address = st.event_session_address
                WHERE s.name = N'system_health'
                  AND st.target_name = N'ring_buffer'
                  AND CAST(target_data AS NVARCHAR(MAX)) LIKE N'%xml_deadlock_report%'
            )
            BEGIN
                ;WITH rb AS (
                    SELECT CAST(target_data AS XML) AS td
                    FROM sys.dm_xe_session_targets st WITH (NOLOCK)
                    JOIN sys.dm_xe_sessions s WITH (NOLOCK) ON s.address = st.event_session_address
                    WHERE s.name = N'system_health' AND st.target_name = N'ring_buffer'
                )
                SELECT TOP (500)
                    xed.value('(@timestamp)[1]', 'datetime2')                                         AS event_time,
                    CAST(xed.query('(data[@name=""xml_report""]/value/deadlock)[1]') AS NVARCHAR(MAX)) AS deadlock_xml
                FROM rb
                CROSS APPLY rb.td.nodes('RingBufferTarget/event[@name=""xml_deadlock_report""]') xe(xed)
                ORDER BY event_time DESC;
            END";
    }
}
