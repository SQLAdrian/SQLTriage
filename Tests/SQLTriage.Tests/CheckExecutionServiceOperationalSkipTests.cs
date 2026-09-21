/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Data.SqlClient;
using SQLTriage.Data;
using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Pins the contract of <see cref="CheckExecutionService.TryMapExpectedConditionSkip"/>: exactly
    /// TWO families of SQL error map to SKIP — canonical permission denials, and availability-group
    /// non-readability. Everything else must surface as ERROR.
    ///
    /// <para>⚠ Be precise about what a green here means. It pins the MAPPING only. It does NOT assert
    /// that the exception filter at the method's single call site still sits on a verdict-bearing
    /// path — the 2026-09-12 lane (082558ec) proved a guard can be wired, run, and still be compiled
    /// into the wrong method, detectable only by an IL census. That wiring remains untested.</para>
    ///
    /// <para>Uses <see cref="SqlExceptionTestFactory"/> because <see cref="SqlException"/> has no
    /// public constructor.</para>
    /// </summary>
    public class CheckExecutionServiceOperationalSkipTests
    {
        private static SqlException Sql(int number, string message, byte errorClass = 14) =>
            SqlExceptionTestFactory.Create(
                number: number,
                errorClass: errorClass,
                state: 1,
                lineNumber: 1,
                server: "TEST-SERVER",
                message: message);

        // FAMILY 2 — availability-group non-readability. The numbers are owned by
        // AvailabilityGroupReadability, not by this file; if that list grows, this theory
        // must grow with it.
        [Theory]
        [InlineData(976)] // participating in an AG, not currently accessible for queries
        [InlineData(978)] // accessible only when application intent is read-only
        [InlineData(979)] // accessible only when application intent is read-write
        [InlineData(983)] // replica is neither PRIMARY nor SECONDARY
        public void AgReplicaErrors_MapToSkip_WithAnOperatorReadableReason(int sqlErrorNumber)
        {
            var sqlEx = Sql(sqlErrorNumber, "Availability Group error");

            bool mapped = CheckExecutionService.TryMapExpectedConditionSkip(sqlEx, out string reason);

            Assert.True(mapped);
            Assert.Contains("availability-group replica that is not readable", reason);
            Assert.Contains("This is expected, not a fault", reason);
        }

        // FAMILY 1 — canonical permission denials. Gemini's original file left these unpinned,
        // so the switch they live in could have been gutted without a single test going red.
        [Theory]
        [InlineData(229)]   // SELECT permission was denied on object X
        [InlineData(230)]   // SELECT permission was denied on column X
        [InlineData(262)]   // Permission to CREATE/ALTER/... was denied
        [InlineData(297)]   // The user does not have permission to perform this action
        [InlineData(300)]   // VIEW SERVER STATE permission was denied
        [InlineData(916)]   // Cannot open database 'X' requested by the login
        [InlineData(18456)] // Login failed
        public void PermissionDeniedErrors_MapToSkip_WithANonEmptyReason(int sqlErrorNumber)
        {
            var sqlEx = Sql(sqlErrorNumber, "Permission denied");

            bool mapped = CheckExecutionService.TryMapExpectedConditionSkip(sqlEx, out string reason);

            Assert.True(mapped);
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }

        /// <summary>
        /// ⚠ REGRESSION GUARD, NOT AN OVERSIGHT. SQL 924 (single-user mode) was added to the SKIP
        /// map on 2026-09-14 and removed the same day: it is an operational state rather than a
        /// permission denial, it is TRANSIENT (a retry may succeed), and no check in the catalogue
        /// detects single-user mode by any other means — so downgrading it to SKIP deletes the
        /// audit's only signal for it. This test goes RED if anyone re-adds it.
        /// </summary>
        [Fact]
        public void SingleUserMode924_DoesNotMapToSkip_SoItStillSurfacesAsAnError()
        {
            var sqlEx = Sql(924, "Database 'db' is already open and can only have one user at a time.");

            bool mapped = CheckExecutionService.TryMapExpectedConditionSkip(sqlEx, out string reason);

            Assert.False(mapped);
            Assert.Empty(reason);
        }

        // Negative control. Without this, a method that returned true unconditionally would pass
        // every other test in this file.
        [Fact]
        public void AnUnrelatedError_DoesNotMapToSkip()
        {
            var sqlEx = Sql(208, "Invalid object name 'sys.tables'", errorClass: 16);

            bool mapped = CheckExecutionService.TryMapExpectedConditionSkip(sqlEx, out string reason);

            Assert.False(mapped);
            Assert.Empty(reason);
        }
    }
}
