/* In the name of God, the Merciful, the Compassionate */

using SQLTriage.Data;

namespace SQLTriage.Tests;

public class CsvParserTests
{
    // ── DetectDelimiter ──────────────────────────────────────────────────────

    [Fact]
    public void DetectDelimiter_TildeHeader_ReturnsTilde()
        => Assert.Equal('~', CsvParser.DetectDelimiter("\"ID\"~\"evaldate\"~\"Server\""));

    [Fact]
    public void DetectDelimiter_CommaHeader_ReturnsComma()
        => Assert.Equal(',', CsvParser.DetectDelimiter("CheckId,DisplayName,Severity"));

    // ── Simple comma-delimited ───────────────────────────────────────────────

    [Fact]
    public void ParseLine_SimpleFields_ReturnsList()
    {
        var result = CsvParser.ParseLine("foo,bar,baz");
        Assert.Equal(new[] { "foo", "bar", "baz" }, result);
    }

    [Fact]
    public void ParseLine_EmptyFields_PreservesEmpties()
    {
        var result = CsvParser.ParseLine("a,,c");
        Assert.Equal(3, result.Count);
        Assert.Equal("", result[1]);
    }

    [Fact]
    public void ParseLine_TrailingComma_ProducesEmptyLastField()
    {
        var result = CsvParser.ParseLine("a,b,");
        Assert.Equal(3, result.Count);
        Assert.Equal("", result[2]);
    }

    // ── Quoted fields ────────────────────────────────────────────────────────

    [Fact]
    public void ParseLine_QuotedFieldWithComma_TreatedAsSingleField()
    {
        var result = CsvParser.ParseLine("\"hello, world\",foo");
        Assert.Equal(2, result.Count);
        Assert.Equal("hello, world", result[0]);
        Assert.Equal("foo", result[1]);
    }

    [Fact]
    public void ParseLine_EscapedQuoteInField_UnescapedCorrectly()
    {
        var result = CsvParser.ParseLine("\"say \"\"hello\"\"\",next");
        Assert.Equal("say \"hello\"", result[0]);
        Assert.Equal("next", result[1]);
    }

    [Fact]
    public void ParseLine_MultiLineQuotedField_ParsedCorrectly()
    {
        // Simulates a field value that contains a newline (pre-joined by the reader)
        var line = "\"line one\nline two\",end";
        var result = CsvParser.ParseLine(line);
        Assert.Equal("line one\nline two", result[0]);
        Assert.Equal("end", result[1]);
    }

    [Fact]
    public void ParseLine_EmptyQuotedField_ReturnsEmpty()
    {
        var result = CsvParser.ParseLine("\"\",b");
        Assert.Equal("", result[0]);
        Assert.Equal("b", result[1]);
    }

    // ── VA-style header (18+ columns) ────────────────────────────────────────

    [Fact]
    public void ParseLine_VaHeaderLine_ParsesAllColumns()
    {
        const string header = "CheckId,DisplayName,Message,Severity,RawSeverity,TargetName," +
                              "TargetType,Category,Description,Remediation,HelpLink,Status," +
                              "ImplementationType,SqlQuery,ThisServer,ThisDomain,IsSQLAzure,IsSQLMI,UTCDateTime";
        var result = CsvParser.ParseLine(header);
        Assert.Equal(19, result.Count);
        Assert.Equal("CheckId", result[0]);
        Assert.Equal("UTCDateTime", result[18]);
    }

    // ── Tilde-delimited (sqlmagic) ────────────────────────────────────────────

    [Fact]
    public void ParseLine_TildeDelimited_SplitsCorrectly()
    {
        var result = CsvParser.ParseLine("\"1\"~\"2025-10-28\"~\"cyclone.co.nz\"~\"CYC-SQL1\"", '~');
        Assert.Equal(4, result.Count);
        Assert.Equal("1", result[0]);
        Assert.Equal("2025-10-28", result[1]);
        Assert.Equal("cyclone.co.nz", result[2]);
        Assert.Equal("CYC-SQL1", result[3]);
    }

    [Fact]
    public void ParseLine_TildeDelimited_StripsQuotes()
    {
        var result = CsvParser.ParseLine("\"hello\"~\"world\"", '~');
        Assert.Equal("hello", result[0]);
        Assert.Equal("world", result[1]);
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public void ParseLine_EmptyString_ReturnsEmptyList()
    {
        // Empty input produces no fields — callers guard with Count checks
        var result = CsvParser.ParseLine("");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseLine_SingleField_ReturnsSingleElement()
    {
        var result = CsvParser.ParseLine("onlyone");
        Assert.Single(result);
        Assert.Equal("onlyone", result[0]);
    }

    [Fact]
    public void ParseLine_AllQuoted_ParsesAllCorrectly()
    {
        var result = CsvParser.ParseLine("\"a\",\"b\",\"c\"");
        Assert.Equal(new[] { "a", "b", "c" }, result);
    }
}
