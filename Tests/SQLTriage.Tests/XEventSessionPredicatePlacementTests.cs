/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

// ── XEvent deploy buttons must emit T-SQL SQL Server will actually parse ─────────
//
// WHAT BROKE. XEventService.CreateSessionAsync built the predicate as a standalone
// "WHERE {predicate}" line and dropped it between ADD EVENT and ADD TARGET. T-SQL wants the
// predicate inside the event's own parentheses. So two of the three deploy buttons on the Alerts
// page (Comprehensive and Error, both passing "severity >= 17") failed at parse time on every
// click and created nothing. The Deadlock button passes no predicate, took the empty branch,
// and worked — which is exactly why this survived: the one button anybody tried was the one
// button that was fine.
//
// PROVED LIVE 2026-08-25 against MSI\NEW2022, both directions, same instance, same session names:
//   old shape  -> Msg 156 "Incorrect syntax near the keyword 'WHERE'", plus Msg 102 and Msg 319;
//                 sys.server_event_sessions returned 0 rows for ComprehensiveMonitor/ErrorMonitor.
//   new shape  -> both sessions created, both ALTER ... STATE = START succeeded,
//                 sys.dm_xe_sessions showed both RUNNING. Dropped afterwards; .xel files removed.
//
// WHY THESE TESTS EXIST RATHER THAN A LIVE TEST. A test that can only see this SQL through an
// open SqlConnection is a test CI never runs, and CI never running it is the condition under
// which the bare WHERE shipped. BuildCreateSessionSql was extracted so the emitted text is
// assertable with no server at all.
//
// NOTE ON THIS FILE'S OWN HONESTY. The structural check below is applied to the HISTORICAL broken
// string as a control. If the check ever stops being able to detect the defect it was written for,
// that control goes red instead of the real assertions going quietly green.
public class XEventSessionPredicatePlacementTests
{
    /// <summary>
    /// The exact statement shape shipped before 2026-08-25, with the interpolations filled in the
    /// way DeployComprehensiveSession filled them. Kept verbatim so the guard below is measured
    /// against the real defect and not against a paraphrase of it.
    /// </summary>
    private const string HistoricalBrokenSql = @"
                CREATE EVENT SESSION [ComprehensiveMonitor] ON SERVER
                ADD EVENT sqlserver.error_reported
                WHERE severity >= 17
                ADD TARGET package0.event_file(SET filename = 'ComprehensiveMonitor.xel', max_file_size = 10, max_rollover_files = 5)
                WITH (MAX_DISPATCH_LATENCY = 1 SECONDS, STARTUP_STATE = OFF)";

    /// <summary>
    /// True when a WHERE sits between ADD EVENT and ADD TARGET at paren depth zero, i.e. outside
    /// the event's own parentheses. That is the shape SQL Server rejects with Msg 156, and the
    /// only shape this suite cares about: a WHERE inside the parentheses is the correct one.
    /// </summary>
    internal static bool HasBareWhereBetweenEventAndTarget(string sql)
    {
        var eventAt = sql.IndexOf("ADD EVENT", StringComparison.OrdinalIgnoreCase);
        var targetAt = sql.IndexOf("ADD TARGET", StringComparison.OrdinalIgnoreCase);
        if (eventAt < 0 || targetAt < 0 || targetAt <= eventAt)
        {
            throw new InvalidOperationException(
                "The statement has no ADD EVENT ... ADD TARGET pair, so this guard cannot judge it "
                + "and must not report clean. Statement was:\n" + sql);
        }

        var span = sql.Substring(eventAt, targetAt - eventAt);

        // Strip everything nested inside parentheses; a WHERE surviving that is a bare WHERE.
        var depth = 0;
        var outside = new System.Text.StringBuilder();
        foreach (var ch in span)
        {
            if (ch == '(') { depth++; continue; }
            if (ch == ')') { depth = Math.Max(0, depth - 1); continue; }
            if (depth == 0) outside.Append(ch);
        }

        return Regex.IsMatch(outside.ToString(), @"\bWHERE\b", RegexOptions.IgnoreCase);
    }

