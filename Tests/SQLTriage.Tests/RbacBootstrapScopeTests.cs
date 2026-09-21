/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Defect 1 and the §5 loopback ruling: an unconfigured install must reach the pages the
    /// bootstrap hatch was written to grant — and ONLY from the box.
    ///
    /// <para>Fixing Defect 1 without the loopback scope would have converted 22 fail-CLOSED
    /// surfaces into fail-OPEN ones for any unauthenticated network client, in the same commit.
    /// These tests hold both halves.</para>
    /// </summary>
    public class RbacBootstrapScopeTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _configPath;
        private readonly string _usersPath;

        public RbacBootstrapScopeTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "rbac-scope-" + Guid.NewGuid().ToString("N"));
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

        private static AppUserState NewState(RbacService rbac, bool browserHosted)
            => new(browserHosted ? HostEnvironmentInfo.BrowserHosted : HostEnvironmentInfo.Desktop,
                   rbac,
                   new ServiceCollection().BuildServiceProvider(),
                   NullLogger<AppUserState>.Instance);

        /// <summary>The permissions behind the 22 converted gate sites.</summary>
        public static TheoryData<string> GatedPermissions => new()
        {
            "manage_servers",     // /servers — the page Adrian hit
            "manage_alerts",      // /alerting-config
            "acknowledge_alerts", // /alerts, /alerts-noc
            "run_scripts",        // /bestpractice, /dbatools, /query, /server-configuration, /sessions, …
            "execute_checks",     // /audit
            "settings",           // /settings, /service-management, /reports owner editor
        };

        // ── The bootstrap hatch, on the box ──────────────────────────────

        [Theory]
        [MemberData(nameof(GatedPermissions))]
        public void UnconfiguredInstall_OnLoopback_ReachesThePreviouslyBlockedPages(string permission)
        {
            var state = NewState(NewRbac(new RbacConfig()), browserHosted: true);
            state.SetLoopbackForTests(true);

            Assert.True(state.IsAuthorized(permission),
                $"An unconfigured install must grant '{permission}' on loopback — that is the bootstrap hatch.");
        }

        [Theory]
        [MemberData(nameof(GatedPermissions))]
        public void UnconfiguredInstall_OnTheDesktop_IsAlwaysAuthorized(string permission)
        {
            // The WPF BlazorWebView: single user, no listener, no auth. Unchanged behaviour.
            var state = NewState(NewRbac(new RbacConfig()), browserHosted: false);

            Assert.True(state.IsAuthorized(permission));
        }

        // ── The same hatch, from the network ─────────────────────────────

        [Theory]
        [InlineData("manage_servers")]
        [InlineData("settings")]
        [InlineData("run_scripts")]
        [InlineData("execute_checks")]
        public void UnconfiguredInstall_FromTheNetwork_DoesNotGetAdmin(string permission)
        {
            // The production posture this closes: the installed service binds 0.0.0.0:5155 and
            // resolved every circuit to admin, so any unauthenticated client on the network
            // rendered /settings, /query and /server-configuration as an administrator.
            var state = NewState(NewRbac(new RbacConfig()), browserHosted: true);
            state.SetLoopbackForTests(false);

            Assert.False(state.IsAuthorized(permission));
        }

        [Fact]
        public void UnconfiguredInstall_FromTheNetwork_StillReadsDashboards()
        {
            // Remote clients drop to viewer, not to nothing.
            var state = NewState(NewRbac(new RbacConfig()), browserHosted: true);
            state.SetLoopbackForTests(false);

            Assert.True(state.IsAuthorized("view_dashboard"));
            Assert.True(state.IsAuthorized("view_results"));
        }

        // The two tests that used to sit here — AllowRemoteBootstrapAdmin_IsOffByDefault and
        // AllowRemoteBootstrapAdmin_OptsTheHeadlessBoxBackIn — are gone with the flag they
        // exercised (2026-08-03). The second one asserted, as a REQUIREMENT, that an anonymous
        // non-loopback caller can be granted settings and run_scripts. Its replacement is
        // RbacRound15RegressionTests.NoConfigurationAdmitsAnAnonymousNonLoopbackCaller, which
        // asserts the opposite across every raw-JSON shape of the deleted keys.

        // ── Once RBAC is enforced, the hatch is closed everywhere ────────

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void EnforcedRbac_LoopbackGrantsNoExtraAuthority(bool loopback)
        {
            var config = new RbacConfig { Enabled = true };
            config.Windows.Enabled = true;
            var rbac = NewRbac(config, new RbacUser
            {
                Email = @"MSI\admin.adrian", Provider = AuthProviders.Windows,
                Role = AppRoles.Admin, Enabled = true
            });
            Assert.True(rbac.IsRbacEnforced());   // precondition

            var state = NewState(rbac, browserHosted: true);
            state.SetRole(AppRoles.Viewer);
            state.SetLoopbackForTests(loopback);

            // Break-glass covers Settings — and NOTHING else. /service-management and the
            // report-bundle owner editor ride the same "settings" permission and must stay shut.
            Assert.False(state.IsAuthorized("settings"));
            Assert.False(state.IsAuthorized("run_scripts"));
            Assert.False(state.IsAuthorized("manage_servers"));
        }

        [Fact]
        public void EnforcedRbac_BreakGlassOpensSettingsOnLoopbackOnly()
        {
            var config = new RbacConfig { Enabled = true };
            config.Windows.Enabled = true;
            var rbac = NewRbac(config, new RbacUser
            {
                Email = @"MSI\admin.adrian", Provider = AuthProviders.Windows,
                Role = AppRoles.Admin, Enabled = true
            });

            var onBox = NewState(rbac, browserHosted: true);
            onBox.SetRole(AppRoles.Viewer);
            onBox.SetLoopbackForTests(true);
            Assert.True(onBox.IsAuthorizedWithBreakGlass("settings"));

            var remote = NewState(rbac, browserHosted: true);
            remote.SetRole(AppRoles.Viewer);
            remote.SetLoopbackForTests(false);
            Assert.False(remote.IsAuthorizedWithBreakGlass("settings"));
        }

        [Fact]
        public void EnforcedRbac_AnAdminIsAuthorizedEverywhere()
        {
            var config = new RbacConfig { Enabled = true };
            config.Windows.Enabled = true;
            var rbac = NewRbac(config, new RbacUser
            {
                Email = @"MSI\admin.adrian", Provider = AuthProviders.Windows,
                Role = AppRoles.Admin, Enabled = true
            });

            var state = NewState(rbac, browserHosted: true);
            state.SetRole(AppRoles.Admin);
            state.SetLoopbackForTests(false);

            foreach (var perm in GatedPermissions.Select(r => (string)r[0]!))
                Assert.True(state.IsAuthorized(perm), perm);
        }

        // ── The mechanism the loopback decision rests on ─────────────────

        [Theory]
        [InlineData("127.0.0.1", true)]
        [InlineData("::1", true)]
        [InlineData("::ffff:127.0.0.1", true)]   // dual-stack ListenAnyIP delivers v4 clients like this
        [InlineData("127.0.0.5", true)]
        [InlineData("192.168.1.20", false)]
        [InlineData("::ffff:192.168.1.20", false)]
        [InlineData("10.0.0.1", false)]
        public void IsLoopbackAddress(string address, bool expected)
            => Assert.Equal(expected, AppUserState.IsLoopbackAddress(IPAddress.Parse(address)));

        [Fact]
        public void IsLoopbackAddress_NullIsNotLoopback()
            => Assert.False(AppUserState.IsLoopbackAddress(null));

        [Fact]
        public async Task NoLoopbackSignalAtAll_FailsClosed()
        {
            // A circuit with no AuthenticationStateProvider and no HttpContext has no way to
            // know where the client is. The answer must be "remote" — a missing signal must
            // never mint an admin.
            var state = NewState(NewRbac(new RbacConfig()), browserHosted: true);
            await state.InitAsync();

            Assert.False(state.IsLoopback);
            Assert.False(state.IsBootstrapEligible);
            Assert.Equal(AppRoles.Viewer, state.Role);
            Assert.False(state.IsAuthorized("settings"));
        }

        [Fact]
        public async Task TheDesktopHostResolvesToAdminWithoutAnyAuthStack()
        {
            var state = NewState(NewRbac(new RbacConfig()), browserHosted: false);
            await state.InitAsync();

            Assert.Equal(AppRoles.Admin, state.Role);
            Assert.True(state.IsAdmin);
        }

        [Fact]
        public async Task HttpContextIsTheFallbackLoopbackSignal()
        {
            // Used during the server-side render, before the circuit exists.
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:127.0.0.1");

            var services = new ServiceCollection();
            services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = ctx });

            var state = new AppUserState(
                HostEnvironmentInfo.BrowserHosted,
                NewRbac(new RbacConfig()),
                services.BuildServiceProvider(),
                NullLogger<AppUserState>.Instance);

            await state.InitAsync();

            Assert.True(state.IsLoopback);
            Assert.True(state.IsBootstrapEligible);
            Assert.True(state.IsAuthorized("manage_servers"));
        }

        [Fact]
        public async Task HttpContextFromTheNetwork_DoesNotGrantBootstrap()
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");
            // The Host header is attacker-controlled; it must not move the answer.
            ctx.Request.Host = new HostString("localhost", 5155);

            var services = new ServiceCollection();
            services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = ctx });

            var state = new AppUserState(
                HostEnvironmentInfo.BrowserHosted,
                NewRbac(new RbacConfig()),
                services.BuildServiceProvider(),
                NullLogger<AppUserState>.Instance);

            await state.InitAsync();

            Assert.False(state.IsLoopback);
            Assert.False(state.IsAuthorized("settings"));
        }

        // ── Host mode is a property of the CONTAINER ─────────────────────

        [Fact]
        public void HostEnvironmentInfo_HasBothShapes()
        {
            Assert.False(HostEnvironmentInfo.Desktop.IsBrowserHosted);
            Assert.True(HostEnvironmentInfo.BrowserHosted.IsBrowserHosted);
        }
    }
}
