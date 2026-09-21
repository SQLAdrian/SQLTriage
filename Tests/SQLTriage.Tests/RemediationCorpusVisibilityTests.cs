/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Honesty hunt (remediation-safety lane, 2026-08-25), clusters 9 and 10.
    ///
    /// <para>r1-08 — <see cref="CheckResolutionLookup"/> is a DI singleton that used to index
    /// <see cref="RemediationTemplateStore"/> once, in its constructor, under a comment claiming
    /// templates were read-only so no invalidation was needed. They are not: /remediation calls
    /// LoadCorpusTemplates at runtime. Whichever page a session opened first therefore decided,
    /// for the life of the process, whether a corpus-fed check offered a one-click fix.</para>
    ///
    /// <para>r1-07 — a corpus fix whose operation block did not parse was counted in the page's
    /// "N malformed" summary and then shown in NEITHER the one-click list nor the preview-only
    /// list, because the page's filter was the exact logical complement of the malformed set.
    /// It vanished from the surface.</para>
    ///
    /// Both are exercised through real objects (a real store, a real lookup) — no fakes.
    /// </summary>
    public class RemediationCorpusVisibilityTests
    {
        // Overlay pointed at a file that does not exist, so no test here can be steered by a
        // Config/remediation-templates.json sitting in the build output.
        private static RemediationTemplateStore NewStore() =>
            new(NullLogger<RemediationTemplateStore>.Instance,
                Path.Combine(Path.GetTempPath(), "sqlt-no-such-overlay-" + Guid.NewGuid().ToString("N") + ".json"));

        private static SqlCheck Check(string id, CheckRemediation remediation) => new()
        {
            Id = id,
            Name = id,
            Remediation = remediation,
        };

        private static SqlCheck WellFormedCorpusFix(string id) => Check(id, new CheckRemediation
        {
            AutoFixable = true,
            RiskClass = "standard",
            Operation = new CheckRemediationOperation
            {
                OpKind = "sp_configure",
                ConfigName = "xp_cmdshell",
                AdvancedOption = true,
                ValueFixed = 0,
            },
        });

        // ── Cluster 9 (r1-08): the lookup follows the store, whatever order pages load in ──

        [Fact]
        public void LookupBuiltBeforeTheCorpusLoad_StillResolvesTheNewTemplate()
        {
            var store = NewStore();
            // The QuickCheck-first session: the singleton is constructed before /remediation has
            // ever run, so the store holds built-ins only.
            var lookup = new CheckResolutionLookup(store);
            Assert.Null(lookup.Resolve("SQLT-VA-XP-CMDSHELL"));

            // Now /remediation loads the corpus into the same store.
            store.LoadCorpusTemplates(new[] { WellFormedCorpusFix("SQLT-VA-XP-CMDSHELL") });

            var res = lookup.Resolve("SQLT-VA-XP-CMDSHELL");
            Assert.NotNull(res);
            Assert.True(res!.IsOneClick);
            Assert.Equal("SQLT-VA-XP-CMDSHELL", res.Template!.Key);
        }

        [Fact]
        public void LookupBuiltBeforeTheCorpusLoad_ResolvesACheckIdAttachedByDedup()
        {
            // The dedup path does not add a dictionary entry at all — it appends the corpus check
            // id to a BUILT-IN template's ResolvesCheckIds, in place. A generation counter that
            // only moved when a key was added or removed would miss this entirely.
            var store = NewStore();
            var lookup = new CheckResolutionLookup(store);
            Assert.Null(lookup.Resolve("SQLT-CORPUS-MAXDOP"));

            store.LoadCorpusTemplates(new[] { Check("SQLT-CORPUS-MAXDOP", new CheckRemediation
            {
                AutoFixable = true,
                Operation = new CheckRemediationOperation
                {
                    OpKind = "sp_configure",
                    ConfigName = "max degree of parallelism",
                    ValueParam = "MaxDop.Value",
                },
            }) });

            var res = lookup.Resolve("SQLT-CORPUS-MAXDOP");
            Assert.NotNull(res);
            Assert.True(res!.IsOneClick);
            Assert.Equal("MAXDOP", res.Template!.Key); // deduped into the built-in, per the contract
        }

        [Fact]
        public void CorpusTemplateThatDisappearsOnReload_StopsResolving()
        {
            // The dangerous direction: the lookup must not keep offering a one-click fix the store
            // no longer registers. A stale index would deep-link the operator to a fix that is gone.
            var store = NewStore();
            store.LoadCorpusTemplates(new[] { WellFormedCorpusFix("SQLT-VA-XP-CMDSHELL") });
            var lookup = new CheckResolutionLookup(store);
            Assert.NotNull(lookup.Resolve("SQLT-VA-XP-CMDSHELL"));

            store.LoadCorpusTemplates(Array.Empty<SqlCheck>()); // corpus reloaded without it

            Assert.Null(lookup.Resolve("SQLT-VA-XP-CMDSHELL"));
        }

        [Fact]
        public void ShippedResolutionsAreUnaffectedByACorpusLoad()
        {
            var store = NewStore();
            var lookup = new CheckResolutionLookup(store);
            Assert.Equal("AGENTALERTPACK", lookup.Resolve("SQLT-BLITZ-NO-OPERATORS")!.Template!.Key);

            store.LoadCorpusTemplates(new[] { WellFormedCorpusFix("SQLT-VA-XP-CMDSHELL") });

            Assert.Equal("AGENTALERTPACK", lookup.Resolve("SQLT-BLITZ-NO-OPERATORS")!.Template!.Key);
            // Review-only maintenance mappings are static and must survive too.
            Assert.True(lookup.Resolve("SQLT-CUSTOM-INDEX-FRAGMENTATION")!.IsMaintenance);
        }

        [Fact]
        public void ResolvingDoesNotMutateTheStoreGeneration()
        {
            // Guards the other way: the lookup must not bump the store on every read, which would
            // turn "rebuild when it changed" into "rebuild always".
            var store = NewStore();
            var lookup = new CheckResolutionLookup(store);
            var before = store.Generation;

            lookup.Resolve("SQLT-BLITZ-NO-OPERATORS");
            lookup.Resolve("SQLT-BLITZ-NO-OPERATORS");
            lookup.Resolve("SQLT-NOTHING-CLAIMS-THIS");

            Assert.Equal(before, store.Generation);
        }

        [Fact]
        public void LoadCorpusTemplates_MovesTheGeneration_EvenWhenNothingRegistered()
        {
            // A load that registers nothing still removed this store's PRIOR corpus additions, so
            // the index built before it is wrong. The counter moves unconditionally on purpose.
            var store = NewStore();
            var before = store.Generation;
            store.LoadCorpusTemplates(Array.Empty<SqlCheck>());
            Assert.True(store.Generation > before, $"Generation did not move: {before} -> {store.Generation}");
        }

        // ── Cluster 10 (r1-07): a malformed corpus fix is visible, with its reason ──

        [Fact]
        public void MalformedCorpusFix_IsPreviewOnly_AndCarriesItsParseProblem()
        {
            var store = NewStore();
            var check = Check("SQLT-TEST-CREATE-INDEX", new CheckRemediation
            {
                AutoFixable = true,
                Operation = new CheckRemediationOperation { OpKind = "create_index" },
            });

            store.LoadCorpusTemplates(new[] { check });

            Assert.False(store.IsRegistered(check.Id));      // not a one-click fix
            Assert.True(store.IsPreviewOnlyCheck(check));    // ...so it must be in the other list
            var reason = store.DescribeCorpusMalformed(check.Id);
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.Contains("create_index", reason!, StringComparison.Ordinal);
        }

        [Theory]
        // sp_configure with nothing to set
        [InlineData("sp_configure", null, null, null, "config_name")]
        // db_set_option with no statement
        [InlineData("db_set_option", null, null, "SELECT 1", "option_sql")]
        // db_set_option with no way to know which databases it would touch
        [InlineData("db_set_option", null, "SET DB_CHAINING OFF", null, "offenders_query")]
        // an op kind this build cannot render at all
        [InlineData("teleport_database", null, null, null, "teleport_database")]
        public void EveryMalformedShape_NamesItsOwnProblem(
            string opKind, string? configName, string? optionSql, string? offendersQuery, string expectedFragment)
        {
            var store = NewStore();
            var check = Check("SQLT-TEST-" + expectedFragment.ToUpperInvariant(), new CheckRemediation
            {
                AutoFixable = true,
                Operation = new CheckRemediationOperation
                {
                    OpKind = opKind,
                    ConfigName = configName,
                    OptionSql = optionSql,
                    OffendersQuery = offendersQuery,
                },
            });

            store.LoadCorpusTemplates(new[] { check });

            var reason = store.DescribeCorpusMalformed(check.Id);
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.Contains(expectedFragment, reason!, StringComparison.Ordinal);
            Assert.True(store.IsPreviewOnlyCheck(check));
        }

        [Fact]
        public void MalformedCountIsDerivedFromTheListItCanShow()
        {
            var store = NewStore();
            var bad1 = Check("SQLT-BAD-1", new CheckRemediation
            {
                AutoFixable = true,
                Operation = new CheckRemediationOperation { OpKind = "create_index" },
            });
            var bad2 = Check("SQLT-BAD-2", new CheckRemediation
            {
                AutoFixable = true,
                Operation = new CheckRemediationOperation { OpKind = "db_set_option", OptionSql = "SET X OFF" },
            });

            store.LoadCorpusTemplates(new[] { bad1, bad2 });

            Assert.Equal(2, store.CorpusMalformedCount);
            Assert.Equal(store.CorpusMalformedChecks.Count, store.CorpusMalformedCount);
            Assert.Equal(new[] { "SQLT-BAD-1", "SQLT-BAD-2" },
                store.CorpusMalformedChecks.Select(m => m.CheckId).OrderBy(x => x, StringComparer.Ordinal).ToArray());

            // A clean reload clears the set AND the count together — they cannot drift.
            store.LoadCorpusTemplates(new[] { WellFormedCorpusFix("SQLT-VA-XP-CMDSHELL") });
            Assert.Equal(0, store.CorpusMalformedCount);
            Assert.Empty(store.CorpusMalformedChecks);
            Assert.Null(store.DescribeCorpusMalformed("SQLT-BAD-1"));
        }

        [Fact]
        public void AWellFormedCorpusFix_IsNotPreviewOnly_AndHasNoParseProblem()
        {
            // The fix must not widen the preview-only list. A registered one-click fix belongs in
            // exactly one place: the one-click list.
            var store = NewStore();
            var good = WellFormedCorpusFix("SQLT-VA-XP-CMDSHELL");

            store.LoadCorpusTemplates(new[] { good });

            Assert.True(store.IsRegistered(good.Id));
            Assert.False(store.IsPreviewOnlyCheck(good));
            Assert.Null(store.DescribeCorpusMalformed(good.Id));
        }

        [Fact]
        public void GenuinelyPreviewOnlyChecks_AreStillPreviewOnly_WithNoFalseParseAlarm()
        {
            var store = NewStore();
            var notAutoFixable = Check("SQLT-PREVIEW-ONLY", new CheckRemediation { AutoFixable = false });
            var noOperation = Check("SQLT-NO-OP-BLOCK", new CheckRemediation { AutoFixable = true, Operation = null });
            var noRemediationAtAll = new SqlCheck { Id = "SQLT-NO-GUIDANCE", Name = "SQLT-NO-GUIDANCE" };

            store.LoadCorpusTemplates(new[] { notAutoFixable, noOperation, noRemediationAtAll });

            Assert.True(store.IsPreviewOnlyCheck(notAutoFixable));
            Assert.True(store.IsPreviewOnlyCheck(noOperation));
            Assert.False(store.IsPreviewOnlyCheck(noRemediationAtAll)); // nothing to show
            Assert.Null(store.DescribeCorpusMalformed(notAutoFixable.Id));
            Assert.Null(store.DescribeCorpusMalformed(noOperation.Id));
            Assert.Equal(0, store.CorpusMalformedCount);
        }

        [Fact]
        public void TheMalformedSetAndTheOneClickSet_TogetherCoverEveryCorpusFixThatAskedForOne()
        {
            // The r1-07 shape stated positively: every check declaring auto_fixable with an
            // operation block ends up either registered as a one-click fix, deduped into one, or
            // named in the malformed list. Nothing falls through the middle unnoticed.
            var store = NewStore();
            var declared = new List<SqlCheck>
            {
                WellFormedCorpusFix("SQLT-VA-XP-CMDSHELL"),                       // registers
                Check("SQLT-CORPUS-MAXDOP", new CheckRemediation                   // dedupes
                {
                    AutoFixable = true,
                    Operation = new CheckRemediationOperation
                    {
                        OpKind = "sp_configure",
                        ConfigName = "max degree of parallelism",
                        ValueParam = "MaxDop.Value",
                    },
                }),
                Check("SQLT-BAD-KIND", new CheckRemediation                        // malformed
                {
                    AutoFixable = true,
                    Operation = new CheckRemediationOperation { OpKind = "create_index" },
                }),
            };

            store.LoadCorpusTemplates(declared);

            foreach (var check in declared)
            {
                var accountedFor = store.IsRegistered(check.Id)
                                   || store.All().Any(t => t.ResolvesCheckIds.Contains(check.Id))
                                   || store.IsCorpusMalformed(check.Id);
                Assert.True(accountedFor, $"'{check.Id}' asked for a one-click fix and is in no list at all.");
            }

            Assert.Equal(1, store.CorpusOneClickCount);
            Assert.Equal(1, store.CorpusDedupedCount);
            Assert.Equal(1, store.CorpusMalformedCount);
        }
    }
}
