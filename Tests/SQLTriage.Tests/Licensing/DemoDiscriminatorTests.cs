/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services.Capacity;
using SQLTriage.Data.Services.Licensing;
using Xunit;

namespace SQLTriage.Tests.Licensing;

/// <summary>
/// THE demo discriminator (<see cref="IBundleAccessor.IsDemo"/>) and the six Full-tier-only
/// surfaces it now stands in front of (Adrian's ruling 2026-08-05).
///
/// <para><b>Why it exists.</b> <c>issue-license.ps1</c>'s <c>encrypt</c> verb emits Full tier and
/// nothing else, so a <c>-Demo</c> mint IS a Full-tier bundle. Every surface that asked
/// <c>Tier == Full</c> therefore opened for a 7-day trial: Export Pack, Advanced Reporting, the
/// Premium page's "Full Audit active" badge, the Check Catalog, the full-tier-only nav descriptors,
/// and the premium consolidation model.</para>
///
/// <para><b>What the discriminator may read.</b> Only the bundle's own signed features — the
/// presence of <c>demoExpiryUtc</c> in the GCM-authenticated manifest, which the mint writes on the
/// <c>-Demo</c> path and no other path. The corpus throttle is deliberately NOT the marker, and
/// that has a test of its own below: a bundle minted before the throttle field existed resolves to
/// 1/24h through the accessor's fail-closed <c>?? 1</c>, indistinguishable by the number alone from
/// a signed demo allowance. Keying off it would lock a paying client's paid surfaces — the mirror
/// image of the 2026-07-18 client defect.</para>
///
/// <para><b>Evidence limit, stated rather than implied.</b> There is no bUnit in this project, so
/// the four RAZOR surfaces cannot be RENDERED here. Each is covered two ways: the gate function is
/// exercised through the real <see cref="BundleAccessor"/>, and the shipped .razor is asserted to
/// resolve that gate. The second is a LINT over a fixed literal — it proves the call is written,
/// not that the branch renders — and it is the same instrument, with the same ceiling, that the
/// RBAC banner tests carry. The consolidation surface needs neither: it is a service and is
/// exercised for real. DevBridge is not exercised anywhere here: <c>MarkDevBridgeActive()</c> is a
/// one-way process-global and would poison every later test in the run.</para>
/// </summary>
public sealed class DemoDiscriminatorTests
{
    // ── fixtures: the real accessor, driven from the manifest wire shape ─────

    private static BundleAccessor Accessor(ManifestFeatures features, Tier tier = Tier.Full)
    {
        var a = new BundleAccessor();
        a.Replace(new BundleManifest { Features = features }, tier);
        return a;
    }

    /// <summary>A paid Full mint as issue-license.ps1 emits it: --demo-instances 0 (unlimited),
    /// no --demo-expiry (that flag is on the -Demo path only).</summary>
    private static BundleAccessor PaidFullBundle() =>
        Accessor(new ManifestFeatures { DemoCorpusInstancesPer24h = 0 });

    /// <summary>A -Demo mint: a positive instance allowance and the demo expiry the flag writes.</summary>
    private static BundleAccessor DemoBundle(DateTime? expiryUtc = null) =>
        Accessor(new ManifestFeatures
        {
            DemoCorpusInstancesPer24h = 15,
            DemoExpiryUtc = (expiryUtc ?? DateTime.UtcNow.AddDays(7)).ToString("yyyy-MM-ddTHH:mm:ssZ"),
        });

    // ── the discriminator ────────────────────────────────────────────────────

    [Fact]
    public void PaidFullMint_IsNotADemo()
    {
        var bundle = PaidFullBundle();

        Assert.False(bundle.IsDemo);
        Assert.Equal(FullTierState.Available, FullTierGate.Evaluate(bundle));
    }

    [Fact]
    public void DemoMint_IsADemo()
    {
        var bundle = DemoBundle();

        Assert.True(bundle.IsDemo);
        Assert.Equal(Tier.Full, bundle.Tier);   // the whole point: it IS Full tier
        Assert.Equal(FullTierState.DemoLicence, FullTierGate.Evaluate(bundle));
    }

    [Fact]
    public void ExpiredDemo_IsStillADemo()
    {
        // The discriminator reads PRESENCE, not whether the date has passed. A demo whose window
        // closed is not thereby promoted to a customer licence — that would hand every expired
        // trial the six surfaces it was refused while it was live.
        var bundle = DemoBundle(DateTime.UtcNow.AddDays(-30));

        Assert.True(bundle.IsDemo);
        Assert.Equal(FullTierState.DemoLicence, FullTierGate.Evaluate(bundle));
    }

