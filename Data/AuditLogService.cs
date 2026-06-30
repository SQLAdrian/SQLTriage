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
using Microsoft.Extensions.Configuration;

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
        private byte[] _hmacKey;
        private readonly ConcurrentQueue<AuditLogEntry> _pendingEntries = new();
        private readonly Timer _flushTimer;
        private readonly Timer? _retentionTimer;
        private readonly Timer? _verificationTimer;
        private readonly object _writeLock = new();
        private string _lastSignature = string.Empty;
        private bool _disposed;
        private bool _eventLogAvailable;
        private readonly int _configuredRetentionDays;
        private int _consecutiveFlushFailures;
        private const int FlushFailoverThreshold = 3;
        // DE-H1: tracks last full multi-segment chain verify time.
        private DateTime _lastFullChainVerifyUtc = DateTime.MinValue;
        // DE-H4: cached current segment path — avoids O(n) Directory.GetFiles on every flush.
        // Invalidated (set null) when MaybeRotate creates a new segment.
        private string? _currentSegmentPath;
        private readonly int _fullChainVerifyIntervalDays;

        /// <summary>Event Log source name. Creating the source requires admin rights.</summary>
        private const string EventLogSource = "SQLTriage-Audit";
        private const string EventLogName = "Application";

        /// <summary>
        /// Maximum log file size before rotation (4 MiB default; configurable via Audit:SegmentMaxBytes).
        /// Prior value was 64 KiB which caused 24-48 segments/day at moderate event rates (DE-H3).
        /// </summary>
        private const long DefaultRotationSizeBytes = 4 * 1024 * 1024;

        private long RotationSizeBytes => _rotationSizeBytes;
        private readonly long _rotationSizeBytes;

        /// <summary>
        /// True when startup verification detected a chain break. While set, new
        /// writes still succeed (so the incident itself is auditable) but the
        /// break is surfaced to callers.
        /// </summary>
        public bool ChainBroken { get; private set; }

        /// <summary>
        /// Audit-writability probe: actively confirms the log directory can be
        /// written right now (write + delete a tiny probe file). The remediation
        /// runner calls this before applying a fix and refuses if it is false —
        /// for a compliance product an apply that cannot be logged must not run.
        /// </summary>
        public bool CanWrite
        {
            get
            {
                try
                {
                    if (!Directory.Exists(_logDirectory))
                        Directory.CreateDirectory(_logDirectory);
                    var probe = Path.Combine(_logDirectory, ".write-probe-" + Guid.NewGuid().ToString("N"));
                    File.WriteAllText(probe, string.Empty);
                    File.Delete(probe);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// First record ID where chain verification failed, or null if chain intact.
        /// Surfaced to UI / IR runbook as the forensic starting point.
        /// </summary>
        public string? ChainBreakFirstRecordId { get; private set; }

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
            : this(Path.Combine(AppContext.BaseDirectory, "audit-logs"), configuration: null)
        {
        }

        /// <summary>Production constructor — reads Audit:RetentionDays from IConfiguration (default 90).</summary>
        public AuditLogService(IConfiguration? configuration)
            : this(Path.Combine(AppContext.BaseDirectory, "audit-logs"), startFlushTimer: true, configuration: configuration)
        {
        }

        // Test seam: explicit log directory + opt-out for the background flush timer.
        // Production callers should use the parameterless or IConfiguration constructor.
        public AuditLogService(string logDirectory, bool startFlushTimer = true, IConfiguration? configuration = null)
        {
            _configuredRetentionDays = configuration?.GetValue<int>("Audit:RetentionDays", 90) ?? 90;
            RetentionDays = _configuredRetentionDays;
            // DE-H3: configurable segment size; defaults to 4 MiB
            _rotationSizeBytes = configuration?.GetValue<long>("Audit:SegmentMaxBytes", DefaultRotationSizeBytes)
                                 ?? DefaultRotationSizeBytes;
            // DE-H1: full chain verify interval; defaults to 7 days
            _fullChainVerifyIntervalDays = configuration?.GetValue<int>("Audit:FullChainVerifyIntervalDays", 7) ?? 7;

            _logDirectory = logDirectory;
            if (!Directory.Exists(_logDirectory))
                Directory.CreateDirectory(_logDirectory);

            _keyPath = Path.Combine(_logDirectory, "hmac.key");
            _hmacKey = LoadOrCreateHmacKey(_keyPath);

            _eventLogAvailable = TryEnsureEventLogSource();
            VerifyChainOnStartup();
            // R-L6: emit one-time migration note on first run after switching to UTC
            // filenames. Production-only (gated like CheckHmacKeyAge below): in test
            // mode startFlushTimer=false and we must not enqueue a self-entry that
            // would skew exact-entry-count assertions (R-L6 side-effect, not a test concern).
            if (startFlushTimer) EmitSegmentFilenameUtcMarkerIfNeeded();
            // L6: check key age after chain verification (only in production mode with timer)
            if (startFlushTimer) CheckHmacKeyAge(configuration);

            // Periodic flush timer (skipped in tests so they don't race the foreground Flush())
            _flushTimer = startFlushTimer
                ? new Timer(_ => Flush(), null, FlushIntervalMs, FlushIntervalMs)
                : new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);

            // Retention timer: fire immediately (catches existing over-retention), then every 24 h.
            // Skipped when startFlushTimer is false (test mode) to avoid background races.
            if (startFlushTimer)
            {
                _retentionTimer = new Timer(_ => RunRetentionSweep(), null,
                    TimeSpan.Zero, TimeSpan.FromHours(24));

                // Verification timer: fires after 1 min (startup settle) then every N hours.
                // Interval is configurable via Audit:VerificationIntervalHours (default 24).
                int verificationHours = configuration?.GetValue<int>("Audit:VerificationIntervalHours", 24) ?? 24;
                _verificationTimer = new Timer(_ => RunScheduledVerification(), null,
                    TimeSpan.FromMinutes(1), TimeSpan.FromHours(verificationHours));
            }
        }

        // DPAPI entropy tag — distinguishes SQLTriage HMAC key blobs from other ProtectedData blobs.
        private static readonly byte[] HmacKeyEntropy =
            System.Text.Encoding.UTF8.GetBytes("SQLTriage.AuditLog.HmacKey.v1");

        // ── L6: Key metadata sidecar ─────────────────────────────────────────────
        // Path: <logDirectory>/hmac.key.meta (JSON: { "createdAt": "...", "rotationDueAt": "..." })

        private static readonly string HmacMetaFileName = "hmac.key.meta";

        private string HmacMetaPath => Path.Combine(_logDirectory, HmacMetaFileName);

        private sealed class HmacKeyMeta
        {
            public string CreatedAt { get; set; } = string.Empty;
            public string RotationDueAt { get; set; } = string.Empty;
        }

        private HmacKeyMeta LoadOrCreateHmacMeta(string keyPath, int maxAgeDays)
        {
            if (File.Exists(HmacMetaPath))
            {
                try
                {
                    var json = File.ReadAllText(HmacMetaPath, Encoding.UTF8);
                    var m = JsonSerializer.Deserialize<HmacKeyMeta>(json, SerializerOptions);
                    if (m != null && !string.IsNullOrEmpty(m.CreatedAt))
                        return m;
                }
                catch { }
            }

            // No meta yet — create it (assume key was created now for new keys, or set
            // to file creation time for existing keys so age is not artificially zero).
            DateTime created = File.Exists(keyPath)
                ? File.GetCreationTimeUtc(keyPath)
                : DateTime.UtcNow;

            var meta = new HmacKeyMeta
            {
                CreatedAt = created.ToString("o"),
                RotationDueAt = created.AddDays(maxAgeDays).ToString("o")
            };
            WriteHmacMeta(meta);
            return meta;
        }

        private void WriteHmacMeta(HmacKeyMeta meta)
        {
            try
            {
                File.WriteAllText(HmacMetaPath,
                    JsonSerializer.Serialize(meta, SerializerOptions),
                    Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[AUDIT] Failed to write HMAC key meta sidecar");
            }
        }

        private void CheckHmacKeyAge(IConfiguration? configuration)
        {
            int maxAgeDays = configuration?.GetValue<int>("Audit:HmacKeyMaxAgeDays", 365) ?? 365;
            var meta = LoadOrCreateHmacMeta(_keyPath, maxAgeDays);

            if (!DateTime.TryParse(meta.CreatedAt, out var created)) return;
            int ageDays = (int)(DateTime.UtcNow - created).TotalDays;

            if (ageDays > maxAgeDays)
            {
                Serilog.Log.Warning("[AUDIT] HMAC key is {AgeDays} days old (max: {MaxAgeDays}). Consider rotating via Settings.", ageDays, maxAgeDays);
                LogHmacKeyAgeExceeded(ageDays, maxAgeDays);
            }
        }

        /// <summary>
        /// L6: Rotates the HMAC key. Generates a new key, re-wraps it, writes both the key file
        /// and the meta sidecar, then appends a chain-anchor entry (HmacKeyRotated).
        /// The chain is NOT broken: the rotation entry uses the old key's last signature as its
        /// PreviousHash, and subsequent entries are signed with the new key.
        /// </summary>
        public void RotateHmacKey(string actor, IConfiguration? configuration = null)
        {
            lock (_writeLock)
            {
                // Flush pending entries under old key first.
                Flush();

                int maxAgeDays = configuration?.GetValue<int>("Audit:HmacKeyMaxAgeDays", 365) ?? 365;
                var meta = LoadOrCreateHmacMeta(_keyPath, maxAgeDays);
                int priorAgeDays = DateTime.TryParse(meta.CreatedAt, out var prevCreated)
                    ? (int)(DateTime.UtcNow - prevCreated).TotalDays : 0;

                // Generate and persist new key.
                var newRaw = RandomNumberGenerator.GetBytes(32);
                try
                {
                    WriteWrappedHmacKey(_keyPath, newRaw);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "[AUDIT] HMAC key rotation failed during key write");
                    throw;
                }

                // Update meta sidecar.
                var now = DateTime.UtcNow;
                var newMeta = new HmacKeyMeta
                {
                    CreatedAt = now.ToString("o"),
                    RotationDueAt = now.AddDays(maxAgeDays).ToString("o")
                };
                WriteHmacMeta(newMeta);

                // Write the chain-anchor entry with the old key BEFORE swapping,
                // so the rotation entry itself is signed correctly under the old key.
                var rotationEntry = new AuditLogEntry
                {
                    EventType = AuditEventType.HmacKeyRotated,
                    Severity = AuditSeverity.Critical,
                    Message = $"Key rotated by {actor}. Prior key age: {priorAgeDays} days.",
                    Details = new Dictionary<string, string>
                    {
                        ["Actor"] = actor,
                        ["PriorAgeDays"] = priorAgeDays.ToString(),
                        ["RotatedAt"] = now.ToString("o")
                    }
                };
                rotationEntry.PreviousHash = _lastSignature;
                rotationEntry.Signature = ComputeSignature(rotationEntry, _lastSignature);
                _lastSignature = rotationEntry.Signature;

                var logFile = GetCurrentLogFile();
                MaybeRotate(ref logFile);
                File.AppendAllText(logFile,
                    JsonSerializer.Serialize(rotationEntry, SerializerOptions) + Environment.NewLine);

                // Atomically replace the reference so concurrent ComputeSignature calls see
                // either fully-old or fully-new key (never a partially-copied array).
                // .NET reference assignment is atomic on all supported architectures.
                _hmacKey = newRaw;

                Serilog.Log.Information("[AUDIT] HMAC key rotated by {Actor}. Prior age: {Age} days", actor, priorAgeDays);
            }
        }

        private static byte[] LoadOrCreateHmacKey(string keyPath)
        {
            if (File.Exists(keyPath))
            {
                try
                {
                    var blob = File.ReadAllBytes(keyPath);

                    // ── Happy path: DPAPI-wrapped blob ──
                    if (OperatingSystem.IsWindows())
                    {
                        try
                        {
                            return UnwrapHmacKey(blob);
                        }
                        catch (CryptographicException)
                        {
                            // Legacy raw key (pre-DPAPI): 32 exact bytes written by older builds.
                            // Migrate: re-wrap under DPAPI, log once, continue with the same key.
                            if (blob.Length == 32)
                            {
                                Serilog.Log.Information("[AUDIT] Migrated HMAC key to DPAPI-wrapped format");
                                WriteWrappedHmacKey(keyPath, blob);
                                return blob;
                            }
                            // Blob is neither a valid DPAPI blob nor a raw 32-byte key — regenerate.
                        }
                    }
                    else
                    {
                        // Non-Windows: key is stored raw (DPAPI unavailable); accept any ≥32-byte file.
                        if (blob.Length >= 32) return blob;
                    }
                }
                catch { /* fall through to recreate */ }
            }

            var fresh = RandomNumberGenerator.GetBytes(32);
            try
            {
                WriteWrappedHmacKey(keyPath, fresh);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to persist audit HMAC key — chain will restart next launch");
            }
            return fresh;
        }

        /// <summary>
        /// Writes <paramref name="rawKey"/> to <paramref name="keyPath"/> wrapped with
        /// DPAPI CurrentUser scope on Windows, or as raw bytes on non-Windows.
        /// </summary>
        private static void WriteWrappedHmacKey(string keyPath, byte[] rawKey)
        {
            byte[] toWrite = OperatingSystem.IsWindows()
                ? WrapHmacKey(rawKey)
                : rawKey;

            // Rotation overwrites an existing key file that we previously marked
            // Hidden (and which may be ReadOnly): File.WriteAllBytes cannot open a
            // Hidden file for write on Windows -> UnauthorizedAccessException.
            // Clear attributes first so rotation can replace the key in place.
            if (File.Exists(keyPath))
            {
                try { File.SetAttributes(keyPath, FileAttributes.Normal); } catch { }
            }
            File.WriteAllBytes(keyPath, toWrite);
            // Hidden as defense-in-depth; primary protection is DPAPI wrapping on Windows.
            try { File.SetAttributes(keyPath, FileAttributes.Hidden); } catch { }
        }

        [SupportedOSPlatform("windows")]
        private static byte[] WrapHmacKey(byte[] rawKey) =>
            System.Security.Cryptography.ProtectedData.Protect(
                rawKey, HmacKeyEntropy,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);

        [SupportedOSPlatform("windows")]
        private static byte[] UnwrapHmacKey(byte[] blob) =>
            System.Security.Cryptography.ProtectedData.Unprotect(
                blob, HmacKeyEntropy,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);

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

        /// <summary>
        /// Returns all audit segment files in chronological order (oldest first).
        /// </summary>
        private string[] GetAllSegmentsChronological()
        {
            var all = Directory.GetFiles(_logDirectory, "audit-*.jsonl");
            return all.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private void VerifyChainOnStartup()
        {
            try
            {
                // DE-H4: seed _lastSignature from the LAST entry of the LAST segment,
                // not just today's file. Entries written to earlier intra-day segments
                // before a crash are no longer orphaned from the chain forward-link.
                var allSegments = GetAllSegmentsChronological();
                if (allSegments.Length == 0) return;

                // Load last-known-chain-break marker to avoid repeating the error on every restart
                var breakMarkerPath = Path.Combine(_logDirectory, ".chain-break-marker");
                var previousBreak = File.Exists(breakMarkerPath) ? File.ReadAllText(breakMarkerPath).Trim() : null;

                // Verify only the most-recent segment on startup (quick check).
                // Full multi-segment verification is performed by VerifyChain() / the weekly sweep.
                var latest = allSegments[^1];

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
                        var breakId = $"{entry.Timestamp}_{recomputed[..16]}";
                        ChainBreakFirstRecordId ??= breakId;

                        // Only log the first time this specific break is detected
                        if (previousBreak != breakId)
                        {
                            Serilog.Log.Warning("Audit log chain break detected at {Timestamp} in {File} (logged once per break)",
                                entry.Timestamp, Path.GetFileName(latest));
                            try { File.WriteAllText(breakMarkerPath, breakId); } catch { }
                        }
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

        // ── Gated remediation lane (build step 2) ─────────────────────────
        // Lifecycle: Proposed → Approved → Applied | RolledBack. Each method
        // follows the Enqueue pattern so entries inherit the existing HMAC
        // chaining / segmentation — for a compliance product the audit trail IS
        // the deliverable, so a remediation that runs must always leave a chained
        // record. The runner is the only caller; it logs at each gate.

        /// <summary>
        /// Canonical terminal-state strings for <see cref="LogRemediationApplied"/>.
        /// The remediation runner (build step 4) maps its own terminal-state enum
        /// to these so both sides share one source of truth. Any value outside this
        /// set is logged at <see cref="AuditSeverity.Warning"/> — an unrecognised
        /// outcome is treated as suspect, never as benign success.
        /// </summary>
        public static class RemediationOutcomes
        {
            /// <summary>Applied and post-verify confirmed the change took effect.</summary>
            public const string AppliedVerified = "AppliedVerified";
            /// <summary>Applied but post-verify could not confirm — needs human review.</summary>
            public const string AppliedVerifyFailed = "AppliedVerifyFailed";
            /// <summary>Did not run (permissions / preflight). Nothing changed.</summary>
            public const string CouldNotRun = "CouldNotRun";
            /// <summary>Already compliant; no change was necessary.</summary>
            public const string NoOp = "NoOp";
        }

        /// <summary>
        /// Logs that a registered remediation template was proposed for a server
        /// (gate 4 entry). Nothing has been applied at this point.
        /// </summary>
        public void LogRemediationProposed(string templateKey, string serverName, string? details = null)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.RemediationProposed,
                Severity = AuditSeverity.Info,
                Message = $"Remediation '{templateKey}' proposed for '{serverName}'",
                Details = new Dictionary<string, string>
                {
                    ["TemplateKey"] = templateKey,
                    ["ServerName"] = serverName,
                    ["Details"] = details ?? string.Empty
                }
            });
        }

        /// <summary>
        /// Logs that a human explicitly approved a proposed remediation (gate 4
        /// cleared). Records who approved so the trail attributes the decision.
        /// </summary>
        public void LogRemediationApproved(string templateKey, string serverName, string approvedBy, string? details = null)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.RemediationApproved,
                Severity = AuditSeverity.Info,
                Message = $"Remediation '{templateKey}' for '{serverName}' approved by '{approvedBy}'",
                Details = new Dictionary<string, string>
                {
                    ["TemplateKey"] = templateKey,
                    ["ServerName"] = serverName,
                    ["ApprovedBy"] = approvedBy,
                    ["Details"] = details ?? string.Empty
                }
            });
        }

        /// <summary>
        /// Logs the outcome of an approved remediation's execution (gate 5). The
        /// <paramref name="outcome"/> is a DISTINCT TERMINAL STATE — never a bare
        /// "success" boolean — so a fix that did not apply can never read as applied:
        /// AppliedVerified / AppliedVerifyFailed / CouldNotRun / NoOp. Severity is
        /// derived from the outcome so the ledger flags the bad terminal states.
        /// </summary>
        public void LogRemediationApplied(string templateKey, string serverName, string outcome, string? errorMessage = null, string? preChangeValue = null)
        {
            // Only the verified-applied terminal state is benign; the others warrant
            // a Warning/Error so they surface in the ledger rather than read as success.
            var severity = outcome switch
            {
                RemediationOutcomes.AppliedVerified => AuditSeverity.Info,
                RemediationOutcomes.NoOp => AuditSeverity.Info,
                RemediationOutcomes.AppliedVerifyFailed => AuditSeverity.Error,
                RemediationOutcomes.CouldNotRun => AuditSeverity.Warning,
                _ => AuditSeverity.Warning
            };

            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.RemediationApplied,
                Severity = severity,
                Message = $"Remediation '{templateKey}' on '{serverName}' — outcome: {outcome}"
                    + (string.IsNullOrEmpty(errorMessage) ? string.Empty : $" ({errorMessage})"),
                Details = new Dictionary<string, string>
                {
                    ["TemplateKey"] = templateKey,
                    ["ServerName"] = serverName,
                    ["Outcome"] = outcome,
                    ["Error"] = errorMessage ?? string.Empty,
                    // The pre-change value rides the HMAC-chained ledger so a cross-session
                    // revert can re-apply it (undo-ability is therefore bounded by audit
                    // retention). Empty when the apply captured no recoverable prior value.
                    ["PreChangeValue"] = preChangeValue ?? string.Empty
                }
            });
        }

        /// <summary>
        /// Returns the pre-change value recorded for the most recent VERIFIED apply of a
        /// remediation template on a server — the target for a cross-session revert. Null when
        /// no such record carries a recoverable value. Reads the HMAC-chained ledger (the single
        /// tamper-evident source of truth), so undo-ability is bounded by audit retention.
        /// </summary>
        // Lookback window for remediation-history queries. Bounded so the per-day file scan
        // stays cheap; comfortably exceeds the default audit retention (so undo-ability is
        // bounded by retention, not by this). Entries are stamped UtcNow, so the window MUST be
        // UTC (a local-time window drops entries on machines behind UTC).
        private const int RemediationHistoryLookbackDays = 366;

        public string? GetLatestRemediationPreChange(string templateKey, string serverName)
        {
            try
            {
                // GetEntries returns NEWEST-FIRST (OrderByDescending Timestamp), so the FIRST
                // matching entry is the most recent verified apply.
                var entries = GetEntries(
                    DateTime.UtcNow.AddDays(-RemediationHistoryLookbackDays),
                    DateTime.UtcNow.AddDays(1),
                    AuditEventType.RemediationApplied);
                foreach (var e in entries)
                {
                    if (e.Details == null) continue;
                    if (!e.Details.TryGetValue("TemplateKey", out var k)
                        || !string.Equals(k, templateKey, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!e.Details.TryGetValue("ServerName", out var s)
                        || !string.Equals(s, serverName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!e.Details.TryGetValue("Outcome", out var o)
                        || !string.Equals(o, RemediationOutcomes.AppliedVerified, StringComparison.OrdinalIgnoreCase)) continue;
                    if (e.Details.TryGetValue("PreChangeValue", out var pv) && !string.IsNullOrWhiteSpace(pv))
                        return pv;
                }
            }
            catch { /* best-effort — history revert is a convenience, never a correctness path */ }
            return null;
        }

        /// <summary>
        /// Single-scan variant: returns the latest verified pre-change value per template key for
        /// a server (templateKey → preChangeValue). One pass over the ledger so a caller showing
        /// many fixes (the Remediation page) does not rescan the log files per row.
        /// </summary>
        public Dictionary<string, string> GetLatestRemediationPreChanges(string serverName)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(serverName)) return result;
            try
            {
                var entries = GetEntries(
                    DateTime.UtcNow.AddDays(-RemediationHistoryLookbackDays),
                    DateTime.UtcNow.AddDays(1),
                    AuditEventType.RemediationApplied);
                // Newest-first; the first verified value seen per key is the most recent, so set
                // only when the key is not already present.
                foreach (var e in entries)
                {
                    if (e.Details == null) continue;
                    if (!e.Details.TryGetValue("ServerName", out var s)
                        || !string.Equals(s, serverName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!e.Details.TryGetValue("Outcome", out var o)
                        || !string.Equals(o, RemediationOutcomes.AppliedVerified, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!e.Details.TryGetValue("TemplateKey", out var k) || string.IsNullOrWhiteSpace(k)) continue;
                    if (result.ContainsKey(k)) continue;
                    if (e.Details.TryGetValue("PreChangeValue", out var pv) && !string.IsNullOrWhiteSpace(pv))
                        result[k] = pv;
                }
            }
            catch { /* best-effort */ }
            return result;
        }

        /// <summary>
        /// Logs the MODELLED per-fix power-saving estimate alongside a remediation
        /// (informational — a modelled estimate, never a measurement; realisable only
        /// if freed capacity is reclaimed). Relative %-band always; watts on physical hosts.
        /// </summary>
        public void LogRemediationPowerEstimate(string templateKey, string serverName, int lowPct, int highPct, int? wattsLow, int? wattsHigh)
        {
            var watts = wattsLow.HasValue ? $"{wattsLow}-{wattsHigh} W" : "n/a (relative only)";
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.RemediationPowerEstimate,
                Severity = AuditSeverity.Info,
                Message = $"Power estimate for remediation '{templateKey}' on '{serverName}' — ~{lowPct}-{highPct}% CPU-work; {watts} (modelled, if reclaimed)",
                Details = new Dictionary<string, string>
                {
                    ["TemplateKey"] = templateKey,
                    ["ServerName"] = serverName,
                    ["Kind"] = "PowerEstimate",
                    ["PowerSavedPct"] = $"{lowPct}-{highPct}",
                    ["PowerSavedWatts"] = wattsLow.HasValue ? $"{wattsLow}-{wattsHigh}" : string.Empty
                }
            });
        }

        /// <summary>
        /// Logs that a previously applied remediation was rolled back to its
        /// pre-change state.
        /// </summary>
        public void LogRemediationRolledBack(string templateKey, string serverName, bool success, string? errorMessage = null)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.RemediationRolledBack,
                Severity = success ? AuditSeverity.Info : AuditSeverity.Error,
                Message = success
                    ? $"Remediation '{templateKey}' on '{serverName}' rolled back"
                    : $"Rollback of remediation '{templateKey}' on '{serverName}' FAILED: {errorMessage}",
                Details = new Dictionary<string, string>
                {
                    ["TemplateKey"] = templateKey,
                    ["ServerName"] = serverName,
                    ["Success"] = success.ToString(),
                    ["Error"] = errorMessage ?? string.Empty
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
        /// Logs a compliance evidence report export (Gap #3 — Compliance Framework).
        /// </summary>
        public void LogComplianceReportExported(string framework, string exportedBy, int totalControls, double overallPercent, bool success, string? errorMessage = null)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.ComplianceReportExported,
                Severity = success ? AuditSeverity.Info : AuditSeverity.Error,
                Message = success
                    ? $"Compliance report exported for framework '{framework}' by {exportedBy}: {overallPercent:F1}% overall compliance"
                    : $"Compliance report export failed for framework '{framework}': {errorMessage}",
                Details = new Dictionary<string, string>
                {
                    ["Framework"] = framework,
                    ["ExportedBy"] = exportedBy,
                    ["TotalControls"] = totalControls.ToString(),
                    ["OverallPercent"] = overallPercent.ToString("F2"),
                    ["Success"] = success.ToString(),
                    ["Error"] = errorMessage ?? string.Empty,
                }
            });
        }

        /// <summary>
        /// Logs generation of a diagnostic report bundle (Executive Summary, DBA Handoff, Audit Evidence).
        /// </summary>
        public void LogReportBundle(string bundleType, string serverName, bool success, string? outputPath = null, string? errorMessage = null)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.ReportBundleGenerated,
                Severity = success ? AuditSeverity.Info : AuditSeverity.Error,
                Message = success
                    ? $"Report bundle '{bundleType}' generated for server '{serverName}'"
                    : $"Report bundle '{bundleType}' failed for server '{serverName}': {errorMessage}",
                Details = new Dictionary<string, string>
                {
                    ["BundleType"] = bundleType,
                    ["ServerName"] = serverName,
                    ["Success"] = success.ToString(),
                    ["OutputPath"] = outputPath ?? string.Empty,
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

            // Capture the batch BEFORE attempting any write so we can requeue on failure.
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
                    _consecutiveFlushFailures = 0;
                }
                catch (Exception ex)
                {
                    _consecutiveFlushFailures++;
                    // Last resort: use Serilog static logger since the audit log itself is failing
                    Serilog.Log.Error(ex,
                        "[AUDIT] Flush failed (attempt {N}); {Count} entries requeued. Path: {Dir}",
                        _consecutiveFlushFailures, entries.Count, _logDirectory);

                    // Requeue the batch in original order so entries are not lost.
                    // Prepend via a temp queue to maintain order.
                    var tempQueue = new Queue<AuditLogEntry>(entries);
                    while (tempQueue.Count > 0)
                        _pendingEntries.Enqueue(tempQueue.Dequeue());

                    // After FlushFailoverThreshold consecutive failures, write to the failover directory.
                    if (_consecutiveFlushFailures >= FlushFailoverThreshold)
                    {
                        TryWriteFailover(entries);
                    }
                }
            }
        }

        /// <summary>
        /// DE-H2: Writes the batch to audit-logs/.failover/ when the primary directory is unavailable.
        /// Each entry is individually signed and chained: the first failover entry chains to the
        /// last main-chain signature; subsequent failover entries chain to each other.
        /// Each entry carries EventType = AuditFailoverEntry so verifiers can identify it.
        /// When the main writer recovers, its first entry will chain to the last failover signature
        /// because _lastSignature is updated here.
        /// Emits AuditFlushFailover once per transition (guarded by _consecutiveFlushFailures == threshold).
        /// </summary>
        private void TryWriteFailover(List<AuditLogEntry> entries)
        {
            try
            {
                var failoverDir = Path.Combine(_logDirectory, ".failover");
                Directory.CreateDirectory(failoverDir);
                var path = Path.Combine(failoverDir,
                    $"audit-failover-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.jsonl");

                // DE-H2: sign and chain every failover entry.
                var sb = new StringBuilder();
                foreach (var e in entries)
                {
                    // Mark as failover so verifiers can identify it.
                    e.EventType = AuditEventType.AuditFailoverEntry;
                    e.PreviousHash = _lastSignature;
                    e.Signature = ComputeSignature(e, _lastSignature);
                    _lastSignature = e.Signature;
                    sb.AppendLine(JsonSerializer.Serialize(e, SerializerOptions));
                }
                File.WriteAllText(path, sb.ToString());

                Serilog.Log.Fatal(
                    "[AUDIT] PRIMARY FLUSH UNAVAILABLE after {N} consecutive failures. " +
                    "Signed failover batch written to {Path}. Investigate disk/permissions immediately.",
                    _consecutiveFlushFailures, path);

                // Emit one AuditFlushFailover event only on the exact transition tick.
                if (_consecutiveFlushFailures == FlushFailoverThreshold)
                {
                    var fe = new AuditLogEntry
                    {
                        EventType = AuditEventType.AuditFlushFailover,
                        Severity = AuditSeverity.Critical,
                        Message = $"Audit flush entered failover mode after {_consecutiveFlushFailures} consecutive failures. " +
                                    $"Failover path: {failoverDir}",
                        Details = new Dictionary<string, string>
                        {
                            ["FailoverDirectory"] = failoverDir,
                            ["ConsecutiveFailures"] = _consecutiveFlushFailures.ToString(),
                            ["EntriesInFailoverBatch"] = entries.Count.ToString()
                        }
                    };
                    fe.PreviousHash = _lastSignature;
                    fe.Signature = ComputeSignature(fe, _lastSignature);
                    _lastSignature = fe.Signature;
                    var fePath = Path.Combine(failoverDir,
                        $"audit-failover-transition-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");
                    File.AppendAllText(fePath, JsonSerializer.Serialize(fe, SerializerOptions) + Environment.NewLine);
                }
            }
            catch (Exception fex)
            {
                Serilog.Log.Fatal(fex,
                    "[AUDIT] Failover write also failed — audit entries are being dropped. " +
                    "Check disk space and directory permissions for {Dir}", _logDirectory);
            }
        }

        // Marker file that tracks whether the UTC segment-filename migration note has been emitted.
        private static readonly string UtcFilenameMarkerFileName = "audit-utc-filename.marker";
        private string UtcFilenameMarkerPath => Path.Combine(_logDirectory, UtcFilenameMarkerFileName);

        /// <summary>
        /// Emits a single audit entry noting the switch from local-time to UTC segment filenames,
        /// then writes a marker file so it only fires once per installation.
        /// </summary>
        private void EmitSegmentFilenameUtcMarkerIfNeeded()
        {
            try
            {
                if (File.Exists(UtcFilenameMarkerPath)) return;
                Enqueue(new AuditLogEntry
                {
                    EventType = AuditEventType.ApplicationLifecycle,
                    Severity = AuditSeverity.Info,
                    Message = "[AUDIT] Segment filename basis changed from local to UTC",
                    Details = new Dictionary<string, string>
                    {
                        ["Note"] = "R-L6 migration: audit-YYYY-MM-DD.jsonl filenames now use UTC date, matching entry timestamp_utc"
                    }
                });
                File.WriteAllText(UtcFilenameMarkerPath, DateTime.UtcNow.ToString("o"));
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[AUDIT] Failed to emit UTC filename migration marker");
            }
        }

        /// <summary>
        /// Returns the path to the current active segment. Segment naming:
        /// audit-YYYYMMDD.jsonl for the first segment of a day, then
        /// audit-YYYYMMDD_NNNN.jsonl for rotated segments.
        /// </summary>
        private string GetCurrentLogFile()
        {
            // DE-H4: return cached segment path when available — avoids O(n) Directory.GetFiles
            // on every flush call (can reach 5000+ files over a 15-year deployment).
            if (_currentSegmentPath != null) return _currentSegmentPath;

            // R-L6: use UTC so segment filenames match entry timestamp_utc — no midnight
            // mismatch for observers in non-UTC zones.
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var baseFile = Path.Combine(_logDirectory, $"audit-{today}.jsonl");

            // Pick the highest-numbered rotated segment if any exist.
            var rotated = Directory.GetFiles(_logDirectory, $"audit-{today}_*.jsonl");
            if (rotated.Length == 0)
            {
                _currentSegmentPath = baseFile;
                return baseFile;
            }

            _currentSegmentPath = rotated
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .First();
            return _currentSegmentPath;
        }

        private void MaybeRotate(ref string logFile)
        {
            try
            {
                var fi = new FileInfo(logFile);
                if (!fi.Exists || fi.Length < RotationSizeBytes) return;

                // R-L6: UTC for consistency with GetCurrentLogFile
                var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
                int next = 1;
                while (File.Exists(Path.Combine(_logDirectory, $"audit-{today}_{next:D4}.jsonl")))
                    next++;

                logFile = Path.Combine(_logDirectory, $"audit-{today}_{next:D4}.jsonl");
                // DE-H4: invalidate cached segment so GetCurrentLogFile rescans on next call.
                _currentSegmentPath = logFile;
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
            // Snapshot the reference so one signature always uses one consistent key,
            // even if RotateHmacKey atomically swaps _hmacKey on another thread.
            var keySnapshot = _hmacKey;
            return Convert.ToBase64String(HMACSHA256.HashData(keySnapshot, input));
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
        /// Called by the 24-hour retention timer. Runs the sweep and emits audit + Serilog telemetry.
        /// </summary>
        private void RunRetentionSweep()
        {
            try
            {
                int deleted = ApplyRetentionPolicy();
                Serilog.Log.Information("[AUDIT] Retention sweep complete: {Deleted} entries removed", deleted);
                LogRetentionSweep(deleted, RetentionDays);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "[AUDIT] Retention sweep failed");
            }
        }

        /// <summary>
        /// Applies the retention policy by deleting old audit log files.
        /// Returns the number of files deleted.
        /// </summary>
        // Parses "audit-yyyy-MM-dd*.jsonl" and returns the UTC date embedded in the filename.
        // Returns null if the filename does not match the expected pattern.
        private static DateTime? TryParseDateFromSegmentName(string fileName)
        {
            // Matches: audit-2026-05-16.jsonl  or  audit-2026-05-16_0001.jsonl
            var name = Path.GetFileNameWithoutExtension(fileName);
            if (name.StartsWith("audit-", StringComparison.Ordinal) && name.Length >= 16)
            {
                var datePart = name.Substring(6, 10); // "yyyy-MM-dd"
                if (DateTime.TryParseExact(datePart, "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal |
                    System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var parsed))
                    return parsed;
            }
            return null;
        }

        /// <summary>
        /// Applies the retention policy by deleting old audit log segments.
        /// DE-C1: uses UTC exclusively — cutoff from DateTime.UtcNow, file age from
        /// UTC date embedded in the filename (falls back to CreationTimeUtc).
        /// Emits a Serilog Info line with the cutoff timestamp for forensic traceability.
        /// </summary>
        private int ApplyRetentionPolicy()
        {
            int deleted = 0;
            try
            {
                // DE-C1: UTC cutoff — never local clock.
                var cutoffDateUtc = DateTime.UtcNow.AddDays(-RetentionDays);
                Serilog.Log.Information(
                    "[AUDIT] Retention sweep: cutoff UTC = {CutoffUtc:o}, retentionDays = {Days}",
                    cutoffDateUtc, RetentionDays);

                var logFiles = Directory.GetFiles(_logDirectory, "audit-*.jsonl");

                foreach (var file in logFiles)
                {
                    var fileInfo = new FileInfo(file);
                    // Prefer the UTC date embedded in the filename; fall back to CreationTimeUtc.
                    var fileDate = TryParseDateFromSegmentName(fileInfo.Name) ?? fileInfo.CreationTimeUtc;

                    if (fileDate < cutoffDateUtc)
                    {
                        fileInfo.Delete();
                        deleted++;
                        Serilog.Log.Information(
                            "[AUDIT] Deleted old audit segment: {FileName} (fileDate={FileDate:o})",
                            fileInfo.Name, fileDate);
                    }
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[AUDIT] Error applying audit log retention policy");
            }
            return deleted;
        }

        /// <summary>
        /// Emits an auditable record of each retention sweep (SOC2 AU-11 evidence).
        /// </summary>
        public void LogRetentionSweep(int deletedCount, int retentionDays)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.AuditRetentionSweep,
                Severity = AuditSeverity.Info,
                Message = $"Audit retention sweep: removed {deletedCount} entries older than {retentionDays} days",
                Details = new Dictionary<string, string>
                {
                    ["DeletedCount"] = deletedCount.ToString(),
                    ["RetentionDays"] = retentionDays.ToString()
                }
            });
        }

        /// <summary>
        /// Emits an audit record when an admin marks a user's access as reviewed (SOC2 CC6.3).
        /// </summary>
        public void LogUserAccessReviewed(string reviewedUser, string reviewerName)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.UserAccessReviewed,
                Severity = AuditSeverity.Info,
                Message = $"User {reviewedUser} access marked reviewed by {reviewerName}",
                Details = new Dictionary<string, string>
                {
                    ["ReviewedUser"] = reviewedUser,
                    ["ReviewerName"] = reviewerName
                }
            });
        }

        /// <summary>
        /// Emits an audit record when an access review report is exported (SOC2 CC6.3).
        /// </summary>
        public void LogAccessReviewExported(string exportedBy, string filePath)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.AccessReviewExported,
                Severity = AuditSeverity.Info,
                Message = $"Access review report exported by {exportedBy}",
                Details = new Dictionary<string, string>
                {
                    ["ExportedBy"] = exportedBy,
                    ["FilePath"] = filePath
                }
            });
        }

        // ================================================================
        // SOC2 AU-6/AU-9 — CHAIN VERIFICATION
        // ================================================================

        /// <summary>
        /// Result returned by <see cref="VerifyChain"/> for use in UI and export artifacts.
        /// </summary>
        public sealed record ChainVerificationResult(
            bool Intact,
            int EntryCount,
            DateTime? FirstEntry,
            DateTime? LastEntry,
            DateTime VerifiedAt);

        /// <summary>
        /// DE-H1: Verifies the HMAC chain across ALL segments in chronological order,
        /// validating PreviousHash continuity across segment boundaries.
        /// Emits a Warning if total entry count exceeds 100 000 (forensic visibility for slow runs).
        /// Returns a <see cref="ChainVerificationResult"/> and emits an <c>AuditChainVerified</c> event.
        /// </summary>
        public ChainVerificationResult VerifyChain(string? triggeredBy = null)
        {
            Flush(); // ensure all pending entries are on disk before verifying
            int count = 0;
            bool intact = true;
            DateTime? first = null, last = null;

            try
            {
                var segments = GetAllSegmentsChronological();

                // Cross-segment chain: previousSig carries across segment boundaries.
                // Within each segment, the first entry's PreviousHash declares the expected
                // carry-in value — if it doesn't match what we carried from the prior segment
                // we treat that as a cross-segment break.
                string? crossSegmentSig = null;

                foreach (var segmentPath in segments)
                {
                    if (!File.Exists(segmentPath)) continue;

                    string? previousSig = null; // within this segment
                    bool firstEntryOfSegment = true;

                    foreach (var line in File.ReadLines(segmentPath))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var entry = JsonSerializer.Deserialize<AuditLogEntry>(line, SerializerOptions);
                        if (entry == null) continue;

                        count++;
                        first ??= entry.Timestamp;
                        last = entry.Timestamp;

                        if (firstEntryOfSegment)
                        {
                            firstEntryOfSegment = false;
                            var declaredPrev = entry.PreviousHash ?? string.Empty;

                            // Validate cross-segment continuity (skip for the very first segment).
                            if (crossSegmentSig != null &&
                                !string.Equals(crossSegmentSig, declaredPrev, StringComparison.Ordinal))
                            {
                                intact = false;
                                Serilog.Log.Warning(
                                    "[AUDIT] Cross-segment chain break at segment {Seg}: expected PreviousHash={Expected} got {Got}",
                                    Path.GetFileName(segmentPath), crossSegmentSig[..Math.Min(16, crossSegmentSig.Length)],
                                    declaredPrev.Length >= 16 ? declaredPrev[..16] : declaredPrev);
                                break;
                            }

                            previousSig = declaredPrev;
                        }

                        var recomputed = ComputeSignature(entry, previousSig!);
                        if (!string.Equals(recomputed, entry.Signature, StringComparison.Ordinal))
                        {
                            intact = false;
                            break;
                        }
                        previousSig = entry.Signature;
                    }

                    if (!intact) break;
                    crossSegmentSig = previousSig; // carry the last sig into next segment
                }

                if (count > 100_000)
                    Serilog.Log.Warning(
                        "[AUDIT] VerifyChain processed {Count} entries across {Segments} segments — consider archiving old segments",
                        count, segments.Length);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[AUDIT] On-demand chain verification failed");
                intact = false;
            }

            var result = new ChainVerificationResult(intact, count, first, last, DateTime.UtcNow);
            LogChainVerified(result, triggeredBy ?? Environment.UserName);
            return result;
        }

        /// <summary>Called by the scheduled verification timer (background).</summary>
        /// <summary>
        /// DE-H1: Scheduled verification — daily quick check (today's segment) + full
        /// multi-segment sweep every Audit:FullChainVerifyIntervalDays (default 7).
        /// </summary>
        private void RunScheduledVerification()
        {
            try
            {
                var daysSinceFull = (DateTime.UtcNow - _lastFullChainVerifyUtc).TotalDays;
                bool runFull = daysSinceFull >= _fullChainVerifyIntervalDays;

                var result = VerifyChain(runFull ? "scheduled-full" : "scheduled-quick");
                Serilog.Log.Information(
                    "[AUDIT] Scheduled chain verification ({Mode}): {Status}, {Count} entries",
                    runFull ? "FULL" : "QUICK",
                    result.Intact ? "INTACT" : "BROKEN",
                    result.EntryCount);

                if (runFull)
                    _lastFullChainVerifyUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "[AUDIT] Scheduled chain verification failed");
            }
        }

        /// <summary>Emits an audit record of a chain verification run (SOC2 AU-6/AU-9).</summary>
        public void LogChainVerified(ChainVerificationResult result, string triggeredBy)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.AuditChainVerified,
                Severity = result.Intact ? AuditSeverity.Info : AuditSeverity.Critical,
                Message = $"Audit chain verification: {(result.Intact ? "INTACT" : "BROKEN")} — {result.EntryCount} entries verified by {triggeredBy}",
                Details = new Dictionary<string, string>
                {
                    ["Intact"] = result.Intact.ToString(),
                    ["EntryCount"] = result.EntryCount.ToString(),
                    ["FirstEntry"] = result.FirstEntry?.ToString("o") ?? string.Empty,
                    ["LastEntry"] = result.LastEntry?.ToString("o") ?? string.Empty,
                    ["VerifiedAt"] = result.VerifiedAt.ToString("o"),
                    ["TriggeredBy"] = triggeredBy
                }
            });
        }

        /// <summary>Emits an audit record when a verification report is exported (SOC2 AU-9).</summary>
        public void LogChainVerificationExported(string exportedBy, string filePath, int entryCount, bool intact)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.AuditChainVerificationExported,
                Severity = AuditSeverity.Info,
                Message = $"Audit chain verification report exported by {exportedBy}",
                Details = new Dictionary<string, string>
                {
                    ["ExportedBy"] = exportedBy,
                    ["FilePath"] = filePath,
                    ["EntryCount"] = entryCount.ToString(),
                    ["Intact"] = intact.ToString()
                }
            });
        }

        // ================================================================
        // SOC2 AU-3 — AUDIT LOG EXPORT
        // ================================================================

        /// <summary>Emits an audit record when the audit log itself is exported (SOC2 AU-3).</summary>
        public void LogAuditLogExported(string exportedBy, string format, int entryCount, DateTime from, DateTime to)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.AuditLogExported,
                Severity = AuditSeverity.Info,
                Message = $"Audit log exported as {format} ({entryCount} entries) by {exportedBy}",
                Details = new Dictionary<string, string>
                {
                    ["ExportedBy"] = exportedBy,
                    ["Format"] = format,
                    ["EntryCount"] = entryCount.ToString(),
                    ["From"] = from.ToString("o"),
                    ["To"] = to.ToString("o")
                }
            });
        }

        // ================================================================
        // SOC2 CC6.2 — INDIVIDUAL USER-MUTATION AUDIT EVENTS
        // ================================================================

        /// <summary>Emits an audit record when a user is added to the RBAC roster (SOC2 CC6.2).</summary>
        public void LogUserAdded(string addedBy, string addedUser, string role)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.UserAdded,
                Severity = AuditSeverity.Info,
                Message = $"User '{addedUser}' added with role '{role}' by {addedBy}",
                Details = new Dictionary<string, string>
                {
                    ["AddedBy"] = addedBy,
                    ["AddedUser"] = addedUser,
                    ["Role"] = role
                }
            });
        }

        /// <summary>Emits an audit record when a user is removed from the RBAC roster (SOC2 CC6.2).</summary>
        public void LogUserRemoved(string removedBy, string removedUser, string formerRole)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.UserRemoved,
                Severity = AuditSeverity.Warning,
                Message = $"User '{removedUser}' (role: '{formerRole}') removed by {removedBy}",
                Details = new Dictionary<string, string>
                {
                    ["RemovedBy"] = removedBy,
                    ["RemovedUser"] = removedUser,
                    ["FormerRole"] = formerRole
                }
            });
        }

        /// <summary>Emits an audit record when a user attribute is changed (SOC2 CC6.2).</summary>
        public void LogUserUpdated(string updatedBy, string updatedUser, string field, string? oldValue, string? newValue)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.UserUpdated,
                Severity = AuditSeverity.Info,
                Message = $"User '{updatedUser}' {field} changed by {updatedBy}",
                Details = new Dictionary<string, string>
                {
                    ["UpdatedBy"] = updatedBy,
                    ["UpdatedUser"] = updatedUser,
                    ["Field"] = field,
                    ["OldValue"] = oldValue ?? string.Empty,
                    ["NewValue"] = newValue ?? string.Empty
                }
            });
        }

        // ================================================================
        // INTERNAL / CROSS-SERVICE HELPER
        // ================================================================

        /// <summary>
        /// General-purpose enqueue used by sibling services that cannot call the
        /// private overload directly (ConfigBaselineService, UptimeTrackerService, etc.).
        /// </summary>
        internal void Enqueue(AuditEventType eventType, AuditSeverity severity, string message,
            Dictionary<string, string>? details = null)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = eventType,
                Severity = severity,
                Message = message,
                Details = details ?? new Dictionary<string, string>()
            });
        }

        // ================================================================
        // L2: A1.2 — UPTIME SNAPSHOT EXPORT
        // ================================================================

        /// <summary>Emits an audit record when a 30-day uptime snapshot is exported (A1.2).</summary>
        public void LogUptimeSnapshotExported(string exportedBy, double uptimePercent, DateTime from, DateTime to)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.UptimeSnapshotExported,
                Severity = AuditSeverity.Info,
                Message = $"Uptime snapshot exported by {exportedBy}: {uptimePercent:F2}% over {(to - from).TotalDays:F0} days",
                Details = new Dictionary<string, string>
                {
                    ["ExportedBy"] = exportedBy,
                    ["UptimePercent"] = uptimePercent.ToString("F4"),
                    ["From"] = from.ToString("o"),
                    ["To"] = to.ToString("o")
                }
            });
        }

        // ================================================================
        // L3: CP-2 — CONTINUITY / DR TEST
        // ================================================================

        /// <summary>Emits an audit record when a DR test is manually recorded (CP-2).</summary>
        public void LogDrTestRecorded(string recordedBy, string? notes = null)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.DrTestRecorded,
                Severity = AuditSeverity.Info,
                Message = $"DR test recorded by {recordedBy}",
                Details = new Dictionary<string, string>
                {
                    ["RecordedBy"] = recordedBy,
                    ["Notes"] = notes ?? string.Empty
                }
            });
        }

        // ================================================================
        // L4: CM-3 — CONFIGURATION BASELINE / DRIFT
        // ================================================================

        /// <summary>Emits an audit record when configuration drift is detected (CM-3).</summary>
        public void LogConfigDriftDetected(string driftDetails, int fileCount)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.ConfigDriftDetected,
                Severity = AuditSeverity.Warning,
                Message = $"Configuration drift detected in {fileCount} file(s)",
                Details = new Dictionary<string, string>
                {
                    ["DriftedFiles"] = driftDetails,
                    ["FileCount"] = fileCount.ToString()
                }
            });
        }

        /// <summary>Emits an audit record when the config baseline is re-snapshotted (CM-3).</summary>
        public void LogConfigBaselineUpdated(string actor, int fileCount)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.ConfigBaselineUpdated,
                Severity = AuditSeverity.Info,
                Message = $"Configuration baseline updated by {actor} ({fileCount} files)",
                Details = new Dictionary<string, string>
                {
                    ["Actor"] = actor,
                    ["FileCount"] = fileCount.ToString()
                }
            });
        }

        // ================================================================
        // Server Config Baseline — Gap #9
        // ================================================================

        /// <summary>
        /// Emits an audit record when a monitored-server config baseline is captured.
        /// </summary>
        public void LogServerConfigBaselineCaptured(string serverName, string label, string capturedBy, int keyCount)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.ServerConfigBaselineCaptured,
                Severity = AuditSeverity.Info,
                Message = $"Server config baseline '{label}' captured for {serverName} by {capturedBy} ({keyCount} keys)",
                Details = new Dictionary<string, string>
                {
                    ["ServerName"] = serverName,
                    ["Label"] = label,
                    ["CapturedBy"] = capturedBy,
                    ["KeyCount"] = keyCount.ToString()
                }
            });
        }

        /// <summary>
        /// Emits an audit record when a config diff comparison is run.
        /// </summary>
        public void LogServerConfigBaselineCompared(string serverName, int baselineId, string label,
            string comparedBy, int added, int removed, int modified)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.ServerConfigBaselineCompared,
                Severity = AuditSeverity.Info,
                Message = $"Config diff for {serverName} vs baseline '{label}' (id={baselineId}): +{added} -{removed} ~{modified} by {comparedBy}",
                Details = new Dictionary<string, string>
                {
                    ["ServerName"] = serverName,
                    ["BaselineId"] = baselineId.ToString(),
                    ["Label"] = label,
                    ["ComparedBy"] = comparedBy,
                    ["Added"] = added.ToString(),
                    ["Removed"] = removed.ToString(),
                    ["Modified"] = modified.ToString()
                }
            });
        }

        /// <summary>
        /// Emits an audit record when a config diff is exported to CSV.
        /// </summary>
        public void LogServerConfigBaselineExported(string serverName, string label, string exportedBy, int rowCount)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.ServerConfigBaselineExported,
                Severity = AuditSeverity.Info,
                Message = $"Config diff exported for {serverName} baseline '{label}' by {exportedBy} ({rowCount} rows)",
                Details = new Dictionary<string, string>
                {
                    ["ServerName"] = serverName,
                    ["Label"] = label,
                    ["ExportedBy"] = exportedBy,
                    ["RowCount"] = rowCount.ToString()
                }
            });
        }

        // ================================================================
        // L5: IR-5 — INCIDENT STATE CHANGE
        // ================================================================

        /// <summary>Emits an audit record when an alert's incident lifecycle state changes (IR-5).</summary>
        public void LogIncidentStateChanged(long alertId, string alertName, string newState,
            string changedBy, string? notes = null)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.IncidentStateChanged,
                Severity = AuditSeverity.Info,
                Message = $"Incident #{alertId} ({alertName}) → {newState} by {changedBy}",
                Details = new Dictionary<string, string>
                {
                    ["AlertId"] = alertId.ToString(),
                    ["AlertName"] = alertName,
                    ["NewState"] = newState,
                    ["ChangedBy"] = changedBy,
                    ["Notes"] = notes ?? string.Empty
                }
            });
        }

        // ================================================================
        // L6: SC-28 / AU-9 — HMAC KEY AGE + ROTATION
        // ================================================================

        /// <summary>Emits a Warning when the HMAC key age exceeds the configured maximum (AU-9).</summary>
        public void LogHmacKeyAgeExceeded(int ageDays, int maxAgeDays)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.HmacKeyAgeExceeded,
                Severity = AuditSeverity.Warning,
                Message = $"HMAC audit key is {ageDays} days old (max: {maxAgeDays} days). Rotation recommended.",
                Details = new Dictionary<string, string>
                {
                    ["AgeDays"] = ageDays.ToString(),
                    ["MaxAgeDays"] = maxAgeDays.ToString()
                }
            });
        }

        /// <summary>Emits a Critical chain-anchor entry when the HMAC key is rotated (AU-9).</summary>
        public void LogHmacKeyRotated(string actor, int priorAgeDays)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.HmacKeyRotated,
                Severity = AuditSeverity.Critical,
                Message = $"Key rotated by {actor}. Prior key age: {priorAgeDays} days.",
                Details = new Dictionary<string, string>
                {
                    ["Actor"] = actor,
                    ["PriorAgeDays"] = priorAgeDays.ToString()
                }
            });
        }

        // ================================================================
        // SOC2 CC8.2 — CONFIG CHANGE WITH PRIOR VALUE
        // ================================================================

        /// <summary>
        /// Logs a configuration change capturing both old and new values (SOC2 CC8.2).
        /// Use this overload at all new/updated call sites.
        /// </summary>
        public void LogConfigurationChange(string configType, string action, string? oldValue, string? newValue, string? changedBy = null)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.ConfigurationChange,
                Severity = AuditSeverity.Info,
                Message = $"Configuration '{configType}' {action}" + (changedBy != null ? $" by {changedBy}" : string.Empty),
                Details = new Dictionary<string, string>
                {
                    ["ConfigType"] = configType,
                    ["Action"] = action,
                    ["OldValue"] = oldValue ?? string.Empty,
                    ["NewValue"] = newValue ?? string.Empty,
                    ["ChangedBy"] = changedBy ?? string.Empty
                }
            });
        }

        // ================================================================
        // PERFORMANCE BASELINES
        // ================================================================

        /// <summary>
        /// Emits an audit record when a user triggers baseline learning for a
        /// server/wait-type pair from the Performance Trends page.
        /// </summary>
        public void LogBaselineLearned(string serverName, string waitType, int bucketsWritten, int lookbackDays)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.BaselineLearned,
                Severity = AuditSeverity.Info,
                Message = $"Performance baseline learned for {serverName}/{waitType} ({bucketsWritten} buckets, {lookbackDays}d lookback)",
                Details = new Dictionary<string, string>
                {
                    ["ServerName"] = serverName,
                    ["WaitType"] = waitType,
                    ["BucketsWritten"] = bucketsWritten.ToString(),
                    ["LookbackDays"] = lookbackDays.ToString()
                }
            });
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
            _retentionTimer?.Dispose();
            _verificationTimer?.Dispose();
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
        SessionEvent,
        /// <summary>SOC2 AU-11: daily retention sweep evidence.</summary>
        AuditRetentionSweep,
        /// <summary>SOC2 CC6.3: admin marked a user's access as reviewed.</summary>
        UserAccessReviewed,
        /// <summary>SOC2 CC6.3: access review report exported.</summary>
        AccessReviewExported,
        /// <summary>SOC2 AU-6/AU-9: on-demand or scheduled chain integrity verification.</summary>
        AuditChainVerified,
        /// <summary>SOC2 AU-9: chain verification report exported as signed artifact.</summary>
        AuditChainVerificationExported,
        /// <summary>SOC2 AU-3/CC6.2: audit log entries exported (CSV or JSON).</summary>
        AuditLogExported,
        /// <summary>SOC2 CC6.2: a user account was added to the RBAC roster.</summary>
        UserAdded,
        /// <summary>SOC2 CC6.2: a user account was removed from the RBAC roster.</summary>
        UserRemoved,
        /// <summary>SOC2 CC6.2: a user account attribute (role, enabled state) was changed.</summary>
        UserUpdated,
        // ── L2: A1.2 ──────────────────────────────────────────────────────
        /// <summary>A1.2: 30-day uptime snapshot exported for SLA evidence.</summary>
        UptimeSnapshotExported,
        // ── L3: CP-2 ──────────────────────────────────────────────────────
        /// <summary>CP-2: DR test manually recorded by an admin.</summary>
        DrTestRecorded,
        // ── L4: CM-3 ──────────────────────────────────────────────────────
        /// <summary>CM-3: configuration drift detected against the baseline snapshot.</summary>
        ConfigDriftDetected,
        /// <summary>CM-3: admin explicitly re-snapshotted the current config as the new baseline.</summary>
        ConfigBaselineUpdated,
        /// <summary>Gap #9: user captured a sp_configure + surface-area snapshot for a monitored server.</summary>
        ServerConfigBaselineCaptured,
        /// <summary>Gap #9: user ran a config diff comparison against a stored server baseline.</summary>
        ServerConfigBaselineCompared,
        /// <summary>Gap #9: user exported a server config diff to CSV.</summary>
        ServerConfigBaselineExported,
        // ── L5: IR-5 ──────────────────────────────────────────────────────
        /// <summary>IR-5: alert incident lifecycle state changed (acknowledged / root-caused / closed).</summary>
        IncidentStateChanged,
        // ── L6: SC-28 / AU-9 ─────────────────────────────────────────────
        /// <summary>AU-9: HMAC key age exceeded the configured maximum; rotation recommended.</summary>
        HmacKeyAgeExceeded,
        /// <summary>AU-9: HMAC key rotated; chain-anchor entry.</summary>
        HmacKeyRotated,
        // ── Reliability ───────────────────────────────────────────────────
        /// <summary>Audit flush entered failover mode (primary write directory unavailable).</summary>
        AuditFlushFailover,
        /// <summary>Server circuit breaker opened; polling suppressed until back-off expires.</summary>
        ServerCircuitOpened,
        /// <summary>Server circuit breaker closed; polling resumed after successful connection.</summary>
        ServerCircuitClosed,
        // ── DE-H2: failover chain ─────────────────────────────────────────
        /// <summary>
        /// DE-H2: entry written to the .failover/ directory when the primary flush target
        /// is unavailable. Fully signed and chained; verifiers should include failover
        /// segments when performing full-chain audits.
        /// </summary>
        AuditFailoverEntry,
        // ── Diagnostic report bundles ──────────────────────────────────────
        /// <summary>One-click diagnostic report bundle generated (Executive Summary, DBA Handoff, or Audit Evidence).</summary>
        ReportBundleGenerated,
        // ── Performance baselines ─────────────────────────────────────────
        /// <summary>User triggered baseline learning for a server/wait-type pair (Performance Trends page).</summary>
        BaselineLearned,
        // ── Compliance Framework (Strategic Gap #3) ───────────────────────
        /// <summary>Gap #3: user exported a compliance evidence report (framework scorecard + VA findings).</summary>
        ComplianceReportExported,
        // ── Gated remediation lane ────────────────────────────────────────
        /// <summary>Remediation: a registered template was proposed for a server (gate 4 entry; nothing applied yet).</summary>
        RemediationProposed,
        /// <summary>Remediation: a human explicitly approved a proposed remediation (gate 4 cleared).</summary>
        RemediationApproved,
        /// <summary>Remediation: an approved remediation was executed against a server (gate 5; outcome recorded).</summary>
        RemediationApplied,
        /// <summary>Remediation: a previously applied remediation was rolled back to its pre-change state.</summary>
        RemediationRolledBack,
        /// <summary>Remediation: a MODELLED per-fix power-saving estimate was recorded (informational; not an apply).</summary>
        RemediationPowerEstimate
    }

    public enum AuditSeverity
    {
        Info,
        Warning,
        Error,
        Critical
    }
}
