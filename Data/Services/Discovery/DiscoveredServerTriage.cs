/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;

namespace SQLTriage.Data.Services.Discovery
{
    /// <summary>One discovered server, with what the crawl actually observed about reaching it.</summary>
    public sealed class DiscoveredCandidate
    {
        public string Server { get; init; } = "";
        internal AlertEvaluationService.ServerReachability Reachability { get; init; }
        /// <summary>The measured reason. Empty for a server that was reached.</summary>
        public string Reason { get; init; } = "";

        internal bool WasReached => Reachability == AlertEvaluationService.ServerReachability.Reached;
    }

    /// <summary>The split "add all" acts on, plus everything it did not act on and why.</summary>
    public sealed class AddAllPartition
    {
        public List<DiscoveredCandidate> ToAdd { get; } = new();
        public List<DiscoveredCandidate> Skipped { get; } = new();

        public int UnreachableCount { get; internal set; }
        public int UndeterminedCount { get; internal set; }
    }

    /// <summary>
    /// Classifies Dig Deeper's discovered servers for the "add to catalogue" action.
    ///
    /// <para><b>The defect this closes.</b> "Add all to catalogue" filtered on one thing only —
    /// "not already in the catalogue" — and wrote every remaining name into a single bulk
    /// connection. Servers the crawl had just failed to connect to went in beside the healthy
    /// ones, because <c>TopologyNode.Reachable</c> was never consulted.</para>
    ///
    /// <para><b>Why three states and not a boolean.</b> The crawl emits a node only for servers it
    /// actually probed. A server can also enter the discovered set as the far end of an edge —
    /// a linked-server target, an AG replica — and then never be probed at all, because the
    /// depth cap or the 250-server cap cut the frontier, the run was cancelled, or the probe task
    /// faulted. That server has no node: <c>Reachable</c> is not false, it is unknown. Folding
    /// unknown into "unreachable" would silently drop servers that are probably fine; folding it
    /// into "reachable" is the exact false-signal bug the circuit-breaker fix (e231bcc) removed
    /// from the alert loop. So this reuses that fix's enum,
    /// <see cref="AlertEvaluationService.ServerReachability"/>, with its rule intact:
    /// Undetermined is a state, not a synonym for success.</para>
    ///
    /// <para>Only <c>Reached</c> servers are added. Everything else is disclosed on screen with
    /// its reason — an add-all that silently drops servers is the same honesty defect as one
    /// that silently adds dead ones.</para>
    /// </summary>
    public static class DiscoveredServerTriage
    {
        /// <summary>
        /// Classifies every discovered server that is not already in the catalogue.
        /// </summary>
        /// <param name="discovered">Every server name the crawl surfaced (probed or edge-only).</param>
        /// <param name="probed">The nodes the crawl actually emitted, keyed by server name.
        /// A name absent from this map was never probed.</param>
        /// <param name="catalogueServers">Server names already in the connection catalogue.</param>
        /// <param name="truncationNote">The crawl's own note when a bound stopped it, quoted into
        /// the reason for never-probed servers so the disclosure names the actual cause.</param>
        public static List<DiscoveredCandidate> Classify(
            IEnumerable<string> discovered,
            IReadOnlyDictionary<string, TopologyNode> probed,
            IEnumerable<string> catalogueServers,
            string? truncationNote)
        {
            var catalogue = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in catalogueServers)
            {
                var k = ServerNames.Normalize(c);
                if (k.Length > 0) catalogue.Add(k);
            }

            var notProbedReason = string.IsNullOrWhiteSpace(truncationNote)
                ? "not probed during this discovery run, so nothing is known about reaching it"
                : $"not probed during this discovery run, so nothing is known about reaching it — {truncationNote.Trim()}";

            var result = new List<DiscoveredCandidate>();
            foreach (var s in discovered)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                if (catalogue.Contains(ServerNames.Normalize(s))) continue;

                if (probed != null && probed.TryGetValue(s, out var node) && node != null)
                {
                    if (node.Reachable)
                    {
                        result.Add(new DiscoveredCandidate
                        {
                            Server = s,
                            Reachability = AlertEvaluationService.ServerReachability.Reached,
                            Reason = ""
                        });
                    }
                    else
                    {
                        var why = string.IsNullOrWhiteSpace(node.Error) ? "the probe did not connect" : node.Error!.Trim();
                        result.Add(new DiscoveredCandidate
                        {
                            Server = s,
                            Reachability = AlertEvaluationService.ServerReachability.Unreachable,
                            Reason = why
                        });
                    }
                }
                else
                {
                    result.Add(new DiscoveredCandidate
                    {
                        Server = s,
                        Reachability = AlertEvaluationService.ServerReachability.Undetermined,
                        Reason = notProbedReason
                    });
                }
            }

