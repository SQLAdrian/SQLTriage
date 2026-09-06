/* In the name of God, the Merciful, the Compassionate */
/*
 * CheckResolutionPrecedenceTests — Phase-2 item 5, the durable-wins ruling (Adrian, 2026-09-01).
 *
 * THE DEFECT, found by the Phase-1 build's own linkage test (B1.6) and deliberately left unfixed
 * there because it needed a ruling rather than a tie-break: five shipped check ids are each claimed
 * by TWO templates, and CheckResolutionLookup.BuildIndex kept whichever one it met first while
 * walking RemediationTemplateStore.All(). Which fix an operator was offered for a backup-recency or
 * CHECKDB finding was therefore decided by dictionary enumeration order - an implementation detail
 * standing in for a product decision nobody had made. Worse, the loser was not deprioritised, it was
 * DROPPED: the second fix became unreachable through this lookup entirely.
 *
 * THE RULING: durable wins. Installing the Maintenance Solution keeps taking backups and running
 * CHECKDB from then on; running a backup now clears today's finding and lets it come back tomorrow.
 * The one-shot stays available as a secondary, because "I need a backup right now" is a real
 * operator intent - it is just not the fix to lead with.
 *
 * WHAT WAS BUILT (stated, because the brief asked which): the lookup is no longer single-valued.
 * CheckResolution.Template is the PRIMARY by explicit rank, and CheckResolution.Alternatives keeps
 * every other claimant in rank order, so the relationship survives in data for a surface to offer
 * later. Nothing about the existing single-claimant path changed.
 */

