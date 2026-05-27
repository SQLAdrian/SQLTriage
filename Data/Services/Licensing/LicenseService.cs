/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Services.Licensing.Crypto;

namespace SQLTriage.Data.Services.Licensing;

/// <summary>
/// Result of a <see cref="LicenseService.TryActivate"/> call.
/// </summary>
public sealed record LicenseActivationResult(
    bool Success,
    Tier ResolvedTier,
    string? ErrorMessage);

/// <summary>
/// Boot-time license resolver. Tries the Full bundle first; falls back to Free; on both failing
/// resets the accessor to unlocked=false. Never crashes the app — failures are logged.
///
/// Thread-safety: <see cref="Initialize"/> is designed to be called once on the UI thread
/// before any consumer accesses <see cref="IBundleAccessor"/>. <see cref="TryActivate"/> and
/// <see cref="Deactivate"/> may be called from any thread; each re-runs Initialize internally.
/// </summary>
public sealed class LicenseService
{
    private readonly ILogger<LicenseService> _logger;
    private readonly UserSettingsService _userSettings;
    private readonly BundleAccessor _accessor;

    // BundleVersion is always 1 for this codec generation (matches the encryptor constant)
    private const int BundleVersion = 1;

    // Free-bundle uses the well-known zero key and a fixed client name + build number.
    // Build number is pinned to 0 (NOT the running app's build number) so a single
    // free-bundle.dat works across patch builds of the same bundle_v. Compatibility
    // re-keys only when BundleVersion bumps. Encryptor side: matches FreeBundleBuildNumber
    // in C:\Github\sqltriage-corpus\tools\CorpusEncryptor\Program.cs.
    private static readonly byte[] FreeKey = new byte[BundleCrypto.KeySize]; // all zeros
    private const string FreeBundleClientName = "FREE";
    private const string FreeBundleTierName = "Free";
    private const int FreeBundleBuildNumber = 0;
    private const string FullBundleTierName = "Full";
    private const string FreeBundleFileName = "Config/free-bundle.dat";       // relative to install dir

    public LicenseService(
        ILogger<LicenseService> logger,
        UserSettingsService userSettings,
        BundleAccessor accessor)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _userSettings = userSettings ?? throw new ArgumentNullException(nameof(userSettings));
        _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Synchronous boot-time initialisation. Must be called once after DI container builds,
    /// before any service consumes <see cref="IBundleAccessor"/>.
    /// Tries Full → Free → logs fatal error but does NOT throw.
    /// </summary>
    public void Initialize()
    {
        _logger.LogInformation("[LicenseService] Initialize starting.");

        // Try Full bundle
        if (TryUnlockFull())
        {
            _logger.LogInformation("[LicenseService] Full bundle active. Tier=Full, Client={Client}",
                _accessor.ClientName);
            return;
        }

        // Fall back to Free bundle
        if (TryUnlockFree())
        {
            _logger.LogInformation("[LicenseService] Free bundle active. Tier=Free.");
            return;
        }

        // Both failed — set unlocked=false and let the app boot anyway
        _accessor.Replace(null, Tier.Free);
        _logger.LogError(
            "[LicenseService] CRITICAL: Neither Full nor Free bundle could be decrypted. " +
            "App will boot but Audit Assessment will show 'Bundle missing — reinstall'. " +
            "Ensure Config/free-bundle.dat is present next to the .exe.");
    }

