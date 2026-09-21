/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace SQLTriage.Data.Services
{
    // BM:SelfSignedTlsCertificate.Class — the HTTPS certificate both hosts bind, and the proof it works
    /// <summary>
    /// The self-signed certificate the two browser-facing hosts bind their HTTPS listener to, in
    /// ONE place, together with the loopback self-test that decides whether the listener is bound
    /// at all.
    ///
    /// <para><b>Why this file exists.</b> Both hosts had their own copy of the same forty lines,
    /// and both copies imported the certificate with
    /// <c>MachineKeySet | EphemeralKeySet</c> — a combination SChannel cannot serve from. Measured
    /// on this box (Windows 11 26200) on 2026-08-03: a server-side
    /// <see cref="SslStream.AuthenticateAsServerAsync(SslServerAuthenticationOptions, CancellationToken)"/>
    /// with such a certificate throws <c>AuthenticationException: Authentication failed because the
    /// platform does not support ephemeral keys</c> (inner
    /// <c>Win32Exception: No credentials are available in the security package</c>), for every
    /// combination containing <c>EphemeralKeySet</c> — with <c>MachineKeySet</c>, with
    /// <c>Exportable</c>, and on its own. The consequence was live: the installed service had been
    /// logging <c>"Service HTTPS configured on port 5156 with ephemeral certificate"</c> at every
    /// startup since it was installed, and <c>https://127.0.0.1:5156/</c> had never completed a
    /// single handshake (curl exit 000, <c>openssl s_client</c> → <i>unexpected eof while
    /// reading</i>), while <c>http://127.0.0.1:5155/</c> answered 200.</para>
    ///
    /// <para>It failed CLOSED — a listener that cannot handshake admits nobody, and the admission
    /// boundary is one pipeline across both listeners either way. The defect was never an
    /// exposure; it was a dead endpoint plus a log line asserting it was alive. Both halves are
    /// fixed here: <see cref="Create"/> chooses key storage that works where the service actually
    /// runs, and <see cref="CreateVerified"/> refuses to hand back a certificate that has not
    /// completed a real handshake, so a caller can only claim HTTPS on the strength of a
    /// measurement.</para>
    /// </summary>
    public static class SelfSignedTlsCertificate
    {
        /// <summary>
        /// The key-storage flags, chosen deliberately — this is the line that was wrong.
        ///
        /// <list type="bullet">
        /// <item><b>No <c>EphemeralKeySet</c>.</b> It keeps the private key in process memory
        ///   only, with no CNG/CAPI key container behind it, and SChannel builds server
        ///   credentials from a container handle. Measured above: every combination containing it
        ///   fails the handshake on this platform. This is the flag that made the endpoint
        ///   dead.</item>
        /// <item><b><c>MachineKeySet</c>, and specifically NOT <c>UserKeySet</c> or the
        ///   default.</b> The flags that pass a handshake when a developer runs the desktop app
        ///   are not the flags that matter: the installed service runs as
        ///   <c>NT SERVICE\SQLTriage</c>, a virtual account with NO user profile on this box
        ///   (there is no <c>C:\Users\SQLTriage</c>), and a user key set has nowhere to live
        ///   without one. <c>MachineKeySet</c> puts the container under
        ///   <c>C:\ProgramData\Microsoft\Crypto</c>, whose ACL is <c>Everyone:(R,W)</c> —
        ///   verified with <c>icacls</c> on 2026-08-03 — so a profile-less virtual account can
        ///   create it. Choosing the flag that works interactively over the flag that works
        ///   under the service account is exactly how this endpoint died the first time.</item>
        /// <item><b><c>Exportable</c>.</b> Carried over from the original import, which needed it
        ///   for Kestrel on Windows. The key is a per-process throwaway that never leaves this
        ///   process and is discarded on exit, so exportability costs nothing an attacker inside
        ///   the process does not already have.</item>
        /// <item><b>No <c>PersistKeySet</c>, WHICH ONLY HELPS A CALLER THAT DISPOSES.</b> The
        ///   container is removed when the certificate object is RELEASED — released, not created,
        ///   and that is the caller's act, not this helper's. Measured here: importing with these
        ///   flags and then disposing left a net delta of ZERO files across
        ///   <c>Crypto\RSA\MachineKeys</c>, <c>Crypto\Keys</c> and <c>Crypto\SystemKeys</c>. That
        ///   sentence used to be written as though the absence of the flag were the whole
        ///   guarantee, and <see cref="WindowsServiceHost"/> never disposed: measured on 2026-08-03,
        ///   24 key containers accumulated in three hours of service restarts, each one a live
        ///   container for a certificate nothing would ever use again. A caller that holds this
        ///   certificate must hold it for the life of its host AND release it at shutdown —
        ///   including on a FAILED start, which never reaches a "stopping" transition. Both hosts
        ///   now do (a <c>finally</c> in the service host, <c>StopAsync</c> in server mode).</item>
        /// </list>
        /// </summary>
        public const X509KeyStorageFlags KeyStorageFlags =
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable;

        /// <summary>How long the loopback self-test may take before it is called a failure.</summary>
        private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Builds the certificate. Does NOT prove it can serve TLS — call
        /// <see cref="CreateVerified"/> for that, and prefer it at every call site that is about
        /// to log something about HTTPS.
        /// </summary>
        /// <param name="subjectPrefix">
        /// Common-name prefix, so the two hosts stay distinguishable in a certificate viewer
        /// ("SQLTriage" for the desktop's share-via-browser host, "SQLTriage Service" for the
        /// installed service).
        /// </param>
        public static X509Certificate2 Create(string subjectPrefix)
        {
            var hostName = Dns.GetHostName();

            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName(hostName);
            sanBuilder.AddDnsName("localhost");
            sanBuilder.AddIpAddress(IPAddress.Loopback);
            sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);

            try
            {
                var hostEntry = Dns.GetHostEntry(hostName);
                foreach (var addr in hostEntry.AddressList)
                    sanBuilder.AddIpAddress(addr);
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "[HTTPS] Could not resolve host addresses for the certificate SAN");
            }

            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                $"CN={subjectPrefix} ({hostName})",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    critical: false));

            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },   // Server Authentication
                    critical: false));

            request.CertificateExtensions.Add(sanBuilder.Build());

            using var created = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5),      // small backdate against clock skew
                DateTimeOffset.UtcNow.AddDays(365));

            // Round-trip through PKCS#12 so the private key lands in a key container SChannel can
            // build credentials from. This step was always here; only the flags were wrong.
            var pfxBytes = created.Export(X509ContentType.Pfx);
            try
            {
                return X509CertificateLoader.LoadPkcs12(pfxBytes, (string?)null, KeyStorageFlags);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfxBytes);
            }
        }

        /// <summary>
        /// Builds the certificate and PROVES it can serve TLS, by completing a real handshake
        /// against it over a loopback socket on an OS-chosen port.
        ///
        /// <para>Returns the certificate on success and null on failure, with
        /// <paramref name="detail"/> carrying the reason either way. The caller is expected to bind
        /// the HTTPS listener only on success — binding a listener that cannot handshake is the
        /// dead endpoint this method exists to prevent, and a log line about it is the false claim
        /// it exists to prevent.</para>
        ///
        /// <para>What the self-test proves and what it does not: it proves SChannel can build
        /// server credentials from this certificate's key and complete a handshake with them on
        /// this machine, under THIS process's account — which is precisely what
        /// <c>EphemeralKeySet</c> could not do. It does not prove Kestrel then bound the port; the
        /// hosts claim that separately, from <c>ApplicationStarted</c>.</para>
        /// </summary>
        public static X509Certificate2? CreateVerified(string subjectPrefix, out string detail)
        {
            X509Certificate2? cert = null;
            try
            {
                cert = Create(subjectPrefix);

                // Off the calling context on purpose: ServerModeService builds its host from the
                // WPF dispatcher thread, and blocking a captured UI context on an async handshake
                // is a deadlock rather than a test result.
                var protocol = Task.Run(() => HandshakeAsync(cert)).GetAwaiter().GetResult();

                detail = $"thumbprint {cert.Thumbprint}, {protocol} handshake completed in a loopback self-test";
                return cert;
            }
            catch (Exception ex)
            {
                cert?.Dispose();
                detail = Describe(ex);
                return null;
            }
        }

        /// <summary>
        /// Whether the LISTENER AT THIS ENDPOINT actually serves TLS, and serves it with
        /// <paramref name="expected"/> — measured by completing a real handshake against it.
        ///
        /// <para><b>Why this is a separate question from <see cref="CreateVerified"/>.</b> That
        /// method proves the platform can build server credentials from a key, on a socket it
        /// stands up itself. It says nothing about whether the host then bound the port, bound it
        /// with this certificate, or bound it at all — and a startup line that claims HTTPS on the
        /// strength of holding a certificate object is asserting more than was established. This is
        /// the same defect class that produced the dead <c>:5156</c>, one layer out.</para>
        ///
        /// <para><b><c>IServerAddressesFeature</c> is not a substitute — measured, it reports the
        /// unserved endpoint.</b> It lists what Kestrel was CONFIGURED with. Only a connection
        /// distinguishes a listener that answers from an address that was merely written down.</para>
        ///
        /// <para>The thumbprint comparison is what makes this a proof about OUR listener rather
        /// than about something on that port: the client's validation callback deliberately accepts
        /// any chain (a self-signed certificate never chains), so identity has to be asserted
        /// explicitly or the check degenerates into "something answered".</para>
        /// </summary>
        /// <param name="port">The port the host was asked to bind for HTTPS.</param>
        /// <param name="expected">The certificate the listener is supposed to be serving.</param>
        /// <param name="detail">What was measured, success or failure — log it.</param>
        public static bool EndpointServesTls(int port, X509Certificate2 expected, out string detail)
        {
            try
            {
                var served = Task.Run(() => ProbeEndpointAsync(port)).GetAwaiter().GetResult();

                if (!string.Equals(served.Thumbprint, expected.Thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    detail = $"the listener on port {port} completed a {served.Protocol} handshake but served "
                             + $"thumbprint {served.Thumbprint}, not the expected {expected.Thumbprint}";
                    return false;
                }

                detail = $"{served.Protocol} handshake completed against the bound listener on port {port}, "
                         + $"serving thumbprint {served.Thumbprint}";
                return true;
            }
            catch (Exception ex)
            {
                detail = Describe(ex);
                return false;
            }
        }

        /// <summary>
        /// Connects to <c>127.0.0.1:port</c> as a TLS client and reports what the listener served.
        /// Throws when there is nothing there, when it will not handshake, or when it times out.
        /// </summary>
        private static async Task<(SslProtocols Protocol, string Thumbprint)> ProbeEndpointAsync(int port)
        {
            using var cts = new CancellationTokenSource(HandshakeTimeout);

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token).ConfigureAwait(false);

            string? thumbprint = null;
            await using var ssl = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false,
                (_, certificate, _, _) =>
                {
                    // Record, do not judge. A self-signed certificate has no chain to validate;
                    // the assertion is made by the caller, against the thumbprint it expected.
                    thumbprint = certificate is X509Certificate2 c2
                        ? c2.Thumbprint
                        : certificate == null ? null : new X509Certificate2(certificate).Thumbprint;
                    return true;
                });

            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = "localhost" },
                cts.Token).ConfigureAwait(false);

            return (ssl.SslProtocol, thumbprint ?? "(no certificate presented)");
        }

        /// <summary>
        /// The self-test: stand a TLS server on <c>127.0.0.1:0</c>, connect to it, and return the
        /// protocol that was negotiated. Throws if the handshake does not complete.
        ///
        /// <para>Loopback and an OS-chosen port, so this can never contend with a listener that
        /// matters. The client's certificate-validation callback accepts anything by design: the
        /// question under test is whether the platform can USE this key to serve TLS, not whether
        /// a self-signed certificate chains — it does not, and never will.</para>
        /// </summary>
        private static async Task<SslProtocols> HandshakeAsync(X509Certificate2 cert)
        {
            using var cts = new CancellationTokenSource(HandshakeTimeout);

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;

                var serverTask = Task.Run(async () =>
                {
                    using var connection = await listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
                    await using var ssl = new SslStream(connection.GetStream(), leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions
                        {
                            ServerCertificate = cert,
                            ClientCertificateRequired = false,
                            EnabledSslProtocols = SslProtocols.None,   // whatever the OS policy allows
                        },
                        cts.Token).ConfigureAwait(false);
                    return ssl.SslProtocol;
                }, cts.Token);

                var clientTask = Task.Run(async () =>
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port, cts.Token).ConfigureAwait(false);
                    await using var ssl = new SslStream(
                        client.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
                    await ssl.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions { TargetHost = "localhost" },
                        cts.Token).ConfigureAwait(false);
                }, cts.Token);

                // The SERVER side is the assertion — that is where the key is used and where the
                // ephemeral-key failure surfaces. The client is awaited afterwards only so its
                // socket is not left half-open; its own failure adds nothing the server did not
                // already report.
                var protocol = await serverTask.ConfigureAwait(false);
                try { await clientTask.ConfigureAwait(false); } catch { /* server already answered */ }
                return protocol;
            }
            finally
            {
                listener.Stop();
                listener.Dispose();
            }
        }

        /// <summary>
        /// Flattens an exception into one log-safe line, inner exceptions included — the useful
        /// half of the ephemeral-key failure lives in the inner <c>Win32Exception</c>, and a
        /// message that stops at the outer one reads like a generic TLS error.
        /// </summary>
        internal static string Describe(Exception ex)
        {
            var text = ex.GetType().Name + ": " + Flatten(ex.Message);
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                text += " <- " + inner.GetType().Name + ": " + Flatten(inner.Message);
            return text;
        }

        private static string Flatten(string message) =>
            message.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
    }
}
