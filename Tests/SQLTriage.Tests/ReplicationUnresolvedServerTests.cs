/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using SQLTriage.Data.Services.Replication;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The Replication Map's top-right node rendered <c>-1</c>.
    ///
    /// <para><b>Where the value was made.</b> Not in the UI. The embedded discovery T-SQL read each
    /// end of a link as <c>COALESCE(pub.name, CAST(da.publisher_id AS sysname))</c>. When the
    /// <c>LEFT JOIN master.sys.servers</c> found no row for that <c>server_id</c>, the COALESCE fell
    /// through and CAST the raw id to a server-name-shaped string. The app received "-1" in the
    /// column called <c>Publisher</c>, and every layer downstream was right to trust it. The fabrication
    /// happened at the data layer, so the fix is there: name and id come back as separate columns, a
    /// NULL name is preserved as NULL, and the app decides what to print.</para>
    ///
    /// <para><b>What is NOT asserted here.</b> What <c>-1</c> MEANS. Replication convention associates
    /// negative ids with internal placeholders, and this query already filters <c>subscriber_id = -2</c>
    /// as anonymous bookkeeping, but SQLTriage measured no such thing and does not say so. The only
    /// claim made on screen is what the join actually established: this distributor's
    /// <c>master.sys.servers</c> has no row with that <c>server_id</c>.</para>
    /// </summary>
    public class ReplicationUnresolvedServerTests
    {
        // ── the reported defect ───────────────────────────────────────────────────

        [Fact]
        public void An_unresolved_id_never_becomes_a_bare_number_on_screen()
        {
            var end = ReplicationNaming.Describe(name: null, serverId: -1, distributor: "DIST01");

            Assert.Equal(ServerNameResolution.UnresolvedId, end.Resolution);
            Assert.NotEqual("-1", end.Label);
            Assert.Contains("server id -1", end.Label);
            Assert.False(end.IsRealServerName);
        }

        [Fact]
        public void An_unresolved_id_carries_the_measured_reason_and_no_guess_about_its_meaning()
        {
            var end = ReplicationNaming.Describe(null, -1, "DIST01");

            Assert.NotNull(end.Detail);
            Assert.Contains("DIST01", end.Detail!);
            Assert.Contains("master.sys.servers", end.Detail!);
            // The one thing the scan did NOT establish must not be claimed.
            Assert.DoesNotContain("local publisher", end.Detail!, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("anonymous", end.Detail!, System.StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(-3)]
        [InlineData(0)]
        [InlineData(1234)]
        public void Every_unresolved_id_is_handled_the_same_way_not_just_minus_one(int id)
        {
            var end = ReplicationNaming.Describe(null, id, "DIST01");
            Assert.Equal(ServerNameResolution.UnresolvedId, end.Resolution);
            Assert.Equal($"server id {id}", end.Label);
        }

        [Fact]
        public void A_resolved_name_is_printed_unchanged()
        {
            var end = ReplicationNaming.Describe("SQLPUB01", 7, "DIST01");
            Assert.Equal(ServerNameResolution.Resolved, end.Resolution);
            Assert.Equal("SQLPUB01", end.Label);
            Assert.Null(end.Detail);
            Assert.True(end.IsRealServerName);
        }

        [Fact]
        public void Neither_a_name_nor_an_id_is_its_own_state_not_an_invented_zero()
        {
            var end = ReplicationNaming.Describe(null, null, "DIST01");
            Assert.Equal(ServerNameResolution.Absent, end.Resolution);
            Assert.DoesNotContain("0", end.Label);
            Assert.NotNull(end.Detail);
        }

        [Fact]
        public void Two_different_unresolved_ids_stay_two_different_nodes()
        {
            var a = ReplicationNaming.Describe(null, -1, "DIST01");
            var b = ReplicationNaming.Describe(null, -4, "DIST01");
            Assert.NotEqual(a.Key, b.Key);
        }

        [Fact]
        public void A_resolved_name_groups_by_canonical_identity_so_an_fqdn_and_a_short_name_are_one_node()
        {
            var a = ReplicationNaming.Describe("SQLPUB01", null, "DIST01");
            var b = ReplicationNaming.Describe("sqlpub01.corp.example.com", null, "DIST01");
            Assert.Equal(a.Key, b.Key);
        }

        // ── the notice beside the map is conditioned on the same measurement ──────

        [Fact]
        public void No_unresolved_end_means_no_notice_at_all()
        {
            var links = new List<ReplicationLink>
            {
                Link("SQLPUB01", null, "SQLSUB01", null)
            };
            Assert.Null(ReplicationTopology.DescribeUnresolved(links));
        }

        [Fact]
        public void The_notice_counts_the_ends_it_actually_found()
        {
            var links = new List<ReplicationLink>
            {
                Link("SQLPUB01", null, null, -1),
                Link("SQLPUB01", null, null, -1),
                Link("SQLPUB01", null, "SQLSUB01", null)
            };
            var notice = ReplicationTopology.DescribeUnresolved(links);
            Assert.NotNull(notice);
            Assert.Contains("2 carry a server id", notice!);
            Assert.Contains("Some ends of these links have no server name", notice!);
            Assert.Contains("does not guess a name for them.", notice!);
            Assert.DoesNotContain("neither a server name nor an id", notice!);
        }

        /// <summary>
        /// The notice rendered "1 carry a server id" — a plural verb on a count of one. The count
        /// and the verb come off the same number, so both directions are pinned: this case beside
        /// the plural one above, because a sentence corrected in one direction rots in the other.
        /// </summary>
        [Fact]
        public void A_single_unresolved_end_is_described_in_the_singular()
        {
            var links = new List<ReplicationLink>
            {
                Link("SQLPUB01", null, null, -1),
                Link("SQLPUB01", null, "SQLSUB01", null)
            };
            var notice = ReplicationTopology.DescribeUnresolved(links);
            Assert.NotNull(notice);
            Assert.Contains("One end of these links has no server name", notice!);
            Assert.Contains("1 carries a server id", notice!);
            Assert.Contains("does not guess a name for it.", notice!);
            Assert.DoesNotContain("carry", notice!);
            Assert.DoesNotContain("Some ends", notice!);
        }

        /// <summary>The end with neither a name nor an id is its own clause, and agrees with its
        /// own count rather than borrowing the other clause's.</summary>
        [Theory]
        [InlineData(1, "1 carries neither a server name nor an id")]
        [InlineData(2, "2 carry neither a server name nor an id")]
        public void The_unrecorded_clause_agrees_with_its_own_count(int count, string expected)
        {
            var links = new List<ReplicationLink>();
            for (var i = 0; i < count; i++) links.Add(Link("SQLPUB01", null, null, null));

            var notice = ReplicationTopology.DescribeUnresolved(links);
            Assert.NotNull(notice);
            Assert.Contains(expected, notice!);
        }

        /// <summary>
        /// Two clauses of one each is two unresolved ends: each clause reads singular, and the
        /// sentence around them reads plural. The opening is counted off the total, not off
        /// whichever clause happens to be first.
        /// </summary>
        [Fact]
        public void One_of_each_kind_is_two_singular_clauses_inside_a_plural_sentence()
        {
            var links = new List<ReplicationLink>
            {
                Link("SQLPUB01", null, null, -1),
                Link("SQLPUB01", null, null, null)
            };
            var notice = ReplicationTopology.DescribeUnresolved(links);
            Assert.NotNull(notice);
            Assert.Contains("Some ends of these links have no server name", notice!);
            Assert.Contains("1 carries a server id", notice!);
            Assert.Contains("1 carries neither a server name nor an id", notice!);
            Assert.Contains("does not guess a name for them.", notice!);
        }

        [Fact]
        public void An_empty_topology_produces_no_notice()
        {
            Assert.Null(ReplicationTopology.DescribeUnresolved(new List<ReplicationLink>()));
        }

        // ── the query itself no longer fabricates the name ───────────────────────

        [Fact]
        public void The_shipped_discovery_query_selects_name_and_id_separately()
        {
            var sql = ShippedQueryText();

            // The exact expression that manufactured the sentinel-as-name must be gone.
            Assert.DoesNotContain("COALESCE(pub.name", sql);
            Assert.DoesNotContain("COALESCE(sub.name", sql);
            Assert.DoesNotContain("CAST(da.publisher_id  AS sysname)", sql);
            Assert.DoesNotContain("CAST(da.subscriber_id AS sysname)", sql);

            // …and both ids must be carried through as ids.
            Assert.Contains("PublisherId int NULL", sql);
            Assert.Contains("SubscriberId int NULL", sql);
        }

        /// <summary>
        /// "Show query" is a client-facing pane. What belongs in it is what a DBA needs to
        /// validate the query on their own distributor; what does not belong in it is our
        /// changelog. The narrative of the COALESCE defect is kept as a C# comment above the
        /// const, and asserted present there — moving prose out of a shipped artifact must not
        /// be a licence to lose it.
        /// </summary>
        [Fact]
        public void The_shipped_query_text_carries_operational_comments_and_not_our_changelog()
        {
            var source = ReplicationMapSource();
            var sql = ShippedQueryText();

            // Changelog vocabulary: a sentence about what the code USED to do, addressed to us.
            foreach (var marker in new[]
                     {
                         "used to be", "no longer", "on purpose", "as if it were",
                         "minus one", "The app decides", "IS the finding"
                     })
            {
                Assert.False(sql.Contains(marker, System.StringComparison.OrdinalIgnoreCase),
                    $"The shipped query text still carries changelog prose: \"{marker}\".");
            }

            // The comments a DBA validating this on-site does need are still there.
            Assert.Contains("Distribution databases are discovered by catalog flag", sql);
            Assert.Contains("skip virtual/anonymous bookkeeping agents", sql);
            Assert.Contains("must not sink the scan", sql);
            Assert.Contains("sys.servers.name is never NULL", sql);

            // And the history survives, above the const, in C#.
            var beforeConst = source.Substring(0, source.IndexOf(QueryMarker, System.StringComparison.Ordinal));
            Assert.Contains("COALESCE(pub.name, CAST(da.publisher_id AS sysname))", beforeConst);
        }

        private const string QueryMarker = "private const string ReplicationQuery = @\"";

        private static string ReplicationMapSource() => File.ReadAllText(Path.Combine(
            RawPassedScan.RepoRoot().FullName, "Pages", "ReplicationMap.razor.cs"));

        /// <summary>The literal the "Show query" pane renders — the const's own text, not the
        /// file around it. Scanning the whole file would let a comment ABOUT the query stand in
        /// for the query, which is the precise confusion this pair of tests exists to separate.</summary>
        private static string ShippedQueryText()
        {
            var source = ReplicationMapSource();
            var start = source.IndexOf(QueryMarker, System.StringComparison.Ordinal);
            Assert.True(start >= 0, "ReplicationQuery const not found in Pages/ReplicationMap.razor.cs.");
            start += QueryMarker.Length;

            // The query contains no double quote, so the first `";` closes the verbatim literal.
            var end = source.IndexOf("\";", start, System.StringComparison.Ordinal);
            Assert.True(end > start, "ReplicationQuery const is not terminated as expected.");
            return source.Substring(start, end - start);
        }

        private static ReplicationLink Link(string? pubName, int? pubId, string? subName, int? subId) => new()
        {
            Distributor = "DIST01",
            PublisherName = pubName,
            PublisherId = pubId,
            PublisherDb = "Sales",
            Publication = "Sales_Tran",
            SubscriberName = subName,
            SubscriberId = subId,
            SubscriberDb = "Sales_Replica",
            RunStatus = 2
        };
    }
}
