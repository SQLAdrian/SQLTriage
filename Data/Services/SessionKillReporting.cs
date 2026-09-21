/* In the name of God, the Merciful, the Compassionate */
/*
 * What a KILL actually achieved, in words.
 *
 * pages-r1-10 (honesty hunt, 2026-08-28): the Sessions page reported `Session {spid} killed.` as a
 * green success on the line immediately after ExecuteNonQueryAsync returned, without ever
 * re-reading the session. KILL returns as soon as the engine has ACCEPTED the request, not when
 * the session is gone - a session with work to undo enters KILLED/ROLLBACK and can stay there for
 * many minutes.
 *
 * Both verdict passes proved this live on .\new2022 at hunt time. The refute pass: 3,000,000 rows
 * inserted in an open transaction, KILL returned after 6 ms, sys.dm_exec_requests then showed
 * status='rollback' with estimated_completion_time=1500 s and 2,972,446 rows still uncommitted.
 * The reproduce pass: `KILL 71` returned in 3.0 ms, the request was at status='rollback',
 * percent_complete=0.0 immediately after, still 'rollback' at 28.0% two seconds later, and
 * sys.dm_exec_sessions still returned the session 18 seconds after that.
 *
 * So the app declared a destructive action complete on the strength of the statement returning.
 * The service now re-reads the session after issuing KILL and these functions describe what the
 * re-read found. Pure functions - no SQL, no UI - so the wording is unit-testable.
 */

using System;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// What a post-KILL re-read of the DMVs found.
    /// </summary>
    /// <param name="Spid">The session the KILL was issued against.</param>
    /// <param name="StillPresent">True when sys.dm_exec_sessions still returns the session.</param>
    /// <param name="RequestStatus">sys.dm_exec_requests.status, when the session still has a
    /// request. "rollback" is the state this finding is about. Null when there is no request.</param>
    /// <param name="PercentComplete">sys.dm_exec_requests.percent_complete, when reported.</param>
    /// <param name="EstimatedSecondsRemaining">estimated_completion_time converted to seconds.</param>
    /// <param name="ConfirmationError">Why the re-read could not be done, when it could not. A
    /// KILL whose outcome is unknown must say so; it must never fall back to the success claim.</param>
    public sealed record SessionKillOutcome(
        int Spid,
        bool StillPresent,
        string? RequestStatus,
        double? PercentComplete,
        int? EstimatedSecondsRemaining,
        string? ConfirmationError = null)
    {
        public bool Confirmed => ConfirmationError is null;

        /// <summary>True only when the re-read succeeded AND found the session gone.</summary>
        public bool GoneConfirmed => Confirmed && !StillPresent;

        public bool RollingBack =>
            StillPresent &&
            string.Equals(RequestStatus, "rollback", StringComparison.OrdinalIgnoreCase);
    }

    public static class SessionKillReporting
    {
        /// <summary>
        /// The sentence the page shows after a KILL. Says "killed" only for a session a re-read
        /// confirmed is gone.
        /// </summary>
        public static string Describe(SessionKillOutcome outcome)
        {
            if (outcome is null) throw new ArgumentNullException(nameof(outcome));

            if (!outcome.Confirmed)
                return $"KILL was issued for session {outcome.Spid}, but the result could not be "
                     + $"confirmed: {outcome.ConfirmationError} The session may still be running.";

            if (!outcome.StillPresent)
                return $"Session {outcome.Spid} killed.";

            if (outcome.RollingBack)
            {
                var progress = outcome.PercentComplete is double pc
                    ? $" It is {pc:F0}% through its rollback"
                    : " It is rolling back";

                var eta = outcome.EstimatedSecondsRemaining is int secs && secs > 0
                    ? $", about {DescribeDuration(secs)} remaining."
                    : ".";

                return $"KILL accepted for session {outcome.Spid}, but it is NOT gone yet.{progress}{eta} "
                     + "The transaction is still being undone on the server.";
            }

            var status = string.IsNullOrWhiteSpace(outcome.RequestStatus)
                ? ""
                : $" (status: {outcome.RequestStatus})";

            return $"KILL accepted for session {outcome.Spid}, but the session is still present on "
                 + $"the server{status}.";
        }

        /// <summary>
        /// Whether the page may paint this outcome as a success. Only a confirmed disappearance
        /// qualifies: an accepted-but-incomplete KILL and an unconfirmed one are both states in
        /// which the operator's next decision depends on knowing the work is not finished.
        /// </summary>
        public static bool IsSuccess(SessionKillOutcome outcome) => outcome.GoneConfirmed;

        private static string DescribeDuration(int seconds)
        {
            if (seconds < 60) return $"{seconds}s";
            if (seconds < 3600) return $"{seconds / 60}m {seconds % 60}s";
            return $"{seconds / 3600}h {(seconds % 3600) / 60}m";
        }
    }
}
