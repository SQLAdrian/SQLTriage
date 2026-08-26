/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace SQLTriage.Tests
{
    // BM:FullAuditTabIsLinkedTests — the sentence that explains a licence failure must be linked to
    /// <summary>
    /// THE PAGE THAT SAYS WHY THE LICENCE DID NOT LOAD MUST BE THE PAGE THESE CONTROLS OPEN.
    ///
    /// <para><b>The defect.</b> <c>LicenseService</c> records one sentence per failing branch and
    /// <c>ActivateFullAuditCard</c> renders it persistently. That card is on the Full Audit tab of
    /// Settings and nowhere else. Measured 2026-08-25 in the state this box's install was actually
    /// in — a saved key from an earlier mint against a later bundle — the reason rendered on
    /// <c>/settings?tab=full-audit</c>, and <c>/settings</c> and <c>/guide</c> showed zero
    /// occurrences of it. Every in-app control that exists BECAUSE of the licence state (the
    /// bundle-not-loaded banner, the Free-tier pill, the tier badge in the nav) navigated to the
    /// slug-less <c>/settings</c>, which opens on General. The explanation was written, and it was
    /// reachable only by an operator who already knew to click a particular tab.</para>
    ///
    /// <para><b>What this does NOT cover.</b> The gear and profile buttons in
    /// <c>MainLayout.razor</c> are a general Settings shortcut, not a licence control, so they still
    /// open General and are deliberately not asserted here. <c>AdvancedReporting.razor</c> carries a
    /// private <c>GoToSettings</c> that no markup in that file calls; it is dead code, so pointing
    /// it anywhere would be a claim about a link nobody can follow.</para>
    ///
    /// <para>⚠ These are LINTS over literals in the shipped markup, in the same class as the other
    /// markup assertions in this suite: an author who composes the address a different way defeats
    /// them. What they do hold is the drift that actually happened — a new licence surface added
    /// with a bare <c>/settings</c>.</para>
    /// </summary>
    public sealed class FullAuditTabIsLinkedTests
    {
        /// <summary>Every shipped control whose reason for existing is the licence state.</summary>
        public static IEnumerable<object[]> LicenceControls => new[]
        {
            new object[] { "FreeStateBanner.razor", "the bundle-not-loaded banner" },
            new object[] { "FullAuditUpsellPill.razor", "the Free-tier pill" },
            new object[] { "NavMenu.razor", "the licence tier badge" },
        };

        [Theory]
        [MemberData(nameof(LicenceControls))]
        public void ALicenceControlOpensTheTabThatExplainsTheFailure(string file, string what)
        {
            var markup = ReadMarkup(file);

            // Anti-vacuity: this scan only means something over a file that navigates at all.
            Assert.Contains("NavigateTo", markup, StringComparison.Ordinal);

            Assert.False(markup.Contains("NavigateTo(\"/settings\")", StringComparison.Ordinal),
                $"{file} ({what}) navigates to bare /settings, which opens the General tab. The "
                + "sentence saying why the licence did not load renders on the Full Audit tab only.");

            Assert.Contains("Settings.FullAuditTabUrl", markup, StringComparison.Ordinal);
        }

        /// <summary>
        /// The address those controls use has to be an address that opens that tab. Written as one
        /// constant and resolved through the page's own slug map, so a rename of either end fails
        /// here rather than sending an operator to a tab that does not answer their question.
        /// </summary>
        [Fact]
        public void TheAddressThoseControlsUseOpensTheFullAuditTab()
        {
            var url = SQLTriage.Pages.Settings.FullAuditTabUrl;
            Assert.StartsWith("/settings?", url, StringComparison.Ordinal);

            var marker = url.IndexOf("tab=", StringComparison.Ordinal);
            Assert.True(marker > 0, $"'{url}' carries no tab slug");
            var slug = url[(marker + 4)..];

            Assert.Equal("Full Audit", SQLTriage.Pages.Settings.ResolveTabSlug(slug, premium: true));

            // A community build renders no Full Audit tab, so the same address falls back to the
            // General tab those controls opened before. Both arms are passed explicitly, so this
            // cell grades both build axes instead of testing one of them twice.
            Assert.Null(SQLTriage.Pages.Settings.ResolveTabSlug(slug, premium: false));
        }

        private static string ReadMarkup(string fileName)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Markup", fileName);
            Assert.True(File.Exists(path),
                $"{fileName} was not copied to the test output ({path}); every assertion over it "
                + "would silently pass.");
            return File.ReadAllText(path);
        }
    }
}
