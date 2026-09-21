/* In the name of God, the Merciful, the Compassionate */

// Pages lane, cluster 4 (2026-08-28) - silent data and diagnostic loss in the dbatools runner.
//
//   pages-r1-09  The results grid and its CSV export built their column set from the FIRST JSON
//                object only and then ran `if (!dt.Columns.Contains(prop.Name)) continue;` over
//                every later object, so a heterogeneous command's extra columns and values were
//                dropped from the screen AND from the exported file with no warning, no
//                ParseError, and therefore no raw-text fallback. Re-proved at HEAD 2026-08-28
//                through the app's own host and argument shape (powershell.exe -NoProfile -NoLogo
//                -NonInteractive -Command -): `@([pscustomobject]@{A=1}, [pscustomobject]@{A=2;B=3})
//                | ConvertTo-Json -Depth 4 -Compress` exits 0 with stdout exactly
//                `[{"A":1},{"A":2,"B":3}]`. That string is the fixture below.
//
//   pages-r2-05  ExecuteAsDataTableAsync returns early whenever the child exits 0 with blank
//                stdout, and the page read result.Error ONLY on the failure branch - so a
//                dbatools diagnostic on standard error was discarded and the screen said
//                "No results returned." beside "Completed in Nms". Re-proved at HEAD 2026-08-28:
//                a script writing only to stderr returned EXIT=0, stdout 0 bytes, stderr 31 bytes.
//
//   pages-r1-05  CLOSED by the platform lane (4c9a1f2) before this lane opened; this lane's brief
//                had verified that by READ only. The empty-folder case is exercised here against
//                a real directory so the claim stops resting on a diff.
//
// Unit tests over the parser and the measured-prose functions, plus LINTS over the shipped
// markup: which sentence /dbatools renders is a fact about the .razor, and a later edit could
// restore the bare "No results returned." with every C# test still green.

using System;
using System.Data;
using System.IO;
using System.Linq;
using FluentAssertions;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests;

public class DbatoolsRunHonestyTests
{
    // The exact stdout the app's own host produced for the finding's own command, twice: at hunt
    // time and again at HEAD on 2026-08-28. Not a hand-written approximation of it.
    private const string HeterogeneousJson = @"[{""A"":1},{""A"":2,""B"":3}]";

    // ── pages-r1-09: the grid carries what the command returned ──────────────

    [Fact]
    public void A_property_missing_from_the_first_object_still_reaches_the_grid()
    {
        var dt = PowerShellService.ParseJsonToDataTable(HeterogeneousJson, out var dropped);

        // The whole defect in one assertion: B existed in the output and was not in the table.
        dt.Columns.Cast<DataColumn>().Select(c => c.ColumnName)
            .Should().Equal("A", "B");
        dropped.Should().BeEmpty();
    }

    [Fact]
    public void The_value_under_that_property_reaches_the_grid_too()
    {
        // A column header with an empty column under it would be a second, quieter version of the
        // same defect, so the VALUE is asserted, not just the header.
        var dt = PowerShellService.ParseJsonToDataTable(HeterogeneousJson, out _);

        dt.Rows.Count.Should().Be(2);
        dt.Rows[1]["B"].Should().Be("3");
    }

    [Fact]
    public void An_object_that_did_not_carry_the_property_renders_empty_not_invented()
    {
        // The cure for a dropped column must not be a fabricated value in its place. Object one
        // genuinely had no B; the honest rendering of that is blank.
        var dt = PowerShellService.ParseJsonToDataTable(HeterogeneousJson, out _);

        dt.Rows[0]["B"].ToString().Should().BeEmpty();
    }

    [Fact]
    public void Column_order_follows_first_appearance_so_the_familiar_shape_is_unchanged()
    {
        // A fix that reorders every existing dbatools preset's columns would be a regression the
        // operator sees on every run. First-seen order keeps the common (homogeneous) case
        // byte-identical to what it always rendered.
        var dt = PowerShellService.ParseJsonToDataTable(
            @"[{""Name"":""a"",""Size"":1},{""Size"":2,""Name"":""b"",""Owner"":""sa""}]", out _);

        dt.Columns.Cast<DataColumn>().Select(c => c.ColumnName)
            .Should().Equal("Name", "Size", "Owner");
    }

