/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Services.Licensing;
using SQLTriage.Data.Services.Licensing.Crypto;
using SQLTriage.Tests.Licensing.Fixtures;

namespace SQLTriage.Tests.Licensing;

/// <summary>
/// Integration-style tests for LicenseService.
///
/// LicenseService reads bundles from AppContext.BaseDirectory, so these tests
/// write test fixtures into the running test binary's output directory and clean
/// up afterward. Tests that write persistent files use unique filenames per test
/// via a GUID suffix to avoid parallelism collisions.
///
/// DPAPI tests are skipped on non-Windows platforms.
/// </summary>
public class LicenseServiceTests : IDisposable
{
    private readonly string _installDir;
    private readonly List<string> _createdFiles = new();

    public LicenseServiceTests()
    {
        // Tests run from the test project output directory — same as AppContext.BaseDirectory
        _installDir = AppContext.BaseDirectory;

        // Ensure Config subdir exists (LicenseService looks here for free-bundle.dat)
        Directory.CreateDirectory(Path.Combine(_installDir, "Config"));

        // Write a synthetic version.json so LicenseService.ReadBuildNumber()
        // returns the same value used when encrypting test bundles.
        WriteSyntheticVersionFile();
    }

    private void WriteSyntheticVersionFile()
    {
        var configDir = Path.Combine(_installDir, "Config");
        Directory.CreateDirectory(configDir);
        var path = Path.Combine(configDir, "version.json");
        var versionJson = $"{{\"version\":\"0.90.2\",\"buildNumber\":{BundleFixtureFactory.TestBuildNumber}}}";
        File.WriteAllText(path, versionJson);
        // Don't add to _createdFiles — we don't own the real version.json
    }

