/* In the name of God, the Merciful, the Compassionate */

// platform-r1-04: the headless --audit run's final stdout line printed an unqualified
// "complete. … result(s) written to …" even when WriteOutputs returned exportFailed=true (a
// requested JSON/CSV/PDF artifact actually failed to write). The exit code was already correctly
// non-zero, but a human or a scheduled-task transcript that reads only stdout saw success. The line
// now states the failure and stops claiming the results were written.

using System;
using System.Collections.Generic;
using SQLTriage.Cli;
using Xunit;

namespace SQLTriage.Tests.Cli;

public class CliAuditCompletionLineTests
{
    private static readonly IReadOnlyList<string> None = Array.Empty<string>();

    [Fact]
    public void Clean_run_reports_complete_and_written()
    {
        var line = CliAuditHost.ComposeAuditCompletionLine(
            exportFailed: false, assessedCount: 1, totalServers: 1, resultCount: 523,
            outDir: @"C:\out", notAssessed: None);

        Assert.Contains("complete.", line, StringComparison.Ordinal);
        Assert.Contains("523 result(s) written to", line, StringComparison.Ordinal);
        Assert.DoesNotContain("WITH ERRORS", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_failure_does_not_read_as_a_clean_success()
    {
        // RED before the fix: this line was byte-identical to the clean-run line above.
        var line = CliAuditHost.ComposeAuditCompletionLine(
            exportFailed: true, assessedCount: 1, totalServers: 1, resultCount: 523,
            outDir: @"C:\out", notAssessed: None);

        Assert.Contains("WITH ERRORS", line, StringComparison.Ordinal);
        Assert.Contains("INCOMPLETE", line, StringComparison.Ordinal);
        // The stdout line must NOT claim the results were "written" when an artifact failed.
        Assert.DoesNotContain("result(s) written to", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Not_assessed_servers_are_named_on_both_arms()
    {
        var notAssessed = new[] { "SQL02", "SQL03" };

        var clean = CliAuditHost.ComposeAuditCompletionLine(
            false, 1, 3, 200, @"C:\out", notAssessed);
        var failed = CliAuditHost.ComposeAuditCompletionLine(
            true, 1, 3, 200, @"C:\out", notAssessed);

        Assert.Contains("NOT assessed: SQL02, SQL03.", clean, StringComparison.Ordinal);
        Assert.Contains("NOT assessed: SQL02, SQL03.", failed, StringComparison.Ordinal);
    }
}
