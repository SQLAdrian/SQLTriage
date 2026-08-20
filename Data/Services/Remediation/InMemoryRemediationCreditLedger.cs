/* In the name of God, the Merciful, the Compassionate */
/*
 * InMemoryRemediationCreditLedger — non-persisted, per-server credit ledger used by
 * tests and the dev RemediationLab. Each server starts with `initialCreditsPerServer`
 * credits (seeded on first touch). The production ledger is PersistedRemediationCreditLedger
 * (atomic persist, seeded from the signed bundle allocation).
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SQLTriage.Data.Services.Remediation
{
    public sealed class InMemoryRemediationCreditLedger : IRemediationCreditLedger
    {
        private readonly object _lock = new();
        private readonly int _initialPerServer;
        private readonly Dictionary<string, int> _balances = new(StringComparer.OrdinalIgnoreCase);
        // Track live reservations so Commit/Refund are idempotent and can't
        // double-refund a credit back into circulation.
        private readonly ConcurrentDictionary<string, (string Server, int Cost)> _outstanding = new(StringComparer.Ordinal);

        public InMemoryRemediationCreditLedger(int initialCreditsPerServer = 0)
        {
            _initialPerServer = Math.Max(0, initialCreditsPerServer);
        }

        // Caller holds _lock. Seeds the per-server balance on first touch.
        private int BalanceOf(string server)
        {
            if (!_balances.TryGetValue(server, out var b)) { b = _initialPerServer; _balances[server] = b; }
            return b;
        }

        public int AvailableFor(string serverName)
        {
            if (string.IsNullOrWhiteSpace(serverName)) return 0;
            lock (_lock) return BalanceOf(serverName);
        }

        public CreditReservation? Reserve(string serverName, int cost)
        {
            if (string.IsNullOrWhiteSpace(serverName)) return null;
            if (cost <= 0) cost = 1;
            lock (_lock)
            {
                var bal = BalanceOf(serverName);
                if (bal < cost) return null;
                _balances[serverName] = bal - cost;
                var res = new CreditReservation(Guid.NewGuid().ToString("N"), serverName, cost);
                _outstanding[res.Id] = (serverName, cost);
                return res;
            }
        }

        public void Commit(CreditReservation reservation)
        {
            if (reservation is null) return;
            // Consume the reservation; credits stay spent. Idempotent.
            _outstanding.TryRemove(reservation.Id, out _);
        }

        public void Refund(CreditReservation reservation)
        {
            if (reservation is null) return;
            // Only refund a reservation we still hold (idempotent — a second
            // refund, or a refund after commit, is a no-op).
            if (_outstanding.TryRemove(reservation.Id, out var held))
            {
                lock (_lock) _balances[held.Server] = BalanceOf(held.Server) + held.Cost;
            }
        }

        public CreditBreakdown GetBreakdown(string serverName)
        {
            if (string.IsNullOrWhiteSpace(serverName)) return new CreditBreakdown(0, 0, 0, 0);
            lock (_lock)
            {
                int available = BalanceOf(serverName);
                int outstanding = 0;
                foreach (var o in _outstanding.Values)
                    if (string.Equals(o.Server, serverName, StringComparison.OrdinalIgnoreCase)) outstanding += o.Cost;
                // This (test/dev) ledger decrements balance on reserve and does not track
                // committed separately; report the seed as allocation and derive committed.
                int allocation = _initialPerServer;
                int committed = Math.Max(0, allocation - available - outstanding);
                return new CreditBreakdown(allocation, committed, outstanding, available);
            }
        }

        /// <summary>Test/admin helper to grant credits to a server.</summary>
        public void Grant(string serverName, int credits)
        {
            if (string.IsNullOrWhiteSpace(serverName) || credits <= 0) return;
            lock (_lock) _balances[serverName] = BalanceOf(serverName) + credits;
        }
    }

    /// <summary>
    /// Step-4 capability adapter for gate 2. Defaults to denied; a later step
    /// wires this to IFeatureGate + the bundle remediation claim. Kept as its own
    /// type so the runner depends on the seam, not on licensing internals.
    /// </summary>
    public sealed class DeniedRemediationCapability : IRemediationCapability
    {
        public bool IsGranted => false;
    }
}
