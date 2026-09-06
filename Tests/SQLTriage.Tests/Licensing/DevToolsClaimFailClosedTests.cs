/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Text.Json;
using SQLTriage.Data.Services.Licensing;
using Xunit;

namespace SQLTriage.Tests.Licensing;

/// <summary>
/// The dev-capability claim, fail-CLOSED (Adrian's ruling 2026-08-05: no gating keyed to machine
/// or user — the licence claim is the mechanism).
///
/// What these pin, and why each one is here rather than assumed:
///   • the record default, the no-bundle branch and the manifest-null branch are THREE separate
///     places that each decided the answer independently. The 2026-06-12 transition set all three
///     to "permit"; a flip that moves only one of them leaves the surface open through the other
///     two, and no earlier test covered any of them.
///   • the WIRE shape stays nullable. Null is now a denial, not a grant — but it must still parse,
///     because every bundle minted before the claim existed carries no key at all and BundleVersion
///     is bound into the AES-GCM AAD (see SeatManifestContractTests.BundleVersion_StaysAtOne).
///
/// NOT covered here, deliberately, and named so nobody reads more into a green run: FeatureRegistrar
/// wires the gate as <c>DevBridgeActive || bundle.Features.DevToolsCapability</c>, and the DevBridge
/// half is a build-mode escape hatch with no bundle input. These tests measure the bundle half.
/// </summary>
public sealed class DevToolsClaimFailClosedTests
{
    private static ManifestFeatures Parse(string json) =>
        JsonSerializer.Deserialize<ManifestFeatures>(json)!;

    [Fact]
    public void RecordDefault_DeniesDevTools()
    {
        // The positional default every hand-built BundleFeatures inherits.
        var features = new BundleFeatures(false, false, false, Array.Empty<int>());
        Assert.False(features.DevToolsCapability);
    }

    [Fact]
    public void NoBundleAtAll_DeniesDevTools()
    {
        // "Not Activated". Before the flip this state handed a full build the whole authoring
        // surface — no bundle, no claim, every dev page reachable.
        Assert.False(new BundleAccessor().Features.DevToolsCapability);
    }

    [Fact]
    public void FullBundle_WithNoDevToolsField_DeniesDevTools()
    {
        // A bundle minted before the claim existed. This is the shape of early-era mints, which
        // would carry it had they not been stamped with an explicit false — absent now denies too.
        var accessor = new BundleAccessor();
        accessor.Replace(new BundleManifest { Features = new ManifestFeatures() }, Tier.Full);

        Assert.False(accessor.Features.DevToolsCapability);
    }

    [Fact]
    public void FullBundle_WithExplicitFalse_DeniesDevTools()
    {
        var accessor = new BundleAccessor();
        accessor.Replace(
            new BundleManifest { Features = new ManifestFeatures { DevTools = false } }, Tier.Full);

        Assert.False(accessor.Features.DevToolsCapability);
    }

    [Fact]
    public void FullBundle_WithExplicitTrue_GrantsDevTools()
    {
        // The maintainer bundle: minted with issue-license.ps1 -DevCapability. This is the ONLY
        // way the surface unlocks in a distributed build.
        var accessor = new BundleAccessor();
        accessor.Replace(
            new BundleManifest { Features = new ManifestFeatures { DevTools = true } }, Tier.Full);

        Assert.True(accessor.Features.DevToolsCapability);
    }

    [Fact]
    public void FreeBundle_WithExplicitTrue_StillGrantsTheClaim_TierIsNotTheGate()
    {
        // Stated so the reading is not inferred: the dev-tools claim is claim-only, NOT tier-bound.
        // A Free-tier bundle carrying an explicit true would grant it. No such bundle is minted
        // (build-free-bundle stamps no dev claim), and community compiles the pages out regardless —
        // but the accessor's answer is what it is, and a test that pretended otherwise would be
        // asserting a control nobody wrote.
        var accessor = new BundleAccessor();
        accessor.Replace(
            new BundleManifest { Features = new ManifestFeatures { DevTools = true } }, Tier.Free);

        Assert.True(accessor.Features.DevToolsCapability);
    }

    [Fact]
    public void DevToolsField_StaysNullableOnTheWire()
    {
        // Additive contract preserved: absent parses to null (and the accessor turns null into a
        // denial). A non-nullable bool here would deserialize absent as false too — but it would
        // also lose the ability to tell "minted without the claim" from "minted denying it", which
        // the ledger and any future re-mint audit need.
        Assert.Null(Parse("""{"ragEnabled":true}""").DevTools);
        Assert.False(Parse("""{"devTools":false}""").DevTools);
        Assert.True(Parse("""{"devTools":true}""").DevTools);
    }

    [Theory]
    [InlineData("AccessSurface.razor")]
    [InlineData("SodMatrix.razor")]
    [InlineData("OffboardingTrace.razor")]
    public void TheTwoGatedEnginePages_AskTheClaimThemselves(string markupFile)
    {
        // The gap this closes: both pages' NAV entries have read the claim since 2026-06-12
        // (NavMenu: BuildModules.DevTools && FeatureGate.IsEnabled(DevTools)) while the pages
        // themselves read nothing. A full build without the claim hid the links and still served
        // the entire privilege-graph tool to whoever typed /access-surface or /sod-matrix.
        //
        // LINT, not a guarantee, and the same instrument the RBAC banner tests carry: it proves the
        // gate CALL is written in the shipped markup, not that the branch renders. There is no
        // bUnit in this project, so a render assertion is not available at any price.
        var markup = string.Join("\n", System.Linq.Enumerable.Where(
            ReadMarkup(markupFile).Split('\n'),
            line =>
            {
                var t = line.TrimStart();
                return !t.StartsWith("@*", StringComparison.Ordinal)
                       && !t.StartsWith("//", StringComparison.Ordinal);
            }));

        Assert.Contains("IFeatureGate DevGate", markup, StringComparison.Ordinal);
        Assert.Contains("!DevGate.IsEnabled(SQLTriage.Data.Services.FeatureRegistrar.DevTools)",
            markup, StringComparison.Ordinal);
        Assert.Contains("return;", markup, StringComparison.Ordinal);
    }

    private static string ReadMarkup(string fileName)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Markup", fileName);
        Assert.True(System.IO.File.Exists(path),
            $"{fileName} is copied to the test output by SQLTriage.Tests.csproj; if this fails the " +
            "assertions on it would vacuously pass");
        return System.IO.File.ReadAllText(path);
    }
}
