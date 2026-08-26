/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services.Licensing;
using Xunit;

namespace SQLTriage.Tests.Licensing;

/// <summary>
/// INSTANCE-SEAT register. Exercises the real SQLCipher store + real HMAC chain against a temp file
/// per test (test-seam dbPath/keyPath ctor), mirroring AcceptedFindingsServiceTests.
///
/// The accounting model under test:
///   D = distinct fingerprints ever claimed; N = seats
///   seatsUsed = currently-held, capped at N by claimedUtc order
///   swapsUsed = max(0, D - N)
///   LOCKED when seatsUsed >= N AND swapsUsed >= swapsAllowed
/// </summary>
public sealed class SeatRegisterTests : IDisposable
{
    private readonly string _tempDir;
    private int _seq;

    public SeatRegisterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "seat-register-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        // Pools hold the SQLCipher file open; clear them or the directory delete races the handle.
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup */ }
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static FakeBundleAccessor Bundle(int? seats, int swaps = 2) =>
        new()
        {
            Features = new BundleFeatures(
                RagEnabled: false, SpBlitzImport: true, FullCorpus: true,
                PermittedCheckIds: Array.Empty<int>(),
                InstanceSeats: seats,
                InstanceSwapsAllowed: swaps),
        };

    private SeatRegister NewRegister(int? seats, int swaps = 2, string? file = null)
    {
        var name = file ?? "seats-" + Guid.NewGuid().ToString("N");
        return new SeatRegister(
            Bundle(seats, swaps),
            NullLogger<SeatRegister>.Instance,
            dbPath: Path.Combine(_tempDir, name + ".db"),
            keyPath: Path.Combine(_tempDir, name + ".key"));
    }

    /// <summary>A distinct instance. Each call yields a different master create_date => a new seat.</summary>
    private InstanceFingerprint Instance(string machine) =>
        new()
        {
            MachineName = machine,
            InstanceName = null,
            ServerName = machine,
            MasterCreateDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(++_seq),
        };

    // ── Absent seats => UNLIMITED (the fail-OPEN ruling) ──────────────────────

    [Fact]
    public void AbsentSeats_IsUnlimited_AndNeverBlocks()
    {
        var reg = NewRegister(seats: null);

        for (int i = 0; i < 50; i++)
        {
            var fp = Instance("SRV" + i);
            Assert.True(reg.ClaimOnProbe(fp, "SRV" + i).Allowed);
        }

        var status = reg.Status();
        Assert.True(status.Unlimited);
        Assert.False(status.Locked);
        Assert.False(reg.IsLocked);
        Assert.True(reg.CanAdmit(9999).Allowed);
    }

    [Fact]
    public void AbsentSeats_StillRecordsFingerprints_SoDataExistsWhenALicenceLands()
    {
        // The whole point of recording under an unlimited licence: when a real licence arrives, the
        // register already knows the estate.
        var reg = NewRegister(seats: null);
        reg.ClaimOnProbe(Instance("SRV1"), "SRV1");
        reg.ClaimOnProbe(Instance("SRV2"), "SRV2");

        Assert.Equal(2, reg.Status().Rows.Count);
    }

    [Fact]
    public void AbsentSeats_ReportsEverything_EvenNeverProbedInstances()
    {
        // Legacy grandfather: a name that was never probed must still report, or a legacy estate
        // would go dark on the first run after upgrade.
        var reg = NewRegister(seats: null);
        Assert.True(reg.IsSeated("NEVER-PROBED"));

        var filter = reg.Filter(new[] { "A", "B", "C" });
        Assert.Equal(3, filter.Seated.Count);
        Assert.Empty(filter.Excluded);
        Assert.Null(filter.ExclusionNotice);
    }

    // ── Claim / seat accounting ──────────────────────────────────────────────

    [Fact]
    public void ClaimOnProbe_FirstSighting_TakesASeat()
    {
        var reg = NewRegister(seats: 6);
        Assert.True(reg.ClaimOnProbe(Instance("SRV1"), "SRV1").Allowed);

        var status = reg.Status();
        Assert.Equal(1, status.SeatsUsed);
        Assert.Equal(6, status.Seats);
        Assert.Equal(0, status.SwapsUsed);
        Assert.True(reg.IsSeated("SRV1"));
    }

    [Fact]
    public void ClaimOnProbe_IsIdempotent_ReprobingCostsNothing()
    {
        var reg = NewRegister(seats: 6);
        var fp = Instance("SRV1");

        for (int i = 0; i < 5; i++)
            Assert.True(reg.ClaimOnProbe(fp, "SRV1").Allowed);

        Assert.Equal(1, reg.Status().SeatsUsed);
        Assert.Equal(0, reg.Status().SwapsUsed);
    }

    [Fact]
    public void UnprobedInstance_IsNotSeated_UnderALicence()
    {
        // No fingerprint => not seated => not reported. Never invented.
        var reg = NewRegister(seats: 6);
        Assert.False(reg.IsSeated("NEVER-PROBED"));
    }

    [Fact]
    public void SeatSurvivesRestart_ViaThePersistedNameJoin()
    {
        // Regression: the name->fingerprint join must be rebuilt from disk. If it is only in memory,
        // a licensed install reports NOTHING after a restart until every server is re-probed.
        var name = "restart-" + Guid.NewGuid().ToString("N");
        var first = new SeatRegister(Bundle(6), NullLogger<SeatRegister>.Instance,
            dbPath: Path.Combine(_tempDir, name + ".db"), keyPath: Path.Combine(_tempDir, name + ".key"));
        first.ClaimOnProbe(Instance("SRV1"), "SRV1");
        Assert.True(first.IsSeated("SRV1"));

        // Fresh instance over the same files == a process restart.
        var second = new SeatRegister(Bundle(6), NullLogger<SeatRegister>.Instance,
            dbPath: Path.Combine(_tempDir, name + ".db"), keyPath: Path.Combine(_tempDir, name + ".key"));
        Assert.True(second.IsSeated("SRV1"));
        Assert.Equal(1, second.Status().SeatsUsed);
    }

    [Fact]
    public void AliasChange_PersistsTheNewJoin_AndCostsNoSwap()
    {
        // Regression: a seated instance later reached under a NEW name (retitled connection, or a
        // switch to an FQDN) must keep resolving after a restart. If the new alias only lived in
        // memory, the seat would survive but the results would silently drop on restart.
        var name = "alias-" + Guid.NewGuid().ToString("N");
        var dbPath = Path.Combine(_tempDir, name + ".db");
        var keyPath = Path.Combine(_tempDir, name + ".key");

        var reg = new SeatRegister(Bundle(2), NullLogger<SeatRegister>.Instance, dbPath, keyPath);
        var fp = Instance("SRV1");
        reg.ClaimOnProbe(fp, "SRV1");
        reg.ClaimOnProbe(fp, "SRV1.corp.local");   // same box, new name

        Assert.True(reg.IsSeated("SRV1.corp.local"));
        Assert.Equal(1, reg.Status().SeatsUsed);   // still ONE seat
        Assert.Equal(0, reg.Status().SwapsUsed);   // a rename is not churn

        // Survives a restart under the new name.
        var restarted = new SeatRegister(Bundle(2), NullLogger<SeatRegister>.Instance, dbPath, keyPath);
        Assert.True(restarted.IsSeated("SRV1.corp.local"));
        Assert.Equal(1, restarted.Status().SeatsUsed);
    }

    [Fact]
    public void RepeatedProbes_UnderTheSameAlias_DoNotGrowTheChain()
    {
        // The alias-change append must not fire on every ordinary re-probe.
        var reg = NewRegister(seats: 2);
        var fp = Instance("SRV1");
        reg.ClaimOnProbe(fp, "SRV1");
        var head = reg.Status().ChainHead;

        for (int i = 0; i < 5; i++) reg.ClaimOnProbe(fp, "SRV1");

        Assert.Equal(head, reg.Status().ChainHead);   // chain did not move
    }

    // ── Release / swap accounting ────────────────────────────────────────────

    [Fact]
    public void Release_FreesTheSeat()
    {
        var reg = NewRegister(seats: 2);
        var fp = Instance("SRV1");
        reg.ClaimOnProbe(fp, "SRV1");
        Assert.Equal(1, reg.Status().SeatsUsed);

        Assert.True(reg.Release(fp.Hash).Allowed);
        Assert.Equal(0, reg.Status().SeatsUsed);
        Assert.False(reg.IsSeated("SRV1"));
    }

    [Fact]
    public void ReleaseThenClaimDifferent_CostsExactlyOneSwap()
    {
        // THE RULING: releasing a seated fingerprint and claiming a DIFFERENT one = 1 swap.
        var reg = NewRegister(seats: 1, swaps: 2);
        var a = Instance("SRV-A");
        reg.ClaimOnProbe(a, "SRV-A");
        Assert.Equal(0, reg.Status().SwapsUsed);

        reg.Release(a.Hash);
        Assert.True(reg.ClaimOnProbe(Instance("SRV-B"), "SRV-B").Allowed);

        Assert.Equal(1, reg.Status().SwapsUsed);
        Assert.Equal(1, reg.Status().SeatsUsed);
    }

    [Fact]
    public void ReleaseThenReclaimSAME_CostsNoSwap()
    {
        // A server that goes away and comes back is not churn: D does not grow, so no swap is spent.
        var reg = NewRegister(seats: 1, swaps: 2);
        var a = Instance("SRV-A");
        reg.ClaimOnProbe(a, "SRV-A");
        reg.Release(a.Hash);
        Assert.True(reg.ClaimOnProbe(a, "SRV-A").Allowed);

        Assert.Equal(0, reg.Status().SwapsUsed);
        Assert.Equal(1, reg.Status().SeatsUsed);
    }

    [Fact]
    public void Release_IsIdempotent()
    {
        var reg = NewRegister(seats: 2);
        var fp = Instance("SRV1");
        reg.ClaimOnProbe(fp, "SRV1");

        Assert.True(reg.Release(fp.Hash).Allowed);
        Assert.True(reg.Release(fp.Hash).Allowed);
        Assert.Equal(0, reg.Status().SeatsUsed);
    }

    [Fact]
    public void Release_UnknownFingerprint_IsRefusedHonestly()
    {
        var reg = NewRegister(seats: 2);
        var decision = reg.Release("not-a-real-fingerprint");
        Assert.False(decision.Allowed);
        Assert.NotNull(decision.Reason);
    }

    // ── Lock at exhaustion ───────────────────────────────────────────────────

    [Fact]
    public void SeatsFull_AndSwapsExhausted_Locks()
    {
        var reg = NewRegister(seats: 1, swaps: 1);
        var a = Instance("SRV-A");
        reg.ClaimOnProbe(a, "SRV-A");

        // Burn the single swap: release A, claim B.
        reg.Release(a.Hash);
        reg.ClaimOnProbe(Instance("SRV-B"), "SRV-B");

        var status = reg.Status();
        Assert.Equal(1, status.SwapsUsed);
        Assert.Equal(1, status.SeatsUsed);
        Assert.True(status.Locked);
        Assert.True(reg.IsLocked);
    }

    [Fact]
    public void LockReason_IsTheExactRuledCopy()
    {
        // The copy is Adrian-approved and must stay non-blaming, state both numbers, and name the
        // single way forward. If this changes, the change was a decision — not a refactor.
        var reg = NewRegister(seats: 1, swaps: 0);
        reg.ClaimOnProbe(Instance("SRV-A"), "SRV-A");

        var status = reg.Status();
        Assert.True(status.Locked);
        Assert.Equal(
            "1 of 1 instance seats in use, 0 swaps remaining — a new licence from sqldba is required to change instances.",
            status.LockReason);
    }

    [Fact]
    public void LockReason_ShapeMatchesTheBriefsSixOfSixExample()
    {
        var reg = NewRegister(seats: 6, swaps: 0);
        for (int i = 0; i < 6; i++) reg.ClaimOnProbe(Instance("SRV" + i), "SRV" + i);

        Assert.Equal(
            "6 of 6 instance seats in use, 0 swaps remaining — a new licence from sqldba is required to change instances.",
            reg.Status().LockReason);
    }

    [Fact]
    public void NotLocked_WhileSwapsRemain()
    {
        var reg = NewRegister(seats: 1, swaps: 2);
        reg.ClaimOnProbe(Instance("SRV-A"), "SRV-A");
        Assert.False(reg.IsLocked);   // seats full, but swaps remain
    }

    [Fact]
    public void ClaimWhenFull_IsRefused_ButRecordedHonestly()
    {
        var reg = NewRegister(seats: 1, swaps: 0);
        reg.ClaimOnProbe(Instance("SRV-A"), "SRV-A");

        var decision = reg.ClaimOnProbe(Instance("SRV-B"), "SRV-B");
        Assert.False(decision.Allowed);
        Assert.NotNull(decision.Reason);            // never a silent no-op
        Assert.False(reg.IsSeated("SRV-B"));
        // Recorded, so the operator can see exactly which instance is uncovered.
        Assert.Equal(2, reg.Status().Rows.Count);
        Assert.Contains(reg.Status().Rows, r => r.State == SeatState.OverAllocated);
    }

    [Fact]
    public void SwapExhausted_WithAFreeSeat_ExplainsTheSWAP_NotTheSeats()
    {
        // seats=1, swaps=0: release the only seat, then meet a NEW instance. A seat IS free, so the
        // refusal must talk about the swap budget. The seats-full copy ("0 of 1 instance seats in
        // use...") would be confusing at best and misleading at worst.
        var reg = NewRegister(seats: 1, swaps: 0);
        var a = Instance("SRV-A");
        reg.ClaimOnProbe(a, "SRV-A");
        reg.Release(a.Hash);
        Assert.Equal(0, reg.Status().SeatsUsed);   // a seat really is free

        var decision = reg.ClaimOnProbe(Instance("SRV-B"), "SRV-B");
        Assert.False(decision.Allowed);
        Assert.Contains("swaps on this licence have been used", decision.Reason);
        Assert.DoesNotContain("seats in use", decision.Reason);
        Assert.False(reg.IsSeated("SRV-B"));
    }

    // ── CanAdmit (the add-time guard's decision) ─────────────────────────────

    [Theory]
    [InlineData(6, 6, true)]
    [InlineData(6, 7, false)]
    [InlineData(6, 1, true)]
    public void CanAdmit_RefusesBeyondSeats(int seats, int resulting, bool allowed)
    {
        var reg = NewRegister(seats: seats);
        var decision = reg.CanAdmit(resulting);
        Assert.Equal(allowed, decision.Allowed);
        if (!allowed) Assert.NotNull(decision.Reason);
    }

    // ── Filter + the EXCLUDED signal ─────────────────────────────────────────

    [Fact]
    public void Filter_ExcludesUnseated_AndReportsThem()
    {
        var reg = NewRegister(seats: 1, swaps: 0);
        reg.ClaimOnProbe(Instance("SEATED"), "SEATED");

        var filter = reg.Filter(new[] { "SEATED", "UNSEATED-1", "UNSEATED-2" });

        Assert.Equal(new[] { "SEATED" }, filter.Seated);
        Assert.Equal(2, filter.Excluded.Count);
        Assert.False(filter.IsComplete);
        Assert.NotNull(filter.ExclusionNotice);
        Assert.Contains("2 instances excluded", filter.ExclusionNotice);
        Assert.Contains("not covered by your licence", filter.ExclusionNotice);
    }

    [Fact]
    public void Filter_SingleExclusion_UsesSingularCopy()
    {
        var reg = NewRegister(seats: 1, swaps: 0);
        reg.ClaimOnProbe(Instance("SEATED"), "SEATED");

        var notice = reg.Filter(new[] { "SEATED", "ODD-ONE" }).ExclusionNotice;
        Assert.NotNull(notice);
        Assert.StartsWith("1 instance excluded", notice);
        Assert.Contains("ODD-ONE", notice);
    }

    [Fact]
    public void Filter_NothingExcluded_YieldsNoNotice()
    {
        var reg = NewRegister(seats: 2);
        reg.ClaimOnProbe(Instance("A"), "A");
        reg.ClaimOnProbe(Instance("B"), "B");

        var filter = reg.Filter(new[] { "A", "B" });
        Assert.True(filter.IsComplete);
        Assert.Null(filter.ExclusionNotice);
    }

    // ── Legacy adoption: a licence lands on an estate already over N ─────────

    [Fact]
    public void LicenceLandsOnOversizedEstate_SeatsFirstNByClaimedUtc_AndDoesNotBrick()
    {
        // Record 5 instances under an UNLIMITED (legacy) licence...
        var name = "adopt-" + Guid.NewGuid().ToString("N");
        var dbPath = Path.Combine(_tempDir, name + ".db");
        var keyPath = Path.Combine(_tempDir, name + ".key");

        var legacy = new SeatRegister(Bundle(null), NullLogger<SeatRegister>.Instance, dbPath, keyPath);
        var fps = new List<InstanceFingerprint>();
        for (int i = 0; i < 5; i++)
        {
            var fp = Instance("SRV" + i);
            fps.Add(fp);
            legacy.ClaimOnProbe(fp, "SRV" + i);
        }
        Assert.Equal(5, legacy.Status().SeatsUsed);

        // ...then a 3-seat licence arrives over the SAME register.
        var licensed = new SeatRegister(Bundle(3), NullLogger<SeatRegister>.Instance, dbPath, keyPath);
        var status = licensed.Status();

        Assert.False(status.Unlimited);
        Assert.Equal(3, status.SeatsUsed);          // capped, not bricked
        Assert.Equal(5, status.Rows.Count);         // all 5 still recorded, honestly

        // The FIRST 3 by claimedUtc keep their seats; the rest are excluded and named.
        Assert.True(licensed.IsSeated("SRV0"));
        Assert.True(licensed.IsSeated("SRV1"));
        Assert.True(licensed.IsSeated("SRV2"));
        Assert.False(licensed.IsSeated("SRV3"));
        Assert.False(licensed.IsSeated("SRV4"));

        var filter = licensed.Filter(new[] { "SRV0", "SRV1", "SRV2", "SRV3", "SRV4" });
        Assert.Equal(2, filter.Excluded.Count);
        Assert.Contains("SRV3", filter.ExclusionNotice);
        Assert.Contains("SRV4", filter.ExclusionNotice);
    }

    [Fact]
    public void AdoptionOrder_IsDeterministicAcrossRestarts_EvenWhenClaimsShareATimestamp()
    {
        // Regression. DateTime.UtcNow has ~15ms resolution, so a burst of claims in one run share a
        // claimedUtc. Ordering by claimedUtc ALONE left the tie to be broken by Dictionary
        // iteration order, so WHICH instances kept their seats could differ between restarts —
        // an estate's reports would shuffle for no reason. The (claimedUtc, firstSeq) sort fixes it.
        var name = "determinism-" + Guid.NewGuid().ToString("N");
        var dbPath = Path.Combine(_tempDir, name + ".db");
        var keyPath = Path.Combine(_tempDir, name + ".key");

        // 10 rapid claims under an unlimited licence => many will share a claimedUtc.
        var legacy = new SeatRegister(Bundle(null), NullLogger<SeatRegister>.Instance, dbPath, keyPath);
        for (int i = 0; i < 10; i++) legacy.ClaimOnProbe(Instance("SRV" + i), "SRV" + i);

        // Re-open under a 4-seat licence repeatedly: the seated set must be identical every time.
        List<string>? firstAnswer = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var licensed = new SeatRegister(Bundle(4), NullLogger<SeatRegister>.Instance, dbPath, keyPath);
            var seated = licensed.Status().Rows
                .Where(r => r.State == SeatState.Seated)
                .Select(r => r.Alias)
                .OrderBy(a => a, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(4, seated.Count);
            if (firstAnswer == null) firstAnswer = seated;
            else Assert.Equal(firstAnswer, seated);
        }

        // And it is the FIRST four claimed, not an arbitrary four.
        Assert.Equal(new[] { "SRV0", "SRV1", "SRV2", "SRV3" }, firstAnswer);
    }

    [Fact]
    public void OverAllocated_BecomesSeated_WhenASeatFrees()
    {
        var name = "free-" + Guid.NewGuid().ToString("N");
        var dbPath = Path.Combine(_tempDir, name + ".db");
        var keyPath = Path.Combine(_tempDir, name + ".key");

        var legacy = new SeatRegister(Bundle(null), NullLogger<SeatRegister>.Instance, dbPath, keyPath);
        var first = Instance("SRV0");
        legacy.ClaimOnProbe(first, "SRV0");
        legacy.ClaimOnProbe(Instance("SRV1"), "SRV1");

        var licensed = new SeatRegister(Bundle(1), NullLogger<SeatRegister>.Instance, dbPath, keyPath);
        Assert.True(licensed.IsSeated("SRV0"));
        Assert.False(licensed.IsSeated("SRV1"));   // over-allocated

        // Retire SRV0 -> SRV1 inherits the seat with no operator action.
        licensed.Release(first.Hash);
        Assert.True(licensed.IsSeated("SRV1"));
    }

    // ── Split-string counting (one profile, six servers = six seats) ─────────

    [Fact]
    public void SplitStringCounting_OneProfileWithSixServers_IsSixInstances()
    {
        // THE TRAP: seats are per real instance, not per connection profile. A single profile that
        // lists six servers is six seats — otherwise a 6-seat licence buys 6 profiles x 50 servers.
        var conn = new ServerConnection { ServerNames = "SRV1\nSRV2\nSRV3,SRV4;SRV5\rSRV6" };
        Assert.Equal(6, conn.GetServerList().Count);
        Assert.Equal(6, conn.GetServerCount());
    }

    [Fact]
    public void AddConnection_CountsSplitStrings_NotProfiles()
    {
        var reg = NewRegister(seats: 6);
        var mgr = NewManager(reg);

        // One profile carrying 6 servers exactly fills a 6-seat licence.
        Assert.True(mgr.AddConnection(new ServerConnection
        {
            Id = "profile-a",
            ServerNames = "S1\nS2\nS3\nS4\nS5\nS6",
        }).Succeeded);

        // A 7th instance — in a SEPARATE profile — must be refused.
        var refused = mgr.AddConnection(new ServerConnection { Id = "profile-b", ServerNames = "S7" });
        Assert.False(refused.Succeeded);
        Assert.NotNull(refused.Reason);
    }

    [Fact]
    public void AddConnection_RefusesAProfileThatAloneExceedsSeats()
    {
        var reg = NewRegister(seats: 2);
        var mgr = NewManager(reg);

        var refused = mgr.AddConnection(new ServerConnection { Id = "big", ServerNames = "A\nB\nC" });
        Assert.False(refused.Succeeded);
        Assert.Contains("3 instances", refused.Reason);
    }

    [Fact]
    public void UpdateConnection_CannotSneakExtraServersPastTheGuard()
    {
        // An UPDATE that appends server strings to an existing profile is the sneakiest way past a
        // per-profile count.
        var reg = NewRegister(seats: 2);
        var mgr = NewManager(reg);
        Assert.True(mgr.AddConnection(new ServerConnection { Id = "p", ServerNames = "A" }).Succeeded);

        var refused = mgr.UpdateConnection(new ServerConnection { Id = "p", ServerNames = "A\nB\nC" });
        Assert.False(refused.Succeeded);
        Assert.NotNull(refused.Reason);
    }

    [Fact]
    public void AddConnection_UnlimitedLicence_NeverRefuses()
    {
        var reg = NewRegister(seats: null);
        var mgr = NewManager(reg);

        Assert.True(mgr.AddConnection(new ServerConnection
        {
            Id = "huge",
            ServerNames = string.Join("\n", Enumerable.Range(0, 100).Select(i => "S" + i)),
        }).Succeeded);
    }

    /// <summary>
    /// A ServerConnectionManager over its OWN temp connections file (the connectionsFilePath test
    /// seam), so these tests never read or write the install's real Config/server-connections.json
    /// and start from a guaranteed-empty estate.
    /// </summary>
    private ServerConnectionManager NewManager(ISeatRegister seats) =>
        new(NullLogger<ServerConnectionManager>.Instance,
            seats,
            connectionsFilePath: Path.Combine(_tempDir, "conns-" + Guid.NewGuid().ToString("N") + ".json"));

    // ── Chain verification detects tampering ─────────────────────────────────

    [Fact]
    public void VerifyChain_IsIntact_OnAnUntouchedRegister()
    {
        var reg = NewRegister(seats: 6);
        reg.ClaimOnProbe(Instance("A"), "A");
        reg.ClaimOnProbe(Instance("B"), "B");
        Assert.Null(reg.VerifyChain());
    }

    [Fact]
    public void VerifyChain_IsIntact_OnAnEmptyRegister()
    {
        Assert.Null(NewRegister(seats: 6).VerifyChain());
    }

    [Fact]
    public void VerifyChain_DetectsAnEditedEntry()
    {
        var name = "tamper-" + Guid.NewGuid().ToString("N");
        var dbPath = Path.Combine(_tempDir, name + ".db");
        var keyPath = Path.Combine(_tempDir, name + ".key");
        var reg = new SeatRegister(Bundle(6), NullLogger<SeatRegister>.Instance, dbPath, keyPath);
        reg.ClaimOnProbe(Instance("A"), "A");
        reg.ClaimOnProbe(Instance("B"), "B");
        Assert.Null(reg.VerifyChain());

        // Rewrite a signed field behind the register's back.
        Tamper(dbPath, "UPDATE seat_events SET display_name = 'FORGED' WHERE seq = 1;");

        var error = reg.VerifyChain();
        Assert.NotNull(error);
        Assert.Contains("modified", error);
    }

    [Fact]
    public void VerifyChain_DetectsADeletedEntry()
    {
        // Deleting a claim is how an operator would try to free a seat without spending a swap.
        var name = "delete-" + Guid.NewGuid().ToString("N");
        var dbPath = Path.Combine(_tempDir, name + ".db");
        var keyPath = Path.Combine(_tempDir, name + ".key");
        var reg = new SeatRegister(Bundle(6), NullLogger<SeatRegister>.Instance, dbPath, keyPath);
        reg.ClaimOnProbe(Instance("A"), "A");
        reg.ClaimOnProbe(Instance("B"), "B");
        reg.ClaimOnProbe(Instance("C"), "C");

        Tamper(dbPath, "DELETE FROM seat_events WHERE seq = 2;");

        var error = reg.VerifyChain();
        Assert.NotNull(error);
        Assert.Contains("chain broken", error);
    }

    [Fact]
    public void VerifyChain_DetectsAnAppendedForgedEntry()
    {
        // Inserting a claim with a made-up signature must not verify.
        var name = "forge-" + Guid.NewGuid().ToString("N");
        var dbPath = Path.Combine(_tempDir, name + ".db");
        var keyPath = Path.Combine(_tempDir, name + ".key");
        var reg = new SeatRegister(Bundle(6), NullLogger<SeatRegister>.Instance, dbPath, keyPath);
        reg.ClaimOnProbe(Instance("A"), "A");

        Tamper(dbPath,
            "INSERT INTO seat_events (fingerprint, display_name, alias, event, event_utc, prev_sig, sig) " +
            "VALUES ('deadbeef', 'GHOST', 'GHOST', 'claim', '2020-01-01T00:00:00.0000000Z', 'bogus', 'bogus');");

        Assert.NotNull(reg.VerifyChain());
    }

    [Fact]
    public void ChainHead_MovesOnEveryMutation()
    {
        // The head is the portal's wiped-register tell, so it must actually advance.
        var reg = NewRegister(seats: 6);
        Assert.Equal(string.Empty, reg.Status().ChainHead);

        reg.ClaimOnProbe(Instance("A"), "A");
        var afterFirst = reg.Status().ChainHead;
        Assert.NotEqual(string.Empty, afterFirst);

        reg.ClaimOnProbe(Instance("B"), "B");
        Assert.NotEqual(afterFirst, reg.Status().ChainHead);
    }

    /// <summary>Opens the encrypted store directly and runs raw SQL — i.e. plays the tamperer.</summary>
    private static void Tamper(string dbPath, string sql)
    {
        using var conn = SqliteCipherHelper.OpenEncrypted(
            $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
