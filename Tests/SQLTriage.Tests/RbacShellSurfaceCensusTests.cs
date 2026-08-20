/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// THE INVERTED CENSUS. Everything before this asked "does this call look like a mutation?"
    /// and let an unrecognised answer pass. This one asks "has anybody looked at this edge?" and
    /// fails when the answer is no.
    ///
    /// <para><b>The proof that the old question was the wrong one.</b> A cold gate added ONE real
    /// control to <c>Components/Layout/MainLayout.razor</c> — the file whose
    /// <c>ReviewedActingComponents</c> entry pinned its exact call set:</para>
    /// <code>
    /// &lt;button class="gate-probe" @onclick="@(() =&gt; UserSettings.SetAnonymiseServerNames(false))"&gt;
    ///     Un-anonymise logs
    /// &lt;/button&gt;
    /// </code>
    /// <para>That call disables install-wide anonymisation of client server names in logs and
    /// resets the alias map (<c>Data/UserSettingsService.cs:502</c>). The census answered
    /// <c>Failed: 0, Passed: 42</c>. Built and clicked from the LAN denial page, the setting went
    /// True → False on disk. Green suite, live exploit. <c>MutatingCalls</c> matched a 29-verb
    /// lexicon with no bare <c>Set</c>; the DI guard only flagged methods that raise an event;
    /// <c>SetAnonymiseServerNames</c> is neither.</para>
    ///
    /// <para><b>What changed.</b> Nothing about the vocabulary — widening a lexicon is what rounds
    /// 4 and 5 both did, and both were defeated through the word they had not added. The
    /// MEMBERSHIP TEST changed: <see cref="ShellSurfaceRegistry.EdgesOf"/> enumerates every call
    /// and every property write the shell makes on an injected service, asks nothing about the
    /// member's name or the service's lifetime, and an edge that is not on
    /// <see cref="ShellSurfaceRegistry.Edges"/> fails here. Unknown is red.</para>
    ///
    /// <para><see cref="TheCensusSeesTheGatesOwnProbe"/> is the permanent canary: it feeds the
    /// scanner the exact control above and fails if it ever goes invisible again.</para>
    ///
    /// <para><b>THIS FILE IS NO LONGER THE DECIDER. Round 7, 2026-08-02.</b> The inversion above
    /// was right about the DEFAULT — unknown is red — and wrong about the QUESTION. A cold gate
    /// put two buttons in <c>StatusBar</c>, identical but for the receiver expression:</para>
    /// <code>
    /// P1   UserSettings.NudgeReportOperator("P1-DIRECT-ALIAS")            → named here, RED
    /// P2   var s = UserSettings; s.NudgeReportOperator("P2-LOCAL-ALIAS")  → invisible here, GREEN
    /// </code>
    /// <para>P2 was built and driven from <c>http://192.10.10.32:5300/scheduled-tasks</c> —
    /// unauthenticated, non-loopback, badge <c>viewer</c>, on a denial page — and it worked while
    /// all five census classes read <c>Total tests: 134, Passed: 134</c>. One line of C#. Widening
    /// the receiver match would only move the boundary to the next shape: a lambda, a method
    /// group, an interface reference, a delegate field, reflection.</para>
    ///
    /// <para>So <c>RbacShellBoundaryCensusTests</c> decides whether a shell control may RENDER,
    /// by asking a markup question the call cannot influence. What survives here is a different
    /// and still-useful question — what an already-permitted control can REACH — and that is why
    /// this file stays. <b>Defence in depth, not the decider.</b> Anyone tempted to promote it
    /// back has to answer for the alias first.</para>
    /// </summary>
    public class RbacShellSurfaceCensusTests
    {
        // ── 1. THE INVERSION ─────────────────────────────────────────────

        /// <summary>
        /// Every edge from an always-rendered shell component into an injected service is on the
        /// register, or this fails and names it.
        /// </summary>
        [Fact]
        public void EveryShellEdgeIsOnTheRegister()
        {
            var unknown = new List<string>();

            foreach (var component in ShellSurfaceRegistry.ShellComponents)
                foreach (var edge in ShellSurfaceRegistry.EdgesOf(component))
                {
                    var key = ShellSurfaceRegistry.Key(component, edge);
                    if (!ShellSurfaceRegistry.Edges.ContainsKey(key)) unknown.Add(key);
                }

            Assert.True(unknown.Count == 0,
                "These calls are made by the ALWAYS-RENDERED SHELL — markup that renders on every "
                + "route in the app, AccessDenied pages included — and nobody has said what they "
                + "do.\n\n"
                + "This test does not ask whether the member's name looks like a mutation. That "
                + "question was asked by a 29-verb lexicon and answered wrongly for "
                + "UserSettingsService.SetAnonymiseServerNames, which an unauthenticated LAN "
                + "caller then drove from a denial page while the suite was green.\n\n"
                + "Gate the control and add the edge to ShellSurfaceRegistry.Edges with the "
                + "permission it needs, or add it with a null permission and a reason that says "
                + "what the call ACTUALLY IS. A reason that turns out to be false is worse than no "
                + "entry: it launders the gap as reviewed, which is how rounds 2 and 5 were both "
                + "defeated.\n  "
                + string.Join("\n  ", unknown));
        }

        /// <summary>An allow-list nobody prunes becomes the whole app.</summary>
        [Fact]
        public void TheRegisterHasNoStaleEntries()
        {
            var live = new HashSet<string>(
                ShellSurfaceRegistry.ShellComponents
                    .SelectMany(c => ShellSurfaceRegistry.EdgesOf(c)
                                        .Select(e => ShellSurfaceRegistry.Key(c, e))),
                StringComparer.Ordinal);

            var stale = ShellSurfaceRegistry.Edges.Keys.Where(k => !live.Contains(k)).ToList();

            Assert.True(stale.Count == 0,
                "These reviewed edges no longer exist. Prune them — an entry that describes a call "
                + "nobody makes any more is a reason the next reviewer will trust about a file it "
                + "no longer describes:\n  " + string.Join("\n  ", stale));
        }

        /// <summary>
        /// Every entry says something. An empty or placeholder reason is how a register becomes
        /// decoration, which is exactly what round 2's page census was.
        /// </summary>
        [Fact]
        public void EveryRegisteredEdgeCarriesARealReason()
        {
            var thin = ShellSurfaceRegistry.Edges
                .Where(kv => kv.Value.Reason.Trim().Length < 40
                             || kv.Value.Reason.Contains("TODO", StringComparison.OrdinalIgnoreCase)
                             || kv.Value.Reason.Contains("n/a", StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .ToList();

            Assert.True(thin.Count == 0,
                "These reviewed edges carry no real reason:\n  " + string.Join("\n  ", thin));
        }

        // ── 2. A NAMED PERMISSION IS A CLAIM ABOUT THE SHIPPED MARKUP ────

        /// <summary>
        /// An edge whose register entry names a permission must be gated in the file that makes
        /// it: a <c>May…</c> property reading <c>UserState.IsAuthorized("&lt;permission&gt;")</c>,
        /// and at least one handler guarded on it.
        ///
        /// <para>Both halves matter. The render gate is what keeps the control off a denied
        /// caller's screen; the handler guard is what holds when somebody moves the markup. Round
        /// 4 learned the second half by driving buttons that a GET said were not there.</para>
        ///
        /// <para><b>Round 7 moved the first half.</b> The render condition is no longer an
        /// <c>@if (MayXxx)</c> in each file — it is a <c>&lt;ShellGate Permission="…"&gt;</c>
        /// boundary, because a boundary is a MARKUP fact the handler's code cannot influence and
        /// an <c>@if</c> on a property is not. So this test now requires three things rather than
        /// counting occurrences of a name: the boundary in the markup, the property declaration,
        /// and the handler guard.</para>
        /// </summary>
        [Theory]
        [MemberData(nameof(PermissionedEdges))]
        public void EachPermissionedEdgeIsGatedInTheFileThatMakesIt(string key, string permission)
        {
            var file = key.Split('→')[0].Trim();
            var text = ReadWithCodeBehind(file);
            Assert.False(text == null, key + " names a file that does not exist.");

            // 1 — THE BOUNDARY. Round 7's decider: the control renders inside <ShellGate>.
            Assert.True(
                ShellBoundaryScan.ElementsOf(file).Any(e => e.Permission == permission),
                file + " makes a call registered as needing \"" + permission + "\" (" + key + ") "
                + "and renders no control inside a <ShellGate Permission=\"" + permission + "\">. "
                + "An @if on a May… property is not a boundary: `var s = UserSettings;` was enough "
                + "to make the census that read those properties answer green on a live exploit.");

            // 2 — THE DECLARATION. Still required, because the handler guard needs something to
            // read, and because a permission written only in markup is a permission no method can
            // re-check.
            var declaration = Regex.Match(text!,
                @"private bool (May[A-Za-z]+)\s*=>[^;]*UserState\.IsAuthorized\(""" + permission + @"""\)");

            Assert.True(declaration.Success,
                file + " renders a \"" + permission + "\" boundary but declares no gate property "
                + "for the handlers to re-check. Add `private bool MayXxx => "
                + "UserState.IsAuthorized(\"" + permission + "\")`.");

            var gate = declaration.Groups[1].Value;

            // 3 — THE HANDLER GUARD. A denied caller renders no button and so registers no
            // handler — but the method must refuse anyway, because that is the assumption round 4
            // falsified by invoking the handlers directly.
            Assert.True(Regex.IsMatch(text!, @"if \(!" + gate + @"\)\s*return"),
                file + " never guards a handler on " + gate + ".");
        }

        public static IEnumerable<object[]> PermissionedEdges =>
            ShellSurfaceRegistry.Edges
                .Where(kv => kv.Value.Permission != null)
                .Select(kv => new object[] { kv.Key, kv.Value.Permission! });

        /// <summary>
        /// The permissions a shell control may name. An unknown string here would be silently
        /// admin-only by <c>RbacService.HasPermission</c>'s fallback, which is a gate nobody chose.
        /// </summary>
        [Fact]
        public void EveryPermissionNamedIsOneTheRbacServiceKnows()
        {
            var known = new[]
            {
                "settings", "manage_servers", "manage_users", "manage_alerts",
                "execute_checks", "run_scripts", "export_data", "acknowledge_alerts",
                "view_dashboard", "view_results", "view_audit_log",
            };

            var unknown = ShellSurfaceRegistry.Edges
                .Where(kv => kv.Value.Permission != null && !known.Contains(kv.Value.Permission))
                .Select(kv => kv.Key + " → \"" + kv.Value.Permission + "\"")
                .ToList();

            Assert.True(unknown.Count == 0,
                "RbacService.HasPermission answers unknown permissions admin-only by fallback, so a "
                + "typo here is a gate that works by accident and would silently widen the day the "
                + "fallback changed:\n  " + string.Join("\n  ", unknown));
        }

        // ── 3. THE CANARY ────────────────────────────────────────────────

        /// <summary>
        /// THE PERMANENT CANARY: the cold gate's own probe, fed to this round's scanner.
        ///
        /// <para>If the census ever goes blind to a bare <c>Set…</c> on an injected singleton
        /// again — a narrowed regex, a re-introduced verb list, a "receiver is benign" excuse —
        /// this goes red, and it names the control that was live on the shipped build while the
        /// suite read <c>Failed: 0, Passed: 42</c>.</para>
        /// </summary>
        [Fact]
        public void TheCensusSeesTheGatesOwnProbe()
        {
            // Verbatim shape of what the gate added to MainLayout, including its inject line.
            const string probe = @"
@inject SQLTriage.Data.UserSettingsService UserSettings
<button class=""gate-probe"" @onclick=""@(() => UserSettings.SetAnonymiseServerNames(false))"">Un-anonymise logs</button>
";

            var edges = ShellSurfaceRegistry.EdgesIn(probe);

            Assert.Contains("UserSettingsService.SetAnonymiseServerNames", edges);

            // …and it is not silently on the register, which would defeat the canary from the
            // other end.
            Assert.DoesNotContain(
                ShellSurfaceRegistry.Key("Components/Layout/MainLayout.razor",
                                         "UserSettingsService.SetAnonymiseServerNames"),
                ShellSurfaceRegistry.Edges.Keys);

            // ── THE DELIBERATE DECISION THIS LINE DEMANDED, taken 2026-08-10 ──────────────────
            //
            // This assertion used to read: MutatingCalls is STILL blind to a bare Set, and if
            // somebody "fixes" that by adding the verb, this line fails and whoever changed it has
            // to decide, deliberately, which instrument decides membership — because it must not go
            // back to being the lexicon. Round 8 of the page census added "Set", so here is the
            // decision, in the place that asked for it.
            //
            // WHY THE VERB WAS ADDED. RbacComponentGateCensusTests skips every routable page
            // ("routable — the page census owns it"), and the page census's lexicon had no Set. So a
            // bare Set call in a HANDLER on a routable page was seen by neither instrument, and
            // there was one: Pages/QuickCheck.razor::ToggleDiagnosticPane calls
            // UserSettings.SetShowDiagnosticPane, which writes this install's settings file. The
            // hole was between the two censuses, not inside either.
            //
            // MEMBERSHIP DID NOT MOVE. For components the edge scanner is still the instrument, and
            // that is a property of ActingCallsIn rather than of the lexicon's blindness: it drops
            // every lexicon hit whose receiver is an injected alias, so this probe's call arrives
            // exactly once, under the edge scanner's TYPE key and never under the lexicon's alias
            // key. Asserting the SPLIT is strictly stronger than asserting the lexicon cannot see
            // it, because the split is the thing that was actually being protected.
            var acting = RbacComponentGateCensusTests.ActingCallsIn(probe);
            Assert.Contains("UserSettingsService.SetAnonymiseServerNames", acting);
            Assert.DoesNotContain("UserSettings.SetAnonymiseServerNames", acting);
        }

        /// <summary>
        /// The scanner sees a PROPERTY WRITE, not only a call. <c>NavMenu</c> assigns
        /// <c>ToastService.Enabled</c> — a singleton, so process-wide — and every instrument in
        /// the tree before this round was call-shaped and could not see it.
        /// </summary>
        [Fact]
        public void TheCensusSeesAPropertyWriteAndNotAComparison()
        {
            const string probe = @"
@inject SQLTriage.Data.ToastService Toast
@code {
    void A() { Toast.Enabled = false; }
    bool B() => Toast.Enabled == false;
    string C() => Toast.Enabled ? ""on"" : ""off"";
}
";
            var edges = ShellSurfaceRegistry.EdgesIn(probe);

            Assert.Contains("ToastService.Enabled=", edges);
            Assert.DoesNotContain("ToastService.Enabled", edges);   // the comparison is not a write
        }

        // ── 4. THE SHELL SET IS DERIVED, AND IT IS NOT EMPTY ─────────────

        /// <summary>
        /// A census that enumerates nothing passes. This pins that the closure really walks
        /// MainLayout's render graph and reaches the components five rounds were defeated through.
        /// </summary>
        [Fact]
        public void TheShellClosureIsDerivedFromWhatMainLayoutActuallyRenders()
        {
            var shell = ShellSurfaceRegistry.ShellComponents;

            Assert.True(shell.Count >= 12,
                "The shell closure found only " + shell.Count + " components. It is meant to walk "
                + "MainLayout's component tags transitively; a number this small means the walk is "
                + "broken and this whole file is passing because it is looking at nothing.");

            foreach (var expected in new[]
                     {
                         "Components/Layout/MainLayout.razor",
                         "Components/Layout/NavMenu.razor",
                         "Components/Layout/StatusBar.razor",
                         "Components/Layout/DashboardToolbar.razor",
                         "Components/Shared/OnboardingWizard.razor",
                         "Components/Shared/GlobalServerSelector.razor",
                         // reached only THROUGH StatusBar — the transitive hop is the point:
                         // this is the component round 4 found on every AccessDenied page.
                         "Components/Shared/ServerModeToggle.razor",
                     })
            {
                Assert.Contains(expected, shell);
            }

            // A routable page is not shell. The censuses partition the tree; an overlap would let a
            // surface be "covered" by whichever one is looser.
            Assert.DoesNotContain("Pages/Settings.razor", shell);
        }

        // ── 5. ONE SOURCE OF TRUTH (the round-5 contradiction, made impossible) ──

        /// <summary>
        /// THE STRUCTURAL FIX FOR TWO REGISTERS THAT DISAGREED.
        ///
        /// <para>Round 5's commit put <c>Components/Layout/DashboardToolbar.razor</c> on
        /// <c>InertInteractiveComponents</c> — the "makes no acting call" list — with the
        /// justification "raises EventCallbacks to the host dashboard", and in the SAME COMMIT put
        /// <c>DashboardToolbar → ServerConnectionManager.SetCurrentServer</c> on
        /// <c>ReviewedLayoutEdges</c>. Both described the same file; only one could be true. The
        /// justification was false for six calls: <c>SetDataSource</c>, <c>SetRefreshInterval</c>,
        /// <c>SetDefaultTimeRange</c>, <c>AutoRefreshService.SetInterval</c>,
        /// <c>PrintToPdfAsync</c>, <c>PrintViaBrowserAsync</c>.</para>
        ///
        /// <para>Fixing the entry would have left the shape that produced it. So the shell is now
        /// owned by ONE register and a shell component may not appear on the component census's
        /// lists at all — there is no second place for a contradicting claim to live.</para>
        /// </summary>
        [Fact]
        public void NoShellComponentSitsOnTheComponentCensusLists()
        {
            var offenders = RbacComponentGateCensusTests.AllListedComponentPaths()
                .Where(ShellSurfaceRegistry.IsShell)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Assert.True(offenders.Count == 0,
                "These components are part of the ALWAYS-RENDERED SHELL and are also named on one "
                + "of RbacComponentGateCensusTests' three lists. The shell has exactly one "
                + "register — ShellSurfaceRegistry.Edges — precisely so that two lists cannot "
                + "disagree about the same file the way they did on 2026-08-02:\n  "
                + string.Join("\n  ", offenders));
        }

        // ── Helper ───────────────────────────────────────────────────────

        private static string? ReadWithCodeBehind(string relativePath)
        {
            var full = Path.Combine(RawPassedScan.RepoRoot().FullName,
                                    relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) return null;
            var text = File.ReadAllText(full);
            if (File.Exists(full + ".cs")) text += File.ReadAllText(full + ".cs");
            return text;
        }
    }
}
