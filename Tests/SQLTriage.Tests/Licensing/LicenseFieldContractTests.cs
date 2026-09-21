/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System.Security.Cryptography;
using System.Text.Json;
using SQLTriage.Data.Services.Licensing;
using SQLTriage.Data.Services.Licensing.Crypto;
using SQLTriage.Tests.Licensing.Fixtures;

namespace SQLTriage.Tests.Licensing;

/// <summary>
/// B1/B2 cross-component contract tests. The manifest schema is a 3-site MUST-AGREE contract
/// (app / off-GitHub CorpusEncryptor / fixtures) — a drift in a JSON property NAME makes every
/// bundle silently fail to decrypt, masked because the service falls back to Free. These tests
/// pin the exact wire names AND prove the new fields survive a real encrypt→decrypt round-trip
/// on the app-side codec (which is byte-identical to the encryptor's, per AadBuilderMirrorTests).
/// </summary>
public class LicenseFieldContractTests
{
    // The wire names the off-GitHub CorpusEncryptor MUST emit. If either changes here, the
    // encryptor's BundleFeatures must change in lockstep or bundles stop decrypting.
    private const string ExpiryWireName = "licenseExpiryUtc";
    private const string LicenseIdWireName = "licenseId";

    [Fact]
    public void ManifestFeatures_SerializesLicenseExpiry_UnderContractWireName()
    {
        var features = new ManifestFeatures { LicenseExpiryUtc = "2027-07-01T00:00:00Z" };
        var json = JsonSerializer.Serialize(features);
        Assert.Contains($"\"{ExpiryWireName}\":\"2027-07-01T00:00:00Z\"", json);
    }

    [Fact]
    public void ManifestFeatures_SerializesLicenseId_UnderContractWireName()
    {
        var id = "11111111-2222-3333-4444-555555555555";
        var features = new ManifestFeatures { LicenseId = id };
        var json = JsonSerializer.Serialize(features);
        Assert.Contains($"\"{LicenseIdWireName}\":\"{id}\"", json);
    }

    [Fact]
    public void ManifestFeatures_DeserializesFromContractWireNames()
    {
        // Simulates the JSON the encryptor writes → the app must read it back.
        var json = $"{{\"{ExpiryWireName}\":\"2028-01-01T00:00:00Z\",\"{LicenseIdWireName}\":\"abc\"}}";
        var features = JsonSerializer.Deserialize<ManifestFeatures>(json)!;
        Assert.Equal("2028-01-01T00:00:00Z", features.LicenseExpiryUtc);
        Assert.Equal("abc", features.LicenseId);
    }

    [Fact]
    public void Roundtrip_EncryptThenDecrypt_PreservesExpiryAndLicenseId()
    {
        var key = new byte[BundleCrypto.KeySize];
        RandomNumberGenerator.Fill(key);

        var manifest = BundleFixtureFactory.MakeFullManifest(
            "Acme Corp",
            licenseExpiryUtc: "2027-07-01T00:00:00Z",
            licenseId: "deadbeef-0000-0000-0000-000000000001");
        var aad = AadBuilder.Build("Acme Corp", "Full", 1, 1903);

        var wire = BundleCrypto.EncryptManifest(manifest, key, aad);
        var decoded = BundleCrypto.DecryptManifest(wire, key, aad);

        Assert.Equal("2027-07-01T00:00:00Z", decoded.Features!.LicenseExpiryUtc);
        Assert.Equal("deadbeef-0000-0000-0000-000000000001", decoded.Features!.LicenseId);
    }

    [Fact]
    public void Roundtrip_NullExpiryAndId_StayNull_LegacyPerpetual()
    {
        var key = new byte[BundleCrypto.KeySize];
        RandomNumberGenerator.Fill(key);

        var manifest = BundleFixtureFactory.MakeFullManifest("Acme Corp"); // both null (legacy shape)
        var aad = AadBuilder.Build("Acme Corp", "Full", 1, 1903);

        var wire = BundleCrypto.EncryptManifest(manifest, key, aad);
        var decoded = BundleCrypto.DecryptManifest(wire, key, aad);

        Assert.Null(decoded.Features!.LicenseExpiryUtc);   // perpetual — fail-OPEN at enforcement
        Assert.Null(decoded.Features!.LicenseId);
    }

    // ── Drift fix 2026-07-02: the 4 capability-lane fields the encryptor previously could not stamp.
    // These pin the exact wire names AND prove BundleAccessor.Features translates encryptor-shaped
    // JSON to the right runtime values (stamped passes through; absent → fail-closed default). ──

    [Fact]
    public void ManifestFeatures_DeserializesCapabilityLanes_FromEncryptorWireNames()
    {
        // Exactly the JSON the encryptor's BundleFeatures now emits for a remediation+MSP bundle.
        var json = "{\"remediation\":true,\"remediationCreditsPerServer\":5," +
                   "\"demoCorpusInstancesPer24h\":0,\"demoExpiryUtc\":\"2027-01-01T00:00:00Z\"}";
        var f = JsonSerializer.Deserialize<ManifestFeatures>(json)!;
        Assert.True(f.Remediation);
        Assert.Equal(5, f.RemediationCreditsPerServer);
        Assert.Equal(0, f.DemoCorpusInstancesPer24h);
        Assert.Equal("2027-01-01T00:00:00Z", f.DemoExpiryUtc);
    }

    [Fact]
    public void BundleAccessor_StampedCapabilityLanes_SurfaceThrough()
    {
        // A bundle where the encryptor granted the remediation lane + unlimited demo + MSP credits.
        var manifest = new BundleManifest
        {
            BundleVersion = 1,
            ClientName = "MSP Co",
            Tier = "Full",
            Features = new ManifestFeatures
            {
                FullCorpus = true,
                CheckIds = new List<int>(),
                Remediation = true,
                RemediationCreditsPerServer = 5,
                DemoCorpusInstancesPer24h = 0,   // 0 = unlimited (full bundle)
            }
        };

        var acc = new BundleAccessor();
        acc.Replace(manifest, Tier.Full);

        Assert.True(acc.Features.Remediation);                       // stamped grant surfaces
        Assert.Equal(5, acc.Features.RemediationCreditsPerServer);
        Assert.Equal(0, acc.Features.DemoCorpusInstancesPer24h);     // unlimited, not clamped to 1
    }

    [Fact]
    public void BundleAccessor_AbsentCapabilityLanes_FailClosed()
    {
        // A pre-drift-fix bundle: encryptor never wrote these → app must fail-CLOSED (this is the
        // "benign today" behaviour we must NOT regress — remediation denied, demo clamped to 1).
        var manifest = BundleFixtureFactory.MakeFullManifest("Legacy Co"); // capability fields null

        var acc = new BundleAccessor();
        acc.Replace(manifest, Tier.Full);

        Assert.False(acc.Features.Remediation);                    // WRITE lane fail-CLOSED
        Assert.Equal(0, acc.Features.RemediationCreditsPerServer); // no credits
        Assert.Equal(1, acc.Features.DemoCorpusInstancesPer24h);   // community limit, not unlimited
    }
}
