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
    /// The census, one layer down: every HTTP endpoint, and whether it declares a permission.
    ///
    /// <para><b>Why a second census.</b> <c>RbacPageGateCensusTests</c> enumerates <c>@page</c>
    /// directives. It was added in round 2 to catch "a page with no gate at all", and it did. But
    /// it can only ever see .razor files, and on 2026-08-02 the hole that mattered most was not a
    /// page: <c>POST /api/v1/rbac/users</c> was reachable from 192.168.10.32 with no credential,
    /// accepted an attacker-supplied Argon2id <c>passwordHash</c>, returned 201, and
    /// <c>POST /auth/local</c> then signed that account in as <c>role: admin</c>. A page-only
    /// census is structurally blind to that, exactly as the HasPermission scan was structurally
    /// blind to Onboarding. So: enumerate the ROUTES, and require every mutating one to declare a
    /// permission on its Map call or be named on a reviewed list.</para>
    ///
    /// <para><b>Why source-scanning rather than reflecting over the endpoint table.</b> Building
    /// the real endpoint table needs the app's DI container and a host. That is exactly the
    /// dependency that made the previous scan easy to leave narrow. Reading the source costs
    /// nothing, runs in a unit test, and fails on the shipped text — which is what a reviewer
    /// would read anyway.</para>
    /// </summary>
    public class RbacHandlerGateCensusTests
    {
        private const string ApiFile = "Data/Services/ApiEndpoints.cs";
        private const string AuthFile = "Data/Services/SqlTriageAuth.cs";

        /// <summary>
        /// The permission each API route must declare, and the fail-closed ones. Written out
        /// rather than merely "has some permission" so a later edit that swaps <c>settings</c> for
        /// <c>view_results</c> fails loudly instead of passing the census.
        ///
        /// <para><b>The eleven GET reads are on this table because until 2026-08-03 they were on a
        /// list called <c>ReviewedOpenReads</c> instead</b> — a written judgement that gating them
        /// "would break an existing integration for no gain the API key does not already provide".
        /// The judgement was wrong in its second half. With no ApiKey configured, which is the
        /// DEFAULT, <c>UseApiKeyAuth</c> degrades to a same-origin heuristic that passes every GET,
        /// so those routes answered 200 to an anonymous caller from a non-loopback origin and
        /// disclosed the client server inventory, the check results and the audit log — measured on
        /// the desktop <see cref="SQLTriage.Data.Services.ServerModeService"/> host. An API key was
        /// not "already providing" anything; it was the only thing that would have, and it was
        /// off.</para>
        ///
        /// <para>The permission per read is the one the equivalent UI surface asks for, so a
        /// caller cannot read through the API what the app would refuse to render them:
        /// <c>manage_servers</c> for the server inventory (Pages/Servers.razor gates its whole page
        /// on it), <c>manage_alerts</c> for the alert thresholds (Pages/AlertingConfig.razor),
        /// <c>view_audit_log</c> for the audit log, and the every-role <c>view_dashboard</c> /
        /// <c>view_results</c> for the rest — which deny nobody who is signed in and deny everybody
        /// who is not.</para>
        /// </summary>
        private static readonly (string Route, string Permission, bool FailClosed)[] ExpectedApiGates =
        {
            ("GET /status",                       "view_dashboard",     false),
            ("GET /servers",                      "manage_servers",     false),
            ("GET /servers/{id}",                 "manage_servers",     false),
            ("GET /checks/results/{instanceName}", "view_results",      false),
            ("GET /checks/summary",               "view_results",       false),
            ("GET /checks/summary/{instanceName}", "view_results",      false),
            ("GET /checks",                       "view_results",       false),
            ("GET /checks/enabled",               "view_results",       false),
            ("GET /alerts",                       "view_results",       false),
            ("GET /alerts/thresholds",            "manage_alerts",      false),
            ("GET /audit-log",                    "view_audit_log",     false),
            ("POST /checks/execute/{serverId}",   "execute_checks",     false),
            ("POST /alerts/{id}/acknowledge",     "acknowledge_alerts", false),
            ("POST /alerts/acknowledge-all",      "acknowledge_alerts", false),
            // POST/PUT/DELETE /alerts/thresholds removed 2026-08-26 (DECISIONS 04:20, ruling 3):
            // they wrote a store only the call-site-less EvaluateAlerts read, so a created threshold
            // never fired. The GET read above stays. See AlertThresholdWriteApiRemovedTests.
            ("GET /rbac/users",                   "settings",           true),
            ("POST /rbac/users",                  "settings",           true),
            ("PUT /rbac/users/{id}",              "settings",           true),
            ("DELETE /rbac/users/{id}",           "settings",           true),
        };

        /// <summary>
        /// EVERY endpoint in <c>/api/v1</c> declares a permission — reads included, and with no
        /// reviewed-exception list to add a new one to.
        ///
        /// <para>The exception list is gone on purpose. It is what let eleven reads sit ungated
        /// through three rounds of this lane while the file above them said <c>/api</c> "has its
        /// own boundary", and an escape hatch in a census is an invitation to use it. A route that
        /// genuinely must be anonymous now needs a code change AND a change to this test's shape,
        /// which is a conversation rather than one line appended to an array.</para>
        ///
        /// <para>This is also the test that would have failed on 2026-08-01, when four RBAC
        /// user-management routes were mapped with nothing in front of them but a same-origin
        /// check.</para>
        /// </summary>
        [Fact]
        public void EveryApiEndpointDeclaresAPermission()
        {
            var endpoints = EnumerateApiEndpoints();
            Assert.True(endpoints.Count > 15,
                "The API endpoint scanner found only " + endpoints.Count + " routes in " + ApiFile
                + ". A scanner that stops matching is a scanner that passes over nothing — fix the "
                + "regex rather than trusting the green.");

            var ungated = endpoints.Where(e => e.Permission == null).ToList();

            Assert.True(ungated.Count == 0,
                "These /api/v1 endpoints declare NO permission. /api is exempt from the "
                + "InteractiveAppAdmission front door, so nothing else stands between them and any caller "
                + "who can reach the listener — and with the default empty ApiKey, UseApiKeyAuth passes "
                + "every GET. Add .RequirePermission(\"...\") to the Map call:\n  "
                + string.Join("\n  ", ungated.Select(e => e.Verb + " " + e.Route)));
        }

        /// <summary>
        /// The table above must cover the surface. Without this, adding a route and forgetting to
        /// pin its permission would leave <see cref="EveryApiEndpointDeclaresAPermission"/> green
        /// (it declares SOMETHING) and the exact-permission theory silent (it never names the
        /// route) — a gate on the wrong permission, passing two censuses.
        /// </summary>
        [Fact]
        public void TheExpectedGateTableCoversEveryMappedRoute()
        {
            var mapped = EnumerateApiEndpoints()
                .Select(e => e.Verb + " " + e.Route)
                .ToList();
            var pinned = new HashSet<string>(ExpectedApiGates.Select(g => g.Route), StringComparer.Ordinal);

            var unpinned = mapped.Where(m => !pinned.Contains(m)).ToList();
            Assert.True(unpinned.Count == 0,
                "These /api/v1 routes are mapped but not pinned in ExpectedApiGates, so nothing checks "
                + "WHICH permission they carry:\n  " + string.Join("\n  ", unpinned));

            var stale = pinned.Where(p => !mapped.Contains(p)).ToList();
            Assert.True(stale.Count == 0,
                "These entries in ExpectedApiGates name routes that are no longer mapped:\n  "
                + string.Join("\n  ", stale));
        }

        /// <summary>
        /// The exact permission per route, not merely "has one". A gate on the wrong permission is
        /// a gate that passes a census and denies nobody.
        /// </summary>
        [Theory]
        [MemberData(nameof(ExpectedGates))]
        public void EachGatedApiEndpointDeclaresTheExpectedPermission(string route, string permission, bool failClosed)
        {
            var endpoints = EnumerateApiEndpoints();
            var parts = route.Split(' ', 2);

            var endpoint = endpoints.SingleOrDefault(e =>
                e.Verb.Equals(parts[0], StringComparison.OrdinalIgnoreCase)
                && e.Route.Equals(parts[1], StringComparison.Ordinal));

            Assert.True(endpoint != null, route + " is no longer mapped in " + ApiFile
                                        + ". If it was removed, remove it from ExpectedApiGates too.");
            Assert.Equal(permission, endpoint!.Permission);
            Assert.Equal(failClosed, endpoint.FailClosed);
        }

        public static IEnumerable<object[]> ExpectedGates =>
            ExpectedApiGates.Select(g => new object[] { g.Route, g.Permission, g.FailClosed });

        /// <summary>
        /// The RBAC user-management routes must be FAIL-CLOSED, all four of them.
        ///
        /// <para>Pinned separately from the table above because this is the property that closes
        /// the proven attack, and it is the one a later "make the API easier to use" change would
        /// remove first. Without it, an install with no ApiKey configured — the default — falls
        /// back to <c>UseApiKeyAuth</c>'s same-origin check, which passes any request carrying
        /// neither Origin nor Referer and reads the attacker-supplied Host header for the rest.</para>
        /// </summary>
        [Fact]
        public void TheRbacUserEndpointsAreAllFailClosed()
        {
            var rbacRoutes = EnumerateApiEndpoints()
                .Where(e => e.Route.StartsWith("/rbac/", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.True(rbacRoutes.Count == 4,
                "Expected exactly 4 /rbac/* routes (GET, POST, PUT, DELETE); found "
                + rbacRoutes.Count + ": " + string.Join(", ", rbacRoutes.Select(r => r.Verb + " " + r.Route))
                + ". A new one must be gated the same way before this test is updated.");

            var notFailClosed = rbacRoutes.Where(r => !r.FailClosed).ToList();
            Assert.True(notFailClosed.Count == 0,
                "These user-management routes are not fail-closed, so with no ApiKey configured they fall "
                + "back to the same-origin check that any header-less request passes:\n  "
                + string.Join("\n  ", notFailClosed.Select(r => r.Verb + " " + r.Route)));
        }

        /// <summary>
        /// The <c>/auth/*</c> endpoints are their OWN surface and are deliberately not gated —
        /// /auth/login and /auth/local are how an unauthenticated caller becomes authenticated, so
        /// requiring a permission there would be a lockout, not a gate. Recorded as a census entry
        /// so "the auth endpoints have no permission" is a decision on the record rather than an
        /// omission, and so a NEW /auth endpoint that does something else has to be considered.
        /// </summary>
        [Fact]
        public void TheAuthEndpointSurfaceIsTheOneWeExpect()
        {
            var text = ReadRepoFile(AuthFile);
            var mapped = new Regex(@"app\.Map(Get|Post)\(\s*""(/auth/[^""]+)""", RegexOptions.Compiled)
                .Matches(text)
                .Select(m => m.Groups[1].Value.ToUpperInvariant() + " " + m.Groups[2].Value)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            var expected = new[]
            {
                "GET /auth/challenge/{provider}",
                "GET /auth/challenge/windows",
                "GET /auth/complete",
                "GET /auth/login",
                "GET /auth/logout",
                "GET /auth/me",
                "POST /auth/local",
            };

            Assert.True(
                expected.All(mapped.Contains),
                "An expected /auth endpoint disappeared. Mapped now:\n  " + string.Join("\n  ", mapped));

            var unexpected = mapped.Except(expected, StringComparer.Ordinal).ToList();
            Assert.True(unexpected.Count == 0,
                "New /auth endpoint(s) appeared. /auth is the ONE surface an unauthenticated caller is "
                + "allowed to reach, so anything added here must be checked by hand and then listed:\n  "
                + string.Join("\n  ", unexpected));
        }

        /// <summary>
        /// A caller must never be able to SUPPLY a password hash. Asserted on the shipped source of
        /// both write routes, because this defence is what turns the proven
        /// create-admin-then-sign-in chain from "denied" into "impossible": even a caller holding a
        /// valid API key has no business choosing the Argon2id digest of an account.
        /// </summary>
        [Fact]
        public void BothRbacWriteRoutesRefuseAnInboundPasswordHash()
        {
            var text = ReadRepoFile(ApiFile);

            var post = SliceBetween(text, "api.MapPost(\"/rbac/users\"", "api.MapPut(\"/rbac/users/{id}\"");
            var put = SliceBetween(text, "api.MapPut(\"/rbac/users/{id}\"", "api.MapDelete(\"/rbac/users/{id}\"");

            Assert.Contains("RefuseInboundPasswordHash", post);
            Assert.Contains("RefuseInboundPasswordHash", put);
        }

        // ── Scanner ──────────────────────────────────────────────────────

        internal sealed record ApiEndpoint(string Verb, string Route, string? Permission, bool FailClosed);

        /// <summary>
        /// Every <c>api.Map{Verb}("route", …)</c> in ApiEndpoints.cs, paired with the
        /// <c>.RequirePermission(...)</c> that closes it — if any. The permission is searched for
        /// between this Map call and the next one, which is why <c>RequirePermission</c> is written
        /// on the Map call rather than inside the handler body: a requirement a census cannot see
        /// is a requirement nobody can audit.
        /// </summary>
        internal static List<ApiEndpoint> EnumerateApiEndpoints()
        {
            var text = ReadRepoFile(ApiFile);
            var map = new Regex(@"api\.Map(Get|Post|Put|Delete|Patch)\(\s*""([^""]+)""", RegexOptions.Compiled);
            var require = new Regex(
                @"\.RequirePermission\(\s*""([a-z_]+)""(\s*,\s*failClosedWithoutApiKey\s*:\s*(true|false))?",
                RegexOptions.Compiled);

            var matches = map.Matches(text).Cast<Match>().ToList();
            var results = new List<ApiEndpoint>();

            for (var i = 0; i < matches.Count; i++)
            {
                var start = matches[i].Index;
                var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
                var body = text.Substring(start, end - start);

                var req = require.Match(body);
                results.Add(new ApiEndpoint(
                    matches[i].Groups[1].Value.ToUpperInvariant(),
                    matches[i].Groups[2].Value,
                    req.Success ? req.Groups[1].Value : null,
                    req.Success && req.Groups[3].Value == "true"));
            }

            return results;
        }

        private static string SliceBetween(string text, string from, string to)
        {
            var a = text.IndexOf(from, StringComparison.Ordinal);
            Assert.True(a >= 0, "Could not find '" + from + "' in " + ApiFile + " — the scanner is stale.");
            var b = text.IndexOf(to, a, StringComparison.Ordinal);
            Assert.True(b > a, "Could not find '" + to + "' after '" + from + "' in " + ApiFile + ".");
            return text.Substring(a, b - a);
        }

        private static string ReadRepoFile(string relative)
        {
            var path = Path.Combine(RawPassedScan.RepoRoot().FullName,
                relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), relative + " not found — the census cannot scan and must fail.");
            return File.ReadAllText(path);
        }
    }
}
