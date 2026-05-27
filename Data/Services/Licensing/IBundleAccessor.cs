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
    IReadOnlyList<int> PermittedCheckIds);

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

    /// <summary>Fired whenever the bundle state changes (e.g. after activation or deactivation).</summary>
    event EventHandler? BundleStateChanged;
}
