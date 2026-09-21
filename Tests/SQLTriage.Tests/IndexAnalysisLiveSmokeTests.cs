/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

/// <summary>
/// EXERCISE VEHICLE for <see cref="IndexAnalysisService"/> — orchestrator/gate-owned. INERT in a
/// normal <c>dotnet test</c> run: <see cref="LiveFactAttribute"/> reports every test as SKIPPED
/// unless <c>INDEX_ANALYSIS_LIVE_TARGET</c> names a SQL instance. It reports SKIPPED rather than
/// early-returning to green, because an assertion-free pass counted inside a "suite green" claim
/// is the same over-claim this lane exists to remove.
///
/// <para>Two jobs. First, it REPRODUCES the mapping defect against a real instance by running the
/// page's old mapping verbatim — <c>(double)reader.GetDecimal(3)</c> on
/// <c>avg_fragmentation_in_percent</c> — and then proves the shipped service reads the same rows
/// cleanly. ⚠ It reproduces THIS defect; it does not establish that this defect produced the
/// banner in the original report — see the provenance block in IndexAnalysisMapperTests. Second,
/// it is the CAPTURE HARNESS for
/// <see cref="IndexAnalysisMapperTests"/>: when <c>INDEX_ANALYSIS_CAPTURE_OUT</c> is set it writes
/// the real reader shapes (declared type + CLR type + round-trippable value, per column, per row)
/// to disk, and those captures — not hand-composed rows — are what the offline tests replay.
/// That is the house rule: a test built from a payload the endpoint never sends proves nothing.</para>
///
/// <para>INVOCATION (gate, on a box with the local test instances). Every fixture is the caller's
/// to create and drop; this file plants nothing:</para>
/// <code>
///   $env:INDEX_ANALYSIS_LIVE_TARGET = ".\old2017"
///   $env:INDEX_ANALYSIS_LIVE_DB     = "_sqlt_idxscope_cap"
///   $env:INDEX_ANALYSIS_CAPTURE_OUT = "C:\temp\idxscope\captured-reader-shapes.json"
///   dotnet test Tests/SQLTriage.Tests --filter "FullyQualifiedName~IndexAnalysisLiveSmokeTests"
/// </code>
///
/// <para>⚠⚠ THE FIXTURE CONTRACT IS A PRECONDITION, NOT A SUGGESTION. These tests create nothing.
/// Arming them against an instance that does not meet the shape below makes several of them fail
/// for reasons that are not defects in the code — each asserts its own precondition and says so in
/// the failure message rather than passing empty, but a reader who arms them on a bare instance
/// sees red and learns to ignore red. Read this list before setting the variable. Everything here
/// was built on .\old2017 by the 2026-08-13 lane as <c>_sqlt_idxscope_*</c> and dropped afterwards;
/// the SQL that built it is not shipped, so the list IS the contract.</para>
/// <list type="bullet">
/// <item>MORE user databases than <see cref="IndexAnalysisService.TopDatabasesByIo"/>, so the cut
/// is observable at all — and more than that many ELIGIBLE ones, or an unranked database is simply
/// scanned and the AUTO_CLOSE test asserts nothing (the fix round used 47 user databases, 25
/// eligible: two purpose-built, one capture database, eighteen padding, one AUTO_CLOSE, plus what
/// was already there);</item>
/// <item>a clear IO spread — one clearly busiest database, and a fragmented index in a database
/// that is NOT it, so finding that index proves the sweep reached past the first rank. ⚠ Drive the
/// busiest one with reads (DBCC DROPCLEANBUFFERS then a full scan), not writes alone: the capture
/// database accumulates IO of its own while being seeded and can otherwise stay ahead;</item>
/// <item>never-read indexes in TWO different databases, so a per-database label cannot be a
/// constant. ⚠ Never <c>SELECT COUNT(*)</c> to verify one — the optimizer picks the narrow
/// nonclustered index and that scan makes the index READ, so it stops qualifying. Verify through
/// sys.dm_db_index_usage_stats instead;</item>
/// <item>an OFFLINE user database. ⚠ Its IO CANNOT be driven after it goes offline —
/// dm_io_virtual_file_stats has no row for it at all — so drive the IO first, then take it
/// offline;</item>
/// <item>MORE passed-over databases than the skip-report cap, or nothing exercises the truncation
/// the page has to disclose (the fix round used 22 offline fixtures against a cap of 20);</item>
/// <item>an ONLINE, MULTI_USER, writable database with AUTO_CLOSE ON and no open connection, which
/// is the eligible-but-unmeasured case. ⚠ Its IO counters RESET when the files close and again
/// when they reopen, so it cannot be given an IO figure that survives; that is the defect, not a
/// fixture problem;</item>
/// <item>a database holding a fragmented index, a never-read index AND a missing-index request at
/// once, named by INDEX_ANALYSIS_LIVE_DB, because the capture reads them on one connection. A
/// missing-index request needs a plan the optimizer costed as expensive: a narrow 40,000-row table
/// produced none, a 300,000-row table with a 400-byte filler produced one. ⚠ A set-based
/// <c>INSERT … SELECT</c> into a clustered index is SORTED before insert and produces ~0%
/// fragmentation; rebuild at FILLFACTOR 100 and then do a few thousand singleton inserts in random
/// key order (that reached 94.95% over 4,060 pages in under three seconds).</item>
/// </list>
/// </summary>
public class IndexAnalysisLiveSmokeTests
{
    // ── LiveFactAttribute LIVED HERE, and moved on 2026-08-14 ────────────────────────────────
    //
    // It is now Tests/SQLTriage.Tests/LiveFactAttribute.cs, unchanged in mechanism. Its own note
    // said to move it to a shared profile-neutral file "the moment a second non-Portal harness
    // wants it"; AuditRestartBannerRenderTests is that harness. Same namespace, so every
    // [LiveFact(...)] below still binds. ⚠ It must NOT be moved under Portal/: this harness is not
    // community-gated, and binding a community-removed type here would make the community test
    // assembly uncompilable.

    /// <summary>
    /// The guard behind <see cref="LiveFactAttribute"/>. If the attribute is ever weakened or
    /// removed, the body must FAIL rather than pass vacuously — same reason
    /// Portal/ExportPackE3LiveHarness asserts its arming variable after its Skip.
    /// </summary>
    private static string RequireTarget()
    {
        Assert.False(string.IsNullOrWhiteSpace(Target),
            "INDEX_ANALYSIS_LIVE_TARGET is not set, so this test has no instance to probe and "
            + "nothing to assert. It should have been SKIPPED by LiveFactAttribute; if it ran, "
            + "that attribute is no longer doing its job.");
        return Target!;
    }

