/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Discovery;
using Xunit;

namespace SQLTriage.Tests
{
    /// <summary>
    /// Operations Hub, "add all to catalogue": it added unreachable servers.
    ///
    /// <para><b>The old filter was catalogue membership and nothing else.</b> Every discovered name
    /// that was not already a connection went into one bulk multi-line ServerNames write, whether the
    /// crawl had reached it or failed on it. <c>TopologyNode.Reachable</c> was populated the whole
    /// time and never consulted.</para>
    ///
    /// <para><b>Why the fix is three-state and not a boolean.</b> The crawl emits a node only for
    /// servers it PROBED. A server can enter the discovered set purely as the far end of an edge and
    /// never be probed at all — the depth cap or the server cap cut the frontier, the run was
    /// cancelled, or the probe task faulted. For that server <c>Reachable</c> is not false, it is
    /// unknown. This reuses <see cref="AlertEvaluationService.ServerReachability"/> from the
    /// circuit-breaker fix (e231bcc) with its rule intact: Undetermined is a state, not a synonym for
    /// success. Only Reached is added; everything else is disclosed with its reason.</para>
    /// </summary>
    public class DiscoveredServerTriageTests
    {
        private static TopologyNode Node(string server, bool reachable, string? error = null)
            => new() { Server = server, Reachable = reachable, Error = error };

        private static Dictionary<string, TopologyNode> Probed(params TopologyNode[] nodes)
            => nodes.ToDictionary(n => n.Server, n => n, StringComparer.OrdinalIgnoreCase);

        // ── the reported defect ──────────────────────────────────────────────────

        [Fact]
        public void An_unreachable_server_is_not_added()
        {
            var candidates = DiscoveredServerTriage.Classify(
                new[] { "GOOD", "DEAD" },
                Probed(Node("GOOD", true), Node("DEAD", false, "A network-related error occurred")),
                Array.Empty<string>(), null);

            var part = DiscoveredServerTriage.PartitionForAdd(candidates);

            Assert.Equal(new[] { "GOOD" }, part.ToAdd.Select(c => c.Server).ToArray());
            Assert.Equal(new[] { "DEAD" }, part.Skipped.Select(c => c.Server).ToArray());
            Assert.Equal(1, part.UnreachableCount);
            Assert.Equal(0, part.UndeterminedCount);
        }

        [Fact]
        public void A_server_that_was_never_probed_is_undetermined_not_unreachable()
        {
            // EDGEONLY appeared as a linked-server target and the crawl stopped before probing it.
            var candidates = DiscoveredServerTriage.Classify(
                new[] { "SEED", "EDGEONLY" },
                Probed(Node("SEED", true)),
                Array.Empty<string>(),
                "Stopped at the 6-hop depth limit; more servers may exist beyond it.");

            var edge = candidates.Single(c => c.Server == "EDGEONLY");
            var part = DiscoveredServerTriage.PartitionForAdd(candidates);

            Assert.Equal(1, part.UndeterminedCount);
            Assert.Equal(0, part.UnreachableCount);
            Assert.DoesNotContain(part.ToAdd, c => c.Server == "EDGEONLY");
            Assert.Contains("nothing is known", edge.Reason);
            // The crawl's own bound is quoted, so the disclosure names the actual cause.
            Assert.Contains("6-hop depth limit", edge.Reason);
        }

        [Fact]
        public void An_unreachable_server_carries_the_probe_s_own_error_text()
        {
            var candidates = DiscoveredServerTriage.Classify(
                new[] { "DEAD" },
                Probed(Node("DEAD", false, "Login failed for user 'x'.")),
                Array.Empty<string>(), null);

            Assert.Equal("Login failed for user 'x'.", candidates.Single().Reason);
        }

        [Fact]
        public void A_failed_probe_with_no_message_still_gets_a_reason()
        {
            var candidates = DiscoveredServerTriage.Classify(
                new[] { "DEAD" }, Probed(Node("DEAD", false, null)), Array.Empty<string>(), null);

            Assert.False(string.IsNullOrWhiteSpace(candidates.Single().Reason));
        }

        [Fact]
        public void Servers_already_in_the_catalogue_are_not_candidates_at_all()
        {
            var candidates = DiscoveredServerTriage.Classify(
                new[] { "GOOD", "KNOWN" },
                Probed(Node("GOOD", true), Node("KNOWN", true)),
                new[] { "known.corp.example.com" }, null);

            Assert.Equal(new[] { "GOOD" }, candidates.Select(c => c.Server).ToArray());
        }

