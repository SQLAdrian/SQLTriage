/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using SQLTriage.Data.Parser;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace SQLTriage.Tests.Parser
{
    /// <summary>
    /// Fail-fast invariant gates — V2 Markdown+frontmatter contract
    /// (972cb27 parser migration; fixtures realigned 2026-06-12).
    /// </summary>
    public class ParseInvariantsTests
    {
        private static YamlScalarNode Scalar(string v) => new(v);
        private static YamlSequenceNode Seq() => new();
        private static YamlMappingNode Map() => new();

        // The V2 required-field set (ParseInvariants.RequiredFields):
        // id, title, category, severity, source, applicability,
        // result_contract, framework_mappings, provenance.
        private static IReadOnlyDictionary<string, YamlNode> ValidMap() => new Dictionary<string, YamlNode>
        {
            ["id"] = Scalar("SQLT-CORE-00010"),
            ["title"] = Scalar("t"),
            ["category"] = Scalar("Performance"),
            ["severity"] = Scalar("Medium"),
            ["source"] = Map(),
            ["applicability"] = Map(),
            ["result_contract"] = Scalar("verdict"),
            ["framework_mappings"] = Seq(),
            ["provenance"] = Scalar("custom"),
        };

        // ── #2 required-field presence ──

        [Fact]
        public void RequireFields_passes_on_valid_map() =>
            ParseInvariants.RequireFields("f.md", ValidMap());

        [Fact]
        public void RequireFields_throws_on_missing()
        {
            var bad = new Dictionary<string, YamlNode>(ValidMap()); bad.Remove("title");
            var ex = Assert.Throws<SourceParseException>(
                () => ParseInvariants.RequireFields("f.md", bad));
            Assert.Contains("title", ex.Message);
        }

        [Fact]
        public void RequireFields_throws_on_empty_string_scalar()
        {
            var bad = new Dictionary<string, YamlNode>(ValidMap()) { ["title"] = Scalar("") };
            var ex = Assert.Throws<SourceParseException>(
                () => ParseInvariants.RequireFields("f.md", bad));
            Assert.Contains("empty", ex.Message);
        }

        // ── #3 v2 check_id regex ──
        // Relaxed 972cb27 to ^SQLT-[A-Z0-9-]+$ (the corpus-repo hard rule):
        // namespaces are open (no closed NS enum) and digit count is free.

        [Theory]
        [InlineData("SQLT-CORE-00010")]
        [InlineData("SQLT-BLITZ-99999")]
        [InlineData("SQLT-DSSTIG-00010")]
        [InlineData("SQLT-FOO-00010")]        // open namespace — valid post-relaxation
        [InlineData("SQLT-CORE-10")]          // digit count free post-relaxation
        [InlineData("SQLT-CORE-000010")]
        [InlineData("SQLT-VA-XP-CMDSHELL")]   // multi-segment ids (VA namespace) are live
        public void RequireV2CheckId_passes_on_valid(string id) =>
            ParseInvariants.RequireV2CheckId("f.md", id);

        [Theory]
        [InlineData("")]
        [InlineData("BLITZ_010")]              // old form (underscore, no SQLT- prefix)
        [InlineData("sqlt-core-00010")]        // lowercase
        [InlineData("SQLT-core-00010")]        // camel/lower segment
        public void RequireV2CheckId_throws_on_invalid(string id)
        {
            Assert.Throws<SourceParseException>(
                () => ParseInvariants.RequireV2CheckId("f.md", id));
        }

        // ── #4 schema_version — deliberate no-op for V2 ──
        // V2 Markdown denotes its schema by format; RequireSchemaV2 must not
        // throw regardless of any schema_version value. Pin the no-op so a
        // future re-tightening is a conscious decision.

        [Fact]
        public void RequireSchemaV2_is_a_noop_for_v2_markdown()
        {
            ParseInvariants.RequireSchemaV2("f.md", ValidMap());
            var withLegacyVersion = new Dictionary<string, YamlNode>(ValidMap())
            {
                ["schema_version"] = Scalar("1")
            };
            ParseInvariants.RequireSchemaV2("f.md", withLegacyVersion); // no throw
        }

        // ── #5 collision ──

        [Fact]
        public void RejectCollision_passes_first_then_throws_second()
        {
            var seen = new HashSet<string>();
            ParseInvariants.RejectCollision("a.md", "SQLT-CORE-00010", seen);
            var ex = Assert.Throws<SourceParseException>(
                () => ParseInvariants.RejectCollision("b.md", "SQLT-CORE-00010", seen));
            Assert.Contains("collision", ex.Message);
        }

        // ── #6 SQL source ──

        [Fact]
        public void RequireSqlSource_passes_when_yamlSql_present() =>
            ParseInvariants.RequireSqlSource("f.md", "X", "SELECT 1", null);

        [Fact]
        public void RequireSqlSource_passes_when_sqlBody_fallback_present() =>
            ParseInvariants.RequireSqlSource("f.md", "X", null, "SELECT 1");

        [Fact]
        public void RequireSqlSource_throws_when_both_absent()
        {
            var ex = Assert.Throws<SourceParseException>(
                () => ParseInvariants.RequireSqlSource("f.md", "X", null, null));
            Assert.Contains("no SQL", ex.Message);
        }

        // ── #7 type drift ──

        [Fact]
        public void RequireType_passes_on_match() =>
            ParseInvariants.RequireType("f.md", "X", "framework_mappings", Seq(), typeof(YamlSequenceNode));

        [Fact]
        public void RequireType_throws_on_mismatch()
        {
            var ex = Assert.Throws<SourceParseException>(
                () => ParseInvariants.RequireType("f.md", "X", "framework_mappings", Scalar("not-a-list"),
                    typeof(YamlSequenceNode)));
            Assert.Contains("wrong type", ex.Message);
        }
    }
}
