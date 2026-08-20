/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;

namespace SQLTriage.Tests
{
    /// <summary>
    /// A REAL Kestrel host, bound to every interface on an ephemeral port, whose front half is the
    /// SHIPPED composition — <see cref="InteractiveAppAdmission.UseSqlTriageFrontDoor"/> followed by
    /// <see cref="SqlTriageAuth.UseSqlTriageAuth"/> — so the decider drives the pipeline the two
    /// production hosts build, not a copy of it that can drift from them.
    ///
    /// <para><b>What is real here.</b> The listener, the sockets, the two origin classes (a real
    /// loopback address and a real non-loopback IPv4 of this machine, so
    /// <c>Connection.RemoteIpAddress</c> is genuinely one or the other), the cookie handler and the
    /// session cookie, every shipped <c>/auth/*</c> endpoint including the rendered sign-in page,
    /// static file serving out of a real web root, the gate itself, and — behind the gate — the
    /// REAL <c>BoundaryCanary</c> component rendered by the real Blazor
    /// <see cref="HtmlRenderer"/>, ungated shapes and all.</para>
    ///
    /// <para><b>What is a stand-in, stated plainly.</b> The endpoint terminal is not the whole
    /// application: booting <c>ServerApp</c> would need the entire DI graph, several background
    /// engines and a SQL Server. It is a document that carries the same two things an attacker
    /// needs — the rendered canary markup and a <c>_framework/blazor.web.js</c> reference — plus
    /// <c>/_blazor</c> handlers standing in for the circuit hub. The property under test is
    /// "is this caller handed a document/circuit at all", and that is decided entirely by the
    /// middleware in front, which is shipped code. The whole application behind the same gate was
    /// driven by hand from a non-loopback origin and recorded in the commit message; this class is
    /// what keeps it true tomorrow.</para>
    ///
    /// <para>Deliberately configured as the WORST case: an unconfigured install (RBAC dormant, no
    /// sign-in provider, no users). That is precisely the state in which, before 2026-08-03, an
    /// unauthenticated caller from the LAN was served the entire interactive application.</para>
    /// </summary>
    internal sealed class InteractiveAppAdmissionHost : IAsyncDisposable
    {
        /// <summary>Marker in the terminal document. Its presence means "an app was served".</summary>
        internal const string InteractiveAppMarker = "SQLTRIAGE-INTERACTIVE-APPLICATION";

        /// <summary>Marker in the <c>/_blazor</c> stand-ins. Its presence means the circuit negotiated.</summary>
        internal const string BlazorCircuitMarker = "SQLTRIAGE-BLAZOR-CIRCUIT-NEGOTIATED";

        /// <summary>Contents of the static asset served out of the real web root.</summary>
        internal const string StaticAssetMarker = "SQLTRIAGE-STATIC-ASSET";

        /// <summary>Path of that asset.</summary>
        internal const string StaticAssetPath = "/admission-probe.css";

        /// <summary>Test-only sign-in, mapped under <c>/auth</c> so a refused caller can reach it.</summary>
        internal const string TestSignInPath = "/auth/_test/signin";

        private readonly WebApplication _app;
        private readonly string _tempRoot;

        internal int Port { get; }
        internal IPAddress LoopbackAddress { get; }
        internal IPAddress NonLoopbackAddress { get; }

        internal string LoopbackBase => $"http://{LoopbackAddress}:{Port}";
        internal string NonLoopbackBase => $"http://{NonLoopbackAddress}:{Port}";

        private InteractiveAppAdmissionHost(WebApplication app, string tempRoot, int port, IPAddress nonLoopback)
        {
            _app = app;
            _tempRoot = tempRoot;
            Port = port;
            LoopbackAddress = IPAddress.Loopback;
            NonLoopbackAddress = nonLoopback;
        }

        /// <summary>
        /// Starts the host.
        ///
        /// <para>The default (both arguments null) is the WORST case described above: an
        /// unconfigured install. Pass a <paramref name="config"/> and <paramref name="users"/> to
        /// stand the same shipped pipeline up over a CONFIGURED one — which is what
        /// <c>ARemoteBootstrapFlagDoesNotSurviveEnforcement</c> needs, because the flag's defect
        /// only appears once RBAC is actually enforcing.</para>
        /// </summary>
        internal static Task<InteractiveAppAdmissionHost> StartAsync(
            RbacConfig? config = null, params RbacUser[] users)
            => StartCoreAsync(
                config == null ? null : JsonSerializer.Serialize(config),
                users.Length > 0 ? JsonSerializer.Serialize(users.ToList()) : null);

