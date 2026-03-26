/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SqlHealthAssessment.Data.Models;

namespace SqlHealthAssessment.Data.Services
{
    public class ScheduledTaskHistoryService : IDisposable
    {
        private readonly ILogger<ScheduledTaskHistoryService> _logger;
        private readonly string _connectionString;
        private readonly System.Timers.Timer _purgeTimer;
        private readonly int _retentionDays;

        public ScheduledTaskHistoryService(ILogger<ScheduledTaskHistoryService> logger, int retentionDays = 90)
        {
            _logger = logger;
            _retentionDays = retentionDays;
            var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scheduled-task-history.db");
            _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared";
            InitializeSchema();

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

                    CREATE TABLE IF NOT EXISTS task_executions (
                        id              INTEGER PRIMARY KEY AUTOINCREMENT,
                        task_id         TEXT NOT NULL,
                        task_name       TEXT NOT NULL,
                        server_name     TEXT NOT NULL,
                        status          TEXT NOT NULL DEFAULT 'Running',
                        started_at      TEXT NOT NULL,
                        completed_at    TEXT,
                        row_count       INTEGER NOT NULL DEFAULT 0,
                        csv_file_path   TEXT,
                        blob_uri        TEXT,
                        email_sent      INTEGER NOT NULL DEFAULT 0,
                        error_message   TEXT,
                        duration_seconds REAL NOT NULL DEFAULT 0
                    );

                    CREATE INDEX IF NOT EXISTS idx_task_exec_task_id ON task_executions (task_id);
                    CREATE INDEX IF NOT EXISTS idx_task_exec_started ON task_executions (started_at);
                ";
                cmd.ExecuteNonQuery();
                _logger.LogInformation("Scheduled task history database initialized");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize scheduled task history database");
            }
        }

        public long InsertExecution(ScheduledTaskExecution exec)
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO task_executions
                        (task_id, task_name, server_name, status, started_at, row_count)
                    VALUES
                        (@taskId, @taskName, @server, @status, @startedAt, 0);
                    SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("@taskId", exec.TaskId);
                cmd.Parameters.AddWithValue("@taskName", exec.TaskName);
                cmd.Parameters.AddWithValue("@server", exec.ServerName);
                cmd.Parameters.AddWithValue("@status", exec.Status);
                cmd.Parameters.AddWithValue("@startedAt", exec.StartedAt.ToString("o"));
                return (long)cmd.ExecuteScalar()!;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to insert task execution for {TaskId}", exec.TaskId);
                return -1;
            }
        }

        public void UpdateExecution(ScheduledTaskExecution exec)
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE task_executions SET
                        status = @status,
                        completed_at = @completedAt,
                        row_count = @rowCount,
                        csv_file_path = @csvPath,
                        blob_uri = @blobUri,
                        email_sent = @emailSent,
                        error_message = @error,
                        duration_seconds = @duration
                    WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", exec.Id);
                cmd.Parameters.AddWithValue("@status", exec.Status);
                cmd.Parameters.AddWithValue("@completedAt", exec.CompletedAt?.ToString("o") ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@rowCount", exec.RowCount);
                cmd.Parameters.AddWithValue("@csvPath", exec.CsvFilePath ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@blobUri", exec.BlobUri ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@emailSent", exec.EmailSent ? 1 : 0);
                cmd.Parameters.AddWithValue("@error", exec.ErrorMessage ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@duration", exec.DurationSeconds);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update task execution {Id}", exec.Id);
            }
        }

        public List<ScheduledTaskExecution> GetRecentExecutions(int maxRecords = 100)
        {
            return QueryRecords($"ORDER BY started_at DESC LIMIT {maxRecords}");
        }

        public List<ScheduledTaskExecution> GetExecutionsByTask(string taskId, int maxRecords = 50)
        {
            return QueryRecords(
                $"WHERE task_id = @taskId ORDER BY started_at DESC LIMIT {maxRecords}",
                ("@taskId", taskId));
        }

        public ScheduledTaskExecution? GetLastExecution(string taskId)
        {
            var results = QueryRecords(
                "WHERE task_id = @taskId ORDER BY started_at DESC LIMIT 1",
                ("@taskId", taskId));
            return results.FirstOrDefault();
        }

        private List<ScheduledTaskExecution> QueryRecords(string whereClause, params (string name, string value)[] parameters)
        {
            var records = new List<ScheduledTaskExecution>();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT * FROM task_executions {whereClause}";
                foreach (var (name, value) in parameters)
                    cmd.Parameters.AddWithValue(name, value);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    records.Add(new ScheduledTaskExecution
                    {
                        Id = reader.GetInt64(reader.GetOrdinal("id")),
                        TaskId = reader.GetString(reader.GetOrdinal("task_id")),
                        TaskName = reader.GetString(reader.GetOrdinal("task_name")),
                        ServerName = reader.GetString(reader.GetOrdinal("server_name")),
                        Status = reader.GetString(reader.GetOrdinal("status")),
                        StartedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("started_at"))),
                        CompletedAt = reader.IsDBNull(reader.GetOrdinal("completed_at"))
                            ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("completed_at"))),
                        RowCount = reader.GetInt32(reader.GetOrdinal("row_count")),
                        CsvFilePath = reader.IsDBNull(reader.GetOrdinal("csv_file_path")) ? null : reader.GetString(reader.GetOrdinal("csv_file_path")),
                        BlobUri = reader.IsDBNull(reader.GetOrdinal("blob_uri")) ? null : reader.GetString(reader.GetOrdinal("blob_uri")),
                        EmailSent = reader.GetInt32(reader.GetOrdinal("email_sent")) == 1,
                        ErrorMessage = reader.IsDBNull(reader.GetOrdinal("error_message")) ? null : reader.GetString(reader.GetOrdinal("error_message")),
                        DurationSeconds = reader.GetDouble(reader.GetOrdinal("duration_seconds"))
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query task execution history");
            }
            return records;
        }

        private void PurgeOldRecords()
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM task_executions WHERE started_at < @cutoff";
                cmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.AddDays(-_retentionDays).ToString("o"));
                var deleted = cmd.ExecuteNonQuery();
                if (deleted > 0)
                    _logger.LogInformation("Purged {Count} old task execution records", deleted);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to purge old task execution records");
            }
        }

        public void Dispose()
        {
            _purgeTimer?.Stop();
            _purgeTimer?.Dispose();
        }
    }
}
