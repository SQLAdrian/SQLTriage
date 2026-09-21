/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.MicrosoftAccount;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Round 2 of the server-mode RBAC lane: the two blocking defects the reviewers exercised
    /// end to end from a LAN origin, plus the lockout guard's missing admin↔provider pairing.
    ///
    /// <para>Every test here compiles against the round-1 tree (<c>a5497e5</c>) and fails there —
    /// a regression test whose "before" state is a compile error proves nothing about behaviour.
    /// Real runner output for the red run is in the commit message.</para>
    /// </summary>
    public class RbacRound2RegressionTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _configPath;
        private readonly string _usersPath;

        public RbacRound2RegressionTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "rbac-round2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _configPath = Path.Combine(_tempDir, "rbac-config.json");
            _usersPath = Path.Combine(_tempDir, "rbac-users.json");
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup; ignore */ }
        }

        private RbacService NewService(RbacConfig? config = null, params RbacUser[] users)
        {
            if (config != null) File.WriteAllText(_configPath, JsonSerializer.Serialize(config));
            if (users.Length > 0) File.WriteAllText(_usersPath, JsonSerializer.Serialize(users.ToList()));
            return new RbacService(NullLogger<RbacService>.Instance, _configPath, _usersPath);
        }

        private static RbacUser Admin(string email = "admin@example.com", string provider = AuthProviders.Google)
            => new() { Email = email, DisplayName = email, Provider = provider, Role = AppRoles.Admin, Enabled = true };

        // ══ B2 — a half-configured OAuth provider must not brick the host ═══════════════════
        //
        // SqlTriageAuth registered Google on `Enabled && ClientId` alone. With a client id set and
        // an empty secret, CredentialProtector.Decrypt("") returns "" and UseAuthentication()
        // throws on EVERY request:
        //   System.ArgumentException: The value cannot be an empty string. (Parameter 'ClientSecret')
        // Measured 2026-08-01: /settings, /auth/login, /auth/me, /servers and /_server/health all
        // 500 on loopback AND the LAN, with RBAC on or off. Loopback break-glass cannot reach a
        // host that 500s on every request, so this is a total, UI-reachable lockout.

        private static RbacConfig GoogleWithEmptySecret()
        {
            var config = new RbacConfig();
            config.Google.Enabled = true;
            config.Google.ClientId = "1234.apps.googleusercontent.com";
            config.Google.ClientSecret = "";
            return config;
        }

        /// <summary>
        /// The mechanism, pinned. This is what the pipeline used to do on the first request of
        /// every host that had a half-typed provider saved. It passes before and after the fix —
        /// it is here so the reason the registration is skipped cannot be argued away later.
        /// </summary>
        [Fact]
        public void GoogleOptionsWithAnEmptySecret_ThrowOnValidate_WhichIsWhatKilledTheHost()
        {
            var options = new GoogleOptions
            {
                ClientId = "1234.apps.googleusercontent.com",
                ClientSecret = ""
            };

            var ex = Assert.Throws<ArgumentException>(() => options.Validate());
            Assert.Contains("ClientSecret", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AddSqlTriageAuth_GoogleEnabledWithNoSecret_DoesNotRegisterTheScheme()
        {
            var schemes = await SchemesFor(GoogleWithEmptySecret());

            Assert.DoesNotContain(GoogleDefaults.AuthenticationScheme, schemes);
        }

        [Fact]
        public async Task AddSqlTriageAuth_GoogleEnabledWithNoSecret_StillRegistersTheCookieScheme()
        {
            // The point of the fix: the provider degrades to unavailable, the HOST KEEPS SERVING.
            var schemes = await SchemesFor(GoogleWithEmptySecret());

            Assert.Contains(CookieAuthenticationDefaults.AuthenticationScheme, schemes);
        }

        [Fact]
        public async Task AddSqlTriageAuth_MicrosoftEnabledWithNoSecret_DoesNotRegisterTheScheme()
        {
            var config = new RbacConfig();
            config.Microsoft.Enabled = true;
            config.Microsoft.ClientId = "00000000-0000-0000-0000-000000000000";
            config.Microsoft.ClientSecret = "";

            var schemes = await SchemesFor(config);

            Assert.DoesNotContain(MicrosoftAccountDefaults.AuthenticationScheme, schemes);
        }

        [Fact]
        public async Task AddSqlTriageAuth_AFullyConfiguredProvider_IsStillRegistered()
        {
            // Guard against over-correction: the fix must not stop working providers from loading.
            // A plaintext secret decrypts to itself (CredentialProtector's legacy path), so this
            // needs no DPAPI/AES key material.
            var config = new RbacConfig();
            config.Google.Enabled = true;
            config.Google.ClientId = "1234.apps.googleusercontent.com";
            config.Google.ClientSecret = "a-real-secret";

            var schemes = await SchemesFor(config);

            Assert.Contains(GoogleDefaults.AuthenticationScheme, schemes);
        }

        private static async Task<List<string>> SchemesFor(RbacConfig config)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSqlTriageAuth(config);
            await using var provider = services.BuildServiceProvider();

            var all = await provider.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();
            return all.Select(s => s.Name).ToList();
        }

        [Fact]
        public void DescribeOAuthProblem_EmptySecret_IsNamed()
        {
            var problem = RbacService.DescribeOAuthProblem(GoogleWithEmptySecret().Google);

            Assert.NotNull(problem);
            Assert.Contains("Secret", problem!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void DescribeProviderConfigProblems_HalfConfiguredProvider_RefusesTheSave_EvenWithRbacOff()
        {
            // Settings calls this before UpdateConfig. RBAC is OFF in this config on purpose: the
            // auth pipeline is built either way, so "RBAC is off" is not a reason to let it through.
            var rbac = NewService(new RbacConfig(), Admin());

            var problems = rbac.DescribeProviderConfigProblems(GoogleWithEmptySecret());

            Assert.NotEmpty(problems);
            Assert.Contains(problems, p => p.Contains("Google", StringComparison.Ordinal));
        }

        [Fact]
        public void DescribeProviderConfigProblems_AProviderThatIsSimplyOff_IsNotAProblem()
        {
            var rbac = NewService(new RbacConfig(), Admin());

            Assert.Empty(rbac.DescribeProviderConfigProblems(new RbacConfig()));
        }

        [Fact]
        public void DescribeEnforcementBlockers_GoogleEnabledWithNoSecret_IsRefused()
        {
            // The same misconfiguration reached through the enforcement guard: an admin whose only
            // route in is a provider that will not register cannot sign in, so enabling RBAC on top
            // of it is the lockout L1/L2 exist to refuse. At a5497e5 `Enabled && ClientId` counted
            // as a usable provider and this list came back empty.
            var config = GoogleWithEmptySecret();
            config.Enabled = true;
            var rbac = NewService(config, Admin());

            Assert.NotEmpty(rbac.DescribeEnforcementBlockers(config));
            Assert.False(rbac.IsRbacEnforced());
        }

        // ══ Reviewer item 4 — the guard must pair an admin to a provider ════════════════════
        //
        // "Is a provider on?" and "is an admin present?" answered separately is not the same
        // question as "can an admin get in?". Windows auth on with only an email-identity admin
        // passed the old test and sealed the box: Negotiate returns MSI\afsul, no email-class
        // record matches it, and the only way back is loopback break-glass — which a headless
        // server does not have.

        [Fact]
        public void IsRbacEnforced_WindowsAuthOnButTheOnlyAdminIsAnEmailIdentity_StaysDormant()
        {
            var config = new RbacConfig { Enabled = true };
            config.Windows.Enabled = true;
            var rbac = NewService(config, Admin(provider: AuthProviders.Google));

            Assert.False(
                rbac.IsRbacEnforced(),
                "A Windows sign-in cannot reach an email-class admin record — that is a lockout, not an enforcement.");
        }

        [Fact]
        public void IsRbacEnforced_WindowsAuthOnWithAWindowsAdmin_Enforces()
        {
            var config = new RbacConfig { Enabled = true };
            config.Windows.Enabled = true;
            var rbac = NewService(config, Admin(Environment.MachineName + @"\admin.adrian", AuthProviders.Windows));

            Assert.True(rbac.IsRbacEnforced());
        }

        [Fact]
        public void IsRbacEnforced_GoogleUsableButItsDomainExcludesTheOnlyAdmin_StaysDormant()
        {
            // /auth/complete refuses a domain that is not the allowed one, so a guard that ignores
            // AllowedDomain approves an enforcement nobody can sign into.
            var config = new RbacConfig { Enabled = true };
            config.Google.Enabled = true;
            config.Google.ClientId = "1234.apps.googleusercontent.com";
            config.Google.ClientSecret = "a-real-secret";
            config.Google.AllowedDomain = "contoso.com";
            var rbac = NewService(config, Admin("admin@example.com"));

            Assert.False(rbac.IsRbacEnforced());
        }

        [Fact]
        public void IsRbacEnforced_GoogleUsableAndItsDomainAdmitsTheAdmin_Enforces()
        {
            var config = new RbacConfig { Enabled = true };
            config.Google.Enabled = true;
            config.Google.ClientId = "1234.apps.googleusercontent.com";
            config.Google.ClientSecret = "a-real-secret";
            config.Google.AllowedDomain = "contoso.com";
            var rbac = NewService(config, Admin("admin@contoso.com"));

            Assert.True(rbac.IsRbacEnforced());
        }

        // ══ B3 — the circuit must trust the STORE, not the cookie ═══════════════════════════
        //
        // AppUserState.InitAsync took ClaimTypes.Role straight off the cookie. Measured: a user
        // DELETED in Settings kept full admin from the LAN under enforced RBAC, across restarts,
        // and with SlidingExpiration the 8h session renewed indefinitely. Remove / disable /
        // demote were advisory.

        private sealed class StubAuthStateProvider : AuthenticationStateProvider
        {
            private readonly ClaimsPrincipal _user;
            public StubAuthStateProvider(ClaimsPrincipal user) => _user = user;
            public override Task<AuthenticationState> GetAuthenticationStateAsync()
                => Task.FromResult(new AuthenticationState(_user));
        }

        /// <summary>
        /// A circuit carrying a cookie that says "admin", arriving from a NON-loopback address —
        /// exactly the LAN case the reviewer exercised.
        /// </summary>
        private static AppUserState CircuitWithAdminCookie(
            RbacService rbac, string identityKey = "admin@example.com", string provider = AuthProviders.Google)
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(SqlTriageAuthClaims.IdentityKey, identityKey),
                new Claim(ClaimTypes.Name, identityKey),
                new Claim(ClaimTypes.Role, AppRoles.Admin),     // the cookie's claim — must not be believed
                new Claim(SqlTriageAuthClaims.Provider, provider),
                new Claim(SqlTriageAuthClaims.Loopback, "false")
            }, CookieAuthenticationDefaults.AuthenticationScheme);

            var services = new ServiceCollection();
            services.AddSingleton<AuthenticationStateProvider>(
                new StubAuthStateProvider(new ClaimsPrincipal(identity)));

            return new AppUserState(
                HostEnvironmentInfo.BrowserHosted, rbac, services.BuildServiceProvider(),
                NullLogger<AppUserState>.Instance);
        }

        /// <summary>Enforced RBAC that does not depend on the user under test still existing.</summary>
        private RbacService EnforcedRbac(params RbacUser[] users)
        {
            var config = new RbacConfig { Enabled = true };
            config.Windows.Enabled = true;
            var all = users.Concat(new[]
            {
                Admin(Environment.MachineName + @"\other.admin", AuthProviders.Windows)
            }).ToArray();
            return NewService(config, all);
        }

        [Fact]
        public async Task InitAsync_UserDeletedFromTheStore_DropsToViewerAndIsDenied()
        {
            var rbac = EnforcedRbac();                 // the cookie's principal is NOT in the store
            Assert.True(rbac.IsRbacEnforced());

            var state = CircuitWithAdminCookie(rbac);
            await state.InitAsync();

            Assert.Equal(AppRoles.Viewer, state.Role);
            Assert.False(state.IsAdmin);
            Assert.False(state.IsAuthorized("settings"));
            Assert.False(state.IsAuthorized("run_scripts"));
            Assert.False(state.IsAuthorizedWithBreakGlass("settings"));   // not loopback either
            Assert.True(state.IsPrincipalRevoked);
        }

        [Fact]
        public async Task InitAsync_UserDisabledInTheStore_DropsToViewer()
        {
            var disabled = Admin();
            disabled.Enabled = false;
            var rbac = EnforcedRbac(disabled);

            var state = CircuitWithAdminCookie(rbac);
            await state.InitAsync();

            Assert.Equal(AppRoles.Viewer, state.Role);
            Assert.True(state.IsPrincipalRevoked);
        }

        [Fact]
        public async Task InitAsync_UserDemotedInTheStore_TakesTheStoreRoleNotTheCookieRole()
        {
            var demoted = Admin();
            demoted.Role = AppRoles.Operator;
            var rbac = EnforcedRbac(demoted);

            var state = CircuitWithAdminCookie(rbac);
            await state.InitAsync();

            Assert.Equal(AppRoles.Operator, state.Role);
            Assert.False(state.IsAdmin);
            Assert.False(state.IsAuthorized("settings"));
            Assert.True(state.IsAuthorized("run_scripts"));   // operator keeps what operators have
            Assert.False(state.IsPrincipalRevoked);
        }

        [Fact]
        public async Task InitAsync_AStillValidAdmin_KeepsAdmin()
        {
            // Guard against over-correction — the store must be able to say YES.
            var rbac = EnforcedRbac(Admin());

            var state = CircuitWithAdminCookie(rbac);
            await state.InitAsync();

            Assert.Equal(AppRoles.Admin, state.Role);
            Assert.True(state.IsAuthorized("settings"));
            Assert.False(state.IsPrincipalRevoked);
        }

        [Fact]
        public async Task InitAsync_WindowsPrincipalMatchedByName_KeepsItsStoredRole()
        {
            var key = Environment.MachineName + @"\admin.adrian";
            var rbac = EnforcedRbac(Admin(key, AuthProviders.Windows));

            var state = CircuitWithAdminCookie(rbac, key, AuthProviders.Windows);
            await state.InitAsync();

            Assert.Equal(AppRoles.Admin, state.Role);
        }

        [Fact]
        public async Task InitAsync_AWindowsCookieMustNotMatchAnEmailRecordWithTheSameText()
        {
            // Provider CLASS matching, held at the resolution boundary too: a UPN-form Windows
            // account and a Google address can be byte-identical and are different principals.
            var rbac = EnforcedRbac(Admin("adrian@contoso.com", AuthProviders.Google));

            var state = CircuitWithAdminCookie(rbac, "adrian@contoso.com", AuthProviders.Windows);
            await state.InitAsync();

            Assert.Equal(AppRoles.Viewer, state.Role);
            Assert.True(state.IsPrincipalRevoked);
        }

        [Fact]
        public async Task ALiveCircuit_LosesAdminWhenTheUserIsRemoved_WithoutReInitialising()
        {
            // A Blazor circuit outlives the request that made it: one open tab is one circuit for
            // hours, and InitAsync is idempotent. Resolving only at init would leave a revoked
            // admin in place until they reloaded the page — "advisory" all over again, just with a
            // shorter fuse. The role is re-read on a TTL; the seam below expires it.
            var admin = Admin();
            var rbac = EnforcedRbac(admin);

            var state = CircuitWithAdminCookie(rbac);
            await state.InitAsync();
            Assert.Equal(AppRoles.Admin, state.Role);       // still a live account

            rbac.RemoveUser(admin.Id);                       // Settings ▸ Access Control ▸ remove
            Assert.Equal(AppRoles.Admin, state.Role);        // cached — inside the TTL

            state.ExpireStoreCacheForTests();

            Assert.Equal(AppRoles.Viewer, state.Role);
            Assert.False(state.IsAuthorized("settings"));
            Assert.True(state.IsPrincipalRevoked);
        }

        [Fact]
        public void ResolvePrincipal_NoIdentityKey_IsUnknownAndViewer()
        {
            var rbac = NewService(new RbacConfig(), Admin());

            var resolved = rbac.ResolvePrincipal(AuthProviders.Google, "");

            Assert.Equal(RbacService.PrincipalStatus.Unknown, resolved.Status);
            Assert.Equal(AppRoles.Viewer, resolved.Role);
        }
    }
}
