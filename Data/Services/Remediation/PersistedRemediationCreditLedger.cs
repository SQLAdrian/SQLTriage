/* In the name of God, the Merciful, the Compassionate */
/*
 * PersistedRemediationCreditLedger — gate 3, production. The per-server "change credit"
 * ledger: each server's allowance is the SIGNED MSP per-server allocation carried in the
 * bundle (BundleFeatures.RemediationCreditsPerServer), and consumption is debited per
 * SUCCESSFUL apply. Preview/approve are free; a no-op / could-not-run / rolled-back apply
 * refunds.
 *
 *   Available(server) = max(0, allocation - committed(server) - outstanding(server))
 *
 * Only the COMMITTED spend per server is persisted (atomic tmp->delete->move, mirroring
 * RemediationWeightStore). Outstanding reservations are in-memory: a crash between reserve
 * and commit simply frees the reservation — credits are never lost, never double-spent.
 *
 * The allocation is read LIVE from the bundle, so re-licensing (more/fewer credits) takes
 * effect immediately. DevBridge (dev machine) gets a dev allotment so the production surface
 * is testable without a stamped bundle; real installs honour only the signed allocation.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Services.Licensing;

namespace SQLTriage.Data.Services.Remediation
{
    public sealed class PersistedRemediationCreditLedger : IRemediationCreditLedger
    {
        private const int DevAllotmentPerServer = 25; // dev-machine testing only (DevBridge)

        private readonly IBundleAccessor _bundle;
        private readonly ILogger<PersistedRemediationCreditLedger> _logger;
        private readonly RemediationGrantStore? _grants;
        private readonly string _path;
        private readonly object _lock = new();

        // Persisted: committed spend per server. Available is derived from the live allocation.
        private Dictionary<string, int> _spent = new(StringComparer.OrdinalIgnoreCase);
        // In-memory only: live reservations not yet committed.
        private readonly ConcurrentDictionary<string, (string Server, int Cost)> _outstanding = new(StringComparer.Ordinal);

        private static readonly JsonSerializerOptions _json = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        /// <param name="pathOverride">
        /// Test seam (house pattern — RemediationGrantStore's pathOverride): null ⇒ the real
        /// Config/ path, exactly as before.
        /// </param>
        public PersistedRemediationCreditLedger(
            IBundleAccessor bundle,
            ILogger<PersistedRemediationCreditLedger> logger,
            RemediationGrantStore? grants = null,
            string? pathOverride = null)
        {
            _bundle = bundle;
            _logger = logger;
            _grants = grants;
            _path = pathOverride ?? Path.Combine(AppContext.BaseDirectory, "Config", "remediation-credit-ledger.json");
            Load();
        }

        public string LedgerPath => _path;

        // The live per-server allocation = the signed bundle allocation PLUS any non-expired
        // redeemed grants for this server (the redeem-signed-allocation-file path). DevBridge
        // floors the BUNDLE part at a dev allotment so the surface is testable on a dev build;
        // grants stack on top. Per-server because grants are issued per server.
        private int AllocationPerServer(string serverName)
        {
            var signed = Math.Max(0, _bundle.Features.RemediationCreditsPerServer);
            var bundleAlloc = SQLTriage.Data.BuildMode.DevBridgeActive ? Math.Max(DevAllotmentPerServer, signed) : signed;
            var granted = _grants?.GrantedCreditsFor(serverName, DateTime.UtcNow) ?? 0;
            return bundleAlloc + granted;
        }

        public int AvailableFor(string serverName)
        {
            if (string.IsNullOrWhiteSpace(serverName)) return 0;
            lock (_lock)
            {
                // Fail CLOSED on a damaged ledger — see _load. The alternative is not "assume zero
                // spend", it is "fabricate a fact about how much this customer has already used".
                if (IsStoreDamaged) return 0;

                int alloc = AllocationPerServer(serverName);
                int spent = _spent.TryGetValue(serverName, out var s) ? Math.Max(0, s) : 0;  // H3: never let negative spend inflate credit
                int outstanding = _outstanding.Values
                    .Where(o => string.Equals(o.Server, serverName, StringComparison.OrdinalIgnoreCase))
                    .Sum(o => o.Cost);
                return Math.Max(0, alloc - spent - outstanding);
            }
        }

        public CreditBreakdown GetBreakdown(string serverName)
        {
            if (string.IsNullOrWhiteSpace(serverName)) return new CreditBreakdown(0, 0, 0, 0);
            lock (_lock)
            {
                // Everything withheld, including the allocation, because with spend unknown every
                // other number on this breakdown would be a figure this process cannot support.
                if (IsStoreDamaged) return new CreditBreakdown(0, 0, 0, 0);

                int alloc = AllocationPerServer(serverName);
                int spent = _spent.TryGetValue(serverName, out var s) ? Math.Max(0, s) : 0;  // H3: never let negative spend inflate credit
                int outstanding = _outstanding.Values
                    .Where(o => string.Equals(o.Server, serverName, StringComparison.OrdinalIgnoreCase))
                    .Sum(o => o.Cost);
                return new CreditBreakdown(alloc, spent, outstanding, Math.Max(0, alloc - spent - outstanding));
            }
        }

        public CreditReservation? Reserve(string serverName, int cost)
        {
            if (string.IsNullOrWhiteSpace(serverName)) return null;
            if (cost <= 0) cost = 1;
            lock (_lock) // Monitor is re-entrant: AvailableFor re-takes _lock on this thread safely.
            {
                if (AvailableFor(serverName) < cost) return null;
                var res = new CreditReservation(Guid.NewGuid().ToString("N"), serverName, cost);
                _outstanding[res.Id] = (serverName, cost);
                return res;
            }
        }

        public void Commit(CreditReservation reservation)
        {
            if (reservation is null) return;
            // Move the held reservation into persisted spend. Idempotent (a second commit,
            // or commit-after-refund, finds nothing outstanding and is a no-op).
            if (_outstanding.TryRemove(reservation.Id, out var held))
            {
                lock (_lock)
                {
                    _spent[held.Server] = (_spent.TryGetValue(held.Server, out var s) ? s : 0) + held.Cost;
                    Save();
                }
            }
        }

        public void Refund(CreditReservation reservation)
        {
            if (reservation is null) return;
            // Drop the in-flight reservation; nothing was persisted, so available is restored.
            _outstanding.TryRemove(reservation.Id, out _);
        }

        /// <summary>
        /// How the ledger came off disk.
        ///
        /// <para><b>TIER 1, and the read is what matters, not the write.</b> This file records
        /// committed remediation spend per server, and available credit is
        /// <c>allocation − spent − outstanding</c>. A damaged ledger deserialises to an EMPTY spend
        /// map, which is indistinguishable from "this server has spent nothing" — so corrupting one
        /// plaintext file restores a customer's entire paid allocation, on every server, for free.
        /// That is a COMMERCIAL control that OPENS on corruption: the same shape as the truncated
        /// grant ledger that answered "never redeemed" for every nonce, and as the truncated user
        /// store that made an install look brand new to a security predicate.</para>
        ///
        /// <para>Unlike <c>DemoRunLedger</c>, whose fail-open is deliberate and documented, nothing
        /// here ever said this was intended. It was not — it was a <c>catch</c> that logged a
        /// warning and left the field at its default.</para>
        ///
        /// <para><b>So this one fails CLOSED on the read</b> (<see cref="AvailableFor"/> returns 0,
        /// <see cref="Reserve"/> refuses) rather than refusing the write, and the difference is not
        /// cosmetic: refusing only the Save would leave every apply free AND unrecorded, which is
        /// strictly worse than the bug. Refusing the write as well is then automatic — nothing can
        /// reserve, so nothing can commit, so nothing overwrites the damaged file and the operator's
        /// real spend record survives to be restored.</para>
        ///
        /// <para><see cref="ConfigLoadOutcome.Missing"/> is NOT damage: no ledger means nothing has
        /// been spent on this install, which is the truth on a fresh one.</para>
        /// </summary>
        private ConfigLoadOutcome _load = ConfigLoadOutcome.Missing;

        private string? _quarantine;

        /// <summary>True when the ledger exists and did not load, so recorded spend is unknown and credit is withheld.</summary>
        public bool IsStoreDamaged { get { lock (_lock) return _load is ConfigLoadOutcome.Unreadable or ConfigLoadOutcome.Empty; } }

        /// <summary>What to do about it. The ONE register — never a locally written sentence.</summary>
        public string DescribeStoreRecovery()
        {
            lock (_lock) return ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine);
        }

        private void Load()
        {
            try
            {
                var payload = ConfigFileHelper.Load<LedgerPayload>(_path, _json, out _load, out _quarantine);

                if (_load == ConfigLoadOutcome.Missing)
                {
                    _logger.LogInformation("No remediation credit ledger at {Path}; starting fresh (per-server spend = 0).", _path);
                    return;
                }

                if (IsStoreDamaged)
                {
                    _logger.LogError(
                        "[Remediation] {Path} exists and did not load ({Outcome}). Recorded remediation spend is "
                        + "UNKNOWN, so NO credit will be issued on this install and no apply will be permitted "
                        + "until it is repaired — reading it as zero spend would hand back every credit already "
                        + "used. The ledger is not being overwritten. {Recovery}",
                        _path, _load, ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine));
                    return;
                }

                if (payload?.Spent == null) return;
                lock (_lock)
                {
                    // H3 (2026-07-07): spent is read from a plaintext file a licensed user can edit.
                    // A negative value would make (alloc - spent) exceed the signed allocation and
                    // grant unlimited production remediation applies. Clamp every entry to >= 0 on
                    // load so a tampered/corrupt negative can never inflate available credit.
                    _spent = payload.Spent.ToDictionary(
                        kv => kv.Key, kv => Math.Max(0, kv.Value), StringComparer.OrdinalIgnoreCase);
                }
                _logger.LogInformation("Loaded remediation credit ledger ({Count} server(s) with spend).", _spent.Count);
            }
            catch (Exception ex)
            {
                // Fail closed here too: an unexpected read fault leaves spend unknown, and unknown
                // spend must not read as zero spend.
                _load = ConfigLoadOutcome.Unreadable;
                _logger.LogWarning(ex, "Failed to load remediation credit ledger from {Path}", _path);
            }
        }

        // Atomic persist (tmp -> delete -> move), mirroring RemediationWeightStore.
        private void Save()
        {
            // Unreachable while the read guard holds — nothing can reserve on a damaged ledger, so
            // nothing can commit — and stated anyway, because the read guard is a predicate in
            // another method and a future caller that commits directly would silently erase the
            // spend record this file exists to hold.
            if (ConfigFileHelper.WouldOverwriteUnreadStore(_load, StoreWriteIntent.FromLoadedStore))
            {
                _logger.LogError(
                    "[Remediation] REFUSED a write to {Path}: it exists and did not load ({Outcome}), so writing "
                    + "the spend this process holds would erase the record of every credit already used. {Recovery}",
                    _path, _load, ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine));
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var payload = new LedgerPayload
                {
                    SchemaVersion = 1,
                    LastUpdatedUtc = DateTime.UtcNow,
                    Spent = new Dictionary<string, int>(_spent, StringComparer.OrdinalIgnoreCase),
                };
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(payload, _json));
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(tmp, _path);
                _load = ConfigLoadOutcome.Loaded;
                _quarantine = null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist remediation credit ledger to {Path}", _path);
            }
        }

        private sealed class LedgerPayload
        {
            [JsonPropertyName("schemaVersion")]
            public int SchemaVersion { get; set; } = 1;

            [JsonPropertyName("lastUpdatedUtc")]
            public DateTime LastUpdatedUtc { get; set; }

            [JsonPropertyName("spent")]
            public Dictionary<string, int> Spent { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
