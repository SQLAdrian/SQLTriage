/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services.Licensing;
using Xunit;

namespace SQLTriage.Tests.Licensing;

/// <summary>
/// The MERGED manifest contract (integration of the seats lane + the portal-intake lane, #79,
/// 2026-07-17). Both lanes added fields to <see cref="ManifestFeatures"/> independently and neither
/// could compile against the other, so these tests pin the union by DECODE — parsing real JSON
/// through the always-compiled deserializer — rather than by reading the type and assuming.
///
/// Why a separate file from SeatManifestContractTests: that suite pins each field in isolation and
/// proves fail-open from an in-memory <c>new ManifestFeatures()</c>. What was never covered is the
/// union (all three fields in ONE payload) and the fail-open path driven from ACTUAL legacy JSON all
/// the way through to the register. That end-to-end is the live-client (Acme) guarantee.
/// </summary>
public sealed class SeatPortalMergedContractTests : IDisposable
{
    private readonly string _tempDir;
    private int _seq;

    public SeatPortalMergedContractTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "seat-portal-merged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup */ }
    }

    private InstanceFingerprint Instance(string machine) =>
        new()
        {
            MachineName = machine,
            InstanceName = null,
            ServerName = machine,
            MasterCreateDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(++_seq),
        };

    private SeatRegister RegisterOver(IBundleAccessor bundle)
    {
        var name = "merged-" + Guid.NewGuid().ToString("N");
        return new SeatRegister(
            bundle,
            NullLogger<SeatRegister>.Instance,
            dbPath: Path.Combine(_tempDir, name + ".db"),
            keyPath: Path.Combine(_tempDir, name + ".key"));
    }

    /// <summary>A bundle as it arrives on the wire: JSON in, decoded manifest out, into the accessor.</summary>
    private static BundleAccessor DecodeIntoAccessor(string manifestJson)
    {
        var manifest = JsonSerializer.Deserialize<BundleManifest>(manifestJson)!;
        var accessor = new BundleAccessor();
        accessor.Replace(manifest, Tier.Full);
        return accessor;
    }

    // ── The union: all three fields in ONE payload ───────────────────────────

    [Fact]
    public void MergedManifest_AllThreeNewFields_DecodeTogether()
    {
        // The exact shape the encryptor will stamp post-merge. If either lane's field silently lost
        // its JsonPropertyName in the merge, this is what catches it.
        var accessor = DecodeIntoAccessor("""
        {
          "bundleVersion": 1,
          "clientName": "Acme",
          "tier": "Full",
          "features": {
            "ragEnabled": true,
            "fullCorpus": true,
            "instanceSeats": 6,
            "instanceSwapsAllowed": 3,
            "portal": { "clientId": "acme", "enrolmentToken": "opaque-one-time-token" }
          }
        }
        """);

        Assert.Equal(6, accessor.Features.InstanceSeats);
        Assert.Equal(3, accessor.Features.InstanceSwapsAllowed);
        Assert.Equal("acme", accessor.Features.PortalClientId);
    }

    // ── The ruling: absent => unlimited, fail-OPEN, proven from real JSON ────

    [Fact]
    public void LegacyManifest_DecodedFromJson_YieldsUnlimitedSeats_FailOpen()
    {
        // Acme's live bundle: issued before any of the three fields existed. Decoding it must not
        // throw, must not lock, and must grant UNLIMITED seats (Adrian's ruling, 2026-07-17).
        var accessor = DecodeIntoAccessor("""
        {"bundleVersion":1,"clientName":"Acme","tier":"Full",
         "features":{"ragEnabled":true,"fullCorpus":true}}
        """);

        Assert.Null(accessor.Features.InstanceSeats);        // null == unlimited
        Assert.Equal(2, accessor.Features.InstanceSwapsAllowed);  // ruled default
        Assert.Null(accessor.Features.PortalClientId);       // => manual portal entry, unchanged
    }

    [Fact]
    public void LegacyManifest_DecodedFromJson_RegisterSeatsEveryInstance_AndNeverLocks()
    {
        // The end-to-end fail-open guarantee: legacy JSON -> decode -> accessor -> real register.
        // A fail-CLOSED regression here is exactly what would brick the live client's estate on its
        // next start, so this asserts the whole chain, not just the parsed field.
        var accessor = DecodeIntoAccessor("""
        {"bundleVersion":1,"clientName":"Acme","tier":"Full",
         "features":{"ragEnabled":true,"fullCorpus":true}}
        """);
        var reg = RegisterOver(accessor);

        for (int i = 0; i < 25; i++)
        {
            var decision = reg.ClaimOnProbe(Instance($"SRV-{i:D2}"), $"SRV-{i:D2}");
            Assert.True(decision.Allowed);
            Assert.False(reg.IsLocked);
        }

        var status = reg.Status();
        Assert.True(status.Unlimited);
        Assert.Equal(25, status.SeatsUsed);
        Assert.False(status.Locked);
        Assert.Null(status.LockReason);
        Assert.Null(reg.VerifyChain());   // the HMAC ledger stayed intact throughout
    }

    [Fact]
    public void LegacyManifest_UnlimitedRegister_StillUnlimitedAfterReload()
    {
        // The projection is rebuilt from the PERSISTED ledger on restart. An unlimited licence must
        // re-derive as unlimited, not collapse to a capped set.
        var accessor = DecodeIntoAccessor("""
        {"bundleVersion":1,"tier":"Full","features":{"fullCorpus":true}}
        """);
        var name = "reload-" + Guid.NewGuid().ToString("N");
        var db = Path.Combine(_tempDir, name + ".db");
        var key = Path.Combine(_tempDir, name + ".key");

        var first = new SeatRegister(accessor, NullLogger<SeatRegister>.Instance, dbPath: db, keyPath: key);
        for (int i = 0; i < 5; i++) first.ClaimOnProbe(Instance($"SRV-{i:D2}"), $"SRV-{i:D2}");
        Assert.Equal(5, first.Status().SeatsUsed);

        // Same store, fresh instance == the restart path.
        var reopened = new SeatRegister(accessor, NullLogger<SeatRegister>.Instance, dbPath: db, keyPath: key);
        Assert.True(reopened.Status().Unlimited);
        Assert.Equal(5, reopened.Status().SeatsUsed);
        Assert.False(reopened.IsLocked);
    }

    // ── The portal block is optional in BOTH directions ──────────────────────

    [Fact]
    public void SeatedManifest_WithNoPortalBlock_StillDecodes()
    {
        // A seats licence issued without portal provisioning: portal absent must mean "manual entry",
        // never "unprovisioned and therefore broken".
        var accessor = DecodeIntoAccessor("""
        {"bundleVersion":1,"tier":"Full","features":{"instanceSeats":2,"instanceSwapsAllowed":1}}
        """);

        Assert.Equal(2, accessor.Features.InstanceSeats);
        Assert.Null(accessor.Features.PortalClientId);
    }

    [Fact]
    public void PortalManifest_WithNoSeatFields_StillUnlimited()
    {
        // The mirror case: a portal-provisioned bundle predating seats keeps the fail-open grant.
        var accessor = DecodeIntoAccessor("""
        {"bundleVersion":1,"tier":"Full",
         "features":{"portal":{"clientId":"acme","enrolmentToken":"t"}}}
        """);

        Assert.Null(accessor.Features.InstanceSeats);
        Assert.Equal("acme", accessor.Features.PortalClientId);
    }

    // ── The secret must not leak through a diagnostic path ───────────────────

    [Fact]
    public void DecodedPortalBlock_ToString_RedactsTheEnrolmentToken()
    {
        // ManifestPortal is deliberately not a positional record: an auto-generated ToString would
        // print the bearer token into any log line that interpolates the manifest.
        var f = JsonSerializer.Deserialize<ManifestFeatures>(
            """{"portal":{"clientId":"acme","enrolmentToken":"super-secret-bearer"}}""")!;

        var rendered = f.Portal!.ToString();
        Assert.DoesNotContain("super-secret-bearer", rendered);
        Assert.Contains("acme", rendered);
    }
}
