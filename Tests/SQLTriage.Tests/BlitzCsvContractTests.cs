/* In the name of God, the Merciful, the Compassionate */

// THE WRITER SIDE of the sp_Blitz CSV: what the shipped output query selects, and what the CSV
// writer puts in front of it.
//
// THE DEFECT THIS EXISTS FOR (proved live 2026-08-24, DECISIONS 2026-08-24 02:28). The shipped
// sp_Blitz output query selected an xp_regread domain and an UNALIASED CONVERT of CheckDate, and
// DiagnosticScriptRunner.ExportToCsv prepended a ServerName column holding the CONNECTION TARGET.
// The file therefore read
//     "ServerName","ID","Domain","ServerName","","Priority",...
// - two columns called ServerName, one with no name at all. The Export Pack's sp_Blitz dataset
// accepts sp_Blitz's own twelve-column output-table shape and refuses anything else, so EVERY
// Export Pack built between 2026-07-23 and 2026-08-24 shipped without sp_Blitz, and said so in a
// line nobody read.
//
// WHAT IS PINNED HERE, and why it is pinned against the SCRIPT. The select list is compared to the
// CREATE TABLE inside the VENDORED scripts/sp_Blitz.sql - not to a list typed into this file - so a
// First Responder Kit refresh that changes the output table breaks the build here instead of
// silently dropping sp_Blitz out of every client's pack again. The Export Pack's own canonical
// header is compared against the same select list in Portal/SpBlitzAppCsvContractTests, which reads
// it off the Portal type; that assertion cannot live in this file because the Portal tree is
// Compile-Removed from the community assembly.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using Xunit;

namespace SQLTriage.Tests
{
    public class BlitzCsvContractTests
    {
        // ── The select list, against sp_Blitz's own CREATE TABLE ────────────────────────────

        [Fact]
        public void The_shipped_sp_Blitz_output_query_selects_the_output_table_column_for_column()
        {
            var fromScript = OutputTableColumnsFromScript();
            var fromConfig = SelectListColumns(ShippedOutputQuery("sp_Blitz"));

            fromScript.Should().HaveCount(12, "sp_Blitz's output table has twelve columns");
            fromConfig.Should().Equal(fromScript,
                "the Export Pack reads this CSV and accepts sp_Blitz's output-table shape, in order, "
                + "and nothing else. If this went red because the kit was refreshed, the select list in "
                + "Config/script-configurations.json has to follow the new CREATE TABLE.");
        }

        [Fact]
        public void The_select_list_aliases_the_converted_CheckDate_so_the_column_has_a_name()
        {
            // The specific loss that made the old file unreadable BY NAME: CONVERT(...) with no
            // alias produces a column whose name is the empty string.
            var query = ShippedOutputQuery("sp_Blitz");
            query.Should().Contain("CONVERT", "the date is still converted to a stable text form");
            SelectListColumns(query).Should().Contain("CheckDate");
            SelectListColumns(query).Should().NotContain("",
                "a nameless column cannot be read by name by anything downstream");
        }

        // ── The prepend rule ────────────────────────────────────────────────────────────────

        [Fact]
        public void A_result_that_does_not_name_its_server_still_gets_the_connection_target()
        {
            DiagnosticScriptRunner.ShouldPrependServerName(new[] { "check_id", "priority" })
                .Should().BeTrue();

            var csv = Write(
                serverName: @".\new2022",
                rows: new[] { Row(("check_id", "1"), ("priority", "10")) });

            Header(csv).Should().Equal(new[] { "ServerName", "check_id", "priority" });
            Records(csv)[1].Should().Equal(new[] { @".\new2022", "1", "10" });
        }

