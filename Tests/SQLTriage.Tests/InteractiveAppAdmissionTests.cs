/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>One host for the whole class — 89 routes × 3 origin classes is a lot of listeners.</summary>
    public sealed class AdmissionHostFixture : IAsyncLifetime
    {
        internal InteractiveAppAdmissionHost Host { get; private set; } = null!;

        public async Task InitializeAsync() => Host = await InteractiveAppAdmissionHost.StartAsync();

        public async Task DisposeAsync()
        {
            if (Host != null) await Host.DisposeAsync();
        }
    }

    // BM:InteractiveAppAdmissionTests — THE DECIDER: runtime HTTP, both origin classes
    /// <summary>
    /// THE DECIDER FOR ROUND 8, and the first one in this lane that does not read component
    /// source.
    ///
    /// <para><b>Why the instrument changed.</b> Four instruments were defeated in four rounds,
    /// each through the category it could not see: a verb lexicon
    /// (<c>SetAnonymiseServerNames</c>), prefix anchoring (<c>ShortcutSvc.TriggerRun</c>), full
    /// edge enumeration (a receiver alias — <c>var s = UserSettings; s.M()</c>), and finally
    /// round 7's markup-position boundary, beaten by fourteen shapes from the verifier and by two
    /// from the cold gate that are pure idiomatic Blazor: <c>&lt;input @bind:get @bind:set /&gt;</c>
    /// and <c>&lt;EditForm OnValidSubmit&gt;</c>. Every one was green on its census and fired live
    /// from <c>http://192.10.10.32/scheduled-tasks</c> as an unauthenticated viewer, on a page
    /// reading "restricted to Admin users".</para>
    ///
    /// <para><b>So this test asserts a PROPERTY OF THE WIRE, not of the source.</b> From a
    /// non-loopback origin, unauthenticated: no route returns an interactive application and
    /// <c>/_blazor</c> does not negotiate. That property is immune to aliases, lambdas, method
    /// groups, <c>@attributes</c>, <c>MarkupString</c>, <c>DynamicComponent</c>, fully-qualified
    /// tags, <c>@bind:get</c>/<c>@bind:set</c> and <c>EditForm</c> alike — not because it handles
    /// them, but because it never looks at the thing they vary.</para>
    ///
    /// <para><b>The routes are enumerated from the SHIPPED ASSEMBLY</b> (<c>RouteAttribute</c> by
    /// reflection), so a page added next week is driven by this test the day it compiles, with
    /// nobody having to remember to add it to a list.</para>
    ///
    /// <para><b>The red half, measured.</b> These assertions bind types that do not exist at
    /// <c>938f3b3</c> — the boundary itself — so at the parent commit they do not compile: there
    /// is no HTTP boundary there to fail. The countable measurement is the single line: with the
    /// files shipped here, deleting <c>app.UseSqlTriageAdmission(rbac)</c> from
    /// <see cref="InteractiveAppAdmission.UseSqlTriageFrontDoor"/> — the composition
    /// <see cref="InteractiveAppAdmissionHost"/> drives — turns every non-loopback assertion below
    /// red, and restoring it turns them green. The count is in the commit message.</para>
    /// </summary>
    public class InteractiveAppAdmissionTests : IClassFixture<AdmissionHostFixture>
    {
        private readonly InteractiveAppAdmissionHost _host;

        public InteractiveAppAdmissionTests(AdmissionHostFixture fixture) => _host = fixture.Host;

        // ── The property, over every route the app publishes ─────────────

        [Fact]
        public async Task NoRouteServesTheInteractiveApplicationToAnUnauthenticatedNonLoopbackCaller()
        {
            var routes = InteractiveAppAdmissionHost.AppRouteTemplates();

            // A sanity floor on the ENUMERATION, not a contract on the page count: its only job is
            // to stop this test quietly driving nothing if reflection breaks. It has to be
            // edition-aware, because a community build Content-Removes most of Pages\ — premium,
            // dev-tools, portal, operations and live-monitoring are all excluded — and the fixed
            // >50 floor was one of the seven failures the community suite produced on 2026-08-04,
            // the first time CI ever got past BUILD to run it. Measured that day: 46 routable pages
            // under community against the full build's larger set. 40 keeps the floor meaningful
            // without pinning a number that every gating change would churn.
            var floor = BuildModules.Community ? 40 : 50;
            Assert.True(routes.Count > floor,
                $"Only {routes.Count} routable pages were found by reflection (floor {floor} for this "
                + "edition). The enumeration is broken, and a test that drives almost nothing proves "
                + "almost nothing.");

            using var client = InteractiveAppAdmissionHost.AnonymousClient(_host.NonLoopbackBase);
            var served = new List<string>();

            foreach (var template in routes)
            {
                var url = InteractiveAppAdmissionHost.Concretise(template);
                using var response = await client.GetAsync(url);
                var body = await response.Content.ReadAsStringAsync();

                if (body.Contains(InteractiveAppAdmissionHost.InteractiveAppMarker, StringComparison.Ordinal)
                    || body.Contains(SQLTriage.Components.Shared.BoundaryCanary.DomMarker, StringComparison.Ordinal)
                    || body.Contains("blazor.web.js", StringComparison.Ordinal))
                {
                    served.Add($"{template} → {url}  [{(int)response.StatusCode}] served an application");
                    continue;
                }

                if (response.StatusCode != HttpStatusCode.Redirect
                    && response.StatusCode != HttpStatusCode.Unauthorized)
                {
                    served.Add($"{template} → {url}  unexpected status {(int)response.StatusCode}");
                    continue;
                }

                if (!response.Headers.TryGetValues(InteractiveAppAdmission.RefusalHeader, out var stamp)
                    || !stamp.Contains(InteractiveAppAdmission.RefusalHeaderValue))
                {
                    served.Add($"{template} → {url}  refused without the admission stamp — refused by something else");
                }
            }

            Assert.True(served.Count == 0,
                "An unauthenticated caller from " + _host.NonLoopbackAddress
                + " was handed an interactive application on " + served.Count + " of " + routes.Count
                + " routes:\n  " + string.Join("\n  ", served));
        }

        [Fact]
        public async Task EveryRouteStillServesTheApplicationOnLoopback()
        {
            // The bootstrap hatch rounds 1-3 built, unchanged. An unconfigured install must stay
            // fully usable from the box it runs on, or the recovery route for a bad RBAC config
            // goes back to "delete Config\rbac-users.json on disk".
            var routes = InteractiveAppAdmissionHost.AppRouteTemplates();
            using var client = InteractiveAppAdmissionHost.AnonymousClient(_host.LoopbackBase);
            var refused = new List<string>();

            foreach (var template in routes)
            {
                var url = InteractiveAppAdmissionHost.Concretise(template);
                using var response = await client.GetAsync(url);
                var body = await response.Content.ReadAsStringAsync();

                if (!body.Contains(InteractiveAppAdmissionHost.InteractiveAppMarker, StringComparison.Ordinal))
                    refused.Add($"{template} → {url}  [{(int)response.StatusCode}]");
            }

            Assert.True(refused.Count == 0,
                "Loopback lost access to " + refused.Count + " of " + routes.Count
                + " routes. The bootstrap hatch is broken:\n  " + string.Join("\n  ", refused));
        }

        [Fact]
        public async Task EveryRouteServesTheApplicationToAnAuthenticatedNonLoopbackCaller()
        {
            // The behavioural consequence of this round is that anonymous LAN browsing ends — NOT
            // that LAN browsing ends. A signed-in caller carries on, and everything the lane
            // already built (per-surface IsAuthorized, ShellGate, store-backed revocation) decides
            // what they then see.
            using var client = InteractiveAppAdmissionHost.CookieClient(_host.NonLoopbackBase);

            using (var signIn = await client.GetAsync(InteractiveAppAdmissionHost.TestSignInPath))
                Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

            var routes = InteractiveAppAdmissionHost.AppRouteTemplates();
            var refused = new List<string>();

            foreach (var template in routes)
            {
                var url = InteractiveAppAdmissionHost.Concretise(template);
                using var response = await client.GetAsync(url);
                var body = await response.Content.ReadAsStringAsync();

                if (!body.Contains(InteractiveAppAdmissionHost.InteractiveAppMarker, StringComparison.Ordinal))
                    refused.Add($"{template} → {url}  [{(int)response.StatusCode}]");
            }

            Assert.True(refused.Count == 0,
                "A SIGNED-IN caller from " + _host.NonLoopbackAddress + " was refused " + refused.Count
                + " of " + routes.Count + " routes. That is a lockout, not a boundary:\n  "
                + string.Join("\n  ", refused));
        }

        // ── The circuit ──────────────────────────────────────────────────

        [Fact]
        public async Task TheBlazorCircuitDoesNotNegotiateForAnUnauthenticatedNonLoopbackCaller()
        {
            // Refusing the document alone is not enough: a Blazor Server page becomes interactive
            // over /_blazor AFTER the HTML is delivered, so a hand-written client that skips the
            // page and posts straight to negotiate would still get a circuit — and a circuit is
            // where every control lives.
            using var client = InteractiveAppAdmissionHost.AnonymousClient(_host.NonLoopbackBase);

            using var negotiate = await client.PostAsync("/_blazor/negotiate?negotiateVersion=1",
                new StringContent("", Encoding.UTF8, "text/plain"));
            var negotiateBody = await negotiate.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
            Assert.DoesNotContain(InteractiveAppAdmissionHost.BlazorCircuitMarker, negotiateBody, StringComparison.Ordinal);

            // The socket half. A WebSocket handshake is a GET carrying Connection: Upgrade, so it
            // must be refused with a status rather than redirected to a sign-in page.
            using var upgrade = new HttpRequestMessage(HttpMethod.Get, "/_blazor?id=probe");
            upgrade.Headers.TryAddWithoutValidation("Connection", "Upgrade");
            upgrade.Headers.TryAddWithoutValidation("Upgrade", "websocket");
            upgrade.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
            upgrade.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");

            using var socket = await client.SendAsync(upgrade);
            var socketBody = await socket.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Unauthorized, socket.StatusCode);
            Assert.DoesNotContain(InteractiveAppAdmissionHost.BlazorCircuitMarker, socketBody, StringComparison.Ordinal);
        }

        [Fact]
        public async Task TheBlazorCircuitStillNegotiatesOnLoopbackAndForASignedInCaller()
        {
            // The positive control. Without it, "does not negotiate" could be true because the
            // stand-in never negotiated for anyone.
            using var loopback = InteractiveAppAdmissionHost.AnonymousClient(_host.LoopbackBase);
            using var onLoopback = await loopback.PostAsync("/_blazor/negotiate?negotiateVersion=1",
                new StringContent("", Encoding.UTF8, "text/plain"));
            Assert.Contains(InteractiveAppAdmissionHost.BlazorCircuitMarker,
                await onLoopback.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var lan = InteractiveAppAdmissionHost.CookieClient(_host.NonLoopbackBase);
            using (var signIn = await lan.GetAsync(InteractiveAppAdmissionHost.TestSignInPath))
                Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

            using var signedIn = await lan.PostAsync("/_blazor/negotiate?negotiateVersion=1",
                new StringContent("", Encoding.UTF8, "text/plain"));
            Assert.Contains(InteractiveAppAdmissionHost.BlazorCircuitMarker,
                await signedIn.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // ── Position: ahead of static files, ahead of routing ────────────

        [Fact]
        public async Task StaticAssetsAreRefusedToAnUnauthenticatedNonLoopbackCaller()
        {
            // Proves the gate runs BEFORE UseStaticFiles. If it did not, every stylesheet, script
            // and font of the application would still be handed to an unauthenticated stranger —
            // and, more to the point, so would _framework/blazor.web.js.
            using var lan = InteractiveAppAdmissionHost.AnonymousClient(_host.NonLoopbackBase);
            using var refused = await lan.GetAsync(InteractiveAppAdmissionHost.StaticAssetPath);
            var refusedBody = await refused.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.DoesNotContain(InteractiveAppAdmissionHost.StaticAssetMarker, refusedBody, StringComparison.Ordinal);

            // Positive control: the asset really is there and really is served on loopback.
            using var loopback = InteractiveAppAdmissionHost.AnonymousClient(_host.LoopbackBase);
            using var served = await loopback.GetAsync(InteractiveAppAdmissionHost.StaticAssetPath);
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
            Assert.Contains(InteractiveAppAdmissionHost.StaticAssetMarker,
                await served.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task AnUnroutedPathIsRefusedRatherThanAnswered()
        {
            // Proves the gate runs BEFORE UseRouting: a path with no endpoint and no file behind
            // it comes back 401 from the gate, not 404 from routing. A boundary that sat after
            // routing would have to enumerate what routing can reach — and enumerating is the
            // thing that lost seven rounds in a row.
            using var lan = InteractiveAppAdmissionHost.AnonymousClient(_host.NonLoopbackBase);
            using var response = await lan.GetAsync("/_framework/blazor.web.js");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(InteractiveAppAdmission.RefusalHeaderValue,
                response.Headers.GetValues(InteractiveAppAdmission.RefusalHeader).Single());
        }

        // ── The way back in ──────────────────────────────────────────────

        [Fact]
        public async Task TheSignInPageIsReachableFromANonLoopbackOrigin()
        {
            // The one thing that must NOT be refused. A boundary that also blocks the sign-in page
            // is not a boundary, it is an outage.
            using var lan = InteractiveAppAdmissionHost.AnonymousClient(_host.NonLoopbackBase);
            using var response = await lan.GetAsync(InteractiveAppAdmission.SignInPath);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Sign In", body, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ARefusedPageRequestIsToldToSignIn()
        {
            // "Tells them to sign in rather than looking broken" is the requirement, so it is
            // asserted rather than assumed: a browser navigation gets a redirect to the sign-in
            // page, and that page then explains what happened in words.
            using var lan = InteractiveAppAdmissionHost.AnonymousClient(_host.NonLoopbackBase);

            using var request = new HttpRequestMessage(HttpMethod.Get, "/scheduled-tasks");
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");

            using var response = await lan.SendAsync(request);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

            var location = response.Headers.Location?.ToString() ?? "";
            Assert.StartsWith(InteractiveAppAdmission.SignInPath, location, StringComparison.Ordinal);
            Assert.Contains(InteractiveAppAdmission.SignInRequiredError, location, StringComparison.Ordinal);

            // Follow it by hand and read what the person actually sees.
            using var page = await lan.GetAsync(location);
            var body = await page.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("sign in", body, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ARefusedNonPageRequestGetsAnExplanationRatherThanARedirect()
        {
            // A 302 answering a script fetch, an XHR or a socket handshake is a failure dressed as
            // success, and the real cause then surfaces three layers away as a parse error.
            using var lan = InteractiveAppAdmissionHost.AnonymousClient(_host.NonLoopbackBase);

            using var request = new HttpRequestMessage(HttpMethod.Get, "/servers");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = await lan.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains(InteractiveAppAdmission.SignInPath,
                await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task TheHealthProbeStaysUnauthenticated()
        {
            // Monitoring depends on it, and it discloses nothing about the host (round 5 reduced
            // it to {status:"ok"}). Asserted so the allow-list cannot be narrowed by accident into
            // an outage on somebody's NOC board.
            using var lan = InteractiveAppAdmissionHost.AnonymousClient(_host.NonLoopbackBase);
            using var response = await lan.GetAsync("/_server/health");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // ── The canary: the two shapes that beat round 7 ─────────────────

        [Fact]
        public async Task TheUngatedCanaryControlsNeverReachAnUnauthenticatedNonLoopbackCaller()
        {
            // Components/Shared/BoundaryCanary.razor ships two REAL, DELIBERATELY UNGATED shell
            // controls in the exact two shapes that defeated round 7 — and they are rendered here
            // by the real Blazor HtmlRenderer, so this is the markup itself, not a description of
            // it. The census is still blind to both (see BoundaryCanarySourceTests). They are
            // unreachable anyway, which is the whole claim of this round.
            using var lan = InteractiveAppAdmissionHost.AnonymousClient(_host.NonLoopbackBase);
            using var refused = await lan.GetAsync("/");
            var refusedBody = await refused.Content.ReadAsStringAsync();

            Assert.DoesNotContain(SQLTriage.Components.Shared.BoundaryCanary.DomMarker,
                refusedBody, StringComparison.Ordinal);
            Assert.DoesNotContain("bind-get-set", refusedBody, StringComparison.Ordinal);
            Assert.DoesNotContain("editform-submit", refusedBody, StringComparison.Ordinal);

            // The positive control, and it is load-bearing: without it "absent" could mean the
            // canary was never rendered to anyone and the assertion above is vacuous.
            using var loopback = InteractiveAppAdmissionHost.AnonymousClient(_host.LoopbackBase);
            using var served = await loopback.GetAsync("/");
            var servedBody = await served.Content.ReadAsStringAsync();

            Assert.Contains(SQLTriage.Components.Shared.BoundaryCanary.DomMarker,
                servedBody, StringComparison.Ordinal);
            Assert.Contains("bind-get-set", servedBody, StringComparison.Ordinal);
            Assert.Contains("editform-submit", servedBody, StringComparison.Ordinal);
        }

        // ── The decision table, without a socket ─────────────────────────

        [Theory]
        // loopback wins outright, whatever the path
        [InlineData(true, false, "/scheduled-tasks", true, "Admit")]
        [InlineData(true, false, "/_blazor/negotiate", false, "Admit")]
        // an authenticated remote caller proceeds
        [InlineData(false, true, "/scheduled-tasks", true, "Admit")]
        // the allow-list, for an unauthenticated remote caller
        [InlineData(false, false, "/auth/login", true, "Admit")]
        [InlineData(false, false, "/auth/challenge/windows", true, "Admit")]
        [InlineData(false, false, "/_server/health", false, "Admit")]
        [InlineData(false, false, "/api/v1/servers", false, "Admit")]
        // everything else
        [InlineData(false, false, "/", true, "RedirectToSignIn")]
        [InlineData(false, false, "/scheduled-tasks", true, "RedirectToSignIn")]
        [InlineData(false, false, "/_blazor/negotiate", false, "Refuse")]
        [InlineData(false, false, "/css/app.css", false, "Refuse")]
        // near-misses on the allow-list must NOT inherit it
        [InlineData(false, false, "/authoring", true, "RedirectToSignIn")]
        [InlineData(false, false, "/apiary", true, "RedirectToSignIn")]
        [InlineData(false, false, "/_server/health-detail", true, "RedirectToSignIn")]
        public void TheDecisionTable(bool bootstrapEligible, bool authenticated, string path, bool isPageRequest, string expected)
        {
            var outcome = InteractiveAppAdmission.Evaluate(
                bootstrapEligible, authenticated, new PathString(path), isPageRequest);

            Assert.Equal(expected, outcome.ToString());
        }

        [Fact]
        public void TheAllowListIsExactlyThese()
        {
            // Pinned so widening the only way past the boundary is a visible diff in a test rather
            // than a quiet one in a lambda. Three prefixes, each justified in
            // InteractiveAppAdmission.AlwaysReachablePrefixes; /api is the one that is a judgement
            // rather than a necessity, and it is written down as one.
            Assert.Equal(
                new[] { "/_server/health", "/api", "/auth" },
                InteractiveAppAdmission.AlwaysReachablePrefixes.OrderBy(p => p, StringComparer.Ordinal).ToArray());
        }

        // ── There is no bootstrap grant to outlive the bootstrap ─────────

        /// <summary>
        /// An anonymous non-loopback caller on an ENFORCED install is refused, over a socket.
        ///
        /// <para>This test used to turn <c>AllowRemoteBootstrapAdmin</c> on first, because that
        /// flag was the way an enforced install could still be talked into admitting a stranger:
        /// the gate computed <c>bootstrapEligible = loopback || AllowRemoteBootstrapAdmin</c> with
        /// no expiry term, so the flag reverted a fully configured install to its pre-round-8
        /// posture — measured on this composition as 200, a negotiated circuit, and the
        /// deliberately-ungated <see cref="SQLTriage.Components.Shared.BoundaryCanary"/> shapes
        /// rendered to a stranger. The flag was deleted on 2026-08-03, so the setup is now just an
        /// enforced install; the assertions are unchanged, and they are what round 8 bought.</para>
        ///
        /// <para>Driven over a socket rather than through <see cref="InteractiveAppAdmission.Evaluate"/>
        /// on purpose: <c>Evaluate</c> takes <c>bootstrapEligible</c> as an ARGUMENT, so it was
        /// correct throughout and a unit test of it would have stayed green for the whole life of
        /// the defect. The bug was in what the middleware passed it.</para>
        /// </summary>
        [Fact]
        public async Task AnAnonymousRemoteCallerIsRefusedOnAnEnforcedInstall()
        {
            await using var host = await InteractiveAppAdmissionHost.StartAsync(
                new RbacConfig
                {
                    Enabled = true,
                    Windows = new WindowsAuthConfig { Enabled = true },
                },
                new RbacUser
                {
                    Email = @"ADMISSION\admin",
                    DisplayName = "Enforced Admin",
                    Provider = AuthProviders.Windows,
                    Role = AppRoles.Admin,
                    Enabled = true,
                });

            using var client = InteractiveAppAdmissionHost.AnonymousClient(host.NonLoopbackBase);

            // The fixture must actually enforce, or this proves nothing at all.
            var me = await client.GetStringAsync("/auth/me");
            Assert.Contains("\"enforced\":true", me, StringComparison.Ordinal);

            // …and /auth/me must not report a hatch this caller does not hold.
            Assert.Contains("\"bootstrapEligible\":false", me, StringComparison.Ordinal);

            using var navigation = new HttpRequestMessage(HttpMethod.Get, "/scheduled-tasks");
            navigation.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");

            using var page = await client.SendAsync(navigation);
            Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
            Assert.Equal(
                InteractiveAppAdmission.RefusalHeaderValue,
                page.Headers.GetValues(InteractiveAppAdmission.RefusalHeader).Single());

            var body = await page.Content.ReadAsStringAsync();
            Assert.DoesNotContain(InteractiveAppAdmissionHost.InteractiveAppMarker, body, StringComparison.Ordinal);

            var negotiate = await client.PostAsync("/_blazor/negotiate", new StringContent(""));
            Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
        }

        /// <summary>
        /// The state the deleted flag existed for, driven both ways on ONE host: a completely
        /// UNCONFIGURED install — nothing enforced, no users, defaults everywhere.
        ///
        /// <para>From the network: refused. This is the reversal. The test that stood here asserted
        /// the opposite as a requirement — "the same flag still opens an unconfigured install" —
        /// and was green for four rounds. An unconfigured install used to become an anonymous-admin
        /// install for anyone who could route to the port, once a box was ticked.</para>
        ///
        /// <para>From loopback: admitted, and rendering the app. That half is the positive control
        /// AND the thing this round had to preserve — without it, "refused" would be
        /// indistinguishable from a fixture that admits nobody, and an unconfigured install would
        /// have no way to configure its first administrator at all.</para>
        /// </summary>
        [Fact]
        public async Task AnUnconfiguredInstallIsOpenFromTheBoxAndShutFromTheNetwork()
        {
            await using var host = await InteractiveAppAdmissionHost.StartAsync(new RbacConfig());

            using var remote = InteractiveAppAdmissionHost.AnonymousClient(host.NonLoopbackBase);

            var remoteMe = await remote.GetStringAsync("/auth/me");
            Assert.Contains("\"enforced\":false", remoteMe, StringComparison.Ordinal);
            Assert.Contains("\"bootstrapEligible\":false", remoteMe, StringComparison.Ordinal);

            using var navigation = new HttpRequestMessage(HttpMethod.Get, "/scheduled-tasks");
            navigation.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
            using var remotePage = await remote.SendAsync(navigation);
            Assert.Equal(HttpStatusCode.Redirect, remotePage.StatusCode);
            Assert.DoesNotContain(
                InteractiveAppAdmissionHost.InteractiveAppMarker,
                await remotePage.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);

            using var onBox = InteractiveAppAdmissionHost.AnonymousClient(host.LoopbackBase);

            var boxMe = await onBox.GetStringAsync("/auth/me");
            Assert.Contains("\"bootstrapEligible\":true", boxMe, StringComparison.Ordinal);

            var boxPage = await onBox.GetAsync("/scheduled-tasks");
            Assert.Equal(HttpStatusCode.OK, boxPage.StatusCode);
            Assert.Contains(
                InteractiveAppAdmissionHost.InteractiveAppMarker,
                await boxPage.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// One rule, one register. Three call sites used to spell the eligibility out by hand and
        /// one of them was wrong; they all ask <see cref="RbacService.IsBootstrapEligible(bool)"/>
        /// now, and this pins its shape.
        ///
        /// <para>The theory that stood here had five rows and three inputs, because the flag and
        /// the enforcement state were both terms. It has four rows and one input now, and the
        /// enforcement state is asserted to be IRRELEVANT rather than absent — break-glass has to
        /// survive enforcement, and a remote caller has to be refused whether or not it does.</para>
        /// </summary>
        [Theory]
        [InlineData(true, false, true)]    // loopback, dormant  → yes (the hatch)
        [InlineData(true, true, true)]     // loopback, enforced → yes (break-glass survives)
        [InlineData(false, false, false)]  // remote,   dormant  → no
        [InlineData(false, true, false)]   // remote,   enforced → no
        public void BootstrapEligibilityIsTheSocketAddressAndNothingElse(
            bool loopback, bool enforced, bool expected)
        {
            var temp = Path.Combine(Path.GetTempPath(), "sqltriage-bootstrap-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var config = new RbacConfig();
                var users = new List<RbacUser>();
                if (enforced)
                {
                    config.Enabled = true;
                    config.Windows.Enabled = true;
                    users.Add(new RbacUser
                    {
                        Email = @"ADMISSION\admin",
                        Provider = AuthProviders.Windows,
                        Role = AppRoles.Admin,
                        Enabled = true,
                    });
                }

                var configPath = Path.Combine(temp, "rbac-config.json");
                var usersPath = Path.Combine(temp, "rbac-users.json");
                File.WriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(config));
                File.WriteAllText(usersPath, System.Text.Json.JsonSerializer.Serialize(users));

                var rbac = new RbacService(
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<RbacService>.Instance,
                    configPath, usersPath);

                Assert.Equal(enforced, rbac.IsRbacEnforced());
                Assert.Equal(expected, rbac.IsBootstrapEligible(loopback));
            }
            finally
            {
                try { Directory.Delete(temp, recursive: true); } catch { }
            }
        }

        // ── Nothing may hide behind an admitted prefix ───────────────────

        /// <summary>
        /// The gate admits <c>/auth</c>, <c>/_server/health</c> and <c>/api</c> ahead of
        /// <c>UseStaticFiles</c>, so a real file under one of those paths in the web root would be
        /// the one static asset an unauthenticated LAN caller still receives. Nothing is served
        /// from them in any shipped layout, which is why this has never been an exposure — and
        /// dropping a file into a folder is not a change anybody reviews as a security change,
        /// which is why it is a startup failure rather than a note.
        /// </summary>
        [Fact]
        public void NoStaticAssetHidesBehindAnAdmittedPrefix()
        {
            var webRoot = Path.Combine(RawPassedScan.RepoRoot().FullName, "wwwroot");
            Assert.True(Directory.Exists(webRoot), "wwwroot not found — this guard cannot run and must fail.");

            // The shipped layout is clean, and the guard agrees.
            InteractiveAppAdmission.GuardAdmittedPrefixDirectories(webRoot);

            // …and the guard can fail, which is the half that makes the line above mean something.
            var temp = Path.Combine(Path.GetTempPath(), "sqltriage-prefix-" + Guid.NewGuid().ToString("N"));
            try
            {
                foreach (var prefix in InteractiveAppAdmission.AlwaysReachablePrefixes)
                {
                    var segment = prefix.Trim('/').Split('/')[0];
                    var planted = Path.Combine(temp, segment);
                    Directory.CreateDirectory(planted);
                    File.WriteAllText(Path.Combine(planted, "leak.css"), "/* served to anyone */");

                    var ex = Assert.Throws<InvalidOperationException>(
                        () => InteractiveAppAdmission.GuardAdmittedPrefixDirectories(temp));
                    Assert.Contains(segment, ex.Message, StringComparison.Ordinal);

                    Directory.Delete(planted, recursive: true);
                }
            }
            finally
            {
                try { Directory.Delete(temp, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// …and the guard is WIRED, not merely present.
        ///
        /// <para>The test above calls <see cref="InteractiveAppAdmission.GuardAdmittedPrefixDirectories"/>
        /// directly, so deleting the call from
        /// <see cref="InteractiveAppAdmission.UseSqlTriageFrontDoor"/> left it green — measured, and
        /// exactly the shape of hole this lane keeps finding. This one builds a host through the
        /// SHIPPED composition over a web root that contains <c>auth\</c>, and requires it to refuse
        /// to start.</para>
        /// </summary>
        [Fact]
        public void TheFrontDoorRefusesToBuildOverAWebRootThatHidesAnAdmittedPrefix()
        {
            var temp = Path.Combine(Path.GetTempPath(), "sqltriage-prefix-wired-" + Guid.NewGuid().ToString("N"));
            var webRoot = Path.Combine(temp, "wwwroot");
            Directory.CreateDirectory(Path.Combine(webRoot, "auth"));
            File.WriteAllText(Path.Combine(webRoot, "auth", "leak.css"), "/* served to anyone */");

            try
            {
                var rbac = new RbacService(
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<RbacService>.Instance,
                    Path.Combine(temp, "rbac-config.json"),
                    Path.Combine(temp, "rbac-users.json"));

                var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                {
                    ContentRootPath = AppContext.BaseDirectory,
                    WebRootPath = webRoot,
                });
                builder.Logging.ClearProviders();
                builder.Services.AddSingleton(rbac);
                builder.Services.AddSqlTriageAuth(rbac.Config);

                var app = builder.Build();

                var ex = Assert.Throws<InvalidOperationException>(() => app.UseSqlTriageFrontDoor(rbac));
                Assert.Contains("auth", ex.Message, StringComparison.Ordinal);
                Assert.Contains("without authentication", ex.Message, StringComparison.Ordinal);
            }
            finally
            {
                try { Directory.Delete(temp, recursive: true); } catch { }
            }
        }

        // ── Composition ──────────────────────────────────────────────────

        [Fact]
        public void UseSqlTriageAuthRefusesToBuildWithoutTheBoundary()
        {
            // No third host can ever ship without the gate: every browser-facing host must call
            // UseSqlTriageAuth (it is where the cookie handler and the /auth endpoints come from),
            // and that call now throws when the boundary was never installed. On 2026-08-01 the
            // installed service was found running a pipeline with NO authentication at all,
            // listening on 0.0.0.0, because auth had been landed in one host and not the other.
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();

            var temp = Path.Combine(Path.GetTempPath(), "sqltriage-admission-guard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var rbac = new RbacService(
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<RbacService>.Instance,
                    Path.Combine(temp, "rbac-config.json"),
                    Path.Combine(temp, "rbac-users.json"));

                builder.Services.AddSingleton(rbac);
                builder.Services.AddAntiforgery();
                builder.Services.AddSqlTriageAuth(rbac.Config);

                var app = builder.Build();
                app.UseRouting();   // deliberately WITHOUT UseSqlTriageFrontDoor

                var ex = Assert.Throws<InvalidOperationException>(() => app.UseSqlTriageAuth(rbac));
                Assert.Contains("UseSqlTriageAdmission", ex.Message, StringComparison.Ordinal);
            }
            finally
            {
                try { Directory.Delete(temp, recursive: true); } catch { }
            }
        }

        // ── The recovery screen must stay self-contained ─────────────────

        [Fact]
        public void TheSignInPageNeedsNoStaticAsset()
        {
            // Since the gate refuses static assets to the caller it redirects, the sign-in page is
            // the one page that must carry its own styling. A <link> or <script> added to it would
            // not 404 — it would 401, and the recovery screen would render unstyled to the person
            // who is already locked out. Asserted against the SHIPPED renderer, not a copy.
            var temp = Path.Combine(Path.GetTempPath(), "sqltriage-loginpage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var rbac = new RbacService(
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<RbacService>.Instance,
                    Path.Combine(temp, "rbac-config.json"),
                    Path.Combine(temp, "rbac-users.json"));

                var config = rbac.Config;
                config.Windows.Enabled = true;
                config.LocalPassword.Enabled = true;

                var ctx = new DefaultHttpContext();
                var html = SqlTriageAuth.RenderLoginPage(ctx, rbac, antiforgery: null);

                var offenders = Regex.Matches(html, @"<(link|script|img|iframe|source)\b", RegexOptions.IgnoreCase)
                    .Select(m => m.Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                Assert.True(offenders.Count == 0,
                    "The sign-in page now references external assets (" + string.Join(", ", offenders)
                    + "). An unauthenticated non-loopback caller is refused every static file, so those "
                    + "would 401 and the recovery screen would render broken. Keep it self-contained, or "
                    + "widen InteractiveAppAdmission.AlwaysReachablePrefixes deliberately.");
            }
            finally
            {
                try { Directory.Delete(temp, recursive: true); } catch { }
            }
        }
    }
}
