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
    /// back). Community builds compile dev-tools out entirely — this claim is the full-build
    /// runtime layer only.
    ///
    /// FAIL-CLOSED since 2026-08-05 (Adrian's ruling: no gating keyed to machine or user —
    /// the licence claim is the mechanism). Nullable is now a WIRE shape, not a grant: null
    /// (a bundle minted before the claim existed) DENIES, exactly like Remediation. The
    /// transition fail-open this replaces is what let a client full build ship the whole
    /// authoring surface — publish-release.ps1 -Profile full does not set SQLTExcludeDevTools,
    /// so nothing else was in front of it. Consequence to hold: a maintainer bundle must be
    /// minted with --dev-capability (issue-license.ps1 -DevCapability) or the dev tools stay
    /// locked; --devbridge remains the dev-build unlock in the meantime.
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

    /// <summary>
    /// Absolute LICENSE expiry (whole-tier), ISO 8601 UTC (trailing Z). null = perpetual.
    /// Legacy bundles predate this field and MUST be treated as perpetual (fail-OPEN) — treating
    /// null as "expired" would instantly brick every existing paying customer. Once passed, the
    /// Full tier gracefully reverts to Free with a renew prompt (see LicenseService.TryUnlockFull).
    /// Read LIVE (like the demo ledger) so a clock change needs no restart. Inside the
    /// GCM-authenticated manifest → tamper-proof (no separate signature needed). Mirrors
    /// <see cref="DemoExpiryUtc"/> exactly. Set only on Full bundles; Free stays null.
    /// Clock-tamper (user winds the system clock back) is an accepted client-side-DRM ceiling.
    /// </summary>
    [JsonPropertyName("licenseExpiryUtc")]
    public string? LicenseExpiryUtc { get; init; }

    /// <summary>
    /// Opaque per-issue license identity (GUID string). Recorded for "which key leaked" forensics
    /// and a future offline CRL (Config/revoked.json shipped in updates). NOT a secret — ordinary
    /// equality is correct here (do not FixedTimeEquals an identifier). No runtime revocation check
    /// ships yet; this is forward-compat plumbing. null on legacy/free bundles.
    /// </summary>
    [JsonPropertyName("licenseId")]
    public string? LicenseId { get; init; }

    /// <summary>
    /// INSTANCE-SEAT allocation: how many distinct real SQL instances this licence covers. A seat is
    /// consumed per SPLIT SERVER STRING (a real instance), never per ServerConnection profile — one
    /// profile listing 6 servers costs 6 seats (see ServerConnection.GetServerList).
    ///
    /// ABSENT/null => UNLIMITED, fail-OPEN. This is a DELIBERATE RULING (Adrian, 2026-07-17) — do
    /// NOT "harden" it to fail-closed. Rationale: a live client is running RIGHT NOW on a
    /// bundle with no seats field; fail-closed would brick their install on next start. Precedent:
    /// <see cref="LicenseExpiryUtc"/> fails open when null/legacy for exactly the same reason. The
    /// community/Free tier is unaffected — it keeps its own separate DemoRunLedger 1-instance/24h
    /// limit, which is a different gate with a different (fail-closed) posture.
    ///
    /// NOTE the asymmetry with Remediation/HostProbe, which fail CLOSED: those are WRITE/egress
    /// capabilities where a missing knob must not grant power. Seats are a COMMERCIAL count over a
    /// read-only lane, and the downside of fail-open (an unlicensed read) is recoverable, while the
    /// downside of fail-closed (a paying client's estate goes dark) is not.
    /// </summary>
    [JsonPropertyName("instanceSeats")]
    public int? InstanceSeats { get; init; }

    /// <summary>
    /// How many seat SWAPS this licence term allows. Releasing a seated fingerprint and claiming a
    /// different one costs 1 swap; legitimate churn (a decommission + replacement) is expected, so
    /// this is deliberately non-zero. ABSENT/null => 2 (the ruled default).
    /// Once seats are full AND swaps are exhausted, the server list LOCKS until sqldba issues a new
    /// licence. Applies only when <see cref="InstanceSeats"/> is set — an unlimited licence never
    /// swaps because it never needs to release.
    /// </summary>
    [JsonPropertyName("instanceSwapsAllowed")]
    public int? InstanceSwapsAllowed { get; init; }

    /// <summary>
    /// Signed portal-intake provisioning (#79). When present, the install takes its portal client id
    /// — and, via a one-time enrolment exchange, its write-only intake credential — from the bundle
    /// instead of an operator hand-pasting both at every install.
    ///
    /// ABSENT/null = today's manual-entry behaviour, unchanged. That is the ONLY legacy contract:
    /// every bundle issued before this field existed carries null, and those installs must keep
    /// working exactly as they do now (the operator-entered client id + SAS in the local portal
    /// settings file). Absent must never mean "unprovisioned and therefore broken".
    ///
    /// The block is inside the GCM-authenticated ciphertext, so it is tamper-proof: flipping a byte
    /// of the client id or the token breaks decryption outright. The consuming lane lives under
    /// Data/Services/Portal/** (compile-removed from the community build); this type is only the
    /// wire shape, which must stay here because it is what the always-compiled deserializer parses.
    ///
    /// SECRET: <see cref="ManifestPortal.EnrolmentToken"/> is bearer material. It is safe AT REST
    /// here (the manifest lives inside the GCM-authenticated ciphertext) but MUST NOT be copied
    /// into any outbound document, log, or UI surface. See DailySummaryModels' §4.5 prohibition.
    /// </summary>
    [JsonPropertyName("portal")]
    public ManifestPortal? Portal { get; init; }
}

/// <summary>
/// Signed portal-intake provisioning carried in <see cref="ManifestFeatures.Portal"/> (#79).
/// Additive/optional — absent on every bundle issued before 2026-07-17 and on every Free bundle.
///
/// NOT a record by deliberate choice: a positional record auto-generates a <c>ToString</c> that
/// prints EVERY member, which would spill <see cref="EnrolmentToken"/> into any log line or
/// exception message that happens to interpolate the manifest. This type overrides
/// <see cref="ToString"/> to redact the token instead.
/// </summary>
public sealed class ManifestPortal
{
    /// <summary>
    /// The estate's stable portal identity and intake blob prefix. Must satisfy the loader's rule
    /// (<c>^[a-z0-9][a-z0-9-]{1,40}$</c>, the SAME rule PortalPublishRunner.IsValidClientId
    /// enforces) — the consuming lane validates it and refuses to act on a malformed value rather
    /// than materialising garbage into the local settings. Validate on read; never trust the shape.
    /// </summary>
    [JsonPropertyName("clientId")]
    public string? ClientId { get; init; }

    /// <summary>
    /// Opaque ONE-TIME token exchanged (once, at first run) for the write-only intake SAS. A SECRET:
    /// never log it, never render it, never persist it outside the bundle. The redeemed SAS — not
    /// this token — is what gets stored, and it is stored through the existing CredentialProtector
    /// path (AES-256-GCM under a DPAPI-machine key), never in plaintext. Not compared here; if a
    /// future path ever compares it, use CryptographicOperations.FixedTimeEquals (repo rule:
    /// constant-time on secrets).
    /// </summary>
    [JsonPropertyName("enrolmentToken")]
    public string? EnrolmentToken { get; init; }

    /// <summary>Redacts the one-time token — see the type remarks for why this override exists.</summary>
    public override string ToString() =>
        $"ManifestPortal {{ ClientId = {ClientId}, EnrolmentToken = {(string.IsNullOrEmpty(EnrolmentToken) ? "(none)" : "(redacted)")} }}";
}
