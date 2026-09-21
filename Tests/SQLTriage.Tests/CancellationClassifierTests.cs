/* In the name of God, the Merciful, the Compassionate */

using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Exhaustive coverage of <see cref="CancellationClassifier.IsCancellationMessage"/>, the seam
    /// extracted from a live-verify-proved defect: <c>DynamicDashboard.IsCancellationException</c> once
    /// classified any SqlException message containing "severe error" as a user cancellation. SQL
    /// Server's KILL / abort message carries exactly that phrase
    /// ("A severe error occurred on the current command. The results, if any, should be discarded."),
    /// so a genuine server-side KILL was swallowed silently — no <c>_panelErrors</c> entry and no
    /// "Data Load Warnings" banner. The load-bearing assertion below is that the KILL message is NOT a
    /// cancellation; if the "severe error" match is ever re-added, this test goes red.
    /// </summary>
    public class CancellationClassifierTests
    {
        [Theory]
        // The KILL / abort message — the exact defect. MUST NOT be treated as a cancellation.
        [InlineData("A severe error occurred on the current command. The results, if any, should be discarded.", false)]
        [InlineData("A severe error occurred", false)]
        // Genuine SqlClient cancellation messages.
        [InlineData("Operation cancelled by user.", true)]
        [InlineData("A command was cancelled by user request", true)]
        // Ordinary errors and empties are never cancellations.
        [InlineData("Invalid object name 'dbo.sqlwatch_logger_perf_os_wait_stats'.", false)]
        [InlineData("Cannot open database \"SQLWATCH\" requested by the login.", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void ClassifiesMessages(string? message, bool expected)
            => Assert.Equal(expected, CancellationClassifier.IsCancellationMessage(message));
    }
}
