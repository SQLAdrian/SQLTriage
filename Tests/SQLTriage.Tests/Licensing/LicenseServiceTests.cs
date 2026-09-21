/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System.IO;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// platform-r1-03 (honesty-hunt 2026-08-25). A valid bundle past its expiry was once reported
    /// everywhere as a decryption/name failure — the client was told to re-check spelling and key
    /// for a licence that had simply lapsed. This drives the REAL expired-bundle branch and pins
    /// that its message names the lapse and the remedy (a renewed bundle), and does NOT recycle the
    /// generic "did not open with the saved customer name" misattribution. Verified already-resolved
    /// on current main; kept as the regression guard the finding asked for.
    /// </summary>
    [Fact]
    public void ExpiredBundle_MessageNamesTheLapse_NotASpellingOrKeyProblem()
    {
        if (!OperatingSystem.IsWindows()) return;

        var message = DriveFailure(FullUnlockFailureReason.Expired);

        Assert.Contains("expired", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("renew", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("did not open with the saved customer name", message,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("character for character", message, StringComparison.OrdinalIgnoreCase);
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

    // ── The reason register: every failing branch NAMES itself ──────────────
    //
    // Until 2026-08-25 a failed Full unlock produced a log line and nothing else. The app booted
    // on Free, the Settings badge said "Not Activated", and the only failure text the activation
    // card could show was a hard-coded list of three possible causes printed for every failure
    // alike. MEASURED on the maintainer's own box that day: the saved key was the one minted
    // 2026-08-06 and the installed bundle was the one minted 2026-08-17, so the true cause was
    // "that key is not this bundle's key" — a fact the service had in hand and could not say.

    [Fact]
    public void Initialize_WrongKey_RecordsWhyRatherThanFallingSilent()
    {
        if (!OperatingSystem.IsWindows()) return;

        var bundlePath = WriteFullBundle(_installDir, BundleFixtureFactory.TestClientName, BundleFixtureFactory.TestKey);
        var freePath = WriteFreeBundle();

        try
        {
            var badKey = (byte[])BundleFixtureFactory.TestKey.Clone();
            badKey[0] ^= 0xFF;

            var (svc, acc) = MakeService(BundleFixtureFactory.TestClientName, badKey);
            svc.Initialize();

            Assert.Equal(Tier.Free, acc.Tier);

            var failure = svc.LastFullFailure;
            Assert.NotNull(failure);
            Assert.Equal(FullUnlockFailureReason.NoBundleDecrypted, failure!.Reason);

            // It names the file it tried and the customer name it tried it with — the two facts
            // the operator has to compare against their licence email.
            Assert.Contains(Path.GetFileName(bundlePath), failure.Message, StringComparison.Ordinal);
            Assert.Contains(BundleFixtureFactory.TestClientName, failure.Message, StringComparison.Ordinal);

            // And it names the re-issue case, which is the one that produced a working install
            // that silently stopped working. A message that only said "wrong key" would send the
            // operator back to the same stale email.
            //
            // Asserted on the two FACTS rather than on the shipped capitalisation. This read
            // Contains("NEW key") and went red on 2026-08-25 when the sentence was rewritten to
            // carry a third cause — a copy edit that changed nothing this test is about.
            Assert.Contains("re-issue", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("new key", failure.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(bundlePath);
            TryDelete(freePath);
        }
    }

    [Fact]
    public void Initialize_NoBundleFile_RecordsTheFolderItLookedIn()
    {
        if (!OperatingSystem.IsWindows()) return;

        var freePath = WriteFreeBundle();

        try
        {
            var (svc, _) = MakeService(BundleFixtureFactory.TestClientName, BundleFixtureFactory.TestKey);
            svc.Initialize();

            var failure = svc.LastFullFailure;
            Assert.NotNull(failure);
            Assert.Equal(FullUnlockFailureReason.NoBundleFile, failure!.Reason);

            // The folder, not "next to the .exe" — an operator running from a worktree, a publish
            // folder and an installed service has three of those and cannot tell which one is meant.
            Assert.Contains(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
                failure.Message.TrimEnd(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(freePath);
        }
    }

    [Fact]
    public void Initialize_NoSavedLicense_RecordsThatNothingWasEverActivated()
    {
        if (!OperatingSystem.IsWindows()) return;

        var freePath = WriteFreeBundle();

        try
        {
            var userSettings = NewUserSettings();
            userSettings.ClearLicense();
            var acc = new BundleAccessor();
            var svc = new LicenseService(NullLogger<LicenseService>.Instance, userSettings, acc);

            svc.Initialize();

            Assert.NotNull(svc.LastFullFailure);
            Assert.Equal(FullUnlockFailureReason.NoSavedLicense, svc.LastFullFailure!.Reason);
        }
        finally
        {
            TryDelete(freePath);
        }
    }

    [Fact]
    public void Initialize_AfterASuccessfulUnlock_HoldsNoFailure()
    {
        if (!OperatingSystem.IsWindows()) return;

        var bundlePath = WriteFullBundle(_installDir);
        var freePath = WriteFreeBundle();

        try
        {
            var (svc, acc) = MakeService(BundleFixtureFactory.TestClientName, BundleFixtureFactory.TestKey);
            svc.Initialize();

            Assert.Equal(Tier.Full, acc.Tier);

            // A stale reason left behind by an earlier attempt would render a red "Full Audit is
            // not active" panel on a working install.
            Assert.Null(svc.LastFullFailure);
        }
        finally
        {
            TryDelete(bundlePath);
            TryDelete(freePath);
        }
    }

    [Fact]
    public void TryActivate_WrongKey_ReturnsTheRecordedReasonNotAGuessList()
    {
        if (!OperatingSystem.IsWindows()) return;

        var bundlePath = WriteFullBundle(_installDir, BundleFixtureFactory.TestClientName, BundleFixtureFactory.TestKey);
        var freePath = WriteFreeBundle();

        try
        {
            var acc = new BundleAccessor();
            var settings = NewUserSettings();
            settings.ClearLicense();
            var svc = new LicenseService(NullLogger<LicenseService>.Instance, settings, acc);

            var badKey = (byte[])BundleFixtureFactory.TestKey.Clone();
            badKey[5] ^= 0xFF;

            var result = svc.TryActivate(BundleFixtureFactory.TestClientName, Convert.ToBase64String(badKey));

            Assert.False(result.Success);
            Assert.NotNull(result.ErrorMessage);

            // The old text was "Bundle decryption failed. Possible causes: …" — three guesses,
            // printed identically whether the bundle was absent, the name misspelt or the key
            // stale. It must not come back.
            Assert.DoesNotContain("Possible causes", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

            // And the reason survives TryActivate's own cleanup: it clears the bad licence and
            // re-Initializes, which re-runs the unlock and would otherwise overwrite the reason
            // with "nothing was ever activated".
            Assert.Contains(Path.GetFileName(bundlePath), result.ErrorMessage!, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(bundlePath);
            TryDelete(freePath);
        }
    }

    // ── Installing the bundle FILE from the UI ──────────────────────────────
    //
    // The activation card printed "save the file into this folder" and had no way to do it. That
    // was the one step of activation the UI could not perform, and the step whose omission put
    // the install on Free with nothing on screen saying why.

    [Fact]
    public void InstallBundleFile_PutsAValidBundleWhereTheUnlockLooks()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sourceDir = Path.Combine(Path.GetTempPath(), "sqlt-bundle-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDir);

        var sourcePath = Path.Combine(sourceDir, $"install-{Guid.NewGuid():N}.aesgcm");
        var manifest = BundleFixtureFactory.MakeFullManifest(BundleFixtureFactory.TestClientName);
        var aad = AadBuilder.Build(BundleFixtureFactory.TestClientName, "Full", 1, BundleFixtureFactory.TestBuildNumber);
        File.WriteAllBytes(sourcePath, BundleCrypto.EncryptManifest(manifest, BundleFixtureFactory.TestKey, aad));

        var landed = Path.Combine(_installDir, Path.GetFileName(sourcePath));
        var freePath = WriteFreeBundle();

        try
        {
            var (svc, acc) = MakeService(BundleFixtureFactory.TestClientName, BundleFixtureFactory.TestKey);

            // Before: the unlock has no file to try.
            svc.Initialize();
            Assert.Equal(Tier.Free, acc.Tier);
            Assert.Equal(FullUnlockFailureReason.NoBundleFile, svc.LastFullFailure!.Reason);

            var install = svc.InstallBundleFile(sourcePath);
            Assert.True(install.Success, install.Message);
            Assert.True(File.Exists(landed));

            // After: the SAME unlock, unchanged, now finds it. The install is proved by the
            // reader, not by asserting a File.Copy happened.
            svc.Initialize();
            Assert.Equal(Tier.Full, acc.Tier);
            Assert.Null(svc.LastFullFailure);
        }
        finally
        {
            TryDelete(landed);
            TryDelete(freePath);
            try { Directory.Delete(sourceDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// THE ONE DESTRUCTIVE ACT THIS SURFACE HAD IS GONE. The copy ran with <c>overwrite: true</c>,
    /// so a caller who named a shape-valid file whose NAME matched the installed bundle replaced
    /// the working bundle, and the next boot fell back to Free. The caller on an unconfigured
    /// install is any loopback caller through break-glass, so this was reachable before anyone had
    /// signed in. Refusing costs the operator nothing: the unlock scans every <c>*.aesgcm</c> in
    /// the folder, so a re-issued bundle installs beside the old one under its own name.
    ///
    /// <para>Both arms are measured through the reader, not by asserting a copy did not happen:
    /// the bytes on disk are compared, and the unlock that was working before still reaches Full
    /// after the refusal.</para>
    /// </summary>
    [Fact]
    public void InstallBundleFile_NeverReplacesAnInstalledBundle()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sourceDir = Path.Combine(Path.GetTempPath(), "sqlt-bundle-replace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDir);

        // The working install: a real bundle, in the folder the unlock reads.
        var name = $"working-{Guid.NewGuid():N}.aesgcm";
        var landed = Path.Combine(_installDir, name);
        var aad = AadBuilder.Build(BundleFixtureFactory.TestClientName, "Full", 1, BundleFixtureFactory.TestBuildNumber);
        File.WriteAllBytes(landed, BundleCrypto.EncryptManifest(
            BundleFixtureFactory.MakeFullManifest(BundleFixtureFactory.TestClientName),
            BundleFixtureFactory.TestKey, aad));
        var before = File.ReadAllBytes(landed);

        // The replacement: a DIFFERENT bundle under the SAME file name. It passes the shape check,
        // so nothing but the overwrite rule stands between it and the working file.
        var sourcePath = Path.Combine(sourceDir, name);
        var otherKey = (byte[])BundleFixtureFactory.TestKey.Clone();
        otherKey[0] ^= 0xFF;
        File.WriteAllBytes(sourcePath, BundleCrypto.EncryptManifest(
            BundleFixtureFactory.MakeFullManifest(BundleFixtureFactory.TestClientName),
            otherKey, aad));

        var freePath = WriteFreeBundle();

        try
        {
            var (svc, acc) = MakeService(BundleFixtureFactory.TestClientName, BundleFixtureFactory.TestKey);

            var result = svc.InstallBundleFile(sourcePath);

            Assert.False(result.Success, result.Message);
            Assert.Contains("already installed", result.Message, StringComparison.OrdinalIgnoreCase);
            // It names the way forward, because "no" without a next step is a support call.
            Assert.Contains("delete it", result.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(before, File.ReadAllBytes(landed));

            // And the install it was pointed at is still an install: the same unlock still reaches
            // Full. Byte-equality alone would pass if the file were replaced by an identical copy.
            svc.Initialize();
            Assert.Equal(Tier.Full, acc.Tier);
            Assert.Null(svc.LastFullFailure);
        }
        finally
        {
            TryDelete(landed);
            TryDelete(freePath);
            try { Directory.Delete(sourceDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void InstallBundleFile_RefusesAFileThatIsNotABundleAndSaysWhy()
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), "sqlt-bundle-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDir);

        var name = $"not-a-bundle-{Guid.NewGuid():N}.aesgcm";
        var sourcePath = Path.Combine(sourceDir, name);
        File.WriteAllBytes(sourcePath, System.Text.Encoding.UTF8.GetBytes(new string('x', 4096)));

        var landed = Path.Combine(_installDir, name);

        try
        {
            var acc = new BundleAccessor();
            var svc = new LicenseService(NullLogger<LicenseService>.Instance, NewUserSettings(), acc);

            var result = svc.InstallBundleFile(sourcePath);

            Assert.False(result.Success);
            Assert.Contains("bundle marker", result.Message, StringComparison.OrdinalIgnoreCase);

            // A refused install must not leave the file in place. A rejected file sitting in the
            // install folder would then be reported by the unlock as a candidate that would not
            // decrypt, which is a different and wrong diagnosis.
            Assert.False(File.Exists(landed));
        }
        finally
        {
            TryDelete(landed);
            try { Directory.Delete(sourceDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void InstallBundleFile_MissingSourceNamesThePathAndTheMachine()
    {
        var acc = new BundleAccessor();
        var svc = new LicenseService(NullLogger<LicenseService>.Instance, NewUserSettings(), acc);

        var missing = Path.Combine(Path.GetTempPath(), "sqlt-absent-" + Guid.NewGuid().ToString("N") + ".aesgcm");
        var result = svc.InstallBundleFile(missing);

        Assert.False(result.Success);
        Assert.Contains(missing, result.Message, StringComparison.Ordinal);

        // The browser and the server are different machines whenever this is used over the LAN,
        // and a path box that does not say which one it means is a support call.
        Assert.Contains("this machine", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── SEC-2: the locality guard, before any File.* touches the path ────────
    //
    // The install ran File.Exists / new FileInfo / File.OpenRead / File.Copy on whatever the
    // caller typed. On the installed service that identity is NT SERVICE\SQLTriage, and a UNC
    // source (\\host\share\x.aesgcm) makes the FIRST of those — File.Exists — an OUTBOUND SMB
    // authentication to a caller-named host: an NTLM leak / relay, before any content check. The
    // guard now refuses a non-local path first, without touching the filesystem or the network.

    /// <summary>
    /// THE RED→GREEN. A UNC source is refused with the network message, and the discriminator is
    /// the message itself: pre-fix there was no locality branch, so the only replies this method
    /// could give a UNC path were "No file at …" (after <c>File.Exists</c> on the UNC path had
    /// already authenticated outbound) or a hang against the dead host. The "network" reply exists
    /// ONLY on the guarded path, so seeing it — and never "No file at" — proves the guard
    /// short-circuited before a single <c>File.*</c> call reached the UNC name.
    /// </summary>
    [Theory]
    [InlineData(@"\\sqlt-nonexistent-sec2-host\share\bundle.aesgcm")]
    [InlineData("//sqlt-nonexistent-sec2-host/share/bundle.aesgcm")]
    public void InstallBundleFile_RefusesAUncPathBeforeAnyFileTouch(string uncPath)
    {
        var svc = new LicenseService(NullLogger<LicenseService>.Instance, NewUserSettings(), new BundleAccessor());

        var result = svc.InstallBundleFile(uncPath);

        Assert.False(result.Success);
        Assert.Contains("network", result.Message, StringComparison.OrdinalIgnoreCase);

        // The pre-fix fall-through. If this appears, File.Exists ran on the UNC path — the exact
        // outbound SMB touch SEC-2 exists to stop — and the guard did not short-circuit.
        Assert.DoesNotContain("No file at", result.Message, StringComparison.Ordinal);

        // Nothing about a refused non-local path may reach the install folder.
        var landed = Path.Combine(_installDir, "bundle.aesgcm");
        Assert.False(File.Exists(landed), "a refused non-local path must not leave a file in the install folder");
    }

    /// <summary>
    /// The guard's shape, proved by mutation directly on the predicate: a UNC path (both slash
    /// styles), a relative path and a drive-relative path are non-local; a fully-qualified local
    /// path is local. This is the string-only decision that runs before any <c>File.*</c> call —
    /// so it is asserted string-only, with no filesystem in the loop.
    /// </summary>
    [Theory]
    [InlineData(@"\\host\share\bundle.aesgcm", false)]   // UNC, backslash
    [InlineData("//host/share/bundle.aesgcm", false)]     // UNC, forward slash
    [InlineData("bundle.aesgcm", false)]                  // relative
    [InlineData(@".\bundle.aesgcm", false)]               // relative, explicit
    [InlineData(@"C:bundle.aesgcm", false)]               // drive-RELATIVE (no separator)
    [InlineData(@"C:\Users\op\Downloads\bundle.aesgcm", true)]   // local absolute, fixed drive
    public void IsLocalBundlePath_JudgesLocalityByShape(string path, bool expectedLocal)
    {
        var isLocal = LicenseService.IsLocalBundlePath(path, out var problem);

        Assert.Equal(expectedLocal, isLocal);
        if (expectedLocal)
            Assert.Equal(string.Empty, problem);
        else
            Assert.NotEqual(string.Empty, problem);
    }

    // ── The reason must survive the cleanup, on BOTH carriers ───────────────
    //
    // TryActivate captured the failure MESSAGE for its return value and let its own cleanup
    // Initialize() overwrite LastFullFailure with NoSavedLicense. ActivateFullAuditCard binds its
    // persistent red panel to that live property, so a failed Activate put the correct sentence
    // inline and the wrong one in the prominent red panel, at the same time. The defence was
    // applied to one surface and not to the other, on the same screen.

    [Fact]
    public void TryActivate_WrongKey_LeavesTheRealReasonOnTheProperty_NotTheCleanupsReason()
    {
        if (!OperatingSystem.IsWindows()) return;

        var bundlePath = WriteFullBundle(_installDir);
        var freePath = WriteFreeBundle();

        try
        {
            var acc = new BundleAccessor();
            var settings = NewUserSettings();
            settings.ClearLicense();
            var svc = new LicenseService(NullLogger<LicenseService>.Instance, settings, acc);

            var badKey = (byte[])BundleFixtureFactory.TestKey.Clone();
            badKey[5] ^= 0xFF;

            var result = svc.TryActivate(BundleFixtureFactory.TestClientName, Convert.ToBase64String(badKey));
            Assert.False(result.Success);

            Assert.NotNull(svc.LastFullFailure);
            Assert.Equal(FullUnlockFailureReason.NoBundleDecrypted, svc.LastFullFailure!.Reason);

            // The two carriers are the SAME sentence. The card renders both at once.
            Assert.Equal(result.ErrorMessage, svc.LastFullFailure.Message);
        }
        finally
        {
            TryDelete(bundlePath);
            TryDelete(freePath);
        }
    }

    // ── The log must say what the UI says ───────────────────────────────────

    /// <summary>
    /// The log line for "none decrypted" asserted a cause: "AAD mismatch — customer name doesn't
    /// match any installed bundle." Measured on this box 2026-08-25, that line was printed while
    /// the saved customer name matched the bundle manifest's clientName character for character
    /// and the real cause was a key from an earlier mint. The corrected reason went to an
    /// in-memory property; the log is the durable record, and it kept the fabricated one.
    /// </summary>
    [Fact]
    public void Initialize_NoBundleDecrypted_LogsWhatIsKnown_NotAFabricatedCause()
    {
        if (!OperatingSystem.IsWindows()) return;

        var bundlePath = WriteFullBundle(_installDir);
        var freePath = WriteFreeBundle();

        try
        {
            var log = new CapturingLogger();
            var settings = NewUserSettings();
            settings.ClearLicense();

            var wrongKey = (byte[])BundleFixtureFactory.TestKey.Clone();
            wrongKey[5] ^= 0xFF;
            var entropy = System.Text.Encoding.UTF8.GetBytes("SQLTriage.License.v1");
            settings.SaveLicense(BundleFixtureFactory.TestClientName,
                System.Security.Cryptography.ProtectedData.Protect(
                    wrongKey, entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser));

            var svc = new LicenseService(log, settings, new BundleAccessor());
            svc.Initialize();

            Assert.Equal(FullUnlockFailureReason.NoBundleDecrypted, svc.LastFullFailure!.Reason);

            var line = Assert.Single(log.Lines.Where(l =>
                l.Contains("none decrypted", StringComparison.OrdinalIgnoreCase)));

            // The fabricated diagnosis, in the wording that shipped.
            Assert.DoesNotContain("AAD mismatch", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("doesn't match any installed bundle", line, StringComparison.OrdinalIgnoreCase);

            // …and the log names the same three indistinguishable causes the UI does, plus the
            // file it tried, so a support engineer reading only the log sees what the operator saw.
            Assert.Contains("damaged", line, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("different issue", line, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Path.GetFileName(bundlePath), line, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(bundlePath);
            TryDelete(freePath);
        }
    }

    // ── A damaged file is the third cause, and it is reachable ──────────────

    /// <summary>
    /// The replacement message named two causes and asserted them ("Check the customer name …
    /// then re-enter the key …"), for a failure with three. This drives the third: the name is
    /// right, the key is right, and the bundle body is damaged. <c>LooksLikeBundle</c> checks the
    /// magic and the wire version, so a truncated or partly downloaded bundle installs cleanly and
    /// fails exactly here — the case the sibling message in <c>BundleCrypto</c> already names.
    /// </summary>
    [Fact]
    public void Initialize_DamagedBundleBody_IsNamedAsAPossibleCause()
    {
        if (!OperatingSystem.IsWindows()) return;

        var bundlePath = WriteFullBundle(_installDir);
        var freePath = WriteFreeBundle();

        try
        {
            // Flip one byte of the CIPHERTEXT, past the header, so the file still passes the shape
            // check and installs — which is the point.
            var wire = File.ReadAllBytes(bundlePath);
            wire[BundleCrypto.HeaderSize + 3] ^= 0xFF;
            File.WriteAllBytes(bundlePath, wire);

            // Same name, same key: both facts the message tells the operator to check are correct.
            var (svc, acc) = MakeService(BundleFixtureFactory.TestClientName, BundleFixtureFactory.TestKey);
            svc.Initialize();

            Assert.Equal(Tier.Free, acc.Tier);
            var message = svc.LastFullFailure!.Message;
            Assert.Equal(FullUnlockFailureReason.NoBundleDecrypted, svc.LastFullFailure.Reason);

            Assert.Contains("damaged", message, StringComparison.OrdinalIgnoreCase);

            // And it must not have become an ASSERTION about which cause it is. The auth tag fails
            // identically for all three; naming one is a fabricated diagnosis.
            Assert.Contains("cannot say which", message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(bundlePath);
            TryDelete(freePath);
        }
    }

    // ── The sentences an operator reads ─────────────────────────────────────

    /// <summary>
    /// VOICE_GUIDE: "One idea per sentence. If you used 'and' twice, split." Mechanical, over the
    /// messages this service actually produces, driven through the real branches rather than
    /// asserted against a literal — a copy rule enforced by quoting the copy is a rule that dies
    /// on the next edit.
    /// </summary>
    [Theory]
    [InlineData(FullUnlockFailureReason.NoSavedLicense)]
    [InlineData(FullUnlockFailureReason.NoBundleFile)]
    [InlineData(FullUnlockFailureReason.NoBundleDecrypted)]
    [InlineData(FullUnlockFailureReason.Expired)]
    public void EveryFailureSentenceHoldsOneIdea(FullUnlockFailureReason reason)
    {
        if (!OperatingSystem.IsWindows()) return;

        var message = DriveFailure(reason);

        foreach (var sentence in message.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var ands = Regex.Matches(sentence, @"(?<![A-Za-z])and(?![A-Za-z])").Count;
            Assert.True(ands < 2,
                $"[{reason}] this sentence carries {ands} 'and's, so it carries more than one idea: "
                + $"\"{sentence.Trim()}.\"");

            var words = WordCount(sentence);
            Assert.True(words <= MaxSentenceWords,
                $"[{reason}] this sentence runs {words} words, over the {MaxSentenceWords}-word ceiling: "
                + $"\"{sentence.Trim()}.\"");
        }
    }

    /// <summary>
    /// The ceiling the sentences an operator reads are held to. VOICE_GUIDE gives the rule in words
    /// ("one idea per sentence", "cut every word you can remove without changing meaning") and no
    /// number, so this is a mechanical proxy for it, set from what the honest sentences on this
    /// service measure: the longest sentence in the other failure branches is the DPAPI one at 23
    /// words. The 'and' count alone did not catch the sentence that failed the gate on 2026-08-25,
    /// because that one carried no "and" at all — it carried three causes in one 56-word sentence
    /// separated by commas. See <see cref="TheCeilingCatchesTheSentenceThatFailedTheGate"/>.
    /// </summary>
    private const int MaxSentenceWords = 25;

    private static int WordCount(string sentence)
        => sentence.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// ANTI-VACUITY. A ceiling that no real sentence ever crossed is a ceiling nobody can show
    /// works. This is the exact copy this service shipped until 2026-08-25, kept as a fixture: the
    /// instrument must call it over the ceiling, and must clear the sentence that replaced it.
    /// </summary>
    [Fact]
    public void TheCeilingCatchesTheSentenceThatFailedTheGate()
    {
        const string shipped =
            "Three things fail the same way here, so this cannot say which one it is: a customer name "
            + "that is not the one the bundle was issued to, a key from a different issue of the bundle "
            + "(a re-issue carries a new key, so an older key will not open it), or a damaged bundle file";

        Assert.True(WordCount(shipped) > MaxSentenceWords,
            $"the fixture measures {WordCount(shipped)} words, which the ceiling would let through");

        const string replacement =
            "A re-issue carries a new key, so an older key will not open the newer bundle";

        Assert.True(WordCount(replacement) <= MaxSentenceWords,
            $"the replacement measures {WordCount(replacement)} words, over the ceiling");
    }

    /// <summary>Runs the branch that records <paramref name="reason"/> and returns its sentence.</summary>
    private string DriveFailure(FullUnlockFailureReason reason)
    {
        var freePath = WriteFreeBundle();
        string? bundlePath = null;
        try
        {
            LicenseService svc;
            switch (reason)
            {
                case FullUnlockFailureReason.NoSavedLicense:
                    var settings = NewUserSettings();
                    settings.ClearLicense();
                    svc = new LicenseService(NullLogger<LicenseService>.Instance, settings, new BundleAccessor());
                    break;

                case FullUnlockFailureReason.NoBundleFile:
                    (svc, _) = MakeService(BundleFixtureFactory.TestClientName, BundleFixtureFactory.TestKey);
                    break;

                case FullUnlockFailureReason.NoBundleDecrypted:
                    bundlePath = WriteFullBundle(_installDir);
                    var wrong = (byte[])BundleFixtureFactory.TestKey.Clone();
                    wrong[5] ^= 0xFF;
                    (svc, _) = MakeService(BundleFixtureFactory.TestClientName, wrong);
                    break;

                case FullUnlockFailureReason.Expired:
                    bundlePath = WriteFullBundle(_installDir, licenseExpiryUtc: Iso(DateTime.UtcNow.AddDays(-1)));
                    (svc, _) = MakeService(BundleFixtureFactory.TestClientName, BundleFixtureFactory.TestKey);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(reason), reason, "no driver for this branch");
            }

            svc.Initialize();

            // The precondition, asserted rather than assumed: a driver that landed on a DIFFERENT
            // branch would have this test grading some other sentence and passing.
            Assert.NotNull(svc.LastFullFailure);
            Assert.Equal(reason, svc.LastFullFailure!.Reason);
            return svc.LastFullFailure.Message;
        }
        finally
        {
            if (bundlePath != null) TryDelete(bundlePath);
            TryDelete(freePath);
        }
    }

    // ── The install read is bounded ─────────────────────────────────────────

    /// <summary>
    /// <c>InstallBundleFile</c> used to <c>File.ReadAllBytes</c> the caller's path IN FULL before
    /// the shape check that was supposed to gate it — an unbounded read of an arbitrary file,
    /// driven from a settings surface that break-glass keeps reachable on an unconfigured install.
    /// The size is now decided before anything is read, and only the header is ever read.
    /// </summary>
    [Fact]
    public void InstallBundleFile_RefusesAnOversizeFileAndNamesTheCeiling()
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), "sqlt-bundle-big-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDir);

        var name = $"oversize-{Guid.NewGuid():N}.aesgcm";
        var sourcePath = Path.Combine(sourceDir, name);

        // A REAL bundle header, so the refusal cannot come from the shape check: the only thing
        // wrong with this file is its size.
        var real = BundleCrypto.EncryptManifest(
            BundleFixtureFactory.MakeFullManifest(),
            BundleFixtureFactory.TestKey,
            AadBuilder.Build(BundleFixtureFactory.TestClientName, "Full", 1, BundleFixtureFactory.TestBuildNumber));

        using (var fs = File.Create(sourcePath))
        {
            fs.Write(real, 0, real.Length);
            fs.SetLength(LicenseService.MaxBundleFileBytes + 1);
        }

        var landed = Path.Combine(_installDir, name);

        try
        {
            var svc = new LicenseService(NullLogger<LicenseService>.Instance, NewUserSettings(), new BundleAccessor());

            var result = svc.InstallBundleFile(sourcePath);

            Assert.False(result.Success);
            Assert.Contains(LicenseService.MaxBundleFileBytes.ToString("N0"), result.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(landed), "a refused install must not leave the file in the install folder");
        }
        finally
        {
            TryDelete(landed);
            try { Directory.Delete(sourceDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// A file too short to hold a header is still refused with the shape message, which is the
    /// arm the header-only read could have broken: reading N bytes from a file shorter than N.
    /// </summary>
    [Fact]
    public void InstallBundleFile_RefusesAFileShorterThanAHeader()
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), "sqlt-bundle-tiny-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDir);

        var name = $"tiny-{Guid.NewGuid():N}.aesgcm";
        var sourcePath = Path.Combine(sourceDir, name);
        File.WriteAllBytes(sourcePath, new byte[] { 0x53, 0x4C, 0x42, 0x4E, 0x01, 0x00 });   // "SLBN" + v1, nothing else

        var landed = Path.Combine(_installDir, name);

        try
        {
            var svc = new LicenseService(NullLogger<LicenseService>.Instance, NewUserSettings(), new BundleAccessor());

            var result = svc.InstallBundleFile(sourcePath);

            Assert.False(result.Success);
            // Anchored on "the file is 6 bytes", not "6 bytes": the same message names the header
            // size (36 bytes), which CONTAINS "6 bytes" and would pass the loose form even if the
            // header-only read had reported the wrong length.
            Assert.Contains("the file is 6 bytes", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(landed));
        }
        finally
        {
            TryDelete(landed);
            try { Directory.Delete(sourceDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Renders log messages the way a sink would, so assertions read the line an engineer reads.</summary>
    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<LicenseService>
    {
        private readonly List<string> _lines = new();

        internal IReadOnlyList<string> Lines
        {
            get { lock (_lines) return _lines.ToList(); }
        }

        IDisposable? Microsoft.Extensions.Logging.ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lines) _lines.Add(formatter(state, exception));
        }
    }

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

