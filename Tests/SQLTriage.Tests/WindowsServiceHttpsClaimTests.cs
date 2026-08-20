/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    // BM:WindowsServiceHttpsClaimTests — the startup line may claim only what was measured
    /// <summary>
    /// Round 12, item 3. The startup line's <c>(HTTPS: …)</c> suffix must be conditioned on the
    /// BOUND ENDPOINT serving TLS, not on the host holding a certificate object.
    ///
    /// <para><b>The defect class, for the sixth time in this arc.</b> A sentence beside a verdict
    /// has to be conditioned on the same measurement as the verdict. Round 10 found the installed
    /// service logging <c>"Service HTTPS configured on port 5156 with ephemeral certificate"</c> at
    /// every startup for the whole time it had been installed, while <c>:5156</c> had never
    /// completed a handshake. Round 11 fixed the certificate and made the claim depend on
    /// <see cref="SelfSignedTlsCertificate.CreateVerified"/> — a real handshake, which is right, and
    /// which proves something about the PLATFORM and the KEY on a socket the helper stands up
    /// itself. It proves nothing about whether Kestrel then bound the port, bound it with that
    /// certificate, or bound it at all. The suffix was still one layer ahead of its evidence.</para>
    ///
    /// <para><b><c>IServerAddressesFeature</c> was measured and rejected as the instrument:</b> it
    /// reports the endpoint Kestrel was configured with, whether or not anything serves there.
    /// Only a connection tells the two apart, so a connection is what is driven — here, and in
    /// <see cref="WindowsServiceHost"/> from <c>ApplicationStarted</c>.</para>
    /// </summary>
    public class WindowsServiceHttpsClaimTests
    {
        /// <summary>The live listeners on this box. A test that bound one would be an incident.</summary>
        private static readonly int[] ForbiddenPorts = { 5150, 5151, 5155, 5156 };

        // ── The required case: a certificate exists, the endpoint cannot serve ──

        /// <summary>
        /// THE ASSERTION ITEM 3 ASKS FOR. A perfectly good certificate object is in hand — verified
        /// by a real handshake, exactly the state that satisfied the round-11 condition — and the
        /// bound port accepts a socket and cannot complete a handshake. No HTTPS claim may be made.
        ///
        /// <para>A listener that accepts and closes is not a contrived shape: it is what the
        /// installed service's <c>:5156</c> did for the whole time it was live
        /// (<c>curl</c> exit 000, <c>openssl s_client</c> → <i>unexpected eof while reading</i>).</para>
        /// </summary>
        [Fact]
        public void ADeadEndpointProducesNoHttpsClaimEvenWithAGoodCertificate()
        {
            using var cert = SelfSignedTlsCertificate.CreateVerified("SQLTriage Claim Test", out var certDetail);

            // The precondition. If this is null the test proves nothing about the SUFFIX, only
            // that certificate creation failed.
            Assert.NotNull(cert);
            Assert.Contains("handshake completed", certDetail, StringComparison.Ordinal);

            using var dead = new AcceptAndCloseListener();

            var serving = SelfSignedTlsCertificate.EndpointServesTls(dead.Port, cert!, out var probe);

            Assert.False(serving);
            Assert.False(string.IsNullOrWhiteSpace(probe));

            // …and therefore the line the service logs carries no HTTPS claim.
            var template = WindowsServiceHost.StartedLineTemplate(serving);
            Assert.DoesNotContain("HTTPS", template, StringComparison.Ordinal);
            Assert.DoesNotContain("{HttpsPort}", template, StringComparison.Ordinal);
            Assert.Equal("SQLTriage Service started on port {Port}", template);
        }

        /// <summary>
        /// Nothing listening at all. Same verdict, different failure — this is the shape a bind
        /// that never happened leaves, and the one a "was a listener configured?" check misses
        /// completely.
        /// </summary>
        [Fact]
        public void AnUnboundPortProducesNoHttpsClaim()
        {
            using var cert = SelfSignedTlsCertificate.CreateVerified("SQLTriage Claim Test", out _);
            Assert.NotNull(cert);

            var port = ReserveAndReleasePort();

            Assert.False(SelfSignedTlsCertificate.EndpointServesTls(port, cert!, out var probe));
            Assert.False(string.IsNullOrWhiteSpace(probe));
            Assert.DoesNotContain("HTTPS", WindowsServiceHost.StartedLineTemplate(false), StringComparison.Ordinal);
        }

        // ── The positive control ─────────────────────────────────────────

        /// <summary>
        /// Without this, "no HTTPS claim" would be indistinguishable from "the probe can never
        /// succeed", and the fix would read as a permanent suppression of the suffix rather than as
        /// a condition on it. A listener that genuinely serves the certificate earns the claim.
        /// </summary>
        [Fact]
        public async Task AServingEndpointEarnsTheHttpsClaim()
        {
            using var cert = SelfSignedTlsCertificate.CreateVerified("SQLTriage Claim Test", out _);
            Assert.NotNull(cert);

            await using var live = new TlsListener(cert!);

            var serving = SelfSignedTlsCertificate.EndpointServesTls(live.Port, cert!, out var probe);

            Assert.True(serving, probe);
            Assert.Contains(cert!.Thumbprint, probe, StringComparison.OrdinalIgnoreCase);

            var template = WindowsServiceHost.StartedLineTemplate(serving);
            Assert.Equal("SQLTriage Service started on port {Port} (HTTPS: {HttpsPort})", template);
        }

        /// <summary>
        /// The probe asserts identity, not merely liveness. A listener answering on that port with
        /// SOMEONE ELSE'S certificate is not this service's HTTPS endpoint, and the claim would be
        /// about a stranger. The client's validation callback accepts any chain by necessity — a
        /// self-signed certificate never chains — so identity has to be asserted explicitly or the
        /// check degenerates into "something answered".
        /// </summary>
        [Fact]
        public async Task AnEndpointServingADifferentCertificateEarnsNoClaim()
        {
            using var ours = SelfSignedTlsCertificate.CreateVerified("SQLTriage Claim Test", out _);
            using var theirs = SelfSignedTlsCertificate.CreateVerified("Somebody Else", out _);
            Assert.NotNull(ours);
            Assert.NotNull(theirs);
            Assert.NotEqual(ours!.Thumbprint, theirs!.Thumbprint);

            await using var live = new TlsListener(theirs);

            Assert.False(SelfSignedTlsCertificate.EndpointServesTls(live.Port, ours, out var probe));
            Assert.Contains("not the expected", probe, StringComparison.Ordinal);
        }

        // ── The disposal contract (item 2) ───────────────────────────────

        /// <summary>
        /// The flags are chosen so that RELEASING the certificate removes its key container — an
        /// act of the caller, not of the helper. <see cref="WindowsServiceHost"/> never performed
        /// it: measured on 2026-08-03, 24 key containers accumulated in three hours of restarts.
        ///
        /// <para>This pins the property the fix depends on: the flags contain no
        /// <c>PersistKeySet</c> and no <c>EphemeralKeySet</c>. The container's removal itself is a
        /// filesystem effect of the platform under a shared directory that other processes write
        /// to, so it is asserted here as the flag choice — the actual delta was measured by hand
        /// and recorded in the commit — while the DISPOSE is what the host now guarantees.</para>
        /// </summary>
        [Fact]
        public void TheKeyStorageFlagsAreTheOnesThatMakeDisposalMatter()
        {
            Assert.False(SelfSignedTlsCertificate.KeyStorageFlags.HasFlag(X509KeyStorageFlags.PersistKeySet));
            Assert.False(SelfSignedTlsCertificate.KeyStorageFlags.HasFlag(X509KeyStorageFlags.EphemeralKeySet));
            Assert.True(SelfSignedTlsCertificate.KeyStorageFlags.HasFlag(X509KeyStorageFlags.MachineKeySet));
        }

        // ── Rigs ─────────────────────────────────────────────────────────

        private static int ReserveAndReleasePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            AssertPortIsSafe(port);
            return port;
        }

        private static void AssertPortIsSafe(int port)
        {
            if (ForbiddenPorts.Contains(port))
                throw new InvalidOperationException(
                    $"This test took port {port}, which belongs to a live SQLTriage listener. Refusing to run.");
        }

        /// <summary>
        /// Accepts the connection and closes it without a handshake — the exact behaviour the dead
        /// <c>:5156</c> exhibited, and the case a bind check cannot distinguish from a healthy one.
        /// </summary>
        private sealed class AcceptAndCloseListener : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _cts = new();

            internal int Port { get; }

            internal AcceptAndCloseListener()
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                AssertPortIsSafe(Port);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!_cts.IsCancellationRequested)
                        {
                            using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                            client.Close();
                        }
                    }
                    catch { /* the listener was stopped */ }
                });
            }

            public void Dispose()
            {
                _cts.Cancel();
                try { _listener.Stop(); } catch { }
                _cts.Dispose();
            }
        }

        /// <summary>A listener that genuinely serves TLS with the certificate it was given.</summary>
        private sealed class TlsListener : IAsyncDisposable
        {
            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _cts = new();

            internal int Port { get; }

            internal TlsListener(X509Certificate2 certificate)
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                AssertPortIsSafe(Port);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!_cts.IsCancellationRequested)
                        {
                            var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    using (client)
                                    await using (var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false))
                                    {
                                        await ssl.AuthenticateAsServerAsync(
                                            new SslServerAuthenticationOptions { ServerCertificate = certificate },
                                            _cts.Token);

                                        // Hold the connection briefly so the client's handshake
                                        // completion is not racing a socket teardown.
                                        await Task.Delay(50, _cts.Token);
                                    }
                                }
                                catch { /* the probe got what it came for, or the rig is shutting down */ }
                            }, _cts.Token);
                        }
                    }
                    catch { /* the listener was stopped */ }
                });
            }

            public ValueTask DisposeAsync()
            {
                _cts.Cancel();
                try { _listener.Stop(); } catch { }
                _cts.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
