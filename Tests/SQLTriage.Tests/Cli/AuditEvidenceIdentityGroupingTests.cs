/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
using SQLTriage.Cli;
using Xunit;

namespace SQLTriage.Tests.Cli
{
    /// <summary>
    /// Lane B final fix: the gate found feeding <c>--report audit-evidence</c> the SAME instance
    /// under two requested names (e.g. <c>.\new2022</c> and <c>MSI\NEW2022</c>) exits 0 and
    /// notarises every finding twice into one attestation PDF. <see
    /// cref="AuditEvidenceIdentityGrouping.FindDuplicates"/> is the pure decision seam
    /// <see cref="SQLTriage.Cli.CliAuditHost"/> now consults BEFORE any VA scan runs — no live SQL
    /// needed here, it takes the (requestedName, resolvedIdentity) pairs the CLI host's preflight
    /// already gathers via <see cref="EphemeralConnectionFactory.PreflightWithIdentityAsync"/>.
    /// </summary>
    public class AuditEvidenceIdentityGroupingTests
    {
        [Fact]
        public void TwoAliases_OneIdentity_IsFlaggedAsOneDuplicateGroup()
        {
            var resolved = new List<(string, string?)>
            {
                (".\\new2022", "SQLBOX\\NEW2022"),
                ("MSI\\NEW2022", "SQLBOX\\NEW2022"),
            };

            var duplicates = AuditEvidenceIdentityGrouping.FindDuplicates(resolved);

            var group = Assert.Single(duplicates);
            Assert.Equal("SQLBOX\\NEW2022", group.ResolvedIdentity);
            Assert.Equal(new[] { ".\\new2022", "MSI\\NEW2022" }, group.RequestedNames);
        }

        [Fact]
        public void TwoDistinctIdentities_NoDuplicateFlagged()
        {
            var resolved = new List<(string, string?)>
            {
                (".\\new2022", "SQLBOX\\NEW2022"),
                (".\\old2017", "SQLBOX\\OLD2017"),
            };

            var duplicates = AuditEvidenceIdentityGrouping.FindDuplicates(resolved);

            Assert.Empty(duplicates);
        }

        [Fact]
        public void CaseOnlyDifference_InResolvedIdentity_StillCollapsesToOneGroup()
        {
            // @@SERVERNAME reflects the server's own registered case; NEW2022\INSTANCE and
            // new2022\instance name the same instance, not two — SQL Server names are not a
            // case-sensitive discriminator.
            var resolved = new List<(string, string?)>
            {
                ("SERVERA", "PROD-SQL\\NEW2022"),
                ("SERVERB", "prod-sql\\new2022"),
            };

            var duplicates = AuditEvidenceIdentityGrouping.FindDuplicates(resolved);

            var group = Assert.Single(duplicates);
            Assert.Equal(new[] { "SERVERA", "SERVERB" }, group.RequestedNames);
        }

        [Fact]
        public void ThreeServers_TwoShareIdentity_OneDistinct_OnlyTheSharedPairFlagged()
        {
            var resolved = new List<(string, string?)>
            {
                (".\\new2022", "SQLBOX\\NEW2022"),
                ("MSI\\NEW2022", "SQLBOX\\NEW2022"),
                (".\\old2017", "SQLBOX\\OLD2017"),
            };

            var duplicates = AuditEvidenceIdentityGrouping.FindDuplicates(resolved);

            var group = Assert.Single(duplicates);
            Assert.Equal("SQLBOX\\NEW2022", group.ResolvedIdentity);
            Assert.Equal(2, group.RequestedNames.Count);
        }

        [Fact]
        public void SingleServer_NeverFlagged()
        {
            var resolved = new List<(string, string?)> { (".\\new2022", "SQLBOX\\NEW2022") };

            var duplicates = AuditEvidenceIdentityGrouping.FindDuplicates(resolved);

            Assert.Empty(duplicates);
        }

        [Fact]
        public void UnknownResolvedIdentity_NeverGroupedWithAnotherUnknown()
        {
            // If preflight couldn't read @@SERVERNAME for either server (network issue after the
            // socket opened, etc.), two nulls must not manufacture a false duplicate.
            var resolved = new List<(string, string?)>
            {
                ("SERVERA", null),
                ("SERVERB", null),
            };

            var duplicates = AuditEvidenceIdentityGrouping.FindDuplicates(resolved);

            Assert.Empty(duplicates);
        }

        [Fact]
        public void SameLiteralNameTwice_IsAlsoFlagged()
        {
            // Not just aliases — the same requested name appearing twice (e.g. a malformed
            // servers @file) resolves to one identity twice and produces the identical bug.
            var resolved = new List<(string, string?)>
            {
                (".\\new2022", "SQLBOX\\NEW2022"),
                (".\\new2022", "SQLBOX\\NEW2022"),
            };

            var duplicates = AuditEvidenceIdentityGrouping.FindDuplicates(resolved);

            var group = Assert.Single(duplicates);
            Assert.Equal(2, group.RequestedNames.Count);
        }
    }
}