    [Fact]
    public void UnparseableDemoExpiry_IsStillADemo_AndPrintsNoDate()
    {
        // The two readings of the same field differ ON PURPOSE and this pins the difference:
        // Features.DemoExpiryUtc drops an unparseable value (never downgrade a paid allocation),
        // while IsDemo counts the field's presence (never open six paid surfaces). Only the real
        // accessor can be in this state — the fake reads the parsed projection.
        var bundle = Accessor(new ManifestFeatures { DemoExpiryUtc = "not-a-date" });

        Assert.True(bundle.IsDemo);
        Assert.Null(bundle.Features.DemoExpiryUtc);

        var words = FullTierGate.DescribeDemoScope(bundle, "Advanced Reporting");
        Assert.Contains("demo mint", words);
        Assert.DoesNotContain("expired on", words);
        Assert.DoesNotContain("runs until", words);
    }

    [Fact]
    public void LegacyFullBundle_ThrottledToOneByTheFailClosedDefault_IsNotADemo()
    {
        // The shape that cost a client on 2026-07-18: a paid Full bundle minted before
        // demoCorpusInstancesPer24h existed. The accessor's `?? 1` gives it the community NUMBER,
        // which is why the number can never be the discriminator.
        var bundle = Accessor(new ManifestFeatures());

        Assert.Equal(1, bundle.Features.DemoCorpusInstancesPer24h);
        Assert.Equal(DemoAllocationOrigin.Unsigned, bundle.Features.DemoAllocationOrigin);
        Assert.False(bundle.IsDemo);
        Assert.Equal(FullTierState.Available, FullTierGate.Evaluate(bundle));
    }

    [Fact]
    public void NoBundle_AndFreeBundle_AreNotDemos()
    {
        var none = new BundleAccessor();
        Assert.False(none.IsDemo);
        Assert.Equal(FullTierState.NoLicenceUnlocked, FullTierGate.Evaluate(none));

        var free = Accessor(new ManifestFeatures(), Tier.Free);
        Assert.False(free.IsDemo);
        Assert.Equal(FullTierState.FreeTier, FullTierGate.Evaluate(free));

        // Fail-closed on null, mirroring the other gates.
        Assert.Equal(FullTierState.NoLicenceUnlocked, FullTierGate.Evaluate(null));
        Assert.False(FullTierGate.IsAvailable(null));
    }

    // ── the words beside the verdict ─────────────────────────────────────────