        // ── the disclosure is conditioned on the partition it sits beside ────────

        [Fact]
        public void Nothing_skipped_means_no_skip_notice_is_printed_at_all()
        {
            var candidates = DiscoveredServerTriage.Classify(
                new[] { "GOOD" }, Probed(Node("GOOD", true)), Array.Empty<string>(), null);

            Assert.Null(DiscoveredServerTriage.DescribeSkipped(
                DiscoveredServerTriage.PartitionForAdd(candidates)));
        }

        [Fact]
        public void The_notice_names_both_reasons_separately_and_counts_them()
        {
            var candidates = DiscoveredServerTriage.Classify(
                new[] { "GOOD", "DEAD1", "DEAD2", "EDGE" },
                Probed(Node("GOOD", true), Node("DEAD1", false, "timeout"), Node("DEAD2", false, "refused")),
                Array.Empty<string>(), null);

            var notice = DiscoveredServerTriage.DescribeSkipped(
                DiscoveredServerTriage.PartitionForAdd(candidates));

            Assert.NotNull(notice);
            Assert.Contains("3 discovered servers were not added", notice!);
            Assert.Contains("2 could not be reached", notice!);
            Assert.Contains("1 was never probed, so its reachability is unknown", notice!);
            Assert.DoesNotContain("1 were never probed", notice!);
        }

        [Fact]
        public void Two_unprobed_servers_take_the_plural_form()
        {
            var candidates = DiscoveredServerTriage.Classify(
                new[] { "EDGE1", "EDGE2" }, Probed(), Array.Empty<string>(), null);

            var notice = DiscoveredServerTriage.DescribeSkipped(
                DiscoveredServerTriage.PartitionForAdd(candidates))!;

            Assert.Contains("2 were never probed, so their reachability is unknown", notice);
        }

        // ── the headline is counted across all three states ──────────────────────
        //
        // It read "N reachable" beside a skip notice reading "1 was never probed". A server that
        // was never probed is not known to be unreachable, so a count called "reachable" that
        // excludes it asserts a measurement nobody took, and the two lines describing one set
        // disagreed. Both lines are now arithmetic on the same partition.

        [Fact]
        public void The_headline_names_every_state_it_has_a_count_for()
        {
            var candidates = DiscoveredServerTriage.Classify(
                new[] { "GOOD", "DEAD", "EDGE" },
                Probed(Node("GOOD", true), Node("DEAD", false, "timeout")),
                Array.Empty<string>(), null);

            var census = DiscoveredServerTriage.DescribeCensus(
                DiscoveredServerTriage.PartitionForAdd(candidates));

            Assert.Contains("3 discovered servers not in your catalogue", census);
            Assert.Contains("1 reached", census);
            Assert.Contains("1 could not be reached", census);
            Assert.Contains("1 never probed", census);
            Assert.DoesNotContain("reachable", census);
        }

        [Fact]
        public void A_headline_with_nothing_skipped_claims_only_what_was_reached()
        {
            var candidates = DiscoveredServerTriage.Classify(
                new[] { "GOOD" }, Probed(Node("GOOD", true)), Array.Empty<string>(), null);

            var census = DiscoveredServerTriage.DescribeCensus(
                DiscoveredServerTriage.PartitionForAdd(candidates));

            Assert.Contains("1 discovered server not in your catalogue: 1 reached.", census);
            Assert.DoesNotContain("never probed", census);
            Assert.DoesNotContain("could not be reached", census);
        }

        [Fact]
        public void The_headline_and_the_skip_notice_agree_on_every_count()
        {
            var candidates = DiscoveredServerTriage.Classify(
                new[] { "DEAD1", "DEAD2", "EDGE" },
                Probed(Node("DEAD1", false, "timeout"), Node("DEAD2", false, "refused")),
                Array.Empty<string>(), null);
            var part = DiscoveredServerTriage.PartitionForAdd(candidates);

            var census = DiscoveredServerTriage.DescribeCensus(part);
            var skipped = DiscoveredServerTriage.DescribeSkipped(part)!;

            // The contradiction that was shipped: "0 reachable" beside "1 was never probed".
            Assert.Contains("0 reached", census);
            Assert.Contains("1 never probed", census);
            Assert.Contains("1 was never probed", skipped);
            Assert.Contains("2 could not be reached", census);
            Assert.Contains("2 could not be reached", skipped);
        }

