/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;

namespace SQLTriage.Tests
{
    /// <summary>
    /// A REAL Kestrel host serving the SHIPPED <c>/api/v1</c> surface, composed exactly the way
    /// <see cref="ServerModeService"/> composes it — front door, then
    /// <see cref="ApiEndpoints.UseApiKeyAuth"/>, then <see cref="SqlTriageAuth.UseSqlTriageAuth"/>,
    /// then <see cref="ApiEndpoints.MapApiEndpoints"/> — and bound to every interface so the two
    /// origin classes are genuinely different sockets rather than a simulated flag.
    ///
    /// <para><b>The services behind the handlers are REAL instances over empty temp state.</b> Not
    /// mocks and not throwing stubs: a handler that runs must be able to answer, or "allowed"
    /// would be indistinguishable from "crashed" and every positive control in the test class
    /// would be worthless. They are constructed with explicit temp paths so nothing here can read
    /// or write a configured install's <c>server-connections.json</c>, audit log or user
    /// store.</para>
    ///
    /// <para><b>Ports.</b> Port 0 — the OS chooses. The operator's host owns 5150/5151 on this box
    /// and the installed production service owns 5155/5156;
    /// <see cref="AssertPortIsSafe"/> refuses to run if a bind somehow lands on one.</para>
    /// </summary>
    internal sealed class ApiSurfaceHost : IAsyncDisposable
    {
        internal const string TestApiKey = "api-surface-host-test-key";

        /// <summary>Down-level Windows principals the store is seeded with.</summary>
        internal const string AdminPrincipal = @"APITEST\admin";
        internal const string ViewerPrincipal = @"APITEST\viewer";

        /// <summary>Test-only sign-in, mapped under <c>/auth</c> so the front door lets it through.</summary>
        internal const string TestSignInPath = "/auth/_test/signin";

        private readonly WebApplication _app;
        private readonly string _tempRoot;

        internal int Port { get; }
        internal IPAddress LoopbackAddress { get; }
        internal IPAddress NonLoopbackAddress { get; }

        internal string LoopbackBase => $"http://{LoopbackAddress}:{Port}";
        internal string NonLoopbackBase => $"http://{NonLoopbackAddress}:{Port}";

        private ApiSurfaceHost(WebApplication app, string tempRoot, int port, IPAddress nonLoopback)
        {
            _app = app;
            _tempRoot = tempRoot;
            Port = port;
            LoopbackAddress = IPAddress.Loopback;
            NonLoopbackAddress = nonLoopback;
        }

        internal static async Task<ApiSurfaceHost> StartAsync(
            string? apiKey = null, RbacConfig? config = null, RbacUser[]? users = null)
        {
            var nonLoopback = InteractiveAppAdmissionHost.FindNonLoopbackIPv4();

            var tempRoot = Path.Combine(Path.GetTempPath(), "sqltriage-apisurface-" + Guid.NewGuid().ToString("N"));
            var webRoot = Path.Combine(tempRoot, "wwwroot");
            Directory.CreateDirectory(webRoot);

            var configPath = Path.Combine(tempRoot, "rbac-config.json");
            var usersPath = Path.Combine(tempRoot, "rbac-users.json");
            if (config != null) File.WriteAllText(configPath, JsonSerializer.Serialize(config));
            if (users is { Length: > 0 }) File.WriteAllText(usersPath, JsonSerializer.Serialize(users.ToList()));

            var rbac = new RbacService(NullLogger<RbacService>.Instance, configPath, usersPath);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
                WebRootPath = webRoot,
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(0));

            // ApiKey lives in IConfiguration, which is where UseApiKeyAuth reads it from. The
            // DEFAULT — the configuration under test — is that it is absent.
            var settings = new Dictionary<string, string?>();
            if (apiKey != null) settings["ApiKey"] = apiKey;
            builder.Configuration.AddInMemoryCollection(settings);

            builder.Services.AddSingleton(rbac);
            builder.Services.AddAntiforgery();
            builder.Services.AddSqlTriageAuth(rbac.Config);
            RegisterApiHandlerServices(builder.Services, tempRoot);

            var app = builder.Build();

            // ── The shipped composition for /api, in ServerModeService's order ──
            app.UseSqlTriageFrontDoor(rbac);
            app.UseApiKeyAuth();
            app.UseSqlTriageAuth(rbac);
            app.UseAntiforgery();
            app.Use(ApiEndpoints.ExceptionHandler);
            app.MapApiEndpoints();

            MapTestSignIn(app);

            await app.StartAsync();

            var port = ResolvePort(app);
            AssertPortIsSafe(port);

            return new ApiSurfaceHost(app, tempRoot, port, nonLoopback);
        }

