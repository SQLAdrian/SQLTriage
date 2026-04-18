/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SQLTriage.Data
{
    /// <summary>
    /// Enterprise audit logging service for tracking security-relevant actions.
    /// Logs connection attempts, script executions, configuration changes, and security events.
    /// Writes to a structured JSON log file with automatic daily rotation.
    /// </summary>
    public class AuditLogService : IDisposable
    {
        private readonly string _logDirectory;
        private readonly ConcurrentQueue<AuditLogEntry> _pendingEntries = new();
        private readonly Timer _flushTimer;
        private readonly object _writeLock = new();
        private bool _disposed;

        /// <summary>
        /// Maximum number of entries to buffer before forcing a flush.
        /// </summary>
        public int MaxBufferSize { get; set; } = 50;

        /// <summary>
        /// Flush interval in milliseconds.
        /// </summary>
        public int FlushIntervalMs { get; set; } = 5000;

        /// <summary>
        /// Retention period in days (default 90 for SOC2).
        /// </summary>
        public int RetentionDays { get; set; } = 90;

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = false // Compact for log files
        };

        public AuditLogService()
        {
            _logDirectory = Path.Combine(AppContext.BaseDirectory, "audit-logs");
            if (!Directory.Exists(_logDirectory))
                Directory.CreateDirectory(_logDirectory);

            // Periodic flush timer
            _flushTimer = new Timer(_ => Flush(), null, FlushIntervalMs, FlushIntervalMs);
        }

        // ================================================================
        // HIGH-LEVEL LOGGING METHODS
        // ================================================================

        /// <summary>
        /// Logs a server connection attempt.
        /// </summary>
        public void LogConnectionAttempt(string serverName, bool success, string? errorMessage = null)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.ConnectionAttempt,
                Severity = success ? AuditSeverity.Info : AuditSeverity.Warning,
                Message = success
                    ? $"Successfully connected to server '{serverName}'"
                    : $"Failed to connect to server '{serverName}': {errorMessage}",
                Details = new Dictionary<string, string>
                {
                    ["ServerName"] = serverName,
                    ["Success"] = success.ToString(),
                    ["Error"] = errorMessage ?? string.Empty
                }
            });
        }

        /// <summary>
        /// Logs a diagnostic script execution.
        /// </summary>
        public void LogScriptExecution(string scriptName, string serverName, bool success, TimeSpan duration, string? errorMessage = null)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.ScriptExecution,
                Severity = success ? AuditSeverity.Info : AuditSeverity.Warning,
                Message = success
                    ? $"Script '{scriptName}' executed on '{serverName}' in {duration.TotalMilliseconds:F0}ms"
                    : $"Script '{scriptName}' failed on '{serverName}': {errorMessage}",
                Details = new Dictionary<string, string>
                {
                    ["ScriptName"] = scriptName,
                    ["ServerName"] = serverName,
                    ["Success"] = success.ToString(),
                    ["DurationMs"] = duration.TotalMilliseconds.ToString("F0"),
                    ["Error"] = errorMessage ?? string.Empty
                }
            });
        }

        /// <summary>
        /// Logs a script blocked by the SQL safety validator.
        /// </summary>
        public void LogScriptBlocked(string scriptName, string reason)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.SecurityBlock,
                Severity = AuditSeverity.Critical,
                Message = $"Script '{scriptName}' BLOCKED by safety validator: {reason}",
                Details = new Dictionary<string, string>
                {
                    ["ScriptName"] = scriptName,
                    ["BlockReason"] = reason
                }
            });
        }

        /// <summary>
        /// Logs a configuration change (dashboard config, connections, checks).
        /// </summary>
        public void LogConfigurationChange(string configType, string action, string? details = null)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.ConfigurationChange,
                Severity = AuditSeverity.Info,
                Message = $"Configuration '{configType}' {action}",
                Details = new Dictionary<string, string>
                {
                    ["ConfigType"] = configType,
                    ["Action"] = action,
                    ["Details"] = details ?? string.Empty
                }
            });
        }

        /// <summary>
        /// Logs a database deployment action.
        /// </summary>
        public void LogDeployment(string databaseName, string serverName, bool success, string? errorMessage = null)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.Deployment,
                Severity = success ? AuditSeverity.Info : AuditSeverity.Error,
                Message = success
                    ? $"Database '{databaseName}' deployed to '{serverName}'"
                    : $"Deployment of '{databaseName}' to '{serverName}' failed: {errorMessage}",
                Details = new Dictionary<string, string>
                {
                    ["DatabaseName"] = databaseName,
                    ["ServerName"] = serverName,
                    ["Success"] = success.ToString(),
                    ["Error"] = errorMessage ?? string.Empty
                }
            });
        }

        /// <summary>
        /// Logs application startup.
        /// </summary>
        public void LogApplicationStart()
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.ApplicationLifecycle,
                Severity = AuditSeverity.Info,
                Message = "SQLTriage application started",
                Details = new Dictionary<string, string>
                {
                    ["User"] = Environment.UserName,
                    ["Machine"] = Environment.MachineName,
                    ["Version"] = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown"
                }
            });
        }

        /// <summary>
        /// Logs a custom query execution (from dashboard editor).
        /// </summary>
        public void LogQueryExecution(string queryId, string serverName, bool success, TimeSpan duration, int rowCount = 0, string? errorMessage = null)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.QueryExecution,
                Severity = success ? AuditSeverity.Info : AuditSeverity.Warning,
                Message = success
                    ? $"Query '{queryId}' executed on '{serverName}' in {duration.TotalMilliseconds:F0}ms ({rowCount} rows)"
                    : $"Query '{queryId}' failed on '{serverName}': {errorMessage}",
                Details = new Dictionary<string, string>
                {
                    ["QueryId"] = queryId,
                    ["ServerName"] = serverName,
                    ["Success"] = success.ToString(),
                    ["DurationMs"] = duration.TotalMilliseconds.ToString("F0"),
                    ["RowCount"] = rowCount.ToString(),
                    ["Error"] = errorMessage ?? string.Empty
                }
            });
        }

        /// <summary>
        /// Logs a security event (authentication, authorization, suspicious activity).
        /// </summary>
        public void LogSecurityEvent(string eventDescription, AuditSeverity severity = AuditSeverity.Warning, Dictionary<string, string>? details = null)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.SecurityEvent,
                Severity = severity,
                Message = eventDescription,
                Details = details ?? new Dictionary<string, string>()
            });
        }

        /// <summary>
        /// Logs a data export operation (PDF, CSV, etc.).
        /// </summary>
        public void LogExportOperation(string exportType, string targetName, bool success, string? errorMessage = null)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.ExportOperation,
                Severity = success ? AuditSeverity.Info : AuditSeverity.Error,
                Message = success
                    ? $"{exportType} export '{targetName}' completed successfully"
                    : $"{exportType} export '{targetName}' failed: {errorMessage}",
                Details = new Dictionary<string, string>
                {
                    ["ExportType"] = exportType,
                    ["TargetName"] = targetName,
                    ["Success"] = success.ToString(),
                    ["Error"] = errorMessage ?? string.Empty
                }
            });
        }

        /// <summary>
        /// Logs cache operations (clearing, eviction due to memory pressure).
        /// </summary>
        public void LogCacheOperation(string operation, string cacheKey, Dictionary<string, string>? details = null)
        {
            var detailsDict = details ?? new Dictionary<string, string>();
            detailsDict["Operation"] = operation;
            detailsDict["CacheKey"] = cacheKey;

            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.CacheOperation,
                Severity = AuditSeverity.Info,
                Message = $"Cache operation '{operation}' on '{cacheKey}'",
                Details = detailsDict
            });
        }

        /// <summary>
        /// Logs dashboard view/access for compliance tracking.
        /// </summary>
        public void LogDashboardAccess(string dashboardId, string viewMode)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.DashboardAccess,
                Severity = AuditSeverity.Info,
                Message = $"Dashboard '{dashboardId}' accessed in {viewMode} mode",
                Details = new Dictionary<string, string>
                {
                    ["DashboardId"] = dashboardId,
                    ["ViewMode"] = viewMode
                }
            });
        }

        /// <summary>
        /// Logs user session events (login, logout, timeout).
        /// </summary>
        public void LogSessionEvent(string eventType, string? sessionId = null)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.SessionEvent,
                Severity = AuditSeverity.Info,
                Message = $"Session event: {eventType}",
                Details = new Dictionary<string, string>
                {
                    ["EventType"] = eventType,
                    ["SessionId"] = sessionId ?? "N/A"
                }
            });
        }

        // ================================================================
        // CORE INFRASTRUCTURE
        // ================================================================

        private void Enqueue(AuditLogEntry entry)
        {
            _pendingEntries.Enqueue(entry);

            // Force flush if buffer is too large
            if (_pendingEntries.Count >= MaxBufferSize)
            {
                Flush();
            }
        }

        /// <summary>
        /// Flushes all pending log entries to the current day's log file.
        /// </summary>
        public void Flush()
        {
            if (_pendingEntries.IsEmpty) return;

            var entries = new List<AuditLogEntry>();
            while (_pendingEntries.TryDequeue(out var entry))
            {
                entries.Add(entry);
            }

            if (entries.Count == 0) return;

            lock (_writeLock)
            {
                try
                {
                    var logFile = Path.Combine(_logDirectory, $"audit-{DateTime.Now:yyyy-MM-dd}.jsonl");
                    var sb = new StringBuilder();
                    foreach (var entry in entries)
                    {
                        sb.AppendLine(JsonSerializer.Serialize(entry, SerializerOptions));
                    }
                    File.AppendAllText(logFile, sb.ToString());
                }
                catch (Exception ex)
                {
                    // Last resort: use Serilog static logger since the audit log itself is failing
                    Serilog.Log.Warning(ex, "Failed to write audit log to file");
                }
            }
        }

        /// <summary>
        /// Applies the retention policy by deleting old audit log files.
        /// Runs automatically once per day.
        /// </summary>
        private void ApplyRetentionPolicy()
        {
            try
            {
                var cutoffDate = DateTime.Now.AddDays(-RetentionDays);
                var logFiles = Directory.GetFiles(_logDirectory, "audit-*.jsonl");

                foreach (var file in logFiles)
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.CreationTime < cutoffDate)
                    {
                        fileInfo.Delete();
                        Serilog.Log.Information("Deleted old audit log: {FileName}", fileInfo.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Error applying audit log retention policy");
            }
        }

        /// <summary>
        /// Reads audit log entries for a specific date range (for UI display).
        /// </summary>
        public List<AuditLogEntry> GetEntries(DateTime from, DateTime to, AuditEventType? filterType = null)
        {
            var entries = new List<AuditLogEntry>();

            for (var date = from.Date; date <= to.Date; date = date.AddDays(1))
            {
                var logFile = Path.Combine(_logDirectory, $"audit-{date:yyyy-MM-dd}.jsonl");
                if (!File.Exists(logFile)) continue;

                try
                {
                    foreach (var line in File.ReadLines(logFile))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var entry = JsonSerializer.Deserialize<AuditLogEntry>(line, SerializerOptions);
                        if (entry != null && entry.Timestamp >= from && entry.Timestamp <= to)
                        {
                            if (filterType == null || entry.EventType == filterType)
                            {
                                entries.Add(entry);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "Error reading audit log {LogFile}", logFile);
                }
            }

            return entries.OrderByDescending(e => e.Timestamp).ToList();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _flushTimer?.Dispose();
            Flush(); // Final flush on dispose
        }
    }

    // ================================================================
    // MODELS
    // ================================================================

    public class AuditLogEntry
    {
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public AuditEventType EventType { get; set; }
        public AuditSeverity Severity { get; set; }
        public string Message { get; set; } = string.Empty;
        public Dictionary<string, string> Details { get; set; } = new();
        public string User { get; set; } = Environment.UserName;
        public string Machine { get; set; } = Environment.MachineName;
    }

    public enum AuditEventType
    {
        ConnectionAttempt,
        ScriptExecution,
        QueryExecution,
        SecurityBlock,
        SecurityEvent,
        ConfigurationChange,
        Deployment,
        ApplicationLifecycle,
        ExportOperation,
        CacheOperation,
        DashboardAccess,
        SessionEvent
    }

    public enum AuditSeverity
    {
        Info,
        Warning,
        Error,
        Critical
    }
}
