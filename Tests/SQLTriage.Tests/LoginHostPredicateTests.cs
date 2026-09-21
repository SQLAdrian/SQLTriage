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
    /// THE SIGN-IN PAGE MUST NOT ASK WHETHER *THIS CONTAINER* STARTED A LISTENER.
    ///
    /// <para><b>The defect, PROVED live on the installed Windows service 2026-09-08.</b>
    /// <c>GET http://localhost:5155/login</c> returned <b>200 carrying "This application is running in
    /// desktop (WPF) mode"</b> with no <c>Location</c> header, while that install's
    /// <c>config/rbac-config.json</c> held <c>"enabled": true</c>. The dead-end arm requires
    /// <c>IsRunning == false</c> AND <c>Config.Enabled == true</c>, so that single response proves both
    /// halves at once. Cause: <c>ServerModeService.IsRunning</c> is <c>_webApp != null</c> on that
    /// container's own service, and the headless hosts (<c>--server</c>, the installed service) never
    /// call <c>StartAsync</c> — <c>WindowsServiceHost.cs:262</c> says so in as many words: "NOTHING HERE
    /// EVER STARTS IT".</para>
    ///
    /// <para><b>ONE predicate, TWO failure modes.</b> The same false flag skipped the
    /// <c>OnInitialized</c> redirect to the working <c>/auth/login</c> AND dropped the markup into its
    /// <c>else</c> arm. The page neither sent the operator onward nor offered them anything. The
    /// redirect's own comment shows the author knew where sign-in lives — "Hand off to the raw auth
    /// endpoint which shows OAuth provider buttons" — so the handoff existed and was unreachable.</para>
    ///
    /// <para><b>⚠ WHAT THIS DEFECT DID NOT DO, recorded because the first telling of it was wrong and a
    /// gate refuted it.</b> It did NOT lock an operator out of Settings. Off-loopback, <c>/login</c>
    /// never renders — admission answers <c>401 X-SQLTriage-Admission: sign-in-required</c> before this
    /// page runs. On loopback, <c>/settings</c> renders 200 through its own recovery banner. The true
    /// and larger finding is that <c>RouteConstants.Login</c> has <b>zero consumers</b>: nothing in the
    /// app links here, so the shell offers no sign-in affordance anywhere and this page is the only
    /// route named for one. A measured defect with an invented consequence attached is the house's most
    /// common failure, and this file is not going to repeat it.</para>
    ///
    /// <para><b>Why <see cref="HostEnvironmentInfo"/> is the right question.</b> It exists for precisely
    /// this and its own summary names this false negative, together with the matching false positive
    /// where server mode ON in the WPF process flipped <c>IsRunning</c> true for the DESKTOP container.
    /// Registered per container: <c>Desktop</c> in <c>ServiceCollectionExtensions.cs:446</c>,
    /// <c>BrowserHosted</c> in <c>ServerModeService.cs:400</c> and <c>WindowsServiceHost.cs:267</c>.
    /// The auth decision was migrated to it; this page was missed.</para>
    ///
    /// <para><b>What this file does NOT claim.</b> That sign-in WORKS — admission and the
    /// <c>/auth/login</c> endpoint are out of scope and were proved working separately. This file
    /// asserts only that the page asks the right question, in the right places, so the dead end cannot
    /// silently return.</para>
    /// </summary>
    public sealed class LoginHostPredicateTests
    {
        private const string Page = "Pages/Login.razor";

        private static string ReadLoginMarkup()
        {
            var root = RawPassedScan.RepoRoot();
            var path = Path.Combine(root.FullName, "Pages", "Login.razor");
            Assert.True(File.Exists(path), $"{Page} not found at {path}");
            return File.ReadAllText(path);
        }

        /// <summary>
        /// Comments are stripped before any rule is applied, so the explanatory block in the page —
        /// which names <c>ServerModeService.IsRunning</c> on purpose, to say why it is NOT used — cannot
        /// fail its own file. Razor comments are not compiled, so a mention inside <c>@*…*@</c> is inert
        /// by construction. This is the same trap the UI ratchet has, where a regex over whole file text
        /// counts a comment that merely mentions the banned thing.
        /// </summary>
        private static string[] CodeLines(string markup)
        {
            var withoutRazor = Regex.Replace(markup, @"@\*.*?\*@", string.Empty, RegexOptions.Singleline);
            var stripped = Regex.Replace(withoutRazor, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);
            return stripped.Split('\n');
        }

        [Fact]
        public void TheSignInPageDoesNotGateItselfOnServerModeIsRunning()
        {
            var offenders = CodeLines(ReadLoginMarkup())
                .Select((line, i) => (line: line.Trim(), number: i + 1))
                .Where(x => x.line.Contains("IsRunning", StringComparison.Ordinal))
                .ToList();

            Assert.True(
                offenders.Count == 0,
                $"{Page} gates on ServerModeService.IsRunning again. That flag is FALSE on the headless "
                + "hosts (--server and the installed Windows service), which never call StartAsync — see "
                + "WindowsServiceHost.cs:262. The page then loses BOTH its redirect to /auth/login and its "
                + "sign-in affordance on exactly the host that needs them, and renders the desktop dead "
                + "end instead. Ask HostEnvironmentInfo.IsBrowserHosted; it is registered per container "
                + "and exists for this question. Offending lines:\n  "
                + string.Join("\n  ", offenders.Select(o => $"{o.number}: {o.line}")));
        }

        [Fact]
        public void TheRedirectToTheWorkingSignInEndpointIsGatedOnBrowserHosting()
        {
            var lines = CodeLines(ReadLoginMarkup());

            var redirect = Array.FindIndex(lines, l => l.Contains("NavigateTo(\"/auth/login\"", StringComparison.Ordinal));
            Assert.True(redirect >= 0,
                $"{Page} no longer navigates to /auth/login. That redirect is the whole point of the page "
                + "on a browser-hosted host; if it moved, point this test at wherever it went.");

            // The nearest `if (` ABOVE the redirect is what governs it. Counting occurrences of
            // IsBrowserHosted anywhere in the file does NOT establish this — a gate proved that a decoy
            // such as `private bool Browser => Host.IsBrowserHosted;` restores the count while leaving
            // the redirect ungated, which would force-navigate a WPF desktop user to an endpoint the
            // BlazorWebView host never maps. So this LOCATES the guard instead of counting it.
            var guard = Enumerable.Range(0, redirect).Reverse()
                .Select(i => lines[i].Trim())
                .FirstOrDefault(l => l.StartsWith("if (", StringComparison.Ordinal));

            Assert.True(
                guard != null && guard.Contains("IsBrowserHosted", StringComparison.Ordinal),
                $"The /auth/login redirect in {Page} is not guarded by IsBrowserHosted. Its nearest "
                + $"enclosing condition is: {guard ?? "(none found)"}. Ungated, this force-navigates a WPF "
                + "desktop user to /auth/login, which only the Kestrel hosts map — the exact false "
                + "positive HostEnvironmentInfo was introduced to remove.");
        }

        [Fact]
        public void EveryRenderedArmBeforeTheDeadEndIsGatedOnBrowserHosting()
        {
            var lines = CodeLines(ReadLoginMarkup());

            var deadEnd = Array.FindIndex(lines, l => l.Contains("desktop (WPF) mode", StringComparison.Ordinal));
            Assert.True(deadEnd >= 0,
                $"The desktop-mode message is gone from {Page}. If it was deliberately removed, delete "
                + "this test with the reason; do not leave it asserting nothing.");

            // Every conditional arm above the dead end must ask the host question, and the dead end
            // itself must sit under a BARE else — an arm with a condition of its own can be true while a
            // browser is being served, which is how the dead end became reachable in the first place.
            var arms = new List<string>();
            for (var i = 0; i < deadEnd; i++)
            {
                var l = lines[i].Trim();
                if (l.StartsWith("@if (", StringComparison.Ordinal) || l.StartsWith("else if (", StringComparison.Ordinal))
                    arms.Add(l);
            }

            Assert.True(arms.Count >= 2,
                $"Expected at least two conditional arms above the dead end in {Page} (RBAC enabled, and "
                + $"RBAC not enabled), found {arms.Count}. If the page was restructured, re-point this test.");

            var unguarded = arms.Where(a => !a.Contains("IsBrowserHosted", StringComparison.Ordinal)).ToList();
            Assert.True(
                unguarded.Count == 0,
                $"These arms in {Page} render before the desktop dead end without asking whether a browser "
                + "is being served, so the dead end can be reached on a browser-hosted host again:\n  "
                + string.Join("\n  ", unguarded));

            var governing = Enumerable.Range(0, deadEnd).Reverse()
                .Select(i => lines[i].Trim())
                .FirstOrDefault(l => l.StartsWith("@if", StringComparison.Ordinal)
                                  || l.StartsWith("else", StringComparison.Ordinal));

            Assert.True(
                governing != null && governing.StartsWith("else", StringComparison.Ordinal)
                    && !governing.Contains("if", StringComparison.Ordinal),
                $"The desktop dead end must sit under a bare `else`, reachable only when the arms above are "
                + $"false. Its governing branch is now: {governing ?? "(none found)"}.");
        }
    }
}
