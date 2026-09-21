/* In the name of God, the Merciful, the Compassionate */

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// The verdict a scheduled run records, and the sentence that goes with it (2026-08-05).
    ///
    /// THE LAW: a printed claim must be conditioned on the same measurement that produced the
    /// verdict beside it. "Success" is a verdict, and three paths in <c>ScheduledTaskEngine</c>
    /// reached it over zero measurements: an assessment that ran no check, a report rendered over
    /// no results, and a restore-verify that found no target. The first is the one that bites —
    /// an unattended overnight run on a host where no page had ever loaded the check catalogue
    /// came back with nothing and was filed as a clean pass.
    ///
    /// The decisions live here rather than inline so they are testable as a cross-product without
    /// a live SQL Server, and so the three paths cannot drift apart again.
    /// </summary>
    public static class ScheduledRunVerdict
    {
        /// <summary>Status strings as written to the execution history. Unchanged vocabulary.</summary>
        public const string Success = "Success";
        public const string Warning = "Warning";
        public const string Failed = "Failed";

        /// <summary>
        /// An assessment run. Zero checks executed is never Success: nothing was assessed, so
        /// there is no pass to report. Names the catalogue load error when there is one, because
        /// "no check is enabled" and "the catalogue never loaded" call for different actions.
        /// </summary>
        public static (string Status, string? Message) Assessment(int totalChecks, string? catalogueLoadError)
        {
            if (totalChecks > 0) return (Success, null);

            var why = string.IsNullOrWhiteSpace(catalogueLoadError)
                ? "The check catalogue loaded, and no check is enabled."
                : $"The check catalogue is not loaded: {catalogueLoadError}";

            return (Warning, "No checks ran, so nothing was assessed on this server. " + why);
        }

        /// <summary>
        /// A scheduled report. Counts RESULTS, not findings: zero FINDINGS on a real run is a
        /// genuinely good outcome and stays Success. Zero RESULTS means the PDF describes no
        /// assessment at all.
        /// </summary>
        public static (string Status, string? Message) Report(int resultCount)
            => resultCount > 0
                ? (Success, null)
                : (Warning, "The report rendered over zero check results, so it describes no "
                          + "assessment. Run an audit against these servers first.");

        /// <summary>
        /// A restore-verify run. The existing cascade was this file's own best idiom and still
        /// carried the defect at its head: with zero targets nothing fails, nothing is skipped,
        /// and the cascade fell through to Success over a backup set nobody read.
        /// </summary>
        public static (string Status, string? Message) RestoreVerify(
            int total, int passed, int failed, int couldNotRun, int notSupported)
        {
            if (total == 0)
                return (Warning, "No backup was verified: no eligible target was found on this "
                               + "server. Nothing is claimed about the state of its backups.");

            var status = failed > 0 ? Failed
                       : (couldNotRun + notSupported) > 0 ? Warning
                       : Success;

            // Counts only, per the §4.5 honesty rail. Surfaced only when something is not a clean pass.
            var message = failed + couldNotRun + notSupported > 0
                ? $"Restore-verify: {passed} passed, {failed} FAILED (corrupt), "
                  + $"{couldNotRun} could not be checked, {notSupported} not supported (lite tier)."
                : null;

            return (status, message);
        }
    }
}
