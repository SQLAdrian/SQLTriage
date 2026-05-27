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
    public static BundleManifest MakeFullManifest(string clientName = TestClientName)
    {
        return new BundleManifest
        {
            BundleVersion = 1,
            BuildNumber = TestBuildNumber,
            CreatedUtc = "2026-05-23T00:00:00Z",
            ClientName = clientName,
            Tier = "Full",
            Features = new ManifestFeatures
            {
                RagEnabled = true,
                SpBlitzImport = true,
                FullCorpus = true,
                CheckIds = new List<int>()   // empty = all checks permitted on Full tier
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
    public static string WriteFullBundle(string dir, string? clientName = null, byte[]? key = null)
    {
        clientName ??= TestClientName;
        key ??= TestKey;

        var manifest = MakeFullManifest(clientName);
        var aad = AadBuilder.Build(clientName, "Full", 1, TestBuildNumber);
        var wireBytes = BundleCrypto.EncryptManifest(manifest, key, aad);

        var path = Path.Combine(dir, "test-bundle.aesgcm");
        File.WriteAllBytes(path, wireBytes);
        return path;
    }

    /// <summary>
    /// Writes the Free-tier bundle to <paramref name="dir"/> and returns the path.
    /// Matches the LicenseService's expected path: Config/free-bundle.dat
    /// </summary>
    public static string WriteFreeBundle(string dir)
    {
        var manifest = MakeFreeManifest();
        // Free bundle AAD is pinned to build 0 (universal across patch builds) — must
        // match LicenseService.FreeBundleBuildNumber, not the running/test build number.
        var aad = AadBuilder.Build("FREE", "Free", 1, 0);
        var wireBytes = BundleCrypto.EncryptManifest(manifest, FreeKey, aad);

        // LicenseService looks in Config sub-dir first, then install dir directly
        var configDir = Path.Combine(dir, "Config");
        Directory.CreateDirectory(configDir);
        var path = Path.Combine(configDir, "free-bundle.dat");
        File.WriteAllBytes(path, wireBytes);
        return path;
    }
}
