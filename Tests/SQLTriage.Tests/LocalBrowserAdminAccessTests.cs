/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    // BM:LocalBrowserAdminAccessTests — the address the app publishes must be one it will serve
    /// <summary>
    /// THE ADDRESS THE APP TELLS ITS OWNER TO OPEN MUST BE AN ADDRESS ITS OWN BOUNDARY ADMITS.
    ///
    /// <para><b>The defect.</b> <see cref="ServerModeService"/> published
    /// <c>http://{Dns.GetHostName()}:{Port}</c> as THE url, and the Server Mode toggle's "Open in
    /// browser" control opened it. A hostname resolves to the box's LAN address, so the browser
    /// arrived on a NON-loopback socket, and <see cref="InteractiveAppAdmission"/> refuses an
    /// unauthenticated non-loopback caller the interactive application outright. On a fresh
    /// install — no RBAC users, no sign-in method configured, which is what an install IS before
    /// anyone has configured it — the sign-in page it redirects to has nothing to offer. The
    /// application was publishing the one address at which it locks its owner out.</para>
    ///
    /// <para><b>Two things this file does NOT claim, both inferences that were written here as
    /// measurements and corrected 2026-08-25.</b> FIRST, it does not claim this is the route by
    /// which any particular operator reached a refused address. The control that opened this URL is
    /// in <c>ServerModeToggle</c>, which renders only while <c>ServerMode.IsRunning</c> — the WPF
    /// desktop's Server Mode. The Windows service host published no URL at all before this branch,
    /// which is what its new <c>[Access]</c> startup line fixes, and <c>http://localhost:5155/settings</c>
    /// on the installed service answered 200 throughout. Which surface a given lockout came through
    /// is UNTESTED. SECOND, it does not claim that reaching Settings is sufficient to create the
    /// first admin: that needs a control on the page, and until 2026-08-25 the only one was gated
    /// behind the RBAC-enabled checkbox that could not be ticked without it (see
    /// <c>RbacBootstrapReachabilityTests</c>). What is measured here is narrower and is the whole
    /// claim: the address the app publishes for local use is one its own boundary admits, and the
    /// address it publishes for other machines is not.</para>
    ///
    /// <para><b>The boundary is not the thing being changed and is not being weakened.</b> Who is
    /// admitted is exactly what it was: loopback, or an authenticated caller, and nothing else.
    /// What changed is that the app stopped advertising an address it will not serve. This test
    /// asserts BOTH halves — the published local address is admitted AND the published LAN address
    /// is still refused — because a "fix" that opened the LAN origin would pass the first half
    /// alone, and that fix would be a remote anonymous admin hatch.</para>
    ///
    /// <para><b>Measured, not reasoned.</b> Both arms are real HTTP over a real socket against the
    /// shipped composition (<c>UseSqlTriageFrontDoor</c> + <c>UseSqlTriageAuth</c>), through
    /// <see cref="InteractiveAppAdmissionHost"/> — the same harness the admission suite uses, over
    /// an UNCONFIGURED install, which is the state the defect lives in. Live confirmation of the
    /// same facts against <c>--server</c> on the maintainer's box, 2026-08-25:
    /// <c>GET http://MSI:5150/</c> answered <c>302 /auth/login?error=signin_required</c> with
    /// <c>X-SQLTriage-Admission: sign-in-required</c>, and <c>GET http://localhost:5150/settings</c>
    /// answered <c>200</c>.</para>
    /// </summary>
    public sealed class LocalBrowserAdminAccessTests : IClassFixture<AdmissionHostFixture>
    {
        private readonly InteractiveAppAdmissionHost _host;

        public LocalBrowserAdminAccessTests(AdmissionHostFixture fixture) => _host = fixture.Host;

        /// <summary>
        /// The host component of what <see cref="ServerModeService.ComposeLocalUrl"/> publishes,
        /// resolved. Every address it resolves to must be loopback, or a browser told to open it
        /// could land on a socket the boundary refuses.
        /// </summary>
        [Fact]
        public void ThePublishedLocalAddressResolvesOnlyToLoopback()
        {
            var localHost = new Uri(ServerModeService.ComposeLocalUrl("http", 5150)).Host;
            var addresses = Dns.GetHostAddresses(localHost);

            Assert.NotEmpty(addresses);
            var nonLoopback = addresses.Where(a => !AppUserState.IsLoopbackAddress(a)).ToList();

            Assert.True(nonLoopback.Count == 0,
                $"ServerModeService publishes '{localHost}' as the address to open on this machine, but it "
                + "resolves to " + string.Join(", ", nonLoopback) + " — not loopback. A browser sent there "
                + "arrives on a socket InteractiveAppAdmission refuses, which is the lockout this "
                + "property exists to prevent.");
        }

        /// <summary>
        /// And the LAN address must NOT resolve to loopback — otherwise the two properties are the
        /// same address wearing two names, the "refused from the LAN" arm below is vacuous, and
        /// this suite would go green on a build where the boundary had been deleted.
        /// </summary>
        [Fact]
        public void ThePublishedLanAddressIsGenuinelyADifferentOrigin()
        {
            var lanHost = new Uri(ServerModeService.ComposeLanUrl("http", Dns.GetHostName(), 5150)).Host;
            var addresses = Dns.GetHostAddresses(lanHost);

            Assert.NotEmpty(addresses);
            Assert.DoesNotContain(addresses, AppUserState.IsLoopbackAddress);
        }

        /// <summary>
        /// The decider for the first half: an unauthenticated caller arriving on the address the
        /// app publishes for local use is served the interactive application on an install with no
        /// RBAC users at all.
        /// </summary>
        [Fact]
        public async Task ThePublishedLocalAddressIsServedTheApplication()
        {
            var localHost = new Uri(ServerModeService.ComposeLocalUrl("http", _host.Port)).Host;

            using var client = InteractiveAppAdmissionHost.AnonymousClient($"http://{localHost}:{_host.Port}");
            using var response = await GetPageAsync(client, "/settings");

            var body = await response.Content.ReadAsStringAsync();

            Assert.True(response.StatusCode == HttpStatusCode.OK,
                $"http://{localHost}:{_host.Port}/settings answered {(int)response.StatusCode}. That is the "
                + "address ServerModeService publishes for use on this machine, and Settings is where the "
                + "first admin is configured.");

            Assert.False(response.Headers.Contains(InteractiveAppAdmission.RefusalHeader),
                "The published local address was stamped with the admission refusal header.");

            Assert.Contains(InteractiveAppAdmissionHost.InteractiveAppMarker, body, StringComparison.Ordinal);
        }

        /// <summary>
        /// The decider for the second half: the address published for OTHER machines is still
        /// refused to an unauthenticated caller. This is the property that must survive the fix,
        /// and it is asserted here rather than only in the admission suite so that a future edit
        /// to <see cref="ServerModeService"/> which "fixes" access by widening the boundary turns
        /// this file red.
        /// </summary>
        [Fact]
        public async Task ThePublishedLanAddressIsStillRefusedWithoutSignIn()
        {
            using var client = InteractiveAppAdmissionHost.AnonymousClient(_host.NonLoopbackBase);
            using var response = await GetPageAsync(client, "/settings");

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal(InteractiveAppAdmission.RefusalHeaderValue,
                response.Headers.GetValues(InteractiveAppAdmission.RefusalHeader).Single());

            var location = response.Headers.Location!.ToString();
            Assert.StartsWith(InteractiveAppAdmission.SignInPath, location, StringComparison.Ordinal);
        }

        /// <summary>
        /// AND THE COMMAND LINE MUST NOT PROMISE WHAT THE BOUNDARY REFUSES. <c>--server</c>'s usage
        /// line read "Headless Kestrel; browse from another machine", and the XML summary above it
        /// read "browse from any machine". Both are the instruction that produces the lockout the
        /// two cells above measure: an unauthenticated caller on a non-loopback socket is refused,
        /// and on a fresh install the sign-in page it lands on has nothing to offer. Graded on the
        /// SHIPPED string, not a copy of it.
        /// </summary>
        [Fact]
        public void TheUsageTextNamesAnAddressTheBoundaryAdmits()
        {
            var usage = SQLTriage.Program.Usage;

            Assert.Contains("--server", usage, StringComparison.Ordinal);   // anti-vacuity
            Assert.DoesNotContain("from another machine", usage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("from any machine", usage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("localhost", usage, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The same claim over the whole file, because the other carrier of it is an XML doc
        /// comment that no runtime string can reach. A lint, and defeatable by rewording — what it
        /// holds is the wording that was actually there.
        /// </summary>
        [Fact]
        public void NoSourceLineInTheEntryPointTellsTheOperatorToBrowseFromAnotherMachine()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Markup", "Program.cs");
            Assert.True(File.Exists(path),
                $"Program.cs was not copied to the test output ({path}); this assertion would silently pass.");

            var source = File.ReadAllText(path);
            Assert.Contains("--server", source, StringComparison.Ordinal);   // anti-vacuity
            Assert.DoesNotContain("from another machine", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("from any machine", source, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A top-level browser navigation: GET asking for <c>text/html</c>, which is the only
        /// shape <see cref="InteractiveAppAdmission.LooksLikePageRequest"/> answers with a
        /// redirect. Composed here rather than defaulted so both arms drive the same request shape
        /// and the difference between them is the ORIGIN and nothing else.
        /// </summary>
        private static Task<HttpResponseMessage> GetPageAsync(HttpClient client, string path)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            return client.SendAsync(request);
        }
    }
}
