/* In the name of God, the Merciful, the Compassionate */

using System;

namespace SQLTriage.Data
{
    /// <summary>
    /// Pure decision: does a caught <see cref="Microsoft.Data.SqlClient.SqlException"/> message denote
    /// that the query was CANCELLED (the user navigated away), as opposed to a genuine server-side error?
    ///
    /// <para><b>The defect this closes.</b> The dashboard's per-panel catch
    /// (<c>DynamicDashboard.IsCancellationException</c>) used to also treat any message containing
    /// "severe error" as a cancellation. But SQL Server's own KILL / abort message —
    /// <c>"A severe error occurred on the current command. The results, if any, should be discarded."</c>
    /// — contains exactly that phrase, so a genuine server-side KILL was swallowed as a user cancel: no
    /// <c>_panelErrors</c> entry, no "Data Load Warnings" banner, only a Debug-level log line. A real
    /// cancellation is already signalled two honest ways — the <see cref="System.Threading.CancellationToken"/>
    /// (checked first by the caller) and SqlClient's own "Operation cancelled by user" message — so
    /// "severe error" is NOT a cancellation signal and must fall through to the real error handler.</para>
    ///
    /// <para>Extracted so the decision can be tested exhaustively without constructing a
    /// <c>SqlException</c> (no public constructor) or running a live KILL, mirroring
    /// <see cref="DashboardDatabaseResolver"/>.</para>
    /// </summary>
    public static class CancellationClassifier
    {
        /// <summary>
        /// True only for SqlClient messages that specifically denote a cancellation. Deliberately does
        /// NOT match "severe error" (the server-side KILL / abort phrase) — see the type remarks.
        /// </summary>
        public static bool IsCancellationMessage(string? message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            return message.Contains("Operation cancelled", StringComparison.OrdinalIgnoreCase)
                || message.Contains("cancelled by user", StringComparison.OrdinalIgnoreCase);
        }
    }
}
