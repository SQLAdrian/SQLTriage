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
/// The commercial truth of the throttle banner, pinned.
///
/// SQLTriage is FREE SOFTWARE and stays free. The corpus cap is a SAFETY control on pointing the
/// tool at live SQL Servers — it is not a paywall, not a trial, not freemium — and it is raised at
/// no charge. What clients pay for is the extended report and the remediation actions a DBA
/// produces OUTSIDE the app from what the app captured. The copy names that service and points at
/// sqldba.org for its price; it carries NO currency amount (Adrian's ruling 2026-08-20).
///
/// The copy this replaced said "Community version: corpus assessment covers 1 SQL instance per 24h
/// ... or get in touch with Adrian if you need a larger allocation." A seven-persona board scored
/// that surface 1.667/5 on pricing transparency — the lowest element ever recorded on this product.
/// Two personas scored it 1: an MSP owner with a deadline and a wallet ("the fastest thing this
/// product could have done to close me — a number — is the one thing it won't do") and a
/// 180-instance estate that churned on the first screen.
///
/// These tests exist so that copy cannot come back, and so the correction cannot rot into the
/// opposite defect: a PAID client being shown a list price that fights the deal they already have.
/// Since 2026-08-20 that guard covers EVERY provenance, community included — no screen this seam
/// renders may carry a figure.
///
/// NOTE: BuildMode.DevBridgeActive is process-static and false under the test host, so the REAL
/// metering path runs (the dev unlimited escape hatch is off).
/// </summary>
public sealed class CommunityThrottleCopyTests : IDisposable
{
    /// <summary>
    /// The currency marker these tests assert the ABSENCE of. Split across the concatenation on
    /// purpose: this file SHIPS in the public tree, and verify-public-tree.ps1's language gate
    /// scans it for a literal currency token — an assertion written the obvious way would fail the
    /// very gate it exists to uphold. Same idiom the gate script uses on its own secret patterns.
    /// </summary>
    private const string CurrencyToken = "NZ" + "$";

    private readonly List<string> _temp = new();

