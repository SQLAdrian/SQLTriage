/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services.Licensing;
using Xunit;

namespace SQLTriage.Tests.Licensing;

/// <summary>
/// The three defects a cold gate found in the demo-allocation work. All three govern whether a
/// PAYING client can run an audit, and none of them had a test:
///
///   D1  TryAdmitRun counted the REQUEST against N but not the slots already CLAIMED, so N=2 with
///       one claim standing admitted a 2-instance request — 3 consumed against 2 allowed.
///   D2  A free COMMUNITY install loads a real Free-tier bundle, so it fell through to Unsigned and
///       was told its bundle "predates the corpus allocation field... ask Adrian to re-mint".
///   D3  The desktop lane (Pages/QuickCheck.razor) kept its own admission check and its own banner
///       copy, so it could still say "Community version" to a paid client.
///
/// NOTE: BuildMode.DevBridgeActive is process-static and false under the test host, so these
/// exercise the REAL metering path (the dev unlimited escape hatch is off).
/// </summary>
public sealed class DemoAllocationGateFixesTests : IDisposable
{
    private readonly List<string> _temp = new();

    private string NewLedgerPath()
    {
        var p = Path.Combine(Path.GetTempPath(), $"demo-gatefix-{Guid.NewGuid():N}.json");
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

    public void Dispose()
    {
        foreach (var p in _temp)
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
    }

    // ══ D1. The meter must not over-admit ════════════════════════════════════
    // Nothing is recorded until a run SUCCEEDS, so a whole-request check that measures each
    // instance against the same untouched free-slot count admits the whole request every time.

    [Fact]
    public void N2_WithOneClaimStanding_RefusesATwoNewInstanceRequest()
    {
        // The gate's exact reproduction. Allowance 2, one slot already spent, two NEW instances
        // asked for: admitting this consumes 3 distinct instances against an allowance of 2.
        var ledger = Ledger(allowance: 2);
        ledger.RecordRun("SQL-A");

        var admission = ledger.TryAdmitRun(new[] { "SQL-B", "SQL-C" });

        Assert.False(admission.Allowed,
            "2 new instances on top of 1 standing claim is 3 against an allowance of 2");
        Assert.False(string.IsNullOrWhiteSpace(admission.BlockReason));
    }

    [Fact]
    public void N2_WithOneClaimStanding_StillAdmitsExactlyOneNewInstance()
    {
        // The other side of the same rule: the fix must not cost the operator the slot they still
        // hold. One free slot, one new instance asked for — that must pass.
        var ledger = Ledger(allowance: 2);
        ledger.RecordRun("SQL-A");

        Assert.True(ledger.TryAdmitRun(new[] { "SQL-B" }).Allowed,
            "one free slot must still admit one new instance");
    }

    [Fact]
    public void ClaimedInstancesDoNotCountAgainstTheRequest()
    {
        // A request that names instances already claimed inside their window consumes nothing, so
        // it must pass however many of them there are.
        var ledger = Ledger(allowance: 2);
        ledger.RecordRun("SQL-A");
        ledger.RecordRun("SQL-B");

        Assert.True(ledger.TryAdmitRun(new[] { "SQL-A", "SQL-B" }).Allowed,
            "re-running instances already claimed in this window is free");
        Assert.True(ledger.TryAdmitRun(new[] { "sql-a", "SQL-B" }).Allowed,
            "and the free path is case-insensitive, like the claim map");
    }

    [Fact]
    public void MixedRequest_CountsOnlyTheNewInstancesAgainstTheFreeSlots()
    {
        // N=3, two claims standing → exactly one free slot. A request naming one CLAIMED and one
        // NEW instance consumes exactly one slot and must pass; two new ones must not.
        var ledger = Ledger(allowance: 3);
        ledger.RecordRun("SQL-A");
        ledger.RecordRun("SQL-B");

        Assert.True(ledger.TryAdmitRun(new[] { "SQL-A", "SQL-C" }).Allowed,
            "one claimed + one new = one slot, and one slot is free");
        Assert.False(ledger.TryAdmitRun(new[] { "SQL-C", "SQL-D" }).Allowed,
            "two new instances need two slots and only one is free");
    }

    /// <summary>
    /// The property the whole defect is about: ADMIT then RECORD must never leave more distinct
    /// claims standing than the licence allows. Swept across the matrix so a fix that only patches
    /// the reported N=2 case fails here.
    /// </summary>
    [Fact]
    public void AdmittedRunsCanNeverConsumeMoreDistinctInstancesThanTheAllowance()
    {
        foreach (var allowance in new[] { 1, 2, 3, 5 })
        {
            foreach (var preClaimed in Enumerable.Range(0, allowance + 1))
            {
                foreach (var requestSize in Enumerable.Range(1, allowance + 2))
                {
                    var ledger = Ledger(allowance);
                    foreach (var i in Enumerable.Range(1, preClaimed)) ledger.RecordRun($"OLD-{i}");

                    var request = Enumerable.Range(1, requestSize).Select(i => $"NEW-{i}").ToArray();
                    var label = $"N={allowance} preClaimed={preClaimed} request={requestSize}";

                    if (!ledger.TryAdmitRun(request).Allowed) continue;

                    // Admitted → simulate the run succeeding, exactly as both lanes do.
                    foreach (var inst in request) ledger.RecordRun(inst);

                    Assert.True(ledger.Status().InstancesUsed <= allowance,
                        $"admitting {label} consumed {ledger.Status().InstancesUsed} " +
                        $"distinct instances against an allowance of {allowance}");
                }
            }
        }
    }

    [Fact]
    public void TheFixNeverRefusesAnUnlimitedLicence()
    {
        // "Do not flip it into failing CLOSED on a paying client." A full bundle (N=0) is unlimited
        // and must stay unlimited no matter how much the ledger has seen.
        var ledger = Ledger(allowance: 0);
        foreach (var i in Enumerable.Range(1, 40)) ledger.RecordRun($"PROD-{i}");

        var request = Enumerable.Range(1, 30).Select(i => $"BRAND-NEW-{i}").ToArray();
        Assert.True(ledger.TryAdmitRun(request).Allowed);
    }

    [Fact]
    public void OverStuffedLedger_StillLetsAClaimedInstanceReRun()
    {
        // Defence for the client mid-window: if _claims somehow exceeds N (a bundle downgraded
        // mid-window, or a run recorded without admission — the over-admit above did exactly this),
        // re-running an instance they ALREADY hold must not start being refused.
        var ledger = Ledger(allowance: 1);
        ledger.RecordRun("SQL-A");
        ledger.RecordRun("SQL-B");   // RecordRun does not gate; this is the over-stuffed state
        Assert.Equal(2, ledger.Status().InstancesUsed);

        Assert.True(ledger.TryAdmitRun(new[] { "SQL-A" }).Allowed,
            "an instance already claimed must never be locked out by the capacity rule");
        Assert.True(ledger.TryAdmitRun(new[] { "SQL-A", "SQL-B" }).Allowed,
            "and neither must the over-request rule — 2 instances they already hold against an " +
            "allowance of 1 is a request for NO new slots, not a request for two");
    }

    [Fact]
    public void OverAllowanceRequest_KeepsItsOwnMessage_NotTheExhaustedOne()
    {
        // The two refusals are different facts and must stay differently worded: "your ask is
        // bigger than the licence" is not "you have used this window up".
        var ledger = Ledger(allowance: 2);

        var tooBig = ledger.TryAdmitRun(new[] { "A", "B", "C" }).BlockReason;
        Assert.Contains("you asked for 3", tooBig);

        ledger.RecordRun("A");
        ledger.RecordRun("B");
        var exhausted = ledger.TryAdmitRun(new[] { "C" }).BlockReason;
        Assert.Contains("used it for this 24h window", exhausted);
        Assert.DoesNotContain("you asked for", exhausted);
    }

    // ══ D2. A free user gets the COMMUNITY message, never the re-mint one ════
    // The community build loads a real Free-tier manifest, so "a manifest is present" never meant
    // "a paying client" — yet that is what the provenance was keyed on.

    [Fact]
    public void FreeTierBundle_WithNoAllocationField_IsCommunity_NotUnsigned()
    {
        // The shipped community shape: a Free-tier bundle that decrypts fine and carries no
        // demoCorpusInstancesPer24h.
        var accessor = new BundleAccessor();
        accessor.Replace(
            new BundleManifest { ClientName = "SQLTriage Community", Features = new ManifestFeatures() },
            Tier.Free);

        Assert.Equal(1, accessor.Features.DemoCorpusInstancesPer24h);
        Assert.Equal(DemoAllocationOrigin.Community, accessor.Features.DemoAllocationOrigin);
    }

    [Fact]
    public void PaidBundle_WithNoAllocationField_IsStillUnsigned()
    {
        // The other half of the ruling — an old PAID bundle must keep the re-mint path. If the fix
        // routed everything to Community, a paying client would be told they are on the free build.
        var accessor = new BundleAccessor();
        accessor.Replace(
            new BundleManifest { ClientName = "Bravo OPS", Features = new ManifestFeatures() },
            Tier.Full);

        Assert.Equal(DemoAllocationOrigin.Unsigned, accessor.Features.DemoAllocationOrigin);
    }

    [Fact]
    public void NoBundleAtAll_IsAlsoCommunity()
    {
        var accessor = new BundleAccessor();   // unactivated: no manifest at all
        Assert.Equal(DemoAllocationOrigin.Community, accessor.Features.DemoAllocationOrigin);
    }

    [Fact]
    public void FreeTierWithASignedField_IsStillSigned()
    {
        // The tier test must not swallow a field that IS present.
        var accessor = new BundleAccessor();
        accessor.Replace(
            new BundleManifest
            {
                ClientName = "Community",
                Features = new ManifestFeatures { DemoCorpusInstancesPer24h = 1 },
            },
            Tier.Free);

        Assert.Equal(DemoAllocationOrigin.Signed, accessor.Features.DemoAllocationOrigin);
    }

    [Fact]
    public void FreeInstall_IsNeverAskedToGetABundleRemminted()
    {
        // End to end, through the real accessor: what a free user is actually told.
        var accessor = new BundleAccessor();
        accessor.Replace(
            new BundleManifest { ClientName = "SQLTriage Community", Features = new ManifestFeatures() },
            Tier.Free);
        var ledger = new DemoRunLedger(accessor, NullLogger<DemoRunLedger>.Instance, NewLedgerPath());

        var banner = ledger.DescribeAllowance();
        ledger.RecordRun("SQL-A");
        var refusal = ledger.TryAdmitRun(new[] { "SQL-B" }).BlockReason!;

        foreach (var text in new[] { banner, refusal })
        {
            Assert.StartsWith("SQLTriage is free software and stays free.", text);
            Assert.DoesNotContain("re-mint", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("predates", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("your licence", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void PaidInstall_IsNeverToldItIsTheCommunityVersion_WhateverTheProvenance()
    {
        // The claim that cost a client, held at the accessor boundary rather than the enum: no
        // Tier.Full bundle may produce community copy, field present or absent.
        foreach (var features in new[]
                 {
                     new ManifestFeatures(),                                          // absent → Unsigned
                     new ManifestFeatures { DemoCorpusInstancesPer24h = 1 },          // signed at the limit
                     new ManifestFeatures { DemoCorpusInstancesPer24h = 3 },
                 })
        {
            var accessor = new BundleAccessor();
            accessor.Replace(new BundleManifest { ClientName = "Paying Client", Features = features }, Tier.Full);
            var ledger = new DemoRunLedger(accessor, NullLogger<DemoRunLedger>.Instance, NewLedgerPath());

            var n = accessor.Features.DemoCorpusInstancesPer24h;
            foreach (var i in Enumerable.Range(1, n)) ledger.RecordRun($"SQL-{i}");

            var banner = ledger.DescribeAllowance();
            var refusal = ledger.TryAdmitRun(new[] { "SQL-NEW" }).BlockReason!;

            Assert.DoesNotContain("Community", banner, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Community", refusal, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void CommunityAndUnsignedNeverShareASentence()
    {
        // Same allowance, same ledger state — a free user and a stale paid bundle must be told
        // different things, or the split has no effect.
        var community = Ledger(1, DemoAllocationOrigin.Community);
        var unsigned = Ledger(1, DemoAllocationOrigin.Unsigned);

        Assert.NotEqual(community.DescribeAllowance(), unsigned.DescribeAllowance());
    }

    // ══ D3. The desktop lane has no rule of its own ══════════════════════════
    // TryAdmitDemoRun and DemoBannerText are private members of a Blazor component, so the only way
    // to hold them to the shared seam is to assert against the real shipped markup, which the test
    // csproj copies next to the test binary.

    [Fact]
    public void DesktopLane_AdmissionDelegatesWholly_AndDerivesNoRuleOfItsOwn()
    {
        var body = MethodBody("private bool TryAdmitDemoRun");

        Assert.Contains("DemoLedger.TryAdmitRun(", body);
        Assert.DoesNotContain("Allowance", body);      // no re-derived allocation arithmetic
        Assert.DoesNotContain("CanRun(", body);        // not the per-instance seam, the whole-run one
        Assert.DoesNotContain("Count >", body);        // no private multi-instance check
        Assert.DoesNotContain("Distinct(", body);      // de-duplication belongs to the seam
    }

    [Fact]
    public void DesktopLane_BannerIsDerivedFromTheSeam_NotWrittenLocally()
    {
        var body = MethodBody("private string DemoBannerText");

        Assert.Contains("DemoLedger.DescribeAllowance()", body);
        Assert.Contains("BlockReason", body);          // the exhausted case renders the seam's words
        Assert.DoesNotContain("Community", body);      // no locally-written entitlement copy
    }

    [Fact]
    public void DesktopMarkup_CarriesNoCommunityVersionStringAnywhere()
    {
        // Whole-file, not just the two members: the string that cost a client must not exist in the
        // desktop lane in any form. Comment LINES are stripped — the file legitimately explains the
        // defect in prose, and prose is not what renders.
        var code = StripCommentLines(ReadQuickCheckMarkup());

        Assert.DoesNotContain("Community version", code);
    }

    [Fact]
    public void DesktopBannerComposition_MatchesTheSeam_ForEveryProvenance()
    {
        // What DemoBannerText composes, reproduced against the seam: whatever the desktop shows,
        // it must be the ledger's own words for that provenance and that state.
        foreach (var origin in new[]
                 {
                     DemoAllocationOrigin.Signed,
                     DemoAllocationOrigin.Unsigned,
                     DemoAllocationOrigin.Community,
                 })
        {
            // Slots remaining → the entitlement sentence.
            var fresh = Ledger(2, origin);
            var status = fresh.Status();
            Assert.True(status.Allowance - status.InstancesUsed > 0);
            Assert.StartsWith(fresh.DescribeAllowance(), BannerFor(fresh));

            // Allowance spent → the seam's refusal copy, verbatim.
            var spent = Ledger(1, origin);
            spent.RecordRun("SQL-A");
            var spentStatus = spent.Status();
            Assert.Equal(0, spentStatus.Allowance - spentStatus.InstancesUsed);
            Assert.Equal(spentStatus.BlockReason, BannerFor(spent));

            // And an unlimited licence renders no banner at all.
            Assert.Equal(string.Empty, BannerFor(Ledger(0, origin)));
        }
    }

    /// <summary>Mirrors QuickCheck.razor's DemoBannerText composition against the seam.</summary>
    private static string BannerFor(IDemoRunLedger ledger)
    {
        var s = ledger.Status();
        if (s.Unlimited) return string.Empty;
        var remaining = Math.Max(0, s.Allowance - s.InstancesUsed);
        return remaining > 0
            ? $"{ledger.DescribeAllowance()} — {remaining} remaining in this window. " +
              "Raw Diagnostics and Vulnerability Assessment are unlimited."
            : s.BlockReason ?? ledger.DescribeAllowance();
    }

    // ── markup helpers ───────────────────────────────────────────────────────

    private static string ReadQuickCheckMarkup()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Markup", "QuickCheck.razor");
        Assert.True(File.Exists(path),
            "QuickCheck.razor is copied to the test output by SQLTriage.Tests.csproj; " +
            "if this fails the assertions above would vacuously pass");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// The source text of one @code member, from its signature to the next member declaration, with
    /// comment lines removed. Asserting on the member rather than the file keeps the surrounding
    /// prose — which explains this very defect — out of the assertions.
    /// </summary>
    private static string MethodBody(string signature)
    {
        var markup = ReadQuickCheckMarkup();
        var start = markup.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' not found in QuickCheck.razor — the member was " +
                                "renamed or removed, and every assertion on it would vacuously pass");

        var next = markup.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        var end = next < 0 ? markup.Length : next;
        return StripCommentLines(markup[start..end]);
    }

    private static string StripCommentLines(string source) =>
        string.Join("\n", source
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
}
