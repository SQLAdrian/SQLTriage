/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The strings lane's cluster 3: six dashboard panels whose description told the operator one
    /// thing while their query did another. Nothing here was broken in the sense of throwing; every
    /// one of them rendered a confident answer to a question the operator had not asked.
    ///
    /// <para>WHAT WAS MEASURED, 2026-08-28, live on <c>.\NEW2022</c> (both instances running):</para>
    /// <list type="bullet">
    /// <item><c>queryperf.top_queries</c> called itself "Real-time" and "(Live)" over
    /// <c>sys.dm_exec_query_stats</c>, which is cumulative from the moment a plan entered cache. The
    /// dashboards lane had already corrected the sort key; the over-claim in the prose survived it.</item>
    /// <item><c>longqueries.querytext</c> promised execution plan XML it never selected, and stamped
    /// every row with <c>getdate()</c>. PROVED: the shipped projection over a row whose event_time
    /// is 2020-01-02 reported 2026-08-28 - a six-year error presented as the query's own timestamp,
    /// while <c>event_time</c> sat unread on the same view (confirmed in sys.columns). It also
    /// carried <c>defaultDatabase: "master"</c> while its view lives in SQLWATCH, so it did not
    /// render at all: <c>Msg 208, Invalid object name
    /// 'dbo.vw_sqlwatch_report_fact_xes_long_queries'</c>.</item>
    /// <item><c>waits.details</c> promised "percentage of total waits". PROVED by running the
    /// shipped query: it returns Time, Category, Wait Type, Wait Time (ms), Waiting Tasks,
    /// sql_instance. There is no percentage, and no division anywhere in it.</item>
    /// <item><c>queryperf.query_summary</c> shipped an internal bug report as its client-facing
    /// text: "BROKEN - StatCard mapper expects a Value column ... Disable until SQL or panel binding
    /// fixed." StatCard.razor renders description as a hover tooltip, and PanelEditorModal shows it
    /// verbatim in an editable textarea, so that note was addressed to us and delivered to them.</item>
    /// <item><c>live.sessions</c> described "Count of active user sessions from sys.dm_exec_sessions
    /// where is_user_process = 1" over a grid of <c>sys.sysprocesses</c> rows. PROVED: 10 of the 11
    /// rows it returned were sleeping, so "active" was false for 91% of what it showed; and its
    /// blank-name filters are genuinely exclusionary - <c>CAST('    ' AS NCHAR(128)) &lt;&gt; N''</c>
    /// evaluates to DROPPED under ANSI padding, discarding 13 of 47 sysprocesses rows on an idle
    /// box.</item>
    /// <item><c>sessions.top</c> promised "Top active sessions ... Shows session_id, login". PROVED
    /// by running it: fourteen columns come back and neither is among them, the oldest row's
    /// last_execution_time was 1900-01-01, and 1,495 cached plans were listed against 1 request
    /// actually executing.</item>
    /// </list>
    ///
    /// <para>THE FLOOR IS HONESTY, NOT NEW FEATURES. Five of the six are corrected by making the
    /// prose describe the measurement that is actually taken. Only <c>longqueries.querytext</c>
    /// changed behaviour, and only where the old behaviour was a fabricated reading: a render-clock
    /// timestamp presented as the row's own, and a database that made the panel unrenderable. This
    /// suite therefore pins the PROSE against the QUERY rather than pinning either alone - a
    /// description asserting a measurement the query does not take is the defect class, and it can
    /// come back from either side.</para>
    /// </summary>
    public class DashboardPanelProseHonestyTests
    {
        private static string RepoRoot() => RawPassedScan.RepoRoot().FullName;

        private static string ShippedJson() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "Config", "dashboard-config.json"));

        /// <summary>
        /// Every panel in the shipped config, keyed dashboardId/panelId, walked exactly the way
        /// DashboardConfigMigrator walks it (flat panels plus every tab's panels).
        /// </summary>
        private static IReadOnlyDictionary<string, JsonObject> ShippedPanels()
        {
            var map = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            var root = JsonNode.Parse(ShippedJson())!.AsObject();

            foreach (var dashboardNode in root["dashboards"]!.AsArray())
            {
                var dashboard = dashboardNode!.AsObject();
                var dashboardId = dashboard["id"]?.GetValue<string>() ?? "";

                var panels = new List<JsonObject>();
                if (dashboard["panels"] is JsonArray flat)
                    panels.AddRange(flat.Select(p => p!.AsObject()));
                if (dashboard["tabs"] is JsonArray tabs)
                    foreach (var tab in tabs)
                        if (tab!.AsObject()["panels"] is JsonArray tabPanels)
                            panels.AddRange(tabPanels.Select(p => p!.AsObject()));

                foreach (var panel in panels)
                {
                    var id = panel["id"]?.GetValue<string>();
                    if (id is not null) map[dashboardId + "/" + id] = panel;
                }
            }

            return map;
        }

        private static JsonObject Panel(string key)
        {
            Assert.True(ShippedPanels().TryGetValue(key, out var panel),
                key + " is no longer in Config/dashboard-config.json. If it was deleted deliberately, "
                    + "delete its rows here in the same commit and say why.");
            return panel!;
        }

        private static string Description(string key) => Panel(key)["description"]?.GetValue<string>() ?? "";
        private static string Title(string key) => Panel(key)["title"]?.GetValue<string>() ?? "";
        private static string Sql(string key) => Panel(key)["query"]?["sqlServer"]?.GetValue<string>() ?? "";

        public static TheoryData<string> AllSix() => new()
        {
            "correlation/queryperf.top_queries",
            "correlation/queryperf.query_summary",
            "longqueries/longqueries.querytext",
            "livewaits/waits.details",
            "live/live.sessions",
            "sessions/sessions.top",
        };

        // ── 1. queryperf.top_queries: a lifetime average is not a real-time reading ─────────

        [Fact]
        public void Top_queries_does_not_call_a_cumulative_dmv_reading_real_time()
        {
            const string key = "correlation/queryperf.top_queries";

            Assert.Contains("dm_exec_query_stats", Sql(key), StringComparison.OrdinalIgnoreCase);

            foreach (var claim in new[] { "real-time", "realtime", "(live)" })
            {
                Assert.DoesNotContain(claim, Description(key), StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(claim, Title(key), StringComparison.OrdinalIgnoreCase);
            }

            // The correction is not merely a deletion: the prose has to say what the number IS, or
            // the operator is left to assume the same wrong thing the removed word told them.
            Assert.Contains("cache", Description(key), StringComparison.OrdinalIgnoreCase);
        }

        // ── 2. longqueries.querytext: no fabricated timestamps, no unpromised plans ─────────

        [Fact]
        public void Query_text_panel_reports_the_time_the_query_ran_not_the_time_it_was_rendered()
        {
            const string key = "longqueries/longqueries.querytext";
            var sql = Sql(key);

            Assert.DoesNotContain("getdate()", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("event_time", sql, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Query_text_panel_reaches_the_database_its_view_lives_in()
        {
            const string key = "longqueries/longqueries.querytext";

            // Its sibling on the same tab reads the same SQLWATCH view and has always been right.
            // Pinning against the sibling rather than against the literal "SQLWATCH" keeps the two
            // panels from drifting apart again if the deployment database is ever renamed.
            var sibling = Panel("longqueries/longqueries.querydetails")["defaultDatabase"]?.GetValue<string>();
            var mine = Panel(key)["defaultDatabase"]?.GetValue<string>();

            Assert.Equal(sibling, mine);
            Assert.Contains("vw_sqlwatch_report_fact_xes_long_queries", Sql(key), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Query_text_panel_does_not_promise_execution_plans_it_never_selects()
        {
            const string key = "longqueries/longqueries.querytext";
            var sql = Sql(key);
            var description = Description(key);

            var promisesPlans = description.Contains("execution plan", StringComparison.OrdinalIgnoreCase)
                                && !description.Contains("not shown", StringComparison.OrdinalIgnoreCase)
                                && !description.Contains("are not", StringComparison.OrdinalIgnoreCase);

            var deliversPlans = sql.Contains("query_plan", StringComparison.OrdinalIgnoreCase);

            Assert.False(promisesPlans && !deliversPlans,
                "longqueries.querytext promises execution plans and its query selects no plan column. "
                + "Either select one, or say plainly that plans are not shown here.");
        }

        // ── 3. waits.details: a percentage claim needs an actual percentage ────────────────

        [Fact]
        public void Wait_details_does_not_promise_a_percentage_it_never_computes()
        {
            const string key = "livewaits/waits.details";
            var sql = Sql(key);
            var description = Description(key);

            var computesAShare = sql.Contains('%') || sql.Contains('/') || sql.Contains("OVER (", StringComparison.OrdinalIgnoreCase);

            // "not each wait type's share of total wait time" is a DENIAL, not a promise, so the
            // word may appear as long as it is being ruled out.
            var promisesAShare =
                (description.Contains("percentage", StringComparison.OrdinalIgnoreCase)
                 || description.Contains("% of", StringComparison.OrdinalIgnoreCase))
                && !description.Contains("not ", StringComparison.OrdinalIgnoreCase);

            Assert.False(promisesAShare && !computesAShare,
                "waits.details promises a percentage of total waits and its query computes no share: "
                + "no percent sign, no division, no window function. Compute it, or stop offering it.");
        }

        // ── 4. queryperf.query_summary: no internal bug reports in client-facing text ──────

        [Theory]
        [MemberData(nameof(AllSix))]
        public void No_panel_ships_an_internal_bug_report_as_its_client_facing_description(string key)
        {
            var description = Description(key);

            // StatCard.razor renders description as a hover tooltip and PanelEditorModal shows it
            // verbatim in an editable textarea. Anything written here is written to the client.
            foreach (var leak in new[]
                     {
                         "BROKEN", "Disable until", "StatCard mapper", "panel binding",
                         "TODO", "FIXME", "HACK", "XXX", "workaround until", "needs fixing",
                     })
            {
                Assert.False(description.Contains(leak, StringComparison.OrdinalIgnoreCase),
                    key + " ships an engineering note to the client in its description: it contains \""
                        + leak + "\". Say what the operator sees and why, in their language.");
            }
        }

        [Fact]
        public void Query_summary_states_that_it_is_off_and_why_rather_than_how_to_fix_it()
        {
            const string key = "correlation/queryperf.query_summary";
            var panel = Panel(key);
            var description = Description(key);

            // The honest state is unchanged: still off. This fix corrects the wording, and must not
            // quietly switch a panel on under cover of a prose commit.
            Assert.False(panel["enabled"]!.GetValue<bool>(),
                "queryperf.query_summary was turned on by a prose fix. Its query still returns no "
                + "Value column, so the card would render empty. Enabling it is a separate decision "
                + "with its own evidence.");

            // "Value column" alone is NOT enough to pin this: the old bug-report text contained
            // that phrase too ("StatCard mapper expects a Value column"), so an assertion on it
            // passed against the very text this fix removed - found by mutation, 2026-08-28. What
            // separates the two is who the sentence is addressed to. The new text has to tell the
            // OPERATOR the panel's state; the old text told an ENGINEER what to go and repair.
            Assert.Contains("Value column", description, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                description.Contains("Turned off", StringComparison.OrdinalIgnoreCase)
                || description.Contains("is off", StringComparison.OrdinalIgnoreCase),
                "queryperf.query_summary's description does not tell the operator the panel is off. "
                + "A tile that renders nothing has to say so, in its own words, not leave them to "
                + "infer it from an engineering note.");

            // The description's central claim has to stay true of the query it describes.
            Assert.DoesNotContain(" AS [Value]", Sql(key), StringComparison.OrdinalIgnoreCase);
        }

        // ── 5. live.sessions: describe the view you query and the rows you drop ───────────

        [Fact]
        public void Live_sessions_names_the_view_it_actually_queries()
        {
            const string key = "live/live.sessions";
            var sql = Sql(key);
            var description = Description(key);

            Assert.Contains("sysprocesses", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dm_exec_sessions", sql, StringComparison.OrdinalIgnoreCase);

            Assert.False(description.Contains("dm_exec_sessions", StringComparison.OrdinalIgnoreCase),
                "live.sessions still tells the operator it reads sys.dm_exec_sessions. It reads "
                + "sys.sysprocesses, which is a different set of rows under different filters.");

            Assert.False(description.Contains("is_user_process", StringComparison.OrdinalIgnoreCase),
                "live.sessions still cites the is_user_process predicate. Its query uses spid > 50, "
                + "which is folklore for the same idea and is not the same predicate.");
        }

        [Fact]
        public void Live_sessions_discloses_every_filter_that_silently_drops_rows()
        {
            const string key = "live/live.sessions";
            var sql = Sql(key);
            var description = Description(key);

            // Each exclusion in the query has to be visible in the prose, because each one makes the
            // list smaller than the operator's mental model of "the sessions on this server". The
            // blank-name pair is not theoretical: under ANSI padding a blank NCHAR compares equal to
            // N'', so those rows are dropped, and 13 of 47 were dropped on an idle instance.
            if (sql.Contains("spid > 50", StringComparison.OrdinalIgnoreCase))
                Assert.Contains("50", description, StringComparison.OrdinalIgnoreCase);

            if (sql.Contains("hostname <> ''", StringComparison.OrdinalIgnoreCase))
                Assert.Contains("host name", description, StringComparison.OrdinalIgnoreCase);

            if (sql.Contains("program_name <> ''", StringComparison.OrdinalIgnoreCase))
                Assert.Contains("program name", description, StringComparison.OrdinalIgnoreCase);

            if (sql.Contains("SQLMonitorUI", StringComparison.OrdinalIgnoreCase))
                Assert.Contains("SQLMonitorUI", description, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Live_sessions_does_not_call_a_mostly_sleeping_list_active_or_a_grid_a_count()
        {
            const string key = "live/live.sessions";

            Assert.Equal("DataGrid", Panel(key)["panelType"]?.GetValue<string>());

            Assert.False(Description(key).StartsWith("Count of", StringComparison.OrdinalIgnoreCase),
                "live.sessions is a DataGrid of session rows, not a count.");

            // 10 of the 11 rows it returned on an idle instance were sleeping. A panel titled
            // "Active Sessions" over that list is wrong most of the time it is looked at.
            Assert.DoesNotContain("Active Session", Title(key), StringComparison.OrdinalIgnoreCase);
        }

        // ── 6. sessions.top: a plan-cache aggregate is not a live session ─────────────────

        [Fact]
        public void Sessions_top_does_not_claim_columns_its_query_cannot_return()
        {
            const string key = "sessions/sessions.top";
            var sql = Sql(key);
            var description = Description(key);

            // Neither column exists in dm_exec_query_stats or dm_exec_procedure_stats, so no branch
            // of this UNION can produce them. Proved by running the query: fourteen columns, and
            // neither of these is among them.
            Assert.DoesNotContain("session_id", sql, StringComparison.OrdinalIgnoreCase);

            var claimsThem =
                description.Contains("session_id", StringComparison.OrdinalIgnoreCase)
                && !description.Contains("not available", StringComparison.OrdinalIgnoreCase);

            Assert.False(claimsThem,
                "sessions.top still tells the operator it shows session_id. Its query is a plan-cache "
                + "aggregate and cannot produce one.");
        }

        [Fact]
        public void Sessions_top_does_not_present_plan_cache_totals_as_current_activity()
        {
            const string key = "sessions/sessions.top";
            var description = Description(key);

            Assert.Contains("dm_exec_query_stats", Sql(key), StringComparison.OrdinalIgnoreCase);

            // "not a session running now" is a denial and must be allowed through; the bare claim
            // is not. On a real instance 1,495 cached plans were listed against 1 executing request,
            // and the oldest row's last_execution_time was 1900-01-01.
            var claimsLive =
                description.Contains("active session", StringComparison.OrdinalIgnoreCase)
                && !description.Contains("not a session", StringComparison.OrdinalIgnoreCase);

            Assert.False(claimsLive,
                "sessions.top still calls plan-cache totals active sessions.");
        }

        // ── 7. Nothing in this cluster reintroduces a client-facing em dash ───────────────

        [Theory]
        [MemberData(nameof(AllSix))]
        public void Corrected_descriptions_carry_no_em_dash(string key)
        {
            // The repo's voice lint reads staged .cs and .razor only, so panel copy in
            // Config/dashboard-config.json has never been covered by it. These six descriptions
            // were rewritten by hand; this is the check that would otherwise not exist.
            Assert.DoesNotContain('—', Description(key));   // voice-lint:allow - the character IS the subject
            Assert.DoesNotContain('—', Title(key));         // voice-lint:allow - the character IS the subject
        }
    }
}
