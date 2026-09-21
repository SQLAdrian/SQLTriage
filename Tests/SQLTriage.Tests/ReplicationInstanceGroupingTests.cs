/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System.Collections.Generic;
using System.Linq;
using SQLTriage.Data.Services.Replication;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Scope A.2/A.3/A.4: the map grouped by SQL instance, scanned across all servers, with the
    /// participants it names but this install has no connection for.
    ///
    /// <para>The rule the last assertion holds is the one that matters: a participant with no
    /// resolvable server NAME is never offered as something to add. Offering it would push the
    /// fabricated value the "-1" fix just removed from the map straight into the server-connections
    /// store — the same defect, one surface further along.</para>
    /// </summary>
    public class ReplicationInstanceGroupingTests
    {
        private static ReplicationLink Link(
            string dist, string? pub, int? pubId, string pubDb, string publication,
            string? sub, int? subId, string subDb, int? runStatus = 2) => new()
        {
            Distributor = dist,
            PublisherName = pub, PublisherId = pubId, PublisherDb = pubDb, Publication = publication,
            SubscriberName = sub, SubscriberId = subId, SubscriberDb = subDb,
            RunStatus = runStatus
        };

        private static List<ReplicationLink> TwoDistributorSample() => new()
        {
            Link("DIST01", "SQLPUB01", 3, "Sales", "Sales_Tran", "SQLSUB01", 4, "Sales_Replica"),
            Link("DIST01", "SQLPUB01", 3, "Inventory", "Inv_Tran", "SQLSUB02", 5, "Inv_Replica"),
            Link("DIST02", "SQLPUB02", 6, "Orders", "Orders_Tran", "SQLSUB01", 4, "Orders_Replica"),
        };

        // ── grouping ─────────────────────────────────────────────────────────────

        [Fact]
        public void Each_instance_appears_once_with_its_own_items()
        {
            var groups = ReplicationTopology.GroupByInstance(
                TwoDistributorSample(), new List<string> { "DIST01", "DIST02" });

            Assert.Equal(new[] { "DIST01", "DIST02", "SQLPUB01", "SQLPUB02", "SQLSUB01", "SQLSUB02" },
                         groups.Select(g => g.Display).OrderBy(d => d).ToArray());

            var pub01 = groups.Single(g => g.Display == "SQLPUB01");
            Assert.True(pub01.IsPublisher);
            Assert.False(pub01.IsSubscriber);
            Assert.Equal(2, pub01.Items.Count(i => i.Kind == ReplicationItemKind.Publication));
        }

        [Fact]
        public void A_subscriber_of_two_publishers_carries_both_subscriptions()
        {
            var groups = ReplicationTopology.GroupByInstance(
                TwoDistributorSample(), new List<string> { "DIST01", "DIST02" });

            var sub01 = groups.Single(g => g.Display == "SQLSUB01");
            Assert.True(sub01.IsSubscriber);
            Assert.Equal(2, sub01.Items.Count(i => i.Kind == ReplicationItemKind.Subscription));
        }

        [Fact]
        public void A_scanned_distributor_that_publishes_nothing_still_appears_with_its_role()
        {
            var groups = ReplicationTopology.GroupByInstance(
                TwoDistributorSample(), new List<string> { "DIST01", "DIST02" });

            var dist = groups.Single(g => g.Display == "DIST01");
            Assert.True(dist.IsDistributor);
            Assert.False(dist.IsPublisher);
            Assert.Contains(dist.Items, i => i.Kind == ReplicationItemKind.DistributorRole);
        }

        [Fact]
        public void The_distributor_role_counts_only_the_links_that_distributor_recorded()
        {
            var groups = ReplicationTopology.GroupByInstance(
                TwoDistributorSample(), new List<string> { "DIST01", "DIST02" });

            var d1 = groups.Single(g => g.Display == "DIST01")
                           .Items.First(i => i.Kind == ReplicationItemKind.DistributorRole);
            var d2 = groups.Single(g => g.Display == "DIST02")
                           .Items.First(i => i.Kind == ReplicationItemKind.DistributorRole);

            Assert.Contains("2 links", d1.Detail);
            Assert.Contains("1 link", d2.Detail);
        }

        [Fact]
        public void An_unresolved_end_becomes_its_own_group_marked_as_having_no_name()
        {
            var links = new List<ReplicationLink>
            {
                Link("DIST01", "SQLPUB01", 3, "HR", "HR_Tran", null, -1, "HR_Replica")
            };
            var groups = ReplicationTopology.GroupByInstance(links, new List<string> { "DIST01" });

            var orphan = groups.Single(g => !g.NameResolved && g.Display.Contains("server id -1"));
            Assert.True(orphan.IsSubscriber);
            Assert.NotNull(orphan.UnresolvedDetail);
        }

        [Fact]
        public void Resolved_instances_sort_ahead_of_unresolved_ones()
        {
            var links = new List<ReplicationLink>
            {
                Link("DIST01", "SQLPUB01", 3, "HR", "HR_Tran", null, -1, "HR_Replica"),
                Link("DIST01", "SQLPUB01", 3, "Sales", "Sales_Tran", "SQLSUB01", 4, "Sales_Replica"),
            };
            var groups = ReplicationTopology.GroupByInstance(links, new List<string> { "DIST01" });
            Assert.True(groups.Last().NameResolved == false);
        }

        // ── the add-from-topology candidate set ──────────────────────────────────

        [Fact]
        public void Only_participants_missing_from_the_catalogue_are_offered()
        {
            var groups = ReplicationTopology.GroupByInstance(
                TwoDistributorSample(), new List<string> { "DIST01", "DIST02" });

            var found = ReplicationTopology.FindAddable(
                groups, new[] { "DIST01", "DIST02", "SQLPUB01" });

            Assert.Equal(new[] { "SQLPUB02", "SQLSUB01", "SQLSUB02" },
                         found.Addable.Select(a => a.Server).OrderBy(s => s).ToArray());
        }

        [Fact]
        public void A_catalogued_server_written_as_an_fqdn_is_recognised_and_not_offered_again()
        {
            var groups = ReplicationTopology.GroupByInstance(
                TwoDistributorSample(), new List<string> { "DIST01", "DIST02" });

            var found = ReplicationTopology.FindAddable(
                groups, new[] { "sqlsub01.corp.example.com" });

            Assert.DoesNotContain(found.Addable, a => a.Server == "SQLSUB01");
        }

        [Fact]
        public void A_participant_with_no_server_name_is_never_offered_and_is_disclosed_instead()
        {
            var links = new List<ReplicationLink>
            {
                Link("DIST01", "SQLPUB01", 3, "HR", "HR_Tran", null, -1, "HR_Replica")
            };
            var groups = ReplicationTopology.GroupByInstance(links, new List<string> { "DIST01" });
            var found = ReplicationTopology.FindAddable(groups, new[] { "DIST01" });

            Assert.DoesNotContain(found.Addable, a => a.Server.Contains("-1"));
            Assert.Contains(found.NotNameable, s => s.Contains("server id -1"));
        }

        [Fact]
        public void Each_offered_participant_remembers_which_scan_named_it()
        {
            var groups = ReplicationTopology.GroupByInstance(
                TwoDistributorSample(), new List<string> { "DIST01", "DIST02" });

            var provenance = new Dictionary<string, (string Distributor, string ConnectionId)>
            {
                ["SQLSUB02"] = ("DIST01", "conn-a")
            };
            var found = ReplicationTopology.FindAddable(groups, new[] { "DIST01" }, provenance);

            var sub02 = found.Addable.Single(a => a.Server == "SQLSUB02");
            Assert.Equal("DIST01", sub02.ViaDistributor);
            Assert.Equal("conn-a", sub02.ViaConnectionId);
        }

        // ── the all-servers scan summary is derived from the outcomes ────────────

        [Fact]
        public void A_server_that_failed_is_never_reported_as_having_no_replication()
        {
            var summary = ReplicationTopology.DescribeScan(new List<ScanOutcome>
            {
                new() { Server = "A", Kind = ScanOutcomeKind.LinksFound, Links = 3 },
                new() { Server = "B", Kind = ScanOutcomeKind.NoReplicationMetadata },
                new() { Server = "C", Kind = ScanOutcomeKind.Failed, Detail = "login timeout" },
            });

            Assert.Contains("Scanned 3 servers", summary);
            Assert.Contains("1 returned replication metadata (3 links)", summary);
            Assert.Contains("1 returned none", summary);
            Assert.Contains("1 could not be read", summary);
        }

        [Fact]
        public void A_clean_single_server_scan_says_nothing_about_failures()
        {
            var summary = ReplicationTopology.DescribeScan(new List<ScanOutcome>
            {
                new() { Server = "A", Kind = ScanOutcomeKind.LinksFound, Links = 1 }
            });

            Assert.Contains("Scanned 1 server:", summary);
            Assert.Contains("(1 link)", summary);
            Assert.DoesNotContain("could not be read", summary);
            Assert.DoesNotContain("returned none", summary);
        }

        [Fact]
        public void Nothing_scanned_says_nothing_scanned()
        {
            Assert.Equal("No server has been scanned yet.",
                         ReplicationTopology.DescribeScan(new List<ScanOutcome>()));
        }
    }
}
