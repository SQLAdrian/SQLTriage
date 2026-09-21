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
/// Logic tests for the contract exercised by ActivateFullAuditCard.razor.
/// Tests call LicenseService.TryActivate and Deactivate directly with a real
/// in-memory bundle, confirming success/failure shape matches what the card handles.
/// </summary>
public class ActivateFullAuditCardTests : IDisposable
{
    private readonly string _installDir;
    private readonly List<string> _createdFiles = new();

    /// <summary>Settings directory for THIS test instance (xunit builds the class per test method).</summary>
    private readonly string _settingsDir =
        Path.Combine(Path.GetTempPath(), "sqlt-activatecard-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// A <see cref="UserSettingsService"/> over this instance's temp file.
    ///
    /// <para><b>Never <c>new UserSettingsService()</c> in this file</b> — these tests call
    /// <c>ClearLicense()</c> and <c>TryActivate(...)</c>, which WRITE. Until 2026-08-04 that write
    /// landed on the developer's real <c>%APPDATA%\SQLTriage\user-settings.json</c>, where it left
    /// a fixture licence named <c>TEST_CLIENT_NEVER_PROD</c>.</para>
    /// </summary>
    private UserSettingsService NewUserSettings()
        => new UserSettingsService(Path.Combine(_settingsDir, "user-settings.json"));

    public ActivateFullAuditCardTests()
    {
        _installDir = AppContext.BaseDirectory;
        Directory.CreateDirectory(Path.Combine(_installDir, "Config"));
        WriteSyntheticVersionFile();
    }

    private void WriteSyntheticVersionFile()
    {
        var configDir = Path.Combine(_installDir, "Config");
        Directory.CreateDirectory(configDir);
        var path = Path.Combine(configDir, "version.json");
        var versionJson = $"{{\"version\":\"0.90.2\",\"buildNumber\":{BundleFixtureFactory.TestBuildNumber}}}";
        File.WriteAllText(path, versionJson);
    }

    public void Dispose()
    {
        foreach (var f in _createdFiles)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best-effort */ }
        }

        try { if (Directory.Exists(_settingsDir)) Directory.Delete(_settingsDir, recursive: true); }
        catch { /* best-effort */ }
    }

    // ── TryActivate returns Success=true when key and name match the bundle ──

    [Fact]
    public void TryActivate_ValidNameAndKey_ReturnsSuccessResult()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI required

        var bundlePath = WriteFullBundle(_installDir);
        var freePath = WriteFreeBundle();

        try
        {
            var (svc, acc) = MakeService();
            var result = svc.TryActivate(
                BundleFixtureFactory.TestClientName,
                Convert.ToBase64String(BundleFixtureFactory.TestKey));

            // Card shows toast with null ErrorMessage on success
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(Tier.Full, result.ResolvedTier);
            Assert.Null(result.ErrorMessage);
        }
        finally
        {
            CleanUp(bundlePath, freePath);
        }
    }

    // ── TryActivate returns Success=false with ErrorMessage when key is wrong ──

    [Fact]
    public void TryActivate_WrongKey_ReturnsFailureWithErrorMessage()
    {
        if (!OperatingSystem.IsWindows()) return;

        var bundlePath = WriteFullBundle(_installDir);
        var freePath = WriteFreeBundle();

        try
        {
            var (svc, _) = MakeService();
            var badKey = new string('A', 44); // valid Base64 length but wrong bytes
            var result = svc.TryActivate(BundleFixtureFactory.TestClientName, badKey);

            // Card shows error toast with result.ErrorMessage
            Assert.False(result.Success);
            Assert.NotNull(result.ErrorMessage);
            Assert.True(result.ErrorMessage!.Length > 0);
        }
        finally
        {
            CleanUp(bundlePath, freePath);
        }
    }

    // ── TryActivate rejects empty customer name ──────────────────────────────

    [Fact]
    public void TryActivate_EmptyCustomerName_ReturnsValidationError()
    {
        if (!OperatingSystem.IsWindows()) return;

        var freePath = WriteFreeBundle();
        try
        {
            var (svc, _) = MakeService();
            var result = svc.TryActivate("   ", Convert.ToBase64String(BundleFixtureFactory.TestKey));

            Assert.False(result.Success);
            Assert.NotNull(result.ErrorMessage);
        }
        finally
        {
            CleanUp(freePath);
        }
    }

    // ── TryActivate rejects empty license key ───────────────────────────────

    [Fact]
    public void TryActivate_EmptyLicenseKey_ReturnsValidationError()
    {
        if (!OperatingSystem.IsWindows()) return;

        var freePath = WriteFreeBundle();
        try
        {
            var (svc, _) = MakeService();
            var result = svc.TryActivate(BundleFixtureFactory.TestClientName, "");

            Assert.False(result.Success);
            Assert.NotNull(result.ErrorMessage);
        }
        finally
        {
            CleanUp(freePath);
        }
    }

    // ── Deactivate after activation reverts tier ─────────────────────────────

    [Fact]
    public void Deactivate_AfterActivation_TierBecomesNonFull()
    {
        if (!OperatingSystem.IsWindows()) return;

        var bundlePath = WriteFullBundle(_installDir);
        var freePath = WriteFreeBundle();

        try
        {
            var (svc, acc) = MakeService();
            svc.TryActivate(
                BundleFixtureFactory.TestClientName,
                Convert.ToBase64String(BundleFixtureFactory.TestKey));
            Assert.Equal(Tier.Full, acc.Tier);

            svc.Deactivate();

            // Card should hide the Deactivate button and show Free tier
            Assert.NotEqual(Tier.Full, acc.Tier);
        }
        finally
        {
            CleanUp(bundlePath, freePath);
        }
    }

    // ── BundleStateChanged fires on activation (card re-renders on it) ──────

    [Fact]
    public void TryActivate_FiresBundleStateChanged()
    {
        if (!OperatingSystem.IsWindows()) return;

        var bundlePath = WriteFullBundle(_installDir);
        var freePath = WriteFreeBundle();

        try
        {
            var (svc, acc) = MakeService();
            var eventFired = false;
            acc.BundleStateChanged += (_, _) => eventFired = true;

            svc.TryActivate(
                BundleFixtureFactory.TestClientName,
                Convert.ToBase64String(BundleFixtureFactory.TestKey));

            Assert.True(eventFired, "BundleStateChanged must fire so ActivateFullAuditCard re-renders.");
        }
        finally
        {
            CleanUp(bundlePath, freePath);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private (LicenseService svc, BundleAccessor acc) MakeService()
    {
        var userSettings = NewUserSettings();
        userSettings.ClearLicense();
        var acc = new BundleAccessor();
        var svc = new LicenseService(NullLogger<LicenseService>.Instance, userSettings, acc);
        return (svc, acc);
    }

    private string WriteFullBundle(string dir, string? clientName = null, byte[]? key = null)
    {
        clientName ??= BundleFixtureFactory.TestClientName;
        key ??= BundleFixtureFactory.TestKey;

        var path = Path.Combine(dir, $"card-test-{Guid.NewGuid():N}.aesgcm");
        var manifest = BundleFixtureFactory.MakeFullManifest(clientName);
        var aad = AadBuilder.Build(clientName, "Full", 1,
                            BundleFixtureFactory.TestBuildNumber);
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

    private static void CleanUp(params string[] paths)
    {
        foreach (var p in paths)
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
    }
}
