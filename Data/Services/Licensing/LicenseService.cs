/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Services.Licensing.Crypto;

namespace SQLTriage.Data.Services.Licensing;

/// <summary>
/// Result of a <see cref="LicenseService.TryActivate"/> call.
///
/// <para><b><see cref="TimedOut"/>.</b> A refusal and a non-answer are different facts, exactly as
/// they are on <see cref="BundleInstallResult"/>. <see cref="LicenseService.TryActivateAsync"/>
/// bounds the WAIT, not the work: when the budget runs out the activation is still running, so a
/// headline reading "Activation failed." would state as fact the one thing that is not yet known.
/// The flag is carried on the result rather than sniffed out of the message text.</para>
/// </summary>
public sealed record LicenseActivationResult(
    bool Success,
    Tier ResolvedTier,
    string? ErrorMessage,
    bool TimedOut = false);

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

/// <summary>
/// Outcome of <see cref="LicenseService.InstallBundleFile"/>.
///
/// <para><b><see cref="TimedOut"/>.</b> A refusal and a non-answer are different facts and the UI
/// must be able to tell them apart without reading the prose. Every refusal here knows that the
/// file was not installed; the bounded wait in
/// <see cref="LicenseService.InstallBundleFileAsync"/> does NOT -- the blocking call it gave up on
/// keeps running and its copy may still land. A caller that titles both "Bundle not installed."
/// states as fact the one thing the timeout message calls unknown.</para>
/// </summary>
public sealed record BundleInstallResult(
    bool Success, string Message, string? InstalledPath, bool TimedOut = false);

/// <summary>
/// What happened to one <c>*.aesgcm</c> file on the last Full-unlock pass.
///
/// <para>The card used to derive this itself, and could not: it enumerated the install folder on
/// every render and named the FIRST file alphabetically as the decrypted one, whichever file had
/// actually opened. Only <c>TryUnlockFull</c> knows, because only it tried the key. This record is
/// that knowledge, published once per pass.</para>
/// </summary>
public enum BundleFileState
{
    /// <summary>This pass did not try the file: no licence is saved, or the saved key would not unwrap.</summary>
    NotTried,

    /// <summary>The bytes could not be read off disk at all.</summary>
    Unreadable,

    /// <summary>Read fine; the saved customer name and key do not open it.</summary>
    DidNotOpen,

    /// <summary>Opened cleanly, and its licence period has ended.</summary>
    Expired,

    /// <summary>Opened cleanly and is current, but another opened file carries a newer manifest date.</summary>
    Superseded,

    /// <summary>Opened cleanly, is current, and is the bundle this install is running on.</summary>
    InUse
}

