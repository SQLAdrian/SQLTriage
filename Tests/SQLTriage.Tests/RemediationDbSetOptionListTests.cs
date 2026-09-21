/* In the name of God, the Merciful, the Compassionate */
/*
 * lane/safeoptionsql-comma (ruled 2026-09-02) — the db_set_option option_sql guard admits the
 * comma-separated MULTI-OPTION form of ALTER DATABASE ... SET.
 *
 * WHY THIS LANE EXISTS. The corpus wave ruled BPCHK-00800's fix to be the combined clause
 *   SET AUTO_UPDATE_STATISTICS ON, AUTO_UPDATE_STATISTICS_ASYNC ON
 * because the async-only remediation is a trap: applying it to a sync-OFF database leaves the
 * database still failing the check but NO LONGER VISIBLE to an async-off offenders query
 * (proved live on .\old2017 by the corpus-wave builder). The combined statement fixes both arms
 * in one auto-committed ALTER. The renderer refused it: SafeOptionSql's character class carries
 * no comma, so the corpus could not ship an operation the app would render.
 *
 * WHAT THIS FILE PINS. That the widening is a LIST OF SAFE ELEMENTS and not a looser regex:
 *   - the comma-free door is byte-for-byte the old charset guard (regression sweep, below);
 *   - the comma door requires a literal SET head, 2..4 elements, and every element to be a
 *     single option keyword followed by ON or OFF — nothing else gets in;
 *   - the inverter is DELIBERATELY not widened (see NotInvertible tests for the reason).
 */

using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Parser;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Remediation;
using Xunit;

namespace SQLTriage.Tests
{
    public class RemediationDbSetOptionListTests
    {
        /// <summary>The exact clause Adrian ruled for BPCHK-00800 on 2026-09-02.</summary>
        private const string Bpchk00800OptionSql =
            "SET AUTO_UPDATE_STATISTICS ON, AUTO_UPDATE_STATISTICS_ASYNC ON";

        // ── 1. The ruled clause renders ────────────────────────────────────────────────────

        [Fact]
        public void Bpchk00800CombinedClause_RendersOneAlterDatabase()
        {
            Assert.True(RemediationOpRenderer.TryRenderDbSetOption(
                "Payments", Bpchk00800OptionSql, out var sql, out var err));
            Assert.Equal(
                "ALTER DATABASE [Payments] SET AUTO_UPDATE_STATISTICS ON, AUTO_UPDATE_STATISTICS_ASYNC ON;",
                sql);
            Assert.Equal(string.Empty, err);
        }

        // ── 2. The list door: what it admits ───────────────────────────────────────────────

        [Theory]
        // two elements, the shipped shape
        [InlineData("SET AUTO_UPDATE_STATISTICS ON, AUTO_UPDATE_STATISTICS_ASYNC ON")]
        // mixed values
        [InlineData("SET AUTO_CLOSE OFF, AUTO_SHRINK OFF")]
        [InlineData("SET AUTO_CLOSE OFF, AUTO_CREATE_STATISTICS ON")]
        // three and four elements (the cap)
        [InlineData("SET AUTO_CLOSE OFF, AUTO_SHRINK OFF, AUTO_CREATE_STATISTICS ON")]
        [InlineData("SET AUTO_CLOSE OFF, AUTO_SHRINK OFF, AUTO_CREATE_STATISTICS ON, AUTO_UPDATE_STATISTICS ON")]
        // whitespace tolerance around the separator, and a leading/trailing trim of the whole clause
        [InlineData("SET AUTO_CLOSE OFF,AUTO_SHRINK OFF")]
        [InlineData("SET AUTO_CLOSE OFF ,  AUTO_SHRINK OFF")]
        [InlineData("  SET AUTO_CLOSE OFF, AUTO_SHRINK OFF  ")]
        // case is a T-SQL irrelevance, not a safety property — the shape is what is guarded
        [InlineData("set auto_close off, auto_shrink off")]
        public void MultiOptionList_OfSimpleToggles_IsAdmitted(string optionSql)
        {
            Assert.True(RemediationOpRenderer.TryRenderDbSetOption("MyDb", optionSql, out var sql, out var err),
                $"expected ACCEPT for: {optionSql} (error was: {err})");
            // The render is the clause verbatim (trimmed) — the guard vets, it never rewrites.
            Assert.Equal($"ALTER DATABASE [MyDb] {optionSql.Trim()};", sql);
        }

