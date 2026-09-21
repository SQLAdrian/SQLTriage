/* In the name of God, the Merciful, the Compassionate */
/*
 * RemediationDedupeLinkageTests — plan item 1.3 (app side), Adrian's ruling 2026-09-01.
 *
 * NINE corpus checks detect settings that a shipped built-in template ALREADY fixes one-click. They
 * carry op_kind but auto_fixable:false, so RemediationTemplateStore.LoadCorpusTemplates never
 * reaches them and its dedupe path never appends their ids — a failed check offered no fix even
 * though the fix had shipped. The ruling was to wire the linkage, which adds no new execution path
 * and no new risk: nine findings point at five existing, gate-blessed templates.
 *
 * WHY THIS TEST EXISTS AT ALL. The linkage is a hard-coded string on one side and corpus frontmatter
 * on the other, with nothing structural joining them: a corpus rename silently unlinks a fix and the
 * app keeps compiling, keeps passing, and quietly stops offering a remediation. So the nine ids are
 * pinned EXACTLY here, and the resolution is asserted through the SAME CheckResolutionLookup the
 * pages read — not by inspecting the list — so a change that leaves the strings in place but breaks
 * resolution also fails.
 *
 * WHAT THIS TEST CANNOT DO. It cannot prove the ids still exist in the corpus: this project has no
 * corpus dependency and none is being added for a linkage table. It pins the APP's half. Each id was
 * grepped from `corpus-v2/checks/*.md` frontmatter at corpus 1ae1928f on 2026-09-01 (proved by that
 * grep, not guessed); keeping them true over time is the corpus lane's job, and a rename there
 * surfaces here only once someone re-runs the grep. That gap is named rather than papered over.
 */

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    public class RemediationDedupeLinkageTests
    {
        private readonly ITestOutputHelper _out;
        public RemediationDedupeLinkageTests(ITestOutputHelper output) => _out = output;

        /// <summary>
        /// The ruled set, verbatim: corpus check id -> the built-in template key that already fixes
        /// it. Nine rows. Both sides are exact strings on purpose — a near-miss is the failure mode
        /// this file exists to catch.
        /// </summary>
        public static readonly IReadOnlyList<(string CheckId, string TemplateKey)> RuledLinks = new[]
        {
            ("SQLT-BPCHK-00220-PARALLELISM-MAXDOP", "MAXDOP"),
            ("SQLT-CUSTOM-MAXDOP", "MAXDOP"),
            ("SQLT-FRONTIER-MAXDOP-CXPACKET", "MAXDOP"),
            ("SQLT-CORE-TUNE-COST-THRESHOLD-FOR-PARALLELISM-DEFAULT-5-IS-O", "CTFP"),
            ("SQLT-BLITZ-MAX-MEMORY-SET-TOO-HIGH", "MAXSERVERMEMORY"),
            ("SQLT-BPCHK-00280-MEMORY-ISSUES-MAXSERVERMEM", "MAXSERVERMEMORY"),
            ("SQLT-VA-MAX-SERVER-MEMORY", "MAXSERVERMEMORY"),
            ("SQLT-VA-AD-HOC-QUERIES-OFF", "ADHOCDISTRIBUTEDQUERIES"),
            ("SQLT-VA-CROSS-DB-OWNERSHIP", "CROSSDBOWNERSHIP"),
        };

        public static IEnumerable<object[]> LinkCases() =>
            RuledLinks.Select(l => new object[] { l.CheckId, l.TemplateKey });

        private static RemediationTemplateStore NewStore() =>
            new(NullLogger<RemediationTemplateStore>.Instance);

        [Theory]
        [MemberData(nameof(LinkCases))]
        public void EachRuledCheck_ResolvesToItsBuiltInTemplate(string checkId, string templateKey)
        {
            var lookup = new CheckResolutionLookup(NewStore());

            var resolution = lookup.Resolve(checkId);

            Assert.NotNull(resolution);
            Assert.True(resolution!.IsOneClick, $"'{checkId}' resolved to no one-click template.");
            Assert.Equal(templateKey, resolution.Template!.Key);
        }

        [Fact]
        public void AllNineWirings_ArePresentOnTheShippedTemplates_ByExactId()
        {
            var store = NewStore();
            foreach (var (checkId, templateKey) in RuledLinks)
            {
                var t = store.TryGet(templateKey);
                Assert.NotNull(t);
                Assert.Contains(checkId, t!.ResolvesCheckIds);
                _out.WriteLine($"{templateKey} <- {checkId}");
            }
            Assert.Equal(9, RuledLinks.Count);
        }

        [Fact]
        public void TheLinkedTemplates_AreTheOnesTheRulingNamed_WithTheExpectedCounts()
        {
            var store = NewStore();

            // Counts, so an accidental extra id on one of these five is caught too.
            Assert.Equal(3, store.TryGet("MAXDOP")!.ResolvesCheckIds.Count);
            Assert.Equal(1, store.TryGet("CTFP")!.ResolvesCheckIds.Count);
            Assert.Equal(3, store.TryGet("MAXSERVERMEMORY")!.ResolvesCheckIds.Count);
            Assert.Equal(1, store.TryGet("ADHOCDISTRIBUTEDQUERIES")!.ResolvesCheckIds.Count);
            Assert.Equal(1, store.TryGet("CROSSDBOWNERSHIP")!.ResolvesCheckIds.Count);
        }

        /// <summary>
        /// Every check id claimed by more than one template, as the store actually ships.
        /// CheckResolutionLookup.BuildIndex keeps the FIRST template it encounters for a given id
        /// and drops the rest, so an id in here resolves to ONE of its claimants and the loser is
        /// unreachable through Resolve.
        /// </summary>
        private static List<(string Id, string[] Claimants)> OverlappingCheckIds(RemediationTemplateStore store) =>
            store.All()
                .SelectMany(t => t.ResolvesCheckIds.Select(id => (id, t.Key)))
                .GroupBy(x => x.id, System.StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => (g.Key, g.Select(x => x.Key).OrderBy(k => k, System.StringComparer.Ordinal).ToArray()))
                .OrderBy(x => x.Key, System.StringComparer.Ordinal)
                .ToList();

        [Fact]
        public void NoneOfTheNineIsClaimedByASecondTemplate_SoEachResolvesUnambiguously()
        {
            // The property this lane actually needs: the nine ids it wired must each be claimed by
            // exactly one template, so Resolve's first-wins rule never has a choice to make about
            // them. (The wider store DOES contain overlaps — see the next test, which pins them.)
            var store = NewStore();
            var overlapping = OverlappingCheckIds(store).Select(x => x.Id).ToHashSet(System.StringComparer.Ordinal);

            var offenders = RuledLinks.Where(l => overlapping.Contains(l.CheckId))
                                      .Select(l => l.CheckId).ToList();

            Assert.True(offenders.Count == 0,
                "newly-linked check ids claimed by more than one template: " + string.Join(", ", offenders));
        }

        [Fact]
        public void ThePreExistingOverlaps_AreExactlyTheFiveThatShippedBeforeThisLane()
        {
            // NOT introduced by the dedupe linkage — these five ship at the lane base (98dca43) and
            // were verified there by reading the seeded ResolvesCheckIds, not inferred. They are
            // pinned rather than deleted because the condition is real and worth watching:
            //
            //   a backup/CHECKDB finding is legitimately resolvable EITHER by installing the
            //   Maintenance Solution (the durable fix) OR by running a one-shot backup/CHECKDB now
            //   (the immediate one), and both templates claim it.
            //
            // ⚠ UPDATED 2026-09-01, PHASE 2 ITEM 5. This test used to say, correctly at the time,
            // that WHICH fix the user is offered "depends on the order CheckResolutionLookup
            // .BuildIndex walks RemediationTemplateStore.All()" and deliberately asserted only the
            // SET, never a winner, because pinning one would have dressed an enumeration-order
            // accident up as an intended ruling. The ruling now exists (durable wins), so the
            // winner is asserted BY NAME — see CheckResolutionPrecedenceTests for the precedence
            // itself and for the secondary that is now kept rather than dropped. The set assertion
            // stays: it is what stops the overlap growing unnoticed.
            var store = NewStore();
            var overlapping = OverlappingCheckIds(store);

            foreach (var (id, claimants) in overlapping)
                _out.WriteLine($"{id} -> [{string.Join(", ", claimants)}]");

            Assert.Equal(new[]
            {
                "SQLT-BLITZ-BACKUP-RECENCY",
                "SQLT-BLITZ-CORRUPTION-CHECKS-NOT-OPTIMAL",
                "SQLT-BLITZ-DBCC-CHECKDB-NOT-PERFORMED-RECENTLY",
                "SQLT-BLITZ-LOG-BACKUP-RECENCY",
                "SQLT-BPCHK-00580-NO-FULL-BACKUPS",
            }, overlapping.Select(x => x.Id));

            // The WINNERS, by name. Every one of the five is a backup or CHECKDB finding, and the
            // durable fix for all five is the same: install the Maintenance Solution, which keeps
            // taking backups and running CHECKDB from then on, instead of clearing today's finding
            // once and letting it come back tomorrow.
            var lookup = new CheckResolutionLookup(store);
            foreach (var (id, _) in overlapping)
            {
                var r = lookup.Resolve(id);
                Assert.NotNull(r);
                Assert.Equal("INSTALLMAINTENANCESOLUTION", r!.Template!.Key);
                Assert.True(r.HasAlternatives, $"{id}: the one-shot fix was dropped rather than kept as a secondary");
            }
        }

        [Fact]
        public void TheseCheckIds_AreNotAlsoClaimedByAMaintenanceGenerator()
        {
            // The maintenance map is the OTHER resolution shape, and Resolve checks the one-click
            // index first — so a linked id that is also a maintenance check would shadow the
            // generator. None of the nine should be in that map; this asserts it through the
            // observable behaviour rather than by reading the private map.
            var lookup = new CheckResolutionLookup(NewStore());
            foreach (var (checkId, _) in RuledLinks)
            {
                var r = lookup.Resolve(checkId);
                Assert.NotNull(r);
                Assert.False(r!.IsMaintenance, $"'{checkId}' also resolves to a maintenance generator.");
            }
        }

        [Fact]
        public void AnUnlinkedCheckStillResolvesToNothing_SoThePinIsNotVacuous()
        {
            // The negative control: if Resolve returned a template for anything, every assertion
            // above would pass for the wrong reason.
            var lookup = new CheckResolutionLookup(NewStore());
            Assert.Null(lookup.Resolve("SQLT-NOT-A-REAL-CHECK-ID"));
            // And a check the ruling deliberately EXCLUDED stays unlinked: SQLT-VA-XP-CMDSHELL is
            // preview-only (plan §4 group 3 — it breaks Agent jobs calling xp_cmdshell).
            Assert.Null(lookup.Resolve("SQLT-VA-XP-CMDSHELL"));
            // So does the other cost-threshold check, which was not in the ruled nine.
            Assert.Null(lookup.Resolve("SQLT-VA-COST-THRESHOLD-PARALLELISM"));
        }
    }
}
