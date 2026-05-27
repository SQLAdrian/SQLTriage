/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using SQLTriage.Data.Parser;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace SQLTriage.Tests.Parser
{
    /// <summary>B1 §4 — fail-fast invariant gates.</summary>
    public class ParseInvariantsTests
    {
        private static YamlScalarNode Scalar(string v) => new(v);
        private static YamlSequenceNode Seq() => new();
        private static YamlMappingNode Map() => new();

        private static IReadOnlyDictionary<string, YamlNode> ValidMap() => new Dictionary<string, YamlNode>
        {
            ["check_id"] = Scalar("SQLT-CORE-00010"),
            ["title"] = Scalar("t"),
            ["description"] = Scalar("d"),
            ["category"] = Scalar("Performance"),
            ["priority"] = Scalar("Medium"),
            ["framework_mappings"] = Seq(),
            ["query_analysis"] = Map(),
            ["schema_version"] = Scalar("2"),
        };

        // ── #2 required-field presence ──

        [Fact]
        public void RequireFields_passes_on_valid_map() =>
            ParseInvariants.RequireFields("f.yaml", ValidMap());

        [Fact]
        public void RequireFields_throws_on_missing()
        {
            var bad = new Dictionary<string, YamlNode>(ValidMap()); bad.Remove("title");
            var ex = Assert.Throws<SourceParseException>(
                () => ParseInvariants.RequireFields("f.yaml", bad));
            Assert.Contains("title", ex.Message);
        }

        [Fact]
        public void RequireFields_throws_on_empty_string_scalar()
        {
            var bad = new Dictionary<string, YamlNode>(ValidMap()) { ["title"] = Scalar("") };
            var ex = Assert.Throws<SourceParseException>(
                () => ParseInvariants.RequireFields("f.yaml", bad));
            Assert.Contains("empty", ex.Message);
        }

        // ── #3 v2 check_id regex ──

        [Theory]
        [InlineData("SQLT-CORE-00010")]
        [InlineData("SQLT-BLITZ-99999")]
        [InlineData("SQLT-DSSTIG-00010")]   // 8-NS extension live
        public void RequireV2CheckId_passes_on_valid(string id) =>
            ParseInvariants.RequireV2CheckId("f.yaml", id);

        [Theory]
        [InlineData("")]
        [InlineData("BLITZ_010")]              // old form
        [InlineData("SQLT-FOO-00010")]         // NS not in enum
        [InlineData("SQLT-CORE-10")]           // <5 digits
        [InlineData("SQLT-CORE-000010")]       // 6 digits
        [InlineData("sqlt-core-00010")]        // lowercase
        public void RequireV2CheckId_throws_on_invalid(string id)
        {
            Assert.Throws<SourceParseException>(
                () => ParseInvariants.RequireV2CheckId("f.yaml", id));
        }

        // ── #4 schema_version == 2 ──

        [Fact]
        public void RequireSchemaV2_passes_on_2() =>
            ParseInvariants.RequireSchemaV2("f.yaml", ValidMap());

        [Fact]
        public void RequireSchemaV2_throws_on_1()
        {
            var bad = new Dictionary<string, YamlNode>(ValidMap()) { ["schema_version"] = Scalar("1") };
            Assert.Throws<SourceParseException>(() => ParseInvariants.RequireSchemaV2("f.yaml", bad));
        }

        [Fact]
        public void RequireSchemaV2_throws_on_missing()
        {
            var bad = new Dictionary<string, YamlNode>(ValidMap()); bad.Remove("schema_version");
            Assert.Throws<SourceParseException>(() => ParseInvariants.RequireSchemaV2("f.yaml", bad));
        }

        // ── #5 collision ──

        [Fact]
        public void RejectCollision_passes_first_then_throws_second()
        {
            var seen = new HashSet<string>();
            ParseInvariants.RejectCollision("a.yaml", "SQLT-CORE-00010", seen);
            var ex = Assert.Throws<SourceParseException>(
                () => ParseInvariants.RejectCollision("b.yaml", "SQLT-CORE-00010", seen));
            Assert.Contains("collision", ex.Message);
        }

        // ── #6 SQL source ──

        [Fact]
        public void RequireSqlSource_passes_when_yamlSql_present() =>
            ParseInvariants.RequireSqlSource("f.yaml", "X", "SELECT 1", null);

        [Fact]
        public void RequireSqlSource_passes_when_sqlBody_fallback_present() =>
            ParseInvariants.RequireSqlSource("f.yaml", "X", null, "SELECT 1");

        [Fact]
        public void RequireSqlSource_throws_when_both_absent()
        {
            var ex = Assert.Throws<SourceParseException>(
                () => ParseInvariants.RequireSqlSource("f.yaml", "X", null, null));
            Assert.Contains("no SQL", ex.Message);
        }

        // ── #7 type drift ──

        [Fact]
        public void RequireType_passes_on_match() =>
            ParseInvariants.RequireType("f.yaml", "X", "framework_mappings", Seq(), typeof(YamlSequenceNode));

        [Fact]
        public void RequireType_throws_on_mismatch()
        {
            var ex = Assert.Throws<SourceParseException>(
                () => ParseInvariants.RequireType("f.yaml", "X", "framework_mappings", Scalar("not-a-list"),
                    typeof(YamlSequenceNode)));
            Assert.Contains("wrong type", ex.Message);
        }
    }
}
