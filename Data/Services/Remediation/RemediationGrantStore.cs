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

        public RemediationGrantStore() : this(null) { }

        // pathOverride is for tests; production uses the Config/ path.
        public RemediationGrantStore(string? pathOverride)
        {
            _filePath = pathOverride ?? Path.Combine(AppContext.BaseDirectory, "Config", "remediation-grants.json");
            _data = ConfigFileHelper.Load<RemediationGrantStoreData>(_filePath);
        }

        /// <summary>True if this nonce has already been redeemed (replay).</summary>
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
                ConfigFileHelper.Save(_filePath, _data);
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