        // ── 3. The list door: what it refuses ──────────────────────────────────────────────

        [Theory]
        // injection shapes riding the new separator
        [InlineData("SET AUTO_CLOSE OFF, AUTO_SHRINK OFF; DROP TABLE x")]
        [InlineData("SET AUTO_CLOSE OFF; DROP TABLE x, AUTO_SHRINK OFF")]
        [InlineData("SET AUTO_CLOSE OFF, EXEC xp_cmdshell 'net user'")]
        [InlineData("SET AUTO_CLOSE OFF, AUTO_SHRINK OFF -- and then")]
        [InlineData("SET AUTO_CLOSE OFF, AUTO_SHRINK OFF /* and then */")]
        [InlineData("SET AUTO_CLOSE OFF, AUTO_SHRINK OFF' ")]
        [InlineData("SET AUTO_CLOSE OFF, [master]..sp_x")]
        [InlineData("SET AUTO_CLOSE OFF, AUTO_SHRINK OFF\r\nDROP TABLE x")]
        // trailing garbage after a well-formed list
        [InlineData("SET AUTO_CLOSE OFF, AUTO_SHRINK OFF EXTRA")]
        [InlineData("SET AUTO_CLOSE OFF, AUTO_SHRINK OFF WITH NO_WAIT")]
        // empty / degenerate elements
        [InlineData("SET AUTO_CLOSE OFF,")]
        [InlineData("SET ,AUTO_CLOSE OFF")]
        [InlineData("SET AUTO_CLOSE OFF,,AUTO_SHRINK OFF")]
        [InlineData("SET AUTO_CLOSE OFF, ")]
        [InlineData("SET ,")]
        [InlineData(",")]
        // no SET head — the list door demands one, unlike the historical single door
        [InlineData("AUTO_CLOSE OFF, AUTO_SHRINK OFF")]
        [InlineData("SETAUTO_CLOSE OFF, AUTO_SHRINK OFF")]
        // an element that is not <KEYWORD> ON|OFF
        [InlineData("SET AUTO_CLOSE OFF, AUTO_SHRINK")]
        [InlineData("SET AUTO_CLOSE OFF, AUTO_SHRINK MAYBE")]
        [InlineData("SET AUTO_CLOSE OFF, 1 ON")]
        [InlineData("SET AUTO_CLOSE OFF, TARGET_RECOVERY_TIME = 60 SECONDS")]
        // compound clauses are NOT admitted inside a list even though each is fine on its own
        [InlineData("SET AUTO_CLOSE OFF, QUERY_STORE = ON (OPERATION_MODE = READ_WRITE)")]
        [InlineData("SET AUTO_CREATE_STATISTICS ON (INCREMENTAL = ON), AUTO_CLOSE OFF")]
        // over the element cap (5)
        [InlineData("SET A ON, B ON, C ON, D ON, E ON")]
        public void MultiOptionList_UnsafeOrMalformed_IsRefused(string optionSql)
        {
            Assert.False(RemediationOpRenderer.TryRenderDbSetOption("MyDb", optionSql, out var sql, out var err),
                $"expected REFUSE for: {optionSql} (it rendered as: {sql})");
            Assert.Equal(string.Empty, sql);
            Assert.False(string.IsNullOrEmpty(err));
        }

