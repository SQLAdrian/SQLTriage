/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System.Text.Json.Serialization;

namespace SQLTriage.Data.Services.Licensing;

/// <summary>
/// Plaintext manifest serialised into JSON, gzipped, then AES-GCM-256-encrypted
/// into the .aesgcm wire format. Decrypted at runtime by SQLTriage's LicenseService.
///
/// The exact field set + ordering of this type is part of the contract with
/// the bundle encryptor. Renaming a JSON property here without
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

    /// <summary>
    /// Verbatim text or base64-encoded payload of RDL and HTML report assets.
    /// Keyed by filename. Available to Full tier only.
    /// </summary>
    [JsonPropertyName("reports")]
    public Dictionary<string, string> Reports { get; init; } = new(StringComparer.Ordinal);
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
    /// Dev-capability claim (2026-06-12): unlocks the dev-tools surface (corpus editors,
    /// check validator, build profile, tuners) in FULL builds at runtime. Single boolean
    /// by design — NOT a tier (self-serve licensing is shelved; don't let complexity creep
    /// back). Nullable for transition: bundles built before the claim existed carry null,
    /// which the accessor treats as PERMITTED (fail-open) because the only Full bundles in
    /// existence are Adrian's own. CorpusEncryptor stamps an explicit false into every
    /// client/free bundle from now on; flip the fail-open default at v1 (worklist).
    /// Community builds compile dev-tools out entirely — this claim is the full-build
    /// runtime layer only.
    /// </summary>
    [JsonPropertyName("devTools")]
    public bool? DevTools { get; init; }

    /// <summary>
    /// Explicit allow-list of check IDs permitted for this customer.
    /// For Free tier this is the curated ~30% non-BLITZ subset.
    /// For Full tier this can be empty (interpreted as "all checks in Corpus").
    /// </summary>
    [JsonPropertyName("checkIds")]
    public List<int> CheckIds { get; init; } = new();

    /// <summary>
    /// Gated remediation lane (write capability). Whether this licence permits applying
    /// remediations at all. FAIL-CLOSED: nullable for transition, but the accessor treats
    /// null/absent as DENIED (only an explicit true grants) — a write capability must not
    /// fail open the way <see cref="DevTools"/> does. CorpusEncryptor stamps this off-GitHub.
    /// </summary>
    [JsonPropertyName("remediation")]
    public bool? Remediation { get; init; }

    /// <summary>
    /// The signed MSP per-server change-credit allocation: how many remediation applies this
    /// licence grants per server. The persisted credit ledger seeds each server's balance from
    /// this on first touch. 0 (default/absent) = no credits → gate 3 stays fail-closed.
    /// </summary>
    [JsonPropertyName("remediationCreditsPerServer")]
    public int RemediationCreditsPerServer { get; init; }

    /// <summary>
    /// Signed corpus-DEMO allocation: how many DISTINCT SQL instances the operator may run the
    /// CORPUS audit (/audit) against within any rolling 24h window. The persisted
    /// <see cref="DemoRunLedger"/> meters claims against this, read LIVE so loading a different
    /// bundle re-licenses immediately.
    ///   • community bundle = 1 (the public demo limit)
    ///   • full bundle      = 0 → UNLIMITED (gate no-ops)
    /// Nullable for transition: a bundle built before this knob existed carries null, which the
    /// accessor treats as the community default (1) — fail-closed (a missing knob must NOT grant
    /// unlimited, or recompiling the public source would lift the limit). The (off-GitHub)
    /// CorpusEncryptor stamps an explicit value into every community/demo/full bundle from now on.
    /// This gate is TIER-AGNOSTIC by design (drives off this number, not Tier) so the same metering
    /// primitive backs future paid metered/token tiers.
    /// </summary>
    [JsonPropertyName("demoCorpusInstancesPer24h")]
    public int? DemoCorpusInstancesPer24h { get; init; }

    /// <summary>
    /// Optional absolute expiry for a time-boxed DEMO allocation, ISO 8601 UTC (trailing Z).
    /// While unset or in the future, <see cref="DemoCorpusInstancesPer24h"/> applies as signed.
    /// Once passed, the corpus-demo allocation reverts to the community default (1/24h). Lets a
    /// bumped "demo" bundle grant full breadth for a fixed trial (default 7 days from issue) then
    /// auto-revert with no app change. Inside the GCM-authenticated manifest → tamper-proof.
    /// </summary>
    [JsonPropertyName("demoExpiryUtc")]
    public string? DemoExpiryUtc { get; init; }
}
