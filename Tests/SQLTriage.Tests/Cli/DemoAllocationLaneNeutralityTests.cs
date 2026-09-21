/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Cli;
using SQLTriage.Data.Services.Licensing;
using SQLTriage.Tests.Licensing;
using Xunit;

namespace SQLTriage.Tests.Cli;

/// <summary>
/// The corpus-demo allocation must be enforced IDENTICALLY in the headless CLI (--audit, which is
/// what runs on client production servers) and in the desktop lane. Until 2026-07-20 the CLI
/// consulted the ledger not at all, so the headline commercial limit — 1 instance/24h — was one
/// documented flag away from unlimited.
///
/// These tests exercise the REAL CLI entry point (CliAuditHost.EvaluateDemoAdmission, internal via
/// InternalsVisibleTo) rather than a re-implementation of it, so a CLI-side regression — dropping
/// the gate, mapping the refusal to exit 0, checking only the first server, paraphrasing the
/// message — fails here.
///
/// NOTE: BuildMode.DevBridgeActive is process-static and false under the test host, so the REAL
/// metering path runs (the dev unlimited escape hatch is off).
/// </summary>
public sealed class DemoAllocationLaneNeutralityTests : IDisposable
{
    private readonly List<string> _temp = new();

    private string NewLedgerPath()
    {
        var p = Path.Combine(Path.GetTempPath(), $"demo-lane-{Guid.NewGuid():N}.json");
        _temp.Add(p);
        return p;
    }

    private static FakeBundleAccessor Bundle(int allowance, DemoAllocationOrigin origin) =>
        new()
        {
            Features = new BundleFeatures(
                RagEnabled: false, SpBlitzImport: true, FullCorpus: false,
                PermittedCheckIds: Array.Empty<int>(),
                DemoCorpusInstancesPer24h: allowance,
                DemoAllocationOrigin: origin),
        };

    private DemoRunLedger Ledger(int allowance, DemoAllocationOrigin origin = DemoAllocationOrigin.Signed) =>
        new(Bundle(allowance, origin), NullLogger<DemoRunLedger>.Instance, NewLedgerPath());

    /// <summary>Runs the CLI's real gate and captures what it wrote to stderr.</summary>
    private static (int Exit, string Stderr) RunCliGate(IDemoRunLedger ledger, params string[] servers)
    {
        var stderr = new StringWriter();
        var exit = CliAuditHost.EvaluateDemoAdmission(ledger, servers, stderr);
        return (exit, stderr.ToString());
    }

    public void Dispose()
    {
        foreach (var p in _temp)
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
    }

    // ── 1. Unlimited (0) is never throttled ──────────────────────────────────