    [Fact]
    public void DemoScopeSentence_IsPrintedOnlyInTheDemoState_AndNamesTheSurface()
    {
        Assert.Equal("", FullTierGate.DescribeDemoScope(PaidFullBundle(), "Advanced Reporting"));
        Assert.Equal("", FullTierGate.DescribeDemoScope(new BundleAccessor(), "Advanced Reporting"));
        Assert.Equal("", FullTierGate.DescribeDemoScope(null, "Advanced Reporting"));

        var words = FullTierGate.DescribeDemoScope(DemoBundle(), "Advanced Reporting");
        Assert.Contains("Advanced Reporting", words);
        Assert.Contains("demo", words, StringComparison.OrdinalIgnoreCase);
        // The refusal is a licence reading. It must not assert anything about the BUILD, which is
        // the over-claim this whole lane keeps re-finding.
        Assert.DoesNotContain("build", words, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DemoScopeSentence_TensesTheExpiryAgainstNow()
    {
        Assert.Contains("runs until", FullTierGate.DescribeDemoScope(
            DemoBundle(DateTime.UtcNow.AddDays(7)), "The Check Catalog"));

        Assert.Contains("expired on", FullTierGate.DescribeDemoScope(
            DemoBundle(DateTime.UtcNow.AddDays(-7)), "The Check Catalog"));
    }

    // ── surface 6: the consolidation premium model (a real behaviour flip) ────

    [Fact]
    public void ConsolidationModel_IsReadForAPaidBundle_AndNotForADemo()
    {
        // The premium model is packed by TIER, so a demo bundle carries Config/consolidation-model.json
        // exactly as a paid bundle does. Both bundles below carry it; only one is allowed to read it.
        //
        // IsLicensed (= "came from the bundle") is the assertion rather than IsUnlocked, because the
        // provider's DEBUG-only dev fallback can also supply a model on a developer box and would
        // make IsUnlocked say nothing about the licence.
        const string Model = "{\"schemaVersion\":1,\"modelName\":\"test\"}";

        var paid = new BundleAccessor();
        paid.Replace(
            new BundleManifest
            {
                Features = new ManifestFeatures { DemoCorpusInstancesPer24h = 0 },
                Files = new Dictionary<string, string> { [ConsolidationModelProvider.BundlePath] = Model },
            },
            Tier.Full);

        var demo = new BundleAccessor();
        demo.Replace(
            new BundleManifest
            {
                Features = new ManifestFeatures
                {
                    DemoCorpusInstancesPer24h = 15,
                    DemoExpiryUtc = DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                },
                Files = new Dictionary<string, string> { [ConsolidationModelProvider.BundlePath] = Model },
            },
            Tier.Full);

        var paidProvider = new ConsolidationModelProvider(
            NullLogger<ConsolidationModelProvider>.Instance, paid);
        var demoProvider = new ConsolidationModelProvider(
            NullLogger<ConsolidationModelProvider>.Instance, demo);

        Assert.True(paidProvider.IsLicensed);       // positive control: the read works at all
        Assert.NotNull(paidProvider.Current);
        Assert.False(demoProvider.IsLicensed);      // the flip: same file, refused
    }

    // ── surfaces 2-5: the gate flips, plus a lint that each surface asks it ───

    [Fact]
    public void EveryFullTierSurface_RefusesADemo_AndAdmitsAPaidBundle()
    {
        // One assertion for all four razor surfaces, because after this commit they resolve the
        // SAME function — which is the property worth pinning. Which file calls it is the lint
        // below; that this is what they get when they call it is here.
        Assert.False(FullTierGate.IsAvailable(DemoBundle()));
        Assert.True(FullTierGate.IsAvailable(PaidFullBundle()));
    }

    [Theory]
    // surface: Advanced Reporting (page-level upsell branch)
    [InlineData("AdvancedReporting.razor", "FullTierGate.Evaluate(Bundle)")]
    [InlineData("AdvancedReporting.razor", "FullTierGate.IsAvailable(Bundle)")]
    [InlineData("AdvancedReporting.razor", "FullTierGate.DescribeDemoScope(Bundle,")]
    // surface: Premium (the "Full Audit active" badge)
    [InlineData("Premium.razor", "FullTierGate.IsAvailable(Bundle)")]
    [InlineData("Premium.razor", "FullTierGate.DescribeDemoScope(Bundle,")]
    // surface: Check Catalog
    [InlineData("Checks.razor", "FullTierGate.IsAvailable(Bundle)")]
    [InlineData("Checks.razor", "FullTierGate.DescribeDemoScope(Bundle,")]
    // surface: the FullTierOnly nav descriptors
    [InlineData("NavMenu.razor", "d.FullTierOnly || SQLTriage.Data.Services.Licensing.FullTierGate.IsAvailable(Bundle)")]
    // surface: consolidation — the page's words (the refusal itself is in the provider, tested above)
    [InlineData("CapacityConsolidation.razor", "FullTierGate.DescribeDemoScope(Bundle,")]
    public void EachSurfaceResolvesTheSharedGate(string markupFile, string expected)
    {
        // LINT, not a guarantee: it proves the call is WRITTEN in the shipped markup, not that the
        // branch renders. A rewrite that keeps the literal and drops the branch would pass. It is
        // still worth having — the defect this closes was six surfaces each holding their own copy
        // of `Tier == Full`, and a copy is exactly what this notices.
        var markup = ReadMarkup(markupFile);

        Assert.Contains(expected, StripCommentLines(markup), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AdvancedReporting.razor")]
    [InlineData("Premium.razor")]
    [InlineData("Checks.razor")]
    public void NoSurfaceStillReadsTheBareTier(string markupFile)
    {
        // The rule that was replaced. If it comes back beside the new call, the surface opens for a
        // demo again and every assertion above still passes.
        var code = StripCommentLines(ReadMarkup(markupFile));

        Assert.DoesNotContain("Bundle.Tier == Tier.Full", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Bundle.Tier != Tier.Full", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Bundle.Tier == Tier.Free", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Bundle.Tier != Tier.Free", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Bundle.Tier == SQLTriage.Data.Services.Licensing.Tier.Full", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Bundle.Tier != SQLTriage.Data.Services.Licensing.Tier.Full", code, StringComparison.Ordinal);
    }

    // ── markup helpers (same idiom as DemoAllocationGateFixesTests) ───────────

    private static string ReadMarkup(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Markup", fileName);
        Assert.True(File.Exists(path),
            $"{fileName} is copied to the test output by SQLTriage.Tests.csproj; if this fails the " +
            "assertions on it would vacuously pass");
        return File.ReadAllText(path);
    }

    private static string StripCommentLines(string source) =>
        string.Join("\n", source
            .Split('\n')
            .Where(line =>
            {
                var t = line.TrimStart();
                return !t.StartsWith("//", StringComparison.Ordinal)
                       && !t.StartsWith("@*", StringComparison.Ordinal)
                       && !t.StartsWith("*", StringComparison.Ordinal);
            }));
}
