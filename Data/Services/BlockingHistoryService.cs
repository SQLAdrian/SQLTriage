/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    // BM:BlockingHistoryService.Class — persists blocking events to blocking-history.db
    /// <summary>
    /// Persists blocker→blocked pairs to a local SQLCipher-encrypted SQLite database
    /// (<c>blocking-history.db</c>). Provides timeline queries and top-offender aggregation.
    /// Noise threshold: events with duration_seconds &lt; 5 are silently dropped.
    /// Retention is configurable via <c>Blocking:RetentionDays</c> (default 30).
    /// </summary>
    public class BlockingHistoryService : IDisposable
    {
        private readonly ILogger<BlockingHistoryService> _logger;
        private readonly string _connectionString;
        private readonly int _retentionDays;
        private readonly System.Timers.Timer _purgeTimer;

        private const int NoiseThresholdSeconds = 5;
        private const int SqlTextMaxLength = 2000;

        public BlockingHistoryService(
            ILogger<BlockingHistoryService> logger,
            int retentionDays = 30,
            string? dbPath = null)
        {
            _logger = logger;
            _retentionDays = retentionDays;
            var resolvedPath = dbPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "blocking-history.db");
            _connectionString = $"Data Source={resolvedPath};Mode=ReadWriteCreate;Cache=Shared";
            InitializeSchema();

            _purgeTimer = new System.Timers.Timer(TimeSpan.FromHours(24).TotalMilliseconds);
            _purgeTimer.Elapsed += (_, _) => PurgeOldRecords();
            _purgeTimer.Start();
        }

        // ── Schema ────────────────────────────────────────────────────────────────

        private void InitializeSchema()
        {
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=NORMAL;
                    PRAGMA foreign_keys=ON;
                    PRAGMA auto_vacuum=INCREMENTAL;

                    CREATE TABLE IF NOT EXISTS blocking_events (
                        id               INTEGER PRIMARY KEY AUTOINCREMENT,
                        server_name      TEXT    NOT NULL,
                        captured_utc     TEXT    NOT NULL,
                        blocker_spid     INTEGER NOT NULL,
                        blocked_spid     INTEGER NOT NULL,
                        blocker_login    TEXT,
                        blocked_login    TEXT,
                        blocker_database TEXT,
                        blocked_database TEXT,
                        wait_type        TEXT,
                        wait_resource    TEXT,
                        blocker_sql_text TEXT,
                        blocked_sql_text TEXT,
                        duration_seconds INTEGER NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS idx_blocking_server_time
                        ON blocking_events (server_name, captured_utc DESC);
                    CREATE INDEX IF NOT EXISTS idx_blocking_blocker
                        ON blocking_events (server_name, blocker_spid, captured_utc DESC);

                    -- Deadlocks captured from the system_health ring buffer. event_time is the
                    -- deadlock's own timestamp (from the XEvent), used as the dedup key so the same
                    -- ring-buffer entry isn't stored twice across polls.
                    CREATE TABLE IF NOT EXISTS deadlock_events (
                        id            INTEGER PRIMARY KEY AUTOINCREMENT,
                        server_name   TEXT NOT NULL,
                        event_time    TEXT NOT NULL,
                        captured_utc  TEXT NOT NULL,
                        victim_spid   TEXT,
                        process_count INTEGER,
                        deadlock_xml  TEXT NOT NULL,
                        UNIQUE (server_name, event_time)
                    );
                    CREATE INDEX IF NOT EXISTS idx_deadlock_server_time
                        ON deadlock_events (server_name, event_time DESC);
                ";
                cmd.ExecuteNonQuery();
                _logger.LogInformation("Blocking history database initialized");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize blocking history database");
            }
        }

        // ── Write ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Persists one blocking event. Silently drops events whose
        /// <see cref="BlockingEvent.DurationSeconds"/> is below the noise threshold (5 s).
        /// </summary>
        public async Task RecordBlockingEventAsync(BlockingEvent ev)
        {
            if (ev.DurationSeconds < NoiseThresholdSeconds)
                return;

            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO blocking_events
                        (server_name, captured_utc, blocker_spid, blocked_spid,
                         blocker_login, blocked_login, blocker_database, blocked_database,
                         wait_type, wait_resource, blocker_sql_text, blocked_sql_text,
                         duration_seconds)
                    VALUES
                        (@server, @captured, @blockerSpid, @blockedSpid,
                         @blockerLogin, @blockedLogin, @blockerDb, @blockedDb,
                         @waitType, @waitResource, @blockerSql, @blockedSql,
                         @duration)";

                cmd.Parameters.AddWithValue("@server", ev.ServerName);
                cmd.Parameters.AddWithValue("@captured", ev.CapturedUtc.ToString("o"));
                cmd.Parameters.AddWithValue("@blockerSpid", ev.BlockerSpid);
                cmd.Parameters.AddWithValue("@blockedSpid", ev.BlockedSpid);
                cmd.Parameters.AddWithValue("@blockerLogin", (object?)ev.BlockerLogin ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@blockedLogin", (object?)ev.BlockedLogin ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@blockerDb", (object?)ev.BlockerDatabase ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@blockedDb", (object?)ev.BlockedDatabase ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@waitType", (object?)ev.WaitType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@waitResource", (object?)ev.WaitResource ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@blockerSql", (object?)Truncate(ev.BlockerSqlText) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@blockedSql", (object?)Truncate(ev.BlockedSqlText) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@duration", ev.DurationSeconds);

                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to record blocking event for server {Server}", ev.ServerName);
            }
        }

        // ── Read ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns up to <paramref name="maxRows"/> events for a server in UTC time range,
        /// ordered newest-first.
        /// </summary>
        public async Task<List<BlockingEvent>> GetEventsAsync(
            string serverName,
            DateTime fromUtc,
            DateTime toUtc,
            int maxRows = 1000)
        {
            var results = new List<BlockingEvent>();
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT id, server_name, captured_utc, blocker_spid, blocked_spid,
                           blocker_login, blocked_login, blocker_database, blocked_database,
                           wait_type, wait_resource, blocker_sql_text, blocked_sql_text,
                           duration_seconds
                    FROM blocking_events
                    WHERE server_name   = @server
                      AND captured_utc >= @from
                      AND captured_utc <= @to
                    ORDER BY captured_utc DESC
                    LIMIT @maxRows";
                cmd.Parameters.AddWithValue("@server", serverName);
                cmd.Parameters.AddWithValue("@from", fromUtc.ToString("o"));
                cmd.Parameters.AddWithValue("@to", toUtc.ToString("o"));
                cmd.Parameters.AddWithValue("@maxRows", maxRows);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    results.Add(MapRow(reader));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query blocking events for {Server}", serverName);
            }
            return results;
        }

        /// <summary>
        /// Returns the top <paramref name="topN"/> blocking offenders for a server since
        /// <paramref name="fromUtc"/>, ranked by total duration descending.
        /// </summary>
        public async Task<List<BlockingOffender>> GetTopOffendersAsync(
            string serverName,
            DateTime fromUtc,
            int topN = 10)
        {
            var results = new List<BlockingOffender>();
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT blocker_login,
                           SUBSTR(COALESCE(blocker_sql_text,''), 1, 100) AS sql_excerpt,
                           COUNT(*)                                        AS event_count,
                           SUM(duration_seconds)                          AS total_duration
                    FROM blocking_events
                    WHERE server_name  = @server
                      AND captured_utc >= @from
                    GROUP BY blocker_login, SUBSTR(COALESCE(blocker_sql_text,''), 1, 100)
                    ORDER BY total_duration DESC
                    LIMIT @topN";
                cmd.Parameters.AddWithValue("@server", serverName);
                cmd.Parameters.AddWithValue("@from", fromUtc.ToString("o"));
                cmd.Parameters.AddWithValue("@topN", topN);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    results.Add(new BlockingOffender
                    {
                        BlockerLogin = reader.IsDBNull(0) ? null : reader.GetString(0),
                        SqlExcerpt = reader.IsDBNull(1) ? null : reader.GetString(1),
                        EventCount = reader.GetInt32(2),
                        TotalDurationSeconds = reader.GetInt32(3)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query top offenders for {Server}", serverName);
            }
            return results;
        }

        // ── Deadlocks ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Persists one deadlock captured from the system_health ring buffer. Idempotent on
        /// (server_name, event_time) so re-polling the same ring buffer never duplicates.
        /// Returns true if a new row was inserted.
        /// </summary>
        public async Task<bool> RecordDeadlockAsync(
            string serverName, string eventTimeIso, string deadlockXml,
            string? victimSpid = null, int processCount = 0)
        {
            if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(deadlockXml) ||
                string.IsNullOrWhiteSpace(eventTimeIso))
                return false;
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT OR IGNORE INTO deadlock_events
                        (server_name, event_time, captured_utc, victim_spid, process_count, deadlock_xml)
                    VALUES (@server, @eventTime, @captured, @victim, @procCount, @xml)";
                cmd.Parameters.AddWithValue("@server", serverName);
                cmd.Parameters.AddWithValue("@eventTime", eventTimeIso);
                cmd.Parameters.AddWithValue("@captured", DateTime.UtcNow.ToString("o"));
                cmd.Parameters.AddWithValue("@victim", (object?)victimSpid ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@procCount", processCount);
                cmd.Parameters.AddWithValue("@xml", deadlockXml);
                return await cmd.ExecuteNonQueryAsync() > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to record deadlock for server {Server}", serverName);
                return false;
            }
        }

        /// <summary>
        /// Returns stored deadlocks for a server in the time range (newest first) as a DataTable
        /// with <c>event_time</c> and <c>deadlock_xml</c> columns — the shape DeadlockViewer renders.
        /// </summary>
        public async Task<DataTable> GetDeadlocksAsync(
            string serverName, DateTime fromUtc, DateTime toUtc, int maxRows = 500)
        {
            var table = new DataTable();
            table.Columns.Add("event_time", typeof(string));
            table.Columns.Add("deadlock_xml", typeof(string));
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT event_time, deadlock_xml
                    FROM deadlock_events
                    WHERE server_name = @server
                      AND event_time >= @from
                      AND event_time <= @to
                    ORDER BY event_time DESC
                    LIMIT @maxRows";
                cmd.Parameters.AddWithValue("@server", serverName);
                cmd.Parameters.AddWithValue("@from", fromUtc.ToString("o"));
                cmd.Parameters.AddWithValue("@to", toUtc.ToString("o"));
                cmd.Parameters.AddWithValue("@maxRows", maxRows);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    table.Rows.Add(reader.GetString(0), reader.GetString(1));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query deadlocks for {Server}", serverName);
            }
            return table;
        }

        // ── Purge ─────────────────────────────────────────────────────────────────

        private void PurgeOldRecords()
        {
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    DELETE FROM blocking_events
                    WHERE captured_utc < @cutoff";
                cmd.Parameters.AddWithValue("@cutoff",
                    DateTime.UtcNow.AddDays(-_retentionDays).ToString("o"));
                var deleted = cmd.ExecuteNonQuery();
                if (deleted > 0)
                    _logger.LogInformation(
                        "Purged {Count} blocking event records older than {Days} days",
                        deleted, _retentionDays);

                using var dlCmd = conn.CreateCommand();
                dlCmd.CommandText = @"
                    DELETE FROM deadlock_events
                    WHERE captured_utc < @cutoff";
                dlCmd.Parameters.AddWithValue("@cutoff",
                    DateTime.UtcNow.AddDays(-_retentionDays).ToString("o"));
                dlCmd.ExecuteNonQuery();

                using var vac = conn.CreateCommand();
                vac.CommandText = "PRAGMA incremental_vacuum;";
                vac.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to purge blocking history records");
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static BlockingEvent MapRow(SqliteDataReader r) => new()
        {
            Id = r.GetInt64(r.GetOrdinal("id")),
            ServerName = r.GetString(r.GetOrdinal("server_name")),
            CapturedUtc = DateTime.Parse(r.GetString(r.GetOrdinal("captured_utc"))),
            BlockerSpid = r.GetInt32(r.GetOrdinal("blocker_spid")),
            BlockedSpid = r.GetInt32(r.GetOrdinal("blocked_spid")),
            BlockerLogin = r.IsDBNull(r.GetOrdinal("blocker_login")) ? null : r.GetString(r.GetOrdinal("blocker_login")),
            BlockedLogin = r.IsDBNull(r.GetOrdinal("blocked_login")) ? null : r.GetString(r.GetOrdinal("blocked_login")),
            BlockerDatabase = r.IsDBNull(r.GetOrdinal("blocker_database")) ? null : r.GetString(r.GetOrdinal("blocker_database")),
            BlockedDatabase = r.IsDBNull(r.GetOrdinal("blocked_database")) ? null : r.GetString(r.GetOrdinal("blocked_database")),
            WaitType = r.IsDBNull(r.GetOrdinal("wait_type")) ? null : r.GetString(r.GetOrdinal("wait_type")),
            WaitResource = r.IsDBNull(r.GetOrdinal("wait_resource")) ? null : r.GetString(r.GetOrdinal("wait_resource")),
            BlockerSqlText = r.IsDBNull(r.GetOrdinal("blocker_sql_text")) ? null : r.GetString(r.GetOrdinal("blocker_sql_text")),
            BlockedSqlText = r.IsDBNull(r.GetOrdinal("blocked_sql_text")) ? null : r.GetString(r.GetOrdinal("blocked_sql_text")),
            DurationSeconds = r.GetInt32(r.GetOrdinal("duration_seconds"))
        };

        private static string? Truncate(string? s) =>
            s is null ? null
            : s.Length <= SqlTextMaxLength ? s
            : s[..SqlTextMaxLength];

        public void Dispose()
        {
            _purgeTimer?.Stop();
            _purgeTimer?.Dispose();
        }
    }
}