    /// <summary>
    /// Activates a Full license. Persists the client name and DPAPI-wrapped key to
    /// user-settings.json, then re-runs the bundle unlock logic.
    /// </summary>
    /// <param name="clientName">Exact customer name (case-sensitive; binds as GCM AAD).</param>
    /// <param name="licenseKey">
    /// Either a Base64 string (44 chars for 32 bytes) or a 24-word BIP39 phrase.
    /// </param>
    public LicenseActivationResult TryActivate(string clientName, string licenseKey)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogWarning("[LicenseService] TryActivate called on non-Windows platform — DPAPI unavailable.");
            return new LicenseActivationResult(false, Tier.Free,
                "License activation requires Windows (DPAPI).");
        }

        if (string.IsNullOrWhiteSpace(clientName))
            return new LicenseActivationResult(false, Tier.Free, "Customer name is required.");
        if (string.IsNullOrWhiteSpace(licenseKey))
            return new LicenseActivationResult(false, Tier.Free, "License key is required.");

        // Decode the key (BIP39 phrase or Base64)
        byte[] rawKey;
        try
        {
            rawKey = DecodeKeyInput(licenseKey.Trim());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LicenseService] Key decode failed.");
            return new LicenseActivationResult(false, Tier.Free,
                $"Key format invalid: {ex.Message}");
        }

        if (rawKey.Length != BundleCrypto.KeySize)
            return new LicenseActivationResult(false, Tier.Free,
                $"Key must decode to {BundleCrypto.KeySize} bytes, got {rawKey.Length}.");

        // Persist to user-settings before attempting decrypt (Initialize reads from there)
        byte[] dpapiKey;
        try
        {
            dpapiKey = WrapKeyDpapi(rawKey);
            _userSettings.SaveLicense(clientName.Trim(), dpapiKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LicenseService] DPAPI wrap or save failed.");
            return new LicenseActivationResult(false, Tier.Free,
                "Failed to store license key securely. Is this a Windows session with DPAPI?");
        }

        // Re-run initialization — will now find the persisted license
        Initialize();

        if (_accessor.Tier == Tier.Full && _accessor.IsUnlocked)
        {
            _logger.LogInformation("[LicenseService] Activation succeeded. Client={Client}, KeyFP={FP}",
                clientName, BundleCrypto.KeyFingerprintHex(rawKey)[..8]);
            return new LicenseActivationResult(true, Tier.Full, null);
        }

        // Decrypt failed — clean up the persisted (bad) license
        _userSettings.ClearLicense();
        Initialize(); // restore Free state

        return new LicenseActivationResult(false, _accessor.Tier,
            "Bundle decryption failed. Possible causes: " +
            "wrong customer name (exact spelling required), wrong key, " +
            "or no .aesgcm file found next to the .exe.");
    }

    /// <summary>
    /// Deactivates the current Full license, clears the persisted key, and reverts to Free.
    /// </summary>
    public void Deactivate()
    {
        _userSettings.ClearLicense();
        Initialize();
        _logger.LogInformation("[LicenseService] License deactivated. Reverted to Tier={Tier}.",
            _accessor.Tier);
    }

    // ── Private: Full unlock ─────────────────────────────────────────────────

    private bool TryUnlockFull()
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogWarning("[LicenseService] Non-Windows platform — Full bundle skipped (DPAPI unavailable).");
            return false;
        }

        (string? clientName, byte[]? encryptedKey) = _userSettings.GetSavedLicense();
        if (clientName is null || encryptedKey is null)
        {
            _logger.LogDebug("[LicenseService] No saved license found — skipping Full bundle.");
            return false;
        }

        byte[] rawKey;
        try
        {
            rawKey = UnwrapKeyDpapi(encryptedKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LicenseService] DPAPI unwrap failed — Full bundle skipped.");
            return false;
        }

        var buildNumber = ReadBuildNumber();
        var aad = AadBuilder.Build(clientName, FullBundleTierName, BundleVersion, buildNumber);
        var installDir = AppContext.BaseDirectory;

        var bundles = Directory.GetFiles(installDir, "*.aesgcm", SearchOption.TopDirectoryOnly);
        if (bundles.Length == 0)
        {
            _logger.LogWarning("[LicenseService] No .aesgcm files found in install dir: {Dir}", installDir);
            return false;
        }

        foreach (var path in bundles)
        {
            try
            {
                var wire = File.ReadAllBytes(path);
                var manifest = BundleCrypto.DecryptManifest(wire, rawKey, aad);
                _accessor.Replace(manifest, Tier.Full);
                _logger.LogInformation(
                    "[LicenseService] Full bundle decrypted: {File}, KeyFP={FP}",
                    Path.GetFileName(path),
                    BundleCrypto.KeyFingerprintHex(rawKey)[..8]);
                return true;
            }
            catch (CryptographicException ex)
            {
                _logger.LogDebug(ex,
                    "[LicenseService] AAD/tag mismatch on {File} — skipping.", Path.GetFileName(path));
            }
            catch (InvalidDataException ex)
            {
                _logger.LogDebug(ex,
                    "[LicenseService] Format error in {File} — skipping.", Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[LicenseService] Unexpected error reading {File} — skipping.", Path.GetFileName(path));
            }
        }

        _logger.LogWarning(
            "[LicenseService] Full bundle: tried {Count} file(s), none decrypted with client '{Client}'. " +
            "AAD mismatch — customer name doesn't match any installed bundle.",
            bundles.Length, clientName);
        return false;
    }

    // ── Private: Free unlock ─────────────────────────────────────────────────

    private bool TryUnlockFree()
    {
        var installDir = AppContext.BaseDirectory;
        var freePath = Path.Combine(installDir, FreeBundleFileName.Replace('/', Path.DirectorySeparatorChar));

        // Also check directly in install dir (Config sub-dir may not be present in some test setups)
        if (!File.Exists(freePath))
        {
            freePath = Path.Combine(installDir, Path.GetFileName(FreeBundleFileName));
        }

        if (!File.Exists(freePath))
        {
            _logger.LogWarning("[LicenseService] Free bundle not found at {Path}.", freePath);
            return false;
        }

        // AAD pinned to FreeBundleBuildNumber (0), not the running build number.
        // Free bundle is universal across patch builds; only re-keyed when bundle_v bumps.
        var aad = AadBuilder.Build(FreeBundleClientName, FreeBundleTierName, BundleVersion, FreeBundleBuildNumber);

        try
        {
            var wire = File.ReadAllBytes(freePath);
            var manifest = BundleCrypto.DecryptManifest(wire, FreeKey, aad);
            _accessor.Replace(manifest, Tier.Free);
            _logger.LogInformation("[LicenseService] Free bundle decrypted from {Path}.", freePath);
            return true;
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "[LicenseService] Free bundle auth-tag failure — file may be corrupt or from a different build.");
            return false;
        }
        catch (InvalidDataException ex)
        {
            _logger.LogError(ex, "[LicenseService] Free bundle format error.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LicenseService] Free bundle read error.");
            return false;
        }
    }

    // ── Private: helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Decodes a user-supplied license key string to raw bytes.
    /// Accepts: 24-word BIP39 phrase (detected by word count) OR Base64 string.
    /// </summary>
    private static byte[] DecodeKeyInput(string input)
    {
        var parts = input.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 24)
        {
            // Looks like a BIP39 phrase
            return Bip39.Decode(input);
        }

        // Try Base64
        return Convert.FromBase64String(input);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] WrapKeyDpapi(byte[] rawKey)
    {
        return ProtectedData.Protect(rawKey, LicenseDpapiEntropy, DataProtectionScope.CurrentUser);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] UnwrapKeyDpapi(byte[] wrapped)
    {
        return ProtectedData.Unprotect(wrapped, LicenseDpapiEntropy, DataProtectionScope.CurrentUser);
    }

    /// <summary>
    /// Per-app entropy for license DPAPI operations — separate from CredentialProtector entropy.
    /// </summary>
    private static readonly byte[] LicenseDpapiEntropy =
        System.Text.Encoding.UTF8.GetBytes("SQLTriage.License.v1");

    /// <summary>
    /// Reads the buildNumber from Config/version.json. Returns 0 on failure.
    /// The build number is embedded in the AAD — it must match between encryptor and decryptor.
    /// </summary>
    private static int ReadBuildNumber()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Config", "version.json");
            if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "config", "version.json");
            if (!File.Exists(path)) return 0;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("buildNumber", out var prop))
                return prop.GetInt32();
        }
        catch { /* non-fatal — return 0 */ }
        return 0;
    }
}
