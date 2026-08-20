/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationGrantStore — the persisted grant-layer that sits ABOVE the signed bundle
 * allocation. A redeemed signed-allocation artifact adds credits for a server here; the
 * credit ledger reads GrantedCreditsFor() and adds it to the bundle allocation.
 *
 * Replay-proof: the artifact nonce is the key — a nonce already in the store is rejected, so
 * the same allocation file can never be redeemed twice. Expired grants stop counting (the
 * ledger allocation shrinks when they lapse). Verification happens in SignedAllocationVerifier;
 * this store only persists what was already verified. Atomic JSON persist via ConfigFileHelper.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SQLTriage.Data;

namespace SQLTriage.Data.Services.Remediation
{
    public sealed class GrantRecord
    {
        public string Server { get; set; } = string.Empty;
        public int Credits { get; set; }
        public DateTime IssuedUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
        public string Nonce { get; set; } = string.Empty;
        public DateTime RedeemedAtUtc { get; set; }
    }

    public sealed class RemediationGrantStoreData
    {
        public int Version { get; set; } = 1;
        public List<GrantRecord> Grants { get; set; } = new();
    }

    public sealed class RemediationGrantStore
    {
        private readonly string _filePath;
        private readonly object _lock = new();
        private RemediationGrantStoreData _data;

        /// <summary>
        /// How the ledger came off disk. Load-bearing, and not merely for the write: this store's
        /// whole job is to say whether a nonce has been seen before, and a damaged file
        /// deserialises to an EMPTY grant list, which answers "never seen" for every nonce ever
        /// redeemed. That is a replay guard that opens when a file is corrupted — the same shape as
        /// the truncated user store that made an install look brand new to a security predicate
        /// (see <c>RbacService.IsBootstrapEligible</c>, 2026-08-03).
        ///
        /// <para>So the guard here is on the READ as well as the write, and it is the read that
        /// matters most: <see cref="IsRedeemed"/> would say false, <see cref="Redeem"/> would accept
        /// the replay, and the save would then write a ledger containing only the replayed grant —
        /// destroying the record of every nonce before it, in the same stroke, with the evidence.
        /// Nothing on the machine can rebuild that list.</para>
        ///
        /// <para><see cref="ConfigLoadOutcome.Missing"/> is NOT damage: no file means nothing has
        /// ever been redeemed here, which is the truth on a fresh install and must keep working.</para>
        /// </summary>
        private readonly ConfigLoadOutcome _load;

        private readonly string? _quarantine;

        public RemediationGrantStore() : this(null) { }

        // pathOverride is for tests; production uses the Config/ path.
        public RemediationGrantStore(string? pathOverride)
        {
            _filePath = pathOverride ?? Path.Combine(AppContext.BaseDirectory, "Config", "remediation-grants.json");
            _data = ConfigFileHelper.Load<RemediationGrantStoreData>(_filePath, null, out _load, out _quarantine);

            if (IsDamaged)
                Serilog.Log.Error(
                    "[Remediation] {Path} exists and did not load ({Outcome}). The redeemed-allocation ledger is "
                    + "NOT available, so replay protection cannot be honoured and no allocation will be redeemed "
                    + "until it is repaired. Credits already granted are also not counted while this stands. {Recovery}",
                    _filePath, _load, ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine));
        }

        /// <summary>True when the ledger exists and did not load — so this process cannot tell a redeemed nonce from a new one.</summary>
        public bool IsDamaged => _load is ConfigLoadOutcome.Unreadable or ConfigLoadOutcome.Empty;

        /// <summary>What to do about it. The ONE register — never a locally written sentence.</summary>
        public string DescribeStoreRecovery() => ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine);

        /// <summary>
        /// True if this nonce has already been redeemed (replay).
        ///
        /// <para>No caller today, and it is left deliberately unguarded rather than made to fail
        /// closed, because a bare bool cannot express the third state: on a damaged ledger the
        /// honest answer is "cannot tell", and both <c>true</c> and <c>false</c> would be a
        /// fabricated fact — <c>false</c> clears a replay, <c>true</c> rejects a legitimate first
        /// redemption. Anything that needs this answer under damage must call <see cref="Redeem"/>,
        /// which refuses and SAYS WHY, or test <see cref="IsDamaged"/> first. Stated here because a
        /// future caller will otherwise read the guard on Redeem as covering this too.</para>
        /// </summary>
        public bool IsRedeemed(string nonce)
        {
            if (string.IsNullOrWhiteSpace(nonce)) return false;
            lock (_lock)
                return _data.Grants.Any(g => string.Equals(g.Nonce, nonce, StringComparison.Ordinal));
        }

        /// <summary>
        /// Records a VERIFIED allocation. Returns null on success, or an error string if the
        /// nonce was already redeemed (replay). Caller must have verified the signature first.
        /// </summary>
        public string? Redeem(SignedAllocation a, DateTime nowUtc)
        {
            if (a is null) return "No allocation.";
            if (string.IsNullOrWhiteSpace(a.Nonce)) return "Allocation has no nonce.";

            // Fail CLOSED, before the nonce check rather than after it, because the nonce check is
            // what cannot be trusted: on a damaged ledger it consults an empty list and clears every
            // replay. Refusing costs a legitimate operator one repair on a file that is already
            // failing them (their existing credits are not being counted either); accepting would
            // hand out credits for an allocation already spent AND overwrite the ledger proving it.
            if (IsDamaged)
                return "This install's redeemed-allocation record exists and could not be read, so it cannot be "
                     + "checked for replay and must not be overwritten. Nothing was redeemed. "
                     + DescribeStoreRecovery();

            lock (_lock)
            {
                if (_data.Grants.Any(g => string.Equals(g.Nonce, a.Nonce, StringComparison.Ordinal)))
                    return "This allocation has already been redeemed (replay rejected).";
                _data.Grants.Add(new GrantRecord
                {
                    Server = a.Server,
                    Credits = a.Credits,
                    IssuedUtc = a.IssuedUtc,
                    ExpiresUtc = a.ExpiresUtc,
                    Nonce = a.Nonce,
                    RedeemedAtUtc = nowUtc,
                });

                try
                {
                    ConfigFileHelper.Save(_filePath, _data);
                }
                catch (Exception ex)
                {
                    // Rolled back so memory matches disk. Keeping it would grant credits this
                    // process cannot prove were granted and would forget the nonce at restart —
                    // a replay window opened by a full disk.
                    _data.Grants.RemoveAt(_data.Grants.Count - 1);
                    Serilog.Log.Error(ex, "[Remediation] Failed to write {Path} — allocation NOT redeemed", _filePath);
                    return "The allocation could not be recorded (the file could not be written), so it has not "
                         + "been redeemed. Check the disk and permissions, then try again.";
                }
                return null;
            }
        }

        /// <summary>Sum of non-expired redeemed grant credits for a server (added to the bundle allocation).</summary>
        public int GrantedCreditsFor(string serverName, DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(serverName)) return 0;
            lock (_lock)
                return _data.Grants
                    .Where(g => string.Equals(g.Server, serverName, StringComparison.OrdinalIgnoreCase) && g.ExpiresUtc >= nowUtc)
                    .Sum(g => g.Credits);
        }
    }
}
