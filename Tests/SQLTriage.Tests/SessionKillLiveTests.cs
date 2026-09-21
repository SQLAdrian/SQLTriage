/* In the name of God, the Merciful, the Compassionate */

// Pages lane, cluster 7 (2026-08-28) - the LIVE half of pages-r1-10.
//
// The unit tests pin the wording for a given outcome. What they cannot give is the fact the whole
// finding rests on: that KILL returns while the session is still there, so a page that announces
// success on the next line is announcing something that has not happened. Both verdict passes
// proved this on .\new2022 at hunt time - KILL returned in 3-6 ms against a session that then sat
// at status='rollback' for minutes. This re-runs that probe against the FIXED code path: it builds
// a real rollback, issues a real KILL, re-reads through the service's own shipped SQL
// (SessionDataService.SessionAfterKillSql), builds the outcome through the service's own
// BuildKillOutcome, and asserts the sentence the page would now show.
//
// INERT in a normal `dotnet test` run: LiveFactAttribute computes Skip at discovery time, so
// every test here reports SKIPPED - not passed - unless PAGESKILL_LIVE_TARGET names a reachable
// SQL instance.
//
// lane9-04 (2026-08-28): these three shipped with a plain Fact attribute and an arming
// early-return, so an unarmed CI run reported "Passed: 3" for three tests that measured
// nothing - the exact class
// this wave exists to close, inside the wave itself. LiveFactAttribute is the house idiom and
// VaCoverageHonestyLiveTests in this same lane already used it.
//
// INVOCATION (first live run 2026-08-28 against .\NEW2022, confirmed RUNNING by sc query):
//   $env:PAGESKILL_LIVE_TARGET = ".\NEW2022"
//   dotnet test Tests/SQLTriage.Tests --filter "FullyQualifiedName~SessionKillLive"
//
// WRITES: creates and drops one table in TEMPDB only (ZZHuntKill_<guid>), inside a transaction
// that is then killed. Nothing outside tempdb is touched, no user database is opened, and the
// installed service's connection store is never read.

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace SQLTriage.Tests;

public class SessionKillLiveTests
{
    private readonly ITestOutputHelper _out;

    public SessionKillLiveTests(ITestOutputHelper output) => _out = output;

    private const string TargetVar = "PAGESKILL_LIVE_TARGET";
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
        $"Server={server};Database=tempdb;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=5";