    private readonly ITestOutputHelper _out;
    public IndexAnalysisLiveSmokeTests(ITestOutputHelper output) => _out = output;

    private static string? Target => Environment.GetEnvironmentVariable("INDEX_ANALYSIS_LIVE_TARGET");
    private static string Database => Environment.GetEnvironmentVariable("INDEX_ANALYSIS_LIVE_DB") ?? "master";
    private static string? CaptureOut => Environment.GetEnvironmentVariable("INDEX_ANALYSIS_CAPTURE_OUT");

    /// <summary>
    /// Where the INDEX-QUERY capture goes — a second, much cheaper capture than
    /// <see cref="CaptureOut"/>'s.
    ///
    /// <para>⚠ WHY TWO CAPTURES AND TWO FILES, 2026-08-14. The full capture above includes the
    /// RANKING pass, whose fixture contract is an entire estate: more than twenty user databases,
    /// more than twenty eligible ones, twenty-two offline ones, an AUTO_CLOSE one. The three index
    /// queries need none of that — one database with a fragmented index, some never-read indexes, a
    /// missing-index request and an object outside dbo. Tying the index shapes to the estate
    /// fixture meant that changing an index query required rebuilding forty-odd databases, and
    /// that cost is what would push a future lane into hand-writing a row instead. The ranking
    /// capture keeps its 14.0.2120.1 provenance and its estate; the index capture is taken wherever
    /// an instance is available and states which one.</para>
    /// </summary>
    private static string? IndexCaptureOut => Environment.GetEnvironmentVariable("INDEX_ANALYSIS_INDEX_CAPTURE_OUT");

    private static string ConnectionString(string target, string database) =>
        new SqlConnectionStringBuilder
        {
            DataSource = target,
            InitialCatalog = database,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            ConnectTimeout = 15,
            ApplicationName = "SQLTriage.Tests.IndexAnalysisLiveSmoke",
        }.ConnectionString;

    /// <summary>
    /// THE REPRODUCTION. The exact mapping the page shipped, against a real fragmented index.
    /// Fails the run if it does NOT throw, because a reproduction that silently stops reproducing
    /// means the fixture no longer exercises the defect and the "after" below proves nothing.
    /// </summary>
    [LiveFact("INDEX_ANALYSIS_LIVE_TARGET")]
    public async Task The_old_page_mapping_still_throws_on_a_real_fragmented_index()
    {
        await using var conn = new SqlConnection(ConnectionString(RequireTarget(), Database));
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(IndexAnalysisService.FragIndexSql, conn) { CommandTimeout = 60 };
        await using var reader = await cmd.ExecuteReaderAsync();

        var read = await reader.ReadAsync();
        read.Should().BeTrue(
            $"the fixture database '{Database}' must hold an index over 5% fragmented across more "
            + "than 1,000 pages, or this reproduction is vacuous");

        _out.WriteLine($"column 3 '{reader.GetName(3)}' declared {reader.GetDataTypeName(3)}, "
                       + $"arrives as {reader.GetFieldType(3).FullName}");

        // The shipped line was: FragPercent = (double)reader.GetDecimal(3)
        var thrown = Record.Exception(() => _ = (double)reader.GetDecimal(3));

        thrown.Should().NotBeNull("this is the defect the fix removes; if it stopped throwing, "
                                  + "the fixture stopped exercising it");
        thrown.Should().BeOfType<InvalidCastException>();
        _out.WriteLine($"OLD PATH THREW: {thrown!.GetType().Name}: {thrown.Message}");

        // ...and the columns the audit left alone are confirmed correct on the same row.
        _ = reader.GetInt64(4);
        _out.WriteLine($"PageCount GetInt64(4) = {reader.GetInt64(4)} (unchanged mapping, still correct)");
    }

    // ── TOP 20 BY IO, 2026-08-13 ─────────────────────────────────────────────────────────────
    //
    // The service now ranks user databases by IO and scans the busiest twenty. Four claims below
    // rest on runtime behaviour and are therefore exercised here, never read-and-believed:
    //
    //   1. the ranking the service produces IS the ranking the DMV produces, compared against a
    //      SECOND, independently written query rather than against the service's own SQL;
    //   2. every row is labelled with the database it came from, across more than one database
    //      (one label could be a constant; two cannot);
    //   3. a budget that runs out mid-sweep leaves the databases it reached in the result and
    //      NAMES every one it did not, with the sentence an operator will read;
    //   4. an offline database is skipped with its state named, not dropped and not thrown on.
    //
    // ⚠ FIXTURE CONTRACT. These need an instance with at least two user databases carrying
    // different IO, one fragmented index in a database that is NOT the busiest, and one offline
    // database. The lane built them as _sqlt_idxscope_* on .\old2017 and dropped them afterwards;
    // an unfixtured instance makes several of these vacuous, so each asserts its own precondition.

