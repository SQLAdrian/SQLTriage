/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationGrantTarget — which server a change-credit grant will actually credit, decided
 * BEFORE the grant is redeemed.
 *
 * The defect this closes (honesty hunt 2026-08-25, remediation-r2-05): /remediation printed a
 * green "Redeemed N change credits for '<server>'" whenever RemediationGrantStore.Redeem returned
 * null, and never once checked that the grant's server was a server this install operates. Credits
 * only become spendable on an EXACT name match against the ledger key (GrantedCreditsFor compares
 * the raw configured name, OrdinalIgnoreCase). Redemption burns the nonce permanently, so a grant
 * naming a name this install does not use is consumed for nothing, under a success message.
 *
 * The hunt proved that on this box: a valid 50-credit grant naming Environment.MachineName ("MSI")
 * redeemed "successfully" while the operating server was ".". GrantedCreditsFor(".") stayed 0,
 * Available(".") stayed 0, and re-redeeming the same nonce was rejected as a replay. The page's own
 * comment already says DisplayServerName (which substitutes MachineName for "." / "(local)" /
 * "localhost") must never be used for ledger keys — and it was the only name shown beside "Credits".
 *
 * Pure, so the decision is testable without a page, a file or a server.
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace SQLTriage.Data.Services.Remediation
{
    /// <summary>Where a grant's credits would land, relative to the install redeeming it.</summary>
    public enum GrantTargetMatch
    {
        /// <summary>The grant names the server this page is operating on. The credits are usable here.</summary>
        OperatingServer = 0,

        /// <summary>The grant names a different server this install is configured for. Usable there, not here.</summary>
        AnotherConfiguredServer = 1,

        /// <summary>The grant names no server this install is configured for. The credits would be unspendable.</summary>
        NoConfiguredServer = 2,
    }

    public static class RemediationGrantTarget
    {
        /// <summary>
        /// Classifies a grant's server against this install's configured servers. Comparison is
        /// OrdinalIgnoreCase on the RAW names, which is exactly how
        /// <see cref="RemediationGrantStore.GrantedCreditsFor"/> and the credit ledger key, so this
        /// verdict and the spendability it predicts cannot drift apart.
        /// </summary>
        public static GrantTargetMatch Classify(
            string? grantServer, string? operatingServer, IEnumerable<string>? configuredServers)
        {
            if (string.IsNullOrWhiteSpace(grantServer)) return GrantTargetMatch.NoConfiguredServer;

            if (!string.IsNullOrWhiteSpace(operatingServer)
                && string.Equals(grantServer, operatingServer, StringComparison.OrdinalIgnoreCase))
                return GrantTargetMatch.OperatingServer;

            var configured = configuredServers?
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Any(s => string.Equals(s, grantServer, StringComparison.OrdinalIgnoreCase)) ?? false;

            return configured ? GrantTargetMatch.AnotherConfiguredServer : GrantTargetMatch.NoConfiguredServer;
        }

        /// <summary>
        /// The refusal shown for <see cref="GrantTargetMatch.NoConfiguredServer"/>. Refusing here is
        /// the whole point: the nonce is one-shot, so a redemption that cannot be spent must not
        /// happen at all. Nothing is consumed, and the operator can redeem the same file again once
        /// the name matches.
        /// </summary>
        public static string DescribeRefusal(string grantServer, IEnumerable<string>? configuredServers)
        {
            var names = (configuredServers ?? Enumerable.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();

            var estate = names.Count == 0
                ? "This install has no servers configured yet."
                : "This install connects to: " + string.Join(", ", names) + ".";

            return $"Nothing was redeemed. This grant is for '{grantServer}', which is not a server this install "
                 + $"connects to. Credits are held under the exact server name, so these could not be spent here. "
                 + $"{estate} Add that server, or ask for a grant naming one of these, then redeem again.";
        }

        /// <summary>
        /// The success sentence, which now states WHERE the credits landed. It used to name only the
        /// grant's own server, beside a credit panel headed with a different name.
        /// </summary>
        public static string DescribeSuccess(
            GrantTargetMatch match, string grantServer, string? operatingServer, int credits, DateTime expiresUtc)
        {
            var head = $"Redeemed {credits} change credit{(credits == 1 ? "" : "s")} for '{grantServer}' (valid until {expiresUtc:u}).";
            return match == GrantTargetMatch.OperatingServer
                ? head + " They are available on this server now."
                : head + $" You are operating on '{operatingServer}', so they are not available here. "
                       + $"Select '{grantServer}' in the top bar to spend them.";
        }
    }
}
