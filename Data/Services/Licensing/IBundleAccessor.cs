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
    // Dev-tools authoring surface. Fail-CLOSED since 2026-08-05 (Adrian's "no gating keyed to
    // machine or user" ruling): the claim IS the mechanism, so a bundle that does not carry it
    // does not get the surface. The transition fail-open this replaced meant a client full build
    // shipped every authoring page reachable. DevBridge (--devbridge) still unlocks it on a dev
    // build. See BundleManifest.ManifestFeatures.DevTools.
    bool DevToolsCapability = false,
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
    DateTime? DemoExpiryUtc = null,
    // INSTANCE SEATS: distinct real SQL instances this licence covers, counted per SPLIT SERVER
    // STRING (not per connection profile). null = UNLIMITED — fail-OPEN by ruling (a live client
    // runs on a seat-less bundle today; fail-closed would brick them). See BundleManifest.
    int? InstanceSeats = null,
    // Seat swaps allowed per licence term. Release-then-claim-a-different-fingerprint = 1 swap.
    // Defaults to 2 when the knob is absent. Irrelevant while InstanceSeats is null (unlimited).
    int InstanceSwapsAllowed = 2,
    // Portal enrolment: the install client id from the manifest, or null for today's manual-entry
    // behaviour. The enrolment TOKEN is deliberately NOT surfaced here — it is bearer material and
    // this record is read by ~19 call sites. Enrolment reads it from the manifest directly.
    string? PortalClientId = null,
    // WHERE DemoCorpusInstancesPer24h came from. The number alone cannot distinguish a bundle
    // SIGNED at 1 (the community limit — correct) from a bundle minted before the field existed,
    // which BundleAccessor's `?? 1` also resolves to 1 (a paid Full client throttled by an
    // omission — WRONG, and it shipped). The two need different words at the block, so the
    // provenance travels with the number instead of being inferred from it.
    DemoAllocationOrigin DemoAllocationOrigin = DemoAllocationOrigin.Signed);

/// <summary>
/// Provenance of <see cref="BundleFeatures.DemoCorpusInstancesPer24h"/>. All three states can
/// present the same allowance of 1, and a DBA must be told which one they are in — telling a paid
/// Full licence holder they are on the "Community version" is the exact error that cost a client.
/// </summary>
public enum DemoAllocationOrigin
{
    /// <summary>
    /// This install is the FREE/community product: either no bundle at all (unactivated) or the
    /// bundled zero-key Free-tier bundle. 1/24h IS the community limit here — say so.
    ///
    /// Deliberately NOT "no bundle loaded": the community build DOES load a real Free-tier manifest,
    /// so a present manifest never meant "a paying client". While this state was keyed on the
    /// manifest being null, every community user fell through to <see cref="Unsigned"/> and was told
    /// their bundle "predates the corpus allocation field... ask Adrian to re-mint" — customer copy,
    /// addressed to someone with no relationship to us and nothing to re-mint.
    /// </summary>
    Community,

    /// <summary>A PAID (Full-tier) bundle IS loaded but carries no demoCorpusInstancesPer24h field,
    /// so the accessor's fail-closed `?? 1` supplied the number. This licence may well entitle more;
    /// the honest message asks for a re-mint rather than claiming a community entitlement. Reachable
    /// only from Tier.Full — a free install is <see cref="Community"/>, never this.</summary>
    Unsigned,

    /// <summary>The field was present in the signed manifest — the number is the real entitlement
    /// (0 = unlimited).</summary>
    Signed,
}

/// <summary>
/// Signed portal-intake provisioning read off the active bundle (#79) — the accessor-side view of
/// <see cref="ManifestPortal"/>. Null when the bundle carries no portal block, which means "manual
/// entry, unchanged" (see <see cref="ManifestFeatures.Portal"/> for the legacy contract).
///
/// Values are returned VERBATIM (trimmed only). Validation deliberately does NOT happen here: the
/// client-id rule belongs to the portal lane (Data/Services/Portal/**, compile-removed from the
/// community build), and licensing must not grow a second, drifting copy of it.
///
/// NOT a record — a positional record's generated ToString would print
/// <see cref="EnrolmentToken"/>; this type redacts it. Do not "simplify" it into one.
/// </summary>
public sealed class BundlePortalConfig
{
    public BundlePortalConfig(string? clientId, string? enrolmentToken)
    {
        ClientId = clientId?.Trim() ?? "";
        EnrolmentToken = enrolmentToken?.Trim() ?? "";
    }

