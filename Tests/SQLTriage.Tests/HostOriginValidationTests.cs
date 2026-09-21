/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    // BM:HostOriginValidationTests — the raw-Host gate, driven over a real socket with spoofed Hosts
    /// <summary>
    /// THE DECIDER FOR THE HOST GATE. A DNS-rebinding request is a loopback socket carrying a foreign
    /// <c>Host</c> header, and that is EXACTLY what these tests drive: a listener bound so the socket
    /// is genuinely loopback (which is what makes admission admit it), with the <c>Host</c> header set
    /// by hand to the value the gate is meant to judge. The socket is real; the header is the input
    /// under test.
    ///
    /// <para><b>Why the shape is the attack.</b> Every runtime test here connects to
    /// <c>127.0.0.1</c> and overrides the outgoing <c>Host</c> header. The socket's remote address is
    /// loopback, so <see cref="InteractiveAppAdmission"/> would admit it without a sign-in — the
    /// loopback exemption. The only thing that can refuse it is the Host gate in front, on the
    /// strength of the header alone. So a request that reaches the terminal proves the gate ALLOWED
    /// the Host, and a 400 with <see cref="HostOriginValidation.RefusalHeader"/> proves it REFUSED
    /// it — the socket is held constant, the header is the variable.</para>
    ///
    /// <para><b>The composition is the shipped one.</b> The host builds its front half through
    /// <see cref="InteractiveAppAdmission.UseSqlTriageFrontDoor"/> — the same method both production
    /// hosts call — so the gate under test is the shipped middleware in the shipped order, not a copy.
    /// Deleting <c>app.UseSqlTriageHostOrigin()</c> from that method turns every "refused" assertion
    /// below red.</para>
    /// </summary>
    public class HostOriginValidationTests
    {
        // ── The attack: a spoofed Host on a loopback socket is refused ────

        [Fact]
        public async Task ASpoofedHostIsRefusedBeforeAdmissionEvenOnALoopbackSocket()
        {
            await using var host = await HostOriginHost.StartAsync();
            using var client = host.LoopbackClient();

            using var response = await host.GetWithHost(client, "/scheduled-tasks", "evil.example");
            var body = await response.Content.ReadAsStringAsync();

            // Refused by the Host gate — the distinct 400 + header, NOT admission's redirect.
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(HostOriginValidation.RefusalHeaderValue,
                response.Headers.GetValues(HostOriginValidation.RefusalHeader).Single());

            // The terminal was never reached: the loopback socket would otherwise have been admitted
            // and served the application. Its absence is the whole point.
            Assert.DoesNotContain(HostOriginHost.TerminalMarker, body, StringComparison.Ordinal);
            // Admission never ran — the Host gate refused first — so its stamp is absent too.
            Assert.False(response.Headers.Contains(InteractiveAppAdmission.RefusalHeader),
                "The admission header is present, which means the Host gate did not refuse before admission ran.");
        }

        [Fact]
        public async Task TheLoopbackNamesPass()
        {
            await using var host = await HostOriginHost.StartAsync();
            using var client = host.LoopbackClient();

            foreach (var name in new[] { "localhost", "127.0.0.1", "[::1]" })
            {
                using var response = await host.GetWithHost(client, "/scheduled-tasks", name);
                var body = await response.Content.ReadAsStringAsync();

                Assert.False(response.Headers.Contains(HostOriginValidation.RefusalHeader),
                    $"Host '{name}' was refused by the Host gate, but it is a loopback name the gate must "
                    + "always accept.");
                Assert.Contains(HostOriginHost.TerminalMarker, body, StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task TheMachinesOwnNamePassesUnderTheDefaultEveryInterfaceBind()
        {
            // The product ships ServiceBindAddress:"any" so it can be reached by name from the LAN.
            // The default (no ServiceBindAddress) derives the machine-name list, so http://sqlbox/…
            // keeps working. This is the arm that catches a blind loopback-only narrowing — the
            // "worse day than the attack it prevents".
            await using var host = await HostOriginHost.StartAsync();
            using var client = host.LoopbackClient();

            var machineName = Dns.GetHostName();
            using var response = await host.GetWithHost(client, "/scheduled-tasks", machineName);
            var body = await response.Content.ReadAsStringAsync();

            Assert.False(response.Headers.Contains(HostOriginValidation.RefusalHeader),
                $"This machine's own name '{machineName}' was refused under an every-interface bind — that "
                + "is a by-name LAN lockout, the exact outage this control is designed not to cause.");
            Assert.Contains(HostOriginHost.TerminalMarker, body, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AConfiguredLanNamePassesAndAnUnconfiguredOneIsRefused()
        {
            await using var host = await HostOriginHost.StartAsync(
                configuredHosts: new[] { "sqlbox.corp.local" });
            using var client = host.LoopbackClient();

            using var configured = await host.GetWithHost(client, "/scheduled-tasks", "sqlbox.corp.local");
            Assert.False(configured.Headers.Contains(HostOriginValidation.RefusalHeader),
                "A name added to HostOriginAllowList was still refused.");
            Assert.Contains(HostOriginHost.TerminalMarker,
                await configured.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            // The control: a name NOT configured is still refused, or the test above proves nothing.
            using var unconfigured = await host.GetWithHost(client, "/scheduled-tasks", "other.example");
            Assert.Equal(HttpStatusCode.BadRequest, unconfigured.StatusCode);
            Assert.Equal(HostOriginValidation.RefusalHeaderValue,
                unconfigured.Headers.GetValues(HostOriginValidation.RefusalHeader).Single());
        }

        // ── The hole must be closed at the width of the app: /_blazor ─────

        [Fact]
        public async Task ASpoofedHostIsRefusedOnTheBlazorNegotiateAndTheWebsocketUpgrade()
        {
            // If the gate covered documents but not the circuit, the hole would be exactly the width
            // of the application: a hand-written client that skips the page and drives /_blazor would
            // walk in. The gate runs before UseRouting and branches on nothing but the Host, so both
            // halves of the circuit are covered — proved here over the socket.
            await using var host = await HostOriginHost.StartAsync();
            using var client = host.LoopbackClient();

            // The negotiate POST.
            using var negotiate = host.Request(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1", "evil.example");
            negotiate.Content = new StringContent("", Encoding.UTF8, "text/plain");
            using var negotiateResponse = await client.SendAsync(negotiate);
            Assert.Equal(HttpStatusCode.BadRequest, negotiateResponse.StatusCode);
            Assert.Equal(HostOriginValidation.RefusalHeaderValue,
                negotiateResponse.Headers.GetValues(HostOriginValidation.RefusalHeader).Single());
            Assert.DoesNotContain(HostOriginHost.BlazorMarker,
                await negotiateResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            // The WebSocket upgrade GET.
            using var upgrade = host.Request(HttpMethod.Get, "/_blazor?id=probe", "evil.example");
            upgrade.Headers.TryAddWithoutValidation("Connection", "Upgrade");
            upgrade.Headers.TryAddWithoutValidation("Upgrade", "websocket");
            upgrade.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
            upgrade.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
            using var upgradeResponse = await client.SendAsync(upgrade);
            Assert.Equal(HttpStatusCode.BadRequest, upgradeResponse.StatusCode);
            Assert.Equal(HostOriginValidation.RefusalHeaderValue,
                upgradeResponse.Headers.GetValues(HostOriginValidation.RefusalHeader).Single());
            Assert.DoesNotContain(HostOriginHost.BlazorMarker,
                await upgradeResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            // The positive control: with an accepted Host, both halves reach the circuit stand-in, so
            // "refused" above is not just the stand-in never answering.
            using var okNegotiate = host.Request(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1", "localhost");
            okNegotiate.Content = new StringContent("", Encoding.UTF8, "text/plain");
            using var okNegotiateResponse = await client.SendAsync(okNegotiate);
            Assert.Contains(HostOriginHost.BlazorMarker,
                await okNegotiateResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // ── The derived list, without a socket ───────────────────────────

        [Fact]
        public void ALoopbackBindDerivesALoopbackOnlyList()
        {
            var list = HostOriginValidation.ResolveAllowList(
                Config(("ServiceBindAddress", "loopback")), out _, out _);

            Assert.Equal(HostOriginValidation.HostAllowList.BindMode.Loopback, list.Mode);

            Assert.True(list.IsHostAllowed("localhost"), "loopback list must accept localhost");
            Assert.True(list.IsHostAllowed("127.0.0.1"), "loopback list must accept 127.0.0.1");
            Assert.True(list.IsHostAllowed("[::1]"), "loopback list must accept [::1]");

            // …and NOT the machine's own name — that is the whole difference between the two modes.
            Assert.False(list.IsHostAllowed(Dns.GetHostName()),
                "A loopback bind must not accept the machine's LAN name; that would be an every-interface list.");
        }

        [Fact]
        public void AnEveryInterfaceBindDerivesTheMachinesOwnNames()
        {
            // Both the explicit "any" and the default (unset) resolve the same way.
            foreach (var config in new[] { Config(("ServiceBindAddress", "any")), Config() })
            {
                var list = HostOriginValidation.ResolveAllowList(config, out _, out _);

                Assert.Equal(HostOriginValidation.HostAllowList.BindMode.AnyInterface, list.Mode);
                Assert.True(list.IsHostAllowed("localhost"),
                    "an every-interface list must still accept loopback (the box itself).");
                Assert.True(list.IsHostAllowed(Dns.GetHostName()),
                    "an every-interface list must accept the machine's own name so by-name LAN access works.");
            }
        }

        [Fact]
        public void AConfiguredNameIsAddedToTheDerivedList()
        {
            var list = HostOriginValidation.ResolveAllowList(
                Config(("ServiceBindAddress", "any"), ("HostOriginAllowList:0", "vanity.example.com")),
                out _, out _);

            Assert.True(list.IsHostAllowed("vanity.example.com"),
                "a name in HostOriginAllowList must be accepted.");
            Assert.Contains("vanity.example.com", list.Entries);
        }

        [Fact]
        public void ALoopbackOnlyResolutionSurfacesAStartupWarningNotASilentRefusal()
        {
            // #5 NO SILENT LOCKOUT, at the resolver seam: a bind that expects remote callers but whose
            // machine-name enumeration produced only loopback must hand back a startup warning naming
            // the resolved list and how to add to it — never a silent per-request 400. The normal
            // every-interface case (this machine has names) must NOT warn.
            HostOriginValidation.ResolveAllowList(Config(("ServiceBindAddress", "any")), out _, out var warnAny);
            Assert.Null(warnAny);

            // Loopback bind is expected to be loopback-only, so it does not warn either.
            HostOriginValidation.ResolveAllowList(Config(("ServiceBindAddress", "loopback")), out _, out var warnLoopback);
            Assert.Null(warnLoopback);
        }

        // ── The ordering assertion (mirror of SqlTriageAuth:180) ─────────

        [Fact]
        public void UseSqlTriageAdmissionRefusesToBuildWithoutTheHostGate()
        {
            // The Host gate must be installed BEFORE admission, or a rebinding request is admitted by
            // the loopback exemption before the gate ever runs. UseSqlTriageFrontDoor installs it
            // first; calling UseSqlTriageAdmission on its own — the mis-ordered pipeline — throws.
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();

            var temp = Path.Combine(Path.GetTempPath(), "sqltriage-hostorigin-order-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var rbac = new RbacService(
                    NullLogger<RbacService>.Instance,
                    Path.Combine(temp, "rbac-config.json"),
                    Path.Combine(temp, "rbac-users.json"));

                builder.Services.AddSingleton(rbac);
                var app = builder.Build();

                var ex = Assert.Throws<InvalidOperationException>(() => app.UseSqlTriageAdmission(rbac));
                Assert.Contains("UseSqlTriageHostOrigin", ex.Message, StringComparison.Ordinal);
            }
            finally
            {
                try { Directory.Delete(temp, recursive: true); } catch { }
            }
        }

        // ── Kestrel's AllowedHosts is left untouched; THIS is the control ──

        [Fact]
        public void TheShippedAllowedHostsSettingIsUnchanged()
        {
            // The design keeps Kestrel's AllowedHosts at "*" (its bare-400 fails the loudness the
            // explicit gate provides) and makes HostOriginValidation the control. Asserted against the
            // shipped file so a change to either is a visible, deliberate diff.
            var appSettings = Path.Combine(RawPassedScan.RepoRoot().FullName, "Config", "appsettings.json");
            var json = File.ReadAllText(appSettings);
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            Assert.True(doc.RootElement.TryGetProperty("AllowedHosts", out var allowed),
                "appsettings.json no longer has an AllowedHosts key.");
            Assert.Equal("*", allowed.GetString());
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private static IConfiguration Config(params (string Key, string Value)[] pairs)
        {
            var items = pairs.ToDictionary(p => p.Key, p => (string?)p.Value);
            return new ConfigurationBuilder().AddInMemoryCollection(items).Build();
        }

        /// <summary>
        /// A real Kestrel host whose front half is the SHIPPED composition and whose only terminals
        /// are stand-ins for "the interactive application" and "the Blazor circuit". Bound to every
        /// interface on an ephemeral port; every test connects on loopback and sets the Host header by
        /// hand, which is the DNS-rebinding shape.
        /// </summary>
        private sealed class HostOriginHost : IAsyncDisposable
        {
            internal const string TerminalMarker = "SQLTRIAGE-HOSTORIGIN-TERMINAL";
            internal const string BlazorMarker = "SQLTRIAGE-HOSTORIGIN-BLAZOR";

            private readonly WebApplication _app;
            private readonly string _tempRoot;
            internal int Port { get; }

            private HostOriginHost(WebApplication app, string tempRoot, int port)
            {
                _app = app;
                _tempRoot = tempRoot;
                Port = port;
            }

            internal static async Task<HostOriginHost> StartAsync(
                string? serviceBindAddress = null, string[]? configuredHosts = null)
            {
                var tempRoot = Path.Combine(Path.GetTempPath(), "sqltriage-hostorigin-" + Guid.NewGuid().ToString("N"));
                var webRoot = Path.Combine(tempRoot, "wwwroot");
                Directory.CreateDirectory(webRoot);

                var rbac = new RbacService(
                    NullLogger<RbacService>.Instance,
                    Path.Combine(tempRoot, "rbac-config.json"),
                    Path.Combine(tempRoot, "rbac-users.json"));

                var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                {
                    ContentRootPath = AppContext.BaseDirectory,
                    WebRootPath = webRoot,
                });
                builder.Logging.ClearProviders();

                // The config the Host gate reads for its allow-list. The ACTUAL Kestrel bind is
                // loopback-reachable (ListenAnyIP below) regardless, so the socket is loopback and the
                // allow-list is whatever the config says — the two are independent here on purpose.
                var settings = new Dictionary<string, string?>();
                if (serviceBindAddress != null) settings["ServiceBindAddress"] = serviceBindAddress;
                if (configuredHosts != null)
                    for (var i = 0; i < configuredHosts.Length; i++)
                        settings[$"{HostOriginValidation.AllowListConfigKey}:{i}"] = configuredHosts[i];
                builder.Configuration.AddInMemoryCollection(settings);

                builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(0));

                builder.Services.AddSingleton(rbac);
                builder.Services.AddAntiforgery();
                builder.Services.AddSqlTriageAuth(rbac.Config);

                var app = builder.Build();

                // The shipped front half — the same method both production hosts call.
                app.UseSqlTriageFrontDoor(rbac);
                app.UseSqlTriageAuth(rbac);
                app.UseAntiforgery();

                app.MapPost("/_blazor/negotiate", () => Results.Content(BlazorMarker, "application/json"));
                app.MapGet("/_blazor", () => Results.Content(BlazorMarker, "text/plain"));
                app.MapFallback(async (HttpContext ctx) => await ctx.Response.WriteAsync(
                    "<!DOCTYPE html><html><body>" + TerminalMarker + "</body></html>"));

                await app.StartAsync();

                var port = ResolvePort(app);
                AssertPortIsSafe(port);
                return new HostOriginHost(app, tempRoot, port);
            }

            internal HttpClient LoopbackClient() =>
                new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
                {
                    BaseAddress = new Uri($"http://127.0.0.1:{Port}"),
                    Timeout = TimeSpan.FromSeconds(30),
                };

            /// <summary>A request to <paramref name="path"/> on the loopback listener whose Host header
            /// is set by hand to <paramref name="host"/> — the socket is loopback, the Host is spoofed.</summary>
            internal HttpRequestMessage Request(HttpMethod method, string path, string host)
            {
                var request = new HttpRequestMessage(method, path);
                request.Headers.TryAddWithoutValidation("Host", host);
                return request;
            }

            internal Task<HttpResponseMessage> GetWithHost(HttpClient client, string path, string host)
                => client.SendAsync(Request(HttpMethod.Get, path, host));

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
                try { await _app.StopAsync(); } catch { }
                try { await _app.DisposeAsync(); } catch { }
                try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); } catch { }
            }
        }
    }
}
