/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;

namespace SQLTriage.Data.Services.Licensing;

/// <summary>
/// What the licence says about a FULL-TIER-ONLY surface, once the demo shape is separated out
/// (Adrian's ruling 2026-08-05: a time-boxed demo is not a customer licence).
/// </summary>
public enum FullTierState
{
    /// <summary>No bundle is unlocked. Measured as <c>IsUnlocked == false</c> — it does NOT
    /// distinguish "no bundle file" from "a bundle that failed to decrypt"; the accessor does not
    /// expose that difference, so nothing here may claim it.</summary>
    NoLicenceUnlocked,

    /// <summary>A bundle is unlocked and it is Free tier. The community bundle lands here.</summary>
    FreeTier,

    /// <summary>Full tier, and the bundle identifies ITSELF as a time-boxed demo mint
    /// (<see cref="IBundleAccessor.IsDemo"/>). The only state whose words are owned here, because
    /// it is the only one the surfaces did not already have a sentence for.</summary>
    DemoLicence,

    /// <summary>Full tier, not a demo. A customer licence.</summary>
    Available,
}

/// <summary>
/// The ONE place the six Full-tier-only surfaces resolve "is this a customer licence?".
///
/// <para>Before 2026-08-05 each of them read <c>Tier == Full</c> alone. <c>issue-license.ps1</c>'s
/// <c>encrypt</c> verb only ever emits Full tier, so a <c>-Demo</c> mint IS Full tier — every one of
/// those six surfaces therefore opened for a 7-day demo. The six, as measured on this branch:
/// <c>ExportPackGate</c>, <c>Pages/AdvancedReporting.razor</c>, <c>Pages/Premium.razor</c>,
/// <c>Pages/Checks.razor</c>, the <c>FullTierOnly</c> dashboard descriptors in
/// <c>Components/Layout/NavMenu.razor</c>, and the premium consolidation model in
/// <c>Data/Services/Capacity/ConsolidationModelProvider.cs</c>.</para>
///
/// <para>The discriminator itself is NOT here — it is <see cref="IBundleAccessor.IsDemo"/>, defined
/// once on the accessor and read only from the bundle's own signed features. This type is the
/// composition (<c>IsUnlocked &amp;&amp; Tier == Full &amp;&amp; !IsDemo</c>) plus the sentence for the
/// state that is new.</para>
///
/// <para>Only the DEMO sentence lives here. The other refusals were already written per surface and
/// say true things about the free/unactivated states; rewording them would be a change with no
/// defect behind it. What every surface gains is a branch that says "demo", because a demo user
/// shown a generic "full edition only" lock cannot tell whether they bought the wrong thing or are
/// simply inside a trial.</para>
///
/// <para>DevBridge is NOT consulted here, matching <see cref="ServerConfigSuiteGate"/>: this stays a
/// pure function of the licence, and any caller that wants the dev hatch stacks it itself.</para>
/// </summary>
public static class FullTierGate
{
    /// <summary>The measured licence state. Fail-closed on a null accessor.</summary>
    public static FullTierState Evaluate(IBundleAccessor? bundle)
    {
        if (bundle is null || !bundle.IsUnlocked) return FullTierState.NoLicenceUnlocked;
        if (bundle.Tier != Tier.Full) return FullTierState.FreeTier;
        if (bundle.IsDemo) return FullTierState.DemoLicence;
        return FullTierState.Available;
    }

    /// <summary>True only for <see cref="FullTierState.Available"/> — Full tier and not a demo.</summary>
    public static bool IsAvailable(IBundleAccessor? bundle) =>
        Evaluate(bundle) == FullTierState.Available;

    /// <summary>
    /// The sentence for the DEMO state, naming <paramref name="surface"/>, or "" in every other
    /// state (the caller's existing copy owns those). Two clauses, both conditioned on a reading
    /// taken here and nowhere else:
    ///
    /// <list type="bullet">
    /// <item>that the bundle identifies itself as a demo mint — the reason the surface is shut;</item>
    /// <item>the demo allocation's expiry, and whether it is ahead or behind <c>UtcNow</c>, printed
    /// only when the accessor could PARSE it. <see cref="IBundleAccessor.IsDemo"/> keys off the
    /// field being PRESENT while <see cref="IBundleAccessor.Features"/> exposes it only when it
    /// parses, so an unparseable value is still a demo and simply gets no date clause rather than
    /// an invented one.</item>
    /// </list>
    /// </summary>
    public static string DescribeDemoScope(IBundleAccessor? bundle, string surface)
    {
        if (Evaluate(bundle) != FullTierState.DemoLicence) return "";

        var name = string.IsNullOrWhiteSpace(surface) ? "This feature" : surface;
        var opening =
            $"This licence bundle is a time-boxed demo mint. {name} is part of a full customer "
            + "licence and is outside the demo.";

        var expiry = bundle!.Features.DemoExpiryUtc;
        if (expiry is not { } exp) return opening;

        var when = exp.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        return DateTime.UtcNow >= exp
            ? opening + $" The demo allocation it carries expired on {when} (UTC)."
            : opening + $" The demo allocation it carries runs until {when} (UTC).";
    }
}