    /// <summary>
    /// The ranking, checked against the DMV read directly. The reference query is written out
    /// again rather than reusing DatabaseRankingSql: comparing the service's SQL to itself would
    /// pass whatever it did, including ranking by the wrong column.
    ///
    /// <para>⚠ THESE COUNTERS TICK. sys.dm_io_virtual_file_stats is cumulative and live, the
    /// service's own scanning drives IO of its own, and the two reads are seconds apart — so a
    /// naive equality on the numbers is a test that fails on a busy box for no defect. The first
    /// cut of this test did exactly that. What is asserted instead is everything that CANNOT
    /// legitimately move between two readings:</para>
    /// <list type="number">
    /// <item>the metric is the same one — each database's reported IO is within a few percent of
    /// the reference reading, which bytes (out by ~4 orders of magnitude) or stall milliseconds
    /// could not be;</item>
    /// <item>the order is the reported metric's own descending order, so the ORDER BY is real;</item>
    /// <item>every database the reference puts UNAMBIGUOUSLY inside the window — strictly busier
    /// than the first database the reference leaves out — was selected. Ties at the boundary may
    /// fall either way between two readings, and only those are allowed to.</item>
    /// </list>
    /// </summary>
    [LiveFact("INDEX_ANALYSIS_LIVE_TARGET")]
    public async Task The_ranking_is_the_one_the_dmv_gives_when_read_independently()
    {
        const string reference = @"
SELECT d.name, ISNULL(s.io, 0) AS io
FROM sys.databases d
LEFT JOIN (
    SELECT database_id, SUM(num_of_reads + num_of_writes) AS io
    FROM sys.dm_io_virtual_file_stats(NULL, NULL) GROUP BY database_id
) s ON s.database_id = d.database_id
WHERE d.database_id > 4 AND d.state = 0 AND d.user_access = 0 AND d.is_read_only = 0
ORDER BY ISNULL(s.io, 0) DESC, d.name ASC;";

        var referenceRanking = new List<(string Name, long Io)>();
        await using (var conn = new SqlConnection(ConnectionString(RequireTarget(), Database)))
        {
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(reference, conn) { CommandTimeout = 30 };
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
                referenceRanking.Add((rdr.GetString(0), rdr.GetInt64(1)));
        }

        referenceRanking.Should().HaveCountGreaterThan(1,
            "one database cannot show that an ORDER BY is being honoured");

        var svc = new IndexAnalysisService();
        var result = await svc.GetAnalysisAsync(ConnectionString(RequireTarget(), Database));

        var selected = SelectedInRankOrder(result);
        foreach (var (name, io) in selected) _out.WriteLine($"  service: {name}={io:N0}");
        foreach (var (name, io) in referenceRanking.Take(selected.Count + 3)) _out.WriteLine($"  DMV    : {name}={io:N0}");

        // 1. Same metric. Cumulative counters only rise, so the service's later reading is at
        //    least the reference and close to it. Bytes or stalls would miss by orders of magnitude.
        var referenceIo = referenceRanking.ToDictionary(x => x.Name, x => x.Io, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, io) in selected)
        {
            referenceIo.Should().ContainKey(name,
                "the service may only select databases the house predicate admits");
            io.Should().BeGreaterThanOrEqualTo(referenceIo[name],
                $"'{name}' was read later, and these counters only rise");
            io.Should().BeLessThan((long)(referenceIo[name] * 1.5) + 20_000,
                $"'{name}' must be the same OPERATION count the reference read, not bytes and not "
                + "stall milliseconds — those miss by orders of magnitude, not by a few seconds of drift");
        }

        // 2. The order really is that metric, descending.
        selected.Select(x => x.Io).Should().BeInDescendingOrder(
            "the sample is ranked by the IO it reports, so the reported numbers must descend");

        // 3. Everything unambiguously inside the window was taken.
        var selectedNames = selected.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var topN = IndexAnalysisService.TopDatabasesByIo;
        var boundaryIo = referenceRanking.Count > topN ? referenceRanking[topN].Io : -1;

