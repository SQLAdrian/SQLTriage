/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    // BM:ApiReadAuthorizationTests — the /api GET reads, driven over a real socket
    /// <summary>
    /// The REST reads, driven from the two origin classes over a real listener.
    ///
    /// <para><b>What was open.</b> <c>/api</c> is exempt from
    /// <see cref="InteractiveAppAdmission"/> because <see cref="ApiAuthorization"/> "is its
    /// boundary" — true only of the routes that carried
    /// <see cref="ApiAuthorization.RequirePermission"/>, and the GET reads carried none. With no
    /// <c>ApiKey</c> configured, which is the DEFAULT, <c>UseApiKeyAuth</c> degrades to a
    /// same-origin CSRF heuristic that passes every GET. Measured 2026-08-03 on the desktop's
    /// <see cref="ServerModeService"/> host: 200 to an anonymous caller from a non-loopback origin
    /// on <c>/status</c>, <c>/servers</c>, <c>/servers/{id}</c>, <c>/checks*</c>, <c>/alerts*</c>
    /// and <c>/audit-log</c> — the client server inventory, the check results and the audit log.
    /// Production was unaffected (<see cref="WindowsServiceHost"/> never maps <c>/api</c> at all,
    /// so the installed service 404s), but the desktop share-via-browser host mapped every
    /// one.</para>
    ///
    /// <para><b>Why this drives a socket rather than calling
    /// <see cref="ApiAuthorization.Evaluate"/>.</b> Evaluate was already unit-tested, and it was
    /// already correct: the defect was that the reads never invoked it. Only a request that
    /// travels the real pipeline — <c>UseApiKeyAuth</c>, then routing, then the endpoint filter —
    /// can tell "the rule denies" from "the rule was never consulted". So the host below composes
    /// exactly what <see cref="ServerModeService"/> composes for <c>/api</c>, and every assertion
    /// is a status code off the wire.</para>
    ///
    /// <para><b>The configuration is the WORST one, and it is the DEFAULT one:</b> no
    /// <c>ApiKey</c>, RBAC dormant. Any weaker configuration would be a test that passes because
    /// the operator was careful.</para>
    ///
    /// <para><b>The red half, measured.</b> Remove <c>.RequirePermission(...)</c> from any read in
    /// <c>ApiEndpoints.cs</c> and that route's case in
    /// <see cref="AnAnonymousNonLoopbackCallerIsRefusedEveryRead"/> flips from 401 to 200 while
    /// the census test names the same route. The count is in the commit message.</para>
    /// </summary>
    public sealed class ApiReadAuthorizationTests : IAsyncLifetime
    {
        /// <summary>
        /// Every GET read on the surface, with a concrete url. Not derived from
        /// <c>ApiEndpoints.cs</c> by regex on purpose: the census already scans the source, and a
        /// test whose SUBJECT comes from the same file it is checking cannot notice a route that
        /// was deleted from both.
        /// </summary>
        public static TheoryData<string> Reads => new()
        {
            "/api/v1/status",
            "/api/v1/servers",
            "/api/v1/servers/anything",
            "/api/v1/checks/results/SOME-INSTANCE",
            "/api/v1/checks/summary",
            "/api/v1/checks/summary/SOME-INSTANCE",
            "/api/v1/checks",
            "/api/v1/checks/enabled",
            "/api/v1/alerts",
            "/api/v1/alerts/thresholds",
            "/api/v1/audit-log",
            "/api/v1/rbac/users",
        };

        private ApiSurfaceHost _host = null!;

        public async Task InitializeAsync() => _host = await ApiSurfaceHost.StartAsync();

        public async Task DisposeAsync()
        {
            if (_host != null) await _host.DisposeAsync();
        }

        // ── The property ─────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(Reads))]
        public async Task AnAnonymousNonLoopbackCallerIsRefusedEveryRead(string path)
        {
            using var client = ApiSurfaceHost.Client(_host.NonLoopbackBase);

            var response = await client.GetAsync(path);

            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized,
                $"GET {path} answered {(int)response.StatusCode} to an anonymous caller from "
                + $"{_host.NonLoopbackAddress}. /api is exempt from the interactive-application boundary "
                + "and no ApiKey is configured — which is the default — so a permission on the route is "
                + "the only thing between this read and the network.");
        }

        /// <summary>
        /// The refusal has to be the AUTHORIZATION one, not an incidental 401 from somewhere else.
        /// A test that accepts any 401 would pass just as happily against a route that 401s
        /// because it crashed.
        /// </summary>
        [Theory]
        [MemberData(nameof(Reads))]
        public async Task TheRefusalNamesThePermissionItRequired(string path)
        {
            using var client = ApiSurfaceHost.Client(_host.NonLoopbackBase);

            var body = await (await client.GetAsync(path)).Content.ReadAsStringAsync();

            Assert.Contains("Authentication required", body, StringComparison.Ordinal);
            Assert.Contains("\"permission\"", body, StringComparison.Ordinal);
        }

        // ── The three ways in that must still work ───────────────────────

        /// <summary>
        /// The machine credential. This is the contract the API exists for, and the reason the
        /// gate is a permission filter rather than a blanket refusal of the prefix.
        /// </summary>
        [Theory]
        [MemberData(nameof(Reads))]
        public async Task AValidApiKeyStillReadsEverything(string path)
        {
            await using var keyed = await ApiSurfaceHost.StartAsync(apiKey: ApiSurfaceHost.TestApiKey);
            using var client = ApiSurfaceHost.Client(keyed.NonLoopbackBase);
            client.DefaultRequestHeaders.Add("X-API-Key", ApiSurfaceHost.TestApiKey);

            await AssertReachedTheHandler(client, path, "a caller holding the configured API key");
        }

        /// <summary>
        /// A WRONG key is refused by <c>UseApiKeyAuth</c> before the filter is reached, so the
        /// "with a key it works" case above cannot be passing because the key was ignored.
        /// </summary>
        [Fact]
        public async Task AWrongApiKeyIsRefused()
        {
            await using var keyed = await ApiSurfaceHost.StartAsync(apiKey: ApiSurfaceHost.TestApiKey);
            using var client = ApiSurfaceHost.Client(keyed.NonLoopbackBase);
            client.DefaultRequestHeaders.Add("X-API-Key", "not-the-key");

            var response = await client.GetAsync("/api/v1/status");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("Invalid or missing API key", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        /// <summary>
        /// Loopback on an install with RBAC dormant keeps the bootstrap hatch — the same scope the
        /// pages use, so an unconfigured install stays usable from the box it runs on. The
        /// fail-closed <c>/rbac/users</c> route is the one exception, and it is asserted as such
        /// rather than skipped.
        /// </summary>
        [Theory]
        [MemberData(nameof(Reads))]
        public async Task LoopbackOnADormantInstallStillReads(string path)
        {
            using var client = ApiSurfaceHost.Client(_host.LoopbackBase);

            if (path == "/api/v1/rbac/users")
            {
                var refused = await client.GetAsync(path);
                Assert.True(refused.StatusCode == HttpStatusCode.Unauthorized,
                    "GET /rbac/users is fail-closed: with no ApiKey there is no path in, not even from "
                    + "loopback. It returns the roster of every principal who can sign in.");
                return;
            }

            await AssertReachedTheHandler(client, path, "a loopback caller on an install with RBAC dormant");
        }

        /// <summary>
        /// A signed-in principal gets what their STORED role holds, and no more. Driven for the two
        /// ends of the matrix on one route each; which permission each route carries is pinned by
        /// <c>RbacHandlerGateCensusTests</c>, and re-driving all eleven here would exercise the same
        /// filter eleven times.
        /// </summary>
        [Fact]
        public async Task ASignedInViewerReadsResultsAndIsRefusedTheServerInventory()
        {
            await using var enforced = await ApiSurfaceHost.StartAsync(
                config: EnforcingConfig(),
                users: new[] { WindowsAdmin(), WindowsViewer() });

            using var client = ApiSurfaceHost.CookieClient(enforced.NonLoopbackBase);
            await enforced.SignInAsync(client, AppRoles.Viewer);

            // view_results is held by every role.
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/checks/summary")).StatusCode);

            // manage_servers is admin-only, and Pages/Servers.razor gates its whole page on it.
            var servers = await client.GetAsync("/api/v1/servers");
            Assert.True(servers.StatusCode == HttpStatusCode.Forbidden,
                $"A signed-in viewer got {(int)servers.StatusCode} from /api/v1/servers. The API must not "
                + "disclose what the equivalent page would refuse to render.");
        }

        [Fact]
        public async Task ASignedInAdminReadsTheServerInventory()
        {
            await using var enforced = await ApiSurfaceHost.StartAsync(
                config: EnforcingConfig(),
                users: new[] { WindowsAdmin(), WindowsViewer() });

            using var client = ApiSurfaceHost.CookieClient(enforced.NonLoopbackBase);
            await enforced.SignInAsync(client, AppRoles.Admin);

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/servers")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/audit-log")).StatusCode);
        }

        /// <summary>
        /// "Reached the handler" rather than "returned 200": three of these routes take an id and
        /// answer 404 for one that does not exist in an empty store, and a 404 FROM THE HANDLER is
        /// proof the caller got past the gate. What must never appear is 401 or 403 — the two
        /// statuses <see cref="ApiAuthorization"/> produces.
        /// </summary>
        private static async Task AssertReachedTheHandler(HttpClient client, string path, string who)
        {
            var response = await client.GetAsync(path);

            Assert.True(
                response.StatusCode != HttpStatusCode.Unauthorized
                && response.StatusCode != HttpStatusCode.Forbidden,
                $"GET {path} answered {(int)response.StatusCode} to {who}. Gating the reads must not break "
                + "the ways in that are supposed to work.");

            Assert.True(
                response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NotFound,
                $"GET {path} answered {(int)response.StatusCode} to {who} — neither an answer nor a "
                + "not-found. A 500 here means the handler ran and threw, which would make every "
                + "'allowed' assertion in this class unfalsifiable.");
        }

        // ── Production is a different shape, and stays that way ──────────

        /// <summary>
        /// The installed headless service never maps <c>/api</c> at all, which is why this defect
        /// never reached production. Asserted on the shipped source so a later "the service should
        /// have the API too" change has to face this note first — mapping it there would put the
        /// REST surface on a 0.0.0.0 listener holding connections to client production servers.
        /// </summary>
        [Fact]
        public void TheHeadlessServiceStillDoesNotMapTheApi()
        {
            var text = File.ReadAllText(Path.Combine(RawPassedScan.RepoRoot().FullName,
                Path.Combine("Data", "Services", "WindowsServiceHost.cs")));

            var codeLines = text.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal));

            Assert.DoesNotContain("MapApiEndpoints", string.Join("\n", codeLines), StringComparison.Ordinal);
        }

        // ── Fixtures ─────────────────────────────────────────────────────

        private static RbacConfig EnforcingConfig() => new()
        {
            Enabled = true,
            Windows = new WindowsAuthConfig { Enabled = true },
        };

        private static RbacUser WindowsAdmin() => new()
        {
            Email = ApiSurfaceHost.AdminPrincipal,
            DisplayName = "API Admin",
            Provider = AuthProviders.Windows,
            Role = AppRoles.Admin,
            Enabled = true,
        };

        private static RbacUser WindowsViewer() => new()
        {
            Email = ApiSurfaceHost.ViewerPrincipal,
            DisplayName = "API Viewer",
            Provider = AuthProviders.Windows,
            Role = AppRoles.Viewer,
            Enabled = true,
        };
    }
}
