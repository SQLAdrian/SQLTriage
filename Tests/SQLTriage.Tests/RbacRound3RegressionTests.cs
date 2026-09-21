/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Round 3 of the server-mode RBAC lane: the twelve pages and the one API surface that still
    /// let an unauthenticated LAN caller mutate a production host after rounds 1 and 2.
    ///
    /// <para>The cold gate proved the page half from a real browser at
    /// <c>http://192.168.10.32:5170/scheduled-tasks</c> with NO credentials, in both dormant and
    /// enforced mode: user badge <c>viewer</c>, a button reading <c>Stop</c>, one <c>.click()</c>,
    /// and the host logged <c>[INF] Scheduled task engine stopped</c>. The same surface answered on
    /// the LIVE production service at <c>127.0.0.1:5155</c> — 29,194 bytes, zero denial markers,
    /// showing "Daily portal check-in", Stop, Run Now ×2 and Delete — and on that host the engine is
    /// what fires the 02:00 unattended assessment and the portal publish.</para>
    ///
    /// <para>The API half was proved from 192.168.10.32 against a faithful replica of the
    /// ServerModeService pipeline: <c>POST /api/v1/rbac/users</c> carrying an attacker-supplied
    /// Argon2id <c>passwordHash</c> → 201, then <c>GET /auth/login</c> for an antiforgery token,
    /// <c>POST /auth/local</c> → 302 with a session cookie, <c>/auth/me</c> →
    /// <c>authenticated:true, role:admin, accountStatus:active</c>.</para>
    ///
    /// <para><b>What these tests are and are not.</b> The page half is asserted in two pieces that
    /// compose: (a) the shipped page calls a named gate with a named permission — asserted against
    /// the real .razor text, because the gate only exists in markup and a comment claiming it is
    /// not evidence; and (b) that gate, with that permission, denies an unauthenticated
    /// non-loopback circuit in BOTH dormant and enforced mode. Neither half alone is the claim;
    /// together they are. The API half is asserted directly against
    /// <see cref="ApiAuthorization.Evaluate"/> driven by a <see cref="DefaultHttpContext"/>, which
    /// is the same decision the shipped endpoint filter makes, with the same inputs.</para>
    /// </summary>
    public class RbacRound3RegressionTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _configPath;
        private readonly string _usersPath;

        public RbacRound3RegressionTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "rbac-round3-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _configPath = Path.Combine(_tempDir, "rbac-config.json");
            _usersPath = Path.Combine(_tempDir, "rbac-users.json");
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup; ignore */ }
        }

        private RbacService NewRbac(RbacConfig? config = null, params RbacUser[] users)
        {
            if (config != null) File.WriteAllText(_configPath, JsonSerializer.Serialize(config));
            if (users.Length > 0) File.WriteAllText(_usersPath, JsonSerializer.Serialize(users.ToList()));
            return new RbacService(NullLogger<RbacService>.Instance, _configPath, _usersPath);
        }

        /// <summary>An enforced-RBAC config with a usable local-password admin (so L1 holds).</summary>
        private RbacService EnforcedRbac()
        {
            var config = new RbacConfig { Enabled = true };
            config.LocalPassword.Enabled = true;
            var admin = new RbacUser
            {
                Email = "admin@example.com",
                DisplayName = "admin",
                Provider = AuthProviders.Local,
                Role = AppRoles.Admin,
                Enabled = true,
                PasswordHash = RbacService.HashPassword("correct horse battery staple"),
            };
            return NewRbac(config, admin);
        }

        /// <summary>A circuit for an unauthenticated request arriving from the LAN address the gate used.</summary>
        private AppUserState LanCircuit(RbacService rbac)
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("192.168.10.32");
            // The Host header is attacker-controlled and must not move the answer.
            ctx.Request.Host = new HostString("localhost", 5170);

            var services = new ServiceCollection();
            services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = ctx });

            return new AppUserState(
                HostEnvironmentInfo.BrowserHosted, rbac,
                services.BuildServiceProvider(), NullLogger<AppUserState>.Instance);
        }

        // ══ 1 — the thirteen pages ═════════════════════════════════════════════════════════

        /// <summary>
        /// Route → file → the permission that page's gate must name.
        ///
        /// <para>Each choice is argued in a comment at the gate in the page itself; the one-liners
        /// here are the summary. The rule applied throughout: pick what the page DOES, not what its
        /// name suggests. Acting on a connected server is <c>run_scripts</c>; authoring standing
        /// configuration that runs later, unattended, under the service identity is <c>settings</c>;
        /// per-server administration is <c>manage_servers</c>; producing results is
        /// <c>execute_checks</c>.</para>
        /// </summary>
        public static TheoryData<string, string, string> GatedPages => new()
        {
            // route                  file                                    permission
            { "/agent-job-guard",     "Pages/AgentJobGuard.razor",            "run_scripts" },     // RemediationRunner.ApplyAsync on a connected server
            { "/agent-job-sync",      "Pages/AgentJobSync.razor",             "run_scripts" },     // creates/alters/deletes Agent jobs on the target replica
            { "/build-profile",       "Pages/BuildProfile.razor",             "settings" },        // writes buildprofile.json — what ships
            { "/editauditscripts",    "Pages/EditAuditScripts.razor",         "settings" },        // authors the T-SQL the scheduler later runs unattended
            { "/fullaudit",           "Pages/FullAudit.razor",                "run_scripts" },     // runs the diagnostic scripts against every instance
            { "/import-results",      "Pages/ImportResults.razor",            "execute_checks" },  // lands a run into the store checks write to
            { "/installation-helper", "Pages/InstallationHelper.razor",       "manage_servers" },  // v2 stub; today it discloses the server inventory
            { "/portal-publish",      "Pages/Portal/PublishToPortal.razor",   "settings" },        // uploads estate data off this machine
            { "/remediation",         "Pages/Remediation.razor",              "run_scripts" },     // ApplyAsync against production
            { "/remediation-lab",     "Pages/RemediationLab.razor",           "run_scripts" },     // ApplyAsync against a real server
            { "/remediation-tuner",   "Pages/RemediationTuner.razor",         "settings" },        // persists the weights that price every remediation
            { "/scheduled-tasks",     "Pages/ScheduledTasks.razor",           "settings" },        // engine start/stop + unattended task authoring + Ola deploy
            { "/servers/baseline",    "Pages/BaselineProgress.razor",         "manage_servers" },  // moves the compliance baseline
        };

        /// <summary>
        /// The gate exists in the SHIPPED markup (or code-behind) and names the expected
        /// permission. Asserted on the real file: a gate is markup, and a claim about markup that
        /// is only made in a comment is not exercised.
        /// </summary>
        [Theory]
        [MemberData(nameof(GatedPages))]
        public void EachPageDeclaresItsGate(string route, string file, string permission)
        {
            var text = ReadPageAndCodeBehind(file);

            Assert.True(
                text.Contains("UserState.IsAuthorized(\"" + permission + "\")", StringComparison.Ordinal),
                route + " (" + file + ") must gate on UserState.IsAuthorized(\"" + permission + "\").");

            Assert.False(
                text.Contains("IsAuthorizedWithBreakGlass(\"", StringComparison.Ordinal),
                route + " (" + file + ") must NOT use IsAuthorizedWithBreakGlass. Plain IsAuthorized "
                + "already grants the loopback BOOTSTRAP hatch on an unconfigured install; break-glass "
                + "additionally opens the surface on loopback when RBAC is ENFORCED, and round 1 scoped "
                + "that to Settings and Onboarding only.");
        }

        /// <summary>
        /// Every mutating handler carries the gate too, not only the render.
        ///
        /// <para>Round 2's onboarding fix did both and said why: an unauthorized circuit never
        /// renders a button and so never registers a handler for one, but a gate that exists only
        /// in markup is one refactor away from being no gate at all. Asserted structurally — the
        /// file must hold a <c>May…</c> gate property AND guard its handlers with it.</para>
        /// </summary>
        [Theory]
        [MemberData(nameof(GatedPages))]
        public void EachPageGuardsItsHandlersToo(string route, string file, string permission)
        {
            var text = ReadPageAndCodeBehind(file);
            _ = permission;

            var declarations = System.Text.RegularExpressions.Regex.Matches(
                text, @"private bool (May[A-Za-z]+)\s*=>\s*UserState\.IsAuthorized\(");
            Assert.True(declarations.Count == 1,
                route + " (" + file + ") must declare exactly one `private bool MayXxx => "
                + "UserState.IsAuthorized(...)` gate property; found " + declarations.Count + ".");

            var gateName = declarations[0].Groups[1].Value;
            var guards = System.Text.RegularExpressions.Regex.Matches(text, @"if \(!" + gateName + @"\) return;").Count;
            Assert.True(guards >= 1,
                route + " (" + file + ") declares " + gateName + " but no handler guards on it. "
                + "The markup `return` is not enough — guard the mutating methods.");
        }

        /// <summary>
        /// The gate itself, exercised. An unauthenticated circuit from 192.168.10.32 is denied
        /// every permission these pages use, with RBAC DORMANT.
        ///
        /// <para>Dormant is the mode that mattered: the bootstrap hatch makes an unconfigured
        /// install behave as admin, and round 1 scoped that to loopback. This is the assertion that
        /// the scoping actually reaches the permissions round 3 gated on.</para>
        /// </summary>
        [Theory]
        [MemberData(nameof(GatedPermissions))]
        public async Task DormantRbac_LanCircuitIsDenied(string permission)
        {
            var state = LanCircuit(NewRbac(new RbacConfig()));
            await state.InitAsync();

            Assert.False(state.IsLoopback);
            Assert.False(state.IsBootstrapEligible);
            Assert.False(state.IsAuthorized(permission));
        }

        /// <summary>Same circuit, RBAC ENFORCED. Denied for the plain permission-matrix reason.</summary>
        [Theory]
        [MemberData(nameof(GatedPermissions))]
        public async Task EnforcedRbac_LanCircuitIsDenied(string permission)
        {
            var rbac = EnforcedRbac();
            Assert.True(rbac.IsRbacEnforced(), "The fixture must actually enforce, or this proves nothing.");

            var state = LanCircuit(rbac);
            await state.InitAsync();

            Assert.False(state.IsAuthorized(permission));
        }

        public static TheoryData<string> GatedPermissions => new()
        {
            "run_scripts", "settings", "manage_servers", "execute_checks",
        };

        /// <summary>
        /// The break-glass half, kept honest: the SAME permissions still open on loopback with RBAC
        /// dormant. A round that only proved denial could have been passed by nailing every door
        /// shut, which would have locked Adrian out of his own unconfigured install.
        /// </summary>
        [Theory]
        [MemberData(nameof(GatedPermissions))]
        public async Task DormantRbac_LoopbackStillReachesTheGatedPages(string permission)
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:127.0.0.1");
            var services = new ServiceCollection();
            services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = ctx });

            var state = new AppUserState(
                HostEnvironmentInfo.BrowserHosted, NewRbac(new RbacConfig()),
                services.BuildServiceProvider(), NullLogger<AppUserState>.Instance);
            await state.InitAsync();

            Assert.True(state.IsAuthorized(permission));
        }

        /// <summary>
        /// BREAK-GLASS DOES NOT SPREAD. <see cref="AppUserState.IsAuthorizedWithBreakGlass"/> opens
        /// a surface on loopback even when RBAC is ENFORCED. Round 1 scoped it, in writing, to "the
        /// two surfaces that can undo a bad RBAC configuration and nothing else" — Settings and
        /// Onboarding — precisely because the <c>settings</c> permission also rides
        /// /service-management and /audit-log, and "a distinct, greppable call site is the only way
        /// to be sure it cannot leak".
        ///
        /// <para>Round 3 gated thirteen more pages and was asked to use the break-glass shape. It
        /// does not, and this test is why: plain <c>IsAuthorized</c> already grants the loopback
        /// BOOTSTRAP hatch on an unconfigured install, which is the property that had to survive.
        /// Break-glass would have gone further and handed a CONFIGURED client install its full
        /// write surface — remediation against production, the scheduler, the portal publish — to
        /// any unauthenticated caller who reaches loopback, and would have made /remediation more
        /// permissive than /query. This test makes the scope greppable, as round 1 intended.</para>
        /// </summary>
        [Fact]
        public void BreakGlassStaysOnTheTwoRecoverySurfaces()
        {
            var root = RawPassedScan.RepoRoot();
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // The two recovery surfaces themselves...
                "Pages/Settings.razor",
                "Pages/Onboarding.razor",
                // ...and the chrome that has to render a way TO them on a broken install.
                "Components/Layout/MainLayout.razor",
                "Components/Layout/NavMenu.razor",
            };

            var offenders = new List<string>();
            foreach (var dir in new[] { "Pages", "Components" })
            {
                var full = new DirectoryInfo(Path.Combine(root.FullName, dir));
                if (!full.Exists) continue;

                foreach (var file in full.EnumerateFiles("*.razor*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');
                    var bare = rel.EndsWith(".cs", StringComparison.Ordinal) ? rel[..^3] : rel;
                    if (allowed.Contains(bare)) continue;

                    var text = File.ReadAllText(file.FullName);
                    // Only a CALL counts; the prose in a gate comment explaining why break-glass was
                    // NOT used must not trip this.
                    if (text.Contains("IsAuthorizedWithBreakGlass(\"", StringComparison.Ordinal))
                        offenders.Add(rel);
                }
            }

            Assert.True(offenders.Count == 0,
                "IsAuthorizedWithBreakGlass has spread beyond the recovery surfaces. It opens a page on "
                + "LOOPBACK even under ENFORCED RBAC — use plain IsAuthorized, which already carries the "
                + "unconfigured-install bootstrap hatch:" + Environment.NewLine + "  "
                + string.Join(Environment.NewLine + "  ", offenders));
        }

        // ══ 1b — change-control attribution ════════════════════════════════════════════════

        /// <summary>
        /// <c>AgentJobGuard</c> hardcoded <c>approved: true</c> and stamped the approver from
        /// <c>Environment.UserName</c> — the PROCESS identity, which on the installed service is
        /// the virtual account <c>NT SERVICE\SQLTriage</c> and never the person who clicked. The
        /// audit ledger therefore named the wrong principal on every browser-made apply.
        /// </summary>
        [Fact]
        public void NoApplySiteStampsTheProcessIdentityAsTheApprover()
        {
            var offenders = new List<string>();
            foreach (var file in new[]
                     {
                         "Pages/AgentJobGuard.razor", "Pages/AgentJobSync.razor",
                         "Pages/Remediation.razor", "Pages/RemediationLab.razor",
                     })
            {
                var text = ReadPageAndCodeBehind(file);
                if (text.Contains("approvedBy: Environment.UserName", StringComparison.Ordinal)
                    || text.Contains("approvedBy: System.Environment.UserName", StringComparison.Ordinal))
                    offenders.Add(file);
            }

            Assert.True(offenders.Count == 0,
                "These pages stamp the change-control approver from the PROCESS identity. On the "
                + "installed service that is NT SERVICE\\SQLTriage, not the caller:\n  "
                + string.Join("\n  ", offenders));
        }

        /// <summary>
        /// And no apply site claims approval unconditionally. <c>approved:</c> must be derived from
        /// whether an approver could be named — the runner's gate 4 then refuses with "Remediation
        /// requires explicit human approval" rather than applying under a fabricated one.
        /// </summary>
        [Fact]
        public void NoApplySiteHardcodesApprovedTrue()
        {
            var offenders = new List<string>();
            foreach (var file in new[]
                     {
                         "Pages/AgentJobGuard.razor", "Pages/AgentJobSync.razor",
                         "Pages/Remediation.razor", "Pages/RemediationLab.razor",
                     })
            {
                if (ReadPageAndCodeBehind(file).Contains("approved: true", StringComparison.Ordinal))
                    offenders.Add(file);
            }

            Assert.True(offenders.Count == 0,
                "These pages pass approved: true unconditionally. If no human can be NAMED, the "
                + "apply must be refused, not recorded as approved by nobody:\n  "
                + string.Join("\n  ", offenders));
        }

        /// <summary>
        /// The property behind that. On the WPF desktop the process owner really is the person at
        /// the keyboard, so it stays <c>Environment.UserName</c>; a browser-hosted circuit with no
        /// authenticated principal can be AUTHORISED (loopback bootstrap) yet cannot be NAMED, and
        /// must resolve to null rather than to a plausible-looking placeholder.
        /// </summary>
        [Fact]
        public async Task ApprovingPrincipal_IsNullForAnUnauthenticatedBrowserCircuit()
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:127.0.0.1");
            var services = new ServiceCollection();
            services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = ctx });

            var state = new AppUserState(
                HostEnvironmentInfo.BrowserHosted, NewRbac(new RbacConfig()),
                services.BuildServiceProvider(), NullLogger<AppUserState>.Instance);
            await state.InitAsync();

            // Authorised by break-glass...
            Assert.True(state.IsAuthorized("run_scripts"));
            // ...and still not an approver, because nobody signed in.
            Assert.Null(state.ApprovingPrincipal);
        }

        [Fact]
        public async Task ApprovingPrincipal_IsTheProcessOwnerOnTheDesktop()
        {
            var state = new AppUserState(
                HostEnvironmentInfo.Desktop, NewRbac(new RbacConfig()),
                new ServiceCollection().BuildServiceProvider(), NullLogger<AppUserState>.Instance);
            await state.InitAsync();

            Assert.Equal(Environment.UserName, state.ApprovingPrincipal);
        }

        // ══ 2 — /api/v1/rbac/users ═════════════════════════════════════════════════════════

        private static DefaultHttpContext ApiRequest(string remoteIp, bool apiKeyValidated = false)
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
            ctx.Request.Method = "POST";
            ctx.Request.Path = "/api/v1/rbac/users";
            // Exactly what the proven attack sent: no Origin, no Referer, and a Host header that
            // says localhost. IsLocalOrSameOriginRequest passes all three.
            ctx.Request.Host = new HostString("localhost", 5170);
            if (apiKeyValidated) ctx.Items[ApiAuthorization.ApiKeyValidatedItem] = true;
            return ctx;
        }

        /// <summary>
        /// THE defect. An unauthenticated caller from the LAN, sending exactly what the proof of
        /// concept sent, is refused — with RBAC dormant, which is the default an unconfigured
        /// install ships in.
        /// </summary>
        [Fact]
        public void PostRbacUsers_FromTheLan_Unauthenticated_IsRefused()
        {
            var outcome = ApiAuthorization.Evaluate(
                ApiRequest("192.168.10.32"), NewRbac(new RbacConfig()),
                "settings", failClosedWithoutApiKey: true);

            Assert.Equal(ApiAuthorization.ApiAuthOutcome.Unauthenticated, outcome);
        }

        /// <summary>
        /// And from LOOPBACK too, because these routes are fail-closed. This is the one place the
        /// bootstrap hatch is deliberately NOT honoured: minting an admin over HTTP with no
        /// credential is the hole, and the loopback recovery route is /onboarding in a browser.
        /// </summary>
        [Fact]
        public void PostRbacUsers_FromLoopback_Unauthenticated_IsAlsoRefused()
        {
            var outcome = ApiAuthorization.Evaluate(
                ApiRequest("127.0.0.1"), NewRbac(new RbacConfig()),
                "settings", failClosedWithoutApiKey: true);

            Assert.Equal(ApiAuthorization.ApiAuthOutcome.Unauthenticated, outcome);
        }

        /// <summary>Enforced mode, same request, same answer.</summary>
        [Fact]
        public void PostRbacUsers_UnderEnforcedRbac_Unauthenticated_IsRefused()
        {
            var rbac = EnforcedRbac();
            Assert.True(rbac.IsRbacEnforced());

            var outcome = ApiAuthorization.Evaluate(
                ApiRequest("192.168.10.32"), rbac, "settings", failClosedWithoutApiKey: true);

            Assert.Equal(ApiAuthorization.ApiAuthOutcome.Unauthenticated, outcome);
        }

        /// <summary>A signed-in VIEWER is forbidden, not merely unauthenticated — 403, not 401.</summary>
        [Fact]
        public void PostRbacUsers_AsAViewer_IsForbidden()
        {
            var config = new RbacConfig { Enabled = true };
            config.LocalPassword.Enabled = true;
            var rbac = NewRbac(config,
                new RbacUser { Email = "a@example.com", Provider = AuthProviders.Local, Role = AppRoles.Admin, Enabled = true, PasswordHash = RbacService.HashPassword("correct horse battery staple") },
                new RbacUser { Email = "v@example.com", Provider = AuthProviders.Local, Role = AppRoles.Viewer, Enabled = true });

            var ctx = ApiRequest("192.168.10.32");
            ctx.User = Principal(AuthProviders.Local, "v@example.com");

            Assert.Equal(ApiAuthorization.ApiAuthOutcome.Forbidden,
                ApiAuthorization.Evaluate(ctx, rbac, "settings", failClosedWithoutApiKey: true));
        }

        /// <summary>A signed-in ADMIN gets through — the gate must not be a wall.</summary>
        [Fact]
        public void PostRbacUsers_AsAnAdmin_IsAllowed()
        {
            var rbac = EnforcedRbac();
            var ctx = ApiRequest("192.168.10.32");
            ctx.User = Principal(AuthProviders.Local, "admin@example.com");

            Assert.Equal(ApiAuthorization.ApiAuthOutcome.Allowed,
                ApiAuthorization.Evaluate(ctx, rbac, "settings", failClosedWithoutApiKey: true));
        }

        /// <summary>The RMM machine credential still works — that is what the API is for.</summary>
        [Fact]
        public void AValidApiKeySatisfiesEvenAFailClosedEndpoint()
        {
            Assert.Equal(ApiAuthorization.ApiAuthOutcome.Allowed,
                ApiAuthorization.Evaluate(
                    ApiRequest("192.168.10.32", apiKeyValidated: true),
                    NewRbac(new RbacConfig()), "settings", failClosedWithoutApiKey: true));
        }

        /// <summary>
        /// A DELETED admin's cookie does not survive. Role comes from the store, so a session
        /// minted before the account was removed cannot keep managing users.
        /// </summary>
        [Fact]
        public void ARevokedAdminsCookieCannotManageUsers()
        {
            var rbac = EnforcedRbac();
            var ctx = ApiRequest("192.168.10.32");
            ctx.User = Principal(AuthProviders.Local, "admin@example.com");
            Assert.Equal(ApiAuthorization.ApiAuthOutcome.Allowed,
                ApiAuthorization.Evaluate(ctx, rbac, "settings", failClosedWithoutApiKey: true));

            var admin = rbac.GetUsers().Single(u => u.Email == "admin@example.com");
            rbac.RemoveUser(admin.Id);

            Assert.Equal(ApiAuthorization.ApiAuthOutcome.Forbidden,
                ApiAuthorization.Evaluate(ctx, rbac, "settings", failClosedWithoutApiKey: true));
        }

        /// <summary>
        /// A non-fail-closed endpoint keeps the bootstrap hatch: dormant + loopback is allowed,
        /// dormant + LAN is not. Same rule as the pages, so the two surfaces cannot drift.
        /// </summary>
        [Theory]
        [InlineData("127.0.0.1", true)]
        [InlineData("::ffff:127.0.0.1", true)]
        [InlineData("192.168.10.32", false)]
        public void NonFailClosedEndpoints_KeepTheLoopbackBootstrapHatch(string ip, bool expectAllowed)
        {
            var outcome = ApiAuthorization.Evaluate(
                ApiRequest(ip), NewRbac(new RbacConfig()), "execute_checks", failClosedWithoutApiKey: false);

            Assert.Equal(
                expectAllowed ? ApiAuthorization.ApiAuthOutcome.Allowed
                              : ApiAuthorization.ApiAuthOutcome.Unauthenticated,
                outcome);
        }

        /// <summary>
        /// VERIFY THE INSTRUMENT. Before believing the new gate is load-bearing, prove what was
        /// there before it was not: the ONLY thing in front of <c>/api/*</c> with no ApiKey
        /// configured is <see cref="ApiEndpoints.IsLocalOrSameOriginRequest"/>, and it PASSES the
        /// exact request the attack sent — from 192.168.10.32, no Origin, no Referer.
        ///
        /// <para>This is the assertion that makes the rest of section 2 mean something. Without it
        /// the commit would only be claiming the old code was open; here it is exercised.</para>
        /// </summary>
        [Fact]
        public void TheOldSameOriginCheckPassesTheAttackRequest()
        {
            var ctx = ApiRequest("192.168.10.32");   // POST /api/v1/rbac/users, no Origin, no Referer

            Assert.True(ApiEndpoints.IsLocalOrSameOriginRequest(ctx),
                "If this ever returns false, the premise of the round-3 API fix has changed and the "
                + "reasoning in ApiAuthorization needs re-reading — not this assertion flipping.");
        }

        /// <summary>
        /// And the Host-header branch: a request from the LAN that simply SAYS it is localhost
        /// passes as well, because the check reads a client-supplied header. Two independent ways
        /// through the same door.
        /// </summary>
        [Fact]
        public void TheOldSameOriginCheckAlsoPassesASpoofedHostHeader()
        {
            var ctx = ApiRequest("192.168.10.32");
            ctx.Request.Host = new HostString("localhost", 5170);
            ctx.Request.Headers["Origin"] = "http://evil.example";

            // Origin says evil.example and Host says localhost — the localhost branch wins first.
            Assert.True(ApiEndpoints.IsLocalOrSameOriginRequest(ctx));
        }

        /// <summary>
        /// The new gate refuses BOTH of those, which is the whole point: the decision no longer
        /// depends on anything the client can write.
        /// </summary>
        [Fact]
        public void TheNewGateRefusesBothShapesTheOldCheckPassed()
        {
            var rbac = NewRbac(new RbacConfig());

            var plain = ApiRequest("192.168.10.32");
            var spoofed = ApiRequest("192.168.10.32");
            spoofed.Request.Headers["Origin"] = "http://evil.example";

            Assert.Equal(ApiAuthorization.ApiAuthOutcome.Unauthenticated,
                ApiAuthorization.Evaluate(plain, rbac, "settings", failClosedWithoutApiKey: true));
            Assert.Equal(ApiAuthorization.ApiAuthOutcome.Unauthenticated,
                ApiAuthorization.Evaluate(spoofed, rbac, "settings", failClosedWithoutApiKey: true));
        }

        // ══ 2b — the inbound passwordHash ══════════════════════════════════════════════════

        /// <summary>
        /// Step 1 of the proven chain. A caller must never SUPPLY the Argon2id digest of an account
        /// they are creating — that is choosing someone's credential.
        /// </summary>
        [Fact]
        public void AnInboundPasswordHashIsRefused()
        {
            var user = new RbacUser
            {
                Email = "attacker@example.com",
                Provider = AuthProviders.Local,
                Role = AppRoles.Admin,
                PasswordHash = RbacService.HashPassword("attacker chosen password"),
            };

            var refusal = ApiAuthorization.RefuseInboundPasswordHash(user);

            Assert.NotNull(refusal);
        }

        /// <summary>
        /// And a body that omits it is sanitised rather than merely tolerated: the field is nulled,
        /// so no downstream path can ever observe a caller-supplied value.
        /// </summary>
        [Fact]
        public void ABodyWithoutAPasswordHashIsAcceptedAndSanitised()
        {
            var user = new RbacUser { Email = "ok@example.com", Provider = AuthProviders.Local, Role = AppRoles.Viewer };

            Assert.Null(ApiAuthorization.RefuseInboundPasswordHash(user));
            Assert.Null(user.PasswordHash);
        }

        /// <summary>
        /// The refusal is independent of the permission gate. A caller holding a valid API key —
        /// who passes every authorization check — still may not plant a hash. A defence that only
        /// exists inside an authorization branch disappears the day the branch moves.
        /// </summary>
        [Fact]
        public void TheHashRefusalDoesNotDependOnTheCallersCredential()
        {
            var ctx = ApiRequest("192.168.10.32", apiKeyValidated: true);
            Assert.Equal(ApiAuthorization.ApiAuthOutcome.Allowed,
                ApiAuthorization.Evaluate(ctx, NewRbac(new RbacConfig()), "settings", failClosedWithoutApiKey: true));

            var user = new RbacUser { Email = "x@example.com", PasswordHash = "argon2id$..." };
            Assert.NotNull(ApiAuthorization.RefuseInboundPasswordHash(user));
        }

        // ── helpers ──────────────────────────────────────────────────────

        private static ClaimsPrincipal Principal(string provider, string identityKey)
            => new(new ClaimsIdentity(new[]
            {
                new Claim(SqlTriageAuthClaims.Provider, provider),
                new Claim(SqlTriageAuthClaims.IdentityKey, identityKey),
                new Claim(ClaimTypes.Name, identityKey),
            }, authenticationType: "TestCookie"));

        private static string ReadPageAndCodeBehind(string relative)
        {
            var full = Path.Combine(RawPassedScan.RepoRoot().FullName,
                relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), relative + " not found — the assertion cannot run and must fail.");
            var text = File.ReadAllText(full);
            if (File.Exists(full + ".cs")) text += File.ReadAllText(full + ".cs");
            return text;
        }
    }
}
