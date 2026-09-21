/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Strings lane, cluster 2 (2026-08-28): alerts that measured one quantity while their
    /// client-facing prose asserted another. Every test here pins a fact that was FALSE at
    /// 84beb1c and is true now, against the catalogue that actually ships.
    ///
    /// <para>These are structural pins, not live probes. What each fix does on a real instance was
    /// proved separately against <c>.\new2022</c> and <c>.\old2017</c> and is recorded in the lane
    /// report; the numbers appear in comments here only to say what the pin is protecting.</para>
    /// </summary>
    public class AlertCluster2MeasurementHonestyTests
    {
        private static readonly string RepoRoot = FindRepoRoot();
        private static readonly Lazy<JsonDocument> Catalogue = new(() =>
            JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "Config", "alert-definitions.json"))));

        // Anchored on SQLTriage.sln, the repo's own convention (RawPassedScan.RepoRoot). Probing for
        // Config/alert-definitions.json instead would match the TEST OUTPUT directory, which carries
        // its own copy - and these tests would then pin the build output rather than the source.
        private static string FindRepoRoot() => RawPassedScan.RepoRoot().FullName;

        private static JsonElement Alert(string id) =>
            Catalogue.Value.RootElement.GetProperty("alerts").EnumerateArray()
                .Single(a => a.GetProperty("id").GetString() == id);

        private static string Query(string id) => Alert(id).GetProperty("query").GetString() ?? "";
        private static string Description(string id) => Alert(id).GetProperty("description").GetString() ?? "";
        private static string Unit(string id) => Alert(id).GetProperty("unit").GetString() ?? "";

        // ── strings-r2-04b: io_error measured cumulative stall, not errors ────────────────────

        /// <summary>
        /// The defect: the executed measurement counted files whose LIFETIME cumulative I/O stall
        /// exceeded 5 s, so a Critical named "SQL Server has encountered an I/O error on a database
        /// file" fired on every server that had simply been running a while. PROVED on .\new2022:
        /// 14 of 33 files after 96 minutes of uptime, while msdb.dbo.suspect_pages held 0 rows.
        /// </summary>
        [Fact]
        public void Io_error_measures_suspect_pages_and_never_cumulative_io_stall()
        {
            var q = Query("io_error");
            Assert.Contains("msdb.dbo.suspect_pages", q, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dm_io_virtual_file_stats", q, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("io_stall", q, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Only unresolved events count. 4/5/7 are restored, repaired and deallocated: a
        /// page that has been fixed must stop firing, or the alert becomes permanent again by a
        /// second route.</summary>
        [Fact]
        public void Io_error_counts_only_unrepaired_suspect_page_event_types()
        {
            Assert.Matches(new Regex(@"event_type\s+IN\s*\(\s*1\s*,\s*2\s*,\s*3\s*\)", RegexOptions.IgnoreCase),
                Query("io_error"));
        }

        /// <summary>
        /// The handler an un-migrated install still routes through has to measure the same honest
        /// quantity. An install whose io_error was edited matches no superseded signature and is
        /// therefore never re-based, so fixing only the catalogue would leave it firing falsely.
        /// </summary>
        [Fact]
        public void The_io_error_handler_measures_the_same_quantity_as_the_catalogue()
        {
            var handler = AlertEvaluationService.BuildIoErrorCheckSql();
            Assert.Contains("msdb.dbo.suspect_pages", handler, StringComparison.OrdinalIgnoreCase);
            Assert.Matches(new Regex(@"event_type\s+IN\s*\(\s*1\s*,\s*2\s*,\s*3\s*\)", RegexOptions.IgnoreCase), handler);
            Assert.DoesNotContain("io_stall", handler, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The dead-query defect: io_error's queryMode routed the evaluator past its own query
        /// field, so an operator editing that SQL changed nothing. Dropping the mode is what makes
        /// the control real.
        /// </summary>
        [Fact]
        public void Io_error_no_longer_carries_a_queryMode_so_its_config_query_is_what_runs()
        {
            Assert.False(Alert("io_error").TryGetProperty("queryMode", out _));
        }

        /// <summary>
        /// Under strict <c>&gt;</c> a warning threshold of 1 needs TWO suspect pages before it
        /// fires. One unreadable page is an incident, so the band has to admit a count of 1.
        /// </summary>
        [Fact]
        public void One_unrepaired_suspect_page_is_enough_to_fire_io_error()
        {
            var thresholds = Alert("io_error").GetProperty("thresholds");
            var warning = thresholds.GetProperty("warning").GetDouble();
            Assert.True(AlertEvaluationService.IsThresholdBreached(1, warning, "greater_than"),
                "a single unrepaired suspect page must breach the warning band");
            Assert.False(AlertEvaluationService.IsThresholdBreached(0, warning, "greater_than"),
                "a clean instance must stay quiet");
        }

        /// <summary>The boundary the measurement cannot see is named in the prose, not left for the
        /// operator to discover.</summary>
        [Fact]
        public void Io_error_description_states_the_transient_retry_boundary()
        {
            Assert.Contains("825", Description("io_error"));
            Assert.Contains("suspect_pages", Description("io_error"), StringComparison.OrdinalIgnoreCase);
        }

        // ── strings-r1-06: five alerts that measured master and no user database ──────────────

        /// <summary>
        /// ExecuteAlertQueryAsync opens every standard alert's connection on master, so a
        /// database-scoped query measured master alone. PROVED on .\new2022: the shipped queries saw
        /// 1 file where the instance has 29 across 11 databases; log_space_full read master at 5.2
        /// percent while a real file stood at 96.6 percent of its current size.
        /// </summary>
        [Theory]
        [InlineData("database_space_full")]
        [InlineData("log_space_full")]
        [InlineData("filegroup_space")]
        public void Space_alerts_iterate_every_accessible_database_rather_than_the_connected_one(string id)
        {
            var q = Query(id);
            Assert.Contains("sys.databases", q, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("HAS_DBACCESS", q, StringComparison.OrdinalIgnoreCase);
            // A bare sys.database_files read carries no database dimension at all: it is exactly the
            // shape that made these measure master. It may appear only inside the per-database batch.
            Assert.Contains("QUOTENAME", q, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Vlf_count_reads_every_database_log_not_only_the_connected_one()
        {
            var q = Query("vlf_count");
            Assert.Contains("sys.databases", q, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("dm_db_log_info(d.database_id)", q, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dm_db_log_info(DB_ID())", q, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Disk_space_low_reads_every_volume_not_one_file_of_the_current_database()
        {
            var q = Query("disk_space_low");
            Assert.Contains("sys.master_files", q, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dm_os_volume_stats(DB_ID()", q, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The plural prose is now earned. This is the claim strings-r1-06 filed: "a disk volume"
        /// over a query that read exactly one file of one database.
        /// </summary>
        [Theory]
        [InlineData("disk_space_low")]
        [InlineData("database_space_full")]
        [InlineData("log_space_full")]
        [InlineData("filegroup_space")]
        [InlineData("vlf_count")]
        public void The_scope_actually_measured_is_named_in_the_description(string id)
        {
            var d = Description(id);
            Assert.True(d.Contains("every", StringComparison.OrdinalIgnoreCase)
                        || d.Contains("all volumes", StringComparison.OrdinalIgnoreCase),
                id + " must say what it examines: " + d);
        }

        /// <summary>
        /// A log at 96 percent of its CURRENT size but free to grow is not running out of space.
        /// Measuring percent-of-current-size across every database would have replaced a silent
        /// alert with a permanent false Critical: model's 8 MB log measured 96.6 percent on a stock
        /// instance. The ceiling has to be the largest size the file may reach.
        /// </summary>
        [Theory]
        [InlineData("database_space_full")]
        [InlineData("log_space_full")]
        [InlineData("filegroup_space")]
        public void Space_alerts_measure_against_the_largest_size_the_file_may_reach(string id)
        {
            var q = Query(id);
            Assert.Contains("max_size", q, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("growth", q, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("available_bytes", q, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("autogrowth", Description(id), StringComparison.OrdinalIgnoreCase);
        }

        // ── strings-r2-05: since-startup totals asserted as current, MAX called an Average ────

        /// <summary>
        /// The DMV is cumulative since instance start, so a single read can only ever be a lifetime
        /// figure. PROVED on .\new2022 before the fix: io_stall_time stood at 141 ms against a
        /// critical of 100, and disk_latency_write at 537 ms against a critical of 50, on an idle
        /// healthy instance - and a lifetime average barely moves, so neither could ever recover.
        /// </summary>
        [Theory]
        [InlineData("io_stall_time")]
        [InlineData("disk_latency_read")]
        [InlineData("disk_latency_write")]
        public void Io_latency_alerts_difference_two_readings_instead_of_reading_a_lifetime_total(string id)
        {
            var q = Query(id);
            Assert.Contains("WAITFOR DELAY", q, StringComparison.OrdinalIgnoreCase);
            // Two reads of the DMV, and the value is built from their difference.
            Assert.True(Regex.Matches(q, "dm_io_virtual_file_stats", RegexOptions.IgnoreCase).Count >= 2,
                id + " must read the DMV twice to produce a delta");
            Assert.Contains("- s.stall", q, StringComparison.Ordinal);
            Assert.Contains("- s.ops", q, StringComparison.Ordinal);
        }

        /// <summary>
        /// Integer division truncated the result to whole milliseconds. PROVED on .\new2022: the
        /// shipped disk_latency_read returned 1 where the true per-read figure was 1.625.
        /// </summary>
        [Theory]
        [InlineData("io_stall_time")]
        [InlineData("disk_latency_read")]
        [InlineData("disk_latency_write")]
        public void Io_latency_alerts_divide_in_floating_point(string id)
        {
            Assert.Contains("* 1.0 /", Query(id), StringComparison.Ordinal);
        }

        /// <summary>These read MAX, the single worst file. Calling that an "Average" was the filed
        /// claim; the word must not come back.</summary>
        [Theory]
        [InlineData("io_stall_time")]
        [InlineData("disk_latency_read")]
        [InlineData("disk_latency_write")]
        public void Io_latency_alerts_do_not_describe_their_MAX_as_an_average(string id)
        {
            var d = Description(id);
            Assert.Contains("MAX(", Query(id), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("worst", d, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("average disk", d, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Average I/O stall", d, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The sample window is a fact about the measurement, so the operator is told it,
        /// including what a zero means.</summary>
        [Theory]
        [InlineData("io_stall_time")]
        [InlineData("disk_latency_read")]
        [InlineData("disk_latency_write")]
        public void Io_latency_descriptions_state_the_window_and_what_zero_means(string id)
        {
            var d = Description(id);
            Assert.Contains("five second", d, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("value is 0 when", d, StringComparison.OrdinalIgnoreCase);
        }

        // ── strings-r2-11: instance-wide waits presented as tempdb allocation contention ──────

        /// <summary>
        /// sys.dm_os_wait_stats carries neither a database nor a page-type column, so nothing in the
        /// old query could scope it to tempdb or to allocation pages. PROVED on .\new2022: 67,059
        /// PAGELATCH_EX waiting tasks from user-database activity were summed in as tempdb
        /// contention.
        /// </summary>
        [Fact]
        public void Tempdb_contention_carries_both_a_database_and_a_page_type_dimension()
        {
            var q = Query("tempdb_contention");
            Assert.DoesNotContain("dm_os_wait_stats", q, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("dm_os_waiting_tasks", q, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("'2:%'", q, StringComparison.Ordinal);          // tempdb only
            Assert.Contains("8088", q, StringComparison.Ordinal);            // PFS interval
            Assert.Contains("511232", q, StringComparison.Ordinal);          // GAM/SGAM interval
        }

        /// <summary>The quantity changed from an average wait time to a count of waiting tasks, so
        /// the declared unit has to change with it.</summary>
        [Fact]
        public void Tempdb_contention_declares_the_unit_it_now_returns()
        {
            Assert.Equal("count", Unit("tempdb_contention"));
            Assert.Contains("not a wait time", Description("tempdb_contention"), StringComparison.OrdinalIgnoreCase);
        }

        // ── strings-r1-03: process CPU described as total ─────────────────────────────────────

        /// <summary>
        /// PROVED on .\new2022, both values off the same ring-buffer record: ProcessUtilization was
        /// 0 while total processor utilization was 6. The query reads the process figure; only the
        /// words were wrong, so only the words changed.
        /// </summary>
        [Fact]
        public void Processor_alert_names_the_process_share_it_actually_reads()
        {
            var q = Query("processor_under_utilization");
            Assert.Contains("ProcessUtilization", q, StringComparison.Ordinal);
            Assert.DoesNotContain("SystemIdle", q, StringComparison.Ordinal);

            var d = Description("processor_under_utilization");
            Assert.Contains("SQL Server process", d, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not total processor utilization", d, StringComparison.OrdinalIgnoreCase);

            var name = Alert("processor_under_utilization").GetProperty("name").GetString() ?? "";
            Assert.Contains("Process", name, StringComparison.OrdinalIgnoreCase);
        }

        // ── strings-r2-07: a declared unit nothing computed, that the editor then destroyed ───

        /// <summary>
        /// The query returns raw elapsed seconds from sysjobactivity and no baseline is computed
        /// anywhere, so "percent_of_average" named a quantity the alert never produced.
        /// </summary>
        [Fact]
        public void Agent_job_long_running_declares_the_seconds_it_actually_returns()
        {
            Assert.Equal("seconds", Unit("agent_job_long_running"));
            Assert.Contains("DATEDIFF(SECOND", Query("agent_job_long_running"), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not compared against", Description("agent_job_long_running"), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The editor's Unit control is a @bind select. A unit with no matching option is not merely
        /// displayed wrong: the bind writes the first option back over it, so opening the alert and
        /// saving silently rewrote its declared unit to "percent". Every unit the catalogue ships
        /// must therefore be representable, or the editor destroys the fact that says what the
        /// alert's number means.
        /// </summary>
        [Fact]
        public void Every_unit_the_catalogue_ships_can_be_represented_by_the_alert_editor()
        {
            var razor = File.ReadAllText(Path.Combine(RepoRoot, "Pages", "Alerts.razor"));
            var selectable = new HashSet<string>(
                Regex.Matches(razor, "<option value=\"([a-z_]+)\">").Select(m => m.Groups[1].Value),
                StringComparer.Ordinal);

            var shipped = Catalogue.Value.RootElement.GetProperty("alerts").EnumerateArray()
                .Select(a => a.GetProperty("unit").GetString() ?? "")
                .Where(u => u.Length > 0).Distinct().ToList();

            var missing = shipped.Where(u => !selectable.Contains(u)).ToList();
            Assert.True(missing.Count == 0,
                "the alert editor cannot represent these shipped units, so @bind rewrites them on save: "
                + string.Join(", ", missing));
        }

        /// <summary>
        /// A unit the operator can select must also render in the message an alert pages out with.
        /// Before this, percent_of_average and percent_difference fell to the empty default and the
        /// notification named a bare number with no quantity attached.
        /// </summary>
        [Theory]
        [InlineData("percent_of_average", "% of average")]
        [InlineData("percent_difference", "% difference")]
        [InlineData("hours", " hrs")]
        [InlineData("minutes", " min")]
        [InlineData("seconds", "s")]
        [InlineData("megabytes", " MB")]
        public void Every_selectable_unit_renders_in_the_fired_message(string unit, string expectedSuffix)
        {
            var alert = new SQLTriage.Data.Models.AlertDefinition
            {
                Id = "u", Name = "U", Unit = unit, Operator = "greater_than",
                Thresholds = new SQLTriage.Data.Models.AlertThresholds { Warning = 1 }
            };
            var msg = AlertEvaluationService.FormatMessage(alert, "srv", 5, FiringBasis.Fixed(1), "warning");

            Assert.Contains("5.0" + expectedSuffix, msg, StringComparison.Ordinal);
        }

        /// <summary>The unitless case, kept separate so the pin above cannot be satisfied by an
        /// empty suffix: "count" renders as a bare number deliberately.</summary>
        [Fact]
        public void A_count_alert_renders_its_number_without_inventing_a_unit()
        {
            var alert = new SQLTriage.Data.Models.AlertDefinition
            {
                Id = "u", Name = "U", Unit = "count", Operator = "greater_than",
                Thresholds = new SQLTriage.Data.Models.AlertThresholds { Warning = 1 }
            };
            var msg = AlertEvaluationService.FormatMessage(alert, "srv", 5, FiringBasis.Fixed(1), "warning");
            Assert.Contains("5.0 (above the 1.0 warning threshold)", msg, StringComparison.Ordinal);
        }
    }
}
