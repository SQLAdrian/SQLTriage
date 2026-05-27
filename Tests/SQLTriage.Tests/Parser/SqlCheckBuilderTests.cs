/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using System.IO;
using System.Linq;
using SQLTriage.Data.Parser;
using Xunit;

namespace SQLTriage.Tests.Parser
{
    public class SqlCheckBuilderTests
    {
        private static string FixturePath(string name) =>
            Path.Combine(AppContext.BaseDirectory, "Parser", "Fixtures", name);

        private static IReadOnlyDictionary<string, YamlDotNet.RepresentationModel.YamlNode> LoadFixture(string name)
            => YamlCheckParser.LoadMapping(FixturePath(name));

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
            Assert.Equal(0, c.ExpectedValue);            // §3.1 default
            Assert.Equal("Binary", c.ExecutionType);     // §3.2: CASE-WHEN binary shape
            Assert.Equal(10, c.ScoreWeight);             // §3.3: Medium × low/unknown = 10
            Assert.False(c.IsBad);                       // §3.4 default
            Assert.True(c.Enabled);

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
        public void Build_falls_back_to_sqlBody_when_yaml_has_no_enhanced_query()
        {
            // craft a yaml in-memory missing query_analysis.enhanced_query
            var tmp = Path.Combine(Path.GetTempPath(), $"b2-s3-{System.Guid.NewGuid():N}.yaml");
            File.WriteAllText(tmp, @"
check_id: SQLT-CORE-00020
schema_version: 2
title: t
description: d
category: Performance
priority: Low
framework_mappings: []
query_analysis: {}
");
            try
            {
                var seen = new HashSet<string>();
                var result = SqlCheckBuilder.Build(
                    YamlCheckParser.LoadMapping(tmp), tmp,
                    sqlFallbackBody: "SELECT 99 AS result",
                    seenIds: seen);
                Assert.Equal("SELECT 99 AS result", result.Check.SqlQuery);
            }
            finally { File.Delete(tmp); }
        }

        [Fact]
        public void Build_throws_when_both_yaml_sql_and_fallback_absent()
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"b2-s3-{System.Guid.NewGuid():N}.yaml");
            File.WriteAllText(tmp, @"
check_id: SQLT-CORE-00021
schema_version: 2
title: t
description: d
category: Performance
priority: Low
framework_mappings: []
query_analysis: {}
");
            try
            {
                var seen = new HashSet<string>();
                var ex = Assert.Throws<SourceParseException>(() =>
                    SqlCheckBuilder.Build(YamlCheckParser.LoadMapping(tmp), tmp, null, seen));
                Assert.Contains("no SQL", ex.Message);
            }
            finally { File.Delete(tmp); }
        }

        [Fact]
        public void Build_throws_on_missing_required_field()
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"b2-s3-{System.Guid.NewGuid():N}.yaml");
            // 'title' deliberately missing
            File.WriteAllText(tmp, @"
check_id: SQLT-CORE-00022
schema_version: 2
description: d
category: Performance
priority: Low
framework_mappings: []
query_analysis:
  enhanced_query: SELECT 1
");
            try
            {
                var ex = Assert.Throws<SourceParseException>(() =>
                    SqlCheckBuilder.Build(YamlCheckParser.LoadMapping(tmp), tmp, null, new HashSet<string>()));
                Assert.Contains("title", ex.Message);
            }
            finally { File.Delete(tmp); }
        }

        [Fact]
        public void Build_loads_real_BPCHECK_001_yaml_and_derives_ResultInterpretation()
        {
            // The 2026-05-21 runtime bug: 158 checks have SQL with SELECT 'SKIP' / 'INFO'
            // but resultInterpretation came back null in audit-diag. The
            // unit-level DeriveResultInterpretation test passes against the
            // raw SQL string — so the bug, if any, must be in the BUILD pipeline
            // (yaml→sql extraction, encoding, escape). This test runs the FULL
            // build path on a corpus YAML and asserts ResultInterpretation
            // ends up non-null on the SqlCheck object.
            var corpus = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "research_output", "LLM1_deepseek");
            corpus = Path.GetFullPath(corpus);
            var yamlPath = Directory.Exists(corpus)
                ? Directory.GetFiles(corpus, "check_BPCHECK_001_*.yaml").FirstOrDefault()
                : null;
            if (yamlPath is null) return;  // corpus not available — skip

            var mapping = YamlCheckParser.LoadMapping(yamlPath);
            var result = SqlCheckBuilder.Build(mapping, yamlPath, null, new HashSet<string>());

            Assert.NotNull(result.Check.ResultInterpretation);
            Assert.Contains("Pass", result.Check.ResultInterpretation);
        }

        [Fact]
        public void Build_throws_on_old_check_id_form()
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"b2-s3-{System.Guid.NewGuid():N}.yaml");
            File.WriteAllText(tmp, @"
check_id: BLITZ_001
schema_version: 2
title: t
description: d
category: Performance
priority: Low
framework_mappings: []
query_analysis:
  enhanced_query: SELECT 1
");
            try
            {
                var ex = Assert.Throws<SourceParseException>(() =>
                    SqlCheckBuilder.Build(YamlCheckParser.LoadMapping(tmp), tmp, null, new HashSet<string>()));
                Assert.Contains("v2 regex", ex.Message);
            }
            finally { File.Delete(tmp); }
        }
    }
}
