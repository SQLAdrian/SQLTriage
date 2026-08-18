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
/// Logic tests for the contract exercised by FreeStateBanner.razor.
/// The banner renders when <see cref="IBundleAccessor.Tier"/> == <see cref="Tier.Free"/>
/// and hides when the tier transitions to Full (via BundleStateChanged).
/// </summary>
public class FreeStateBannerTests : IDisposable
{
    private readonly string _installDir;
    private readonly List<string> _createdFiles = new();

    public FreeStateBannerTests()
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
    }

    // ── Banner should render: Tier is Free before any activation ────────────

    [Fact]
    public void BundleAccessor_WithFreeBundle_TierIsFree()
    {
        var freePath = WriteFreeBundle();
        try
        {
            var acc = new BundleAccessor();
            var svc = new LicenseService(NullLogger<LicenseService>.Instance,
                new UserSettingsService(), acc);
            svc.Initialize();

            // FreeStateBanner checks this — should render when true
            Assert.Equal(Tier.Free, acc.Tier);
        }
        finally
        {
            CleanUp(freePath);
        }
    }

    // ── Banner should hide: Tier becomes Full after activation ───────────────

    [Fact]
    public void BundleAccessor_AfterFullActivation_TierIsFull()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI required

        var bundlePath = WriteFullBundle(_installDir);
        var freePath = WriteFreeBundle();

        try
        {
            var userSettings = new UserSettingsService();
            userSettings.ClearLicense();
            var acc = new BundleAccessor();
            var svc = new LicenseService(NullLogger<LicenseService>.Instance, userSettings, acc);

            svc.TryActivate(
                BundleFixtureFactory.TestClientName,
                Convert.ToBase64String(BundleFixtureFactory.TestKey));

            // After activation banner should NOT render (Tier != Free)
            Assert.Equal(Tier.Full, acc.Tier);

            userSettings.ClearLicense();
        }
        finally
        {
            CleanUp(bundlePath, freePath);
        }
    }

    // ── BundleStateChanged fires on deactivation (banner re-appears) ─────────

    [Fact]
    public void Deactivate_FiresBundleStateChanged_BannerCanReappear()
    {
        if (!OperatingSystem.IsWindows()) return;

        var bundlePath = WriteFullBundle(_installDir);
        var freePath = WriteFreeBundle();

        try
        {
            var userSettings = new UserSettingsService();
            userSettings.ClearLicense();
            var acc = new BundleAccessor();
            var svc = new LicenseService(NullLogger<LicenseService>.Instance, userSettings, acc);

            svc.TryActivate(
                BundleFixtureFactory.TestClientName,
                Convert.ToBase64String(BundleFixtureFactory.TestKey));
            Assert.Equal(Tier.Full, acc.Tier);

            // Banner subscribes to this and calls StateHasChanged
            var stateChangeFired = false;
            acc.BundleStateChanged += (_, _) => stateChangeFired = true;

            svc.Deactivate();

            Assert.True(stateChangeFired, "BundleStateChanged must fire so FreeStateBanner re-evaluates visibility.");
            Assert.NotEqual(Tier.Full, acc.Tier);
        }
        finally
        {
            CleanUp(bundlePath, freePath);
        }
    }

    // ── No bundle at all: Tier is still Free (banner renders, not hidden) ────

    [Fact]
    public void NoBundleFiles_TierIsFree_BannerWouldRender()
    {
        var acc = new BundleAccessor();
        var svc = new LicenseService(NullLogger<LicenseService>.Instance,
            new UserSettingsService(), acc);
        svc.Deactivate(); // clear any stale state
        svc.Initialize();

        // Banner render condition: Tier == Free
        Assert.Equal(Tier.Free, acc.Tier);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string WriteFullBundle(string dir, string? clientName = null, byte[]? key = null)
    {
        clientName ??= BundleFixtureFactory.TestClientName;
        key ??= BundleFixtureFactory.TestKey;

        var path = Path.Combine(dir, $"banner-test-{Guid.NewGuid():N}.aesgcm");
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