        [Fact]
        public void MultiOptionList_OverTheLengthCap_IsRefused()
        {
            // 200 characters is the historical cap and the list door does not raise it.
            var longName = new string('A', 190);
            Assert.False(RemediationOpRenderer.TryRenderDbSetOption(
                "MyDb", $"SET {longName} ON, {longName} OFF", out var sql, out _));
            Assert.Equal(string.Empty, sql);
        }

        // ── 4. Regression sweep: every option_sql the corpus ships today still renders ─────
        //
        // Enumerated read-only from C:/GitHub/corpus-ag @ 941b5c89 (master), with
        //   grep -rn "option_sql" corpus-v2/checks --include=*.md
        // which returns 10 occurrences / 9 distinct values. The 10th InlineData is the
        // "SET PAGE_VERIFY CHECKSUM WITH NO_WAIT" shape named in RemediationOpRenderer's own
        // doc comment as an observed corpus shape — pinned so the comment stays true.
        [Theory]
        [InlineData("SET DB_CHAINING OFF")]
        [InlineData("SET TRUSTWORTHY OFF")]
        [InlineData("SET AUTO_CLOSE OFF")]
        [InlineData("SET AUTO_SHRINK OFF")]
        [InlineData("SET AUTO_UPDATE_STATISTICS ON")]
        [InlineData("SET AUTO_CREATE_STATISTICS ON")]
        [InlineData("SET PAGE_VERIFY CHECKSUM")]
        [InlineData("SET PAGE_VERIFY CHECKSUM WITH NO_WAIT")]
        [InlineData("SET AUTO_CREATE_STATISTICS ON (INCREMENTAL = ON)")]
        [InlineData("SET QUERY_STORE = ON (OPERATION_MODE = READ_WRITE)")]
        public void EveryShippedCorpusOptionSql_StillRenders(string optionSql)
        {
            Assert.True(RemediationOpRenderer.TryRenderDbSetOption("MyDb", optionSql, out var sql, out var err),
                $"REGRESSION — shipped corpus option_sql now refused: {optionSql} ({err})");
            Assert.Equal($"ALTER DATABASE [MyDb] {optionSql};", sql);
        }

        // Comma-free strings the old guard refused must STILL be refused: the comma door is
        // additive, it does not re-judge anything the single door already decided.
        [Theory]
        [InlineData("SET X OFF; DROP TABLE y; --")]
        [InlineData("SET X OFF'")]
        [InlineData("SET X OFF\r\nDROP TABLE y")]
        [InlineData("")]
        [InlineData("   ")]
        public void CommaFreeRefusals_AreUnchanged(string optionSql)
        {
            Assert.False(RemediationOpRenderer.TryRenderDbSetOption("MyDb", optionSql, out var sql, out _));
            Assert.Equal(string.Empty, sql);
        }

        // ── 5. The inverter is deliberately NOT widened ────────────────────────────────────
        //
        // A DbSetOption rollback derives the prior state from offenders-query membership: an
        // offender is, by definition, not at the target, so its prior state is the opposite.
        // That premise holds for ONE toggle and breaks for a list. A database can be an offender
        // because AUTO_UPDATE_STATISTICS is OFF while AUTO_UPDATE_STATISTICS_ASYNC is ALREADY ON;
        // inverting both would set the async option to a state it was never in. That is a
        // fabricated inverse, so the executor must keep saying "no rollback available" here.

        [Fact]
        public void CombinedClause_IsNotInvertible_NoFabricatedInverse()
        {
            Assert.False(RemediationOpRenderer.TryInvertBooleanOptionSql(Bpchk00800OptionSql, out var inverted));
            Assert.Equal(string.Empty, inverted);
        }

        [Fact]
        public void CombinedClause_ReversibilityProse_SaysThereIsNoInverse()
        {
            var r = RemediationRollbackProse.ForDbSetOption(
                new RemediationTemplate { Key = "K", Reversible = true }, Bpchk00800OptionSql);
            Assert.Equal(false, r.CanRollBack);
            Assert.StartsWith(RemediationRollbackProse.NoRollbackMarker, r.Sentence);
        }

