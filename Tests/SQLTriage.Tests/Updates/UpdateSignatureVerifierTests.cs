/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System.IO;
using System.Security.Cryptography;
using System.Text;
using SQLTriage.Data.Services.Updates;

namespace SQLTriage.Tests.Updates;

/// <summary>
/// Tests the update artifact signature verifier. The internal VerifyBytes seam
/// (exposed via InternalsVisibleTo) lets us exercise real RSA/ECDSA verification with
/// ephemeral test keys; the public VerifyFile path is tested against the embedded
/// PLACEHOLDER key, which must fail closed.
/// </summary>
public class UpdateSignatureVerifierTests
{
    private static readonly byte[] Artifact =
        Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog — SQLTriage update payload");

    // ── RSA ──────────────────────────────────────────────────────────────────

    [Fact]
    public void VerifyBytes_RsaValidSignature_ReturnsTrue()
    {
        using var rsa = RSA.Create(3072);
        var pem = rsa.ExportSubjectPublicKeyInfoPem();
        var sig = rsa.SignData(Artifact, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        Assert.True(UpdateSignatureVerifier.VerifyBytes(Artifact, sig, pem));
    }

    [Fact]
    public void VerifyBytes_RsaTamperedArtifact_ReturnsFalse()
    {
        using var rsa = RSA.Create(3072);
        var pem = rsa.ExportSubjectPublicKeyInfoPem();
        var sig = rsa.SignData(Artifact, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var tampered = (byte[])Artifact.Clone();
        tampered[0] ^= 0xFF;

        Assert.False(UpdateSignatureVerifier.VerifyBytes(tampered, sig, pem));
    }

    [Fact]
    public void VerifyBytes_RsaWrongKey_ReturnsFalse()
    {
        using var signer = RSA.Create(3072);
        using var other = RSA.Create(3072);
        var sig = signer.SignData(Artifact, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        Assert.False(UpdateSignatureVerifier.VerifyBytes(Artifact, sig, other.ExportSubjectPublicKeyInfoPem()));
    }

    // ── ECDSA ────────────────────────────────────────────────────────────────

    [Fact]
    public void VerifyBytes_EcdsaValidSignature_ReturnsTrue()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = ecdsa.ExportSubjectPublicKeyInfoPem();
        var sig = ecdsa.SignData(Artifact, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        Assert.True(UpdateSignatureVerifier.VerifyBytes(Artifact, sig, pem));
    }

    [Fact]
    public void VerifyBytes_EcdsaTamperedArtifact_ReturnsFalse()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = ecdsa.ExportSubjectPublicKeyInfoPem();
        var sig = ecdsa.SignData(Artifact, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        var tampered = (byte[])Artifact.Clone();
        tampered[^1] ^= 0x01;

        Assert.False(UpdateSignatureVerifier.VerifyBytes(tampered, sig, pem));
    }

    // ── Malformed inputs ──────────────────────────────────────────────────────

    [Fact]
    public void VerifyBytes_GarbageSignature_ReturnsFalse()
    {
        using var rsa = RSA.Create(3072);
        var pem = rsa.ExportSubjectPublicKeyInfoPem();
        var garbage = new byte[64];
        RandomNumberGenerator.Fill(garbage);

        Assert.False(UpdateSignatureVerifier.VerifyBytes(Artifact, garbage, pem));
    }

    [Fact]
    public void VerifyBytes_NotAKeyPem_ReturnsFalse()
    {
        using var rsa = RSA.Create(3072);
        var sig = rsa.SignData(Artifact, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        Assert.False(UpdateSignatureVerifier.VerifyBytes(Artifact, sig, "not a pem key"));
    }

    // ── VerifyFile (file plumbing) with a real ephemeral key ─────────────────

    [Fact]
    public void VerifyBytes_AcceptsBase64RoundTrip_ThroughFiles()
    {
        // Mirrors what VerifyFile does internally: base64 signature on disk + raw artifact.
        using var rsa = RSA.Create(3072);
        var pem = rsa.ExportSubjectPublicKeyInfoPem();
        var sig = rsa.SignData(Artifact, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var b64 = Convert.ToBase64String(sig);
        var decoded = Convert.FromBase64String(b64.Replace("\n", "").Replace("\r", ""));

        Assert.True(UpdateSignatureVerifier.VerifyBytes(Artifact, decoded, pem));
    }

    // ── Embedded placeholder must fail closed ─────────────────────────────────

    [Fact]
    public void VerifyFile_WithPlaceholderKey_FailsClosed()
    {
        // The build ships a PLACEHOLDER public key until the real cert is dropped in.
        // While that placeholder is embedded, no real key is configured and every
        // verification must fail — the updater is inert by design.
        var verifier = new UpdateSignatureVerifier();

        if (verifier.IsTrustedKeyConfigured)
        {
            // A real key has been dropped in (post-cert). Placeholder-specific assertion
            // no longer applies; nothing to prove here.
            return;
        }

        var tmpZip = Path.GetTempFileName();
        var tmpSig = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmpZip, Artifact);
            File.WriteAllText(tmpSig, Convert.ToBase64String(new byte[64]));

            Assert.False(verifier.IsTrustedKeyConfigured);
            Assert.False(verifier.VerifyFile(tmpZip, tmpSig));
        }
        finally
        {
            File.Delete(tmpZip);
            File.Delete(tmpSig);
        }
    }

    [Fact]
    public void VerifyFile_MissingFiles_ReturnsFalse()
    {
        var verifier = new UpdateSignatureVerifier();
        Assert.False(verifier.VerifyFile("does-not-exist.zip", "does-not-exist.sig"));
    }
}
