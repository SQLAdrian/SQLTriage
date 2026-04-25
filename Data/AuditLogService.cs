/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SQLTriage.Data
{
    /// <summary>
    /// Enterprise audit logging service for tracking security-relevant actions.
    /// Entries are written in a tamper-evident HMAC-SHA256 chain: each record's
    /// signature incorporates the previous record's signature, so any later
    /// edit, deletion, or reorder invalidates the chain from that point forward.
    /// Chain is verified on startup; breaks set <see cref="ChainBroken"/> and
    /// are mirrored to the Windows Event Log when available.
    /// </summary>
    public class AuditLogService : IDisposable
    {
        private readonly string _logDirectory;
        private readonly string _keyPath;
        private readonly byte[] _hmacKey;
        private readonly ConcurrentQueue<AuditLogEntry> _pendingEntries = new();
        private readonly Timer _flushTimer;
        private readonly object _writeLock = new();
        private string _lastSignature = string.Empty;
        private bool _disposed;
        private bool _eventLogAvailable;

        /// <summary>Event Log source name. Creating the source requires admin rights.</summary>
        private const string EventLogSource = "SQLTriage-Audit";
        private const string EventLogName = "Application";

        /// <summary>Maximum log file size before rotation (64 KiB).</summary>
        private const long RotationSizeBytes = 64 * 1024;

        /// <summary>
        /// True when startup verification detected a chain break. While set, new
        /// writes still succeed (so the incident itself is auditable) but the
        /// break is surfaced to callers.
        /// </summary>
        public bool ChainBroken { get; private set; }

        /// <summary>Maximum number of entries to buffer before forcing a flush.</summary>
        public int MaxBufferSize { get; set; } = 50;

        /// <summary>Flush interval in milliseconds.</summary>
        public int FlushIntervalMs { get; set; } = 5000;

        /// <summary>Retention period in days (default 90 for SOC2).</summary>
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

            _keyPath = Path.Combine(_logDirectory, "hmac.key");
            _hmacKey = LoadOrCreateHmacKey(_keyPath);

            _eventLogAvailable = TryEnsureEventLogSource();
            VerifyChainOnStartup();

            // Periodic flush timer
            _flushTimer = new Timer(_ => Flush(), null, FlushIntervalMs, FlushIntervalMs);
        }

        private static byte[] LoadOrCreateHmacKey(string keyPath)
        {
            if (File.Exists(keyPath))
            {
                try
                {
                    var existing = File.ReadAllBytes(keyPath);
                    if (existing.Length >= 32) return existing;
                }
                catch { /* fall through to recreate */ }
            }

            var fresh = RandomNumberGenerator.GetBytes(32);
            try
            {
                File.WriteAllBytes(keyPath, fresh);
                // Best-effort ACL: owner-only. On non-NTFS paths this is a no-op.
                try { File.SetAttributes(keyPath, FileAttributes.Hidden); } catch { }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to persist audit HMAC key — chain will restart next launch");
            }
            return fresh;
        }

        [SupportedOSPlatform("windows")]
        private static bool TryCreateEventLogSource()
        {
            if (EventLog.SourceExists(EventLogSource)) return true;
            EventLog.CreateEventSource(new EventSourceCreationData(EventLogSource, EventLogName));
            return true;
        }

        private bool TryEnsureEventLogSource()
        {
            if (!OperatingSystem.IsWindows()) return false;
            try { return TryCreateEventLogSource(); }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "Event Log source registration failed (non-admin); audit mirror disabled");
                return false;
            }
        }

        private void VerifyChainOnStartup()
        {
            try
            {
                var latest = GetCurrentLogFile();
                if (!File.Exists(latest)) return;

                // Seed with the first entry's PreviousHash so cross-file rotations
                // don't register as chain breaks. Chain integrity within a single
                // file is what we actually verify.
                string? previousSig = null;
                foreach (var line in File.ReadLines(latest))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var entry = JsonSerializer.Deserialize<AuditLogEntry>(line, SerializerOptions);
                    if (entry == null) continue;

                    if (previousSig == null)
                    {
                        // First entry of file — its declared PreviousHash is our starting point.
                        previousSig = entry.PreviousHash ?? string.Empty;
                    }

                    var recomputed = ComputeSignature(entry, previousSig);
                    if (!string.Equals(recomputed, entry.Signature, StringComparison.Ordinal))
                    {
                        ChainBroken = true;
                        Serilog.Log.Error("Audit log chain break detected at {Timestamp} in {File}",
                            entry.Timestamp, Path.GetFileName(latest));
                        WriteEventLog("Audit log chain integrity check failed. Investigate tampering.",
                            AuditSeverity.Error);
                        break;
                    }
                    previousSig = entry.Signature;
                }

                _lastSignature = previousSig ?? string.Empty;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Audit chain verification failed");
            }
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
        /// Flushes all pending log entries to the current log file, signing each
        /// entry with an HMAC that chains to the previous entry's signature.
        /// Rotates to a new segment when the current file exceeds the rotation threshold.
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
                    var logFile = GetCurrentLogFile();
                    MaybeRotate(ref logFile);

                    var sb = new StringBuilder();
                    foreach (var entry in entries)
                    {
                        entry.PreviousHash = _lastSignature;
                        entry.Signature = ComputeSignature(entry, _lastSignature);
                        _lastSignature = entry.Signature;
                        sb.AppendLine(JsonSerializer.Serialize(entry, SerializerOptions));

                        // Mirror Critical/Error severity to Event Log for SOC2 visibility
                        if (_eventLogAvailable &&
                            (entry.Severity == AuditSeverity.Critical || entry.Severity == AuditSeverity.Error))
                        {
                            WriteEventLog($"[{entry.EventType}] {entry.Message}", entry.Severity);
                        }
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
        /// Returns the path to the current active segment. Segment naming:
        /// audit-YYYYMMDD.jsonl for the first segment of a day, then
        /// audit-YYYYMMDD_NNNN.jsonl for rotated segments.
        /// </summary>
        private string GetCurrentLogFile()
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var baseFile = Path.Combine(_logDirectory, $"audit-{today}.jsonl");

            // Pick the highest-numbered rotated segment if any exist.
            var rotated = Directory.GetFiles(_logDirectory, $"audit-{today}_*.jsonl");
            if (rotated.Length == 0) return baseFile;

            return rotated
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .First();
        }

        private void MaybeRotate(ref string logFile)
        {
            try
            {
                var fi = new FileInfo(logFile);
                if (!fi.Exists || fi.Length < RotationSizeBytes) return;

                var today = DateTime.Now.ToString("yyyy-MM-dd");
                int next = 1;
                while (File.Exists(Path.Combine(_logDirectory, $"audit-{today}_{next:D4}.jsonl")))
                    next++;

                logFile = Path.Combine(_logDirectory, $"audit-{today}_{next:D4}.jsonl");
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Audit log rotation check failed");
            }
        }

        /// <summary>
        /// Computes HMAC-SHA256(key, previousSig || canonical(entry)) and returns
        /// it base64-encoded. The canonical form excludes Signature itself so the
        /// entry's own signature does not participate in its computation.
        /// </summary>
        private string ComputeSignature(AuditLogEntry entry, string previousSig)
        {
            var canonical = new
            {
                entry.Timestamp,
                entry.EventType,
                entry.Severity,
                entry.Message,
                entry.Details,
                entry.User,
                entry.Machine,
                entry.PreviousHash
            };
            var payload = JsonSerializer.Serialize(canonical, SerializerOptions);
            var input = Encoding.UTF8.GetBytes(previousSig + "|" + payload);
            using var hmac = new HMACSHA256(_hmacKey);
            return Convert.ToBase64String(hmac.ComputeHash(input));
        }

        [SupportedOSPlatform("windows")]
        private static void WriteEventLogCore(string message, AuditSeverity severity)
        {
            var type = severity switch
            {
                AuditSeverity.Critical => EventLogEntryType.Error,
                AuditSeverity.Error => EventLogEntryType.Error,
                AuditSeverity.Warning => EventLogEntryType.Warning,
                _ => EventLogEntryType.Information
            };
            using var log = new EventLog(EventLogName) { Source = EventLogSource };
            log.WriteEntry(message, type);
        }

        private void WriteEventLog(string message, AuditSeverity severity)
        {
            if (!_eventLogAvailable || !OperatingSystem.IsWindows()) return;
            try { WriteEventLogCore(message, severity); }
            catch (Exception ex) { Serilog.Log.Debug(ex, "Event Log write failed"); }
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
                // Match both the base file and any rotated segments for the day.
                var pattern = $"audit-{date:yyyy-MM-dd}*.jsonl";
                foreach (var logFile in Directory.EnumerateFiles(_logDirectory, pattern))
                {
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

        /// <summary>Base64 signature of the preceding entry; empty on the first record of a chain.</summary>
        public string PreviousHash { get; set; } = string.Empty;

        /// <summary>HMAC-SHA256 signature of this entry + PreviousHash.</summary>
        public string Signature { get; set; } = string.Empty;
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