        [Fact]
        public void A_result_that_names_its_own_server_is_not_given_a_second_ServerName_column()
        {
            DiagnosticScriptRunner.ShouldPrependServerName(new[] { "ID", "ServerName", "CheckID" })
                .Should().BeFalse();

            var csv = Write(
                serverName: @".\new2022",
                rows: new[] { Row(("ID", "1"), ("ServerName", @"MSI\NEW2022"), ("CheckID", "93")) });

            Header(csv).Should().Equal(new[] { "ID", "ServerName", "CheckID" });
            Header(csv).Count(h => h.Equals("ServerName", StringComparison.OrdinalIgnoreCase))
                .Should().Be(1, "two columns of one name cannot be resolved by name");
            Records(csv)[1].Should().Equal(new[] { "1", @"MSI\NEW2022", "93" },
                "the server's own name is what the row says, not the string the operator typed");
        }

        [Fact]
        public void The_prepend_rule_is_case_insensitive_about_the_column_it_looks_for()
        {
            DiagnosticScriptRunner.ShouldPrependServerName(new[] { "servername" }).Should().BeFalse();
            DiagnosticScriptRunner.ShouldPrependServerName(Array.Empty<string>()).Should().BeTrue();
            DiagnosticScriptRunner.ShouldPrependServerName(null).Should().BeTrue();
        }

        // ── The blast radius of that rule, censused rather than assumed ─────────────────────

        [Fact]
        public void sp_Blitz_is_the_only_shipped_entry_whose_output_query_returns_a_ServerName_column()
        {
            var carriers = ShippedEntries()
                .Where(e => !string.IsNullOrWhiteSpace(e.Query))
                .Where(e => SelectListColumns(e.Query)
                    .Any(c => c.Equals("ServerName", StringComparison.OrdinalIgnoreCase)))
                .Select(e => e.Name)
                .ToList();

            carriers.Should().Equal(new[] { "sp_Blitz" },
                "ShouldPrependServerName changes the CSV shape of exactly the entries listed here. "
                + "A new entry selecting ServerName is a new file shape and needs its readers checked. "
                + "This assertion is hollow unless SelectListColumns actually parses the non-trivial "
                + "entries - see At_least_three_shipped_entries_produce_a_parsed_select_list.");
        }

        // The non-vacuity guard for the census above. Until 2026-08-24, SelectListColumns returned
        // empty for 4 of the 6 shipped entries - sp_ineachdb and Check_BP_Servers legitimately (no
        // query configured), but also sp_PerfCheck (EXEC-only, expected) AND SQLDBA.ORG.sp_triage
        // (a real select list the old " FROM " match could not find - see IsFromKeywordAt). The census
        // test above still passed the whole time, because "carriers.Should().Equal({"sp_Blitz"})" is
        // true whether sp_triage was correctly found to have no ServerName column or never actually
        // examined at all. This asserts the parser is doing real work: at least the entries known to
        // have a genuine select list - sp_Blitz, stpChecklist_Seguranca, SQLDBA.ORG.sp_triage - must
        // parse to a NON-EMPTY column list, or the census above is certifying nothing.
        [Fact]
        public void At_least_three_shipped_entries_produce_a_parsed_select_list()
        {
            var nonEmpty = ShippedEntries()
                .Where(e => !string.IsNullOrWhiteSpace(e.Query) && !IsExecOnly(e.Query))
                .Count(e => SelectListColumns(e.Query).Count > 0);

            nonEmpty.Should().BeGreaterThanOrEqualTo(3,
                "sp_Blitz, stpChecklist_Seguranca and SQLDBA.ORG.sp_triage all have a genuine select "
                + "list; if fewer than three shipped entries parse to a non-empty column list, "
                + "SelectListColumns is silently failing on real SQL again and the ServerName census "
                + "is examining nothing for the entries that fail.");
        }

        // The EXEC-only case named explicitly, so it is never confused with a select list this parser
        // failed to find (the failure mode the test above exists to catch).
        [Fact]
        public void The_EXEC_only_entry_has_no_select_list_by_design_not_by_parse_failure()
        {
            var execOnly = ShippedEntries()
                .Where(e => IsExecOnly(e.Query))
                .Select(e => e.Name)
                .ToList();

            execOnly.Should().Equal(new[] { "sp_PerfCheck" },
                "sp_PerfCheck is the one shipped entry that is EXEC-only: its result columns come from "
                + "the stored procedure at execution time, and this config file has no static select "
                + "list to read for it at all.");

            foreach (var name in execOnly)
                SelectListColumns(ShippedOutputQuery(name)).Should().BeEmpty(
                    name + " is EXEC-only, so SelectListColumns correctly returns no columns - "
                    + "there is no select list to parse, which is different from failing to find one.");
        }

