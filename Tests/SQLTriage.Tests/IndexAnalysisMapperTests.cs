/* In the name of God, the Merciful, the Compassionate */

// ── Index Analysis mapper regression, 2026-08-13 ─────────────────────────────────────────────
//
// A defect report opened this lane: an /index-analysis page on a SQL 2017 server showing a red
// "Error:" banner. Auditing the page's @code block found one line that cannot work:
//
//     FragPercent = (double)reader.GetDecimal(3)
//
// Column 3 is ips.avg_fragmentation_in_percent, declared SQL float, so it arrives at the reader
// as System.Double and GetDecimal throws InvalidCastException. It had survived because the mapper
// lived inside a .razor @code block where nothing could reach it, and because the fragmented
// query only returns rows when the connected database holds an index over 5% fragmented across
// more than 1,000 pages, so most instances never got that far.
//
// ⚠ THE PROVENANCE, HEDGED TO WHAT WAS MEASURED. PROVED live on .\old2017 (14.0.2120.1) against
// a real fragmented index: that expression throws InvalidCastException with the message "Unable
// to cast object of type 'System.Double' to type 'System.Decimal'." — see
// IndexAnalysisLiveSmokeTests. The page composes its banner as $"Error: {ex.Message}"
// (Pages/IndexAnalysis.razor:383, read not run), so this defect's banner carries that long
// message. The REPORTED banner read "Specified cast is not valid.", the parameterless
// InvalidCastException message, which this path does not emit on that stack. So: this defect is
// proved, its identity with that screenshot is UNPROVED, and the banner wording remains the
// discriminator if that server still errors after the fix. The tests below defend the MAPPING;
// they do not, and cannot, establish what produced the reported screenshot.
//
// THESE TESTS ARE DRIVEN FROM CAPTURED READER SHAPES, not hand-composed rows. The house rule
// exists because two cold gates once passed against payloads the endpoint never sends: a mapper
// test that invents its own column types cannot catch a mapper that assumes the wrong column
// type — it would simply invent the wrong type too, agree with itself, and stay green.
// captured-reader-shapes.json was written by IndexAnalysisLiveSmokeTests.Capture_reader_shapes_
// for_the_offline_tests against a real SQL 2017 instance (14.0.2120.1, the same major version as
// the server in the report), reading a real fragmented index. Each column carries its declared SQL
// type, its CLR type and a round-trippable value, and CapturedRowReader replays exactly those
// boxes. If the capture is regenerated against a version whose types differ, these tests change
// with it — which is the point.
//
// ⚠ WHAT THIS DOES NOT PROVE. It exercises the MAPPERS over the captured shapes. It does not
// prove the live query still returns those shapes on some other version; that claim belongs to
// IndexAnalysisLiveSmokeTests, which runs against a real instance and is inert here.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

public class IndexAnalysisMapperTests
{
    // ── The capture ──────────────────────────────────────────────────────────────────────────

