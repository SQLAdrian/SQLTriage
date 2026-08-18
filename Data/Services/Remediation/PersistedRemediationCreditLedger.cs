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

        public PersistedRemediationCreditLedger(
            IBundleAccessor bundle,
            ILogger<PersistedRemediationCreditLedger> logger,
            RemediationGrantStore? grants = null)
        {
            _bundle = bundle;
            _logger = logger;
            _grants = grants;
            _path = Path.Combine(AppContext.BaseDirectory, "Config", "remediation-credit-ledger.json");
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
                int alloc = AllocationPerServer(serverName);
                int spent = _spent.TryGetValue(serverName, out var s) ? s : 0;
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
                int alloc = AllocationPerServer(serverName);
                int spent = _spent.TryGetValue(serverName, out var s) ? s : 0;
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

        private void Load()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    _logger.LogInformation("No remediation credit ledger at {Path}; starting fresh (per-server spend = 0).", _path);
                    return;
                }
                var payload = JsonSerializer.Deserialize<LedgerPayload>(File.ReadAllText(_path), _json);
                if (payload?.Spent == null) return;
                lock (_lock)
                {
                    _spent = new Dictionary<string, int>(payload.Spent, StringComparer.OrdinalIgnoreCase);
                }
                _logger.LogInformation("Loaded remediation credit ledger ({Count} server(s) with spend).", _spent.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load remediation credit ledger from {Path}", _path);
            }
        }

        // Atomic persist (tmp -> delete -> move), mirroring RemediationWeightStore.
        private void Save()
        {
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
