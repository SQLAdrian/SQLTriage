/* In the name of God, the Merciful, the Compassionate */

// Pages lane, cluster 5 (2026-08-28) - the LIVE half of BestPracticeRunHonestyTests.
//
// The unit tests pin the wording and the colour for a given tally. What they cannot give is the
// fact the tally rests on: that the failure shapes the page catches really do throw where the page
// catches them, so a failed script really does land in the Failed column rather than being counted
// as a success. This drives real SqlConnection/SqlCommand through the exact sequence
// BestPractice.razor's ExecuteScript runs - CreateConnection("master"), OpenAsync,
// ExecuteReaderAsync, DataTable.Load - against a real instance and a real dead host, then feeds
// the real outcomes to the real prose functions the page calls.
//
// INERT in a normal `dotnet test` run: LiveFactAttribute computes Skip at discovery time, so every
// test here reports SKIPPED - not passed - unless PAGESBP_LIVE_TARGET names a reachable SQL
// instance. These three shipped with a plain Fact attribute and an arming early-return, which
// reports PASSED for three tests that measured nothing (lane9-04, 2026-08-28).
//
// INVOCATION (first live run 2026-08-28 against .\NEW2022, confirmed RUNNING by sc query):
//   $env:PAGESBP_LIVE_TARGET = ".\NEW2022"
//   dotnet test Tests/SQLTriage.Tests --filter "FullyQualifiedName~BestPracticeRunLive"
//
// Reads only: SELECT over sys.databases, and one deliberately malformed batch that never compiles.
// It writes nothing to any instance and opens no connection store, so the installed service's
// configuration is never touched.

using System;
using System.Data;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

public class BestPracticeRunLiveTests
{
    private const string DeadHost = "ZZHUNTNOSUCHHOST";

    private readonly ITestOutputHelper _out;

    public BestPracticeRunLiveTests(ITestOutputHelper output) => _out = output;

    private const string TargetVar = "PAGESBP_LIVE_TARGET";
    private static string? Target => Environment.GetEnvironmentVariable(TargetVar);

    /// <summary>
    /// The attribute is not the whole guard. If <see cref="LiveFactAttribute"/> is ever weakened or
    /// removed, an unarmed body must FAIL here rather than run past an empty target and pass.
    /// </summary>
    private static string RequireTarget()
    {
        var value = Environment.GetEnvironmentVariable(TargetVar);
        value.Should().NotBeNullOrWhiteSpace(
            $"{TargetVar} must name a reachable SQL instance for this harness to mean anything");
        return value!;
    }

    private static string ConnString(string server) =>
        $"Server={server};Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=5";

    /// <summary>
    /// The exact sequence BestPractice.razor's ExecuteScript runs, with the same catch boundary.
    /// Returns true only when the script produced a result without throwing - the value that
    /// feeds the run's Succeeded/Failed tally.
    /// </summary>
    private async Task<(bool Ok, int Rows, string? Error)> RunOneScript(string server, string sql)
    {
        try
        {
            using var conn = new SqlConnection(ConnString(server));
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 30;
            using var reader = await cmd.ExecuteReaderAsync();
            var table = new DataTable();
            table.Load(reader);
            return (true, table.Rows.Count, null);
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    [LiveFact(TargetVar)]
    public async Task A_run_against_an_unreachable_host_is_counted_as_failed_and_says_so()
    {
        RequireTarget();

        // The finding's own proved scenario, in miniature: every script fails because nothing can
        // be reached. Before this lane the panel's last line was a green "All scripts completed."
        var a = await RunOneScript(DeadHost, "SELECT name FROM sys.databases");
        var b = await RunOneScript(DeadHost, "SELECT name FROM sys.databases");

        a.Ok.Should().BeFalse();
        b.Ok.Should().BeFalse();
        _out.WriteLine($"connect failure: {a.Error}");

        var succeeded = (a.Ok ? 1 : 0) + (b.Ok ? 1 : 0);
        var tally = new BestPracticeRunTally(2, succeeded, 2 - succeeded);

        var line = BestPracticeRunReporting.DescribeCompletion(tally);
        _out.WriteLine($"panel closing line: {line}");

        line.Should().Contain("FAILED");
        BestPracticeRunReporting.CompletionIsSuccess(tally).Should().BeFalse();
    }

    [LiveFact(TargetVar)]
    public async Task A_syntax_error_throws_where_the_page_catches_it_so_no_stale_table_can_survive()
    {
        RequireTarget();

        // pages-r2-06's refinement, live: the stale-table defect needed a failure AT OR BEFORE
        // ExecuteReaderAsync. This proves the malformed batch really does throw there against a
        // real instance, which is the case the page must clear the grid for.
        var good = await RunOneScript(Target!, "SELECT name, database_id FROM sys.databases");
        good.Ok.Should().BeTrue("the live target must be reachable for this probe to mean anything");
        good.Rows.Should().BeGreaterThan(0);
        _out.WriteLine($"script A: OK, {good.Rows} row(s)");

        var bad = await RunOneScript(Target!, "SELECT FROM WHERE");
        bad.Ok.Should().BeFalse();
        _out.WriteLine($"script B: FAILED, {bad.Error}");

        // The run that follows: one succeeded, one failed. The old panel said "All scripts
        // completed." in green and rendered script A's 11 rows under script B's error.
        var tally = new BestPracticeRunTally(2, 1, 1);
        var line = BestPracticeRunReporting.DescribeCompletion(tally);
        _out.WriteLine($"panel closing line: {line}");

        line.Should().Contain("1 succeeded").And.Contain("1 failed");
        BestPracticeRunReporting.CompletionIsSuccess(tally).Should().BeFalse();
    }

    [LiveFact(TargetVar)]
    public async Task A_genuinely_clean_live_run_is_still_reported_as_clean()
    {
        RequireTarget();

        var a = await RunOneScript(Target!, "SELECT name FROM sys.databases");
        var b = await RunOneScript(Target!, "SELECT database_id FROM sys.databases");

        a.Ok.Should().BeTrue();
        b.Ok.Should().BeTrue();

        var tally = new BestPracticeRunTally(2, 2, 0);
        _out.WriteLine($"panel closing line: {BestPracticeRunReporting.DescribeCompletion(tally)}");

        BestPracticeRunReporting.CompletionIsSuccess(tally).Should().BeTrue();

        // Both scripts returned rows, so the panel must disclose that it keeps only the last set.
        BestPracticeRunReporting.DescribeRetainedResults(2).Should().NotBeNull();
    }
}
