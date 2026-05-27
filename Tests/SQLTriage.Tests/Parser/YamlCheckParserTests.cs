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
        public void LoadMapping_minimal_valid_returns_expected_keys()
        {
            var map = YamlCheckParser.LoadMapping(FixturePath("minimal-valid.yaml"));

            Assert.Contains("check_id", map);
            Assert.Contains("schema_version", map);
            Assert.Contains("framework_mappings", map);
            Assert.Contains("query_analysis", map);

            // check_id is a scalar with the v2 form
            var checkId = (YamlScalarNode)map["check_id"];
            Assert.Equal("SQLT-CORE-00010", checkId.Value);

            // framework_mappings is a sequence (list)
            Assert.IsType<YamlSequenceNode>(map["framework_mappings"]);

            // query_analysis is a sub-mapping
            Assert.IsType<YamlMappingNode>(map["query_analysis"]);
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
