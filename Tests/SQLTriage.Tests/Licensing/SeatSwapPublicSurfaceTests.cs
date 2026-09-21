/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
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
/// The seat SWAP model driven ONLY through the surfaces the app itself uses:
/// ServerConnectionManager.RemoveConnection/UpdateConnection/AddConnection + SeatRegister.ClaimOnProbe.
///
/// WHY THIS FILE EXISTS (regression, 2026-07-17): SeatRegisterTests exercises the swap model through
/// direct ISeatRegister.Release calls — and Release had NO production caller, so the whole swap
/// subsystem was unreachable in the real app while its tests passed. A seat is only ever freed by a
/// release, so without one seatsUsed never fell below N, ClaimOnProbe short-circuited every new claim
/// to OverAllocated before the swap branch, and a client who decommissioned a box was trapped: dead
/// instances holding every seat, replacements permanently unreported, swaps burned without ever
/// moving a seat.
///
/// So: NOTHING here may call Release (or ReleaseInstance) directly. Every release must be a
/// consequence of an operator action. That constraint is the point of the file — if the wiring is
/// ever removed, these tests must fail. SeatRegisterTests keeps the unit-level Release coverage.
/// </summary>
public sealed class SeatSwapPublicSurfaceTests : IDisposable
{
    private readonly string _tempDir;
    private int _seq;

    public SeatSwapPublicSurfaceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "seat-swap-public-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* test cleanup */ }
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static FakeBundleAccessor Bundle(int? seats, int swaps) =>
        new()
        {
            Features = new BundleFeatures(
                RagEnabled: false, SpBlitzImport: true, FullCorpus: true,
                PermittedCheckIds: Array.Empty<int>(),
                InstanceSeats: seats,
                InstanceSwapsAllowed: swaps),
        };

    private SeatRegister NewRegister(int? seats, int swaps)
    {
        var name = "seats-" + Guid.NewGuid().ToString("N");
        return new SeatRegister(
            Bundle(seats, swaps),
            NullLogger<SeatRegister>.Instance,
            dbPath: Path.Combine(_tempDir, name + ".db"),
            keyPath: Path.Combine(_tempDir, name + ".key"));
    }

    /// <summary>A manager backed by a temp connections file (never the install's real one).</summary>
    private ServerConnectionManager NewManager(ISeatRegister seats) =>
        new(NullLogger<ServerConnectionManager>.Instance, seats,
            connectionsFilePath: Path.Combine(_tempDir, "conns-" + Guid.NewGuid().ToString("N") + ".json"));

    /// <summary>A distinct real instance — each call is a different box (distinct master create_date).</summary>
    private InstanceFingerprint Instance(string machine) =>
        new()
        {
            MachineName = machine,
            InstanceName = null,
            ServerName = machine,
            MasterCreateDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(++_seq),
        };

    private static ServerConnection Profile(string id, params string[] servers) =>
        new() { Id = id, ServerNames = string.Join(",", servers) };

    /// <summary>What the first scan after a config change does: probe each configured server.</summary>
    private static SeatDecision Scan(ISeatRegister seats, InstanceFingerprint fp, string server) =>
        seats.ClaimOnProbe(fp, server);

    // ── The scenario the swap budget was bought for ──────────────────────────

    [Fact]
    public void DecommissionThenReplace_SpendsOneSwap_AndTheReplacementIsReported()
    {
        // Acme's licence: 6 seats, 2 swaps.
        var seats = NewRegister(seats: 6, swaps: 2);
        var mgr = NewManager(seats);

        var boxes = Enumerable.Range(1, 6).Select(i => $"SQL{i:00}").ToArray();
        foreach (var b in boxes)
            Assert.True(mgr.AddConnection(Profile(b, b)).Succeeded);
        foreach (var b in boxes)
            Assert.True(Scan(seats, Instance(b), b).Allowed);

        Assert.Equal(6, seats.Status().SeatsUsed);
        Assert.Equal(0, seats.Status().SwapsUsed);
        Assert.False(seats.IsLocked);

        // SQL03 is decommissioned. The operator deletes its profile — the ONLY release trigger.
        Assert.True(mgr.RemoveConnection("SQL03").Succeeded);
        Assert.Equal(5, seats.Status().SeatsUsed);   // the seat is actually freed
        Assert.False(seats.IsSeated("SQL03"));

        // The replacement is admitted and, on first scan, covered — spending exactly one swap.
        Assert.True(mgr.AddConnection(Profile("SQL03-NEW", "SQL03-NEW")).Succeeded);
        var decision = Scan(seats, Instance("SQL03-NEW"), "SQL03-NEW");

        Assert.True(decision.Allowed);               // reported, not silently excluded
        Assert.True(seats.IsSeated("SQL03-NEW"));
        Assert.Equal(6, seats.Status().SeatsUsed);
        Assert.Equal(1, seats.Status().SwapsUsed);   // one swap bought one instance move
        Assert.False(seats.IsLocked);

        // And the replacement is not dropped from a report.
        Assert.True(seats.Filter(new[] { "SQL03-NEW" }).IsComplete);
    }

