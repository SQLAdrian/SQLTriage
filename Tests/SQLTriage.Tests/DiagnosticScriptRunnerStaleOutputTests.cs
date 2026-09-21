/* In the name of God, the Merciful, the Compassionate */

// THE STALE-OUTPUT GUARD.
//
// Until 2026-08-23 DiagnosticScriptRunner treated an ExecutionParameters failure as a WARNING:
// it recorded the message, carried on to SqlQueryForOutput, exported the rows, and set
// Success = true. The shipped sp_Blitz entry's output query reads a PERSISTED table by its
// latest timestamp -- WHERE CheckDate = (SELECT MAX(CheckDate) ...) -- so on any server where
// the EXEC failed, the app exported the PREVIOUS run's audit under today's file name and
// reported it as a success. Stale data presented as fresh, and presented precisely on the
// servers where something was wrong.
//
// The FRK 8.34 refresh made that state reachable in a new way: sp_Blitz 8.34 requires
// dbo.sp_ineachdb, which arrives from its own config entry, and an install that keeps an older
// Config/script-configurations.json (installer/SQLTriage.iss uses onlyifdoesntexist) or an
// operator who unticks sp_ineachdb in Full Audit leaves sp_Blitz with a missing prerequisite.
//
// This file measures the DECISION in CI. The end-to-end proof -- Success false, no rows, no CSV
// on a real instance -- is FrkLiveSmokeTests.A_failed_execution_never_exports_the_previous_run,
// which is skipped unless FRK_LIVE_TARGET is set. A rule measured only by a skipped test is not
// measured, which is why the decision is a testable member rather than an inline expression.

using System;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using Xunit;

namespace SQLTriage.Tests;

public class DiagnosticScriptRunnerStaleOutputTests
{
    /// <summary>
    /// The whole truth table, all eight combinations, stated explicitly rather than derived --
    /// a test that recomputes the expression it is testing agrees with any version of it.
    /// </summary>
    [Theory]
    // execParamsFailed = true: NEVER, whatever else is true. These four are the fix.
    [InlineData(true,  true,  true,  false)]
    [InlineData(true,  true,  false, false)]
    [InlineData(true,  false, true,  false)]
    [InlineData(true,  false, false, false)]
    // execParamsFailed = false: the pre-existing behaviour, unchanged.
    [InlineData(false, true,  true,  true)]
    [InlineData(false, true,  false, true)]
    [InlineData(false, false, true,  true)]   // ToRun=0 + ExportToCsv: the deliberate "load previous execution" path
    [InlineData(false, false, false, false)]
    public void The_output_query_runs_only_when_this_run_produced_the_rows(
        bool execParamsFailed, bool shouldRunExecParams, bool exportToCsv, bool expected)
    {
        Assert.Equal(expected,
            DiagnosticScriptRunner.ShouldRunOutputQuery(execParamsFailed, shouldRunExecParams, exportToCsv));
    }

    /// <summary>
    /// The one clause that is the defect, said on its own so a failure names it. If this goes red,
    /// a failed execution can read the output table again, and for sp_Blitz that table holds the
    /// last successful audit.
    /// </summary>
    [Fact]
    public void A_failed_execution_never_reads_the_output_table()
    {
        Assert.False(DiagnosticScriptRunner.ShouldRunOutputQuery(
            execParamsFailed: true, shouldRunExecParams: true, exportToCsv: true),
            "A run whose ExecutionParameters threw is about to read a table it did not write. For the "
            + "shipped sp_Blitz entry that is the PREVIOUS audit, and it would be exported to CSV "
            + "under today's file name with Success = true.");
    }

    // -- WHICH RESULT SET IS THE OUTPUT, and what happens when it is not there -------------------
    //
    // Added 2026-08-24 with the sp_PerfCheck bundle. Until then the runner took the FIRST result
    // set of SqlQueryForOutput and ignored anything after it, which is right for every entry whose
    // output query is a single SELECT against a table the run just wrote. sp_PerfCheck has no
    // output table at all: the EXEC is the output query, and it hands back a two-column server
    // banner and THEN the nine-column findings. Capturing set 0 would export the banner under the
    // name of a performance audit, with Success = true and a CSV that parses.
    //
    // The decision is a testable member for the same reason ShouldRunOutputQuery is: the live proof
    // needs a real instance and a real two-set procedure, and a rule measured only by a skipped
    // test is not measured. The reader here is a real DbDataReader (DataSet.CreateDataReader), so
    // this exercises NextResultAsync rather than a stand-in for it.

    private static DbDataReader ReaderWith(int resultSets)
    {
        var set = new DataSet();
        for (var i = 0; i < resultSets; i++)
        {
            var table = new DataTable("t" + i);
            table.Columns.Add("col" + i, typeof(string));
            table.Rows.Add("row in set " + i);
            set.Tables.Add(table);
        }
        return set.CreateDataReader();
    }

    [Theory]
    // index 0 is what every entry that predates OutputResultSetIndex does: take the set the reader
    // already sits on, however many follow it.
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    // index 1 is the sp_PerfCheck shape: skip the banner, land on the findings.
    [InlineData(1, 2)]
    [InlineData(1, 3)]
    [InlineData(2, 3)]
    public async Task The_reader_lands_on_the_configured_result_set(int index, int available)
    {
        using var reader = ReaderWith(available);

        await DiagnosticScriptRunner.AdvanceToOutputResultSetAsync(
            reader, index, "fixture", CancellationToken.None);

        Assert.True(await reader.ReadAsync());
        Assert.Equal("col" + index, reader.GetName(0));
        Assert.Equal("row in set " + index, reader.GetString(0));
    }

