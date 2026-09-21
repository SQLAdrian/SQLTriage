/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SQLTriage.Data;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services.Licensing
{
    /// <summary>Current state of a fingerprint in the register.</summary>
    public enum SeatState
    {
        /// <summary>Holds a seat and may contribute to audits and reports.</summary>
        Seated,
        /// <summary>Was seated, then released. Re-claiming it later is free (it costs no swap).</summary>
        Released,
        /// <summary>Claimed, but outside the licence's seat count — recorded, honest, NOT reported.</summary>
        OverAllocated,
    }

    /// <summary>One instance known to the register.</summary>
    public sealed record SeatRow(
        string Fingerprint,
        string DisplayName,
        string Alias,
        DateTime ClaimedUtc,
        DateTime? ReleasedUtc,
        SeatState State);

    /// <summary>The register's headline figures — what the UI banner reads. Local surfaces only:
    /// no portal or off-box consumer (ruled 2026-07-17, see <see cref="SeatRegister"/>).</summary>
    public sealed record SeatSummary(
        bool Unlimited,
        int Seats,              // 0 when unlimited
        int SeatsUsed,
        int SwapsAllowed,
        int SwapsUsed,
        bool Locked,
        string? LockReason,
        IReadOnlyList<SeatRow> Rows,
        string ChainHead);

    /// <summary>Outcome of asking the register to claim/permit something.</summary>
    public sealed record SeatDecision(bool Allowed, string? Reason);

    /// <summary>
    /// The result of filtering a server set down to seated instances. Carries the EXCLUDED set so
    /// every surface can render an honest banner instead of silently dropping servers.
    /// </summary>
    public sealed record SeatFilter(IReadOnlyList<string> Seated, IReadOnlyList<string> Excluded)
    {
        /// <summary>True when nothing was dropped — the common (licensed / unlimited) path.</summary>
        public bool IsComplete => Excluded.Count == 0;

        /// <summary>
        /// The honest banner text, or null when nothing was excluded. Non-blaming, states the number
        /// and the reason. Callers render this verbatim; never a silent drop.
        /// </summary>
        public string? ExclusionNotice => Excluded.Count == 0
            ? null
            : Excluded.Count == 1
                ? $"1 instance excluded: not covered by your licence ({Excluded[0]})."
                : $"{Excluded.Count} instances excluded: not covered by your licence " +
                  $"({string.Join(", ", Excluded.Take(5))}{(Excluded.Count > 5 ? ", …" : "")}).";
    }

    public interface ISeatRegister
    {
        /// <summary>Cheap, synchronous, hot-path read: may this instance contribute to reports?</summary>
        bool IsSeated(string instanceName);

        /// <summary>Filters a server set to seated instances, reporting what was excluded.</summary>
        SeatFilter Filter(IEnumerable<string> instanceNames);

        /// <summary>Headline figures for the local UI. Not published off the box.</summary>
        SeatSummary Status();

        /// <summary>
        /// Records a successful fingerprint probe and claims a seat if this is the first sighting.
        /// Idempotent: re-probing a seated instance spends nothing and normally writes nothing (the
        /// one exception is an alias change, which appends a rename record so the name→fingerprint
        /// join survives a restart — it moves no seat and costs no swap).
        /// </summary>
        SeatDecision ClaimOnProbe(InstanceFingerprint fingerprint, string instanceName);

        /// <summary>Releases a seated fingerprint (frees the seat; the swap is charged on the NEXT new claim).</summary>
        SeatDecision Release(string fingerprint);

        /// <summary>
        /// Releases the seat held by an instance NAME (a split server string, the key
        /// <see cref="ClaimOnProbe"/> maps). This is the release the APP can actually call: callers
        /// hold configured server strings, never fingerprints — the fingerprint is an internal
        /// detail known only to the register and the probe.
        ///
        /// Returns Allowed for a name that holds no seat (never probed, already released): "there is
        /// no seat to free" is a satisfied post-condition, not a failure, and a caller removing a
        /// server must not have to care which of its instances were ever reachable.
        /// </summary>
        SeatDecision ReleaseInstance(string instanceName);

        /// <summary>
        /// Would an estate of <paramref name="resultingInstanceCount"/> instances exceed the licence?
        /// The caller passes the count the estate WOULD have after its change, counted per split
        /// server string. Used by the add-time guard, BEFORE any probe — so an operator is told at
        /// the point of action rather than silently discovering it at report time.
        /// </summary>
        SeatDecision CanAdmit(int resultingInstanceCount);

        /// <summary>True when seats are full AND swaps are exhausted: the server list is locked.</summary>
        bool IsLocked { get; }

        /// <summary>Walks the HMAC chain. Returns null when intact, else an honest description of the break.</summary>
        string? VerifyChain();

        /// <summary>Fired when the register changes so cached surfaces can refresh.</summary>
        event Action? SeatsChanged;
    }

    /// <summary>
    /// INSTANCE-SEAT REGISTER — the persisted, tamper-evident record of which real SQL instances
    /// this licence covers.
    ///
    /// TRUST MODEL (mirrors DemoRunLedger + AuditLogService):
    ///   • The ALLOCATION lives in the GCM-authenticated bundle, never in this store and never in
    ///     GitHub source — recompiling the public build cannot lift it.
    ///   • This store is the LEDGER. It is SQLCipher-encrypted (SqliteCipherHelper, DPAPI-LocalMachine
    ///     key) and every claim/release is HMAC hash-CHAINED (AuditLogService idiom:
    ///     sig = HMACSHA256(key, prevSig + "|" + canonicalJson(entry))), so deleting or editing rows
    ///     is EVIDENT — VerifyChain() catches it locally.
    ///
    /// WHAT THE CHAIN DOES AND DOES NOT BUY (be honest — this is client-side DRM):
    ///   Chaining makes tampering DETECTABLE, not IMPOSSIBLE. A determined operator with admin on
    ///   the box can delete the whole store and start clean; the DPAPI key is theirs. Enforcement
    ///   is LOCAL and tamper-EVIDENT: the break is detectable ON THIS BOX, and that is the whole of
    ///   what ships today. Nothing reports it off the box.
    ///   NOTE (ruled 2026-07-17): a seats block DID briefly ride the daily portal summary, and an
    ///   earlier version of this comment called that "the actual enforcement". It was reverted and
    ///   the claim was false twice over: §4.5 bars licence fields from that payload as a named
    ///   category, and the intake SAS is CLIENT-BOX-WRITABLE (§4.5 item 7) — anti-rotation evidence
    ///   authored by the policed party proves nothing, and shipping it back to them leaks the
    ///   enforcement model. A client-opaque detection channel is future portal work; until it
    ///   exists, do not claim detection happens anywhere but locally.
    ///   Do not oversell this as tamper-PROOF anywhere in code or copy.
    ///
    /// SEAT / SWAP ACCOUNTING (the whole model in four lines):
    ///   • D = distinct fingerprints ever claimed.  N = licensed seats.
    ///   • seatsUsed = fingerprints currently in the claimed state, capped at N by claimedUtc order.
    ///   • swapsUsed = max(0, D - N). Releasing to make room then claiming a DIFFERENT instance
    ///     pushes D past N and costs exactly one swap; re-claiming a previously-released
    ///     fingerprint does NOT grow D and so is free (a server that comes back is not churn).
    ///   • LOCKED when seatsUsed >= N AND swapsUsed >= swapsAllowed.
    ///
    /// LEGACY / ADOPTION: seats absent => UNLIMITED => the register still records every fingerprint
    /// but never blocks, so the data already exists when a real licence lands. When a seats licence
    /// FIRST appears against an estate already larger than N, nothing bricks: the first N claims by
    /// claimedUtc stay seated, the remainder become OverAllocated (recorded, excluded, and named in
    /// the honest banner).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class SeatRegister : ISeatRegister
    {
        private readonly IBundleAccessor _bundle;
        private readonly ILogger<SeatRegister> _logger;
        private readonly string _connectionString;
        private readonly string _keyPath;
        private readonly object _lock = new();

        // DPAPI entropy tag — distinguishes seat-register key blobs from other ProtectedData blobs
        // (AuditLogService.HmacKeyEntropy idiom).
        private static readonly byte[] HmacKeyEntropy =
            Encoding.UTF8.GetBytes("SQLTriage.SeatRegister.HmacKey.v1");

        private byte[]? _hmacKey;

        // In-memory snapshot for O(1) synchronous reads — GetResults is hot (~19 call sites resolve
        // through it), so it must never touch SQLite. Rebuilt from disk at init and after each
        // mutation. volatile + whole-dictionary swap = lock-free reads (AcceptedFindingsService idiom).
        private volatile Dictionary<string, SeatRow> _cache = new(StringComparer.OrdinalIgnoreCase);

        // Maps an instance NAME (as results are keyed) to its fingerprint. A name is only ever
        // mapped after a successful probe, so an unfingerprintable server simply is not in here.
        private volatile Dictionary<string, string> _nameToFingerprint = new(StringComparer.OrdinalIgnoreCase);

        private volatile string _chainHead = string.Empty;

        public event Action? SeatsChanged;

        private static readonly JsonSerializerOptions CanonicalJson = new()
        {
            WriteIndented = false,
        };

        public SeatRegister(
            IBundleAccessor bundle,
            ILogger<SeatRegister> logger,
            string? dbPath = null,
            string? keyPath = null)
        {
            _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            var path = dbPath ?? Path.Combine(AppContext.BaseDirectory, "Data", "seat-register.db");
            var dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // ⚠ REVIEWED 2026-08-20 for the C1 DPAPI-scope ruling: _keyPath is AppContext.BaseDirectory
            // -relative (the shared install folder), same as CredentialProtector's .credential-key (see
            // that class's 2026-08-20 note). Seat consumption happens from whichever host runs a scan —
            // interactive desktop or the installed service — against the same seat-register.db, so the
            // HMAC key that signs those events must stay readable by both. Genuinely shared; stays
            // LocalMachine.
            _keyPath = keyPath ?? Path.Combine(AppContext.BaseDirectory, "Config", ".seat-register-key");
            _connectionString = $"Data Source={path};Mode=ReadWriteCreate;Cache=Shared";

            InitializeSchema();
            ReloadCache();
        }

        // ── Licence view (read LIVE from the bundle, like DemoRunLedger) ──────────────

        /// <summary>null => UNLIMITED (fail-OPEN by ruling — see BundleManifest.InstanceSeats).</summary>
        private int? Seats => _bundle.Features.InstanceSeats;
        private bool IsUnlimited => Seats is null;
        private int SwapsAllowed => Math.Max(0, _bundle.Features.InstanceSwapsAllowed);

        // ── Schema ────────────────────────────────────────────────────────────────────

        private void InitializeSchema()
        {
            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                // APPEND-ONLY by design: a claim and a release are both new rows, never an in-place
                // UPDATE. That is what makes the HMAC chain meaningful — the table IS the chain, and
                // current state is derived as the latest event per fingerprint (by seq).
                // alias/display_name are '' NOT NULL (never NULL) — the AcceptedFindingsService
                // lesson: SQLite treats NULLs as DISTINCT, which quietly breaks keyed reasoning.
                cmd.CommandText = @"
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=NORMAL;

                    CREATE TABLE IF NOT EXISTS seat_events (
                        seq          INTEGER PRIMARY KEY AUTOINCREMENT,
                        fingerprint  TEXT NOT NULL,
                        display_name TEXT NOT NULL DEFAULT '',
                        alias        TEXT NOT NULL DEFAULT '',
                        event        TEXT NOT NULL,
                        event_utc    TEXT NOT NULL,
                        prev_sig     TEXT NOT NULL DEFAULT '',
                        sig          TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_seat_events_fingerprint
                        ON seat_events(fingerprint, seq DESC);
                ";
                cmd.ExecuteNonQuery();
                _logger.LogInformation("[SEATS] Seat register schema initialised");
            }
            catch (Exception ex)
            {
                // Fail-OPEN on an unusable store: an install whose register cannot initialise must
                // not have its estate go dark. Reads then see an empty cache; because seats are
                // unlimited by default, the estate keeps reporting. A LICENSED install with a broken
                // store degrades to "nothing seated" — which is loud and honest (the banner names
                // every instance), never a silent grant.
                _logger.LogError(ex, "[SEATS] Seat register schema initialisation failed");
            }
        }

        // ── HMAC key (DPAPI-LocalMachine, AuditLogService idiom) ─────────────────────

        [SQLTriage.Data.I1ReadFailureMayWrite(
            "Regenerates seat HMAC key material when the stored key will not unwrap. Not operator "
            + "configuration and never a built-in default: the key is machine-generated, and BOTH "
            + "failure arms take the shared key-aside route under RefuseUnlessPreserved first, so the "
            + "unreadable original is provably kept aside before anything is written and the write is "
            + "refused outright if that aside cannot be taken. RULED 2026-08-10.")]
        private byte[] GetOrCreateHmacKey()
        {
            if (_hmacKey != null) return _hmacKey;
            var regenerating = false;

            if (File.Exists(_keyPath))
            {
                try
                {
                    var wrapped = File.ReadAllBytes(_keyPath);
                    var raw = ProtectedData.Unprotect(wrapped, HmacKeyEntropy, DataProtectionScope.LocalMachine);
                    if (raw.Length == 32) return _hmacKey = raw;

                    // RULED 2026-08-10: this material UNWRAPPED. It is the wrong length to sign with,
                    // so it cannot be used, but it is READABLE key material and it is the only thing
                    // that could ever re-verify the seat events signed under it. Until this ruling the
                    // branch regenerated straight over it with no aside at all. It now takes the same
                    // aside as material that will not unwrap, and the same refusal if that aside
                    // cannot be taken.
                    _logger.LogError(
                        "[SEATS] The seat HMAC key at {Path} unwrapped to {Len} bytes rather than 32, so it "
                        + "cannot be used to sign; preserving it aside and regenerating", _keyPath, raw.Length);
                    RefuseUnlessPreserved(KeyAsideLifecycle.SetAside(_keyPath, AsideLog));
                    regenerating = true;
                }
                catch (CryptographicException ex)
                {
                    // The key exists but will not unwrap (backup restore, SID change). Overwriting it
                    // in place would orphan the chain irrecoverably AND destroy the evidence, so
                    // preserve it aside first (KeyAsideLifecycle, the shared house rule). The chain
                    // will then fail verification honestly rather than silently re-baselining.
                    _logger.LogError(ex, "[SEATS] Could not unwrap seat HMAC key at {Path}; preserving aside and regenerating (chain history will no longer verify)", _keyPath);
                    RefuseUnlessPreserved(KeyAsideLifecycle.SetAside(_keyPath, AsideLog));
                    regenerating = true;
                }
            }

            var key = new byte[32];
            RandomNumberGenerator.Fill(key);
            var protectedBytes = ProtectedData.Protect(key, HmacKeyEntropy, DataProtectionScope.LocalMachine);

            var dir = Path.GetDirectoryName(_keyPath);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(_keyPath, protectedBytes);
            try { new FileInfo(_keyPath).Attributes |= FileAttributes.Hidden; }
            catch (Exception ex) { _logger.LogDebug(ex, "[SEATS] Could not hide seat key file"); }

            _logger.LogInformation("[SEATS] Generated new seat-register HMAC key at {Path}", _keyPath);

            // The discriminate-and-prove lifecycle (HOUSE RULE 2026-08-09). The seat register is a
            // REFUSE-class store in the export fallback registry, and its aside posture matches:
            // KeyAsideLifecycle deletes only what a measurement proves worthless (zero bytes, or a
            // byte-identical duplicate of the key now in use) and keeps everything else, named.
            // Only on the replacement path, and the cleanup is authorised by the proof alone.
            if (regenerating)
            {
                var proof = KeyAsideLifecycle.ProveReplacement(
                    _keyPath, key, HmacKeyEntropy, DataProtectionScope.LocalMachine);

                // Reconcile BEFORE the refusal: it deletes nothing on an unproven proof (it is handed
                // the same one) and it names every preserved file in the log, which is what the
                // refusal tells the operator to go and look for.
                KeyAsideLifecycle.ReconcileAsides(
                    _keyPath, key, HmacKeyEntropy, DataProtectionScope.LocalMachine, proof, AsideLog);

                // RULED 2026-08-10: an unproven replacement does not become the key in force. Until
                // this ruling it was logged at Error and signed with anyway, which produces a chain
                // that verifies for the rest of this process and fails from the next start on.
                if (!proof.Proven)
                {
                    var message = KeyAsideLifecycle.RefusalNotProven(
                        _keyPath,
                        "seat events signed under a key the next start cannot read would every one "
                        + "of them fail verification, and the chain would report itself broken",
                        proof.Detail);
                    _logger.LogError("[SEATS] {Message}", message);
                    throw new KeyAsideRefusedException(message);
                }
            }

            return _hmacKey = key;
        }

        /// <summary>
        /// Posture (b), RULED 2026-08-10: this service does not write a fresh HMAC key over material
        /// it could not preserve.
        ///
        /// <para>What the caller sees follows this service's OWN fail-open contract rather than a new
        /// one. Both callers of <see cref="GetOrCreateHmacKey"/> already catch and degrade honestly:
        /// <c>AppendEvent</c> logs the failure and does not record the seat (it never silently
        /// grants), and <see cref="VerifyChain"/> returns "could not be verified" carrying this
        /// message verbatim. Nothing is cached, so a file that was briefly locked is retried on the
        /// next claim.</para>
        ///
        /// <para>⚠ That VerifyChain return is NOT what shapes the message, and this paragraph said
        /// it was until 2026-08-10. MEASURED that day: <see cref="ISeatRegister.VerifyChain"/> has
        /// no production caller at all, in this file or any other, so the only surface the refusal
        /// reaches here is the Error line below. The message is written for whoever reads that log
        /// (see <see cref="KeyAsideRefusedException"/>), and the day a screen calls VerifyChain the
        /// census in <c>AsideProducerCensusTests</c> fails so this copy gets read again.</para>
        /// </summary>
        private void RefuseUnlessPreserved(KeyAsideResult aside)
        {
            if (aside.SafeToOverwrite) return;

            var message = KeyAsideLifecycle.RefusalNotPreserved(
                _keyPath,
                "the seat history already signed under it could never be verified again",
                aside);
            _logger.LogError("[SEATS] {Message}", message);
            throw new KeyAsideRefusedException(message);
        }

        /// <summary>Routes <see cref="KeyAsideLifecycle"/>'s sentences into this service's log prefix.</summary>
        private void AsideLog(bool isError, Exception? error, string message)
        {
            if (isError) _logger.LogError(error, "[SEATS] {Message}", message);
            else _logger.LogWarning(error, "[SEATS] {Message}", message);
        }

        /// <summary>
        /// sig = HMACSHA256(key, prevSig + "|" + canonicalJson(entry)) — AuditLogService.ComputeSignature
        /// idiom, byte-for-byte. The signature excludes itself; prev_sig chains the row to its parent.
        /// </summary>
        private static string ComputeSignature(SeatEventRow e, string previousSig, byte[] key)
        {
            var canonical = new
            {
                e.Fingerprint,
                e.DisplayName,
                e.Alias,
                e.Event,
                e.EventUtc,
            };
            var payload = JsonSerializer.Serialize(canonical, CanonicalJson);
            var input = Encoding.UTF8.GetBytes(previousSig + "|" + payload);
            return Convert.ToBase64String(HMACSHA256.HashData(key, input));
        }

        // ── Read path (hot, lock-free) ───────────────────────────────────────────────

        /// <inheritdoc/>
        public bool IsSeated(string instanceName)
        {
            // Unlimited (legacy grandfather) => everything reports. This is the fail-OPEN ruling and
            // it must short-circuit BEFORE any lookup: a legacy install has no fingerprints yet, so
            // a lookup-first order would black out the estate on the very first run after upgrade.
            if (IsUnlimited) return true;

            if (string.IsNullOrWhiteSpace(instanceName)) return false;

            var names = _nameToFingerprint;
            if (!names.TryGetValue(instanceName, out var fp)) return false;   // never probed => not seated
            var cache = _cache;
            return cache.TryGetValue(fp, out var row) && row.State == SeatState.Seated;
        }

        /// <inheritdoc/>
        public SeatFilter Filter(IEnumerable<string> instanceNames)
        {
            var all = instanceNames?.Where(n => !string.IsNullOrWhiteSpace(n))
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToList() ?? new List<string>();

            if (IsUnlimited)
                return new SeatFilter(all, Array.Empty<string>());

            var seated = new List<string>(all.Count);
            var excluded = new List<string>();
            foreach (var n in all)
            {
                if (IsSeated(n)) seated.Add(n);
                else excluded.Add(n);
            }
            return new SeatFilter(seated, excluded);
        }

        /// <inheritdoc/>
        public bool IsLocked => Status().Locked;

        /// <inheritdoc/>
        public SeatSummary Status()
        {
            var cache = _cache;
            // Fingerprint tiebreaker: claims made in the same run share a claimedUtc (~15ms clock
            // resolution), and _cache is a Dictionary, so a bare claimedUtc sort would leave the
            // display/publish order at the mercy of dictionary iteration.
            var rows = cache.Values
                .OrderBy(r => r.ClaimedUtc)
                .ThenBy(r => r.Fingerprint, StringComparer.Ordinal)
                .ToList();

            if (IsUnlimited)
                return new SeatSummary(
                    Unlimited: true, Seats: 0, SeatsUsed: rows.Count(r => r.State == SeatState.Seated),
                    SwapsAllowed: SwapsAllowed, SwapsUsed: 0, Locked: false, LockReason: null,
                    Rows: rows, ChainHead: _chainHead);

            int n = Seats!.Value;
            int distinctEverClaimed = rows.Count;
            int swapsUsed = Math.Max(0, distinctEverClaimed - n);
            int seatsUsed = rows.Count(r => r.State == SeatState.Seated);
            bool locked = seatsUsed >= n && swapsUsed >= SwapsAllowed;

            return new SeatSummary(
                Unlimited: false, Seats: n, SeatsUsed: seatsUsed,
                SwapsAllowed: SwapsAllowed, SwapsUsed: swapsUsed,
                Locked: locked,
                LockReason: locked ? BuildLockReason(seatsUsed, n, swapsUsed) : null,
                Rows: rows, ChainHead: _chainHead);
        }

        /// <summary>
        /// The exact ruled copy. Honest, non-blaming, states the numbers and the one way forward.
        /// Adrian-approved wording — do not soften, dramatise, or add an upsell.
        /// </summary>
        private string BuildLockReason(int seatsUsed, int seats, int swapsUsed)
        {
            int swapsLeft = Math.Max(0, SwapsAllowed - swapsUsed);
            return $"{seatsUsed} of {seats} instance seats in use, {swapsLeft} swaps remaining — " +
                   "a new licence from sqldba is required to change instances.";
        }

        // ── Write path ───────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public SeatDecision ClaimOnProbe(InstanceFingerprint fingerprint, string instanceName)
        {
            if (fingerprint is null) throw new ArgumentNullException(nameof(fingerprint));

            var fp = fingerprint.Hash;

            lock (_lock)
            {
                // Map the results-key name to the fingerprint regardless of seat outcome, so the
                // report filter can resolve this instance. Rebuilt maps are swapped whole.
                MapName(instanceName, fp);

                var cache = _cache;
                if (cache.TryGetValue(fp, out var existing) && existing.State != SeatState.Released)
                {
                    // Already known and holding (or over-allocated). Idempotent — re-probing costs
                    // nothing, spends no swap, and normally appends nothing.
                    //
                    // EXCEPT on an alias change. The name->fingerprint join is rebuilt from the
                    // persisted alias column, so if this seated instance is now reached under a NEW
                    // name (operator retitled the connection, or switched to an FQDN), the new name
                    // would live only in memory and stop resolving after a restart — the seat would
                    // still exist but its results would drop. Append a claim event to persist the
                    // new alias. This keeps the ORIGINAL claimedUtc (see ReloadCache), so it neither
                    // grows D nor jumps the adoption queue: it records a rename, it does not re-seat.
                    if (!string.IsNullOrWhiteSpace(instanceName) &&
                        !string.Equals(existing.Alias, instanceName, StringComparison.OrdinalIgnoreCase))
                    {
                        AppendEvent(fp, fingerprint.DisplayName, instanceName, "claim");
                        _logger.LogInformation(
                            "[SEATS] Instance {Fp} is now reached as {Instance} (was {Old}) — seat unchanged.",
                            fingerprint.ShortHash, LogAnon.S(instanceName), LogAnon.S(existing.Alias));
                    }

                    return new SeatDecision(existing.State == SeatState.Seated, existing.State == SeatState.Seated
                        ? null
                        : "Recorded but not covered by your licence.");
                }

                if (IsUnlimited)
                {
                    // Unlimited: record the fingerprint (so the data exists when a real licence lands)
                    // and never block.
                    AppendEvent(fp, fingerprint.DisplayName, instanceName, "claim");
                    return new SeatDecision(true, null);
                }

                int n = Seats!.Value;
                var status = Status();
                bool isNewFingerprint = !cache.ContainsKey(fp);
                int distinctAfter = status.Rows.Count + (isNewFingerprint ? 1 : 0);
                int swapsAfter = Math.Max(0, distinctAfter - n);

                if (status.SeatsUsed >= n)
                {
                    // No free seat. Record the sighting honestly as over-allocated (the operator can
                    // see exactly which instance is uncovered) but do not report it.
                    AppendEvent(fp, fingerprint.DisplayName, instanceName, "claim");
                    return new SeatDecision(false, BuildLockReason(status.SeatsUsed, n, status.SwapsUsed));
                }

                if (swapsAfter > SwapsAllowed)
                {
                    // A seat IS free, but this is an instance the register has never seen, and
                    // covering a NEW instance spends a swap the licence no longer has. Say exactly
                    // that: reusing the seats-full copy here would claim "0 of 1 seats in use — a new
                    // licence is required to change instances", which is confusing at best (a seat is
                    // plainly free) and misleading at worst.
                    AppendEvent(fp, fingerprint.DisplayName, instanceName, "claim");
                    return new SeatDecision(false,
                        $"This is a new instance and all {SwapsAllowed} instance swaps on this licence " +
                        "have been used — a new licence from sqldba is required to change instances.");
                }

                AppendEvent(fp, fingerprint.DisplayName, instanceName, "claim");
                _logger.LogInformation(
                    "[SEATS] Claimed seat for {Instance} (fp {Fp}) — {Used}/{Seats} seats, {Swaps}/{Allowed} swaps.",
                    LogAnon.S(instanceName), fingerprint.ShortHash, Status().SeatsUsed, n, swapsAfter, SwapsAllowed);
                return new SeatDecision(true, null);
            }
        }

        /// <inheritdoc/>
        public SeatDecision Release(string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint))
                return new SeatDecision(false, "No instance fingerprint supplied.");

            lock (_lock)
            {
                var cache = _cache;
                if (!cache.TryGetValue(fingerprint, out var row))
                    return new SeatDecision(false, "That instance is not in the seat register.");
                if (row.State == SeatState.Released)
                    return new SeatDecision(true, null);   // idempotent

                AppendEvent(fingerprint, row.DisplayName, row.Alias, "release");
                _logger.LogInformation("[SEATS] Released seat {Fp} ({Display}).",
                    fingerprint.Length >= 12 ? fingerprint[..12] : fingerprint, LogAnon.S(row.DisplayName));
                return new SeatDecision(true, null);
            }
        }

        /// <inheritdoc/>
        public SeatDecision ReleaseInstance(string instanceName)
        {
            if (string.IsNullOrWhiteSpace(instanceName))
                return new SeatDecision(false, "No instance name supplied.");

            // Never probed => no fingerprint => it holds no seat. Nothing to free, and that is a
            // SUCCESS: an unreachable server that was configured and then removed cost the licence
            // nothing on the way in and must cost it nothing on the way out.
            var names = _nameToFingerprint;
            if (!names.TryGetValue(instanceName, out var fp))
                return new SeatDecision(true, null);

            return Release(fp);
        }

        /// <inheritdoc/>
        public SeatDecision CanAdmit(int resultingInstanceCount)
        {
            if (IsUnlimited) return new SeatDecision(true, null);

            int n = Seats!.Value;
            if (resultingInstanceCount <= n) return new SeatDecision(true, null);

            return new SeatDecision(false,
                $"That would configure {resultingInstanceCount} instances against a {n}-instance licence. " +
                "Remove an instance first, or contact sqldba for a larger licence.");
        }

        /// <summary>
        /// Appends one chained event and rebuilds the cache. Caller holds _lock.
        /// Writes the chain link INSIDE a transaction with the tail read, so two concurrent appends
        /// cannot both read the same prev_sig and fork the chain.
        /// </summary>
        private void AppendEvent(string fingerprint, string displayName, string alias, string evt)
        {
            try
            {
                var key = GetOrCreateHmacKey();
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var tx = conn.BeginTransaction();

                string prevSig;
                using (var tail = conn.CreateCommand())
                {
                    tail.Transaction = tx;
                    tail.CommandText = "SELECT sig FROM seat_events ORDER BY seq DESC LIMIT 1;";
                    prevSig = tail.ExecuteScalar() as string ?? string.Empty;
                }

                var row = new SeatEventRow
                {
                    Fingerprint = fingerprint,
                    DisplayName = displayName ?? string.Empty,
                    Alias = alias ?? string.Empty,
                    Event = evt,
                    EventUtc = DateTime.UtcNow.ToString("O"),
                };
                var sig = ComputeSignature(row, prevSig, key);

                using (var ins = conn.CreateCommand())
                {
                    ins.Transaction = tx;
                    ins.CommandText = @"
                        INSERT INTO seat_events (fingerprint, display_name, alias, event, event_utc, prev_sig, sig)
                        VALUES ($fp, $display, $alias, $evt, $utc, $prev, $sig);";
                    ins.Parameters.AddWithValue("$fp", row.Fingerprint);
                    ins.Parameters.AddWithValue("$display", row.DisplayName);
                    ins.Parameters.AddWithValue("$alias", row.Alias);
                    ins.Parameters.AddWithValue("$evt", row.Event);
                    ins.Parameters.AddWithValue("$utc", row.EventUtc);
                    ins.Parameters.AddWithValue("$prev", prevSig);
                    ins.Parameters.AddWithValue("$sig", sig);
                    ins.ExecuteNonQuery();
                }

                tx.Commit();
            }
            catch (Exception ex)
            {
                // Fail-OPEN on persistence (DemoRunLedger idiom): a store we cannot write must not
                // block the operator's estate. The seat simply is not recorded and the failure is
                // logged here, rather than silently granting.
                _logger.LogError(ex, "[SEATS] Failed to append seat event for {Fp}",
                    fingerprint.Length >= 12 ? fingerprint[..12] : fingerprint);
            }

            ReloadCache();
        }

        // ── Cache projection ─────────────────────────────────────────────────────────

        /// <summary>
        /// Rebuilds the in-memory snapshot by replaying the chain in seq order. This is where the
        /// "first N by claimedUtc stay seated" adoption rule is applied, so it holds uniformly for a
        /// legacy estate meeting its first licence AND for any later drift.
        ///
        /// DETERMINISM: ordering is (claimedUtc, firstSeq), never claimedUtc alone. DateTime.UtcNow
        /// has ~15ms resolution, so several instances claimed in one run routinely share a
        /// timestamp; with a bare claimedUtc sort the tie would be broken by Dictionary iteration
        /// order, which is not a guaranteed order — meaning WHICH instances stayed seated could
        /// differ between restarts. firstSeq is the store's AUTOINCREMENT key: unique, monotonic,
        /// persisted, and exactly the order the claims were appended.
        /// </summary>
        private void ReloadCache()
        {
            var latest = new Dictionary<string, (string Display, string Alias, DateTime Claimed, long FirstSeq, DateTime? Released, bool Held)>(StringComparer.OrdinalIgnoreCase);
            // Rebuilt from the persisted alias column, NOT carried over from the live map: the
            // name→fingerprint join must survive a restart. Without this, a licensed install would
            // resolve NOTHING as seated after a restart until every server happened to be probed
            // again — i.e. the estate's reports would silently go dark. The alias column is written
            // on every claim precisely so this projection is possible.
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var head = string.Empty;

            try
            {
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT fingerprint, display_name, alias, event, event_utc, sig, seq
                                    FROM seat_events ORDER BY seq ASC;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var fp = reader.GetString(0);
                    var display = reader.GetString(1);
                    var alias = reader.GetString(2);
                    var evt = reader.GetString(3);
                    var utc = ParseUtc(reader.GetString(4));
                    head = reader.GetString(5);
                    var seq = reader.GetInt64(6);

                    // Later events win: if an instance NAME is ever repointed at a different box,
                    // the newest claim under that name owns the join (and the old fingerprint keeps
                    // its own seat row until released).
                    if (evt == "claim" && !string.IsNullOrWhiteSpace(alias))
                        names[alias] = fp;

                    if (evt == "claim")
                    {
                        // First claim anchors claimedUtc AND firstSeq; a RE-claim of a released
                        // fingerprint (or a rename) keeps both, so it does not jump the adoption
                        // queue ahead of instances seated continuously for longer.
                        if (latest.TryGetValue(fp, out var prior))
                            latest[fp] = (display, alias, prior.Claimed, prior.FirstSeq, null, true);
                        else
                            latest[fp] = (display, alias, utc, seq, null, true);
                    }
                    else if (evt == "release" && latest.TryGetValue(fp, out var cur))
                    {
                        latest[fp] = (cur.Display, cur.Alias, cur.Claimed, cur.FirstSeq, utc, false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SEATS] Failed to rebuild seat cache; treating register as empty");
            }

            // Apply the seat cap in (claimedUtc, firstSeq) order — oldest claims keep their seats.
            // The firstSeq tiebreaker is what makes this deterministic across restarts; see the
            // method comment.
            var seats = Seats;
            var seatedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (seats is null)
            {
                // Unlimited (absent seats field): every held fingerprint keeps a seat, and the swap
                // budget is irrelevant because an unlimited licence never needs to release.
                foreach (var kv in latest.Where(kv => kv.Value.Held)) seatedSet.Add(kv.Key);
            }
            else
            {
                // The swap budget is enforced HERE, not only in ClaimOnProbe, because the ledger is
                // PERSISTED: a claim recorded while swaps were exhausted (the branch records the
                // sighting honestly, then refuses) must not be silently re-derived as Seated by the
                // next restart's rebuild. A licence covers at most (seats + swapsAllowed) DISTINCT
                // fingerprints over its term; rank by firstSeq — first-sighting order — so the
                // ceiling is stable across restarts and a later sighting can never displace an
                // earlier one by winning a seat freed after the budget was already spent.
                var withinBudget = new HashSet<string>(
                    latest.OrderBy(kv => kv.Value.FirstSeq)
                          .Take(seats.Value + SwapsAllowed)
                          .Select(kv => kv.Key),
                    StringComparer.OrdinalIgnoreCase);

                var held = latest.Where(kv => kv.Value.Held && withinBudget.Contains(kv.Key))
                                 .OrderBy(kv => kv.Value.Claimed)
                                 .ThenBy(kv => kv.Value.FirstSeq)
                                 .ToList();
                foreach (var kv in held.Take(seats.Value)) seatedSet.Add(kv.Key);
            }

            var next = new Dictionary<string, SeatRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in latest)
            {
                var state = !kv.Value.Held ? SeatState.Released
                          : seatedSet.Contains(kv.Key) ? SeatState.Seated
                          : SeatState.OverAllocated;
                next[kv.Key] = new SeatRow(kv.Key, kv.Value.Display, kv.Value.Alias,
                    kv.Value.Claimed, kv.Value.Released, state);
            }

            _cache = next;
            // Merge the live map over the persisted projection: a name mapped THIS session by a probe
            // whose append failed (fail-open persistence) still resolves until restart, rather than
            // being erased by a reload that could not see it on disk.
            foreach (var kv in _nameToFingerprint)
                names[kv.Key] = kv.Value;
            _nameToFingerprint = names;
            _chainHead = head;
            SeatsChanged?.Invoke();
        }

        private void MapName(string instanceName, string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(instanceName)) return;
            var next = new Dictionary<string, string>(_nameToFingerprint, StringComparer.OrdinalIgnoreCase)
            {
                [instanceName] = fingerprint,
            };
            _nameToFingerprint = next;
        }

        private static DateTime ParseUtc(string s) =>
            DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var d)
                ? d
                : DateTime.MinValue;

        // ── Verification ─────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public string? VerifyChain()
        {
            try
            {
                var key = GetOrCreateHmacKey();
                using var conn = SqliteCipherHelper.OpenEncrypted(_connectionString);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT seq, fingerprint, display_name, alias, event, event_utc, prev_sig, sig
                                    FROM seat_events ORDER BY seq ASC;";
                using var reader = cmd.ExecuteReader();

                var expectedPrev = string.Empty;
                while (reader.Read())
                {
                    var seq = reader.GetInt64(0);
                    var row = new SeatEventRow
                    {
                        Fingerprint = reader.GetString(1),
                        DisplayName = reader.GetString(2),
                        Alias = reader.GetString(3),
                        Event = reader.GetString(4),
                        EventUtc = reader.GetString(5),
                    };
                    var storedPrev = reader.GetString(6);
                    var storedSig = reader.GetString(7);

                    if (!CryptographicOperations.FixedTimeEquals(
                            Encoding.UTF8.GetBytes(storedPrev), Encoding.UTF8.GetBytes(expectedPrev)))
                        return $"Seat register chain broken at entry {seq}: previous-signature link does not match.";

                    var recomputed = ComputeSignature(row, storedPrev, key);
                    // Repo rule: constant-time on HMAC comparisons. Lengths are equal for two valid
                    // base64 SHA-256s; FixedTimeEquals returns false on a length mismatch anyway.
                    if (!CryptographicOperations.FixedTimeEquals(
                            Encoding.UTF8.GetBytes(recomputed), Encoding.UTF8.GetBytes(storedSig)))
                        return $"Seat register chain broken at entry {seq}: entry has been modified since it was written.";

                    expectedPrev = storedSig;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SEATS] Chain verification could not complete");
                // Do NOT report "intact" when we could not check — that would be a fabricated
                // assurance (DD rule). Say honestly that verification failed.
                return "Seat register chain could not be verified: " + ex.Message;
            }
        }

        /// <summary>Row shape used for signing + verification. Mirrors the table columns that are signed.</summary>
        private sealed class SeatEventRow
        {
            public string Fingerprint { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public string Alias { get; set; } = string.Empty;
            public string Event { get; set; } = string.Empty;
            public string EventUtc { get; set; } = string.Empty;
        }
    }
}
