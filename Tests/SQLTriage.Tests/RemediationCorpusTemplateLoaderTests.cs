/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Task #39 — RemediationTemplateStore.LoadCorpusTemplates: parses SqlCheck.Remediation into
    /// one-click templates, dedupes by Operation identity (preferring the built-in), and never
    /// aborts on a malformed entry.
    /// </summary>
    public class RemediationCorpusTemplateLoaderTests
    {
        private static RemediationTemplateStore NewStore() =>
            new(NullLogger<RemediationTemplateStore>.Instance);

        private static SqlCheck Check(string id, CheckRemediation remediation) => new()
        {
            Id = id,
            Name = id,
            Remediation = remediation,
        };

        // ── New one-click sp_configure (not covered by any built-in) ────────

        [Fact]
        public void NewSpConfigure_NotCoveredByBuiltIn_RegistersUnderCheckId()
        {
            var store = NewStore();
            var check = Check("SQLT-VA-XP-CMDSHELL", new CheckRemediation
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

            store.LoadCorpusTemplates(new[] { check });

            Assert.True(store.IsRegistered("SQLT-VA-XP-CMDSHELL"));
            var t = store.TryGet("SQLT-VA-XP-CMDSHELL")!;
            Assert.Equal(RemediationOpKind.SpConfigure, t.Operation!.OpKind);
            Assert.Equal(0, t.Operation.ValueFixed);
            Assert.Equal(1, store.CorpusOneClickCount);
            Assert.Equal(0, store.CorpusDedupedCount);
        }

        // ── Dedup: corpus MAXDOP check collides with the built-in — prefer built-in ──

        [Fact]
        public void CorpusMaxDop_DedupesAgainstBuiltIn_AttachesCheckIdForLinkage()
        {
            var store = NewStore();
            var check = Check("SQLT-BPCHK-00220-PARALLELISM-MAXDOP", new CheckRemediation
            {
                AutoFixable = true,
                Operation = new CheckRemediationOperation
                {
                    OpKind = "sp_configure",
                    ConfigName = "max degree of parallelism", // same setting as the built-in MAXDOP
                    AdvancedOption = true,
                    ValueParam = "MaxDop",
                    MinValue = 0,
                    MaxValue = 64,
                },
            });

            store.LoadCorpusTemplates(new[] { check });

            // No NEW template registered under the check id — the built-in is preferred.
            Assert.False(store.IsRegistered("SQLT-BPCHK-00220-PARALLELISM-MAXDOP"));
            var builtin = store.TryGet("MAXDOP")!;
            Assert.Contains("SQLT-BPCHK-00220-PARALLELISM-MAXDOP", builtin.ResolvesCheckIds);
            Assert.Equal(0, store.CorpusOneClickCount);
            Assert.Equal(1, store.CorpusDedupedCount);
        }

        // ── New db_set_option (net-new op kind — no built-in exists yet) ─────

        [Fact]
        public void DbSetOption_RegistersUnderCheckId_NoDedupTarget()
        {
            var store = NewStore();
            var check = Check("SQLT-VA-DB-CHAINING", new CheckRemediation
            {
                AutoFixable = true,
                Reversible = true,
                Operation = new CheckRemediationOperation
                {
                    OpKind = "db_set_option",
                    OptionSql = "SET DB_CHAINING OFF",
                    OffendersQuery = "SELECT name FROM sys.databases WHERE is_db_chaining_on = 1;",
                },
            });

            store.LoadCorpusTemplates(new[] { check });

            var t = store.TryGet("SQLT-VA-DB-CHAINING");
            Assert.NotNull(t);
            Assert.Equal(RemediationKind.Configuration, t!.Kind);
            Assert.Equal(RemediationOpKind.DbSetOption, t.Operation!.OpKind);
            Assert.Equal("SET DB_CHAINING OFF", t.Operation.OptionSql);
            Assert.True(t.Reversible);
            Assert.Equal(1, store.CorpusOneClickCount);
        }

        // ── Two db_set_option checks with the SAME option_sql+offenders_query dedupe to each other ──

        [Fact]
        public void TwoDbSetOptionChecks_SameOperationIdentity_SecondDedupesAgainstFirst()
        {
            var store = NewStore();
            var remA = new CheckRemediation
            {
                AutoFixable = true,
                Operation = new CheckRemediationOperation
                {
                    OpKind = "db_set_option",
                    OptionSql = "SET TRUSTWORTHY OFF",
                    OffendersQuery = "SELECT name FROM sys.databases WHERE is_trustworthy_on = 1;",
                },
            };
            var remB = new CheckRemediation
            {
                AutoFixable = true,
                Operation = new CheckRemediationOperation
                {
                    OpKind = "db_set_option",
                    OptionSql = "SET TRUSTWORTHY OFF",
                    OffendersQuery = "SELECT name FROM sys.databases WHERE is_trustworthy_on = 1;",
                },
            };
            store.LoadCorpusTemplates(new[] { Check("SQLT-GAPFIL-00930", remA), Check("SQLT-VA-TRUSTWORTHY-DB", remB) });

            Assert.True(store.IsRegistered("SQLT-GAPFIL-00930"));   // first wins
            Assert.False(store.IsRegistered("SQLT-VA-TRUSTWORTHY-DB")); // second dedupes
            Assert.Contains("SQLT-VA-TRUSTWORTHY-DB", store.TryGet("SQLT-GAPFIL-00930")!.ResolvesCheckIds);
            Assert.Equal(1, store.CorpusOneClickCount);
            Assert.Equal(1, store.CorpusDedupedCount);
        }

        // ── Malformed: db_set_option without offenders_query never registers, never throws ──

        [Fact]
        public void MalformedDbSetOption_MissingOffendersQuery_NeverRegistered_NeverThrows()
        {
            var store = NewStore();
            var check = Check("SQLT-TEST-MALFORMED", new CheckRemediation
            {
                AutoFixable = true,
                Operation = new CheckRemediationOperation
                {
                    OpKind = "db_set_option",
                    OptionSql = "SET DB_CHAINING OFF",
                    OffendersQuery = null, // malformed — hard contract violation
                },
            });

            var ex = Record.Exception(() => store.LoadCorpusTemplates(new[] { check }));
            Assert.Null(ex);
            Assert.False(store.IsRegistered("SQLT-TEST-MALFORMED"));
            Assert.Equal(1, store.CorpusMalformedCount);
            // Shipped built-ins are unaffected by one malformed corpus entry.
            Assert.True(store.IsRegistered("MAXDOP"));
        }

        // ── Not auto_fixable / no Operation at all -> simply skipped (preview-only tier) ──

        [Fact]
        public void NotAutoFixable_OrNoOperation_IsSkipped_NotRegistered()
        {
            var store = NewStore();
            var previewOnly = Check("SQLT-PREVIEW-ONLY", new CheckRemediation { AutoFixable = false });
            var noOp = Check("SQLT-NO-OP-BLOCK", new CheckRemediation { AutoFixable = true, Operation = null });

            store.LoadCorpusTemplates(new[] { previewOnly, noOp });

            Assert.False(store.IsRegistered("SQLT-PREVIEW-ONLY"));
            Assert.False(store.IsRegistered("SQLT-NO-OP-BLOCK"));
            Assert.Equal(0, store.CorpusOneClickCount);
        }

        // ── Idempotent reload: calling twice doesn't duplicate or leak entries ──

        [Fact]
        public void LoadCorpusTemplates_CalledTwice_IsIdempotent()
        {
            var store = NewStore();
            var check = Check("SQLT-VA-DB-CHAINING", new CheckRemediation
            {
                AutoFixable = true,
                Operation = new CheckRemediationOperation
                {
                    OpKind = "db_set_option",
                    OptionSql = "SET DB_CHAINING OFF",
                    OffendersQuery = "SELECT name FROM sys.databases WHERE is_db_chaining_on = 1;",
                },
            });

            store.LoadCorpusTemplates(new[] { check });
            var countAfterFirst = store.Count;
            store.LoadCorpusTemplates(new[] { check });

            Assert.Equal(countAfterFirst, store.Count);
            Assert.True(store.IsRegistered("SQLT-VA-DB-CHAINING"));
        }

        [Fact]
        public void LoadCorpusTemplates_NullChecks_NoOp()
        {
            var store = NewStore();
            var before = store.Count;
            store.LoadCorpusTemplates(null);
            Assert.Equal(before, store.Count);
        }
    }
}
