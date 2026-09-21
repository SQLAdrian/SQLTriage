/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

// ── A session name may not become T-SQL (fresh-eyes F-D, ruled 2026-09-01) ──────────
//
// WHAT BROKE. T-SQL has no parameter form for an identifier, so every XEvent lifecycle
// statement interpolates the session name as text. Four of the five did it raw. XEvents.razor
// passes NewSessionName — a free-text box the operator types — so a name carrying a ']' closed
// its own brackets and everything after it was parsed as T-SQL.
//
// PROVED LIVE 2026-08-25 against MSI\NEW2022 (the triage that found it, recorded in
// XEventService.BuildCreateSessionSql's own doc comment): typing a name containing ']' made one
// CreateSessionAsync call emit a three-statement batch, run the injected SELECT, and create TWO
// event sessions.
//
// WHY THESE TESTS LOOK LIKE THIS. Same lesson as XEventSessionPredicatePlacementTests: SQL that
// can only be seen through an open SqlConnection is SQL that CI never reads, and that is the
// condition under which this shipped. The four remaining statements were extracted into builders
// so all five emitted strings are assertable with no server at all.
//
// WHAT "INERT" MEANS HERE, precisely. Escaping does not remove the hostile characters — it makes
// them part of ONE identifier. So the assertion is not "the payload is absent"; it is "every
// character of the payload sits inside a bracket-quoted identifier, and nothing the caller typed
// reaches statement level". OutsideQuoting below is the instrument that decides that, and it is
// measured against the real pre-fix strings by the controls at the bottom of this file.
public class XEventIdentifierEscapingTests
{
    /// <summary>
    /// The name the 2026-08-25 triage typed, extended with a second statement and a comment
    /// marker so a partial fix cannot pass. Every assertion in this file uses this one string.
    /// </summary>
    private const string HostileName = "x] TO SERVER; DROP TABLE t;--";

    /// <summary>
    /// What the bracket-quoted form of <see cref="HostileName"/> must look like: interior ']'
    /// doubled, wrapped once. Written out longhand rather than computed, so a broken helper
    /// cannot define its own expected answer.
    /// </summary>
    private const string HostileNameQuoted = "[x]] TO SERVER; DROP TABLE t;--]";

    // ── The instrument ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the parts of <paramref name="sql"/> that are OUTSIDE bracket-quoted identifiers
    /// and outside single-quoted string literals, with each removed region replaced by a single
    /// space. Mirrors T-SQL's own rules: inside brackets, <c>]]</c> is an escaped <c>]</c> and
    /// anything else is literal name text; inside a literal, <c>''</c> is an escaped quote.
    ///
    /// This is the whole test: text that survives this filter is text SQL Server would parse as
    /// syntax, and caller-supplied text must never survive it.
    /// </summary>
    internal static string OutsideQuoting(string sql)
    {
        if (sql is null) throw new ArgumentNullException(nameof(sql));

        var outside = new StringBuilder();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];

