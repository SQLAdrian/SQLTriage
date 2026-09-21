/* In the name of God, the Merciful, the Compassionate */

// ── The FIRST result set is the only one the evaluator reads (lane Q14, 2026-09-18) ──────────────
//
// INVARIANT. Every ENABLED shipped alert executed as a standard query must hand the evaluator the
// value the alert means. AlertEvaluationService.ExecuteAlertQueryAsync calls ExecuteScalarAsync,
// which reads the first column of the first row of the FIRST result set and never calls NextResult.
// sql_response_time shipped "DECLARE @t DATETIME = GETDATE(); SELECT 1; SELECT DATEDIFF(...) AS
// value", so the value was always the constant 1, against thresholds of 1000 and 2000 ms, and the
// alert could never fire (PROVED live on .\NEW2022 and .\OLD2017, scout wf_ee2a97c4-5b3).
//
// WHAT THIS CENSUS READS: STRUCTURE. Each query is parsed by ScriptDom's TSql160Parser and walked as
// a syntax tree. A parse error is a FAILURE naming the alert, never a skip. Two rules:
//
//   RULE 1 - AT MOST ONE RESULT SET ON ANY PATH, AT LEAST ONE ON SOME PATH. The statements are
//   walked as control flow: IF/ELSE
//   branches, BEGIN/END sequences, RETURN ends a path, WHILE repeats its body, TRY/CATCH may run
//   the catch after any part of the try. Along every path at most ONE client-visible result set may
//   be produced, and at least one path must produce one. Client-visible: a SELECT that is not a
//   variable assignment, not SELECT ... INTO and not a cursor's definition; a FETCH without INTO;
//   an INSERT/UPDATE/DELETE/MERGE with a bare OUTPUT clause. INSERT ... SELECT and INSERT ... EXEC
//   return nothing to the client. A bare EXEC (not inside INSERT ... EXEC), a DBCC, a GOTO, SET
//   STATISTICS XML/PROFILE, or a result set inside a WHILE body is UNBOUNDED and fails.
//   This is the rule that matters: it states the invariant itself. The six ag_* queries
//   (IF ... BEGIN SELECT 0 AS value; RETURN; END SELECT ... AS value) pass it, because each path
//   produces exactly one.
//
//   RULE 2 - THE FIRST COLUMN IS NAMED value. ExecuteScalar reads the first COLUMN too, so every
//   client-visible SELECT's first column must be named value (an alias, or a column of that name).
//   Every shipped standard query already follows this convention; it is kept as a second rule
//   because rule 1 alone would pass "SELECT COUNT(*) AS n, MAX(x) AS value". It is NOT sufficient on
//   its own, and that was measured rather than assumed: "SELECT 1 AS value; SELECT 2 AS value" is
//   the sql_response_time defect with its first SELECT renamed, and rule 2 passes it. The specimen
//   controls below prove each rule catches what the other cannot.
//
// BOTH RULES WERE CHOSEN BY MEASUREMENT: at 11e6f88 there were 67 enabled standard queries; 66 of
// them (and the six disabled ones) passed both rules unmodified, and sql_response_time failed both.
// No other shipped query was edited to make this census pass.
//
// WHAT IS NOT SEEN, stated rather than hidden. A statement kind this walker does not model is treated
// as producing no result set (the distribution below prints every kind it saw, so a new one is
// visible). Result sets produced INSIDE dynamic SQL are seen only through the EXEC that runs them,
// which fails unless it is INSERT ... EXEC. A raised error that ends a batch early is not modelled.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The shipped-catalogue loader and the ScriptDom parse shared by the two lane-Q14 censuses,
    /// AlertQueryResultSetCensusTests and AlertQueryPropertyNameCensusTests.
    /// </summary>
    internal static class AlertQueryCensusSource
    {
        /// <summary>The bytes that install, anchored on the repo root, never the test-output copy.</summary>
        internal static string ShippedPath() =>
            Path.Combine(RawPassedScan.RepoRoot().FullName, "Config", "alert-definitions.json");

        internal static List<AlertDefinition> Load(string path)
        {
            var file = JsonSerializer.Deserialize<AlertDefinitionsFile>(
                File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(file);
            return file!.Alerts;
        }

        internal static List<AlertDefinition> Shipped() => Load(ShippedPath());

        /// <summary>Every ENABLED alert whose query field the evaluator actually executes. The routing
        /// test is the evaluator's own, so this can never census text the evaluator does not run.</summary>
        internal static List<AlertDefinition> EnabledStandard(IEnumerable<AlertDefinition> alerts) =>
            alerts.Where(a => a.Enabled && !AlertEvaluationService.IsRoutedToBuiltInHandler(a)).ToList();

        /// <summary>The three alerts as they shipped at 11e6f88 (the pre-lane fixture).</summary>
        internal static AlertDefinition PreLane(string id) =>
            Load(PreLanePath()).Single(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

        private static string PreLanePath() =>
            Path.Combine(RawPassedScan.RepoRoot().FullName, "Tests", "SQLTriage.Tests", "Fixtures", "prelane-q14-alerts-11e6f88.json");

        /// <summary>AlertDefinitionMigrator.DefinitionSignature of the SHIPPED body of <paramref name="id"/>,
        /// hashed from the raw JSON node exactly as the migrator hashes an installed file.</summary>
        internal static string ShippedSignature(string id) => SignatureIn(ShippedPath(), id);

        /// <summary>The same, for the pre-lane fixture's body of <paramref name="id"/>.</summary>
        internal static string PreLaneSignature(string id) => SignatureIn(PreLanePath(), id);

        private static string SignatureIn(string path, string id)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["alerts"]!.AsArray()
                .Select(n => n!.AsObject())
                .SingleOrDefault(o => string.Equals(o["id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase));
            Assert.True(node != null, $"{id}: no alert with this id in {path}");
            return SQLTriage.Data.AlertDefinitionMigrator.DefinitionSignature(node!);
        }

        internal static TSqlScript? Parse(string sql, out IList<ParseError> errors)
        {
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            using var reader = new StringReader(sql ?? string.Empty);
            return parser.Parse(reader, out errors) as TSqlScript;
        }

        internal static string DescribeParseErrors(IList<ParseError> errors) =>
            string.Join("; ", errors.Take(3).Select(e => $"line {e.Line} col {e.Column}: {e.Message}"));
    }

    public sealed class AlertQueryResultSetCensusTests
    {
        private readonly ITestOutputHelper _out;
        public AlertQueryResultSetCensusTests(ITestOutputHelper output) => _out = output;

        /// <summary>A path count this large means "cannot be bounded" (a loop, a bare EXEC).</summary>
        internal const int Unbounded = 1_000_000;

        internal sealed class Report
        {
            public string Id = "";
            public IList<ParseError> ParseErrors = Array.Empty<ParseError>();
            public int Batches;
            public int MaxResultSetsOnAnyPath;
            public List<string> UnboundedReasons = new();
            public List<string> FirstColumns = new();
            public Dictionary<string, int> StatementKinds = new(StringComparer.Ordinal);
            public int ExcludedAssignmentOrInto;
            public int ExcludedCursorSelects;
            public int InsertExec;
            public int ReturnStatements;
        }

        private readonly record struct Flow(int? Open, int Done);

        private static int? MaxOpen(int? a, int? b) =>
            a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

        private static int Add(int a, int b) => Math.Min(Unbounded, a + b);

        private static QuerySpecification? FirstSpecification(QueryExpression? q) => q switch
        {
            QuerySpecification s => s,
            BinaryQueryExpression b => FirstSpecification(b.FirstQueryExpression),
            QueryParenthesisExpression p => FirstSpecification(p.QueryExpression),
            _ => null
        };

        /// <summary>A SELECT whose rows reach the client: not SELECT ... INTO, not a pure variable
        /// assignment. (A cursor's SELECT is excluded by the caller, which knows the parent.)</summary>
        internal static bool IsClientVisible(SelectStatement s)
        {
            if (s.Into != null) return false;
            var spec = FirstSpecification(s.QueryExpression);
            return spec == null || !spec.SelectElements.All(e => e is SelectSetVariable);
        }

        private static string FirstColumnName(SelectStatement s)
        {
            var spec = FirstSpecification(s.QueryExpression);
            if (spec == null || spec.SelectElements.Count == 0) return "(unreadable)";
            return spec.SelectElements[0] switch
            {
                SelectScalarExpression { ColumnName: not null } e => e.ColumnName.Value,
                SelectScalarExpression { Expression: ColumnReferenceExpression c } when c.MultiPartIdentifier != null
                    => c.MultiPartIdentifier.Identifiers.Last().Value,
                SelectScalarExpression => "(no name)",
                SelectStarExpression => "*",
                var other => "(" + other.GetType().Name + ")"
            };
        }

        private static OutputClause? BareOutput(TSqlStatement st) => st switch
        {
            InsertStatement i => i.InsertSpecification?.OutputClause,
            UpdateStatement u => u.UpdateSpecification?.OutputClause,
            DeleteStatement d => d.DeleteSpecification?.OutputClause,
            MergeStatement m => m.MergeSpecification?.OutputClause,
            _ => null
        };

        private static Flow EvalList(IEnumerable<TSqlStatement> statements, int open, Report r)
        {
            int? current = open;
            var done = -1;
            foreach (var st in statements)
            {
                if (current is null) break;               // every path already returned
                var f = EvalStatement(st, current.Value, r);
                current = f.Open;
                done = Math.Max(done, f.Done);
            }
            return new Flow(current, done);
        }

        private static Flow EvalStatement(TSqlStatement st, int open, Report r)
        {
            var kind = st.GetType().Name;
            r.StatementKinds[kind] = r.StatementKinds.TryGetValue(kind, out var n) ? n + 1 : 1;

            switch (st)
            {
                case SelectStatement s:
                    return new Flow(IsClientVisible(s) ? Add(open, 1) : open, -1);

                case ReturnStatement:
                    r.ReturnStatements++;
                    return new Flow(null, open);

                case BeginEndBlockStatement b:
                    return EvalList(b.StatementList.Statements, open, r);

                case IfStatement i:
                {
                    var then = EvalStatement(i.ThenStatement, open, r);
                    var otherwise = i.ElseStatement != null ? EvalStatement(i.ElseStatement, open, r) : new Flow(open, -1);
                    return new Flow(MaxOpen(then.Open, otherwise.Open), Math.Max(then.Done, otherwise.Done));
                }

                case WhileStatement w:
                {
                    var body = EvalStatement(w.Statement, 0, r);
                    if (Math.Max(body.Open ?? 0, body.Done) > 0)
                    {
                        r.UnboundedReasons.Add($"a result set inside a WHILE body (line {w.StartLine}) repeats once per iteration");
                        return new Flow(Unbounded, Unbounded);
                    }
                    return new Flow(open, body.Done >= 0 ? Add(open, body.Done) : -1);
                }

                case TryCatchStatement t:
                {
                    var tried = EvalList(t.TryStatements.Statements, open, r);
                    // The catch can run after any part of the try, so it starts from the most the try
                    // could have produced on any path.
                    var start = Math.Max(open, Math.Max(tried.Open ?? open, tried.Done));
                    var caught = EvalList(t.CatchStatements.Statements, start, r);
                    return new Flow(MaxOpen(tried.Open, caught.Open), Math.Max(tried.Done, caught.Done));
                }

                case InsertStatement ins when ins.InsertSpecification?.InsertSource is ExecuteInsertSource:
                    r.InsertExec++;
                    return BareOutput(st) != null ? new Flow(Add(open, 1), -1) : new Flow(open, -1);

                case ExecuteStatement:
                    r.UnboundedReasons.Add($"a bare EXEC (line {st.StartLine}) returns whatever result sets the procedure or batch returns");
                    return new Flow(Unbounded, Unbounded);

                case DbccStatement:
                    r.UnboundedReasons.Add($"a DBCC statement (line {st.StartLine}) can return result sets of its own");
                    return new Flow(Unbounded, Unbounded);

                case GoToStatement:
                    r.UnboundedReasons.Add($"a GOTO (line {st.StartLine}) makes the paths unreadable to this walker");
                    return new Flow(Unbounded, Unbounded);

                case SetStatisticsStatement stats when (stats.Options & (SetStatisticsOptions.Xml | SetStatisticsOptions.Profile)) != 0:
                    r.UnboundedReasons.Add($"SET STATISTICS XML/PROFILE (line {st.StartLine}) adds a result set after every statement");
                    return new Flow(Unbounded, Unbounded);

                case FetchCursorStatement fetch:
                    return new Flow((fetch.IntoVariables == null || fetch.IntoVariables.Count == 0) ? Add(open, 1) : open, -1);

                default:
                    return new Flow(BareOutput(st) != null ? Add(open, 1) : open, -1);
            }
        }

        private sealed class SelectCollector : TSqlFragmentVisitor
        {
            private readonly HashSet<SelectStatement> _cursorSelects = new(ReferenceEqualityComparer.Instance);
            public readonly Report R;
            public SelectCollector(Report r) => R = r;

            public override void Visit(DeclareCursorStatement node)
            {
                if (node.CursorDefinition?.Select != null) _cursorSelects.Add(node.CursorDefinition.Select);
            }

            public override void Visit(SelectStatement node)
            {
                if (_cursorSelects.Contains(node)) { R.ExcludedCursorSelects++; return; }
                if (!IsClientVisible(node)) { R.ExcludedAssignmentOrInto++; return; }
                R.FirstColumns.Add(FirstColumnName(node));
            }
        }

        /// <summary>Walks one query. The census and every specimen control go through this.</summary>
        internal static Report Analyse(string id, string sql)
        {
            var r = new Report { Id = id };
            var script = AlertQueryCensusSource.Parse(sql, out var errors);
            r.ParseErrors = errors;
            if (errors.Count > 0 || script == null) return r;

            r.Batches = script.Batches.Count;
            var statements = script.Batches.SelectMany(b => b.Statements).ToList();
            var flow = EvalList(statements, 0, r);
            r.MaxResultSetsOnAnyPath = Math.Max(flow.Open ?? 0, flow.Done);
            script.Accept(new SelectCollector(r));
            return r;
        }

        /// <summary>The failures for one report, each naming the alert and WHAT TO CHECK.</summary>
        internal static List<string> Violations(Report r)
        {
            var v = new List<string>();
            if (r.ParseErrors.Count > 0)
            {
                v.Add($"{r.Id}: ScriptDom (TSql160Parser) could not parse this query ({AlertQueryCensusSource.DescribeParseErrors(r.ParseErrors)}). "
                      + "A query this census cannot read is not one it can vouch for. CHECK: does the query run as written in SSMS against a test instance?");
                return v;
            }
            if (r.Batches != 1)
                v.Add($"{r.Id}: the query parses as {r.Batches} batches. The evaluator sends it as ONE command, where a GO separator is a syntax error and an empty query returns nothing. "
                      + "CHECK: the query text as a single batch in SSMS.");

            if (r.MaxResultSetsOnAnyPath != 1)
            {
                var why = r.UnboundedReasons.Count > 0 ? " (" + string.Join("; ", r.UnboundedReasons) + ")" : "";
                var count = r.MaxResultSetsOnAnyPath >= Unbounded ? "an unbounded number of" : r.MaxResultSetsOnAnyPath.ToString();
                v.Add($"{r.Id}: on at least one path this query can return {count} client-visible result sets{why}. "
                      + "The evaluator reads ONLY the first column of the first row of the FIRST result set (ExecuteAlertQueryAsync, ExecuteScalar), so a result set before the one the alert means replaces its value, and a query with none can never produce one. "
                      + "CHECK: run it in SSMS against a test instance and look at the Results tab on each branch: is the FIRST grid the number this alert's thresholds describe, and is it the only grid? "
                      + "A variable assignment (SELECT @x = ...), SELECT ... INTO, INSERT ... SELECT and INSERT ... EXEC return no grid.");
            }

            var misnamed = r.FirstColumns.Where(c => !string.Equals(c, "value", StringComparison.OrdinalIgnoreCase)).ToList();
            if (misnamed.Count > 0)
                v.Add($"{r.Id}: a client-visible SELECT's first column is named [{string.Join(", ", misnamed)}], not value. "
                      + "ExecuteScalar reads the FIRST column whatever it is called. CHECK: is the first column of that SELECT the quantity the alert's thresholds and unit describe?");
            return v;
        }

        // ── The census, over the bytes that install ───────────────────────────────────────────────

        [Fact]
        public void Every_enabled_standard_alert_returns_at_most_one_result_set_on_any_path_and_at_least_one_on_some_path_whose_first_column_is_its_value()
        {
            var all = AlertQueryCensusSource.Shipped();
            var haystack = AlertQueryCensusSource.EnabledStandard(all);

            // The haystack must be non-empty BEFORE any needle is looked for: an empty catalogue, or a
            // routing predicate that excluded everything, would otherwise pass as "no violations".
            Assert.NotEmpty(haystack);

            var reports = haystack.Select(a => Analyse(a.Id, a.Query)).ToList();
            var violations = reports.SelectMany(Violations).ToList();

            _out.WriteLine($"shipped alerts: {all.Count}; enabled standard (census haystack): {haystack.Count}; "
                           + $"handler-routed, not censused: {all.Count(AlertEvaluationService.IsRoutedToBuiltInHandler)}; "
                           + $"disabled, not censused: {all.Count(a => !a.Enabled && !AlertEvaluationService.IsRoutedToBuiltInHandler(a))}");
            _out.WriteLine($"parsed cleanly: {reports.Count(r => r.ParseErrors.Count == 0)}");
            _out.WriteLine("RULE 1 max result sets on any path -> queries: "
                           + string.Join(", ", reports.GroupBy(r => r.MaxResultSetsOnAnyPath).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}")));
            _out.WriteLine($"RULE 1 queries with a RETURN (branching shape): {reports.Count(r => r.ReturnStatements > 0)}; "
                           + $"with INSERT ... EXEC: {reports.Count(r => r.InsertExec > 0)}; "
                           + $"SELECTs excluded as assignment/INTO: {reports.Sum(r => r.ExcludedAssignmentOrInto)}; cursor SELECTs excluded: {reports.Sum(r => r.ExcludedCursorSelects)}");
            _out.WriteLine("RULE 2 first-column names seen -> SELECTs: "
                           + string.Join(", ", reports.SelectMany(r => r.FirstColumns).GroupBy(c => c.ToLowerInvariant()).Select(g => $"{g.Key}={g.Count()}")));
            _out.WriteLine("statement kinds walked: "
                           + string.Join(", ", reports.SelectMany(r => r.StatementKinds)
                               .GroupBy(kv => kv.Key).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Sum(kv => kv.Value)}")));
            foreach (var line in violations) _out.WriteLine("VIOLATION " + line);

            Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
        }

        // ── Specimen controls: the same walker must be able to show a positive ───────────────────

        /// <summary>The defect itself, from the pre-lane fixture: both rules flag it, by id.</summary>
        [Fact]
        public void The_census_flags_the_sql_response_time_query_that_shipped_at_11e6f88()
        {
            var specimen = AlertQueryCensusSource.PreLane("sql_response_time");
            Assert.Contains("SELECT 1;", specimen.Query);

            var r = Analyse(specimen.Id, specimen.Query);
            Assert.Empty(r.ParseErrors);
            Assert.Equal(2, r.MaxResultSetsOnAnyPath);
            Assert.Contains("(no name)", r.FirstColumns);

            var v = Violations(r);
            Assert.Equal(2, v.Count);
            Assert.All(v, line => Assert.StartsWith("sql_response_time:", line));
        }

        /// <summary>Rule 2 alone is NOT enough: the defect with its first SELECT renamed passes it.</summary>
        [Fact]
        public void The_renamed_defect_passes_the_column_rule_and_is_still_caught_by_the_path_rule()
        {
            var r = Analyse("renamed", "DECLARE @t DATETIME = GETDATE(); SELECT 1 AS value; SELECT DATEDIFF(MILLISECOND, @t, GETDATE()) AS value");
            Assert.All(r.FirstColumns, c => Assert.Equal("value", c));
            Assert.Equal(2, r.MaxResultSetsOnAnyPath);
            Assert.Single(Violations(r));
        }

        /// <summary>Rule 1 alone is NOT enough: one result set whose first column is not the value.</summary>
        [Fact]
        public void A_single_result_set_whose_first_column_is_not_the_value_is_caught_by_the_column_rule()
        {
            var r = Analyse("first-column", "SELECT COUNT(*) AS n, MAX(database_id) AS value FROM sys.databases");
            Assert.Equal(1, r.MaxResultSetsOnAnyPath);
            var v = Assert.Single(Violations(r));
            Assert.Contains("[n]", v);
        }

        /// <summary>The shapes that must PASS, so the rules are not simply strict: the ag_* early return,
        /// INSERT ... EXEC, a cursor, variable assignments and SELECT ... INTO.</summary>
        [Theory]
        [InlineData("IF SERVERPROPERTY('IsHadrEnabled') = 0 BEGIN SELECT 0 AS value; RETURN; END SELECT COUNT(*) AS value FROM sys.databases")]
        [InlineData("DECLARE @r TABLE(v INT); INSERT INTO @r EXEC sp_executesql N'SELECT 1'; SELECT MAX(v) AS value FROM @r")]
        [InlineData("DECLARE @d SYSNAME, @n INT = 0; DECLARE c CURSOR LOCAL FOR SELECT name FROM sys.databases; OPEN c; FETCH NEXT FROM c INTO @d; WHILE @@FETCH_STATUS = 0 BEGIN SET @n += 1; FETCH NEXT FROM c INTO @d; END CLOSE c; DEALLOCATE c; SELECT @n AS value")]
        [InlineData("DECLARE @x INT; SELECT @x = COUNT(*) FROM sys.databases; SELECT name INTO #t FROM sys.databases; SELECT @x AS value")]
        [InlineData("IF 1 = 1 SELECT 1 AS value ELSE SELECT 2 AS value")]
        public void Known_good_shapes_are_not_flagged(string sql)
        {
            var v = Violations(Analyse("known-good", sql));
            Assert.True(v.Count == 0, string.Join(Environment.NewLine, v));
        }

        /// <summary>The shapes that must FAIL, each for its own reason.</summary>
        [Theory]
        [InlineData("EXEC sp_who2", "bare EXEC")]
        [InlineData("DECLARE @i INT = 0; WHILE @i < 2 BEGIN SELECT @i AS value; SET @i += 1; END", "WHILE body")]
        [InlineData("IF 1 = 1 SELECT 1 AS value; SELECT 2 AS value", "2 client-visible")]
        [InlineData("DECLARE @x INT = 1", "0 client-visible")]
        [InlineData("SELECT 1 AS value\nGO\nSELECT 2 AS value", "batches")]
        [InlineData("SELECT FROM WHERE", "could not parse")]
        public void Known_bad_shapes_are_flagged_with_a_reason(string sql, string expected)
        {
            var v = Violations(Analyse("known-bad", sql));
            Assert.Contains(v, line => line.Contains(expected, StringComparison.Ordinal));
        }

        /// <summary>The census reads the SHIPPED file and the handler-routed alerts are outside it by the
        /// evaluator's own routing: sql_response_time is handler-routed now, so its inert query is not
        /// censused, while connection_count still is.</summary>
        [Fact]
        public void The_haystack_follows_the_evaluators_routing()
        {
            var haystack = AlertQueryCensusSource.EnabledStandard(AlertQueryCensusSource.Shipped());
            Assert.DoesNotContain(haystack, a => a.Id == "sql_response_time");
            Assert.Contains(haystack, a => a.Id == "connection_count");
        }
    }
}
