/* In the name of God, the Merciful, the Compassionate */

using System.Linq;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.MicrosoftAccount;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Composition assertions for the shared auth stack.
    ///
    /// <para>The design flagged one assumption as load-bearing and unexercised: that the circuit
    /// can read its principal from <see cref="AuthenticationStateProvider"/>. That was probed
    /// live against a running <c>--server</c> instance (loopback rendered /servers, the LAN
    /// address rendered AccessDenied on the same listener). These tests pin the DI half of it so
    /// a future package bump cannot silently remove the registration and leave the console user
    /// locked out with no failing test.</para>
    /// </summary>
    public class SqlTriageAuthPipelineTests
    {
        private static ServiceProvider Build(RbacConfig config)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            // The same two calls the hosts make around AddSqlTriageAuth.
            services.AddRazorComponents().AddInteractiveServerComponents();
            services.AddSqlTriageAuth(config);
            return services.BuildServiceProvider();
        }

        [Fact]
        public void AuthenticationStateProvider_IsResolvableFromACircuitScope()
        {
            // If this ever stops resolving, AppUserState loses the ONLY signal that survives
            // into a live circuit and every console user silently drops to viewer.
            using var sp = Build(new RbacConfig());
            using var scope = sp.CreateScope();

            Assert.NotNull(scope.ServiceProvider.GetService<AuthenticationStateProvider>());
        }

        [Fact]
        public void HttpContextAccessor_IsRegisteredForTheServerRenderFallback()
        {
            using var sp = Build(new RbacConfig());
            Assert.NotNull(sp.GetService<IHttpContextAccessor>());
        }

        [Fact]
        public void TheCookieSchemeIsTheDefault_EvenWithWindowsAuthOn()
        {
            // Negotiate is connection-based; making it the default would re-challenge per
            // connection instead of trading the handshake for one session cookie.
            var config = new RbacConfig();
            config.Windows.Enabled = true;

            using var sp = Build(config);
            var options = sp.GetRequiredService<IOptions<AuthenticationOptions>>().Value;

            Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, options.DefaultScheme);
            Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, options.DefaultChallengeScheme);
        }

        [Fact]
        public void AuthIsRegisteredEvenWhenRbacIsCompletelyUnconfigured()
        {
            // Gating this on rbac.Enabled is exactly what left the headless host with no
            // UseAuthentication and /auth/me returning 404 on a 0.0.0.0 listener.
            using var sp = Build(new RbacConfig());
            var options = sp.GetRequiredService<IOptions<AuthenticationOptions>>().Value;

            Assert.Contains(options.Schemes, s => s.Name == CookieAuthenticationDefaults.AuthenticationScheme);
        }

        [Fact]
        public void NegotiateSchemeIsRegisteredOnlyWhenWindowsAuthIsEnabled()
        {
            var off = new RbacConfig();
            using (var sp = Build(off))
            {
                var options = sp.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
                Assert.DoesNotContain(options.Schemes, s => s.Name == NegotiateDefaults.AuthenticationScheme);
            }

            var on = new RbacConfig();
            on.Windows.Enabled = true;
            using (var sp = Build(on))
            {
                var options = sp.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
                Assert.Contains(options.Schemes, s => s.Name == NegotiateDefaults.AuthenticationScheme);
            }
        }

        [Fact]
        public void OAuthSchemesNeedAClientId_NotJustTheEnabledFlag()
        {
            // The lockout guard treats "Enabled without a ClientId" as no provider at all.
            // That is only correct while the pipeline agrees — assert they cannot drift.
            var flagOnly = new RbacConfig();
            flagOnly.Google.Enabled = true;
            flagOnly.Microsoft.Enabled = true;

            using (var sp = Build(flagOnly))
            {
                var options = sp.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
                Assert.DoesNotContain(options.Schemes, s => s.Name == GoogleDefaults.AuthenticationScheme);
                Assert.DoesNotContain(options.Schemes, s => s.Name == MicrosoftAccountDefaults.AuthenticationScheme);
            }

            var configured = new RbacConfig();
            configured.Google.Enabled = true;
            configured.Google.ClientId = "gid";
            configured.Google.ClientSecret = "gsecret";

            using (var sp = Build(configured))
            {
                var options = sp.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
                Assert.Contains(options.Schemes, s => s.Name == GoogleDefaults.AuthenticationScheme);
            }
        }
    }
}
