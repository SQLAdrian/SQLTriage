/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Pins the remediation credit price contract:
    ///   Cost = clamp( Base(RiskClass) + (Reversible ? 0 : 1), 1, 5 )
    ///   Base: Trivial = 1, Standard = 2, Sensitive = 3; CreditCostOverride wins.
    /// The properties that matter operationally are that it is TOTAL (never throws, never
    /// returns free), BOUNDED, and DETERMINISTIC — a fix must not change price between two
    /// renders of the same page, or the number on the button stops meaning anything.
    /// </summary>
    public class RemediationCreditCostTests
    {
        private static RemediationTemplate T(
            RemediationRiskClass risk = RemediationRiskClass.Standard,
            bool reversible = true,
            int? over = null) => new()
            {
                Key = "TEST",
                DisplayName = "Test fix",
                RiskClass = risk,
                Reversible = reversible,
                CreditCostOverride = over
            };

        // ── Derived mapping ──────────────────────────────────────────────────

        [Theory]
        [InlineData(RemediationRiskClass.Trivial,   true,  1)]
        [InlineData(RemediationRiskClass.Trivial,   false, 2)]
        [InlineData(RemediationRiskClass.Standard,  true,  1)]
        [InlineData(RemediationRiskClass.Standard,  false, 2)]
        [InlineData(RemediationRiskClass.Sensitive, true,  2)]
        [InlineData(RemediationRiskClass.Sensitive, false, 3)]
        public void Derives_from_risk_class_plus_irreversibility(
            RemediationRiskClass risk, bool reversible, int expected)
        {
            Assert.Equal(expected, RemediationCreditCost.For(T(risk, reversible)));
        }

        /// <summary>
        /// The economics the product was sized around: an ordinary bounded fix costs exactly
        /// one credit. If this ever fails, every existing per-server allocation and entitlement
        /// tier has been silently repriced — treat it as a commercial change, not a test fix.
        /// </summary>
        [Fact]
        public void An_ordinary_reversible_fix_still_costs_exactly_one_credit()
        {
            Assert.Equal(1, RemediationCreditCost.For(T(RemediationRiskClass.Standard, reversible: true)));
        }

        // ── Bounds ───────────────────────────────────────────────────────────

        [Fact]
        public void Never_prices_an_apply_free()
        {
            Assert.True(RemediationCreditCost.For(T(RemediationRiskClass.Trivial)) >= RemediationCreditCost.Min);
            Assert.Equal(RemediationCreditCost.Min, RemediationCreditCost.Clamp(0));
            Assert.Equal(RemediationCreditCost.Min, RemediationCreditCost.Clamp(-7));
        }

        [Fact]
        public void Clamps_to_max()
        {
            Assert.Equal(RemediationCreditCost.Max, RemediationCreditCost.Clamp(99));
            Assert.Equal(RemediationCreditCost.Max, RemediationCreditCost.For(T(over: 40)));
        }

        [Fact]
        public void Null_template_prices_at_min_rather_than_throwing()
        {
            Assert.Equal(RemediationCreditCost.Min, RemediationCreditCost.For(null));
        }

        // ── Override ─────────────────────────────────────────────────────────

        [Fact]
        public void Override_wins_over_the_derived_price()
        {
            // Sensitive + irreversible derives 3; the authored 1 must win.
            var t = T(RemediationRiskClass.Sensitive, reversible: false, over: 1);
            Assert.Equal(1, RemediationCreditCost.For(t));
        }

        [Fact]
        public void Absent_override_derives()
        {
            Assert.Equal(1, RemediationCreditCost.For(T(over: null)));
        }

        // ── Determinism ──────────────────────────────────────────────────────

        [Fact]
        public void Same_template_prices_identically_every_call()
        {
            var t = T(RemediationRiskClass.Sensitive, reversible: false);
            var first = RemediationCreditCost.For(t);
            for (var i = 0; i < 50; i++)
                Assert.Equal(first, RemediationCreditCost.For(t));
        }

        // ── Shipped templates ────────────────────────────────────────────────

        [Fact]
        public void Every_shipped_template_prices_within_bounds()
        {
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            foreach (var t in store.All())
            {
                var cost = RemediationCreditCost.For(t);
                Assert.InRange(cost, RemediationCreditCost.Min, RemediationCreditCost.Max);
            }
        }
    }
}