    public void Dispose()
    {
        // Remove test-specific files written during the test
        foreach (var f in _createdFiles)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best-effort */ }
        }
    }

    // ── Happy path: Full bundle ──────────────────────────────────────────────

    [Fact]
    public void Initialize_WithValidFullBundle_SetsFullTier()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI required

        var bundlePath = WriteFullBundle(_installDir, BundleFixtureFactory.TestClientName, BundleFixtureFactory.TestKey);
        var freePath = WriteFreeBundle();

        try
        {
            var (svc, acc) = MakeService(BundleFixtureFactory.TestClientName, BundleFixtureFactory.TestKey);
            svc.Initialize();

            Assert.True(acc.IsUnlocked);
            Assert.Equal(Tier.Full, acc.Tier);
            Assert.Equal(BundleFixtureFactory.TestClientName, acc.ClientName);
        }
        finally
        {
            TryDelete(bundlePath);
            TryDelete(freePath);
        }
    }

    // ── Wrong client name → falls back to Free ───────────────────────────────

    [Fact]
    public void Initialize_WrongClientName_FallsBackToFree()
    {
        if (!OperatingSystem.IsWindows()) return;

        var bundlePath = WriteFullBundle(_installDir, BundleFixtureFactory.TestClientName, BundleFixtureFactory.TestKey);
        var freePath = WriteFreeBundle();

        try
        {
            // Persist with a DIFFERENT client name than the bundle was created with
            var (svc, acc) = MakeService("WRONG_CLIENT", BundleFixtureFactory.TestKey);
            svc.Initialize();

            Assert.Equal(Tier.Free, acc.Tier);
        }
        finally
        {
            TryDelete(bundlePath);
            TryDelete(freePath);
        }
    }

    // ── Wrong key → falls back to Free ──────────────────────────────────────

    [Fact]
    public void Initialize_WrongKey_FallsBackToFree()
    {
        if (!OperatingSystem.IsWindows()) return;

        var bundlePath = WriteFullBundle(_installDir, BundleFixtureFactory.TestClientName, BundleFixtureFactory.TestKey);
        var freePath = WriteFreeBundle();

        try
        {
            var badKey = (byte[])BundleFixtureFactory.TestKey.Clone();
            badKey[0] ^= 0xFF; // flip one byte

            var (svc, acc) = MakeService(BundleFixtureFactory.TestClientName, badKey);
            svc.Initialize();

            Assert.Equal(Tier.Free, acc.Tier);
        }
        finally
        {
            TryDelete(bundlePath);
            TryDelete(freePath);
        }
    }

    // ── No bundle file → falls back to Free ─────────────────────────────────

    [Fact]
    public void Initialize_NoBundleFile_FallsBackToFree()
    {
        if (!OperatingSystem.IsWindows()) return;

        var freePath = WriteFreeBundle();

        try
        {
            // No .aesgcm present — free-bundle.dat still present
            var (svc, acc) = MakeService(BundleFixtureFactory.TestClientName, BundleFixtureFactory.TestKey);
            svc.Initialize();

            Assert.Equal(Tier.Free, acc.Tier);
            Assert.True(acc.IsUnlocked); // Free bundle loaded
        }
        finally
        {
            TryDelete(freePath);
        }
    }

    // ── Both bundles missing → IsUnlocked=false, no crash ───────────────────

    [Fact]
    public void Initialize_BothBundlesMissing_IsUnlockedFalse_NoCrash()
    {
        if (!OperatingSystem.IsWindows()) return;

        // No .aesgcm and no free-bundle.dat; clear any persisted license
        var acc = new BundleAccessor();
        var svc = new LicenseService(NullLogger<LicenseService>.Instance, new UserSettingsService(), acc);
        svc.Deactivate(); // ensures user-settings has no license
        svc.Initialize();

        Assert.False(acc.IsUnlocked);
        Assert.Equal(Tier.Free, acc.Tier); // tier falls back to Free even when bundle missing
    }

    // ── TryActivate happy path ───────────────────────────────────────────────

    [Fact]
    public void TryActivate_ValidKey_ReturnsSuccess()
    {
        if (!OperatingSystem.IsWindows()) return;

        var bundlePath = WriteFullBundle(_installDir, BundleFixtureFactory.TestClientName, BundleFixtureFactory.TestKey);
        var freePath = WriteFreeBundle();

        try
        {
            var userSettings = new UserSettingsService();
            // Pre-clear any stale license
            userSettings.ClearLicense();

            var acc = new BundleAccessor();
            var svc = new LicenseService(NullLogger<LicenseService>.Instance, userSettings, acc);

            var keyB64 = Convert.ToBase64String(BundleFixtureFactory.TestKey);
            var result = svc.TryActivate(BundleFixtureFactory.TestClientName, keyB64);

            Assert.True(result.Success);
            Assert.Equal(Tier.Full, result.ResolvedTier);
            Assert.Null(result.ErrorMessage);

            // Clean up persisted license
            userSettings.ClearLicense();
        }
        finally
        {
            TryDelete(bundlePath);
            TryDelete(freePath);
        }
    }

    // ── Deactivate clears state ──────────────────────────────────────────────

    [Fact]
    public void Deactivate_AfterActivation_RevertsToFreeOrUnlocked()
    {
        if (!OperatingSystem.IsWindows()) return;

        var bundlePath = WriteFullBundle(_installDir, BundleFixtureFactory.TestClientName, BundleFixtureFactory.TestKey);
        var freePath = WriteFreeBundle();

        try
        {
            var userSettings = new UserSettingsService();
            userSettings.ClearLicense();

            var acc = new BundleAccessor();
            var svc = new LicenseService(NullLogger<LicenseService>.Instance, userSettings, acc);

            // Activate
            svc.TryActivate(BundleFixtureFactory.TestClientName,
                Convert.ToBase64String(BundleFixtureFactory.TestKey));
            Assert.Equal(Tier.Full, acc.Tier);

            // Deactivate
            svc.Deactivate();
            Assert.NotEqual(Tier.Full, acc.Tier); // must have reverted

            // Ensure no stale license remains
            var (savedName, savedKey) = userSettings.GetSavedLicense();
            Assert.Null(savedName);
            Assert.Null(savedKey);
        }
        finally
        {
            TryDelete(bundlePath);
            TryDelete(freePath);
        }
    }

    // ── DPAPI roundtrip (Windows-only) ───────────────────────────────────────

    [Fact]
    public void DpapiRoundtrip_WrapsAndUnwrapsKey()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI only on Windows

        var key = BundleFixtureFactory.TestKey;
        var entropy = System.Text.Encoding.UTF8.GetBytes("SQLTriage.License.v1");
        var wrapped = System.Security.Cryptography.ProtectedData.Protect(
            key, entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        var unwrapped = System.Security.Cryptography.ProtectedData.Unprotect(
            wrapped, entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);

        Assert.Equal(key, unwrapped);
    }

    // ── BundleStateChanged fires on Initialize ───────────────────────────────

    [Fact]
    public void Initialize_FiresBundleStateChanged()
    {
        var freePath = WriteFreeBundle();

        try
        {
            var acc = new BundleAccessor();
            var svc = new LicenseService(NullLogger<LicenseService>.Instance, new UserSettingsService(), acc);
            var fired = false;
            acc.BundleStateChanged += (_, _) => fired = true;

            svc.Initialize();

            Assert.True(fired);
        }
        finally
        {
            TryDelete(freePath);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private (LicenseService svc, BundleAccessor acc) MakeService(string clientName, byte[] rawKey)
    {
        var userSettings = new UserSettingsService();
        userSettings.ClearLicense();

        // Persist the DPAPI-wrapped key
        var entropy = System.Text.Encoding.UTF8.GetBytes("SQLTriage.License.v1");
        var wrapped = System.Security.Cryptography.ProtectedData.Protect(
            rawKey, entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        userSettings.SaveLicense(clientName, wrapped);

        var acc = new BundleAccessor();
        var svc = new LicenseService(NullLogger<LicenseService>.Instance, userSettings, acc);
        return (svc, acc);
    }

    private string WriteFullBundle(string dir, string? clientName = null, byte[]? key = null)
    {
        clientName ??= BundleFixtureFactory.TestClientName;
        key ??= BundleFixtureFactory.TestKey;

        var path = Path.Combine(dir, $"test-{Guid.NewGuid():N}.aesgcm");
        var manifest = BundleFixtureFactory.MakeFullManifest(clientName);
        var aad = AadBuilder.Build(clientName, "Full", 1, BundleFixtureFactory.TestBuildNumber);
        var wireBytes = BundleCrypto.EncryptManifest(manifest, key, aad);
        File.WriteAllBytes(path, wireBytes);
        _createdFiles.Add(path);
        return path;
    }

    private string WriteFreeBundle()
    {
        var path = BundleFixtureFactory.WriteFreeBundle(_installDir);
        _createdFiles.Add(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }
}