    [Fact]
    public void A_property_appearing_only_in_the_last_object_of_many_still_reaches_the_grid()
    {
        // The union has to be over ALL objects, not "the first two". A dbatools result set where
        // only the final row carries an extra field is the realistic shape of this defect.
        var dt = PowerShellService.ParseJsonToDataTable(
            @"[{""A"":1},{""A"":2},{""A"":3},{""A"":4,""Z"":9}]", out _);

        dt.Columns.Contains("Z").Should().BeTrue();
        dt.Rows[3]["Z"].Should().Be("9");
    }

    [Fact]
    public void A_single_object_result_still_parses()
    {
        // ConvertTo-Json emits a bare object, not an array, for a one-item result. That path used
        // a separate wrapper helper before this fix; this pins that it survived.
        var dt = PowerShellService.ParseJsonToDataTable(@"{""A"":1,""B"":""x""}", out _);

        dt.Rows.Count.Should().Be(1);
        dt.Rows[0]["B"].Should().Be("x");
    }

    [Fact]
    public void Non_object_entries_do_not_become_phantom_rows()
    {
        // A mixed array must not manufacture a row for a scalar - a row count is a claim about
        // how many records the command returned.
        var dt = PowerShellService.ParseJsonToDataTable(@"[{""A"":1},5,null,{""A"":2}]", out _);

        dt.Rows.Count.Should().Be(2);
    }

    [Fact]
    public void An_empty_array_is_zero_rows_and_not_an_error()
    {
        var dt = PowerShellService.ParseJsonToDataTable("[]", out var dropped);

        dt.Rows.Count.Should().Be(0);
        dropped.Should().BeEmpty();
    }

    // ── pages-r2-05: the empty state says which kind of empty ────────────────

    [Fact]
    public void Exit_zero_with_a_stderr_diagnostic_is_not_described_as_no_results()
    {
        // The proved state: EXIT=0, stdout 0 bytes, stderr non-empty. Before this lane the page
        // rendered "No results returned." here.
        var result = new PowerShellResult
        {
            Success = true, ExitCode = 0, Output = "",
            Error = "could not connect to instance"
        };

        var note = PowerShellRunReporting.DescribeEmptyResult(result);

        note.Should().NotBeNull();
        note!.Should().NotContain("No results returned");
        note.Should().Contain("standard error");
    }

    [Fact]
    public void That_same_state_flags_the_diagnostic_as_needing_a_render_site()
    {
        PowerShellRunReporting.HasUnreportedDiagnostic(new PowerShellResult
        {
            Success = true, ExitCode = 0, Output = "", Error = "Access denied"
        }).Should().BeTrue();
    }

    [Fact]
    public void A_genuinely_silent_run_is_described_as_genuinely_silent()
    {
        // Honesty runs both ways: when nothing was written to either stream, the page must not
        // imply a hidden diagnostic exists.
        var note = PowerShellRunReporting.DescribeEmptyResult(new PowerShellResult
        {
            Success = true, ExitCode = 0, Output = "", Error = ""
        });

        note.Should().NotBeNull();
        note!.Should().Contain("no diagnostic on either stream");
    }

    [Fact]
    public void A_run_that_produced_output_gets_no_empty_state_sentence()
    {
        PowerShellRunReporting.DescribeEmptyResult(new PowerShellResult
        {
            Success = true, ExitCode = 0, Output = HeterogeneousJson, Error = ""
        }).Should().BeNull();
    }

    [Fact]
    public void A_failed_run_is_left_to_the_error_panel()
    {
        // The error panel already reads result.Error on this branch. A second sentence saying the
        // same thing is noise, not disclosure.
        PowerShellRunReporting.DescribeEmptyResult(new PowerShellResult
        {
            Success = false, ExitCode = 1, Output = "", Error = "boom"
        }).Should().BeNull();

        PowerShellRunReporting.HasUnreportedDiagnostic(new PowerShellResult
        {
            Success = false, ExitCode = 1, Output = "", Error = "boom"
        }).Should().BeFalse();
    }

    [Fact]
    public void Whitespace_only_stdout_counts_as_blank()
    {
        // result.Output of "\r\n" is what a host that printed nothing but a newline leaves. The
        // service's own early-return uses IsNullOrWhiteSpace; the describer must agree with it or
        // the two disagree about which state the page is in.
        PowerShellRunReporting.DescribeEmptyResult(new PowerShellResult
        {
            Success = true, ExitCode = 0, Output = "  \r\n ", Error = "diagnostic here"
        }).Should().NotBeNull();
    }

