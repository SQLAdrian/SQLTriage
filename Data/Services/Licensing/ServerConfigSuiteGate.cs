/* In the name of God, the Merciful, the Compassionate */

#nullable enable

namespace SQLTriage.Data.Services.Licensing;

/// <summary>
/// What the licence says about the SERVER CONFIGURATION feature set — Server Config &amp; Hardening,
/// Remediation, AG Job Guard, AG Job Sync (Adrian's ruling 2026-08-05). One evaluation, one set of
/// words, so nav, page and write path can never disagree about why a DBA is looking at a lock.
/// </summary>
public enum ServerConfigSuiteState
{
    /// <summary>No bundle is unlocked. Measured as <c>IsUnlocked == false</c> — this does NOT
    /// distinguish "no bundle file present" from "a bundle that failed to decrypt"; the accessor
    /// does not expose that difference, so nothing here may claim it.</summary>
    NoLicenceUnlocked,

    /// <summary>A bundle IS unlocked and it is Free tier. Community and demo bundles both land
    /// here; the tier is all that was measured, so the words say tier and nothing else.</summary>
    FreeTier,

    /// <summary>A Full-tier customer bundle is unlocked and it does not carry the remediation
    /// claim. Absent and explicit-false are indistinguishable at this layer by design (the
    /// accessor resolves both to false, fail-closed) — so the words must not guess which.</summary>
    ClaimNotGranted,

    /// <summary>Full tier, claim granted. The set is licensed.</summary>
    Available,
}

/// <summary>
/// The runtime licence gate for the Server Configuration feature set. Same idiom as
/// <c>ExportPackGate.IsAvailable</c> (<c>IsUnlocked &amp;&amp; Tier == Full</c>) with the existing
/// <c>Features.Remediation</c> write-claim stacked on it — no new claim, no new manifest field, no
/// new tier. Both halves earn their place:
///
/// <list type="bullet">
/// <item><b>Tier == Full</b> binds the set to a customer licence, which is the ruling. It also
/// refuses the community/free bundle at runtime, behind the compile-time removal.</item>
/// <item><b>Features.Remediation</b> is what makes "not available on DEMO" true no matter HOW a
/// demo bundle is produced. A demo minted as a Free-tier bundle is refused by tier; a demo minted
/// as a time-boxed FULL bundle (the <c>encrypt</c> verb only ever emits Full, so a demo issued
/// through issue-license.ps1 IS Full tier) is refused because a demo mint passes no
/// <c>--remediation</c>. Tier alone would have let that second shape through.</item>
/// </list>
///
/// <para>Consequence, stated rather than discovered later: a Full-tier bundle minted before
/// <c>-Remediation</c> existed carries no claim and is refused. That is the fail-closed posture the
/// write claim has always had (<c>BundleAccessor</c>: <c>f.Remediation ?? false</c>) — this gate
/// does not change who passes it, it adds the tier binding. A paying customer who should have the
/// set needs a re-issue, exactly as the dev claim already required.</para>
///
/// <para>NOT in <c>.handoff/gated-routes.txt</c>, deliberately: this type is always-compiled (the
/// write path it guards ships in every profile) so listing it would fail every community publish.
/// What community removes is the four PAGES; this is the runtime layer for full builds.</para>
///
/// <para>DevBridge is NOT consulted here. The <c>--devbridge</c> unlock belongs to the callers
/// (<c>BundleBackedRemediationCapability</c>, <c>FeatureRegistrar</c>) so this stays a pure
/// function of the licence and can be tested as one.</para>
/// </summary>
public static class ServerConfigSuiteGate
{
    /// <summary>The measured licence state. Fail-closed on a null accessor.</summary>
    public static ServerConfigSuiteState Evaluate(IBundleAccessor? bundle)
    {
        if (bundle is null || !bundle.IsUnlocked) return ServerConfigSuiteState.NoLicenceUnlocked;
        if (bundle.Tier != Tier.Full) return ServerConfigSuiteState.FreeTier;
        if (!bundle.Features.Remediation) return ServerConfigSuiteState.ClaimNotGranted;
        return ServerConfigSuiteState.Available;
    }

    /// <summary>True only for <see cref="ServerConfigSuiteState.Available"/>.</summary>
    public static bool IsAvailable(IBundleAccessor? bundle) =>
        Evaluate(bundle) == ServerConfigSuiteState.Available;

    /// <summary>
    /// The refusal sentence for a state, conditioned on that state and nothing else. Every clause
    /// below is something <see cref="Evaluate"/> actually measured: no sentence mentions the build,
    /// because the build is not what was read, and none says "not enabled" where what was found was
    /// "not licensed". <see cref="ServerConfigSuiteState.Available"/> has no refusal and returns "".
    ///
    /// <para>The remedy clause names a destination that EXISTS. It first read "Settings, License",
    /// which is nothing on this app: the licence UI is the <c>Full Audit</c> tab on
    /// <c>Pages/Settings.razor</c> (its own tab label), holding the <c>Full Audit License</c> card
    /// (<c>Components/Shared/ActivateFullAuditCard.razor</c>). Sending a stuck DBA to a tab that is
    /// not there costs more than saying nothing, so the destination is pinned by a test. It is
    /// reachable wherever this sentence renders: every surface that prints it is Content-Removed
    /// from the community build, which is the only profile where that tab is absent
    /// (<c>@if (BuildModules.Premium &amp;&amp; _activeTab == "Full Audit")</c>).</para>
    /// </summary>
    public static string DescribeRefusal(ServerConfigSuiteState state) => state switch
    {
        ServerConfigSuiteState.NoLicenceUnlocked =>
            "No licence bundle is unlocked on this install, so there is no remediation claim to read. "
            + "Activate one on the Settings page, Full Audit tab.",
        ServerConfigSuiteState.FreeTier =>
            "The unlocked licence is Free tier. The server configuration and remediation features are "
            + "bound to a customer licence.",
        ServerConfigSuiteState.ClaimNotGranted =>
            "This customer licence does not carry the remediation claim these features are bound to. "
            + "If it is meant to, ask sqldba.org to re-issue it.",
        _ => "",
    };

    /// <summary>Convenience overload for a page that holds the accessor rather than the state.</summary>
    public static string DescribeRefusal(IBundleAccessor? bundle) => DescribeRefusal(Evaluate(bundle));
}
