/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services.Licensing;
using Xunit;

namespace SQLTriage.Tests.Licensing;

/// <summary>
/// Tests for the corpus-DEMO gate (DemoRunLedger). The ledger meters /audit corpus runs against
/// the signed per-bundle allowance (BundleFeatures.DemoCorpusInstancesPer24h):
///   community N=1 → one distinct instance / 24h; full N=0 → unlimited.
///
/// Each test uses a unique temp ledger file (test-seam ctor pathOverride) and cleans up via
/// IDisposable, so they are parallel-safe and never touch the install dir.
///
/// NOTE: BuildMode.DevBridgeActive is process-static and false under the test host, so these
/// tests exercise the REAL metering path (the dev escape hatch is off).
/// </summary>
public sealed class DemoRunLedgerTests : IDisposable
{
    private readonly List<string> _temp = new();

    private string NewLedgerPath()
    {
        var p = Path.Combine(Path.GetTempPath(), $"demo-ledger-{Guid.NewGuid():N}.json");
        _temp.Add(p);
        return p;
    }

    private static FakeBundleAccessor Bundle(int allowance, DateTime? demoExpiryUtc = null) =>
        new()
        {
            Features = new BundleFeatures(
                RagEnabled: false, SpBlitzImport: true, FullCorpus: false,
                PermittedCheckIds: Array.Empty<int>(),
                DemoCorpusInstancesPer24h: allowance,
                DemoExpiryUtc: demoExpiryUtc),
        };

    private DemoRunLedger Ledger(int allowance, string? path = null) =>
        new(Bundle(allowance), NullLogger<DemoRunLedger>.Instance, path ?? NewLedgerPath());

    public void Dispose()
    {
        foreach (var p in _temp)
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
    }

    // ── Community N=1 ────────────────────────────────────────────────────────

    [Fact]
    public void CommunityN1_FirstInstance_IsAllowed()
    {
        var ledger = Ledger(1);
        Assert.True(ledger.CanRun("SQL-A").Allowed);
    }

    [Fact]
    public void CommunityN1_SameInstance_IsFreeAfterClaim()
    {
        var ledger = Ledger(1);
        ledger.RecordRun("SQL-A");
        Assert.True(ledger.CanRun("SQL-A").Allowed, "re-running the claimed instance must stay free");
    }

    [Fact]
    public void CommunityN1_DifferentInstance_IsBlockedAfterClaim()
    {
        var ledger = Ledger(1);
        ledger.RecordRun("SQL-A");
        var d = ledger.CanRun("SQL-B");
        Assert.False(d.Allowed);
        Assert.False(string.IsNullOrWhiteSpace(d.BlockReason));
        Assert.NotNull(d.UnlocksUtc); // tells the operator when a slot frees
    }

    [Fact]
    public void CommunityN1_InstanceNameIsCaseInsensitive()
    {
        var ledger = Ledger(1);
        ledger.RecordRun("SQL-A");
        Assert.True(ledger.CanRun("sql-a").Allowed, "same instance, different case, must be free");
        Assert.False(ledger.CanRun("SQL-B").Allowed);
    }

    // ── Demo bundle N>1 ──────────────────────────────────────────────────────

    [Fact]
    public void DemoN3_AllowsThreeDistinct_BlocksFourth()
    {
        var ledger = Ledger(3);
        ledger.RecordRun("SQL-A");
        ledger.RecordRun("SQL-B");
        ledger.RecordRun("SQL-C");
        Assert.True(ledger.CanRun("SQL-A").Allowed);   // already claimed
        Assert.False(ledger.CanRun("SQL-D").Allowed);  // 4th distinct over allowance
    }

    [Fact]
    public void DemoN3_Status_ReportsUsageAndRemaining()
    {
        var ledger = Ledger(3);
        ledger.RecordRun("SQL-A");
        var s = ledger.Status();
        Assert.False(s.Unlimited);
        Assert.Equal(3, s.Allowance);
        Assert.Equal(1, s.InstancesUsed);
        Assert.True(s.Allowed); // slots remain
    }