using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    public class CheckResolutionPrecedenceTests
    {
        private readonly ITestOutputHelper _out;
        public CheckResolutionPrecedenceTests(ITestOutputHelper output) => _out = output;

        private static RemediationTemplateStore NewStore() =>
            new(NullLogger<RemediationTemplateStore>.Instance);

        /// <summary>The five ids B1.6 found, with the WINNER the ruling names for each.</summary>
        public static readonly (string CheckId, string Winner, string Secondary)[] RuledPrecedence =
        {
            ("SQLT-BLITZ-DBCC-CHECKDB-NOT-PERFORMED-RECENTLY", "INSTALLMAINTENANCESOLUTION", "CHECKDBNOW"),
            ("SQLT-BLITZ-CORRUPTION-CHECKS-NOT-OPTIMAL",       "INSTALLMAINTENANCESOLUTION", "CHECKDBNOW"),
            ("SQLT-BPCHK-00580-NO-FULL-BACKUPS",               "INSTALLMAINTENANCESOLUTION", "BACKUPDATABASENOW"),
            ("SQLT-BLITZ-BACKUP-RECENCY",                      "INSTALLMAINTENANCESOLUTION", "BACKUPDATABASENOW"),
            ("SQLT-BLITZ-LOG-BACKUP-RECENCY",                  "INSTALLMAINTENANCESOLUTION", "BACKUPDATABASENOW"),
        };

        /// <summary>
        /// ⚠ WHAT THIS TEST DOES AND DOES NOT PROVE, measured by mutation rather than assumed.
        /// Replacing the rank comparison with `return 0` (enumeration order again) left this test
        /// GREEN: today's dictionary order already happens to put INSTALLMAINTENANCESOLUTION first
        /// for all five ids. That is exactly B1.6's point - the answer was RIGHT BY ACCIDENT - and
        /// it means this test is a PIN of the ruled outcome, not a proof that the ordering
        /// mechanism is doing the work. The mechanism is proved by
        /// <see cref="TwoTemplatesOfTheSameRank_AreOrderedByKey_SoTheAnswerIsStable"/>, which DID
        /// go red under that mutation, and by
        /// <see cref="ThePrecedenceIsAnExplicitRANK_NotEnumerationOrder"/>.
        /// </summary>
        [Fact]
        public void TheFiveDoublyClaimedIds_ResolveToTheDURABLEFix()
        {
            var lookup = new CheckResolutionLookup(NewStore());

            foreach (var (checkId, winner, _) in RuledPrecedence)
            {
                var r = lookup.Resolve(checkId);
                Assert.NotNull(r);
                Assert.True(r!.IsOneClick, checkId);
                _out.WriteLine($"{checkId} -> {r.Template!.Key} (alt: {string.Join(", ", r.Alternatives.Select(a => a.Key))})");
                Assert.Equal(winner, r.Template!.Key);
            }
        }

        [Fact]
        public void TheONESHOTIsKeptAsASecondary_NotDropped()
        {
            // The half of the fix that is easy to forget: the loser is real product data. Before
            // this, it was discarded at index time and could not be offered at all.
            var lookup = new CheckResolutionLookup(NewStore());

            foreach (var (checkId, winner, secondary) in RuledPrecedence)
            {
                var r = lookup.Resolve(checkId)!;
                Assert.True(r.HasAlternatives, $"{checkId} lost its secondary fix");
                Assert.Equal(new[] { secondary }, r.Alternatives.Select(a => a.Key));
                Assert.DoesNotContain(winner, r.Alternatives.Select(a => a.Key));
            }
        }

        [Fact]
        public void TheOrdinarySingleClaimantCase_IsUnchanged_AndHasNoAlternatives()
        {
            // The negative control. If every id suddenly reported alternatives, the assertions
            // above would pass for the wrong reason.
            var lookup = new CheckResolutionLookup(NewStore());

            var maxdop = lookup.Resolve("SQLT-BPCHK-00220-PARALLELISM-MAXDOP");
            Assert.NotNull(maxdop);
            Assert.Equal("MAXDOP", maxdop!.Template!.Key);
            Assert.False(maxdop.HasAlternatives);
            Assert.Empty(maxdop.Alternatives);

            Assert.Null(lookup.Resolve("SQLT-NOT-A-REAL-CHECK-ID"));
        }

        [Fact]
        public void ThePrecedenceIsAnExplicitRANK_NotEnumerationOrder()
        {
            // The property, asserted on the ranking function itself rather than only through its
            // effect: an order-dependent implementation would still pass the two tests above on a
            // day the dictionary happened to enumerate the durable fix first.
            Assert.Equal(ResolutionPrecedence.Durable, ResolutionPrecedence.RankOf("INSTALLMAINTENANCESOLUTION"));
            Assert.Equal(ResolutionPrecedence.OneShot, ResolutionPrecedence.RankOf("BACKUPDATABASENOW"));
            Assert.Equal(ResolutionPrecedence.OneShot, ResolutionPrecedence.RankOf("CHECKDBNOW"));
            Assert.Equal(ResolutionPrecedence.Default, ResolutionPrecedence.RankOf("MAXDOP"));
            Assert.Equal(ResolutionPrecedence.Default, ResolutionPrecedence.RankOf("SOMETHING-UNKNOWN"));
            Assert.Equal(ResolutionPrecedence.Default, ResolutionPrecedence.RankOf((string?)null));

            Assert.True(ResolutionPrecedence.Durable < ResolutionPrecedence.Default);
            Assert.True(ResolutionPrecedence.Default < ResolutionPrecedence.OneShot);
        }

        [Fact]
        public void TwoTemplatesOfTheSameRank_AreOrderedByKey_SoTheAnswerIsStable()
        {
            // The tie-break matters as much as the rank: without it, two same-rank claimants would
            // reintroduce enumeration order through the back door, and the answer would depend on
            // the store's internal ordering exactly as before.
            // Two same-rank claimants come in through the shipped overlay seam, declared in the
            // order that would give the WRONG answer if enumeration order still decided.
            // ⚠ The overlay only admits a Configuration op whose sp_configure setting a SHIPPED
            // template already remediates (the store's own anti-minting rule), so these two ride
            // an existing option name rather than an invented one.
            const string overlayJson = @"{""schemaVersion"":1,""templates"":[
              {""key"":""ZZZSECOND"",""displayName"":""Z"",""kind"":""Configuration"",
               ""resolvesCheckIds"":[""SQLT-PRECEDENCE-TIE""],
               ""operation"":{""opKind"":""SpConfigure"",""configName"":""optimize for ad hoc workloads"",
                              ""valueParam"":""V"",""minValue"":0,""maxValue"":1}},
              {""key"":""AAAFIRST"",""displayName"":""A"",""kind"":""Configuration"",
               ""resolvesCheckIds"":[""SQLT-PRECEDENCE-TIE""],
               ""operation"":{""opKind"":""SpConfigure"",""configName"":""optimize for ad hoc workloads"",
                              ""valueParam"":""V"",""minValue"":0,""maxValue"":1}}
            ]}";
            var tempPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "rem-precedence-" + System.Guid.NewGuid().ToString("N") + ".json");
            System.IO.File.WriteAllText(tempPath, overlayJson);
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance, tempPath);

            var lookup = new CheckResolutionLookup(store);
            var r = lookup.Resolve("SQLT-PRECEDENCE-TIE");
            Assert.NotNull(r);
            Assert.Equal("AAAFIRST", r!.Template!.Key);
            Assert.Equal(new[] { "ZZZSECOND" }, r.Alternatives.Select(a => a.Key));

            System.IO.File.Delete(tempPath);
        }
    }
}
