/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SQLTriage.Data.Services.Replication;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// The Replication Map's <b>Sample</b> button renders a topology whose servers do not exist:
    /// SQLPUB01, SQLSUB03 and the rest are strings this app wrote. Those rows landed in the
    /// "Discovered participants not in your server list" table wearing the same two buttons as
    /// scanned rows.
    ///
    /// <para><b>What that cost.</b> "Add…" navigated to <c>/servers?add=SQLSUB03</c>, and the
    /// add-connection dialog opened pre-filled with a name SQLTriage had invented, on a page with
    /// no way to know where the name came from. The operator would have been reviewing a
    /// fabrication believing it had been discovered. "Probe" was worse in kind rather than degree:
    /// with no provenance recorded for a sample row it reported "the connection that discovered
    /// this server is no longer in the catalogue" — a removal, asserted, that nothing observed.</para>
    ///
    /// <para><b>Where the gate is.</b> On the model (<see cref="ParticipantOffer"/>), asked by the
    /// markup AND by the two handlers. Hiding a button hides an action; it does not refuse one.</para>
    /// </summary>
    public class ReplicationSampleParticipantTests
    {
        private static List<ReplicationLink> SampleLinks() => new()
        {
            new()
            {
                Distributor = "DIST01",
                PublisherName = "SQLPUB01", PublisherDb = "Sales", Publication = "Sales_Tran",
                SubscriberName = "SQLSUB01", SubscriberDb = "Sales_Replica", RunStatus = 2
            },
            new()
            {
                Distributor = "DIST01",
                PublisherName = "SQLPUB02", PublisherDb = "HR", Publication = "HR_Tran",
                SubscriberName = null, SubscriberId = -1, SubscriberDb = "HR_Replica", RunStatus = 4
            },
        };

        private static ParticipantCandidates SampleParticipants()
        {
            var groups = ReplicationTopology.GroupByInstance(SampleLinks(), new List<string> { "DIST01" });
            return ReplicationTopology.FindAddable(
                groups, Array.Empty<string>(), provenance: null, fabricated: true);
        }

        private static ParticipantCandidates ScannedParticipants(
            IReadOnlyDictionary<string, (string Distributor, string ConnectionId)>? provenance = null)
        {
            var groups = ReplicationTopology.GroupByInstance(SampleLinks(), new List<string> { "DIST01" });
            return ReplicationTopology.FindAddable(groups, Array.Empty<string>(), provenance);
        }

        // ── D1: a fabricated name never reaches the real add path ────────────────

        [Fact]
        public void Every_sample_participant_is_marked_fabricated()
        {
            var found = SampleParticipants();

            Assert.True(found.Fabricated);
            Assert.NotEmpty(found.Addable);
            Assert.All(found.Addable, c => Assert.True(c.Fabricated));
        }

        [Fact]
        public void A_scanned_participant_is_not_marked_fabricated()
        {
            var found = ScannedParticipants();

            Assert.False(found.Fabricated);
            Assert.NotEmpty(found.Addable);
            Assert.All(found.Addable, c => Assert.False(c.Fabricated));
        }

        [Fact]
        public void The_add_dialog_handoff_is_refused_for_every_sample_participant()
        {
            Assert.All(SampleParticipants().Addable,
                c => Assert.False(ParticipantOffer.MayHandOffToAddDialog(c)));
        }

        [Fact]
        public void The_add_dialog_handoff_is_still_offered_for_a_scanned_participant()
        {
            Assert.All(ScannedParticipants().Addable,
                c => Assert.True(ParticipantOffer.MayHandOffToAddDialog(c)));
        }

        [Fact]
        public void The_reachability_probe_is_refused_for_every_sample_participant()
        {
            Assert.All(SampleParticipants().Addable,
                c => Assert.False(ParticipantOffer.MayProbe(c)));
        }

        [Fact]
        public void A_nameless_candidate_is_never_handed_off_even_when_it_is_not_fabricated()
        {
            var blank = new ParticipantCandidate { Server = "  ", Key = "#blank" };

            Assert.False(ParticipantOffer.MayHandOffToAddDialog(blank));
            Assert.False(ParticipantOffer.MayProbe(blank));
            Assert.False(ParticipantOffer.MayHandOffToAddDialog(null));
        }

        // ── D2: the probe-failure sentence is conditioned on what was measured ───

        [Fact]
        public void A_sample_row_says_it_is_sample_data_and_claims_no_removal()
        {
            var c = SampleParticipants().Addable.First();
            var reason = ParticipantOffer.DescribeUnprobeable(c, connectionStillInCatalogue: false);

            Assert.Contains("sample data", reason);
            Assert.DoesNotContain("no longer in the catalogue", reason);
        }

        [Fact]
        public void No_recorded_provenance_says_so_and_claims_no_removal()
        {
            // The scan named this participant but recorded no connection for it. Nothing was
            // observed about any connection being removed, so nothing is said about one.
            var c = ScannedParticipants().Addable.First(a => a.ViaConnectionId.Length == 0);
            var reason = ParticipantOffer.DescribeUnprobeable(c, connectionStillInCatalogue: false);

            Assert.Contains("no provenance was recorded", reason);
            Assert.DoesNotContain("no longer in the catalogue", reason);
        }

        [Fact]
        public void A_removal_is_claimed_only_when_a_recorded_connection_is_gone()
        {
            var provenance = new Dictionary<string, (string Distributor, string ConnectionId)>
            {
                ["SQLPUB01"] = ("DIST01", "conn-a")
            };
            var c = ScannedParticipants(provenance).Addable.Single(a => a.Server == "SQLPUB01");
            var reason = ParticipantOffer.DescribeUnprobeable(c, connectionStillInCatalogue: false);

            Assert.Contains("no longer in the catalogue", reason);
            Assert.Contains("DIST01", reason);
            Assert.DoesNotContain("no provenance", reason);
        }

        [Fact]
        public void The_three_unprobeable_reasons_are_three_different_sentences()
        {
            var fabricated = SampleParticipants().Addable.First();
            var noProvenance = ScannedParticipants().Addable.First(a => a.ViaConnectionId.Length == 0);
            var removed = new ParticipantCandidate
            {
                Server = "SQLSUB09", Key = "SQLSUB09", ViaDistributor = "DIST01", ViaConnectionId = "conn-gone"
            };

            var sentences = new[]
            {
                ParticipantOffer.DescribeUnprobeable(fabricated, false),
                ParticipantOffer.DescribeUnprobeable(noProvenance, false),
                ParticipantOffer.DescribeUnprobeable(removed, false),
            };

            Assert.Equal(3, sentences.Distinct(StringComparer.Ordinal).Count());
        }

        // ── the page asks the gate, on both surfaces ─────────────────────────────

        /// <summary>
        /// A lint, not the boundary. The boundary is the refusal inside the two handlers, which
        /// the tests above exercise directly; this only pins that the markup stops rendering the
        /// controls as well, so a sample row does not present an action that would be refused.
        /// </summary>
        [Fact]
        public void The_participants_table_gates_both_controls_on_the_same_model_call()
        {
            var markup = File.ReadAllText(Path.Combine(
                RawPassedScan.RepoRoot().FullName, "Pages", "ReplicationMap.razor"));

            Assert.Contains("ParticipantOffer.MayProbe(c)", markup);
            Assert.Contains("ParticipantOffer.MayHandOffToAddDialog(c)", markup);
        }

        /// <summary>
        /// The foot sentence under the table describes what the Probe button does. Stated flat,
        /// "probing opens a connection with the credentials of the connection that discovered the
        /// server" is untrue for every row where that connection was never recorded or is gone —
        /// those short-circuit before any connection is opened — and untrue for every sample row.
        /// It is now conditioned on both.
        /// </summary>
        [Fact]
        public void The_foot_sentence_does_not_claim_a_connection_is_opened_for_rows_that_short_circuit()
        {
            var markup = File.ReadAllText(Path.Combine(
                RawPassedScan.RepoRoot().FullName, "Pages", "ReplicationMap.razor"));

            Assert.DoesNotContain(
                "Probing opens a connection with the credentials of the connection that discovered the server",
                markup);
            Assert.Contains("Where the connection that discovered a server is still in your catalogue", markup);
            Assert.Contains("A row without that connection is not probed at all", markup);

            // …and the sample-mode branch says no connection is opened at all.
            Assert.Contains("No connection is opened for these rows", markup);
        }

        /// <summary>Both handlers refuse before doing anything, not after.</summary>
        [Fact]
        public void Both_handlers_refuse_a_candidate_the_model_will_not_offer()
        {
            var code = File.ReadAllText(Path.Combine(
                RawPassedScan.RepoRoot().FullName, "Pages", "ReplicationMap.razor.cs"));

            Assert.Contains("if (!ParticipantOffer.MayProbe(c)) return;", code);
            Assert.Contains("if (!ParticipantOffer.MayHandOffToAddDialog(c)) return;", code);

            // …and the sample topology is the thing that marks them.
            Assert.Contains("fabricated: true", code);
        }
    }
}
