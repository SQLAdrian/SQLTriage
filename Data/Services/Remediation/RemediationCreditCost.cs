/* In the name of God, the Merciful, the Compassionate */
/*
 * Prices a single remediation apply in change credits.
 *
 * Contract
 *   Cost(template) = clamp( Base(RiskClass) + (Reversible ? 0 : 1), Min, Max )
 *   Base: Trivial = 1, Standard = 1, Sensitive = 2
 *   A CreditCostOverride on the template wins outright (still clamped).
 *
 * Why Standard is 1 and not 2: a standard bounded fix (MAXDOP, CTFP, ...) has always cost one
 * credit, and every allocation and entitlement tier in the product was sized against that.
 * Making the ordinary case dearer would be a repricing of existing customers dressed up as a
 * feature. Differentiation is applied where it earns its keep — at the dangerous end — so the
 * ceiling in practice is 3 (Sensitive + irreversible), not 5.
 *
 * Why these two inputs and no others: RiskClass already encodes operation kind and
 * blast radius by construction (Trivial is documented as "always-safe, idempotent,
 * instantly reversible"; Sensitive as "destructive or security-affecting"), so adding
 * an operation-kind or blast-radius term would double-count the same signal.
 * Reversibility is the one axis RiskClass does NOT fully capture — a Standard template
 * can still be non-reversible — and an irreversible spend deserves to cost more because
 * the ledger's refund-on-rollback promise can never fire for it.
 *
 * Invariants (see RemediationCreditCostTests):
 *   - total: never throws, null template prices at Min rather than free
 *   - bounded: result is always within [Min, Max]
 *   - deterministic: same template -> same cost, forever, within a build
 */

namespace SQLTriage.Data.Services.Remediation
{
    public static class RemediationCreditCost
    {
        /// <summary>No apply is ever free — preview is the free tier.</summary>
        public const int Min = 1;

        /// <summary>Ceiling, so a mis-authored override can't drain a server's allocation.</summary>
        public const int Max = 5;

        /// <summary>
        /// The credit price of applying <paramref name="template"/> once.
        /// </summary>
        public static int For(RemediationTemplate? template)
        {
            if (template is null) return Min;

            if (template.CreditCostOverride is int authored)
                return Clamp(authored);

            var baseCost = template.RiskClass switch
            {
                RemediationRiskClass.Trivial   => 1,
                RemediationRiskClass.Sensitive => 2,
                // Standard, and any future member: price as Standard. Never free by default.
                _                              => 1
            };

            return Clamp(baseCost + (template.Reversible ? 0 : 1));
        }

        /// <summary>Bounds a cost into [<see cref="Min"/>, <see cref="Max"/>].</summary>
        public static int Clamp(int cost) =>
            cost < Min ? Min : (cost > Max ? Max : cost);
    }
}