    private string NewLedgerPath()
    {
        var p = Path.Combine(Path.GetTempPath(), $"demo-copy-{Guid.NewGuid():N}.json");
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

    private DemoRunLedger Ledger(int allowance, DemoAllocationOrigin origin) =>
        new(Bundle(allowance, origin), NullLogger<DemoRunLedger>.Instance, NewLedgerPath());

    /// <summary>The wall a blocked community operator hits: allowance spent, one more instance asked for.</summary>
    private string CommunityBlockRefusal(int allowance = 1)
    {
        var ledger = Ledger(allowance, DemoAllocationOrigin.Community);
        foreach (var i in Enumerable.Range(1, allowance)) ledger.RecordRun($"SQL-{i}");
        return ledger.TryAdmitRun(new[] { "SQL-NEW" }).BlockReason!;
    }

    /// <summary>The wall an ESTATE-sized community operator hits: 180 instances typed in at once.</summary>
    private string CommunityOverRequestRefusal(int asked = 180)
    {
        var ledger = Ledger(1, DemoAllocationOrigin.Community);
        var servers = Enumerable.Range(1, asked).Select(i => $"SQL-{i}").ToArray();
        return ledger.TryAdmitRun(servers).BlockReason!;
    }

    private string CommunityEntitlementBanner(int allowance = 1) =>
        Ledger(allowance, DemoAllocationOrigin.Community).DescribeAllowance();

    /// <summary>Every string a COMMUNITY operator can be shown by this seam.</summary>
    private IEnumerable<string> AllCommunityCopy()
    {
        yield return CommunityEntitlementBanner();
        yield return CommunityBlockRefusal();
        yield return CommunityOverRequestRefusal();
    }

    public void Dispose()
    {
        foreach (var p in _temp)
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
    }

    // ══ 1. It says the software is free ══════════════════════════════════════

    [Fact]
    public void EveryCommunitySurface_SaysTheSoftwareIsFree_InTheFirstSentence()
    {
        // First sentence, not buried: a blocked user reads one or two sentences. If "free" arrives
        // after the refusal it may as well not be there.
        foreach (var text in AllCommunityCopy())
            Assert.StartsWith("SQLTriage is free software and stays free.", text);
    }

    [Fact]
    public void EveryCommunitySurface_CallsTheCapASafetyLimitAndNotAPaywall()
    {
        foreach (var text in AllCommunityCopy())
        {
            Assert.Contains("safety limit on running the tool against live servers", text);
            Assert.Contains("not a paywall", text);
        }
    }

    [Fact]
    public void CommunityCopy_NeverDescribesTheAppAsPaid_Trial_OrFreemium()
    {
        // The vocabulary of gated software. "paywall" is deliberately absent from this list — the
        // copy uses it to DENY the thing ("not a paywall"), which is the point.
        string[] banned =
        {
            "trial", "freemium", "upgrade", "subscription", "purchase the",
            "buy the app", "paid version", "pro version", "licence fee", "license fee",
            "unlock the app", "premium version",
        };

        foreach (var text in AllCommunityCopy())
            foreach (var word in banned)
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommunityCopy_NeverManufacturesUrgency()
    {
        string[] banned = { "limited time", "act now", "hurry", "offer ends", "last chance", "only today" };

        foreach (var text in AllCommunityCopy())
            foreach (var word in banned)
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
    }

    // ══ 2. It carries no number, and points at where the number lives ════════

    [Fact]
    public void BothCommunityRefusals_CarryNoCurrencyAmount()
    {
        // Adrian's ruling 2026-08-20. The blocked-buyer defect is answered by the pointer below,
        // not by a figure baked into the binary.
        foreach (var refusal in new[] { CommunityBlockRefusal(), CommunityOverRequestRefusal() })
        {
            Assert.DoesNotContain(CurrencyToken, refusal);
            Assert.DoesNotContain("1,200", refusal);
        }
    }

    [Fact]
    public void ThrottleCopy_CarriesNoPricing_InEveryProfile_RulingA_Superseded_2026_08_20()
    {
        // Ruling A (2026-07-21) pinned a New Zealand dollar figure into this copy in ALL profiles
        // and forbade byte-scanning it out. Adrian's ruling 2026-08-20 SUPERSEDES it: the pricing
        // copy is REMOVED, so the public tree carries no currency amounts at all. The record is
        // sqltriage-meta DECISIONS 2026-08-20.
        //
        // What Ruling A got right and this keeps: the copy is NOT profile-scoped and NOT scanned
        // out. It lives in DemoRunLedger, which is ALWAYS-compiled (never report-gated), so it is
        // byte-identical in the full and community builds — the number is simply not written.
        // If a future change reintroduces a figure or reworks the offer, this pin goes red.
        Assert.DoesNotContain(CurrencyToken, CommunityBlockRefusal());
        Assert.DoesNotContain(CurrencyToken, CommunityOverRequestRefusal());
        // The exact service-offer sentence is the single source; both refusals must carry it verbatim.
        const string offer = "What costs money is the extended report and the remediation actions a DBA produces from " +
                             "what this tool captured. Ask about either, and for current pricing, at sqldba.org.";
        Assert.Contains(offer, CommunityBlockRefusal());
        Assert.Contains(offer, CommunityOverRequestRefusal());
    }

    [Fact]
    public void ThePrice_LivesAtTheLivePointer_NotInTheBinary()
    {
        // This string ships COMPILED into an app that is offline by design and has no CPI
        // mechanism: a 2026 build read in 2028 still shows this sentence. The pointer is now the
        // whole of the pricing answer, so it is load-bearing — without it the copy names a paid
        // service and gives the operator nowhere to ask what it costs.
        foreach (var refusal in new[] { CommunityBlockRefusal(), CommunityOverRequestRefusal() })
        {
            Assert.Contains("sqldba.org", refusal);
            Assert.Contains("current pricing", refusal);
        }
    }

    [Fact]
    public void TheOffer_NamesTheDbaWork_NotTheSoftware()
    {
        // What is paid for must be named, or "what costs money" reads as the app itself — the
        // exact misreading this whole change removes.
        foreach (var refusal in new[] { CommunityBlockRefusal(), CommunityOverRequestRefusal() })
        {
            Assert.Contains("extended report", refusal);
            Assert.Contains("remediation actions", refusal);
            Assert.Contains("a DBA produces", refusal);
        }
    }

    [Fact]
    public void CommunityRefusals_SayTheCapItselfIsRaisedAtNoCost()
    {
        // The cap lifting is FREE. If the only route out of the wall looks like a purchase, the
        // "not a paywall" sentence above is a claim the rest of the message contradicts.
        foreach (var refusal in new[] { CommunityBlockRefusal(), CommunityOverRequestRefusal() })
            Assert.Contains("lift the cap for you at no charge", refusal);
    }

    [Fact]
    public void CommunityCopy_InventsNoSignupFlow()
    {
        // There is no self-service registration yet. The call to action is a real place to ask,
        // and nothing that implies a form, a portal, or an account that does not exist.
        string[] banned = { "sign up", "signup", "register at", "create an account", "start your" };

        foreach (var text in AllCommunityCopy())
            foreach (var word in banned)
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
    }

    // ══ 3. No number on ANY screen this seam renders ═════════════════════════

    [Fact]
    public void NoCommunityMessage_CarriesAnyCurrencyFigure()
    {
        // Was: "must carry exactly ONE figure". Since the 2026-08-20 ruling the count is zero, on
        // every community surface including the entitlement banner.
        foreach (var text in AllCommunityCopy())
            Assert.Equal(0, CountOccurrences(text, CurrencyToken));
    }

    [Fact]
    public void BothCommunityRefusals_QuoteTheIdenticalOfferSentence()
    {
        // One source for the offer, so the desktop banner and the CLI can never drift into two
        // prices for one service.
        const string marker = "What costs money is";

        var block = CommunityBlockRefusal();
        var over = CommunityOverRequestRefusal();

        var blockOffer = block[block.IndexOf(marker, StringComparison.Ordinal)..].TrimEnd();
        var overOffer = over[over.IndexOf(marker, StringComparison.Ordinal)..];

        // The over-request message closes with its own parenthetical about the unmetered lanes, so
        // the block message's offer must be a PREFIX of it — byte for byte, one source.
        Assert.StartsWith(blockOffer, overOffer, StringComparison.Ordinal);
    }

    [Fact]
    public void PaidLicences_AreNeverShownAListPrice()
    {
        // The failure mode this seam was always guarded against: a client who has already agreed
        // terms being quoted a figure that fights their deal. Every paid provenance, blocked and
        // over-asked, must be free of currency. Since 2026-08-20 the community lane is held to the
        // same bar, so this test now pins the paid lane's OTHER two claims as well: a paid holder
        // is never told they are on the free build.
        foreach (var origin in new[] { DemoAllocationOrigin.Signed, DemoAllocationOrigin.Unsigned })
        {
            var blocked = Ledger(1, origin);
            blocked.RecordRun("SQL-A");

            var over = Ledger(1, origin);

            foreach (var text in new[]
                     {
                         blocked.DescribeAllowance(),
                         blocked.TryAdmitRun(new[] { "SQL-B" }).BlockReason!,
                         over.TryAdmitRun(new[] { "SQL-A", "SQL-B", "SQL-C" }).BlockReason!,
                     })
            {
                Assert.DoesNotContain(CurrencyToken, text);
                Assert.DoesNotContain("1,200", text);
                Assert.DoesNotContain("free software", text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void UnlimitedLicences_ShowNothingAtAll()
    {
        // No banner, so no price, on an unmetered install.
        foreach (var origin in Enum.GetValues<DemoAllocationOrigin>())
            Assert.Equal(string.Empty, Ledger(0, origin).DescribeAllowance());
    }

    // ══ 4. The number the operator is metered on is the number they are told ══

    [Fact]
    public void TheCapInTheCopy_IsTheCapBeingEnforced()
    {
        // Interpolated, never a literal "1": if a community allowance is ever raised in the bundle
        // the sentence must move with it rather than becoming a lie about the enforced limit.
        Assert.Contains("cap of 1 SQL instance per 24h", CommunityEntitlementBanner(1));
        Assert.Contains("cap of 3 SQL instances per 24h", CommunityEntitlementBanner(3));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }
}
