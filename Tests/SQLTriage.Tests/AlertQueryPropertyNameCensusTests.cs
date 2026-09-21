/* In the name of God, the Merciful, the Compassionate */

// ── A property name nobody proved is a NULL waiting to happen (lane Q14, 2026-09-18) ─────────────
//
// INVARIANT. Every ENABLED shipped alert executed as a standard query must be able to produce a
// value. SERVERPROPERTY and its family do not raise an error for a name they do not know: they return
// NULL, silently, and a NULL measurement can never cross a threshold. connection_count divided by
// SERVERPROPERTY('MaxConnections') - not a property - from v0.80.0 until this lane, so its value was
// NULL on every server (PROVED live on .\NEW2022 and .\OLD2017).
//
// WHAT THIS CENSUS READS: STRUCTURE. Each query is parsed by ScriptDom's TSql160Parser and walked with
// a TSqlFragmentVisitor. Every FunctionCall whose name ends in PROPERTY or PROPERTYEX is a member of
// the family; the argument that names the property is taken from ArgumentPositions. When that
// argument is a string literal, the (function, name) pair must be in AllowList, which holds ONLY
// names PROVED live to return non-NULL on both test instances. A parse error is a FAILURE naming the
// alert, never a skip. A family member whose argument position is not in ArgumentPositions is a
// FAILURE too, so a new function cannot pass by being unrecognised.
//
// DYNAMIC SQL, AND THE HEURISTIC THAT FINDS IT - THIS IS THE BLIND SPOT. Several shipped queries
// build a batch as a string and run it with sp_executesql (database_space_full, log_space_full and
// filegroup_space call FILEPROPERTY only inside such a string). The parser sees a string literal,
// not SQL, so this census chooses which literals to parse, and that choice is a HEURISTIC, not a
// structural fact. The rule: a chain of + concatenations containing at least one string literal is
// reassembled with every non-literal operand replaced by the identifier [__dynamic__] (so
// N'USE ' + QUOTENAME(@d) + N'; SELECT ...' becomes "USE [__dynamic__]; SELECT ..."); a string literal
// outside any chain is taken as it is. A candidate whose text contains a SQL statement keyword AND
// parses with zero errors into at least one statement is walked, recursively. What that MISSES: SQL
// assembled through variables in several SET statements, REPLACE() templates, CHAR()-built text, a
// fragment too partial to parse on its own, and a property name that is itself concatenated. Every
// keyword-bearing literal that did NOT parse is printed in the distribution, so the blind spot is
// visible on every run rather than silent.
//
// A CONFIGURATION NAME IS NOT A PROPERTY NAME, AND THIS CENSUS DOES NOT CHECK IT - THE SECOND BLIND
// SPOT. connection_count reads sys.configurations WHERE name = N'user connections'. A misspelt name
// raises no error either: the subquery returns no row, its value is NULL, and that query's
// ISNULL(NULLIF(..., 0), 32767) would silently divide by 32,767 instead. PROVED by Q14 gate 2 on both
// instances with N'user connexions' (the project's private evidence archive, evidence/alerts-that-cannot-fire-2026-09-18/gate2/
// g6-premises.out.txt). This census walks only the PROPERTY family, so a sys.configurations name
// literal, or any other catalog-view name compared as a string, is NOT validated. The shipped name is
// right (the same probe read 'user connections', value_in_use 0, on both instances); nothing here
// would notice if it were not.
//
// A NULL ON ONE TEST INSTANCE IS TOLERATED ONLY BY BODY. Both tolerance lists below are keyed by the
// alert's DefinitionSignature as well as its (alert, function, property) triple, so a changed query
// is never tolerated on the strength of an entry written about an earlier one: the census goes red,
// and so does the ratchet, until someone re-decides the entry against the body that ships now.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SQLTriage.Data;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;
using SQLTriage.Data.Services;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests
{
    public sealed class AlertQueryPropertyNameCensusTests
    {
        private readonly ITestOutputHelper _out;
        public AlertQueryPropertyNameCensusTests(ITestOutputHelper output) => _out = output;

        /// <summary>Which argument (zero-based) names the property, per the SQL Server documentation of
        /// each function.</summary>
        internal static readonly IReadOnlyDictionary<string, int> ArgumentPositions =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["SERVERPROPERTY"] = 0,
                ["CONNECTIONPROPERTY"] = 0,
                ["SESSIONPROPERTY"] = 0,
                ["FULLTEXTSERVICEPROPERTY"] = 0,
                ["DATABASEPROPERTYEX"] = 1,
                ["DATABASEPROPERTY"] = 1,
                ["FILEPROPERTY"] = 1,
                ["FILEPROPERTYEX"] = 1,
                ["FILEGROUPPROPERTY"] = 1,
                ["OBJECTPROPERTY"] = 1,
                ["OBJECTPROPERTYEX"] = 1,
                ["TYPEPROPERTY"] = 1,
                ["ASSEMBLYPROPERTY"] = 1,
                ["COLLATIONPROPERTY"] = 1,
                ["LOGINPROPERTY"] = 1,
                ["FULLTEXTCATALOGPROPERTY"] = 1,
                ["INDEXPROPERTY"] = 2,
                ["COLUMNPROPERTY"] = 2,
                ["INDEXKEY_PROPERTY"] = 3,
            };

        /// <summary>
        /// The ONLY property names a shipped alert query may use. Each was PROVED live to return
        /// non-NULL on BOTH lpc:MSI\NEW2022 (16.0.4275.2, CU26) and lpc:MSI\OLD2017 (14.0.2130.4, RTM)
        /// on 2026-09-18 01:50 NZST by the lane-Q14 probe probe-r2-and-props.sql (its output is
        /// the project's private evidence archive, evidence/alerts-that-cannot-fire-2026-09-18/builder/live/probe-r2-and-props.out.txt).
        /// The same probe returned NULL for
        /// SERVERPROPERTY('MaxConnections') and for an invented name on both instances, which is the
        /// control that shows it can tell a property from a non-property.
        /// </summary>
        internal static readonly IReadOnlyDictionary<(string Function, string Property), string> AllowList =
            new Dictionary<(string, string), string>(new PairComparer())
            {
                [("SERVERPROPERTY", "IsHadrEnabled")] = "probe-r2-and-props: NEW2022 0, OLD2017 0",
                [("SERVERPROPERTY", "ServerName")] = "probe-r2-and-props: NEW2022 MSI\\NEW2022, OLD2017 MSI\\OLD2017",
                [("FILEPROPERTY", "SpaceUsed")] = "probe-r2-and-props: master data file NEW2022 14816, OLD2017 2864; log NEW2022 248, OLD2017 460",
                [("FULLTEXTSERVICEPROPERTY", "IsFullTextInstalled")] = "probe-r2-and-props: NEW2022 0, OLD2017 0",
            };

        /// <summary>
        /// One tolerated NULL: a property that is a real property on one test instance and NULL on the
        /// other, tolerated for exactly one alert body. <paramref name="DefinitionSignature"/> is
        /// <c>AlertDefinitionMigrator.DefinitionSignature</c> of that body as it ships.
        /// </summary>
        internal sealed record ToleratedNull(string AlertId, string Function, string Property, string DefinitionSignature, string Why);

        /// <summary>
        /// KNOWN DEBT, pinned rather than hidden: a NULL-on-one-instance name the alert's value still
        /// DEPENDS on. It is NOT in <see cref="AllowList"/>, because nobody proved it non-NULL on both;
        /// the census tolerates exactly these entries, for exactly the body named, and
        /// <see cref="Every_tolerated_null_still_describes_the_body_that_ships"/> goes red when one stops
        /// matching, which is how a fix gets noticed.
        ///
        /// <para><b>EMPTY since lane Q14 fix round 2 (2026-09-18).</b> Its one entry was
        /// <c>integrity_check_overdue</c>'s DATABASEPROPERTYEX(name, 'LastGoodCheckDbTime'), body
        /// cf2d11e2... (the 11e6f88 query). PROVED 2026-09-18 01:50 NZST (the project's private evidence archive,
        /// evidence/alerts-that-cannot-fire-2026-09-18/builder/live/probe-lastgoodcheckdb.out.txt):
        /// non-NULL for every database on NEW2022 (16.0.4275.2; 1900-01-01 where CHECKDB never ran), NULL
        /// for EVERY database on OLD2017 (14.0.2130.4, RTM, no CU), so that body read 0 hours for ever there
        /// and could not fire. Ruling 3 of DECISIONS 2026-09-18 04:21 fixed it, and the entry moved to
        /// <see cref="NullOnATestInstanceWithAProvedFallback"/> against the new body.
        /// <see cref="The_retired_debt_entry_is_noticed_by_the_ratchet"/> keeps the retired entry as a
        /// specimen.</para>
        /// </summary>
        internal static readonly IReadOnlyList<ToleratedNull> KnownNullOnATestInstance = Array.Empty<ToleratedNull>();

        /// <summary>The debt entry this list held until fix round 2, kept verbatim as the ratchet's specimen.</summary>
        internal static readonly ToleratedNull RetiredIntegrityCheckOverdueDebt = new(
            "integrity_check_overdue", "DATABASEPROPERTYEX", "LastGoodCheckDbTime",
            "cf2d11e2914cde94086f2d53e5bde018c521ff3e043b1e58eff786b49d6f74fa",
            "the 11e6f88 body: NULL on OLD2017 for every database, so the alert read 0 for ever there");

        /// <summary>
        /// A NULL-on-one-instance name the alert calls, whose NULL the query HANDLES through a fallback
        /// that was PROVED live to produce a value on the instance where the property is NULL. Tolerated
        /// for the named body only. Each entry names, in <see cref="ToleratedNull.Why"/>, the LiveFact on
        /// this class that re-proves it; the ratchet requires that test to exist and to be a LiveFact.
        /// </summary>
        internal static readonly IReadOnlyList<ToleratedNull> NullOnATestInstanceWithAProvedFallback =
            new[]
            {
                new ToleratedNull(
                    "integrity_check_overdue", "DATABASEPROPERTYEX", "LastGoodCheckDbTime",
                    // Re-keyed 2026-09-19 (lane alert-correctness 2, C1: a THROW before the final SELECT when no
                    // date is readable). Value printed by the REAL AlertDefinitionMigrator.DefinitionSignature over
                    // the new shipped file (the project's private evidence archive, evidence/alert-correctness-l2-2026-09-19, c1-sig/sig.out.txt)
                    // and by AlertDefinitionMigratorLane2Tests; was 39b7e60e... for the 4082 body.
                    "f005a1275af59db596c516441662958c26d95c8ec2c5ad9d121798a9b2a70a46",
                    nameof(Integrity_check_overdue_reads_a_value_on_a_live_instance)
                    + ": where the property is NULL the query reads DBCC DBINFO's dbi_dbccLastKnownGood. PROVED 2026-09-18 on "
                    + "OLD2017 (property NULL for all 8 databases; DBINFO dates for 7, and the alert read 1220 hours) and NEW2022 "
                    + "(DBINFO agreed with the property on all 11). Evidence: the project's private evidence archive, evidence/alerts-that-cannot-fire-2026-09-18/fix2/w3/. "
                    + "Re-proved on the C1 body by the armed LiveFact on OLD2017, 2026-09-19 (evidence/alert-correctness-l2-2026-09-19)."),
            };

        /// <summary>Whether a call is tolerated by either list: same triple, same body.</summary>
        internal static bool Tolerated(PropertyCall c, string? definitionSignature) =>
            KnownNullOnATestInstance.Concat(NullOnATestInstanceWithAProvedFallback).Any(k => Matches(k, c, definitionSignature));

        internal static bool Matches(ToleratedNull k, PropertyCall c, string? definitionSignature) =>
            string.Equals(k.AlertId, c.AlertId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(k.Function, c.Function, StringComparison.OrdinalIgnoreCase)
            && string.Equals(k.Property, c.Property, StringComparison.OrdinalIgnoreCase)
            && string.Equals(k.DefinitionSignature, definitionSignature, StringComparison.OrdinalIgnoreCase);

        private sealed class PairComparer : IEqualityComparer<(string, string)>
        {
            public bool Equals((string, string) x, (string, string) y) =>
                string.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode((string, string) obj) =>
                HashCode.Combine(obj.Item1.ToUpperInvariant(), obj.Item2.ToUpperInvariant());
        }

        internal sealed record PropertyCall(string AlertId, string Function, string? Property, int Depth, string Rendered, bool UnknownFunction);

        internal sealed class Walk
        {
            public string? DefinitionSignature;
            public List<PropertyCall> Calls = new();
            public List<string> ParseFailures = new();
            public List<string> DynamicWalked = new();
            public List<string> DynamicNotParsed = new();
            public int NonLiteralPropertyArguments;
        }

        /// <summary>The keyword test that decides whether a literal is worth parsing. A character test,
        /// used ONLY to pick candidates; what is then walked is the parse tree.</summary>
        private static readonly Regex StatementKeyword = new(
            @"\b(SELECT|INSERT|UPDATE|DELETE|MERGE|EXEC|EXECUTE|USE|DECLARE)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private const string DynamicPlaceholder = "[__dynamic__]";

        internal static Walk WalkQuery(string alertId, string sql, string? definitionSignature = null)
        {
            var w = new Walk { DefinitionSignature = definitionSignature };
            var script = AlertQueryCensusSource.Parse(sql, out var errors);
            if (errors.Count > 0 || script == null)
            {
                w.ParseFailures.Add($"{alertId}: ScriptDom (TSql160Parser) could not parse this query ({AlertQueryCensusSource.DescribeParseErrors(errors)}). "
                                    + "A query this census cannot read is not one it can vouch for. CHECK: does the query run as written in SSMS against a test instance?");
                return w;
            }
            WalkFragment(alertId, script, 0, w);
            return w;
        }

        private static void WalkFragment(string alertId, TSqlFragment fragment, int depth, Walk w)
        {
            var visitor = new FamilyVisitor(alertId, depth, w);
            fragment.Accept(visitor);

            foreach (var candidate in visitor.Candidates)
            {
                if (!StatementKeyword.IsMatch(candidate)) continue;
                var inner = AlertQueryCensusSource.Parse(candidate, out var errors);
                var statements = inner?.Batches.Sum(b => b.Statements.Count) ?? 0;
                var shown = candidate.Length > 100 ? candidate.Substring(0, 100) + "..." : candidate;
                if (errors.Count == 0 && inner != null && statements > 0)
                {
                    w.DynamicWalked.Add($"{alertId} depth {depth + 1}: {shown}");
                    WalkFragment(alertId, inner, depth + 1, w);
                }
                else
                {
                    w.DynamicNotParsed.Add($"{alertId} depth {depth + 1}: {shown}");
                }
            }
        }

        private sealed class FamilyVisitor : TSqlFragmentVisitor
        {
            private readonly string _alertId;
            private readonly int _depth;
            private readonly Walk _w;
            private readonly HashSet<TSqlFragment> _consumed = new(ReferenceEqualityComparer.Instance);
            public readonly List<string> Candidates = new();

            public FamilyVisitor(string alertId, int depth, Walk w)
            {
                _alertId = alertId;
                _depth = depth;
                _w = w;
            }

            public override void Visit(FunctionCall node)
            {
                var name = node.FunctionName?.Value ?? "";
                var upper = name.ToUpperInvariant();
                if (!upper.EndsWith("PROPERTY", StringComparison.Ordinal) && !upper.EndsWith("PROPERTYEX", StringComparison.Ordinal))
                    return;

                var rendered = Render(node);
                if (!ArgumentPositions.TryGetValue(upper, out var position))
                {
                    _w.Calls.Add(new PropertyCall(_alertId, upper, null, _depth, rendered, UnknownFunction: true));
                    return;
                }

                if (position < node.Parameters.Count && node.Parameters[position] is StringLiteral literal)
                    _w.Calls.Add(new PropertyCall(_alertId, upper, literal.Value, _depth, rendered, UnknownFunction: false));
                else
                    _w.NonLiteralPropertyArguments++;
            }

            public override void Visit(BinaryExpression node)
            {
                if (node.BinaryExpressionType != BinaryExpressionType.Add || _consumed.Contains(node)) return;
                var sb = new StringBuilder();
                var sawLiteral = false;
                Flatten(node, sb, ref sawLiteral);
                if (sawLiteral) Candidates.Add(sb.ToString());
            }

            public override void Visit(StringLiteral node)
            {
                if (!_consumed.Contains(node)) Candidates.Add(node.Value);
            }

            private void Flatten(ScalarExpression e, StringBuilder sb, ref bool sawLiteral)
            {
                switch (e)
                {
                    case BinaryExpression b when b.BinaryExpressionType == BinaryExpressionType.Add:
                        _consumed.Add(b);
                        Flatten(b.FirstExpression, sb, ref sawLiteral);
                        Flatten(b.SecondExpression, sb, ref sawLiteral);
                        break;
                    case ParenthesisExpression p:
                        Flatten(p.Expression, sb, ref sawLiteral);
                        break;
                    case StringLiteral s:
                        _consumed.Add(s);
                        sawLiteral = true;
                        sb.Append(s.Value);
                        break;
                    default:
                        sb.Append(DynamicPlaceholder);
                        break;
                }
            }
        }

        private static string Render(TSqlFragment fragment)
        {
            new Sql160ScriptGenerator().GenerateScript(fragment, out var script);
            return script.Trim();
        }

        /// <summary>The failures for one alert, each naming the alert and WHAT TO CHECK.</summary>
        internal static List<string> Violations(Walk w)
        {
            var v = new List<string>(w.ParseFailures);
            foreach (var c in w.Calls)
            {
                var where = c.Depth == 0 ? "in its query" : $"inside dynamic SQL (depth {c.Depth})";
                if (c.UnknownFunction)
                {
                    v.Add($"{c.AlertId}: {c.Rendered} {where} belongs to the PROPERTY family, and this census does not know which of its arguments names the property, so it cannot check the name. "
                          + "An unknown property name returns NULL silently. CHECK: that function's SQL Server documentation for its property argument, then run the call against a test instance.");
                    continue;
                }
                if (AllowList.ContainsKey((c.Function, c.Property!))) continue;
                if (Tolerated(c, w.DefinitionSignature)) continue;

                v.Add($"{c.AlertId}: {c.Rendered} {where} names the property '{c.Property}', which nobody has proved returns a value. "
                      + "A name SQL Server does not know raises no error: it returns NULL, and a NULL measurement can never cross a threshold "
                      + "(connection_count divided by SERVERPROPERTY('MaxConnections') and never fired). "
                      + $"CHECK: run SELECT {c.Rendered} against a test instance (lpc:MSI\\NEW2022 and lpc:MSI\\OLD2017 on the build box; for a file or object property, in a database where its other arguments resolve). "
                      + "NULL on a healthy instance means the name is not a property on that build.");
            }
            return v;
        }

        // ── The census, over the bytes that install ───────────────────────────────────────────────

        [Fact]
        public void Every_property_name_in_an_enabled_standard_alert_query_was_proved_live()
        {
            var all = AlertQueryCensusSource.Shipped();
            var haystack = AlertQueryCensusSource.EnabledStandard(all);
            Assert.NotEmpty(haystack);

            var walks = haystack.Select(a => WalkQuery(a.Id, a.Query, AlertQueryCensusSource.ShippedSignature(a.Id))).ToList();
            var calls = walks.SelectMany(x => x.Calls).ToList();
            var violations = walks.SelectMany(Violations).ToList();

            _out.WriteLine($"enabled standard queries walked: {haystack.Count} of {all.Count} shipped alerts; parse failures: {walks.Sum(x => x.ParseFailures.Count)}");
            _out.WriteLine($"dynamic-SQL literals parsed and walked: {walks.Sum(x => x.DynamicWalked.Count)}");
            foreach (var d in walks.SelectMany(x => x.DynamicWalked)) _out.WriteLine("  WALKED " + d);
            _out.WriteLine($"BLIND SPOT - keyword-bearing literals that did NOT parse, so were NOT walked: {walks.Sum(x => x.DynamicNotParsed.Count)}");
            foreach (var d in walks.SelectMany(x => x.DynamicNotParsed)) _out.WriteLine("  NOT WALKED " + d);
            _out.WriteLine($"property-family calls with a literal name: {calls.Count(c => !c.UnknownFunction)}; with a non-literal name (not checkable): {walks.Sum(x => x.NonLiteralPropertyArguments)}; unknown family functions: {calls.Count(c => c.UnknownFunction)}");
            foreach (var g in calls.GroupBy(c => $"{c.Function}('{c.Property}') depth {c.Depth}").OrderBy(g => g.Key))
                _out.WriteLine($"  {g.Key}: {g.Count()} [{string.Join(", ", g.Select(c => c.AlertId).Distinct())}]");
            foreach (var line in violations) _out.WriteLine("VIOLATION " + line);

            // Non-empty haystacks for BOTH walkers, derived from the tree: the top-level walk found a
            // literal property name, and the dynamic-SQL walk found one too. Without these a walker that
            // silently stopped visiting would pass as "no unknown names".
            Assert.Contains(calls, c => !c.UnknownFunction && c.Depth == 0);
            Assert.Contains(calls, c => !c.UnknownFunction && c.Depth > 0);

            Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
        }

        /// <summary>Every call in the shipped enabled standard queries, each with its body's signature.</summary>
        private static List<(PropertyCall Call, string Signature)> ShippedCalls() =>
            AlertQueryCensusSource.EnabledStandard(AlertQueryCensusSource.Shipped())
                .SelectMany(a =>
                {
                    var signature = AlertQueryCensusSource.ShippedSignature(a.Id);
                    return WalkQuery(a.Id, a.Query, signature).Calls.Select(c => (c, signature));
                })
                .ToList();

        /// <summary>
        /// Both tolerance lists are a ratchet in the direction of a FIX: an entry that no longer matches a
        /// shipped call ON THE SAME BODY is reported, so a repaired or changed alert is noticed instead of
        /// keeping its exemption. An entry claiming a proved fallback must also name a LiveFact on this class
        /// that exists.
        /// </summary>
        [Fact]
        public void Every_tolerated_null_still_describes_the_body_that_ships()
        {
            var calls = ShippedCalls();
            Assert.NotEmpty(calls);

            foreach (var entry in KnownNullOnATestInstance.Concat(NullOnATestInstanceWithAProvedFallback))
            {
                Assert.False(AllowList.ContainsKey((entry.Function, entry.Property)),
                    $"{entry.Function}('{entry.Property}') is in both the allow-list and a tolerance list; a name proved non-NULL on both instances needs no tolerance.");
                Assert.True(calls.Any(x => Matches(entry, x.Call, x.Signature)),
                    $"tolerated NULL ({entry.AlertId}, {entry.Function}, {entry.Property}) for body {entry.DefinitionSignature} matches no call in the body that ships. "
                    + "The defect it records may have been fixed, or the alert's query, unit, operator or thresholds changed. "
                    + "CHECK: that alert's current query, whether the property is still called, whether it now returns a value on both test instances, and "
                    + "whether any fallback still produces a value where it is NULL. Then move, re-sign or delete the entry.");
            }

            foreach (var entry in NullOnATestInstanceWithAProvedFallback)
            {
                var testName = entry.Why.Split(':')[0].Trim();
                var method = typeof(AlertQueryPropertyNameCensusTests).GetMethod(testName, BindingFlags.Public | BindingFlags.Instance);
                Assert.True(method != null && method.GetCustomAttributes(typeof(LiveFactAttribute), false).Length == 1,
                    $"({entry.AlertId}, {entry.Property}) claims a proved fallback and names '{testName}', which is not a LiveFact on this class. "
                    + "A tolerance whose proof cannot be re-run is not a proof. CHECK: the name in the entry's Why.");
            }
        }

        /// <summary>
        /// THE SPECIMEN FOR THE RATCHET. The entry the debt list held until fix round 2 described the body
        /// that shipped at 11e6f88, and it matches nothing that ships now: left in place, the ratchet above
        /// would go red naming it, and the census would stop tolerating the call. Both halves are asserted,
        /// so a key change that blinded either one fails here.
        /// </summary>
        [Fact]
        public void The_retired_debt_entry_is_noticed_by_the_ratchet()
        {
            var old = RetiredIntegrityCheckOverdueDebt;

            var prelane = AlertQueryCensusSource.PreLane("integrity_check_overdue");
            var prelaneSignature = AlertQueryCensusSource.PreLaneSignature("integrity_check_overdue");
            Assert.Equal(old.DefinitionSignature, prelaneSignature);
            var prelaneCalls = WalkQuery(prelane.Id, prelane.Query, prelaneSignature).Calls;
            Assert.Contains(prelaneCalls, c => Matches(old, c, prelaneSignature));   // it DID describe the old body

            var calls = ShippedCalls();
            Assert.Contains(calls, x => x.Call.AlertId == old.AlertId && x.Call.Property == old.Property);   // the call is still there
            Assert.DoesNotContain(calls, x => Matches(old, x.Call, x.Signature));                             // the ratchet notices
        }

        /// <summary>
        /// THE LIVE PROOF behind <see cref="NullOnATestInstanceWithAProvedFallback"/>. Runs the SHIPPED
        /// integrity_check_overdue query through the evaluator's own ExecuteAlertQueryAsync against the
        /// instance in SQLTRIAGE_LIVE_INSTANCE (for example lpc:MSI\OLD2017), as the login running the
        /// test. It must produce a value. Where every online user database reads NULL for
        /// LastGoodCheckDbTime, that value can only have come from the DBCC DBINFO fallback, and the test
        /// says so. Read-only: DATABASEPROPERTYEX, sys.databases and DBCC DBINFO.
        /// </summary>
        [LiveFact("SQLTRIAGE_LIVE_INSTANCE")]
        public async Task Integrity_check_overdue_reads_a_value_on_a_live_instance()
        {
            var target = Environment.GetEnvironmentVariable("SQLTRIAGE_LIVE_INSTANCE");
            Assert.False(string.IsNullOrWhiteSpace(target),
                "the attribute skips an unarmed run; this assertion is what makes a WEAKENED attribute fail instead of passing on nothing");

            var connection = new ServerConnection
            {
                Id = Guid.NewGuid().ToString(),
                ServerNames = target!,
                UseWindowsAuthentication = true,
                TrustServerCertificate = true,
                ConnectionTimeout = 10,
                IsEnabled = true,
            };

            int userDatabases, propertyNull;
            bool sysadmin;
            await using (var sql = new SqlConnection(connection.GetConnectionString(target!, "master")))
            {
                await sql.OpenAsync();
                await using var cmd = new SqlCommand(
                    "SELECT COUNT(*), SUM(CASE WHEN DATABASEPROPERTYEX(name, 'LastGoodCheckDbTime') IS NULL THEN 1 ELSE 0 END), "
                    + "CAST(IS_SRVROLEMEMBER('sysadmin') AS int) FROM sys.databases WHERE database_id > 4 AND state = 0", sql);
                await using var reader = await cmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                userDatabases = reader.GetInt32(0);
                propertyNull = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                sysadmin = !reader.IsDBNull(2) && reader.GetInt32(2) == 1;
            }

            var alert = AlertQueryCensusSource.Shipped().Single(a => a.Id == "integrity_check_overdue");
            using var svc = new AlertEvaluationService(
                NullLogger<AlertEvaluationService>.Instance,
                new AlertDefinitionService(NullLogger<AlertDefinitionService>.Instance),
                new AlertHistoryService(NullLogger<AlertHistoryService>.Instance),
                new AlertingService(NullLogger<AlertingService>.Instance),
                new ServerConnectionManager(NullLogger<ServerConnectionManager>.Instance),
                new ToastService(),
                new NotificationChannelService(NullLogger<NotificationChannelService>.Instance,
                    new AlertTemplateService(NullLogger<AlertTemplateService>.Instance)),
                new liveQueriesCacheStore(),
                null!)
            { DryRun = true };

            var execute = typeof(AlertEvaluationService).GetMethod("ExecuteAlertQueryAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(execute != null, "the harness calls AlertEvaluationService.ExecuteAlertQueryAsync; a rename must fail loudly");
            var value = await (Task<double?>)execute!.Invoke(svc, new object[] { alert, connection, target! })!;

            _out.WriteLine($"{target}: online user databases {userDatabases}, LastGoodCheckDbTime NULL for {propertyNull}, sysadmin {sysadmin}, integrity_check_overdue value {(value.HasValue ? value.Value.ToString("N0") : "NULL")} hours");

            Assert.True(userDatabases > 0, $"HARNESS: {target} has no online user database, so this test cannot show the alert reading one");
            Assert.True(sysadmin || propertyNull == 0,
                $"HARNESS: {target} reports NULL LastGoodCheckDbTime and the test login is not sysadmin, so DBCC DBINFO may refuse it and a NULL answer would be honest. Run this armed as a sysadmin login.");
            Assert.True(value.HasValue,
                $"integrity_check_overdue returned NO VALUE on {target} ({propertyNull} of {userDatabases} user databases read NULL LastGoodCheckDbTime), "
                + "so it cannot fire there. CHECK: the DBCC DBINFO fallback in the shipped query, run as written in SSMS against that instance.");
            Assert.True(value!.Value >= 0, $"integrity_check_overdue read {value} hours on {target}, which no date in the past produces");
            if (propertyNull == userDatabases)
                _out.WriteLine("every user database read NULL for the property, so this value came from the DBCC DBINFO fallback");
        }

        // ── Specimen controls ─────────────────────────────────────────────────────────────────────

        /// <summary>The defect itself, from the pre-lane fixture, through the same walker, by id.</summary>
        [Fact]
        public void The_census_flags_the_connection_count_query_that_shipped_at_11e6f88()
        {
            var specimen = AlertQueryCensusSource.PreLane("connection_count");
            var v = Violations(WalkQuery(specimen.Id, specimen.Query));

            var line = Assert.Single(v);
            Assert.StartsWith("connection_count:", line);
            Assert.Contains("MaxConnections", line);
            Assert.Contains("CHECK: run SELECT", line);
            Assert.DoesNotContain("allow", line, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The dynamic-SQL walker can show a positive: an unknown name reached only through a
        /// concatenated sp_executesql batch, the shape database_space_full uses.</summary>
        [Fact]
        public void An_unknown_name_inside_concatenated_dynamic_sql_is_flagged()
        {
            const string sql = "DECLARE @d SYSNAME = N'master', @s NVARCHAR(MAX); "
                               + "SET @s = N'USE ' + QUOTENAME(@d) + N'; SELECT FILEPROPERTY(name, ''NoSuchPropertyQ14'') FROM sys.database_files;'; "
                               + "DECLARE @r TABLE(v INT); INSERT INTO @r EXEC sp_executesql @s; SELECT MAX(v) AS value FROM @r";
            var w = WalkQuery("dynamic-specimen", sql);
            var v = Violations(w);

            var line = Assert.Single(v);
            Assert.Contains("NoSuchPropertyQ14", line);
            Assert.Contains("inside dynamic SQL (depth 1)", line);
        }

        /// <summary>The shipped shape that needs the dynamic walk: FILEPROPERTY SpaceUsed is found at depth 1
        /// in database_space_full, and passes, so the positive above is not the walker flagging anything.</summary>
        [Fact]
        public void A_proved_name_inside_shipped_dynamic_sql_is_found_and_passes()
        {
            var alert = AlertQueryCensusSource.Shipped().Single(a => a.Id == "database_space_full");
            var w = WalkQuery(alert.Id, alert.Query);

            Assert.Contains(w.Calls, c => c.Function == "FILEPROPERTY" && c.Property == "SpaceUsed" && c.Depth == 1);
            Assert.Empty(Violations(w));
        }

        [Fact]
        public void A_property_function_the_census_does_not_know_is_a_failure_not_a_pass()
        {
            var v = Violations(WalkQuery("unknown-function", "SELECT CERTPROPERTY(1, 'Subject') AS value"));
            var line = Assert.Single(v);
            Assert.Contains("does not know which of its arguments", line);
        }
    }
}