    [LiveFact(TargetVar)]
    public async Task A_kill_issued_against_a_long_rollback_is_not_reported_as_killed()
    {
        RequireTarget();

        var table = "ZZHuntKill_" + Guid.NewGuid().ToString("N")[..8];

        // ── Victim session: build real work to undo, inside an open transaction ──
        using var victim = new SqlConnection(ConnString(Target!));
        await victim.OpenAsync();

        var spid = (int)(short)await Scalar(victim, "SELECT @@SPID");
        _out.WriteLine($"victim SPID = {spid}");

        await Exec(victim, $@"
SELECT TOP (400000) IDENTITY(INT,1,1) AS id, CAST(REPLICATE('x', 200) AS CHAR(200)) AS pad
INTO tempdb.dbo.{table}
FROM sys.all_columns a CROSS JOIN sys.all_columns b;", timeout: 120);

        await Exec(victim, $"BEGIN TRANSACTION; UPDATE tempdb.dbo.{table} SET pad = REPLICATE('y', 200);", timeout: 120);

        var open = Convert.ToInt32(await Scalar(victim,
            "SELECT COUNT(*) FROM sys.dm_tran_session_transactions WHERE session_id = @@SPID"));
        open.Should().BeGreaterThan(0, "the probe needs a genuinely open transaction to roll back");
        _out.WriteLine($"open transactions on victim: {open}");

        // ── Killer session: the exact sequence KillSessionAsync now runs ──
        using var killer = new SqlConnection(ConnString(Target!));
        await killer.OpenAsync();

        var before = DateTime.UtcNow;
        using (var kill = new SqlCommand($"KILL {spid}", killer) { CommandTimeout = 30 })
            await kill.ExecuteNonQueryAsync();
        var killMs = (DateTime.UtcNow - before).TotalMilliseconds;
        _out.WriteLine($"KILL returned in {killMs:F1} ms");

        // Re-read through the SERVICE'S OWN shipped SQL and outcome builder.
        SessionKillOutcome outcome;
        using (var check = new SqlCommand(SessionDataService.SessionAfterKillSql, killer) { CommandTimeout = 10 })
        {
            check.Parameters.AddWithValue("@Spid", spid);
            using var reader = await check.ExecuteReaderAsync();
            outcome = await reader.ReadAsync()
                ? SessionDataService.BuildKillOutcome(
                    spid, true,
                    reader.IsDBNull(1) ? null : reader.GetValue(1),
                    reader.IsDBNull(2) ? null : reader.GetValue(2),
                    reader.IsDBNull(3) ? null : reader.GetValue(3))
                : SessionDataService.BuildKillOutcome(spid, false, null, null, null);
        }

        // Diagnostic only (does not change the service's single immediate read): sample the DMVs
        // for a few seconds so the probe's log records whether the rollback state appeared at all.
        // The finding's own passes saw status='rollback' immediately and again 2 seconds later.
        for (var i = 0; i < 6; i++)
        {
            using var peek = new SqlCommand(SessionDataService.SessionAfterKillSql, killer) { CommandTimeout = 10 };
            peek.Parameters.AddWithValue("@Spid", spid);
            using var pr = await peek.ExecuteReaderAsync();
            if (await pr.ReadAsync())
                _out.WriteLine($"  sample {i}: present, status={(pr.IsDBNull(1) ? "(no request)" : pr.GetValue(1))}, "
                             + $"percent={(pr.IsDBNull(2) ? "-" : pr.GetValue(2))}, "
                             + $"etaMs={(pr.IsDBNull(3) ? "-" : pr.GetValue(3))}");
            else
                _out.WriteLine($"  sample {i}: GONE");
            await Task.Delay(400);
        }

        var sentence = SessionKillReporting.Describe(outcome);
        _out.WriteLine($"StillPresent={outcome.StillPresent} status={outcome.RequestStatus} "
                     + $"percent={outcome.PercentComplete} etaSeconds={outcome.EstimatedSecondsRemaining}");
        _out.WriteLine($"TOAST WOULD READ: {sentence}");
        _out.WriteLine($"painted as success: {SessionKillReporting.IsSuccess(outcome)}");

        // The finding, re-proved against the fix. The old page said "Session N killed." here.
        if (outcome.StillPresent)
        {
            sentence.Should().NotContain($"Session {spid} killed.");
            SessionKillReporting.IsSuccess(outcome).Should()
                .BeFalse("a session still on the server has not been killed yet");
        }
        else
        {
            // The rollback finished inside the round-trip. Then "killed." is the TRUE sentence,
            // and reporting it is correct - which is the other half of the honesty contract.
            _out.WriteLine("NOTE: rollback completed within the confirming read; 'killed.' is accurate here.");
            sentence.Should().Be($"Session {spid} killed.");
        }

        await Cleanup(Target!, table);
    }

    [LiveFact(TargetVar)]
    public async Task A_session_that_never_existed_is_reported_gone_by_the_same_query()
    {
        RequireTarget();

        // The control. Without it, a query that always returned zero rows would satisfy the test
        // above for the wrong reason.
        using var conn = new SqlConnection(ConnString(Target!));
        await conn.OpenAsync();

        using var check = new SqlCommand(SessionDataService.SessionAfterKillSql, conn) { CommandTimeout = 10 };
        check.Parameters.AddWithValue("@Spid", 32760); // no such session

        using var reader = await check.ExecuteReaderAsync();
        var found = await reader.ReadAsync();
        found.Should().BeFalse();

        var outcome = SessionDataService.BuildKillOutcome(32760, found, null, null, null);
        _out.WriteLine($"TOAST WOULD READ: {SessionKillReporting.Describe(outcome)}");

        outcome.GoneConfirmed.Should().BeTrue();
        SessionKillReporting.IsSuccess(outcome).Should().BeTrue();
    }

    [LiveFact(TargetVar)]
    public async Task A_live_session_that_was_never_killed_is_reported_as_still_present()
    {
        RequireTarget();

        // The second control: the query must genuinely find a session that IS there, or the
        // "still present" branch above could never be reached and its assertion would be vacuous.
        using var conn = new SqlConnection(ConnString(Target!));
        await conn.OpenAsync();
        var spid = (int)(short)await Scalar(conn, "SELECT @@SPID");

        using var check = new SqlCommand(SessionDataService.SessionAfterKillSql, conn) { CommandTimeout = 10 };
        check.Parameters.AddWithValue("@Spid", spid);
        using var reader = await check.ExecuteReaderAsync();

        (await reader.ReadAsync()).Should().BeTrue("this very session is connected");
    }

    private static async Task<object> Scalar(SqlConnection conn, string sql)
    {
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        return (await cmd.ExecuteScalarAsync())!;
    }

    private static async Task Exec(SqlConnection conn, string sql, int timeout)
    {
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = timeout };
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task Cleanup(string server, string table)
    {
        try
        {
            using var conn = new SqlConnection(ConnString(server));
            await conn.OpenAsync();
            await Exec(conn, $"IF OBJECT_ID('tempdb.dbo.{table}') IS NOT NULL DROP TABLE tempdb.dbo.{table};", 60);
            _out.WriteLine($"cleanup: dropped tempdb.dbo.{table} (or it was already gone)");
        }
        catch (Exception ex) { _out.WriteLine($"cleanup note: {ex.Message}"); }
    }
}
