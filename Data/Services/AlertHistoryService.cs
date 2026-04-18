/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Persists alert history to a local SQLite database (alert-history.db).
    /// Supports insert, update, query by date range, and automatic purge of records older than retention period.
    /// </summary>
    public class AlertHistoryService : IDisposable
    {
        private readonly ILogger<AlertHistoryService> _logger;
        private readonly string _connectionString;
        private readonly System.Timers.Timer _purgeTimer;
        private readonly int _retentionDays;

        public AlertHistoryService(ILogger<AlertHistoryService> logger, int retentionDays = 365)
        {
            _logger = logger;
            _retentionDays = retentionDays;
            var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "alert-history.db");
            _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared";
            InitializeSchema();

            // Purge old records once a day
            _purgeTimer = new System.Timers.Timer(TimeSpan.FromHours(24).TotalMilliseconds);
            _purgeTimer.Elapsed += (_, _) => PurgeOldRecords();
            _purgeTimer.Start();
        }

        private void InitializeSchema()
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=NORMAL;

                    CREATE TABLE IF NOT EXISTS alert_history (
                        id              INTEGER PRIMARY KEY AUTOINCREMENT,
                        alert_id        TEXT NOT NULL,
                        alert_name      TEXT NOT NULL,
                        server_name     TEXT NOT NULL,
                        severity        TEXT NOT NULL,
                        value           REAL NOT NULL,
                        threshold_value REAL NOT NULL,
                        status          TEXT NOT NULL DEFAULT 'Active',
                        hit_count       INTEGER NOT NULL DEFAULT 1,
                        first_triggered TEXT NOT NULL,
                        last_triggered  TEXT NOT NULL,
                        acknowledged_at TEXT,
                        resolved_at     TEXT,
                        message         TEXT NOT NULL DEFAULT ''
                    );

                    CREATE INDEX IF NOT EXISTS idx_alert_history_alert_server
                        ON alert_history (alert_id, server_name);
                    CREATE INDEX IF NOT EXISTS idx_alert_history_status
                        ON alert_history (status);
                    CREATE INDEX IF NOT EXISTS idx_alert_history_last_triggered
                        ON alert_history (last_triggered);
                ";
                cmd.ExecuteNonQuery();

                _logger.LogInformation("Alert history database initialized");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize alert history database");
            }
        }

        /// <summary>
        /// Inserts or updates an alert history record. If an Active/Acknowledged record exists
        /// for the same alertId+server, increments hit count. Otherwise inserts a new row.
        /// Returns the record ID.
        /// </summary>
        public long UpsertAlert(AlertState state)
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();

                // Atomic upsert: try UPDATE first, fall back to INSERT if no rows affected.
                // Runs inside a transaction to prevent race conditions.
                using var tx = conn.BeginTransaction();

                using var updateCmd = conn.CreateCommand();
                updateCmd.Transaction = tx;
                updateCmd.CommandText = @"
                    UPDATE alert_history SET
                        hit_count = hit_count + 1,
                        value = @value,
                        last_triggered = @lastTriggered,
                        severity = @severity,
                        message = @message
                    WHERE alert_id = @alertId AND server_name = @server
                      AND status IN ('Active', 'Acknowledged')";
                updateCmd.Parameters.AddWithValue("@alertId", state.AlertId);
                updateCmd.Parameters.AddWithValue("@server", state.ServerName);
                updateCmd.Parameters.AddWithValue("@value", state.LastValue);
                updateCmd.Parameters.AddWithValue("@lastTriggered", state.LastTriggered.ToString("o"));
                updateCmd.Parameters.AddWithValue("@severity", state.Severity);
                updateCmd.Parameters.AddWithValue("@message", state.Message);

                var rowsUpdated = updateCmd.ExecuteNonQuery();
                if (rowsUpdated > 0)
                {
                    // Return the ID of the updated row
                    using var idCmd = conn.CreateCommand();
                    idCmd.Transaction = tx;
                    idCmd.CommandText = @"
                        SELECT id FROM alert_history
                        WHERE alert_id = @alertId AND server_name = @server
                          AND status IN ('Active', 'Acknowledged')
                        ORDER BY last_triggered DESC LIMIT 1";
                    idCmd.Parameters.AddWithValue("@alertId", state.AlertId);
                    idCmd.Parameters.AddWithValue("@server", state.ServerName);
                    var id = (long)idCmd.ExecuteScalar()!;
                    tx.Commit();
                    return id;
                }

                // No existing active record — insert new
                using var insertCmd = conn.CreateCommand();
                insertCmd.Transaction = tx;
                insertCmd.CommandText = @"
                    INSERT INTO alert_history
                        (alert_id, alert_name, server_name, severity, value, threshold_value,
                         status, hit_count, first_triggered, last_triggered, message)
                    VALUES
                        (@alertId, @alertName, @server, @severity, @value, @threshold,
                         @status, 1, @first, @last, @message);
                    SELECT last_insert_rowid();";
                insertCmd.Parameters.AddWithValue("@alertId", state.AlertId);
                insertCmd.Parameters.AddWithValue("@alertName", state.AlertName);
                insertCmd.Parameters.AddWithValue("@server", state.ServerName);
                insertCmd.Parameters.AddWithValue("@severity", state.Severity);
                insertCmd.Parameters.AddWithValue("@value", state.LastValue);
                insertCmd.Parameters.AddWithValue("@threshold", state.ThresholdValue);
                insertCmd.Parameters.AddWithValue("@status", state.Status.ToString());
                insertCmd.Parameters.AddWithValue("@first", state.FirstTriggered.ToString("o"));
                insertCmd.Parameters.AddWithValue("@last", state.LastTriggered.ToString("o"));
                insertCmd.Parameters.AddWithValue("@message", state.Message);
                var newId = (long)insertCmd.ExecuteScalar()!;
                tx.Commit();
                return newId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upsert alert history for {AlertId} on {Server}",
                    state.AlertId, state.ServerName);
                return -1;
            }
        }

        public void ResolveAlert(string alertId, string serverName)
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE alert_history SET status = 'Resolved', resolved_at = @now
                    WHERE alert_id = @alertId AND server_name = @server
                      AND status IN ('Active', 'Acknowledged')";
                cmd.Parameters.AddWithValue("@alertId", alertId);
                cmd.Parameters.AddWithValue("@server", serverName);
                cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve alert {AlertId} on {Server}", alertId, serverName);
            }
        }

        public void AcknowledgeAlert(string alertId, string serverName)
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE alert_history SET status = 'Acknowledged', acknowledged_at = @now
                    WHERE alert_id = @alertId AND server_name = @server
                      AND status = 'Active'";
                cmd.Parameters.AddWithValue("@alertId", alertId);
                cmd.Parameters.AddWithValue("@server", serverName);
                cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to acknowledge alert {AlertId} on {Server}", alertId, serverName);
            }
        }

        public void AcknowledgeAll()
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE alert_history SET status = 'Acknowledged', acknowledged_at = @now
                    WHERE status = 'Active'";
                cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to acknowledge all alerts");
            }
        }

        /// <summary>
        /// Auto-acknowledge alerts that have been active longer than the specified hours.
        /// </summary>
        public int AutoAcknowledge(int hoursThreshold)
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE alert_history SET status = 'Acknowledged', acknowledged_at = @now
                    WHERE status = 'Active'
                      AND first_triggered < @cutoff";
                cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
                cmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.AddHours(-hoursThreshold).ToString("o"));
                return cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to auto-acknowledge alerts");
                return 0;
            }
        }

        public List<AlertHistoryRecord> GetActiveAlerts()
        {
            return QueryRecords("WHERE status IN ('Active', 'Acknowledged') ORDER BY last_triggered DESC");
        }

        public List<AlertHistoryRecord> GetRecentHistory(int maxRecords = 100)
        {
            return QueryRecords($"ORDER BY last_triggered DESC LIMIT {maxRecords}");
        }

        public List<AlertHistoryRecord> GetHistoryByDateRange(DateTime from, DateTime to)
        {
            return QueryRecords(
                "WHERE last_triggered BETWEEN @from AND @to ORDER BY last_triggered DESC",
                ("@from", from.ToString("o")),
                ("@to", to.ToString("o")));
        }

        public List<AlertHistoryRecord> GetHistoryByAlert(string alertId, int maxRecords = 50)
        {
            return QueryRecords(
                $"WHERE alert_id = @alertId ORDER BY last_triggered DESC LIMIT {maxRecords}",
                ("@alertId", alertId));
        }

        public int GetActiveCount()
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM alert_history WHERE status = 'Active'";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query active alert count from history database");
                return 0;
            }
        }

        private List<AlertHistoryRecord> QueryRecords(string whereClause, params (string name, string value)[] parameters)
        {
            var records = new List<AlertHistoryRecord>();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT * FROM alert_history {whereClause}";
                foreach (var (name, value) in parameters)
                    cmd.Parameters.AddWithValue(name, value);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    records.Add(new AlertHistoryRecord
                    {
                        Id = reader.GetInt64(reader.GetOrdinal("id")),
                        AlertId = reader.GetString(reader.GetOrdinal("alert_id")),
                        AlertName = reader.GetString(reader.GetOrdinal("alert_name")),
                        ServerName = reader.GetString(reader.GetOrdinal("server_name")),
                        Severity = reader.GetString(reader.GetOrdinal("severity")),
                        Value = reader.GetDouble(reader.GetOrdinal("value")),
                        ThresholdValue = reader.GetDouble(reader.GetOrdinal("threshold_value")),
                        Status = reader.GetString(reader.GetOrdinal("status")),
                        HitCount = reader.GetInt32(reader.GetOrdinal("hit_count")),
                        FirstTriggered = DateTime.Parse(reader.GetString(reader.GetOrdinal("first_triggered"))),
                        LastTriggered = DateTime.Parse(reader.GetString(reader.GetOrdinal("last_triggered"))),
                        AcknowledgedAt = reader.IsDBNull(reader.GetOrdinal("acknowledged_at"))
                            ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("acknowledged_at"))),
                        ResolvedAt = reader.IsDBNull(reader.GetOrdinal("resolved_at"))
                            ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("resolved_at"))),
                        Message = reader.GetString(reader.GetOrdinal("message"))
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query alert history");
            }
            return records;
        }

        /// <summary>
        /// Enforces retention based on the current global default (called each evaluation cycle).
        /// Only purges if the provided retentionDays differs from the configured default.
        /// </summary>
        public void EnforceRetention(int retentionDays)
        {
            if (retentionDays == _retentionDays) return; // no change, daily timer handles it
            PurgeOldRecords(retentionDays);
        }

        private void PurgeOldRecords() => PurgeOldRecords(_retentionDays);

        private void PurgeOldRecords(int retentionDays)
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    DELETE FROM alert_history
                    WHERE last_triggered < @cutoff AND status = 'Resolved'";
                cmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.AddDays(-retentionDays).ToString("o"));
                var deleted = cmd.ExecuteNonQuery();
                if (deleted > 0)
                    _logger.LogInformation("Purged {Count} resolved alert history records older than {Days} days",
                        deleted, retentionDays);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to purge old alert history records");
            }
        }

        public void Dispose()
        {
            _purgeTimer?.Stop();
            _purgeTimer?.Dispose();
        }
    }
}
