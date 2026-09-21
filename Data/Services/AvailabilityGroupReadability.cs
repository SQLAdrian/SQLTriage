/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Data.SqlClient;

namespace SQLTriage.Data.Services;

/// <summary>
/// Classifies the SQL Server errors that mean "this database cannot be read on THIS replica, by
/// design" — as distinct from "this run failed".
///
/// <para><b>THE INVARIANT.</b> A database that is a non-readable availability-group replica is a
/// <b>SKIP</b>, never a FAILURE. It is an expected, permanent condition of where the caller is
/// connected: the answer will be identical on every retry until someone changes the AG's
/// configuration. Counting it as a failure makes one unreadable database suppress the verdict and
/// the output for every database that DID read — which is what happened on a client on
/// 2026-09-12, where two AG secondaries (Exonet_Test, ReportServer) blocked a whole
/// Server Configuration preview from producing anything at all.</para>
///
/// <para><b>The distinction that matters.</b> "Skip" here does NOT mean "ignore quietly". The
/// caller MUST report each skipped database by name and say why — an unreported skip is the
/// failure mode this codebase has paid for repeatedly (a guard that treats ABSENT as OK). The
/// point is only that a skip does not poison the verdict for everything else.</para>
///
/// <para><b>If you add a call site, add it here, not inline.</b> This predicate exists because the
/// same decision is needed in at least seven places — the server-config script runner and SIX catch
/// blocks in <c>DbatoolsRemediationExecutor</c>'s preview path — and hand-written copies of a number
/// list is how one of them ends up stale.</para>
///
/// <para>⚠ This comment said FIVE catch blocks until 2026-09-12, and so did the brief that was
/// written from it. The sixth ("Database-size read failed") is the structural twin of the fifth
/// ("Backup-size estimate failed") in the sibling CHECKDB method, and a hand count missed it twice.
/// The set is therefore enumerated FROM THE SOURCE by
/// <c>RemediationAgSkipTests.EveryPreviewSqlExceptionCatchHasAnAvailabilityGroupSkipAheadOfIt</c>,
/// which goes red when a seventh appears without a guard. Trust that test, not this number. Pinned by
/// <c>Tests/SQLTriage.Tests/AvailabilityGroupReadabilityTests.cs</c>, which covers every number
/// below plus a negative control. ⚠ Be precise about what that green means: it pins the NUMBER
/// LIST only. It does not exercise the <see cref="SqlException"/> collection walk, and it does not
/// assert that any database is named — <see cref="DescribeSkip"/> deliberately does NOT name one,
/// because the identity comes from the server's own error text, which the CALLER appends.</para>
/// </summary>
public static class AvailabilityGroupReadability
{
    /// <summary>
    /// SQL Server errors meaning the target database is in an availability group and is not
    /// readable through this connection, because of the replica's role or its readable-secondary
    /// setting. Each is a statement about WHERE WE ARE CONNECTED, not about the database's health.
    /// </summary>
    /// <remarks>
    /// 976 — target database is participating in an AG and is not currently accessible for queries
    ///       (data movement suspended, or the replica is not enabled for read access).
    /// 978 — the target database is accessible only when application intent is set to read-only.
    /// 979 — the target database is accessible only when application intent is read-write.
    /// 983 — unable to access the availability database: the replica is neither PRIMARY nor
    ///       SECONDARY (resolving, or transitioning roles).
    /// </remarks>
    private static readonly int[] NotReadableOnThisReplica = { 976, 978, 979, 983 };

    /// <summary>
    /// True when <paramref name="ex"/> means the database is not readable on this replica by
    /// configuration or role. Checks EVERY error in the collection, not just
    /// <see cref="SqlException.Number"/>: a batch can raise several errors and the AG refusal is
    /// not guaranteed to be the one surfaced as the exception's own number.
    /// </summary>
    public static bool IsNotReadableOnThisReplica(SqlException? ex)
    {
        if (ex is null) return false;
        if (IsNotReadableOnThisReplica(ex.Number)) return true;

        foreach (SqlError err in ex.Errors)
        {
            if (IsNotReadableOnThisReplica(err.Number)) return true;
        }

        return false;
    }

    /// <summary>
    /// The number check, split out so it is testable. <see cref="SqlException"/> has no public
    /// constructor, so a test cannot build one; without this overload the pinned list could only be
    /// exercised against a live availability group. ⚠ That means the COLLECTION WALK in the
    /// <see cref="SqlException"/> overload above is NOT covered by a unit test — only this list is.
    /// Say so rather than implying the whole predicate is pinned.
    /// </summary>
    public static bool IsNotReadableOnThisReplica(int sqlErrorNumber) =>
        System.Array.IndexOf(NotReadableOnThisReplica, sqlErrorNumber) >= 0;

    /// <summary>
    /// A one-line, operator-readable reason for a skip. Names the SQL error number so the reader
    /// can look it up, and says plainly that this is not a fault and not a retryable condition.
    /// </summary>
    public static string DescribeSkip(SqlException ex) =>
        $"Skipped: this database is an availability-group replica that is not readable through this "
        + $"connection (SQL error {ex.Number}). This is expected, not a fault. Run against the replica "
        + $"where it is primary, or enable read access on the secondary. Nothing was changed.";
}
