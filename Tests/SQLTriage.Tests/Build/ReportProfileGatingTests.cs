/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests.Build;

/// <summary>
/// Per-report community gating (Adrian's ruling 2026-07-21). Every generated report/PDF is one key
/// under "reports" in buildprofile.json; a report ships in a community build ONLY when its value is
/// EXACTLY "on". FAIL-CLOSED: an unlisted/"off"/garbled key is gated. Community KEEPS exactly Audit
/// Evidence, Risk Register, Risk Acknowledgement — "three keepers only" (Adrian's follow-up ruling
/// 2026-07-21 gated the compliance-map Export Evidence report, which was briefly a fourth keeper).
///
/// These tests pin the RULE (BuildProfileStore.ReportShipsRule) and the committed profile's
/// dispositions. The build-time mechanism (buildprofile.targets → SQLT_NO_REPORT_&lt;X&gt; →
/// BuildModules.Reports.* → #if-compiled builders) is proven separately by verify-community-build.ps1
/// against .handoff/gated-routes.txt.
/// </summary>
public sealed class ReportProfileGatingTests
{
    // ── 1. The fail-closed rule, deterministically (no file needed) ──────────────────────────

    [Theory]
    [InlineData("on", true)]
    [InlineData("ON", true)]      // case-insensitive
    [InlineData("On", true)]
    [InlineData("off", false)]
    [InlineData("OFF", false)]
    [InlineData("ship", false)]   // module vocabulary is NOT a report "on"
    [InlineData("true", false)]
    [InlineData("1", false)]
    [InlineData("", false)]
    [InlineData("  on  ", false)] // must be exactly "on", not padded
    public void ReportShipsRule_ShipsOnlyOnExactlyOn(string state, bool expected) =>
        Assert.Equal(expected, BuildProfileStore.ReportShipsRule(state));

    [Fact]
    public void ReportShipsRule_FailsClosed_OnAnUnlistedKey()
    {
        // THE fail-closed default: a report key absent from the community profile resolves to null,
        // which must be OFF. A future report added to the code without a "reports" entry is gated,
        // never shipped by accident.
        Assert.False(BuildProfileStore.ReportShipsRule(null));
    }

    // ── 2. The catalog is coherent ───────────────────────────────────────────────────────────

    [Fact]
    public void KeeperAndGatedCatalogs_AreDisjoint_AndCoverTheNamedReports()
    {
        var keepers = BuildProfileStore.CommunityKeeperReports;
        var gated = BuildProfileStore.GatedReports;

        // No report is both kept and gated.
        Assert.Empty(keepers.Intersect(gated, StringComparer.OrdinalIgnoreCase));

        // The three instruments Adrian named are keepers — and ONLY those three ("three keepers
        // only", the 2026-07-21 follow-up ruling that gated compliance-report).
        Assert.Contains("audit-evidence", keepers);
        Assert.Contains("risk-register", keepers);
        Assert.Contains("risk-acknowledgement", keepers);
        Assert.Equal(3, keepers.Count);
        Assert.Contains("compliance-report", gated);

        // The deliverables ruling B lists as lost are gated.
        foreach (var lost in new[] { "executive-summary", "dba-handoff", "hadr-posture",
                                     "findings-pdf", "executive-briefing", "cio-executive",
                                     "diagnostics-roadmap" })
            Assert.Contains(lost, gated);
    }

    // ── 3. The committed profile actually enforces the split (dev-tree; no-op off-tree) ──────

    [Fact]
    public void CommittedProfile_KeepsTheThreeInstruments_GatesEverythingElse()
    {
        var store = new BuildProfileStore(NullLogger<BuildProfileStore>.Instance);
        if (!store.IsAvailable) return; // not a dev working tree — the rule tests above still hold

        foreach (var keeper in BuildProfileStore.CommunityKeeperReports)
            Assert.True(store.ReportShipsInCommunity(keeper),
                $"keeper '{keeper}' must ship in community (expected reports.{keeper} == \"on\").");

        foreach (var gated in BuildProfileStore.GatedReports)
            Assert.False(store.ReportShipsInCommunity(gated),
                $"gated report '{gated}' must NOT ship in community (expected reports.{gated} != \"on\").");
    }

    [Fact]
    public void CommittedProfile_FailsClosed_ForAReportKeyItHasNeverHeardOf()
    {
        var store = new BuildProfileStore(NullLogger<BuildProfileStore>.Instance);
        if (!store.IsAvailable) return;

        // A brand-new report key that no one added to the profile: gated by default.
        Assert.False(store.ReportShipsInCommunity("a-future-report-nobody-listed-yet"));
        Assert.Null(store.GetReportState("a-future-report-nobody-listed-yet"));
    }
}
