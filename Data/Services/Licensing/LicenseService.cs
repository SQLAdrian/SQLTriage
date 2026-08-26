/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System.Globalization;
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
/// Why the Full bundle did not unlock on the last attempt. One value per branch of
/// <see cref="LicenseService.TryUnlockFull"/>, so the UI can say which one happened instead of
/// listing every possibility as a guess.
/// </summary>
public enum FullUnlockFailureReason
{
    /// <summary>No attempt has failed since the last success.</summary>
    None,

    /// <summary>Not Windows, so the DPAPI-wrapped key cannot be unwrapped at all.</summary>
    PlatformUnsupported,

    /// <summary>Nothing has ever been activated on this Windows profile.</summary>
    NoSavedLicense,

    /// <summary>A key is saved but DPAPI would not unwrap it — different Windows profile or machine.</summary>
    SavedKeyUnreadable,

    /// <summary>No <c>*.aesgcm</c> file is present next to the executable.</summary>
    NoBundleFile,

    /// <summary>Bundle files are present and none of them decrypted with the saved name and key.</summary>
    NoBundleDecrypted,

    /// <summary>A bundle decrypted cleanly and its licence period has ended.</summary>
    Expired,
}

/// <summary>
/// The reason register for a failed Full unlock: the machine-readable branch and the ONE sentence
/// every surface renders for it.
///
/// <para><b>Why it exists.</b> Boot-time failure was invisible outside the log — <c>Initialize</c>
/// logged and the app carried on at Free with no UI saying anything, and the activation card's
/// only failure text was a hard-coded list of three possible causes ("wrong customer name, wrong
/// key, or no .aesgcm file"). Measured on this box 2026-08-25: the saved key was the one minted
/// 2026-08-06 while the installed bundle was the one minted 2026-08-17, so the true cause was
/// "the key you activated with is not this bundle's key" and the app could not say so.</para>
/// </summary>
public sealed record FullUnlockFailure(FullUnlockFailureReason Reason, string Message);