        [Fact]
        public void Nothing_reached_prints_the_reasons_rather_than_a_verdict_on_reachability()
        {
            var candidates = DiscoveredServerTriage.Classify(
                new[] { "DEAD", "EDGE" }, Probed(Node("DEAD", false, "timeout")),
                Array.Empty<string>(), null);

            var line = DiscoveredServerTriage.DescribeNothingToAdd(
                DiscoveredServerTriage.PartitionForAdd(candidates))!;

            Assert.Contains("0 reached", line);
            Assert.Contains("1 could not be reached", line);
            Assert.Contains("1 was never probed", line);
            Assert.DoesNotContain("None of them", line);
        }

        [Fact]
        public void The_nothing_to_add_line_is_absent_whenever_there_is_something_to_add()
        {
            var candidates = DiscoveredServerTriage.Classify(
                new[] { "GOOD", "EDGE" }, Probed(Node("GOOD", true)), Array.Empty<string>(), null);

            Assert.Null(DiscoveredServerTriage.DescribeNothingToAdd(
                DiscoveredServerTriage.PartitionForAdd(candidates)));
        }

        [Fact]
        public void A_notice_about_only_unreachable_servers_says_nothing_about_unprobed_ones()
        {
            var candidates = DiscoveredServerTriage.Classify(
                new[] { "DEAD" }, Probed(Node("DEAD", false, "timeout")), Array.Empty<string>(), null);

            var notice = DiscoveredServerTriage.DescribeSkipped(
                DiscoveredServerTriage.PartitionForAdd(candidates))!;

            Assert.Contains("1 could not be reached", notice);
            Assert.DoesNotContain("never probed", notice);
        }

        [Fact]
        public void Every_skipped_server_is_individually_listable_with_its_own_reason()
        {
            var candidates = DiscoveredServerTriage.Classify(
                new[] { "DEAD", "EDGE" }, Probed(Node("DEAD", false, "timeout")),
                Array.Empty<string>(), null);

            var part = DiscoveredServerTriage.PartitionForAdd(candidates);

            Assert.Equal(2, part.Skipped.Count);
            Assert.All(part.Skipped, s => Assert.False(string.IsNullOrWhiteSpace(s.Reason)));
        }

        [Fact]
        public void When_nothing_was_reached_the_add_set_is_empty_rather_than_a_fallback_to_everything()
        {
            var candidates = DiscoveredServerTriage.Classify(
                new[] { "DEAD", "EDGE" }, Probed(Node("DEAD", false, "timeout")),
                Array.Empty<string>(), null);

            Assert.Empty(DiscoveredServerTriage.PartitionForAdd(candidates).ToAdd);
        }

        // ── the same three states, applied to a single on-demand probe ───────────

        [Fact]
        public void A_successful_probe_is_Reached()
        {
            var o = ServerReachabilityCheck.Classify(null, cancelled: false);
            Assert.Equal(AlertEvaluationService.ServerReachability.Reached, o.State);
            Assert.Equal("", o.Reason);
        }

        [Theory]
        [InlineData(typeof(TimeoutException))]
        [InlineData(typeof(SocketException))]
        public void A_transport_or_timeout_failure_is_Unreachable(Type exceptionType)
        {
            var ex = exceptionType == typeof(SocketException)
                ? (Exception)new SocketException(10061)
                : new TimeoutException("Connection Timeout Expired.");

            var o = ServerReachabilityCheck.Classify(ex, cancelled: false);
            Assert.Equal(AlertEvaluationService.ServerReachability.Unreachable, o.State);
            Assert.False(string.IsNullOrWhiteSpace(o.Reason));
        }

        [Fact]
        public void A_cancelled_probe_is_Undetermined_never_Unreachable()
        {
            Assert.Equal(AlertEvaluationService.ServerReachability.Undetermined,
                ServerReachabilityCheck.Classify(null, cancelled: true).State);
            Assert.Equal(AlertEvaluationService.ServerReachability.Undetermined,
                ServerReachabilityCheck.Classify(new OperationCanceledException(), false).State);
        }

        [Fact]
        public void A_fault_that_is_ours_is_Undetermined_and_says_so()
        {
            // A platform/config fault says nothing about the server. Calling it Unreachable would
            // condemn a healthy server for a defect in our own code — the inverted-predicate class.
            var o = ServerReachabilityCheck.Classify(new PlatformNotSupportedException("no SSPI here"), false);

            Assert.Equal(AlertEvaluationService.ServerReachability.Undetermined, o.State);
            Assert.Contains("nothing was learned", o.Reason);
        }
    }
}
