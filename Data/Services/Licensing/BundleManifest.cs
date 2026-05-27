/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System.Text.Json.Serialization;

namespace SQLTriage.Data.Services.Licensing;

/// <summary>
/// Plaintext manifest serialised into JSON, gzipped, then AES-GCM-256-encrypted
/// into the .aesgcm wire format. Decrypted at runtime by SQLTriage's LicenseService.
///
/// The exact field set + ordering of this type is part of the public contract with
/// the CorpusEncryptor (sqltriage-corpus repo). Renaming a JSON property here without
/// bumping <see cref="BundleVersion"/> WILL break decryption on existing installs.
/// </summary>
public sealed class BundleManifest
{
    /// <summary>Wire-format version. Increment ONLY on breaking schema changes.</summary>
    [JsonPropertyName("bundleVersion")]
    public int BundleVersion { get; init; } = 1;

    /// <summary>SQLTriage build number this bundle was generated against.</summary>
    [JsonPropertyName("buildNumber")]
    public int BuildNumber { get; init; }

    /// <summary>UTC timestamp at encryption time, ISO 8601 with trailing Z.</summary>
    [JsonPropertyName("createdUtc")]
    public string CreatedUtc { get; init; } = string.Empty;

    /// <summary>Customer-facing display name. Also bound as GCM AAD.</summary>
    [JsonPropertyName("clientName")]
    public string ClientName { get; init; } = string.Empty;

    /// <summary>"Free" or "Full". Also bound as GCM AAD.</summary>
    [JsonPropertyName("tier")]
    public string Tier { get; init; } = "Free";

    /// <summary>Feature flags interpreted by SQLTriage at runtime.</summary>
    [JsonPropertyName("features")]
    public ManifestFeatures Features { get; init; } = new();

    /// <summary>
    /// Verbatim text of the gated Config/*.json files keyed by their relative path.
    /// Keys MUST use forward slashes (e.g., "Config/control_mappings.json")
    /// regardless of operating system.
    /// </summary>
    [JsonPropertyName("files")]
    public Dictionary<string, string> Files { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Verbatim text of YAML check definitions + their SQL siblings, keyed by
    /// the YAML/SQL filename (no directory prefix). YAML and SQL siblings share
    /// the same filename stem, distinguished by extension.
    /// </summary>
    [JsonPropertyName("corpus")]
    public Dictionary<string, string> Corpus { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Tier-aware feature flags stored inside the manifest.
/// Tamper-proof because the manifest is inside the GCM-authenticated ciphertext:
/// any byte flip breaks decryption.
///
/// NOTE: Named <c>ManifestFeatures</c> here to avoid a name collision with the
/// <see cref="BundleFeatures"/> record exposed on <see cref="IBundleAccessor"/>.
/// JSON property names are identical to the encryptor's <c>BundleFeatures</c> class.
/// </summary>
public sealed class ManifestFeatures
{
    /// <summary>Whether the customer's license permits RAG-powered retrieval features.</summary>
    [JsonPropertyName("ragEnabled")]
    public bool RagEnabled { get; init; }

    /// <summary>Whether the customer can import sp_BLITZ CSVs into their assessment.</summary>
    [JsonPropertyName("spBlitzImport")]
    public bool SpBlitzImport { get; init; } = true; // free + full both get this

    /// <summary>Whether the customer can access the full (non-free) corpus.</summary>
    [JsonPropertyName("fullCorpus")]
    public bool FullCorpus { get; init; }

    /// <summary>
    /// Explicit allow-list of check IDs permitted for this customer.
    /// For Free tier this is the curated ~30% non-BLITZ subset.
    /// For Full tier this can be empty (interpreted as "all checks in Corpus").
    /// </summary>
    [JsonPropertyName("checkIds")]
    public List<int> CheckIds { get; init; } = new();
}
