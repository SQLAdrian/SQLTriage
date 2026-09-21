/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using System.IO;
using System.Linq;
using SQLTriage.Data.Parser;
using Xunit;

namespace SQLTriage.Tests.Parser
{
    /// <summary>
    /// SqlCheckBuilder against the V2 Markdown+frontmatter contract
    /// (972cb27 migration; fixtures realigned 2026-06-12).
    /// V2 contract pinned here: SQL comes from the Markdown `## Query` block
    /// (synthetic 'query_sql') or the .sql sidecar; description from `## Intent`;
    /// source is a map whose `ref` feeds Source/LegacyIds/Sources;
    /// `score_weight` is ignored (weight = severity baseline × effort bucket);
    /// the V1 `sources:` list contract was retired with the migration.
    /// </summary>
    public class SqlCheckBuilderTests
    {
        private static string FixturePath(string name) =>
            Path.Combine(AppContext.BaseDirectory, "Parser", "Fixtures", name);

        // V2 docs (Markdown+frontmatter) must go through the production
        // document parser — YamlCheckParser.LoadMapping is the raw-yaml
        // loader and does not strip frontmatter or extract `## Query`.
        private static IReadOnlyDictionary<string, YamlDotNet.RepresentationModel.YamlNode> LoadDoc(string path)
            => SourceCatalogueLoader.ParseMappingFromText(File.ReadAllText(path), path);

        private static IReadOnlyDictionary<string, YamlDotNet.RepresentationModel.YamlNode> LoadFixture(string name)
            => LoadDoc(FixturePath(name));

        /// <summary>Minimal V2 Markdown doc. Frontmatter must start at byte 0 ("---\n").</summary>
        private static string V2Doc(string id, string severity = "Medium",
            string extraFrontmatter = "", bool includeQuery = true)
        {
            var query = includeQuery
                ? "\n## Query\n```sql\nSELECT CASE WHEN 1=0 THEN 1 ELSE 0 END\n```\n"
                : "\n";
            // Newlines in a verbatim ($@) string are whatever the SOURCE FILE has on
            // disk — CRLF when git checks this out with autocrlf (e.g. CI runners). The
            // line-stripping tests below (.Replace("title: t\n", "")) assume LF, so
            // normalise to LF here. Without this the strips silently no-op on CRLF
            // checkouts and the "missing field" / "no ref" assertions flip. 2026-06-30.
            return
$@"---
id: {id}
title: t
category: Performance
severity: {severity}
source:
  framework: sp_Blitz
  ref: 48
applicability:
  engine_editions: [SqlServer]
  scope: instance
result_contract: verdict
framework_mappings: []
provenance: custom
{extraFrontmatter}---

## Intent
d
{query}".Replace("\r\n", "\n");
        }

