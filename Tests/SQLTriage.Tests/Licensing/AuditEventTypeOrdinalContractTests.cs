/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using SQLTriage.Data;
using Xunit;

namespace SQLTriage.Tests.Licensing;

/// <summary>
/// <see cref="AuditEventType"/> serialises as its ORDINAL into the signed canonical form of an
/// audit entry, so a member inserted anywhere but the END silently re-labels every historical
/// entry after the insertion point: a chain written months ago would read back with different event
/// types and still verify, because the signature covers the number, not the name.
///
/// <para>Board #19 appended two members for key-file activation. Appending is only safe while
/// somebody checks that it WAS an append, so this pins the whole ordering by name and ordinal. It
/// fails on an insertion, on a rename, and on a deletion — all three of which are the same defect
/// wearing different clothes.</para>
///
/// <para><b>This is a literal list on purpose.</b> Deriving the expected ordinals from the enum
/// itself would assert that the enum equals itself, which is the assertion-shaped-nothing this
/// house has caught 25 times. The list below was read off the declaration on 2026-09-02 and is the
/// independent second copy; changing the enum without changing this file is what turns red.</para>
///
/// <para>Profile-neutral: <c>AuditEventType</c> lives in <c>Data\AuditLogService.cs</c>, which every
/// build profile compiles.</para>
/// </summary>
public class AuditEventTypeOrdinalContractTests
{
    /// <summary>
    /// Every member of <see cref="AuditEventType"/>, in declaration order, as of 2026-09-02.
    /// The index in this array IS the asserted ordinal.
    /// </summary>
    private static readonly string[] ExpectedInOrder =
    {
        "ConnectionAttempt",
        "ScriptExecution",
        "QueryExecution",
        "SecurityBlock",
        "SecurityEvent",
        "ConfigurationChange",
        "Deployment",
        "ApplicationLifecycle",
        "ExportOperation",
        "CacheOperation",
        "DashboardAccess",
        "SessionEvent",
        "AuditRetentionSweep",
        "UserAccessReviewed",
        "AccessReviewExported",
        "AuditChainVerified",
        "AuditChainVerificationExported",
        "AuditLogExported",
        "UserAdded",
        "UserRemoved",
        "UserUpdated",
        "UptimeSnapshotExported",
        "DrTestRecorded",
        "ConfigDriftDetected",
        "ConfigBaselineUpdated",
        "ServerConfigBaselineCaptured",
        "ServerConfigBaselineCompared",
        "ServerConfigBaselineExported",
        "IncidentStateChanged",
        "HmacKeyAgeExceeded",
        "HmacKeyRotated",
        "AuditFlushFailover",
        "ServerCircuitOpened",
        "ServerCircuitClosed",
        "AuditFailoverEntry",
        "ReportBundleGenerated",
        "BaselineLearned",
        "ComplianceReportExported",
        "RemediationProposed",
        "RemediationApproved",
        "RemediationApplied",
        "RemediationRolledBack",
        "RemediationPowerEstimate",
        "RemediationVerifyScheduled",
        "RemediationVerifyResolved",
        "HmacKeyReplacedUnreadable",
        "AuditChainUnverifiable",
        "AuditChainIndeterminate",
        "AnchorReceiptRecorded",
        "AnchorReceiptRejected",
        "DdlAttempted",
        "DdlCompleted",
        "DdlBlocked",
        "LicenseActivatedFromKeyFile",
        "LicenseKeyFileRejected",
    };

    [Fact]
    public void EveryOrdinalIsWhatItWasWhenTheEntryWasSigned()
    {
        for (int i = 0; i < ExpectedInOrder.Length; i++)
        {
            Assert.Equal(ExpectedInOrder[i], ((AuditEventType)i).ToString());
        }

        // …and nothing was added without this list being updated. Cardinality, not just presence:
        // a member appended past the end of the list would otherwise sail through the loop above.
        Assert.Equal(ExpectedInOrder.Length, Enum.GetValues<AuditEventType>().Length);
    }

    [Fact]
    public void TheTwoKeyFileMembersAreTheLastTwo()
    {
        var all = Enum.GetValues<AuditEventType>();
        var last = all[^1];
        var secondToLast = all[^2];

        Assert.Equal(AuditEventType.LicenseKeyFileRejected, last);
        Assert.Equal(AuditEventType.LicenseActivatedFromKeyFile, secondToLast);
    }

    [Fact]
    public void TheMemberThatWasLastBeforeThisLane_KeptItsOrdinal()
    {
        // DdlBlocked was the final member on the base branch (c1f01ae). If the two new members had
        // been inserted rather than appended, this ordinal would have moved and every DdlBlocked
        // entry already on disk would read back as something else.
        var all = Enum.GetValues<AuditEventType>();
        Assert.Equal(AuditEventType.DdlBlocked, all[^3]);
    }

    [Fact]
    public void NoTwoMembersShareAnOrdinal()
    {
        // An explicit "= N" on any member could collide with another and make two event types
        // indistinguishable in the signed form. Nothing in the enum assigns values today; this
        // fails the day one does.
        var values = Enum.GetValues<AuditEventType>().Select(v => (int)v).ToList();
        Assert.Equal(values.Count, values.Distinct().Count());
        Assert.Equal(Enumerable.Range(0, values.Count).ToList(), values.OrderBy(v => v).ToList());
    }
}
