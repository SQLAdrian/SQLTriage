/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace SQLTriage.Data.Services.Updates;

/// <summary>
/// Verifies detached signatures over update artifacts (the release ZIP) against a
/// trusted public key embedded in this assembly.
///
/// THREAT MODEL: the auto-updater downloads a ZIP and executes it as a full app
/// replacement. Without authenticity verification, anyone who can influence the bytes
/// (compromised release, DNS, CDN, or a hostile proxy) achieves remote code execution
/// on every install and a pivot into the client's SQL estate. This verifier is the
/// gate: a ZIP is only ever staged/applied if its signature verifies against the
/// embedded public key. <see cref="VerifyFile"/> fails closed on EVERY error path.
///
/// CERT DROP-IN: the trusted key is the embedded resource
/// <c>Resources/update-signing-public.pem</c>. Until the real code-signing certificate
/// is available it ships as a PLACEHOLDER and <see cref="IsTrustedKeyConfigured"/> is
/// false, so verification always fails. To go live: run
/// <c>tools/extract-update-pubkey.ps1 -Pfx &lt;cert&gt;.pfx</c> to overwrite the PEM with the
/// real SubjectPublicKeyInfo, rebuild, and sign releases with the matching private key.
/// The verifier auto-detects RSA vs ECDSA from the PEM, so any standard code-signing
/// certificate works without code changes.
///
/// Signature scheme: signature is over SHA-256 of the raw artifact bytes.
///   - RSA: PKCS#1 v1.5 over SHA-256 (signtool / openssl default; widest compatibility)
///   - ECDSA: SHA-256, signature in IEEE-P1363 (r||s) encoding
/// The signing side (publish-release.ps1) must match this scheme.
/// </summary>
public sealed class UpdateSignatureVerifier
{
    private const string PublicKeyResourceSuffix = "update-signing-public.pem";
    private const string PemPublicKeyHeader = "-----BEGIN PUBLIC KEY-----";

    // Loaded once; null if no real key is embedded (placeholder or missing).
    private static readonly object _loadLock = new();
    private static bool _loaded;
    private static string? _publicKeyPem;

    /// <summary>
    /// True only if a real PEM public key (SubjectPublicKeyInfo) is embedded. False while
    /// the placeholder is in place — in which case all verification fails closed.
    /// </summary>
    public bool IsTrustedKeyConfigured
    {
        get
        {
            EnsureLoaded();
            return _publicKeyPem != null;
        }
    }

    /// <summary>
    /// Verifies that <paramref name="signaturePath"/> is a valid detached signature over
    /// <paramref name="artifactPath"/> under the embedded trusted public key.
    /// Returns false on ANY problem: no trusted key, missing files, malformed signature,
    /// unknown key type, or signature mismatch. Never throws.
    /// </summary>
    public bool VerifyFile(string artifactPath, string signaturePath)
    {
        try
        {
            EnsureLoaded();
            if (_publicKeyPem == null)
                return false; // placeholder / no trusted key — fail closed
            if (string.IsNullOrWhiteSpace(artifactPath) || !File.Exists(artifactPath))
                return false;
            if (string.IsNullOrWhiteSpace(signaturePath) || !File.Exists(signaturePath))
                return false;

            byte[] signature = ReadSignature(signaturePath);
            if (signature.Length == 0)
                return false;

            byte[] artifact = File.ReadAllBytes(artifactPath);
            return VerifyBytes(artifact, signature, _publicKeyPem);
        }
        catch
        {
            // Fail closed — a verification path must never throw its way into "trusted".
            return false;
        }
    }

    /// <summary>
    /// Verifies a signature over <paramref name="artifact"/> using the supplied PEM public
    /// key. Auto-detects RSA vs ECDSA. Internal/testable; production callers use
    /// <see cref="VerifyFile"/> with the embedded key.
    /// </summary>
    internal static bool VerifyBytes(byte[] artifact, byte[] signature, string publicKeyPem)
    {
        // Try RSA first, then ECDSA. ImportFromPem throws if the PEM isn't that key type,
        // so we attempt each and treat exceptions as "not this algorithm".
        if (TryVerifyRsa(artifact, signature, publicKeyPem, out var rsaOk))
            return rsaOk;
        if (TryVerifyEcdsa(artifact, signature, publicKeyPem, out var ecOk))
            return ecOk;
        return false; // unknown / unsupported key type
    }

    private static bool TryVerifyRsa(byte[] artifact, byte[] signature, string pem, out bool result)
    {
        result = false;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            result = rsa.VerifyData(artifact, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return true; // key WAS RSA; result is authoritative
        }
        catch (CryptographicException)
        {
            return false; // not an RSA key (or import failed) — let caller try ECDSA
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryVerifyEcdsa(byte[] artifact, byte[] signature, string pem, out bool result)
    {
        result = false;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(pem);
            result = ecdsa.VerifyData(artifact, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return true; // key WAS ECDSA; result is authoritative
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads a detached signature file. Accepts either base64 text (with optional
    /// whitespace/newlines) or raw binary. Returns empty on failure.
    /// </summary>
    private static byte[] ReadSignature(string signaturePath)
    {
        byte[] raw = File.ReadAllBytes(signaturePath);

        // Prefer base64 (what publish-release.ps1 writes). Trim and strip whitespace.
        try
        {
            var text = Encoding.UTF8.GetString(raw).Trim();
            if (text.Length > 0)
            {
                var compact = text
                    .Replace("\r", string.Empty)
                    .Replace("\n", string.Empty)
                    .Replace(" ", string.Empty)
                    .Replace("\t", string.Empty);
                return Convert.FromBase64String(compact);
            }
        }
        catch (FormatException)
        {
            // Not base64 — fall through to treating the bytes as a raw binary signature.
        }

        return raw;
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_loadLock)
        {
            if (_loaded) return;
            _publicKeyPem = LoadEmbeddedTrustedKey();
            _loaded = true;
        }
    }

    /// <summary>
    /// Loads the embedded public-key PEM. Returns null if the resource is missing or is the
    /// placeholder (i.e. does not contain a real "BEGIN PUBLIC KEY" block).
    /// </summary>
    private static string? LoadEmbeddedTrustedKey()
    {
        var asm = Assembly.GetExecutingAssembly();
        var resName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(PublicKeyResourceSuffix, StringComparison.OrdinalIgnoreCase));
        if (resName == null)
            return null;

        using var stream = asm.GetManifestResourceStream(resName);
        if (stream == null)
            return null;

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = reader.ReadToEnd();

        // The placeholder deliberately lacks a real SubjectPublicKeyInfo PEM block.
        if (!content.Contains(PemPublicKeyHeader, StringComparison.Ordinal))
            return null;

        return content;
    }
}