        /// <summary>
        /// Stands the same shipped pipeline over a user store whose CONTENT is chosen byte for
        /// byte — a truncated file, an empty one, the wrong shape.
        ///
        /// <para>Serialising an <see cref="RbacUser"/> array cannot express any of those, and they
        /// are the exact states that made the round-9 bootstrap expiry fail open: a store that does
        /// not parse deserialises to an empty list, which is indistinguishable from a fresh install
        /// unless the loader says which it was.</para>
        /// </summary>
        internal static Task<InteractiveAppAdmissionHost> StartAsync(RbacConfig config, string? rawUsersJson)
            => StartCoreAsync(JsonSerializer.Serialize(config), rawUsersJson);

        private static async Task<InteractiveAppAdmissionHost> StartCoreAsync(
            string? rawConfigJson, string? rawUsersJson)
        {
            var nonLoopback = FindNonLoopbackIPv4();

            var tempRoot = Path.Combine(Path.GetTempPath(), "sqltriage-admission-" + Guid.NewGuid().ToString("N"));
            var webRoot = Path.Combine(tempRoot, "wwwroot");
            Directory.CreateDirectory(webRoot);
            File.WriteAllText(Path.Combine(webRoot, StaticAssetPath.TrimStart('/')), "/* " + StaticAssetMarker + " */");

            // Written to disk rather than poked onto the object, so RbacService loads them exactly
            // the way it loads a real install's Config\rbac-*.json.
            var configPath = Path.Combine(tempRoot, "rbac-config.json");
            var usersPath = Path.Combine(tempRoot, "rbac-users.json");
            if (rawConfigJson != null) File.WriteAllText(configPath, rawConfigJson);
            if (rawUsersJson != null) File.WriteAllText(usersPath, rawUsersJson);

            var rbac = new RbacService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RbacService>.Instance,
                configPath,
                usersPath);