            if (c == '[')
            {
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == ']')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == ']') { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                outside.Append(' ');
                continue;
            }

            if (c == '\'')
            {
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'') { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                outside.Append(' ');
                continue;
            }

            outside.Append(c);
            i++;
        }

        return outside.ToString();
    }

    /// <summary>
    /// Every lifecycle statement, built with the hostile name. Named so a failure says which of
    /// the five is unescaped rather than only that one of them is.
    /// </summary>
    public static IEnumerable<object[]> AllFiveLifecycleStatements() => new[]
    {
        new object[] { "create",        XEventService.BuildCreateSessionSql(HostileName, "error_reported", "severity >= 17") },
        new object[] { "start",         XEventService.BuildStartSessionSql(HostileName) },
        new object[] { "stop",          XEventService.BuildStopSessionSql(HostileName) },
        new object[] { "drop",          XEventService.BuildDropSessionSql(HostileName) },
        new object[] { "startup_state", XEventService.BuildStartupStateSql(HostileName, true) },
    };

    // ── The guards ──────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllFiveLifecycleStatements))]
    public void A_hostile_session_name_stays_inside_one_quoted_identifier(string surface, string sql)
    {
        var outside = OutsideQuoting(sql);

        outside.Should().NotContain(";",
            $"the {surface} statement must be ONE statement. A ';' surviving outside quoting is the "
            + "operator's typed text arriving at statement level — the exact shape that created two "
            + "event sessions from one call on MSI\\NEW2022.");
        outside.Should().NotContain("--",
            $"a comment marker outside quoting means the {surface} statement's tail can be commented away.");
        outside.Should().NotContain("DROP TABLE",
            $"the injected statement must not survive quoting in the {surface} statement.");
    }

    [Theory]
    [MemberData(nameof(AllFiveLifecycleStatements))]
    public void Every_lifecycle_statement_doubles_the_closing_bracket(string surface, string sql)
    {
        sql.Should().Contain(HostileNameQuoted,
            $"the {surface} statement must carry the name QUOTENAME-style: interior ']' doubled, "
            + "wrapped once. This is the literal text, not a computed expectation.");
    }

    [Fact]
    public void The_create_statement_also_escapes_the_name_inside_the_xel_filename_literal()
    {
        // The filename is a STRING LITERAL, a different quoting context: a ']' is harmless there
        // and a "'" is not. A name that closes the literal reaches statement level just as surely.
        const string quoteName = "probe'; DROP TABLE t;--";
        var sql = XEventService.BuildCreateSessionSql(quoteName, "error_reported");

        var outside = OutsideQuoting(sql);
        outside.Should().NotContain("DROP TABLE",
            "the .xel filename literal must double the interior quote, or the name escapes the literal.");
        sql.Should().Contain("filename = 'probe''; DROP TABLE t;--.xel'",
            "the filename literal doubles the interior quote and is otherwise unchanged.");
    }

    [Fact]
    public void An_ordinary_session_name_is_emitted_unchanged()
    {
        // Escaping must be invisible to every name that never needed it — including the three
        // shipped Alerts deploy constants, which are the only names most estates ever see.
        foreach (var name in new[] { "ComprehensiveMonitor", "DeadlockMonitor", "ErrorMonitor" })
        {
            XEventService.BuildStartSessionSql(name)
                .Should().Be($"ALTER EVENT SESSION [{name}] ON SERVER STATE = START");
            XEventService.BuildStopSessionSql(name)
                .Should().Be($"ALTER EVENT SESSION [{name}] ON SERVER STATE = STOP");
            XEventService.BuildDropSessionSql(name)
                .Should().Be($"DROP EVENT SESSION [{name}] ON SERVER");
            XEventService.BuildStartupStateSql(name, true)
                .Should().Be($"ALTER EVENT SESSION [{name}] ON SERVER WITH (STARTUP_STATE = ON)");
            XEventService.BuildStartupStateSql(name, false)
                .Should().Be($"ALTER EVENT SESSION [{name}] ON SERVER WITH (STARTUP_STATE = OFF)");
        }
    }

    [Fact]
    public void The_startup_state_keyword_comes_from_the_bool_and_not_from_caller_text()
    {
        // ON/OFF is the one other interpolated token in these statements. It is derived from a
        // bool, so there is no caller string that can land in that position — asserted here so a
        // later refactor to a string parameter has to break this test first.
        XEventService.BuildStartupStateSql("s", true).Should().EndWith("(STARTUP_STATE = ON)");
        XEventService.BuildStartupStateSql("s", false).Should().EndWith("(STARTUP_STATE = OFF)");
    }

    // ── The shipped call paths must go through the builders ─────────────────────
    //
    // Everything above asserts the builders. Nothing above ties the four async methods the UI
    // actually calls to them. Without this, a body that rebuilt its statement inline would leave
    // every assertion above measuring dead code — the failure mode
    // XEventSessionPredicatePlacementTests measured by mutation on 2026-08-25.

    [Theory]
    [InlineData("StartSessionAsync",     "BuildStartSessionSql(",   "ALTER EVENT SESSION")]
    [InlineData("StopSessionAsync",      "BuildStopSessionSql(",    "ALTER EVENT SESSION")]
    [InlineData("DropSessionAsync",      "BuildDropSessionSql(",    "DROP EVENT SESSION")]
    [InlineData("SetStartupStateAsync",  "BuildStartupStateSql(",   "ALTER EVENT SESSION")]
    public void The_shipped_method_builds_its_statement_only_through_its_builder(
        string methodName, string builderCall, string rawStatementMarker)
    {
        var body = ReadMethodBody(ReadXEventServiceSource(), methodName);

        body.Should().Contain(builderCall,
            $"{methodName} is what the UI calls. If it stops calling its builder, the assertions "
            + "above measure dead code.");
        StripCommentLines(body).Should().NotContain(rawStatementMarker,
            $"a raw '{rawStatementMarker}' literal inside {methodName} is a second, unescaped copy "
            + "of the statement — which is how the unescaped identifier survived in the first place.");
    }

    [Fact]
    public void No_lifecycle_statement_interpolates_the_session_name_raw()
    {
        // A whole-file sweep, so a NEW lifecycle statement added later cannot quietly reintroduce
        // the shape. Matches the pre-fix idiom directly: a brace-interpolated name between
        // brackets, or between the quotes of a T-SQL literal.
        var code = StripCommentLines(ReadXEventServiceSource());

        Regex.Matches(code, @"\[\{sessionName[^}]*\}\]").Count.Should().Be(0,
            "a bare brace-interpolated session name between brackets is the raw identifier shape. "
            + "Route it through XEventService.BracketQuote instead.");

        // The literal sweep is restricted to SQL-BEARING lines. The service also builds operator
        // MESSAGES that quote the name — $"Session '{sessionName}' created successfully" — and
        // those are C# strings shown in the UI, not T-SQL, so a quote in them injects nothing. A
        // sweep that flagged them would be five false findings, and a guard that cries wolf is a
        // guard somebody deletes. SqlLineMarkers is what decides, and it is measured by the control
        // below rather than trusted.
        var sqlLines = code.Split('\n')
            .Where(l => SqlLineMarkers.Any(m => l.Contains(m, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        sqlLines.Should().NotBeEmpty(
            "no SQL-bearing lines were found, so this sweep cannot judge the file and must not "
            + "report clean. Update SqlLineMarkers if the statements changed shape.");

        foreach (var line in sqlLines)
        {
            Regex.Matches(line, @"'[^'\r\n]*\{sessionName[^}]*\}[^'\r\n]*'").Count.Should().Be(0,
                "a session name interpolated inside a T-SQL string literal must go through "
                + $"XEventService.QuoteLiteral first. Offending line:\n{line}");
        }
    }

    /// <summary>
    /// Tokens that mark a line as carrying T-SQL rather than an operator message. Kept small and
    /// literal; the control below measures it against the real pre-fix line.
    /// </summary>
    private static readonly string[] SqlLineMarkers =
    {
        "EVENT SESSION", ".xel", ".xem", "fn_xe_file_target_read_file", "SELECT", "ON SERVER"
    };

    [Fact]
    public void Control_the_literal_sweep_recognises_the_pre_fix_sql_line_and_ignores_a_message()
    {
        // The real pre-fix filename line, and the real operator message that must NOT be flagged.
        const string PreFixSqlLine =
            "                ADD TARGET package0.event_file(SET filename = '{sessionName}.xel', max_file_size = 10)";
        const string OperatorMessageLine =
            "            return $\"Session '{sessionName}' created successfully\";";

        SqlLineMarkers.Any(m => PreFixSqlLine.Contains(m, StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("the SQL line must be recognised as SQL, or the sweep looks past the defect.");
        Regex.IsMatch(PreFixSqlLine, @"'[^'\r\n]*\{sessionName[^}]*\}[^'\r\n]*'")
            .Should().BeTrue("the pattern must flag the raw filename interpolation it was written for.");

        SqlLineMarkers.Any(m => OperatorMessageLine.Contains(m, StringComparison.OrdinalIgnoreCase))
            .Should().BeFalse(
                "an operator message that quotes the session name is a C# string shown in the UI, "
                + "not T-SQL. Flagging it would make this guard noise.");
    }

    // ── Controls: the instrument must still be able to see the defect ───────────

    [Theory]
    [InlineData("CREATE EVENT SESSION [x] TO SERVER; DROP TABLE t;--] ON SERVER")]
    [InlineData("ALTER EVENT SESSION [x] TO SERVER; DROP TABLE t;--] ON SERVER STATE = START")]
    [InlineData("DROP EVENT SESSION [x] TO SERVER; DROP TABLE t;--] ON SERVER")]
    public void Instrument_control_flags_the_pre_fix_raw_interpolation(string preFixSql)
    {
        // These are the strings the four raw statements emitted for HostileName before
        // 2026-09-01. If this goes green, OutsideQuoting has stopped detecting the very shape it
        // was written for and every assertion above it is worthless.
        var outside = OutsideQuoting(preFixSql);

        outside.Should().Contain(";",
            "the pre-fix form leaks a statement separator to statement level — that is the defect.");
        outside.Should().Contain("DROP TABLE",
            "the pre-fix form leaks the injected statement to statement level.");
    }

    [Fact]
    public void Instrument_control_flags_a_name_that_escapes_a_string_literal()
    {
        var preFixSql = "SET filename = 'probe'; DROP TABLE t;--.xel'";

        OutsideQuoting(preFixSql).Should().Contain("DROP TABLE",
            "an unescaped quote in the filename literal leaks the rest of the name to statement "
            + "level. A control that cannot flag it cannot protect the escaped form.");
    }

    [Fact]
    public void Call_path_control_refuses_to_judge_a_method_it_cannot_find()
    {
        // Silence on a missing method would read as "clean". It must throw instead.
        var act = () => ReadMethodBody("public class Nothing { }", "StartSessionAsync");
        act.Should().Throw<InvalidOperationException>();
    }

    // ── Source helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// The body of a method, from its name to the next member's doc comment. Throws rather than
    /// returning empty when the method is absent: an absence must never read as a pass.
    /// </summary>
    internal static string ReadMethodBody(string source, string methodName)
    {
        var start = source.IndexOf(methodName + "(", StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException(
                $"{methodName} was not found in the source, so this guard cannot judge its call "
                + "path and must not report clean. Rename it here if the method moved.");
        }

        var end = source.IndexOf("\n    /// <summary>", start, StringComparison.Ordinal);
        return end < 0 ? source.Substring(start) : source.Substring(start, end - start);
    }

    private static string StripCommentLines(string source) =>
        string.Join("\n", source
            .Split('\n')
            .Where(line =>
            {
                var t = line.TrimStart();
                return !t.StartsWith("//", StringComparison.Ordinal)
                    && !t.StartsWith("///", StringComparison.Ordinal)
                    && !t.StartsWith("*", StringComparison.Ordinal);
            }));

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
}