    [Theory]
    // Asking for a set the query did not return. Falling back to an earlier one would export the
    // wrong columns under this script's name, so the run has to fail instead.
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 2)]
    public async Task An_absent_result_set_fails_the_run_instead_of_exporting_the_wrong_one(
        int index, int available)
    {
        using var reader = ReaderWith(available);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DiagnosticScriptRunner.AdvanceToOutputResultSetAsync(
                      reader, index, "sp_Fixture", CancellationToken.None));

        // ExecuteScriptAsync's generic catch composes this into ErrorMessage and leaves
        // Success = false, so the operator sees an Error and not a plausible CSV.
        Assert.Contains("sp_Fixture", ex.Message);
        Assert.Contains("result set " + index, ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task The_rows_the_runner_keeps_come_from_the_configured_set(int index)
    {
        // ReadOutputRowsAsync is the member ExecuteScriptAsync actually calls, and it advances and
        // reads in one step. That is the point of it: when the advance was a separate call at the
        // call site, deleting that call left every test in this file green while the runner
        // exported the wrong result set. Measured 2026-08-24; only the live harness caught it.
        using var reader = ReaderWith(3);

        var rows = await DiagnosticScriptRunner.ReadOutputRowsAsync(
            reader, index, "fixture", CancellationToken.None);

        Assert.Single(rows);
        Assert.Equal("col" + index, rows[0].Keys.Single());
        Assert.Equal("row in set " + index, rows[0]["col" + index]);
    }

    [Fact]
    public async Task Reading_an_absent_set_returns_no_rows_at_all_because_it_throws()
    {
        using var reader = ReaderWith(1);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => DiagnosticScriptRunner.ReadOutputRowsAsync(
                      reader, 1, "sp_Fixture", CancellationToken.None));
    }

    [Fact]
    public async Task A_negative_result_set_index_names_no_set_and_is_refused()
    {
        using var reader = ReaderWith(2);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DiagnosticScriptRunner.AdvanceToOutputResultSetAsync(
                      reader, -1, "sp_Fixture", CancellationToken.None));

        Assert.Contains("counted from 0", ex.Message);
    }

    // -- ZERO ROWS IS NOT ALWAYS A PROBLEM -------------------------------------------------------
    //
    // Full Audit raises a "No-Rows" issue when a CSV-exporting script succeeds with no rows. For
    // sp_Blitz and sp_triage that is right: their output query reads a table the run just wrote, so
    // an empty result means the export broke. sp_PerfCheck emits one row per problem FOUND, so an
    // empty result is the best answer a server can give. Flagging it every run would train the
    // operator to close the post-run modal without reading it, which costs more than it saves.

    private static ScriptConfiguration Cfg(bool exportToCsv, bool emptyIsNormal) =>
        new() { Name = "fixture", ExportToCsv = exportToCsv, EmptyResultIsNormal = emptyIsNormal };

    [Theory]
    // exportToCsv, emptyIsNormal, rows, expected
    [InlineData(true, false, 0, true)]    // sp_Blitz with an empty export: the pre-existing warning
    [InlineData(true, false, 5, false)]
    [InlineData(true, true, 0, false)]    // sp_PerfCheck on a healthy instance
    [InlineData(true, true, 5, false)]
    [InlineData(false, false, 0, false)]  // install-only entries never export, so never warn
    [InlineData(false, true, 0, false)]
    public void The_no_rows_warning_asks_whether_empty_means_broken(
        bool exportToCsv, bool emptyIsNormal, int rows, bool expected)
    {
        Assert.Equal(expected,
            DiagnosticScriptRunner.ShouldRaiseNoRowsIssue(Cfg(exportToCsv, emptyIsNormal), rows));
    }

    [Fact]
    public void A_script_with_no_configuration_is_never_flagged()
    {
        // FullAudit looks the config up by name and can miss; a null there must not become a
        // NullReferenceException inside the post-run modal.
        Assert.False(DiagnosticScriptRunner.ShouldRaiseNoRowsIssue(null, 0));
    }

    [Fact]
    public void The_shipped_sp_PerfCheck_entry_does_not_warn_on_a_healthy_instance()
    {
        // Through the REAL shipped config rather than a fixture, so flipping EmptyResultIsNormal to
        // false in Config/script-configurations.json turns this red. The truth table above would
        // not: it builds its own configurations and would agree with any shipped file.
        var entry = new ScriptConfiguration
        {
            Name = DarlingContractTests.ConfigEntryName,
            ExportToCsv = DarlingContractTests.Entry().ExportToCsv,
            EmptyResultIsNormal = DarlingContractTests.Entry().EmptyResultIsNormal,
        };

        Assert.False(DiagnosticScriptRunner.ShouldRaiseNoRowsIssue(entry, 0),
            "sp_PerfCheck found nothing wrong with the server and Full Audit is about to tell the "
            + "operator that the tooling is broken.");
    }

    [Fact]
    public void The_shipped_sp_Blitz_entry_still_warns_on_an_empty_export()
    {
        // The other half of the same claim: the new flag must not have loosened the check for the
        // entries it was never meant to change.
        var blitz = DarlingContractTests.Configurations().Single(c => c.ScriptPath == "sp_Blitz.sql");

        var entry = new ScriptConfiguration
        {
            Name = blitz.Name,
            ExportToCsv = blitz.ExportToCsv,
            EmptyResultIsNormal = blitz.EmptyResultIsNormal,
        };

        Assert.True(DiagnosticScriptRunner.ShouldRaiseNoRowsIssue(entry, 0));
    }
}
