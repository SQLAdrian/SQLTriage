/* In the name of God, the Merciful, the Compassionate */
/*
 * What a Best Practice script run actually did, in words.
 *
 * pages-r1-04 (honesty hunt, 2026-08-28): RunSelected appended
 * `("All scripts completed.", false, true)` unconditionally after the loop - the third tuple
 * member is IsSuccess, rendered in var(--green). ExecuteScript swallowed every exception into a
 * red message and returned normally, so RunSelected never learned of a failure and nothing counted
 * successes, failures or aborts. Proved at hunt time by pointing the global server context at a
 * nonexistent host and running all nine enabled scripts: nine red "Error in <script>" lines, then
 * a green "All scripts completed." Zero of nine had run.
 *
 * pages-r2-06 (same page, same run): the panel renders exactly ONE result grid from a single
 * reused DataTable, with no caption naming the script it came from. The catch did not clear it,
 * so when script N threw before its reader opened, the panel showed script N-1's rows directly
 * beneath script N's red error and the green completion line - a different script's data
 * presented as this run's output. Proved at hunt time with two probe scripts against .\NEW2022.
 * Even on a fully clean run, every script's row count was asserted to the operator while only the
 * last script's table rendered.
 *
 * These are pure functions over the run's own tally - no SQL, no UI - so the wording is
 * unit-testable and cannot drift from the counts the panel displays.
 */

using System;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// The tally a Best Practice run keeps about itself. <paramref name="Attempted"/> counts
    /// scripts the loop reached; <paramref name="Succeeded"/> counts those that ran to a result
    /// without throwing.
    /// </summary>
    public readonly record struct BestPracticeRunTally(int Attempted, int Succeeded, int Failed)
    {
        public bool AnyFailed => Failed > 0;
    }

    public static class BestPracticeRunReporting
    {
        /// <summary>
        /// The closing line of a run. Replaces the unconditional "All scripts completed.", which
        /// was literally true read as "the loop finished" and read by an operator as "the work
        /// succeeded" - in green, as the most recent line on the panel.
        /// </summary>
        public static string DescribeCompletion(BestPracticeRunTally tally)
        {
            if (tally.Attempted == 0)
                return "No scripts were enabled, so nothing ran.";

            if (!tally.AnyFailed)
                return tally.Attempted == 1
                    ? "The 1 selected script completed."
                    : $"All {tally.Attempted} selected scripts completed.";

            if (tally.Succeeded == 0)
                return tally.Attempted == 1
                    ? "The selected script FAILED. Nothing ran."
                    : $"All {tally.Attempted} selected scripts FAILED. Nothing ran.";

            return $"Run finished with failures: {tally.Attempted} scripts attempted, "
                 + $"{tally.Succeeded} succeeded, {tally.Failed} failed.";
        }

        /// <summary>
        /// Whether the closing line may be painted as a success. False the moment anything failed:
        /// the colour is the half of this defect an operator reads first.
        /// </summary>
        public static bool CompletionIsSuccess(BestPracticeRunTally tally) =>
            tally.Attempted > 0 && !tally.AnyFailed;

        /// <summary>
        /// The caption that must sit on the single result grid, naming the script whose rows it
        /// actually holds. An unlabelled table under another script's error was the whole of
        /// pages-r2-06.
        /// </summary>
        public static string DescribeResultsOwner(string scriptName, int rowCount) =>
            $"Results from {scriptName} - {rowCount} row(s)";

        /// <summary>
        /// The disclosure that the panel retains only the most recent result set. Null when there
        /// is nothing withheld, so a caller can render it unconditionally.
        /// </summary>
        /// <param name="scriptsWithRows">How many scripts in this run returned at least one row.</param>
        public static string? DescribeRetainedResults(int scriptsWithRows)
        {
            if (scriptsWithRows <= 1) return null;

            var earlier = scriptsWithRows - 1;
            return earlier == 1
                ? "1 earlier script in this run also returned rows. This panel keeps only the most "
                  + "recent result set, so those rows are counted above and not shown."
                : $"{earlier} earlier scripts in this run also returned rows. This panel keeps only "
                  + "the most recent result set, so those rows are counted above and not shown.";
        }
    }
}