    // ── The guards ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ComprehensiveMonitor", "error_reported", "severity >= 17")]
    [InlineData("ErrorMonitor", "error_reported", "severity >= 17")]
    public void A_predicate_is_emitted_inside_the_events_own_parentheses(
        string sessionName, string eventName, string predicate)
    {
        var sql = XEventService.BuildCreateSessionSql(sessionName, eventName, predicate);

        sql.Should().Contain($"ADD EVENT sqlserver.{eventName}(WHERE {predicate})",
            "SQL Server parses the predicate only when it is inside the event's parentheses. "
            + "Proved live 2026-08-25 on MSI\\NEW2022: this shape created and started the session.");

        HasBareWhereBetweenEventAndTarget(sql).Should().BeFalse(
            "a WHERE outside the event parentheses is the exact defect that made the Comprehensive "
            + "and Error deploy buttons fail on every click with Msg 156.");
    }

    [Fact]
    public void No_predicate_still_emits_a_plain_event_clause()
    {
        // The Deadlock button's shape. It worked before the fix and must keep working: no
        // parentheses, no WHERE, nothing added.
        var sql = XEventService.BuildCreateSessionSql("DeadlockMonitor", "xml_deadlock_report");

        sql.Should().Contain("ADD EVENT sqlserver.xml_deadlock_report",
            "the predicate-free form is the shape proved working before and after the fix.");
        sql.Should().NotContain("xml_deadlock_report(",
            "the predicate-free form must not grow parentheses it does not need.");
        sql.Should().NotContain("WHERE");
        HasBareWhereBetweenEventAndTarget(sql).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_predicate_is_treated_as_no_predicate(string predicate)
    {
        // A whitespace-only predicate would emit "(WHERE    )", which is its own parse error.
        var sql = XEventService.BuildCreateSessionSql("Probe", "error_reported", predicate);

        sql.Should().NotContain("WHERE");
        sql.Should().NotContain("()");
    }

    [Fact]
    public void The_target_and_session_options_are_unchanged_by_the_fix()
    {
        var sql = XEventService.BuildCreateSessionSql("Probe", "error_reported", "severity >= 17");

        sql.Should().Contain("CREATE EVENT SESSION [Probe] ON SERVER");
        sql.Should().Contain("ADD TARGET package0.event_file(SET filename = 'Probe.xel', max_file_size = 10, max_rollover_files = 5)");
        sql.Should().Contain("WITH (MAX_DISPATCH_LATENCY = 1 SECONDS, STARTUP_STATE = OFF)");
    }

    // ── The shipped call path must go through the builder ───────────────────────
    //
    // Everything above asserts BuildCreateSessionSql. Nothing above ties CreateSessionAsync — the
    // method the three deploy buttons actually call — to it. MEASURED 2026-08-25 by mutation: leave
    // the builder correct, rebuild the pre-fix bare-WHERE statement inline inside CreateSessionAsync,
    // and the full Debug suite stays green at 0 failures while both buttons emit the exact text
    // proved to return Msg 156. The extraction is only worth what this pin is worth.
    //
    // It reads SOURCE rather than IL because the fact being pinned is a source fact: this SQL has
    // one and only one home.

    /// <summary>
    /// What CreateSessionAsync's body does about its statement. Throws rather than reporting clean
    /// when it cannot find the method: an absence must never read as a pass.
    /// </summary>
    internal static (bool BuildsViaHelper, bool ContainsRawStatement) InspectCreateSessionBody(string source)
    {
        const string signature = "public async Task<string> CreateSessionAsync(";
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException(
                "CreateSessionAsync was not found by its signature, so this guard cannot judge the "
                + "call path and must not report clean. Rename the signature here if it moved.");
        }

        // The body runs to the next member's doc comment, or to the end of the file.
        var end = source.IndexOf("\n    /// <summary>", start, StringComparison.Ordinal);
        var body = end < 0 ? source.Substring(start) : source.Substring(start, end - start);

        return (body.Contains("BuildCreateSessionSql(", StringComparison.Ordinal),
                body.Contains("CREATE EVENT SESSION", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_deploy_call_path_builds_its_statement_only_through_the_builder()
    {
        var source = ReadXEventServiceSource();

        var (buildsViaHelper, containsRawStatement) = InspectCreateSessionBody(source);

        buildsViaHelper.Should().BeTrue(
            "the deploy buttons call CreateSessionAsync, not BuildCreateSessionSql. If the body stops "
            + "calling the builder, every assertion in this file measures dead code.");
        containsRawStatement.Should().BeFalse(
            "a CREATE EVENT SESSION literal inside CreateSessionAsync is a second, untested copy of "
            + "the statement — which is how the bare WHERE survived in the first place.");
    }

    [Fact]
    public void The_create_statement_has_exactly_one_home_in_the_service()
    {
        var source = ReadXEventServiceSource();

        // Comment lines are excluded on purpose: BuildCreateSessionSql's own doc comment names the
        // statement, and prose about the statement is not a second emitter of it.
        var codeOnly = string.Join("\n", source
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        var occurrences = Regex.Matches(codeOnly, "CREATE EVENT SESSION", RegexOptions.IgnoreCase).Count;
        occurrences.Should().Be(1,
            "one statement, one place it is written. Two copies drift, and only one of them is under "
            + "test. The single copy lives in BuildCreateSessionSql.");
    }

    /// <summary>Reads the service source, failing loudly if the path moved.</summary>
    private static string ReadXEventServiceSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SQLTriage.sln")))
            dir = dir.Parent;
        dir.Should().NotBeNull("this guard reads app source, so it needs the repo root.");

        var path = Path.Combine(dir!.FullName, "Data", "Services", "XEventService.cs");
        File.Exists(path).Should().BeTrue(
            $"XEventService.cs is the file under guard and it was not at {path}.");
        return File.ReadAllText(path);
    }

    // ── Control: the guard must still be able to see the defect ─────────────────

    [Fact]
    public void Guard_control_flags_the_historical_broken_statement()
    {
        // If this goes green, HasBareWhereBetweenEventAndTarget has stopped detecting the very
        // shape it was written for, and every assertion above it is worthless.
        HasBareWhereBetweenEventAndTarget(HistoricalBrokenSql).Should().BeTrue(
            "this is the verbatim statement that returned Msg 156 against MSI\\NEW2022 on 2026-08-25. "
            + "A guard that cannot flag it cannot protect anything.");
    }

    [Fact]
    public void Guard_control_refuses_to_judge_a_statement_it_cannot_parse()
    {
        // Silence on an unrecognised statement would read as "clean". It must throw instead.
        var act = () => HasBareWhereBetweenEventAndTarget("SELECT 1");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Call_path_control_flags_a_body_that_rebuilds_the_statement_inline()
    {
        // The exact mutation measured on 2026-08-25: builder left correct, CreateSessionAsync
        // reverted to inline SQL. The full suite stayed green. This control is what turns it red.
        const string RevertedSource = @"
    public async Task<string> CreateSessionAsync(string connectionString, string sessionName, string eventName, string predicate = """")
    {
        var whereClause = string.IsNullOrEmpty(predicate) ? """" : $""WHERE {predicate}"";
        var sql = $@""
                CREATE EVENT SESSION [{sessionName}] ON SERVER
                ADD EVENT sqlserver.{eventName}
                {whereClause}
                ADD TARGET package0.event_file(SET filename = '{sessionName}.xel')"";
        return sql;
    }
";

        var (buildsViaHelper, containsRawStatement) = InspectCreateSessionBody(RevertedSource);

        buildsViaHelper.Should().BeFalse();
        containsRawStatement.Should().BeTrue(
            "if this control goes green, the call-path guard has stopped detecting the revert it was "
            + "written for, and the guard above it is worthless.");
    }

    [Fact]
    public void Call_path_control_refuses_to_judge_a_source_without_the_method()
    {
        // A missing method must throw, not report a clean call path.
        var act = () => InspectCreateSessionBody("public class Nothing { }");
        act.Should().Throw<InvalidOperationException>();
    }
}
