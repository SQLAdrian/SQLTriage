/* In the name of God, the Merciful, the Compassionate */

using SQLTriage.Data.Services;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Pins <see cref="AvailabilityGroupReadability"/>, named by the invariant comment at its
    /// declaration. Born 2026-09-12: two AG secondaries on a client (Exonet_Test, ReportServer)
    /// raised SQL error 976 in a Server Configuration preview; the runner counted each as a failed
    /// batch, and <c>PreviewIsReadable</c> treats ANY batch error as "the whole preview is
    /// unreadable", so the verdict and the output were suppressed for every database that DID read.
    ///
    /// <para>⚠ SCOPE, stated so nobody reads more into a green than it carries: these tests cover
    /// the pinned NUMBER LIST and the skip prose. They do NOT cover the
    /// <see cref="System.Data.Common.DbException"/> collection walk in the SqlException overload,
    /// because SqlException has no public constructor and cannot be built in a unit test. That path
    /// is exercised only against a live availability group.</para>
    /// </summary>
    public class AvailabilityGroupReadabilityTests
    {
        // The whole point of the class: these four mean "not readable HERE", not "failed".
        [Theory]
        [InlineData(976)] // participating in an AG, not currently accessible for queries
        [InlineData(978)] // accessible only when application intent is read-only
        [InlineData(979)] // accessible only when application intent is read-write
        [InlineData(983)] // replica is neither PRIMARY nor SECONDARY
        public void AgReplicaRefusals_AreClassifiedAsNotReadable(int sqlErrorNumber)
        {
            Assert.True(
                AvailabilityGroupReadability.IsNotReadableOnThisReplica(sqlErrorNumber),
                $"SQL error {sqlErrorNumber} means the database is not readable on this replica and "
                + "must be a SKIP, never a failed batch.");
        }

        // The negative control. Without this the predicate could return true for everything and
        // every assertion above would still pass - a control that cannot show a negative proves
        // nothing about the positives.
        [Theory]
        [InlineData(0)]     // no number
        [InlineData(208)]   // invalid object name - a real script fault
        [InlineData(229)]   // permission denied - a real refusal the operator must see
        [InlineData(4060)]  // cannot open database - NOT an AG-role condition
        [InlineData(18456)] // login failed
        [InlineData(975)]   // deliberately adjacent to 976: a boundary, not a member
        [InlineData(984)]   // deliberately adjacent to 983
        public void OtherErrors_AreNotSwallowedAsSkips(int sqlErrorNumber)
        {
            Assert.False(
                AvailabilityGroupReadability.IsNotReadableOnThisReplica(sqlErrorNumber),
                $"SQL error {sqlErrorNumber} is a genuine failure and must still poison the verdict. "
                + "Widening this predicate hides real faults behind a skip.");
        }

        [Fact]
        public void NullException_IsNotASkip()
        {
            Assert.False(AvailabilityGroupReadability.IsNotReadableOnThisReplica((Microsoft.Data.SqlClient.SqlException?)null));
        }
    }
}