    // ── Full bundle N=0 → unlimited ──────────────────────────────────────────

    [Fact]
    public void FullN0_IsUnlimited_NeverBlocks_NeverSpends()
    {
        var ledger = Ledger(0);
        for (int i = 0; i < 10; i++)
            ledger.RecordRun($"SQL-{i}");
        Assert.True(ledger.CanRun("SQL-NEW").Allowed);
        var s = ledger.Status();
        Assert.True(s.Unlimited);
        Assert.Equal(0, s.InstancesUsed); // unlimited path doesn't persist claims
    }

    // ── 24h rolling window expiry ────────────────────────────────────────────

    [Fact]
    public void Claim_OlderThan24h_Expires_FreesSlot()
    {
        var path = NewLedgerPath();
        // Seed a ledger file with a 25h-old claim, then load it fresh (Load prunes on construction).
        SeedLedger(path, ("SQL-OLD", DateTime.UtcNow - TimeSpan.FromHours(25)));
        var ledger = Ledger(1, path);

        // The expired claim should have been pruned → a brand-new instance is allowed again.
        Assert.True(ledger.CanRun("SQL-NEW").Allowed, "a >24h-old claim must not keep blocking new instances");
        Assert.Equal(0, ledger.Status().InstancesUsed);
    }

    [Fact]
    public void Claim_Within24h_StillBlocks()
    {
        var path = NewLedgerPath();
        SeedLedger(path, ("SQL-RECENT", DateTime.UtcNow - TimeSpan.FromHours(1)));
        var ledger = Ledger(1, path);
        Assert.False(ledger.CanRun("SQL-OTHER").Allowed, "a 1h-old claim still occupies the only slot");
    }

    // ── DemoExpiryUtc reverts a bumped allocation to community (1) ────────────

    [Fact]
    public void DemoExpiry_Passed_RevertsAllocationToCommunity()
    {
        // The accessor itself clamps a bumped N back to 1 once DemoExpiryUtc has passed; verify the
        // ledger honours the clamped figure it reads live.
        var bundle = new FakeBundleAccessor
        {
            Features = ClampedExpiredDemoFeatures(bumpedN: 5, expiredHoursAgo: 2),
        };
        var ledger = new DemoRunLedger(bundle, NullLogger<DemoRunLedger>.Instance, NewLedgerPath());
        ledger.RecordRun("SQL-A");
        Assert.False(ledger.CanRun("SQL-B").Allowed, "after demo expiry, allowance is back to 1 instance/24h");
    }

    // The production clamp lives in BundleAccessor.Features; for the FAKE accessor we emulate the
    // post-clamp result (allowance already reduced to 1) so the ledger test stays focused on metering.
    private static BundleFeatures ClampedExpiredDemoFeatures(int bumpedN, int expiredHoursAgo) =>
        new(RagEnabled: false, SpBlitzImport: true, FullCorpus: false,
            PermittedCheckIds: Array.Empty<int>(),
            DemoCorpusInstancesPer24h: 1, // expired → clamped to community
            DemoExpiryUtc: DateTime.UtcNow - TimeSpan.FromHours(expiredHoursAgo));

    // ── Persistence round-trip ───────────────────────────────────────────────

    [Fact]
    public void Claim_Persists_AcrossLedgerInstances()
    {
        var path = NewLedgerPath();
        var l1 = Ledger(1, path);
        l1.RecordRun("SQL-A");

        var l2 = Ledger(1, path); // fresh instance reads the persisted claim
        Assert.True(l2.CanRun("SQL-A").Allowed);
        Assert.False(l2.CanRun("SQL-B").Allowed);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static void SeedLedger(string path, params (string Instance, DateTime FirstRunUtc)[] claims)
    {
        var claimsMap = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in claims) claimsMap[c.Instance] = c.FirstRunUtc;
        var payload = new
        {
            schemaVersion = 1,
            lastUpdatedUtc = DateTime.UtcNow,
            claims = claimsMap,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(payload));
    }
}