        [Fact]
        public void SingleToggle_StillInverts()
        {
            Assert.True(RemediationOpRenderer.TryInvertBooleanOptionSql("SET AUTO_UPDATE_STATISTICS ON", out var inv));
            Assert.Equal("SET AUTO_UPDATE_STATISTICS OFF", inv);
        }

        // ── 6. End-to-end, offline: corpus text → parser → REAL store → render → classify ──

        [Fact]
        public void CombinedClause_LoadsThroughStore_RendersAndClassifiesAsRemediation()
        {
            const string checkId = "SQLT-BPCHK-00800-AUTO-UPDATE-STATS-ASYNC";
            var doc =
$@"---
id: {checkId}
title: t
category: Performance
severity: Medium
source:
  framework: sp_Blitz
  ref: 48
applicability:
  engine_editions: [SqlServer]
  scope: instance
result_contract: verdict
framework_mappings: []
provenance: custom
remediation:
  auto_fixable: true
  risk_class: standard
  operation:
    op_kind: db_set_option
    option_sql: ""{Bpchk00800OptionSql}""
    offenders_query: ""SELECT name FROM sys.databases WHERE is_auto_update_stats_async_on = 0;""
---

## Intent
d

## Query
```sql
SELECT CASE WHEN 1=0 THEN 1 ELSE 0 END
```
".Replace("\r\n", "\n");

            var tmp = Path.Combine(Path.GetTempPath(), $"safeopt-{System.Guid.NewGuid():N}.md");
            File.WriteAllText(tmp, doc);
            SqlCheck check;
            try
            {
                check = SqlCheckBuilder.Build(
                    SourceCatalogueLoader.ParseMappingFromText(File.ReadAllText(tmp), tmp), tmp,
                    sqlFallbackBody: null, seenIds: new HashSet<string>()).Check;
            }
            finally { File.Delete(tmp); }

            // (a) the parser carried the comma through verbatim
            Assert.Equal(Bpchk00800OptionSql, check.Remediation?.Operation?.OptionSql);

            // (b) the REAL store registers it as a one-click template under the check id
            var store = new RemediationTemplateStore(NullLogger<RemediationTemplateStore>.Instance);
            store.LoadCorpusTemplates(new[] { check });
            Assert.True(store.IsRegistered(checkId));
            Assert.Equal(1, store.CorpusOneClickCount);
            Assert.Equal(0, store.CorpusMalformedCount);

            var template = store.TryGet(checkId)!;
            Assert.Equal(RemediationOpKind.DbSetOption, template.Operation!.OpKind);

            // (c) the shipped renderer turns it into one ALTER for one database
            Assert.True(RemediationOpRenderer.TryRenderDbSetOption(
                "Payments", template.Operation.OptionSql!, out var rendered, out var renderErr), renderErr);
            Assert.Equal(
                "ALTER DATABASE [Payments] SET AUTO_UPDATE_STATISTICS ON, AUTO_UPDATE_STATISTICS_ASYNC ON;",
                rendered);

            // (d) Route A's exec-surface guard lets it through
            Assert.True(RemediationExecGuard.Allows(rendered, RemediationExecGuard.RenderedFix, out var guardErr), guardErr);

            // (e) and the safety validator promotes it to Remediation under the store's own key
            //     set — the same three arguments RemediationRunner passes.
            Assert.Equal(
                SqlClassification.Remediation,
                SqlSafetyValidator.Classify(rendered, new RemediationContext(template.Key), store.RegisteredKeys()));

            // (f) unauthorised text stays Blocked — the widening did not open a text-only door
            Assert.Equal(SqlClassification.Blocked, SqlSafetyValidator.Classify(rendered));
        }
    }
}
