/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SQLTriage.Data;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// A1.2 — Persists app availability evidence to a local SQLite store.
    /// Writes session_start / heartbeat (every 5 min) / session_end rows so that
    /// 30-day uptime % can be computed from auditable data without external APM.
    /// Schema: uptime_events(timestamp_utc TEXT PK, event_type TEXT, session_id TEXT)
    /// </summary>
    public sealed class UptimeTrackerService : IDisposable
    {
        private readonly ILogger<UptimeTrackerService> _logger;
        private readonly string _connectionString;
        private readonly string _sessionId;
        private readonly Timer? _heartbeatTimer;
        private bool _disposed;

        // Configurable heartbeat interval; tests can pass a short value.
        private const int DefaultHeartbeatMinutes = 5;

        public UptimeTrackerService(
            ILogger<UptimeTrackerService> logger,
            bool startTimer = true)
        {
            _logger = logger;
            _sessionId = Guid.NewGuid().ToString("N");

            var dbPath = Path.Combine(AppContext.BaseDirectory, "Data", "Caching", "uptime.db");
            var dir = Path.GetDirectoryName(dbPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared";
            InitializeSchema();
            WriteEvent("session_start");
            _logger.LogInformation("[UPTIME] Session {SessionId} started", _sessionId);

            if (startTimer)
            {
                var intervalMs = TimeSpan.FromMinutes(DefaultHeartbeatMinutes).TotalMilliseconds;
                _heartbeatTimer = new Timer(_ => WriteEvent("heartbeat"), null,
                    (long)intervalMs, (long)intervalMs);
            }
        }

        // ── Schema ────────────────────────────────────────────────────────

        private void InitializeSchema()
        {
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();

                // R-L3: Detect old single-column PK schema and drop+recreate.
                // uptime.db is regenerable (no long-term business data), so this is safe.
                // Old schema had timestamp_utc TEXT PRIMARY KEY alone — vulnerable to
                // sub-millisecond clock collisions and backward clock jumps silently
                // dropping rows via INSERT OR IGNORE. New schema uses AUTOINCREMENT id PK
                // with a UNIQUE constraint on (timestamp_utc, event_type, session_id).
                cmd.CommandText = @"
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=NORMAL;
                    PRAGMA foreign_keys=ON;

                    CREATE TABLE IF NOT EXISTS uptime_events (
                        id            INTEGER PRIMARY KEY AUTOINCREMENT,
                        timestamp_utc TEXT NOT NULL,
                        event_type    TEXT NOT NULL,
                        session_id    TEXT NOT NULL,
                        UNIQUE (timestamp_utc, event_type, session_id)
                    );

                    CREATE INDEX IF NOT EXISTS idx_uptime_ts
                        ON uptime_events (timestamp_utc);";
                cmd.ExecuteNonQuery();

                // Detect old schema (no id column) and migrate by drop+recreate.
                using var checkCmd = conn.CreateCommand();
                checkCmd.CommandText = "PRAGMA table_info(uptime_events);";
                bool hasIdColumn = false;
                using (var reader = checkCmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (reader.GetString(1) == "id") { hasIdColumn = true; break; }
                    }
                }
                if (!hasIdColumn)
                {
                    _logger.LogWarning("[UPTIME] Old single-column PK schema detected — migrating (drop+recreate uptime_events)");
                    using var dropCmd = conn.CreateCommand();
                    dropCmd.CommandText = @"
                        DROP TABLE IF EXISTS uptime_events;
                        CREATE TABLE uptime_events (
                            id            INTEGER PRIMARY KEY AUTOINCREMENT,
                            timestamp_utc TEXT NOT NULL,
                            event_type    TEXT NOT NULL,
                            session_id    TEXT NOT NULL,
                            UNIQUE (timestamp_utc, event_type, session_id)
                        );
                        CREATE INDEX IF NOT EXISTS idx_uptime_ts
                            ON uptime_events (timestamp_utc);";
                    dropCmd.ExecuteNonQuery();
                    _logger.LogInformation("[UPTIME] Migration to compound-key schema complete");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UPTIME] Schema initialisation failed");
            }
        }

        // ── Event writer ──────────────────────────────────────────────────

        private void WriteEvent(string eventType)
        {
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                // timestamp_utc is PK — use INSERT OR IGNORE so duplicate heartbeats
                // (e.g. from rapid test runs) are silently discarded.
                cmd.CommandText = @"
                    INSERT OR IGNORE INTO uptime_events (timestamp_utc, event_type, session_id)
                    VALUES (@ts, @type, @sid)";
                cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("o"));
                cmd.Parameters.AddWithValue("@type", eventType);
                cmd.Parameters.AddWithValue("@sid", _sessionId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[UPTIME] Failed to write {EventType} row", eventType);
            }
        }

        // ── Availability calculation ───────────────────────────────────────

        /// <summary>
        /// Computes uptime percentage for the window [from, to].
        /// A 5-minute slot is considered "up" if at least one heartbeat row falls
        /// within ±2.5 minutes of the slot centre.
        /// Returns 0.0 – 100.0.
        /// </summary>
        public double GetUptimePercent(DateTime from, DateTime to)
        {
            if (to <= from) return 0.0;

            try
            {
                // Load all event timestamps in range (include session_start/end as proof of life).
                var events = new System.Collections.Generic.List<DateTime>();
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT timestamp_utc FROM uptime_events
                    WHERE timestamp_utc >= @from AND timestamp_utc <= @to
                    ORDER BY timestamp_utc";
                cmd.Parameters.AddWithValue("@from", from.ToString("o"));
                cmd.Parameters.AddWithValue("@to", to.ToString("o"));
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    events.Add(DateTime.Parse(reader.GetString(0)));

                if (events.Count == 0) return 0.0;

                // Walk 5-minute slots and count covered ones.
                var slotSize = TimeSpan.FromMinutes(DefaultHeartbeatMinutes);
                var halfSlot = TimeSpan.FromMinutes(DefaultHeartbeatMinutes / 2.0);
                var totalSlots = 0;
                var coveredSlots = 0;

                var slotCentre = from.Add(halfSlot);
                while (slotCentre <= to)
                {
                    totalSlots++;
                    var slotFrom = slotCentre - halfSlot;
                    var slotTo = slotCentre + halfSlot;
                    // Binary search would be optimal for large lists; for typical
                    // 30-day windows (~8 640 events) linear scan is fine.
                    foreach (var ev in events)
                    {
                        if (ev >= slotFrom && ev <= slotTo)
                        {
                            coveredSlots++;
                            break;
                        }
                    }
                    slotCentre = slotCentre.Add(slotSize);
                }

                return totalSlots == 0 ? 0.0 : 100.0 * coveredSlots / totalSlots;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[UPTIME] GetUptimePercent failed");
                return 0.0;
            }
        }

        // ── Shutdown ──────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _heartbeatTimer?.Dispose();
            WriteEvent("session_end");
            _logger.LogInformation("[UPTIME] Session {SessionId} ended", _sessionId);
        }
    }
}
