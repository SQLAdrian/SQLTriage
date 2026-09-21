/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System.IO;
using SQLTriage.Data.Services.Licensing;
using SQLTriage.Data.Services.Licensing.Crypto;

namespace SQLTriage.Tests.Licensing.Fixtures;

/// <summary>
/// Produces in-process test bundles using the SQLTriage-side BundleCrypto.
/// Used by LicenseServiceTests to create .aesgcm fixtures in a temp directory.
///
/// NEVER_USE_IN_PROD: These keys and bundles are for testing only.
/// </summary>
public static class BundleFixtureFactory
{
    // Fixed 32-byte test key — deterministic, never used in production
    public static readonly byte[] TestKey = new byte[]
    {
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
        0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
        0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20
    };

    public const string TestClientName = "TEST_CLIENT_NEVER_PROD";
    public const int TestBuildNumber = 1903;

    // Free tier zero key — matches LicenseService.FreeKey exactly
    public static readonly byte[] FreeKey = new byte[BundleCrypto.KeySize];

    /// <summary>Creates a Full-tier test manifest with minimal corpus + files.</summary>
    /// <param name="licenseExpiryUtc">B1: ISO-8601 UTC license expiry; null = perpetual.</param>
    /// <param name="licenseId">B2: opaque per-issue license id; null = unidentified.</param>
    /// <param name="createdUtc">
    /// The AUTHENTICATED mint date. Default keeps the long-standing fixture value, so every existing
    /// caller is byte-for-byte unchanged. Overridden by the bundle-precedence tests, which need two
    /// bundles that differ ONLY in this field.
    /// </param>
    public static BundleManifest MakeFullManifest(
        string clientName = TestClientName,
        string? licenseExpiryUtc = null,
        string? licenseId = null,
        string createdUtc = "2026-05-23T00:00:00Z")
    {
        return new BundleManifest
        {
            BundleVersion = 1,
            BuildNumber = TestBuildNumber,
            CreatedUtc = createdUtc,
            ClientName = clientName,
            Tier = "Full",
            Features = new ManifestFeatures
            {
                RagEnabled = true,
                SpBlitzImport = true,
                FullCorpus = true,
                CheckIds = new List<int>(),  // empty = all checks permitted on Full tier
                LicenseExpiryUtc = licenseExpiryUtc,
                LicenseId = licenseId
            },
            Files = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Config/control_mappings.json"] = "{\"test\":true}",
                ["Config/governance-weights.json"] = "{\"test\":true}",
            },
            Corpus = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["check_001_test.yaml"] = "id: 1\ntitle: Test Check",
                ["check_001_test.sql"] = "SELECT 1 AS Result",
                ["check_002_test.yaml"] = "id: 2\ntitle: Test Check 2",
            }
        };
    }

    /// <summary>Creates a Free-tier test manifest.</summary>
    public static BundleManifest MakeFreeManifest()
    {
        return new BundleManifest
        {
            BundleVersion = 1,
            BuildNumber = TestBuildNumber,
            CreatedUtc = "2026-05-23T00:00:00Z",
            ClientName = "FREE",
            Tier = "Free",
            Features = new ManifestFeatures
            {
                RagEnabled = false,
                SpBlitzImport = true,
                FullCorpus = false,
                CheckIds = new List<int> { 1, 2, 3 }
            },
            Files = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Config/control_mappings.json"] = "{\"free\":true}",
            },
            Corpus = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["check_001_test.yaml"] = "id: 1\ntitle: Free Check",
                ["check_001_test.sql"] = "SELECT 1 AS Result",
            }
        };
    }

    /// <summary>
    /// Writes a Full-tier .aesgcm bundle to <paramref name="dir"/> and returns the file path.
    /// </summary>
    public static string WriteFullBundle(string dir, string? clientName = null, byte[]? key = null,
        string? licenseExpiryUtc = null, string? licenseId = null)
    {
        clientName ??= TestClientName;
        key ??= TestKey;

        var manifest = MakeFullManifest(clientName, licenseExpiryUtc, licenseId);
        var aad = AadBuilder.Build(clientName, "Full", 1, TestBuildNumber);
        var wireBytes = BundleCrypto.EncryptManifest(manifest, key, aad);

        var path = Path.Combine(dir, "test-bundle.aesgcm");
        File.WriteAllBytes(path, wireBytes);
        return path;
    }

    /// <summary>
    /// <see cref="WriteFullBundle"/> with the file NAME chosen by the caller.
    ///
    /// <para>Key-file pickup pairs a bundle to its key file by exact base name
    /// (<c>X.aesgcm</c> ↔ <c>X.key.txt</c>) and orders several pairs by
    /// <see cref="StringComparer.Ordinal"/> on the path, so a suite that exercises pairing and
    /// precedence needs two bundles whose names differ. The single fixed name above cannot express
    /// either. Same crypto, same manifest, same AAD — only the file name moves.</para>
    /// </summary>
    public static string WriteFullBundleNamed(
        string dir, string fileName, string? clientName = null, byte[]? key = null,
        string? licenseExpiryUtc = null, string createdUtc = "2026-05-23T00:00:00Z")
    {
        clientName ??= TestClientName;
        key ??= TestKey;

        var manifest = MakeFullManifest(clientName, licenseExpiryUtc, licenseId: null, createdUtc: createdUtc);
        var aad = AadBuilder.Build(clientName, "Full", 1, TestBuildNumber);
        var wireBytes = BundleCrypto.EncryptManifest(manifest, key, aad);

        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, wireBytes);
        return path;
    }

    /// <summary>
    /// Writes the Free-tier bundle to <paramref name="dir"/> and returns the path.
    /// Matches the LicenseService's expected path: Config/free-bundle.dat
    /// Uses the HARDENED FreeBundleCodec format (derived key + generation rotation) — the legacy
    /// zero-key format is no longer produced or accepted.
    /// </summary>
    public static string WriteFreeBundle(string dir)
    {
        var manifest = MakeFreeManifest();
        var wireBytes = FreeBundleCodec.Pack(manifest);

        // LicenseService looks in Config sub-dir first, then install dir directly
        var configDir = Path.Combine(dir, "Config");
        Directory.CreateDirectory(configDir);
        var path = Path.Combine(configDir, "free-bundle.dat");
        File.WriteAllBytes(path, wireBytes);
        return path;
    }
}