        var unambiguous = referenceRanking.Take(topN).Where(x => x.Io > boundaryIo).ToList();
        unambiguous.Should().NotBeEmpty("otherwise this assertion checks nothing");
        foreach (var (name, io) in unambiguous)
            selectedNames.Should().Contain(name,
                $"'{name}' had {io:N0} operations against a boundary of {boundaryIo:N0}, so no "
                + "reading taken seconds later could put it outside the top {0}", topN);
    }

    /// <summary>
    /// Rows carry their own database, proved across more than one. Also proves the frag scan
    /// reached a database that is NOT first in the ranking, which is the whole point of the
    /// expansion: the old shape could only ever have seen the connection's own database.
    /// </summary>
    [LiveFact("INDEX_ANALYSIS_LIVE_TARGET")]
    public async Task Rows_name_the_database_they_came_from_across_several_databases()
    {
        var svc = new IndexAnalysisService();
        var result = await svc.GetAnalysisAsync(ConnectionString(RequireTarget(), Database));

        result.Scanned.Should().HaveCountGreaterThan(1, "a one-database scan proves no labelling");

        var scannedNames = result.Scanned.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var d in result.Scanned)
            _out.WriteLine($"  scanned #{d.IoRank} {d.Name} io={d.IoOperations:N0}");

        result.Fragmented.Should().NotBeEmpty("the fixture holds a qualifying fragmented index");
        result.Fragmented.Select(f => f.Database).Should().OnlyContain(n => scannedNames.Contains(n),
            "a row may only be labelled with a database this run actually read");

        var fragDatabases = result.Fragmented.Select(f => f.Database).Distinct().ToList();
        var busiest = result.Scanned[0].Name;
        fragDatabases.Should().NotContain(busiest,
            "the fixture puts the fragmented index in a database that is NOT the busiest, so that "
            + "finding it proves the sweep reached past the first rank rather than stopping there");

        // Unused rows are the multi-database discriminator: the fixture seeds never-read indexes
        // in two separate databases, so a single hard-coded label cannot satisfy this.
        result.Unused.Select(u => u.Database).Distinct().Should().HaveCountGreaterThan(1,
            "two distinct database labels on the unused rows is what rules out a constant");
        result.Unused.Select(u => u.Database).Should().OnlyContain(n => scannedNames.Contains(n));
    }

    /// <summary>
    /// THE BUDGET, exercised rather than read. The clock is driven so the deadline passes after a
    /// fixed number of databases: the databases already read stay in the result, and every one
    /// that did not get its turn appears in Skipped with the sentence the page renders. Nothing
    /// throws, and nothing goes missing without being named.
    /// </summary>
    [LiveFact("INDEX_ANALYSIS_LIVE_TARGET")]
    public async Task A_budget_that_runs_out_leaves_partial_results_and_names_every_database_it_skipped()
    {
        var budget = TimeSpan.FromSeconds(90);
        var start = DateTime.UtcNow;

        // Call 1 sets the deadline. Calls 2..4 are the budget check for the first three
        // databases and stay inside it. Every later call is past the deadline.
        var calls = 0;
        DateTime Clock()
        {
            calls++;
            return calls <= 4 ? start : start + budget + TimeSpan.FromSeconds(1);
        }

        var svc = new IndexAnalysisService(utcNow: Clock, overallBudget: budget);
        var result = await svc.GetAnalysisAsync(ConnectionString(RequireTarget(), Database));

        result.Scanned.Should().HaveCount(3, "the clock allowed exactly three databases their turn");
        result.BudgetExhausted.Should().BeTrue();

        var budgeted = result.Skipped.Where(s => s.Reason.Contains("budget", StringComparison.Ordinal)).ToList();
        budgeted.Should().NotBeEmpty("the remaining databases must be listed, not silently dropped");
        foreach (var s in budgeted.Take(5)) _out.WriteLine($"  skipped: {s.Database} — {s.Reason}");

        budgeted.Should().OnlyContain(
            s => s.Reason == $"Not scanned. The {(int)budget.TotalSeconds}-second budget for this run ran out first.",
            "the page renders this string verbatim, so the exact words are part of the contract");

        // The union is still the full selection: partial does not mean lossy.
        SelectedInRankOrder(result).Should().HaveCount(result.Scanned.Count + budgeted.Count);

        // ...and the databases it DID reach produced real rows rather than an empty pass.
        result.Unused.Concat<object>(result.Fragmented).Should().NotBeEmpty(
            "three scanned databases that yielded nothing would make the partial claim untestable");
    }

    /// <summary>
    /// An offline database is named with its state. It must not error the run.
    ///
    /// <para>⚠ RE-SCOPED 2026-08-13 (the fix round). This used to require EVERY offline database to
    /// be in the list, which is a promise the cap cannot keep: the list stops at
    /// <c>SkipReportCap</c>, so on an instance with more offline databases than that, the test
    /// failed for a reason that was not a defect in the code under test — and it did, which is how
    /// the capped-count defect was found. What is asserted now is what the service actually
    /// promises: the offline databases that ARE listed carry their state, none of them is scanned,
    /// and the census counts every one of them whether the list holds it or not.</para>
    /// </summary>
    [LiveFact("INDEX_ANALYSIS_LIVE_TARGET")]
    public async Task An_offline_database_is_skipped_with_its_state_named()
    {
        var offline = new List<string>();
        await using (var conn = new SqlConnection(ConnectionString(RequireTarget(), Database)))
        {
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(
                "SELECT name FROM sys.databases WHERE database_id > 4 AND state_desc = 'OFFLINE';", conn);
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync()) offline.Add(rdr.GetString(0));
        }

        offline.Should().NotBeEmpty(
            "this proof needs an offline user database on the instance; without one it asserts nothing");

        var svc = new IndexAnalysisService();
        var result = await svc.GetAnalysisAsync(ConnectionString(RequireTarget(), Database));

        foreach (var s in result.Skipped) _out.WriteLine($"  skipped: {s.Database} — {s.Reason}");

        var listed = result.Skipped
            .Where(s => offline.Contains(s.Database, StringComparer.OrdinalIgnoreCase))
            .ToList();

        listed.Should().NotBeEmpty("an offline database must be NAMED, not silently absent");
        listed.Should().OnlyContain(s => s.Reason == "Not scanned. The database is OFFLINE.",
            "the reason names the state that was actually read from sys.databases");
        listed.Should().OnlyContain(s => s.PassedOverBeforeScanning,
            "these were passed over by the ranking pass, not stopped mid-scan");

        // Whatever the cap did to the LIST, the census counted them all — and the run reports
        // exactly as many as the cap allows, never fewer and never silently.
        var cap = result.Census.SkipReportCap;
        result.Census.PassedOver.Should().BeGreaterThanOrEqualTo(offline.Count,
            "every offline database is a passed-over database, listed or not");
        result.Skipped.Count(s => s.PassedOverBeforeScanning)
            .Should().Be(Math.Min(result.Census.PassedOver, cap),
                "the list is exactly as long as the cap allows — a shorter one would be a database "
                + "dropped without being counted anywhere");

        result.Scanned.Select(d => d.Name).Should().NotIntersectWith(offline);
    }

    /// <summary>
    /// THE AUTO_CLOSE CASE, end to end through the shipped service. It is ONLINE, MULTI_USER and
    /// writable — the house predicate ADMITS it — and its files are shut, so
    /// sys.dm_io_virtual_file_stats has no row for it and it cannot be ranked. The first cut gated
    /// the manufactured-zero rescue on <c>is_eligible = 0</c>, so a database in this state was
    /// neither scanned nor listed: it disappeared, while the page's scope sentence counted it among
    /// the databases that "ranked below the cut".
    ///
    /// <para>⚠ FIXTURE: an ONLINE user database with AUTO_CLOSE on and no connections, on an
    /// instance with MORE eligible databases than <c>TopDatabasesByIo</c> — otherwise it is simply
    /// scanned and this asserts nothing, which it says out loud rather than passing empty.</para>
    /// </summary>
    [LiveFact("INDEX_ANALYSIS_LIVE_TARGET")]
    public async Task An_eligible_database_with_no_io_reading_is_named_rather_than_dropped()
    {
        const string unmeasuredEligible = @"
SELECT d.name
FROM sys.databases d
LEFT JOIN (SELECT database_id FROM sys.dm_io_virtual_file_stats(NULL, NULL) GROUP BY database_id) s
       ON s.database_id = d.database_id
WHERE d.database_id > 4 AND d.state = 0 AND d.user_access = 0 AND d.is_read_only = 0
  AND s.database_id IS NULL;";

        var unmeasured = new List<string>();
        await using (var conn = new SqlConnection(ConnectionString(RequireTarget(), Database)))
        {
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(unmeasuredEligible, conn) { CommandTimeout = 30 };
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync()) unmeasured.Add(rdr.GetString(0));
        }

        unmeasured.Should().NotBeEmpty(
            "this proof needs an ELIGIBLE user database with no dm_io_virtual_file_stats row — an "
            + "AUTO_CLOSE database with no open connections. Without one it asserts nothing");

        var svc = new IndexAnalysisService();
        var result = await svc.GetAnalysisAsync(ConnectionString(RequireTarget(), Database));

        result.Census.Eligible.Should().BeGreaterThan(IndexAnalysisService.TopDatabasesByIo,
            "with room in the sample an unranked database is simply SCANNED, and the defect this "
            + "test defends against cannot occur");

        foreach (var name in unmeasured)
        {
            _out.WriteLine($"  unmeasured eligible: {name}");

            var scanned = result.Scanned.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
            var skip = result.Skipped.FirstOrDefault(
                s => string.Equals(s.Database, name, StringComparison.OrdinalIgnoreCase));

            (scanned || skip is not null).Should().BeTrue(
                $"'{name}' is a user database this run neither read nor named — the exact defect: "
                + "it is eligible, it has no IO reading, and it belonged to no bucket at all");

            if (scanned) continue;

            skip!.Reason.Should().Be(
                "Not scanned. Its files are not open, so no IO was measured for it and the ranking "
                + "could not place it. A database with AUTO_CLOSE on reads like this.",
                "it is not OFFLINE and must not be told it is");
            skip.IoMeasured.Should().BeFalse();
            skip.PassedOverBeforeScanning.Should().BeTrue();
        }
    }

    /// <summary>
    /// THE SENTENCES, against the instance they describe. Every number the page renders is checked
    /// against a second reading of sys.databases taken here — not against the census that produced
    /// it — because a narrative that agrees with its own inputs proves only that the arithmetic is
    /// consistent, which is exactly what the broken version also was.
    /// </summary>
    [LiveFact("INDEX_ANALYSIS_LIVE_TARGET")]
    public async Task The_rendered_scope_sentences_reconcile_to_the_instance()
    {
        var svc = new IndexAnalysisService();
        var result = await svc.GetAnalysisAsync(ConnectionString(RequireTarget(), Database));

        int userDatabases;
        await using (var conn = new SqlConnection(ConnectionString(RequireTarget(), Database)))
        {
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(
                "SELECT COUNT(*) FROM sys.databases WHERE database_id > 4;", conn);
            userDatabases = (int)(await cmd.ExecuteScalarAsync())!;
        }

        var scope = IndexAnalysisScopeNarrative.For(
            result.Census, result.Scanned.Count, result.Skipped.Count);

        _out.WriteLine($"  HEAD    : {scope.Headline}");
        _out.WriteLine($"  SUB     : {scope.MetricSentence}");
        _out.WriteLine($"  SKIPHEAD: {scope.SkippedHeadline}");
        _out.WriteLine($"  UNLISTED: {(scope.UnlistedCount > 0 ? scope.UnlistedSentence : "(not rendered)")}");

        result.Census.UserDatabases.Should().Be(userDatabases,
            "the census must count the instance an independent read of sys.databases sees");
        (result.Census.Sampled + result.Census.PassedOver + result.Census.RankedBelowCut)
            .Should().Be(userDatabases, "picked, reported and out-ranked partition the instance");

        scope.NotRead.Should().Be(userDatabases - result.Scanned.Count);
        scope.Headline.Should().Contain($"{result.Scanned.Count} of {userDatabases} user databases read.");
        scope.SkippedHeadline.Should().Contain($"{scope.NotRead} of the {userDatabases} user databases");

        // The list is a subset, and the page says by how much. This is the claim the cap broke.
        result.Skipped.Count.Should().BeLessThanOrEqualTo(scope.NotRead,
            "the page can never name more databases than the run failed to read");
        scope.UnlistedCount.Should().Be(scope.NotRead - result.Skipped.Count);
        (result.Skipped.Count + scope.UnlistedCount).Should().Be(scope.NotRead,
            "named plus unnamed must be the whole of what the run did not read, with nothing "
            + "falling between them — which is what happened to the AUTO_CLOSE database");
    }

    /// <summary>
    /// The databases the ranking pass SELECTED, in rank order — scanned ones plus the ones a
    /// budget or an error stopped. Written as one helper so two tests cannot disagree about what
    /// "selected" means.
    /// </summary>
    private static List<(string Name, long Io)> SelectedInRankOrder(IndexAnalysisResult result)
    {
        var scanned = result.Scanned.Select(d => (d.IoRank, Name: d.Name, Io: d.IoOperations));

        // Passed-over rows were never selected. ⚠ The discriminator used to be a PREFIX MATCH on
        // the reason sentence, which quietly made an operator-facing string load-bearing for a
        // classification: adding one skip sentence (DescribeUnrankedSkip, 2026-08-13) put every
        // AUTO_CLOSE database on the selected side of this helper without a compiler error. It is
        // a flag on the row now, set by the only code that can know it.
        var stopped = result.Skipped
            .Where(s => !s.PassedOverBeforeScanning)
            .Select(s => (s.IoRank, Name: s.Database, Io: s.IoOperations));

        return scanned.Concat(stopped).OrderBy(x => x.IoRank).Select(x => (x.Name, x.Io)).ToList();
    }

    /// <summary>THE PROOF. The shipped service, same instance, same rows, end to end.</summary>
    [LiveFact("INDEX_ANALYSIS_LIVE_TARGET")]
    public async Task The_service_reads_the_same_instance_cleanly()
    {
        var svc = new IndexAnalysisService();
        var result = await svc.GetAnalysisAsync(ConnectionString(RequireTarget(), Database));

        _out.WriteLine($"census: topN={result.Census.TopN} userDbs={result.Census.UserDatabases} "
                       + $"eligible={result.Census.Eligible} sampled={result.Census.Sampled} "
                       + $"ineligible={result.Census.Ineligible} passedOver={result.Census.PassedOver} "
                       + $"belowCut={result.Census.RankedBelowCut} cap={result.Census.SkipReportCap}");
        _out.WriteLine($"scanned={result.Scanned.Count} skipped={result.Skipped.Count}");

        _out.WriteLine($"scope database   : '{result.ScopeDatabase}'");
        _out.WriteLine($"server start time: {result.ServerStartTime:yyyy-MM-dd HH:mm:ss}");
        _out.WriteLine($"missing / unused / fragmented = "
                       + $"{result.Missing.Count} / {result.Unused.Count} / {result.Fragmented.Count}");

        foreach (var f in result.Fragmented)
            _out.WriteLine($"  FRAG  {f.Database}.{f.Table}.{f.IndexName}  "
                           + $"frag={f.FragPercent:F2}%  pages={f.PageCount}  "
                           + $"size={(f.SizeMB.HasValue ? f.SizeMB.Value.ToString("F2") : "not measured")}  {f.Action}");
        foreach (var u in result.Unused)
            _out.WriteLine($"  UNUSED {u.Database}.{u.Table}.{u.IndexName} ({u.IndexType})  "
                           + $"reads={u.UserReads} writes={u.UserWrites}  "
                           + $"size={(u.SizeMB.HasValue ? u.SizeMB.Value.ToString("F2") : "not measured")}");
        foreach (var m in result.Missing)
            _out.WriteLine($"  MISSING {m.Database}.{m.Table}  hits={m.UserHits} "
                           + $"impact={IndexAnalysisRendering.Percent(m.AvgImpact, "F2")} "
                           + $"score={IndexAnalysisRendering.Number(m.ImpactScore, "N0")}");

        // ⚠ THE HEALTHY PATH THROUGH THE GUARDS, 2026-08-14. The two instance-wide reads no longer
        // throw out of GetAnalysisAsync: they degrade into a sentence and an empty result. That is
        // the right behaviour for a read that failed and a catastrophe for one that did not, so the
        // live run asserts the guards are SILENT here. Without this, a guard that reported failure
        // unconditionally would leave every list empty and every offline test about the degraded
        // state still green.
        result.RankingFailure.Should().BeNull(
            "the ranking read succeeded against this instance, so nothing may claim it failed");
        result.MissingReadFailure.Should().BeNull(
            "the missing-index read succeeded against this instance");
        result.Scanned.Should().NotBeEmpty(
            "a healthy ranking pass produces a sample, and an empty one is what a swallowed "
            + "failure would look like");

        result.ScopeDatabase.Should().Be(Database,
            "ScopeDatabase names where the ranking and missing-index reads ran, which is still the "
            + "database the caller's connection string landed in");
        result.Fragmented.Should().NotBeEmpty("the fixture holds a qualifying fragmented index");
        result.Fragmented.Should().OnlyContain(f => f.FragPercent > 5 && f.PageCount > 1000);

        // The census has to describe the instance, not the sample. Selected is capped at TopN;
        // Eligible is not, and on the lane's fixture it exceeds it.
        result.Census.TopN.Should().Be(IndexAnalysisService.TopDatabasesByIo);
        result.Census.Sampled.Should().Be(Math.Min(result.Census.Eligible, result.Census.TopN));
        result.Census.UserDatabases.Should().Be(result.Census.Eligible + result.Census.Ineligible);
        (result.Scanned.Count + result.Skipped.Count).Should().BeGreaterThan(0);
    }

    /// <summary>
    /// The capture pass. Writes real reader shapes for the three queries plus a fourth read that
    /// forces the size LEFT JOIN to miss, which is the only way to obtain a genuine NULL SizeMB
    /// on demand — the row still comes from the real DMVs through the real provider, so the
    /// captured column keeps its true declared type and true null-ness.
    /// </summary>
    [LiveFact("INDEX_ANALYSIS_LIVE_TARGET", "INDEX_ANALYSIS_CAPTURE_OUT")]
    public async Task Capture_reader_shapes_for_the_offline_tests()
    {
        var target = RequireTarget();
        Assert.False(string.IsNullOrWhiteSpace(CaptureOut),
            "INDEX_ANALYSIS_CAPTURE_OUT is not set, so there is nowhere to write the capture. "
            + "It should have been SKIPPED by LiveFactAttribute.");

        // The same UnusedIndexSql with the size join forced to miss. Everything else is verbatim.
        // ⚠ ONE DEFINITION, 2026-08-14: the rewrite lives in ForceUnusedSizeJoinToMiss and is
        // shared with the index capture below. Two copies of a string surgery whose whole job is to
        // stay byte-identical to a query is how the two captures come to describe different SQL.
        var unusedNullSize = ForceUnusedSizeJoinToMiss(IndexAnalysisService.UnusedIndexSql);

        var capture = new Dictionary<string, object>
        {
            ["capturedUtc"] = DateTime.UtcNow.ToString("O"),
            ["target"] = target,
            ["database"] = Database,
        };

        await using var conn = new SqlConnection(ConnectionString(target, Database));
        await conn.OpenAsync();

        capture["serverVersion"] = conn.ServerVersion;
        capture["missing"] = await CaptureAsync(conn, IndexAnalysisService.MissingIndexSql);
        capture["unused"] = await CaptureAsync(conn, IndexAnalysisService.UnusedIndexSql);
        capture["fragmented"] = await CaptureAsync(conn, IndexAnalysisService.FragIndexSql);
        capture["unusedNullSize"] = await CaptureAsync(conn, unusedNullSize);
        capture["usageWindow"] = await CaptureAsync(conn, IndexAnalysisService.UsageWindowSql);

        // The ranking pass, both result sets. Captured from the real instance for the same reason
        // as the rest: MapRankedDatabase reads database_id, io_rank and is_read_only, and their
        // declared types come from a table variable rather than a DMV — a hand-written row would
        // simply agree with whatever the mapper assumed.
        var ranking = await CaptureMultiAsync(
            conn, IndexAnalysisService.DatabaseRankingSql,
            ("@top_n", IndexAnalysisService.TopDatabasesByIo),
            ("@skip_report_cap", IndexAnalysisService.TopDatabasesByIo));
        ranking.Should().HaveCount(2,
            "DatabaseRankingSql returns the candidate rows and then the census; if it stopped "
            + "returning two result sets the service's NextResult would be reading nothing");
        capture["dbRanking"] = ranking[0];
        capture["dbRankingCensus"] = ranking[1];

        var json = JsonSerializer.Serialize(capture, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(CaptureOut!)!);
        File.WriteAllText(CaptureOut!, json);
        _out.WriteLine($"captured to {CaptureOut} ({json.Length} bytes)");
    }

    /// <summary>
    /// THE SCRIPTS, RUN. 2026-08-14.
    ///
    /// <para>⚠⚠ THIS IS THE ONLY INSTRUMENT THAT CAN SETTLE IT. Both script builders wrote
    /// <c>[dbo]</c> as a literal, because the queries projected OBJECT_NAME and OBJECT_NAME returns
    /// a BARE table name — so every DROP INDEX and every ALTER INDEX the product emitted for a table
    /// outside dbo named an object that does not exist. No amount of string assertion settles that:
    /// a test can only compare the emitted text to text a test author wrote. The SERVER is the
    /// authority on whether a name resolves, so this asks it.</para>
    ///
    /// <para>Both directions are exercised. The OLD shape — the same statement with the schema
    /// replaced by the literal <c>dbo</c> — must be REJECTED, or the fixture is a table in dbo and
    /// the whole comparison is vacuous. The NEW shape must be ACCEPTED. Nothing survives: every
    /// statement runs inside a transaction that is always rolled back, and DDL in SQL Server is
    /// transactional, so the index is still there afterwards (asserted).</para>
    ///
    /// <para>FIXTURE: the database named by INDEX_ANALYSIS_LIVE_DB must hold a never-read index and
    /// a fragmented index on tables OUTSIDE dbo, or the negative half asserts nothing.</para>
    /// </summary>
    [LiveFact("INDEX_ANALYSIS_LIVE_TARGET")]
    public async Task The_emitted_scripts_name_objects_the_server_can_resolve()
    {
        var target = RequireTarget();
        var svc = new IndexAnalysisService();
        var result = await svc.GetAnalysisAsync(ConnectionString(target, Database));

        var unused = result.Unused.FirstOrDefault(u => u.Database == Database && u.Schema != "dbo");
        var frag = result.Fragmented.FirstOrDefault(f => f.Database == Database && f.Schema != "dbo");

        unused.Should().NotBeNull(
            $"'{Database}' must hold a never-read index on a table OUTSIDE dbo, or the old "
            + "hardcoded [dbo] was right for every row and this proves nothing");
        frag.Should().NotBeNull(
            $"'{Database}' must hold a fragmented index on a table OUTSIDE dbo, for the same reason");

        await using var conn = new SqlConnection(ConnectionString(target, Database));
        await conn.OpenAsync();

        // ⚠⚠ THESE RUN FOR REAL, and the first cut of this test did not -- which is the finding
        // worth keeping. `SET PARSEONLY ON` checks SYNTAX and never resolves a name, so the old
        // [dbo]-guessing script parses perfectly. `IF 1 = 0 <ddl>` was tried next and was WORSE
        // than useless: SQL Server defers name resolution for DDL in a never-executed branch, so
        // the wrong-schema DROP came back ACCEPTED and the negative control silently proved
        // nothing. Only EXECUTION binds. So the statements execute, inside a transaction that is
        // rolled back -- DDL in SQL Server is transactional, and the survival check below is what
        // makes that a measurement rather than a belief.
        // ⚠⚠ AND A ROLLBACK DOES NOT UNDO A REORGANIZE. Measured on .\new2022, 2026-08-14: an
        // ALTER INDEX ... REORGANIZE executed inside an explicit transaction that was then ROLLED
        // BACK left the index defragmented -- 97.39% before, 0.51% after -- and permanently
        // destroyed this harness's own fixture, which then had to be rebuilt. DROP INDEX IS fully
        // transactional (the survival assertion at the end of this method is the measurement).
        // So the REORGANIZE probes below run against the UNUSED row's tiny, already-tidy index:
        // the STATEMENT SHAPE and the three-part NAME are the maintenance script's, which is the
        // property under test, and reorganizing a tidy index changes nothing. The fragmented rows'
        // qualification is asserted as DATA rather than executed, for the same reason.
        frag!.Schema.Should().NotBe("dbo",
            "the maintenance script's real inputs must be qualified too. It is asserted rather "
            + "than executed because running its REORGANIZE would consume this fixture");

        var statements = new[]
        {
            ("new unused/DROP",
             $"DROP INDEX [{IndexAnalysisRendering.QuoteName(unused!.IndexName)}] ON "
             + IndexAnalysisRendering.ScriptTarget(unused.Database, unused.Schema, unused.Table) + ";",
             true),
            ("old unused/DROP with a guessed dbo",
             $"DROP INDEX [{IndexAnalysisRendering.QuoteName(unused.IndexName)}] ON "
             + IndexAnalysisRendering.ScriptTarget(unused.Database, "dbo", unused.Table) + ";",
             false),
            ("new maintenance/ALTER shape",
             $"ALTER INDEX [{IndexAnalysisRendering.QuoteName(unused.IndexName)}] ON "
             + IndexAnalysisRendering.ScriptTarget(unused.Database, unused.Schema, unused.Table) + " REORGANIZE;",
             true),
            ("old maintenance/ALTER shape with a guessed dbo",
             $"ALTER INDEX [{IndexAnalysisRendering.QuoteName(unused.IndexName)}] ON "
             + IndexAnalysisRendering.ScriptTarget(unused.Database, "dbo", unused.Table) + " REORGANIZE;",
             false),
        };

        foreach (var (label, sql, mustSucceed) in statements)
        {
            await using var tran = (SqlTransaction)await conn.BeginTransactionAsync();
            await using var cmd = new SqlCommand(sql, conn, tran) { CommandTimeout = 60 };

            var thrown = await Record.ExceptionAsync(() => cmd.ExecuteNonQueryAsync());
            await tran.RollbackAsync();

            _out.WriteLine($"[{label}] {sql}");
            _out.WriteLine($"    -> {(thrown is null ? "ACCEPTED" : "REJECTED: " + thrown.Message)}");

            if (mustSucceed)
                thrown.Should().BeNull(
                    $"the emitted script must name an object this server can resolve ({label})");
            else
                thrown!.Message.Should().Contain("does not exist",
                    $"the OLD shape must still be rejected BY NAME RESOLUTION ({label}). If the "
                    + "server accepts it, the fixture table is reachable as dbo after all and the "
                    + "comparison is vacuous; if it fails for some other reason, this test is "
                    + "measuring something else");
        }

        // The rollback really rolled back. Without this the whole probe could be passing while
        // quietly dropping the fixture's index, and the next run would fail for a reason that
        // looks like a product defect.
        await using var survived = new SqlCommand(
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(@t) AND name = @n", conn);
        survived.Parameters.AddWithValue("@t", $"{unused.Schema}.{unused.Table}");
        survived.Parameters.AddWithValue("@n", unused.IndexName);
        Convert.ToInt32(await survived.ExecuteScalarAsync()).Should().Be(1,
            "DDL is transactional, so the rolled-back DROP must have left the index in place");
    }

    /// <summary>
    /// THE INDEX-QUERY CAPTURE, 2026-08-14. Writes the reader shapes for the three index queries
    /// and their two forced-absence variants, from ONE database, so a change to any of those
    /// queries can be re-captured without rebuilding the ranking pass's estate fixture.
    ///
    /// <para>⚠⚠ THE TWO FORCED-ABSENCE VARIANTS ARE THE POINT, and neither is a hand-written row.
    /// <c>unusedNullSize</c> is the real unused query with its size LEFT JOIN forced to miss —
    /// which is how a NULL SizeMB is obtained on demand. <c>missingNullImpact</c> is the real
    /// missing query with each impact projection wrapped in <c>NULLIF(x, x)</c>, so the value is
    /// NULL while the CAST, the declared type and the nullability are the query's own. A hand-built
    /// reader would simply agree with whatever the mapper assumed, which is the failure this whole
    /// capture mechanism exists to prevent.</para>
    ///
    /// <para>FIXTURE CONTRACT — one database, named by <c>INDEX_ANALYSIS_LIVE_DB</c>, holding:
    /// a missing-index request the optimizer has actually costed; at least two never-read indexes
    /// that ARE written to, in two different schemas; an index over 5% fragmented across more than
    /// 1,000 pages; and at least one object OUTSIDE dbo, or nothing here can show that a script
    /// naming <c>[dbo]</c> is naming the wrong object. The lane that wrote this used
    /// <c>_sqlt_smalls_cap</c> on <c>.\new2022</c>, with schemas <c>rep</c> and <c>we]ird</c> — the
    /// second because a schema whose name needs quoting had never been exercised.</para>
    /// </summary>
    [LiveFact("INDEX_ANALYSIS_LIVE_TARGET", "INDEX_ANALYSIS_INDEX_CAPTURE_OUT")]
    public async Task Capture_index_query_shapes_for_the_offline_tests()
    {
        var target = RequireTarget();
        Assert.False(string.IsNullOrWhiteSpace(IndexCaptureOut),
            "INDEX_ANALYSIS_INDEX_CAPTURE_OUT is not set, so there is nowhere to write the capture. "
            + "It should have been SKIPPED by LiveFactAttribute.");

        var unusedNullSize = ForceUnusedSizeJoinToMiss(IndexAnalysisService.UnusedIndexSql);
        var missingNullImpact = ForceMissingImpactToNull(IndexAnalysisService.MissingIndexSql);

        await using var conn = new SqlConnection(ConnectionString(target, Database));
        await conn.OpenAsync();

        var capture = new Dictionary<string, object>
        {
            ["capturedUtc"] = DateTime.UtcNow.ToString("O"),
            ["target"] = target,
            ["database"] = Database,
            ["serverVersion"] = conn.ServerVersion,
            ["missing"] = await CaptureAsync(conn, IndexAnalysisService.MissingIndexSql),
            ["missingNullImpact"] = await CaptureAsync(conn, missingNullImpact),
            ["unused"] = await CaptureAsync(conn, IndexAnalysisService.UnusedIndexSql),
            ["unusedNullSize"] = await CaptureAsync(conn, unusedNullSize),
            ["fragmented"] = await CaptureAsync(conn, IndexAnalysisService.FragIndexSql),
        };

        var json = JsonSerializer.Serialize(capture, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(IndexCaptureOut!)!);
        File.WriteAllText(IndexCaptureOut!, json);
        _out.WriteLine($"captured to {IndexCaptureOut} ({json.Length} bytes)");
    }

    /// <summary>The real unused query with the size LEFT JOIN forced to miss. Everything else verbatim.</summary>
    private static string ForceUnusedSizeJoinToMiss(string sql)
    {
        var forced = sql.Replace(
            "LEFT JOIN sys.dm_db_partition_stats ps ON us.object_id = ps.object_id AND us.index_id = ps.index_id",
            "LEFT JOIN sys.dm_db_partition_stats ps ON us.object_id = ps.object_id AND us.index_id = ps.index_id AND 1 = 0",
            StringComparison.Ordinal);
        forced.Should().NotBe(sql, "the forced-miss variant must actually have been rewritten");
        return forced;
    }

    /// <summary>
    /// The real missing query with both impact projections forced to NULL — by
    /// <c>NULLIF(x, x)</c> over each expression, so the CAST and therefore the declared type and
    /// nullability are the query's own rather than something this harness asserted.
    /// </summary>
    private static string ForceMissingImpactToNull(string sql)
    {
        const string avgSource = "migs.avg_user_impact";
        const string scoreSource =
            "(migs.user_seeks + migs.user_scans) * migs.avg_total_user_cost * migs.avg_user_impact";

        var forced = sql
            .Replace($"CAST({avgSource} AS DECIMAL(6,2)) AS AvgImpact",
                     $"CAST(NULLIF({avgSource}, {avgSource}) AS DECIMAL(6,2)) AS AvgImpact",
                     StringComparison.Ordinal)
            .Replace($"CAST({scoreSource} AS DECIMAL(18,0)) AS ImpactScore",
                     $"CAST(NULLIF({scoreSource}, {scoreSource}) AS DECIMAL(18,0)) AS ImpactScore",
                     StringComparison.Ordinal);

        forced.Should().Contain("NULLIF(migs.avg_user_impact",
            "the AvgImpact projection must actually have been rewritten, or the capture holds a "
            + "measured value and every null assertion over it passes by never seeing a null");
        forced.Should().Contain("NULLIF((migs.user_seeks",
            "the ImpactScore projection must actually have been rewritten, for the same reason");
        return forced;
    }

    private static async Task<List<List<Dictionary<string, string?>>>> CaptureAsync(SqlConnection conn, string sql)
        => (await CaptureMultiAsync(conn, sql))[0];

    /// <summary>
    /// Captures EVERY result set a command returns. DatabaseRankingSql returns two, and a capture
    /// helper that silently kept only the first would have produced a fixture the service could
    /// not be tested against.
    /// </summary>
    private static async Task<List<List<List<Dictionary<string, string?>>>>> CaptureMultiAsync(
        SqlConnection conn, string sql, params (string Name, int Value)[] parameters)
    {
        var sets = new List<List<List<Dictionary<string, string?>>>>();

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        foreach (var (name, value) in parameters)
            cmd.Parameters.Add(name, System.Data.SqlDbType.Int).Value = value;

        await using var reader = await cmd.ExecuteReaderAsync();
        do
        {
            var rows = new List<List<Dictionary<string, string?>>>();
            while (await reader.ReadAsync())
            {
                var row = new List<Dictionary<string, string?>>();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var isNull = await reader.IsDBNullAsync(i);
                    row.Add(new Dictionary<string, string?>
                    {
                        ["name"] = reader.GetName(i),
                        ["sqlType"] = reader.GetDataTypeName(i),
                        ["clrType"] = reader.GetFieldType(i).FullName,
                        ["isNull"] = isNull ? "true" : "false",
                        ["value"] = isNull ? null : RoundTrip(reader.GetValue(i)),
                    });
                }
                rows.Add(row);
            }
            sets.Add(rows);
        }
        while (await reader.NextResultAsync());

        return sets;
    }

    /// <summary>Round-trippable rendering, so replay reconstructs the SAME boxed value.</summary>
    private static string RoundTrip(object v) => v switch
    {
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        IFormattable fo => fo.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

}