    [Fact]
    public void Dropped_columns_are_named_when_there_are_any_and_silent_when_there_are_none()
    {
        PowerShellRunReporting.DescribeDroppedColumns(new PowerShellResult()).Should().BeNull();

        var note = PowerShellRunReporting.DescribeDroppedColumns(new PowerShellResult
        {
            DroppedColumns = { "Alpha", "Beta" }
        });
        note.Should().NotBeNull();
        note!.Should().Contain("Alpha").And.Contain("Beta").And.Contain("CSV");
    }

    // ── pages-r1-05 (CLOSED by 4c9a1f2): exercised, not read ─────────────────

    [Fact]
    public void An_empty_dbatools_folder_does_not_count_as_the_module_being_present()
    {
        // This is the finding's own repro state - the debris Directory.CreateDirectory leaves
        // when Save-Module then fails - against a REAL directory, not a mocked filesystem. The
        // brief that opened this lane had verified the platform lane's fix by read/diff only.
        var dir = Path.Combine(Path.GetTempPath(), "sqltriage-pages-dbatools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Directory.GetFileSystemEntries(dir).Should().BeEmpty("the probe state is an EMPTY folder");

            PowerShellService.DbatoolsFolderHasModule(dir).Should().BeFalse();
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void A_folder_holding_the_module_does_count()
    {
        // The other half: the fix must not have closed the false tick by breaking the true one.
        var dir = Path.Combine(Path.GetTempPath(), "sqltriage-pages-dbatools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "dbatools"));
        try
        {
            PowerShellService.DbatoolsFolderHasModule(dir).Should().BeTrue();
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void A_folder_that_does_not_exist_does_not_count()
    {
        PowerShellService.DbatoolsFolderHasModule(
            Path.Combine(Path.GetTempPath(), "sqltriage-pages-absent-" + Guid.NewGuid().ToString("N")))
            .Should().BeFalse();
    }

    // ── Markup lints: the sentence the page renders lives in the .razor ──────

    private static string Markup(string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Markup", file));

    [Fact]
    public void The_page_renders_the_measured_empty_state_not_a_flat_claim()
    {
        var markup = Markup("DbaTools.razor");

        markup.Should().Contain("PowerShellRunReporting.DescribeEmptyResult",
            "the empty state must be computed from the same result the grid is");

        // Not a bare Contains("_emptyStateNote") - that passes on the FIELD DECLARATION alone, so
        // deleting the note from the render site and leaving the field behind kept it green.
        // Proved by mutation on 2026-08-28. The assertion is that the empty-state block RENDERS it.
        var emptyBlockAt = markup.IndexOf("fa-solid fa-inbox", StringComparison.Ordinal);
        emptyBlockAt.Should().BeGreaterThan(0, "the empty state block must still exist");
        var emptyBlock = markup.Substring(emptyBlockAt, Math.Min(400, markup.Length - emptyBlockAt));

        emptyBlock.Should().Contain("_emptyStateNote",
            "the empty state must render the measured sentence, not a flat literal");
    }

    [Fact]
    public void The_page_has_a_render_site_for_standard_error_on_the_success_path()
    {
        // pages-r2-05's root: result.Error was read only inside the failure branch, so on a
        // success-with-diagnostic run it had nowhere on screen to go.
        var markup = Markup("DbaTools.razor");

        markup.Should().Contain("HasUnreportedDiagnostic");
        markup.Should().Contain("_diagnosticOutput");
        markup.Should().Contain("Diagnostics (standard error)");
    }

    [Fact]
    public void The_diagnostic_is_computed_before_the_success_branch()
    {
        // If the three lines that compute the disclosure were moved inside `if (result.Success)`'s
        // else, the defect returns with every other test in this file still green.
        var markup = Markup("DbaTools.razor");

        var describeAt = markup.IndexOf("DescribeEmptyResult", StringComparison.Ordinal);
        var successBranchAt = markup.IndexOf("if (result.Success)", StringComparison.Ordinal);

        describeAt.Should().BeGreaterThan(0);
        successBranchAt.Should().BeGreaterThan(0);
        describeAt.Should().BeLessThan(successBranchAt,
            "the discarded diagnostic lives on the SUCCESS path, so it must be read before the branch");
    }
}