            return result
                .OrderBy(c => c.Server, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Splits the candidates into what "add all" writes and what it refuses to write.</summary>
        public static AddAllPartition PartitionForAdd(IEnumerable<DiscoveredCandidate> candidates)
        {
            var p = new AddAllPartition();
            foreach (var c in candidates)
            {
                if (c.WasReached) { p.ToAdd.Add(c); continue; }
                p.Skipped.Add(c);
                if (c.Reachability == AlertEvaluationService.ServerReachability.Unreachable) p.UnreachableCount++;
                else p.UndeterminedCount++;
            }
            return p;
        }

        /// <summary>
        /// The one sentence the button prints about what it skipped, derived from the partition
        /// it is about to act on. No partition, no sentence: this returns null when nothing was
        /// skipped, so the UI cannot print a skip notice on a run that skipped nothing.
        /// </summary>
        public static string? DescribeSkipped(AddAllPartition partition)
        {
            if (partition == null || partition.Skipped.Count == 0) return null;

            var parts = new List<string>();
            if (partition.UnreachableCount > 0)
                parts.Add($"{partition.UnreachableCount} could not be reached");
            if (partition.UndeterminedCount > 0)
            {
                var u = partition.UndeterminedCount;
                parts.Add(u == 1
                    ? "1 was never probed, so its reachability is unknown"
                    : $"{u} were never probed, so their reachability is unknown");
            }

            var n = partition.Skipped.Count;
            return $"{n} discovered server{(n == 1 ? "" : "s")} {(n == 1 ? "was" : "were")} not added: "
                 + string.Join("; ", parts) + ".";
        }

        /// <summary>
        /// The headline above the button, counted across all three states.
        ///
        /// <para><b>Why not "N reachable".</b> The headline said "<c>N</c> reachable" beside a skip
        /// notice saying "1 was never probed". Those two sentences describe the same set and
        /// disagree: a server that was never probed is not known to be unreachable, so subtracting
        /// it from a count called "reachable" asserts something no probe measured. The headline now
        /// reports the observation ("reached") and names the never-probed as their own state, so
        /// the two lines are arithmetic on one partition and cannot contradict each other.</para>
        ///
        /// <para>A state with a count of zero is not printed, except <c>reached</c> — that is the
        /// number the button acts on, and "0 reached" is the thing an operator needs to see.</para>
        /// </summary>
        public static string DescribeCensus(AddAllPartition partition)
        {
            if (partition == null) return "No discovery run has finished.";

            var total = partition.ToAdd.Count + partition.Skipped.Count;
            var parts = new List<string> { $"{partition.ToAdd.Count} reached" };
            if (partition.UnreachableCount > 0)
                parts.Add($"{partition.UnreachableCount} could not be reached");
            if (partition.UndeterminedCount > 0)
                parts.Add($"{partition.UndeterminedCount} never probed");

            return $"{total} discovered server{(total == 1 ? "" : "s")} not in your catalogue: "
                 + string.Join(", ", parts) + ".";
        }

        /// <summary>
        /// What stands where the button would be when nothing was reached. Derived from the same
        /// partition as <see cref="DescribeCensus"/>, so it restates the census rather than
        /// asserting a second, softer version of it. Returns null whenever there IS something to
        /// add, so this sentence cannot appear next to an enabled button.
        /// </summary>
        public static string? DescribeNothingToAdd(AddAllPartition partition)
        {
            if (partition == null || partition.ToAdd.Count > 0) return null;
            if (partition.Skipped.Count == 0) return "Nothing was discovered outside your catalogue.";

            var parts = new List<string>();
            if (partition.UnreachableCount > 0)
                parts.Add($"{partition.UnreachableCount} could not be reached");
            if (partition.UndeterminedCount > 0)
            {
                var u = partition.UndeterminedCount;
                parts.Add(u == 1 ? "1 was never probed" : $"{u} were never probed");
            }

            return "0 reached, so there is nothing to add (" + string.Join(", ", parts) + ").";
        }
    }
}