        // ── Helpers: shipped config ─────────────────────────────────────────────────────────

        internal static string ConfigPath() =>
            Path.Combine(FrkContractTests.RepoRoot(), "Config", "script-configurations.json");

        internal static IReadOnlyList<(string Name, string Query)> ShippedEntries()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath()));
            return doc.RootElement.EnumerateArray()
                .Select(e => (
                    Name: e.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "",
                    Query: e.TryGetProperty("SqlQueryForOutput", out var q) ? q.GetString() ?? "" : ""))
                .ToList();
        }

        internal static string ShippedOutputQuery(string entryName)
        {
            var entry = ShippedEntries().SingleOrDefault(e =>
                e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase));
            entry.Name.Should().NotBeNullOrEmpty("Config/script-configurations.json must hold " + entryName);
            return entry.Query;
        }

        // ── Helpers: two small SQL readers ──────────────────────────────────────────────────

        /// <summary>
        /// The output table's columns, in order, read out of the CREATE TABLE the vendored script
        /// builds for @OutputTableName (scripts/sp_Blitz.sql). Textual, like FrkContractTests'
        /// CheckID extraction, and cross-checked live by FrkLiveSmokeTests, which asks the real
        /// instance for the table's physical columns and compares them to the same twelve.
        /// </summary>
        internal static IReadOnlyList<string> OutputTableColumnsFromScript()
        {
            var sql = File.ReadAllText(FrkContractTests.ScriptPath("sp_Blitz.sql"));
            const string anchor = "(ID INT IDENTITY(1,1) NOT NULL,";
            var start = sql.IndexOf(anchor, StringComparison.Ordinal);
            start.Should().BeGreaterThan(-1, "the script builds its output table with this DDL");
            var end = sql.IndexOf("CONSTRAINT [PK_", start, StringComparison.Ordinal);
            end.Should().BeGreaterThan(start);

            var body = sql.Substring(start + 1, end - start - 1);
            return SplitTopLevel(body)
                .Select(item => Unbracket(item.Split(new[] { ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries)[0]))
                .ToList();
        }

        /// <summary>
        /// The column NAMES a select list produces, in order: the alias after a top-level " AS " when
        /// there is one, otherwise the last identifier in the item. Depth-aware, so the commas inside
        /// CONVERT(VARCHAR(30), CheckDate, 120) are not column separators.
        ///
        /// <para>Returns empty for two DIFFERENT reasons, deliberately not distinguished by this
        /// return type: the query is empty/whitespace (no query configured at all), or the query has
        /// no top-level SELECT (an EXEC-only entry - see <see cref="IsExecOnly"/>, which names that
        /// case explicitly rather than leaving it to look like a parse failure). Everything else with
        /// a SELECT is expected to parse to a non-empty list; <c>At_least_three_shipped_entries_
        /// produce_a_parsed_select_list</c> below is the tripwire that catches this method quietly
        /// going back to returning empty for real select lists, the way it did until 2026-08-24 (see
        /// <see cref="IsFromKeywordAt"/>).</para>
        /// </summary>
        internal static IReadOnlyList<string> SelectListColumns(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return Array.Empty<string>();

            var select = query.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase);
            if (select < 0) return Array.Empty<string>();          // EXEC-only: no select list at all

            var body = query.Substring(select + "SELECT".Length);
            var depth = 0;
            var cut = -1;
            for (var i = 0; i < body.Length && cut < 0; i++)
            {
                if (body[i] == '(') depth++;
                else if (body[i] == ')') depth--;
                else if (depth == 0 && IsFromKeywordAt(body, i))
                    cut = i;
            }
            if (cut < 0) return Array.Empty<string>();

            return SplitTopLevel(body.Substring(0, cut)).Select(AliasOf).ToList();
        }

        /// <summary>
        /// Whether "FROM" starts at <paramref name="i"/> as a whole word, honouring whitespace
        /// generously rather than requiring a literal single space on each side. Until 2026-08-24 this
        /// method looked for the literal six characters " FROM " with exactly one space either side,
        /// which is what a hand-typed single-line query has and what NONE of a script formatted across
        /// lines has - SQLDBA.ORG.sp_triage's own select list ends "...QueryPlan\r\n\r\nFROM \r\n
        /// (SELECT...", with no space at all before "FROM", so the old check never found its cut point
        /// and <see cref="SelectListColumns"/> silently returned empty for it. A whole-word match -
        /// preceded and followed by whitespace, or by the start/end of the select list - finds "FROM"
        /// regardless of how many spaces, tabs or newlines surround it, and still never matches "FROM"
        /// as a substring of a longer identifier.
        /// </summary>
        private static bool IsFromKeywordAt(string body, int i)
        {
            if (i + 4 > body.Length) return false;
            if (!body.Substring(i, 4).Equals("FROM", StringComparison.OrdinalIgnoreCase)) return false;
            if (i > 0 && !char.IsWhiteSpace(body[i - 1])) return false;
            if (i + 4 < body.Length && !char.IsWhiteSpace(body[i + 4])) return false;
            return true;
        }

        /// <summary>
        /// Whether a shipped entry's output query is EXEC-only - a non-empty query with no top-level
        /// SELECT, so it has no static select list for <see cref="SelectListColumns"/> to read at all.
        /// sp_PerfCheck is the one shipped example: its result columns come from the stored procedure
        /// at execution time, not from anything in this config file. Named explicitly (rather than
        /// left to fall out of "the parser found nothing") so an EXEC-only entry can never be misread
        /// as a select list this parser failed to find.
        /// </summary>
        internal static bool IsExecOnly(string query) =>
            !string.IsNullOrWhiteSpace(query)
            && query.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase) < 0;

        private static string AliasOf(string item)
        {
            var depth = 0;
            var asAt = -1;
            for (var i = 0; i + 4 <= item.Length; i++)
            {
                if (item[i] == '(') depth++;
                else if (item[i] == ')') depth--;
                else if (depth == 0 && item.Substring(i, 4).Equals(" AS ", StringComparison.OrdinalIgnoreCase))
                    asAt = i;
            }

            if (asAt >= 0) return Unbracket(item.Substring(asAt + 4).Trim());

            var words = item.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return words.Length == 0 ? "" : Unbracket(words[^1]);
        }

        private static IEnumerable<string> SplitTopLevel(string text)
        {
            var depth = 0;
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')') depth--;
                else if (text[i] == ',' && depth == 0)
                {
                    var piece = text.Substring(start, i - start).Trim();
                    if (piece.Length > 0) yield return piece;
                    start = i + 1;
                }
            }

            var tail = text.Substring(start).Trim();
            if (tail.Length > 0) yield return tail;
        }

        private static string Unbracket(string token) => token.Trim().Trim('[', ']', '"');

        // ── Helpers: the real writer ────────────────────────────────────────────────────────

        private static Dictionary<string, object> Row(params (string Column, string Value)[] cells)
        {
            var row = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var (column, value) in cells) row[column] = value;
            return row;
        }

        internal static string Write(string serverName, IEnumerable<Dictionary<string, object>> rows)
        {
            var runner = new DiagnosticScriptRunner(
                new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance),
                NullLogger<DiagnosticScriptRunner>.Instance);

            return runner.ExportToCsv(new ScriptExecutionResult
            {
                ScriptName = "sp_Blitz",
                ServerName = serverName,
                Success = true,
                Results = rows.ToList(),
            });
        }

        internal static List<List<string>> Records(string csv)
        {
            using var reader = new StringReader(csv);
            return CsvParser.ParseRecords(reader).ToList();
        }

        internal static List<string> Header(string csv) => Records(csv)[0];
    }
}