            // A real web root with a real file in it, so "static assets are refused" is measured
            // against a file that genuinely exists rather than against a 404. It has to go through
            // WebApplicationOptions: WebApplicationBuilder refuses a web-root change made after
            // construction.
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
                WebRootPath = webRoot,
            });
            builder.Logging.ClearProviders();

            // Port 0 = let the OS choose. The operator's host owns 5150/5151 and the installed
            // service owns 5155/5156 on this box; an ephemeral port cannot collide with either,
            // and AssertPortIsSafe below refuses to run if it somehow does.
            builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(0));

            builder.Services.AddSingleton(rbac);

            // The same two registrations the production hosts make before AddSqlTriageAuth.
            // AddCascadingAuthenticationState (inside AddSqlTriageAuth) resolves the
            // AuthenticationStateProvider that AddInteractiveServerComponents supplies, and it is
            // that provider AppUserState reads a live circuit's principal from — so leaving it out
            // would be a host this app never builds.
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();
            builder.Services.AddSqlTriageAuth(rbac.Config);

            var app = builder.Build();

            // ── THE SHIPPED FRONT HALF ───────────────────────────────────
            // Exactly what WindowsServiceHost and ServerModeService call, in the same order,
            // through the same two methods. Delete the UseSqlTriageAdmission line inside
            // UseSqlTriageFrontDoor and every non-loopback assertion in
            // InteractiveAppAdmissionTests goes red — that is the measured red half.
            app.UseSqlTriageFrontDoor(rbac);
            app.UseSqlTriageAuth(rbac);
            app.UseAntiforgery();

            MapTerminals(app);

            await app.StartAsync();

            var port = ResolvePort(app);
            AssertPortIsSafe(port);

            return new InteractiveAppAdmissionHost(app, tempRoot, port, nonLoopback);
        }

        /// <summary>
        /// The things that stand in for "the interactive application" and "the Blazor circuit",
        /// plus a test-only sign-in.
        /// </summary>
        private static void MapTerminals(WebApplication app)
        {
            // Test-only sign-in. Under /auth on purpose: the gate's whole promise is that the
            // sign-in path stays reachable from a non-loopback origin, so this exercises that
            // promise rather than working around it. It mints the same cookie, on the same scheme,
            // through the same handler that SqlTriageAuth.IssueSessionCookie uses.
            app.MapGet(TestSignInPath, async (HttpContext ctx) =>
            {
                var identity = new ClaimsIdentity(
                    new[]
                    {
                        new Claim(SqlTriageAuthClaims.IdentityKey, "probe@example.test"),
                        new Claim(ClaimTypes.Name, "Admission Probe"),
                        new Claim(ClaimTypes.Role, SQLTriage.Data.Models.AppRoles.Viewer),
                        new Claim(SqlTriageAuthClaims.Provider, "test"),
                    },
                    CookieAuthenticationDefaults.AuthenticationScheme);

                await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
                return Results.Content("signed-in", "text/plain");
            });

            // The liveness probe, mapped exactly as both production hosts map it, so the
            // "monitoring still works from the LAN" assertion is about the real endpoint rather
            // than about the fallback answering in its place.
            app.MapGet("/_server/health", () => Results.Ok(new { status = "ok" }));

            // The Blazor circuit, both halves. Refusing only the document leaves a hand-written
            // client able to open a circuit; refusing only the socket leaves a prerendered DOM on
            // the wire. Both are driven, and both must be refused.
            app.MapPost("/_blazor/negotiate", () => Results.Content(BlazorCircuitMarker, "application/json"));
            app.MapGet("/_blazor", () => Results.Content(BlazorCircuitMarker, "text/plain"));

            // Every other path: the document. Rendered around the REAL BoundaryCanary component —
            // the two ungated shapes that defeated round 7 — so a response that reaches here
            // genuinely carries an ungated shell control, not a string pretending to be one.
            app.MapFallback(async (HttpContext ctx) =>
            {
                var canary = await RenderCanaryAsync();
                await ctx.Response.WriteAsync(
                    "<!DOCTYPE html><html><head><title>" + InteractiveAppMarker + "</title></head><body>"
                    + "<div id=\"app\">" + canary + "</div>"
                    + "<script src=\"_framework/blazor.web.js\"></script>"
                    + "</body></html>");
            });
        }

        /// <summary>
        /// Renders <c>Components/Shared/BoundaryCanary.razor</c> with the real Blazor HTML
        /// renderer. If the renderer ever cannot run the component the test must NOT quietly fall
        /// back to a literal string — a terminal that stopped carrying the canary would make every
        /// "the marker is absent" assertion pass for the wrong reason.
        /// </summary>
        private static async Task<string> RenderCanaryAsync()
        {
            var services = _rendererServices.Value;
            var loggerFactory = services.GetRequiredService<ILoggerFactory>();
            await using var renderer = new HtmlRenderer(services, loggerFactory);

            var html = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var output = await renderer.RenderComponentAsync<SQLTriage.Components.Shared.BoundaryCanary>();
                return output.ToHtmlString();
            });

            if (!html.Contains(SQLTriage.Components.Shared.BoundaryCanary.DomMarker, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "BoundaryCanary rendered without its DomMarker. The terminal is no longer serving an "
                    + "ungated shell control, so every absence assertion in InteractiveAppAdmissionTests "
                    + "would pass vacuously. Fix the canary before trusting the suite.");

            return html;
        }

        /// <summary>
        /// A minimal container for the HTML renderer. BoundaryCanary injects nothing, but its
        /// EditForm asks the <see cref="NavigationManager"/> for a form action, and the one the
        /// server-components registration supplies is a <c>RemoteNavigationManager</c> that is only
        /// initialised inside a live circuit.
        /// </summary>
        private static readonly Lazy<IServiceProvider> _rendererServices = new(() =>
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<NavigationManager, AdmissionTestNavigationManager>();
            return services.BuildServiceProvider();
        });

        private sealed class AdmissionTestNavigationManager : NavigationManager
        {
            public AdmissionTestNavigationManager() => Initialize("http://localhost/", "http://localhost/");

            protected override void NavigateToCore(string uri, bool forceLoad) { }
        }

        // ── Origins ──────────────────────────────────────────────────────

        /// <summary>
        /// A real non-loopback IPv4 address of this machine. Connecting to it from this same
        /// machine still presents that address as <c>Connection.RemoteIpAddress</c>, which is what
        /// makes it a genuine second origin class rather than a simulated one.
        ///
        /// <para>Never silently skipped. A test that cannot reach the second origin has not tested
        /// anything, and this lane has already shipped one "ALL CHECKS PASSED" that came from steps
        /// that never ran.</para>
        /// </summary>
        internal static IPAddress FindNonLoopbackIPv4()
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Where(a => !IPAddress.IsLoopback(a))
                .Where(a => !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .ToList();

            if (candidates.Count == 0)
                throw new InvalidOperationException(
                    "No non-loopback IPv4 address on this machine, so the second origin class cannot be "
                    + "driven and the boundary cannot be tested. This test refuses to pass without it.");

            return candidates[0];
        }

        private static int ResolvePort(WebApplication app)
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                            ?? throw new InvalidOperationException("Kestrel exposed no server addresses feature.");

            foreach (var address in addresses.Addresses)
            {
                var parsed = BindingAddressPort(address);
                if (parsed > 0) return parsed;
            }

            throw new InvalidOperationException(
                "Kestrel reported no bound address: " + string.Join(", ", addresses.Addresses));
        }

        private static int BindingAddressPort(string address)
        {
            var colon = address.LastIndexOf(':');
            if (colon < 0) return 0;
            var tail = address[(colon + 1)..].TrimEnd('/');
            return int.TryParse(tail, out var port) ? port : 0;
        }

        /// <summary>
        /// The ports this box is not allowed to take: the operator's own host (5150/5151) and the
        /// installed production service (5155/5156). An ephemeral bind cannot land on one, but a
        /// test that binds a live service's port would be a genuine incident, so it is asserted
        /// rather than assumed.
        /// </summary>
        private static void AssertPortIsSafe(int port)
        {
            int[] forbidden = { 5150, 5151, 5155, 5156 };
            if (forbidden.Contains(port))
                throw new InvalidOperationException(
                    $"The test host bound port {port}, which belongs to a live SQLTriage listener on this "
                    + "machine. Refusing to run.");
        }

        // ── Clients ──────────────────────────────────────────────────────

        internal static HttpClient AnonymousClient(string baseAddress) =>
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

        // ── The app's own routes, by reflection ──────────────────────────

        /// <summary>
        /// Every routable page in the shipped assembly, read from <see cref="RouteAttribute"/> on
        /// the compiled component types.
        ///
        /// <para>Reflection over the BUILT assembly rather than a scan of <c>@page</c> lines: the
        /// point of this round is that component source is not the instrument. This only decides
        /// WHICH urls to drive; every assertion is made against a real HTTP response.</para>
        /// </summary>
        internal static IReadOnlyList<string> AppRouteTemplates()
        {
            var assembly = typeof(SQLTriage.Components.Layout.MainLayout).Assembly;

            Type?[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }

            var templates = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var type in types)
            {
                if (type == null) continue;
                if (!typeof(IComponent).IsAssignableFrom(type)) continue;

                foreach (var route in type.GetCustomAttributes<RouteAttribute>(inherit: false))
                    if (!string.IsNullOrWhiteSpace(route.Template))
                        templates.Add(route.Template);
            }

            return templates.ToList();
        }

        /// <summary>
        /// A concrete url for a route template — <c>{id}</c> and <c>{id:int}</c> become <c>1</c>,
        /// and an optional segment is simply dropped. The gate never looks at the path beyond its
        /// allow-list prefixes, so the substituted value is immaterial; what matters is that a real
        /// request is made for every route the app publishes.
        /// </summary>
        internal static string Concretise(string template)
        {
            var parts = template.Split('/', StringSplitOptions.None)
                .Select(segment =>
                {
                    if (!segment.StartsWith("{", StringComparison.Ordinal)) return segment;
                    if (segment.Contains('?')) return "";          // optional parameter
                    return "1";
                })
                .Where((segment, index) => index == 0 || segment.Length > 0);

            var url = string.Join("/", parts);
            if (url.Length == 0) url = "/";
            if (!url.StartsWith("/", StringComparison.Ordinal)) url = "/" + url;
            return url;
        }

        public async ValueTask DisposeAsync()
        {
            try { await _app.StopAsync(); } catch { /* the test is over either way */ }
            try { await _app.DisposeAsync(); } catch { }
            try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); } catch { }
        }
    }
}