    [Fact]
    public void SpendingTheLastSwap_LocksTheList_AndTheLockThenHoldsTheSeats()
    {
        var seats = NewRegister(seats: 2, swaps: 1);
        var mgr = NewManager(seats);

        foreach (var b in new[] { "A", "B" })
        {
            Assert.True(mgr.AddConnection(Profile(b, b)).Succeeded);
            Assert.True(Scan(seats, Instance(b), b).Allowed);
        }

        // Churn 1 — inside the budget, so it works end to end.
        Assert.True(mgr.RemoveConnection("B").Succeeded);
        Assert.True(mgr.AddConnection(Profile("C", "C")).Succeeded);
        Assert.True(Scan(seats, Instance("C"), "C").Allowed);
        Assert.Equal(1, seats.Status().SwapsUsed);
        Assert.True(seats.IsLocked);   // seats full AND the single swap is spent

        // Now the lock does its job: a seat-holder can no longer be removed, so remove-then-add
        // cannot rotate a seat onto a new box for free. This is the DESIGNED end state, and it is
        // why the release wiring is safe — it frees seats only while the licence still permits churn.
        var refused = mgr.RemoveConnection("C");
        Assert.False(refused.Succeeded);
        Assert.Contains("licence", refused.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, seats.Status().SeatsUsed);   // nothing was freed behind the refusal
        Assert.True(seats.IsSeated("C"));
    }