/// <summary>
/// One <c>*.aesgcm</c> file and what the last unlock pass made of it. <c>CreatedUtc</c> is the
/// AUTHENTICATED manifest date (inside the GCM tag), null when the file did not open or carried no
/// parseable date. <c>ExpiredOn</c> is set only on <see cref="BundleFileState.Expired"/>.
/// </summary>
public sealed record BundleFileStatus(
    string Path,
    BundleFileState State,
    DateTime? CreatedUtc,
    DateTime? ExpiredOn);

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

    /// <summary>
    /// Full path of the <c>*.aesgcm</c> file the running Full licence was decrypted from, or null
    /// when Full is not active.
    ///
    /// <para>It exists because the activation card had no way to say WHICH file opened. It printed
    /// the first file alphabetically followed by "(decrypted)", which on a folder holding several
    /// bundles named a file that had never been opened. Cleared at the top of every unlock pass, so
    /// a failed pass or a Deactivate leaves it null rather than naming a stale winner.</para>
    /// </summary>
    public string? LastUnlockedBundlePath { get; private set; }

    /// <summary>
    /// Every <c>*.aesgcm</c> file the last unlock pass saw, and what it made of each. Replaced
    /// wholesale on each pass; never null. The card renders this instead of enumerating the install
    /// folder itself on every render.
    /// </summary>
    public IReadOnlyList<BundleFileStatus> LastBundleScan { get; private set; } =
        Array.Empty<BundleFileStatus>();

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

    /// <summary>
    /// The audit sink for key-file pickup, or null when nothing is wired.
    ///
    /// <para><b>Optional on purpose.</b> Thirty-two existing tests construct this service directly
    /// with three arguments; a required fourth would have broken every one of them, and a test file
    /// edited to satisfy a constructor is a test file nobody re-read. <c>AuditLogService</c> takes
    /// only a log directory and <c>IConfiguration</c>, so resolving it here is not a dependency
    /// cycle back into the bundle.</para>
    /// </summary>
    private readonly AuditLogService? _audit;

    /// <summary>
    /// The host's configuration, or null when nothing is wired. Read for ONE key today:
    /// <see cref="KeyFilePickupConfigKey"/>.
    ///
    /// <para>Optional for the same reason <see cref="_audit"/> is: every direct construction in the
    /// test tree passes three arguments. Every shipped host registers
    /// <c>IConfiguration</c> as a singleton over its own <c>Config/appsettings.json</c>
    /// (<c>WindowsServiceHost</c>, <c>App.xaml.cs</c>, <c>CliAuditHost</c>, <c>CliImportHost</c>),
    /// so the container fills this in for the real app and leaves it null in a bare test.</para>
    /// </summary>
    private readonly IConfiguration? _configuration;

    public LicenseService(
        ILogger<LicenseService> logger,
        UserSettingsService userSettings,
        BundleAccessor accessor,
        AuditLogService? audit = null,
        IConfiguration? configuration = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _userSettings = userSettings ?? throw new ArgumentNullException(nameof(userSettings));
        _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
        _audit = audit;
        _configuration = configuration;
    }

    /// <summary>
    /// The operator's kill switch for key-file pickup. <c>false</c> in
    /// <c>Config/appsettings.json</c> means this install NEVER opens, renames or deletes a
    /// <c>*.key.txt</c> dropped beside the executable — not at start-up, not from the card's
    /// button, and not from an administrator accepting a held pair.
    ///
    /// <para><b>It is in the design and it was dropped.</b> DESIGN-2026-09-02-keyfile-activation
    /// §4.2 carries this check in its pseudocode; the first implementation shipped only
    /// <see cref="KeyFilePickupMode.Disabled"/>, which nothing but <c>--audit</c> passes — so an
    /// operator who did not want a service that reads a dropped credential had no supported way to
    /// say so. Found by the adversarial verifier, round 2 (defect D2), and restored here.</para>
    /// </summary>
    internal const string KeyFilePickupConfigKey = "Licensing:KeyFilePickup:Enabled";

    /// <summary>
    /// DEFAULT TRUE, and the default is what ships: an install with no such key in its
    /// <c>appsettings.json</c> — every install today — behaves exactly as it did before the switch
    /// existed. Only an explicit <c>false</c> turns pickup off.
    /// </summary>
    private bool KeyFilePickupEnabledInConfig
        => _configuration?.GetValue(KeyFilePickupConfigKey, true) ?? true;

    /// <summary>The one message every entry point gives when the switch is off.</summary>
    private const string PickupDisabledByConfigMessage =
        "Activating from a key file is switched off for this install (" + KeyFilePickupConfigKey
        + " is false in appsettings.json). No key file was opened, renamed or removed.";

    /// <summary>
    /// TEST SEAM. The folder this service scans for <c>*.aesgcm</c> bundles and <c>*.key.txt</c>
    /// key files. Null (every shipped composition) means <see cref="AppContext.BaseDirectory"/>.
    ///
    /// <para>It exists because key-file pickup DELETES a file in that folder, and the folder the
    /// test assembly runs from is shared with every other licensing suite in the process. Never set
    /// outside tests; nothing in the shipped composition writes it.</para>
    /// </summary>
    internal string? InstallDirOverrideForTests { get; set; }

    /// <summary>
    /// The one folder this service reads bundles and key files from. Both the unlock scan and the
    /// key-file pickup read it from HERE rather than from <see cref="AppContext.BaseDirectory"/>
    /// separately, so the two cannot drift onto different folders.
    /// </summary>
    private string InstallDirectory => InstallDirOverrideForTests ?? AppContext.BaseDirectory;

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
    /// <param name="pickup">
    /// Whether this process may activate from a key file dropped beside the executable.
    /// <see cref="KeyFilePickupMode.Disabled"/> opts the WHOLE PROCESS out, not just this call —
    /// see the enum's own note on why <c>--audit</c> must not race the service for the key file.
    /// </param>
    public void Initialize(string? freeBundlePathOverride = null,
                           KeyFilePickupMode pickup = KeyFilePickupMode.Enabled)
    {
        _logger.LogInformation("[LicenseService] Initialize starting.");

        // KEY-FILE PICKUP RUNS FIRST, ONCE PER PROCESS, AND CANNOT STOP THE LINES BELOW IT.
        // It may write user-settings.json (via TryActivate), so it has to happen before the unlock
        // reads it. Its whole body is inside a try/catch: a malformed customer name throws out of
        // AadBuilder.Build, and a pickup failure must never be able to keep the free bundle from
        // loading. Board #19, DESIGN-2026-09-02-keyfile-activation §4.
        if (pickup == KeyFilePickupMode.Disabled) _pickupCompleted = true;
        RunKeyFilePickupOnce();

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
        SafeReplace(null, Tier.Free);
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

        // THE WORKING LICENCE IS SNAPSHOTTED BEFORE IT IS OVERWRITTEN. Activation persists the new
        // credentials and only then re-runs the unlock, so the decrypt attempt happens with the old
        // pair already gone from user-settings.json. When it failed, the cleanup below called
        // ClearLicense() -- and a client who mistyped one character on a working install lost the
        // licence they had and dropped to Free. Reported from a live client site 2026-09-02. Taking
        // a copy here costs one file read and makes the failure path non-destructive.
        (string? priorClientName, byte[]? priorEncryptedKey) = _userSettings.GetSavedLicense();

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

        // PUT THE OLD PAIR BACK, then re-resolve. When there was a previous licence this restores
        // exactly the state the operator had before they pressed Activate. When there was none it
        // clears, which is what this branch did unconditionally.
        // THE RESTORE ITSELF CAN FAIL, AND SILENCE THERE IS THE DESTRUCTIVE OUTCOME AGAIN.
        // SaveLicense rewrites user-settings.json, so a locked file, a full disk or an ACL change
        // throws here — with the NEW, bad pair already persisted a few lines above. Left unguarded
        // that exception escapes TryActivate, the card prints the raw IO message, and the client is
        // on Free holding a licence they cannot get back without their original email. Which is
        // precisely the failure this snapshot was added to remove.
        bool hadPrior = priorClientName is not null && priorEncryptedKey is not null;
        string restoreFailure = string.Empty;
        if (hadPrior)
        {
            try
            {
                _userSettings.SaveLicense(priorClientName!, priorEncryptedKey!);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[LicenseService] Restoring the previous licence for '{Prior}' failed. The " +
                    "credentials just entered remain saved.", priorClientName);

                // hadPrior goes false because nothing was restored, and every sentence below it is
                // a claim about a licence that was put back. The operator gets the fact instead.
                hadPrior = false;
                restoreFailure =
                    $" Your previous licence for '{priorClientName}' could NOT be written back: "
                    + ex.Message + " The customer name and key you just entered are what is saved "
                    + $"now, in {_userSettings.SettingsFilePath}. Re-enter your original customer "
                    + "name and key to put the working licence back.";
            }
        }
        else
        {
            _userSettings.ClearLicense();
        }

        Initialize();

        bool priorStillActive = hadPrior && _accessor.Tier == Tier.Full && _accessor.IsUnlocked;

        // …and PUT THE REASON BACK. Capturing the message for the return value defended one
        // surface and left the other one wrong: ActivateFullAuditCard binds its persistent red panel
        // to this LIVE property, so after a failed Activate the screen carried the correct sentence
        // inline and "No licence has been activated on this Windows account" in the prominent red
        // panel, at the same time. That second sentence describes the state the cleanup just
        // created; it is not why the operator is on Free. Measured on this box 2026-08-25 with the
        // real 2026-08-17 bundle and the 2026-08-06 phrase.
        //
        // NOT when the restore put a working licence back, though. The panel then describes a
        // licence that is running, and overwriting the null the successful Initialize just wrote
        // would put a failure on record that no longer exists.
        if (why is not null && !priorStillActive) LastFullFailure = why;

        var reason = why?.Message
            ?? "Bundle decryption failed and no reason was recorded. That is a bug; please report it.";

        if (priorStillActive)
        {
            _logger.LogInformation(
                "[LicenseService] Activation for '{Client}' failed. The previous licence for '{Prior}' " +
                "was restored and is active.", clientName, priorClientName);
            return new LicenseActivationResult(false, _accessor.Tier,
                reason + " " + PreviousLicenceKeptMessage);
        }

        return new LicenseActivationResult(false, _accessor.Tier, reason + restoreFailure);
    }

    /// <summary>
    /// The sentence appended when a failed Activate left the previous licence in place. Written
    /// once so the service and the tests that grade it cannot drift.
    /// </summary>
    public const string PreviousLicenceKeptMessage =
        "Your previous licence was kept and is still active. Nothing was lost.";

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

        // AND NOT FROM A MAPPED OR REMOVABLE DRIVE. The locality guard above reads the STRING, so a
        // mapped network drive (Z:\ pointing at a share) passes it -- IsLocalBundlePath says so in
        // its own remarks, and that was the accepted scope when it was written. It is not acceptable
        // for the blocking calls below: a mapped drive whose server is gone makes File.Exists sit in
        // the SMB client for the redirector timeout, which is what froze the licence card at a live
        // client site. This asks Windows what kind of drive the path's root is -- a local, in-session
        // lookup -- and refuses anything that is not a fixed disk BEFORE any File.* runs.
        if (!IsFixedDriveSource(sourcePath, out var driveProblem))
        {
            _logger.LogWarning("[LicenseService] Bundle install refused a non-fixed-drive path: {Path}", sourcePath);
            return new BundleInstallResult(false, driveProblem, null);
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

        var installDir = InstallDirectory;
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
    /// True only when the path's root is a FIXED disk on this machine. False for a mapped network
    /// drive, a removable or optical drive, a RAM disk, an unmounted drive letter, and anything
    /// whose root cannot be identified.
    ///
    /// <para><b>Why a second guard.</b> <see cref="IsLocalBundlePath"/> is pure string work and
    /// says so; a mapped drive letter is indistinguishable from a local one as a string. This one
    /// asks the OS. <see cref="DriveInfo.DriveType"/> reads the session's own drive table, so it
    /// answers for a mapped drive without contacting the server -- unlike <c>File.Exists</c> on the
    /// same path, which does.</para>
    ///
    /// <para>Fails CLOSED: any exception identifying the drive is a refusal, not a pass.</para>
    /// </summary>
    internal static bool IsFixedDriveSource(string path, out string problem)
    {
        string? root;
        try { root = Path.GetPathRoot(path); }
        catch (Exception) { root = null; }

        if (string.IsNullOrEmpty(root))
        {
            problem = "SQLTriage could not work out which drive that path is on. Give the full path "
                + "of your .aesgcm bundle on this machine, for example C:\\Users\\you\\Downloads\\bundle.aesgcm.";
            return false;
        }

        DriveType type;
        try
        {
            type = new DriveInfo(root).DriveType;
        }
        catch (Exception ex)
        {
            problem = $"SQLTriage could not identify the drive {root}: {ex.Message} A licence bundle "
                + "is loaded only from a fixed drive on this machine. Copy the .aesgcm file to a "
                + "folder on this machine's own disk, then give that path.";
            return false;
        }

        if (type != DriveType.Fixed)
        {
            problem = $"{root} is not a fixed drive on this machine. Windows reports it as "
                + $"{DescribeDriveType(type)}. SQLTriage loads a licence bundle only from a fixed "
                + "drive, because a disconnected network or removable drive can make the copy hang. "
                + "Copy the .aesgcm file to a folder on this machine's own disk, then give that path.";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    /// <summary>Plain English for a <see cref="DriveType"/>, for the refusal above.</summary>
    private static string DescribeDriveType(DriveType type) => type switch
    {
        DriveType.Network => "a mapped network drive",
        DriveType.Removable => "a removable drive",
        DriveType.CDRom => "a CD or DVD drive",
        DriveType.Ram => "a RAM disk",
        DriveType.NoRootDirectory => "a drive letter with nothing mounted on it",
        _ => "a drive of an unknown kind"
    };

    /// <summary>
    /// How long <see cref="InstallBundleFileAsync"/> waits for the blocking file work before it
    /// gives the operator a sentence back.
    /// </summary>
    public static readonly TimeSpan InstallProbeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// TEST SEAM. When set, the bounded wait races THIS delegate instead of
    /// <see cref="InstallBundleFile"/>. It exists because a source that never responds cannot be
    /// created portably on Windows -- there is no FIFO to point at, and a locked file does not make
    /// <c>File.Exists</c> block. Never set outside tests; nothing in the shipped composition writes it.
    /// </summary>
    internal Func<string, CancellationToken, BundleInstallResult>? InstallWorkOverrideForTests { get; set; }

    /// <summary>
    /// <see cref="InstallBundleFile"/> with a wall-clock ceiling.
    ///
    /// <para><b>Why.</b> That method makes four blocking file calls on an operator-typed path --
    /// <c>File.Exists</c>, <c>FileInfo.Length</c>, <c>File.OpenRead</c> + <c>ReadExactly</c>, and
    /// <c>File.Copy</c>. None of them takes a timeout or a CancellationToken, and none of them is
    /// guaranteed to return: a drive whose backing store has gone away leaves the caller in the
    /// filesystem for as long as the OS decides. The card awaited that call with its Working
    /// indicator up, so the page read as frozen and stayed that way.</para>
    ///
    /// <para><b>What the ceiling does and does not do.</b> It bounds the WAIT, not the work: the
    /// blocking call cannot be cancelled, so it keeps running on its thread pool thread and its copy
    /// may still land. The timeout message says exactly that rather than claiming nothing
    /// happened.</para>
    /// </summary>
    public async Task<BundleInstallResult> InstallBundleFileAsync(
        string sourcePath,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var budget = timeout ?? InstallProbeTimeout;
        var work = InstallWorkOverrideForTests;

        var task = Task.Run(
            () => work is null ? InstallBundleFile(sourcePath) : work(sourcePath, cancellationToken),
            cancellationToken);

        using var delayCts = new CancellationTokenSource();
        var finished = await Task.WhenAny(task, Task.Delay(budget, delayCts.Token)).ConfigureAwait(false);

        if (ReferenceEquals(finished, task))
        {
            delayCts.Cancel();   // stop the timer rather than leaving it to fire into nothing
            try
            {
                return await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[LicenseService] Bundle install threw for {Path}.", sourcePath);
                return new BundleInstallResult(false,
                    $"The bundle was not installed: {ex.Message}", null);
            }
        }

        // Timed out. The abandoned task must still have its exception observed if it ever faults;
        // nothing else is going to look at it.
        _ = task.ContinueWith(t => { _ = t.Exception; },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        _logger.LogWarning(
            "[LicenseService] Bundle install from {Path} did not return within {Seconds}s. " +
            "The file work was left running; it may still complete.",
            sourcePath, (int)budget.TotalSeconds);

        return new BundleInstallResult(false,
            $"SQLTriage waited {(int)budget.TotalSeconds} seconds for {sourcePath} and the file system "
            + "has not answered. Installing a bundle checks that the file is there, reads its size, "
            + "reads its first bytes, and copies it into the install folder. Which of those has not "
            + "returned is not known here. The copy may still finish on its own, so look in the "
            + "install folder before trying again. A drive that is slow or no longer connected is the "
            + "usual cause.", null, TimedOut: true);
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

    // ── The other two bounded waits ──────────────────────────────────────────

    /// <summary>
    /// How long <see cref="TryActivateAsync"/> and <see cref="DeactivateAsync"/> wait before they
    /// answer the operator. Same budget as <see cref="InstallProbeTimeout"/> and the same reason.
    /// </summary>
    public static readonly TimeSpan ActivateProbeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// TEST SEAM for <see cref="TryActivateAsync"/>, mirroring
    /// <see cref="InstallWorkOverrideForTests"/>: a volume that never answers cannot be created
    /// portably on Windows, so the blocking call is stood in for. Never set outside tests.
    /// </summary>
    internal Func<string, string, LicenseActivationResult>? ActivateWorkOverrideForTests { get; set; }

    /// <summary>TEST SEAM for <see cref="DeactivateAsync"/>. Never set outside tests.</summary>
    internal Action? DeactivateWorkOverrideForTests { get; set; }

    /// <summary>
    /// <see cref="TryActivate"/> with a wall-clock ceiling.
    ///
    /// <para><b>Why this exists.</b> The client reported Install AND Activate freezing the page.
    /// Round 1 of this lane bounded the install and left Activate awaiting a bare
    /// <c>Task.Run</c> — bounded against a FAULT by the card's finally, and not bounded at all
    /// against a call that never returns. Underneath, <see cref="TryUnlockFull"/> enumerates the
    /// install folder and <c>File.ReadAllBytes</c> every <c>.aesgcm</c> in it; this lane
    /// deliberately stopped short-circuiting on the first file that opened, so on the client
    /// install holding ten bundles an Activate now reads ten files where it used to read one. That
    /// made the unbounded path wider, not narrower.</para>
    ///
    /// <para><b>What the ceiling does and does not do.</b> It bounds the WAIT. The blocking file
    /// calls underneath take no CancellationToken, so the activation keeps running on its pool
    /// thread and may still succeed. The timeout message says that, and
    /// <see cref="LicenseActivationResult.TimedOut"/> lets the card title it without claiming a
    /// failure that has not happened.</para>
    /// </summary>
    public async Task<LicenseActivationResult> TryActivateAsync(
        string clientName, string licenseKey, TimeSpan? timeout = null)
    {
        var budget = timeout ?? ActivateProbeTimeout;
        var work = ActivateWorkOverrideForTests;

        var task = Task.Run(() => work is null
            ? TryActivate(clientName, licenseKey)
            : work(clientName, licenseKey));

        using var delayCts = new CancellationTokenSource();
        var finished = await Task.WhenAny(task, Task.Delay(budget, delayCts.Token))
            .ConfigureAwait(false);

        if (ReferenceEquals(finished, task))
        {
            delayCts.Cancel();
            // Faults are NOT swallowed here: the card's catch is what turns one into a sentence,
            // and it needs to know whether the outcome was decided. Awaiting rethrows in place.
            return await task.ConfigureAwait(false);
        }

        // Abandoned, so its exception must still be observed by someone.
        _ = task.ContinueWith(t => { _ = t.Exception; },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        _logger.LogWarning(
            "[LicenseService] Activation for '{Client}' did not return within {Seconds}s. " +
            "The work was left running; it may still complete.", clientName, (int)budget.TotalSeconds);

        return new LicenseActivationResult(false, _accessor.Tier,
            $"SQLTriage waited {(int)budget.TotalSeconds} seconds for the activation and it has not "
            + "answered. Activating reads every .aesgcm bundle in the install folder and tries your "
            + "key on each one, so a drive that is slow or no longer connected can hold it up. The "
            + "activation is still running and may still finish. Do not assume it failed: reload "
            + "the page in a minute and read the tier shown at the top of this card.",
            TimedOut: true);
    }

    /// <summary>
    /// <see cref="Deactivate"/> with the same wall-clock ceiling, for the same reason: it calls
    /// <see cref="Initialize"/>, which is the same folder enumeration and the same per-file read.
    /// </summary>
    /// <returns><c>true</c> if the deactivation finished inside the budget.</returns>
    public async Task<bool> DeactivateAsync(TimeSpan? timeout = null)
    {
        var budget = timeout ?? ActivateProbeTimeout;
        var work = DeactivateWorkOverrideForTests;

        var task = Task.Run(() =>
        {
            if (work is null) Deactivate();
            else work();
        });

        using var delayCts = new CancellationTokenSource();
        var finished = await Task.WhenAny(task, Task.Delay(budget, delayCts.Token))
            .ConfigureAwait(false);

        if (ReferenceEquals(finished, task))
        {
            delayCts.Cancel();
            await task.ConfigureAwait(false);   // rethrows in place, as above
            return true;
        }

        _ = task.ContinueWith(t => { _ = t.Exception; },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        _logger.LogWarning(
            "[LicenseService] Deactivate did not return within {Seconds}s. The work was left " +
            "running; it may still complete.", (int)budget.TotalSeconds);

        return false;
    }

    // ── Key-file pickup (board #19) ──────────────────────────────────────────

    /// <summary>
    /// One-shot guard. Set BEFORE any pickup work rather than after, so a throw inside pickup
    /// cannot leave it clear and re-arm the recursion.
    ///
    /// <para><b>The recursion is real, not theoretical.</b> <c>TryActivate</c> calls
    /// <c>Initialize</c>, and pickup calls <c>TryActivate</c>. Without this flag,
    /// pickup → TryActivate → Initialize → pickup would run until the stack ran out; and a
    /// <c>Deactivate</c> (which also re-runs Initialize) would be undone on the spot by a re-pickup
    /// of a key file still on disk.</para>
    /// </summary>
    private bool _pickupCompleted;

    /// <summary>
    /// What the last key-file pickup pass did, or null when pickup has not run. A LIVE property,
    /// like <see cref="LastFullFailure"/>, because the card binds it and a message captured into a
    /// return value defends one surface and leaves the other one wrong.
    /// </summary>
    public KeyFilePickupOutcome? LastKeyFilePickup { get; private set; }

    /// <summary>
    /// Every <c>*.key.txt</c> the last pickup pass saw and what it made of each. Replaced wholesale
    /// on each pass; never null. The card renders this as a standing panel, which is the only thing
    /// that makes an un-restarted or held drop visible: between the drop and the pickup, a
    /// plaintext credential is sitting in the install folder.
    /// </summary>
    public IReadOnlyList<KeyFilePairStatus> LastKeyFileScan => LastKeyFileScanSnapshot.Pairs;

    /// <summary>
    /// The same list with the moment it was measured, as ONE atomic read. The card dates its
    /// standing panel from this rather than from <see cref="LastKeyFilePickup"/>: the outcome and
    /// the scan are published by different statements, and a second circuit running a pass between
    /// two reads would date one list with another pass's clock.
    /// </summary>
    public KeyFileScanSnapshot LastKeyFileScanSnapshot => Volatile.Read(ref _keyFileScan);

    private KeyFileScanSnapshot _keyFileScan = KeyFileScanSnapshot.Empty;

    /// <summary>
    /// Publishes the standing list and stamps it. The stamp is taken HERE, with the list, so it
    /// dates the thing it is attached to.
    /// </summary>
    private void PublishKeyFileScan(IReadOnlyList<KeyFilePairStatus> pairs)
        => Volatile.Write(ref _keyFileScan, new KeyFileScanSnapshot(pairs, DateTime.UtcNow));

    /// <summary>The suffix that pairs a key file to a bundle. Rule P1: exact base name.</summary>
    private const string KeyFileSuffix = ".key.txt";

    /// <summary>Where a key file goes when its key did not open the bundle it was paired with.</summary>
    private const string RejectedSuffix = ".rejected";

    /// <summary>
    /// Runs key-file pickup at most once per process.
    ///
    /// <para>Everything is inside a single try/catch. Refuter correction 2 of the 15:35 addendum:
    /// a malformed customer name (empty, or carrying the AAD's pipe separator) throws
    /// <c>ArgumentException</c> out of <c>AadBuilder.Build</c>, and that must be reported as a
    /// pickup failure rather than escaping into <c>Initialize</c> and taking the free bundle down
    /// with it.</para>
    /// </summary>
    private void RunKeyFilePickupOnce()
    {
        if (_pickupCompleted) return;
        _pickupCompleted = true;

        if (!OperatingSystem.IsWindows())
        {
            LastKeyFilePickup = null;   // DPAPI-bound feature; nothing to say on another platform
            return;
        }

        // THE OPERATOR'S KILL SWITCH. DO NOT ACT BUT STILL LIST.
        //
        // It logs AND publishes an outcome rather than returning silently, so an engineer reading
        // either the log or LastKeyFilePickup can see that pickup was configured away rather than
        // broken — and, since 2026-09-03, it also LISTS what is in the folder without touching any
        // of it. See ListKeyFilesWithoutActing for why.
        if (!KeyFilePickupEnabledInConfig)
        {
            ListKeyFilesWithoutActing();
            return;
        }

        RunKeyFilePickupCore(acceptKeyFilePath: null);
    }

    /// <summary>
    /// The admin-gated "Check for a key file now" action. Runs the pass regardless of the one-shot,
    /// over the SAME code path start-up runs, so nothing exists that only the button can do. Called
    /// from the card's click handler, never from inside <see cref="Initialize"/>.
    ///
    /// <para>It does not RESET the one-shot — three surfaces said it did, and the code has always
    /// set the flag rather than clearing it. Setting it is what keeps the <c>TryActivate</c> inside
    /// from re-entering <c>Initialize</c> and re-running pickup underneath this call.</para>
    /// </summary>
    public KeyFilePickupOutcome RunKeyFilePickupNow()
    {
        _pickupCompleted = true;   // set first: the TryActivate inside will re-enter Initialize
        if (!OperatingSystem.IsWindows())
        {
            return Record(KeyFilePickupKind.NotRun, null, null, null, 0,
                KeyFileConsumeState.NotApplicable,
                "Activating from a key file needs Windows, because the key is stored with Windows DPAPI.");
        }

        if (!KeyFilePickupEnabledInConfig) return ListKeyFilesWithoutActing();

        return RunKeyFilePickupCore(acceptKeyFilePath: null);
    }

    /// <summary>
    /// The admin's explicit "Accept licence for &lt;name&gt;" on a HELD pair.
    ///
    /// <para>RATCHET WITH ADMIN CONFIRM (Adrian, 2026-09-02 15:35). A key file whose customer is
    /// not the saved one is held, not refused for good: this is the one path that gets past the
    /// identity gate, and it exists so that switching a licence identity is a deliberate act
    /// somebody performed, rather than a file appearing in a folder. The caller is responsible for
    /// the admin gate — the card applies <c>MayInstallBundle</c> before it calls this.</para>
    /// </summary>
    /// <param name="keyFilePath">Full path of the key file the operator accepted.</param>
    public KeyFilePickupOutcome AcceptKeyFilePair(string keyFilePath)
    {
        _pickupCompleted = true;
        if (!OperatingSystem.IsWindows())
        {
            return Record(KeyFilePickupKind.NotRun, null, null, null, 0,
                KeyFileConsumeState.NotApplicable,
                "Activating from a key file needs Windows, because the key is stored with Windows DPAPI.");
        }

        // THE SWITCH BINDS THE ADMIN PATH TOO. An install configured never to read a dropped
        // credential must not have that decision undone by a button, however well gated the button
        // is: the operator who set the key is the one who owns the answer. It still LISTS: refusing
        // to act on a credential is not a reason to stop saying one is on disk.
        if (!KeyFilePickupEnabledInConfig) return ListKeyFilesWithoutActing();

        return RunKeyFilePickupCore(acceptKeyFilePath: keyFilePath);
    }

    /// <summary>
    /// THE DISABLED ARM. <b>Do not act, but still list</b> (Adrian, DECISIONS 2026-09-03 11:20).
    ///
    /// <para>Runs the same two globs the acting pass runs — <c>*.key.txt</c> and the
    /// <c>*.key.txt.rejected</c> residue — pairs them against the installed bundles, and publishes
    /// every file it saw as an un-acted-on state. It <b>opens nothing, renames nothing and removes
    /// nothing</b>: no <c>FileInfo</c>, no <c>ReadAllBytes</c>, no parse, no identity gate, no
    /// decrypt. A directory listing is not a read of the credential.</para>
    ///
    /// <para><b>Why the switch does not also silence the panel.</b> Turning pickup off is a
    /// decision not to CONSUME a dropped credential. It was never a decision to stop being told one
    /// is sitting beside the executable — and the standing panel is the only surface in the product
    /// that says so. Before this, a disabled install published an empty scan, so the card rendered
    /// nothing at all about key files and a plaintext 32-byte AES key could sit in the install
    /// folder indefinitely with every screen silent. That is the same failure the residue glob was
    /// added to close, reached through the config file instead of through a rename.</para>
    ///
    /// <para><b>The log.</b> One <c>Skipped</c> line, so an engineer reading the log sees the
    /// feature was configured away rather than broken, then one scan summary, so they can see what
    /// the pass declined to act on. The ACL check is NOT run: it exists to warn that a pair
    /// <i>this install is about to open</i> could have been planted, and nothing here opens
    /// anything.</para>
    /// </summary>
    private KeyFilePickupOutcome ListKeyFilesWithoutActing()
    {
        var installDir = InstallDirectory;

        _logger.LogInformation(
            "[KeyFilePickup] Skipped: {Key} is false in appsettings.json.", KeyFilePickupConfigKey);

        string[] bundles, allKeyFiles, rejectedResidue;
        try
        {
            bundles = Directory.GetFiles(installDir, "*.aesgcm", SearchOption.TopDirectoryOnly);
            allKeyFiles = Directory.GetFiles(
                installDir, "*" + KeyFileSuffix, SearchOption.TopDirectoryOnly);
            rejectedResidue = Directory.GetFiles(
                installDir, "*" + KeyFileSuffix + RejectedSuffix, SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            // Same rule as the acting pass: a pass that could not read the folder may not leave an
            // older pass's per-file verdicts on screen.
            PublishKeyFileScan(Array.Empty<KeyFilePairStatus>());
            _logger.LogWarning(
                "[KeyFilePickup] Listing {Dir} failed: {Message} Nothing was opened.",
                installDir, ex.Message);
            return RecordDisabled(0,
                PickupDisabledByConfigMessage
                + $" SQLTriage could not list {installDir}, so it cannot say whether one is waiting: "
                + ex.Message);
        }

        Array.Sort(bundles, StringComparer.Ordinal);
        Array.Sort(allKeyFiles, StringComparer.Ordinal);
        Array.Sort(rejectedResidue, StringComparer.Ordinal);

        var pairs = PairUp(bundles, allKeyFiles, out var pairedKeyPaths);

        var scan = new List<KeyFilePairStatus>();

        // The customer name is null on every entry below, and that is the point: naming the
        // customer means reading the file, and this arm reads no files.
        foreach (var (bundlePath, keyPath) in pairs)
            scan.Add(new KeyFilePairStatus(keyPath, bundlePath, KeyFilePairState.PickupDisabled, null));

        foreach (var orphan in allKeyFiles.Where(k => !pairedKeyPaths.Contains(k)))
            scan.Add(new KeyFilePairStatus(orphan, null, KeyFilePairState.NoBundle, null));

        foreach (var residue in rejectedResidue)
            scan.Add(new KeyFilePairStatus(residue, null, KeyFilePairState.RejectedResidue, null));

        PublishKeyFileScan(scan);

        _logger.LogInformation(
            "[KeyFilePickup] Listed without acting. {Dir}: {Bundles} bundle(s), {Keys} key file(s) "
            + "({Pairs} paired), {Residue} rejected key file(s) left over. Nothing was opened, "
            + "renamed or removed.",
            installDir, bundles.Length, allKeyFiles.Length, pairs.Count, rejectedResidue.Length);

        var orphanCount = allKeyFiles.Length - pairs.Count;
        var parts = new List<string>();
        if (pairs.Count > 0)
            parts.Add($"{pairs.Count} key file(s) with a matching bundle are still in the folder.");
        if (orphanCount > 0)
            parts.Add($"{orphanCount} key file(s) are present with no matching .aesgcm bundle.");
        if (rejectedResidue.Length > 0)
            parts.Add($"{rejectedResidue.Length} key file(s) rejected on an earlier start are still "
                + $"here, renamed to *{KeyFileSuffix}{RejectedSuffix}.");

        var listing = parts.Count == 0
            ? "No key file is beside the program."
            : string.Join(" ", parts)
              + " THEY STILL HOLD A PLAINTEXT KEY. Remove them, or turn pickup back on to use them.";

        return RecordDisabled(pairs.Count, PickupDisabledByConfigMessage + " " + listing);
    }

    /// <summary>
    /// Publishes the disabled arm's outcome. <see cref="KeyFilePickupKind.NotRun"/> is the honest
    /// kind — no pass ran — and <see cref="KeyFilePickupOutcome.PickupDisabledByConfig"/> is what
    /// lets a surface tell this NotRun apart from "not Windows" and "not reached yet" without
    /// reading the message text. No audit row: <c>WriteKeyFileAudit</c> already refuses NotRun, and
    /// a pass that touched nothing is not an event.
    /// </summary>
    private KeyFilePickupOutcome RecordDisabled(int pairsSeen, string message)
    {
        var outcome = Build(KeyFilePickupKind.NotRun, null, null, null, pairsSeen,
            KeyFileConsumeState.NotApplicable, message) with { PickupDisabledByConfig = true };
        LastKeyFilePickup = outcome;
        return outcome;
    }

    /// <summary>
    /// The state a paired key file has WITHOUT acting on it: read and parsed for its customer name,
    /// then put through the identity gate, and nothing else. Never decrypts, never renames, never
    /// consumes.
    ///
    /// <para><b>Where it is used.</b> Only for the pairs an <see cref="AcceptKeyFilePair"/> narrowed
    /// away. They used to be listed as <see cref="KeyFilePairState.NotTried"/> with a null customer,
    /// which cost each of them its name and its Accept button — so an administrator who accepted one
    /// of several held pairs could not then accept the next one without restarting the service.</para>
    ///
    /// <para><b>Why reading them is not a new act.</b> The boot pass already reads and parses every
    /// pair it reaches in order to evaluate the identity gate — that is how a held pair gets a
    /// customer name onto the card at all. This does exactly that and stops. The bytes are cleared
    /// straight after the parse, and nothing about the key text leaves this method.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    private KeyFilePairStatus DescribeWithoutOpening(string bundlePath, string keyPath)
    {
        long length;
        try { length = new FileInfo(keyPath).Length; }
        catch (Exception) { return new(keyPath, bundlePath, KeyFilePairState.Unreadable, null); }

        // The same ceiling the acting pass applies, and for the same reason: a file over it is
        // never opened at all.
        if (length > KeyFileParser.MaxKeyFileBytes)
            return new(keyPath, bundlePath, KeyFilePairState.Malformed, null);

        byte[] keyBytes;
        try { keyBytes = File.ReadAllBytes(keyPath); }
        catch (Exception) { return new(keyPath, bundlePath, KeyFilePairState.Unreadable, null); }

        string? customer = null;
        try
        {
            if (!KeyFileParser.TryParse(KeyFileParser.Decode(keyBytes), out customer, out _, out _))
                return new(keyPath, bundlePath, KeyFilePairState.Malformed, null);
        }
        finally { Array.Clear(keyBytes); }

        // Waiting, not NotTried, when the gate would let it through: nothing opened it, it is whole,
        // and it is still a credential — which is exactly what Waiting's card word says.
        return IdentityGateAllows(customer!, out _)
            ? new(keyPath, bundlePath, KeyFilePairState.Waiting, customer)
            : new(keyPath, bundlePath, KeyFilePairState.HeldForIdentity, customer);
    }

    /// <summary>
    /// Rule P1 pairing, shared by the acting pass and the disabled listing so the two cannot drift
    /// onto different ideas of what "paired" means.
    /// </summary>
    private static List<(string BundlePath, string KeyPath)> PairUp(
        string[] bundles, string[] allKeyFiles, out HashSet<string> pairedKeyPaths)
    {
        pairedKeyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pairs = new List<(string BundlePath, string KeyPath)>();
        foreach (var bundlePath in bundles)
        {
            var keyPath = KeyFilePathFor(bundlePath);
            var match = allKeyFiles.FirstOrDefault(
                k => string.Equals(k, keyPath, StringComparison.OrdinalIgnoreCase));
            if (match is null) continue;   // Rule P3: a bundle with no key file changes nothing
            pairedKeyPaths.Add(match);
            pairs.Add((bundlePath, match));
        }

        return pairs;
    }

    /// <summary>
    /// 0 = no pass in flight, 1 = one is. <c>Interlocked</c> rather than a lock, because the loser
    /// must be TOLD it lost and not queued behind the winner: a second overwrite of a file the
    /// first pass already zeroed and deleted is not work worth waiting for.
    ///
    /// <para><b>The race is reachable.</b> <see cref="LicenseService"/> is a DI singleton, and both
    /// <see cref="RunKeyFilePickupNow"/> and <see cref="AcceptKeyFilePair"/> are public and reached
    /// from a Blazor circuit through <c>Task.Run</c> — two administrators clicking together ran two
    /// passes over the same folder. <c>ConsumeKeyFile</c> opens the key file
    /// <c>FileShare.None</c>, so the loser's overwrite threw and it reported
    /// <see cref="KeyFileConsumeState.Intact"/> — "THE KEY FILE IS STILL ON DISK WITH THE KEY IN
    /// IT" — about a file the winner had already zeroed and removed. No credential leaked; a false
    /// alarm printed.</para>
    ///
    /// <para><b>Re-entrancy is safe.</b> The one path that re-enters is
    /// pickup → <c>TryActivate</c> → <c>Initialize</c> → <see cref="RunKeyFilePickupOnce"/>, and
    /// that returns on <c>_pickupCompleted</c> before it ever reaches this gate.</para>
    /// </summary>
    private int _pickupInFlight;

    /// <summary>
    /// The pickup pass. <paramref name="acceptKeyFilePath"/> null = the boot-time pass over every
    /// pair with the identity gate live; non-null = one named pair with the gate bypassed by an
    /// administrator who clicked Accept.
    ///
    /// <para>ONE AT A TIME. A second entry while a pass is in flight is refused by name rather than
    /// queued — see <see cref="_pickupInFlight"/>.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    private KeyFilePickupOutcome RunKeyFilePickupCore(string? acceptKeyFilePath)
    {
        if (Interlocked.CompareExchange(ref _pickupInFlight, 1, 0) != 0)
        {
            _logger.LogInformation(
                "[KeyFilePickup] A pass is already running; this one was not started.");
            // BUILD, NOT RECORD. This call did nothing, so it publishes nothing: overwriting
            // LastKeyFilePickup here would let the loser's "already running" replace the winner's
            // real outcome on the card, and an audit row would count an act that never happened.
            return Build(KeyFilePickupKind.NotRun, null, null, null, 0,
                KeyFileConsumeState.NotApplicable,
                "A key-file check is already running. Nothing was started twice, and nothing on "
                + "disk was touched by this request. Wait for it to finish and read the result.");
        }

        try
        {
            return PickUpKeyFile(acceptKeyFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[KeyFilePickup] The pickup pass threw and was contained. The licence path below it " +
                "runs unaffected; no key file was consumed on this path.");
            // Same rule as the enumeration failure below: a pass that threw does not know what is
            // in the folder, so it may not leave the previous pass's per-file verdicts on screen.
            var seenBeforeTheThrow = LastKeyFileScan.Count;
            PublishKeyFileScan(Array.Empty<KeyFilePairStatus>());
            return Record(KeyFilePickupKind.Failed, null, null, null,
                seenBeforeTheThrow, KeyFileConsumeState.NotApplicable,
                "Checking for a key file failed: " + ex.Message
                + " Nothing was activated and no key file was removed.");
        }
        finally
        {
            // In a finally, not after the return: the catch above returns too, and a throw that
            // escaped both would otherwise leave the gate shut for the life of the process — every
            // later click answering "a check is already running" about a pass that ended.
            Interlocked.Exchange(ref _pickupInFlight, 0);
        }
    }

    [SupportedOSPlatform("windows")]
    private KeyFilePickupOutcome PickUpKeyFile(string? acceptKeyFilePath)
    {
        var installDir = InstallDirectory;

        // WARN ONLY (Adrian, 2026-09-02 15:35). The security premise of this feature is "only
        // someone who can write the install folder can drop a pair". When that is not true the
        // operator is told, on the card and in the log, and pickup still runs. A refusal here was
        // the recommendation and was ruled against, because it fails closed on a real client's
        // activation.
        var looseAcl = DescribeLooseInstallFolderAcl(installDir);
        if (looseAcl is not null)
        {
            _logger.LogWarning(
                "[KeyFilePickup] WARNING {Dir} grants write to {Principals}. A key file there could " +
                "be planted by someone who is not an administrator.", installDir, looseAcl);
        }

        string[] bundles;
        try
        {
            bundles = Directory.GetFiles(installDir, "*.aesgcm", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            // The scan list belongs to the pass that produced it. Leaving the PREVIOUS pass's list
            // standing would make the card render per-file state words decided against a folder
            // THIS pass could not read, beside a panel saying the check failed.
            PublishKeyFileScan(Array.Empty<KeyFilePairStatus>());
            return Record(KeyFilePickupKind.Failed, null, null, null, 0,
                KeyFileConsumeState.NotApplicable,
                $"SQLTriage could not read {installDir}: {ex.Message}", looseAcl);
        }

        // Deterministic order, not the filesystem's. Rule P5: the first pair that opens wins and
        // the run stops; every other key file is left exactly as it is. Consuming a credential that
        // was not used destroys the operator's material for no benefit.
        Array.Sort(bundles, StringComparer.Ordinal);

        var scan = new List<KeyFilePairStatus>();

        // The pairs an Accept narrowed away. Declared here because NothingActedOnMessage below
        // counts them, and a local function may only capture locals already in scope.
        var untouchedByAccept = new List<(string BundlePath, string KeyPath)>();

        // Key files with NO bundle are listed and NEVER OPENED (Rule P2). Reading a credential you
        // cannot use is the same act as reading one you can.
        string[] allKeyFiles;
        try
        {
            allKeyFiles = Directory.GetFiles(installDir, "*" + KeyFileSuffix, SearchOption.TopDirectoryOnly);
        }
        catch (Exception) { allKeyFiles = Array.Empty<string>(); }
        Array.Sort(allKeyFiles, StringComparer.Ordinal);

        // RESIDUE — the second glob, and the reason it exists.
        //
        // A rejected key file is RENAMED to <name>.key.txt.rejected rather than deleted, and the
        // glob above cannot see it: "*.key.txt" does not match "X.key.txt.rejected". Without this
        // second enumeration the very next start reported "No key file is waiting beside the
        // program." while a plaintext 32-byte AES key sat in the install folder, and nothing on any
        // screen said otherwise. The rename is still the right trade (see RenameRejected), but it
        // is only honest while the residue keeps being named — the card saying so is the whole
        // justification, and it held for exactly one pass before this.
        string[] rejectedResidue;
        try
        {
            rejectedResidue = Directory.GetFiles(
                installDir, "*" + KeyFileSuffix + RejectedSuffix, SearchOption.TopDirectoryOnly);
        }
        catch (Exception) { rejectedResidue = Array.Empty<string>(); }
        Array.Sort(rejectedResidue, StringComparer.Ordinal);

        var pairs = PairUp(bundles, allKeyFiles, out var pairedKeyPaths);

        foreach (var orphan in allKeyFiles.Where(k => !pairedKeyPaths.Contains(k)))
            scan.Add(new KeyFilePairStatus(orphan, null, KeyFilePairState.NoBundle, null));

        // Residue is LISTED and never opened, for the same reason an unpaired key file is not:
        // reading a credential you are not going to use is the same act as reading one you are.
        foreach (var residue in rejectedResidue)
            scan.Add(new KeyFilePairStatus(residue, null, KeyFilePairState.RejectedResidue, null));

        // The one sentence a pass that activated nothing is allowed to say. It may NOT claim
        // nothing is waiting while a key file — orphaned, rejected, or paired and simply not
        // opened by this pass — is still in the folder.
        string NothingActedOnMessage()
        {
            var orphanCount = scan.Count(s => s.State == KeyFilePairState.NoBundle);
            var residueCount = scan.Count(s => s.State == KeyFilePairState.RejectedResidue);
            var notTriedCount = scan.Count(s => s.State == KeyFilePairState.NotTried);
            var parts = new List<string>();

            // VERIFIER ROUND 3, V2. The Accept path narrows the pass to one pair, and when that
            // pair's key file has gone between the render and the click the pass acts on nothing —
            // while a SECOND paired key file is sitting in the folder holding a plaintext key. This
            // counted orphans and residue only, so it returned the bare "No key file is waiting
            // beside the program." and that sentence went to the card and to a toast. A pair the
            // pass did not open is exactly as present as an orphan.
            //
            // The Accept path's own untouched pairs are counted from the LIST the accept narrowed
            // away, not from a NotTried state word: since the re-scan below they carry their real
            // state (held, waiting, malformed) and a state-only count would silently drop them.
            notTriedCount += untouchedByAccept.Count;

            if (notTriedCount > 0)
                parts.Add($"{notTriedCount} key file(s) with a matching bundle were not opened by "
                    + "this check and are still in the folder. THEY STILL HOLD A PLAINTEXT KEY. "
                    + "Remove them once the licence is active.");

            if (orphanCount > 0)
                parts.Add($"{orphanCount} key file(s) are present with no matching .aesgcm bundle. "
                    + "They were not opened. Install the matching bundle, or delete them.");

            if (residueCount > 0)
                parts.Add($"{residueCount} key file(s) rejected on an earlier start are still here, "
                    + $"renamed to *{KeyFileSuffix}{RejectedSuffix}. THEY STILL HOLD A PLAINTEXT KEY. "
                    + "Remove them.");

            return parts.Count == 0
                ? "No key file is waiting beside the program."
                : string.Join(" ", parts);
        }

        // THE ACCEPT PATH NARROWS WHICH PAIR IS OPENED, NOT WHICH PAIRS ARE LISTED.
        //
        // VERIFIER ROUND 3, V1. This used to narrow `pairs` before the loop that fills `scan`, so
        // every OTHER waiting key file simply never reached the list — and the list is published
        // wholesale below, so accepting one held pair erased the standing panel that was the only
        // thing naming the others. Two held pairs on a virgin install, one Accept, and a second
        // plaintext key file was still on disk with nothing on any screen saying so. That is the
        // same failure the residue glob above was added to close, on a different path.
        //
        // ACCEPT RE-SCANS (Adrian, DECISIONS 2026-09-03 11:20). The first fix listed the untouched
        // pairs as NotTried with a null customer, which is a DEMOTION: the card shows an Accept
        // button only for HeldForIdentity WITH a name, so accepting one of three held pairs left
        // the other two named on the panel and unactionable — the administrator's only way forward
        // was to restart the service. The pass now re-scans those pairs after it has acted and
        // gives each one the state it really has, so a formerly-held pair keeps its customer name
        // and its own Accept button.
        //
        // Re-scanned AFTER the act, not before: an Accept changes which customer this install is
        // licensed to, so the identity gate's answer for every other pair is only true once the
        // activation has landed. Two pairs for the same customer are the case that separates them —
        // accept one and the other is no longer foreign.
        if (acceptKeyFilePath is not null)
        {
            var accepted = pairs
                .Where(p => string.Equals(p.KeyPath, acceptKeyFilePath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            untouchedByAccept = pairs.Where(p => !string.Equals(
                p.KeyPath, acceptKeyFilePath, StringComparison.OrdinalIgnoreCase)).ToList();

            pairs = accepted;
        }

        // Idempotent: every publish site below calls it, and only the first call appends.
        var untouchedListed = false;
        void ListUntouchedPairs()
        {
            if (untouchedListed) return;
            untouchedListed = true;
            foreach (var (bundlePath, keyPath) in untouchedByAccept)
                scan.Add(DescribeWithoutOpening(bundlePath, keyPath));
        }

        if (pairs.Count == 0)
        {
            ListUntouchedPairs();
            PublishKeyFileScan(scan);

            // The accepted pair is gone. A card is rendered once and clicked later, and the panel
            // it was clicked from is documented as possibly stale — so this is a NORMAL outcome,
            // and it has to say which file it went looking for. NothingActedOnMessage then names
            // everything still in the folder, the untried pairs above included.
            var nothing = NothingActedOnMessage();
            if (acceptKeyFilePath is not null)
            {
                var acceptedName = Path.GetFileName(acceptKeyFilePath);
                _logger.LogWarning(
                    "[KeyFilePickup] Accept named {Key}, which is no longer paired with an installed "
                    + "bundle in {Dir}. Nothing was opened.", acceptedName, installDir);
                nothing = $"{acceptedName} is no longer beside the program with its bundle, so "
                    + $"nothing was activated. {nothing}";
            }

            return Record(KeyFilePickupKind.NoKeyFile, null, null, null, 0,
                KeyFileConsumeState.NotApplicable, nothing, looseAcl);
        }

        _logger.LogInformation(
            "[KeyFilePickup] Scanning {Dir}: {Bundles} bundle(s), {Keys} key file(s), {Pairs} pair(s).",
            installDir, bundles.Length, allKeyFiles.Length, pairs.Count);

        KeyFilePickupOutcome? firstFailure = null;

        // ONE SIGNED ROW PER PAIR, not one per pass.
        //
        // Only the ordinal-first failure is ever PUBLISHED — a card states the outcome of a pass,
        // and listing several is how an operator learns to skim. The ledger is the opposite: two
        // problem pairs in one pass used to leave exactly one row, so the second pair's outcome
        // existed nowhere an auditor reads, and a pair that failed before a later pair activated
        // left no trace at all. Every terminal per-pair result goes through here, and the row
        // carries the pair's position so the pass can be reconstructed from the ledger alone.
        KeyFilePickupOutcome Note(
            int index, KeyFilePickupKind kind, string? bundleName, string? keyName, string? customer,
            KeyFileConsumeState consume, string message)
        {
            var built = Build(kind, bundleName, keyName, customer, pairs.Count, consume, message, looseAcl);
            WriteKeyFileAudit(built, $"{index + 1} of {pairs.Count}");
            firstFailure ??= built;
            return built;
        }

        for (int i = 0; i < pairs.Count; i++)
        {
            var (bundlePath, keyPath) = pairs[i];
            var bundleName = Path.GetFileName(bundlePath);
            var keyName = Path.GetFileName(keyPath);

            // Bounded BEFORE the read. The ceiling is what a caller can make this service read,
            // and a file over it is never opened at all.
            long length;
            try { length = new FileInfo(keyPath).Length; }
            catch (Exception ex)
            {
                scan.Add(new KeyFilePairStatus(keyPath, bundlePath, KeyFilePairState.Unreadable, null));
                Note(i, KeyFilePickupKind.Failed, bundleName, keyName, null,
                    KeyFileConsumeState.NotApplicable,
                    $"{keyName} could not be read: {ex.Message} It was left in place.");
                continue;
            }

            if (length > KeyFileParser.MaxKeyFileBytes)
            {
                _logger.LogWarning(
                    "[KeyFilePickup] {Key} is {Bytes} bytes, over the {Max}-byte ceiling. Not read.",
                    keyName, length, KeyFileParser.MaxKeyFileBytes);
                scan.Add(new KeyFilePairStatus(keyPath, bundlePath, KeyFilePairState.Malformed, null));
                Note(i, KeyFilePickupKind.Malformed, bundleName, keyName, null,
                    KeyFileConsumeState.NotApplicable,
                    $"{keyName} is too large to be a key file, so it was not read. A key file holds "
                    + "one Customer line and one 24-word phrase.");
                continue;
            }

            byte[] keyBytes;
            try { keyBytes = File.ReadAllBytes(keyPath); }
            catch (Exception ex)
            {
                scan.Add(new KeyFilePairStatus(keyPath, bundlePath, KeyFilePairState.Unreadable, null));
                Note(i, KeyFilePickupKind.Failed, bundleName, keyName, null,
                    KeyFileConsumeState.NotApplicable,
                    $"{keyName} could not be read: {ex.Message} It was left in place.");
                continue;
            }

            if (!KeyFileParser.TryParse(KeyFileParser.Decode(keyBytes),
                    out var customer, out var keyText, out var parseProblem))
            {
                Array.Clear(keyBytes);
                // THE PARSE PROBLEM, NOT THE FILE. Every other terminal branch logs — oversize,
                // held, rejected, unreadable, consumed — and this one was silent, so a malformed
                // drop showed only "1 pair(s)." and then nothing at all about that pair. The
                // problem strings name what is MISSING (no Customer line, no 24-word phrase); no
                // line of the file and no fragment of the key is ever logged.
                _logger.LogWarning(
                    "[KeyFilePickup] {Key} could not be read as a key file: {Problem}",
                    keyName, parseProblem);
                scan.Add(new KeyFilePairStatus(keyPath, bundlePath, KeyFilePairState.Malformed, null));
                Note(i, KeyFilePickupKind.Malformed, bundleName, keyName, null,
                    KeyFileConsumeState.NotApplicable, parseProblem);
                continue;
            }
            Array.Clear(keyBytes);

            // THE IDENTITY GATE RUNS ON THE PARSED NAME, BEFORE ANY DECRYPT. Refuter correction 3:
            // a foreign pair is never decrypted, so a pair minted for another customer is not even
            // proved genuine by this box before it is held.
            if (acceptKeyFilePath is null && !IdentityGateAllows(customer!, out var identityMessage))
            {
                _logger.LogWarning(
                    "[KeyFilePickup] HELD {Key}: it names a different customer from the licence saved " +
                    "on this Windows account. Nothing was decrypted and nothing was consumed.", keyName);
                scan.Add(new KeyFilePairStatus(keyPath, bundlePath, KeyFilePairState.HeldForIdentity, customer));
                Note(i, KeyFilePickupKind.RefusedByIdentity, bundleName, keyName, customer,
                    KeyFileConsumeState.NotApplicable, identityMessage);
                continue;
            }

            // READ-ONLY PROBE. It proves the (customer, key, bundle) triple opens before a single
            // byte is written to user-settings.json, which makes the destructive arm of a failed
            // TryActivate unreachable from here regardless of what board #17 does to it.
            byte[] rawKey;
            try { rawKey = DecodeKeyInput(keyText!); }
            catch (Exception ex)
            {
                // NOT ex.Message. VERIFIER ROUND 3, V3: the comment here used to assert that
                // DecodeKeyInput only fails on SHAPE ("not 24 words", "not Base64") and that its
                // message was therefore safe to print. It was wrong. KeyFileParser.LooksLikeKey
                // hands this branch a line of exactly 24 words or a single Base64 token that
                // decodes to 32 bytes, so the shape arm is UNREACHABLE from pickup and the three
                // causes that are reachable are all Bip39.Decode's: the wordlist resource missing,
                // a word that is not in the wordlist, and a checksum failure. The middle one throws
                // ArgumentException("Word not in BIP39 wordlist: '<w>'.") — and <w> is a token read
                // out of the operator's key file, which this then wrote to the log and onto the
                // card. The TYPE NAME is printed instead: it separates a broken install from a
                // mistyped phrase and it cannot carry a byte of the file.
                _logger.LogWarning(
                    "[KeyFilePickup] The key line in {Key} is not a usable licence key ({Kind}). "
                    + "The line itself is not logged.", keyName, ex.GetType().Name);
                scan.Add(new KeyFilePairStatus(keyPath, bundlePath, KeyFilePairState.Malformed, customer));
                Note(i, KeyFilePickupKind.Malformed, bundleName, keyName, customer,
                    KeyFileConsumeState.NotApplicable,
                    $"The key in {keyName} is not a usable licence key, so nothing was activated. A "
                    + "licence phrase is exactly 24 words from the standard word list. Check it "
                    + "against your licence email, whole.");
                continue;
            }

            try
            {
                if (!ProbeBundleOpens(bundlePath, customer!, rawKey))
                {
                    // The auth tag fails identically for a customer name that is not the one the
                    // bundle was issued to, for a key from a different issue, and for a damaged
                    // file. This does not diagnose which — a fabricated cause in a log outlives the
                    // corrected one in the UI.
                    var renamed = RenameRejected(keyPath);
                    _logger.LogWarning(
                        "[KeyFilePickup] Rejected {Key}: the key did not open {Bundle}. Renamed to " +
                        "{Renamed}. The saved licence is unchanged.",
                        keyName, bundleName, renamed is null ? "(rename failed)" : Path.GetFileName(renamed));

                    scan.Add(new KeyFilePairStatus(renamed ?? keyPath, bundlePath,
                        KeyFilePairState.Rejected, customer));
                    Note(i, KeyFilePickupKind.Rejected, bundleName, keyName, customer,
                        KeyFileConsumeState.NotApplicable,
                        $"The key in {keyName} did not open {bundleName}, so nothing was activated and "
                        + "your saved licence was not changed. Three causes fail the same way here, so "
                        + "this cannot say which one it is: the customer name may not be the one the "
                        + "bundle was issued to, the key may be from a different issue of the bundle, "
                        + "or the bundle file may be damaged. "
                        + (renamed is null
                            ? "The key file was left where it is, and it still holds your key. Remove it."
                            : $"It was renamed to {Path.GetFileName(renamed)} so it is not re-read on "
                              + "every start. IT IS STILL A PLAINTEXT KEY ON DISK, and it stays "
                              + "named on this card until you remove it."));
                    continue;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(rawKey);
            }

            // THE RULED CALL PATH. TryActivate OWNS THE RESTORE, and this code does not repeat it.
            //
            // Pickup used to take its own snapshot here and write it back when the activation
            // failed. A mutation probe deleted that call and the whole licensing suite stayed green
            // (409 passed, 0 failed), because TryActivate already snapshots the working pair before
            // it overwrites it and puts it back on every failing arm that reached the write — the
            // c1f01ae non-destructive-failed-activate work, above at "THE WORKING LICENCE IS
            // SNAPSHOTTED BEFORE IT IS OVERWRITTEN". The second restore could not add cover either:
            // the only way TryActivate's restore fails is SaveLicense throwing, and a second call to
            // the same SaveLicense throws the same way.
            //
            // It was not merely dead, it could LIE. When TryActivate's restore fails it says so in
            // its own message — "your previous licence could NOT be written back ... Re-enter your
            // original customer name and key" — and pickup embeds that sentence verbatim below. A
            // redundant restore that then succeeded would leave the card printing an instruction
            // that was no longer true. One owner, one claim.
            //
            // What still holds the non-destructive property is a test, not this comment:
            // KeyFilePickupTests.AFailedActivationFromAPair_LeavesTheWorkingLicenceInPlace.
            var result = TryActivate(customer!, keyText!);

            if (!result.Success)
            {
                _logger.LogWarning(
                    "[KeyFilePickup] {Key} opened {Bundle} on the probe and the activation still " +
                    "failed. The key file was NOT consumed.", keyName, bundleName);
                scan.Add(new KeyFilePairStatus(keyPath, bundlePath, KeyFilePairState.Waiting, customer));
                Note(i, KeyFilePickupKind.Rejected, bundleName, keyName, customer,
                    KeyFileConsumeState.NotApplicable,
                    $"{bundleName} opened with the key in {keyName}, and activating it still failed: "
                    + (result.ErrorMessage ?? "no reason was recorded.")
                    + $" {keyName} is still on disk and still holds your key.");
                continue;
            }

            // Consume. Overwrite first, delete second, deliberately: if the delete fails what is
            // left is a zero-length file whose bytes were already overwritten — a name, not a
            // credential. Reversed, a failed delete would leave the phrase intact.
            var consume = ConsumeKeyFile(keyPath);
            for (int j = i + 1; j < pairs.Count; j++)
            {
                scan.Add(new KeyFilePairStatus(pairs[j].KeyPath, pairs[j].BundlePath,
                    KeyFilePairState.NotTried, null));
            }
            // THE TWO SURVIVING STATES ARE NOT THE SAME FACT, and listing both as Waiting made the
            // card call a zero-length file a plaintext credential. NotDeleted leaves a name whose
            // bytes are already zeros; Intact leaves the phrase. Only the second one is still
            // "waiting; it still holds your licence key in plain text".
            if (consume == KeyFileConsumeState.NotDeleted)
                scan.Add(new KeyFilePairStatus(keyPath, bundlePath,
                    KeyFilePairState.OverwrittenNotDeleted, customer));
            else if (consume != KeyFileConsumeState.Consumed)
                scan.Add(new KeyFilePairStatus(keyPath, bundlePath, KeyFilePairState.Waiting, customer));

            // AFTER the activation, so the identity gate inside sees the licence this pass just
            // saved. That is the whole reason the re-scan is here and not before the loop.
            ListUntouchedPairs();
            PublishKeyFileScan(scan);

            _logger.LogInformation(
                "[KeyFilePickup] Activated from key file. Bundle={Bundle}, Client={Client}, Consume={Consume}.",
                bundleName, customer, consume);

            var consumeSentence = consume switch
            {
                KeyFileConsumeState.Consumed => "The key file was overwritten and removed.",
                KeyFileConsumeState.NotDeleted =>
                    $"Its contents were overwritten but the file could not be removed. Delete {keyPath} yourself.",
                _ => $"THE KEY FILE IS STILL ON DISK WITH THE KEY IN IT: {keyPath}. Remove it.",
            };

            if (consume == KeyFileConsumeState.NotDeleted)
                _logger.LogWarning("[KeyFilePickup] Key file overwritten but NOT deleted: {Path}. Remove it.", keyPath);
            else if (consume == KeyFileConsumeState.Intact)
                _logger.LogError(
                    "[KeyFilePickup] Key file could not be overwritten and is STILL ON DISK WITH THE " +
                    "KEY IN IT: {Path}.", keyPath);

            return Record(KeyFilePickupKind.Activated, bundleName, keyName, customer, pairs.Count,
                consume,
                $"Full Audit was activated from {keyName} for '{customer}'. {consumeSentence}",
                looseAcl);
        }

        ListUntouchedPairs();
        PublishKeyFileScan(scan);

        // Nothing activated. Publish the FIRST failure, which is the ordinal-first pair's — the
        // same ordering the winner would have been chosen by, so the card names the pair an
        // operator would look at first. Every pair's own row is already in the ledger.
        var outcome = firstFailure ?? Build(KeyFilePickupKind.NoKeyFile, null, null, null, pairs.Count,
            KeyFileConsumeState.NotApplicable, NothingActedOnMessage(), looseAcl);

        LastKeyFilePickup = outcome;

        // NOT re-audited when it came from Note(): publishing a pass's headline is not a second
        // act, and a duplicate row would inflate the count an auditor reads.
        if (firstFailure is null) WriteKeyFileAudit(outcome);
        return outcome;
    }

    /// <summary>
    /// Rule P1. For <c>X.aesgcm</c> the key file is <c>X.key.txt</c> in the same folder — strip the
    /// extension and append. This reproduces the minted name character for character, because the
    /// encryptor writes <c>&lt;slug&gt;-&lt;ts&gt;.aesgcm</c> and
    /// <c>&lt;slug&gt;-&lt;ts&gt;.key.txt</c> from the same two parts.
    /// </summary>
    internal static string KeyFilePathFor(string bundlePath)
    {
        var dir = Path.GetDirectoryName(bundlePath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(bundlePath);
        return Path.Combine(dir, baseName + KeyFileSuffix);
    }

    /// <summary>
    /// THE RATCHET. A key file may renew or update the licence this account already holds, silently.
    /// Switching identity, or activating on an install that holds nothing, needs an administrator to
    /// click Accept on the card.
    ///
    /// <para>The AAD binds the customer name, so a pair minted for customer B and dropped on
    /// customer A's box WOULD decrypt and activate. That is not a privilege escalation past Full —
    /// whoever plants it already holds a valid Full bundle and its key — but it is an identity
    /// substitution with commercial and evidentiary consequences: report headers name the wrong
    /// customer, seats are charged against the wrong licence, and customer A's saved licence is
    /// replaced. It is also the ACCIDENT case, an operator with two clients on one laptop.</para>
    /// </summary>
    private bool IdentityGateAllows(string customer, out string message)
    {
        var (savedName, _) = _userSettings.GetSavedLicense();

        if (savedName is null)
        {
            message =
                $"A key file for '{customer}' is waiting beside the program. No licence is saved on "
                + "this Windows account yet, so SQLTriage did not open it. An administrator can "
                + $"accept it below. Nothing has been decrypted and the key file has not been touched.";
            return false;
        }

        if (!string.Equals(savedName, customer, StringComparison.Ordinal))
        {
            message =
                $"A key file for '{customer}' is waiting beside the program, and this install is "
                + $"licensed to '{savedName}'. SQLTriage did not open it, because a key file may "
                + "renew the licence you already have but may not change which customer this install "
                + "runs as. An administrator can accept it below. Nothing has been decrypted and the "
                + "key file has not been touched.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    /// <summary>
    /// The read-only probe: does this bundle open with this name and this key? Touches no settings
    /// and writes nothing. Same two AAD build candidates the unlock scan uses.
    /// </summary>
    private bool ProbeBundleOpens(string bundlePath, string customer, byte[] rawKey)
    {
        byte[] wire;
        try { wire = File.ReadAllBytes(bundlePath); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[KeyFilePickup] Bundle {File} could not be read.", Path.GetFileName(bundlePath));
            return false;
        }

        var runBuild = ReadBuildNumber();
        var buildCandidates = runBuild == FullBundleAadBuildNumber
            ? new[] { FullBundleAadBuildNumber }
            : new[] { FullBundleAadBuildNumber, runBuild };

        foreach (var build in buildCandidates)
        {
            var aad = AadBuilder.Build(customer, FullBundleTierName, BundleVersion, build);
            try
            {
                BundleCrypto.DecryptManifest(wire, rawKey, aad);
                return true;
            }
            catch (CryptographicException) { /* try the next build candidate */ }
            catch (InvalidDataException) { return false; }   // format error — no candidate fixes it
        }

        return false;
    }

    // RestoreSnapshot lived here. It was pickup's own write-back of the licence saved before a
    // pickup-driven TryActivate, and it was removed rather than left unmeasured: TryActivate
    // restores its own snapshot, the mutation probe showed no cell could tell the difference, and
    // the duplicate could contradict the message pickup had already copied out of TryActivate.
    // See the comment at the TryActivate call in PickUpKeyFile.

    /// <summary>
    /// TEST SEAM, invoked between the overwrite and the delete with the key file's path and the
    /// number of bytes the overwrite ACTUALLY WROTE.
    ///
    /// <para>It exists because the ORDER is the safety property and nothing else can observe it:
    /// the overwrite opens the file <c>FileShare.None</c>, so a test cannot hold a handle across it
    /// to make the delete fail, and by the time the method returns both steps are over. From inside
    /// this callback a test can open a handle without <c>FileShare.Delete</c> (forcing the delete
    /// to fail, proving the <c>NotDeleted</c> branch is reachable and leaves a zeroed file rather
    /// than a credential). Never set outside tests; nothing in the shipped composition writes it.</para>
    ///
    /// <para><b>The byte count is the load-bearing half, and it was added because the other half
    /// measured nothing.</b> Reading the file back here cannot witness the overwrite: the truncate
    /// that follows the write on the same handle leaves an EMPTY file either way, and asserting
    /// "every byte is zero" over an empty array passes vacuously. Deleting the write outright left
    /// the whole suite green. The count is the only witness that zeros were written at all, so a
    /// test that wants the overwrite guarded must assert on it.</para>
    /// </summary>
    internal Action<string, long>? AfterKeyFileOverwriteForTests { get; set; }

    /// <summary>
    /// TEST SEAM. Called by <see cref="RenameRejected"/> after a STALE <c>.rejected</c> file has
    /// been zeroed and before it is unlinked, with the path and the number of bytes overwritten
    /// (-1 when the overwrite failed). Never set outside tests.
    ///
    /// <para>It exists because the zeroing is otherwise unwitnessable: the file is deleted on the
    /// next line, and a handle held open across the call would make the
    /// <c>FileShare.None</c> overwrite fail — the test would prevent the behaviour it came to
    /// measure. As with the consume seam, the BYTE COUNT is the load-bearing half: the file is
    /// truncated, so reading it back finds an empty file whether or not any zero was written.</para>
    /// </summary>
    internal Action<string, long>? AfterRejectedOverwriteForTests { get; set; }

    /// <summary>
    /// Overwrite the key file's bytes with zeros, then delete it.
    ///
    /// <para><b>This is best effort and is NOT an erasure guarantee.</b> On NTFS a small file's data
    /// lives in the MFT record, a Volume Shadow Copy may hold the old contents, and an SSD's
    /// translation layer may not reuse the same physical page. The mint's own copy also still exists
    /// wherever it was issued from. Say "overwritten and deleted"; never say "securely erased".</para>
    /// </summary>
    /// <summary>
    /// Zero a key file's bytes and truncate it, reporting how many bytes were written. Best effort:
    /// false means the file was NOT overwritten and still holds whatever it held.
    ///
    /// <para>Extracted so <see cref="RenameRejected"/> can reuse the exact overwrite
    /// <see cref="ConsumeKeyFile"/> performs, rather than deleting a stale <c>.rejected</c> file —
    /// itself a plaintext credential — with a bare <c>File.Delete</c>.</para>
    /// </summary>
    private bool OverwriteWithZeros(string path, out long overwrittenBytes)
    {
        overwrittenBytes = 0;
        try
        {
            long length;
            try { length = new FileInfo(path).Length; }
            catch (Exception) { length = 0; }

            // Allocate the CLAMPED size, not the file's: new byte[length] on a length past
            // int.MaxValue throws before a single zero is written.
            int toWrite = (int)Math.Min(length, int.MaxValue);

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                if (toWrite > 0) fs.Write(new byte[toWrite], 0, toWrite);
                fs.Flush(flushToDisk: true);

                // READ OFF THE HANDLE, not restated from toWrite. The handle opens at offset 0, so
                // the position after the write IS the number of bytes this method put on disk. A
                // count assigned beside the write would survive the write being deleted and go on
                // reporting the intent; this one goes to 0, which is what the guarding test needs.
                overwrittenBytes = fs.Position;

                fs.SetLength(0);
                fs.Flush(flushToDisk: true);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[KeyFilePickup] Overwriting {Path} failed.", path);
            return false;
        }
    }

    private KeyFileConsumeState ConsumeKeyFile(string keyPath)
    {
        bool overwritten = OverwriteWithZeros(keyPath, out var overwrittenBytes);

        if (AfterKeyFileOverwriteForTests is { } probe)
        {
            try { probe(keyPath, overwrittenBytes); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[KeyFilePickup] The overwrite test seam threw. Ignored.");
            }
        }

        bool deleted = false;
        try { File.Delete(keyPath); deleted = true; }
        catch (Exception)
        {
            try
            {
                System.Threading.Thread.Sleep(250);
                File.Delete(keyPath);
                deleted = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[KeyFilePickup] Deleting {Path} failed twice.", keyPath);
            }
        }

        if (!overwritten) return KeyFileConsumeState.Intact;
        return deleted ? KeyFileConsumeState.Consumed : KeyFileConsumeState.NotDeleted;
    }

    /// <summary>
    /// Renames a rejected key file to <c>&lt;name&gt;.rejected</c>, or returns null if the rename
    /// failed.
    ///
    /// <para><b>Renamed, not deleted.</b> Deleting it would destroy what may be the operator's only
    /// on-site copy of a credential they were sent, on the strength of a failure whose cause this
    /// code explicitly cannot diagnose. The rename stops the file being re-read on every start — a
    /// loop that would re-open a credential forever — and leaves it recoverable. The trade is
    /// honest and has to be stated: a rejected key file is still a plaintext credential in the
    /// install folder, and the card says so.</para>
    ///
    /// <para><b>A stale <c>.rejected</c> is ZEROED before it is replaced, not just deleted.</b>
    /// The whole reason this method renames rather than deletes is that a rejected key file may be
    /// the operator's only on-site copy of a credential — and the previous version of this line
    /// then destroyed exactly such a file with a bare <c>File.Delete</c>, giving a plaintext key
    /// the one treatment <see cref="ConsumeKeyFile"/> is careful never to give. The choice made
    /// here is ZERO-THEN-DELETE rather than move-aside-with-a-suffix: a second copy under
    /// <c>.rejected.1</c> would accumulate credentials in the install folder on every retry, which
    /// is the opposite of what this feature owes an operator. The file being replaced was ALREADY
    /// rejected once and reported on the card, so it is the older of two copies of a key that does
    /// not open the bundle.</para>
    /// </summary>
    private string? RenameRejected(string keyPath)
    {
        var target = keyPath + RejectedSuffix;
        try
        {
            if (File.Exists(target))
            {
                var overwritten = OverwriteWithZeros(target, out var zeroedBytes);
                // The file is still on disk and its handle is closed, so this is the only moment at
                // which the zeroing can be witnessed: after it, the file is unlinked. Same seam
                // shape, and same reason, as AfterKeyFileOverwriteForTests.
                AfterRejectedOverwriteForTests?.Invoke(target, overwritten ? zeroedBytes : -1);
                File.Delete(target);
            }
            File.Move(keyPath, target);
            return target;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[KeyFilePickup] Renaming {Path} to .rejected failed.", keyPath);
            return null;
        }
    }

    /// <summary>
    /// The broad principals that hold write on the install folder, comma separated, or null when
    /// none do. WARN ONLY — this never refuses a pickup.
    ///
    /// <para>Fails QUIET, not closed, and that is the ruling rather than an oversight: an ACL this
    /// process cannot read is not evidence the folder is loose, and turning an unreadable ACL into
    /// a warning would print one on every install whose folder denies READ_CONTROL.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    private string? DescribeLooseInstallFolderAcl(string installDir)
    {
        // S-1-1-0 Everyone, S-1-5-32-545 Users, S-1-5-11 Authenticated Users, S-1-5-4 Interactive,
        // S-1-5-32-546 Guests. These are the principals whose write access makes "only an
        // administrator could have dropped this pair" untrue.
        var broad = new[]
        {
            System.Security.Principal.WellKnownSidType.WorldSid,
            System.Security.Principal.WellKnownSidType.BuiltinUsersSid,
            System.Security.Principal.WellKnownSidType.AuthenticatedUserSid,
            System.Security.Principal.WellKnownSidType.InteractiveSid,
            System.Security.Principal.WellKnownSidType.BuiltinGuestsSid,
        };

        try
        {
            var security = new DirectoryInfo(installDir).GetAccessControl(
                System.Security.AccessControl.AccessControlSections.Access);
            var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));

            const System.Security.AccessControl.FileSystemRights WriteLike =
                System.Security.AccessControl.FileSystemRights.WriteData
                | System.Security.AccessControl.FileSystemRights.AppendData
                | System.Security.AccessControl.FileSystemRights.Modify
                | System.Security.AccessControl.FileSystemRights.ChangePermissions
                | System.Security.AccessControl.FileSystemRights.TakeOwnership
                | System.Security.AccessControl.FileSystemRights.FullControl;

            var named = new List<string>();
            foreach (System.Security.AccessControl.FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType != System.Security.AccessControl.AccessControlType.Allow) continue;
                if ((rule.FileSystemRights & WriteLike) == 0) continue;
                if (rule.IdentityReference is not System.Security.Principal.SecurityIdentifier sid) continue;
                if (!broad.Any(sid.IsWellKnown)) continue;

                string label;
                try { label = sid.Translate(typeof(System.Security.Principal.NTAccount)).Value; }
                catch (Exception) { label = sid.Value; }
                if (!named.Contains(label)) named.Add(label);
            }

            return named.Count == 0 ? null : string.Join(", ", named);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "[KeyFilePickup] The install folder's ACL could not be read, so no ACL warning is " +
                "raised. An unreadable ACL is not evidence the folder is loose.");
            return null;
        }
    }

    private static KeyFilePickupOutcome Build(
        KeyFilePickupKind kind, string? bundleName, string? keyName, string? customer,
        int pairsSeen, KeyFileConsumeState consume, string message, string? looseAcl = null)
        => new(kind, bundleName, keyName, customer, DateTime.UtcNow, pairsSeen, consume, message)
        {
            InstallFolderLooselyAcled = looseAcl is not null,
            LooseAclPrincipals = looseAcl,
        };

    private KeyFilePickupOutcome Record(
        KeyFilePickupKind kind, string? bundleName, string? keyName, string? customer,
        int pairsSeen, KeyFileConsumeState consume, string message, string? looseAcl = null)
    {
        var outcome = Build(kind, bundleName, keyName, customer, pairsSeen, consume, message, looseAcl);
        LastKeyFilePickup = outcome;
        WriteKeyFileAudit(outcome);
        return outcome;
    }

    /// <summary>
    /// One redacted audit entry per KEY FILE that pickup did something to — not one per pass. No
    /// key material and no key fingerprint: the fingerprint belongs in the log, not in a signed
    /// record a client's auditor reads. <c>NotRun</c> and <c>NoKeyFile</c> write nothing — a pass
    /// that found no key file is not an event.
    /// </summary>
    /// <param name="pairPosition">
    /// "&lt;n&gt; of &lt;total&gt;" for a per-pair row, so a multi-pair pass can be reconstructed
    /// from the ledger alone; null for the pass's own single row (an activation, a failure before
    /// any pair was reached).
    /// </param>
    private void WriteKeyFileAudit(KeyFilePickupOutcome outcome, string? pairPosition = null)
    {
        if (_audit is null) return;
        if (outcome.Kind is KeyFilePickupKind.NotRun or KeyFilePickupKind.NoKeyFile) return;

        try
        {
            _audit.LogLicenseKeyFilePickup(
                activated: outcome.Kind == KeyFilePickupKind.Activated,
                outcomeKind: outcome.Kind.ToString(),
                bundleFileName: outcome.BundleFileName,
                keyFileName: outcome.KeyFileName,
                customerName: outcome.CustomerName,
                consumeState: outcome.Consume.ToString(),
                pairPosition: pairPosition);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[KeyFilePickup] Writing the audit entry failed. The licence stands.");
        }
    }

    // ── Private: Full unlock ─────────────────────────────────────────────────

    private bool TryUnlockFull()
    {
        // B1: clear any prior "expired" marker; it is set again below only if a valid-but-expired
        // bundle is the reason we decline Full this pass.
        _fullExpiredOn = null;
        LastFullFailure = null;
        LastUnlockedBundlePath = null;
        LastBundleScan = Array.Empty<BundleFileStatus>();

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
            LastBundleScan = ScanNotTried();
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
            LastBundleScan = ScanNotTried();
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
        var installDir = InstallDirectory;

        var bundles = Directory.GetFiles(installDir, "*.aesgcm", SearchOption.TopDirectoryOnly);
        if (bundles.Length == 0)
        {
            _logger.LogWarning("[LicenseService] No .aesgcm files found in install dir: {Dir}", installDir);
            return Fail(FullUnlockFailureReason.NoBundleFile,
                $"No .aesgcm bundle file is installed. SQLTriage looks in {installDir} and found none. "
                + "Use 'Install bundle file' below to point at the .aesgcm you were sent, or copy it "
                + "into that folder yourself.");
        }

        // EVERY FILE IS TRIED, AND THE WINNER IS THE NEWEST AUTHENTICATED MINT DATE.
        //
        // This used to return on the FIRST file that opened, in Directory.GetFiles order. That order
        // is the filesystem's, not a preference, so on an install holding several bundles the active
        // licence was an accident of enumeration -- and a re-issued bundle installed beside the old
        // one might never be the one that ran. File write time cannot break the tie either:
        // InstallBundleFile uses File.Copy, which PRESERVES the source's last-write time, so ten
        // bundles copied in on the same afternoon carry ten mint-time stamps in whatever order they
        // were minted. The manifest's createdUtc sits inside the GCM-authenticated ciphertext, so it
        // is both the right fact and one that cannot be edited without breaking decryption.
        //
        // A file with no parseable createdUtc sorts oldest, so any dated bundle beats an undated one.
        // Equal dates keep the first file seen, which is stable for a given folder.
        var scan = new List<BundleFileStatus>(bundles.Length);
        BundleManifest? bestManifest = null;
        string? bestPath = null;
        DateTime bestCreated = DateTime.MinValue;
        int bestIndex = -1;

        foreach (var path in bundles)
        {
            var fileName = Path.GetFileName(path);

            byte[]? wire = null;
            try { wire = File.ReadAllBytes(path); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LicenseService] Bundle {File}: unreadable.", fileName);
                scan.Add(new BundleFileStatus(path, BundleFileState.Unreadable, null, null));
                continue;
            }

            BundleManifest? opened = null;
            int openedBuild = -1;
            foreach (var build in buildCandidates)
            {
                var aad = AadBuilder.Build(clientName, FullBundleTierName, BundleVersion, build);
                try
                {
                    opened = BundleCrypto.DecryptManifest(wire, rawKey, aad);
                    openedBuild = build;
                    break;
                }
                catch (CryptographicException) { /* try next build candidate */ }
                catch (InvalidDataException)  { break; } // format error — no build candidate fixes it
            }

            if (opened is null)
            {
                _logger.LogInformation(
                    "[LicenseService] Bundle {File}: did-not-open-with-saved-key.", fileName);
                scan.Add(new BundleFileStatus(path, BundleFileState.DidNotOpen, null, null));
                continue;
            }

            var created = ParseManifestUtc(opened.CreatedUtc);

            // B1: a valid decrypt is not enough — honour license expiry. Read LIVE off the
            // just-decrypted manifest (same ISO/UTC convention as BundleAccessor.ParseUtc).
            // null/unparseable => perpetual (fail-OPEN: legacy bundles predate this field and
            // must NOT be treated as expired). The date is inside the GCM tag, so tampering it
            // breaks decryption — an attacker cannot forge a later date.
            var expiry = ParseManifestUtc(opened.Features.LicenseExpiryUtc);
            if (expiry is { } exp && DateTime.UtcNow >= exp)
            {
                // THE LATEST LAPSE, NOT THE LAST FILE ENUMERATED. This used to assign
                // unconditionally, so with several expired bundles and none current the date the
                // operator was shown was whichever expired file Directory.GetFiles happened to
                // return last. Ten bundles were minted for one client on 2026-09-02, which is
                // exactly the shape that makes an arbitrary date visible. The date that answers
                // "when did this install stop being licensed" is the LATEST of them.
                // Surfaces "expired, renew" only if NOTHING current opens.
                if (_fullExpiredOn is null || exp > _fullExpiredOn) _fullExpiredOn = exp;
                _logger.LogWarning(
                    "[LicenseService] Bundle {File}: skipped-expired ({Exp:o}).", fileName, exp);
                scan.Add(new BundleFileStatus(path, BundleFileState.Expired, created, exp));
                continue;
            }

            _logger.LogInformation(
                "[LicenseService] Bundle {File}: opened (createdUtc={Created}, AAD_build={Build}).",
                fileName,
                created is { } c ? c.ToString("o", CultureInfo.InvariantCulture) : "(absent)",
                openedBuild);

            scan.Add(new BundleFileStatus(path, BundleFileState.Superseded, created, null));

            var rank = created ?? DateTime.MinValue;
            if (bestManifest is null || rank > bestCreated)
            {
                bestManifest = opened;
                bestPath = path;
                bestCreated = rank;
                bestIndex = scan.Count - 1;
            }
        }

        if (bestManifest is not null)
        {
            // An expired sibling is not this install's state once a current bundle has opened.
            _fullExpiredOn = null;

            scan[bestIndex] = scan[bestIndex] with { State = BundleFileState.InUse };
            LastBundleScan = scan;
            LastUnlockedBundlePath = bestPath;

            // No LastFullFailure reset here: this method clears it on entry, so a success path that
            // also cleared it would be a line no test could turn red.
            SafeReplace(bestManifest, Tier.Full);

            _logger.LogInformation(
                "[LicenseService] Full bundle active: {File} (createdUtc={Created}), KeyFP={FP}. " +
                "{Opened} of {Count} file(s) opened with the saved key.",
                Path.GetFileName(bestPath!),
                bestCreated == DateTime.MinValue
                    ? "(absent)"
                    : bestCreated.ToString("o", CultureInfo.InvariantCulture),
                BundleCrypto.KeyFingerprintHex(rawKey)[..8],
                // AN EXPIRED BUNDLE DID OPEN WITH THE SAVED KEY -- that is how its expiry date was
                // read, off the manifest this method had just decrypted. Excluding it made a folder
                // holding one current and three expired bundles log "1 of 4 opened" beside four
                // per-file lines saying otherwise. The three states below are exactly the ones the
                // key opened.
                scan.Count(s => s.State is BundleFileState.InUse
                                        or BundleFileState.Superseded
                                        or BundleFileState.Expired),
                scan.Count);
            return true;
        }

        LastBundleScan = scan;

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

    /// <summary>
    /// Swaps the accessor's manifest and absorbs anything that escapes the state fan-out.
    ///
    /// <para>BundleAccessor.Replace now isolates each subscriber, so nothing should reach here.
    /// This is the second half of the same rule stated from the caller's side: the licence decision
    /// is complete BEFORE this line runs, so a consumer's exception must not be able to turn a
    /// resolved licence into a thrown activation. Both halves are kept because either one alone
    /// leaves the other file free to reintroduce the coupling.</para>
    /// </summary>
    private void SafeReplace(BundleManifest? manifest, Tier tier)
    {
        try
        {
            _accessor.Replace(manifest, tier);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[LicenseService] A bundle-state subscriber faulted after the licence decision was " +
                "made. The licence stands; the subscriber does not.");
        }
    }

    /// <summary>
    /// Parses an ISO-8601 UTC manifest timestamp, or null when absent or unparseable. Same
    /// convention as BundleAccessor.ParseUtc, kept as its own method here because this file reads
    /// two different manifest dates (createdUtc and licenseExpiryUtc) with the same rules.
    /// </summary>
    private static DateTime? ParseManifestUtc(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        return DateTime.TryParse(iso, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)
            ? dt
            : null;
    }

    /// <summary>
    /// The install folder's bundle files, all marked NotTried. Published by the unlock branches that
    /// give up BEFORE any key is available, so the card can still list what is on disk instead of
    /// enumerating the folder itself on every render.
    /// </summary>
    private IReadOnlyList<BundleFileStatus> ScanNotTried()
    {
        try
        {
            return Directory.GetFiles(InstallDirectory, "*.aesgcm", SearchOption.TopDirectoryOnly)
                .Select(p => new BundleFileStatus(p, BundleFileState.NotTried, null, null))
                .ToList();
        }
        catch (Exception)
        {
            // A folder we cannot enumerate is reported as no files rather than as a crash: this
            // method exists to populate a display, and it must never be the reason a boot fails.
            return Array.Empty<BundleFileStatus>();
        }
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
            SafeReplace(manifest, Tier.Free);
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

        try
        {
            return Convert.FromBase64String(input);
        }
        catch (FormatException)
        {
            // HONEST ABOUT BOTH ACCEPTED FORMS. The .NET message is "The input is not a valid Base-64
            // string...", which names one of the two things this box accepts and hides the other. An
            // operator pasting a licence phrase reads that and has no idea the word count is what
            // decided which branch ran.
            throw new FormatException(
                "that is not a 24-word phrase and not a Base-64 key. What you pasted carries "
                + $"{parts.Length} " + (parts.Length == 1 ? "word" : "words")
                + ". A licence phrase is exactly 24 words. Paste it from your licence email, whole.");
        }
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
