/* In the name of God, the Merciful, the Compassionate */

using SQLTriage.Data.Parser;
using Xunit;

namespace SQLTriage.Tests.Parser
{
    public class SqlHeaderParserTests
    {
        [Fact]
        public void Parse_extracts_check_metadata_block()
        {
            const string sql = @"/*
CHECK_METADATA
check_id: WAIT_001
title: Top Wait Stats
category: Performance
*/
SELECT 1 AS result;";
            var (meta, body) = SqlHeaderParser.ParseText(sql);
            Assert.Equal("WAIT_001", meta["check_id"]);
            Assert.Equal("Top Wait Stats", meta["title"]);
            Assert.Equal("Performance", meta["category"]);
            Assert.StartsWith("SELECT 1", body);
        }

        [Fact]
        public void Parse_skips_basmalah_then_finds_check_metadata()
        {
            const string sql = @"/* In the name of God, the Merciful, the Compassionate */

/*
CHECK_METADATA
check_id: NEW_010
title: x
*/
SELECT 0;";
            var (meta, body) = SqlHeaderParser.ParseText(sql);
            Assert.Equal("NEW_010", meta["check_id"]);
            Assert.StartsWith("SELECT 0", body);
        }

        [Fact]
        public void Parse_returns_empty_meta_and_full_body_when_no_header()
        {
            const string sql = "SELECT 42 AS result;";
            var (meta, body) = SqlHeaderParser.ParseText(sql);
            Assert.Empty(meta);
            Assert.Equal(sql, body);
        }

        [Fact]
        public void Parse_ignores_non_key_value_lines_inside_block()
        {
            const string sql = @"/*
CHECK_METADATA
this is just prose with no colon
check_id: X
*/
SELECT 1;";
            var (meta, _) = SqlHeaderParser.ParseText(sql);
            Assert.Single(meta);
            Assert.Equal("X", meta["check_id"]);
        }
    }
}