    [Fact]
    public void Unlimited_ManyInstances_NotThrottledByCli()
    {
        var ledger = Ledger(allowance: 0);
        var servers = Enumerable.Range(1, 25).Select(i => $"SQL-{i}").ToArray();

        var (exit, stderr) = RunCliGate(ledger, servers);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public void Unlimited_StaysUnlimitedAfterManyRecordedRuns()
    {
        var ledger = Ledger(allowance: 0);
        foreach (var i in Enumerable.Range(1, 50)) ledger.RecordRun($"SQL-{i}");

        var (exit, _) = RunCliGate(ledger, "SQL-BRAND-NEW");

        Assert.Equal(0, exit);
        Assert.True(ledger.TryAdmitRun(new[] { "SQL-BRAND-NEW" }).Allowed);
    }

    // ── 2. N/24h enforced at the boundary ────────────────────────────────────

    [Fact]
    public void SignedN3_ExactlyThreeDistinct_Admitted_FourthRefused()
    {
        var ledger = Ledger(allowance: 3);

        // At the boundary: 3 distinct instances is the whole allocation and must pass.
        Assert.Equal(0, RunCliGate(ledger, "SQL-A", "SQL-B", "SQL-C").Exit);

        // One over the boundary must be refused, as a whole, before anything runs.
        var (exit, stderr) = RunCliGate(ledger, "SQL-A", "SQL-B", "SQL-C", "SQL-D");
        Assert.Equal(CliAuditHost.ExitDemoAllocationRefused, exit);
        Assert.Contains("3 SQL instances per 24h", stderr);
        Assert.Contains("you asked for 4", stderr);
    }

    [Fact]
    public void SignedN3_ClaimsConsumeTheAllocation_NewInstanceRefused()
    {
        var ledger = Ledger(allowance: 3);
        ledger.RecordRun("SQL-A");
        ledger.RecordRun("SQL-B");
        ledger.RecordRun("SQL-C");

        // Re-running a CLAIMED instance stays free inside the window.
        Assert.Equal(0, RunCliGate(ledger, "SQL-B").Exit);

        // A NEW instance has no slot left.
        var (exit, stderr) = RunCliGate(ledger, "SQL-D");
        Assert.Equal(CliAuditHost.ExitDemoAllocationRefused, exit);
        Assert.Contains("used it for this 24h window", stderr);
    }

    [Fact]
    public void SignedN1_DuplicateNamesAreNotAnOverAllocationBreach()
    {
        var ledger = Ledger(allowance: 1);

        // The same instance named twice (different case) is ONE distinct instance.
        Assert.Equal(0, RunCliGate(ledger, "SQL-A", "sql-a").Exit);
    }

    [Fact]
    public void RefusalGoesToStderr_AndNothingToTheReturnedExitOfZero()
    {
        var ledger = Ledger(allowance: 1);
        ledger.RecordRun("SQL-A");

        var (exit, stderr) = RunCliGate(ledger, "SQL-B");

        Assert.NotEqual(0, exit);
        Assert.Equal(4, exit);                       // pinned: scripts branch on this number
        Assert.StartsWith("ERROR: ", stderr);        // stderr, and marked as an error line
        Assert.Contains("corpus allocation", stderr);
    }

    // ── 3. Absent-field path gets its OWN message ────────────────────────────

    [Fact]
    public void AbsentField_SaysBundlePredatesTheField_AndAsksForARemint()
    {
        // BundleAccessor's `?? 1` supplied this 1 — the licence may well entitle more.
        var ledger = Ledger(allowance: 1, origin: DemoAllocationOrigin.Unsigned);
        ledger.RecordRun("SQL-A");

        var (exit, stderr) = RunCliGate(ledger, "SQL-B");

        Assert.Equal(CliAuditHost.ExitDemoAllocationRefused, exit);
        Assert.Contains("predates", stderr);
        Assert.Contains("re-mint", stderr);
        Assert.Contains("NOT necessarily your entitlement", stderr);
    }

    [Fact]
    public void AbsentField_NeverClaimsTheOperatorIsOnTheCommunityVersion()
    {
        // The exact message that shipped and cost a client: a paid Full licence holder whose
        // bundle simply lacked the field, told they were on the community build.
        var ledger = Ledger(allowance: 1, origin: DemoAllocationOrigin.Unsigned);
        ledger.RecordRun("SQL-A");

        var (_, stderr) = RunCliGate(ledger, "SQL-B");

        Assert.DoesNotContain("Community", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SignedAllowance_NeverClaimsCommunityVersion_AtAnyN()
    {
        foreach (var n in new[] { 1, 2, 5 })
        {
            var ledger = Ledger(allowance: n, origin: DemoAllocationOrigin.Signed);
            foreach (var i in Enumerable.Range(1, n)) ledger.RecordRun($"SQL-{i}");

            var (exit, stderr) = RunCliGate(ledger, "SQL-NEW");

            Assert.Equal(CliAuditHost.ExitDemoAllocationRefused, exit);
            Assert.DoesNotContain("Community", stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Your licence allocates", stderr);
        }
    }

    [Fact]
    public void CommunityInstall_IsTheOneStateThatGetsTheFreeSoftwareAndServiceCopy()
    {
        // Renamed from ...AllowedToSayCommunityVersion when that lead was replaced: "Community
        // version:" described the product as gated software with an allocation to buy and then
        // refused to name a price. The community state is still the ONE state with its own words —
        // they are now "the software is free" plus a named service that isn't.
        //
        // The figure that used to be asserted here came out on Adrian's ruling 2026-08-20 (it
        // supersedes Ruling A of 2026-07-21); the public tree carries no currency amounts. The CLI
        // lane must track the desktop lane exactly, so this asserts the ABSENCE too.
        var ledger = Ledger(allowance: 1, origin: DemoAllocationOrigin.Community);
        ledger.RecordRun("SQL-A");

        var (_, stderr) = RunCliGate(ledger, "SQL-B");

        Assert.Contains("SQLTriage is free software and stays free.", stderr);
        Assert.Contains("for current pricing, at sqldba.org", stderr);
        // Currency token split across the concatenation: this file SHIPS in the public tree and
        // verify-public-tree.ps1's language gate scans it for a literal one.
        Assert.DoesNotContain("NZ" + "$", stderr);
    }

    [Fact]
    public void AbsentFieldAndSignedOne_DoNotShareAMessage()
    {
        // Same allowance (1), same ledger state, different provenance — a DBA must be able to tell
        // "this is the limit you bought" from "your bundle is out of date".
        var signed = Ledger(allowance: 1, origin: DemoAllocationOrigin.Signed);
        var unsigned = Ledger(allowance: 1, origin: DemoAllocationOrigin.Unsigned);
        signed.RecordRun("SQL-A");
        unsigned.RecordRun("SQL-A");

        var signedMsg = RunCliGate(signed, "SQL-B").Stderr;
        var unsignedMsg = RunCliGate(unsigned, "SQL-B").Stderr;

        Assert.NotEqual(signedMsg, unsignedMsg);
    }

    // ── 4. BundleAccessor derives the provenance from the manifest ───────────

    [Fact]
    public void BundleAccessor_AbsentManifestField_ResolvesToOne_MarkedUnsigned()
    {
        var accessor = new BundleAccessor();
        accessor.Replace(
            new BundleManifest { ClientName = "Legacy", Features = new ManifestFeatures() },
            Tier.Full);

        Assert.Equal(1, accessor.Features.DemoCorpusInstancesPer24h);
        Assert.Equal(DemoAllocationOrigin.Unsigned, accessor.Features.DemoAllocationOrigin);
    }

    [Fact]
    public void BundleAccessor_PresentManifestField_IsMarkedSigned()
    {
        var accessor = new BundleAccessor();
        accessor.Replace(
            new BundleManifest
            {
                ClientName = "Re-minted",
                Features = new ManifestFeatures { DemoCorpusInstancesPer24h = 0 },
            },
            Tier.Full);

        Assert.Equal(0, accessor.Features.DemoCorpusInstancesPer24h);
        Assert.Equal(DemoAllocationOrigin.Signed, accessor.Features.DemoAllocationOrigin);
    }

    [Fact]
    public void BundleAccessor_SignedZero_IsUnlimited_AndTheCliDoesNotThrottleIt()
    {
        // The live-client shape after the re-mint: Full bundle, field present, 0 = unlimited.
        var accessor = new BundleAccessor();
        accessor.Replace(
            new BundleManifest
            {
                ClientName = "Acme",
                Features = new ManifestFeatures { DemoCorpusInstancesPer24h = 0 },
            },
            Tier.Full);

        var ledger = new DemoRunLedger(accessor, NullLogger<DemoRunLedger>.Instance, NewLedgerPath());
        var servers = Enumerable.Range(1, 12).Select(i => $"PROD-SQL-{i}").ToArray();

        Assert.Equal(0, RunCliGate(ledger, servers).Exit);
    }

    // ── 5. The two lanes agree ───────────────────────────────────────────────

    [Fact]
    public void CliLane_AgreesWithTheSharedSeam_AcrossTheMatrix()
    {
        var cases = new (int Allowance, DemoAllocationOrigin Origin, string[] Claimed, string[] Request)[]
        {
            (0, DemoAllocationOrigin.Signed,   Array.Empty<string>(),        new[] { "A", "B", "C" }),
            (1, DemoAllocationOrigin.Signed,   Array.Empty<string>(),        new[] { "A" }),
            (1, DemoAllocationOrigin.Signed,   new[] { "A" },                new[] { "B" }),
            (1, DemoAllocationOrigin.Signed,   new[] { "A" },                new[] { "A" }),
            (1, DemoAllocationOrigin.Unsigned, new[] { "A" },                new[] { "B" }),
            (1, DemoAllocationOrigin.Community, new[] { "A" },                new[] { "B" }),
            (2, DemoAllocationOrigin.Signed,   Array.Empty<string>(),        new[] { "A", "B" }),
            (2, DemoAllocationOrigin.Signed,   Array.Empty<string>(),        new[] { "A", "B", "C" }),
            (3, DemoAllocationOrigin.Signed,   new[] { "A", "B" },           new[] { "C" }),
            (3, DemoAllocationOrigin.Signed,   new[] { "A", "B", "C" },      new[] { "D" }),
        };

        foreach (var (allowance, origin, claimed, request) in cases)
        {
            var ledger = Ledger(allowance, origin);
            foreach (var c in claimed) ledger.RecordRun(c);

            var seam = ledger.TryAdmitRun(request);
            var (exit, stderr) = RunCliGate(ledger, request);

            var label = $"allowance={allowance} origin={origin} claimed=[{string.Join(",", claimed)}] " +
                        $"request=[{string.Join(",", request)}]";

            Assert.True(seam.Allowed == (exit == 0),
                $"CLI verdict disagrees with the shared seam for {label}: seam.Allowed={seam.Allowed}, exit={exit}");

            if (seam.Allowed)
            {
                Assert.Equal(string.Empty, stderr);
            }
            else
            {
                Assert.Equal(CliAuditHost.ExitDemoAllocationRefused, exit);
                // Verbatim, not paraphrased — one account of the licence, whatever the lane.
                Assert.Contains(seam.BlockReason!, stderr);
            }
        }
    }

    [Fact]
    public void DesktopLanePerInstanceCall_AgreesWithTheSharedSeam()
    {
        // The desktop lane (Pages/QuickCheck.razor) now delegates admission wholly to TryAdmitRun,
        // but CanRun remains a public seam member. For a one-instance request the two must reach the
        // same verdict AND the same words — otherwise the same licence tells two stories.
        foreach (var (allowance, origin) in new[]
                 {
                     (1, DemoAllocationOrigin.Signed),
                     (1, DemoAllocationOrigin.Unsigned),
                     (1, DemoAllocationOrigin.Community),
                     (2, DemoAllocationOrigin.Signed),
                 })
        {
            var ledger = Ledger(allowance, origin);
            foreach (var i in Enumerable.Range(1, allowance)) ledger.RecordRun($"SQL-{i}");

            var desktop = ledger.CanRun("SQL-NEW");          // what QuickCheck.razor calls
            var seam = ledger.TryAdmitRun(new[] { "SQL-NEW" }); // what the CLI calls

            Assert.Equal(desktop.Allowed, seam.Allowed);
            Assert.Equal(desktop.BlockReason, seam.BlockReason);
        }
    }

    // ── 11. The DESKTOP BANNER copy (DescribeAllowance) ──────────────────────
    // Pages/QuickCheck.razor used to build its own banner text, every branch of which opened with
    // "Community version:". That is the claim that cost a client, and it reached a paid Full licence
    // holder on the desktop even after the refusal copy was fixed. The banner now renders
    // DescribeAllowance(); these tests hold that one string to the same rule as the refusal.

    [Fact]
    public void DescribeAllowance_SignedLicence_NeverClaimsCommunityVersion_AtAnyN()
    {
        foreach (var n in new[] { 1, 2, 5 })
        {
            var text = Ledger(allowance: n, origin: DemoAllocationOrigin.Signed).DescribeAllowance();

            Assert.DoesNotContain("Community", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Your licence allocates", text);
            Assert.Contains(n.ToString(), text);
        }
    }

    [Fact]
    public void DescribeAllowance_AbsentField_AsksForARemint_AndClaimsNoEntitlement()
    {
        var text = Ledger(allowance: 1, origin: DemoAllocationOrigin.Unsigned).DescribeAllowance();

        Assert.DoesNotContain("Community", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("re-mint", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT necessarily your entitlement", text);
    }

    [Fact]
    public void DescribeAllowance_CommunityInstall_LeadsWithTheSoftwareBeingFree()
    {
        // Was ...IsTheOneStateAllowedToSayCommunityVersion. "Community version:" told a free user
        // there was a product tier they were beneath; the true statement is that the software is
        // free and the cap is a safety control. Copy detail is pinned in CommunityThrottleCopyTests.
        var text = Ledger(allowance: 1, origin: DemoAllocationOrigin.Community).DescribeAllowance();

        Assert.StartsWith("SQLTriage is free software and stays free.", text);
        Assert.Contains("not a paywall", text);
    }

    [Fact]
    public void DescribeAllowance_Unlimited_IsEmpty_SoNoBannerIsRendered()
    {
        // The banner is suppressed on an unlimited allocation; an entitlement sentence here would
        // be rendered by any caller that forgot to check Unlimited first.
        Assert.Equal(string.Empty, Ledger(allowance: 0).DescribeAllowance());
    }

    [Fact]
    public void DescribeAllowance_IsTheSameLeadTheRefusalUses()
    {
        // The invariant that keeps banner and refusal from drifting: whatever the banner tells the
        // operator they are entitled to, the refusal must open with the same words.
        foreach (var origin in new[]
                 {
                     DemoAllocationOrigin.Signed,
                     DemoAllocationOrigin.Unsigned,
                     DemoAllocationOrigin.Community,
                 })
        {
            var ledger = Ledger(allowance: 1, origin: origin);
            ledger.RecordRun("SQL-1");                       // spend the allowance

            var banner = ledger.DescribeAllowance();
            var refusal = ledger.TryAdmitRun(new[] { "SQL-NEW" }).BlockReason;

            Assert.False(string.IsNullOrEmpty(refusal));
            Assert.StartsWith(banner, refusal);
        }
    }
}