    [Fact]
    public void ANewInstanceBeyondTheSwapBudget_IsRefusedWithTheSwapReason_NotTheSeatsReason()
    {
        // Reaches SeatRegister's swap branch: a seat IS free but covering a NEVER-SEEN instance would
        // spend a swap the licence no longer has. Before the release wiring existed this branch was
        // unreachable in the app — seatsUsed never fell below N, so the seats-full branch always won.
        // The distinction matters: telling an operator "seats are full" while a seat plainly sits
        // empty is a lie, and the two messages send them to different remedies.
        var seats = NewRegister(seats: 2, swaps: 1);
        var mgr = NewManager(seats);

        foreach (var b in new[] { "A", "B" })
        {
            Assert.True(mgr.AddConnection(Profile(b, b)).Succeeded);
            Assert.True(Scan(seats, Instance(b), b).Allowed);
        }

        // Retire BOTH boxes while the register is still unlocked, leaving seats free and D at 2.
        Assert.True(mgr.RemoveConnection("B").Succeeded);
        Assert.True(mgr.RemoveConnection("A").Succeeded);
        Assert.Equal(0, seats.Status().SeatsUsed);
        Assert.False(seats.IsLocked);

        // First replacement: D -> 3, spends the one allowed swap. Covered.
        Assert.True(mgr.AddConnection(Profile("C", "C")).Succeeded);
        Assert.True(Scan(seats, Instance("C"), "C").Allowed);
        Assert.Equal(1, seats.Status().SwapsUsed);

        // Second replacement: a seat is free (1 of 2 used), but D would go to 4 = 2 swaps > 1 allowed.
        Assert.True(mgr.AddConnection(Profile("D", "D")).Succeeded);
        var refused = Scan(seats, Instance("D"), "D");

        Assert.False(refused.Allowed);
        Assert.Contains("swap", refused.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("seats in use", refused.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.False(seats.IsSeated("D"));
        Assert.Equal(1, seats.Status().SeatsUsed);   // the free seat stays free; D is recorded, not seated
        Assert.Null(seats.VerifyChain());
    }

    // ── The trap: a released seat must not strand the operator ───────────────

    [Fact]
    public void RemovingADeadBox_DoesNotLeaveItsSeatHeld()
    {
        // The original defect, stated as an invariant: after removing every profile that named an
        // instance, that instance holds no seat. Before the fix SeatsUsed stayed pinned at N here.
        var seats = NewRegister(seats: 1, swaps: 0);
        var mgr = NewManager(seats);

        Assert.True(mgr.AddConnection(Profile("only", "DEAD")).Succeeded);
        Assert.True(Scan(seats, Instance("DEAD"), "DEAD").Allowed);
        Assert.Equal(1, seats.Status().SeatsUsed);
        Assert.True(seats.IsLocked);                      // 1/1 seats, 0 swaps

        // A locked register still permits removing a seat-holder ONLY via... it does not. Locked +
        // holds a seat = refused, by design (remove-then-add would rotate the seat for free).
        var refused = mgr.RemoveConnection("only");
        Assert.False(refused.Succeeded);
        Assert.Equal(1, seats.Status().SeatsUsed);        // still held — the refusal is honest
    }

    [Fact]
    public void AnInstanceStillListedByAnotherProfile_KeepsItsSeat()
    {
        // Two profiles naming the same server = ONE instance = ONE seat. Removing one profile must
        // not free a seat the other still relies on.
        var seats = NewRegister(seats: 2, swaps: 2);
        var mgr = NewManager(seats);

        Assert.True(mgr.AddConnection(Profile("prod", "SHARED")).Succeeded);
        Assert.True(mgr.AddConnection(Profile("adhoc", "SHARED")).Succeeded);
        Assert.True(Scan(seats, Instance("SHARED"), "SHARED").Allowed);
        Assert.Equal(1, seats.Status().SeatsUsed);

        Assert.True(mgr.RemoveConnection("adhoc").Succeeded);

        Assert.True(seats.IsSeated("SHARED"));            // still configured by "prod"
        Assert.Equal(1, seats.Status().SeatsUsed);

        // Only when the LAST profile naming it goes does the seat free.
        Assert.True(mgr.RemoveConnection("prod").Succeeded);
        Assert.False(seats.IsSeated("SHARED"));
        Assert.Equal(0, seats.Status().SeatsUsed);
    }

    [Fact]
    public void EditingAServerOutOfAProfile_FreesItsSeat()
    {
        // Dropping a server string from a multi-server profile retires that instance just as surely
        // as deleting the profile would.
        var seats = NewRegister(seats: 2, swaps: 2);
        var mgr = NewManager(seats);

        Assert.True(mgr.AddConnection(Profile("estate", "KEEP", "DROP")).Succeeded);
        Assert.True(Scan(seats, Instance("KEEP"), "KEEP").Allowed);
        Assert.True(Scan(seats, Instance("DROP"), "DROP").Allowed);
        Assert.Equal(2, seats.Status().SeatsUsed);

        Assert.True(mgr.UpdateConnection(Profile("estate", "KEEP")).Succeeded);

        Assert.True(seats.IsSeated("KEEP"));
        Assert.False(seats.IsSeated("DROP"));
        Assert.Equal(1, seats.Status().SeatsUsed);
    }

    [Fact]
    public void RemovingANeverProbedProfile_CostsNothing()
    {
        // An unreachable server was never fingerprinted, so it holds no seat: removing it must be a
        // no-op against the register, not an error and not a spent swap.
        var seats = NewRegister(seats: 2, swaps: 2);
        var mgr = NewManager(seats);

        Assert.True(mgr.AddConnection(Profile("ghost", "UNREACHABLE")).Succeeded);
        Assert.Equal(0, seats.Status().SeatsUsed);

        Assert.True(mgr.RemoveConnection("ghost").Succeeded);

        Assert.Equal(0, seats.Status().SeatsUsed);
        Assert.Equal(0, seats.Status().SwapsUsed);
        Assert.Null(seats.VerifyChain());   // no release event was invented for it
    }

    // ── A returning server is not churn ──────────────────────────────────────

    [Fact]
    public void ReleaseThenReclaimTheSameFingerprint_ThroughTheManager_SpendsNoSwap()
    {
        // swaps: 1, not 0 — at 0 the register locks the moment seats fill (seatsUsed >= N AND
        // swapsUsed >= 0 is immediately true), so the removal would be refused and the round trip
        // could never start. A licence that permits no churn at all is a different case, covered by
        // RemovingADeadBox_DoesNotLeaveItsSeatHeld.
        var seats = NewRegister(seats: 2, swaps: 1);
        var mgr = NewManager(seats);

        var b = Instance("B");
        Assert.True(mgr.AddConnection(Profile("A", "A")).Succeeded);
        Assert.True(mgr.AddConnection(Profile("B", "B")).Succeeded);
        Assert.True(Scan(seats, Instance("A"), "A").Allowed);
        Assert.True(Scan(seats, b, "B").Allowed);

        // B goes away and comes back — same box, same fingerprint.
        Assert.True(mgr.RemoveConnection("B").Succeeded);
        Assert.Equal(1, seats.Status().SeatsUsed);

        Assert.True(mgr.AddConnection(Profile("B", "B")).Succeeded);
        Assert.True(Scan(seats, b, "B").Allowed);

        Assert.Equal(2, seats.Status().SeatsUsed);
        Assert.Equal(0, seats.Status().SwapsUsed);   // D never grew: not churn, so free
        Assert.False(seats.IsLocked);
        Assert.Null(seats.VerifyChain());            // the whole round trip is chain-intact
    }
}