    /// <summary>Bundle-supplied portal client id, verbatim (trimmed). "" when the bundle omits it.</summary>
    public string ClientId { get; }

    /// <summary>Bundle-supplied one-time enrolment token. A SECRET — never log or render it. "" when omitted.</summary>
    public string EnrolmentToken { get; }

    /// <summary>True when this block carries a token to redeem at all.</summary>
    public bool HasEnrolmentToken => EnrolmentToken.Length > 0;

    /// <summary>Redacts the one-time token — see the type remarks.</summary>
    public override string ToString() =>
        $"BundlePortalConfig {{ ClientId = {ClientId}, EnrolmentToken = {(HasEnrolmentToken ? "(redacted)" : "(none)")} }}";
}

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

    /// <summary>
    /// True when the unlocked bundle identifies ITSELF as a time-boxed <c>-Demo</c> mint. THE demo
    /// discriminator — defined once, here, and read by every Full-tier-only surface through
    /// <see cref="FullTierGate"/> (Adrian's ruling 2026-08-05).
    ///
    /// <para>The measurement is the bundle's own signed features and nothing else: no machine name,
    /// no user name, no file beside the bundle, no app setting. The marker is the presence of
    /// <c>demoExpiryUtc</c> in the GCM-authenticated manifest. <c>issue-license.ps1</c> emits
    /// <c>--demo-expiry</c> on the <c>-Demo</c> path and on no other path, so the field's presence
    /// is exactly the mint's own statement that this is a demo, and it cannot be edited without
    /// breaking the auth tag.</para>
    ///
    /// <para>Deliberately NOT keyed on the corpus throttle. A bundle minted before
    /// <c>demoCorpusInstancesPer24h</c> existed carries no value, and the accessor's fail-closed
    /// <c>?? 1</c> resolves it to 1 — indistinguishable, by the number alone, from a signed demo
    /// allowance. Keying the discriminator there would re-run the defect that throttled a paying
    /// client on 2026-07-18 (see <see cref="DemoAllocationOrigin"/>), this time by locking their
    /// paid surfaces instead.</para>
    ///
    /// <para>A demo bundle STAYS a demo after its allocation expires: this reads presence, not
    /// whether the date has passed. The tier reversion at licence expiry is a different mechanism
    /// (<c>licenseExpiryUtc</c>, LicenseService) and is not what this answers.</para>
    /// </summary>
    bool IsDemo { get; }

    /// <summary>Customer display name from the manifest. Null on Free tier.</summary>
    string? ClientName { get; }

    /// <summary>
    /// Opaque per-issue license id (GUID string) from the manifest. Null on Free/legacy bundles.
    /// Recorded for forensics + a future offline CRL; not currently enforced at runtime.
    /// </summary>
    string? LicenseId { get; }

    /// <summary>Feature flags from the manifest.</summary>
    BundleFeatures Features { get; }

    /// <summary>
    /// The SQLTriage build number this bundle was LICENSED against (<see cref="BundleManifest.BuildNumber"/>).
    /// 0 on Free/legacy bundles or when no bundle is loaded. This is the LICENSED build, deliberately
    /// distinct from the live app build (Config/version.json) — the checks-executed inventory must
    /// stamp the licensed number so the export reflects what the client is entitled to, not whatever
    /// build happens to be running at collect time.
    /// </summary>
    int BuildNumber { get; }

    /// <summary>
    /// Signed portal-intake provisioning (#79), or null when the bundle carries no portal block —
    /// null means "the install keeps its manual client-id/SAS entry", NOT an error.
    ///
    /// Kept OFF <see cref="BundleFeatures"/> on purpose: that is a positional record whose generated
    /// ToString prints every member, and this block carries a one-time secret. A dedicated member
    /// with a redacting type keeps the token out of anything that stringifies the feature flags.
    /// </summary>
    BundlePortalConfig? PortalConfig { get; }

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