        private static SqlCheckBuilder.BuildResult BuildInline(string doc, string? sqlFallback = null)
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"v2-build-{System.Guid.NewGuid():N}.md");
            File.WriteAllText(tmp, doc);
            try
            {
                return SqlCheckBuilder.Build(
                    LoadDoc(tmp), tmp,
                    sqlFallbackBody: sqlFallback, seenIds: new HashSet<string>());
            }
            finally { File.Delete(tmp); }
        }

        // ── Host-probe method: no SQL, carries a probe_key ──────────────────

        [Fact]
        public void Build_hostProbe_check_parsesWithoutSql_andCarriesProbeKey()
        {
            var doc = V2Doc("SQLT-CORE-00099", includeQuery: false,
                extraFrontmatter: "method: host-probe\nprobe_key: host.powerplan\n");
            var c = BuildInline(doc).Check;
            Assert.Equal("host-probe", c.Method);
            Assert.Equal("host.powerplan", c.ProbeKey);
            Assert.True(string.IsNullOrEmpty(c.SqlQuery)); // no SQL required for a probe check
        }

        [Fact]
        public void Build_hostProbe_withoutProbeKey_throws()
        {
            var doc = V2Doc("SQLT-CORE-00098", includeQuery: false,
                extraFrontmatter: "method: host-probe\n");
            Assert.ThrowsAny<System.Exception>(() => BuildInline(doc));
        }

        [Fact]
        public void Build_tsqlCheck_withNoQuery_stillThrows()
        {
            // The host-probe relaxation must NOT weaken the SQL requirement for ordinary checks.
            var doc = V2Doc("SQLT-CORE-00097", includeQuery: false);
            Assert.ThrowsAny<System.Exception>(() => BuildInline(doc));
        }

        [Fact]
        public void Build_minimal_valid_yields_populated_SqlCheck()
        {
            var seen = new HashSet<string>();
            var result = SqlCheckBuilder.Build(
                LoadFixture("minimal-valid.yaml"),
                FixturePath("minimal-valid.yaml"),
                sqlFallbackBody: null,
                seenIds: seen);

            var c = result.Check;
            Assert.Equal("SQLT-CORE-00010", c.Id);
            Assert.Equal("Minimal valid check fixture", c.Name);
            Assert.Equal("Performance", c.Category);
            Assert.Equal("Medium", c.Severity);
            Assert.Equal("SELECT CASE WHEN 1=0 THEN 1 ELSE 0 END", c.SqlQuery);
            Assert.Equal("Smallest viable V2 Markdown+frontmatter check for parser tests.", c.Description);
            Assert.Equal(0, c.ExpectedValue);            // §3.1 default
            Assert.Equal("Binary", c.ExecutionType);     // §3.2: CASE-WHEN binary shape
            Assert.Equal(10, c.ScoreWeight);             // §3.3: Medium × low/unknown = 10
            Assert.False(c.IsBad);                       // §3.4 default
            Assert.True(c.Enabled);
            Assert.Equal("PassFail", c.ResultInterpretation); // result_contract: verdict
            Assert.Equal("10", c.Source);                // source.ref promoted to Source

            // derivation infos emitted for every defaulted field
            Assert.Equal(4, result.Derivations.Count);
            Assert.Contains(result.Derivations, d => d.Field == "ExpectedValue");
            Assert.Contains(result.Derivations, d => d.Field == "ExecutionType");
            Assert.Contains(result.Derivations, d => d.Field == "ScoreWeight");
            Assert.Contains(result.Derivations, d => d.Field == "IsBad");
        }

        [Fact]
        public void Build_collision_detected_on_second_call_same_id()
        {
            var seen = new HashSet<string>();
            SqlCheckBuilder.Build(LoadFixture("minimal-valid.yaml"),
                FixturePath("minimal-valid.yaml"), null, seen);

            var ex = Assert.Throws<SourceParseException>(() =>
                SqlCheckBuilder.Build(LoadFixture("minimal-valid.yaml"),
                    FixturePath("minimal-valid.yaml"), null, seen));
            Assert.Contains("collision", ex.Message);
        }

        [Fact]
        public void Build_falls_back_to_sqlBody_when_markdown_has_no_query_block()
        {
            var result = BuildInline(V2Doc("SQLT-CORE-00020", includeQuery: false),
                sqlFallback: "SELECT 99 AS result");
            Assert.Equal("SELECT 99 AS result", result.Check.SqlQuery);
        }

        [Fact]
        public void Build_throws_when_both_query_block_and_fallback_absent()
        {
            var ex = Assert.Throws<SourceParseException>(() =>
                BuildInline(V2Doc("SQLT-CORE-00021", includeQuery: false), sqlFallback: null));
            Assert.Contains("no SQL", ex.Message);
        }

        [Fact]
        public void Build_throws_on_missing_required_field()
        {
            // 'title' deliberately stripped from the frontmatter
            var doc = V2Doc("SQLT-CORE-00022").Replace("title: t\n", "");
            var ex = Assert.Throws<SourceParseException>(() => BuildInline(doc));
            Assert.Contains("title", ex.Message);
        }

        [Fact]
        public void Build_loads_real_corpusV2_check_and_derives_ResultInterpretation()
        {
            // Full build path against a real corpus-v2 Markdown check: the
            // result_contract:verdict contract must surface as a non-null
            // ResultInterpretation so verdict SQL routes through the text
            // executor path (the 2026-05-21 runtime bug class).
            var corpus = FindCorpusV2Checks();
            if (corpus is null) return;  // corpus repo not on this machine — skip

            var mdPath = Directory.GetFiles(corpus, "*.md").OrderBy(p => p).FirstOrDefault();
            if (mdPath is null) return;

            var mapping = LoadDoc(mdPath);
            var result = SqlCheckBuilder.Build(mapping, mdPath, null, new HashSet<string>());

            Assert.NotNull(result.Check.ResultInterpretation);
            Assert.Contains("Pass", result.Check.ResultInterpretation);
            Assert.False(string.IsNullOrWhiteSpace(result.Check.SqlQuery));
        }

        internal static string? FindCorpusV2Checks()
        {
            // corpus repo is a sibling working tree alongside this one (probed by name below)
            var d = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && d != null; i++)
            {
                var probe = Path.Combine(d, "corpus-ag", "corpus-v2", "checks");
                if (Directory.Exists(probe) && Directory.GetFiles(probe, "*.md").Any())
                    return probe;
                d = Path.GetDirectoryName(d);
            }
            return null;
        }

        [Fact]
        public void Build_throws_on_old_check_id_form()
        {
            var ex = Assert.Throws<SourceParseException>(() => BuildInline(V2Doc("BLITZ_001")));
            Assert.Contains("v2 regex", ex.Message);
        }

        // ── remediation_effort_hours → EffortHours + ScoreWeight bucket (PL11) ──

        [Fact]
        public void RemediationEffortHours_populates_EffortHours()
        {
            var r = BuildInline(V2Doc("SQLT-CORE-00110",
                extraFrontmatter: "remediation_effort_hours: 0.5\n"));
            Assert.Equal(0.5, r.Check.EffortHours);
        }

        [Fact]
        public void RemediationEffortHours_absent_leaves_EffortHours_zero()
        {
            var r = BuildInline(V2Doc("SQLT-CORE-00111"));
            Assert.Equal(0, r.Check.EffortHours);
        }

        [Theory]
        [InlineData(0.5, 15)]   // <2h → low ×1.0  → High(15)×1.0 = 15
        [InlineData(2, 22)]     // 2–<4h → medium ×1.5 → 15×1.5 = 22.5 → 22 (banker's)
        [InlineData(8, 30)]     // ≥4h → high ×2.0  → 15×2.0 = 30
        public void RemediationEffortHours_buckets_into_ScoreWeight_multiplier(double hours, int expectedWeight)
        {
            var r = BuildInline(V2Doc("SQLT-CORE-00112", severity: "High",
                extraFrontmatter: $"remediation_effort_hours: {hours.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n"));
            Assert.Equal(expectedWeight, r.Check.ScoreWeight);
        }

        [Fact]
        public void Score_weight_frontmatter_is_ignored_in_v2()
        {
            // V2 retired authored score_weight — weight always derives from
            // severity × effort bucket. Pin the ignore so a silent re-read of
            // the field is a conscious change.
            var r = BuildInline(V2Doc("SQLT-CORE-00113", severity: "High",
                extraFrontmatter: "score_weight: 7\nremediation_effort_hours: 8\n"));
            Assert.Equal(30, r.Check.ScoreWeight);   // not 7
        }

        // ── source map → Source / LegacyIds / Sources ──

        [Fact]
        public void Source_ref_populates_Source_LegacyIds_and_Sources()
        {
            var r = BuildInline(V2Doc("SQLT-CORE-00100"));
            Assert.Equal("48", r.Check.Source);
            Assert.Single(r.Check.Sources);
            Assert.Equal("48", r.Check.Sources[0]);
            Assert.NotNull(r.Check.LegacyIds);
            Assert.Equal("48", Assert.Single(r.Check.LegacyIds!));
        }

        [Fact]
        public void Source_without_ref_leaves_Source_null_and_Sources_empty()
        {
            var doc = V2Doc("SQLT-CORE-00101").Replace("  ref: 48\n", "");
            var r = BuildInline(doc);
            Assert.Null(r.Check.Source);
            Assert.Empty(r.Check.Sources);
        }

        // ── remediation block (task #39 — corpus-fed Remediation UI, SCHEMA.md "The remediation
        // block" / "The remediation.operation sub-block") ──────────────────────────────────────

        [Fact]
        public void NoRemediationBlock_LeavesRemediationNull()
        {
            var r = BuildInline(V2Doc("SQLT-CORE-00200"));
            Assert.Null(r.Check.Remediation);
        }

        [Fact]
        public void Remediation_WithoutOperation_IsPreviewOnly_RecommendedActionFromMarkdownSection()
        {
            // The bug this fix closes: the frontmatter `remediation:` mapping and the markdown
            // `## Remediation` section used to share ONE map key ("remediation"), so the markdown
            // parser's overwrite clobbered the structured frontmatter before the builder ever saw
            // it. Both must now survive independently.
            var doc = V2Doc("SQLT-CORE-00201",
                extraFrontmatter:
                    "remediation:\n" +
                    "  auto_fixable: false\n" +
                    "  risk_class: standard\n" +
                    "  method: manual\n" +
                    "  cmdlet_or_template: \"Review and fix manually.\"\n" +
                    "  reversible: true\n")
                .Replace("\n## Intent\nd\n", "\n## Intent\nd\n\n## Remediation\nDo the manual fix.\n");
            var r = BuildInline(doc);

            Assert.NotNull(r.Check.Remediation);
            Assert.False(r.Check.Remediation!.AutoFixable);
            Assert.Null(r.Check.Remediation.Operation); // no operation: sub-block -> preview-only tier
            Assert.Equal("Review and fix manually.", r.Check.Remediation.CmdletOrTemplate);
            // RecommendedAction still comes from the markdown '## Remediation' section (renamed
            // synthetic field), independently of the structured block above.
            Assert.Contains("Do the manual fix.", r.Check.RecommendedAction);
        }

        [Fact]
        public void Remediation_SpConfigure_ValueParam_ParsesOperation()
        {
            var doc = V2Doc("SQLT-CORE-00202",
                extraFrontmatter:
                    "remediation:\n" +
                    "  auto_fixable: true\n" +
                    "  risk_class: standard\n" +
                    "  operation:\n" +
                    "    op_kind: sp_configure\n" +
                    "    config_name: \"blocked process threshold (s)\"\n" +
                    "    advanced_option: true\n" +
                    "    value_param: BlockedProcThreshold\n" +
                    "    min_value: 5\n" +
                    "    max_value: 86400\n");
            var r = BuildInline(doc);

            Assert.NotNull(r.Check.Remediation);
            Assert.True(r.Check.Remediation!.AutoFixable);
            var op = r.Check.Remediation.Operation;
            Assert.NotNull(op);
            Assert.Equal("sp_configure", op!.OpKind);
            Assert.Equal("blocked process threshold (s)", op.ConfigName);
            Assert.Equal("BlockedProcThreshold", op.ValueParam);
            Assert.Equal(5, op.MinValue);
            Assert.Equal(86400, op.MaxValue);
            Assert.Null(op.ValueFixed);
        }

        [Fact]
        public void Remediation_SpConfigure_ValueFixed_ParsesOperation()
        {
            var doc = V2Doc("SQLT-CORE-00203",
                extraFrontmatter:
                    "remediation:\n" +
                    "  auto_fixable: true\n" +
                    "  operation:\n" +
                    "    op_kind: sp_configure\n" +
                    "    config_name: \"xp_cmdshell\"\n" +
                    "    advanced_option: true\n" +
                    "    value_fixed: 0\n");
            var r = BuildInline(doc);

            var op = r.Check.Remediation?.Operation;
            Assert.NotNull(op);
            Assert.Equal(0, op!.ValueFixed);
            Assert.Null(op.ValueParam);
        }

        [Fact]
        public void Remediation_DbSetOption_WellFormed_ParsesOperation()
        {
            var doc = V2Doc("SQLT-CORE-00204",
                extraFrontmatter:
                    "remediation:\n" +
                    "  auto_fixable: true\n" +
                    "  operation:\n" +
                    "    op_kind: db_set_option\n" +
                    "    option_sql: \"SET DB_CHAINING OFF\"\n" +
                    "    offenders_query: \"SELECT name FROM sys.databases WHERE is_db_chaining_on = 1;\"\n");
            var r = BuildInline(doc);

            var op = r.Check.Remediation?.Operation;
            Assert.NotNull(op);
            Assert.Equal("db_set_option", op!.OpKind);
            Assert.Equal("SET DB_CHAINING OFF", op.OptionSql);
            Assert.Contains("is_db_chaining_on", op.OffendersQuery);
        }

        [Fact]
        public void Remediation_DbSetOption_MissingOffendersQuery_DegradesToPreviewOnly_ButCheckStillLoads()
        {
            // The hard contract rule: db_set_option REQUIRES offenders_query. Malformed here must
            // NOT throw — the check still loads (fail-tolerant), just without a one-click operation.
            var doc = V2Doc("SQLT-CORE-00205",
                extraFrontmatter:
                    "remediation:\n" +
                    "  auto_fixable: true\n" +
                    "  operation:\n" +
                    "    op_kind: db_set_option\n" +
                    "    option_sql: \"SET DB_CHAINING OFF\"\n");
            var r = BuildInline(doc);

            Assert.NotNull(r.Check.Remediation); // the check still loaded fine
            Assert.Null(r.Check.Remediation!.Operation); // but the op degraded to preview-only
            Assert.Contains(r.Derivations, d => d.Field == "remediation.operation" && d.Source == "malformed");
        }

        [Fact]
        public void Remediation_ValueParamAndValueFixed_BothPresent_IsMalformed()
        {
            var doc = V2Doc("SQLT-CORE-00206",
                extraFrontmatter:
                    "remediation:\n" +
                    "  auto_fixable: true\n" +
                    "  operation:\n" +
                    "    op_kind: sp_configure\n" +
                    "    config_name: \"max degree of parallelism\"\n" +
                    "    value_param: MaxDop\n" +
                    "    value_fixed: 0\n");
            var r = BuildInline(doc);

            Assert.Null(r.Check.Remediation!.Operation);
            Assert.Contains(r.Derivations, d => d.Field == "remediation.operation" && d.Source == "malformed");
        }

        [Fact]
        public void Remediation_UnknownOpKind_NoOperationField_Malformed()
        {
            var doc = V2Doc("SQLT-CORE-00207",
                extraFrontmatter:
                    "remediation:\n" +
                    "  auto_fixable: false\n" +
                    "  operation:\n" +
                    "    config_name: \"whatever\"\n"); // no op_kind at all
            var r = BuildInline(doc);

            Assert.NotNull(r.Check.Remediation);
            Assert.Null(r.Check.Remediation!.Operation);
            Assert.Contains(r.Derivations, d => d.Field == "remediation.operation" && d.Source == "malformed");
        }
    }
}
