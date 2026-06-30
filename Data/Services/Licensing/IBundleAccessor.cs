/* In the name of God, the Merciful, the Compassionate */

#nullable enable

namespace SQLTriage.Data.Services.Licensing;

/// <summary>License tier — Free (bundled, zero-key) or Full (customer key).</summary>
public enum Tier { Free, Full }

/// <summary>
/// Tier-aware feature flags. Immutable — baked into the GCM-authenticated manifest.
/// Any tamper attempt breaks the auth tag at decrypt time.
/// </summary>
public sealed record BundleFeatures(
    bool RagEnabled,
    bool SpBlitzImport,
    bool FullCorpus,
    IReadOnlyList<int> PermittedCheckIds,
    bool DevToolsCapability = true, // transition fail-open — see BundleManifest.ManifestFeatures.DevTools
    // Gated remediation lane. Fail-CLOSED (default false): this is a WRITE capability, so —
    // unlike DevToolsCapability — only an explicit bundle grant unlocks it. RemediationCreditsPerServer
    // is the signed MSP per-server change-credit allocation the persisted ledger seeds from (0 = none).
    bool Remediation = false,
    int RemediationCreditsPerServer = 0,
    // Gated host-probe lane (OS/AD probes via dbatools). Fail-CLOSED like Remediation: this
    // capability LEAVES the SQL connection (WMI/AD), so only an explicit bundle grant unlocks it.
    // Defaults false in every real bundle until the (off-GitHub) signer sets it; DevBridge unlocks
    // it on a dev build for testing. Stacks on top of the elevation gate.
    bool HostProbe = false,
    // Signed corpus-demo allocation: distinct SQL instances the operator may run the CORPUS audit
    // against per rolling 24h window. 1 = community public limit; 0 = unlimited (full bundle).
    // Default 1 = fail-closed to the community limit when the knob is absent (a missing knob must
    // NOT grant unlimited). The DemoRunLedger meters against this. DemoExpiryUtc, once passed,
    // reverts a bumped allocation to 1. Tier-agnostic — drives off this number, not Tier.
    int DemoCorpusInstancesPer24h = 1,
    // Absolute UTC expiry for a bumped demo allocation; null = no expiry. Once passed, the
    // accessor clamps DemoCorpusInstancesPer24h back to 1.
    DateTime? DemoExpiryUtc = null);

/// <summary>
/// Read-only view of the active bundle. Implemented by <see cref="BundleAccessor"/>.
/// All members are thread-safe; the underlying manifest snapshot is immutable once set.
/// </summary>
public interface IBundleAccessor
{
    /// <summary>True when ANY bundle (Free or Full) has been successfully decrypted.</summary>
    bool IsUnlocked { get; }

    /// <summary>Active tier. Free until a Full bundle decrypts successfully.</summary>
    Tier Tier { get; }

    /// <summary>Customer display name from the manifest. Null on Free tier.</summary>
    string? ClientName { get; }

    /// <summary>Feature flags from the manifest.</summary>
    BundleFeatures Features { get; }

    /// <summary>
    /// Returns true if the current tier permits <paramref name="checkId"/>.
    /// Full tier with an empty PermittedCheckIds list → all checks are permitted.
    /// </summary>
    bool IsCheckPermitted(int checkId);

    /// <summary>
    /// Returns the verbatim text of a file stored in <c>manifest.Files</c> by
    /// forward-slash relative path (e.g. <c>"Config/control_mappings.json"</c>).
    /// Returns null if not in the bundle.
    /// </summary>
    string? GetText(string relativePath);

    /// <summary>
    /// Returns the raw bytes of a file stored in <c>manifest.Files</c>.
    /// Returns null if not in the bundle.
    /// </summary>
    byte[]? GetBytes(string relativePath);

    /// <summary>
    /// Enumerates the handles (filenames) of all YAML check definitions in
    /// <c>manifest.Corpus</c>. Handles end with <c>.yaml</c> (case-insensitive).
    /// </summary>
    IEnumerable<string> EnumerateCorpusYamlHandles();

    /// <summary>Returns the YAML text for <paramref name="handle"/>. Null if not in bundle.</summary>
    string? ReadCorpusYaml(string handle);

    /// <summary>
    /// Returns the SQL sibling for <paramref name="handle"/> (same stem, .sql extension).
    /// Null if no SQL sibling is present.
    /// </summary>
    string? ReadCorpusSqlFallback(string handle);

    /// <summary>
    /// Returns the text or base64 payload of a report asset by filename.
    /// Returns null if not in the bundle or if the active tier is Free.
    /// </summary>
    string? TryGetReportAsset(string reportId);

    /// <summary>
    /// Enumerates the handles (filenames) of all reports in the bundle.
    /// </summary>
    IEnumerable<string> EnumerateReportHandles();

    /// <summary>Fired whenever the bundle state changes (e.g. after activation or deactivation).</summary>
    event EventHandler? BundleStateChanged;
}