        /// <summary>
        /// The services the shipped handlers take as minimal-API parameters. Every path they touch
        /// is under a per-run temp root: <see cref="ServerConnectionManager"/> otherwise defaults to
        /// <c>AppContext.BaseDirectory\Config\server-connections.json</c>, and a test that read a
        /// configured install's connections would be reading client server names.
        /// </summary>
        private static void RegisterApiHandlerServices(IServiceCollection services, string tempRoot)
        {
            var configuration = new ConfigurationBuilder().Build();

            var connections = Path.Combine(tempRoot, "server-connections.json");
            var auditDir = Path.Combine(tempRoot, "audit-logs");

            var connMgr = new ServerConnectionManager(
                NullLogger<ServerConnectionManager>.Instance, seats: null, connectionsFilePath: connections);
            var checkRepo = new CheckRepositoryService(NullLogger<CheckRepositoryService>.Instance, configuration);

            services.AddSingleton(connMgr);
            services.AddSingleton(checkRepo);
            services.AddSingleton(new CheckExecutionService(
                NullLogger<CheckExecutionService>.Instance, checkRepo, connMgr, configuration));
            services.AddSingleton(new AlertingService(NullLogger<AlertingService>.Instance));
            // r2-06: /status and /alerts read the real active-alert state from here, not the dead
            // AlertingService notification queue. A real instance over the shared temp history db.
            services.AddSingleton(new AlertHistoryService(NullLogger<AlertHistoryService>.Instance));
            services.AddSingleton(new AutoUpdateService(NullLogger<AutoUpdateService>.Instance, configuration));
            services.AddSingleton(new AuditLogService(auditDir, startFlushTimer: false));
        }

        /// <summary>
        /// Mints the same cookie, on the same scheme, through the same handler
        /// <c>SqlTriageAuth.IssueSessionCookie</c> uses — carrying the provider and identity-key
        /// claims <see cref="RbacService.ResolvePrincipal"/> reads, so the role comes from the
        /// STORE and a cookie cannot assert a role of its own.
        /// </summary>
        private static void MapTestSignIn(WebApplication app)
        {
            app.MapGet(TestSignInPath, async (HttpContext ctx, string principal) =>
            {
                var identity = new ClaimsIdentity(
                    new[]
                    {
                        new Claim(SqlTriageAuthClaims.IdentityKey, principal),
                        new Claim(ClaimTypes.Name, principal),
                        new Claim(SqlTriageAuthClaims.Provider, AuthProviders.Windows),
                    },
                    CookieAuthenticationDefaults.AuthenticationScheme);

                await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
                return Results.Content("signed-in", "text/plain");
            });
        }

        /// <summary>Signs the given client in as the seeded admin or viewer.</summary>
        internal async Task SignInAsync(HttpClient client, string role)
        {
            var principal = role == AppRoles.Admin ? AdminPrincipal : ViewerPrincipal;
            var response = await client.GetAsync(TestSignInPath + "?principal=" + Uri.EscapeDataString(principal));
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"The test sign-in returned {(int)response.StatusCode}; every authenticated assertion "
                    + "below it would then be measuring an anonymous caller.");
        }

        /// <summary>Resolves a service from the running host — used to seed real state (e.g. an
        /// active alert in the history db) before driving a request against it.</summary>
        internal T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

        internal static HttpClient Client(string baseAddress) =>
            new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(baseAddress),
                Timeout = TimeSpan.FromSeconds(30),
            };

        internal static HttpClient CookieClient(string baseAddress) =>
            new(new HttpClientHandler { UseCookies = true, AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(baseAddress),
                Timeout = TimeSpan.FromSeconds(30),
            };

        private static int ResolvePort(WebApplication app)
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                            ?? throw new InvalidOperationException("Kestrel exposed no server addresses feature.");

            foreach (var address in addresses.Addresses)
            {
                var colon = address.LastIndexOf(':');
                if (colon < 0) continue;
                if (int.TryParse(address[(colon + 1)..].TrimEnd('/'), out var port) && port > 0) return port;
            }

            throw new InvalidOperationException(
                "Kestrel reported no bound address: " + string.Join(", ", addresses.Addresses));
        }

        private static void AssertPortIsSafe(int port)
        {
            int[] forbidden = { 5150, 5151, 5155, 5156 };
            if (forbidden.Contains(port))
                throw new InvalidOperationException(
                    $"The test host bound port {port}, which belongs to a live SQLTriage listener on this "
                    + "machine. Refusing to run.");
        }

        public async ValueTask DisposeAsync()
        {
            try { await _app.StopAsync(); } catch { /* the test is over either way */ }
            try { await _app.DisposeAsync(); } catch { }
            try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); } catch { }
        }
    }
}
