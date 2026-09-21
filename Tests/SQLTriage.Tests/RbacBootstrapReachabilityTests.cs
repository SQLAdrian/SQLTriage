/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    /// <summary>
    /// THE CONTROL THAT CREATES THE FIRST ADMIN MUST NOT BE GATED ON THE STATE IT EXISTS TO REACH.
    ///
    /// <para><b>The defect.</b> On a cold RBAC store — no <c>rbac-config.json</c>, no
    /// <c>rbac-users.json</c>, which is what an install IS before anyone configures it — every
    /// control on Settings &gt; Security &amp; Access that could add a user or enable a sign-in
    /// method sat inside <c>@if (_rbacEnabled &amp;&amp; RbacControlsMayShowValues)</c>. Ticking
    /// "Enable RBAC" ran <c>SaveRbacConfig</c> → <c>RefuseUnsafeRbacConfig</c>, which refused with
    /// "No enabled Admin user exists. Add an Admin account before enforcing RBAC.", and
    /// <c>Settings.razor.cs</c> then snapped <c>_rbacEnabled</c> back to the stored false. The error
    /// named a control, and the same error is what kept that control from rendering. Measured
    /// 2026-08-25 in-process against a cold store: two blockers, refusal true, <c>Config.Enabled</c>
    /// false, zero users. The only surface that could mint a first admin was <c>/onboarding</c>,
    /// which has no inbound link anywhere in <c>Pages/</c>, <c>Components/</c> or <c>Data/</c>.</para>
    ///
    /// <para><b>Why this reads the nesting instead of the words.</b> A literal lint over
    /// "<c>_rbacEnabled &amp;&amp; RbacControlsMayShowValues</c>" passes the moment someone reorders
    /// the operands, splits the condition, or moves the same controls under a different
    /// <c>_rbacEnabled</c> block further up. <see cref="EnclosingConditions"/> walks the file's
    /// brace nesting and returns the actual chain of conditions a target line sits inside, so the
    /// question asked is "can this control render with RBAC off", which is the question the defect
    /// answered wrongly.</para>
    ///
    /// <para><b>What this does NOT claim.</b> That the control WORKS: the RBAC store guards
    /// (<c>UpdateConfig</c>, <c>AddUser</c>, the lockout pre-flight) are covered by the RBAC suites,
    /// and the order of operations they permit — save a provider and an admin while
    /// <c>config.Enabled</c> is false, because <c>DescribeEnforcementBlockers</c> returns empty
    /// there, then enable — is asserted in <see cref="TheBootstrapOrderIsPermittedByTheStoreGuards"/>.
    /// This file asserts only that the markup lets the operator reach them.</para>
    /// </summary>
    public sealed class RbacBootstrapReachabilityTests
    {
        private readonly ITestOutputHelper _out;
        public RbacBootstrapReachabilityTests(ITestOutputHelper output) => _out = output;

        /// <summary>The button that adds a user, and the checkbox that enables Windows sign-in.</summary>
        public static IEnumerable<object[]> BootstrapControls => new[]
        {
            new object[] { "@onclick=\"AddRbacUser\"", "the only control that adds an RBAC user" },
            new object[] { "@bind=\"_rbacWindowsEnabled\"", "the Windows sign-in method an added Windows account needs" },
            new object[] { "@bind=\"_rbacLocalPasswordEnabled\"", "the local-password sign-in method an added email account needs" },
        };

        [Theory]
        [MemberData(nameof(BootstrapControls))]
        public void ABootstrapControlIsNotGatedOnRbacBeingEnabled(string anchor, string what)
        {
            var markup = ReadSettingsMarkup();
            var conditions = EnclosingConditions(markup, anchor);

            foreach (var c in conditions) _out.WriteLine($"{anchor} <- {c}");

            // Anti-vacuity FIRST. A scanner that silently found nothing would make every assertion
            // below pass over an empty list, which is exactly how the censuses in this repo have
            // been defeated before. RbacControlsMayShowValues must still be one of the enclosing
            // conditions: it is the damaged-store withholding, and it is not being removed here.
            Assert.True(conditions.Count > 0,
                $"The nesting scan found no enclosing condition for {anchor}. Either the anchor moved "
                + "or the scan is broken; both make this test vacuous.");
            Assert.Contains(conditions, c => c.Contains("RbacControlsMayShowValues", StringComparison.Ordinal));

            var gated = conditions
                .Where(c => Regex.IsMatch(c, @"(?<![A-Za-z0-9_])_rbacEnabled(?![A-Za-z0-9_])"))
                .ToList();

            Assert.True(gated.Count == 0,
                $"{anchor} — {what} — renders only inside: {string.Join(" | ", gated)}. That condition is "
                + "false on a cold store, and the refusal that tells the operator to use this control is "
                + "the same fact that keeps it from rendering. Configure the sign-in method and the first "
                + "Admin account with RBAC off, then enable it.");
        }

        /// <summary>
        /// The order the markup now invites: save a provider with RBAC off, add an Admin, then
        /// enable. Driven against a real <c>RbacService</c> over a cold store, because a markup
        /// change that exposes controls the service would refuse anyway fixes nothing.
        /// </summary>
        [Fact]
        public void TheBootstrapOrderIsPermittedByTheStoreGuards()
        {
            var dir = Path.Combine(Path.GetTempPath(), "sqlt-bootstrap-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var configPath = Path.Combine(dir, "rbac-config.json");
                var usersPath = Path.Combine(dir, "rbac-users.json");
                Assert.False(File.Exists(configPath));   // the cold store the defect lives in
                Assert.False(File.Exists(usersPath));

                var rbac = new RbacService(NullLogger<RbacService>.Instance, configPath, usersPath);

                Assert.Empty(rbac.GetUsers());
                Assert.False(rbac.Config.Enabled);

                // 1. Enabling first is refused, and names the missing admin. This is the state the
                //    operator was left in, and it is unchanged: the guard is not being weakened.
                var enableFirst = new RbacConfig { Enabled = true };
                enableFirst.Windows.Enabled = true;
                Assert.NotEmpty(rbac.DescribeEnforcementBlockers(enableFirst));

                // 2. The sign-in method saves with RBAC still OFF.
                var providerOnly = new RbacConfig { Enabled = false };
                providerOnly.Windows.Enabled = true;
                Assert.Empty(rbac.DescribeEnforcementBlockers(providerOnly));
                Assert.Empty(rbac.DescribeProviderConfigProblems(providerOnly));
                Assert.Equal(StoreWriteOutcome.Saved,
                    rbac.UpdateConfig(providerOnly, StoreWriteIntent.FromLoadedStore));

                // 3. The Admin account is added — the step that had no reachable control.
                Assert.Equal(StoreWriteOutcome.Saved,
                    rbac.AddUser(new RbacUser
                    {
                        Email = @"MSI\admin.test",
                        DisplayName = @"MSI\admin.test",
                        Provider = AuthProviders.Windows,
                        Role = AppRoles.Admin,
                    }));

                // 4. NOW enabling passes the same guard that refused it in step 1.
                var enableLast = new RbacConfig { Enabled = true };
                enableLast.Windows.Enabled = true;
                Assert.Empty(rbac.DescribeEnforcementBlockers(enableLast));
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
            }
        }

        /// <summary>
        /// THE ROLE THE FORM OPENS ON MUST BE ONE THE GUARDS ACCEPT AS A FIRST USER.
        ///
        /// <para><b>The defect.</b> The empty-state line added by this lane says "Add the first
        /// Admin account below, then tick Enable RBAC above", and the Role select beside it opened
        /// on Viewer. Measured through the browser 2026-08-25: following that line verbatim added a
        /// viewer, the Enable RBAC tick was then refused for want of an Admin, and the checkbox
        /// snapped back. That is the shape of the defect this lane exists to fix, reproduced inside
        /// this lane's own new copy.</para>
        ///
        /// <para>Driven against a real <c>RbacService</c> over a cold store, using the page's own
        /// preselection rather than a role this test picked, so a change to that preselection is
        /// graded here rather than only in a browser. The contrast arm adds the AFTERWARDS default
        /// instead and asserts the blockers are still there, so this cell measures the role and not
        /// the ceremony around it.</para>
        /// </summary>
        [Fact]
        public void ThePreselectedFirstRoleIsOneTheStoreGuardsAccept()
        {
            var dir = Path.Combine(Path.GetTempPath(), "sqlt-firstrole-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var rbac = new RbacService(NullLogger<RbacService>.Instance,
                    Path.Combine(dir, "rbac-config.json"), Path.Combine(dir, "rbac-users.json"));

                Assert.Empty(rbac.GetUsers());   // the cold store, which is what the operator has

                // The order the page invites: the sign-in method saves with RBAC still off.
                var providerOnly = new RbacConfig { Enabled = false };
                providerOnly.Windows.Enabled = true;
                Assert.Equal(StoreWriteOutcome.Saved,
                    rbac.UpdateConfig(providerOnly, StoreWriteIntent.FromLoadedStore));

                var enable = new RbacConfig { Enabled = true };
                enable.Windows.Enabled = true;

                // The contrast: the role the form used to open on, and still opens on once there
                // are users. It leaves the operator exactly where the gate found them.
                var afterwards = SQLTriage.Pages.Settings.DefaultNewUserRole(existingUserCount: 1);
                Assert.Equal(StoreWriteOutcome.Saved, rbac.AddUser(new RbacUser
                {
                    Email = @"MSI\contrast.test",
                    DisplayName = @"MSI\contrast.test",
                    Provider = AuthProviders.Windows,
                    Role = afterwards,
                }));
                Assert.NotEmpty(rbac.DescribeEnforcementBlockers(enable));

                // The preselection the page makes on a store with no users.
                var first = SQLTriage.Pages.Settings.DefaultNewUserRole(existingUserCount: 0);
                Assert.Equal(StoreWriteOutcome.Saved, rbac.AddUser(new RbacUser
                {
                    Email = @"MSI\first.test",
                    DisplayName = @"MSI\first.test",
                    Provider = AuthProviders.Windows,
                    Role = first,
                }));

                Assert.Empty(rbac.DescribeEnforcementBlockers(enable));
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
            }
        }

        /// <summary>
        /// The copy and the control must say the same thing. The line names the Role box, so a copy
        /// edit that drops it is caught here rather than by an operator following it into a refusal.
        /// </summary>
        [Fact]
        public void TheEmptyUserListNamesTheRoleTheFormOpensOn()
        {
            var markup = ReadSettingsMarkup();

            var start = markup.IndexOf("No users yet.", StringComparison.Ordinal);
            Assert.True(start >= 0, "the empty-user-list line was not found; this assertion would pass vacuously");

            var end = markup.IndexOf("</p>", start, StringComparison.Ordinal);
            Assert.True(end > start, "the empty-user-list paragraph did not close; the scan is wrong");

            var line = markup[start..end];
            Assert.Contains("Admin", line, StringComparison.Ordinal);
            Assert.Contains("Role", line, StringComparison.Ordinal);
        }

        // ── Every tab has an address ─────────────────────────────────────────────────────────

        /// <summary>
        /// A tab reachable only by <c>@onclick</c> cannot be linked to, cannot be reopened, and does
        /// not appear in a Blazor Server prerender — so <c>curl /settings</c> returned markup with
        /// neither "Add User" nor "Install bundle file" in it, and every claim about the controls
        /// this lane added rested on reading the file rather than rendering it. Both directions are
        /// re-derived from the shipped rail so a new tab cannot be quietly unaddressable.
        /// </summary>
        [Fact]
        public void EverySettingsTabIsReachableByQueryString()
        {
            var markup = ReadSettingsMarkup();

            var railTabs = Regex.Matches(markup, @"SetActiveTab\(""([^""]+)""\)")
                                .Select(m => m.Groups[1].Value)
                                .Distinct()
                                .OrderBy(s => s, StringComparer.Ordinal)
                                .ToList();

            Assert.True(railTabs.Count >= 5,
                $"Only {railTabs.Count} rail tab(s) were found in the markup; the scan is not seeing the rail.");

            var slugged = SQLTriage.Pages.Settings.TabSlugs.Values
                                .Distinct()
                                .OrderBy(s => s, StringComparer.Ordinal)
                                .ToList();

            Assert.Equal(railTabs, slugged);
        }

        [Theory]
        [InlineData("security", "Security & Access")]
        [InlineData("SECURITY", "Security & Access")]   // a link is retyped in whatever case
        [InlineData("  security  ", "Security & Access")]
        [InlineData("general", "General")]
        [InlineData("compliance", "Compliance")]
        public void AKnownSlugSelectsItsTab(string slug, string expected)
            => Assert.Equal(expected, SQLTriage.Pages.Settings.ResolveTabSlug(slug, premium: true));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("nope")]
        [InlineData("Security & Access")]   // the display name is not the address
        public void AnUnknownSlugSelectsNothing(string? slug)
            => Assert.Null(SQLTriage.Pages.Settings.ResolveTabSlug(slug, premium: true));

        /// <summary>
        /// The Full Audit tab is not rendered in a community build, so its slug must not select it
        /// there: a selected-but-not-rendered tab is a blank page, which reads as a broken install
        /// rather than as a feature this build does not carry. Both arms are passed explicitly
        /// rather than read off <c>BuildModules.Premium</c>, so this cell runs identically on both
        /// build axes instead of silently testing one of them twice.
        /// </summary>
        [Fact]
        public void TheFullAuditSlugSelectsThatTabOnlyWhereItIsRendered()
        {
            Assert.Equal("Full Audit", SQLTriage.Pages.Settings.ResolveTabSlug("full-audit", premium: true));
            Assert.Null(SQLTriage.Pages.Settings.ResolveTabSlug("full-audit", premium: false));
        }

        // ── The instrument ───────────────────────────────────────────────────────────────────

        private static string ReadSettingsMarkup()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Markup", "Settings.razor");
            Assert.True(File.Exists(path),
                $"Settings.razor was not copied to the test output ({path}); every assertion in this "
                + "file would silently pass.");
            return File.ReadAllText(path);
        }

        /// <summary>
        /// The chain of Razor block conditions the first occurrence of <paramref name="anchor"/>
        /// sits inside, outermost first.
        ///
        /// <para>Razor comments are blanked (length-preserving, so indices stay valid) before the
        /// walk, because a <c>@* … *@</c> block in this file discusses <c>_rbacEnabled</c> and
        /// carries braces. Nothing else in <c>Settings.razor</c> puts a brace inside an attribute
        /// value, which the balance assertion in <see cref="EnclosingConditions"/> checks rather
        /// than assumes: a file whose braces do not balance means the walk is wrong and the caller
        /// is told, instead of being handed a plausible-looking chain.</para>
        /// </summary>
        internal static IReadOnlyList<string> EnclosingConditions(string markup, string anchor)
        {
            var text = Regex.Replace(markup, @"@\*.*?\*@",
                m => Regex.Replace(m.Value, @"[^\n]", " "), RegexOptions.Singleline);

            var target = text.IndexOf(anchor, StringComparison.Ordinal);
            Assert.True(target >= 0, $"The anchor {anchor} is not in the markup; the test is pinned to a line that moved.");

            var header = new Regex(@"@(else if|else|if|foreach|for|while|switch|code)\b[^\n]*",
                                   RegexOptions.RightToLeft);

            var stack = new List<string>();
            List<string>? atTarget = null;

            for (var i = 0; i < text.Length; i++)
            {
                if (i == target) atTarget = new List<string>(stack);

                if (text[i] == '{')
                {
                    var lookBack = text.Substring(Math.Max(0, i - 400), i - Math.Max(0, i - 400));
                    var m = header.Match(lookBack);
                    // Only a construct whose header runs up to the brace with nothing but
                    // whitespace between opens a CONDITIONAL block; anything else is a plain block.
                    var cond = m.Success && lookBack.Substring(m.Index + m.Length).Trim().Length == 0
                        ? m.Value.Trim()
                        : "(unconditional)";
                    stack.Add(cond);
                }
                else if (text[i] == '}' && stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }
            }

            Assert.True(stack.Count == 0,
                $"Braces in Settings.razor did not balance ({stack.Count} left open), so the nesting walk "
                + "cannot be trusted and this test must not report a verdict from it.");
            Assert.NotNull(atTarget);

            return atTarget!.Where(c => c != "(unconditional)").ToList();
        }
    }
}
