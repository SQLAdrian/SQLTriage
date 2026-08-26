/* In the name of God, the Merciful, the Compassionate */

// ── livewaits.verdict: THE WAIT-STATS VERDICT WIDGET. Rulings D1(a) and D3(a), 2026-08-24 ─────
//
// WHAT IT IS. The Live Wait Stats page showed eight numbers and three charts and never said
// whether any of them was bad. This adds one DataGrid that puts a severity in a column, using
// thresholds quoted from the vendored sp_PerfCheck 2.8 rather than invented here:
//   report floor  10.0 % of uptime   scripts/sp_PerfCheck.sql:63   (@significant_wait_threshold_pct)
//                                    applied as the FINDING filter at :2354
//   High          50.0 % of uptime   scripts/sp_PerfCheck.sql:64   (@wait_high_pct)
//                 or avg wait >= 1000 ms                    :2284
//   Medium        20.0 % of uptime   scripts/sp_PerfCheck.sql:65   (@wait_medium_pct)
//                 or avg wait >=  250 ms                    :2290
//   Parallelism   Medium at most, and only at >= 100 %      :2294-2295
//   the 14-category taxonomy                                :2166-2195
//   the wait-type ALLOW-LIST                                :2200-2238
//
// THE ALLOW-LIST IS THE LOAD-BEARING PART, AND THE DESIGN GOT IT WRONG. The design document
// proposed filtering with a single "wait_type <> N'SLEEP_TASK'". Run verbatim against .\new2022 on
// 2026-08-24 that returned ten rows, and ALL TEN were idle background waits filed as 'Other' with
// verdict 'Low' - SOS_WORK_DISPATCHER at 8,948% of uptime, LOGMGR_QUEUE, DISPATCHER_QUEUE_SEMAPHORE,
// CLR_AUTO_EVENT, BROKER_* and so on. The grid was pure noise, and a real problem wait could never
// have reached the top ten. That is the same defect the design itself criticised in the old
// wait_time_anomaly alert. sp_PerfCheck does not use a deny-list here: :2200-2238 is an ALLOW-list
// of 38 wait types plus LIKE N'LCK%', which is why every row it reports has a real category. The
// shipped panel uses that allow-list, and the census below pins it so it cannot quietly revert.
//
// D3(a): CUMULATIVE SINCE RESTART, NORMALISED BY UPTIME. Darling's percentages are percentages of
// wall-clock uptime, so applying them to a five-minute delta would compare a number to a threshold
// describing a different quantity - the house's most-repeated defect class. The cost is that the
// figure is a LIFETIME AVERAGE, and the panel description says so in as many words rather than
// hiding it. The uptime expression, including the >= 24 day branch that avoids DATEDIFF's int
// overflow at ~24.8 days, is copied from scripts/sp_PerfCheck.sql:2031-2039.
//
// D6(b): every title and description this lane writes carries "(DRAFT wording, voice review
// pending)". The words are Adrian's to write; these tests pin the MEASUREMENT and the marker, and
// deliberately do not pin the prose.
//
// ── THE RENDERER DEFECT THIS LANE ALSO CLOSED ────────────────────────────────────────────────
//
// The verdict column needs two colour rules - High red, Medium amber - and DataGrid.GetCellColor
// selected rules with FirstOrDefault over the whole column, so only the FIRST rule authored for a
// column could ever fire. Two text-match rules on one column meant High coloured and Medium
// silently plain. Measured at a4c3a1c: Config/dashboard-config.json carried NO dataGridColumnColors
// at all (which is why nothing had visibly broken), while Config/dashboard-config.default.json
// carried 147 text-match rules across 12 panels (24 carry colour rules of some kind), 11 of them
// stacking MULTIPLE rules on one column, one holding 14 - all dead after the first. That second file has
// since been DELETED (DECISIONS 2026-08-26 18:23, ruling 3), so the measurement is history and the
// counts are only reachable at a4c3a1c. The fix walks all text-match
// rules for the column. The render test below fails against the pre-fix renderer.
//
// ── THE FIXTURE, AND EXACTLY HOW HONEST IT IS ────────────────────────────────────────────────
//
// Fixtures/captured-wait-verdict-rows.json is CAPTURED from a real execution against .\new2022 by
// Capture_the_rendered_rows_from_a_real_instance below, not hand-composed - the standing lesson is
// that two cold gates once passed against payloads the endpoint never sends. Genuine column names,
// genuine SQL and CLR types, genuine Category and Verdict strings computed by the engine's own CASE.
//
// ONE DISCLOSED SUBSTITUTION, and it is recorded inside the fixture itself. .\new2022 is an idle
// test instance that has been up seven days, and NO wait type on it reaches 10% of that uptime -
// the shipped query returns zero rows there, proved. So the capture runs the shipped SQL with the
// single literal "w.pct >= 10.0" replaced by "w.pct >= 0". Everything else, TOP (10) and ORDER BY
// included, is the shipped text byte for byte. The shipped floor is exercised separately, by the
// live smoke, against the unmodified query.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SQLTriage.Components.Shared;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    public class WaitVerdictPanelTests
    {
        private const string PanelId = "livewaits.verdict";
        private const string FixtureName = "captured-wait-verdict-rows.json";

        private const string TargetVar = "WAITVERDICT_LIVE_TARGET";
        private const string CaptureVar = "WAITVERDICT_CAPTURE_OUT";

        private static readonly string[] ExpectedColumns =
            { "Category", "Wait Type", "% of uptime", "Avg wait ms", "Hours", "Verdict" };

        private readonly ITestOutputHelper _out;
        public WaitVerdictPanelTests(ITestOutputHelper output) => _out = output;

        /// <summary>
        /// Inert unless armed, and SKIPPED rather than passed when it is not.
        /// </summary>
        public sealed class LiveFactAttribute : FactAttribute
        {
            public LiveFactAttribute(params string[] required)
            {
                var missing = required
                    .Where(v => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(v)))
                    .ToList();
                if (missing.Count > 0)
                    Skip = "live harness not armed; set " + string.Join(", ", missing);
            }
        }

        private static string Require(string name) =>
            Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException(
                name + " must be set; LiveFact should have skipped this test");

        // ── locating the REPO SOURCE config, not the build-output copy ───────────────────────
        // Same locator, and the same reason, as DashboardCounterArithmeticTests: walking up from
        // BaseDirectory looking for a "Config" folder finds the test assembly's own lowercase
        // "config\" output copy, which is only as fresh as the last build. A negative control
        // caught exactly that during an earlier lane. Anchor on the solution file instead.
        private static string ConfigPath(string fileName)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
            {
                if (!File.Exists(Path.Combine(dir.FullName, "SQLTriage.sln"))) continue;
                var candidate = Path.Combine(dir.FullName, "Config", fileName);
                if (File.Exists(candidate)) return candidate;
                throw new InvalidOperationException(
                    $"Found the repo root at {dir.FullName} but no Config/{fileName} beneath it.");
            }
            throw new InvalidOperationException(
                $"Could not locate the repo root (SQLTriage.sln) above {AppContext.BaseDirectory}.");
        }

        private static JsonElement FindPanel(string fileName, string panelId)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath(fileName)));
            var hit = Search(doc.RootElement.Clone(), panelId);
            hit.Should().NotBeNull("{0} must contain the panel {1}", fileName, panelId);
            return hit!.Value;
        }

        private static JsonElement? Search(JsonElement el, string panelId)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    if (el.TryGetProperty("id", out var id)
                        && id.ValueKind == JsonValueKind.String
                        && id.GetString() == panelId
                        && el.TryGetProperty("panelType", out _))
                        return el;
                    foreach (var p in el.EnumerateObject())
                    {
                        var found = Search(p.Value, panelId);
                        if (found is not null) return found;
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in el.EnumerateArray())
                    {
                        var found = Search(item, panelId);
                        if (found is not null) return found;
                    }
                    break;
            }
            return null;
        }

        private static string PanelSql(string fileName = "dashboard-config.json")
            => FindPanel(fileName, PanelId).GetProperty("query").GetProperty("sqlServer").GetString()!;

        // ══ Tier 1: the config census ════════════════════════════════════════════════════════

        /// <summary>
        /// The panel exists, is enabled, is a DataGrid, and is on the livewaits dashboard, in the config
        /// the app actually reads: DashboardConfigService's _configPath is
        /// BaseDirectory/Config/dashboard-config.json. The second row every census in this tier used to
        /// carry, dashboard-config.default.json, is deleted (DECISIONS 2026-08-26 18:23, ruling 3) — it
        /// was Content-Removed from every build and shipped by nothing, so keeping it in step was work
        /// that protected no install.
        /// </summary>
        [Theory]
        [InlineData("dashboard-config.json")]
        public void The_verdict_panel_ships_enabled_as_a_datagrid(string fileName)
        {
            var panel = FindPanel(fileName, PanelId);

            panel.GetProperty("panelType").GetString().Should().Be("DataGrid");
            panel.GetProperty("enabled").GetBoolean().Should().BeTrue();
            panel.GetProperty("defaultDatabase").GetString().Should().Be("master");

            panel.GetProperty("title").GetString().Should().Contain("DRAFT",
                "D6(b): client-facing wording is Adrian's, and ships marked until he has written it");
            panel.GetProperty("description").GetString().Should().Contain("DRAFT");
        }

        /// <summary>
        /// The honest caveat must be PRINTED, not merely known. A since-restart figure is a lifetime
        /// average: a server that was fine for forty days and has been drowning for two hours will
        /// not show it here. D3(a) was ruled on the explicit condition that the widget says so.
        /// </summary>
        [Theory]
        [InlineData("dashboard-config.json")]
        public void The_description_admits_the_figure_is_a_lifetime_average(string fileName)
        {
            var description = FindPanel(fileName, PanelId).GetProperty("description").GetString()!;

            description.Should().Contain("since the last SQL Server restart",
                "the measurement basis has to be in the words the client reads");
            description.ToLowerInvariant().Should().Contain("rather than its last hour",
                "naming the basis is not enough - the CONSEQUENCE of the basis is the caveat");
            description.Should().Contain("10%",
                "the reporting floor is why the grid can be empty, and an empty grid is otherwise "
                + "indistinguishable from a broken one");
        }

        /// <summary>
        /// The census that catches a later edit quietly dropping a category or moving a band. Every
        /// number and every category name here is quoted from the vendored proc.
        /// </summary>
        [Theory]
        [InlineData("dashboard-config.json")]
        public void The_query_carries_every_resource_category_and_both_numeric_bands(string fileName)
        {
            var sql = PanelSql(fileName);

            foreach (var category in new[] { "Locking", "Memory", "I/O", "TempDB Contention",
                                             "Transaction Log", "CPU" })
                sql.Should().Contain("N'" + category + "'",
                    "{0} is one of the six categories sp_PerfCheck:2278-2298 lets reach High", category);

            foreach (var category in new[] { "Parallelism", "Network", "Availability Groups",
                                             "Azure SQL Throttling", "Index Management",
                                             "Statistics", "Query Execution", "Other" })
                sql.Should().Contain("N'" + category + "'",
                    "{0} is part of the 14-category taxonomy at sp_PerfCheck:2166-2195", category);

            sql.Should().Contain("50.0", "High at 50% of uptime, sp_PerfCheck.sql:64");
            sql.Should().Contain("20.0", "Medium at 20% of uptime, sp_PerfCheck.sql:65");
            sql.Should().Contain("1000.0", "High by average wait, sp_PerfCheck.sql:2284");
            sql.Should().Contain("250.0", "Medium by average wait, sp_PerfCheck.sql:2290");
            sql.Should().Contain("100.0", "Parallelism tops out at Medium and only at 100%, :2294-2295");
            sql.Should().Contain("w.pct >= 10.0", "the reporting floor, sp_PerfCheck.sql:63 and :2354");

            sql.Should().Contain("N'High'").And.Contain("N'Medium'").And.Contain("N'Low'");
        }

        /// <summary>
        /// THE ALLOW-LIST. This is the assertion that would have caught the design's proposed SQL,
        /// which filtered only SLEEP_TASK and returned ten rows of idle noise off a real instance.
        /// </summary>
        /// <summary>
        /// The WHERE clause of the derived table, isolated. This matters: the allow-listed wait
        /// types are ALSO named in the category CASE, so a census over the whole query text cannot
        /// tell "filtered to these types" from "categorised these types". A first cut of this test
        /// searched the whole string and PASSED against a mutant whose allow-list had been deleted
        /// outright, because the CASE still named every type. Isolating the clause is what makes
        /// the assertion mean what it says.
        /// </summary>
        private static string AllowListClause(string fileName)
        {
            var sql = PanelSql(fileName);
            var from = sql.IndexOf("FROM sys.dm_os_wait_stats AS dows", StringComparison.Ordinal);
            from.Should().BeGreaterThan(0, "the derived table must read the wait-stats DMV");
            var where = sql.IndexOf("WHERE", from, StringComparison.Ordinal);
            where.Should().BeGreaterThan(from, "the derived table must carry a WHERE clause at all");
            var close = sql.IndexOf(") AS w", where, StringComparison.Ordinal);
            close.Should().BeGreaterThan(where);
            return sql.Substring(where, close - where);
        }

        [Theory]
        [InlineData("dashboard-config.json")]
        public void The_query_filters_to_sp_PerfCheck_allow_list_and_not_to_a_deny_list(string fileName)
        {
            var clause = AllowListClause(fileName);

            clause.Should().Contain("dows.wait_type IN (",
                "sp_PerfCheck:2200-2238 filters by an ALLOW-list; a WHERE without one lets every "
                + "idle background wait into the grid");
            clause.Should().Contain("LIKE N'LCK%'",
                "the allow-list's one prefix match, sp_PerfCheck.sql:2216");

            // The whole allow-list, in the clause itself. 39 equality types plus the LCK prefix.
            var allowed = Regex.Matches(clause, @"N'([A-Z_0-9]+)'")
                .Select(m => m.Groups[1].Value).ToList();
            // sp_PerfCheck:2200-2238 lists 38 equality types plus LIKE N'LCK%', counted at a4c3a1c.
            // SLEEP_TASK is one of the 38 - he collects it for context and drops it from the
            // findings at :2355 - so the panel puts the other 37 in the IN list and SLEEP_TASK in
            // its own "<> N'SLEEP_TASK'". 37 + 1 = 38 names in the clause either way.
            allowed.Should().HaveCount(38,
                "sp_PerfCheck:2200-2238 names 38 wait types by equality; a different number means "
                + "the allow-list was edited rather than copied");

            foreach (var t in new[] { "PAGEIOLATCH_SH", "RESOURCE_SEMAPHORE", "CXPACKET",
                                      "SOS_SCHEDULER_YIELD", "THREADPOOL", "PAGELATCH_EX",
                                      "WRITELOG", "ASYNC_NETWORK_IO", "HADR_SYNC_COMMIT",
                                      "RESMGR_THROTTLED", "BTREE_INSERT_FLOW_CONTROL",
                                      "WAIT_ON_SYNC_STATISTICS_REFRESH", "HTBUILD", "EXECSYNC" })
                allowed.Should().Contain(t, "{0} is in sp_PerfCheck's allow-list :2200-2238", t);

            // The negative control. These are the idle types the design's proposed filter let
            // through, measured on .\new2022 on 2026-08-24 as ten out of ten returned rows. An
            // allow-list cannot name them; a deny-list-shaped rewrite would.
            foreach (var noise in new[] { "SOS_WORK_DISPATCHER", "LOGMGR_QUEUE", "CLR_AUTO_EVENT",
                                          "BROKER_TASK_STOP", "DIRTY_PAGE_POLL", "XE_TIMER_EVENT" })
                PanelSql(fileName).Should().NotContain("N'" + noise + "'",
                    "{0} is an idle wait; naming it anywhere means the filter turned back into a "
                    + "deny-list, which is how the grid filled with noise", noise);

            clause.Should().Contain("dows.wait_type <> N'SLEEP_TASK'",
                "sp_PerfCheck collects SLEEP_TASK for context and excludes it from findings, :2355");
        }

        /// <summary>
        /// The colour rules must name a column the query actually returns, and dataGridColumns must
        /// be exactly the query's projection. A renamed column silently kills the colour rule, and
        /// nothing else in the suite would notice.
        /// </summary>
        [Theory]
        [InlineData("dashboard-config.json")]
        public void The_declared_columns_and_the_colour_rules_agree(string fileName)
        {
            var panel = FindPanel(fileName, PanelId);

            panel.GetProperty("dataGridColumns").EnumerateArray().Select(x => x.GetString())
                .Should().Equal(ExpectedColumns);

            var rules = panel.GetProperty("dataGridColumnColors").EnumerateArray().ToList();
            rules.Should().HaveCount(2, "one rule per coloured verdict: High and Medium");

            foreach (var rule in rules)
            {
                rule.GetProperty("mode").GetString().Should().Be("text-match");
                rule.GetProperty("column").GetString().Should().Be("Verdict")
                    .And.BeOneOf(ExpectedColumns);
            }

            rules.Select(r => r.GetProperty("matchValue").GetString()).Should().Equal("High", "Medium");
            rules.Select(r => r.GetProperty("matchColor").GetString()).Should().Equal("#f44336", "#ff9800");
        }

        /// <summary>
        /// The config loader REJECTS a dashboard whose panel SQL trips SqlSafetyValidator, so a
        /// panel that cannot pass it would take the whole configuration down rather than just
        /// itself. The panel uses a DECLARE batch, which is allowed (eleven shipped panels already
        /// do), but that is worth measuring rather than assuming.
        /// </summary>
        [Fact]
        public void The_panel_sql_passes_the_safety_validator_the_config_loader_runs()
        {
            var result = SqlSafetyValidator.Validate(PanelSql());
            result.IsSafe.Should().BeTrue(
                "DashboardConfigService.UpdateConfig throws SqlSafetyException on any unsafe panel "
                + "query, which would reject the entire configuration: {0}", result.Reason);
        }

        // A test here used to assert that dashboard-config.json and dashboard-config.default.json carried
        // the same verdict query. Its premise died with the seed file (DECISIONS 2026-08-26 18:23,
        // ruling 3). The drift class it guarded is real and did not go away, so it moved up a level and
        // got wider: DashboardConfigMigratorTests.The_embedded_catalogue_is_byte_identical_to_the_shipped_config_file
        // pins the WHOLE catalogue compiled into SQLTriage.dll against this file, which covers this panel's
        // query and every other one, instead of the panels somebody remembered to pair up.

        /// <summary>
        /// The signal_pct re-band. Darling's bands for the signal-wait ratio are 50 / 30 / 25
        /// (sp_PerfCheck.sql:2478, :2494-2497); the shipped tile's top band was 25, which is his
        /// BOTTOM band. It now reads 30 / 50. The old mojibake in that description
        /// ("pressure â€” queries") went with the rewrite - an em-dash encoded as
        /// UTF-8 and decoded as Latin-1, which a client read as mangled characters.
        /// </summary>
        [Theory]
        [InlineData("dashboard-config.json")]
        public void The_signal_pct_tile_uses_Darlings_bands_and_carries_no_mojibake(string fileName)
        {
            var panel = FindPanel(fileName, "livewaits.signal_pct");

            var bands = panel.GetProperty("colorThresholds").EnumerateArray()
                .Select(r => (Op: r.GetProperty("operator").GetString(),
                              Value: r.GetProperty("value").GetDouble()))
                .ToList();

            bands.Should().Equal(new[] { (">=", 30.0), (">=", 50.0) },
                "sp_PerfCheck.sql:2496 is Medium at 30 and :2494 is High at 50; the shipped 10/25 "
                + "topped out below his lowest band");

            var description = panel.GetProperty("description").GetString()!;
            description.Should().Contain("30%").And.Contain("50%");
            description.Should().Contain("DRAFT");

            // The mojibake gate, on the raw file text rather than the decoded string: the defect was
            // an escape sequence in the JSON, so decoding it away would hide exactly what is tested.
            var raw = File.ReadAllText(ConfigPath(fileName));
            raw.Should().NotContain("\\u00e2\\u20ac\\u201d",
                "an em-dash encoded UTF-8 and decoded Latin-1 reaches the client as mangled text");
            // And the same mangling written as raw characters rather than JSON escapes - the app's
            // own writer escapes non-ASCII, but a hand edit or an external tool would not.
            raw.Should().NotContain("\u00e2\u20ac",
                "mojibake as raw text is the same defect in a form the escape-sequence gate misses");
        }

        // ══ Tier 2: the RENDERER, driven from the captured rows ══════════════════════════════

        private sealed record CapturedCell(string Name, string SqlType, string ClrType, bool IsNull, string? Value);

        private static string FixturePath()
            => Path.Combine(AppContext.BaseDirectory, "Fixtures", FixtureName);

        private static (List<List<CapturedCell>> Rows, JsonElement Meta) LoadFixture()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(FixturePath()));
            var root = doc.RootElement.Clone();
            var rows = root.GetProperty("rows").EnumerateArray()
                .Select(r => r.EnumerateArray().Select(c => new CapturedCell(
                    c.GetProperty("name").GetString()!,
                    c.GetProperty("sqlType").GetString()!,
                    c.GetProperty("clrType").GetString()!,
                    c.GetProperty("isNull").GetBoolean(),
                    c.GetProperty("value").ValueKind == JsonValueKind.Null
                        ? null : c.GetProperty("value").GetString())).ToList())
                .ToList();
            return (rows, root);
        }

        /// <summary>
        /// Rebuilds the DataTable the panel path hands the grid, from the captured cells, using the
        /// CLR types the engine actually reported. Optionally rewrites one Verdict cell - that is
        /// the mutation.
        /// </summary>
        private static DataTable ToDataTable(List<List<CapturedCell>> rows, int mutateRow = -1, string? mutateVerdictTo = null)
        {
            var dt = new DataTable();
            foreach (var cell in rows[0])
                dt.Columns.Add(cell.Name, Type.GetType(cell.ClrType) ?? typeof(string));

            for (var i = 0; i < rows.Count; i++)
            {
                var row = dt.NewRow();
                foreach (var cell in rows[i])
                {
                    if (cell.IsNull) { row[cell.Name] = DBNull.Value; continue; }
                    var text = cell.Value!;
                    if (i == mutateRow && cell.Name == "Verdict" && mutateVerdictTo is not null)
                        text = mutateVerdictTo;
                    row[cell.Name] = Convert.ChangeType(
                        text, dt.Columns[cell.Name]!.DataType,
                        System.Globalization.CultureInfo.InvariantCulture);
                }
                dt.Rows.Add(row);
            }
            return dt;
        }

        private static List<DataGridColumnColorRule> ShippedColourRules()
            => FindPanel("dashboard-config.json", PanelId).GetProperty("dataGridColumnColors")
                .EnumerateArray()
                .Select(r => new DataGridColumnColorRule
                {
                    Column = r.GetProperty("column").GetString()!,
                    Mode = r.GetProperty("mode").GetString()!,
                    MatchValue = r.GetProperty("matchValue").GetString(),
                    MatchColor = r.GetProperty("matchColor").GetString(),
                })
                .ToList();

        /// <summary>Renders the REAL DataGrid component through the real Blazor HtmlRenderer, the
        /// same instrument DataGridErrorBodyRenderTests uses. DataGrid takes no injected services,
        /// so this is the component itself and not a stand-in for it.</summary>
        private static async Task<string> RenderAsync(DataTable data, List<DataGridColumnColorRule> rules)
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.None));
            await using var provider = services.BuildServiceProvider();
            var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

            await using var renderer = new HtmlRenderer(provider, loggerFactory);
            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Data"] = data,
                    ["ColumnColorRules"] = rules,
                });
                var output = await renderer.RenderComponentAsync<DataGrid>(parameters);
                return output.ToHtmlString();
            });
        }

        /// <summary>Pulls the inline style off the cell whose visible text is <paramref name="text"/>.</summary>
        private static string? StyleOfCellContaining(string html, string text)
        {
            foreach (Match m in Regex.Matches(html, "<td([^>]*)>(.*?)</td>", RegexOptions.Singleline))
            {
                var body = Regex.Replace(m.Groups[2].Value, "<[^>]+>", string.Empty).Trim();
                if (!string.Equals(body, text, StringComparison.Ordinal)) continue;
                var style = Regex.Match(m.Groups[1].Value, @"style\s*=\s*""([^""]*)""");
                return style.Success ? style.Groups[1].Value : string.Empty;
            }
            return null;
        }

        /// <summary>
        /// THE REGRESSION TEST FOR THE RENDERER FIX. "Medium" is the SECOND text-match rule on the
        /// Verdict column, so against the pre-fix FirstOrDefault renderer this assertion fails: the
        /// cell renders with no colour at all. The rows are the captured ones; the rule list is the
        /// shipped one, read out of dashboard-config.json rather than restated here.
        /// </summary>
        [Fact]
        public async Task The_second_colour_rule_on_the_verdict_column_still_colours_its_cells()
        {
            var (rows, meta) = LoadFixture();
            _out.WriteLine("fixture captured " + meta.GetProperty("capturedUtc").GetString()
                           + " from " + meta.GetProperty("target").GetString()
                           + " (" + meta.GetProperty("serverVersion").GetString() + ")");
            _out.WriteLine("capture substitution: " + meta.GetProperty("captureSubstitution").GetString());

            var verdicts = rows.Select(r => r.Single(c => c.Name == "Verdict").Value).ToList();
            verdicts.Should().Contain("Medium",
                "the captured result set must carry a Medium row or this test proves nothing; "
                + "re-capture against an instance that has one");

            var html = await RenderAsync(ToDataTable(rows), ShippedColourRules());

            StyleOfCellContaining(html, "Medium").Should().NotBeNull("the Medium cell must render");
            StyleOfCellContaining(html, "Medium").Should().Contain("#ff9800",
                "Medium is the SECOND rule on the Verdict column. Before the DataGrid.GetCellColor "
                + "fix, FirstOrDefault took the High rule, found no match, and returned null - so "
                + "every Medium verdict rendered plain while High rendered red.");

            StyleOfCellContaining(html, "Low").Should().NotBeNull();
            StyleOfCellContaining(html, "Low").Should().NotContain("#",
                "Low has no rule and must stay unstyled");
        }

        /// <summary>
        /// THE MUTATION. One Verdict cell in the captured rows is rewritten from its genuine value
        /// to "High"; the same render must turn that cell red. Assert on the rendered colour rather
        /// than on the string, because the string is what was mutated.
        /// </summary>
        [Fact]
        public async Task Changing_one_verdict_cell_to_High_turns_that_cell_red()
        {
            var (rows, _) = LoadFixture();
            var target = rows.FindIndex(r => r.Single(c => c.Name == "Verdict").Value == "Medium");
            target.Should().BeGreaterThanOrEqualTo(0);

            var waitType = rows[target].Single(c => c.Name == "Wait Type").Value!;

            var before = await RenderAsync(ToDataTable(rows), ShippedColourRules());
            var after = await RenderAsync(
                ToDataTable(rows, mutateRow: target, mutateVerdictTo: "High"), ShippedColourRules());

            before.Should().NotBe(after, "the mutation must be visible in the markup");

            StyleOfCellContaining(after, "High").Should().Contain("#f44336",
                "row {0} ({1}) was mutated to High and must render red", target, waitType);
            _out.WriteLine("mutated row " + target + " (" + waitType + ") Medium -> High; "
                           + "style became " + StyleOfCellContaining(after, "High"));
        }

        /// <summary>
        /// The captured column names must be exactly the ones the config declares. This is the
        /// offline half of the live smoke's third claim: a renamed projection alias silently kills
        /// both dataGridColumns and the colour rule, and neither fails loudly.
        /// </summary>
        [Fact]
        public void The_captured_columns_are_exactly_the_columns_the_config_names()
        {
            var (rows, _) = LoadFixture();
            rows.Should().NotBeEmpty();
            rows[0].Select(c => c.Name).Should().Equal(ExpectedColumns);
        }

        // ══ Tier 3: live, on a real instance. Inert unless armed. ════════════════════════════

        private static SqlConnection Open(string target)
        {
            var conn = new SqlConnection(
                $"Server={target};Integrated Security=true;TrustServerCertificate=true;Connect Timeout=15");
            conn.Open();
            return conn;
        }

        /// <summary>
        /// THE LIVE SMOKE. Runs the SHIPPED panel SQL, unmodified, and proves three things:
        /// it executes; the column names it returns are exactly the ones dataGridColumns and
        /// dataGridColumnColors name; and every row it returns respects the 10% floor.
        ///
        /// <para>The floor assertion is vacuous on an idle instance, and this test SAYS SO rather
        /// than passing quietly: it reports the row count, and it separately runs the same query
        /// with the floor lowered so the categorisation and the verdict vocabulary are exercised
        /// with real rows even when the shipped floor admits none.</para>
        /// </summary>
        [LiveFact(TargetVar)]
        public void The_shipped_query_runs_and_returns_the_columns_the_config_names()
        {
            var target = Require(TargetVar);
            var sql = PanelSql();

            using var conn = Open(target);
            _out.WriteLine("live target " + target + " ("
                           + conn.ServerVersion + "), shipped SQL, unmodified");

            // 1 + 2: it runs, and the schema is what the config assumes. The schema comes back even
            // for a zero-row result, so this half is never vacuous.
            using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 })
            using (var reader = cmd.ExecuteReader())
            {
                var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
                names.Should().Equal(ExpectedColumns,
                    "dataGridColumns names these exactly, and the Verdict colour rule keys off the "
                    + "last of them; rename any alias and both stop working silently");

                var rowCount = 0;
                while (reader.Read())
                {
                    rowCount++;
                    // 3: the floor.
                    var pct = reader.GetDecimal(reader.GetOrdinal("% of uptime"));
                    pct.Should().BeGreaterThanOrEqualTo(10.0m,
                        "the shipped WHERE is 'w.pct >= 10.0' (sp_PerfCheck.sql:63, :2354); a row "
                        + "below it means the floor was edited out");

                    var verdict = reader.GetString(reader.GetOrdinal("Verdict"));
                    verdict.Should().BeOneOf("High", "Medium", "Low");
                }

                _out.WriteLine("shipped query returned " + rowCount + " row(s)");
                if (rowCount == 0)
                    _out.WriteLine("NOTE: the floor assertion above was VACUOUS on this instance - "
                                   + "no wait type reaches 10% of its uptime. The exercise with real "
                                   + "rows is the floor-lowered run below.");
            }

            // The non-vacuous half: same query, floor lowered, so the allow-list, the categories and
            // the verdict vocabulary are all exercised against real engine output.
            var lowered = sql.Replace("w.pct >= 10.0", "w.pct >= 0");
            lowered.Should().NotBe(sql, "the floor literal must be exactly where the census says");

            var categories = new List<string>();
            using (var cmd = new SqlCommand(lowered, conn) { CommandTimeout = 60 })
            using (var reader = cmd.ExecuteReader())
            {
                Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)
                    .Should().Equal(ExpectedColumns);
                while (reader.Read())
                {
                    categories.Add(reader.GetString(0));
                    reader.GetString(reader.GetOrdinal("Verdict")).Should().BeOneOf("High", "Medium", "Low");
                }
            }

            categories.Should().NotBeEmpty(
                "with the floor removed a real instance must produce SOME allow-listed wait");
            categories.Should().NotContain("Other",
                "every allow-listed wait type has a category in the 14-category taxonomy; an 'Other' "
                + "row means the allow-list and the CASE have drifted apart");
            _out.WriteLine("floor-lowered run: " + categories.Count + " row(s), categories "
                           + string.Join(", ", categories.Distinct().OrderBy(x => x)));
        }

        /// <summary>
        /// Writes Fixtures/captured-wait-verdict-rows.json from a real execution. Armed separately
        /// from the smoke because it WRITES into the repo. The one substitution it makes is recorded
        /// in the file it writes, so a later reader does not have to take this comment's word.
        /// </summary>
        [LiveFact(TargetVar, CaptureVar)]
        public void Capture_the_rendered_rows_from_a_real_instance()
        {
            var target = Require(TargetVar);
            var outPath = Require(CaptureVar);

            var shipped = PanelSql();
            const string floor = "w.pct >= 10.0";
            shipped.Should().Contain(floor);
            var captureSql = shipped.Replace(floor, "w.pct >= 0");

            using var conn = Open(target);
            string startTime;
            using (var cmd = new SqlCommand(
                "SELECT CONVERT(varchar(30), sqlserver_start_time, 126) FROM sys.dm_os_sys_info", conn))
                startTime = (string)cmd.ExecuteScalar();

            var rows = new List<object>();
            using (var cmd = new SqlCommand(captureSql, conn) { CommandTimeout = 60 })
            using (var reader = cmd.ExecuteReader())
            {
                var schema = reader.GetColumnSchema();
                while (reader.Read())
                {
                    var cells = new List<object>();
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        var isNull = reader.IsDBNull(i);
                        cells.Add(new
                        {
                            name = reader.GetName(i),
                            sqlType = reader.GetDataTypeName(i),
                            clrType = reader.GetFieldType(i).FullName,
                            isNull,
                            value = isNull ? null : Convert.ToString(
                                reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture),
                        });
                    }
                    rows.Add(cells);
                }
            }

            var payload = new
            {
                capturedUtc = DateTime.UtcNow.ToString("o"),
                target,
                serverVersion = conn.ServerVersion,
                sqlServerStartTime = startTime,
                panelId = PanelId,
                capturedBy = nameof(WaitVerdictPanelTests) + "." + nameof(Capture_the_rendered_rows_from_a_real_instance),
                captureSubstitution =
                    "The SHIPPED panel SQL with exactly one literal replaced: 'w.pct >= 10.0' -> "
                    + "'w.pct >= 0'. The capture instance is idle and has been up for days, so no "
                    + "wait type reaches 10% of its uptime and the shipped query returns zero rows "
                    + "there. TOP (10), ORDER BY, the allow-list, the category CASE and the verdict "
                    + "CASE are all the shipped text byte for byte. The shipped floor is exercised "
                    + "against the unmodified query by the live smoke in the same file.",
                sqlExecuted = captureSql,
                rows,
            };

            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllText(outPath,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }) + "\n");

            rows.Should().NotBeEmpty("a fixture with no rows proves nothing downstream");
            _out.WriteLine("captured " + rows.Count + " row(s) to " + outPath);
        }
    }
}
