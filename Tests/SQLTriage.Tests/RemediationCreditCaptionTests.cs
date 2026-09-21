/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// remediation-r1-01 + remediation-r2-07 (honesty hunt, 2026-08-25): NINE approval and undo
    /// captions on /remediation spelled the price as the literal "1 credit" while the runner priced
    /// the same apply from the template. BACKUPDATABASENOW and CHECKDBNOW cost 3 (Sensitive and not
    /// reversible), ADDMISSINGINDEX costs 2 — and the coin badge one screen up already showed the
    /// real number, so the page contradicted itself inside a single viewport.
    ///
    /// <para>r2-07 widens it past the three fixed-price built-ins: corpus-fed Configuration rows are
    /// priced at RUNTIME from the check's own risk_class/reversible, so a caption cannot be correct
    /// by being edited to the right constant. It has to be computed.</para>
    ///
    /// <para>Two halves here, deliberately. The pure function is pinned against the REAL shipped
    /// templates, and the page is scanned so a literal price cannot come back. The scan is the half
    /// that was red before the fix: nine matches, at base 8f38917 lines 140, 259, 334, 399, 461,
    /// 538, 655, 756 and 852. The count said "eight" in this file's first draft, which nothing
    /// measured; see the reproducing command on the scan itself below.</para>
    /// </summary>
    public class RemediationCreditCaptionTests
    {
        private static RemediationTemplateStore Store() => new(NullLogger<RemediationTemplateStore>.Instance);

        private static string Describe(string key) => RemediationCreditCost.Describe(Store().TryGet(key));

        [Theory]
        [InlineData("BACKUPDATABASENOW", "3 credits")]
        [InlineData("CHECKDBNOW", "3 credits")]
        [InlineData("ADDMISSINGINDEX", "2 credits")]
        [InlineData("AGENTALERTPACK", "1 credit")]
        [InlineData("INSTALLMAINTENANCESOLUTION", "1 credit")]
        public void TheCaptionPhrase_MatchesWhatTheRunnerCharges_ForEveryKeyedSectionOnThePage(string key, string expected)
        {
            var template = Store().TryGet(key);
            Assert.NotNull(template); // a caption for a template that is not registered is the r1-01 shape

            Assert.Equal(expected, RemediationCreditCost.Describe(template));

            // The phrase is not a second opinion: it is the SAME number RemediationRunner reserves,
            // which prices from the template whenever the caller passes no override (no /remediation
            // call site passes one).
            Assert.StartsWith(RemediationCreditCost.For(template).ToString(), Describe(key), StringComparison.Ordinal);
        }

        [Fact]
        public void TheThreeDearFixes_AreNotOneCredit()
        {
            // The literal the page used to print, against the templates it printed it for.
            Assert.NotEqual("1 credit", Describe("BACKUPDATABASENOW"));
            Assert.NotEqual("1 credit", Describe("CHECKDBNOW"));
            Assert.NotEqual("1 credit", Describe("ADDMISSINGINDEX"));
        }

        [Fact]
        public void ARuntimePricedTemplate_IsDescribedFromItsOwnRiskClass_NotAConstant()
        {
            // The r2-07 shape: a corpus-fed row whose price is decided at runtime. Sensitive and
            // reversible prices at 2; Sensitive and irreversible at 3. A hand-written caption cannot
            // track either.
            var sensitiveReversible = new RemediationTemplate
            {
                Key = "ZZTEST-SENSITIVE-REVERSIBLE",
                DisplayName = "Test",
                Kind = RemediationKind.Configuration,
                RiskClass = RemediationRiskClass.Sensitive,
                Reversible = true,
            };
            var sensitiveIrreversible = new RemediationTemplate
            {
                Key = "ZZTEST-SENSITIVE-IRREVERSIBLE",
                DisplayName = "Test",
                Kind = RemediationKind.Configuration,
                RiskClass = RemediationRiskClass.Sensitive,
                Reversible = false,
            };

            Assert.Equal("2 credits", RemediationCreditCost.Describe(sensitiveReversible));
            Assert.Equal("3 credits", RemediationCreditCost.Describe(sensitiveIrreversible));
        }

        [Fact]
        public void AnUnknownTemplate_IsNotPricedAtTheFloor()
        {
            // Quoting Min for a fix nobody can price is the same fabrication in miniature. The
            // numeric For() still floors at Min for the runner's arithmetic; the SENTENCE does not.
            var phrase = RemediationCreditCost.Describe(null);
            Assert.DoesNotContain("1 credit", phrase, StringComparison.Ordinal);
            Assert.Contains("unknown", phrase, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(RemediationCreditCost.Min, RemediationCreditCost.For(null));
        }

        [Fact]
        public void Noun_AgreesWithTheNumber()
        {
            Assert.Equal("credit", RemediationCreditCost.Noun(1));
            Assert.Equal("credits", RemediationCreditCost.Noun(2));
            Assert.Equal("credits", RemediationCreditCost.Noun(3));
        }

        // ── The page cannot go back to a literal price ────────────────────────────

        private static readonly Regex CreditLiteral =
            new(@"\b\d+\s+(?:[A-Za-z]+\s+)?credits?\b(?!\s*\{)", RegexOptions.IgnoreCase);

        [Theory]
        // The plain form the page used at 8f38917.
        [InlineData("Applies as adrian and debits 1 credit.", true)]
        [InlineData("and debits 2 credits.", true)]
        // The house phrasing every computed caption uses. The first draft of this scan missed it.
        [InlineData("debits 1 change credit.", true)]
        [InlineData("Applying this fix debits 3 change credits (Sensitive risk).", true)]
        // Computed captions, which are the point of the fix and must not trip the scan.
        [InlineData("debits @applyCost @costNoun", false)]
        [InlineData("debits @RemediationCreditCost.Describe(row.Template)", false)]
        public void TheScan_SeesEveryLiteralFormThePageCouldUse_AndNoComputedOne(string candidate, bool expected)
        {
            Assert.Equal(expected, CreditLiteral.IsMatch(candidate));
        }

        [Fact]
        public void RemediationPage_QuotesNoCreditPriceAsALiteral()
        {
            var markup = File.ReadAllText(Path.Combine(RawPassedScan.RepoRoot().FullName, "Pages", "Remediation.razor"));

            // "debits 1 credit", "debits 2 credits", "1 credit per applied change", and the form
            // every COMPUTED caption on this page actually uses, "N change credit(s)" — any
            // spelled-out price. The optional middle word is why: the first draft of this scan
            // matched only "N credit(s)", so the one literal a future edit is most likely to type,
            // "debits 1 change credit", was the one shape the guard could not see. A test named for
            // literal prices that misses the house phrasing is the test-side blind spot this wave
            // is about.
            //
            // NINE of these were live on this page at 8f38917. Reproduce the count with:
            //   git show 8f38917:Pages/Remediation.razor | rg -Pio "\b\d+\s+([A-Za-z]+\s+)?credits?\b"
            // Every caption now reads its number from RemediationCreditCost, which is also what the
            // runner charges, so the expected count here is zero.
            var hits = CreditLiteral.Matches(markup);

            Assert.True(hits.Count == 0,
                "Pages/Remediation.razor states a credit price as a literal: " +
                string.Join(" | ", System.Linq.Enumerable.Select(hits, m => Context(markup, m.Index))));
        }

        private static string Context(string text, int index)
        {
            var start = Math.Max(0, index - 40);
            var length = Math.Min(90, text.Length - start);
            return text.Substring(start, length).Replace("\r", " ").Replace("\n", " ");
        }
    }
}
