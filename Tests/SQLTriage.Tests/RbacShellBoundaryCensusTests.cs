/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The decider for round 7. <b>NOT the decider any more</b> — see the warning below before
    /// reading the rest of this as a security guarantee.
    ///
    /// <para>⚠ <b>ROUND 8 MOVED THE BOUNDARY OUT OF THIS FILE.</b> This census lasted exactly one
    /// round. The verifier beat it with fourteen shapes (an <c>@attributes</c> splat, a
    /// <c>RenderFragment</c> that builds the handler in C#, <c>MarkupString</c> plus an off-shell
    /// <c>[JSInvokable]</c>, <c>DynamicComponent</c>, fully-qualified tags, a fake
    /// <c>&lt;ShellGate&gt;</c> written inside a <c>//</c> comment that this scanner parsed as a
    /// real region), and the cold gate beat it with two more that are not adversarial at all:
    /// <c>&lt;input @bind:get @bind:set /&gt;</c> — there is no <c>@bind=</c> attribute to match —
    /// and <c>&lt;EditForm OnValidSubmit&gt;</c> with a submit button — there is no
    /// <c>&lt;form&gt;</c> tag and no repo-declared <c>EventCallback</c>. Both fired live from
    /// <c>http://192.10.10.32/scheduled-tasks</c> as an unauthenticated viewer.</para>
    ///
    /// <para><b>The decider is now <see cref="InteractiveAppAdmissionTests"/></b>, which asserts a
    /// property of the WIRE: from a non-loopback origin, unauthenticated, no route returns an
    /// interactive application and <c>/_blazor</c> does not negotiate. It never reads component
    /// source, so aliases, lambdas, <c>@attributes</c>, <c>MarkupString</c>, <c>@bind:get</c> and
    /// <c>EditForm</c> are all equally irrelevant to it.</para>
    ///
    /// <para><b>Why this file stays.</b> It is the second layer, and its job changed rather than
    /// ended: it decides what an AUTHENTICATED caller who lacks a permission may see. A hole here
    /// is now a privilege escalation between roles, not an anonymous compromise. Read a green run
    /// of this census as "no known-shaped control is ungated", never as "no ungated control can be
    /// reached" — <c>Components/Shared/BoundaryCanary.razor</c> ships two ungated controls that
    /// this census reports as zero, permanently and on purpose, and
    /// <see cref="BoundaryCanaryTests"/> asserts that it still cannot see them.</para>
    ///
    /// <para>The rest of this comment is round 7's reasoning, kept because it is still why the
    /// scanner has the shape it has.</para>
    ///
    /// <para><b>What it asks.</b> Not "what does this handler call?" — a markup question the call
    /// cannot influence: <b>is this interactive element inside an authorization boundary?</b></para>
    ///
    /// <para><b>Why the old question had to go.</b> Three instruments were defeated in sequence,
    /// each through the category it could not see:</para>
    /// <list type="number">
    /// <item>a 29-verb LEXICON — missed <c>UserSettingsService.SetAnonymiseServerNames</c>;</item>
    /// <item>PREFIX ANCHORING — missed <c>ShortcutSvc.TriggerRun</c>;</item>
    /// <item>full edge enumeration — missed a RECEIVER ALIAS.</item>
    /// </list>
    /// <para>The third is the one that settles it. On 2026-08-02 a cold gate put two buttons in
    /// <c>Components/Layout/StatusBar.razor</c>, always-rendered, outside every <c>@if</c>,
    /// identical in every respect but the receiver expression:</para>
    /// <code>
    /// P1   UserSettings.NudgeReportOperator("P1-DIRECT-ALIAS")
    /// P2   var s = UserSettings; s.NudgeReportOperator("P2-LOCAL-ALIAS")
    /// </code>
    /// <para>P1 was named by <c>EveryShellEdgeIsOnTheRegister</c>. P2 — one extra line — returned
    /// <c>Total tests: 134, Passed: 134</c> across all five census classes, and was then built,
    /// served on :5300 and driven from <c>http://192.10.10.32:5300/scheduled-tasks</c>: a
    /// non-loopback, unauthenticated caller, badge <c>viewer</c>, on a page reading "restricted to
    /// Admin users". The control rendered and it worked. Green suite, working exploit.</para>
    ///
    /// <para>Aliasing is not a hole to plug; it is the first member of an infinite family —
    /// lambdas, method groups, an interface reference, a local function, a delegate field,
    /// reflection, <c>DynamicInvoke</c>. Widening the scanner once more is exactly what rounds 4,
    /// 5 and 6 each did.</para>
    ///
    /// <para><b>So the decider never reads the call.</b> P1 and P2 are the same markup — an
    /// <c>@onclick</c> on a <c>&lt;button&gt;</c> in a shell file, outside every
    /// <c>&lt;ShellGate&gt;</c> — so they now fail identically, and so does every shape nobody has
    /// thought of yet. <see cref="TheCensusJudgesAllFourReceiverShapesIdentically"/> pins that
    /// with the four shapes side by side.</para>
    ///
    /// <para><b>The round-6 edge census stays</b> (<c>RbacShellSurfaceCensusTests</c>) and is
    /// still worth running: it answers a question this one does not — what an already-permitted
    /// control can REACH. It is no longer what decides whether a control may render. That is said
    /// here, and again in that file, so nobody quietly promotes it back.</para>
    /// </summary>
    public class RbacShellBoundaryCensusTests
    {
        // ── 1. THE DECIDER ───────────────────────────────────────────────

        /// <summary>
        /// No interactive element may render in an always-rendered shell component unless it sits
        /// inside a <c>&lt;ShellGate&gt;</c> — or is named, with a reason, on
        /// <see cref="ShellSurfaceRegistry.InertControls"/>.
        /// </summary>
        [Fact]
        public void NoInteractiveShellControlRendersOutsideABoundary()
        {
            var loose = AllElements()
                .Where(e => e.Permission == null)
                .Where(e => !ShellSurfaceRegistry.InertControls.ContainsKey(e.Key))
                .Select(e => e.Describe)
                .ToList();

            Assert.True(loose.Count == 0,
                "These interactive controls render in the ALWAYS-RENDERED SHELL — markup that is "
                + "on every route in the app, AccessDenied pages included — and they are not "
                + "inside an authorization boundary.\n\n"
                + "This test does not read the handler. It cannot be answered by renaming a "
                + "method, aliasing the receiver, wrapping it in a lambda or handing over a method "
                + "group: on 2026-08-02 `var s = UserSettings;` turned a RED census GREEN with the "
                + "control still live from the LAN denial page, and the three instruments before "
                + "it fell to a verb, a prefix and a property write. Position in the markup is the "
                + "one property none of those can change.\n\n"
                + "Fix it one of two ways.\n"
                + "  (a) Wrap the control: <ShellGate Permission=\"…\"> … </ShellGate>, with the "
                + "permission chosen by WHAT THE CONTROL WRITES — settings for an install-wide "
                + "write, export_data for a file on the host, execute_checks for anything that "
                + "retargets the estate.\n"
                + "  (b) If it truly moves nothing but this circuit's own view, add it to "
                + "ShellSurfaceRegistry.InertControls with a reason that says so. A reason that "
                + "turns out to be false is worse than no entry — it launders the gap as "
                + "reviewed, which is how rounds 2 and 5 were both defeated.\n  "
                + string.Join("\n  ", loose));
        }

        /// <summary>
        /// A census that enumerates nothing passes. This pins that the scan really walks the
        /// shell's markup and finds the controls five rounds were defeated through.
        /// </summary>
        [Fact]
        public void TheScanFindsTheShellsControlsAtAll()
        {
            var all = AllElements();

            Assert.True(all.Count >= 60,
                "The boundary scan found only " + all.Count + " interactive elements across "
                + ShellSurfaceRegistry.ShellComponents.Count + " shell components. The shell had "
                + "over ninety on 2026-08-02; a number this small means the scan is broken and "
                + "this file is passing because it is looking at nothing.");

            Assert.True(all.Count(e => e.Permission != null) >= 10,
                "The scan found " + all.Count(e => e.Permission != null) + " controls inside a "
                + "boundary. The round-6 gates alone put more than that behind a permission, so a "
                + "number this small means <ShellGate> regions are not being detected and every "
                + "gated control is silently landing on the exemption path instead.");

            // The three files each of the last three rounds was defeated through must all be
            // enumerated, or the scan is looking past the evidence.
            foreach (var file in new[]
                     {
                         "Components/Layout/StatusBar.razor",      // round 6's alias exploit
                         "Components/Layout/NavMenu.razor",        // round 6's Experimental write
                         "Components/Layout/MainLayout.razor",     // round 5's probe
                     })
                Assert.Contains(all, e => e.File == file);
        }

        // ── 2. THE REGISTERS ARE CLAIMS, AND CLAIMS GO STALE ─────────────

        [Fact]
        public void EveryInertExemptionStillDescribesAControlThatExists()
        {
            var live = new HashSet<string>(AllElements().Select(e => e.Key), StringComparer.Ordinal);
            var stale = ShellSurfaceRegistry.InertControls.Keys.Where(k => !live.Contains(k)).ToList();

            Assert.True(stale.Count == 0,
                "These exemptions describe controls that no longer exist. Prune them — an "
                + "exemption nobody prunes is a reason the next reviewer will trust about markup "
                + "it no longer describes:\n  " + string.Join("\n  ", stale));
        }

        [Fact]
        public void EveryInertExemptionCarriesARealReason()
        {
            var thin = ShellSurfaceRegistry.InertControls
                .Where(kv => kv.Value.Trim().Length < 40
                             || kv.Value.Contains("TODO", StringComparison.OrdinalIgnoreCase)
                             || kv.Value.Contains("n/a", StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .ToList();

            Assert.True(thin.Count == 0,
                "An exemption is a claim that a control on every page in the app is inert. These "
                + "make no claim at all:\n  " + string.Join("\n  ", thin));
        }

        /// <summary>
        /// <c>RbacService.HasPermission</c> answers an unknown permission ADMIN-ONLY by fallback,
        /// so a typo in a boundary is a gate that works by accident and would silently widen the
        /// day the fallback changed.
        /// </summary>
        [Fact]
        public void EveryBoundaryNamesAPermissionTheRbacServiceKnows()
        {
            var known = new[]
            {
                "settings", "manage_servers", "manage_users", "manage_alerts",
                "execute_checks", "run_scripts", "export_data", "acknowledge_alerts",
                "view_dashboard", "view_results", "view_audit_log",
            };

            var bad = AllElements()
                .Where(e => e.Permission != null && !known.Contains(e.Permission))
                .Select(e => e.File + " line " + e.Line + " → <ShellGate Permission=\"" + e.Permission + "\">")
                .Distinct(StringComparer.Ordinal)
                .ToList();

            Assert.True(bad.Count == 0,
                "These boundaries name a permission RbacService does not define:\n  "
                + string.Join("\n  ", bad));
        }

        /// <summary>
        /// Break-glass is a hatch, and round 1 scoped it to SETTINGS and ONBOARDING. Every use of
        /// it in the shell must be on the ruling, by name, with the reason it is one of those two.
        /// </summary>
        [Fact]
        public void EveryBreakGlassBoundaryIsOnTheRuling()
        {
            var used = AllElements()
                .Where(e => e.BreakGlass)
                .Select(e => e.File + " → " + e.Attribute + " (line " + e.Line + ")")
                .ToList();

            var unruled = AllElements()
                .Where(e => e.BreakGlass)
                .Select(e => e.File)
                .Distinct(StringComparer.Ordinal)
                .Where(f => !ShellSurfaceRegistry.BreakGlassBoundaries.ContainsKey(f))
                .ToList();

            Assert.True(unruled.Count == 0,
                "These files open a <ShellGate BreakGlass> and are not on the ruling. Break-glass "
                + "lets a caller through a CLOSED gate; round 1 scoped it to Settings and "
                + "Onboarding — the two surfaces that can undo a bad RBAC configuration — and "
                + "nowhere else. Name the file on ShellSurfaceRegistry.BreakGlassBoundaries with "
                + "the reason it is one of those two, or use a plain boundary:\n  "
                + string.Join("\n  ", unruled)
                + "\n\nAll break-glass controls currently rendering:\n  "
                + string.Join("\n  ", used));

            var stale = ShellSurfaceRegistry.BreakGlassBoundaries.Keys
                .Where(f => !AllElements().Any(e => e.BreakGlass && e.File == f))
                .ToList();

            Assert.True(stale.Count == 0,
                "These files are on the break-glass ruling and no longer use it:\n  "
                + string.Join("\n  ", stale));

            var thin = ShellSurfaceRegistry.BreakGlassBoundaries
                .Where(kv => kv.Value.Trim().Length < 40)
                .Select(kv => kv.Key).ToList();
            Assert.True(thin.Count == 0,
                "These break-glass rulings carry no reason:\n  " + string.Join("\n  ", thin));
        }

        /// <summary>
        /// A <c>[JSInvokable]</c> is an entry point the browser's own JavaScript can call without
        /// any element rendering at all, so the boundary rule cannot answer for it. Each one is
        /// answered on its own register instead.
        /// </summary>
        [Fact]
        public void EveryShellJsInvokableIsOnTheRegister()
        {
            var live = ShellSurfaceRegistry.ShellComponents
                .SelectMany(c => ShellBoundaryScan.JsInvokablesOf(c).Select(m => c + " → " + m))
                .ToList();

            var unknown = live.Where(k => !ShellSurfaceRegistry.ShellJsInvokables.ContainsKey(k)).ToList();
            Assert.True(unknown.Count == 0,
                "These [JSInvokable] methods sit on always-rendered shell components. JS on the "
                + "page can call them with no element rendered and no click, so hiding the control "
                + "says nothing about them. Register each with what it does and why the caller's "
                + "own browser may drive it:\n  " + string.Join("\n  ", unknown));

            var stale = ShellSurfaceRegistry.ShellJsInvokables.Keys.Where(k => !live.Contains(k)).ToList();
            Assert.True(stale.Count == 0,
                "These registered [JSInvokable] methods no longer exist:\n  "
                + string.Join("\n  ", stale));

            var thin = ShellSurfaceRegistry.ShellJsInvokables
                .Where(kv => kv.Value.Trim().Length < 40).Select(kv => kv.Key).ToList();
            Assert.True(thin.Count == 0,
                "These [JSInvokable] entries carry no reason:\n  " + string.Join("\n  ", thin));
        }

        // ── 3. THE CANARIES ──────────────────────────────────────────────

        /// <summary>
        /// THE ROUND-7 CANARY. The four receiver shapes, side by side, in the file the exploit was
        /// planted in. All four are ungated markup, so all four must be named — and when the same
        /// four are wrapped in one boundary, none of them may be.
        ///
        /// <para>P2 is the shape that beat round 6 with a green suite and a working exploit. If a
        /// future change ever makes this census answer these four differently from one another,
        /// somebody has put a code-reading question back in the decider and this goes red.</para>
        /// </summary>
        [Fact]
        public void TheCensusJudgesAllFourReceiverShapesIdentically()
        {
            const string open = "@inject SQLTriage.Data.UserSettingsService UserSettings\n";

            const string direct = "<button class=\"p1\" @onclick=\"Direct\">P1</button>\n";
            const string alias = "<button class=\"p2\" @onclick=\"LocalAlias\">P2</button>\n";
            const string lambda = "<button class=\"p3\" @onclick=\"@(() => { var s = UserSettings; s.Whatever(); })\">P3</button>\n";
            const string group = "<button class=\"p4\" @onclick=\"UserSettings.SetExperimentalMode\">P4</button>\n";

            const string code = @"
@code {
    void Direct()     { UserSettings.NudgeReportOperator(""P1-DIRECT-ALIAS""); }
    void LocalAlias() { var s = UserSettings; s.NudgeReportOperator(""P2-LOCAL-ALIAS""); }
}
";

            // ── ungated: every one of the four is named ──
            var loose = ShellBoundaryScan
                .ElementsIn("Components/Layout/StatusBar.razor", open + direct + alias + lambda + group + code)
                .Where(e => e.Permission == null)
                .ToList();

            Assert.Equal(4, loose.Count);
            foreach (var shape in new[] { "Direct", "LocalAlias", "var s = UserSettings", "UserSettings.SetExperimentalMode" })
                Assert.Contains(loose, e => e.Expression.Contains(shape, StringComparison.Ordinal));

            // …and not one of them is quietly pre-exempted, which would defeat the canary from
            // the other end.
            foreach (var e in loose)
                Assert.DoesNotContain(e.Key, ShellSurfaceRegistry.InertControls.Keys);

            // ── inside ONE boundary: none of the four is named ──
            var gated = ShellBoundaryScan.ElementsIn(
                "Components/Layout/StatusBar.razor",
                open + "<ShellGate Permission=\"settings\">\n"
                     + direct + alias + lambda + group
                     + "</ShellGate>\n" + code);

            Assert.Equal(4, gated.Count);
            Assert.All(gated, e => Assert.Equal("settings", e.Permission));
        }

        /// <summary>
        /// The P2 shape on its own, verbatim, in the file it was planted in — including the second
        /// half of the exploit, that the OLD decider passed it. Asserted rather than assumed: if
        /// somebody "fixes" the edge scanner to see through an alias and then quietly makes it the
        /// decider again, this line fails and they have to say so out loud.
        /// </summary>
        [Fact]
        public void TheCensusSeesTheGatesP2AliasProbe()
        {
            const string probe = @"
@inject SQLTriage.Data.UserSettingsService UserSettings
<button class=""gate-probe-p2"" @onclick=""P2"">P2</button>
@code {
    void P2() { var s = UserSettings; s.NudgeReportOperator(""P2-LOCAL-ALIAS""); }
}
";
            var found = ShellBoundaryScan.ElementsIn("Components/Layout/StatusBar.razor", probe);

            var loose = found.Single(e => e.Permission == null);
            Assert.Equal("@onclick", loose.Attribute);
            Assert.Equal("P2", loose.Expression);

            // The instrument this replaced is STILL blind to it — the alias hides the receiver, so
            // no edge is produced at all.
            Assert.DoesNotContain(ShellSurfaceRegistry.EdgesIn(probe),
                                  e => e.Contains("NudgeReportOperator", StringComparison.Ordinal));
        }

        /// <summary>
        /// A self-closing <c>&lt;ShellGate /&gt;</c> has no children and must cover nothing. This
        /// is the cheapest way to fake a boundary and it must not work.
        /// </summary>
        [Fact]
        public void ASelfClosingBoundaryCoversNothing()
        {
            var found = ShellBoundaryScan.ElementsIn(
                "Components/Layout/StatusBar.razor",
                "<ShellGate Permission=\"settings\" />\n<button @onclick=\"Evil\">x</button>\n");

            Assert.Null(found.Single().Permission);
        }

        /// <summary>
        /// A boundary covers what it WRAPS and nothing after it. Markup that follows the closing
        /// tag is outside.
        /// </summary>
        [Fact]
        public void MarkupAfterTheClosingTagIsOutsideTheBoundary()
        {
            var found = ShellBoundaryScan.ElementsIn(
                "Components/Layout/StatusBar.razor",
                "<ShellGate Permission=\"settings\"><button @onclick=\"Inside\">a</button></ShellGate>\n"
                + "<button @onclick=\"Outside\">b</button>\n");

            Assert.Equal("settings", found.Single(e => e.Expression == "Inside").Permission);
            Assert.Null(found.Single(e => e.Expression == "Outside").Permission);
        }

        /// <summary>
        /// <c>@onclick:stopPropagation</c> and <c>@onclick:preventDefault</c> take a bool and
        /// register no delegate; <c>@onclick=</c> does. The scan must tell them apart, or the
        /// shell's several event MODIFIERS become phantom controls and the register fills with
        /// noise nobody reads.
        /// </summary>
        [Fact]
        public void AnEventModifierIsNotAHandler()
        {
            var found = ShellBoundaryScan.ElementsIn(
                "Components/Shared/CommandPalette.razor",
                "<div @onclick:stopPropagation @onclick:preventDefault=\"true\">x</div>\n"
                + "<div @onclick=\"Real\">y</div>\n");

            Assert.Equal("Real", found.Single().Expression);
        }

        /// <summary>
        /// A component tag that binds one of the child's <c>EventCallback</c> parameters is an
        /// interactive control AT THE USE SITE.
        ///
        /// <para>This is the hole a raw <c>@on…</c> scan would have left wide open, and it is not
        /// hypothetical: <c>NavMenu</c>'s four install-wide preference switches — the Experimental
        /// switch among them, the exact control an unauthenticated LAN caller drove on 2026-08-01
        /// to write <c>%APPDATA%\SQLTriage\user-settings.json</c> — carry no <c>@onclick</c> at
        /// all. They are <c>&lt;ToggleSwitch ValueChanged="…" /&gt;</c>, and the click lives in
        /// ToggleSwitch.razor, where it is generic and says nothing about who may flip an install
        /// setting.</para>
        ///
        /// <para>Derived from the child's declared <c>[Parameter] public EventCallback</c> names,
        /// never from a name pattern. "Starts with On, ends with Changed" is a lexicon, and a
        /// lexicon is what this round exists to stop using.</para>
        /// </summary>
        [Fact]
        public void AComponentCallbackBindingIsAControlAtTheUseSite()
        {
            Assert.Contains("ValueChanged", ShellBoundaryScan.CallbackParametersOf("ToggleSwitch"));

            var found = ShellBoundaryScan.ElementsIn(
                "Components/Layout/NavMenu.razor",
                "<ToggleSwitch Value=\"@_experimentalMode\" ValueChanged=\"ToggleExperimentalMode\" />\n");

            var element = found.Single();
            Assert.Equal("ToggleSwitch.ValueChanged", element.Attribute);
            Assert.Null(element.Permission);

            // Value="@_experimentalMode" is a plain parameter and must NOT be counted: an
            // instrument that cried wolf on every attribute would be turned off within a week.
            Assert.Single(found);

            // And the real NavMenu switches are inside a boundary today.
            var real = ShellBoundaryScan.ElementsOf("Components/Layout/NavMenu.razor")
                .Where(e => e.Attribute == "ToggleSwitch.ValueChanged")
                .ToList();
            Assert.True(real.Count >= 4,
                "NavMenu should still render its four install-wide preference switches; found "
                + real.Count + ".");
            Assert.All(real, e => Assert.Equal("settings", e.Permission));
        }

        /// <summary>
        /// Razor balances parentheses inside <c>@(…)</c>, so a double-quoted attribute may legally
        /// contain double quotes. Reading the value with a naive "up to the next quote" scan
        /// truncated NavMenu's four category controls to the same string —
        /// <c>@(() =&gt; SetCategory(</c> — which collapsed four different controls onto ONE
        /// register key, so a single exemption would silently have covered all four and any fifth
        /// somebody added later.
        /// </summary>
        [Fact]
        public void FourControlsThatDifferInsideNestedQuotesAreFourKeys()
        {
            var found = ShellBoundaryScan.ElementsIn(
                "Components/Layout/NavMenu.razor",
                "<div @onclick=\"@(() => SetCategory(\"Governance\"))\">a</div>\n"
                + "<div @onclick=\"@(() => SetCategory(\"Operations\"))\">b</div>\n"
                + "<div @onclick=\"@(() => SetCategory(\"Diagnostics\"))\">c</div>\n"
                + "<div @onclick=\"@(() => SetCategory(\"Intelligence\"))\">d</div>\n");

            Assert.Equal(4, found.Count);
            Assert.Equal(4, found.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count());
            Assert.Contains(found, e => e.Expression == "@(() => SetCategory(\"Governance\"))");
        }

        /// <summary>
        /// The controls the last three rounds were each defeated through, pinned by name and
        /// permission in the SHIPPED markup. A refactor that loses one of these fails here rather
        /// than on somebody's LAN.
        /// </summary>
        [Theory]
        [InlineData("Components/Layout/StatusBar.razor", "@onclick", "ToggleAnimationsAsync", "settings")]
        [InlineData("Components/Layout/NavMenu.razor", "ToggleSwitch.ValueChanged", "ToggleExperimentalMode", "settings")]
        [InlineData("Components/Layout/NavMenu.razor", "ToggleSwitch.ValueChanged", "ToggleNotifications", "settings")]
        [InlineData("Components/Layout/DashboardToolbar.razor", "@onchange", "OnRefreshIntervalChanged", "settings")]
        [InlineData("Components/Layout/DashboardToolbar.razor", "@onclick", "OpenPdfModal", "export_data")]
        [InlineData("Components/Layout/DashboardToolbar.razor", "@onchange", "OnServerConnectionChanged", "execute_checks")]
        [InlineData("Components/Shared/GlobalServerSelector.razor", "@onchange", "OnServerChanged", "execute_checks")]
        [InlineData("Components/Shared/ServerModeToggle.razor", "@onclick", "StopServer", "settings")]
        [InlineData("Components/Shared/ServerModeToggle.razor", "@onclick", "StartServer", "settings")]
        [InlineData("Components/Shared/OnboardingWizard.razor", "@onclick", "Dismiss", "settings")]
        public void TheControlsEarlierRoundsWereDefeatedThroughAreStillBehindABoundary(
            string file, string attribute, string expression, string permission)
        {
            var matches = ShellBoundaryScan.ElementsOf(file)
                .Where(e => e.Attribute == attribute && e.Expression == expression)
                .ToList();

            Assert.True(matches.Count > 0,
                file + " no longer renders " + attribute + "=\"" + expression + "\". If it moved, "
                + "move this row with it — a pin that silently stops pinning anything is how an "
                + "allow-list becomes decoration.");

            Assert.All(matches, e => Assert.Equal(permission, e.Permission));
        }

        // ── 4. THE BOUNDARY ITSELF ───────────────────────────────────────

        /// <summary>
        /// The boundary must actually GATE. A <c>ShellGate</c> that rendered its children
        /// unconditionally would turn every test above into decoration — the whole file would pass
        /// while nothing was gated at all, which is precisely the failure mode round 2's page
        /// census shipped with.
        /// </summary>
        [Fact]
        public void TheBoundaryComponentEvaluatesThePermissionAndRendersNothingWhenDenied()
        {
            var path = Path.Combine(RawPassedScan.RepoRoot().FullName,
                                    "Components", "Shared", "ShellGate.razor");
            Assert.True(File.Exists(path), "Components/Shared/ShellGate.razor is missing — the "
                                           + "boundary census has nothing to measure.");

            var text = File.ReadAllText(path);

            Assert.Contains("UserState.IsAuthorized(Permission)", text);
            Assert.Contains("UserState.IsAuthorizedWithBreakGlass(Permission)", text);
            Assert.Contains("@if (Permitted)", text);

            // No else-branch: denied renders NOTHING. A boundary that could also render the denied
            // case would be two decisions in one component.
            Assert.DoesNotContain("else", text.Split("@code")[0]);

            // The permission is a required parameter, so <ShellGate> with no permission will not
            // compile into an accidental allow.
            Assert.Contains("[Parameter, EditorRequired] public string Permission", text);
        }

        /// <summary>
        /// The boundary is one component, and there is one of it. Two boundary components is the
        /// round-5 defect in a new costume: two places for a claim about the same control to live,
        /// and only one of them read by the census.
        /// </summary>
        [Fact]
        public void TheShellHasExactlyOneBoundaryComponent()
        {
            var shipped = new DirectoryInfo(RawPassedScan.RepoRoot().FullName)
                .EnumerateFiles("ShellGate*.razor", SearchOption.AllDirectories)
                .Where(f => !f.FullName.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .Where(f => !f.FullName.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .Select(f => Path.GetRelativePath(RawPassedScan.RepoRoot().FullName, f.FullName))
                .ToList();

            Assert.Single(shipped);
        }

        // ── Helper ───────────────────────────────────────────────────────

        private static List<ShellBoundaryScan.Element> AllElements() =>
            ShellSurfaceRegistry.ShellComponents
                .SelectMany(ShellBoundaryScan.ElementsOf)
                .ToList();
    }
}