/// <summary>Outcome of <see cref="LicenseService.InstallBundleFile"/>.</summary>
public sealed record BundleInstallResult(bool Success, string Message, string? InstalledPath);

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

    // B1: when an otherwise-valid Full bundle is declined PURELY because its licenseExpiryUtc has
    // passed, we record the expiry date here so the activation card can show "License expired on
    // <date> — renew" (vs the generic "no valid license"). null = not expired / no bundle tried.
    private DateTime? _fullExpiredOn;

    /// <summary>
    /// If the last Full-unlock attempt found a valid bundle that had EXPIRED, the expiry instant;
    /// otherwise null. The UI reads this to distinguish "expired, renew" from "no license".
    /// </summary>
    public DateTime? FullExpiredOn => _fullExpiredOn;

    /// <summary>
    /// Why the last Full-unlock attempt failed, or null when the last attempt succeeded.
    ///
    /// <para>Written by <see cref="TryUnlockFull"/> on every branch, including the boot-time one
    /// that nothing used to render. The activation card reads it so an operator who launches the
    /// app and finds themselves on Free is told which fact is wrong, rather than being shown a
    /// "Not Activated" badge and left to guess. Never carries key material, a key fingerprint or a
    /// file's contents — only the customer name the operator typed themselves and the file names
    /// they can see in their own install folder.</para>
    /// </summary>
    public FullUnlockFailure? LastFullFailure { get; private set; }

    // BundleVersion is always 1 for this codec generation (matches the encryptor constant)
    private const int BundleVersion = 1;

    // Free bundle is now read via FreeBundleCodec (hardened: derived key + per-generation rotation +
    // two-stage). The legacy zero-key path was REMOVED 2026-06-30 (it leaked the full corpus to
    // anyone reading the OSS source). See FreeBundleCodec / FreeKeyDerivation and
    // .handoff/HANDOFF-2026-06-30-free-bundle-hardening-SPEC.md.
    private const string FullBundleTierName = "Full";
    // Full bundles also pin to build=0 in the AAD so a single per-customer .aesgcm
    // survives app build-number bumps. The manifest still carries the real buildNumber
    // for diagnostics. Only bundle_v bumps require re-encryption.
    private const int FullBundleAadBuildNumber = 0;
    private const string FreeBundleFileName = "Config/free-bundle.dat";       // relative to install dir

    /// <summary>
    /// The largest file <see cref="InstallBundleFile"/> will copy into the install folder.
    ///
    /// <para>Measured on this box 2026-08-25, the real files are 1,138,679 bytes (the 2026-08-17
    /// mint), 1,141,600 bytes (the 2026-08-06 mint) and 1,114,344 bytes (the Free bundle), so this
    /// ceiling is roughly 56x the largest bundle anyone has been issued. It is a bound on what a
    /// caller can make this service copy, not an estimate of how big a bundle is: an operator with
    /// the settings permission names the source path, and without a ceiling the destination is the
    /// install folder and the size is whatever they point at.</para>
    /// </summary>
    public const long MaxBundleFileBytes = 64L * 1024 * 1024;

    public LicenseService(
        ILogger<LicenseService> logger,
        UserSettingsService userSettings,
        BundleAccessor accessor)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _userSettings = userSettings ?? throw new ArgumentNullException(nameof(userSettings));
        _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    }

    /// <summary>
    /// Set true when a hardened free bundle could not be decrypted because its generation is NEWER
    /// than this app's <see cref="FreeBundleCodec.Generation"/> — i.e. the app is stale and must be
    /// updated to read it. The UI can read this to prompt "download the latest SQLTriage" instead of
    /// a generic "bundle missing" error. Cleared on each successful free unlock.
    /// </summary>
    public bool FreeBundleNeedsNewerApp { get; private set; }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Synchronous boot-time initialisation. Must be called once after DI container builds,
    /// before any service consumes <see cref="IBundleAccessor"/>.
    /// Tries Full → Free → logs fatal error but does NOT throw.
    /// </summary>
    /// <param name="freeBundlePathOverride">
    /// When set (the headless <c>--audit --bundle &lt;path&gt;</c> caller), load exactly this
    /// file as the Free bundle instead of deriving the path from <see cref="AppContext.BaseDirectory"/>
    /// — the caller has already existence-checked it. Null (the default; every GUI/service caller)
    /// preserves the original ambient-path resolution untouched.
    /// </param>
    public void Initialize(string? freeBundlePathOverride = null)
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
        if (TryUnlockFree(freeBundlePathOverride))
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

        // Decrypt failed. Capture WHY before the cleanup Initialize() below overwrites it with
        // the NoSavedLicense branch — clearing the licence changes the reason, and reporting the
        // consequence of our own cleanup as the operator's problem is how the old three-guess
        // message came to be printed for every failure alike.
        var why = LastFullFailure;

        _userSettings.ClearLicense();
        Initialize(); // restore Free state

        // …and PUT IT BACK. Capturing the message for the return value defended one surface and
        // left the other one wrong: ActivateFullAuditCard binds its persistent red panel to this
        // LIVE property, so after a failed Activate the screen carried the correct sentence inline
        // and "No licence has been activated on this Windows account" in the prominent red panel,
        // at the same time. That second sentence describes the state the cleanup two lines above
        // just created; it is not why the operator is on Free. Measured on this box 2026-08-25
        // with the real 2026-08-17 bundle and the 2026-08-06 phrase.
        if (why is not null) LastFullFailure = why;

        return new LicenseActivationResult(false, _accessor.Tier,
            why?.Message ?? "Bundle decryption failed and no reason was recorded. That is a bug; please report it.");
    }

    /// <summary>
    /// Copies a <c>.aesgcm</c> bundle from anywhere on this machine into the folder SQLTriage
    /// reads bundles from, after checking it is a bundle at all.
    ///
    /// <para><b>Why this exists.</b> There was no way to load a licence file from the UI. The
    /// activation card took a customer name and a key, and the bundle itself had to be copied next
    /// to the executable by hand — an instruction the card printed but could not carry out. An
    /// operator who had been sent a bundle and a key could do everything the screen asked and
    /// still land on Free, because the half the screen could not do was the half that was
    /// missing.</para>
    ///
    /// <para>It does NOT decrypt, and deliberately does not need the key: putting the file in
    /// place and proving the key opens it are two different failures with two different fixes, and
    /// merging them is what produced the three-guess error message this lane deleted. Call
    /// <see cref="TryActivate"/> afterwards.</para>
    ///
    /// <para>The destination is <see cref="AppContext.BaseDirectory"/> — the same folder
    /// <see cref="TryUnlockFull"/> scans, read from the same property, so the two cannot drift.</para>
    ///
    /// <para><b>What this hands the caller, written down rather than left to be re-derived.</b> The
    /// caller names any path the service account can read. The three distinct replies — no file at
    /// that path, could not read it (with the OS message), and refused on shape — make this a
    /// file-existence and readability probe over the whole filesystem for whoever holds
    /// <c>settings</c>, which on an unconfigured install is any loopback caller through
    /// break-glass. The replies are kept because a path box that will not say whether the file is
    /// there is a support call. An earlier version of this comment said the same reach was already
    /// carried by the diagnostic-script and baseline surfaces on the same page; that comparison is
    /// WITHDRAWN, because the only other path box on Settings (the AzCopy executable path) stores a
    /// string rather than answering whether it exists. Treat the probe as new reach on this page,
    /// and Adrian's to rule.</para>
    ///
    /// <para><b>What is bounded.</b> The read is capped at <see cref="MaxBundleFileBytes"/> and
    /// stops after the header. The destination folder and file name are not caller-chosen. And an
    /// existing file is never replaced: the copy is <c>overwrite: false</c> and a name already in
    /// the install folder is refused, so no caller of this method can take a working install back
    /// to Free by dropping a shape-valid file over its bundle. That arm was live until 2026-08-25
    /// and is the one destructive act this surface ever had.</para>
    /// </summary>
    /// <param name="sourcePath">Full path of the .aesgcm file the operator was sent.</param>
    public BundleInstallResult InstallBundleFile(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return new BundleInstallResult(false, "Enter the full path of your .aesgcm bundle file.", null);

        sourcePath = sourcePath.Trim().Trim('"');

        // SEC-2 (DECISIONS 2026-08-25 23:01): reject a NON-LOCAL path BEFORE any File.* touches it.
        // Every branch below runs File.Exists / new FileInfo / File.OpenRead / File.Copy under the
        // service identity — NT SERVICE\SQLTriage on the installed service. A UNC source
        // (\\attacker\share\x.aesgcm) would make that identity authenticate OUTBOUND to an
        // attacker-named SMB host — an NTLM leak / relay — before any content check ran. The
        // operator text at the missing-file branch below has always said "a path on this machine";
        // nothing enforced it. This does, and it fails CLOSED: a fully-qualified LOCAL path only.
        // IsLocalBundlePath parses the string — it makes no filesystem or network call — so a
        // non-local path returns here without a single File.* or SMB touch. See the method note.
        if (!IsLocalBundlePath(sourcePath, out var localityProblem))
        {
            _logger.LogWarning("[LicenseService] Bundle install refused a non-local path: {Path}", sourcePath);
            return new BundleInstallResult(false, localityProblem, null);
        }

        if (!File.Exists(sourcePath))
        {
            return new BundleInstallResult(false,
                $"No file at {sourcePath}. Check the path. It must be a path on this machine, not on "
                + "the machine your browser is running on.", null);
        }

        // BOUNDED, AND THE SHAPE IS DECIDED BEFORE THE PAYLOAD IS TOUCHED. This was
        // File.ReadAllBytes of whatever path the caller named, in full, BEFORE anything looked at
        // it: an unbounded read of an arbitrary file, ordered so that the size check the shape test
        // performs could only run after the whole file was already in memory. Only the first
        // HeaderSize + 1 bytes decide whether this is a bundle, so only those are read; the payload
        // never enters this process and is handed straight to File.Copy.
        //
        // DISCLOSED, NOT CLOSED (SEC-2 review, 2026-08-25): a TOCTOU remains here. The ceiling is
        // measured on this handle-free FileInfo, the header is read from a second open, and File.Copy
        // opens a third time — so a source swapped between these steps is copied at whatever it
        // resolves to at copy time. It is left open deliberately: the load-bearing SEC-2 fix is the
        // locality guard above (the path is now proven local, so the swapper needs local write access
        // to the operator's own source file, already inside the trust boundary), and closing it means
        // a single-handle rewrite of a read/copy flow that must not regress. Close it by opening the
        // source once and copying from that handle if this method is revisited.
        long length;
        try
        {
            length = new FileInfo(sourcePath).Length;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LicenseService] Bundle install: cannot read {Path}", sourcePath);
            return new BundleInstallResult(false,
                $"Could not read {sourcePath}: {ex.Message}", null);
        }

        if (length > MaxBundleFileBytes)
        {
            _logger.LogWarning(
                "[LicenseService] Bundle install rejected {Path}: {Length} bytes is over the {Max}-byte ceiling.",
                sourcePath, length, MaxBundleFileBytes);
            return new BundleInstallResult(false,
                $"That file was not installed because it is {length:N0} bytes. A SQLTriage licence bundle "
                + $"is about a megabyte, so anything over {MaxBundleFileBytes:N0} bytes is refused rather "
                + "than copied into the install folder. Check that you picked the .aesgcm file you were "
                + "sent.", null);
        }

        var header = new byte[(int)Math.Min(length, BundleCrypto.HeaderSize + 1)];
        try
        {
            using var stream = File.OpenRead(sourcePath);
            stream.ReadExactly(header);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LicenseService] Bundle install: cannot read {Path}", sourcePath);
            return new BundleInstallResult(false,
                $"Could not read {sourcePath}: {ex.Message}", null);
        }

        if (!BundleCrypto.LooksLikeBundle(header, out var problem))
        {
            _logger.LogWarning("[LicenseService] Bundle install rejected {Path}: {Problem}", sourcePath, problem);
            return new BundleInstallResult(false, $"That file was not installed because {problem}", null);
        }

        var installDir = AppContext.BaseDirectory;
        var fileName = Path.GetFileName(sourcePath);
        if (!fileName.EndsWith(".aesgcm", StringComparison.OrdinalIgnoreCase))
            fileName += ".aesgcm";   // the scan is by extension; a renamed copy would be invisible

        var destination = Path.Combine(installDir, fileName);

        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destination),
                StringComparison.OrdinalIgnoreCase))
        {
            return new BundleInstallResult(true,
                $"{fileName} is already installed in {installDir}. Enter your customer name and key, "
                + "then click Activate.", destination);
        }

        // IT NEVER REPLACES AN INSTALLED BUNDLE. This copied with overwrite:true, which handed the
        // caller one destructive act: pointing at a shape-valid file whose name matches the working
        // bundle replaced it, and the next boot fell back to Free. Refusing the overwrite costs the
        // operator nothing, because the unlock scans EVERY *.aesgcm in the folder — a re-issued
        // bundle carries a new file name, installs beside the old one, and opens on the next
        // Activate. Replacing a file is a file-manager job, and this says so.
        if (File.Exists(destination))
        {
            _logger.LogWarning(
                "[LicenseService] Bundle install refused {Path}: {File} is already in {Dir}.",
                sourcePath, fileName, installDir);
            return new BundleInstallResult(false,
                $"{fileName} is already installed in {installDir}. SQLTriage does not overwrite a bundle "
                + "it has. If this is a re-issued bundle, it carries its own file name, so install that "
                + "file instead. To replace this exact file, delete it from the install folder first.",
                null);
        }

        try
        {
            File.Copy(sourcePath, destination, overwrite: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LicenseService] Bundle install: cannot write {Path}", destination);
            return new BundleInstallResult(false,
                $"Could not write {destination}: {ex.Message}. SQLTriage must be able to write its own "
                + "install folder to hold a licence bundle.", null);
        }

        _logger.LogInformation("[LicenseService] Bundle installed: {File} -> {Dir}", fileName, installDir);

        return new BundleInstallResult(true,
            $"{fileName} was installed into {installDir}. "
            + "Now enter your customer name and key, then click Activate.", destination);
    }

    /// <summary>
    /// The SEC-2 locality guard for <see cref="InstallBundleFile"/>: true only for a fully-qualified
    /// LOCAL path, false for a UNC (network) path or a relative / drive-relative one.
    ///
    /// <para><b>It touches nothing.</b> Every test here is on the STRING — a raw prefix check, a
    /// URI parse, and <see cref="Path.IsPathFullyQualified"/>, none of which open a handle or a
    /// socket. That is the whole point: the guard has to reach its verdict before any
    /// <c>File.*</c> call, because on the installed service the first <c>File.Exists</c> on a UNC
    /// path is already an outbound SMB authentication as <c>NT SERVICE\SQLTriage</c>.</para>
    ///
    /// <para><b>UNC is checked first, from the raw prefix.</b> A UNC path IS fully qualified, so the
    /// fully-qualified test cannot catch it — the order matters. The <c>\\</c> / <c>//</c> prefix is
    /// the authoritative signal (it is what the OS acts on); <see cref="Uri.IsUnc"/> is a second
    /// opinion for the forms that parse as an absolute URI.</para>
    ///
    /// <para><b>Scope, kept deliberately simple.</b> A local absolute path on a fixed or removable
    /// drive is allowed. A MAPPED network drive (<c>Z:\…</c> pointing at a share) is OUT of scope
    /// and allowed — resolving a drive letter to its backing store is a per-session lookup this
    /// guard will not chase, and the DECISIONS ruling names UNC and non-local-shape as the fix, not
    /// drive-mapping introspection.</para>
    /// </summary>
    internal static bool IsLocalBundlePath(string path, out string problem)
    {
        // UNC (\\server\share\... or //server/share/...). The raw prefix is the primary signal and
        // is pure string work; Uri confirms the forms that parse as an absolute URI. Neither call
        // touches the filesystem or the network.
        bool startsUnc = path.StartsWith(@"\\", StringComparison.Ordinal)
                         || path.StartsWith("//", StringComparison.Ordinal);
        bool uriUnc = false;
        if (!startsUnc && Uri.TryCreate(path, UriKind.Absolute, out var uri))
            uriUnc = uri.IsUnc;

        if (startsUnc || uriUnc)
        {
            problem = "That is a network path. SQLTriage loads a licence bundle only from the machine "
                + "it runs on, never from a network share. Copy the .aesgcm file to a local folder on "
                + "this machine, then give that local path.";
            return false;
        }

        // Anything not fully qualified is relative or drive-relative. It resolves against a current
        // directory this service makes no promise about, so it is not a path we can vouch is local.
        if (!Path.IsPathFullyQualified(path))
        {
            problem = "That is not a full local path. Give the full path of your .aesgcm bundle on "
                + "this machine, for example C:\\Users\\you\\Downloads\\bundle.aesgcm.";
            return false;
        }

        problem = string.Empty;
        return true;
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
        // B1: clear any prior "expired" marker; it is set again below only if a valid-but-expired
        // bundle is the reason we decline Full this pass.
        _fullExpiredOn = null;
        LastFullFailure = null;

        if (!OperatingSystem.IsWindows())
        {
            _logger.LogWarning("[LicenseService] Non-Windows platform — Full bundle skipped (DPAPI unavailable).");
            return Fail(FullUnlockFailureReason.PlatformUnsupported,
                "Full Audit activation needs Windows. The licence key is stored with Windows DPAPI, "
                + "which this platform does not provide.");
        }

        (string? clientName, byte[]? encryptedKey) = _userSettings.GetSavedLicense();
        if (clientName is null || encryptedKey is null)
        {
            _logger.LogDebug("[LicenseService] No saved license found — skipping Full bundle.");
            return Fail(FullUnlockFailureReason.NoSavedLicense,
                "No licence has been activated on this Windows account. Enter your customer name and "
                + "key below. Check that your .aesgcm bundle file is installed as well.");
        }

        byte[] rawKey;
        try
        {
            rawKey = UnwrapKeyDpapi(encryptedKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LicenseService] DPAPI unwrap failed — Full bundle skipped.");
            return Fail(FullUnlockFailureReason.SavedKeyUnreadable,
                $"The saved licence key for '{clientName}' could not be read back. A DPAPI key is bound "
                + "to the Windows account that saved it, so a different account or a different machine "
                + "cannot unwrap it. Re-enter the key below to store it for this account.");
        }

        // Try AAD with build=0 (new stable pin), then fall back to the running
        // build number for backward compatibility with bundles encrypted before
        // the build-number pin was introduced.
        var runBuild = ReadBuildNumber();
        var buildCandidates = runBuild == FullBundleAadBuildNumber
            ? new[] { FullBundleAadBuildNumber }
            : new[] { FullBundleAadBuildNumber, runBuild };
        var installDir = AppContext.BaseDirectory;

        var bundles = Directory.GetFiles(installDir, "*.aesgcm", SearchOption.TopDirectoryOnly);
        if (bundles.Length == 0)
        {
            _logger.LogWarning("[LicenseService] No .aesgcm files found in install dir: {Dir}", installDir);
            return Fail(FullUnlockFailureReason.NoBundleFile,
                $"No .aesgcm bundle file is installed. SQLTriage looks in {installDir} and found none. "
                + "Use 'Install bundle file' below to point at the .aesgcm you were sent, or copy it "
                + "into that folder yourself.");
        }

        foreach (var path in bundles)
        {
            byte[]? wire = null;
            try { wire = File.ReadAllBytes(path); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LicenseService] Cannot read {File} — skipping.", Path.GetFileName(path));
                continue;
            }

            foreach (var build in buildCandidates)
            {
                var aad = AadBuilder.Build(clientName, FullBundleTierName, BundleVersion, build);
                try
                {
                    var manifest = BundleCrypto.DecryptManifest(wire, rawKey, aad);

                    // B1: a valid decrypt is not enough — honour license expiry. Read LIVE off the
                    // just-decrypted manifest (same ISO/UTC convention as BundleAccessor.ParseUtc).
                    // null/unparseable => perpetual (fail-OPEN: legacy bundles predate this field and
                    // must NOT be treated as expired). The date is inside the GCM tag, so tampering it
                    // breaks decryption — an attacker cannot forge a later date.
                    var expiryRaw = manifest.Features.LicenseExpiryUtc;
                    if (!string.IsNullOrWhiteSpace(expiryRaw)
                        && DateTime.TryParse(expiryRaw, CultureInfo.InvariantCulture,
                               DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var exp)
                        && DateTime.UtcNow >= exp)
                    {
                        _fullExpiredOn = exp;   // surface "expired, renew" to the activation card
                        _logger.LogWarning(
                            "[LicenseService] Full bundle '{File}' is valid but EXPIRED {Exp:o} — reverting to Free.",
                            Path.GetFileName(path), exp);
                        break;  // this file is expired; no other build candidate changes that → next file
                    }

                    // No LastFullFailure reset here: this method clears it on entry, so a success
                    // path that also cleared it would be a line no test could turn red.
                    _accessor.Replace(manifest, Tier.Full);
                    _logger.LogInformation(
                        "[LicenseService] Full bundle decrypted: {File}, KeyFP={FP}, AAD_build={Build}",
                        Path.GetFileName(path),
                        BundleCrypto.KeyFingerprintHex(rawKey)[..8],
                        build);
                    return true;
                }
                catch (CryptographicException) { /* try next build candidate */ }
                catch (InvalidDataException)  { break; } // format error — no point retrying with different build
            }
        }

        if (_fullExpiredOn is { } expiredOn)
        {
            return Fail(FullUnlockFailureReason.Expired,
                $"Your Full Audit licence expired on {expiredOn:yyyy-MM-dd}. The bundle is valid and the "
                + "key is correct. What ran out is the licence period. Ask for a renewed bundle.");
        }

        var names = string.Join(", ", bundles.Select(Path.GetFileName));

        // THE LOG SAYS WHAT THE UI SAYS. This line used to end "AAD mismatch — customer name
        // doesn't match any installed bundle", which is a diagnosis nothing here can make: on this
        // box 2026-08-25 it was printed while the saved customer name matched the manifest's
        // clientName character for character and the real cause was a key from an earlier mint.
        // The log is the durable record a support engineer reads, so a fabricated cause here
        // outlives the corrected one below.
        _logger.LogWarning(
            "[LicenseService] Full bundle: tried {Count} file(s) ({Files}), none decrypted for client " +
            "'{Client}'. The authentication tag fails identically for a customer name that is not the " +
            "one the bundle was issued to, for a key from a different issue of the bundle, or for a " +
            "damaged file, so which of the three it is cannot be read here.",
            bundles.Length, names, clientName);

        // Names the three facts that are actually in play and the order to check them in. It does
        // NOT claim which one is wrong, because the auth tag fails identically for all three.
        // The damaged-file arm is not theoretical: InstallBundleFile validates the header only, so
        // a truncated or partly downloaded bundle installs cleanly and fails exactly here, with
        // the name and the key both correct.
        return Fail(FullUnlockFailureReason.NoBundleDecrypted,
            $"The installed bundle ({names}) did not open with the saved customer name '{clientName}'. "
            + "Three causes fail the same way here, so this cannot say which one it is. "
            + "The customer name may not be the one the bundle was issued to. "
            + "The key may be from a different issue of the bundle. "
            + "A re-issue carries a new key, so an older key will not open the newer bundle. "
            + "The bundle file may be damaged. "
            + "Check the customer name against your licence email, character for character. Then "
            + "re-enter the key from the SAME email as this bundle file. If both are right, ask for a "
            + "fresh copy of the bundle.");
    }

    /// <summary>Records the reason and returns false, so every failing branch reads as one line.</summary>
    private bool Fail(FullUnlockFailureReason reason, string message)
    {
        LastFullFailure = new FullUnlockFailure(reason, message);
        return false;
    }

    // ── Private: Free unlock ─────────────────────────────────────────────────

    /// <param name="overridePath">See <see cref="Initialize"/>'s freeBundlePathOverride — when
    /// set, this exact path is loaded verbatim (no ambient install-dir search).</param>
    private bool TryUnlockFree(string? overridePath = null)
    {
        FreeBundleNeedsNewerApp = false;

        string freePath;
        if (overridePath is not null)
        {
            freePath = overridePath;
        }
        else
        {
            var installDir = AppContext.BaseDirectory;
            freePath = Path.Combine(installDir, FreeBundleFileName.Replace('/', Path.DirectorySeparatorChar));

            // Also check directly in install dir (Config sub-dir may not be present in some test setups)
            if (!File.Exists(freePath))
            {
                freePath = Path.Combine(installDir, Path.GetFileName(FreeBundleFileName));
            }
        }

        if (!File.Exists(freePath))
        {
            _logger.LogWarning("[LicenseService] Free bundle not found at {Path}.", freePath);
            return false;
        }

        byte[] wire;
        try { wire = File.ReadAllBytes(freePath); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LicenseService] Free bundle read error.");
            return false;
        }

        try
        {
            // Hardened format: derived key + per-generation rotation + two-stage (FreeBundleCodec).
            var manifest = FreeBundleCodec.Unpack(wire, out var bundleGeneration);
            _accessor.Replace(manifest, Tier.Free);
            _logger.LogInformation(
                "[LicenseService] Free bundle decrypted from {Path} (generation {Gen}).",
                freePath, bundleGeneration);
            return true;
        }
        catch (CryptographicException ex)
        {
            // Decrypt failed. If the bundle's generation is NEWER than this app's, the app is stale
            // (deliberate per-release rotation) — flag it so the UI can prompt an update.
            if (FreeBundleCodec.TryReadGeneration(wire, out var bundleGen) &&
                bundleGen > FreeBundleCodec.Generation)
            {
                FreeBundleNeedsNewerApp = true;
                _logger.LogError(
                    "[LicenseService] Free bundle generation {BundleGen} is newer than this app ({AppGen}). " +
                    "A newer SQLTriage is required to read it.", bundleGen, FreeBundleCodec.Generation);
            }
            else if (!FreeBundleCodec.IsHardenedFormat(wire))
            {
                _logger.LogError(ex,
                    "[LicenseService] Free bundle is in the LEGACY (pre-hardening) format and is no longer supported. " +
                    "Reinstall to obtain a current free-bundle.dat.");
            }
            else
            {
                _logger.LogError(ex, "[LicenseService] Free bundle auth-tag failure — file may be corrupt.");
            }
            return false;
        }
        catch (InvalidDataException ex)
        {
            _logger.LogError(ex, "[LicenseService] Free bundle format error.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LicenseService] Free bundle decode error.");
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
