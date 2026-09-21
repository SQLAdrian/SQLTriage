/* In the name of God, the Merciful, the Compassionate */

// THE READER SIDE of the sp_Blitz CSV, driven over CAPTURED PAYLOADS.
//
// Both fixtures under Tests/SQLTriage.Tests/Fixtures are records the REAL writer
// (DiagnosticScriptRunner.ExportToCsv) produced against .\NEW2022 - not CSV text composed by hand
// to look like output. sp_Blitz-app-written-legacy-2026-08-23.csv came off the read-only probe that
// proved the Export Pack defect; sp_Blitz-app-written-canonical-2026-08-24.csv came off the live run
// of this lane. Each holds the file's own header plus a subset of its records, re-emitted by the
// writer's own rule (every field quoted, embedded quotes doubled) - a rule that reproduces each
// source file BYTE FOR BYTE when applied to all of its records, which is what was checked when the
// subsets were cut.
//
// The house lesson these serve: "drive renderer tests from captured responses" - two cold gates once
// passed against payloads the producing code never emits. A hand-typed sp_Blitz CSV is exactly that
// trap, and it is how the Export Pack's own tests certified a shape the app has never written.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class AuditOutputScannerBlitzTests
    {
        internal const string CanonicalFixture = "sp_Blitz-app-written-canonical-2026-08-24.csv";
        internal const string LegacyFixture = "sp_Blitz-app-written-legacy-2026-08-23.csv";

        internal static string FixturePath(string name)
        {
            var path = Path.Combine(FrkContractTests.RepoRoot(), "Tests", "SQLTriage.Tests", "Fixtures", name);
            File.Exists(path).Should().BeTrue("the captured fixture must be at " + path);
            return path;
        }

        internal static string FixtureText(string name) => File.ReadAllText(FixturePath(name));

        // ── The layout, on each shape ───────────────────────────────────────────────────────

        [Fact]
        public void The_canonical_header_resolves_to_sp_Blitzs_own_columns()
        {
            var layout = BlitzCsvLayout.Resolve(HeaderOf(CanonicalFixture));

            layout.InstanceIdx.Should().Be(1, "ServerName is the output table's second column");
            layout.DateIdx.Should().Be(2, "CheckDate is found by name");
            layout.CheckIdIdx.Should().Be(11);
            layout.DomainIdx.Should().Be(-1, "the canonical shape carries no Domain column");
        }

        [Fact]
        public void The_legacy_header_resolves_to_the_LAST_ServerName_not_the_prepended_one()
        {
            var header = HeaderOf(LegacyFixture);
            header.Should().Equal(new[]
            {
                "ServerName", "ID", "Domain", "ServerName", "", "Priority",
                "FindingsGroup", "Finding", "DatabaseName", "URL", "Details", "CheckID"
            }, "this is the shape the app wrote until 2026-08-24, captured verbatim");

            var layout = BlitzCsvLayout.Resolve(header);

            layout.InstanceIdx.Should().Be(3,
                "column 0 is the connection target the writer prepended; column 3 is sp_Blitz's own "
                + "ServerName, and reading the LAST one keys old files the same way as new ones");
            layout.DateIdx.Should().Be(4, "the old CONVERT had no alias, so its column has no name");
            layout.DomainIdx.Should().Be(2);
            layout.CheckIdIdx.Should().Be(11);
        }

        [Fact]
        public void A_header_with_no_date_column_at_all_reports_no_date()
        {
            var layout = BlitzCsvLayout.Resolve(new[] { "ServerName", "Priority", "CheckID" });
            layout.DateIdx.Should().Be(-1, "with nothing to read, the caller must fall back to the file");
            layout.InstanceIdx.Should().Be(0);
        }

        // ── The scan, over each captured file ───────────────────────────────────────────────

        [Fact]
        public async Task The_canonical_capture_is_keyed_on_the_name_the_server_reports()
        {
            using var dir = new TempDir();
            var file = dir.Write("MSI_sp_Blitz_20260824_082141.csv", FixtureText(CanonicalFixture));

            // A file timestamp deliberately far from the audit: if the date came from here rather
            // than from CheckDate, this test says so.
            File.SetLastWriteTime(file, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Local));

            var scanned = await Scan(dir);

            scanned.Should().HaveCount(1);
            scanned[0].SqlInstance.Should().Be(@"MSI\NEW2022");
            scanned[0].FileType.Should().Be(AuditFileType.SpBlitz);
            scanned[0].AuditDate.Should().Be(FirstCheckDate(CanonicalFixture, 2),
                "the audit is dated by the CheckDate in the file, never by when the file was touched");
            scanned[0].AuditDate.Year.Should().NotBe(2001);
            scanned[0].Domain.Should().BeEmpty("the canonical shape has no Domain and nothing else to adopt one from");
        }

        [Fact]
        public async Task The_legacy_capture_is_keyed_on_the_same_name_as_the_canonical_one()
        {
            using var dir = new TempDir();
            var file = dir.Write("._new2022_sp_Blitz_20260823_040935.csv", FixtureText(LegacyFixture));
            File.SetLastWriteTime(file, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Local));

            var scanned = await Scan(dir);

            scanned.Should().HaveCount(1);
            scanned[0].SqlInstance.Should().Be(@"MSI\NEW2022",
                "not '.\\new2022' - an upgraded install's existing files must not become a second server");
            scanned[0].AuditDate.Should().Be(FirstCheckDate(LegacyFixture, 4));
            scanned[0].Domain.Should().Be("WORKGROUP", "the old shape did carry a domain, and it is still read");
        }

        [Fact]
        public async Task An_upgraded_install_holding_both_shapes_reports_ONE_server()
        {
            using var dir = new TempDir();
            var old = dir.Write("._new2022_sp_Blitz_20260823_040935.csv", FixtureText(LegacyFixture));
            var fresh = dir.Write("MSI_sp_Blitz_20260824_082141.csv", FixtureText(CanonicalFixture));
            File.SetLastWriteTime(old, DateTime.Now.AddDays(-1));
            File.SetLastWriteTime(fresh, DateTime.Now);

            var scanned = await Scan(dir);

            scanned.Should().HaveCount(1,
                "one instance, one sp_Blitz entry - the newest file wins, and the older one is not a "
                + "second server just because the writer used to record the connection string");
            scanned[0].FilePath.Should().Be(fresh);
            scanned[0].SqlInstance.Should().Be(@"MSI\NEW2022");
        }

        [Fact]
        public async Task A_domainless_blitz_file_adopts_the_domain_of_its_own_instances_triage_file()
        {
            using var dir = new TempDir();
            dir.Write("MSI_sp_Blitz_20260824_082141.csv", FixtureText(CanonicalFixture));
            dir.Write("MSI_sp_triage_20260824.csv",
                "ID,evaldate,domain,SQLInstance,SectionID,Section,Summary,Severity,Details,HoursToResolveWithTesting,QueryPlan\r\n"
                + "1,2026-08-24,WORKGROUP,MSI\\NEW2022,12,sp_Blitz: Backups,full backup missing,1,take a full backup,2,\r\n");

            var scanned = await Scan(dir);

            scanned.Should().HaveCount(2, "two file types for one instance");
            scanned.Select(s => s.Domain).Should().AllBe("WORKGROUP",
                "the domain came from a CSV describing the SAME instance in the same folder");
            scanned.Select(s => s.SqlInstance).Distinct().Should().ContainSingle(
                "the sp_Blitz key and the sp_triage key are the same identity now");
        }

        [Fact]
        public void Two_files_disagreeing_about_an_instances_domain_leave_it_empty()
        {
            var files = new List<AuditedFile>
            {
                new() { SqlInstance = @"MSI\NEW2022", Domain = "WORKGROUP", FileType = AuditFileType.SpTriage },
                new() { SqlInstance = @"MSI\NEW2022", Domain = "CORP", FileType = AuditFileType.SpTriage },
                new() { SqlInstance = @"MSI\NEW2022", Domain = "", FileType = AuditFileType.SpBlitz },
            };

            var adopted = AuditOutputScanner.AdoptDomainWithinInstance(files);

            adopted.Single(f => f.FileType == AuditFileType.SpBlitz).Domain.Should().BeEmpty(
                "picking a winner between two answers would be a guess");
        }

        // ── The fired-check parse, including the records that span lines ────────────────────

        [Fact]
        public async Task Every_fired_CheckID_in_the_canonical_capture_is_counted()
        {
            using var dir = new TempDir();
            dir.Write("MSI_sp_Blitz_20260824_082141.csv", FixtureText(CanonicalFixture));

            var scanned = await Scan(dir);
            var scanner = NewScanner(dir);
            await scanner.LoadFiredChecksAsync(scanned[0]);

            scanned[0].FiredCheckCounts.Should().Equal(ExpectedCounts(CanonicalFixture, checkIdIndex: 11));

            // One count fixed as a literal, independent of ExpectedCounts: that helper re-parses the
            // SAME fixture with the SAME CsvParser the scanner itself uses, so a shared parsing bug
            // would agree with itself on every key and this assertion would still pass. Counted by
            // hand against the fixture file, 2026-08-24.
            scanned[0].FiredCheckCounts[150].Should().Be(3,
                "the CheckID 150 records are the ones whose Details spans several lines, and the "
                + "fixture holds exactly three of them");
            scanned[0].FiredCheckCounts.Should().ContainKey(150,
                "the CheckID 150 records are the ones whose Details spans several lines");
        }

        [Fact]
        public async Task Every_fired_CheckID_in_the_legacy_capture_is_still_counted()
        {
            using var dir = new TempDir();
            dir.Write("._new2022_sp_Blitz_20260823_040935.csv", FixtureText(LegacyFixture));

            var scanned = await Scan(dir);
            var scanner = NewScanner(dir);
            await scanner.LoadFiredChecksAsync(scanned[0]);

            scanned[0].FiredCheckCounts.Should().Equal(ExpectedCounts(LegacyFixture, checkIdIndex: 11));
        }

        [Fact]
        public void The_capture_really_does_hold_records_that_a_line_reader_would_fragment()
        {
            // The premise of the two tests above, measured rather than assumed. Reading this file a
            // line at a time yields MORE pieces than it has records, and the pieces are not rows.
            var text = FixtureText(CanonicalFixture);
            var records = Read(text);
            var lines = text.Split('\n').Count(l => l.Trim().Length > 0);

            lines.Should().BeGreaterThan(records.Count,
                "a captured sp_Blitz CSV holds fields with newlines in them (Details carries error-log "
                + "text), which is why the scanner reads RECORDS and not lines");
            records.Skip(1).Should().OnlyContain(r => r.Count == records[0].Count,
                "every record has exactly as many fields as the header");
        }

        // ── The second and third copies of this parse are gone ──────────────────────────────

        [Fact]
        public void The_roadmap_page_reads_audit_files_through_this_scanner_and_not_its_own_copy()
        {
            // A text tripwire, and the only instrument that reaches a .razor at all: nothing in CI
            // renders this page. Pages/DiagnosticsRoadmap.razor carried a second copy of the
            // discovery rule and a THIRD copy of the fired-CheckID parse, both resolving the date by
            // a hard-coded ordinal 4. That ordinal is FindingsGroup in the shape the app writes now,
            // so a surviving copy would have dated every audit by the file's timestamp and said
            // nothing. Same class as RuleCallSiteTests: a decision that exists only inside a Blazor
            // component method is a decision no test reaches.
            var razor = File.ReadAllText(
                Path.Combine(FrkContractTests.RepoRoot(), "Pages", "DiagnosticsRoadmap.razor"));

            razor.Should().Contain("AuditScanner.ScanAsync",
                "the page must get its file list from the one scanner");
            razor.Should().Contain("AuditScanner.LoadFiredChecksAsync",
                "and its fired-check counts from the same place");

            razor.Should().NotContain("ParseCsvLine",
                "a page-local CSV reader is how the second copy came back last time");
            razor.Should().NotContain("DetectDelimiter");
            razor.Should().NotContain("FindIndex(h => h.Equals(\"CheckID\"");
            razor.Should().NotContain("private enum AuditFileType",
                "the file type is SQLTriage.Data.Services.AuditFileType, not a page-local twin");
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────

        private static List<List<string>> Read(string csv)
        {
            using var reader = new StringReader(csv);
            return CsvParser.ParseRecords(reader).ToList();
        }

        private static List<string> HeaderOf(string fixture) => Read(FixtureText(fixture))[0];

        private static DateTime FirstCheckDate(string fixture, int column)
        {
            var value = Read(FixtureText(fixture))[1][column];
            DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out var parsed)
                .Should().BeTrue("the captured date '" + value + "' must be parseable");
            return parsed;
        }

        private static Dictionary<int, int> ExpectedCounts(string fixture, int checkIdIndex)
        {
            var counts = new Dictionary<int, int>();
            foreach (var record in Read(FixtureText(fixture)).Skip(1))
            {
                if (!int.TryParse(record[checkIdIndex], out var id) || id <= 0) continue;
                counts[id] = counts.TryGetValue(id, out var c) ? c + 1 : 1;
            }
            return counts;
        }

        private static AuditOutputScanner NewScanner(TempDir dir) =>
            new(NullLogger<AuditOutputScanner>.Instance, dir.Path);

        private static async Task<IReadOnlyList<AuditedFile>> Scan(TempDir dir) =>
            (await NewScanner(dir).ScanAsync()).Files;

        private sealed class TempDir : IDisposable
        {
            public string Path { get; } = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "sqlt-blitz-scan-" + Guid.NewGuid().ToString("N"));

            public TempDir() => Directory.CreateDirectory(Path);

            public string Write(string name, string content)
            {
                var path = System.IO.Path.Combine(Path, name);
                File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
                return path;
            }

            public void Dispose()
            {
                try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
            }
        }
    }
}
