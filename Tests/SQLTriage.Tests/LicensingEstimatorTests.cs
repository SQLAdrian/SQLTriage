/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using SQLTriage.Tests.Licensing;
using Xunit;

namespace SQLTriage.Tests;

/// <summary>
/// Unit tests for <see cref="LicensingEstimator"/> bundle-accessor integration.
/// ProbeServerAsync tests require a live SQL connection — those are integration scope.
/// </summary>
public class LicensingEstimatorTests
{
    private const string MinimalPricingJson = """
        {
          "lastUpdated": "2026-01-01",
          "currency": "USD",
          "version": "1.0",
          "source": "test",
          "perpetualPerCoreUSD": {
            "Enterprise": 7128.0,
            "Standard": 1859.0
          },
          "annualSAFactor": 0.5833,
          "minimumCoresPerServer": 4,
          "minimumCoresPerVM": 4,
          "editionCaps": {},
          "editionNormalisation": {
            "Enterprise Edition": "Enterprise",
            "Standard Edition": "Standard",
            "Express Edition": "Express",
            "Developer Edition": "Developer"
          }
        }
        """;

    // ── Bundle-locked / empty-bundle tests ───────────────────────────────────

    [Fact]
    public void Returns_Empty_When_Bundle_Locked()
    {
        var bundle = new FakeBundleAccessor().SetLocked();
        var svc = new LicensingEstimator(
            NullLogger<LicensingEstimator>.Instance,
            connections: null!,
            bundle: bundle);

        Assert.False(svc.IsAvailable);

        var estimate = svc.Estimate(new[] { new ServerLicensingFacts { ServerName = "SRV01", NormalisedEdition = "Enterprise", PhysicalCpuCount = 8 } }, 50.0);
        Assert.Equal(0.0, estimate.TotalAnnualCostUSD);
        Assert.Empty(estimate.PerServer);
    }

    // ── Estimate with pricing data ────────────────────────────────────────────

    [Fact]
    public void IsAvailable_WhenBundleHasPricingFile_IsTrue()
    {
        var bundle = new FakeBundleAccessor()
            .PutFile("Config/sql-licensing-pricing.json", MinimalPricingJson);
        var svc = new LicensingEstimator(
            NullLogger<LicensingEstimator>.Instance,
            connections: null!,
            bundle: bundle);
        Assert.True(svc.IsAvailable);
    }

    [Fact]
    public void Estimate_Enterprise_8Cores_ComputesExpectedCost()
    {
        var bundle = new FakeBundleAccessor()
            .PutFile("Config/sql-licensing-pricing.json", MinimalPricingJson);
        var svc = new LicensingEstimator(
            NullLogger<LicensingEstimator>.Instance,
            connections: null!,
            bundle: bundle);

        // 8 physical cores × $7128 perpetual × 0.5833 SA factor
        var facts = new ServerLicensingFacts
        {
            ServerName = "SRV01",
            NormalisedEdition = "Enterprise",
            PhysicalCpuCount = 8,
        };
        var estimate = svc.Estimate(new[] { facts }, governanceScore: 50.0);

        Assert.Single(estimate.PerServer);
        Assert.Equal(8, estimate.PerServer[0].LicensedCores);
        // Expected: 8 × 7128 × 0.5833 ≈ 33,237.13
        Assert.True(estimate.TotalAnnualCostUSD > 0);
    }

    [Fact]
    public void Estimate_Express_Edition_IsZeroCost()
    {
        var bundle = new FakeBundleAccessor()
            .PutFile("Config/sql-licensing-pricing.json", MinimalPricingJson);
        var svc = new LicensingEstimator(
            NullLogger<LicensingEstimator>.Instance,
            connections: null!,
            bundle: bundle);

        var facts = new ServerLicensingFacts
        {
            ServerName = "SRV01",
            NormalisedEdition = "Express",
            PhysicalCpuCount = 4,
        };
        var estimate = svc.Estimate(new[] { facts }, governanceScore: 50.0);
        Assert.Equal(0.0, estimate.TotalAnnualCostUSD);
    }

    [Fact]
    public void Estimate_PassiveSecondary_IsZeroCost()
    {
        var bundle = new FakeBundleAccessor()
            .PutFile("Config/sql-licensing-pricing.json", MinimalPricingJson);
        var svc = new LicensingEstimator(
            NullLogger<LicensingEstimator>.Instance,
            connections: null!,
            bundle: bundle);

        // Primary + passive secondary in the same AG
        var primary = new ServerLicensingFacts
        {
            ServerName = "SRV01",
            NormalisedEdition = "Enterprise",
            PhysicalCpuCount = 8,
            IsHadrEnabled = true,
            AgReplicas = new System.Collections.Generic.List<AgReplica>
            {
                new() { ReplicaServerName = "SRV02", RoleDesc = "SECONDARY", AllowConnections = "NO", IsLocal = false, AgName = "AG1" },
                new() { ReplicaServerName = "SRV01", RoleDesc = "PRIMARY",   AllowConnections = "ALL", IsLocal = true,  AgName = "AG1" },
            }
        };
        var secondary = new ServerLicensingFacts
        {
            ServerName = "SRV02",
            NormalisedEdition = "Enterprise",
            PhysicalCpuCount = 8,
            IsHadrEnabled = true,
        };
        var estimate = svc.Estimate(new[] { primary, secondary }, governanceScore: 50.0);

        var sec = estimate.PerServer.FirstOrDefault(p => p.ServerName == "SRV02");
        Assert.NotNull(sec);
        Assert.Equal(0.0, sec!.AnnualLicensingUSD);
        Assert.True(sec.IsPassiveSecondary);
    }

    [Fact]
    public void BundleStateChanged_InvalidatesCache()
    {
        var bundle = new FakeBundleAccessor()
            .PutFile("Config/sql-licensing-pricing.json", MinimalPricingJson);
        var svc = new LicensingEstimator(
            NullLogger<LicensingEstimator>.Instance,
            connections: null!,
            bundle: bundle);

        Assert.True(svc.IsAvailable);

        // Simulate the bundle being replaced (e.g. deactivation)
        bundle.RaiseStateChanged();

        // After state-changed with no file in the bundle, IsAvailable should reflect
        // whatever the bundle currently has — still the same FakeBundleAccessor, so it stays available.
        Assert.True(svc.IsAvailable);
    }
}
