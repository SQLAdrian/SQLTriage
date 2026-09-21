/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;
using SQLTriage.Data.Services.Licensing;
using Xunit;

namespace SQLTriage.Tests.Licensing;

/// <summary>
/// The additive manifest contract for instance seats (2026-07-17). These fields were added WITHOUT
/// bumping BundleVersion, which is only safe because the manifest deserializes with default options
/// (no JsonUnmappedMemberHandling.Disallow) — BundleVersion is bound into the AES-GCM AAD, so a bump
/// would break every existing install. These tests pin both halves of that reasoning.
/// </summary>
public sealed class SeatManifestContractTests
{
    private static ManifestFeatures Parse(string json) =>
        JsonSerializer.Deserialize<ManifestFeatures>(json)!;

    // ── The legacy grandfather: absent => unlimited, fail-OPEN ────────────────

    [Fact]
    public void LegacyManifest_WithNoSeatsField_ParsesAsNull()
    {
        // A live client (Acme) is running on exactly this shape RIGHT NOW.
        var f = Parse("""{"ragEnabled":true,"fullCorpus":true}""");
        Assert.Null(f.InstanceSeats);
        Assert.Null(f.InstanceSwapsAllowed);
        Assert.Null(f.Portal);
    }

    [Fact]
    public void LegacyBundle_YieldsUnlimitedSeats_FailOpen()
    {
        // THE RULING: absent => UNLIMITED. Fail-closed would brick the live client on next start.
        // Do not "harden" this.
        var accessor = new BundleAccessor();
        accessor.Replace(new BundleManifest { Features = new ManifestFeatures() }, Tier.Full);

        Assert.Null(accessor.Features.InstanceSeats);
        Assert.Equal(2, accessor.Features.InstanceSwapsAllowed);   // ruled default
    }

    [Fact]
    public void NoBundleAtAll_YieldsUnlimitedSeats_FailOpen()
    {
        // "Not Activated" must not lock the server list either.
        Assert.Null(new BundleAccessor().Features.InstanceSeats);
    }

    // ── Round trip ───────────────────────────────────────────────────────────

    [Fact]
    public void SeatFields_RoundTripThroughTheManifest()
    {
        var f = Parse("""{"instanceSeats":6,"instanceSwapsAllowed":2}""");
        Assert.Equal(6, f.InstanceSeats);
        Assert.Equal(2, f.InstanceSwapsAllowed);
    }

    [Fact]
    public void PortalBlock_RoundTripsThroughTheManifest()
    {
        var f = Parse("""{"portal":{"clientId":"acme","enrolmentToken":"opaque-token"}}""");
        Assert.NotNull(f.Portal);
        Assert.Equal("acme", f.Portal!.ClientId);
        Assert.Equal("opaque-token", f.Portal.EnrolmentToken);
    }

    [Fact]
    public void PortalClientId_ReachesTheAccessor_WhenValid()
    {
        var accessor = new BundleAccessor();
        accessor.Replace(new BundleManifest
        {
            Features = new ManifestFeatures
            {
                Portal = new ManifestPortal { ClientId = "acme", EnrolmentToken = "secret" },
            },
        }, Tier.Full);

        Assert.Equal("acme", accessor.Features.PortalClientId);
    }

    [Theory]
    [InlineData("Acme")]           // uppercase
    [InlineData("-acme")]          // leading hyphen
    [InlineData("c")]              // too short (min length 2)
    [InlineData("acme_1")]         // underscore not permitted
    [InlineData("acme\n")]         // trailing newline — the Regex '$' trap; must NOT be accepted
    [InlineData("")]
    public void MalformedPortalClientId_DegradesToManualEntry(string bad)
    {
        // A bad shape can only have come from a bundle we signed, so it is our bug, not an attack.
        // Degrade to manual entry (null) rather than trust it.
        var accessor = new BundleAccessor();
        accessor.Replace(new BundleManifest
        {
            Features = new ManifestFeatures
            {
                Portal = new ManifestPortal { ClientId = bad, EnrolmentToken = "t" },
            },
        }, Tier.Full);

        Assert.Null(accessor.Features.PortalClientId);
    }

    [Fact]
    public void ValidPortalClientId_AtMaxLength_IsAccepted()
    {
        var id = "c" + new string('a', 40);   // 41 chars = the documented ceiling
        var accessor = new BundleAccessor();
        accessor.Replace(new BundleManifest
        {
            Features = new ManifestFeatures
            {
                Portal = new ManifestPortal { ClientId = id, EnrolmentToken = "t" },
            },
        }, Tier.Full);

        Assert.Equal(id, accessor.Features.PortalClientId);
    }

    // ── Nonsense values are clamped, never reinterpreted as a grant ──────────

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(6, 6)]
    public void SignedSeatCount_IsClampedNonNegative_NeverReinterpretedAsUnlimited(int signed, int expected)
    {
        // A signed 0/negative is nonsense we must not silently turn into "unlimited" — clamp to
        // "no seats", which LOCKS rather than grants. A legitimate no-seats licence omits the field.
        var accessor = new BundleAccessor();
        accessor.Replace(new BundleManifest
        {
            Features = new ManifestFeatures { InstanceSeats = signed },
        }, Tier.Full);

        Assert.Equal(expected, accessor.Features.InstanceSeats);
        Assert.NotNull(accessor.Features.InstanceSeats);   // crucially NOT null/unlimited
    }

    [Fact]
    public void NegativeSwaps_ClampToZero()
    {
        var accessor = new BundleAccessor();
        accessor.Replace(new BundleManifest
        {
            Features = new ManifestFeatures { InstanceSeats = 6, InstanceSwapsAllowed = -3 },
        }, Tier.Full);

        Assert.Equal(0, accessor.Features.InstanceSwapsAllowed);
    }

    // ── The additive-safety premise itself ──────────────────────────────────

    [Fact]
    public void UnknownFutureFields_DoNotBreakDeserialization()
    {
        // This is WHY the seat fields could be added without bumping BundleVersion. If this test
        // ever fails, someone set JsonUnmappedMemberHandling.Disallow and every additive field
        // (DevTools, Remediation, LicenseExpiryUtc, seats...) became a format break.
        var f = Parse("""{"instanceSeats":6,"somethingFromTheFuture":{"nested":true}}""");
        Assert.Equal(6, f.InstanceSeats);
    }

    [Fact]
    public void BundleVersion_StaysAtOne()
    {
        // BundleVersion is bound into the AES-GCM AAD: a bump breaks decryption on every existing
        // install. Seats are additive precisely so this stays 1.
        Assert.Equal(1, new BundleManifest().BundleVersion);
    }
}
