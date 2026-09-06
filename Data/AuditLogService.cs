/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
        // H4 (2026-07-07): id of the current signing key + a cache of retired keys, so verification
        // can validate entries signed under a key that has since been rotated away. Archived keys
        // live beside the main key file as "<keyPath>.<keyId>" (same DPAPI-wrapped format).
        private string _currentKeyId = string.Empty;
        private readonly Dictionary<string, byte[]> _keyCache = new(StringComparer.Ordinal);
        private readonly object _keyCacheLock = new();
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
        // Cross-process append serialisation + the on-disk state we last left behind. Together
        // these let a second SQLTriage process (desktop app + a scheduled --audit CLI run share
        // one audit-logs directory) append to the same chain instead of forking it. See
        // TryCreateAppendMutex and SyncChainTailFromDisk.
        private readonly Mutex? _appendMutex;
        private string? _lastKnownSegmentPath;
        private long _lastKnownSegmentLength = -1;
        // The last completed VerifyChain result (see LastVerification). Written under _writeLock at
        // the end of the walk; null until this process has finished one.
        private ChainVerificationResult? _lastVerification;

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
        /// True when startup verification found entries it cannot judge either way: their
        /// <see cref="AuditLogEntry.KeyId"/> does not resolve to any key available on this box, so
        /// the signature can be neither confirmed nor refuted. That is an integrity GAP, not a
        /// tamper signal — a key-management accident (identity change, deleted archive) produces
        /// it, and conflating it with <see cref="ChainBroken"/> is what destroys the tamper signal.
        /// </summary>
        public bool ChainUnverifiable { get; private set; }

        /// <summary>
        /// Id of the key this service is signing NEW entries with, right now. Read under the same
        /// lock the rotation path writes it under (<c>_writeLock</c>, see <see cref="RotateHmacKey"/>,
        /// which wraps the whole rotation — key material and this field together — in it), so a
        /// caller can never observe the half-rotated state where the field has moved and the key
        /// material has not.
        /// <para>
        /// Added 2026-08-12 for the off-box co-sign lane: an observer that records the key id
        /// alongside a chain head can tell a key CHANGE from a chain change, which the head
        /// signature alone cannot. It is an identifier, not key material — <see cref="ComputeKeyId"/>
        /// is a truncated hash of the key and is already written into every entry's KeyId field and
        /// into the sidecar, so exposing it discloses nothing the log does not already carry.
        /// </para>
        /// </summary>
        public string CurrentKeyId
        {
            get { lock (_writeLock) { return _currentKeyId; } }
        }

        /// <summary>
        /// The most recent completed <see cref="VerifyChain"/> result, or null when this process has
        /// not finished one yet. Carries its own <see cref="ChainVerificationResult.VerifiedAt"/>,
        /// so a consumer that reports it can state WHEN the verdict was measured instead of
        /// implying it is current.
        /// <para>
        /// Exists because a full verify is not free: measured 2026-08-12 over a copy of this
        /// install's own chain (1631 entries, 5 segments) a single VerifyChain took hundreds of
        /// milliseconds — 813-910 ms on a loaded box, 246-342 ms re-measured the same day on the
        /// same binary and the same copied chain while idle, so treat the figure as load-dependent
        /// and not as a constant. What did reproduce number for number is the side effect, and it is
        /// the reason this property exists: each call APPENDS its own AuditChainVerified entry, so a
        /// caller that verifies on every
        /// publish both pays that cost per publish and grows the chain it is measuring. A consumer
        /// that only needs a recent verdict reads this and decides for itself whether it is fresh
        /// enough; one that needs a current verdict still calls <see cref="VerifyChain"/>.
        /// </para>
        /// </summary>
        public ChainVerificationResult? LastVerification
        {
            get { lock (_writeLock) { return _lastVerification; } }
        }

        /// <summary>Identifier of the first entry whose signing key could not be resolved (or null).</summary>
        public string? ChainUnverifiableFirstRecordId { get; private set; }

        /// <summary>KeyId of the first unresolvable signing key seen at startup (or null).</summary>
        public string? ChainUnverifiableKeyId { get; private set; }

        /// <summary>
        /// True when startup verification found entries it cannot judge AND has no provenance
        /// evidence with which to judge them. Distinct from <see cref="ChainUnverifiable"/> on
        /// purpose: that one means "we know this is a benign key gap", this one means "we do not
        /// know what this is". Callers that print a verdict to an operator, an auditor or a client
        /// MUST treat it as its own state and must not fold it into either neighbour.
        /// </summary>
        public bool ChainProvenanceIndeterminate { get; private set; }

        /// <summary>Identifier of the first entry the provenance evidence could not speak to (or null).</summary>
        public string? ChainIndeterminateFirstRecordId { get; private set; }

        /// <summary>KeyId of the first run that could be neither corroborated nor refuted (or null).</summary>
        public string? ChainIndeterminateKeyId { get; private set; }

        /// <summary>
        /// Plain-English cause when the indeterminate state came from the out-of-band anchor rather
        /// than from a run of entries (there is no key id to name in that case). Null otherwise.
        /// </summary>
        public string? ChainIndeterminateDetail { get; private set; }

        /// <summary>
        /// <see cref="ChainIndeterminateDetail"/> as a STANDALONE sentence, first letter capitalised.
        /// </summary>
        /// <remarks>
        /// The stored text is authored lower-case because <see cref="FoldWithAnchorFindings"/>
        /// splices it mid-sentence after "In addition, ". The audit page prints it as a sentence of
        /// its own between two others, where it began lower-case mid-banner. Rather than change the
        /// stored text and break the other reader, each surface takes the form it needs.
        /// </remarks>
        /// <summary>
        /// 2026-08-11. The startup scan found at least one point where the chain RESUMED after a
        /// break: an entry declaring a previous-hash link other than the one the scan carried in,
        /// whose own signature verifies against the link it declares. Distinct from
        /// <see cref="ChainBroken"/>, and it deliberately does NOT set it — see
        /// <see cref="ChainVerificationStatus.Restarted"/> for the incident that separated them.
        /// </summary>
        public bool ChainRestarted { get; private set; }

        /// <summary>Record id of the first restart point the startup scan found, if any.</summary>
        public string? ChainRestartFirstRecordId { get; private set; }

        public string? ChainIndeterminateDetailSentence =>
            string.IsNullOrEmpty(ChainIndeterminateDetail)
                ? ChainIndeterminateDetail
                : char.ToUpperInvariant(ChainIndeterminateDetail[0]) + ChainIndeterminateDetail[1..];

        /// <summary>
        /// Plain-English cause when <see cref="ChainBroken"/> was set by the out-of-band truncation
        /// check rather than by a signature mismatch. Null otherwise. Compliance artifacts fold this
        /// in — an on-disk chain whose remaining entries all verify is still not intact if entries
        /// were removed from it, and <see cref="VerifyChain"/> alone cannot see that.
        /// </summary>
        public string? ChainTruncationDetail { get; private set; }

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
            _hmacKey = LoadOrCreateHmacKey(_keyPath, out var keyLoadReport);
            // H4: establish the current key id and archive the key so it (and any prior archived
            // keys) remain available to verification across future rotations.
            _currentKeyId = ComputeKeyId(_hmacKey);
            EnsureCurrentKeyArchived(_currentKeyId, _hmacKey);

            // The key-provenance ledger MUST be loaded before any chain walk — the classifier uses
            // it to tell genuine key loss from a rewritten KeyId field. Read-only: an unreadable
            // anchor is evidence CheckTruncationAnchor still has to report.
            LoadKeyLedgerFromAnchor();
            RememberKeyInLedger(_currentKeyId);

            // L6 sidecar honesty (2026-08-01): hmac.key.meta records when the CURRENT key was born.
            // Replacing an unreadable key left the dead key's birth date in place, so the age check
            // then measured a key that no longer exists — it would have gone on warning (or staying
            // quiet) about the wrong key for the rest of the install's life. Reset it whenever the
            // key material changes. Unconditional: writing a sidecar enqueues nothing, so it cannot
            // skew the exact-entry-count assertions that gate the timer-only work below.
            if (keyLoadReport.Outcome is HmacKeyLoadOutcome.ReplacedUnreadable or HmacKeyLoadOutcome.Created)
                _hmacKeyTransition = ResetHmacMetaForNewKey(
                    configuration,
                    replacedExisting: keyLoadReport.Outcome == HmacKeyLoadOutcome.ReplacedUnreadable);

            _eventLogAvailable = TryEnsureEventLogSource();
            _appendMutex = TryCreateAppendMutex(_logDirectory);

            // Seed the chain tail under the cross-process lock so a concurrent process cannot
            // append between our read of the tail and our first write.
            bool seedLock = AcquireAppendLock();
            try { VerifyChainOnStartup(); }
            finally { ReleaseAppendLock(seedLock); }

            // The key replacement must land ON the chain, not only in the Serilog file — an
            // operator reading the audit log has to be able to see why history stopped verifying.
            // Emitted AFTER startup verification so it chains onto the real tail. Unconditional
            // (not gated on startFlushTimer): it only fires on the incident path, so it cannot
            // skew the exact-entry-count assertions of healthy tests.
            if (keyLoadReport.Outcome == HmacKeyLoadOutcome.ReplacedUnreadable)
                LogHmacKeyReplaced(keyLoadReport);
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

        /// <summary>
        /// The sidecar this launch wrote when the key material changed, held so the on-chain
        /// key-replacement record can name the transition it describes. Null on every launch that
        /// did not change the key.
        /// </summary>
        private HmacKeyMeta? _hmacKeyTransition;

        /// <summary>
        /// The last <see cref="AuditLogEntry.KeyId"/> the startup scan saw declared on disk. On a
        /// launch that replaced an unreadable key this is the id the outgoing entries CLAIMED, which
        /// is the only id available for a key whose blob cannot be unwrapped: the material is gone,
        /// so nothing can be recomputed from it. Reported as what it is, never as a measured id.
        /// </summary>
        private string? _lastKeyIdOnChainAtStartup;

        /// <summary>
        /// The key-age sidecar. Every field below <see cref="RotationDueAt"/> was added 2026-08-11.
        /// <para>
        /// THE DEFECT THIS FIXES (forensic verdict D5, measured on the live service's own
        /// audit-logs directory): the sidecar recorded <c>CreatedAt</c> and nothing else, so it
        /// could not say WHICH key it described. On the installed service it still carried the birth
        /// date of a key that had been destroyed eleven days earlier, and the age check and the
        /// rotation-due date were both computed from a key that no longer existed. A record that
        /// cannot name its subject cannot be checked against reality, so this one names it.
        /// </para>
        /// </summary>
        private sealed class HmacKeyMeta
        {
            public string CreatedAt { get; set; } = string.Empty;
            public string RotationDueAt { get; set; } = string.Empty;
            /// <summary>Id of the key this record describes. Empty on sidecars written before 2026-08-11.</summary>
            public string KeyId { get; set; } = string.Empty;
            /// <summary>Windows identity that wrote the key this record describes.</summary>
            public string WrittenBy { get; set; } = string.Empty;
            /// <summary>Id of the key this one replaced, when it replaced one.</summary>
            public string PreviousKeyId { get; set; } = string.Empty;
            /// <summary>Windows identity that wrote the key this one replaced, when it is known.</summary>
            public string PreviousWrittenBy { get; set; } = string.Empty;
            /// <summary>When the replacement happened, when this record describes one.</summary>
            public string ReplacedAt { get; set; } = string.Empty;
        }

        /// <summary>
        /// Reads the sidecar exactly as it is on disk, or null. Unlike
        /// <see cref="LoadOrCreateHmacMeta"/> this NEVER writes: the replacement path has to see the
        /// outgoing record before it overwrites it, and a rebuild in between would erase the very
        /// facts the replacement is supposed to carry forward.
        /// </summary>
        private HmacKeyMeta? TryReadHmacMeta()
        {
            try
            {
                if (!File.Exists(HmacMetaPath)) return null;
                var json = File.ReadAllText(HmacMetaPath, Encoding.UTF8);
                return JsonSerializer.Deserialize<HmacKeyMeta>(json, SerializerOptions);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[AUDIT] Could not read {Path} while recording a key transition", HmacMetaPath);
                return null;
            }
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
                catch (Exception ex)
                {
                    // Triaged against the write-guard tier rule on 2026-08-05: BELOW the line, but
                    // not silent. The sidecar is DERIVED — it is rebuilt below from the key file's
                    // own creation time, which the in-place key overwrite preserves, so a damaged
                    // one costs no key material and shifts the rotation clock by nothing on the
                    // normal path. It gets neither a refusal nor a quarantine. It does get a line,
                    // because a bare catch left a corrupted rotation-age record replaced with no
                    // evidence it had ever existed.
                    Serilog.Log.Warning(ex,
                        "[AUDIT] {Path} could not be read and is being rebuilt from the key file's creation time; "
                        + "the previous rotation-age record is gone.", HmacMetaPath);
                }
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

        /// <summary>
        /// L6 (2026-08-01): stamp the age sidecar with NOW because the key material just changed.
        /// <para>
        /// Deleting the sidecar instead would not work: <see cref="LoadOrCreateHmacMeta"/> falls back
        /// to <c>File.GetCreationTimeUtc(hmac.key)</c>, the key file is overwritten in place, and NTFS
        /// tunnelling hands back the ORIGINAL creation timestamp for a file recreated under the same
        /// name — so the dead key's birth date would come straight back. Write it explicitly.
        /// </para>
        /// </summary>
        /// <param name="replacedExisting">
        /// True when this write follows a key that was DESTROYED rather than a first mint. It
        /// decides whether the transition fields are filled: naming a "previous key" on a brand-new
        /// install would invent a predecessor that never existed.
        /// </param>
        private HmacKeyMeta ResetHmacMetaForNewKey(IConfiguration? configuration, bool replacedExisting)
        {
            int maxAgeDays = configuration?.GetValue<int>("Audit:HmacKeyMaxAgeDays", 365) ?? 365;
            var now = DateTime.UtcNow;
            // Read the outgoing record BEFORE overwriting it. Its KeyId and WrittenBy are the only
            // surviving description of the key that just went dark: the blob itself cannot be
            // unwrapped, so its id cannot be recomputed from the material.
            var outgoing = replacedExisting ? TryReadHmacMeta() : null;

            var meta = new HmacKeyMeta
            {
                CreatedAt = now.ToString("o"),
                RotationDueAt = now.AddDays(maxAgeDays).ToString("o"),
                KeyId = _currentKeyId,
                WrittenBy = SafeIdentityName(),
                PreviousKeyId = outgoing?.KeyId ?? string.Empty,
                PreviousWrittenBy = outgoing?.WrittenBy ?? string.Empty,
                ReplacedAt = replacedExisting ? now.ToString("o") : string.Empty
            };
            WriteHmacMeta(meta);
            return meta;
        }

        /// <summary>What the key-age sidecar can and cannot support on this launch.</summary>
        internal enum HmacKeyAgeBasis
        {
            /// <summary>The sidecar names the key in use. The age describes that key.</summary>
            NamesTheKeyInUse,
            /// <summary>
            /// The sidecar names a DIFFERENT key from the one in use. Its age and rotation-due date
            /// belong to that other key, so neither is reported.
            /// </summary>
            NamesAnotherKey,
            /// <summary>
            /// The sidecar names no key at all (written before 2026-08-11). The age it supports
            /// cannot be attributed to the key in use, and is reported with that said.
            /// </summary>
            NamesNoKey
        }

        /// <summary>
        /// Reads the sidecar and says what it can support, WITHOUT logging or auditing, so the
        /// decision is testable on its own. See <see cref="HmacKeyMeta"/> for the live-service
        /// defect this exists to stop: a sidecar describing a destroyed key, with the age and the
        /// rotation-due date computed from it as though it described the key in use.
        /// </summary>
        /// <summary>
        /// Whole days between a sidecar's <c>CreatedAt</c> and now, or -1 when it cannot be read.
        /// <para>
        /// FOUND 2026-08-11 by replaying the installed service's own chain: the age was short by
        /// this machine's UTC offset on every reading. <c>DateTime.TryParse</c> converts a
        /// round-trip "…Z" stamp to a LOCAL <c>DateTime</c>, and the result was then subtracted from
        /// <c>DateTime.UtcNow</c> — so the live sidecar's 10-day-old record measured 9 days at
        /// +12:00. A rotation-due date that drifts by the timezone is a small lie in a record whose
        /// only job is to be accurate, and it sits in the method this lane is already fixing.
        /// </para>
        /// </summary>
        private static int WholeDaysSince(string stamp)
        {
            // RoundtripKind alone: it cannot be combined with AdjustToUniversal, and it is the one
            // that preserves the trailing Z as Kind=Utc instead of silently shifting to local.
            if (!DateTime.TryParse(stamp, CultureInfo.InvariantCulture,
                                   DateTimeStyles.RoundtripKind, out var parsed))
                return -1;
            var utc = parsed.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
                : parsed.ToUniversalTime();
            return (int)(DateTime.UtcNow - utc).TotalDays;
        }

        internal (HmacKeyAgeBasis Basis, string SidecarKeyId, int AgeDays) DescribeHmacKeyAgeBasis(int maxAgeDays)
        {
            var meta = LoadOrCreateHmacMeta(_keyPath, maxAgeDays);
            int ageDays = WholeDaysSince(meta.CreatedAt);

            if (string.IsNullOrEmpty(meta.KeyId))
                return (HmacKeyAgeBasis.NamesNoKey, string.Empty, ageDays);

            return string.Equals(meta.KeyId, _currentKeyId, StringComparison.OrdinalIgnoreCase)
                ? (HmacKeyAgeBasis.NamesTheKeyInUse, meta.KeyId, ageDays)
                : (HmacKeyAgeBasis.NamesAnotherKey, meta.KeyId, ageDays);
        }

        private void CheckHmacKeyAge(IConfiguration? configuration)
        {
            int maxAgeDays = configuration?.GetValue<int>("Audit:HmacKeyMaxAgeDays", 365) ?? 365;
            var (basis, sidecarKeyId, ageDays) = DescribeHmacKeyAgeBasis(maxAgeDays);

            if (basis == HmacKeyAgeBasis.NamesAnotherKey)
            {
                // Say so, and compute nothing from it. This is the exact shape found on the live
                // service: a sidecar describing a key destroyed eleven days earlier, with the age
                // and the rotation-due date derived from it as though it were the key in use.
                Serilog.Log.Error(
                    "[AUDIT] {Path} describes key {SidecarKeyId}, but the key in use is {CurrentKeyId}. The age and " +
                    "rotation-due date in that record belong to a key that is not the one signing entries, so neither " +
                    "is reported. Rotating the key from Settings writes a record that names the key in use.",
                    HmacMetaPath, sidecarKeyId, _currentKeyId);
                return;
            }

            if (ageDays < 0) return;

            if (ageDays > maxAgeDays)
            {
                if (basis == HmacKeyAgeBasis.NamesNoKey)
                    Serilog.Log.Warning(
                        "[AUDIT] The key-age record says {AgeDays} days (max: {MaxAgeDays}), but it was written before " +
                        "this build began naming the key it describes, so that age cannot be attributed to key " +
                        "{CurrentKeyId}. Treat it as the age of whatever key was in use when the record was written. " +
                        "Consider rotating via Settings.", ageDays, maxAgeDays, _currentKeyId);
                else
                    Serilog.Log.Warning(
                        "[AUDIT] HMAC key {CurrentKeyId} is {AgeDays} days old (max: {MaxAgeDays}). Consider rotating via Settings.",
                        _currentKeyId, ageDays, maxAgeDays);

                LogHmacKeyAgeExceeded(ageDays, maxAgeDays, _currentKeyId,
                                      ageAttributedToKeyInUse: basis == HmacKeyAgeBasis.NamesTheKeyInUse);
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
                // Same UTC-offset defect as the age check had — see WholeDaysSince. The prior age
                // is printed on the rotation's own on-chain anchor entry, so it is a recorded fact.
                int priorAgeDays = Math.Max(0, WholeDaysSince(meta.CreatedAt));

                // H4: archive the OUTGOING key before we overwrite the main key file, so every
                // entry it signed stays verifiable after this rotation. We still hold its material,
                // so this is also the last chance to upgrade a legacy-scope archive of it (B3).
                EnsureCurrentKeyArchived(_currentKeyId, _hmacKey);

                // Generate and persist new key.
                var newRaw = RandomNumberGenerator.GetBytes(32);
                var newKeyId = ComputeKeyId(newRaw);
                try
                {
                    WriteWrappedHmacKey(_keyPath, newRaw);
                    EnsureCurrentKeyArchived(newKeyId, newRaw); // H4: archive the incoming key too
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "[AUDIT] HMAC key rotation failed during key write");
                    throw;
                }

                // Update meta sidecar.
                var now = DateTime.UtcNow;
                // The sidecar is written in the same operation as the key, and it NAMES the key it
                // describes plus the transition it came from. A rotation is a planned replacement,
                // so both the outgoing id and the identity that wrote it are known here and are
                // recorded rather than left to be reconstructed later.
                var newMeta = new HmacKeyMeta
                {
                    CreatedAt = now.ToString("o"),
                    RotationDueAt = now.AddDays(maxAgeDays).ToString("o"),
                    KeyId = newKeyId,
                    WrittenBy = SafeIdentityName(),
                    PreviousKeyId = _currentKeyId,
                    PreviousWrittenBy = meta.WrittenBy,
                    ReplacedAt = now.ToString("o")
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
                // The anchor entry is signed under the OLD key, so it carries the OLD key id.
                rotationEntry.KeyId = _currentKeyId;

                // Same cross-process append lock and tail reconciliation as Flush — the anchor
                // has to land after whatever another SQLTriage process last wrote, otherwise
                // the chain forks at exactly the rotation point.
                bool appendLock = AcquireAppendLock();
                try
                {
                    var anchorPrevSig = SyncChainTailFromDisk();
                    rotationEntry.PreviousHash = anchorPrevSig;
                    rotationEntry.Signature = ComputeSignature(rotationEntry, anchorPrevSig);
                    _lastSignature = rotationEntry.Signature;

                    var logFile = GetCurrentLogFile();
                    MaybeRotate(ref logFile);
                    File.AppendAllText(logFile,
                        JsonSerializer.Serialize(rotationEntry, SerializerOptions) + Environment.NewLine);
                    RememberSegmentState(logFile);
                }
                finally
                {
                    ReleaseAppendLock(appendLock);
                }

                // Atomically replace the reference so concurrent ComputeSignature calls see
                // either fully-old or fully-new key (never a partially-copied array).
                // .NET reference assignment is atomic on all supported architectures.
                _hmacKey = newRaw;
                _currentKeyId = newKeyId;   // H4: subsequent entries sign+tag under the new key
                // Succession order matters to the verifier: an entry claiming a key that was already
                // superseded before it was written is refuted (R2 in ClassifyUnverifiableRun).
                RememberKeyInLedger(newKeyId);
                // H5: the rotation anchor is a flush of its own — advance the truncation anchor.
                WriteTruncationAnchor(_lastSignature, rotationEntry.Timestamp);

                Serilog.Log.Information("[AUDIT] HMAC key rotated by {Actor}. Prior age: {Age} days", actor, priorAgeDays);
            }
        }

        /// <summary>How <see cref="LoadOrCreateHmacKey"/> arrived at the key it returned.</summary>
        internal enum HmacKeyLoadOutcome
        {
            /// <summary>Existing key file unwrapped cleanly — the normal case.</summary>
            Loaded,
            /// <summary>No key file existed; a fresh key was minted for a brand-new chain.</summary>
            Created,
            /// <summary>Legacy raw 32-byte key was re-wrapped under DPAPI; SAME key material.</summary>
            Migrated,
            /// <summary>
            /// An existing key file was present but could NOT be unwrapped under the current
            /// Windows identity, so a brand-new key had to be minted. Every entry signed by the
            /// old key is now unverifiable. This is an incident, not a routine outcome.
            /// </summary>
            ReplacedUnreadable
        }

        /// <summary>Diagnostic detail about a key load, so the caller can log + audit it loudly.</summary>
        internal sealed record HmacKeyLoadReport(
            HmacKeyLoadOutcome Outcome,
            int UnreadableBlobLength = 0,
            string? PreservedPath = null,
            string? FailureDetail = null);

        private static byte[] LoadOrCreateHmacKey(string keyPath)
            => LoadOrCreateHmacKey(keyPath, out _);

        private static byte[] LoadOrCreateHmacKey(string keyPath, out HmacKeyLoadReport report)
        {
            // Non-null only when a key FILE existed and we could not read a usable key out of it.
            // That is the "we are about to destroy the ability to verify history" case, and it must
            // never be silent (2026-08-01: a service-account change did exactly this in production
            // and the only visible symptom was a false BROKEN verdict).
            byte[]? unreadableBlob = null;
            string? failureDetail = null;

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
                            var loaded = TryUnwrapHmacKey(blob, out var wasLegacyScope);
                            if (wasLegacyScope)
                            {
                                // Same key material, safer wrapping. Nothing is recovered here — this
                                // only fires when the identity that wrapped the blob is the one
                                // running, i.e. before the accident, never after it.
                                Serilog.Log.Information(
                                    "[AUDIT] Re-wrapped the audit HMAC key from DPAPI CurrentUser to LocalMachine " +
                                    "scope (same key, unchanged chain). A later change of service identity can no " +
                                    "longer orphan it.");
                                try { WriteWrappedHmacKey(keyPath, loaded); }
                                catch (Exception wex)
                                {
                                    Serilog.Log.Warning(wex,
                                        "[AUDIT] Could not re-wrap the audit HMAC key under LocalMachine scope — " +
                                        "it stays readable only by the current identity.");
                                }
                            }
                            report = new HmacKeyLoadReport(HmacKeyLoadOutcome.Loaded);
                            return loaded;
                        }
                        catch (CryptographicException ex)
                        {
                            // Legacy raw key (pre-DPAPI): 32 exact bytes written by older builds.
                            // Migrate: re-wrap under DPAPI, log once, continue with the same key.
                            // UNCHANGED behaviour — this is a legitimate same-key upgrade.
                            if (blob.Length == 32)
                            {
                                Serilog.Log.Information("[AUDIT] Migrated HMAC key to DPAPI-wrapped format");
                                WriteWrappedHmacKey(keyPath, blob);
                                report = new HmacKeyLoadReport(HmacKeyLoadOutcome.Migrated);
                                return blob;
                            }
                            // Neither scope opened it and it is not a raw 32-byte key. Overwhelmingly
                            // likely cause: the blob was wrapped under the LEGACY CurrentUser scope by
                            // a DIFFERENT Windows identity — e.g. the service account changed before
                            // this build's LocalMachine wrapping was in place. The raw key is NOT
                            // recoverable under this identity, and nothing below pretends otherwise.
                            // Keys written by this build are LocalMachine-wrapped, so an identity
                            // change cannot orphan them again.
                            unreadableBlob = blob;
                            failureDetail = ex.GetType().Name + ": " + ex.Message;
                        }
                    }
                    else
                    {
                        // Non-Windows: key is stored raw (DPAPI unavailable); accept any ≥32-byte file.
                        if (blob.Length >= 32)
                        {
                            report = new HmacKeyLoadReport(HmacKeyLoadOutcome.Loaded);
                            return blob;
                        }
                        unreadableBlob = blob;
                        failureDetail = $"key file is {blob.Length} bytes, below the 32-byte minimum";
                    }
                }
                catch (Exception ex)
                {
                    // A key file exists but we could not even read it (locked/ACL'd). Still an
                    // incident: we are about to sign under a key that cannot verify history.
                    failureDetail = ex.GetType().Name + ": " + ex.Message;
                }
            }

            var fresh = RandomNumberGenerator.GetBytes(32);
            bool replacingExisting = File.Exists(keyPath);
            string? preservedPath = null;

            if (replacingExisting)
            {
                // Preserve the unreadable blob BEFORE overwriting it. Losing a key is recoverable
                // if the original identity is ever restored; destroying the blob is not.
                preservedPath = PreserveUnreadableKeyFile(keyPath, unreadableBlob);

                Serilog.Log.Error(
                    "[AUDIT] HMAC KEY REPLACED — an existing audit signing key was found at {KeyPath} but could not be " +
                    "unwrapped under the current Windows identity ({Identity}). Blob length: {BlobLength} bytes " +
                    "(a raw legacy key is exactly 32). Reason: {Reason}. A NEW key has been minted, so every audit " +
                    "entry signed by the previous key is now UNVERIFIABLE — its integrity can be neither confirmed " +
                    "nor denied. This is a key-management event, NOT evidence of tampering. The unreadable key blob " +
                    "was preserved at {PreservedPath} for forensic recovery if the original identity is restored.",
                    keyPath,
                    SafeIdentityName(),
                    unreadableBlob?.Length ?? -1,
                    failureDetail ?? "unknown",
                    preservedPath ?? "(preservation FAILED — see prior warning)");
            }

            try
            {
                WriteWrappedHmacKey(keyPath, fresh);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to persist audit HMAC key — chain will restart next launch");
            }

            report = replacingExisting
                ? new HmacKeyLoadReport(HmacKeyLoadOutcome.ReplacedUnreadable,
                                        unreadableBlob?.Length ?? -1, preservedPath, failureDetail)
                : new HmacKeyLoadReport(HmacKeyLoadOutcome.Created);
            return fresh;
        }

        /// <summary>
        /// Copies an unreadable key file aside as "&lt;keyPath&gt;.unreadable-&lt;utc-stamp&gt;" so the
        /// original blob survives the overwrite. Returns the path written, or null if it could not be.
        /// </summary>
        private static string? PreserveUnreadableKeyFile(string keyPath, byte[]? blob)
        {
            try
            {
                var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
                var preservedPath = keyPath + ".unreadable-" + stamp;
                // Never clobber a previously preserved blob.
                int suffix = 1;
                while (File.Exists(preservedPath))
                    preservedPath = keyPath + ".unreadable-" + stamp + "-" + suffix++;

                if (blob != null)
                {
                    File.WriteAllBytes(preservedPath, blob);
                }
                else
                {
                    // We never got the bytes into memory (read failed) — copy the file itself.
                    try { File.SetAttributes(keyPath, FileAttributes.Normal); } catch { }
                    File.Copy(keyPath, preservedPath, overwrite: false);
                }
                try { File.SetAttributes(preservedPath, FileAttributes.Hidden); } catch { }
                return preservedPath;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex,
                    "[AUDIT] Could not preserve the unreadable HMAC key blob beside {KeyPath} — " +
                    "forensic recovery of the previous key will NOT be possible.", keyPath);
                return null;
            }
        }

        /// <summary>Best-effort current-identity name for diagnostics; never throws.</summary>
        private static string SafeIdentityName()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var wi = System.Security.Principal.WindowsIdentity.GetCurrent();
                    if (!string.IsNullOrEmpty(wi.Name)) return wi.Name;
                }
            }
            catch { }
            try { return Environment.UserDomainName + "\\" + Environment.UserName; }
            catch { return "(unknown)"; }
        }

        /// <summary>
        /// Writes <paramref name="rawKey"/> to <paramref name="keyPath"/> wrapped with
        /// DPAPI LocalMachine scope on Windows, or as raw bytes on non-Windows.
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

        /// <summary>
        /// RULED 2026-08-01 (Adrian): the audit HMAC key is wrapped LocalMachine, not CurrentUser.
        /// <para>
        /// This file was the outlier — <c>Data/SqliteCipherHelper.cs</c> already wraps the SQLite
        /// cipher key LocalMachine, and anyone who can unprotect that key can already decrypt the
        /// store, so the marginal exposure is small. The cost of the old scope was not small: when
        /// the installed service's Windows account changed between two launches, the key became
        /// unreadable, a fresh one was minted, and a 307-entry chain nobody had touched was reported
        /// as tampered.
        /// </para>
        /// <para>
        /// PREVENTS RECURRENCE ONLY. A key that is ALREADY orphaned inside another identity's
        /// CurrentUser blob is not recovered by this and never will be — the plaintext exists nowhere
        /// else. The read-path upgrade in <see cref="TryUnwrapHmacKey"/> re-wraps a legacy blob only
        /// while the identity that wrote it is still the one running, i.e. before the accident and
        /// never after it. Entries the live incident already orphaned stay permanently unverifiable.
        /// </para>
        /// <para>
        /// ⚠ RE-REVIEWED 2026-08-20 for the C1 DPAPI-scope ruling (CurrentUser + LocalMachine
        /// read-fallback, but only where one account both writes and reads). The 2026-08-01 ruling
        /// above IS that answer already: LocalMachine here is the direct fix for exactly the
        /// cross-account failure C1 is trying to avoid elsewhere, so this site stays as-is. Left
        /// unchanged; recorded in WORKLIST-2026-08-20-app-lane-wave.md needsRuling alongside the
        /// other three LocalMachine-DPAPI sites.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static byte[] WrapHmacKey(byte[] rawKey) =>
            System.Security.Cryptography.ProtectedData.Protect(
                rawKey, HmacKeyEntropy,
                System.Security.Cryptography.DataProtectionScope.LocalMachine);

        /// <summary>
        /// Unwraps a key blob written under EITHER scope, and reports which one it was.
        /// <para>
        /// MEASURED, not assumed (2026-08-01, this platform): DPAPI stores the scope INSIDE the blob
        /// and <c>CryptUnprotectData</c> ignores the scope argument entirely — a CurrentUser blob
        /// unprotects fine when you pass LocalMachine and vice versa. So one call opens either kind,
        /// the argument below documents intent only, and a try/catch "fall back to the other scope"
        /// would be unreachable code that reads as a safety net while doing nothing. The scope has to
        /// be read out of the blob instead; see <see cref="IsLocalMachineWrapped"/>.
        /// </para>
        /// <para>
        /// <paramref name="wasLegacyScope"/> true tells the caller to re-wrap the file LocalMachine:
        /// a silent, same-material upgrade that removes the identity dependency for next time.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static byte[] TryUnwrapHmacKey(byte[] blob, out bool wasLegacyScope)
        {
            var raw = System.Security.Cryptography.ProtectedData.Unprotect(
                blob, HmacKeyEntropy,
                System.Security.Cryptography.DataProtectionScope.LocalMachine);
            wasLegacyScope = !IsLocalMachineWrapped(blob);
            return raw;
        }

        // A DPAPI blob carries a flags DWORD at byte 40; bit 0x4 is CRYPTPROTECT_LOCAL_MACHINE.
        // This layout is not in the public contract, so it is pinned by
        // DpapiScopeProbe_SeparatesCurrentUserFromLocalMachineBlobs, which generates both kinds with
        // ProtectedData itself and asserts the probe still splits them.
        private const int DpapiFlagsOffset = 40;
        private const uint CryptProtectLocalMachine = 0x4;

        /// <summary>
        /// True only when the blob is DEFINITELY LocalMachine-wrapped. Anything unreadable returns
        /// false, so the caller re-wraps — the conservative direction: an unnecessary rewrite is a
        /// wasted 262-byte write, a missed one leaves the key tied to one Windows identity.
        /// </summary>
        private static bool IsLocalMachineWrapped(byte[] blob)
        {
            if (blob.Length < DpapiFlagsOffset + sizeof(uint)) return false;
            var flags = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                blob.AsSpan(DpapiFlagsOffset, sizeof(uint)));
            return (flags & CryptProtectLocalMachine) != 0;
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
                // Warning, not Debug: with the source unregistered, Critical audit events lose
                // their out-of-band copy, which is the whole point of the Event Log mirror.
                // The operator needs to know that happened, and Debug is off by default.
                Serilog.Log.Warning(ex,
                    "[AUDIT] Event Log source '{Source}' could not be registered (requires admin) — " +
                    "Critical audit entries will not be mirrored to the Windows Event Log.",
                    EventLogSource);
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

        /// <summary>True if any audit segment currently holds at least one entry.</summary>
        private bool HasContentSegments()
        {
            try
            {
                foreach (var seg in GetAllSegmentsChronological())
                    foreach (var line in File.ReadLines(seg))
                        if (!string.IsNullOrWhiteSpace(line)) return true;
            }
            catch { /* best-effort */ }
            return false;
        }

        // ── Cross-process append lock ────────────────────────────────────────────
        // The desktop app and a scheduled `--audit` CLI run are two processes over ONE
        // audit-logs directory. Each holds its chain tail in memory, so without coordination
        // both sign against the same tail and the chain forks. The lock serialises the
        // read-tail-then-append window; SyncChainTailFromDisk does the reconciliation.

        /// <summary>
        /// Creates the named append mutex for <paramref name="logDirectory"/>. Tries the Global
        /// namespace first so an interactive desktop session and a scheduled task running under a
        /// service account serialise against each other; creating a Global object needs
        /// SeCreateGlobalPrivilege, which an ordinary interactive user does NOT hold, so it falls
        /// back to the session-local namespace and then to no lock at all. Returning null is not a
        /// failure path we can refuse on — audit writing must never stop because a lock is
        /// unavailable — so the caller proceeds unserialised and the warning below is the signal.
        /// </summary>
        private static Mutex? TryCreateAppendMutex(string logDirectory)
        {
            string key;
            try
            {
                key = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(Path.GetFullPath(logDirectory).ToLowerInvariant())), 0, 8);
            }
            catch
            {
                key = "default";
            }

            foreach (var name in new[] { $@"Global\SQLTriage.AuditLog.{key}", $@"Local\SQLTriage.AuditLog.{key}" })
            {
                try { return new Mutex(initiallyOwned: false, name); }
                catch (Exception ex)
                {
                    Serilog.Log.Debug(ex, "[AUDIT] Could not open append mutex {Name}", name);
                }
            }

            Serilog.Log.Warning(
                "[AUDIT] Cross-process append lock unavailable for {Dir} — if another SQLTriage " +
                "process writes here concurrently, appends are not serialised.", logDirectory);
            return null;
        }

        /// <summary>
        /// Waits for the append lock. Returns true only when this call took ownership (so the
        /// caller must release). A timeout returns false and the caller proceeds WITHOUT the
        /// lock: dropping an audit entry is worse than an unserialised write.
        /// </summary>
        private bool AcquireAppendLock()
        {
            if (_appendMutex == null) return false;
            try
            {
                if (_appendMutex.WaitOne(TimeSpan.FromSeconds(5))) return true;
                Serilog.Log.Warning("[AUDIT] Timed out waiting for the cross-process append lock — writing unserialised.");
                return false;
            }
            catch (AbandonedMutexException)
            {
                // Previous holder died mid-write. We now own the mutex; the on-disk tail is
                // re-read by SyncChainTailFromDisk before anything is signed.
                Serilog.Log.Warning("[AUDIT] Append lock was abandoned by another process — taking ownership.");
                return true;
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "[AUDIT] Append lock wait failed — writing unserialised.");
                return false;
            }
        }

        private void ReleaseAppendLock(bool held)
        {
            if (!held || _appendMutex == null) return;
            try { _appendMutex.ReleaseMutex(); }
            catch (Exception ex) { Serilog.Log.Debug(ex, "[AUDIT] Append lock release failed"); }
        }

        /// <summary>
        /// Returns the signature the next batch must chain to, reconciling with anything another
        /// SQLTriage process appended since our last write.
        ///
        /// Fast path: the active segment is still byte-for-byte what this process left it as, so
        /// the cached in-memory tail is authoritative and no extra IO happens — the single-process
        /// case costs one FileInfo.Length per flush and nothing else.
        ///
        /// Slow path (length moved, or first flush of the session): the segment cache is dropped,
        /// the newest segment is rescanned and its last signature is read back. Re-seeding is
        /// deliberately NOT unconditional — the segment having SHRUNK since our own last write
        /// means entries were removed, and re-seeding onto the new tail would let a truncation
        /// verify clean. In that case the in-memory tail is kept so the break still surfaces.
        /// Full truncation detection remains CheckTruncationAnchor's job; this only avoids
        /// papering over one.
        /// </summary>
        private string SyncChainTailFromDisk()
        {
            // A break is already known in this chain, so the startup seed deliberately points at
            // the last signature that still verified — NOT at the file's last line. Re-reading the
            // tail here would chain new entries onto the suspect record and hide the break.
            if (ChainBroken) return _lastSignature;

            try
            {
                var current = GetCurrentLogFile();
                var info = new FileInfo(current);
                long length = info.Exists ? info.Length : 0;

                bool sameSegmentAsOurLastWrite =
                    string.Equals(current, _lastKnownSegmentPath, StringComparison.OrdinalIgnoreCase);

                if (sameSegmentAsOurLastWrite && length == _lastKnownSegmentLength)
                    return _lastSignature;

                if (sameSegmentAsOurLastWrite && _lastKnownSegmentLength >= 0 && length < _lastKnownSegmentLength)
                {
                    Serilog.Log.Error(
                        "[AUDIT] {File} SHRANK from {Was} to {Now} bytes since this process last wrote to it — " +
                        "entries have been removed. Signing against the in-memory tail so the break stays visible.",
                        Path.GetFileName(current), _lastKnownSegmentLength, length);
                    return _lastSignature;
                }

                // Another writer may also have rotated to a newer segment, so drop the cached
                // segment path (DE-H4's optimisation) and rescan before reading the tail.
                _currentSegmentPath = null;
                var latest = GetCurrentLogFile();

                string? tailLine = null;
                if (File.Exists(latest))
                    foreach (var line in File.ReadLines(latest))
                        if (!string.IsNullOrWhiteSpace(line)) tailLine = line;

                // Empty or absent segment: keep the in-memory tail. A freshly rotated file's
                // first entry must declare the PREVIOUS segment's last signature as its
                // PreviousHash, which is exactly what cross-segment verification checks.
                if (tailLine == null) return _lastSignature;

                var diskSig = JsonSerializer.Deserialize<AuditLogEntry>(tailLine, SerializerOptions)?.Signature;
                if (string.IsNullOrEmpty(diskSig)) return _lastSignature;
                if (string.Equals(diskSig, _lastSignature, StringComparison.Ordinal)) return _lastSignature;

                // First flush of this session — the startup seed already read this file.
                if (_lastSignature.Length == 0) return diskSig;

                Serilog.Log.Information(
                    "[AUDIT] Chain tail in {File} advanced by another SQLTriage process — re-seeding and " +
                    "appending after it. This is concurrent access, NOT a tamper signal.",
                    Path.GetFileName(latest));
                return diskSig;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex,
                    "[AUDIT] Could not re-read the chain tail from disk — signing against the in-memory tail.");
                return _lastSignature;
            }
        }

        /// <summary>Records the segment state we just left behind, so the next flush can take the fast path.</summary>
        private void RememberSegmentState(string logFile)
        {
            try
            {
                var info = new FileInfo(logFile);
                _lastKnownSegmentPath = logFile;
                _lastKnownSegmentLength = info.Exists ? info.Length : 0;
            }
            catch
            {
                // Unknown length just forces the slow path next time — never fatal.
                _lastKnownSegmentPath = null;
                _lastKnownSegmentLength = -1;
            }
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
                int ordinal = 0;

                // Same run-shape classifier as VerifyChain: a rewritten KeyId must not buy the
                // benign verdict here either, or the startup banner exonerates a tampered entry.
                UnverifiableRun? openRun = null;
                string? lastKeyIdSeen = null;
                int maxSuccessionSeen = -1;

                // Same restart discriminator as VerifyChain, for the same reason: a resumption
                // point must not be reported as a failed integrity check, and a link that appears
                // nowhere in the scan must not be reported as a resumption point.
                var signaturesSeen = new HashSet<string>(StringComparer.Ordinal);

                void CloseStartupRun(string? followingKeyId)
                {
                    if (openRun == null) return;
                    var run = openRun;
                    openRun = null;

                    var verdict = ClassifyUnverifiableRun(run, followingKeyId, out var reason);
                    if (verdict == RunVerdict.Rewritten)
                    {
                        ChainBroken = true;
                        ChainBreakFirstRecordId ??= $"{run.FirstRecord}_keyid-rewritten";
                        Serilog.Log.Error(
                            "[AUDIT] Chain BROKEN — {Count} entrie(s) from {Record} in {File} claim signing key " +
                            "{KeyId}, which resolves to nothing AND cannot be genuine key loss: {Reason}. " +
                            "Treated as tampering.",
                            run.Count, run.FirstRecord, Path.GetFileName(latest), run.KeyId, reason);
                        return;
                    }

                    if (verdict == RunVerdict.Indeterminate)
                    {
                        // NOT the benign bucket, and NOT learned into the ledger — see B1(a).
                        ChainProvenanceIndeterminate = true;
                        ChainIndeterminateFirstRecordId ??= run.FirstRecord;
                        ChainIndeterminateKeyId ??= run.KeyId;
                        Serilog.Log.Error(
                            "[AUDIT] Chain INDETERMINATE — {Count} entrie(s) from {Record} in {File} name signing " +
                            "key {KeyId}, which resolves to nothing on this machine, and {Reason}. Their integrity " +
                            "can be neither confirmed nor denied AND this cannot be shown to be an ordinary key " +
                            "loss. Do not close it as key management without evidence.",
                            run.Count, run.FirstRecord, Path.GetFileName(latest), run.KeyId, reason);
                        return;
                    }

                    ChainUnverifiable = true;
                    ChainUnverifiableFirstRecordId ??= run.FirstRecord;
                    ChainUnverifiableKeyId ??= run.KeyId;
                    RememberObservedKeyId(run.KeyId);
                    maxSuccessionSeen = Math.Max(maxSuccessionSeen, SuccessionIndex(run.KeyId));
                }

                foreach (var line in File.ReadLines(latest))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var entry = JsonSerializer.Deserialize<AuditLogEntry>(line, SerializerOptions);
                    if (entry == null) continue;
                    ordinal++;

                    if (previousSig == null)
                    {
                        // First entry of file — its declared PreviousHash is our starting point.
                        previousSig = entry.PreviousHash ?? string.Empty;
                    }

                    // UNVERIFIABLE ≠ BROKEN. If the entry's signing key is not available we cannot
                    // form a verdict at all, so we must NOT recompute against the current key and
                    // call the inevitable mismatch a tamper. Bank the gap as part of a run, carry the
                    // entry's own signature forward (structural linkage is still checkable for later
                    // entries) and keep going — otherwise one benign first-entry gap hides the whole
                    // chain. The run's verdict is settled once its neighbours are known.
                    if (!TryResolveKeyForEntry(entry, out var entryKey))
                    {
                        if (openRun != null &&
                            !string.Equals(openRun.KeyId, entry.KeyId, StringComparison.Ordinal))
                            CloseStartupRun(entry.KeyId);

                        openRun ??= new UnverifiableRun
                        {
                            KeyId = entry.KeyId,
                            PrecedingKeyId = lastKeyIdSeen,
                            FirstOrdinal = ordinal - 1,
                            FirstRecord = $"{entry.Timestamp:o}_key:{entry.KeyId}",
                            MaxSuccessionBefore = maxSuccessionSeen
                        };
                        openRun.Count++;
                        lastKeyIdSeen = entry.KeyId;
                        if (entry.Signature != null) signaturesSeen.Add(entry.Signature);
                        previousSig = entry.Signature;
                        continue;
                    }

                    var resolvedKeyId = NeighbourKeyId(entry);
                    CloseStartupRun(resolvedKeyId);
                    lastKeyIdSeen = resolvedKeyId;
                    maxSuccessionSeen = Math.Max(maxSuccessionSeen, SuccessionIndex(resolvedKeyId));

                    var recomputed = ComputeSignature(entry, previousSig, entryKey);
                    if (!string.Equals(recomputed, entry.Signature, StringComparison.Ordinal))
                    {
                        // Before calling it a break: does the entry verify against the link IT
                        // declares, and is that link one this scan has actually read? Then the chain
                        // RESUMED here and this entry is sound (the declared link is inside the
                        // signed canonical form, so it cannot have been moved without the key).
                        // Advancing past it is deliberate: the accident that produced the live
                        // restarts was a verify loop that returned WITHOUT advancing the running
                        // signature, so the next entry written chained onto a stale one and made a
                        // fresh restart every launch.
                        // The third condition is the REPLAY fence, added 2026-08-11: a signature this
                        // scan has already read means the same signed record is on disk twice, and a
                        // copy of a genuine entry passes both tests above wherever it is pasted
                        // (its declared link is by definition one this scan read). A duplicate is an
                        // edit to the log, so it must not buy the benign restart arm here either.
                        var declaredPrev = entry.PreviousHash ?? string.Empty;
                        if (string.Equals(ComputeSignature(entry, declaredPrev, entryKey), entry.Signature,
                                          StringComparison.Ordinal) &&
                            (declaredPrev.Length == 0 || signaturesSeen.Contains(declaredPrev)) &&
                            !(!string.IsNullOrEmpty(entry.Signature) && signaturesSeen.Contains(entry.Signature!)))
                        {
                            ChainRestarted = true;
                            ChainRestartFirstRecordId ??= entry.Timestamp.ToString("o");
                            Serilog.Log.Warning(
                                "[AUDIT] Chain RESTART at {Timestamp} in {File}: this entry declares a different " +
                                "previous-hash link and its own signature verifies against it. The entries on both " +
                                "sides of the gap are sound; the link across the gap is what is lost. Not a tamper signal.",
                                entry.Timestamp, Path.GetFileName(latest));
                            signaturesSeen.Add(entry.Signature!);
                            previousSig = entry.Signature;
                            continue;
                        }

                        ChainBroken = true;
                        // ISO-8601 round-trip, like every other record id this class hands an
                        // operator (see the run FirstRecord construction below). Until 2026-08-02
                        // this one interpolated the DateTime with the CURRENT CULTURE's default
                        // format, so the audit page printed the first failing record as
                        // "1/08/2026 1:51:44 pm_BVAGWnBUVG+KBAa1" — the one identifier an operator
                        // quotes to an auditor, rendered ambiguous (d/M vs M/d) and locale-dependent
                        // on a page whose every other id is round-trip ISO. Changing the format
                        // changes the .chain-break-marker's contents, so a marker written by an
                        // older build compares unequal once and the break is logged one extra time;
                        // that is the whole consequence, and it is preferable to the ambiguity.
                        var breakId = $"{entry.Timestamp:o}_{recomputed[..16]}";
                        ChainBreakFirstRecordId ??= breakId;

                        // Only log the first time this specific break is detected
                        if (previousBreak != breakId)
                        {
                            // Error, not Warning: a signature that does not recompute is the
                            // tamper-evidence signal this whole chain exists to raise.
                            Serilog.Log.Error("[AUDIT] Chain BROKEN — signature mismatch at {Timestamp} in {File}. " +
                                "Entries from this point on cannot be shown to have gone unmodified. (Logged once per break.)",
                                entry.Timestamp, Path.GetFileName(latest));
                            try { File.WriteAllText(breakMarkerPath, breakId); } catch { }
                        }
                        break;
                    }
                    if (entry.Signature != null) signaturesSeen.Add(entry.Signature);
                    previousSig = entry.Signature;
                }

                // Segment end (or an early break on a mismatch): nothing follows the last run.
                CloseStartupRun(null);
                // The id the entries on disk DECLARED, kept for the key-replacement record. On a
                // launch that just replaced an unreadable key, no entry has yet been written under
                // the new key, so this is the outgoing key's id as the chain names it.
                _lastKeyIdOnChainAtStartup = lastKeyIdSeen;

                _lastSignature = previousSig ?? string.Empty;

                if (ChainProvenanceIndeterminate)
                {
                    // This event is also the ON-CHAIN record that the key-provenance ledger was
                    // adopted over a chain whose earlier key history was never observed: it is
                    // written in the launch where the ledger was missing, BEFORE the first flush
                    // establishes one, and it names the run the new ledger will not be able to
                    // speak to. An auditor reading the chain can therefore see exactly which
                    // entries predate the evidence, and no later launch silently upgrades their
                    // verdict — the forged/lost key id is never learned (B1(a)), so from the next
                    // launch on it is judged on R1's three traces like any other.
                    var indeterminateMarkerPath = Path.Combine(_logDirectory, ".chain-indeterminate-marker");
                    var previousIndeterminate = File.Exists(indeterminateMarkerPath)
                        ? File.ReadAllText(indeterminateMarkerPath).Trim()
                        : null;
                    var indeterminateMarker = ChainIndeterminateKeyId ?? "unknown";

                    if (previousIndeterminate != indeterminateMarker)
                    {
                        try { File.WriteAllText(indeterminateMarkerPath, indeterminateMarker); } catch { }

                        Enqueue(new AuditLogEntry
                        {
                            EventType = AuditEventType.AuditChainIndeterminate,
                            // Error, deliberately, and the choice is load-bearing. Warning is what a
                            // KNOWN-benign key gap gets, and this is not known to be benign; Critical
                            // is the tamper signal, and this is not known to be tampering either.
                            // Filing it as Warning would let an operator close it as routine key
                            // management, which is the exact false comfort this round exists to kill.
                            Severity = AuditSeverity.Error,
                            Message = $"Audit chain INDETERMINATE: entries naming key {indeterminateMarker} cannot be " +
                                      "checked, and no key-provenance evidence is available to show whether that key " +
                                      "id is one this installation ever held. Whether those entries were altered can " +
                                      "be neither confirmed nor ruled out.",
                            Details = new Dictionary<string, string>
                            {
                                ["UnresolvableKeyId"] = indeterminateMarker,
                                ["FirstRecord"] = ChainIndeterminateFirstRecordId ?? string.Empty,
                                ["Segment"] = Path.GetFileName(latest),
                                ["ProvenanceLedger"] = "unavailable",
                                ["DetectedBy"] = "startup-verification"
                            }
                        });
                    }
                }

                if (ChainUnverifiable)
                {
                    // Lower severity than a tamper, but NOT silent: it is still a real gap in the
                    // evidence. Logged (and audited) once per distinct unresolvable key.
                    var unverifiableMarkerPath = Path.Combine(_logDirectory, ".chain-unverifiable-marker");
                    var previousUnverifiable = File.Exists(unverifiableMarkerPath)
                        ? File.ReadAllText(unverifiableMarkerPath).Trim()
                        : null;
                    var marker = ChainUnverifiableKeyId ?? "unknown";

                    if (previousUnverifiable != marker)
                    {
                        Serilog.Log.Warning(
                            "[AUDIT] Chain UNVERIFIABLE — entries in {File} are signed with key {KeyId}, which is not " +
                            "available on this machine (first: {Record}). Their integrity can be neither confirmed nor " +
                            "denied. This is NOT a tamper signal: the usual cause is that the signing key was replaced " +
                            "(see any preceding HMAC KEY REPLACED error). Later entries signed with an available key " +
                            "are still verified normally. (Logged once per unresolvable key.)",
                            Path.GetFileName(latest), marker, ChainUnverifiableFirstRecordId);
                        try { File.WriteAllText(unverifiableMarkerPath, marker); } catch { }

                        Enqueue(new AuditLogEntry
                        {
                            EventType = AuditEventType.AuditChainUnverifiable,
                            Severity = AuditSeverity.Warning,
                            Message = $"Audit chain UNVERIFIABLE: entries signed with key {marker} cannot be checked — " +
                                      "the signing key is not available on this machine. Not a tamper signal.",
                            Details = new Dictionary<string, string>
                            {
                                ["UnresolvableKeyId"] = marker,
                                ["FirstRecord"] = ChainUnverifiableFirstRecordId ?? string.Empty,
                                ["Segment"] = Path.GetFileName(latest),
                                ["DetectedBy"] = "startup-verification"
                            }
                        });
                    }
                }

                // H5: reconcile the on-disk tail against the out-of-band anchor to catch truncation
                // (deleted trailing lines / removed newest segment) that in-band verification alone
                // cannot see, because a truncated-but-otherwise-valid prefix re-verifies clean.
                CheckTruncationAnchor();
            }
            catch (Exception ex)
            {
                // Error: startup verification not completing means the chain state is UNKNOWN,
                // which for a compliance deliverable is not a warning-level outcome.
                Serilog.Log.Error(ex, "[AUDIT] Startup chain verification failed — chain state unknown");
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
        /// The correlation-id detail key. ONE operator decision can authorise several applies (a
        /// batch); before this key existed the ledger recorded them as N unrelated
        /// Proposed/Approved/Applied triples with nothing joining them but adjacency in the chain
        /// and a shared approver — an auditor could not ask "show me everything that one approval
        /// authorised" (proved live 2026-09-01, spike S1 §3.5). The runner threads one run-id
        /// through every entry of a run, so the join is a field rather than an inference.
        ///
        /// <para>SAFE TO ADD, and the reason matters. <c>Details</c> is serialized INSIDE both the
        /// v1 and v2 signed canonical forms (<see cref="ComputeSignature(AuditLogEntry, string, byte[])"/>),
        /// signatures are computed at write time, and stored entries are never retrofitted — so a
        /// new key on NEW entries is signed like any other content and verifies exactly as before,
        /// while entries written before this key existed keep verifying without it. The one rule
        /// that makes that true: NEVER retro-edit a stored entry to add this key. Doing so
        /// re-canonicalises a signed record and breaks the chain at that entry (exercised by
        /// AuditLogCorrelationIdTests.MutatingAStoredRunId_BreaksThatEntry...).</para>
        /// </summary>
        public const string CorrelationDetailKey = "RunId";

        // Adds the run-id to a remediation entry's details when one was supplied. Omitted entirely
        // when null or blank, so an ordinary single apply writes byte-identical details to what it
        // always did — which is what keeps every pre-existing entry and caller honest. A blank id
        // is treated as no id: an empty RunId on every entry would make the whole ledger look like
        // one enormous batch.
        private static void AddCorrelation(Dictionary<string, string> details, string? correlationId)
        {
            if (!string.IsNullOrWhiteSpace(correlationId))
                details[CorrelationDetailKey] = correlationId!;
        }

        /// <summary>
        /// Logs that a registered remediation template was proposed for a server
        /// (gate 4 entry). Nothing has been applied at this point.
        /// </summary>
        public void LogRemediationProposed(string templateKey, string serverName, string? details = null,
            string? correlationId = null)
        {
            var d = new Dictionary<string, string>
            {
                ["TemplateKey"] = templateKey,
                ["ServerName"] = serverName,
                ["Details"] = details ?? string.Empty
            };
            AddCorrelation(d, correlationId);
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.RemediationProposed,
                Severity = AuditSeverity.Info,
                Message = $"Remediation '{templateKey}' proposed for '{serverName}'",
                Details = d
            });
        }

        /// <summary>
        /// Logs that a human explicitly approved a proposed remediation (gate 4
        /// cleared). Records who approved so the trail attributes the decision.
        /// </summary>
        public void LogRemediationApproved(string templateKey, string serverName, string approvedBy, string? details = null,
            string? correlationId = null)
        {
            var d = new Dictionary<string, string>
            {
                ["TemplateKey"] = templateKey,
                ["ServerName"] = serverName,
                ["ApprovedBy"] = approvedBy,
                ["Details"] = details ?? string.Empty
            };
            AddCorrelation(d, correlationId);
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.RemediationApproved,
                Severity = AuditSeverity.Info,
                Message = $"Remediation '{templateKey}' for '{serverName}' approved by '{approvedBy}'",
                Details = d
            });
        }

        /// <summary>
        /// Logs the outcome of an approved remediation's execution (gate 5). The
        /// <paramref name="outcome"/> is a DISTINCT TERMINAL STATE — never a bare
        /// "success" boolean — so a fix that did not apply can never read as applied:
        /// AppliedVerified / AppliedVerifyFailed / CouldNotRun / NoOp. Severity is
        /// derived from the outcome so the ledger flags the bad terminal states.
        /// </summary>
        public void LogRemediationApplied(string templateKey, string serverName, string outcome, string? errorMessage = null, string? preChangeValue = null,
            string? correlationId = null)
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

            var d = new Dictionary<string, string>
            {
                ["TemplateKey"] = templateKey,
                ["ServerName"] = serverName,
                ["Outcome"] = outcome,
                ["Error"] = errorMessage ?? string.Empty,
                // The pre-change value rides the HMAC-chained ledger so a cross-session
                // revert can re-apply it (undo-ability is therefore bounded by audit
                // retention). Empty when the apply captured no recoverable prior value.
                ["PreChangeValue"] = preChangeValue ?? string.Empty
            };
            AddCorrelation(d, correlationId);
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.RemediationApplied,
                Severity = severity,
                Message = $"Remediation '{templateKey}' on '{serverName}' — outcome: {outcome}"
                    + (string.IsNullOrEmpty(errorMessage) ? string.Empty : $" ({errorMessage})"),
                Details = d
            });
        }

        /// <summary>
        /// S1: logs that an applied remediation's real-world effect needs a FOLLOW-UP
        /// verification by a deadline ("resolution applied — verifying by &lt;date&gt;").
        /// Written only after an AppliedVerified apply of a template declaring
        /// <c>DeferredVerify</c>. Carries the apply's parameters (JSON) so the check can be
        /// re-rendered later from the registered template — never from stored SQL.
        /// </summary>
        public void LogRemediationVerifyScheduled(
            string templateKey, string serverName, DateTime verifyByUtc, string description, string parametersJson)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.RemediationVerifyScheduled,
                Severity = AuditSeverity.Info,
                Message = $"Remediation '{templateKey}' on '{serverName}' — deferred verification scheduled (verify by {verifyByUtc:yyyy-MM-dd})",
                Details = new Dictionary<string, string>
                {
                    ["TemplateKey"] = templateKey,
                    ["ServerName"] = serverName,
                    ["VerifyByUtc"] = verifyByUtc.ToString("o"),
                    ["Description"] = description,
                    ["ParametersJson"] = parametersJson ?? string.Empty
                }
            });
        }

        /// <summary>
        /// S1: logs the terminal resolution of a scheduled deferred verification. Passed is
        /// Info; not-passed is Error — an unconfirmed effect must surface in the ledger, never
        /// read as benign. Pairs with the RemediationVerifyScheduled entry via
        /// (TemplateKey, ServerName, ParametersJson) at-or-after the schedule's timestamp.
        /// </summary>
        public void LogRemediationVerifyResolved(
            string templateKey, string serverName, string parametersJson, bool passed, string? message = null)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.RemediationVerifyResolved,
                Severity = passed ? AuditSeverity.Info : AuditSeverity.Error,
                Message = $"Remediation '{templateKey}' on '{serverName}' — deferred verification {(passed ? "PASSED" : "FAILED")}"
                    + (string.IsNullOrEmpty(message) ? string.Empty : $" ({message})"),
                Details = new Dictionary<string, string>
                {
                    ["TemplateKey"] = templateKey,
                    ["ServerName"] = serverName,
                    ["ParametersJson"] = parametersJson ?? string.Empty,
                    ["Passed"] = passed ? "true" : "false",
                    ["Details"] = message ?? string.Empty
                }
            });
        }

        // ── App-issued DDL journaling (fresh-eyes F-A / ruling R1, 2026-09-01) ──────────────
        //
        // Three surfaces execute DDL against a monitored server and, until this lane, journaled
        // NOTHING: the plan viewer's index-create, the dashboard action cell's double-hop, and the
        // XEvent session lifecycle. Remediation was already audited; these three were the gap.
        //
        // R1 ruled BOTH registers for every app-issued DDL: this tamper-evident chain AND a Change
        // Ledger row. The two answer different questions. The chain answers "what did this
        // installation send to that server, and has the record been altered?" — HMAC-chained,
        // append-only, exportable as evidence. The ledger answers "what changed on my estate, and
        // who owns it?" — queryable, editable status, the operator's working view. Neither is a
        // substitute for the other, so a surface that writes only one is not journaled.
        //
        // THE ORDERING RULE. Attempted is written and FLUSHED before the statement is sent. A
        // statement must never execute unjournaled, and enqueue-only is not journaled: the buffer
        // is in-process, so a crash mid-DDL would lose exactly the record that mattered. Completed
        // follows on BOTH the success and the failure path — a DDL that threw is journaled as
        // failed, never as absent, because "no record" and "no attempt" must not look alike.
        //
        // THE STATEMENT IS STORED IN FULL. No truncation anywhere on this path. A 120-character
        // prefix of a CREATE INDEX tells an auditor which table and nothing about what was built;
        // for the double-hop, whose text the app did not author, a prefix hides the entire payload.
        // Details is serialized inside the signed canonical form, so the stored statement is
        // covered by the same signature as the rest of the entry.

        /// <summary>
        /// Canonical terminal-state strings for <see cref="LogDdlCompleted"/>. Distinct states, not
        /// a success boolean, for the same reason <see cref="RemediationOutcomes"/> is: a statement
        /// that never ran must not read the same as one that ran and failed. Any value outside this
        /// set is logged at <see cref="AuditSeverity.Warning"/> — an unrecognised outcome is treated
        /// as suspect, never as benign.
        /// </summary>
        public static class DdlOutcomes
        {
            /// <summary>The statement executed and the server returned no error.</summary>
            public const string Succeeded = "Succeeded";
            /// <summary>The statement was sent and the server or the driver returned an error.</summary>
            public const string Failed = "Failed";
            /// <summary>The operator cancelled the statement after it was sent. Server-side effect unknown.</summary>
            public const string Cancelled = "Cancelled";
        }

        /// <summary>
        /// Stable surface identifiers for the DDL journal. Fixed strings rather than caller-typed
        /// prose so an auditor can filter "every index the plan viewer created" without matching on
        /// wording that drifts. They are also the Change Ledger check-ids for the same rows.
        /// </summary>
        public static class DdlSurfaces
        {
            /// <summary>Query plan viewer, No-Pants index-create (QueryPlanModal.ExecuteSingleIndex).</summary>
            public const string IndexCreate = "ddl.index-create";
            /// <summary>Dashboard action cell, including the "query" double-hop (DynamicDashboard.ExecuteActionSqlAsync).</summary>
            public const string DashboardAction = "ddl.dashboard-action";
            /// <summary>Extended Events session lifecycle (XEventService create/start/stop/drop/startup-state).</summary>
            public const string XEventLifecycle = "ddl.xevent-lifecycle";
        }

        /// <summary>
        /// Records that the app is ABOUT to send a DDL statement to a monitored server. Written
        /// and flushed before the statement is sent, so no statement can execute unjournaled.
        /// <paramref name="statement"/> is stored in full — never truncated.
        /// </summary>
        /// <param name="surface">One of <see cref="DdlSurfaces"/> — which surface issued it.</param>
        /// <param name="operation">The act in the surface's own vocabulary ("create", "start", "drop").</param>
        /// <param name="serverName">The instance the statement is being sent to.</param>
        /// <param name="statement">The exact text that will be executed.</param>
        public void LogDdlAttempted(string surface, string operation, string serverName, string statement,
            string? details = null)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.DdlAttempted,
                Severity = AuditSeverity.Info,
                Message = $"DDL '{operation}' from {surface} about to run on '{serverName}'",
                Details = new Dictionary<string, string>
                {
                    ["Surface"] = surface,
                    ["Operation"] = operation,
                    ["ServerName"] = serverName,
                    ["Statement"] = statement ?? string.Empty,
                    ["Details"] = details ?? string.Empty
                }
            });
        }

        /// <summary>
        /// Records the terminal state of a DDL statement this app sent. Called on the success path
        /// AND on every failure path, so a statement that threw is journaled as failed rather than
        /// leaving an Attempted entry with no successor. Severity is derived from the outcome.
        /// </summary>
        public void LogDdlCompleted(string surface, string operation, string serverName, string statement,
            string outcome, string? errorMessage = null)
        {
            var severity = outcome switch
            {
                DdlOutcomes.Succeeded => AuditSeverity.Info,
                DdlOutcomes.Failed => AuditSeverity.Error,
                DdlOutcomes.Cancelled => AuditSeverity.Warning,
                _ => AuditSeverity.Warning
            };

            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.DdlCompleted,
                Severity = severity,
                Message = $"DDL '{operation}' from {surface} on '{serverName}': outcome {outcome}"
                    + (string.IsNullOrEmpty(errorMessage) ? string.Empty : $" ({errorMessage})"),
                Details = new Dictionary<string, string>
                {
                    ["Surface"] = surface,
                    ["Operation"] = operation,
                    ["ServerName"] = serverName,
                    ["Statement"] = statement ?? string.Empty,
                    ["Outcome"] = outcome,
                    ["Error"] = errorMessage ?? string.Empty
                }
            });
        }

        /// <summary>
        /// Records DDL that was REFUSED before reaching the server — a guard verdict, a permission
        /// check, or a rejected shape. A refusal is evidence in its own right: it is the only trace
        /// that something tried, so it is journaled at <see cref="AuditSeverity.Warning"/> and the
        /// refused text is kept in full.
        /// </summary>
        public void LogDdlBlocked(string surface, string operation, string serverName, string statement, string reason)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.DdlBlocked,
                Severity = AuditSeverity.Warning,
                Message = $"DDL '{operation}' from {surface} on '{serverName}' was blocked: {reason}",
                Details = new Dictionary<string, string>
                {
                    ["Surface"] = surface,
                    ["Operation"] = operation,
                    ["ServerName"] = serverName,
                    ["Statement"] = statement ?? string.Empty,
                    ["Reason"] = reason ?? string.Empty
                }
            });
        }

        /// <summary>
        /// Records one key-file pickup outcome (board #19). Called once per KEY FILE the pass acted
        /// on, not once per pass, so two problem pairs dropped together leave two rows.
        ///
        /// <para><b>Redacted by construction.</b> The parameters are the customer name and the two
        /// file names — values the operator can already read off their own install folder — plus the
        /// outcome words. There is no parameter that can carry key material, so no caller can put a
        /// phrase in here by accident.</para>
        ///
        /// <para>A pickup that activated is <see cref="AuditSeverity.Info"/>; every other outcome is
        /// <see cref="AuditSeverity.Warning"/>, because a credential that was dropped and not
        /// consumed is still sitting in the install folder.</para>
        /// </summary>
        public void LogLicenseKeyFilePickup(
            bool activated,
            string outcomeKind,
            string? bundleFileName,
            string? keyFileName,
            string? customerName,
            string consumeState,
            string? pairPosition = null)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = activated
                    ? AuditEventType.LicenseActivatedFromKeyFile
                    : AuditEventType.LicenseKeyFileRejected,
                Severity = activated ? AuditSeverity.Info : AuditSeverity.Warning,
                Message = activated
                    ? $"Full licence activated from key file '{keyFileName}' for '{customerName}' ({consumeState})."
                    : $"Key file '{keyFileName}' was not activated from: {outcomeKind}.",
                Details = new Dictionary<string, string>
                {
                    ["Outcome"] = outcomeKind ?? string.Empty,
                    ["BundleFileName"] = bundleFileName ?? string.Empty,
                    ["KeyFileName"] = keyFileName ?? string.Empty,
                    ["CustomerName"] = customerName ?? string.Empty,
                    ["KeyFileState"] = consumeState ?? string.Empty,
                    // "<n> of <total>" when this row is one pair inside a multi-pair pass, empty
                    // when it is the pass's own row. A pass with two problem key files writes two
                    // rows; without this an auditor could not tell them apart from two passes.
                    ["PairPosition"] = pairPosition ?? string.Empty
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
        ///
        /// <para><paramref name="success"/> is NULLABLE, and that is the point. The executor can
        /// finish the inverse action and then fail to READ the server back, which is neither a
        /// success nor a failure. This entry used to carry only true/false, so every unconfirmed
        /// rollback was ledgered as an unqualified "rolled back", Success=True, Error="" — a
        /// compliance attestation about a post-state nobody had observed. Null now writes its own
        /// state: Warning severity, "NOT CONFIRMED" in the message, RollbackState=Unconfirmed in
        /// the details, and Success=False (an unconfirmed rollback is never a success).</para>
        /// </summary>
        public void LogRemediationRolledBack(string templateKey, string serverName, bool? success, string? errorMessage = null,
            string? correlationId = null)
        {
            var state = success switch
            {
                true => "Confirmed",
                false => "Failed",
                null => "Unconfirmed",
            };
            var severity = success switch
            {
                true => AuditSeverity.Info,
                false => AuditSeverity.Error,
                null => AuditSeverity.Warning,
            };
            var message = success switch
            {
                true => $"Remediation '{templateKey}' on '{serverName}' rolled back",
                false => $"Rollback of remediation '{templateKey}' on '{serverName}' FAILED: {errorMessage}",
                null => $"Rollback of remediation '{templateKey}' on '{serverName}' ran; the result is NOT CONFIRMED: {errorMessage}",
            };
            var d = new Dictionary<string, string>
            {
                ["TemplateKey"] = templateKey,
                ["ServerName"] = serverName,
                ["Success"] = (success == true).ToString(),
                ["RollbackState"] = state,
                ["Error"] = errorMessage ?? string.Empty
            };
            AddCorrelation(d, correlationId);
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.RemediationRolledBack,
                Severity = severity,
                Message = message,
                Details = d
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
                // Held across the whole read-tail/sign/append window so a concurrent SQLTriage
                // process cannot interleave. False = lock unavailable or timed out; we still write.
                bool appendLock = AcquireAppendLock();
                try
                {
                    // Reconcile with anything another process appended since our last write,
                    // BEFORE choosing the segment — the sync may rescan to a newer segment.
                    var pendingSig = SyncChainTailFromDisk();
                    var logFile = GetCurrentLogFile();
                    MaybeRotate(ref logFile);

                    var sb = new StringBuilder();
                    // Sign into a LOCAL cursor and commit _lastSignature only AFTER the durable
                    // write succeeds. A failed flush (dir gone, disk full) must NOT advance the
                    // chain pointer — otherwise the requeued batch re-signs against a tail that was
                    // never persisted, and the recovered chain fails verification (H6, 2026-07-07).
                    foreach (var entry in entries)
                    {
                        entry.KeyId = _currentKeyId;   // H4: tag with the signing key's id
                        entry.PreviousHash = pendingSig;
                        entry.Signature = ComputeSignature(entry, pendingSig);
                        pendingSig = entry.Signature;
                        sb.AppendLine(JsonSerializer.Serialize(entry, SerializerOptions));
                    }
                    File.AppendAllText(logFile, sb.ToString());
                    _lastSignature = pendingSig;   // commit the chain tail on success only
                    RememberSegmentState(logFile); // so the next flush can skip the tail re-read
                    _consecutiveFlushFailures = 0;
                    // H5: record the chain tail out-of-band so trailing-entry / whole-segment
                    // truncation is detectable at next startup (see CheckTruncationAnchor).
                    WriteTruncationAnchor(_lastSignature, entries[^1].Timestamp);

                    // Mirror Critical/Error AFTER the durable write, so a failed-then-retried
                    // batch doesn't emit duplicate mirror entries.
                    foreach (var entry in entries)
                    {
                        if (entry.Severity != AuditSeverity.Critical && entry.Severity != AuditSeverity.Error)
                            continue;

                        if (_eventLogAvailable)
                            WriteEventLog($"[{entry.EventType}] {entry.Message}", entry.Severity);

                        // Also mirror Critical into Serilog. Registering the Event Log source
                        // needs admin rights, so on an ordinary desktop install _eventLogAvailable
                        // is false and a security block or chain break otherwise appeared ONLY
                        // inside audit-*.jsonl — not in the log an operator actually opens.
                        if (entry.Severity == AuditSeverity.Critical)
                            Serilog.Log.Error("[AUDIT] CRITICAL {EventType}: {Message}",
                                entry.EventType, entry.Message);
                    }
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
                finally
                {
                    ReleaseAppendLock(appendLock);
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

                // H6 (2026-07-07): the failover file is an INDEPENDENT side-chain, not part of the
                // main chain. The originals stay requeued and, on recovery, flush into the main
                // chain intact. So we must NOT advance _lastSignature (that pointed the main chain
                // at a signature living only in a failover file → permanent break), and we must NOT
                // mutate the requeued originals (EventType overwrite corrupted them on recovery).
                // Sign CLONES against a LOCAL side-chain cursor seeded from the main tail.
                var sb = new StringBuilder();
                var sideSig = _lastSignature;
                foreach (var e in entries)
                {
                    var clone = CloneForFailover(e);
                    clone.KeyId = _currentKeyId;
                    clone.PreviousHash = sideSig;
                    clone.Signature = ComputeSignature(clone, sideSig);
                    sideSig = clone.Signature;
                    sb.AppendLine(JsonSerializer.Serialize(clone, SerializerOptions));
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
                    // H6: transition marker is part of the failover side-chain — chain off the
                    // local sideSig, do NOT advance the main _lastSignature.
                    fe.KeyId = _currentKeyId;
                    fe.PreviousHash = sideSig;
                    fe.Signature = ComputeSignature(fe, sideSig);
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
            // SIGNING path (never verification). Stamp the canonical-form version onto the entry
            // BEFORE hashing, so every entry this build writes is v2 and carries the discriminator
            // that tells a future verifier which form to recompute. Every caller sets entry.KeyId
            // immediately before this, which is what makes binding KeyId into v2 safe here.
            entry.SigVersion = CurrentSignatureVersion;
            // Snapshot the reference so one signature always uses one consistent key,
            // even if RotateHmacKey atomically swaps _hmacKey on another thread.
            return ComputeSignature(entry, previousSig, _hmacKey);
        }

        /// <summary>
        /// The canonical form this build SIGNS with. Verification still honours v1 for every entry
        /// that declares it (or declares nothing), so existing history is untouched.
        /// </summary>
        internal const int CurrentSignatureVersion = 2;

        /// <summary>
        /// H4: sign/verify with an EXPLICIT key. Signing paths pass the current key; verification
        /// passes the key resolved from the entry's KeyId (so rotated-away keys still validate).
        /// <para>
        /// TWO canonical forms, selected by <see cref="AuditLogEntry.SigVersion"/>:
        /// </para>
        /// <list type="bullet">
        /// <item><b>v1</b> (SigVersion null/absent — every entry written before 2026-08-01):
        /// Timestamp, EventType, Severity, Message, Details, User, Machine, PreviousHash.
        /// BYTE-FOR-BYTE FROZEN. Changing it invalidates the entire existing chain on every
        /// installation, so it is pinned by
        /// <c>LegacyEntriesWithoutSigVersion_StillVerifyUnderTheOriginalCanonicalForm</c>, which
        /// rebuilds the form independently in the test rather than calling this method.</item>
        /// <item><b>v2</b> (SigVersion 2): the v1 fields plus KeyId and SigVersion. KeyId stops being
        /// a free-to-edit unsigned field, and including SigVersion itself makes a downgrade to v1
        /// (or a forged upgrade of a legacy entry) produce a mismatch instead of a silent
        /// reinterpretation.</item>
        /// </list>
        /// Entries are NEVER retrofitted from v1 to v2 — the version travels with the entry.
        /// </summary>
        private static string ComputeSignature(AuditLogEntry entry, string previousSig, byte[] key)
        {
            object canonical = entry.SigVersion is null or < CurrentSignatureVersion
                ? new
                {
                    entry.Timestamp,
                    entry.EventType,
                    entry.Severity,
                    entry.Message,
                    entry.Details,
                    entry.User,
                    entry.Machine,
                    entry.PreviousHash
                }
                : new
                {
                    entry.Timestamp,
                    entry.EventType,
                    entry.Severity,
                    entry.Message,
                    entry.Details,
                    entry.User,
                    entry.Machine,
                    entry.PreviousHash,
                    entry.KeyId,
                    entry.SigVersion
                };
            var payload = JsonSerializer.Serialize(canonical, SerializerOptions);
            var input = Encoding.UTF8.GetBytes(previousSig + "|" + payload);
            return Convert.ToBase64String(HMACSHA256.HashData(key, input));
        }

        // ── H4 key-id / archival helpers ─────────────────────────────────────────

        /// <summary>Stable 16-hex id for a key (first 8 bytes of SHA-256). No secret material leaks.</summary>
        private static string ComputeKeyId(byte[] key) =>
            Convert.ToHexString(SHA256.HashData(key))[..16];

        /// <summary>
        /// B3 (2026-08-01 round 3): archive the CURRENT key, and upgrade its wrapping if the file
        /// is already there under the legacy CurrentUser scope.
        /// <para>
        /// <see cref="ArchiveKeyIfAbsent"/> skips files that exist, and current-key resolution never
        /// reads the archive (a null or current KeyId short-circuits to <c>_hmacKey</c>), so on a
        /// legacy store the current key's own archive stayed CurrentUser-wrapped forever while the
        /// live key file was upgraded on its read path. The claim that "the key, both archives and
        /// the anchor all wrap LocalMachine now" was therefore over-broad, and the archive would
        /// have gone dark at the next identity change — turning every entry it covers unverifiable
        /// for exactly the reason this round of work exists to prevent.
        /// </para>
        /// <para>
        /// This is a genuine re-wrap, not a read-path upgrade: we hold the current key's material,
        /// so the file is rewritten from that rather than from whatever can be unwrapped. That
        /// matters — it works even if the existing blob is unreadable under this identity, which is
        /// precisely the state the read path cannot rescue. Archives of PRIOR keys keep the
        /// read-path upgrade in <see cref="TryReadKeyFile"/>; nothing here can help them, because
        /// their material only exists inside the blob.
        /// </para>
        /// </summary>
        private void EnsureCurrentKeyArchived(string keyId, byte[] rawKey)
        {
            var archivePath = _keyPath + "." + keyId;
            if (!File.Exists(archivePath))
            {
                ArchiveKeyIfAbsent(keyId, rawKey);
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    if (!IsLocalMachineWrapped(File.ReadAllBytes(archivePath)))
                    {
                        WriteWrappedHmacKey(archivePath, rawKey);
                        Serilog.Log.Information(
                            "[AUDIT] Re-wrapped the current audit key's archive ({File}) from DPAPI CurrentUser to " +
                            "LocalMachine scope (same key, unchanged chain).", Path.GetFileName(archivePath));
                    }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex,
                        "[AUDIT] Could not re-wrap the current audit key's archive {File} under LocalMachine scope — " +
                        "it stays readable only by the identity that wrote it.", Path.GetFileName(archivePath));
                }
            }

            lock (_keyCacheLock) { _keyCache[keyId] = rawKey; }
        }

        /// <summary>Archive a key beside the main key file as "&lt;keyPath&gt;.&lt;keyId&gt;" if absent.</summary>
        private void ArchiveKeyIfAbsent(string keyId, byte[] rawKey)
        {
            try
            {
                var archivePath = _keyPath + "." + keyId;
                if (!File.Exists(archivePath))
                    WriteWrappedHmacKey(archivePath, rawKey);
                lock (_keyCacheLock) { _keyCache[keyId] = rawKey; }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[AUDIT] Could not archive HMAC key {KeyId}", keyId);
            }
        }

        /// <summary>Reads a DPAPI-wrapped (or raw, non-Windows) key file. Returns null on failure.</summary>
        /// <remarks>
        /// ⚠ KNOWN, PRE-EXISTING, UNCLOSED — the FULL-FORGERY PATH. Named here deliberately so that
        /// nobody reads the archive-shape hardening in <see cref="ArchivedKeyFileIsKeyShaped"/> as a
        /// fix for it. Reproduced on the 791d2cb binary, i.e. this is older than the 2026-08-01
        /// audit-honesty work and is not a regression from it.
        /// <para>
        /// The <c>blob.Length == 32</c> fallback below accepts a RAW 32-byte file as key material,
        /// and <see cref="TryResolveKeyForEntry"/> resolves the file to read from the entry's own
        /// (attacker-writable, on v1 entries) <c>KeyId</c>. So an attacker with write access to the
        /// audit-log directory can: plant 32 arbitrary bytes as <c>hmac.key.&lt;id&gt;</c>, rewrite an
        /// entry, re-sign it in v1 canonical form under those bytes, name that id in its KeyId, and
        /// mint a matching <c>.chain-anchor</c> (see <see cref="ChainAnchor"/> — the DPAPI entropy is
        /// a plaintext literal in the shipped assembly and the wrap is LocalMachine, so any local
        /// process can produce a valid one). Verification then reports <c>INTACT</c> — "All N entries
        /// verified against their signing keys" — with no alarm, on that launch and every later one.
        /// </para>
        /// <para>
        /// Closing it means changing what the chain is (an out-of-band or asymmetric root of trust —
        /// remote attestation, an HSM/KMS-held key, or a signed off-box mirror). That is Adrian's
        /// architectural call, not a code fix, so it is recorded rather than patched:
        /// <c>docs/compliance/incident-response-runbook.md</c> § What this chain does and does not
        /// guarantee.
        /// </para>
        /// </remarks>
        private static byte[]? TryReadKeyFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var blob = File.ReadAllBytes(path);
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        var raw = TryUnwrapHmacKey(blob, out var wasLegacyScope);
                        // Archived keys get the same silent LocalMachine upgrade as the main key —
                        // otherwise the archive of a key we CAN still read today goes dark the next
                        // time the service identity changes, and its entries turn unverifiable.
                        if (wasLegacyScope)
                        {
                            try { WriteWrappedHmacKey(path, raw); } catch { /* best effort */ }
                        }
                        return raw;
                    }
                    // ⚠ the full-forgery path's entry point — see the remarks above.
                    catch (CryptographicException) { return blob.Length == 32 ? blob : null; }
                }
                return blob.Length >= 32 ? blob : null;
            }
            catch { return null; }
        }

        // A DPAPI blob opens with dwVersion (little-endian 1) followed by the 16-byte provider GUID.
        // MEASURED on this platform 2026-08-01 with ProtectedData.Protect over a 32-byte payload —
        // identical under LocalMachine, CurrentUser, and with no entropy at all, blob length 262:
        //   01 00 00 00 | D0 8C 9D DF 01 15 D1 11 8C 7A 00 C0 4F C2 97 EB | ...
        // Pinned by DpapiBlobHeaderProbe_MatchesWhatProtectedDataActuallyWrites.
        private static readonly byte[] DpapiBlobProviderGuid =
        {
            0xD0, 0x8C, 0x9D, 0xDF, 0x01, 0x15, 0xD1, 0x11,
            0x8C, 0x7A, 0x00, 0xC0, 0x4F, 0xC2, 0x97, 0xEB
        };

        /// <summary>
        /// True when <paramref name="blob"/> carries the DPAPI blob header this service's own writes
        /// produce. Shape only — it says nothing about whether the blob can be unwrapped here, which
        /// is the point: a key orphaned by an identity change is unreadable and still perfectly
        /// genuine.
        /// </summary>
        private static bool LooksLikeDpapiBlob(byte[] blob) =>
            blob.Length >= 4 + 16 &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(0, 4)) == 1 &&
            blob.AsSpan(4, 16).SequenceEqual(DpapiBlobProviderGuid);

        /// <summary>
        /// R1's archive trace: does <paramref name="path"/> hold something this installation could
        /// actually have written as an archived signing key?
        /// <para>
        /// It used to be a bare <c>File.Exists</c>, which was measurably a free win for an attacker:
        /// a ONE-BYTE file named <c>hmac.key.&lt;forged-id&gt;</c> corroborated the forged id, bought
        /// the benign UNVERIFIABLE verdict with its printed exculpation, and — because the benign
        /// verdict is what licenses learning — put the forged id into the anchor's
        /// <c>SeenKeyIds</c>, after which deleting the planted file left the verdict benign forever.
        /// It was also incoherent: a 32-byte junk file at the same path reported BROKEN instead,
        /// because 32 bytes resolve as key material and the signature check then fails.
        /// </para>
        /// <para>
        /// ⚠ THIS DOES NOT CLOSE THE HOLE AND IT DOES NOT REMOVE THE PERMANENT LAUNDERING. It must
        /// never be described as doing either. Everything R1 consults lives in the audit-log
        /// directory under the same ACL as the <c>.jsonl</c> the threat model already concedes the
        /// attacker can write. MEASURED 2026-08-01 on a healthy install — head entry's
        /// <c>Message</c> tampered and its <c>KeyId</c> rewritten to <c>CAFEBABECAFEBABE</c>, one
        /// plant per row, verdict / broken count / whether the forged id was learned into
        /// <c>SeenKeyIds</c> / verdict after the plant is deleted again:
        /// <code>
        ///   no plant             BROKEN         1   no    BROKEN
        ///   1 arbitrary byte     BROKEN         1   no    BROKEN    &lt;- all this check bought
        ///   32 junk bytes        BROKEN         1   no    BROKEN
        ///   20 header bytes      UNVERIFIABLE   0   YES   UNVERIFIABLE
        ///   a copied real blob   UNVERIFIABLE   0   YES   UNVERIFIABLE
        ///   a foreign DPAPI blob UNVERIFIABLE   0   YES   UNVERIFIABLE
        /// </code>
        /// The 20-byte row is a file holding ONLY the header this method matches on — the exact bytes
        /// printed in the comment above <see cref="DpapiBlobProviderGuid"/> and pinned a second time
        /// by <c>DpapiBlobHeaderProbe_MatchesWhatProtectedDataActuallyWrites</c>. No key material of
        /// any kind. It buys the benign UNVERIFIABLE verdict with its printed exculpation, lands the
        /// forged id in the anchor's <c>SeenKeyIds</c>, and SURVIVES DELETION OF THE PLANT, exactly
        /// as the one-byte version did. So permanent laundering is still reachable and only its
        /// PRICE moved: from one arbitrary byte to twenty published constant bytes. Copying a real
        /// key blob under a forged name satisfies this check just as completely. A speed bump on one
        /// route to the hole, not a boundary — see
        /// <c>ForgedKeyId_CorroboratedByTheHeaderBytesAlone_StillLaundersPermanently</c>, which pins
        /// the 20-byte row so no later round can restate this residual smaller than it is.
        /// </para>
        /// <para>
        /// PRESENT-BUT-UNREADABLE MUST STILL PASS. That is what genuine key loss looks like on disk
        /// (a valid blob wrapped under an identity that is gone), and refusing it would reinstate the
        /// false-tamper incident this whole branch exists to remove. So an IO/ACL failure to read a
        /// file that exists is treated as corroboration, not as evidence against it; only a file we
        /// successfully read and that is NOT key-shaped is refused.
        /// </para>
        /// </summary>
        private static bool ArchivedKeyFileIsKeyShaped(string path)
        {
            if (!File.Exists(path)) return false;
            byte[] blob;
            try { blob = File.ReadAllBytes(path); }
            catch { return true; } // present, unreadable — indistinguishable from genuine loss
            if (blob.Length == 32) return true;                      // raw key (non-Windows write path)
            return OperatingSystem.IsWindows() && LooksLikeDpapiBlob(blob);
        }

        /// <summary>
        /// H4: resolve the key that signed an entry. Current/legacy (null KeyId) → current key.
        /// A known KeyId → its archived key (cached).
        /// <para>
        /// Returns FALSE when the entry's KeyId resolves to nothing available on this box. The old
        /// code silently fell back to the current key here, which made the recomputed signature
        /// mismatch and reported the entry as TAMPERED — manufacturing a false tamper verdict out
        /// of an ordinary key-management accident. Callers must treat false as UNVERIFIABLE
        /// ("cannot be confirmed or denied"), never as BROKEN ("signature is present and wrong").
        /// </para>
        /// </summary>
        private bool TryResolveKeyForEntry(AuditLogEntry entry, out byte[] key)
        {
            var kid = entry.KeyId;
            if (string.IsNullOrEmpty(kid) || string.Equals(kid, _currentKeyId, StringComparison.Ordinal))
            {
                key = _hmacKey;
                return true;
            }
            // KeyId is attacker-writable text on legacy entries and it is about to be concatenated
            // into a file path. Anything this installation could have produced is 16 uppercase hex
            // (ComputeKeyId); refuse the rest outright rather than probing "hmac.key.../../x".
            if (!IsWellFormedKeyId(kid))
            {
                key = _hmacKey;
                return false;
            }
            lock (_keyCacheLock)
            {
                if (_keyCache.TryGetValue(kid, out var cached)) { key = cached; return true; }
                var raw = TryReadKeyFile(_keyPath + "." + kid);
                if (raw != null) { _keyCache[kid] = raw; key = raw; return true; }
            }
            key = _hmacKey; // not usable for a verdict — caller must not treat a mismatch as tamper
            return false;
        }

        // ── 2026-08-01: is this unverifiable run key LOSS, or a rewritten KeyId field? ───────────

        /// <summary>
        /// One contiguous run of entries whose signing key could not be resolved, plus the key ids
        /// on either side of it. Tracked streaming (no chain is ever held in memory) because the
        /// verdict for such a run cannot be formed from the entry alone.
        /// </summary>
        private sealed class UnverifiableRun
        {
            public string? KeyId;
            public string? PrecedingKeyId;   // key id of the entry immediately before the run (null = chain head)
            public int Count;
            public string? FirstRecord;
            public int FirstOrdinal;
            public int MaxSuccessionBefore = -1; // highest succession index seen before the run opened
        }

        /// <summary>What an unresolvable-key run turned out to be. See <see cref="ClassifyUnverifiableRun"/>.</summary>
        private enum RunVerdict
        {
            /// <summary>The run has the shape of a real key lifetime AND provenance corroborates it.</summary>
            KeyLoss,
            /// <summary>A refutation fired: the KeyId field was rewritten. Tamper.</summary>
            Rewritten,
            /// <summary>
            /// No refutation fired, but no provenance evidence was available to corroborate it either.
            /// Neither verdict is earned; say so rather than picking the flattering one.
            /// </summary>
            Indeterminate
        }

        /// <summary>
        /// THE CLASSIFIER. Decides whether an unresolvable-key run is genuine key loss (UNVERIFIABLE,
        /// Warning), a rewritten <see cref="AuditLogEntry.KeyId"/> field (BROKEN, Critical), or
        /// undecidable on the evidence available (INDETERMINATE, Error).
        /// <para>
        /// ROUND 3 (2026-08-01, second review). Round 2 returned a BOOLEAN, so "no refutation fired"
        /// and "no evidence to fire one with" collapsed into the same benign answer. Two independent
        /// reviewers then measured the consequence: with the ledger unavailable — which is the state
        /// of EVERY existing installation on its first launch after that upgrade, and a state an
        /// attacker can restore by deleting the anchor and its regime marker together — a single
        /// altered entry at the HEAD of the chain, with its unsigned KeyId set to sixteen arbitrary
        /// hex digits, came out UNVERIFIABLE / broken=0 / Warning, and the product printed a
        /// confident exculpation over the record that had just been altered. It needed no prior key
        /// incident, no real dead key id, and no file deletion. It was also permanent, because the
        /// forged id was learned into the ledger during the fail-open and corroborated itself on
        /// every later launch.
        /// </para>
        /// <para>
        /// WHY THIS EXISTS. Splitting UNVERIFIABLE out of BROKEN on 2026-08-01 stopped a key-management
        /// accident being reported as tampering — correct, and it fixed a real incident. But it made
        /// the verdict turn on KeyId, which is NOT in the v1 signed canonical form. Editing that one
        /// unsigned string (tamper an entry, then point its KeyId at a key nobody has) downgraded
        /// Critical to Warning for zero cryptographic work, and made three client-facing surfaces
        /// print "no entry was found to be altered" about an entry that had just been altered.
        /// </para>
        /// <para>
        /// THE SHAPE ARGUMENT. Genuine key loss is a property of a KEY, not of an entry: the key was
        /// in force over one contiguous stretch of the chain, and every entry it signed goes dark at
        /// once. A rewrite is a property of an ENTRY. Three refutations follow, and any one of them is
        /// enough to call the run tampering:
        /// </para>
        /// <list type="number">
        /// <item><b>R1 unknown key.</b> The key id leaves NO trace of ever having existed here — not
        /// in succession order, not in the remembered observed-key set, and no archive file bears it.
        /// Real loss leaves the archive file on disk, merely unreadable (verified on the live service,
        /// which still holds hmac.key.&lt;id&gt; for both of its historical keys). Three independent
        /// traces, so losing any one of them cannot by itself flip a benign chain to Critical.</item>
        /// <item><b>R2 succession regression.</b> Keys only ever move forward. A run claiming a key
        /// that had already been superseded before these entries were written is chronologically
        /// impossible.</item>
        /// <item><b>R3 sandwich.</b> The run is bracketed by the SAME key id on both sides. A key that
        /// was in force, vanished for a few records, then came back is not a key lifetime. This one
        /// needs no ledger at all, so it still fires on an install with no provenance evidence.</item>
        /// </list>
        /// <para>
        /// NO LONGER FAIL-OPEN. R1 and R2 require a ledger. With none, they stay silent — absence of
        /// evidence is still not evidence of forgery, so the run is NOT called tampering. But it is
        /// not called benign either: it returns <see cref="RunVerdict.Indeterminate"/>, which drives
        /// a distinct verdict, distinct wording that asserts nothing about alteration, and a higher
        /// severity than a known-benign gap. The live 2026-08-01 incident (which orphaned the anchor
        /// along with the key) therefore still does NOT report BROKEN — the false-tamper verdict this
        /// branch exists to remove stays removed — it reports "cannot be determined", which is the
        /// truth about an installation that has lost both the key and the evidence about the key.
        /// </para>
        /// <para>
        /// R0 and R3 need no ledger and stay authoritative, so they are evaluated on every path and
        /// still return <see cref="RunVerdict.Rewritten"/> with no provenance at all.
        /// </para>
        /// <para>See the residual-risk note on <see cref="LoadKeyLedgerFromAnchor"/>'s field.</para>
        /// </summary>
        /// <param name="followingKeyId">Key id of the first entry after the run; null at chain end.</param>
        /// <param name="reason">Populated with the refutation, or with why no verdict could be formed.</param>
        private RunVerdict ClassifyUnverifiableRun(UnverifiableRun run, string? followingKeyId, out string reason)
        {
            var kid = run.KeyId;

            // R0: not even shaped like something ComputeKeyId could have produced. Needs no ledger.
            if (!IsWellFormedKeyId(kid))
            {
                reason = $"key id '{kid}' is not a well-formed audit key id (16 hex) — it was not written by this service";
                return RunVerdict.Rewritten;
            }

            if (_keyLedgerAvailable)
            {
                var index = SuccessionIndex(kid);

                // R1. Three independent traces count as corroboration: succession order, the
                // remembered observed-key set, and an archived key file of the right shape
                // (present-but-unreadable is exactly what real loss looks like on disk).
                //
                // LOSING any one of them cannot by itself flip a benign chain to Critical — that is
                // the property the three-trace design buys, and it is why an archive tidy-up is not
                // an incident on its own.
                //
                // ⚠ THE CONVERSE, which this comment used to leave unstated and which is the whole
                // exposure: GAINING any one of them flips a TAMPERED chain to benign. All three live
                // in the audit-log directory, under the same ACL as the .jsonl the threat model
                // already concedes the attacker can write — the anchor carries KeyOrder and
                // SeenKeyIds and can be minted locally (see ChainAnchor), and the archive trace is
                // satisfied by copying any real key blob to hmac.key.<forged-id>. Until 2026-08-01
                // round 4 the archive trace cost ONE BYTE, because it was a bare File.Exists; it now
                // costs a copy of a genuine blob (see ArchivedKeyFileIsKeyShaped), which is cheap,
                // not impossible. R1 raises the floor on a careless forger. It does not stop one.
                bool observed;
                lock (_observedKeyIds) { observed = _observedKeyIds.Contains(kid!); }
                if (index < 0 && !observed && !ArchivedKeyFileIsKeyShaped(_keyPath + "." + kid))
                {
                    reason = $"key id {kid} was never made current on this installation, has never been seen on " +
                             "this chain, and no archived key file of that id holds anything shaped like a key " +
                             "this service wrote — the KeyId field was rewritten, not the key lost";
                    return RunVerdict.Rewritten;
                }

                // R2
                if (index >= 0 && index < run.MaxSuccessionBefore)
                {
                    reason = $"key id {kid} had already been superseded before these entries were written " +
                             "— a chain cannot go backwards through key succession";
                    return RunVerdict.Rewritten;
                }
            }

            // R3 — evaluated with or without a ledger.
            if (run.PrecedingKeyId != null && followingKeyId != null &&
                string.Equals(run.PrecedingKeyId, followingKeyId, StringComparison.Ordinal))
            {
                reason = $"the run is bracketed by key {run.PrecedingKeyId} on both sides — a key does not " +
                         "lapse for a handful of records and then resume, so these entries were rewritten";
                return RunVerdict.Rewritten;
            }

            if (!_keyLedgerAvailable)
            {
                // The benign verdict is a CLAIM ("this key really did exist here and really was
                // lost"), and nothing on this machine backs it. Neither does anything refute it.
                reason = $"no key-provenance evidence is available on this machine, so key id {kid} can be " +
                         "neither corroborated as one this installation held nor refuted as invented";
                return RunVerdict.Indeterminate;
            }

            reason = string.Empty;
            return RunVerdict.KeyLoss;
        }

        /// <summary>Normalised key id used for neighbour comparison; legacy null becomes the current key.</summary>
        private string NeighbourKeyId(AuditLogEntry entry) =>
            string.IsNullOrEmpty(entry.KeyId) ? _currentKeyId : entry.KeyId;

        // ── H6 failover-clone helper ─────────────────────────────────────────────

        /// <summary>Shallow clone tagged as a failover entry, so the requeued ORIGINAL (which flushes
        /// into the main chain on recovery) is never mutated.</summary>
        private static AuditLogEntry CloneForFailover(AuditLogEntry e) => new()
        {
            Timestamp = e.Timestamp,
            EventType = AuditEventType.AuditFailoverEntry,
            Severity = e.Severity,
            Message = e.Message,
            Details = e.Details,
            User = e.User,
            Machine = e.Machine
        };

        // ── H5 truncation anchor (out-of-band chain-tail checkpoint) ─────────────

        private string ChainAnchorPath => Path.Combine(_logDirectory, ".chain-anchor");
        // "Regime active" marker: proves an anchor HAS been written on this install, so a MISSING
        // anchor with a present log can be distinguished from a genuine first-run / pre-upgrade log.
        // Its establishment is mirrored to the (out-of-band, admin-cleared-only) Event Log, so an
        // auditor can confirm from T0 onward that an anchor must always exist.
        private string ChainAnchorRegimePath => Path.Combine(_logDirectory, ".chain-anchor.regime");

        private sealed class ChainAnchor
        {
            public string Sig { get; set; } = string.Empty;
            public string Ts { get; set; } = string.Empty;

            /// <summary>
            /// 2026-08-01 KEY-PROVENANCE LEDGER. Every HMAC key id this installation has made
            /// current, in succession order. Absent on anchors written by earlier builds (an empty
            /// list simply means "no provenance evidence available", never "that key is bogus").
            /// <para>
            /// ⚠ 2026-08-01 ROUND 4 CORRECTION — THE PREVIOUS CLAIM HERE WAS FALSE AND LOAD-BEARING.
            /// This used to read: "it lives in the anchor because the anchor is already written
            /// out-of-band and DPAPI-wrapped, so it cannot be edited by whoever can edit the .jsonl.
            /// That asymmetry is the whole point." THERE IS NO SUCH ASYMMETRY. The anchor sits in
            /// the audit-log directory beside the .jsonl, under the same ACL. Its DPAPI entropy is
            /// the plaintext string literal <c>SQLTriage.AuditLog.HmacKey.v1</c>. It ships IN THE
            /// CLEAR inside SQLTriage.dll — a C# string literal, so it sits UTF-16 in the assembly's
            /// user-string heap and one UTF-16 byte search over the file returns it. No tooling, no
            /// debugger, no key. (An earlier note pinned that to a byte OFFSET. The load-bearing
            /// claim is that the entropy is recoverable; the offset is build-specific and goes stale
            /// immediately — measured 2026-08-01 it differed between the Debug and Release win-x64
            /// assemblies of the same commit, and it moved again between two Debug builds of this
            /// branch on the same day. A fixed number there reads as precision the statement does
            /// not have.) Round 2 also
            /// moved the wrap to <c>DataProtectionScope.LocalMachine</c> — so ANY local process can
            /// mint a valid anchor carrying any ledger it likes. Measured both directions:
            /// a minted ledger naming a forged key buys UNVERIFIABLE on a TAMPERED store; a minted
            /// ledger naming a foreign key flips an INNOCENT store to BROKEN.
            /// </para>
            /// <para>
            /// What the anchor actually buys is therefore narrower, and worth keeping on its own
            /// terms: it is OUT-OF-BAND, so an attacker who edits only the .jsonl and forgets the
            /// anchor is caught, and truncation of the tail is detectable at all. It is not a
            /// tamper-proof store and nothing downstream may be written as if it were.
            /// </para>
            /// </summary>
            public List<string> KeyOrder { get; set; } = new();

            /// <summary>
            /// Key ids seen ON THE CHAIN and accepted as genuine loss by the classifier. Distinct
            /// from <see cref="KeyOrder"/>, which is succession and must stay strictly ordered.
            /// <para>
            /// This exists so R1 corroboration does not depend on the hmac.key.&lt;id&gt; archive files
            /// surviving forever: once a lost key has been accepted, removing its archive file (a log
            /// directory tidy-up over a 15-year deployment) must not turn a benign chain into a
            /// Critical tamper verdict.
            /// </para>
            /// <para>
            /// ⚠ 2026-08-01 ROUND 4: THAT PROTECTION STARTS ONLY ONCE THE ID IS IN THIS LIST, and the
            /// sentence above used to read as a general invitation to tidy up. It is not one. A key
            /// id reaches this list only on a launch where the classifier returned
            /// <see cref="RunVerdict.KeyLoss"/>, which needs a ledger, which the first launch after
            /// the upgrade does not have. So for a genuine loss the safe window opens at the SECOND
            /// launch after the upgrade. Deleting the archive file before that — measured — gives
            /// INDETERMINATE at launch 1 and then permanent BROKEN, a false tamper verdict on an
            /// innocent chain, produced by nothing more than housekeeping. DO NOT delete
            /// <c>hmac.key.*</c> files as maintenance; the runbook says the same thing.
            /// </para>
            /// <para>
            /// 2026-08-01 ROUND 3 CORRECTION. This used to claim that "learning is gated on the run
            /// ALREADY passing the classifier, so it cannot launder a key id that the ledger would
            /// otherwise refute." That was measurably FALSE: passing the classifier was fail-open
            /// whenever the ledger was unavailable, so an id the ledger would have refuted was
            /// learned anyway — and a one-launch window became permanent laundering, because from
            /// the next launch on the forged id was in this very list and corroborated itself.
            /// What is true NOW: a key id is learned only when the classifier returned
            /// <see cref="RunVerdict.KeyLoss"/>, and that verdict is only reachable when a ledger
            /// was actually read. With no ledger the verdict is Indeterminate and NOTHING is
            /// learned, so this list can never be seeded from a state that had no evidence.
            /// Enforced twice: at the call sites, and in <see cref="RememberObservedKeyId"/> itself.
            /// </para>
            /// </summary>
            public List<string> SeenKeyIds { get; set; } = new();
        }

        // Key-provenance ledger, loaded from the anchor at construction (BEFORE verification runs).
        private readonly List<string> _knownKeyOrder = new();
        private readonly HashSet<string> _observedKeyIds = new(StringComparer.Ordinal);
        /// <summary>
        /// True only when a ledger was actually read out of the anchor. With no ledger we fall back
        /// to shape analysis alone (R0 and R3) and never manufacture a tamper verdict — absence of
        /// evidence is not evidence of forgery, and the live 2026-08-01 incident orphaned the
        /// CurrentUser-wrapped anchor along with the key. But an unrefuted run in that state is
        /// reported INDETERMINATE, not UNVERIFIABLE: the benign verdict is a positive claim and
        /// there is nothing here to back it.
        /// <para>
        /// RESIDUAL RISK, restated 2026-08-01 (round 4) from what was MEASURED rather than from the
        /// attack each round happened to design against. Three consecutive rounds understated this
        /// and were caught by measurement each time; round 3's version claimed the attacker's
        /// ceiling was "an alarm the operator cannot close rather than a clean bill of health",
        /// which is false. The honest summary is one sentence:
        /// </para>
        /// <para>
        /// <b>AN ATTACKER WHO CAN WRITE THE AUDIT-LOG DIRECTORY CAN MANUFACTURE ANY VERDICT,
        /// INCLUDING INTACT.</b> Every artefact the benign and the clean verdicts rest on — the
        /// <c>.chain-anchor</c> ledger, its <c>.regime</c> marker, and the <c>hmac.key.&lt;id&gt;</c>
        /// archive files — sits in that directory under the same ACL as the <c>.jsonl</c>. The
        /// anchor's DPAPI entropy is a plaintext literal in the shipped assembly and the wrap is
        /// LocalMachine, so any local process can mint a valid one. Measured on this branch:
        /// a minted ledger naming a forged key buys UNVERIFIABLE on a store that WAS tampered;
        /// a minted ledger naming a foreign key flips an INNOCENT store to BROKEN; and the
        /// pre-existing full-forgery path in <see cref="TryReadKeyFile"/> (32 raw bytes + a v1
        /// re-sign + a minted anchor) reports a silent <c>INTACT</c>. The classifier raises the cost
        /// of a careless forgery; it is not a boundary.
        /// </para>
        /// <para>
        /// The specific residuals, each measured, none of them closed by this round:
        /// (a) a run rewritten at the HEAD of the chain to a key id this install genuinely held and
        /// genuinely lost reads as loss, because it is indistinguishable from loss;
        /// (b) with no ledger a forged run is reported as undecidable rather than as tampering —
        /// INDETERMINATE is an alarm the operator cannot close, but it is NOT the attacker's ceiling,
        /// because the paths above reach UNVERIFIABLE and INTACT;
        /// (c) a genuine key loss whose archive file was ALSO gone before that key id reached
        /// <c>SeenKeyIds</c> — i.e. at any point before the SECOND launch after the upgrade, not
        /// merely "before the upgrade" as round 3 wrote it — is INDETERMINATE at launch 1 and then
        /// refuted by R1 and reported permanently BROKEN from launch 2 onward, because nothing on
        /// the box can then distinguish it from a forgery. An ordinary log-directory tidy-up in that
        /// window is enough to trigger it;
        /// (d) the archive trace is satisfied by any file shaped like a DPAPI blob, so copying a
        /// real key blob under a forged id defeats R1 outright (see
        /// <see cref="ArchivedKeyFileIsKeyShaped"/>).
        /// Full write-up: <c>docs/compliance/incident-response-runbook.md</c> § Audit Chain
        /// Provenance Indeterminate and § What this chain does and does not guarantee.
        /// </para>
        /// </summary>
        private bool _keyLedgerAvailable;

        /// <summary>Key ids are always 16 uppercase hex chars (<see cref="ComputeKeyId"/>).</summary>
        private static bool IsWellFormedKeyId(string? kid) =>
            kid is { Length: 16 } && kid.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

        /// <summary>
        /// Reads the key-provenance ledger out of the anchor. Must run BEFORE any chain walk.
        /// Never writes: an unreadable anchor is evidence <see cref="CheckTruncationAnchor"/> still
        /// has to see, so this is strictly a read.
        /// </summary>
        private void LoadKeyLedgerFromAnchor()
        {
            try
            {
                var anchor = TryReadChainAnchor();
                if (anchor == null) return;
                foreach (var kid in anchor.SeenKeyIds)
                    if (IsWellFormedKeyId(kid)) _observedKeyIds.Add(kid);
                if (anchor.KeyOrder is not { Count: > 0 }) return;
                foreach (var kid in anchor.KeyOrder)
                    if (IsWellFormedKeyId(kid) && !_knownKeyOrder.Contains(kid))
                        _knownKeyOrder.Add(kid);
                _keyLedgerAvailable = _knownKeyOrder.Count > 0;
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "[AUDIT] Could not read the key-provenance ledger from the chain anchor");
            }
        }

        /// <summary>
        /// Appends a key id to the in-memory ledger. Persistence rides the next anchor write, which
        /// is deliberate: writing the anchor here would either establish the anchor regime early or
        /// overwrite an unreadable anchor before startup verification has reported it.
        /// </summary>
        private void RememberKeyInLedger(string keyId)
        {
            if (!IsWellFormedKeyId(keyId)) return;
            lock (_knownKeyOrder)
            {
                if (!_knownKeyOrder.Contains(keyId)) _knownKeyOrder.Add(keyId);
            }
        }

        /// <summary>Position of a key id in succession order, or -1 if this install never recorded it.</summary>
        private int SuccessionIndex(string? keyId)
        {
            if (string.IsNullOrEmpty(keyId)) return -1;
            lock (_knownKeyOrder) { return _knownKeyOrder.IndexOf(keyId); }
        }

        private string[] KeyOrderSnapshot()
        {
            lock (_knownKeyOrder) { return _knownKeyOrder.ToArray(); }
        }

        /// <summary>
        /// Records a key id the classifier accepted as genuine loss. See ChainAnchor.SeenKeyIds.
        /// <para>
        /// B1(a), 2026-08-01 round 3: REFUSES to learn while the provenance ledger is unavailable,
        /// even if a caller asks. Learning during the fail-open window is precisely what turned a
        /// one-launch hole into permanent laundering — the forged id went into SeenKeyIds, and from
        /// the next launch on it corroborated itself against R1. A ledger that can be seeded from a
        /// state with no evidence is not a ledger.
        /// </para>
        /// </summary>
        private void RememberObservedKeyId(string? keyId)
        {
            if (!_keyLedgerAvailable) return;
            if (!IsWellFormedKeyId(keyId)) return;
            lock (_observedKeyIds) { _observedKeyIds.Add(keyId!); }
        }

        private string[] ObservedKeyIdsSnapshot()
        {
            lock (_observedKeyIds) { return _observedKeyIds.ToArray(); }
        }

        /// <summary>Record the current chain tail out-of-band (DPAPI-wrapped) after each flush.</summary>
        private void WriteTruncationAnchor(string lastSig, DateTime lastTs)
        {
            try
            {
                var json = JsonSerializer.Serialize(
                    new ChainAnchor
                    {
                        Sig = lastSig,
                        Ts = lastTs.ToString("o"),
                        KeyOrder = KeyOrderSnapshot().ToList(),
                        SeenKeyIds = ObservedKeyIdsSnapshot().ToList()
                    }, SerializerOptions);
                var bytes = Encoding.UTF8.GetBytes(json);
                var toWrite = OperatingSystem.IsWindows()
                    ? ProtectedData.Protect(bytes, HmacKeyEntropy, DataProtectionScope.LocalMachine)
                    : bytes;
                File.WriteAllBytes(ChainAnchorPath, toWrite);

                // First anchor ever → establish the regime marker + out-of-band Event Log record.
                if (!File.Exists(ChainAnchorRegimePath))
                {
                    File.WriteAllText(ChainAnchorRegimePath, DateTime.UtcNow.ToString("o"));
                    WriteEventLog("Audit chain truncation-anchor regime initialised — an anchor must exist from now on.",
                        AuditSeverity.Info);
                }
            }
            catch (Exception ex) { Serilog.Log.Debug(ex, "[AUDIT] Could not write chain anchor"); }
        }

        /// <summary>
        /// Reads and unwraps the chain anchor, or throws. Reads a LocalMachine-wrapped blob (the scope
        /// this build writes) or a legacy CurrentUser one with the same call — DPAPI ignores the scope
        /// argument on unprotect (see <see cref="TryUnwrapHmacKey"/>). The anchor needs no explicit
        /// upgrade path: it is rewritten after every flush and every write is LocalMachine, so it
        /// self-upgrades on first use. A CurrentUser anchor written by a DIFFERENT identity stays
        /// unreadable, which is correct — nothing here recovers it, and nothing claims to.
        /// </summary>
        private ChainAnchor? ReadChainAnchorOrThrow()
        {
            var raw = File.ReadAllBytes(ChainAnchorPath);
            var bytes = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(raw, HmacKeyEntropy, DataProtectionScope.LocalMachine)
                : raw;
            return JsonSerializer.Deserialize<ChainAnchor>(Encoding.UTF8.GetString(bytes), SerializerOptions);
        }

        /// <summary>Non-throwing anchor read for the provenance ledger; null when absent or unreadable.</summary>
        private ChainAnchor? TryReadChainAnchor()
        {
            try
            {
                return File.Exists(ChainAnchorPath) ? ReadChainAnchorOrThrow() : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// H5: detect trailing-entry / whole-segment truncation by reconciling the on-disk chain
        /// against the out-of-band anchor. The anchored tail signature MUST still be present in the
        /// latest segment (as the tail, or an earlier line if we crashed between append and anchor
        /// write). If it is gone, entries were removed. Called at the end of startup verification.
        /// </summary>
        private void CheckTruncationAnchor()
        {
            try
            {
                if (!File.Exists(ChainAnchorPath))
                {
                    // Adversarial-review fix (2026-07-07): a MISSING anchor is only benign on a
                    // genuine first-run / first-launch-after-upgrade. If the regime marker shows an
                    // anchor was previously established AND the log still has entries, the anchor was
                    // deleted — exactly the truncation threat model. Do not treat that as first-run.
                    if (File.Exists(ChainAnchorRegimePath) && HasContentSegments())
                        FlagTruncation("chain anchor is missing while the log has entries and the anchor regime was active — likely deletion/tampering");
                    return;
                }

                ChainAnchor? anchor;
                try
                {
                    anchor = ReadChainAnchorOrThrow();
                }
                catch (Exception ex)
                {
                    // Anchor PRESENT but unreadable. Surfaced, never swallowed — but NOT as tampering.
                    //
                    // 2026-08-01 round 4. This used to call FlagTruncation("possible tampering"),
                    // which set ChainBroken and handed the operator the tamper playbook. Measured on
                    // the live incident's own shape — key, archive and anchor all present-but-
                    // unreadable, nothing altered, which is exactly what a service-account change
                    // produces on a pre-LocalMachine install — startup said BROKEN while VerifyChain
                    // on the same bytes said INDETERMINATE with broken=0. The runbook classifies
                    // that shape as INDETERMINATE (§ Audit Chain Provenance Indeterminate, cause 2),
                    // so the startup banner was contradicting both the verifier and the runbook, and
                    // the false-tamper verdict this branch exists to remove was still reachable.
                    //
                    // An unreadable anchor is an evidence failure, not evidence of an attack: the
                    // service cannot read the record it needs, so it cannot conclude anything. It is
                    // also not benign — a deliberately corrupted anchor looks identical — so it must
                    // not be silent or Warning-level either. INDETERMINATE is the honest verdict and
                    // it is the one state the operator is told not to close without evidence.
                    FlagAnchorIndeterminate(
                        $"the out-of-band chain anchor is present but could not be read ({ex.GetType().Name}). " +
                        "The tail checkpoint it holds cannot be reconciled against the log, so truncation can be " +
                        "neither detected nor ruled out. A DPAPI identity change orphans the anchor exactly like " +
                        "this, and so does deliberate corruption — nothing here distinguishes them");
                    return;
                }
                if (anchor == null || string.IsNullOrEmpty(anchor.Sig)) return;

                // Anchored tail == current tail → intact, nothing removed.
                if (string.Equals(anchor.Sig, _lastSignature, StringComparison.Ordinal)) return;

                // If the anchored point is older than the retention window it may have legitimately
                // aged out — don't cry wolf on a long-idle install.
                if (DateTime.TryParse(anchor.Ts, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var anchoredTs)
                    && (DateTime.UtcNow - anchoredTs).TotalDays > RetentionDays)
                    return;

                var segments = GetAllSegmentsChronological();
                if (segments.Length == 0)
                {
                    FlagTruncation("all audit segments removed while a chain anchor still exists");
                    return;
                }

                // The anchored tail must appear somewhere in the latest segment. It may not be the
                // very last line: we intentionally tolerate the one-flush lag where a crash landed
                // between the segment append and the anchor write.
                // KNOWN RESIDUAL (adversarial review 2026-07-07): that same lag is a small window in
                // which a same-user attacker could delete exactly the entries appended after the
                // anchored line and evade this check — the anchor never recorded those in-flight
                // entries, so their loss is undetectable here. This is inherent to post-flush
                // anchoring; the exposure is at most one unclean-shutdown's final batch, and the
                // regime marker + Event Log record still prove the anchor's continued existence.
                var latest = segments[^1];
                bool found = false;
                foreach (var line in File.ReadLines(latest))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var entry = JsonSerializer.Deserialize<AuditLogEntry>(line, SerializerOptions);
                    if (entry != null && string.Equals(entry.Signature, anchor.Sig, StringComparison.Ordinal))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                    FlagTruncation($"anchored chain tail is missing from latest segment {Path.GetFileName(latest)}");
            }
            catch (Exception ex) { Serilog.Log.Debug(ex, "[AUDIT] Chain anchor check failed"); }
        }

        private void FlagTruncation(string detail)
        {
            ChainBroken = true;
            ChainTruncationDetail ??= detail;
            Serilog.Log.Error("[AUDIT] Audit chain TRUNCATION detected: {Detail}", detail);
            WriteEventLog($"Audit chain truncation detected: {detail}", AuditSeverity.Critical);
        }

        /// <summary>
        /// The anchor check could form no verdict. Sets <see cref="ChainProvenanceIndeterminate"/>,
        /// never <see cref="ChainBroken"/>: nothing was shown to be altered and nothing was shown not
        /// to be. Severity Error, deliberately — Warning is what a KNOWN-benign gap gets and this is
        /// not known to be benign; Critical is the tamper signal and this is not known to be an
        /// attack. Writes its own on-chain record (deduped by marker file) because this runs after
        /// the startup verdict blocks have already emitted theirs.
        /// </summary>
        private void FlagAnchorIndeterminate(string detail)
        {
            ChainProvenanceIndeterminate = true;
            ChainIndeterminateDetail ??= detail;
            Serilog.Log.Error(
                "[AUDIT] Audit chain INDETERMINATE (anchor): {Detail}. This is NOT a tamper finding and NOT a " +
                "clean result — see the incident-response runbook, § Audit Chain Provenance Indeterminate.", detail);
            WriteEventLog($"Audit chain state could not be determined from the anchor: {detail}", AuditSeverity.Error);

            try
            {
                var markerPath = Path.Combine(_logDirectory, ".chain-anchor-indeterminate-marker");
                var previous = File.Exists(markerPath) ? File.ReadAllText(markerPath).Trim() : null;
                const string marker = "chain-anchor-unreadable";
                if (previous == marker) return;
                try { File.WriteAllText(markerPath, marker); } catch { }

                Enqueue(new AuditLogEntry
                {
                    EventType = AuditEventType.AuditChainIndeterminate,
                    Severity = AuditSeverity.Error,
                    Message = "Audit chain INDETERMINATE: " + detail +
                              ". Whether entries were removed from the chain can be neither confirmed nor ruled out.",
                    Details = new Dictionary<string, string>
                    {
                        ["Cause"] = "chain-anchor-unreadable",
                        ["ProvenanceLedger"] = "unavailable",
                        ["DetectedBy"] = "startup-verification"
                    }
                });
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "[AUDIT] Could not record the anchor-indeterminate event on the chain");
            }
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
        /// Four-state verdict for a chain verification run. The Unverifiable split exists because a
        /// key-management accident and a genuine tamper used to raise the IDENTICAL Critical
        /// signal — which destroys the tamper signal this product sells. The Indeterminate split
        /// exists because the OPPOSITE mistake is just as bad: calling something a benign key gap
        /// when the evidence needed to say that is not available.
        /// </summary>
        public enum ChainVerificationStatus
        {
            /// <summary>Every entry was checked against its signing key and matched.</summary>
            Intact,
            /// <summary>
            /// At least one entry's signing key is unavailable, so its integrity can be neither
            /// confirmed nor denied — and no entry was found to be actually wrong. An integrity
            /// GAP, not an attack. Reaching this verdict requires the key id to leave a TRACE in
            /// this installation's provenance records; where no trace survives at all the verdict is
            /// <see cref="Indeterminate"/>, not this.
            /// <para>
            /// ⚠ Corrected 2026-08-01 (round 4): this used to say the verdict "requires POSITIVE
            /// provenance evidence that the key id is one this installation held", which is false.
            /// The traces live in the audit-log directory under the same ACL as the log itself —
            /// see the residual note on <c>_keyLedgerAvailable</c>. Read this verdict as "the
            /// forged-KeyId shortcut was not taken carelessly", never as "this key is proven real".
            /// </para>
            /// </summary>
            Unverifiable,
            /// <summary>
            /// At least one entry's signature was checked against its own signing key and did NOT
            /// match (or the cross-segment link is wrong). This is the tamper signal.
            /// </summary>
            Broken,
            /// <summary>
            /// 2026-08-01 round 3. At least one entry could not be checked AND the key-provenance
            /// evidence needed to tell genuine key loss from a rewritten <see cref="AuditLogEntry.KeyId"/>
            /// is not available on this machine. NO claim is made about those entries in either
            /// direction — not benign, not tampered, unknown.
            /// <para>
            /// APPENDED, not inserted: the numeric values of the first three members are load-bearing
            /// for anything that persisted them.
            /// </para>
            /// </summary>
            Indeterminate,
            /// <summary>
            /// 2026-08-11. The chain RESTARTED at least once and nothing else was found wrong. A
            /// restart is an entry that declares a previous-hash link other than the one the walk
            /// carried in, and whose own signature verifies against the link it declares. The
            /// declared link is inside the signed canonical form, so an entry that verifies against
            /// it was written by a holder of the signing key and has not been edited since: the
            /// entries on both sides of the gap are sound, and what is lost is the LINK across it.
            /// <para>
            /// This state exists because the service printed "Treat as tampering until proven
            /// otherwise" nine times over restarts its own predecessor build caused (the old verify
            /// loop returned without advancing the running signature, so the next entry chained onto
            /// a stale one). Counting a provable restart as a tamper is the same false-positive the
            /// <see cref="Unverifiable"/> split exists to kill, one layer along.
            /// </para>
            /// <para>
            /// It is NOT a clean result: <c>Intact</c> stays false, because contiguity across the
            /// gap cannot be shown. APPENDED, like every member above it.
            /// </para>
            /// </summary>
            Restarted
        }

        /// <summary>
        /// Result returned by <see cref="VerifyChain"/> for use in UI and export artifacts.
        /// <para>
        /// <c>Intact</c> stays a strict "everything checked out" boolean — it is false for
        /// Unverifiable, Indeterminate and Broken alike, so no existing fail-safe reader is
        /// loosened. Readers that need to tell an operational gap from an attack from a hole in
        /// the evidence must use <see cref="Status"/>.
        /// </para>
        /// <para>
        /// <c>HeadSignature</c> (appended 2026-08-12) is the signature of the LAST entry this walk
        /// read: the chain tail as it stood when the counts and the verdict beside it were
        /// measured. It rides the RESULT rather than a separate accessor on purpose — a caller that
        /// reads the tail in one call and the counts in another can report a head and an entry
        /// count that never coexisted, because a flush can land between the two. Null when the
        /// chain is empty, or when the last entry carried no signature.
        /// </para>
        /// <para>
        /// Copying that value somewhere else confers no authority on it. Anyone who can write the
        /// audit-log directory can write the tail too, so this field is exactly as forgeable as the
        /// verdict it accompanies (the same caveat the compliance report states under "Scope of this
        /// report", composed in <see cref="ComposeChainVerificationReport"/>: an attacker with
        /// write access there can manufacture any verdict, INTACT included). Recording it off the
        /// box does not close that window. It only means a LATER rewrite disagrees with something
        /// already recorded elsewhere, which raises the cost of a RETROACTIVE forgery and nothing
        /// else. A rewrite before the first record, or inside the window before the next
        /// comparison, is invisible.
        /// </para>
        /// </summary>
        public sealed record ChainVerificationResult(
            bool Intact,
            int EntryCount,
            DateTime? FirstEntry,
            DateTime? LastEntry,
            DateTime VerifiedAt,
            ChainVerificationStatus Status = ChainVerificationStatus.Intact,
            int BrokenCount = 0,
            int UnverifiableCount = 0,
            string? FirstBrokenRecord = null,
            string? FirstUnresolvableKeyId = null,
            int IndeterminateCount = 0,
            string? FirstIndeterminateKeyId = null,
            int LinkBreakCount = 0,
            string? FirstLinkBreakRecord = null,
            int RestartCount = 0,
            string? FirstRestartRecord = null,
            string? KeyReplacementExplanation = null,
            int DuplicateEntryCount = 0,
            string? FirstDuplicateRecord = null,
            string? HeadSignature = null)
        {
            /// <summary>
            /// Entries whose signature was checked against their own key and matched.
            /// <para>
            /// RESTART entries are counted here, and the arithmetic is deliberate: a restart's
            /// signature WAS recomputed against its own signing key and DID match (against the link
            /// the entry itself declares, which is a signed field). It is a verified entry that
            /// happens to sit on the far side of a link gap, and <see cref="RestartCount"/> is where
            /// that second fact is carried. So the closed sum over every entry examined is
            /// <c>EntryCount = VerifiedCount + BrokenCount + UnverifiableCount + IndeterminateCount</c>,
            /// and <see cref="LinkBreakCount"/> is outside it because a link is not an entry.
            /// </para>
            /// </summary>
            public int VerifiedCount =>
                Math.Max(0, EntryCount - BrokenCount - UnverifiableCount - IndeterminateCount);

            /// <summary>Entries that were not checked against a signing key at all, either bucket.</summary>
            public int UncheckedCount => UnverifiableCount + IndeterminateCount;

            /// <summary>
            /// Single word for logs, UI and exported artifacts.
            /// <para>
            /// This used to be its own copy of the switch, and so did
            /// <see cref="ComplianceChainStatement.StatusWord"/>. Three copies of one mapping is
            /// three chances to add a status to two of them: measured 2026-08-11, the sealed
            /// verification report printed "Chain status: BROKEN" over a chain whose own detail two
            /// lines below read "do not file this as tampering", because RESTARTED had been added
            /// here and not there. Both now read the SAME switch, so the arm cannot be added once.
            /// </para>
            /// </summary>
            public string StatusLabel => StatusWordFor(Status);

            /// <summary>
            /// What a restart is and what it costs, appended to EVERY verdict that carries one, so
            /// the fact cannot go unsaid just because a worse finding shares the same run.
            /// Empty when there is no restart to describe.
            /// </summary>
            /// <summary>
            /// The chain's own account of why a key went dark, when one is present and verifies.
            /// Appended to every verdict that has one. It is stated as corroboration and its limit
            /// is stated with it: the record is signed with the key that REPLACED the dead one, so
            /// it rules out a silent transition rather than proving an authorised one.
            /// </summary>
            public string KeyReplacementClause =>
                string.IsNullOrEmpty(KeyReplacementExplanation) ? string.Empty : " " + KeyReplacementExplanation;

            public string RestartClause => RestartCount == 0
                ? string.Empty
                : $" Chain restarts found: {RestartCount}" +
                  (FirstRestartRecord is null ? "" : $" (first: {FirstRestartRecord})") +
                  ". A restart is a point where the chain resumed after a break. The entries on both " +
                  "sides of it verified against their own signing keys, so neither side is in question; " +
                  "what is lost is the LINK across the gap, which means the records cannot be shown to " +
                  "be contiguous there. A restart is not evidence of tampering.";

            /// <summary>
            /// Duplicated (replayed) records, appended to every verdict that carries one, on the
            /// same rule as <see cref="RestartClause"/>: a finding may not go unsaid because a
            /// worse one shares the run. Empty when there is none, so the sentence is conditioned
            /// on the same count it reports.
            /// </summary>
            public string ReplayClause => DuplicateEntryCount == 0
                ? string.Empty
                : $" Duplicated entries found: {DuplicateEntryCount}" +
                  (FirstDuplicateRecord is null ? "" : $" (first: {FirstDuplicateRecord})") +
                  ". A duplicated entry is a record whose signature this run had already read earlier " +
                  "in the same chain, so the same signed record is present more than once. A signature " +
                  "shows who wrote a record, not where it belongs, so a copy of a genuine entry " +
                  "verifies wherever it is placed and cannot be told from the original by its own key. " +
                  "Nothing legitimate writes the same entry twice: treat this as tampering, and do not " +
                  "read it as a restart.";

            /// <summary>One-line plain-English explanation suitable for an operator or auditor.</summary>
            public string StatusDetail => Status switch
            {
                ChainVerificationStatus.Intact =>
                    $"All {EntryCount} entries verified against their signing keys.",
                // The claim is SCOPED to what verification actually establishes. It used to read
                // "No entry was found to be altered", which the code cannot back up: entries whose
                // key is missing were never checked, so nothing is known about them either way.
                // Saying otherwise printed an exculpation over an unexamined record.
                ChainVerificationStatus.Unverifiable =>
                    $"{VerifiedCount} of {EntryCount} entries verified; {UnverifiableCount} could not be checked " +
                    $"because their signing key is unavailable on this machine" +
                    (FirstUnresolvableKeyId is null ? "" : $" (first unresolvable key: {FirstUnresolvableKeyId})") +
                    ". No entry WITH AN AVAILABLE SIGNING KEY was found to have been altered. The unchecked entries " +
                    "are an integrity gap: their contents can be neither confirmed nor denied." +
                    RestartClause + ReplayClause + KeyReplacementClause,
                // NOTHING is asserted here about whether anything was altered, in either direction.
                // The exculpation that belongs on the Unverifiable path would be a lie of emphasis
                // here: the entries in question were never checked, AND the evidence that would
                // show their key id is a real one this installation held is missing too.
                ChainVerificationStatus.Indeterminate =>
                    $"{VerifiedCount} of {EntryCount} entries were checked against their own signing keys and " +
                    $"matched. {IndeterminateCount} could not be checked at all" +
                    (FirstIndeterminateKeyId is null ? "" : $" (first unresolvable key: {FirstIndeterminateKeyId})") +
                    ", AND the key-provenance evidence needed to tell a genuine key loss from a rewritten KeyId " +
                    "field is not available on this machine. This report therefore makes no claim about those " +
                    $"{IndeterminateCount} entries: it can neither confirm nor rule out that they were altered. " +
                    "Do not record them as verified, and do not close this as a key-management event without " +
                    "the evidence — see the incident-response runbook, § Audit Chain Provenance Indeterminate." + // voice-lint:allow pre-existing wording, unchanged; only the concatenation moved
                    RestartClause + ReplayClause + KeyReplacementClause,
                // A chain whose ONLY finding is one or more provable restarts. Not clean, not an
                // accusation. Every number it prints is measured on this run.
                ChainVerificationStatus.Restarted =>
                    $"{VerifiedCount} of {EntryCount} entries were checked against their own signing keys and " +
                    "matched. No entry failed verification and no entry went unchecked." + RestartClause +
                    ReplayClause +
                    // The exculpation is CONDITIONED on the same count the clause above reports.
                    // A replay forces the Broken status, so this arm cannot carry one today; the
                    // condition is here because the reachability of an arm is not what makes a
                    // sentence beside a verdict true, and one unconditioned closing is how this
                    // lane's first defect got written.
                    (DuplicateEntryCount == 0
                        ? " Do not record the chain as contiguous across the restart points, and do not file this " +
                          "as tampering: every entry examined verified."
                        : " Do not record the chain as contiguous across the restart points. Every entry examined " +
                          "verified against its own signing key, and that is not an exculpation here: the " +
                          "duplicated records above are an edit to the log that signature checking alone cannot " +
                          "refute."),
                // THE DEFECT THIS FIXES (2026-08-11; forensic verdict D3, on the live service).
                // The sentence here read "{BrokenCount} of {EntryCount} entries FAILED verification
                // against their own signing key ... a further {UnverifiableCount} could not be
                // checked", and it was false twice over on the bytes it was printed on:
                //   · BrokenCount included cross-segment LINK breaks, which are not entries and
                //     which do not fail against their own key. The live run printed "4 of 1562
                //     entries FAILED" where the honest count of failing entries was 0.
                //   · EntryCount is the total INCLUDING the unverifiable entries, so "a further N"
                //     added a subset back to a set that already contained it.
                // Each fact now carries its own number and its own noun, and the numbers close:
                // verified + mismatches + unchecked = examined, with link breaks stated outside
                // that sum because a link is not an entry.
                _ =>
                    $"Entries examined: {EntryCount}. Verified against their own signing key: {VerifiedCount}. " +
                    $"Entry signature mismatches: {BrokenCount}" +
                    (FirstBrokenRecord is null ? "" : $" (first: {FirstBrokenRecord})") +
                    $". Chain link breaks (a break BETWEEN entries, not a failed entry): {LinkBreakCount}" +
                    (FirstLinkBreakRecord is null ? "" : $" (first: {FirstLinkBreakRecord})") +
                    $". Entries not checked at all: {UncheckedCount}" +
                    (UncheckedCount == 0
                        ? ""
                        : $" ({UnverifiableCount} whose signing key is unavailable on this machine, " +
                          $"{IndeterminateCount} whose key can be placed against no provenance record here)") +
                    // Duplicated entries are carried by ReplayClause, on the same rule as restarts:
                    // one number, in one place, appended to every verdict that has one. They are
                    // outside the closed sum above for the reason link breaks are: a replayed record
                    // IS one of the entries examined and it DID verify against its own key, so
                    // adding it to a bucket would break the arithmetic that sum exists to keep.
                    "." + RestartClause + ReplayClause + KeyReplacementClause +
                    " Treat as tampering until proven otherwise."
            };
        }

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
            int brokenCount = 0;          // signature checked against ITS OWN key and wrong -> tamper
            int unverifiableCount = 0;    // signing key unavailable, provenance says genuine loss
            int indeterminateCount = 0;   // signing key unavailable AND no provenance either way
            int linkBreakCount = 0;       // a LINK between entries is wrong; not an entry, never counted as one
            int restartCount = 0;         // the chain resumed after a break, provably (see IsProvableRestart)
            int duplicateEntryCount = 0;  // the same signed record read twice; a REPLAY, never a restart
            string? firstBrokenRecord = null;
            string? firstDuplicateRecord = null;
            int firstBrokenOrdinal = int.MaxValue;
            string? firstUnresolvableKeyId = null;
            string? firstIndeterminateKeyId = null;
            string? firstLinkBreakRecord = null;
            string? firstRestartRecord = null;
            string? keyTransitionFrom = null;
            string? keyTransitionTo = null;
            DateTime? first = null, last = null;
            // The signature of the last entry this walk reads — the chain tail measured by THIS
            // run, so it can never disagree with the counts reported beside it. See the
            // ChainVerificationResult doc for why it is not a separate accessor.
            string? headSignature = null;

            // Every signature the walk has actually READ, in order. It is the discriminator that
            // stops a DELETION being downgraded to a restart: an entry that resumes from a link
            // still present in the chain removed nothing, while an entry resuming from a link that
            // is nowhere in the chain is consistent with the entries in between having been taken
            // out. The declared link is inside the signed canonical form (v1 and v2 alike), so an
            // attacker without the key cannot move an entry from the second shape into the first.
            var signaturesSeen = new HashSet<string>(StringComparer.Ordinal);

            // 2026-08-01: an unresolvable-key run is only benign if it has the SHAPE of key loss.
            // See ClassifyUnverifiableRun — a rewritten KeyId field must not buy the Warning verdict,
            // and with no provenance evidence NEITHER benign verdict is earned.
            UnverifiableRun? openRun = null;
            string? lastKeyIdSeen = null;
            int maxSuccessionSeen = -1;

            void NoteBroken(int ordinal, string record)
            {
                if (ordinal >= firstBrokenOrdinal) return;
                firstBrokenOrdinal = ordinal;
                firstBrokenRecord = record;
            }

            // A declared link is PROVABLY a resumption point, not a hole where records used to be,
            // when it is either the empty link (the chain restarting from nothing, which no deletion
            // can manufacture because the field is signed) or a signature this walk has actually
            // read (so nothing between it and here is missing from the file).
            //
            // This fences the DELETION shape only, and 2026-08-11 verification measured why that is
            // not enough on its own: a signature proves WHO wrote a record, never WHERE it belongs.
            // A byte-identical copy of an already-signed entry verifies against its own key and its
            // own declared link wherever it is pasted, and its declared link is by definition
            // already in signaturesSeen, so every test above passed a replayed entry straight into
            // the benign RESTARTED arm (measured: a copy of entry 1 appended at the tail gave
            // restarts 1, broken 0, and a verdict reading "do not file this as tampering"). Position
            // is the fact a signature does not carry, so the walk carries it: IsReplay below is the
            // second half of the discriminator, and it is applied BEFORE the restart question.
            bool IsProvableRestart(string declaredPrev) =>
                declaredPrev.Length == 0 || signaturesSeen.Contains(declaredPrev);

            // The same signature read twice means the same signed record appears in the chain more
            // than once. Nothing legitimate writes an entry twice: every genuine record carries its
            // own timestamp and its own predecessor link, so the canonical form it is signed over is
            // unique to its place in the chain.
            bool IsReplay(AuditLogEntry entry) =>
                !string.IsNullOrEmpty(entry.Signature) && signaturesSeen.Contains(entry.Signature!);

            void CloseRun(string? followingKeyId)
            {
                if (openRun == null) return;
                var run = openRun;
                openRun = null;

                var verdict = ClassifyUnverifiableRun(run, followingKeyId, out var reason);
                if (verdict == RunVerdict.Rewritten)
                {
                    brokenCount += run.Count;
                    NoteBroken(run.FirstOrdinal, $"{run.FirstRecord} (KeyId rewritten: {reason})");
                    Serilog.Log.Error(
                        "[AUDIT] Chain BROKEN — {Count} entrie(s) starting at {Record} claim signing key {KeyId}, " +
                        "which cannot be resolved AND cannot be genuine key loss: {Reason}. Rewriting the (unsigned, " +
                        "on legacy entries) KeyId field is how an altered record tries to buy the benign UNVERIFIABLE " +
                        "verdict instead of the tamper verdict. Treated as tampering.",
                        run.Count, run.FirstRecord, run.KeyId, reason);
                    return;
                }

                if (verdict == RunVerdict.Indeterminate)
                {
                    // Neither bucket is earned. Not counted as unverifiable (that bucket carries a
                    // benign claim), not counted as broken (nothing was shown to be wrong), and
                    // NOT learned into the ledger — learning here is what made the round-2 hole
                    // permanent. Succession is not advanced either: an unproven lifetime must not
                    // become the basis on which a LATER run gets refuted by R2.
                    indeterminateCount += run.Count;
                    firstIndeterminateKeyId ??= run.KeyId;
                    keyTransitionFrom ??= run.KeyId;
                    keyTransitionTo ??= followingKeyId ?? _currentKeyId;
                    Serilog.Log.Error(
                        "[AUDIT] Chain INDETERMINATE — {Count} entrie(s) starting at {Record} name signing key " +
                        "{KeyId}, and {Reason}. No claim is made about whether they were altered.",
                        run.Count, run.FirstRecord, run.KeyId, reason);
                    return;
                }

                unverifiableCount += run.Count;
                firstUnresolvableKeyId ??= run.KeyId;
                // Remember the transition this run represents, so ONE lookup after the walk can ask
                // whether the chain carries its own account of it. Deliberately NOT fed into the
                // verdict above: that record is signed with the key that replaced the dead one and
                // lives in the same directory as everything else here, so treating it as evidence
                // FOR the benign verdict would add another forgeable trace to a corroboration story
                // that is already self-referential. It is surfaced, attributed, and left to the
                // reader. Closing that gap properly is the portal co-sign lane's job.
                keyTransitionFrom ??= run.KeyId;
                keyTransitionTo ??= followingKeyId ?? _currentKeyId;
                // Accepted as genuine loss -> remember it, so R1 corroboration survives the archived
                // key file being tidied away later. Gated on acceptance: a refuted id is never learned.
                RememberObservedKeyId(run.KeyId);
                // An accepted lifetime advances succession, so a LATER run reclaiming the same
                // (now superseded) key is refuted by R2.
                maxSuccessionSeen = Math.Max(maxSuccessionSeen, SuccessionIndex(run.KeyId));
            }

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

                        var declaredPrev = entry.PreviousHash ?? string.Empty;

                        // Does the carry-in link from the previous segment disagree with what this
                        // segment's first entry declares? The finding is NOT classified here: whether
                        // it is a link break or a restart depends on whether this entry's own
                        // signature verifies against the link it declares, which is not known until
                        // the key is resolved and the signature recomputed a few lines down.
                        bool carryInMismatch = false;
                        if (firstEntryOfSegment)
                        {
                            firstEntryOfSegment = false;
                            carryInMismatch = crossSegmentSig != null &&
                                !string.Equals(crossSegmentSig, declaredPrev, StringComparison.Ordinal);
                            if (carryInMismatch)
                                Serilog.Log.Warning(
                                    "[AUDIT] Cross-segment link at segment {Seg} does not match the carry-in: expected " +
                                    "PreviousHash={Expected} got {Got}. Classifying it once the entry's own signature is checked.",
                                    Path.GetFileName(segmentPath),
                                    (crossSegmentSig ?? string.Empty) is var carried && carried.Length >= 16
                                        ? carried[..16] : carried,
                                    declaredPrev.Length >= 16 ? declaredPrev[..16] : declaredPrev);

                            // Keep scanning from this segment's declared prev — stopping here would
                            // leave every later entry unexamined, so a deeper tamper could hide
                            // behind an earlier one.
                            previousSig = declaredPrev;
                        }

                        // UNVERIFIABLE ≠ BROKEN. With no key there is no verdict to give — recomputing
                        // against the current key and reporting the guaranteed mismatch as tampering is
                        // exactly the false-positive this split exists to kill. But the run is only
                        // BANKED here; its verdict is decided when the run closes and we can see the
                        // key ids on both sides of it (see CloseRun / ClassifyUnverifiableRun).
                        var record = $"{Path.GetFileName(segmentPath)}@{entry.Timestamp:o}";

                        // Asked BEFORE the key is resolved, because a repeated signature is a fact
                        // about the FILE and not about the key: the same signed record is on disk
                        // twice whether or not this machine can still check it.
                        bool isReplay = IsReplay(entry);
                        if (isReplay)
                        {
                            duplicateEntryCount++;
                            firstDuplicateRecord ??= record;
                            Serilog.Log.Error(
                                "[AUDIT] Chain REPLAY at {Record}: this entry's signature was already read earlier in " +
                                "this chain, so the same signed record appears more than once. A signature shows who " +
                                "wrote a record, not where it belongs, so a copy of a genuine entry verifies wherever " +
                                "it is placed. This is not a restart. Treated as tampering.", record);
                        }

                        if (!TryResolveKeyForEntry(entry, out var entryKey))
                        {
                            // No key means no verdict on this entry, and therefore no way to prove a
                            // restart either: a carry-in mismatch here stays a LINK break.
                            if (carryInMismatch)
                            {
                                linkBreakCount++;
                                firstLinkBreakRecord ??= $"{Path.GetFileName(segmentPath)}@cross-segment-link ({entry.Timestamp:o})";
                                Serilog.Log.Warning(
                                    "[AUDIT] Cross-segment LINK BREAK at {Record}: the entry's signing key is unavailable, " +
                                    "so whether the chain resumed here cannot be shown either way.", record);
                            }

                            if (openRun != null &&
                                !string.Equals(openRun.KeyId, entry.KeyId, StringComparison.Ordinal))
                                CloseRun(entry.KeyId); // a different dead key starts a new lifetime

                            openRun ??= new UnverifiableRun
                            {
                                KeyId = entry.KeyId,
                                PrecedingKeyId = lastKeyIdSeen,
                                FirstOrdinal = count - 1,
                                FirstRecord = record,
                                MaxSuccessionBefore = maxSuccessionSeen
                            };
                            openRun.Count++;
                            lastKeyIdSeen = entry.KeyId;
                            if (entry.Signature != null) signaturesSeen.Add(entry.Signature);
                            previousSig = entry.Signature; // structural carry-forward; still unchecked
                            continue;
                        }

                        var resolvedKeyId = NeighbourKeyId(entry);
                        CloseRun(resolvedKeyId);
                        lastKeyIdSeen = resolvedKeyId;
                        maxSuccessionSeen = Math.Max(maxSuccessionSeen, SuccessionIndex(resolvedKeyId));

                        // Two questions, and until 2026-08-11 only the first was asked:
                        //  Q1 does it verify against the link the WALK carried in?
                        //  Q2 does it verify against the link the ENTRY ITSELF declares?
                        // An entry answering no to Q1 and yes to Q2 was written intact by a holder of
                        // the signing key and has not been edited since (the declared link is inside
                        // the signed canonical form). That is a RESTART, not a tamper — see
                        // ChainVerificationStatus.Restarted for the incident that proved it.
                        var recomputed = ComputeSignature(entry, previousSig!, entryKey);
                        bool matchesCarriedLink = string.Equals(recomputed, entry.Signature, StringComparison.Ordinal);
                        bool matchesOwnLink = matchesCarriedLink ||
                            string.Equals(ComputeSignature(entry, declaredPrev, entryKey), entry.Signature,
                                          StringComparison.Ordinal);

                        if (!matchesOwnLink)
                        {
                            // It verifies against no link at all, its own included. The tamper signal.
                            brokenCount++;
                            NoteBroken(count - 1, record);
                            if (carryInMismatch)
                            {
                                linkBreakCount++;
                                firstLinkBreakRecord ??= $"{Path.GetFileName(segmentPath)}@cross-segment-link ({entry.Timestamp:o})";
                            }
                            // Continue: the remaining entries are still worth examining. Note this
                            // does NOT relax detection — brokenCount > 0 is still a BROKEN verdict.
                        }
                        else if (carryInMismatch || !matchesCarriedLink)
                        {
                            if (isReplay)
                            {
                                // Already counted, and counted as what it is. It is neither a
                                // restart (nothing resumed; a record was duplicated) nor a link
                                // break (no records are missing between the declared link and here),
                                // so it is not counted again under either noun.
                            }
                            else if (IsProvableRestart(declaredPrev))
                            {
                                restartCount++;
                                firstRestartRecord ??= record;
                                Serilog.Log.Warning(
                                    "[AUDIT] Chain RESTART at {Record}: this entry declares a previous-hash link other " +
                                    "than the one the walk carried in, and its own signature verifies against the link " +
                                    "it declares. The entry is sound and so is the one before the gap; what is lost is " +
                                    "the link across the gap. This is not a tamper signal.", record);
                            }
                            else
                            {
                                // Verified against its own key, but resuming from a link that is
                                // nowhere in this chain. Consistent with the entries in between
                                // having been REMOVED, so it keeps the break severity.
                                linkBreakCount++;
                                firstLinkBreakRecord ??= record;
                                Serilog.Log.Error(
                                    "[AUDIT] Chain LINK BREAK at {Record}: the entry verifies against its own signing " +
                                    "key, but the previous-hash link it declares appears nowhere in this chain. Records " +
                                    "between that link and this entry may have been removed.", record);
                            }
                        }

                        if (entry.Signature != null) signaturesSeen.Add(entry.Signature);
                        previousSig = entry.Signature;
                    }

                    crossSegmentSig = previousSig; // carry the last sig into next segment
                    // Only advance the head on a segment that actually yielded an entry: an empty
                    // (or all-blank) trailing segment leaves previousSig null, and taking that
                    // would report "no head" over a chain that plainly has one.
                    if (previousSig != null) headSignature = previousSig;
                }

                // Chain end: nothing follows the last run, so decide it now.
                CloseRun(null);

                if (count > 100_000)
                    Serilog.Log.Warning(
                        "[AUDIT] VerifyChain processed {Count} entries across {Segments} segments — consider archiving old segments",
                        count, segments.Length);
            }
            catch (Exception ex)
            {
                // Error: the run reports a non-intact verdict below, so this is a BROKEN verdict
                // reaching the caller — not a warning.
                Serilog.Log.Error(ex, "[AUDIT] On-demand chain verification failed — reporting BROKEN");
                brokenCount++;
                firstBrokenRecord ??= "verification aborted: " + ex.GetType().Name;
            }

            // Precedence is worst-first, and Indeterminate outranks Unverifiable deliberately: a
            // chain carrying both a known-benign gap and an undecidable one must report the
            // undecidable one, because that is the finding the operator cannot close.
            // One lookup, after the walk, for the chain's own account of the first key transition it
            // could not check across. Guarded so a chain with no unresolvable key never pays for it.
            string? keyReplacementExplanation = null;
            if (keyTransitionFrom != null && keyTransitionTo != null &&
                !string.Equals(keyTransitionFrom, keyTransitionTo, StringComparison.OrdinalIgnoreCase) &&
                HasVerifiedKeyReplacementRecord(keyTransitionFrom, keyTransitionTo))
                keyReplacementExplanation =
                    $"This chain carries its own record of the key transition {keyTransitionFrom} to " +
                    $"{keyTransitionTo}: a signed HmacKeyReplacedUnreadable entry naming both key ids, the identity " +
                    "that performed the replacement and the reason the old key could not be read. That record is " +
                    "signed with the REPLACEMENT key, so it shows the change was not silent; it is not proof that " +
                    "the change was authorised.";

            // A LINK break carries the same weight as a failed entry: it is an unexplained
            // discontinuity, and the deletion shape lands here. So does a REPLAY: a duplicated
            // record is an edit to the log, and it is the shape that was reaching the benign arm.
            // A RESTART does not — it is the lowest non-clean rung, below Unverifiable, because
            // nothing about it is unknown. Restarts still SHOW UP on every worse verdict, through
            // RestartClause, and replays through ReplayClause.
            var status = brokenCount > 0 || linkBreakCount > 0 || duplicateEntryCount > 0
                ? ChainVerificationStatus.Broken
                : indeterminateCount > 0
                    ? ChainVerificationStatus.Indeterminate
                    : unverifiableCount > 0
                        ? ChainVerificationStatus.Unverifiable
                        : restartCount > 0
                            ? ChainVerificationStatus.Restarted
                            : ChainVerificationStatus.Intact;

            // Intact stays STRICT: an unverifiable entry, an indeterminate one and a restart are
            // each short of a clean bill of health.
            var result = new ChainVerificationResult(
                status == ChainVerificationStatus.Intact, count, first, last, DateTime.UtcNow,
                status, brokenCount, unverifiableCount, firstBrokenRecord, firstUnresolvableKeyId,
                indeterminateCount, firstIndeterminateKeyId,
                linkBreakCount, firstLinkBreakRecord, restartCount, firstRestartRecord,
                keyReplacementExplanation, duplicateEntryCount, firstDuplicateRecord,
                headSignature);
            // Published to LastVerification BEFORE LogChainVerified, so the cached result is the
            // one that describes the chain as this walk read it — the entry LogChainVerified is
            // about to enqueue is not part of what was measured, and pretending otherwise would
            // make the cached entryCount off by one against its own head signature.
            lock (_writeLock) { _lastVerification = result; }
            LogChainVerified(result, triggeredBy ?? Environment.UserName);
            return result;
        }

        // ── The startup banners, composed as ONE state ───────────────────────────

        /// <summary>Which of the three startup findings a banner reports.</summary>
        public enum StartupChainFinding
        {
            /// <summary>A chain-integrity FAILURE: a signature mismatch, a rewritten
            /// <see cref="AuditLogEntry.KeyId"/>, or an out-of-band truncation finding.</summary>
            Break,
            /// <summary>Provenance could not be determined — see <see cref="ChainProvenanceIndeterminate"/>.</summary>
            ProvenanceIndeterminate,
            /// <summary>A known-benign evidence gap — see <see cref="ChainUnverifiable"/>.</summary>
            UnverifiableGap,
            /// <summary>The chain resumed after a break — see <see cref="ChainRestarted"/>.
            /// APPENDED, like every member above it.</summary>
            Restart
        }

        /// <summary>
        /// One startup banner, with EVERY CHAIN-WIDE sentence it may print — opening and closing
        /// alike — chosen against every other finding on the same launch. <see cref="ClosingLead"/>
        /// is the emphasised closing clause and <see cref="ClosingDetail"/> the rest; the page
        /// renders the per-finding facts (key ids, record ids, causes) around them from its own
        /// properties, because those are scoped to the finding and are true whatever else was found.
        /// </summary>
        /// <remarks>
        /// ROUND 9 (2026-08-02) added <see cref="Headline"/>. Round 8 composed the CLOSINGS and left
        /// the OPENINGS as markup literals, and the INDETERMINATE banner's opening was the same
        /// defect one sentence higher up: "Audit chain integrity cannot be determined" is an
        /// unscoped chain-wide claim, false on the very launch whose scan DETERMINED the integrity
        /// to be broken (<c>ChainBroken=True, ChainProvenanceIndeterminate=True</c>) — and it is the
        /// quotable half, the one an operator reads first and repeats to an auditor.
        /// <para>
        /// The two sibling openings were audited in the same pass and are NOT composed, for stated
        /// reasons rather than by omission:
        /// <list type="bullet">
        /// <item><b>Break</b> — "Audit chain integrity check failed" is chain-wide but POSITIVE: it
        /// is true on every launch that renders it, and no other finding can falsify it. It is
        /// composed here anyway, so that no chain-wide claim of any polarity is left in markup
        /// where a test cannot reach it.</item>
        /// <item><b>UnverifiableGap</b> — its opening is SCOPED BY CONSTRUCTION ("Some audit entries
        /// are unverifiable… their integrity can be neither confirmed nor denied"): the negative
        /// attaches to the named entries, not to the chain, so a break beside it cannot make it
        /// false. It also interleaves the key id and record id it is about, and round 8's rule is
        /// that per-finding FACTS stay on the page. It is left in markup and held there by
        /// <c>AuditLogStartupBannerTests.ThePage_KeepsOnlyScopedOpenings</c>, which asserts both the
        /// scoped wording and the absence of any unscoped variant.</item>
        /// </list>
        /// </para>
        /// </remarks>
        public sealed record StartupChainBanner(
            StartupChainFinding Finding,
            string Severity,
            string Headline,
            string ClosingLead,
            string ClosingDetail)
        {
            /// <summary>The closing claim as one plain-text string, for assertions and for logs.</summary>
            public string ClosingClaim =>
                string.IsNullOrEmpty(ClosingLead) ? ClosingDetail : ClosingLead + " — " + ClosingDetail;

            /// <summary>Every chain-wide sentence this banner prints, opening and closing, as one
            /// string — so a rule about what may not be said is applied to the whole banner and
            /// cannot be satisfied by moving a sentence from the closing to the opening.</summary>
            public string AllClaims => (Headline + " " + ClosingClaim).Trim();
        }

        /// <summary>
        /// The startup chain state, composed as ONE state rather than three independent findings.
        /// </summary>
        /// <remarks>
        /// THE DEFECT THIS EXISTS TO FIX (2026-08-02 round 8; pre-existing, reproduced at 9825fb5).
        /// <c>Pages/AuditLogViewer.razor</c> rendered the three startup findings as three independent
        /// <c>@if</c> blocks, so more than one could render at once — and each of the two quieter
        /// banners closed with a CHAIN-WIDE NEGATIVE that is only true when its own finding is the
        /// only one. Every previous round enumerated SURFACES; nobody enumerated COMBINATIONS.
        /// <para>
        /// MEASURED, on one launch over one set of bytes each (2026-08-02):
        /// (1) a benign key-loss run (unresolvable KeyId + a real-but-unreadable DPAPI archive) plus
        /// an ordinary tamper of a later entry gives <c>ChainBroken=True ChainUnverifiable=True</c>,
        /// and the amber banner printed "No entry with an available signing key was found to have
        /// been altered" directly beneath the red break alert — false on those bytes: the tampered
        /// entry's key resolved and its signature failed.
        /// (2) an unreadable anchor plus an ordinary tamper gives <c>ChainBroken=True
        /// ChainProvenanceIndeterminate=True</c>, and the red INDETERMINATE banner printed "Whether
        /// the chain was altered can be neither confirmed nor ruled out" — also false: the mismatch
        /// on the same scan confirmed it.
        /// </para>
        /// <para>
        /// THE SHAPE CHOSEN, and why. The three banners are kept as three, because each reports a
        /// distinct FINDING with facts that are true regardless of what else was found (which key id
        /// went dark, which record failed first, what the anchor could not tell us) — collapsing them
        /// into one verdict line would delete evidence the operator needs, and the page already has a
        /// single folded VERDICT for the whole chain in <see cref="DescribeChainForCompliance"/>
        /// behind Verify Chain. What is composed here is the part that was actually wrong: the
        /// chain-wide claim each banner closes with is chosen with every finding in view, so a
        /// negative about the chain cannot survive beside a break. Findings are per-finding; claims
        /// about the chain are composed once.
        /// </para>
        /// <para>
        /// It lives here, not in the markup, for the same reason
        /// <see cref="ComposeChainVerificationReport"/> does: a sentence inside a Razor page is not
        /// reachable by a test, and these two sentences survived seven rounds of review inside one.
        /// </para>
        /// </remarks>
        public IReadOnlyList<StartupChainBanner> ComposeStartupChainBanners() =>
            ComposeStartupChainBanners(ChainBroken, ChainProvenanceIndeterminate, ChainUnverifiable,
                                       ChainRestarted);

        /// <summary>
        /// The pure composition, so every combination of the three flags can be exercised without
        /// having to reach it through on-disk bytes first.
        /// </summary>
        internal static IReadOnlyList<StartupChainBanner> ComposeStartupChainBanners(
            bool broken, bool provenanceIndeterminate, bool unverifiableGap, bool restarted = false)
        {
            var banners = new List<StartupChainBanner>(4);

            // The break banner states facts only (first failing record, cause) — it closes with no
            // chain-wide claim, so there is nothing here for another finding to falsify. Its
            // OPENING is chain-wide, but positive and true on every launch that renders it; it is
            // composed here so that the page holds no chain-wide sentence of either polarity.
            if (broken)
                banners.Add(new StartupChainBanner(
                    StartupChainFinding.Break, "error",
                    "Audit chain integrity check failed.",
                    string.Empty, string.Empty));

            if (provenanceIndeterminate)
                banners.Add(broken
                    ? new StartupChainBanner(
                        StartupChainFinding.ProvenanceIndeterminate, "error",
                        // SCOPED. Beside a break the integrity of this chain WAS determined — it
                        // failed — so the opening may only claim what this finding actually leaves
                        // open: provenance. True on both routes into the flag (a signing key that
                        // resolves to nothing with no ledger to corroborate it, and an anchor that
                        // is present but unreadable), and true whether or not truncation was also
                        // detected, which is why it says nothing about completeness.
                        "Audit chain provenance cannot be determined.",
                        "This is not a clean result and it does not soften the failure above",
                        "the audit chain integrity check on this same launch FAILED, on evidence this gap does " +
                        "not cover. What can be neither confirmed nor ruled out here is whether anything BEYOND " +
                        "that finding was altered or removed. Do not record the affected range as verified, and " +
                        "do not close this as a key-management event without independent evidence.")
                    : new StartupChainBanner(
                        StartupChainFinding.ProvenanceIndeterminate, "error",
                        // ALONE this is the honest headline and it is kept verbatim: nothing else
                        // was determined on this launch, so integrity as a whole really is open.
                        "Audit chain integrity cannot be determined.",
                        "Whether the chain was altered can be neither confirmed nor ruled out",
                        "this is not a clean result and it is not evidence of tampering. Do not close it as a " +
                        "key-management event without the evidence."));

            // The UnverifiableGap opening stays on the page: it is scoped to the entries it names
            // and interleaves their key id and record id. See the remarks on StartupChainBanner.
            if (unverifiableGap)
                banners.Add(broken
                    ? new StartupChainBanner(
                        StartupChainFinding.UnverifiableGap, "warning",
                        string.Empty,
                        "This is not a clean result",
                        "the audit chain integrity check on this same launch FAILED. That finding rests on " +
                        "evidence outside this gap, so nothing in this notice bears on it and nothing here says " +
                        "the chain is unaltered. The unverifiable entries themselves were never checked, so " +
                        "nothing is claimed about their contents either way.")
                    : new StartupChainBanner(
                        StartupChainFinding.UnverifiableGap, "warning",
                        string.Empty,
                        "No entry with an available signing key was found to have been altered",
                        "the unverifiable entries themselves were never checked, so nothing is claimed about " +
                        "their contents either way. The usual cause is that the signing key was replaced."));

            // The restart headline is SCOPED to the restart points it names, so a break beside it
            // cannot make it false. What is composed against the other findings is the closing
            // claim: "not evidence of tampering" is true OF THE RESTART on every launch, and would
            // be read as a claim about the chain if a break were also on screen.
            if (restarted)
                banners.Add(broken
                    ? new StartupChainBanner(
                        StartupChainFinding.Restart, "warning",
                        "The audit chain resumed after a break.",
                        "This is not a clean result",
                        "the entries on both sides of each restart point verified against their own signing keys, " +
                        "so nothing in this notice bears on the integrity check that FAILED on this same launch. " +
                        "What this notice covers is only the missing LINK across each gap: the records cannot be " +
                        "shown to be contiguous there.")
                    : new StartupChainBanner(
                        StartupChainFinding.Restart, "warning",
                        "The audit chain resumed after a break.",
                        "This is not a clean result and it is not evidence of tampering",
                        "the entries on both sides of each restart point verified against their own signing keys. " +
                        "What is lost is the link across the gap, so the records cannot be shown to be contiguous " +
                        "there. The usual cause is that the service stopped mid-write, or that an earlier build " +
                        "resumed writing from a stale link."));

            return banners;
        }

        /// <summary>The composed banner for one finding, or null when that finding did not fire.</summary>
        public StartupChainBanner? StartupBannerFor(StartupChainFinding finding) =>
            ComposeStartupChainBanners().FirstOrDefault(b => b.Finding == finding);

        /// <summary>
        /// The chain statement a CLIENT-FACING compliance artifact prints. One sentence, scoped, and
        /// by construction unable to print a BETTER verdict than <see cref="VerifyChain"/> reached
        /// over the same bytes. (It may print a worse one — the fold is escalate-only and names the
        /// out-of-band anchor evidence VerifyChain cannot see. "Cannot disagree" is what this said
        /// until round 5; measurement falsified it twice, and the guarantee that survives is the
        /// one-directional one.)
        /// </summary>
        /// <remarks>
        /// THE DEFECT THIS EXISTS TO FIX (2026-08-01 round 4; pre-existing, reproduced on 791d2cb).
        /// The Audit Evidence bundle used to read the STARTUP flags — <see cref="ChainBroken"/>,
        /// <see cref="ChainUnverifiable"/>, <see cref="ChainProvenanceIndeterminate"/> — and
        /// <see cref="VerifyChainOnStartup"/> walks ONLY the most-recent segment. That gave: startup
        /// flags all false, so the client-facing artifact printed a bare "Intact", while the same
        /// launch's <see cref="VerifyChain"/> reported UNVERIFIABLE with most of the chain unchecked.
        /// A compliance artifact asserting integrity over a chain that is demonstrably not intact is
        /// the exact class of false statement the four-state verdict was built to stop, and it was
        /// live on the estate.
        /// <para>
        /// THE MEASUREMENT, stated once and dated. Source: a read-only, byte-verified copy of the
        /// installed service's own audit-log directory (<c>C:\SQLTriage-Service\audit-logs</c>),
        /// taken 2026-08-01 (UTC). On that copy the artifact printed "Intact" while VerifyChain on
        /// the same launch reported UNVERIFIABLE with <b>357 of 363 retained entries unchecked</b>.
        /// It is a SNAPSHOT of a live, still-growing chain and nothing else: earlier notes on this
        /// branch quote 347-of-350 and 351-of-355 from copies taken hours apart the same day. Those
        /// are the same defect measured at three chain lengths, not three findings — do not treat
        /// the differing numbers as a discrepancy, and do not restate any of them undated.
        /// </para>
        /// <para>
        /// A bundle is generated on demand and is nowhere near a hot path, so the artifact runs its
        /// own FULL verification rather than reading a partial startup scan. The result is folded
        /// with the out-of-band anchor findings, which <see cref="VerifyChain"/> structurally cannot
        /// see: a truncated chain whose surviving entries all verify is still not intact, and an
        /// unreadable anchor means truncation can be neither detected nor ruled out. Folding only
        /// ever escalates — the artifact can print a WORSE verdict than VerifyChain (with the extra
        /// evidence named), never a better one.
        /// </para>
        /// <para>
        /// Verification is itself audited: the <c>AuditChainVerified</c> event names the artifact as
        /// the trigger, so the chain records that this document's claim was checked when it was made.
        /// </para>
        /// </remarks>
        public ComplianceChainStatement DescribeChainForCompliance(string triggeredBy)
        {
            var result = VerifyChain(triggeredBy);
            var (status, detail) = FoldWithAnchorFindings(result);

            var label = StatusLabelFor(status);

            // The scope is stated on EVERY verdict, including Intact. An unqualified "Intact" is what
            // the partial-scan defect printed, and a reader cannot weigh a verdict without its scope.
            var text =
                $"{label} — full-chain verification of all {result.EntryCount} retained entries across every " +
                $"segment, run when this document was generated ({result.VerifiedAt:yyyy-MM-dd HH:mm:ss}Z). {detail} " +
                "Scope of the attestation: the stored records reconcile (or do not) against the signing keys and " +
                "checkpoint held in this installation's audit-log directory. It is not an attestation against an " +
                "attacker holding write access to that directory — see the incident-response runbook, " +
                "§ What this chain does and does not guarantee.";

            return new ComplianceChainStatement(status, label, text, result.EntryCount, detail, result);
        }

        /// <summary>
        /// Folds a raw <see cref="VerifyChain"/> result with the out-of-band anchor findings from the
        /// last startup check. ONE definition of the fold, so nothing can produce a "folded" verdict
        /// that differs from the one on the client-facing artifact.
        /// <para>
        /// The fold ESCALATES ONLY. <see cref="VerifyChain"/> reads only what is still on disk, so
        /// removed entries re-verify clean and the anchor is the sole witness that anything was
        /// taken; an unreadable anchor means truncation can be neither detected nor ruled out. Both
        /// are findings VerifyChain structurally cannot reach, and neither can make a verdict better.
        /// </para>
        /// </summary>
        private (ChainVerificationStatus Status, string Detail) FoldWithAnchorFindings(
            ChainVerificationResult result)
        {
            var status = result.Status;
            var detail = result.StatusDetail;

            if (!string.IsNullOrEmpty(ChainTruncationDetail))
            {
                status = ChainVerificationStatus.Broken;
                detail += " In addition, the out-of-band tail checkpoint does not reconcile with the log: " +
                          ChainTruncationDetail + ". Entries appear to have been REMOVED — a chain whose " +
                          "surviving entries all verify is still not intact.";
            }
            else if (!string.IsNullOrEmpty(ChainIndeterminateDetail) &&
                     status != ChainVerificationStatus.Broken &&
                     status != ChainVerificationStatus.Indeterminate)
            {
                status = ChainVerificationStatus.Indeterminate;
                detail += " In addition, " + ChainIndeterminateDetail +
                          ". This report therefore makes no claim that the chain is complete.";
            }

            return (status, detail);
        }

        /// <summary>
        /// The verdict a client-facing artifact WOULD PRINT for <paramref name="result"/>: the raw
        /// walk folded with this installation's out-of-band anchor findings, through the one
        /// definition of the fold above.
        /// <para>
        /// EXISTS BECAUSE A RAW VERDICT ESCAPED. The chain-head block published off-box on
        /// 2026-08-12 read <c>AuditLogService.StatusWordFor(result.Status)</c> — the unfolded value
        /// — so a chain truncated after its last flush published INTACT while this box's own
        /// compliance statement and its own on-chain AuditChainVerified entry both said BROKEN. The
        /// one surface whose entire purpose is off-box corroboration was the only one reporting a
        /// verdict kinder than the box held. Verified by exercise before and after the fix (the
        /// truncation probe, and <c>The_published_verdict_is_the_folded_one…</c> in the portal
        /// suite).
        /// </para>
        /// <para>
        /// The fold needs INSTANCE state (<see cref="ChainTruncationDetail"/>,
        /// <see cref="ChainIndeterminateDetail"/>), which is why a pure composer cannot do it and
        /// has to be handed the answer. Escalating only, exactly as the fold is: this can return a
        /// worse status than <paramref name="result"/> carries, never a better one.
        /// </para>
        /// </summary>
        public ChainVerificationStatus FoldedStatusFor(ChainVerificationResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            return FoldWithAnchorFindings(result).Status;
        }

        /// <summary>
        /// The all-caps verdict word for a status. THE ONLY ONE: <see cref="ChainVerificationResult.StatusLabel"/>
        /// and <see cref="ComplianceChainStatement.StatusWord"/> both read this switch, so every
        /// surface reads identically and a new status cannot be named on one of them only.
        /// </summary>
        /// <remarks>
        /// Public so a test can ENUMERATE it against <see cref="Enum.GetValues{TEnum}()"/> rather
        /// than assert a hand-written list of arms. The 2026-08-11 verification found RESTARTED
        /// missing from a second copy of this switch while a source-text control that asserted six
        /// substrings stayed green, because a substring list cannot notice the arm it was not told
        /// about.
        /// </remarks>
        public static string StatusWordFor(ChainVerificationStatus status) => status switch
        {
            ChainVerificationStatus.Intact => "INTACT",
            ChainVerificationStatus.Unverifiable => "UNVERIFIABLE",
            ChainVerificationStatus.Indeterminate => "INDETERMINATE",
            ChainVerificationStatus.Restarted => "RESTARTED",
            _ => "BROKEN"
        };

        /// <summary>
        /// The mixed-case label for a status, as printed on a client-facing document and as
        /// lower-cased into the Audit Evidence bundle's CSS class (<c>chain-&lt;label&gt;</c>).
        /// </summary>
        /// <remarks>
        /// Lifted out of <see cref="DescribeChainForCompliance"/> on 2026-08-11 so the bundle's
        /// stylesheet can be checked against EVERY status the enum can produce instead of against a
        /// hard-coded list of class names. Measured that day: a restart-only chain emitted
        /// <c>class="chain-restarted"</c> into a document whose stylesheet had no such rule, so the
        /// verdict cell rendered as plain body text, which is the same defect the 2026-08-01 round
        /// closed for the two middle verdicts.
        /// </remarks>
        public static string StatusLabelFor(ChainVerificationStatus status) => status switch
        {
            ChainVerificationStatus.Intact => "Intact",
            ChainVerificationStatus.Unverifiable => "Unverifiable",
            ChainVerificationStatus.Indeterminate => "INDETERMINATE",
            ChainVerificationStatus.Restarted => "Restarted",
            _ => "Broken"
        };

        /// <summary>
        /// The severity the chain's own <c>AuditChainVerified</c> record carries for a status.
        /// A missing key is NOT an attack: filing it as Critical alongside real tampering is what
        /// made the signal worthless, and Warning keeps it visible without crying wolf.
        /// </summary>
        /// <remarks>
        /// Lifted out of <see cref="LogChainVerified"/> on 2026-08-11 so a test can drive it once
        /// per status instead of scanning the source for an arm. The default is Critical, so a
        /// status this switch does not name is filed as tampering by omission, which is the thing
        /// worth measuring rather than trusting.
        /// <list type="bullet">
        /// <item><b>Indeterminate is Error, not Critical</b> — an operator triaging by severity must
        /// not be able to file "we cannot tell whether this was altered" alongside "a service
        /// account changed", and nothing here is evidence of tampering. Crying tamper on missing
        /// evidence is the failure mode the whole UNVERIFIABLE split exists to prevent.</item>
        /// <item><b>Restarted is Warning</b>, at the same rung as an evidence gap and deliberately
        /// below the tamper signal: every entry examined verified against its own key, so nothing
        /// there is evidence of an edit. It is not Info either, because a chain that cannot be shown
        /// to be contiguous is a compliance finding.</item>
        /// </list>
        /// </remarks>
        public static AuditSeverity SeverityFor(ChainVerificationStatus status) => status switch
        {
            ChainVerificationStatus.Intact => AuditSeverity.Info,
            ChainVerificationStatus.Unverifiable => AuditSeverity.Warning,
            ChainVerificationStatus.Indeterminate => AuditSeverity.Error,
            ChainVerificationStatus.Restarted => AuditSeverity.Warning,
            _ => AuditSeverity.Critical
        };

        /// <summary>
        /// A full-scope, self-consistent chain verdict for a client-facing document.
        /// <see cref="Text"/> is the whole sentence to print; <see cref="Label"/> is the single word.
        /// <para>
        /// <see cref="Result"/> is the RAW <see cref="VerifyChain"/> result the verdict was folded
        /// from, exposed so an artifact can print the per-entry counts without re-running
        /// verification (a second run is a second set of bytes and a second chance to disagree).
        /// Anything printing a VERDICT must use <see cref="Status"/>/<see cref="StatusWord"/>/
        /// <see cref="Detail"/>, never <c>Result.StatusLabel</c> — the raw result has not been
        /// folded with the out-of-band anchor findings and is exactly what printed "INTACT" over a
        /// truncated chain.
        /// </para>
        /// </summary>
        public sealed record ComplianceChainStatement(
            ChainVerificationStatus Status,
            string Label,
            string Text,
            int EntryCount,
            string Detail,
            ChainVerificationResult Result)
        {
            /// <summary>
            /// The all-caps verdict word, matching <see cref="ChainVerificationResult.StatusLabel"/>
            /// so the exported report and the on-screen banner read identically.
            /// </summary>
            /// <remarks>
            /// THE DEFECT THIS FIXES (2026-08-11 verification). This was its own copy of the switch
            /// and it had four arms where the other two copies had five, so a RESTARTED chain
            /// reached this property and fell through to <c>BROKEN</c>. Measured on a three-entry
            /// chain written by the production writer with a planted mid-segment restart:
            /// <c>Status=Restarted Label=Restarted StatusWord=BROKEN</c>. That value is what the
            /// sealed, SHA-256-anchored verification report prints as "Chain status", two lines
            /// above its own status detail reading "do not file this as tampering"; it is what the
            /// audit page prints as "Chain BROKEN" inside an AMBER panel; and it is what the chain's
            /// own export record stores as <c>Status</c> beside <c>UnfoldedStatus=RESTARTED</c>.
            /// One switch now, in <see cref="StatusWordFor"/>, for exactly that reason.
            /// </remarks>
            public string StatusWord => StatusWordFor(Status);
        }

        /// <summary>How one key-material file on disk is actually wrapped, right now.</summary>
        /// <param name="FileName">Bare file name inside the audit-log directory.</param>
        /// <param name="Scope">Human-readable wrapping, READ from the blob — never asserted.</param>
        public sealed record KeyMaterialScope(string FileName, string Scope);

        /// <summary>
        /// What every key-material file in this installation's audit-log directory is ACTUALLY
        /// wrapped with, determined by reading each blob.
        /// </summary>
        /// <remarks>
        /// THE DEFECT THIS EXISTS TO FIX (2026-08-01 round 5). The exported, SHA-256-sealed
        /// verification report printed a flat constant — "HMAC key wrapped: DPAPI (LocalMachine
        /// scope)" — swapped in from "(CurrentUser scope)" by <c>e2eb6b9</c> when the WRITE path
        /// changed. It was false on the estate the day it shipped. Measured 2026-08-01 on the
        /// installed service's own directory (flags DWORD at byte 40, bit 0x4): <c>hmac.key</c>,
        /// both <c>hmac.key.&lt;id&gt;</c> archives and <c>.chain-anchor</c> all carry
        /// <c>flags=0x00000000</c>, i.e. CURRENT_USER. That is not a contradiction of the round-2
        /// write path — that install's blobs were written by a pre-round-2 binary — and it is
        /// exactly the point: EVERY existing installation's blobs predate the change, and each one
        /// upgrades on its own path, at its own time, or not at all. A constant was therefore false
        /// on every deployed install, on a document handed to an auditor, and materially so:
        /// LocalMachine is precisely the claim that a service-account change can no longer orphan
        /// these keys — the incident this branch exists to remove.
        /// <para>
        /// Wrapping is a property of the BLOB ON DISK, not of the build. Legacy blobs upgrade
        /// per-file, on paths that need the identity that wrote them to still be running (see the
        /// runbook's per-file table), so at any moment the files may disagree with each other and
        /// with the writer's intent. The scope is knowable at runtime, so a sealed artifact states
        /// what it read.
        /// </para>
        /// <para>
        /// Non-Windows installs write raw key bytes and no DPAPI is involved; that is reported as
        /// such rather than silently rendered as a scope.
        /// </para>
        /// </remarks>
        public IReadOnlyList<KeyMaterialScope> DescribeKeyMaterialScopes()
        {
            var paths = new List<string> { _keyPath };
            try
            {
                paths.AddRange(Directory
                    .GetFiles(_logDirectory, "hmac.key.*")
                    // Win32 wildcard matching: "hmac.key.*" also returns "hmac.key" itself
                    // (a trailing ".*" matches an absent extension). Drop it — it is already row 1.
                    .Where(p => !string.Equals(p, _keyPath, StringComparison.OrdinalIgnoreCase))
                    // hmac.key.meta is a plaintext JSON sidecar, not key material.
                    .Where(p => !string.Equals(Path.GetFileName(p), HmacMetaFileName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            }
            catch { /* directory unreadable — the per-file rows below say so */ }
            paths.Add(ChainAnchorPath);

            var rows = new List<KeyMaterialScope>(paths.Count);
            foreach (var path in paths)
                rows.Add(new KeyMaterialScope(Path.GetFileName(path), DescribeWrappingOf(path)));
            return rows;
        }

        /// <summary>
        /// Reads one blob and says how it is wrapped. Every branch reports what was OBSERVED; there
        /// is deliberately no default that names a scope, because "we could not tell" and "it is
        /// LocalMachine" are different facts and collapsing them is the defect being fixed.
        /// </summary>
        /// <summary>
        /// The exact scope string a MEASURED CurrentUser blob produces. Named so that the only
        /// consumer of "is anything CurrentUser-scoped here?" (the sealed report's footnote) tests
        /// the same value <see cref="DescribeWrappingOf"/> emits, rather than re-deriving the fact
        /// from a second read or matching a substring that a reworded neighbour could also hit.
        /// </summary>
        internal const string CurrentUserScopeText =
            "DPAPI, CurrentUser scope - bound to the Windows identity that wrote it";

        private static string DescribeWrappingOf(string path)
        {
            byte[] blob;
            try
            {
                if (!File.Exists(path)) return "absent";
                blob = File.ReadAllBytes(path);
            }
            catch (Exception ex) { return $"present, could not be read ({ex.GetType().Name})"; }

            if (!OperatingSystem.IsWindows())
                return $"not DPAPI-wrapped (non-Windows install, {blob.Length} raw bytes)";
            if (!LooksLikeDpapiBlob(blob))
                return blob.Length == 32
                    ? "NOT WRAPPED - 32 raw key bytes on disk"
                    : $"not DPAPI-wrapped ({blob.Length} bytes, unrecognised)";
            // The scope lives in the flags DWORD at byte 40. LooksLikeDpapiBlob only needs the
            // first 20 bytes, so a blob can be DPAPI-SHAPED and still too short to carry a scope
            // at all — measured with a 20-byte header-only file, which reported "CurrentUser"
            // because IsLocalMachineWrapped returns false for anything it cannot read. That
            // conservative false is right for the re-wrap decision and wrong as a statement of
            // fact on a sealed document. Say nothing about scope here.
            if (blob.Length < DpapiFlagsOffset + sizeof(uint))
                return $"DPAPI-shaped, scope not readable ({blob.Length} bytes - too short to carry the scope flags)";
            return IsLocalMachineWrapped(blob)
                ? "DPAPI, LocalMachine scope"
                : CurrentUserScopeText;
        }

        /// <summary>
        /// Composes the body of the exported, SHA-256-sealed "Audit Chain Verification Report" —
        /// everything above the hash line. The caller seals it and writes it out.
        /// </summary>
        /// <remarks>
        /// 2026-08-01 round 5. This text used to live in <c>Pages/AuditLogViewer.razor</c>, where it
        /// printed a RAW <see cref="VerifyChain"/> result. Measured 2026-08-01 at <c>dc9d4dc</c>,
        /// same launch, same bytes: a truncated chain with its anchor in place gave the Audit
        /// Evidence bundle <c>chain-broken</c> / "Broken" while this report printed
        /// <c>Chain status:     INTACT</c>; an unreadable anchor over an untouched chain gave the
        /// bundle <c>chain-indeterminate</c> while this report again printed INTACT. Two documents,
        /// one build, one launch, opposite claims — and the one printing INTACT is the one that gets
        /// sealed and handed over.
        /// <para>
        /// It lives here now so that a single method — <see cref="DescribeChainForCompliance"/> —
        /// owns every client-facing verdict, and so this artifact's wording is reachable by a test
        /// (a private method on a Razor page is not). The caller passes the statement it already
        /// showed on screen, so the banner and the sealed document cannot be produced from two
        /// different verification runs.
        /// </para>
        /// </remarks>
        public string ComposeChainVerificationReport(ComplianceChainStatement statement, string actor)
        {
            ArgumentNullException.ThrowIfNull(statement);
            var v = statement.Result;
            var body = new StringBuilder();

            body.AppendLine("SQLTriage Audit Chain Verification Report");
            body.AppendLine("=========================================");
            body.AppendLine($"Generated:        {v.VerifiedAt:o}");
            body.AppendLine($"Generated by:     {actor}");
            body.AppendLine($"Entries examined: {v.EntryCount}");
            body.AppendLine($"Entries verified: {v.VerifiedCount}");
            body.AppendLine($"Unverifiable:     {v.UnverifiableCount}" +
                            (v.FirstUnresolvableKeyId is null ? "" : $"  (first unresolvable key id: {v.FirstUnresolvableKeyId})"));
            body.AppendLine($"Indeterminate:    {v.IndeterminateCount}" +
                            (v.FirstIndeterminateKeyId is null ? "" : $"  (first unresolvable key id: {v.FirstIndeterminateKeyId})"));
            body.AppendLine($"Signature failed: {v.BrokenCount}" +
                            (v.FirstBrokenRecord is null ? "" : $"  (first: {v.FirstBrokenRecord})"));
            // Two rows a reader could otherwise only get by subtraction, and the subtraction was
            // wrong: link breaks used to be added into "Signature failed" above, where they read as
            // entries that failed against their own key.
            body.AppendLine($"Link breaks:      {v.LinkBreakCount}" +
                            (v.FirstLinkBreakRecord is null ? "" : $"  (first: {v.FirstLinkBreakRecord})") +
                            "  (a break BETWEEN entries, not a failed entry)");
            body.AppendLine($"Chain restarts:   {v.RestartCount}" +
                            (v.FirstRestartRecord is null ? "" : $"  (first: {v.FirstRestartRecord})") +
                            "  (counted in Entries verified; see RESTARTED below)");
            body.AppendLine($"Duplicated:       {v.DuplicateEntryCount}" +
                            (v.FirstDuplicateRecord is null ? "" : $"  (first: {v.FirstDuplicateRecord})") +
                            "  (the same signed record present more than once; counted in Entries verified)");
            body.AppendLine($"First entry:      {(v.FirstEntry.HasValue ? v.FirstEntry.Value.ToString("o") : "N/A")}");
            body.AppendLine($"Last entry:       {(v.LastEntry.HasValue ? v.LastEntry.Value.ToString("o") : "N/A")}");
            // Rendered and read before shipping: on a truncated chain the counts above are all
            // clean ("3 examined, 3 verified, 0 failed") under a BROKEN verdict, because they
            // describe the records STILL PRESENT. Without this line a reader can take the counts
            // as the finding and the verdict as an error.
            body.AppendLine("  (The counts above describe the records still present in the log. A verdict worse than");
            body.AppendLine("   those counts suggest comes from out-of-band evidence, named in the status detail.)");
            // The FOLDED verdict, not v.StatusLabel. See the remarks above: the raw label is what
            // printed INTACT over a chain records had been removed from.
            body.AppendLine($"Chain status:     {statement.StatusWord}");
            body.AppendLine($"Status detail:    {statement.Detail}");
            body.AppendLine("HMAC algorithm:   SHA-256");
            body.AppendLine();
            // MEASURED per file, never asserted — see DescribeKeyMaterialScopes.
            body.AppendLine("Key material, as wrapped on disk at the time of this report:");
            var scopes = DescribeKeyMaterialScopes();
            foreach (var row in scopes)
                body.AppendLine($"  {row.FileName,-40}{row.Scope}");
            // GATED, and the gate is the table immediately above: this footnote prints only when a
            // row in THAT table was measured CurrentUser-scoped.
            //
            // THE DEFECT THIS FIXES (2026-08-01 round 6; introduced by round 5's own fix, 8f04748).
            // The three lines here were unconditional, and each of the three over-claimed:
            //   · they printed under a BROKEN verdict on an ordinary-tamper chain, and on a fresh
            //     install holding ZERO CurrentUser blobs — describing evidence that did not exist;
            //   · "and it is not tampering" is a judgement this code never established. It sat
            //     eleven lines above INDETERMINATE's own "do not close this as a key-management
            //     event without the evidence", contradicting it on the same sealed page;
            //   · it named UNVERIFIABLE as the outcome, where the deployed shape's measured verdict
            //     was INDETERMINATE (376 entries: 373 indeterminate, 0 unverifiable, 0 broken).
            // So: gated on the measurement, no verdict named, no exculpation. What survives is the
            // definition of the scope that was just read off the blob, and the consequence of
            // losing the identity it names — neither of which asserts anything about THIS chain.
            //
            // ROUND 8 (2026-08-02), non-blocking tightening. The consequence is stated PER ROW-KIND,
            // because the anchor is not key material. Measured on a chain whose verdict was INTACT
            // and whose only CurrentUser row was `.chain-anchor`: the footnote said "entries signed
            // with that key can no longer be checked", which is vacuously true — the anchor signs no
            // entries — while the consequence that actually follows, loss of TRUNCATION DETECTION,
            // went unsaid. Not false, so it was not blocking; useless to the reader, so it is fixed.
            var anchorFileName = Path.GetFileName(ChainAnchorPath);
            var currentUserRows = scopes
                .Where(r => string.Equals(r.Scope, CurrentUserScopeText, StringComparison.Ordinal))
                .ToList();
            var currentUserKeyRows = currentUserRows
                .Where(r => !string.Equals(r.FileName, anchorFileName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (currentUserRows.Count > 0)
            {
                body.AppendLine("  At least one blob above is wrapped CurrentUser: it is readable only by the Windows");
                body.AppendLine("  identity that wrote it. Should that identity cease to be available on this machine:");
                if (currentUserKeyRows.Count > 0)
                {
                    body.AppendLine("   - the KEY MATERIAL it protects cannot be read here, and entries signed with that");
                    body.AppendLine("     key can no longer be checked. See the status meanings below for how this report");
                    body.AppendLine("     treats entries it could not check.");
                }
                if (currentUserRows.Count != currentUserKeyRows.Count)
                {
                    body.AppendLine($"   - the out-of-band tail checkpoint ({anchorFileName}) cannot be read here. It signs no");
                    body.AppendLine("     entries, so no entry becomes uncheckable by its loss; what is lost is TRUNCATION");
                    body.AppendLine("     DETECTION - whether records were removed from the log could then be neither");
                    body.AppendLine("     detected nor ruled out. See INDETERMINATE in the status meanings below.");
                }
            }
            body.AppendLine();
            body.AppendLine("Status meanings:");
            body.AppendLine("  INTACT       - every entry was recomputed against its own signing key and matched, and");
            body.AppendLine("                 the out-of-band tail checkpoint reconciles with the log.");
            body.AppendLine("  UNVERIFIABLE - one or more entries were signed with a key that is not available on");
            body.AppendLine("                 this machine. Their integrity can be neither confirmed nor denied.");
            body.AppendLine("                 No entry WITH AN AVAILABLE SIGNING KEY was found to have been altered;");
            body.AppendLine("                 the unverifiable entries were not checked at all, so this report makes");
            body.AppendLine("                 no claim about their contents. This is an evidence gap (usually a");
            body.AppendLine("                 key-management event such as a service-account change), NOT tampering.");
            // CORRECTED 2026-08-01 (round 4). This used to print "Reaching this verdict REQUIRES
            // surviving key-provenance evidence that the named key is one this installation held."
            // That was false, and it was false on a document handed to an auditor. Measured: the
            // evidence R1 consults is three traces in the audit-log directory, all writable by
            // whoever can write the .jsonl. ROUND 5: and one of them still costs only the twenty
            // published header bytes, which is why the sentence below claims a raised cost and
            // nothing more.
            body.AppendLine("                 Reaching this verdict requires the key id named to leave a trace in this");
            body.AppendLine("                 installation's own provenance records. Those records - the .chain-anchor");
            body.AppendLine("                 ledger and the hmac.key.<id> archive files - live in the audit-log");
            body.AppendLine("                 directory under the SAME permissions as the log they vouch for, so this");
            body.AppendLine("                 verdict raises the cost of a forged KeyId rather than ruling one out.");
            body.AppendLine("                 Where no provenance record survives at all, see INDETERMINATE below.");
            body.AppendLine("  INDETERMINATE- one or more entries could not be checked AND no key-provenance evidence");
            body.AppendLine("                 survives on this machine to show whether the key they name is a real key");
            body.AppendLine("                 this installation held or a value written into the record afterwards;");
            body.AppendLine("                 or the tail checkpoint itself could not be read, so whether records were");
            body.AppendLine("                 removed can be neither detected nor ruled out.");
            body.AppendLine("                 THIS REPORT MAKES NO CLAIM ABOUT THOSE ENTRIES. It does not say they are");
            body.AppendLine("                 intact, and it does not say they were altered - both are consistent with");
            body.AppendLine("                 what was observed. Do not record the affected range as verified, and do");
            body.AppendLine("                 not close it as a key-management event without independent evidence.");
            body.AppendLine("  RESTARTED    - the chain resumed after a break, and nothing else was found wrong. An");
            body.AppendLine("                 entry declared a previous-hash link other than the one the walk carried");
            body.AppendLine("                 in, AND its own signature verified against the link it declares. That");
            body.AppendLine("                 link is part of what the signature covers, so the entry was written by a");
            body.AppendLine("                 holder of the signing key and has not been edited since. The entries on");
            body.AppendLine("                 both sides of the gap are sound; what is lost is the LINK across it, so");
            body.AppendLine("                 the records cannot be shown to be contiguous at that point. This is not");
            body.AppendLine("                 tampering. It is also not clean, and it is not the same finding as");
            body.AppendLine("                 BROKEN's link breaks below: a break whose declared link appears nowhere");
            body.AppendLine("                 in the chain is consistent with records having been removed, and it is");
            body.AppendLine("                 reported as BROKEN, never as a restart.");
            body.AppendLine("  BROKEN       - one or more entries were checked against their own signing key and the");
            body.AppendLine("                 signature did not match, OR an entry named a signing key this install");
            body.AppendLine("                 never held / had already superseded (a rewritten KeyId field), OR the");
            body.AppendLine("                 out-of-band tail checkpoint shows entries were REMOVED - a chain whose");
            body.AppendLine("                 surviving entries all verify is still not intact, OR a LINK between two");
            body.AppendLine("                 entries does not reconcile and cannot be shown to be a restart.");
            body.AppendLine("                 Treat as tampering until proven otherwise.");
            body.AppendLine();
            body.AppendLine("Scope of this report:");
            body.AppendLine("  Verification reconciles the stored records against the signing keys and the chain");
            body.AppendLine("  checkpoint held in this installation's audit-log directory. Those files sit under");
            body.AppendLine("  the same permissions as the log they vouch for, so this is NOT an attestation");
            body.AppendLine("  against an attacker who can write that directory: such an attacker can manufacture");
            body.AppendLine("  any verdict above, INTACT included. See docs/compliance/incident-response-runbook.md,");
            body.AppendLine("  § What this chain does and does not guarantee.");

            return body.ToString();
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
                // FOLDED, for the same reason LogChainVerified is: switching on the raw status made
                // a truncated estate chain log INTACT at Information every single night, while the
                // anchor sat there as the only witness that records had been removed.
                var (status, detail) = FoldWithAnchorFindings(result);
                // A BROKEN verdict must not be filed at the same level as a clean pass —
                // it was previously indistinguishable from routine Information noise.
                switch (status)
                {
                    case ChainVerificationStatus.Intact:
                        Serilog.Log.Information(
                            "[AUDIT] Scheduled chain verification ({Mode}): INTACT, {Count} entries",
                            runFull ? "FULL" : "QUICK", result.EntryCount);
                        break;
                    case ChainVerificationStatus.Unverifiable:
                        // Warning, not Error: nothing was found altered. But not silent either —
                        // an evidence gap is still a compliance finding.
                        Serilog.Log.Warning(
                            "[AUDIT] Scheduled chain verification ({Mode}): UNVERIFIABLE — {Detail}",
                            runFull ? "FULL" : "QUICK", detail);
                        break;
                    case ChainVerificationStatus.Indeterminate:
                        // Error: above a known-benign gap, below the tamper signal. Nothing was
                        // shown to be altered and nothing was shown NOT to be.
                        Serilog.Log.Error(
                            "[AUDIT] Scheduled chain verification ({Mode}): INDETERMINATE — {Detail}",
                            runFull ? "FULL" : "QUICK", detail);
                        break;
                    case ChainVerificationStatus.Restarted:
                        // Warning: every entry verified; only the link across a gap is missing.
                        // Filing this at Error is what buried the real finding under nine nights of
                        // tamper alerts nobody could act on.
                        Serilog.Log.Warning(
                            "[AUDIT] Scheduled chain verification ({Mode}): RESTARTED. {Detail}",
                            runFull ? "FULL" : "QUICK", detail);
                        break;
                    default:
                        Serilog.Log.Error(
                            "[AUDIT] Scheduled chain verification ({Mode}): BROKEN — {Detail}",
                            runFull ? "FULL" : "QUICK", detail);
                        break;
                }

                if (runFull)
                    _lastFullChainVerifyUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "[AUDIT] Scheduled chain verification failed");
            }
        }

        /// <summary>Emits an audit record of a chain verification run (SOC2 AU-6/AU-9).</summary>
        /// <remarks>
        /// THE DEFECT THIS FIXES (2026-08-01 round 6). This method is called from inside
        /// <see cref="VerifyChain"/> with the RAW result, so the chain's own record of a
        /// verification contradicted every artifact produced by the same call. Measured on a
        /// truncated chain, one launch: the sealed statement said BROKEN while the on-chain
        /// <c>AuditChainVerified</c> record filed <c>Status=INTACT</c>, <c>Intact=True</c>,
        /// <c>Severity=Info</c>, message "Audit chain verification: INTACT".
        /// <para>
        /// It is not a tidiness problem. That record is client-reachable through the audit viewer's
        /// Export CSV / Export JSON buttons, and a truncation finding filed at Info is invisible to
        /// severity-based triage — nightly, because the scheduled sweep goes through here too.
        /// </para>
        /// <para>
        /// Fixed symmetrically with <see cref="LogChainVerificationExported"/> one method below,
        /// which already filed <c>Status</c> folded and <c>UnfoldedStatus</c> raw: the folded
        /// verdict is the record, the raw one is kept beside it. The fold only ever escalates, so
        /// <c>Intact</c> can only move True→False here — the fail-safe direction for every existing
        /// reader of that key.
        /// </para>
        /// </remarks>
        public void LogChainVerified(ChainVerificationResult result, string triggeredBy)
        {
            var (foldedStatus, foldedDetail) = FoldWithAnchorFindings(result);
            var statusWord = StatusWordFor(foldedStatus);

            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.AuditChainVerified,
                // Derived from the FOLDED status: a severity that describes the raw result is a
                // severity that describes a verdict no artifact printed.
                Severity = SeverityFor(foldedStatus),
                Message = $"Audit chain verification: {statusWord} — {result.EntryCount} entries examined " +
                          $"by {triggeredBy}. {foldedDetail}",
                Details = new Dictionary<string, string>
                {
                    // Kept for backwards compatibility with existing readers/exports — folded, so a
                    // reader keying off it cannot be told True over a chain the same run called BROKEN.
                    ["Intact"] = (foldedStatus == ChainVerificationStatus.Intact).ToString(),
                    ["Status"] = statusWord,
                    // The unfolded VerifyChain verdict, kept beside it: the fold's escalation is
                    // then visible in the record rather than erasing what the signatures alone said.
                    ["UnfoldedStatus"] = result.StatusLabel,
                    ["EntryCount"] = result.EntryCount.ToString(),
                    ["VerifiedCount"] = result.VerifiedCount.ToString(),
                    ["BrokenCount"] = result.BrokenCount.ToString(),
                    ["UnverifiableCount"] = result.UnverifiableCount.ToString(),
                    ["IndeterminateCount"] = result.IndeterminateCount.ToString(),
                    // Split out 2026-08-11: these two used to be folded into BrokenCount, so the
                    // chain's own record of a verification could not tell an auditor whether the
                    // number meant failed entries or missing links.
                    ["LinkBreakCount"] = result.LinkBreakCount.ToString(),
                    ["RestartCount"] = result.RestartCount.ToString(),
                    ["DuplicateEntryCount"] = result.DuplicateEntryCount.ToString(),
                    ["FirstDuplicateRecord"] = result.FirstDuplicateRecord ?? string.Empty,
                    ["FirstLinkBreakRecord"] = result.FirstLinkBreakRecord ?? string.Empty,
                    ["FirstRestartRecord"] = result.FirstRestartRecord ?? string.Empty,
                    ["FirstBrokenRecord"] = result.FirstBrokenRecord ?? string.Empty,
                    ["FirstUnresolvableKeyId"] = result.FirstUnresolvableKeyId ?? string.Empty,
                    ["FirstIndeterminateKeyId"] = result.FirstIndeterminateKeyId ?? string.Empty,
                    ["FirstEntry"] = result.FirstEntry?.ToString("o") ?? string.Empty,
                    ["LastEntry"] = result.LastEntry?.ToString("o") ?? string.Empty,
                    ["VerifiedAt"] = result.VerifiedAt.ToString("o"),
                    ["TriggeredBy"] = triggeredBy
                }
            });
        }

        /// <summary>Emits an audit record when a verification report is exported (SOC2 AU-9).</summary>
        public void LogChainVerificationExported(string exportedBy, string filePath, ComplianceChainStatement statement)
        {
            var result = statement.Result;
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.AuditChainVerificationExported,
                Severity = AuditSeverity.Info,
                Message = $"Audit chain verification report exported by {exportedBy}",
                Details = new Dictionary<string, string>
                {
                    ["ExportedBy"] = exportedBy,
                    ["FilePath"] = filePath,
                    ["EntryCount"] = result.EntryCount.ToString(),
                    // The FOLDED verdict — what the exported document actually printed. Recording
                    // the raw one here would let the chain's own record of the export disagree with
                    // the export, which is the defect one layer down.
                    ["Status"] = statement.StatusWord,
                    ["Intact"] = (statement.Status == ChainVerificationStatus.Intact).ToString(),
                    ["UnfoldedStatus"] = result.StatusLabel,
                    ["BrokenCount"] = result.BrokenCount.ToString(),
                    ["UnverifiableCount"] = result.UnverifiableCount.ToString(),
                    ["IndeterminateCount"] = result.IndeterminateCount.ToString(),
                    ["LinkBreakCount"] = result.LinkBreakCount.ToString(),
                    ["RestartCount"] = result.RestartCount.ToString(),
                    ["DuplicateEntryCount"] = result.DuplicateEntryCount.ToString()
                }
            });
        }

        /// <summary>
        /// AU-9: records that the audit signing key had to be replaced because the existing one
        /// could not be read. The event lands ON the chain (under the NEW key, because the old one
        /// is by definition unusable) so the reason history stopped verifying is itself auditable.
        /// </summary>
        /// <remarks>
        /// WHAT 2026-08-11 ADDED, and why. The record already named the new key and the identity
        /// that minted it. It did not name the key that went dark, nor the identity that had written
        /// it — so the forensic account of the live incident had to be RECONSTRUCTED from NTFS
        /// timestamps, a rejected-config artifact and the key ids the surviving entries happened to
        /// declare. Those facts are all knowable at the moment of replacement, so they are written
        /// down at that moment instead.
        /// <para>
        /// EVERY id here is labelled by its SOURCE, because they are not equally strong. The old
        /// key's blob cannot be unwrapped — that is what made this an incident — so its id cannot be
        /// computed from the material at all. What exists is what the sidecar recorded when that key
        /// was written, and what the entries on disk declare. Neither is presented as a measurement
        /// of the dead key, and where a source is silent the field says so rather than guessing.
        /// </para>
        /// </remarks>
        private void LogHmacKeyReplaced(HmacKeyLoadReport report)
        {
            const string NotRecorded = "(not recorded)";
            var sidecarOldKeyId = string.IsNullOrEmpty(_hmacKeyTransition?.PreviousKeyId)
                ? NotRecorded : _hmacKeyTransition!.PreviousKeyId;
            var sidecarOldIdentity = string.IsNullOrEmpty(_hmacKeyTransition?.PreviousWrittenBy)
                ? NotRecorded : _hmacKeyTransition!.PreviousWrittenBy;
            var chainOldKeyId = string.IsNullOrEmpty(_lastKeyIdOnChainAtStartup)
                ? NotRecorded : _lastKeyIdOnChainAtStartup!;

            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.HmacKeyReplacedUnreadable,
                Severity = AuditSeverity.Critical,
                Message = "Audit HMAC signing key REPLACED: the existing key file could not be unwrapped under the " +
                          $"current identity ({SafeIdentityName()}), so a new key ({_currentKeyId}) was minted. The " +
                          $"outgoing key is named as {sidecarOldKeyId} by the key-age sidecar and as {chainOldKeyId} " +
                          "by the entries already on disk; its blob cannot be unwrapped, so its id cannot be computed " +
                          "from the material here. Entries signed with the previous key are now UNVERIFIABLE (not " +
                          "tampered). The unreadable key blob was preserved.",
                Details = new Dictionary<string, string>
                {
                    ["KeyPath"] = _keyPath,
                    ["NewKeyId"] = _currentKeyId,
                    // Two independently sourced answers to "which key went dark", each labelled.
                    // They are kept apart on purpose: if they ever disagree, that disagreement is
                    // itself the finding, and folding them into one field would hide it.
                    ["OldKeyIdFromSidecar"] = sidecarOldKeyId,
                    ["OldKeyIdOnChain"] = chainOldKeyId,
                    ["OldKeyWrittenBy"] = sidecarOldIdentity,
                    ["ReplacedByIdentity"] = SafeIdentityName(),
                    ["ReplacedAt"] = _hmacKeyTransition?.ReplacedAt ?? DateTime.UtcNow.ToString("o"),
                    ["UnreadableBlobLength"] = report.UnreadableBlobLength.ToString(CultureInfo.InvariantCulture),
                    ["PreservedPath"] = report.PreservedPath ?? "(preservation failed)",
                    ["Identity"] = SafeIdentityName(),
                    ["Reason"] = report.FailureDetail ?? "unknown"
                }
            });
        }

        /// <summary>
        /// Does a verified <see cref="AuditEventType.HmacKeyReplacedUnreadable"/> entry on this
        /// chain account for the transition from <paramref name="fromKeyId"/> to
        /// <paramref name="toKeyId"/>? An entry only counts when its own signature verifies, so this
        /// is an explanation the chain itself vouches for rather than one an editor could type in.
        /// <para>
        /// It is corroboration, NOT proof: the entry is signed with the NEW key, which is the only
        /// key available at the moment it is written, so anyone holding the new key could produce
        /// one. What it rules out is the silent transition — a key change with no record of itself.
        /// </para>
        /// </summary>
        public bool HasVerifiedKeyReplacementRecord(string fromKeyId, string toKeyId)
        {
            if (string.IsNullOrEmpty(fromKeyId) || string.IsNullOrEmpty(toKeyId)) return false;

            foreach (var segmentPath in GetAllSegmentsChronological())
            {
                if (!File.Exists(segmentPath)) continue;
                string previousSig = string.Empty;
                bool firstOfSegment = true;
                foreach (var line in File.ReadLines(segmentPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    AuditLogEntry? entry;
                    try { entry = JsonSerializer.Deserialize<AuditLogEntry>(line, SerializerOptions); }
                    catch { continue; }
                    if (entry == null) continue;
                    if (firstOfSegment) { firstOfSegment = false; previousSig = entry.PreviousHash ?? string.Empty; }

                    if (entry.EventType == AuditEventType.HmacKeyReplacedUnreadable &&
                        entry.Details != null &&
                        string.Equals(entry.Details.GetValueOrDefault("NewKeyId"), toKeyId, StringComparison.OrdinalIgnoreCase) &&
                        (string.Equals(entry.Details.GetValueOrDefault("OldKeyIdFromSidecar"), fromKeyId, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(entry.Details.GetValueOrDefault("OldKeyIdOnChain"), fromKeyId, StringComparison.OrdinalIgnoreCase)) &&
                        TryResolveKeyForEntry(entry, out var key) &&
                        (string.Equals(ComputeSignature(entry, previousSig, key), entry.Signature, StringComparison.Ordinal) ||
                         string.Equals(ComputeSignature(entry, entry.PreviousHash ?? string.Empty, key), entry.Signature, StringComparison.Ordinal)))
                        return true;

                    previousSig = entry.Signature ?? string.Empty;
                }
            }
            return false;
        }

        // ================================================================
        // AU-9 — OFF-BOX CO-SIGN RECEIPTS (2026-08-12)
        //
        // WHAT THIS BUYS AND WHAT IT DOES NOT. Everything above this line is verifiable only by
        // this box, over files this box can write — the compliance report states the limit plainly
        // under "Scope of this report" (ComposeChainVerificationReport): an attacker who can write
        // the audit-log directory can manufacture any verdict, including INTACT, and
        // DescribeChainForCompliance carries the same sentence onto the client-facing statement.
        // These three members are the box's half of the answer: a head recorded
        // by an independent observer, signed by that observer, read back and bound INTO the chain
        // so the chain carries its own account of having been observed.
        //
        // The limit that survives, and must be stated wherever this is described: a rewrite BEFORE
        // the first record, or inside the window before the next comparison, is invisible. This
        // raises the cost of a RETROACTIVE forgery. It does not make the chain tamper-proof, and
        // nothing here may be described as though it did.
        // ================================================================

        /// <summary>
        /// Could this raw line be the given event type? A pre-filter for the two scans below, so a
        /// chain of tens of thousands of entries is not JSON-parsed line by line to answer a
        /// question about a handful of them.
        /// <para>
        /// IT ACCEPTS BOTH ENCODINGS, and that is the whole reason it exists as a method. Entries
        /// serialise <see cref="AuditLogEntry.EventType"/> as its ORDINAL (SerializerOptions carries
        /// no enum converter), so a filter written against the member NAME matches nothing at all —
        /// measured 2026-08-12, the first version of these scans was written that way and both
        /// silently found zero, which reads exactly like "no such entry exists". Matching either
        /// form means a later decision to serialise names cannot re-open the same hole. The
        /// authoritative check is still the parse below; this only decides what to skip.
        /// </para>
        /// </summary>
        private static bool MayCarryEvent(string line, AuditEventType type) =>
            line.IndexOf("\"EventType\":" + ((int)type).ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal) >= 0 ||
            line.IndexOf(type.ToString(), StringComparison.Ordinal) >= 0;

        /// <summary>
        /// Whether the head a countersign receipt names is one THIS chain actually holds.
        ///
        /// <para>Three states and no fourth, because the anti-rewrite sentence
        /// <see cref="LogAnchorReceiptRecorded"/> may print is only true in the first one.</para>
        /// </summary>
        public enum HeadCorroboration
        {
            /// <summary>
            /// The comparison was not attempted, or the chain could not be read to make it. NOT a
            /// synonym for <see cref="NotFound"/>: "we did not look" and "we looked and it is not
            /// ours" are different facts and the entry says which.
            /// </summary>
            NotChecked = 0,
            /// <summary>An entry in this chain carries exactly that signature.</summary>
            Matched,
            /// <summary>The chain was read end to end and holds no entry with that signature.</summary>
            NotFound,
        }

        /// <summary>
        /// Reads this chain back and answers whether it holds an entry whose
        /// <see cref="AuditLogEntry.Signature"/> is <paramref name="headSignature"/>.
        ///
        /// <para>WHY THIS EXISTS (2026-08-27, portal-r1-03). The receipt intake recorded a verified
        /// receipt while asserting, in the chain entry's own message, that "a later rewrite of this
        /// chain would disagree with a value already held elsewhere". Nothing measured that. The
        /// signature check proves the OBSERVER signed the receipt; it says nothing about whether the
        /// head inside it was ever this box's. Measured: a fresh chain whose own head was
        /// <c>u+or79nzZlmSfAsGO9ojyhAh9MVOLkPpjmHCzPCE6Hc=</c> recorded a receipt naming
        /// <c>k3Qm1yQz9r7Xh2pLwEo0cVbN4tYgUiA6sDfGhJkLzXc=</c> and printed the guarantee anyway. The
        /// receipt path has no per-box discriminator beyond the client id, so two installs sharing
        /// one clientId each recorded the other's head as their own anchor.</para>
        ///
        /// <para>NOT the current head. The receipt describes the tail as it stood during the
        /// observer's sweep on some past day; the chain has grown since. The honest question is
        /// therefore "did this chain EVER hold that head", which is a membership test over every
        /// entry's signature, not an equality test against
        /// <see cref="ChainVerificationResult.HeadSignature"/>.</para>
        ///
        /// <para>COST. O(chain), the same order as <see cref="AnchorReceiptStateFor"/>, which the
        /// caller already runs on the same pass — and this one runs at most once per day, only on
        /// the branch about to write a record. The substring pre-filter skips the JSON parse for the
        /// overwhelming majority of lines.</para>
        ///
        /// <para>FAILS TO <see cref="HeadCorroboration.NotChecked"/>, never to a verdict. An
        /// unreadable chain must not manufacture "not ours" any more than it may manufacture "ours".</para>
        /// </summary>
        public HeadCorroboration CorroborateHeadSignature(string? headSignature)
        {
            if (string.IsNullOrWhiteSpace(headSignature)) return HeadCorroboration.NotChecked;

            // THE PRE-FILTER NEEDLE MUST BE THE SERIALIZED FORM, NOT THE IN-MEMORY ONE. The chain is
            // written with SerializerOptions, whose default encoder turns '+' into a JSON unicode
            // escape (backslash-u-0-0-2-B), and a base64 signature carries a '+' often. Measured on
            // a 12-entry chain: 5 of 12 signatures did NOT appear verbatim in their own line —
            // WCfQ...O9+P0AI= is on disk with the '+' replaced by that escape. A verbatim-only
            // pre-filter therefore skipped the very line it was looking for and returned NotFound
            // for a head this chain plainly held — which would
            // have made this honesty fix print "NOT found in this chain" over a genuine anchor about
            // forty per cent of the time. '/' and '=' are not escaped, so both forms are tested
            // rather than assuming which one a given line uses.
            var quoted = JsonSerializer.Serialize(headSignature, SerializerOptions);
            var escaped = quoted.Length >= 2 ? quoted.Substring(1, quoted.Length - 2) : headSignature;

            Flush();   // an entry written moments ago may still be queued
            try
            {
                foreach (var segmentPath in GetAllSegmentsChronological())
                {
                    if (!File.Exists(segmentPath)) continue;
                    foreach (var line in File.ReadLines(segmentPath))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        // Cheap pre-filter: a base64 signature is a long, distinctive token, so a
                        // line containing neither form cannot be the entry we want.
                        if (line.IndexOf(escaped, StringComparison.Ordinal) < 0 &&
                            line.IndexOf(headSignature, StringComparison.Ordinal) < 0) continue;
                        AuditLogEntry? entry;
                        try { entry = JsonSerializer.Deserialize<AuditLogEntry>(line, SerializerOptions); }
                        catch { continue; }
                        // The SIGNATURE field specifically. A match on PreviousHash would mean the
                        // same thing, but a match anywhere else (a Details value quoting a head, as
                        // this very event type does) would not, and one authoritative field is
                        // cheaper to reason about than an enumeration of the safe ones.
                        if (entry != null &&
                            string.Equals(entry.Signature, headSignature, StringComparison.Ordinal))
                            return HeadCorroboration.Matched;
                    }
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(
                    "[AUDIT] Could not scan the chain to corroborate a receipt's head ({Kind}); " +
                    "recording it as not checked.", ex.GetType().Name);
                return HeadCorroboration.NotChecked;
            }
            return HeadCorroboration.NotFound;
        }

        /// <summary>
        /// Binds a VERIFIED countersign receipt into this chain. The caller has already checked the
        /// signature against the configured public key — this method records, it does not judge, so
        /// it must never be reached with an unverified receipt (that is what
        /// <see cref="LogAnchorReceiptRejected"/> is for).
        ///
        /// <para>THE MESSAGE MAY ONLY ASSERT WHAT <paramref name="corroboration"/> MEASURED
        /// (2026-08-27, portal-r1-03). The anti-rewrite sentence — "a later rewrite of this chain
        /// would disagree with a value already held elsewhere" — is true only of a head this chain
        /// actually holds, so it prints only under <see cref="HeadCorroboration.Matched"/>. The other
        /// two states say what they are and why, and neither is written as a smaller version of the
        /// guarantee.</para>
        ///
        /// <para>SEVERITY FOLLOWS THE SAME MEASUREMENT. Matched is Info — a receipt arriving is the
        /// NORMAL daily outcome, and filing routine success as a warning is how a severity-triaged
        /// reader learns to ignore the category. NotFound is Warning: a signed receipt naming a head
        /// this box never held is not routine, and it is the observable shape of two installs sharing
        /// one client id. NotChecked stays Info — an unreadable chain is reported elsewhere and this
        /// lane's posture is fail-open, not fail-loud.</para>
        /// </summary>
        /// <param name="day">UTC day the receipt is for, <c>yyyy-MM-dd</c>.</param>
        /// <param name="headSignature">The chain head the observer says it saw.</param>
        /// <param name="entryCount">The entry count the observer says it saw.</param>
        /// <param name="observerSignature">The observer's signature over the canonical receipt bytes.</param>
        /// <param name="observedAtUtc">When the observer says it made the observation, verbatim.</param>
        /// <param name="corroboration">
        /// What this box measured when it looked for that head in its own chain — see
        /// <see cref="CorroborateHeadSignature"/>. Defaulting is deliberate: an explicit
        /// <see cref="HeadCorroboration.NotChecked"/> is the only honest value for a caller that
        /// did not look.
        /// </param>
        public void LogAnchorReceiptRecorded(
            string day, string headSignature, int entryCount, string observerSignature, string observedAtUtc,
            HeadCorroboration corroboration = HeadCorroboration.NotChecked)
        {
            var opening = "An independent observer's signed receipt for the chain head of " + day +
                          " was verified and recorded here. ";
            var (message, severity) = corroboration switch
            {
                HeadCorroboration.Matched => (
                    opening +
                    "The head it names is one this chain holds, so it shows that head existed off " +
                    "this box on that day, and a later rewrite of this chain would disagree with a " +
                    "value already held elsewhere. It says nothing about entries written since, and " +
                    "nothing about the window before the head was first recorded.",
                    AuditSeverity.Info),

                HeadCorroboration.NotFound => (
                    opening +
                    "The observer's signature checked out, but the head it names was NOT found in " +
                    "this chain, so this receipt corroborates nothing about this chain's history. " +
                    "It does not raise the cost of rewriting anything here. Two things produce this: " +
                    "a receipt written for a DIFFERENT install (the receipt path is keyed on the " +
                    "client id alone, so installs sharing one see each other's receipts), or an " +
                    "entry that is no longer in the segments on this box. Neither is by itself a " +
                    "sign that this chain was altered.",
                    AuditSeverity.Warning),

                _ => (
                    opening +
                    "This chain could NOT be read back to check whether the head it names was ever " +
                    "this chain's, so nothing here corroborates this box's history. The receipt is " +
                    "recorded; the comparison is not made. Read this as unverified corroboration, " +
                    "not as the guarantee a matched head carries.",
                    AuditSeverity.Info),
            };

            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.AnchorReceiptRecorded,
                Severity = severity,
                Message = message,
                Details = new Dictionary<string, string>
                {
                    ["Day"] = day,
                    ["HeadSignature"] = headSignature,
                    // The machine-readable half of the same fact, so a reader that never parses the
                    // prose still cannot mistake an unmatched anchor for a matched one.
                    ["HeadCorroboration"] = corroboration switch
                    {
                        HeadCorroboration.Matched => "matched-this-chain",
                        HeadCorroboration.NotFound => "not-found-in-this-chain",
                        _ => "not-checked",
                    },
                    ["EntryCount"] = entryCount.ToString(CultureInfo.InvariantCulture),
                    ["ObserverSignature"] = observerSignature,
                    ["ObservedAtUtc"] = observedAtUtc,
                    ["Identity"] = SafeIdentityName()
                }
            });
        }

        /// <summary>
        /// Records a receipt this box REFUSED, and why. Warning, not Critical: the receipt file
        /// lives in a container this box can write, so a damaged, missing or unverifiable one is
        /// as consistent with an accident (a re-issued key, a truncated write, a partial upload) as
        /// with anything else. Nothing here may be read as a tamper finding on its own.
        /// </summary>
        /// <param name="day">UTC day the refused receipt claimed to be for, or "(not stated)".</param>
        /// <param name="reason">Enumerated refusal reason. Never a credential, never a URL.</param>
        /// <param name="claimedHeadSignature">The head the refused receipt claimed, when it stated one.</param>
        public void LogAnchorReceiptRejected(string day, string reason, string? claimedHeadSignature)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.AnchorReceiptRejected,
                Severity = AuditSeverity.Warning,
                Message = "A countersign receipt for " + day + " was found and REFUSED: " + reason +
                          ". Nothing was recorded as verified. A refused receipt is not by itself a " +
                          "sign that this chain was altered; the file sits where this box can write, so " +
                          "a damaged or re-issued one produces the same outcome.",
                Details = new Dictionary<string, string>
                {
                    ["Day"] = day,
                    ["Reason"] = reason,
                    ["ClaimedHeadSignature"] = claimedHeadSignature ?? "(not stated)",
                    ["Identity"] = SafeIdentityName()
                }
            });
        }

        /// <summary>
        /// Has a receipt for <paramref name="day"/> already been recorded on this chain? The
        /// idempotence check for the intake cycle: a day's receipt is bound in at most once,
        /// however many times the cycle runs.
        /// <para>
        /// THE CLAIM HOLDS ONLY IF THE CALLER CHECKS AND RECORDS THE SAME DAY. It did not on
        /// 2026-08-12: the intake asked about the day in the blob PATH and recorded the day in the
        /// receipt's CONTENT, so a receipt whose two disagreed was re-recorded on every cycle — four
        /// cycles, four entries — and the mismatched day's genuine receipt was then never fetched
        /// again. Fixed at the caller (<c>AnchorReceiptIntake</c>), which now refuses a receipt whose
        /// stated day is not the day it was fetched for, and writes the day it checked.
        /// </para>
        /// <para>
        /// HONEST LIMIT, and it is the same one that runs through this whole file: the answer is
        /// read from files in the audit directory, so anyone who can write there can suppress a
        /// TRUE by deleting the entry, or manufacture one by writing it. That costs an attacker
        /// nothing they did not already have, and the failure it produces is a duplicate or a
        /// missing record — not a false claim of verification, which is decided by the signature
        /// check upstream and never by this method.
        /// </para>
        /// </summary>
        public bool HasAnchorReceiptRecordedFor(string day) => AnchorReceiptStateFor(day).Recorded;

        /// <summary>
        /// Everything this chain already holds about one day's countersign receipt: whether it was
        /// RECORDED, and which refusal reasons are already filed against it.
        /// <para>
        /// ONE SCAN FOR BOTH FACTS, on purpose. The intake needs both on every cycle, and this walk
        /// is O(chain) over a chain that only grows — asking twice would double the cost of the
        /// thing being measured. <see cref="HasAnchorReceiptRecordedFor"/> is a thin read of the
        /// first field.
        /// </para>
        /// <para>
        /// THE REFUSAL SET EXISTS BECAUSE REFUSALS AMPLIFIED. Until 2026-08-12 only the recorded
        /// path was deduplicated, so one unreadable file left in the intake container wrote a fresh
        /// Warning-severity AnchorReceiptRejected entry on EVERY outbox batch — measured at six
        /// entries for six cycles — which is an unbounded, attacker-influenceable write into the
        /// audit chain from a file the design calls untrusted, and it degrades the very cost this
        /// scan and <see cref="VerifyChain"/> pay. The caller records a day's refusal at most once
        /// per distinct reason.
        /// </para>
        /// <para>
        /// <see cref="AnchorReceiptDayState.RefusalReasons"/> is NOT populated once
        /// <see cref="AnchorReceiptDayState.Recorded"/> is true: the scan stops at the record,
        /// because a recorded day is skipped whole by the caller and the refusal history could not
        /// change what it does.
        /// </para>
        /// </summary>
        public AnchorReceiptDayState AnchorReceiptStateFor(string day)
        {
            var reasons = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(day)) return new AnchorReceiptDayState(false, reasons);
            Flush();   // a receipt recorded moments ago may still be queued
            try
            {
                foreach (var segmentPath in GetAllSegmentsChronological())
                {
                    if (!File.Exists(segmentPath)) continue;
                    foreach (var line in File.ReadLines(segmentPath))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        // Cheap pre-filter before the JSON parse: the overwhelming majority of
                        // lines are neither, and this scan runs once per intake cycle over a
                        // chain that only grows.
                        var mayBeRecord = MayCarryEvent(line, AuditEventType.AnchorReceiptRecorded);
                        if (!mayBeRecord && !MayCarryEvent(line, AuditEventType.AnchorReceiptRejected)) continue;
                        AuditLogEntry? entry;
                        try { entry = JsonSerializer.Deserialize<AuditLogEntry>(line, SerializerOptions); }
                        catch { continue; }
                        if (entry?.Details == null) continue;
                        if (!string.Equals(entry.Details.GetValueOrDefault("Day"), day, StringComparison.Ordinal))
                            continue;

                        if (entry.EventType == AuditEventType.AnchorReceiptRecorded)
                            return new AnchorReceiptDayState(true, new HashSet<string>(StringComparer.Ordinal));
                        if (entry.EventType == AuditEventType.AnchorReceiptRejected)
                            reasons.Add(entry.Details.GetValueOrDefault("Reason") ?? "");
                    }
                }
            }
            catch (Exception ex)
            {
                // Fail OPEN on an unreadable chain: the cost of a duplicate record is a duplicate
                // record, and the cost of failing closed is a receipt silently never bound in. The
                // caller carries its own in-process memory of what it has already filed, so an
                // unreadable chain cannot turn this fail-open into an unbounded write loop.
                Serilog.Log.Warning(
                    "[AUDIT] Could not scan for an existing receipt record ({Kind}); treating the day as unrecorded.",
                    ex.GetType().Name);
            }
            return new AnchorReceiptDayState(false, reasons);
        }

        /// <summary>
        /// What the chain already says about one day's countersign receipt. A plain observation;
        /// it decides nothing and verifies nothing.
        /// </summary>
        public sealed class AnchorReceiptDayState
        {
            internal AnchorReceiptDayState(bool recorded, HashSet<string> refusalReasons)
            {
                Recorded = recorded;
                RefusalReasons = refusalReasons;
            }

            /// <summary>
            /// What to assume when the chain could not be read at all: nothing recorded, nothing
            /// suppressed. FAIL OPEN — a duplicate record beats a receipt silently never bound in.
            /// </summary>
            public static AnchorReceiptDayState Unknown { get; } =
                new(false, new HashSet<string>(StringComparer.Ordinal));

            /// <summary>A verified receipt for the day is already bound into this chain.</summary>
            public bool Recorded { get; }

            /// <summary>
            /// Refusal reasons already filed for the day. Empty when <see cref="Recorded"/> is true
            /// (the scan stops at the record) and empty when the chain could not be read.
            /// </summary>
            public IReadOnlySet<string> RefusalReasons { get; }

            /// <summary>Is this exact refusal reason already on the chain for the day?</summary>
            public bool AlreadyRefusedFor(string reason) =>
                reason != null && RefusalReasons.Contains(reason);
        }

        /// <summary>
        /// Did this chain record a signing-key change at or after <paramref name="sinceUtc"/>? Both
        /// shapes count: a deliberate rotation (<see cref="AuditEventType.HmacKeyRotated"/>) and a
        /// key REPLACED because the old one could not be unwrapped
        /// (<see cref="AuditEventType.HmacKeyReplacedUnreadable"/>).
        /// <para>
        /// WHY BOTH, and why this is not derived from a verification result: an off-box observer
        /// comparing today's key id with yesterday's sees a change and cannot tell a rotation from
        /// a substitution. <see cref="ChainVerificationResult.KeyReplacementExplanation"/> only
        /// speaks when the walk found entries it could not check, so a CLEAN rotation — the common,
        /// benign case, where the retired key is still archived and every entry verifies — leaves
        /// it null. Reporting that as "no key change on record" would flag every healthy rotation.
        /// </para>
        /// <para>
        /// PRESENCE ONLY. This does not verify the signature of the record it finds, and it is not
        /// evidence that the change was authorised: it distinguishes a change the chain accounts
        /// for from one it is silent about, which is all an observer needs to avoid raising an
        /// alarm on a routine rotation.
        /// </para>
        /// </summary>
        public bool HasRecordedKeyChangeSince(DateTime sinceUtc)
        {
            try
            {
                foreach (var segmentPath in GetAllSegmentsChronological())
                {
                    if (!File.Exists(segmentPath)) continue;
                    // Segments are chronological and append-only, so one whose LAST write predates
                    // the window cannot hold an entry inside it.
                    try { if (File.GetLastWriteTimeUtc(segmentPath) < sinceUtc) continue; }
                    catch { /* unreadable timestamp: scan it rather than skip it */ }

                    foreach (var line in File.ReadLines(segmentPath))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        if (!MayCarryEvent(line, AuditEventType.HmacKeyRotated) &&
                            !MayCarryEvent(line, AuditEventType.HmacKeyReplacedUnreadable))
                            continue;
                        AuditLogEntry? entry;
                        try { entry = JsonSerializer.Deserialize<AuditLogEntry>(line, SerializerOptions); }
                        catch { continue; }
                        if (entry == null) continue;
                        if (entry.EventType != AuditEventType.HmacKeyRotated &&
                            entry.EventType != AuditEventType.HmacKeyReplacedUnreadable) continue;
                        if (entry.Timestamp.ToUniversalTime() >= sinceUtc) return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(
                    "[AUDIT] Could not scan for a recorded key change ({Kind}); reporting none found.",
                    ex.GetType().Name);
            }
            return false;
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

        /// <summary>
        /// Emits a Warning when the HMAC key age exceeds the configured maximum (AU-9).
        /// <para>
        /// <paramref name="ageAttributedToKeyInUse"/> conditions the sentence on the same fact the
        /// caller measured. An age read from a sidecar that does not name its subject is an age of
        /// SOME key, and printing it as the age of the key now signing entries is the same
        /// unattributed claim one layer down from the one the sidecar itself was making.
        /// </para>
        /// </summary>
        public void LogHmacKeyAgeExceeded(int ageDays, int maxAgeDays,
                                          string? keyIdInUse = null, bool ageAttributedToKeyInUse = true)
        {
            Enqueue(new AuditLogEntry
            {
                EventType = AuditEventType.HmacKeyAgeExceeded,
                Severity = AuditSeverity.Warning,
                Message = ageAttributedToKeyInUse
                    ? $"HMAC audit key is {ageDays} days old (max: {maxAgeDays} days). Rotation recommended."
                    : $"The HMAC key-age record says {ageDays} days (max: {maxAgeDays} days), and it does not name " +
                      "the key it describes, so that age cannot be attributed to the key now signing entries" +
                      (string.IsNullOrEmpty(keyIdInUse) ? "" : $" ({keyIdInUse})") +
                      ". Rotation recommended, which also writes a record that names its key.",
                Details = new Dictionary<string, string>
                {
                    ["AgeDays"] = ageDays.ToString(),
                    ["MaxAgeDays"] = maxAgeDays.ToString(),
                    ["KeyIdInUse"] = keyIdInUse ?? string.Empty,
                    ["AgeAttributedToKeyInUse"] = ageAttributedToKeyInUse.ToString()
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
            Flush(); // Final flush on dispose — takes the append lock, so release after it.
            _appendMutex?.Dispose();
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

        /// <summary>
        /// H4 (2026-07-07): id of the HMAC key that signed this entry, so verification can pick the
        /// right key after a rotation instead of failing every pre-rotation entry. NULL on entries
        /// written before this field existed (they verify under the current key, as before).
        /// <para>
        /// 2026-08-01 CORRECTION. This field used to be documented as "not signed, but tampering with
        /// it is still detected". That was TRUE only while an unresolvable KeyId fell back to the
        /// current key (the mismatch surfaced as a break) and it became FALSE the moment UNVERIFIABLE
        /// was split out of BROKEN: rewriting this one unsigned field then downgraded a tamper to an
        /// evidence gap for zero cryptographic work. Two changes close that:
        /// (1) <see cref="SigVersion"/> 2 entries bind KeyId INTO the signed canonical form, so on any
        ///     entry written by this build or later the field is no longer free to edit; and
        /// (2) verification refutes an unresolvable KeyId that this installation provably never held,
        ///     or whose position in the chain contradicts key succession, or whose run is sandwiched
        ///     between entries sharing one live key — see <c>ClassifyUnverifiableRun</c>. Where none of
///     those can be evaluated for want of provenance evidence, the run is reported INDETERMINATE
///     rather than being given the benign verdict by default.
        /// Legacy (SigVersion null) entries keep the original canonical form and verify unchanged.
        /// </para>
        /// </summary>
        public string? KeyId { get; set; }

        /// <summary>
        /// Version of the signed canonical form used to produce <see cref="Signature"/>.
        /// <para>
        /// NULL/absent = v1, the original form: Timestamp, EventType, Severity, Message, Details,
        /// User, Machine, PreviousHash. Every entry written before 2026-08-01 is v1 and MUST keep
        /// verifying under exactly that form — retrofitting v2 to them would invalidate all existing
        /// history. 2 = v1 plus <see cref="KeyId"/> and this version field itself.
        /// </para>
        /// <para>
        /// The field is self-protecting: because it participates in the v2 canonical form, an
        /// attacker who strips it to force a v1 recomputation gets a signature mismatch (BROKEN), and
        /// one who adds it to a legacy entry gets the same. See
        /// <c>SignatureVersion_DowngradeOnANewEntry_IsClassifiedBroken</c>.
        /// </para>
        /// </summary>
        public int? SigVersion { get; set; }
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
        RemediationPowerEstimate,
        // ── S1: deferred-verify state ─────────────────────────────────────
        /// <summary>S1: an applied remediation's effect needs follow-up verification by a deadline ("verifying by &lt;date&gt;").</summary>
        RemediationVerifyScheduled,
        /// <summary>S1: a scheduled deferred verification reached a terminal state (passed, or failed at/after the deadline).</summary>
        RemediationVerifyResolved,
        // ── 2026-08-01 audit-chain honesty ────────────────────────────────
        // APPEND-ONLY: EventType serialises as its ORDINAL into the signed canonical form, so a
        // new member inserted above would silently re-label every historical entry after it.
        /// <summary>AU-9: the HMAC signing key was replaced because the existing key file could not be unwrapped.</summary>
        HmacKeyReplacedUnreadable,
        /// <summary>AU-9: entries were found whose signing key is unavailable — integrity gap, NOT a tamper signal.</summary>
        AuditChainUnverifiable,
        /// <summary>
        /// AU-9: entries were found that could not be checked AND for which no key-provenance
        /// evidence exists — neither a benign gap nor a tamper finding, and it must not be filed as
        /// either. Also serves as the on-chain record that the provenance ledger was established
        /// over a chain whose earlier key history was never observed.
        /// </summary>
        AuditChainIndeterminate,
        // ── 2026-08-12 off-box co-sign ────────────────────────────────────
        // APPENDED, like every member above. Both names are chain vocabulary, not feature
        // vocabulary: an anchor and a receipt describe what the record IS, and neither leaks the
        // name of the surface that produced it, which is the rule for anything compiled into every
        // profile.
        /// <summary>
        /// AU-9: a countersigned receipt for a previously recorded chain head was READ BACK, its
        /// signature verified against the configured public key, and bound into this chain. It
        /// says an independent observer recorded that head; it says nothing about entries written
        /// after it, and nothing about the window before the head was first recorded.
        /// </summary>
        AnchorReceiptRecorded,
        /// <summary>
        /// AU-9: a receipt was found and REFUSED, with the reason. Refusal is the honest outcome
        /// for a malformed file, an unverifiable signature, or a receipt describing a head this
        /// chain does not carry. It is never treated as evidence of tampering on its own: the file
        /// sits in a container this box can write, so a damaged or absent receipt is as consistent
        /// with an accident as with anything else.
        /// </summary>
        AnchorReceiptRejected,
        // ── 2026-09-01 app-issued DDL journaling (fresh-eyes F-A / ruling R1) ──
        // APPENDED, like every member above — EventType serialises as its ORDINAL into the signed
        // canonical form, so inserting anywhere but the end would silently re-label every
        // historical entry after the insertion point.
        /// <summary>
        /// DDL the app is about to send to a monitored server, recorded (and flushed) BEFORE the
        /// statement is sent. Carries the exact statement in full. An Attempted entry with no
        /// DdlCompleted successor means the process did not survive the statement — which is
        /// information, not a gap.
        /// </summary>
        DdlAttempted,
        /// <summary>
        /// The terminal state of a DDL statement the app sent: Succeeded, Failed, or Cancelled.
        /// Written on the failure path too, so a failed statement is journaled as failed.
        /// </summary>
        DdlCompleted,
        /// <summary>
        /// DDL refused before it reached the server — a DangerousExecGuard verdict, a permission
        /// check, or a rejected statement shape. The refused text is kept in full.
        /// </summary>
        DdlBlocked,
        // ── 2026-09-02 key-file pickup (board #19) ──
        // APPENDED, like every member above. EventType serialises as its ORDINAL into the signed
        // canonical form, so a member inserted anywhere but the END would silently re-label every
        // historical entry after the insertion point. AuditEventTypeOrdinalContractTests pins every
        // ordinal in this enum, so a future insertion fails a test rather than rewriting history.
        /// <summary>
        /// A Full licence was activated from a <c>*.key.txt</c> dropped beside the executable.
        /// Carries the bundle file name, the key file name, the customer name and what happened to
        /// the key file afterwards. Never the key, and never its fingerprint — the fingerprint
        /// belongs in the log, not in a signed record shared with a client's auditor.
        /// </summary>
        LicenseActivatedFromKeyFile,
        /// <summary>
        /// A key file was found and NOT activated from: held by the identity ratchet, malformed,
        /// too large to read, or its key did not open the paired bundle. The saved licence is
        /// unchanged on every one of those branches.
        /// </summary>
        LicenseKeyFileRejected
    }

    public enum AuditSeverity
    {
        Info,
        Warning,
        Error,
        Critical
    }
}
