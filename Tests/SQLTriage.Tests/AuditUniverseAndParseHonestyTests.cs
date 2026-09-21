/* In the name of God, the Merciful, the Compassionate */

// THE SCORING UNIVERSE AND THE PARSE OUTCOME, honesty-hunt ledger lane 5 (Cluster A + Cluster D).
//
// The defect this file exists to keep closed: a maturity score computed over checks that CANNOT
// FIRE on the audited target. It arrived through two doors.
//
//   DOOR ONE (audit-r1-02, audit-r2-01, audit-r2-02) - the free-pass CheckID. sp_Blitz emits a row
//   only when a check FIRES, so every scoring surface SYNTHESIZES a universe and reads "in the
//   universe, not in the fired set" as a PASS. An id no deployed script can emit is therefore a
//   permanent free pass on every instance forever, and it renders to the client as a check that was
//   assessed and found healthy. Until 2026-08-26 the dashboard excluded two such ids and the
//   client-facing Compliance Roadmap excluded none: two universes, one guard.
//
//   DOOR TWO (audit-r1-07, #4 in the wave's top ten) - the unreadable file. An unparseable audit CSV
//   produced an EMPTY fired set with no log line, and an empty fired set scores 100%, maturity L5
//   Governed, every compliance badge covered. A live sp_Blitz run always emits at least its
//   CheckID -1 banner row, so an all-pass result is the tell that parsing failed.
//
// Cluster D is the same fail-open shape one surface over: an empty server-environment list dropped
// the non-production watermark (audit-r2-03), and two of three exits from the roadmap's mapping
// loader did not raise the "data missing" flag that suppresses the score (audit-r2-04).
//
// WHAT THE INSTRUMENTS HERE ARE, honestly labelled. CheckUniverse, AuditOutputScanner and
// BlitzDashboardService are exercised for real. Pages/DiagnosticsRoadmap.razor is a Blazor
// component and nothing in CI renders it (no bUnit in this test project), so its rules are held by
// TEXT TRIPWIRES - the same instrument, and the same admitted limitation, as
// AuditOutputScannerBlitzTests.The_roadmap_page_reads_audit_files_through_this_scanner. A tripwire
// proves the call site exists. It does not prove the rendered page behaves.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    public class AuditUniverseAndParseHonestyTests
    {
        // ── Door one: the universe filter ───────────────────────────────────────────────────

        [Theory]
        [InlineData(9)]     // Endpoints Configured, Review Required
        [InlineData(38)]    // Heap Tables. No Clustered Index
        [InlineData(39)]    // Possible Stale Backup/Shadow Tables
        [InlineData(52)]    // Cluster Failover History Detected
        [InlineData(127)]   // Cumulative Update Available - the live id for this check is 217
        [InlineData(129)]   // retired by FRK 20260708
        [InlineData(157)]   // retired by FRK 20260708
        [InlineData(999)]   // sp_triage's "Blitz from here" separator row, not a backup check
        [InlineData(9999)]  // sp_triage custom-check sentinel
        public void An_id_no_deployed_script_can_emit_is_not_in_the_universe(int checkId)
        {
            CheckUniverse.CanFire(checkId).Should().BeFalse(
                "CheckID {0} cannot be emitted by any vendored script, so its absence from a fired "
                + "set is not evidence of a pass. FrkContractTests derives this set from the script "
                + "itself; CheckUniverse.WhyItCannotFire says which rule excludes it: {1}",
                checkId, CheckUniverse.WhyItCannotFire(checkId));
        }

        [Theory]
        [InlineData(1)]     // Databases Not Being Backed Up
        [InlineData(199)]   // There Is An Error With The Default Trace - the quoted-literal form
        [InlineData(217)]   // Cumulative Update Available - the LIVE twin of 127
        [InlineData(275)]   // Non-Default Database Config, new at 8.34
        public void A_live_id_stays_in_the_universe(int checkId)
        {
            CheckUniverse.CanFire(checkId).Should().BeTrue(
                "CheckID {0} is emitted by the vendored script. Excluding it would silently stop "
                + "grading a real check: the score rises on an instance where it fires and falls on "
                + "one where it passes, and measures the check on neither. That is the worse of the "
                + "two failures this filter can have", checkId);
        }

        [Fact]
        public void The_dashboard_catalog_refuses_an_unfireable_id_from_the_corpus_side()
        {
            var corpus = new[]
            {
                BlitzCheck("SQLT-BLITZ-127", "127", "Cumulative Update Available"),
                BlitzCheck("SQLT-BLITZ-217", "217", "Cumulative Update Available"),
                BlitzCheck("SQLT-BLITZ-38", "38", "Heap Tables. No Clustered Index"),
            };

            var catalog = BlitzDashboardService.BuildCatalog(corpus, mapJson: null);

            catalog.Keys.Should().BeEquivalentTo(new[] { 217 },
                "127 duplicates 217's finding name and can never fire, so keeping it would print "
                + "'Cumulative Update Available' twice in one card: once as a green tick and once "
                + "as a red cross");
        }

        [Fact]
        public void The_dashboard_catalog_refuses_an_unfireable_id_from_the_roadmap_map_side()
        {
            const string mapJson = """
            { "blitzCheckMap": [
              { "checkId": 9,    "findingName": "Endpoints Configured, Review Required", "category": "Security",    "level": 3, "IsBad": 0 },
              { "checkId": 999,  "findingName": "Third-Party Backup Tool Detected",      "category": "Reliability", "level": 3, "IsBad": 0 },
              { "checkId": 9999, "findingName": "Weak SQL Login Passwords",              "category": "Security",    "level": 1, "IsBad": 1 },
              { "checkId": 1,    "findingName": "Databases Not Being Backed Up",         "category": "Reliability", "level": 1, "IsBad": 1 }
            ] }
            """;

            var catalog = BlitzDashboardService.BuildCatalog(Array.Empty<SqlCheck>(), mapJson);

            catalog.Keys.Should().BeEquivalentTo(new[] { 1 },
                "the map is a data file generated from AllCheckTable and still carries history; the "
                + "exclusion is a code rule, so it has to bite on this side too");
        }

        [Fact]
        public void An_unfireable_id_arriving_in_a_FIRED_set_is_not_turned_into_a_nameless_ding()
        {
            // The filter is applied in BOTH directions on purpose. A historical CSV can carry 129,
            // and an sp_triage file under the new sqlmagic shape carries the 999 separator row.
            // Without the second half of the rule each becomes a weight-1 "sp_Blitz Check 999",
            // which is a worse lie than the inflation the first half removes.
            var catalog = BlitzDashboardService.BuildCatalog(
                new[] { BlitzCheck("SQLT-BLITZ-1", "1", "Databases Not Being Backed Up") },
                mapJson: null);

            var file = new AuditedFile
            {
                SqlInstance = "MSI\\OLD2017",
                FileType = AuditFileType.SpTriage,
                ParseOutcome = AuditParseOutcome.Parsed,
                FiredCheckCounts = new Dictionary<int, int> { [999] = 1, [129] = 2, [9999] = 4 },
            };

            var report = BlitzDashboardService.ComputeInstanceReport(file, catalog);

            report.Findings.Should().BeEmpty("none of the three ids is a finding on any instance");
            report.UnclassifiedFired.Should().Be(0);
            report.ChecksFired.Should().Be(0);
            report.UniverseSize.Should().Be(1, "only CheckID 1 is a real universe member here");
        }

        // ── Door two: the parse outcome ─────────────────────────────────────────────────────

        [Fact]
        public async Task A_csv_with_no_CheckID_column_is_UNREADABLE_and_not_an_empty_fired_set()
        {
            // audit-r1-07, the exact arm as filed: AuditOutputScanner returned an empty dictionary
            // and logged nothing when the header carried no CheckID column.
            //
            // The input is the CAPTURED payload the real writer produced, with its CheckID column
            // DROPPED by rule. A hand-typed CSV would be the trap this fixture set exists to avoid:
            // it would test a shape the app has never written. The same mutilation over the file the
            // live .\OLD2017 run wrote on 2026-08-26 produced the same outcome.
            using var dir = new TempDir();
            dir.Write("MSI_sp_Blitz_20260826_090000.csv",
                WithoutColumn(
                    AuditOutputScannerBlitzTests.FixtureText(AuditOutputScannerBlitzTests.CanonicalFixture),
                    "CheckID"));

            var file = await ScanOne(dir);
            await NewScanner(dir).LoadFiredChecksAsync(file);

            file.ParseOutcome.Should().Be(AuditParseOutcome.Unreadable);
            file.IsScorable.Should().BeFalse(
                "an empty fired set from an unreadable file scores 100% and maturity L5 Governed, "
                + "which is the single highest-impact defect in this lane");
            file.ParseFailureReason.Should().Contain("CheckID",
                "the reason has to name what was missing, or the operator cannot act on it");
            file.FiredCheckCounts.Should().BeEmpty();
        }

        [Fact]
        public async Task A_csv_with_a_header_and_no_rows_is_UNREADABLE()
        {
            // A real audit run always writes at least one row: sp_Blitz emits its CheckID -1 banner
            // on every execution. A header alone is a truncated or failed export, not a clean server.
            using var dir = new TempDir();
            dir.Write("MSI_sp_Blitz_20260826_090001.csv",
                "ServerName,ID,CheckDate,Priority,FindingsGroup,Finding,DatabaseName,URL,Details,CheckID\r\n");

            // Discovery needs one row to learn the instance, so build the AuditedFile directly.
            var file = new AuditedFile
            {
                SqlInstance = "MSI\\OLD2017",
                FilePath = Path.Combine(dir.Path, "MSI_sp_Blitz_20260826_090001.csv"),
                FileType = AuditFileType.SpBlitz,
            };
            await NewScanner(dir).LoadFiredChecksAsync(file);

            file.ParseOutcome.Should().Be(AuditParseOutcome.Unreadable);
            file.ParseFailureReason.Should().Contain("no data rows");
        }

        [Fact]
        public async Task A_completely_unreadable_file_is_UNREADABLE_rather_than_silently_empty()
        {
            using var dir = new TempDir();
            dir.Write("MSI_sp_Blitz_20260826_090002.csv", "");

            var file = new AuditedFile
            {
                SqlInstance = "MSI\\OLD2017",
                FilePath = Path.Combine(dir.Path, "MSI_sp_Blitz_20260826_090002.csv"),
                FileType = AuditFileType.SpBlitz,
            };
            await NewScanner(dir).LoadFiredChecksAsync(file);

            file.ParseOutcome.Should().Be(AuditParseOutcome.Unreadable);
        }

        [Fact]
        public async Task A_csv_that_parses_but_yields_no_CheckID_is_UNREADABLE_and_not_a_clean_server()
        {
            // THE ARM THE FIRST FIX LEFT OPEN, and the highest-impact one: the file has a CheckID
            // COLUMN and 124 real data rows, and every value in that column is blank. Every guard
            // written on 2026-08-26 morning passes it - the file is not empty, it has a header, it
            // has the column, it has rows - and the fired set comes back empty, which scored the
            // server 100%, maturity L5 Governed, 301 of 301 checks passed, with no banner and no
            // log line. Measured on the rendered page before this test existed.
            //
            // The input is the CAPTURED payload with its CheckID VALUES emptied, not a typed CSV.
            using var dir = new TempDir();
            dir.Write("MSI_sp_Blitz_20260826_210000.csv",
                WithBlankedColumn(
                    AuditOutputScannerBlitzTests.FixtureText(AuditOutputScannerBlitzTests.CanonicalFixture),
                    "CheckID"));

            var file = await ScanOne(dir);
            await NewScanner(dir).LoadFiredChecksAsync(file);

            file.ParseOutcome.Should().Be(AuditParseOutcome.Unreadable,
                "an empty fired set from a file with rows in it has measured nothing. sp_Blitz "
                + "writes a row only when a check FIRES and always emits its own rows on a live "
                + "run, so an all-pass result is the tell that parsing failed");
            file.IsScorable.Should().BeFalse();
            file.FiredCheckCounts.Should().BeEmpty();
            file.ParseFailureReason.Should().Contain("CheckID",
                "the reason names the column that yielded nothing");
            file.ParseFailureReason.Should().Contain("no fired-check evidence");
        }

        [Fact]
        public async Task An_sp_triage_export_carrying_no_sp_Blitz_sections_is_UNREADABLE()
        {
            // The same defect reached WITHOUT mutating anything: an ordinary sp_triage export whose
            // Section labels never start "sp_Blitz:" is a real shape a real run produces. It carries
            // no sp_Blitz check evidence at all, so scoring it prints a full maturity ladder off a
            // file that measured none of it.
            //
            // Hand-built, and labelled as such: Tests/SQLTriage.Tests/Fixtures holds captured
            // sp_Blitz payloads and no captured sp_triage one. The columns here are the ones the
            // scanner resolves by name, taken from the shape asserted in
            // AuditOutputScannerBlitzTests.A_domainless_blitz_file_adopts_the_domain...
            using var dir = new TempDir();
            dir.Write("MSI_sp_triage_20260826_210000.csv",
                "ID,evaldate,domain,SQLInstance,SectionID,Section,Summary,Severity,Details,HoursToResolveWithTesting,QueryPlan\r\n"
                + "1,2026-08-26,WORKGROUP,MSI\\OLD2017,4,Configuration,max server memory is default,2,set it,1,\r\n"
                + "2,2026-08-26,WORKGROUP,MSI\\OLD2017,7,Waits,CXPACKET dominates,3,review MAXDOP,2,\r\n");

            var file = await ScanOne(dir);
            file.FileType.Should().Be(AuditFileType.SpTriage);
            await NewScanner(dir).LoadFiredChecksAsync(file);

            file.ParseOutcome.Should().Be(AuditParseOutcome.Unreadable);
            file.ParseFailureReason.Should().Contain("SectionID",
                "the reason has to name what this file did not carry, because the operator's next "
                + "step is to run sp_Blitz for that server");
        }

        [Fact]
        public void An_empty_fired_set_from_an_unreadable_file_never_reaches_a_score()
        {
            // The consequence, through the production scoring rule rather than a second copy of it.
            var catalog = BlitzDashboardService.BuildCatalog(
                new[] { BlitzCheck("SQLT-BLITZ-1", "1", "Databases Not Being Backed Up") },
                mapJson: null);

            var parsedAndEmpty = new AuditedFile
            {
                SqlInstance = "MSI\\OLD2017",
                FileType = AuditFileType.SpBlitz,
                ParseOutcome = AuditParseOutcome.Unreadable,
                ParseFailureReason = "no row carried a usable CheckID (124 data row(s) read)",
            };

            var report = BlitzDashboardService.ComputeInstanceReport(parsedAndEmpty, catalog);

            report.Scorable.Should().BeFalse();
            report.HealthScore.Should().Be(0, "a refused instance carries no number at all");
            BlitzDashboardService.EstateScore(new[] { report }).Should().Be(0,
                "and it must not average a phantom 100 into the estate");
        }

        [Fact]
        public async Task A_real_captured_sp_Blitz_export_still_reads_as_PARSED()
        {
            // The guard must not turn a good file red. Driven off the captured payload the real
            // writer produced, not hand-typed CSV, per the house lesson these fixtures serve.
            using var dir = new TempDir();
            dir.Write("MSI_sp_Blitz_20260824_082141.csv",
                AuditOutputScannerBlitzTests.FixtureText(AuditOutputScannerBlitzTests.CanonicalFixture));

            var file = await ScanOne(dir);
            await NewScanner(dir).LoadFiredChecksAsync(file);

            file.ParseOutcome.Should().Be(AuditParseOutcome.Parsed);
            file.IsScorable.Should().BeTrue();
            file.ParseFailureReason.Should().BeEmpty();
            file.FiredCheckCounts.Should().NotBeEmpty();
        }

        [Fact]
        public void A_freshly_scanned_file_is_NotParsed_and_therefore_not_scorable()
        {
            // The third state matters as much as the second: a file nobody has read yet must not
            // read as "parsed, nothing fired" either.
            var file = new AuditedFile { SqlInstance = "MSI\\OLD2017" };

            file.ParseOutcome.Should().Be(AuditParseOutcome.NotParsed);
            file.IsScorable.Should().BeFalse();
        }

        [Fact]
        public void The_dashboard_refuses_to_score_an_instance_whose_file_could_not_be_read()
        {
            // The r1-06 dedup made the roadmap and the dashboard share ONE parser, which widened
            // this defect's blast radius rather than narrowing it: the silent arm now feeds both.
            var catalog = BlitzDashboardService.BuildCatalog(
                new[] { BlitzCheck("SQLT-BLITZ-1", "1", "Databases Not Being Backed Up") },
                mapJson: null);

            var unreadable = new AuditedFile
            {
                SqlInstance = "MSI\\OLD2017",
                FileType = AuditFileType.SpBlitz,
                ParseOutcome = AuditParseOutcome.Unreadable,
                ParseFailureReason = "no CheckID column in the header",
            };

            var report = BlitzDashboardService.ComputeInstanceReport(unreadable, catalog);

            report.Scorable.Should().BeFalse();
            report.UnscorableReason.Should().Contain("CheckID");
            report.UniverseSize.Should().Be(0,
                "there is no universe to score against when nothing was measured");
        }

        [Fact]
        public void An_unreadable_instance_does_not_average_a_phantom_100_into_the_estate()
        {
            var repo = new[] { BlitzCheck("SQLT-BLITZ-1", "1", "Databases Not Being Backed Up") };

            var healthy = new AuditedFile
            {
                SqlInstance = "MSI\\GOOD",
                FileType = AuditFileType.SpBlitz,
                ParseOutcome = AuditParseOutcome.Parsed,
                FiredCheckCounts = new Dictionary<int, int> { [1] = 1 },
            };
            var unreadable = new AuditedFile
            {
                SqlInstance = "MSI\\BAD",
                FileType = AuditFileType.SpBlitz,
                ParseOutcome = AuditParseOutcome.Unreadable,
                ParseFailureReason = "the file is empty (no header line)",
            };

            var catalog = BlitzDashboardService.BuildCatalog(repo, mapJson: null);
            var scored = BlitzDashboardService.ComputeInstanceReport(healthy, catalog);
            scored.HealthScore.Should().Be(0, "its only universe check fired");

            // The production rule itself, not a second copy of it in the test.
            var estateOfBoth = BlitzDashboardService.EstateScore(new[]
            {
                scored,
                BlitzDashboardService.ComputeInstanceReport(unreadable, catalog),
            });

            estateOfBoth.Should().Be(0,
                "the unreadable instance must contribute nothing. Averaging its phantom 100 in "
                + "would report the estate at 50% healthy on the strength of a file nobody read");
        }

        // ── Discovery: a file that names no server is reported, never dropped ───────────────

        [Fact]
        public async Task A_csv_that_names_no_server_is_REPORTED_by_the_scan_and_not_dropped()
        {
            // Before this, discovery `continue`d past a file whose header carried no ServerName
            // column: no log line, no return value, nothing. The page then rendered "No audit files
            // found in the output folder" with the file sitting in that folder, and an operator reads
            // that as "this server was never audited". A file that cannot be read and a server that
            // was never audited have opposite consequences for a client report.
            using var dir = new TempDir();
            dir.Write("MSI_sp_Blitz_20260826_211500.csv",
                WithoutColumn(
                    AuditOutputScannerBlitzTests.FixtureText(AuditOutputScannerBlitzTests.CanonicalFixture),
                    "ServerName"));

            var scan = await NewScanner(dir).ScanAsync();

            scan.Files.Should().BeEmpty("no row in the file names an instance to attribute it to");
            scan.Unreadable.Should().ContainSingle("and the file must still be accounted for");
            scan.Unreadable[0].FilePath.Should().EndWith("MSI_sp_Blitz_20260826_211500.csv");
            scan.Unreadable[0].Reason.Should().Contain("ServerName",
                "the reason names the column that was missing, or the operator cannot act on it");
        }

        [Fact]
        public async Task A_scan_of_a_readable_folder_reports_nothing_unreadable()
        {
            // The guard must not turn a good folder into a warning banner.
            using var dir = new TempDir();
            dir.Write("MSI_sp_Blitz_20260824_082141.csv",
                AuditOutputScannerBlitzTests.FixtureText(AuditOutputScannerBlitzTests.CanonicalFixture));

            var scan = await NewScanner(dir).ScanAsync();

            scan.Files.Should().ContainSingle();
            scan.Unreadable.Should().BeEmpty();
        }

        [Fact]
        public void The_roadmap_page_and_the_dashboard_both_surface_an_unattributable_file()
        {
            // Text tripwires, per this file's header: nothing in CI renders either page.
            RoadmapRazor().Should().Contain("_unattributedOutputFiles",
                "the roadmap must carry discovery's leftovers as their own state, or it prints an "
                + "empty-folder message over a folder with files in it");
            RoadmapRazor().Should().Contain("scan.Unreadable",
                "and it has to read them off the scan result rather than re-deriving them");

            var dashboard = File.ReadAllText(
                Path.Combine(FrkContractTests.RepoRoot(), "Pages", "BlitzDashboard.razor"));
            dashboard.Should().Contain("scan.Unreadable",
                "the dashboard prints 'No sp_Blitz output found' off the same scan, so it needs the "
                + "same half of the answer");
        }

        // ── Cluster F: the depth label's denominator (audit-r1-11) ──────────────────────────

        [Fact]
        public void Depth_is_measured_over_the_exceptions_the_card_counts_and_not_the_informational_rows()
        {
            // The card prints "N exceptions" from ChecksFired and "M informational" beside it, and
            // labels the percentage "Share of fired exceptions with full corpus analysis". Counting
            // the informational fires in the denominator read 33% analysed on an instance where
            // every exception that fired was corpus-backed.
            const string mapJson = """
            { "blitzCheckMap": [
              { "checkId": 172, "findingName": "Operating System Version", "category": "Server Info", "level": 1, "IsBad": 0 },
              { "checkId": 173, "findingName": "Server Last Restart",      "category": "Server Info", "level": 1, "IsBad": 0 }
            ] }
            """;
            var catalog = BlitzDashboardService.BuildCatalog(
                new[]
                {
                    BlitzCheck("SQLT-BLITZ-1", "1", "Databases Not Being Backed Up"),
                    BlitzCheck("SQLT-BLITZ-2", "2", "Full Recovery Model Without Log Backups"),
                },
                mapJson);

            var file = new AuditedFile
            {
                SqlInstance = "MSI\\OLD2017",
                FileType = AuditFileType.SpBlitz,
                ParseOutcome = AuditParseOutcome.Parsed,
                FiredCheckCounts = new Dictionary<int, int> { [1] = 1, [172] = 1, [173] = 1 },
            };

            var report = BlitzDashboardService.ComputeInstanceReport(file, catalog);

            report.ChecksFired.Should().Be(1, "one exception fired; 172 and 173 are banner rows");
            report.InfoFired.Should().Be(2, "and the card lists those two separately");
            report.AnalysisDepth.Should().Be(1.0,
                "the one exception that fired is corpus-backed, so 100% of the population the label "
                + "names is analysed. The denominator is ChecksFired, the number printed beside it");
        }

        [Fact]
        public void An_exception_with_no_corpus_analysis_lowers_depth()
        {
            // The other direction, so the rule above is not satisfied by always returning 1. A fired
            // id nobody can name IS an exception and carries no analysis, which is the gap this
            // number exists to show.
            var catalog = BlitzDashboardService.BuildCatalog(
                new[] { BlitzCheck("SQLT-BLITZ-1", "1", "Databases Not Being Backed Up") },
                mapJson: null);

            var file = new AuditedFile
            {
                SqlInstance = "MSI\\OLD2017",
                FileType = AuditFileType.SpBlitz,
                ParseOutcome = AuditParseOutcome.Parsed,
                FiredCheckCounts = new Dictionary<int, int> { [1] = 1, [217] = 1 },
            };

            var report = BlitzDashboardService.ComputeInstanceReport(file, catalog);

            report.ChecksFired.Should().Be(2, "217 is a real fireable id with no catalogue entry");
            report.UnclassifiedFired.Should().Be(1);
            report.AnalysisDepth.Should().Be(0.5);
        }

        // ── Cluster D: the watermark predicate ──────────────────────────────────────────────

        [Fact]
        public void An_unknown_server_watermarks_rather_than_reading_as_production()
        {
            // audit-r2-03. The empty list is the NORMAL shape when an operator drops an audit CSV
            // into the output folder by hand, which the app's own empty-state text invites: the
            // instance matches no configured connection, so the environment list comes back empty.
            ServerEnvironment.RequiresWatermark(Array.Empty<string?>()).Should().BeTrue(
                "absence of evidence that a server is production is not evidence that it is, and a "
                + "missing watermark ships a non-production report that reads as an assessment of "
                + "live infrastructure");

            ServerEnvironment.RequiresWatermark(null).Should().BeTrue();
        }

        [Theory]
        [InlineData(new[] { "Production" }, false)]
        [InlineData(new[] { "Production", "Production" }, false)]
        [InlineData(new[] { "Production", "Staging" }, true)]
        [InlineData(new[] { "Development" }, true)]
        [InlineData(new[] { "" }, true)]
        [InlineData(new string?[] { null }, true)]
        public void The_documented_rule_is_unchanged_for_a_known_server(string?[] environments, bool expected)
        {
            ServerEnvironment.RequiresWatermark(environments).Should().Be(expected,
                "the fix widens the predicate to cover the empty case; it must not move any case "
                + "that already had an answer");
        }

        // ── The razor's call sites (text tripwires, see the file header) ─────────────────────

        private static string RoadmapRazor() => File.ReadAllText(
            Path.Combine(FrkContractTests.RepoRoot(), "Pages", "DiagnosticsRoadmap.razor"));

        [Fact]
        public void The_roadmap_page_builds_its_universe_through_the_shared_filter()
        {
            var razor = RoadmapRazor();

            razor.Should().Contain("CheckUniverse.CanFire(checkId)",
                "the client-facing roadmap must use the SAME universe rule as the dashboard. It had "
                + "no rule at all until 2026-08-26, which is how retired and never-emitted ids "
                + "scored as passes in the client PDF");
        }

        [Fact]
        public void Every_exit_from_the_roadmap_mapping_loader_raises_the_data_missing_flag()
        {
            // audit-r2-04. One of three exits set the flag; the other two returned an empty or
            // partial map that painted the identical misleading 100% the flag exists to prevent.
            var razor = RoadmapRazor();
            var loader = Between(razor, "private Dictionary<int, (string FindingName, int Level, string Roadmap, string Tag, bool IsBad)> LoadRoadmapMapping()", "LoadRoadmapExtras()");

            loader.Should().NotBeEmpty("the loader method must still be findable by name");

            var arms = loader.Split("_roadmapDataMissing = true").Length - 1;
            arms.Should().BeGreaterThanOrEqualTo(4,
                "there are four ways out of this method with nothing usable: no json, no "
                + "blitzCheckMap property, a parse exception, and a well-formed file that yields an "
                + "empty map. Each has to say so, or the page scores an empty universe at 100%");

            loader.Should().NotContain("catch { ",
                "a bare swallowing catch is how two of these arms went silent in the first place");
        }

        [Fact]
        public void An_unreadable_audit_file_suppresses_the_score_and_both_exports()
        {
            var razor = RoadmapRazor();

            razor.Should().Contain("_auditDataUnreadable",
                "the page needs its own state for 'the file could not be read', distinct from "
                + "'the mapping is missing' and from 'nothing fired'");

            razor.Should().Contain("!_roadmapDataMissing && !_auditDataUnreadable",
                "the main roadmap region must be gated on both suppression states");

            razor.Should().Contain("if (_roadmapDataMissing || _auditDataUnreadable) return;",
                "the Action Plan CSV is a projection of the same numbers, so it needs the same "
                + "refusal. Leaving it enabled is exactly what audit-r2-04 documented");

            razor.Should().Contain("_roadmapDataMissing || _auditDataUnreadable) return;",
                "and so does the PDF export handler");
        }

        // ── Which file the app picks when the operator has not picked one ───────────────────
        //
        // The cold gate's B-2, found by rendering the page: refusing to score an unreadable file
        // (right) turned into withholding the readable report next to it (wrong), because the
        // automatic pick ranked file TYPE above whether the file could be read at all. One
        // Blitz-evidence-free sp_triage export therefore blanked the whole roadmap while a
        // scoreable sp_Blitz export for the same instance sat deselected.

        private sealed record Candidate(
            AuditFileType FileType,
            DateTime AuditDate,
            AuditParseOutcome ParseOutcome,
            string Name) : IAuditFileCandidate;

        // Named Cand and not File: this class reads its own source files through System.IO.File.
        private static Candidate Cand(string name, AuditFileType type, string date,
            AuditParseOutcome outcome = AuditParseOutcome.NotParsed)
            => new(type, DateTime.Parse(date, System.Globalization.CultureInfo.InvariantCulture),
                   outcome, name);

        [Fact]
        public void A_readable_file_outranks_one_proved_unreadable_whatever_its_type_or_date()
        {
            var candidates = new[]
            {
                Cand("triage-today.csv", AuditFileType.SpTriage, "2026-08-26", AuditParseOutcome.Unreadable),
                Cand("blitz-yesterday.csv", AuditFileType.SpBlitz, "2026-08-25", AuditParseOutcome.Parsed),
            };

            AuditFileSelection.InPreferenceOrder(candidates).First().Name
                .Should().Be("blitz-yesterday.csv",
                    "the newer sp_triage export carries no readable check evidence, so preferring it "
                    + "suppresses the whole page and withholds a report the operator can have");
        }

        [Fact]
        public void The_documented_order_is_unchanged_for_files_that_can_be_read()
        {
            var candidates = new[]
            {
                Cand("blitz-today.csv", AuditFileType.SpBlitz, "2026-08-26"),
                Cand("triage-yesterday.csv", AuditFileType.SpTriage, "2026-08-25"),
            };

            AuditFileSelection.InPreferenceOrder(candidates).First().Name
                .Should().Be("triage-yesterday.csv",
                    "sp_triage still beats sp_Blitz, and a newer file does not overturn that. Only "
                    + "PROVED unreadability moves a candidate, and only downwards");
        }

        [Fact]
        public void Newest_wins_inside_one_file_type()
        {
            var candidates = new[]
            {
                Cand("triage-old.csv", AuditFileType.SpTriage, "2026-08-20"),
                Cand("triage-new.csv", AuditFileType.SpTriage, "2026-08-26"),
            };

            AuditFileSelection.InPreferenceOrder(candidates).First().Name.Should().Be("triage-new.csv");
        }

        [Fact]
        public void A_file_nobody_has_tried_is_not_demoted()
        {
            // Rank is demoted by EVIDENCE (a parse attempt that failed), never by a guess. An
            // untried file must keep its documented place, or the page starts preferring whichever
            // file it happened to open first.
            var candidates = new[]
            {
                Cand("blitz-parsed.csv", AuditFileType.SpBlitz, "2026-08-26", AuditParseOutcome.Parsed),
                Cand("triage-untried.csv", AuditFileType.SpTriage, "2026-08-20"),
            };

            AuditFileSelection.InPreferenceOrder(candidates).First().Name
                .Should().Be("triage-untried.csv");
        }

        [Fact]
        public void Every_candidate_is_returned_and_the_unreadable_ones_come_last()
        {
            var candidates = new[]
            {
                Cand("triage-dead.csv", AuditFileType.SpTriage, "2026-08-26", AuditParseOutcome.Unreadable),
                Cand("blitz-dead.csv", AuditFileType.SpBlitz, "2026-08-26", AuditParseOutcome.Unreadable),
                Cand("blitz-live.csv", AuditFileType.SpBlitz, "2026-08-24", AuditParseOutcome.Parsed),
            };

            // Nothing is dropped from the list. A skipped file is still listed and still
            // selectable by hand; it is only refused the automatic pick.
            AuditFileSelection.InPreferenceOrder(candidates).Select(c => c.Name)
                .Should().Equal(new[] { "blitz-live.csv", "triage-dead.csv", "blitz-dead.csv" });

            AuditFileSelection.IsProvedUnreadable(candidates[0]).Should().BeTrue();
            AuditFileSelection.IsProvedUnreadable(candidates[2]).Should().BeFalse();
        }

        [Fact]
        public void The_page_falls_back_only_where_the_app_chose_the_file()
        {
            var razor = RoadmapRazor();

            razor.Should().Contain("AuditFileSelection.InPreferenceOrder",
                "the page must rank candidate files through the shared rule, not a second copy of it");

            var onInit = Between(razor, "protected override async Task OnInitializedAsync()", "public void Dispose()");
            onInit.Should().Contain("await RecalculateWithFallbackAsync();",
                "first load is the app choosing the file, so a dead pick must fall back to a "
                + "readable file for the same instance");

            var toggleDomain = Between(razor, "private async Task ToggleDomain(", "private int    _overallLevel");
            toggleDomain.Should().Contain("await RecalculateWithFallbackAsync();",
                "selecting a whole domain is also the app choosing the file per instance");

            var onServerToggled = Between(razor, "private async Task OnServerToggled(", "private async Task LoadServerData(");
            onServerToggled.Should().NotBeEmpty("the handler must still be findable by name");
            onServerToggled.Should().NotContain("RecalculateWithFallbackAsync",
                "a file the OPERATOR selected is never substituted. That path fails closed and says "
                + "why, which is the audit-r1-07 guard and must stay reachable");
        }

        [Fact]
        public void Every_file_the_automatic_pick_stepped_over_is_named_on_screen()
        {
            var razor = RoadmapRazor();

            razor.Should().Contain("_autoSelectSkipped[unreadable.FilePath] =",
                "a substitution the operator cannot see is the same silence the lane exists to close");

            razor.Should().Contain("@foreach (var note in _autoSelectSkipped.Values)",
                "and the page has to render them, not just hold them");

            razor.Should().Contain("_autoSelectSkipped.Remove(server.FilePath)",
                "once the operator selects the skipped file themselves the page refuses to score it, "
                + "so it must not also claim it scored something else instead");
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Re-emits a captured CSV with one named column's VALUES emptied and the column itself
        /// kept, by the writer's own quoting rule. This is the shape a truncated export or a query
        /// change produces: the header still promises the column, and nothing is under it.
        /// </summary>
        private static string WithBlankedColumn(string csv, string columnName)
        {
            using var reader = new StringReader(csv);
            var records = CsvParser.ParseRecords(reader).ToList();
            records.Should().NotBeEmpty();

            var target = records[0].FindIndex(h =>
                h.Trim('\'', '"', ' ').Equals(columnName, StringComparison.OrdinalIgnoreCase));
            target.Should().BeGreaterThanOrEqualTo(0,
                "the captured fixture must really carry a " + columnName + " column to blank");

            var sb = new System.Text.StringBuilder();
            for (var row = 0; row < records.Count; row++)
            {
                var fields = records[row]
                    .Select((f, i) => row > 0 && i == target ? "" : f)
                    .Select(f => "\"" + f.Replace("\"", "\"\"") + "\"");
                sb.Append(string.Join(",", fields)).Append("\r\n");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Re-emits a captured CSV with one named column removed, by the SAME quoting rule the app's
        /// writer uses (every field quoted, embedded quotes doubled). This is a truncated export or
        /// a hand-edited file, built out of a real one rather than typed.
        /// </summary>
        private static string WithoutColumn(string csv, string columnName)
        {
            using var reader = new StringReader(csv);
            var records = CsvParser.ParseRecords(reader).ToList();
            records.Should().NotBeEmpty();

            var drop = records[0].FindIndex(h =>
                h.Trim('\'', '"', ' ').Equals(columnName, StringComparison.OrdinalIgnoreCase));
            drop.Should().BeGreaterThanOrEqualTo(0,
                "the captured fixture must really carry a " + columnName + " column to remove");

            var sb = new System.Text.StringBuilder();
            foreach (var record in records)
            {
                var kept = record.Where((_, i) => i != drop)
                                 .Select(f => "\"" + f.Replace("\"", "\"\"") + "\"");
                sb.Append(string.Join(",", kept)).Append("\r\n");
            }
            return sb.ToString();
        }

        private static string Between(string text, string startMarker, string endMarker)
        {
            var start = text.IndexOf(startMarker, StringComparison.Ordinal);
            if (start < 0) return "";
            var end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
            return end < 0 ? text[start..] : text[start..end];
        }

        private static SqlCheck BlitzCheck(string id, string source, string name) => new()
        {
            Id = id,
            Source = source,
            Name = name,
            Category = "Reliability",
            Severity = "High",
            IsBad = true,
            ScoreWeight = 1,
            EffortHours = 1.0,
        };

        private static AuditOutputScanner NewScanner(TempDir dir) =>
            new(NullLogger<AuditOutputScanner>.Instance, dir.Path);

        private static async Task<AuditedFile> ScanOne(TempDir dir)
        {
            var scanned = (await NewScanner(dir).ScanAsync()).Files;
            scanned.Should().ContainSingle("the fixture directory holds exactly one audit file");
            return scanned[0];
        }

        private sealed class TempDir : IDisposable
        {
            public string Path { get; } = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "sqlt-audit-universe-" + Guid.NewGuid().ToString("N"));

            public TempDir() => Directory.CreateDirectory(Path);

            public void Write(string name, string content) =>
                File.WriteAllText(System.IO.Path.Combine(Path, name), content,
                    new System.Text.UTF8Encoding(false));

            public void Dispose()
            {
                try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
            }
        }
    }
}
