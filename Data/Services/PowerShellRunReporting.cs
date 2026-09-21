/* In the name of God, the Merciful, the Compassionate */
/*
 * What a dbatools/PowerShell run actually produced, in words.
 *
 * pages-r2-05 (honesty hunt, 2026-08-28): ExecuteAsDataTableAsync returns early whenever the
 * child process exits 0 with blank stdout. `result.Success` is nothing more than
 * `proc.ExitCode == 0`, and `result.Error` - which at that moment holds everything the command
 * wrote to standard error, where dbatools puts its "could not connect" / "access denied"
 * diagnostics - was read by the page ONLY on the non-success branch. So the diagnostic was
 * discarded and /dbatools rendered a grey inbox reading "No results returned." beside the header
 * "Completed in Nms": a run that failed to reach the instance, presented as a run that reached it
 * and found nothing.
 *
 * Re-proved at HEAD on 2026-08-28 through the exact host and argument shape the service builds
 * (`powershell.exe -NoProfile -NoLogo -NonInteractive -Command -`, PowerShellService.cs:199): a
 * script writing only to stderr returned EXIT=0, stdout 0 bytes, stderr 31 bytes.
 *
 * The sentences below are computed from the same result object the grid is computed from, so the
 * empty state and the run's real outcome can never disagree. Pure functions - no process launch,
 * no UI - so the wording is unit-testable.
 */

using System;

namespace SQLTriage.Data.Services
{
    public static class PowerShellRunReporting
    {
        /// <summary>
        /// The sentence that must stand where the grid would have been when a run produced no
        /// rows. Never says "no results" for a run that reported a problem instead.
        /// </summary>
        /// <remarks>
        /// Returns null when there is nothing to describe - the caller has rows to render, or the
        /// run failed outright and the error panel already owns the screen.
        /// </remarks>
        public static string? DescribeEmptyResult(PowerShellResult? result)
        {
            if (result is null) return null;

            // A failed run is already reported by the error panel, which reads result.Error on
            // exactly this branch. Saying it twice is not more honest, only louder.
            if (!result.Success) return null;

            // Output arrived; emptiness (if any) is the command's own answer, parsed downstream.
            if (!string.IsNullOrWhiteSpace(result.Output)) return null;

            if (!string.IsNullOrWhiteSpace(result.Error))
                return "The command exited 0 but wrote nothing to standard output. It did write a "
                     + "diagnostic to standard error, shown below - so this is not a result of "
                     + "\"nothing to report\".";

            return "The command completed and returned no output at all: no rows, and no "
                 + "diagnostic on either stream.";
        }

        /// <summary>
        /// True when a run the page will treat as successful nonetheless wrote something to
        /// standard error. That text is the only account of what went wrong on this path and had
        /// no render site at all before this lane.
        /// </summary>
        public static bool HasUnreportedDiagnostic(PowerShellResult? result) =>
            result is not null && result.Success && !string.IsNullOrWhiteSpace(result.Error);

        /// <summary>
        /// Names the columns a parse could not represent, for the caller to render beside the
        /// grid. Null when the grid carries every property the command emitted - which, since
        /// pages-r1-09 was fixed, is every heterogeneous shape ConvertTo-Json can produce.
        /// </summary>
        public static string? DescribeDroppedColumns(PowerShellResult? result)
        {
            var dropped = result?.DroppedColumns;
            if (dropped is null || dropped.Count == 0) return null;

            var names = string.Join(", ", dropped);
            return dropped.Count == 1
                ? $"One property returned by the command is not in this table or its CSV export: {names}."
                : $"{dropped.Count} properties returned by the command are not in this table or its "
                  + $"CSV export: {names}.";
        }
    }
}
