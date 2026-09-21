/* In the name of God, the Merciful, the Compassionate */

using System.IO;
using SQLTriage.Data.Parser;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace SQLTriage.Tests.Parser
{
    /// <summary>
    /// B2 Slice 1 — proves YamlDotNet + Data/Parser/ structure + the
    /// minimal LoadMapping contract. Subsequent slices (Derivations,
    /// ParseInvariants, SqlCheckBuilder) layer on top.
    /// </summary>
    public class YamlCheckParserTests
    {
        private static string FixturePath(string name) =>
            Path.Combine(AppContext.BaseDirectory, "Parser", "Fixtures", name);

        [Fact]
        public void LoadMapping_plain_yaml_returns_expected_keys()
        {
            // LoadMapping is the RAW yaml loader (no frontmatter handling) —
            // the V2 Markdown document path goes through
            // SourceCatalogueLoader.ParseMappingFromText instead. Pin the
            // plain-yaml contract with an inline fixture (the shared
            // minimal-valid.yaml fixture is a V2 Markdown doc since 2026-06-12).
            var tmp = Path.Combine(Path.GetTempPath(), $"b2-slice1-{System.Guid.NewGuid():N}.yaml");
            File.WriteAllText(tmp, @"
id: SQLT-CORE-00010
severity: Medium
framework_mappings:
  - framework: NIST SP 800-53 Rev 5
source:
  framework: sp_Blitz
");
            try
            {
                var map = YamlCheckParser.LoadMapping(tmp);

                Assert.Contains("id", map);
                Assert.Contains("severity", map);
                Assert.Contains("framework_mappings", map);
                Assert.Contains("source", map);

                var checkId = (YamlScalarNode)map["id"];
                Assert.Equal("SQLT-CORE-00010", checkId.Value);

                // framework_mappings is a sequence (list)
                Assert.IsType<YamlSequenceNode>(map["framework_mappings"]);

                // source is a sub-mapping
                Assert.IsType<YamlMappingNode>(map["source"]);
            }
            finally { File.Delete(tmp); }
        }

        [Fact]
        public void ParseMappingFromText_extracts_frontmatter_and_synthetic_fields()
        {
            // The production V2 document path: frontmatter → map, Markdown body
            // sections → synthetic description / query_sql nodes.
            var fixture = File.ReadAllText(FixturePath("minimal-valid.yaml"));
            var map = SourceCatalogueLoader.ParseMappingFromText(fixture, "minimal-valid.yaml");

            Assert.Contains("id", map);
            Assert.Contains("result_contract", map);
            Assert.Equal("SQLT-CORE-00010", ((YamlScalarNode)map["id"]).Value);
            Assert.Equal("SELECT CASE WHEN 1=0 THEN 1 ELSE 0 END",
                ((YamlScalarNode)map["query_sql"]).Value);
            Assert.Contains("Smallest viable", ((YamlScalarNode)map["description"]).Value);
        }

        [Fact]
        public void LoadMapping_missing_file_throws_SourceParseException()
        {
            var ex = Assert.Throws<SourceParseException>(
                () => YamlCheckParser.LoadMapping(FixturePath("does-not-exist.yaml")));
            Assert.Contains("does not exist", ex.Message);
        }

        [Fact]
        public void LoadMapping_non_mapping_root_throws_SourceParseException()
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"b2-slice1-{System.Guid.NewGuid():N}.yaml");
            File.WriteAllText(tmp, "- just\n- a\n- list\n"); // sequence root, not mapping
            try
            {
                var ex = Assert.Throws<SourceParseException>(
                    () => YamlCheckParser.LoadMapping(tmp));
                Assert.Contains("not a mapping", ex.Message);
            }
            finally { File.Delete(tmp); }
        }
    }
}