    private static readonly Lazy<JsonElement> Capture = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "captured-reader-shapes.json");
        File.Exists(path).Should().BeTrue(
            "captured-reader-shapes.json is copied to the test output by SQLTriage.Tests.csproj; "
            + "if this fails every assertion below would vacuously pass");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    });

    /// <summary>
    /// THE SECOND CAPTURE, 2026-08-14. The three INDEX queries and their two forced-absence
    /// variants, taken from one purpose-built database on <c>.\new2022</c> (16.0.4262) by
    /// <see cref="IndexAnalysisLiveSmokeTests.Capture_index_query_shapes_for_the_offline_tests"/>.
    ///
    /// <para>⚠ WHY IT IS A SECOND FILE AND NOT AN EXTENSION OF THE FIRST. The capture above is
    /// bound to an ESTATE fixture — more than twenty user databases, twenty-two offline ones, an
    /// AUTO_CLOSE one — because the RANKING pass needs one. The index queries need one database.
    /// Tying the two together meant every index-query change demanded that estate be rebuilt, and
    /// that cost is exactly what pushes a future lane into hand-writing a row. Splitting them also
    /// keeps the first capture's <b>14.0.2120.1 provenance</b> intact: it is the version in the
    /// original defect report, and re-taking it on 2022 would have quietly thrown that away.</para>
    ///
    /// <para>⚠ AND WHAT THE SPLIT COSTS, stated rather than hidden: the index shapes below are now
    /// proved on SQL 2022 and NOT on SQL 2017. The declared types of these columns are set by the
    /// queries' own CASTs and by the DMVs, and the 2017 capture still holds the same columns, so
    /// the two agree today — <see cref="The_two_captures_agree_on_the_index_column_types"/> asserts
    /// exactly that and is what will notice if a future version disagrees.</para>
    /// </summary>
    private static readonly Lazy<JsonElement> IndexCapture = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "captured-index-query-shapes.json");
        File.Exists(path).Should().BeTrue(
            "captured-index-query-shapes.json is copied to the test output by SQLTriage.Tests.csproj; "
            + "if this fails every assertion over it would vacuously pass");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    });

    private static List<CapturedRowReader> Rows(string set) => Rows(Capture.Value, set);

    /// <summary>Rows from the INDEX-query capture. Anything that calls an index mapper reads here:
    /// those queries are what that capture was taken against.</summary>
    private static List<CapturedRowReader> IndexRows(string set) => Rows(IndexCapture.Value, set);

    private static List<CapturedRowReader> Rows(JsonElement capture, string set)
        => capture.GetProperty(set).EnumerateArray()
            .Select(r => new CapturedRowReader(r))
            .ToList();

    private static CapturedRowReader FirstRow(string set) => First(Rows(set), set);

    private static CapturedRowReader FirstIndexRow(string set) => First(IndexRows(set), set);

    private static CapturedRowReader First(List<CapturedRowReader> rows, string set)
    {
        rows.Should().NotBeEmpty($"the capture must hold at least one '{set}' row or the "
                                 + "assertions below prove nothing");
        return rows[0];
    }

    [Fact]
    public void The_capture_came_from_a_sql_2017_instance_reading_a_real_fragmented_index()
    {
        Capture.Value.GetProperty("serverVersion").GetString()
            .Should().StartWith("14.", "the reported failure was on a SQL 2017 (major 14) server");

        var row = FirstRow("fragmented");
        row.GetName(3).Should().Be("FragPercent");
        row.GetInt64(4).Should().BeGreaterThan(1000, "the page filters page_count > 1000");
        row.Dbl(3).Should().BeGreaterThan(5d, "the page filters avg_fragmentation_in_percent > 5");
    }

    // ── The defect ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The captured column really is a Double, and the shipped line really did throw on it. This
    /// is the negative control: without it, the passing test below could be passing because the
    /// capture is wrong rather than because the mapper is right.
    /// </summary>
    /// <summary>
    /// ⚠ RUN OVER BOTH CAPTURES SINCE 2026-08-14. The defect is proved on the version it was
    /// reported against (14.0.2120.1) and re-proved on the version the index shapes are now
    /// captured from (16.0.4262) — so the split between the two fixture files cannot quietly turn
    /// this reproduction into a claim about one version that the other contradicts.
    /// </summary>
    [Theory]
    [InlineData(false)]   // the 2017 estate capture
    [InlineData(true)]    // the 2022 index-query capture
    public void Frag_percent_arrives_as_a_float_and_the_old_mapping_threw_on_it(bool fromIndexCapture)
    {
        var row = fromIndexCapture ? FirstIndexRow("fragmented") : FirstRow("fragmented");

        // BY NAME, NOT BY ORDINAL, since 2026-08-14: [Schema] was added at position 1, so this
        // column sits at 3 in the older capture and 4 in the newer one. A hardcoded ordinal here
        // would have quietly started asserting about a different column.
        var frag = row.GetOrdinal("FragPercent");
        frag.Should().BeGreaterThanOrEqualTo(0, "the capture must still project FragPercent");

        row.GetDataTypeName(frag).Should().Be("float", "avg_fragmentation_in_percent is SQL float");
        row.GetFieldType(frag).Should().Be(typeof(double));

        // The exact expression the page shipped.
        var thrown = Record.Exception(() => _ = (double)row.GetDecimal(frag));
        thrown.Should().BeOfType<InvalidCastException>(
            "this is the defect this lane fixes, reproduced from the captured shape");
    }

    /// <summary>
    /// The two captures were taken from two servers four years apart, against the same three
    /// queries. This is what makes the split between them safe to reason about: if a future version
    /// declares one of these columns differently, the disagreement surfaces here as a named
    /// mismatch rather than as a mapper that reads one file and ships against the other.
    /// </summary>
    /// <summary>
    /// ⚠ MATCHED BY NAME, NOT BY ORDINAL, since 2026-08-14. The queries gained a <c>[Schema]</c>
    /// column at position 1, so every later ordinal shifted and the two captures no longer line up
    /// index-for-index — which is precisely why the mappers are replayed against a RE-CAPTURED
    /// reader rather than a hand-written one: a hand-written row would simply have been edited to
    /// agree with whatever the mapper now assumed.
    /// </summary>
    [Fact]
    public void The_two_captures_agree_on_every_index_column_they_share()
    {
        foreach (var set in new[] { "missing", "unused", "fragmented", "unusedNullSize" })
        {
            var old2017 = FirstRow(set);
            var new2022 = FirstIndexRow(set);

            var older = Enumerable.Range(0, old2017.FieldCount).ToDictionary(old2017.GetName);
            var newer = Enumerable.Range(0, new2022.FieldCount).ToDictionary(new2022.GetName);

            foreach (var (name, oldOrdinal) in older)
            {
                newer.Should().ContainKey(name,
                    $"'{set}' dropped the '{name}' column between the two captures. A column that "
                    + "disappears is a mapper reading something else, not a smaller result set");

                var newOrdinal = newer[name];
                new2022.GetDataTypeName(newOrdinal).Should().Be(old2017.GetDataTypeName(oldOrdinal),
                    $"{set}.'{name}' declared SQL type");
                new2022.GetFieldType(newOrdinal).Should().Be(old2017.GetFieldType(oldOrdinal),
                    $"{set}.'{name}' CLR type");
            }

            // Exactly one column is new, and it is the one this lane added. Without this the test
            // would tolerate any number of silent additions, and an added column is an ordinal
            // shift for every mapper reading past it.
            newer.Keys.Except(older.Keys).Should().Equal(new[] { "Schema" },
                $"'{set}' gained exactly the schema column, and nothing else, on 2026-08-14");
        }
    }

    // ── The real schema, 2026-08-14 ──────────────────────────────────────────────────────────
    //
    // The three queries projected OBJECT_NAME, which returns a BARE table name. Both script
    // builders therefore wrote [dbo] as a LITERAL, so every DROP INDEX and every ALTER INDEX the
    // product emitted for a table outside dbo named an object that does not exist. The queries now
    // project OBJECT_SCHEMA_NAME.

    [Fact]
    public void Every_index_row_carries_the_schema_the_server_reported()
    {
        var missing = IndexAnalysisService.MapMissing(FirstIndexRow("missing"));
        var unused = IndexAnalysisService.MapUnused(FirstIndexRow("unused"));
        var frag = IndexAnalysisService.MapFrag(FirstIndexRow("fragmented"));

        foreach (var (label, row) in new[]
                 {
                     ("missing", (Schema: missing.Schema, Table: missing.Table)),
                     ("unused", (unused.Schema, unused.Table)),
                     ("fragmented", (frag.Schema, frag.Table)),
                 })
        {
            row.Schema.Should().NotBeNullOrWhiteSpace($"the {label} query projects [Schema] now");
            row.Table.Should().NotBeNullOrWhiteSpace();
            row.Table.Should().NotContain(".",
                $"the {label} row's Table is a BARE object name; the schema is its own column, and "
                + "a script builder that has to split a string is a script builder that will "
                + "split the wrong one");
        }
    }

    /// <summary>
    /// THE CAPTURE MUST HOLD A NON-DBO SCHEMA, or every assertion about the fix is being made over
    /// rows where the old hardcoded <c>[dbo]</c> happened to be right.
    /// </summary>
    [Fact]
    public void The_capture_holds_a_table_outside_dbo_and_a_schema_that_needs_quoting()
    {
        var schemas = new[] { "missing", "unused", "fragmented" }
            .SelectMany(IndexRows)
            .Select(r => r.Str(1))
            .Distinct()
            .ToList();

        schemas.Should().Contain(s => !string.Equals(s, "dbo", StringComparison.OrdinalIgnoreCase),
            "the fixture must hold an object OUTSIDE dbo, or the old hardcoded [dbo] was right for "
            + $"every row here and nothing is proved. Found: {string.Join(", ", schemas)}");

        schemas.Should().Contain(s => s.Contains(']'),
            "a schema whose name needs quoting had never been exercised: the escaping shipped at "
            + "d833ebf covered databases, tables and index names, and the schema is new surface");
    }

    /// <summary>
    /// The three-part name a script targets. Every part is bracketed and every <c>]</c> doubled,
    /// and the SCHEMA is now one of those parts rather than the literal <c>dbo</c>.
    /// </summary>
    [Theory]
    [InlineData("db", "rep", "wide", "[db].[rep].[wide]")]
    [InlineData("db", "dbo", "wide", "[db].[dbo].[wide]")]
    // The escaping, on each part in turn. A ']' inside a bracketed name ends the identifier early
    // and the emitted script is a syntax error.
    [InlineData("we]ird", "rep", "wide", "[we]]ird].[rep].[wide]")]
    [InlineData("db", "we]ird", "wide", "[db].[we]]ird].[wide]")]
    [InlineData("db", "rep", "wi]de", "[db].[rep].[wi]]de]")]
    public void A_script_target_brackets_and_escapes_every_part_including_the_schema(
        string database, string schema, string table, string expected)
        => IndexAnalysisRendering.ScriptTarget(database, schema, table).Should().Be(expected);

    [Fact]
    public void No_script_target_can_be_built_from_a_hardcoded_dbo_any_more()
    {
        var service = File.ReadAllText(Path.Combine(RepoRoot(), "Data", "Services", "IndexAnalysisService.cs"));
        var page = File.ReadAllText(Path.Combine(RepoRoot(), "Pages", "IndexAnalysis.razor"));

        page.Should().NotContain(".[dbo].",
            "the page wrote [dbo] as a literal into every DROP INDEX and ALTER INDEX it emitted, "
            + "because the query gave it no schema to use");
        page.Should().Contain("IndexAnalysisRendering.ScriptTarget(",
            "both script builders go through the one target builder, so they cannot disagree about "
            + "what an object is called");

        foreach (var sql in new[]
                 {
                     IndexAnalysisService.MissingIndexSql,
                     IndexAnalysisService.UnusedIndexSql,
                     IndexAnalysisService.FragIndexSql,
                 })
            sql.Should().Contain("OBJECT_SCHEMA_NAME(",
                "a script builder can only name the right schema if the query read one");
    }

    /// <summary>
    /// The suggested CREATE INDEX is composed in T-SQL, so its schema fix is in the query text and
    /// not in any C# the mappers touch. It has to be qualified AND quoted there.
    /// </summary>
    [Fact]
    public void The_suggested_create_index_script_names_the_real_schema()
    {
        IndexAnalysisService.MissingIndexSql.Should().Contain(
            "' ON ' + QUOTENAME(OBJECT_SCHEMA_NAME(mid.object_id, mid.database_id)) + '.' + QUOTENAME(OBJECT_NAME(mid.object_id, mid.database_id))",
            "the emitted CREATE INDEX used to say ' ON ' + OBJECT_NAME(...), a bare unquoted table "
            + "name, so it named the wrong object outside dbo and would not parse for a name "
            + "needing brackets");

        // ...and the capture shows the server really composed it that way.
        var script = IndexAnalysisService.MapMissing(FirstIndexRow("missing")).SuggestedScript;
        script.Should().Contain(" ON [", "the emitted target must be bracketed");
        script.Should().MatchRegex(@" ON \[[^\]]+\]\.\[", "and schema-qualified");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Data", "Services", "IndexAnalysisService.cs")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find the repo root above " + AppContext.BaseDirectory
            + ". These assertions read source on purpose; without it they must FAIL, not pass.");
    }

    [Fact]
    public void The_mapper_reads_the_float_frag_percent_without_throwing()
    {
        var row = FirstIndexRow("fragmented");

        var mapped = IndexAnalysisService.MapFrag(row);

        mapped.FragPercent.Should().Be(row.Dbl(row.GetOrdinal("FragPercent")));
        mapped.FragPercent.Should().BeGreaterThan(5d);
        mapped.PageCount.Should().Be(row.GetInt64(row.GetOrdinal("PageCount")));
        mapped.IndexName.Should().NotBeNullOrWhiteSpace();
        mapped.Action.Should().BeOneOf("REBUILD", "REORGANIZE");
        // The badge and the verb are two renderings of one number; they must not disagree.
        mapped.Action.Should().Be(mapped.FragPercent > 30 ? "REBUILD" : "REORGANIZE");
    }

    // ── The latent sibling: a NULL that a typed Get* would have thrown on ─────────────────────

    /// <summary>
    /// SizeMB reaches sys.dm_db_partition_stats through a LEFT JOIN, so it is genuinely nullable.
    /// The old mapper called GetDecimal(6) on it, which raises SqlNullValueException — a second
    /// crash of the same class, waiting on a join miss. The 'unusedNullSize' capture is the real
    /// query with that join forced to miss, so this replays a true NULL in a true decimal column.
    /// </summary>
    [Fact]
    public void A_null_size_is_carried_as_absent_and_never_invented_as_zero()
    {
        var row = FirstIndexRow("unusedNullSize");

        var size = row.GetOrdinal("SizeMB");
        row.IsDBNull(size).Should().BeTrue("the capture's whole purpose is a genuinely NULL SizeMB");
        Record.Exception(() => _ = row.GetDecimal(size))
            .Should().NotBeNull("a typed Get* on a NULL is the second half of this defect class");

        var mapped = IndexAnalysisService.MapUnused(row);

        mapped.SizeMB.Should().BeNull("null in, null out — 0 would assert a measurement we never took");
        mapped.SizeMB.Should().NotBe(0m);
        // ...and the rest of the row is still read, rather than the whole row being lost.
        mapped.IndexName.Should().NotBeNullOrWhiteSpace();
        mapped.UserWrites.Should().BeGreaterThan(0);
    }

    // ── The columns the audit MOVED but did not change ────────────────────────────────────────

    [Fact]
    public void The_untouched_unused_columns_map_to_the_same_values_the_reader_holds()
    {
        var row = FirstIndexRow("unused");
        var mapped = IndexAnalysisService.MapUnused(row);

        mapped.Database.Should().Be(row.Str(row.GetOrdinal("Database")));
        mapped.Schema.Should().Be(row.Str(row.GetOrdinal("Schema")));
        mapped.Table.Should().Be(row.Str(row.GetOrdinal("Table")));
        mapped.IndexName.Should().Be(row.Str(row.GetOrdinal("IndexName")));
        mapped.IndexType.Should().Be(row.Str(row.GetOrdinal("IndexType")));
        mapped.UserReads.Should().Be(row.GetInt64(row.GetOrdinal("UserReads")));
        mapped.UserWrites.Should().Be(row.GetInt64(row.GetOrdinal("UserWrites")));
        var unusedSize = row.GetOrdinal("SizeMB");
        mapped.SizeMB.Should().Be(row.IsDBNull(unusedSize) ? null : row.GetDecimal(unusedSize));

        // The query's own predicate: this tab is written-but-never-read indexes.
        mapped.UserReads.Should().Be(0);
        mapped.UserWrites.Should().BeGreaterThan(0);
    }

    [Fact]
    public void The_untouched_frag_columns_map_to_the_same_values_the_reader_holds()
    {
        var row = FirstIndexRow("fragmented");
        var mapped = IndexAnalysisService.MapFrag(row);

        mapped.Database.Should().Be(row.Str(row.GetOrdinal("Database")));
        mapped.Schema.Should().Be(row.Str(row.GetOrdinal("Schema")));
        mapped.Table.Should().Be(row.Str(row.GetOrdinal("Table")));
        mapped.IndexName.Should().Be(row.Str(row.GetOrdinal("IndexName")));
        var fragSize = row.GetOrdinal("SizeMB");
        mapped.SizeMB.Should().Be(row.IsDBNull(fragSize) ? null : row.GetDecimal(fragSize));
        mapped.Action.Should().Be(row.Str(row.GetOrdinal("RecommendedAction")));
    }

    [Fact]
    public void The_missing_index_mapper_survives_its_captured_shape()
    {
        // Not tolerated empty: the capture was taken against a fixture deliberately seeded to
        // produce a suggestion, so an empty set here means the capture rotted, and every
        // assertion below would pass by never running.
        var row = FirstIndexRow("missing");
        var mapped = IndexAnalysisService.MapMissing(row);

        mapped.UserHits.Should().Be(row.GetInt64(row.GetOrdinal("UserHits")));
        mapped.AvgImpact.Should().Be(row.GetDecimal(row.GetOrdinal("AvgImpact")));
        mapped.ImpactScore.Should().Be(row.GetDecimal(row.GetOrdinal("ImpactScore")));
        mapped.Schema.Should().Be(row.Str(row.GetOrdinal("Schema")));
        mapped.KeyColumns.Should().Be(
            IndexAnalysisService.CombineColumns(
                row.Str(row.GetOrdinal("EqualityColumns")),
                row.Str(row.GetOrdinal("InequalityColumns"))));
    }

    // ── The impact columns, which used to become a measurement, 2026-08-14 ────────────────────
    //
    // Both are declared nullable by the server, MapMissing routed both through a helper that
    // substituted 0m, and the page rendered "0" and "0.00%" — which reads as "the optimizer expects
    // nothing from this index", the opposite of "nobody read a figure". Same defect as SizeMB, one
    // lane later, and closed the same way.
    //
    // ⚠ THE CAPTURE IS NOT HAND-BUILT. 'missingNullImpact' is the REAL missing-index query with
    // each impact projection wrapped in NULLIF(x, x), so the value is NULL while the CAST, the
    // declared type and the nullability are the query's own — captured through the real provider on
    // 16.0.4262. A hand-written null-returning reader would agree with whatever the mapper assumed.

    [Fact]
    public void The_captured_null_impact_row_really_holds_nulls_in_real_decimal_columns()
    {
        var row = FirstIndexRow("missingNullImpact");

        var avg = row.GetOrdinal("AvgImpact");
        var score = row.GetOrdinal("ImpactScore");
        avg.Should().BeGreaterThanOrEqualTo(0);
        score.Should().BeGreaterThanOrEqualTo(0);

        row.GetDataTypeName(avg).Should().Be("decimal",
            "the NULL must arrive in the column's own declared type, not in some substitute");
        row.GetDataTypeName(score).Should().Be("decimal");
        row.IsDBNull(avg).Should().BeTrue("the capture's whole purpose is a genuinely NULL AvgImpact");
        row.IsDBNull(score).Should().BeTrue("...and a genuinely NULL ImpactScore");

        // The negative control. A typed Get* on these is the same crash class SizeMB carried, and
        // without it the assertions below could pass because the capture is wrong.
        Record.Exception(() => _ = row.GetDecimal(avg)).Should().NotBeNull();
        Record.Exception(() => _ = row.GetDecimal(score)).Should().NotBeNull();

        // ...and the rest of the row is a real suggestion, so this is a null IN a populated row
        // rather than a row that is empty for some other reason.
        row.Str(row.GetOrdinal("Database")).Should().NotBeNullOrWhiteSpace();
        row.GetInt64(row.GetOrdinal("UserHits")).Should().BeGreaterThan(0);
    }

    [Fact]
    public void A_null_impact_is_carried_as_absent_and_never_invented_as_zero()
    {
        var row = FirstIndexRow("missingNullImpact");

        var mapped = IndexAnalysisService.MapMissing(row);

        mapped.AvgImpact.Should().BeNull("null in, null out — 0 asserts a measurement nobody took");
        mapped.ImpactScore.Should().BeNull();
        mapped.AvgImpact.Should().NotBe(0m, "0.00% is a verdict about the optimizer's expectation");
        mapped.ImpactScore.Should().NotBe(0m);

        // The row is still read rather than lost.
        mapped.Database.Should().NotBeNullOrWhiteSpace();
        mapped.UserHits.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// THE WORD, asserted once, for both surfaces. It was a literal on Pages/IndexAnalysis.razor
    /// and a second literal in the client PDF renderer, agreeing only because one author wrote both.
    /// </summary>
    [Fact]
    public void An_absent_reading_is_written_the_same_way_wherever_it_is_rendered()
    {
        IndexAnalysisRendering.Unmeasured.Should().Be("not measured");

        IndexAnalysisRendering.Number((decimal?)null, "N0").Should().Be("not measured");
        IndexAnalysisRendering.Number((double?)null, "N2").Should().Be("not measured");
        IndexAnalysisRendering.Percent((decimal?)null, "F2").Should().Be("not measured");
        IndexAnalysisRendering.Percent((double?)null, "F2").Should().Be("not measured");

        // ⚠ The percent sign is INSIDE the helper. The page used to write it in the markup beside
        // the value, which on a null would have produced "not measured%".
        IndexAnalysisRendering.Percent((decimal?)null, "F2").Should().NotEndWith("%");
        IndexAnalysisRendering.Percent(12.5m, "F2").Should().Be(12.5m.ToString("F2") + "%");

        // A measured ZERO is still a measurement and must survive as one. Without this the fix
        // could be "print the label whenever the number is falsy", which loses a real reading.
        IndexAnalysisRendering.Number(0m, "N0").Should().Be(0m.ToString("N0"));
        IndexAnalysisRendering.Percent(0d, "F2").Should().Be(0d.ToString("F2") + "%");
    }

    /// <summary>
    /// ⚠ THE HELPER THAT MANUFACTURED THE MEASUREMENT IS GONE, and this is what keeps it gone. A
    /// <c>Dec</c> that substitutes 0m for a NULL is the shape a future mapper author would reach
    /// for by name; only <c>DecOrNull</c> remains, and it forces the caller to decide how to say
    /// "not measured".
    /// </summary>
    [Fact]
    public void The_service_offers_no_decimal_reader_that_turns_a_null_into_a_zero()
    {
        var decimalReaders = typeof(IndexAnalysisService)
            .GetMethods(System.Reflection.BindingFlags.Static
                        | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Public)
            .Where(m => m.ReturnType == typeof(decimal))
            .Select(m => m.Name)
            .ToList();

        decimalReaders.Should().BeEmpty(
            "a decimal-returning reader helper cannot represent an absent reading, so it can only "
            + $"invent one; found: {string.Join(", ", decimalReaders)}");

        typeof(IndexAnalysisService)
            .GetMethod("DecOrNull", System.Reflection.BindingFlags.Static
                                    | System.Reflection.BindingFlags.NonPublic
                                    | System.Reflection.BindingFlags.Public)
            .Should().NotBeNull("the nullable reader is the one the mappers must use");
    }

    [Fact]
    public void The_usage_window_mapper_reads_the_start_time_and_the_scope_database()
    {
        var row = FirstRow("usageWindow");
        var (startTime, scopeDb) = IndexAnalysisService.MapUsageWindow(row);

        startTime.Should().NotBeNull("sys.dm_os_sys_info always reports a start time");
        startTime!.Value.Should().BeBefore(DateTime.Now);
        scopeDb.Should().NotBeNullOrWhiteSpace(
            "the empty string is the page's 'not measured' signal and must not be produced by a "
            + "successful read");
    }

    // ── The ranking pass, 2026-08-13 ─────────────────────────────────────────────────────────
    //
    // The page reads the 20 busiest user databases by IO instead of the one it connected to.
    // These replay the ranking pass's OWN captured shapes: DatabaseRankingSql projects from table
    // variables rather than straight from a DMV, so its declared types are the service's to get
    // right and a hand-written row would simply agree with whatever the mapper assumed.

    [Fact]
    public void The_capture_holds_a_ranking_wide_enough_to_prove_anything_about()
    {
        var rows = Rows("dbRanking");
        rows.Should().HaveCountGreaterThan(2,
            "a one-row ranking cannot show an order, a cut, or a skip");

        var census = IndexAnalysisService.MapRankingCensus(FirstRow("dbRankingCensus"));
        census.TopN.Should().Be(IndexAnalysisService.TopDatabasesByIo);
        census.UserDatabases.Should().BeGreaterThan(census.TopN,
            "the capture was taken against an instance with MORE user databases than the sample "
            + "size, which is the only shape where a top-20 cut is observable at all");
        census.Sampled.Should().Be(census.TopN);
        census.UserDatabases.Should().Be(census.Eligible + census.Ineligible);
        census.Ineligible.Should().BeGreaterThan(0,
            "the capture must include at least one database that could not be scanned, or the "
            + "skip assertions below prove nothing");

        // The capped case, which is the shape the page's counts got wrong. Without it every
        // truncation assertion below would be checking a branch this capture never enters.
        census.PassedOver.Should().BeGreaterThan(census.SkipReportCap,
            "the capture must hold MORE passed-over databases than the skip list can show, or "
            + "nothing here exercises the cap that made 'This run did not read N databases' false");
    }

    /// <summary>
    /// THE PARTITION the page's arithmetic stands on. Every user database is picked, reported, or
    /// out-ranked on a reading it earned — never a fourth thing, and never two at once.
    ///
    /// <para>This is the invariant the first cut broke. An AUTO_CLOSE database is ELIGIBLE and has
    /// no IO reading, and the reported set was gated on <c>is_eligible = 0</c>, so it belonged to
    /// no bucket at all: not scanned, not listed, and silently counted inside the page's "ranked
    /// below the cut" figure — a rank it was given by the absence of the very measurement it would
    /// have been ranked on.</para>
    /// </summary>
    [Fact]
    public void The_census_partitions_every_user_database_exactly_once()
    {
        var census = IndexAnalysisService.MapRankingCensus(FirstRow("dbRankingCensus"));

        (census.Sampled + census.PassedOver + census.RankedBelowCut)
            .Should().Be(census.UserDatabases,
                "picked, reported and out-ranked must partition the instance — if they do not, "
                + "every total the page renders is arithmetic over overlapping sets");

        census.RankedBelowCut.Should().BeLessThanOrEqualTo(census.UserDatabases - census.Sampled);
    }

    [Fact]
    public void The_scan_rows_map_in_descending_io_order_and_are_marked_eligible()
    {
        var mapped = Rows("dbRanking")
            .Select(IndexAnalysisService.MapRankedDatabase)
            .Where(x => x.Kind == IndexAnalysisService.RankRowKindScan)
            .Select(x => x.Database)
            .ToList();

        mapped.Should().NotBeEmpty();
        mapped.Should().OnlyContain(d => d.IsEligible);
        mapped.Should().OnlyContain(d => d.IoMeasured,
            "a database that is online enough to scan has open files, so the DMV has a row for it");
        mapped.Should().OnlyContain(d => d.StateDesc == "ONLINE" && d.UserAccessDesc == "MULTI_USER" && !d.IsReadOnly,
            "the house predicate admits nothing else");
        mapped.Select(d => d.IoOperations).Should().BeInDescendingOrder(
            "the sample is the BUSIEST databases, so the rows must arrive busiest first");
        mapped.Select(d => d.Name).Should().OnlyHaveUniqueItems();
        mapped[0].IoRank.Should().Be(1, "the busiest selected database is rank one of the estate");
    }

    /// <summary>
    /// The offline row, and the reason it is in the capture at all. An OFFLINE database has no
    /// open files, so sys.dm_io_virtual_file_stats returns NOTHING for it and the LEFT JOIN yields
    /// NULL — which ISNULL turns into a zero that sorts it dead last. The first cut of this service
    /// reported a skipped database only when it out-ranked the last one picked, so on an instance
    /// with more than twenty user databases an offline one was ALWAYS ranked bottom by a zero it
    /// never earned, and was therefore never listed at all. Found live, 2026-08-13.
    /// </summary>
    [Fact]
    public void An_unscannable_database_is_returned_with_its_state_and_its_io_marked_unmeasured()
    {
        var skipped = Rows("dbRanking")
            .Select(IndexAnalysisService.MapRankedDatabase)
            .Where(x => x.Kind == IndexAnalysisService.RankRowKindSkippedByState)
            .Select(x => x.Database)
            .ToList();

        skipped.Should().NotBeEmpty(
            "the capture was taken with an offline database on the instance; if this is empty the "
            + "capture no longer exercises the case it was regenerated for");
        skipped.Should().OnlyContain(d => !d.IsEligible);

        var offline = skipped.First(d => d.StateDesc == "OFFLINE");
        offline.IoMeasured.Should().BeFalse(
            "the DMV returned no row for it, and that absence must not be carried as a reading");
        offline.IoOperations.Should().Be(0, "the ORDER BY needed a number; it is not a measurement");

        var census = IndexAnalysisService.MapRankingCensus(FirstRow("dbRankingCensus"));
        offline.IoRank.Should().BeGreaterThan(census.TopN,
            "this is the whole point: its apparent rank is BELOW the cut, and it is reported "
            + "anyway, because that rank was manufactured by the absence of the reading");

        IndexAnalysisService.DescribePassedOver(IndexAnalysisService.RankRowKindSkippedByState, offline)
            .Should().Be("Not scanned. The database is OFFLINE.",
                "the kind picks the sentence, and an ineligible database is named by its state");
    }

    /// <summary>
    /// THE AUTO_CLOSE CASE, from the capture that now holds one. The database is ONLINE,
    /// MULTI_USER and writable — so the house predicate ADMITS it — and sys.dm_io_virtual_file_stats
    /// still returned no row for it, because AUTO_CLOSE shuts a database's files when the last
    /// connection leaves. The first cut gated the manufactured-zero rescue on
    /// <c>is_eligible = 0</c>, so this database was neither picked nor reported and vanished from
    /// the page entirely, while the page's own sentence counted it among the databases that
    /// "ranked below the cut". PROVED absent live on .\old2017, 2026-08-13, end to end through the
    /// built service, before this row kind existed.
    /// </summary>
    [Fact]
    public void An_eligible_database_with_no_io_reading_is_reported_as_unranked_not_as_broken()
    {
        var unranked = Rows("dbRanking")
            .Select(IndexAnalysisService.MapRankedDatabase)
            .Where(x => x.Kind == IndexAnalysisService.RankRowKindSkippedUnranked)
            .Select(x => x.Database)
            .ToList();

        unranked.Should().NotBeEmpty(
            "the capture was taken with an AUTO_CLOSE user database on the instance; if this is "
            + "empty the capture no longer exercises the defect it was regenerated for");

        foreach (var db in unranked)
        {
            db.IoMeasured.Should().BeFalse("this row kind exists precisely for the absent reading");
            db.IoOperations.Should().Be(0, "the ORDER BY needed a number; it is not a measurement");

            // It passed the house predicate. That is what makes it different from every other
            // row in the skip list, and what the first cut got wrong.
            db.IsEligible.Should().BeTrue(
                "an unranked database is ELIGIBLE — reading eligibility off 'kind == SCAN' would "
                + "report it as failing a predicate it actually passed");
            db.StateDesc.Should().Be("ONLINE");
            db.UserAccessDesc.Should().Be("MULTI_USER");
            db.IsReadOnly.Should().BeFalse();

            IndexAnalysisService
                .DescribePassedOver(IndexAnalysisService.RankRowKindSkippedUnranked, db)
                .Should().Be(
                    "Not scanned. Its files are not open, so no IO was measured for it and the "
                    + "ranking could not place it. A database with AUTO_CLOSE on reads like this.",
                    "the page renders this verbatim; naming a STATE here would name one the "
                    + "database is not in");
        }

        // ...and the negative control: the state sentence this row must NOT get. DescribeStateSkip
        // over an ONLINE, MULTI_USER, writable database falls through to the HADR arm, which is
        // what an unranked database would have been told if the kind did not pick the sentence.
        IndexAnalysisService.DescribeStateSkip(unranked[0])
            .Should().Be("Not scanned. This is a secondary replica that does not allow connections.",
                "this is the wrong sentence, kept as the control: if DescribePassedOver ever stops "
                + "branching on the row kind, the unranked rows silently get THIS instead");
    }

    [Fact]
    public void The_ranking_row_kind_decides_eligibility_rather_than_a_second_column()
    {
        foreach (var row in Rows("dbRanking"))
        {
            var (kind, db) = IndexAnalysisService.MapRankedDatabase(row);
            kind.Should().BeOneOf(IndexAnalysisService.RankRowKindScan,
                                  IndexAnalysisService.RankRowKindSkippedByState,
                                  IndexAnalysisService.RankRowKindSkippedUnranked);
            // NOT `kind == SCAN`: a SKIP-UNRANKED row is an eligible database the ranking could
            // not place, and calling it ineligible inverts what the SQL decided about it.
            db.IsEligible.Should().Be(kind != IndexAnalysisService.RankRowKindSkippedByState);
            db.Name.Should().NotBeNullOrWhiteSpace();
            db.DatabaseId.Should().BeGreaterThan(4, "system databases are never ranked");
        }
    }

    /// <summary>
    /// The list the page shows is CAPPED, and the capture proves the cap is a cap: the rows
    /// returned stop at SkipReportCap while the census counts more. Nothing may render that list
    /// length as a measurement of what the run did not read.
    /// </summary>
    [Fact]
    public void The_passed_over_rows_stop_at_the_cap_while_the_census_carries_the_true_total()
    {
        var census = IndexAnalysisService.MapRankingCensus(FirstRow("dbRankingCensus"));

        var passedOver = Rows("dbRanking")
            .Select(IndexAnalysisService.MapRankedDatabase)
            .Where(x => x.Kind != IndexAnalysisService.RankRowKindScan)
            .ToList();

        passedOver.Should().HaveCount(census.SkipReportCap,
            "the SQL takes TOP (@skip_report_cap) over BOTH passed-over kinds together");
        census.PassedOver.Should().BeGreaterThan(passedOver.Count,
            "the capture's instance has more passed-over databases than the list holds, which is "
            + "the only shape where the truncation is observable");
    }

    // ── The skip sentences ───────────────────────────────────────────────────────────────────
    //
    // These strings are rendered verbatim on the page, so they are asserted verbatim. The house
    // defect class is a sentence beside a verdict that was not conditioned on the same
    // measurement; the defence is that the sentence and the measurement come from one function.

    [Theory]
    [InlineData("OFFLINE", "MULTI_USER", false, "Not scanned. The database is OFFLINE.")]
    [InlineData("RESTORING", "MULTI_USER", false, "Not scanned. The database is RESTORING.")]
    [InlineData("RECOVERY_PENDING", "MULTI_USER", false, "Not scanned. The database is RECOVERY_PENDING.")]
    [InlineData("SUSPECT", "MULTI_USER", false, "Not scanned. The database is SUSPECT.")]
    [InlineData("ONLINE", "SINGLE_USER", false, "Not scanned. Access is restricted to SINGLE_USER.")]
    [InlineData("ONLINE", "RESTRICTED_USER", false, "Not scanned. Access is restricted to RESTRICTED_USER.")]
    [InlineData("ONLINE", "MULTI_USER", true, "Not scanned. The database is read-only.")]
    public void Each_unscannable_state_produces_the_sentence_that_names_it(
        string state, string access, bool readOnly, string expected)
        => IndexAnalysisService.DescribeStateSkip(new IndexAnalysisDatabase
        {
            Name = "db", StateDesc = state, UserAccessDesc = access, IsReadOnly = readOnly,
        }).Should().Be(expected);

    /// <summary>
    /// The last arm of the eligibility predicate is a non-readable HADR secondary, and there is no
    /// column on sys.databases that shows it. The sentence must say THAT, not invent a state.
    /// </summary>
    [Fact]
    public void A_database_that_is_healthy_and_still_ineligible_is_named_as_a_closed_secondary()
        => IndexAnalysisService.DescribeStateSkip(new IndexAnalysisDatabase
        {
            Name = "db", StateDesc = "ONLINE", UserAccessDesc = "MULTI_USER", IsReadOnly = false,
        }).Should().Be("Not scanned. This is a secondary replica that does not allow connections.");

    [Fact]
    public void The_budget_sentence_quotes_the_budget_the_run_was_actually_given()
    {
        IndexAnalysisService.DescribeBudgetSkip(TimeSpan.FromSeconds(120))
            .Should().Be("Not scanned. The 120-second budget for this run ran out first.");
        IndexAnalysisService.DescribeBudgetSkip(TimeSpan.FromSeconds(30))
            .Should().Be("Not scanned. The 30-second budget for this run ran out first.",
                "a page that quotes the default while the run used something else is the house "
                + "defect class in one sentence");
    }

    [Fact]
    public void A_timeout_is_named_as_the_slice_and_anything_else_carries_the_servers_own_words()
    {
        IndexAnalysisService.DescribeScanFailure(new TimeoutException("x"), TimeSpan.FromSeconds(15))
            .Should().Be("Scan stopped. It passed its 15-second slice, so the rest of this database was not read.");

        IndexAnalysisService.DescribeScanFailure(
                new InvalidOperationException("Login failed for user 'x'."), TimeSpan.FromSeconds(15))
            .Should().Be("Not scanned. Login failed for user 'x'.",
                "a guess about why a database would not open is worth less than the sentence the "
                + "server already wrote");
    }

    /// <summary>
    /// THE SLICE IS ONE BUDGET FOR THE DATABASE, not one per command. Both reads used to be handed
    /// the same CommandTimeout, so a database could spend 2 × 15s while the sentence above told the
    /// operator it had "passed its 15-second slice" — a duration nobody spent (gate finding,
    /// 2026-08-13). ⚠ THIS PROVES THE ARITHMETIC ONLY. No database here is large enough to time out
    /// a LIMITED-mode sweep, so the cancellation the number feeds remains UNEXERCISED.
    /// </summary>
    [Theory]
    [InlineData(15, 0, 15)]      // nothing spent yet: the full slice
    [InlineData(15, 4, 11)]      // four seconds gone
    [InlineData(15, 14.2, 1)]    // sub-second remainder rounds UP to one, never down to zero
    [InlineData(15, 15, 1)]      // exactly spent
    [InlineData(15, 90, 1)]      // overspent — still 1, because 0 means NO LIMIT in SqlClient
    public void The_second_read_of_a_database_only_gets_what_the_slice_has_left(
        double sliceSeconds, double elapsedSeconds, int expected)
        => IndexAnalysisService.RemainingSliceSeconds(
                TimeSpan.FromSeconds(sliceSeconds), TimeSpan.FromSeconds(elapsedSeconds))
            .Should().Be(expected);

    [Fact]
    public void A_spent_slice_never_becomes_an_unlimited_command_timeout()
        => IndexAnalysisService.RemainingSliceSeconds(TimeSpan.FromSeconds(15), TimeSpan.FromDays(1))
            .Should().BeGreaterThan(0,
                "CommandTimeout = 0 means NO LIMIT in SqlClient, so a slice that has run out would "
                + "silently become unbounded — the fail-safe inverting into the failure it exists "
                + "to prevent");

    // ── The scope sentences the page renders ─────────────────────────────────────────────────
    //
    // These moved out of Pages/IndexAnalysis.razor on 2026-08-13 after the gate proved three of
    // them stated numbers the run never measured. They are asserted VERBATIM because that is what
    // an operator reads, and against a census rather than against list lengths because a list
    // length is exactly what made them false.

    private static IndexAnalysisRankingCensus CapturedCensus()
        => IndexAnalysisService.MapRankingCensus(FirstRow("dbRankingCensus"));

    /// <summary>
    /// The three broken sentences, over the CAPTURED census — the same instance shape that produced
    /// them. 47 user databases, 20 scanned, a skip list capped at 20. The old page rendered "20
    /// scanned, 20 skipped" and "This run did not read 20 databases" against a run that had not
    /// read 27.
    /// </summary>
    [Fact]
    public void The_scope_sentences_count_the_instance_and_never_the_capped_list()
    {
        var census = CapturedCensus();
        var scope = IndexAnalysisScopeNarrative.For(census, scannedCount: 20, skippedListCount: 20);

        scope.NotRead.Should().Be(census.UserDatabases - 20,
            "'did not read' is a count over the INSTANCE; the list is a subset of it");
        scope.NotRead.Should().BeGreaterThan(20,
            "if this is not above the list length, the capture stopped exercising the defect");

        scope.Headline.Should().Be(
            $"Unused and fragmented indexes: the 20 busiest user databases by IO. "
            + $"20 of {census.UserDatabases} user databases read.");
        scope.Headline.Should().NotContain("skipped",
            "'N scanned, M skipped' invited an operator to add two list lengths into an instance "
            + "total the run never measured");

        scope.SkippedHeadline.Should().Be(
            $"This run did not read {scope.NotRead} of the {census.UserDatabases} user databases "
            + "on this instance.");

        scope.MetricSentence.Should().EndWith(
            $"{census.RankedBelowCut} user databases were left out on a measured IO figure below the cut.");
        scope.MetricSentence.Should().NotContain("ranked below the cut and were not read",
            "the old wording counted every eligible database the sample missed, including the ones "
            + "whose IO was never read at all");
    }

    /// <summary>
    /// The unlisted count is exact, and it decomposes into the two causes without either being
    /// asserted as the other. The old sentence said the unlisted databases "ranked into this
    /// sample" and that the list showed "the busiest 20" — both claims about an ordering that, for
    /// unmeasured databases, is an alphabetical tiebreak on manufactured zeros.
    /// </summary>
    [Fact]
    public void The_unlisted_count_is_exact_and_names_both_causes_without_confusing_them()
    {
        var census = CapturedCensus();
        var scope = IndexAnalysisScopeNarrative.For(census, scannedCount: 20, skippedListCount: 20);

        var truncated = census.PassedOver - census.SkipReportCap;
        scope.TruncatedCount.Should().Be(truncated).And.BeGreaterThan(0);
        scope.UnlistedCount.Should().Be(truncated + census.RankedBelowCut,
            "the count the page prints must decompose exactly into the rows the cap cut and the "
            + "databases that lost the sample on a reading they earned — nothing else is left over");

        scope.UnlistedSentence.Should().Be(
            $"{scope.UnlistedCount} more user databases were not read and are not named above: "
            + $"{truncated} passed over beyond the {census.SkipReportCap} this list holds, and "
            + $"{census.RankedBelowCut} that lost the sample on a measured IO figure.");

        scope.UnlistedSentence.Should().NotContain("busiest",
            "every unmeasured row carries io_ops = 0, so which of them survive the cap is an "
            + "alphabetical tiebreak, not a finding about how busy they are");
        scope.UnlistedSentence.Should().NotContain("ranked into this sample",
            "they did not rank into anything — that is the whole reason they are reported");
    }

    [Theory]
    // scanned, listed, userDbs, sampled, passedOver, belowCut, cap  → expected sentence
    [InlineData(20, 20, 47, 20, 23, 4, 20,
        "7 more user databases were not read and are not named above: 3 passed over beyond the 20 this list holds, and 4 that lost the sample on a measured IO figure.")]
    [InlineData(20, 21, 44, 20, 21, 3, 20,
        "3 more user databases were not read and are not named above: 1 passed over beyond the 20 this list holds, and 2 that lost the sample on a measured IO figure.")]
    [InlineData(20, 20, 41, 20, 21, 0, 20,
        "1 more user database was not read and is not named above. This list holds 20 of the databases the run passed over.")]
    [InlineData(20, 2, 27, 20, 2, 5, 20,
        "5 more user databases were not read and are not named above. They lost the sample on a measured IO figure, so the line above counts them rather than naming them.")]
    public void Every_unlisted_branch_names_the_whole_of_the_count_it_opens_with(
        int scanned, int listed, int userDbs, int sampled, int passedOver, int belowCut, int cap,
        string expected)
    {
        var scope = IndexAnalysisScopeNarrative.For(
            new IndexAnalysisRankingCensus
            {
                TopN = 20, UserDatabases = userDbs, Sampled = sampled,
                PassedOver = passedOver, RankedBelowCut = belowCut, SkipReportCap = cap,
                Eligible = userDbs, Ineligible = 0,
            }, scanned, listed);

        // The composed census must itself partition, or this Theory is asserting prose over
        // arithmetic that could never occur.
        (sampled + passedOver + belowCut).Should().Be(userDbs);

        scope.UnlistedSentence.Should().Be(expected);
    }

    [Fact]
    public void Nothing_is_unlisted_when_the_page_names_every_database_the_run_did_not_read()
    {
        var scope = IndexAnalysisScopeNarrative.For(
            new IndexAnalysisRankingCensus
            {
                TopN = 20, UserDatabases = 23, Sampled = 20,
                PassedOver = 3, RankedBelowCut = 0, SkipReportCap = 20,
            }, scannedCount: 20, skippedListCount: 3);

        scope.NotRead.Should().Be(3);
        scope.UnlistedCount.Should().Be(0, "the list holds all three, so there is nothing to disclose");
        scope.MetricSentence.Should().EndWith("No user database was left out of the sample on rank alone.");
    }

    /// <summary>
    /// The per-database row caps, DISCLOSED. TOP 50 and TOP 30 are unchanged from the base commit,
    /// but they now run once per database, so a populated table is up to twenty independently
    /// truncated lists laid end to end. ⚠ A disclosed cap that has drifted from the query is worse
    /// than an undisclosed one, so the constants are pinned to the SQL text here.
    /// </summary>
    [Fact]
    public void The_row_caps_the_page_discloses_are_the_caps_the_queries_actually_apply()
    {
        IndexAnalysisService.UnusedIndexSql.Should().Contain(
            $"SELECT TOP {IndexAnalysisService.UnusedRowsPerDatabase}",
            "the disclosed per-database cap must be the number in the query");
        IndexAnalysisService.FragIndexSql.Should().Contain(
            $"SELECT TOP {IndexAnalysisService.FragmentedRowsPerDatabase}");
        IndexAnalysisService.MissingIndexSql.Should().Contain(
            $"SELECT TOP {IndexAnalysisService.MissingIndexRowCap}");

        var scope = IndexAnalysisScopeNarrative.For(CapturedCensus(), scannedCount: 20, skippedListCount: 20);

        scope.TableCoverage(IndexAnalysisService.UnusedRowsPerDatabase, "unused-index rows")
            .Should().Contain("Each database contributes at most 50 unused-index rows")
            .And.Contain("rather than a ranking across the instance")
            .And.Contain($"It says nothing about the other {scope.NotRead} user databases on this instance.");

        IndexAnalysisScopeNarrative.MissingSentence
            .Should().NotContain("covers every user database",
                "the read is capped AND the missing-index DMVs hold a suggestion only for a query "
                + "the optimizer actually costed, so an absent database is unmentioned, not cleared")
            .And.Contain("capped at the 50 highest-impact suggestions");
    }

    [Fact]
    public void A_run_that_read_nothing_claims_nothing_about_the_tables()
        => IndexAnalysisScopeNarrative
            .For(CapturedCensus(), scannedCount: 0, skippedListCount: 20)
            .TableCoverage(50, "unused-index rows")
            .Should().Be("This run read no database, so this table describes nothing on this instance.");

    /// <summary>
    /// ⚠⚠ NO UI-SURFACE NOUNS IN THE SHARED SENTENCES, 2026-08-14 (gate finding). This type is
    /// rendered by TWO surfaces: Pages/IndexAnalysis.razor, where the three lists are tabs, and the
    /// full-private client PDF, where they are printed sections. The sentences said "tab" — true on
    /// the screen and false on paper, and the paper one is what a paying client forwards. Pointing
    /// a reader at a control they cannot see is the same class as any other sentence asserting more
    /// than the run measured, so the shared narrative may name only what both surfaces hold: rows,
    /// tables, databases and reads.
    ///
    /// <para>This is the structural half of the fix. The sentences are one source, so the check has
    /// to sit on the source; it runs in BOTH shipped suites, while the PDF that motivated it
    /// compiles in neither.</para>
    /// </summary>
    [Fact]
    public void No_shared_scope_sentence_points_at_a_screen_control_the_pdf_reader_cannot_see()
    {
        var scope = IndexAnalysisScopeNarrative.For(CapturedCensus(), scannedCount: 20, skippedListCount: 20);

        var sentences = new[]
        {
            scope.Headline,
            scope.MetricSentence,
            scope.SkippedHeadline,
            scope.UnlistedSentence,
            scope.TableCoverage(IndexAnalysisService.UnusedRowsPerDatabase, "unused-index rows"),
            scope.TableCoverage(IndexAnalysisService.FragmentedRowsPerDatabase, "fragmented-index rows"),
            IndexAnalysisScopeNarrative
                .For(CapturedCensus(), scannedCount: 0, skippedListCount: 0)
                .TableCoverage(IndexAnalysisService.UnusedRowsPerDatabase, "unused-index rows"),
            IndexAnalysisScopeNarrative.MissingSentence,
        };

        foreach (var sentence in sentences)
            sentence.Should().NotMatchRegex(@"\btabs?\b",
                "these strings render verbatim into a printed client report, where there is no tab "
                + $"to consult — offending sentence: \"{sentence}\"");

        IndexAnalysisScopeNarrative.MissingSentence
            .Should().StartWith("Missing indexes are read separately.",
                "it used to open 'The Missing Indexes tab is separate', which named the screen's "
                + "chrome instead of the read whose scope it is describing");
    }

    // ── The two instance-wide reads, when they fail, 2026-08-14 ──────────────────────────────
    //
    // The ranking read was the only uncaught command on this path. A fifteen-second timeout on it
    // replaced the whole /index-analysis page with a red banner, and in the client report it hit
    // ComposeAsync's live-collection catch, which marks the entire INSTANCE Succeeded=false and
    // returns -- so one slow ranking read also cost that report its disk section, its hotspots, its
    // maintenance advice and its capacity page. The read now degrades.
    //
    // ⚠ THE TWO HALVES ARE SEPARATE PROPERTIES, and both are asserted below, because getting one
    // right and the other wrong is worse than the crash:
    //   1. NO FALLBACK. The selection stays EMPTY, so the run scans nothing. The shape this must
    //      never become is "fall back to whatever database the connection landed in" while the
    //      surfaces claim a top-20 sweep, which is the silent truncation the 2026-08-13 lane
    //      existed to remove and the reason the throw was deliberate in the first place.
    //   2. NO SILENCE. The failure is a sentence on the result. Every table renders empty either
    //      way, so that sentence is the only thing separating "read twenty, found nothing" from
    //      "read nothing".
    //
    // The seam is a delegate for the same reason ApplySelectionAsync's is: a failure branch whose
    // only exercise is a real server that happens to time out is a branch nobody tests.

    private static IndexAnalysisService Svc()
        => new(utcNow: () => new DateTime(2026, 8, 14, 9, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task A_ranking_read_that_times_out_selects_nothing_and_says_so()
    {
        var outcome = await Svc().GuardedRankingAsync(
            _ => throw new TimeoutException("Execution Timeout Expired."), default);

        outcome.Failure.Should().Be(
            "The database ranking read did not finish inside its 15-second limit, so this run chose "
            + "no database to scan. The index tables below are empty because nothing was read, not "
            + "because nothing was found.");

        outcome.Sample.Should().BeEmpty(
            "an empty sample scans nothing; a fallback to the connected database would be the "
            + "silent truncation the throw was there to prevent");
        outcome.PassedOver.Should().BeEmpty();
        outcome.Census.UserDatabases.Should().Be(0,
            "a census nobody took must stay zero, because that is what makes the narrative refuse "
            + "to quote an instance-wide total");
    }

    /// <summary>
    /// The quoted limit is the one the command was actually given. This is the house defect class
    /// in its most literal form, and it has already happened once on this file with the
    /// per-database slice, which is why the timeouts became named constants.
    /// </summary>
    [Fact]
    public void The_degraded_sentences_quote_the_timeouts_the_commands_are_given()
    {
        IndexAnalysisService.RankingCommandTimeoutSeconds.Should().Be(15);
        IndexAnalysisService.MissingIndexCommandTimeoutSeconds.Should().Be(30);

        IndexAnalysisService.DescribeRankingFailure(new TimeoutException("x"))
            .Should().Contain($"{IndexAnalysisService.RankingCommandTimeoutSeconds}-second limit");
        IndexAnalysisService.DescribeMissingReadFailure(new TimeoutException("x"))
            .Should().Contain($"{IndexAnalysisService.MissingIndexCommandTimeoutSeconds}-second limit");
    }

    [Fact]
    public async Task A_ranking_read_that_fails_for_any_other_reason_carries_the_servers_own_words()
    {
        var outcome = await Svc().GuardedRankingAsync(
            _ => throw new InvalidOperationException("The SELECT permission was denied on the object 'dm_io_virtual_file_stats'."),
            default);

        outcome.Failure.Should().Be(
            "The database ranking read failed, so this run chose no database to scan. The index "
            + "tables below are empty because nothing was read, not because nothing was found. "
            + "The SELECT permission was denied on the object 'dm_io_virtual_file_stats'.");
        outcome.Failure.Should().NotContain("15-second",
            "a permission failure is not a timeout, and naming a limit it did not hit would send "
            + "an operator to tune a timeout over a grant they are missing");
    }

    /// <summary>
    /// A CANCELLED run is not a degraded read and must not be dressed as one. If the caller stopped
    /// the work, saying "the ranking read failed" invents a server-side problem that did not occur.
    /// </summary>
    [Fact]
    public async Task A_cancelled_ranking_read_still_propagates_rather_than_becoming_a_failure_sentence()
    {
        var svc = Svc();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.GuardedRankingAsync(_ => throw new OperationCanceledException(), default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.GuardedMissingAsync(_ => throw new OperationCanceledException(), default));
    }

    [Fact]
    public async Task A_ranking_read_that_succeeds_reports_no_failure_at_all()
    {
        var census = CapturedCensus();
        var selection = Selection(3);

        var outcome = await Svc().GuardedRankingAsync(
            _ => Task.FromResult((selection,
                                  new List<(string, IndexAnalysisDatabase)>(),
                                  census)),
            default);

        outcome.Failure.Should().BeNull("a healthy run must carry no failure sentence to render");
        outcome.Sample.Should().HaveCount(3);
        outcome.Census.UserDatabases.Should().Be(census.UserDatabases);
    }

    // ── The missing-index read, which is independent of the ranking one ──────────────────────

    [Fact]
    public async Task A_missing_index_read_that_times_out_loses_only_its_own_list()
    {
        var outcome = await Svc().GuardedMissingAsync(
            _ => throw new TimeoutException("Execution Timeout Expired."), default);

        outcome.Rows.Should().BeEmpty();
        outcome.Failure.Should().Be(
            "The missing-index read did not finish inside its 30-second limit, so no suggestion was "
            + "read on this run. That is not the same as the optimizer having no suggestion to give.");
    }

    [Fact]
    public async Task A_missing_index_read_that_succeeds_reports_no_failure_at_all()
    {
        var rows = new List<IndexAnalysisMissingRow> { new() { Database = "A" } };

        var outcome = await Svc().GuardedMissingAsync(_ => Task.FromResult(rows), default);

        outcome.Failure.Should().BeNull();
        outcome.Rows.Should().BeSameAs(rows);
    }

    /// <summary>
    /// THE DEGRADED STATE, RENDERED. With no census the narrative already refuses to quote any
    /// instance-wide total, which is what makes an empty result safe to render at all — that branch
    /// predates this change and is what the degradation leans on. Asserted here so a future edit
    /// cannot make the narrative "helpfully" fall back to the service's default sample size and
    /// start describing an instance nobody counted.
    /// </summary>
    [Fact]
    public void A_run_whose_ranking_failed_states_no_instance_wide_total()
    {
        var scope = IndexAnalysisScopeNarrative.For(
            new IndexAnalysisRankingCensus(), scannedCount: 0, skippedListCount: 0);

        scope.HasCensus.Should().BeFalse("nothing was counted, so nothing may be quoted");
        scope.NotRead.Should().Be(0, "a count over an instance nobody surveyed is not a number");
        scope.UnlistedCount.Should().Be(0);
        scope.Headline.Should().Be(
            "Unused and fragmented indexes: the 20 busiest user databases by IO. 0 databases read.");
        scope.Headline.Should().NotContain(" of ",
            "with no census there is no instance total to render '0 of N' against");
        scope.TableCoverage(IndexAnalysisService.UnusedRowsPerDatabase, "unused-index rows")
            .Should().Be("This run read no database, so this table describes nothing on this instance.");
    }

    // ── The budget loop ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Six databases, a clock that expires after three, and a scanner that never touches SQL. The
    /// live harness runs this against a real instance; this runs every branch deterministically in
    /// milliseconds, including the ordering of the two lists.
    /// </summary>
    [Fact]
    public async Task When_the_budget_runs_out_the_rest_are_listed_and_the_reached_ones_are_kept()
    {
        var start = new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc);
        var budget = TimeSpan.FromSeconds(120);

        var calls = 0;
        DateTime Clock() { calls++; return calls <= 3 ? start : start + budget + TimeSpan.FromSeconds(1); }

        var svc = new IndexAnalysisService(utcNow: Clock, overallBudget: budget);
        var result = new IndexAnalysisResult { OverallBudget = budget };
        var selection = Selection(6);
        var scanned = new List<string>();

        await svc.ApplySelectionAsync(selection, start + budget, (db, slice, _) =>
        {
            scanned.Add(db.Name);
            slice.Should().Be(IndexAnalysisService.PerDatabaseSliceDefault);
            return Task.FromResult(new IndexAnalysisService.DatabaseScanOutcome
            {
                Fragmented = { new IndexAnalysisFragRow { Database = db.Name } },
            });
        }, result, default);

        scanned.Should().Equal("db1", "db2", "db3");
        result.Scanned.Select(d => d.Name).Should().Equal("db1", "db2", "db3");
        result.BudgetExhausted.Should().BeTrue();

        result.Skipped.Select(s => s.Database).Should().Equal("db4", "db5", "db6");
        result.Skipped.Should().OnlyContain(
            s => s.Reason == "Not scanned. The 120-second budget for this run ran out first.");

        // The rows from the databases it DID reach survive: partial is not empty.
        result.Fragmented.Select(f => f.Database).Should().Equal("db1", "db2", "db3");

        // Nothing vanished. Every selected database is in exactly one of the two lists.
        result.Scanned.Select(d => d.Name).Concat(result.Skipped.Select(s => s.Database))
            .Should().BeEquivalentTo(selection.Select(d => d.Name));
    }

    [Fact]
    public async Task A_database_whose_scan_fails_is_named_and_its_partial_rows_are_not_merged()
    {
        var svc = new IndexAnalysisService(utcNow: () => new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc));
        var result = new IndexAnalysisResult();

        await svc.ApplySelectionAsync(Selection(3), DateTime.MaxValue, (db, slice, _) =>
            Task.FromResult(db.Name == "db2"
                ? new IndexAnalysisService.DatabaseScanOutcome
                {
                    // A half-read database offers rows AND a failure. The rows must not survive:
                    // an absence inside a scan that never finished means nothing, and mixing them
                    // into the table makes the page's counts uncountable.
                    Unused = { new IndexAnalysisUnusedRow { Database = db.Name } },
                    Failure = "Not scanned. Login failed for user 'x'.",
                }
                : new IndexAnalysisService.DatabaseScanOutcome
                {
                    Unused = { new IndexAnalysisUnusedRow { Database = db.Name } },
                }), result, default);

        result.Scanned.Select(d => d.Name).Should().Equal("db1", "db3");
        result.Unused.Select(u => u.Database).Should().Equal("db1", "db3");
        result.Skipped.Should().ContainSingle()
            .Which.Reason.Should().Be("Not scanned. Login failed for user 'x'.");
        result.BudgetExhausted.Should().BeFalse("a failed scan is not a budget failure");
    }

    /// <summary>The last database must not be handed a slice longer than the budget has left.</summary>
    [Fact]
    public async Task The_slice_never_outruns_what_is_left_of_the_overall_budget()
    {
        var start = new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc);
        var budget = TimeSpan.FromSeconds(120);
        var deadline = start + budget;

        // Four seconds left when the first database is considered — less than a full slice.
        var svc = new IndexAnalysisService(
            utcNow: () => deadline - TimeSpan.FromSeconds(4),
            overallBudget: budget,
            perDatabaseSlice: TimeSpan.FromSeconds(15));

        var slices = new List<TimeSpan>();
        await svc.ApplySelectionAsync(Selection(2), deadline, (_, slice, _2) =>
        {
            slices.Add(slice);
            return Task.FromResult(new IndexAnalysisService.DatabaseScanOutcome());
        }, new IndexAnalysisResult(), default);

        slices.Should().OnlyContain(s => s == TimeSpan.FromSeconds(4),
            "a 15-second slice with 4 seconds of budget left would overrun the budget it exists "
            + "to protect");
    }

    [Theory]
    [InlineData(0.0, 1)]      // never 0 — CommandTimeout 0 means NO limit, the opposite of a slice
    [InlineData(0.4, 1)]
    [InlineData(1.0, 1)]
    [InlineData(4.2, 5)]
    [InlineData(15.0, 15)]
    public void A_slice_becomes_a_whole_second_command_timeout_and_never_becomes_unlimited(
        double seconds, int expected)
        => IndexAnalysisService.CommandTimeoutSeconds(TimeSpan.FromSeconds(seconds))
            .Should().Be(expected);

    [Fact]
    public void Retargeting_a_connection_string_changes_only_the_database()
    {
        const string original =
            "Data Source=.\\old2017;Initial Catalog=master;Integrated Security=True;TrustServerCertificate=True";

        var retargeted = IndexAnalysisService.WithDatabase(original, "AdventureWorks");
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(retargeted);

        builder.InitialCatalog.Should().Be("AdventureWorks");
        builder.DataSource.Should().Be(".\\old2017");
        builder.IntegratedSecurity.Should().BeTrue();
        builder.TrustServerCertificate.Should().BeTrue();
    }

    /// <summary>
    /// Database names are not identifiers here, they are connection-string values, and a name with
    /// a semicolon or a quote in it would otherwise splice the string. The builder quotes it; this
    /// pins that we go through the builder rather than concatenating.
    /// </summary>
    [Fact]
    public void A_database_name_with_connection_string_punctuation_survives_a_round_trip()
    {
        const string awkward = "odd;name=with\"quotes'and spaces";

        var retargeted = IndexAnalysisService.WithDatabase(
            "Data Source=.\\old2017;Initial Catalog=master;Integrated Security=True", awkward);

        new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(retargeted)
            .InitialCatalog.Should().Be(awkward);
    }

    private static List<IndexAnalysisDatabase> Selection(int count)
        => Enumerable.Range(1, count)
            .Select(i => new IndexAnalysisDatabase
            {
                DatabaseId = 4 + i,
                Name = $"db{i}",
                IoOperations = 1000 - i,
                IoMeasured = true,
                IoRank = i,
                StateDesc = "ONLINE",
                UserAccessDesc = "MULTI_USER",
                IsEligible = true,
            })
            .ToList();

    // ── Helper-level guards ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole point of the helpers: they never ask the reader for a specific CLR type, so a
    /// column whose type differs from the mapper's expectation converts instead of throwing.
    /// Driven over EVERY captured column of every set, so a future capture with a changed type
    /// is caught here rather than at a customer.
    /// </summary>
    [Fact]
    public void No_captured_column_can_make_a_helper_throw()
    {
        var sets = new[] { "missing", "unused", "fragmented", "unusedNullSize", "usageWindow",
                           "dbRanking", "dbRankingCensus" }
            .Select(s => (Set: s, Rows: Rows(s)))
            // The index-query capture too, including its two forced-absence variants: a helper that
            // throws on the absent shape is exactly the crash class this file exists to close.
            .Concat(new[] { "missing", "missingNullImpact", "unused", "unusedNullSize", "fragmented" }
                        .Select(s => (Set: "index:" + s, Rows: IndexRows(s))))
            .ToList();

        foreach (var (set, rows) in sets)
        {
            foreach (var row in rows)
            {
                for (var i = 0; i < row.FieldCount; i++)
                {
                    Record.Exception(() => IndexAnalysisService.Str(row, i))
                        .Should().BeNull($"{set}[{i}] '{row.GetName(i)}' ({row.GetDataTypeName(i)}) via Str");

                    // The numeric helpers are only claimed for numeric columns; a string column
                    // is not one a mapper points them at, and pretending otherwise would make
                    // this test assert something the code never does.
                    if (row.GetFieldType(i) == typeof(string) || row.GetFieldType(i) == typeof(DateTime)) continue;

                    Record.Exception(() => IndexAnalysisService.Long(row, i))
                        .Should().BeNull($"{set}[{i}] '{row.GetName(i)}' ({row.GetDataTypeName(i)}) via Long");
                    Record.Exception(() => IndexAnalysisService.Dbl(row, i))
                        .Should().BeNull($"{set}[{i}] '{row.GetName(i)}' ({row.GetDataTypeName(i)}) via Dbl");
                    Record.Exception(() => IndexAnalysisService.DecOrNull(row, i))
                        .Should().BeNull($"{set}[{i}] '{row.GetName(i)}' ({row.GetDataTypeName(i)}) via DecOrNull");
                }
            }
        }
    }

    /// <summary>
    /// Mutation control. The captured Double is fed to the helper AS A DECIMAL BOX and the answer
    /// must be the same number — that is what "robust against provider numeric-type quirks" has
    /// to mean, and a test that only ever sees one box cannot tell a robust mapper from a lucky one.
    /// </summary>
    [Fact]
    public void The_double_helper_agrees_with_itself_across_boxes()
    {
        var row = FirstRow("fragmented");
        var asDouble = row.Dbl(3);

        var rebox = new SingleValueReader((decimal)asDouble, "decimal", typeof(decimal));
        IndexAnalysisService.Dbl(rebox, 0).Should().BeApproximately(asDouble, 0.005d);

        var asString = new SingleValueReader(
            asDouble.ToString("R", CultureInfo.InvariantCulture), "nvarchar", typeof(string));
        IndexAnalysisService.Str(asString, 0).Should().NotBeNullOrEmpty();
    }

    // ── Replay plumbing ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Replays ONE captured row: the boxes it hands out are reconstructed from the captured CLR
    /// type and a round-trippable rendering of the captured value, so the mappers see what the
    /// live provider handed the page.
    /// </summary>
    private sealed class CapturedRowReader : DbDataReader
    {
        private readonly string?[] _values;
        private readonly string[] _names;
        private readonly string[] _sqlTypes;
        private readonly Type[] _clrTypes;
        private readonly bool[] _nulls;

        public CapturedRowReader(JsonElement row)
        {
            var cols = row.EnumerateArray().ToList();
            _values = cols.Select(c => c.GetProperty("value").ValueKind == JsonValueKind.Null
                                       ? null : c.GetProperty("value").GetString()).ToArray();
            _names = cols.Select(c => c.GetProperty("name").GetString() ?? "").ToArray();
            _sqlTypes = cols.Select(c => c.GetProperty("sqlType").GetString() ?? "").ToArray();
            _clrTypes = cols.Select(c => Type.GetType(c.GetProperty("clrType").GetString()!)!).ToArray();
            _nulls = cols.Select(c => c.GetProperty("isNull").GetString() == "true").ToArray();
        }

        public string Str(int i) => IndexAnalysisService.Str(this, i);
        public double Dbl(int i) => IndexAnalysisService.Dbl(this, i);

        public override object GetValue(int ordinal)
        {
            if (_nulls[ordinal]) return DBNull.Value;
            var t = _clrTypes[ordinal];
            var raw = _values[ordinal]!;
            if (t == typeof(string)) return raw;
            if (t == typeof(double)) return double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (t == typeof(float)) return float.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (t == typeof(decimal)) return decimal.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (t == typeof(long)) return long.Parse(raw, CultureInfo.InvariantCulture);
            if (t == typeof(int)) return int.Parse(raw, CultureInfo.InvariantCulture);
            if (t == typeof(short)) return short.Parse(raw, CultureInfo.InvariantCulture);
            if (t == typeof(bool)) return bool.Parse(raw);
            if (t == typeof(DateTime)) return DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (t == typeof(Guid)) return Guid.Parse(raw);
            throw new NotSupportedException(
                $"captured column '{_names[ordinal]}' has CLR type {t.FullName}, which this replay "
                + "does not reconstruct — add it rather than coercing it to something else");
        }

        public override bool IsDBNull(int ordinal) => _nulls[ordinal];
        public override int FieldCount => _values.Length;
        public override string GetName(int ordinal) => _names[ordinal];
        public override string GetDataTypeName(int ordinal) => _sqlTypes[ordinal];
        public override Type GetFieldType(int ordinal) => _clrTypes[ordinal];

        // Typed accessors behave like the provider's: they cast the box, so a wrong one throws.
        public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);
        public override double GetDouble(int ordinal) => (double)GetValue(ordinal);
        public override long GetInt64(int ordinal) => (long)GetValue(ordinal);
        public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
        public override string GetString(int ordinal) => (string)GetValue(ordinal);
        public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
        public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
        public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);
        public override char GetChar(int ordinal) => (char)GetValue(ordinal);
        public override float GetFloat(int ordinal) => (float)GetValue(ordinal);
        public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
        public override short GetInt16(int ordinal) => (short)GetValue(ordinal);

        public override int GetOrdinal(string name) => Array.IndexOf(_names, name);
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));
        public override int GetValues(object[] values)
        {
            var n = Math.Min(values.Length, FieldCount);
            for (var i = 0; i < n; i++) values[i] = GetValue(i);
            return n;
        }

        public override bool HasRows => true;
        public override bool Read() => false;          // one row, already positioned
        public override bool NextResult() => false;
        public override int Depth => 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => 0;
        public override System.Collections.IEnumerator GetEnumerator() => _values.GetEnumerator();
        public override long GetBytes(int o, long f, byte[]? b, int bo, int l) => throw new NotSupportedException();
        public override long GetChars(int o, long f, char[]? b, int bo, int l) => throw new NotSupportedException();
    }

    /// <summary>A one-column reader, for the box-agreement control above.</summary>
    private sealed class SingleValueReader : DbDataReader
    {
        private readonly object _value;
        private readonly string _sqlType;
        private readonly Type _clrType;

        public SingleValueReader(object value, string sqlType, Type clrType)
            => (_value, _sqlType, _clrType) = (value, sqlType, clrType);

        public override object GetValue(int ordinal) => _value;
        public override bool IsDBNull(int ordinal) => false;
        public override int FieldCount => 1;
        public override string GetName(int ordinal) => "value";
        public override string GetDataTypeName(int ordinal) => _sqlType;
        public override Type GetFieldType(int ordinal) => _clrType;
        public override decimal GetDecimal(int ordinal) => (decimal)_value;
        public override double GetDouble(int ordinal) => (double)_value;
        public override long GetInt64(int ordinal) => (long)_value;
        public override int GetInt32(int ordinal) => (int)_value;
        public override string GetString(int ordinal) => (string)_value;
        public override DateTime GetDateTime(int ordinal) => (DateTime)_value;
        public override bool GetBoolean(int ordinal) => (bool)_value;
        public override byte GetByte(int ordinal) => (byte)_value;
        public override char GetChar(int ordinal) => (char)_value;
        public override float GetFloat(int ordinal) => (float)_value;
        public override Guid GetGuid(int ordinal) => (Guid)_value;
        public override short GetInt16(int ordinal) => (short)_value;
        public override int GetOrdinal(string name) => 0;
        public override object this[int ordinal] => _value;
        public override object this[string name] => _value;
        public override int GetValues(object[] values) { values[0] = _value; return 1; }
        public override bool HasRows => true;
        public override bool Read() => false;
        public override bool NextResult() => false;
        public override int Depth => 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => 0;
        public override System.Collections.IEnumerator GetEnumerator() => new[] { _value }.GetEnumerator();
        public override long GetBytes(int o, long f, byte[]? b, int bo, int l) => throw new NotSupportedException();
        public override long GetChars(int o, long f, char[]? b, int bo, int l) => throw new NotSupportedException();
    }
}
