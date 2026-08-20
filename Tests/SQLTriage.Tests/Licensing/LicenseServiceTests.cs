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

    /// <summary>
    /// Settings directory for THIS test instance. xunit constructs the class once per test method,
    /// so every method gets its own file and no method can see another's licence state.
    /// </summary>
    private readonly string _settingsDir =
        Path.Combine(Path.GetTempPath(), "sqlt-license-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// A <see cref="UserSettingsService"/> over this instance's temp file.
    ///
    /// <para><b>Never <c>new UserSettingsService()</c> in this file.</b> These tests call
    /// <c>ClearLicense()</c>, <c>SaveLicense(...)</c> and <c>TryActivate(...)</c>, all of which
    /// WRITE the settings file. With the parameterless constructor that file was the developer's
    /// real <c>%APPDATA%\SQLTriage\user-settings.json</c>: measured on 2026-08-04, this suite had
    /// been writing a fixture licence named <c>TEST_CLIENT_NEVER_PROD</c> into the operator's real
    /// profile and clearing it again on every run, for weeks, unnoticed.</para>
    /// </summary>
    private UserSettingsService NewUserSettings()
        => new UserSettingsService(Path.Combine(_settingsDir, "user-settings.json"));

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

        try { if (Directory.Exists(_settingsDir)) Directory.Delete(_settingsDir, recursive: true); }
        catch { /* best-effort */ }
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

    // ── B1: license expiry ───────────────────────────────────────────────────

    [Fact]
    public void Initialize_UnexpiredFullBundle_UnlocksFull()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI required

        var bundlePath = WriteFullBundle(_installDir, BundleFixtureFactory.TestClientName,
            BundleFixtureFactory.TestKey, licenseExpiryUtc: Iso(DateTime.UtcNow.AddDays(1)));
        var freePath = WriteFreeBundle();

        try
        {
            var (svc, acc) = MakeService(BundleFixtureFactory.TestClientName, BundleFixtureFactory.TestKey);
            svc.Initialize();

            Assert.True(acc.IsUnlocked);           // real decrypt succeeded
            Assert.Equal(Tier.Full, acc.Tier);
            Assert.Null(svc.FullExpiredOn);         // not expired
        }
        finally
        {
            TryDelete(bundlePath);
            TryDelete(freePath);
        }
    }

    [Fact]
    public void Initialize_ExpiredFullBundle_RevertsToFree_WithExpiryMarker()
    {
        if (!OperatingSystem.IsWindows()) return;

        var expiredAt = DateTime.UtcNow.AddDays(-1);
        var bundlePath = WriteFullBundle(_installDir, BundleFixtureFactory.TestClientName,
            BundleFixtureFactory.TestKey, licenseExpiryUtc: Iso(expiredAt));
        var freePath = WriteFreeBundle();

        try
        {
            var (svc, acc) = MakeService(BundleFixtureFactory.TestClientName, BundleFixtureFactory.TestKey);
            svc.Initialize();

            // Valid bundle but past expiry → NOT Full; lands on Free with the renew marker set.
            Assert.Equal(Tier.Free, acc.Tier);
            Assert.NotNull(svc.FullExpiredOn);
            Assert.Equal(expiredAt, svc.FullExpiredOn!.Value, TimeSpan.FromSeconds(1));
        }
        finally
        {
            TryDelete(bundlePath);
            TryDelete(freePath);
        }
    }

    [Fact]
    public void Initialize_NullExpiryFullBundle_IsPerpetual()
    {
        if (!OperatingSystem.IsWindows()) return;

        // licenseExpiryUtc null = legacy/perpetual bundle — MUST fail-OPEN (unlock succeeds).
        var bundlePath = WriteFullBundle(_installDir, BundleFixtureFactory.TestClientName,
            BundleFixtureFactory.TestKey, licenseExpiryUtc: null);
        var freePath = WriteFreeBundle();

        try
        {
            var (svc, acc) = MakeService(BundleFixtureFactory.TestClientName, BundleFixtureFactory.TestKey);
            svc.Initialize();

            Assert.True(acc.IsUnlocked);
            Assert.Equal(Tier.Full, acc.Tier);
            Assert.Null(svc.FullExpiredOn);
        }
        finally
        {
            TryDelete(bundlePath);
            TryDelete(freePath);
        }
    }

    // ── B2: license id round-trips through the manifest ──────────────────────

    [Fact]
    public void Initialize_FullBundle_RoundTripsLicenseId()
    {
        if (!OperatingSystem.IsWindows()) return;

        var id = "11111111-2222-3333-4444-555555555555";
        var bundlePath = WriteFullBundle(_installDir, BundleFixtureFactory.TestClientName,
            BundleFixtureFactory.TestKey, licenseId: id);
        var freePath = WriteFreeBundle();

        try
        {
            var (svc, acc) = MakeService(BundleFixtureFactory.TestClientName, BundleFixtureFactory.TestKey);
            svc.Initialize();

            Assert.True(acc.IsUnlocked);
            Assert.Equal(Tier.Full, acc.Tier);
            Assert.Equal(id, acc.LicenseId);   // survived encrypt → decrypt intact
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
        var svc = new LicenseService(NullLogger<LicenseService>.Instance, NewUserSettings(), acc);
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
            var userSettings = NewUserSettings();
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
            var userSettings = NewUserSettings();
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
            var svc = new LicenseService(NullLogger<LicenseService>.Instance, NewUserSettings(), acc);
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
        var userSettings = NewUserSettings();
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

    private string WriteFullBundle(string dir, string? clientName = null, byte[]? key = null,
        string? licenseExpiryUtc = null, string? licenseId = null)
    {
        clientName ??= BundleFixtureFactory.TestClientName;
        key ??= BundleFixtureFactory.TestKey;

        var path = Path.Combine(dir, $"test-{Guid.NewGuid():N}.aesgcm");
        var manifest = BundleFixtureFactory.MakeFullManifest(clientName, licenseExpiryUtc, licenseId);
        var aad = AadBuilder.Build(clientName, "Full", 1, BundleFixtureFactory.TestBuildNumber);
        var wireBytes = BundleCrypto.EncryptManifest(manifest, key, aad);
        File.WriteAllBytes(path, wireBytes);
        _createdFiles.Add(path);
        return path;
    }

    private static string Iso(DateTime utc) =>
        utc.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

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

