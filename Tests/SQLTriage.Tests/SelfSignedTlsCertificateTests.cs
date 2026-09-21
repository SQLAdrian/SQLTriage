/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    // BM:SelfSignedTlsCertificateTests — the HTTPS listener has to complete a real handshake
    /// <summary>
    /// The HTTPS listener, driven rather than asserted.
    ///
    /// <para><b>What was wrong.</b> Both browser-facing hosts built their self-signed certificate
    /// with <c>MachineKeySet | EphemeralKeySet</c>, a combination SChannel cannot build server
    /// credentials from, and then logged <c>"Service HTTPS configured on port {n} with ephemeral
    /// certificate"</c> on the strength of having CONSTRUCTED the certificate. The installed
    /// service had printed that line at every startup since it was installed;
    /// <c>https://127.0.0.1:5156/</c> had never completed a handshake (curl exit 000,
    /// <c>openssl s_client</c> → <i>unexpected eof while reading</i>) while
    /// <c>http://127.0.0.1:5155/</c> answered 200. It failed CLOSED — a listener that cannot
    /// handshake admits nobody — so this was a dead endpoint and a false log line, not an
    /// exposure. Both halves are the defect.</para>
    ///
    /// <para><b>Why a handshake and not an assertion about flags.</b> A test that checks
    /// <see cref="SelfSignedTlsCertificate.KeyStorageFlags"/> equals some constant would have
    /// passed for the whole life of the bug — the flags were exactly what the author intended.
    /// The only instrument that can tell a working certificate from a decorative one is a
    /// completed handshake, so that is the instrument: an in-process TLS server (below) and then
    /// a real Kestrel listener with a real <see cref="HttpClient"/> over it.</para>
    ///
    /// <para><b>The red half, measured.</b> Restore <c>EphemeralKeySet</c> to
    /// <see cref="SelfSignedTlsCertificate.KeyStorageFlags"/> and every test in this class fails:
    /// the SslStream ones with <c>AuthenticationException: Authentication failed because the
    /// platform does not support ephemeral keys</c>, the Kestrel one because
    /// <see cref="SelfSignedTlsCertificate.CreateVerified"/> returns null so no HTTPS listener is
    /// bound at all. That last consequence is the point of the design: a certificate that cannot
    /// serve TLS produces no listener and no claim, instead of a listener nobody can reach and a
    /// log line saying they can.</para>
    /// </summary>
    public class SelfSignedTlsCertificateTests
    {
        // ── The certificate itself ───────────────────────────────────────

        [Fact]
        public void TheGeneratedCertificateCompletesARealServerSideHandshake()
        {
            using var cert = SelfSignedTlsCertificate.Create("SQLTriage Test");

            var protocol = HandshakeOnce(cert);

            Assert.True(protocol != SslProtocols.None,
                "The generated certificate did not negotiate a TLS protocol. This is the exact failure the "
                + "shipped hosts had: a certificate that constructs cleanly and cannot serve.");
        }

        [Fact]
        public void CreateVerifiedReturnsACertificateAndSaysWhatItMeasured()
        {
            using var cert = SelfSignedTlsCertificate.CreateVerified("SQLTriage Test", out var detail);

            Assert.NotNull(cert);

            // The detail string is what the hosts print. It must name the measurement, because the
            // whole defect was a log line that named an intention.
            Assert.Contains(cert!.Thumbprint, detail, StringComparison.Ordinal);
            Assert.Contains("handshake completed", detail, StringComparison.Ordinal);
        }

        /// <summary>
        /// The flags are pinned — not as the test of correctness (the handshake above is that),
        /// but so that reintroducing the fatal one is a visible diff rather than a silent
        /// regression on a machine where nobody happens to open the HTTPS port.
        /// </summary>
        [Fact]
        public void TheKeyStorageFlagsExcludeEphemeralAndUseTheMachineStore()
        {
            var flags = SelfSignedTlsCertificate.KeyStorageFlags;

            Assert.False(flags.HasFlag(X509KeyStorageFlags.EphemeralKeySet),
                "EphemeralKeySet keeps the private key in process memory with no key container behind it, "
                + "and SChannel builds server credentials from a container handle. Measured on this "
                + "platform: every combination containing it fails the handshake.");

            Assert.True(flags.HasFlag(X509KeyStorageFlags.MachineKeySet),
                "The installed service runs as NT SERVICE\\SQLTriage, a virtual account with no user "
                + "profile, so a user key set has nowhere to live. MachineKeySet writes under "
                + "C:\\ProgramData\\Microsoft\\Crypto, whose ACL is Everyone:(R,W).");

            Assert.False(flags.HasFlag(X509KeyStorageFlags.UserKeySet),
                "UserKeySet passes on a developer's interactive session and is precisely the flag that "
                + "would fail where this actually runs.");

            Assert.False(flags.HasFlag(X509KeyStorageFlags.PersistKeySet),
                "Without PersistKeySet the key container is removed when the certificate is released, so "
                + "a service in a restart loop cannot accumulate key material on disk.");
        }

        /// <summary>
        /// The negative control. Without it, "the handshake succeeded" proves only that this test
        /// can pass — not that it can fail for the reason the lane says it failed.
        /// </summary>
        [Fact]
        public void AnEphemeralKeyCertificateIsExactlyWhatCannotHandshakeHere()
        {
            using var ephemeral = BuildWithFlags(
                X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.EphemeralKeySet);

            var ex = Assert.ThrowsAny<Exception>(() => HandshakeOnce(ephemeral));

            var described = SelfSignedTlsCertificate.Describe(ex);
            Assert.Contains("ephemeral", described, StringComparison.OrdinalIgnoreCase);
        }

        // ── The shipped path, through a real Kestrel listener ────────────

        /// <summary>
        /// End to end: <see cref="SelfSignedTlsCertificate.CreateVerified"/> → Kestrel
        /// <c>UseHttps</c> → a real HTTPS request over the socket. This is the assertion the
        /// installed service's log line was making and could not have supported.
        /// </summary>
        [Fact]
        public async Task KestrelServesHttpsWithTheCertificateTheHostsBind()
        {
            var cert = SelfSignedTlsCertificate.CreateVerified("SQLTriage Test", out var detail);
            Assert.True(cert != null, "CreateVerified refused the certificate: " + detail);

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();

            // Loopback and an OS-chosen port. The operator's host owns 5150/5151 on this box and
            // the installed service owns 5155/5156; AssertPortIsSafe re-checks below.
            builder.WebHost.ConfigureKestrel(k =>
                k.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(cert!)));

            var app = builder.Build();
            app.MapGet("/", () => Results.Text("tls-ok"));

            await app.StartAsync();
            try
            {
                var port = ResolvePort(app);
                AssertPortIsSafe(port);

                using var handler = new HttpClientHandler
                {
                    // A self-signed certificate does not chain and never will. The property under
                    // test is that the handshake COMPLETES, not that the certificate is trusted.
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

                var response = await client.GetAsync($"https://127.0.0.1:{port}/");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("tls-ok", await response.Content.ReadAsStringAsync());
            }
            finally
            {
                await app.StopAsync();
                await app.DisposeAsync();
                cert!.Dispose();
            }
        }

        // ── The log lines the hosts print ────────────────────────────────

        /// <summary>
        /// Neither host may claim HTTPS on the strength of having constructed a certificate.
        ///
        /// <para>Source-scanned, because the claim is a LOG LINE and no runtime assertion reaches
        /// it: the two hosts build real containers over real SQL dependencies. The property is
        /// narrow and checkable — the string that announces HTTPS must sit downstream of
        /// <c>CreateVerified</c>, and the words that made the old line false ("configured", with no
        /// measurement behind it) must not be what announces it.</para>
        /// </summary>
        [Theory]
        [InlineData("Data/Services/WindowsServiceHost.cs")]
        [InlineData("Data/Services/ServerModeService.cs")]
        public void NeitherHostClaimsHttpsWithoutVerifyingIt(string relativePath)
        {
            var text = ReadRepoFile(relativePath);

            // Comment lines are excluded on purpose: both hosts NAME the old flags and the old log
            // line in the comment explaining why they are gone, and a scan that could not tell a
            // record of a defect from the defect would forbid writing the record down.
            var codeLines = text
                .Split('\n')
                .Where(line =>
                {
                    var t = line.TrimStart();
                    return !t.StartsWith("//", StringComparison.Ordinal)
                        && !t.StartsWith("*", StringComparison.Ordinal);
                })
                .ToList();
            var code = string.Join("\n", codeLines);

            Assert.DoesNotContain("EphemeralKeySet", code, StringComparison.Ordinal);
            Assert.DoesNotContain("GenerateSelfSignedCertificate", code, StringComparison.Ordinal);
            Assert.Contains("SelfSignedTlsCertificate.CreateVerified", code, StringComparison.Ordinal);

            // Every line that announces an HTTPS listener has to be downstream of the verification.
            // The old wording — "HTTPS configured on port … with ephemeral certificate" — asserted
            // a working endpoint on the strength of a constructor call.
            var announcements = codeLines
                .Where(line => line.Contains("HTTPS configured", StringComparison.OrdinalIgnoreCase)
                            || line.Contains("configured to listen on HTTPS", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.True(announcements.Count == 0,
                relativePath + " still announces HTTPS with wording that asserts more than it measured:\n  "
                + string.Join("\n  ", announcements.Select(l => l.Trim())));
        }

        // ── Helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// The same shape as <see cref="SelfSignedTlsCertificate.Create"/> but with caller-chosen
        /// storage flags, so the negative control builds the FATAL combination the hosts shipped
        /// rather than describing it.
        /// </summary>
        private static X509Certificate2 BuildWithFlags(X509KeyStorageFlags flags)
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddIpAddress(IPAddress.Loopback);

            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=SQLTriage Test (negative control)", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            request.CertificateExtensions.Add(san.Build());

            using var created = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

            var pfx = created.Export(X509ContentType.Pfx);
            try { return X509CertificateLoader.LoadPkcs12(pfx, (string?)null, flags); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }

        /// <summary>
        /// One server-side TLS handshake over a loopback socket, returning the negotiated
        /// protocol. Throws whatever SChannel throws — which is the whole point for the negative
        /// control.
        /// </summary>
        private static SslProtocols HandshakeOnce(X509Certificate2 cert)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;

                var server = Task.Run(async () =>
                {
                    using var connection = await listener.AcceptTcpClientAsync(cts.Token);
                    await using var ssl = new SslStream(connection.GetStream(), false);
                    await ssl.AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions { ServerCertificate = cert },
                        cts.Token);
                    return ssl.SslProtocol;
                }, cts.Token);

                var client = Task.Run(async () =>
                {
                    using var tcp = new TcpClient();
                    await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
                    await using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
                    await ssl.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions { TargetHost = "localhost" }, cts.Token);
                }, cts.Token);

                try
                {
                    return server.GetAwaiter().GetResult();
                }
                finally
                {
                    try { client.GetAwaiter().GetResult(); } catch { /* the server side is the assertion */ }
                }
            }
            finally
            {
                listener.Stop();
                listener.Dispose();
            }
        }

        private static int ResolvePort(WebApplication app)
        {
            var addresses = app.Services.GetService(typeof(IServer)) is IServer server
                ? server.Features.Get<IServerAddressesFeature>()
                : null;

            var address = addresses?.Addresses.FirstOrDefault()
                          ?? throw new InvalidOperationException("Kestrel reported no bound address.");

            var colon = address.LastIndexOf(':');
            return int.Parse(address[(colon + 1)..].TrimEnd('/'));
        }

        /// <summary>
        /// The ports this box is not allowed to take: the operator's own host (5150/5151) and the
        /// installed production service (5155/5156). An ephemeral bind cannot land on one, but a
        /// test that bound a live listener's port would be a genuine incident.
        /// </summary>
        private static void AssertPortIsSafe(int port)
        {
            int[] forbidden = { 5150, 5151, 5155, 5156 };
            Assert.DoesNotContain(port, forbidden);
        }

        private static string ReadRepoFile(string relative)
        {
            var path = Path.Combine(RawPassedScan.RepoRoot().FullName,
                relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), relative + " not found — the scan cannot run and must fail.");
            return File.ReadAllText(path);
        }
    }
}
