/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SQLTriage.Data.Services.Discovery;

namespace SQLTriage.Data.Services.Replication
{
    /// <summary>
    /// One publication -> subscription link exactly as the distributor recorded it.
    /// <para><b>Names and ids are carried separately, deliberately.</b> The distribution
    /// database stores each end of a link as a <c>server_id</c> into the distributor's
    /// <c>master.sys.servers</c>. When that join finds no row, there is no server NAME to
    /// show — only an id. The earlier query collapsed the two with
    /// <c>COALESCE(pub.name, CAST(da.publisher_id AS sysname))</c>, so an unresolved id was
    /// handed to the UI wearing a server name's clothes and rendered verbatim as a node
    /// label ("-1"). The model keeps them apart so the UI can say which one it has.</para>
    /// </summary>
    public sealed class ReplicationLink
    {
        /// <summary>The server SQLTriage queried as the distributor to obtain this row.</summary>
        public string Distributor { get; set; } = "";

        /// <summary>Publisher name from master.sys.servers, or null when the id did not resolve.</summary>
        public string? PublisherName { get; set; }
        /// <summary>publisher_id as recorded in MSdistribution_agents (null when the column was NULL).</summary>
        public int? PublisherId { get; set; }
        public string PublisherDb { get; set; } = "";
        public string Publication { get; set; } = "";

        /// <summary>Subscriber name from master.sys.servers, or null when the id did not resolve.</summary>
        public string? SubscriberName { get; set; }
        /// <summary>subscriber_id as recorded in MSdistribution_agents (null when the column was NULL).</summary>
        public int? SubscriberId { get; set; }
        public string SubscriberDb { get; set; } = "";

        public string Agent { get; set; } = "";
        public string LastMessage { get; set; } = "";
        public int? RunStatus { get; set; }
        public long? LatencyMs { get; set; }
        public DateTime? LastActivity { get; set; }
    }

    /// <summary>What SQLTriage actually has for one end of a replication link.</summary>
    public enum ServerNameResolution
    {
        /// <summary>A real server name came back from master.sys.servers on the distributor.</summary>
        Resolved = 0,
        /// <summary>Only an id: the distribution metadata's server_id matched no row in
        /// master.sys.servers on the distributor. The id is printed, never dressed as a name.</summary>
        UnresolvedId,
        /// <summary>Neither a name nor an id was recorded for this end of the link.</summary>
        Absent
    }

    /// <summary>The honest rendering of one end of a link: what to print, and why.</summary>
    public sealed class ResolvedServerName
    {
        public ServerNameResolution Resolution { get; init; }
        /// <summary>The label the UI prints. Never a bare sentinel: an unresolved id reads
        /// "server id -1", which cannot be mistaken for a server called "-1".</summary>
        public string Label { get; init; } = "";
        /// <summary>The measured explanation, or null when the name resolved normally.</summary>
        public string? Detail { get; init; }
        /// <summary>Grouping/dedup identity. Resolved names use the canonical
        /// <see cref="ServerNames.Normalize"/> form so an FQDN and a NetBIOS name are one node.</summary>
        public string Key { get; init; } = "";
        /// <summary>True only when a real server name was read. Everything that acts on a
        /// server name (probe it, offer to add it) must test this first.</summary>
        public bool IsRealServerName => Resolution == ServerNameResolution.Resolved;
    }

    /// <summary>Turns a (name, id) pair from the distribution metadata into what the UI prints.</summary>
    public static class ReplicationNaming
    {
        public static ResolvedServerName Describe(string? name, int? serverId, string distributor)
        {
            var dist = string.IsNullOrWhiteSpace(distributor) ? "the distributor" : distributor;

            if (!string.IsNullOrWhiteSpace(name))
            {
                var trimmed = name.Trim();
                return new ResolvedServerName
                {
                    Resolution = ServerNameResolution.Resolved,
                    Label = trimmed,
                    Key = ServerNames.Normalize(trimmed) is { Length: > 0 } k ? k : trimmed.ToUpperInvariant(),
                    Detail = null
                };
            }

            if (serverId.HasValue)
            {
                var id = serverId.Value.ToString(CultureInfo.InvariantCulture);
                return new ResolvedServerName
                {
                    Resolution = ServerNameResolution.UnresolvedId,
                    Label = $"server id {id}",
                    Key = "#id:" + id,
                    // Measured, not inferred: the query LEFT JOINs master.sys.servers on
                    // server_id, and sys.servers.name is never NULL, so a NULL name means no
                    // row matched. What the id MEANS (a local publisher, a dropped server, a
                    // replication-internal placeholder) is not something this scan measured,
                    // so it is not asserted here.
                    Detail = $"The distribution metadata on {dist} recorded server id {id} for this end of the link, "
                           + $"and no row in {dist}'s master.sys.servers has that server_id. SQLTriage shows the recorded id "
                           + "rather than guessing which server it stands for."
                };
            }

            return new ResolvedServerName
            {
                Resolution = ServerNameResolution.Absent,
                Label = "server not recorded",
                Key = "#unrecorded",
                Detail = $"The distribution metadata on {dist} recorded neither a server name nor a server id for this end of the link."
            };
        }
    }

    /// <summary>Distribution-agent health, derived from runstatus + delivery latency.</summary>
    public static class ReplicationHealth
    {
        // runstatus: 1 start, 2 succeed, 3 in-progress, 4 idle, 5 retry, 6 fail
        public static (string Status, string Text) Classify(int? runStatus, long? latencyMs)
        {
            if (runStatus is null) return ("unknown", "No agent history");
            switch (runStatus.Value)
            {
                case 6: return ("bad", "Failed");
                case 5: return ("warn", "Retrying");
                case 1:
                case 2:
                case 3:
                case 4:
                    if (latencyMs is > 300000) return ("warn", "Running (high latency)");
                    return ("ok", runStatus.Value == 4 ? "Idle (caught up)" : "Running");
                default: return ("unknown", $"Status {runStatus.Value}");
            }
        }
    }

    public enum ReplicationItemKind
    {
        DistributorRole = 0,
        Publication,
        Subscription
    }

    /// <summary>One replication item nested under the instance that owns it.</summary>
    public sealed class ReplicationItem
    {
        public ReplicationItemKind Kind { get; init; }
        public string Title { get; init; } = "";
        public string Detail { get; init; } = "";
        public string Status { get; init; } = "unknown";
        public string StatusText { get; init; } = "";
    }

    /// <summary>Every replication item one SQL instance owns, whatever role it plays.</summary>
    public sealed class ReplicationInstanceGroup
    {
        public string Key { get; init; } = "";
        public string Display { get; init; } = "";
        public bool NameResolved { get; init; }
        /// <summary>Set only when <see cref="NameResolved"/> is false — the measured reason.</summary>
        public string? UnresolvedDetail { get; set; }
        public bool IsDistributor { get; set; }
        public bool IsPublisher { get; set; }
        public bool IsSubscriber { get; set; }
        public List<ReplicationItem> Items { get; } = new();

        public string RoleSummary
        {
            get
            {
                var roles = new List<string>();
                if (IsDistributor) roles.Add("distributor");
                if (IsPublisher) roles.Add("publisher");
                if (IsSubscriber) roles.Add("subscriber");
                return roles.Count == 0 ? "no role observed" : string.Join(" · ", roles);
            }
        }
    }

    /// <summary>Participants named in the topology that this install has no connection for.</summary>
    public sealed class ParticipantCandidates
    {
        /// <summary>Server names that can be offered for adding: a real name, not in the catalogue.</summary>
        public List<ParticipantCandidate> Addable { get; } = new();
        /// <summary>Ends of links that cannot be offered because SQLTriage has no server NAME
        /// for them (an unresolved id or nothing recorded). Disclosed, never silently dropped.</summary>
        public List<string> NotNameable { get; } = new();
        /// <summary>True when these candidates came from the synthetic sample topology rather
        /// than from a server. Every action offered on them is gated on this.</summary>
        public bool Fabricated { get; internal set; }
    }

    public sealed class ParticipantCandidate
    {
        public string Server { get; init; } = "";
        public string Key { get; init; } = "";
        /// <summary>The distributor whose scan named this participant — also the connection
        /// whose credentials the reachability probe borrows.</summary>
        public string ViaDistributor { get; init; } = "";
        public string ViaConnectionId { get; init; } = "";
        /// <summary>True when this name was invented by <c>Sample</c>, not read from a server.
        /// A fabricated name must never leave this page as if it were a discovered one.</summary>
        public bool Fabricated { get; init; }
    }

    /// <summary>
    /// What the participants table may DO with one candidate.
    ///
    /// <para><b>The defect this closes.</b> The Sample button renders a synthetic topology whose
    /// participants (SQLPUB01, SQLSUB03 …) are invented. Those rows carried the same
    /// <c>Add…</c> button as scanned rows, so one click handed a fabricated name to
    /// <c>/servers?add=</c> and the add-connection dialog opened pre-filled with it, with
    /// nothing on that page saying where it came from. The operator would have been reviewing
    /// a name SQLTriage made up, believing it had been discovered.</para>
    ///
    /// <para>The gate is here, on the model, rather than in the markup: an <c>@if</c> around a
    /// button hides an action, it does not refuse it. Both the button and the handler ask this.</para>
    /// </summary>
    public static class ParticipantOffer
    {
        /// <summary>May this candidate's name be handed to the add-connection dialog?</summary>
        public static bool MayHandOffToAddDialog(ParticipantCandidate? c)
            => c != null && !c.Fabricated && !string.IsNullOrWhiteSpace(c.Server);

        /// <summary>May a reachability probe be run against this candidate? A fabricated name
        /// has nothing behind it, so the probe would report on a server that does not exist.</summary>
        public static bool MayProbe(ParticipantCandidate? c)
            => c != null && !c.Fabricated && !string.IsNullOrWhiteSpace(c.Server);

        /// <summary>What the Reachability column prints where a probe is refused, saying which
        /// state it is in rather than leaving a blank that reads as "not yet".</summary>
        public static string ProbeRefusedLabel => "Sample data — not probed";

        /// <summary>
        /// Why a participant could not be probed, conditioned on what was actually observed.
        ///
        /// <para>Three different facts used to print one sentence, which claimed the connection
        /// that discovered the server had been REMOVED from the catalogue. That is only true when
        /// a connection id was recorded and is now absent. When no id was recorded at all —
        /// sample mode, or a scan that produced no provenance for this end — nothing was observed
        /// about any removal, so nothing is claimed about one.</para>
        /// </summary>
        public static string DescribeUnprobeable(ParticipantCandidate c, bool connectionStillInCatalogue)
        {
            if (c == null) return "there is no participant to probe.";

            if (c.Fabricated)
                return "this row is sample data: the name was generated to preview the map, "
                     + "so there is no server to probe and no connection to probe it with.";

            if (string.IsNullOrWhiteSpace(c.ViaConnectionId))
                return "no provenance was recorded for this participant, so this install has no "
                     + "credentials to probe it with. Nothing was observed about any connection "
                     + "being removed.";

            if (!connectionStillInCatalogue)
                return "the connection that discovered this server"
                     + (string.IsNullOrWhiteSpace(c.ViaDistributor) ? "" : $" (via {c.ViaDistributor})")
                     + " is no longer in the catalogue, so no credentials were available to probe with.";

            return "the probe was not run.";
        }
    }

    public enum ScanOutcomeKind
    {
        LinksFound = 0,
        NoReplicationMetadata,
        Failed
    }

    /// <summary>What one scanned server actually returned. One per server attempted.</summary>
    public sealed class ScanOutcome
    {
        public string Server { get; init; } = "";
        public ScanOutcomeKind Kind { get; init; }
        public int Links { get; init; }
        public string Detail { get; init; } = "";
    }

    public static class ReplicationTopology
    {
        /// <summary>
        /// The scan's own summary line, derived only from the per-server outcomes it produced.
        /// A server that failed is never folded into "no replication found" — those are different
        /// facts, and the empty map used to print the second when it had measured the first.
        /// </summary>
        public static string DescribeScan(IReadOnlyList<ScanOutcome> outcomes)
        {
            if (outcomes == null || outcomes.Count == 0) return "No server has been scanned yet.";

            var withLinks = outcomes.Where(o => o.Kind == ScanOutcomeKind.LinksFound).ToList();
            var empty = outcomes.Count(o => o.Kind == ScanOutcomeKind.NoReplicationMetadata);
            var failed = outcomes.Count(o => o.Kind == ScanOutcomeKind.Failed);
            var links = withLinks.Sum(o => o.Links);

            var parts = new List<string>();
            if (withLinks.Count > 0)
                parts.Add($"{withLinks.Count} returned replication metadata ({links} link{(links == 1 ? "" : "s")})");
            if (empty > 0)
                parts.Add($"{empty} returned none");
            if (failed > 0)
                parts.Add($"{failed} could not be read");

            var n = outcomes.Count;
            return $"Scanned {n} server{(n == 1 ? "" : "s")}: " + string.Join(", ", parts) + ".";
        }

        /// <summary>
        /// The notice printed when any end of any link has no server name. Returns null when every
        /// end resolved — the notice cannot appear on a topology that has nothing unresolved in it.
        ///
        /// <para><b>Number agreement.</b> The notice read "1 carry a server id" — a plural verb on a
        /// count of one. Every verb and pronoun here is branched off the same number it describes:
        /// each clause off its own count, and the opening and the closing pronoun off the total, so
        /// one unresolved end reads as one end throughout the sentence.</para>
        /// </summary>
        public static string? DescribeUnresolved(IReadOnlyList<ReplicationLink> links)
        {
            if (links == null || links.Count == 0) return null;

            int unresolvedId = 0, unrecorded = 0;
            foreach (var l in links)
            {
                foreach (var end in new[]
                {
                    ReplicationNaming.Describe(l.PublisherName, l.PublisherId, l.Distributor),
                    ReplicationNaming.Describe(l.SubscriberName, l.SubscriberId, l.Distributor)
                })
                {
                    if (end.Resolution == ServerNameResolution.UnresolvedId) unresolvedId++;
                    else if (end.Resolution == ServerNameResolution.Absent) unrecorded++;
                }
            }

            if (unresolvedId == 0 && unrecorded == 0) return null;

            var parts = new List<string>();
            if (unresolvedId > 0)
                parts.Add(unresolvedId == 1
                    ? "1 carries a server id that the distributor's master.sys.servers does not resolve to a server"
                    : $"{unresolvedId} carry a server id that the distributor's master.sys.servers does not resolve to a server");
            if (unrecorded > 0)
                parts.Add(unrecorded == 1
                    ? "1 carries neither a server name nor an id"
                    : $"{unrecorded} carry neither a server name nor an id");

            var ends = unresolvedId + unrecorded;
            return $"{(ends == 1 ? "One end" : "Some ends")} of these links {(ends == 1 ? "has" : "have")} no server name: "
                 + string.Join("; ", parts)
                 + $". SQLTriage prints what was recorded and does not guess a name for {(ends == 1 ? "it" : "them")}.";
        }


        /// <summary>Groups every link by the SQL instance that owns each end, so publications,
        /// subscriptions and the distributor role nest under the instance rather than floating
        /// in one flat bipartite graph.</summary>
        public static List<ReplicationInstanceGroup> GroupByInstance(
            IReadOnlyList<ReplicationLink> links,
            IReadOnlyList<string>? scannedDistributors = null)
        {
            var groups = new Dictionary<string, ReplicationInstanceGroup>(StringComparer.Ordinal);

            ReplicationInstanceGroup Ensure(ResolvedServerName rn)
            {
                if (!groups.TryGetValue(rn.Key, out var g))
                {
                    g = new ReplicationInstanceGroup
                    {
                        Key = rn.Key,
                        Display = rn.Label,
                        NameResolved = rn.IsRealServerName,
                        UnresolvedDetail = rn.Detail
                    };
                    groups[rn.Key] = g;
                }
                else if (g.UnresolvedDetail == null && rn.Detail != null)
                {
                    g.UnresolvedDetail = rn.Detail;
                }
                return g;
            }

            // Distributor role first, so an instance that hosts distribution but publishes
            // nothing still appears with the role it was observed in.
            foreach (var d in (scannedDistributors ?? Array.Empty<string>()))
            {
                if (string.IsNullOrWhiteSpace(d)) continue;
                var rn = ReplicationNaming.Describe(d, null, d);
                var g = Ensure(rn);
                g.IsDistributor = true;
            }

            foreach (var l in links)
            {
                var pub = ReplicationNaming.Describe(l.PublisherName, l.PublisherId, l.Distributor);
                var sub = ReplicationNaming.Describe(l.SubscriberName, l.SubscriberId, l.Distributor);
                var (status, statusText) = ReplicationHealth.Classify(l.RunStatus, l.LatencyMs);

                var pg = Ensure(pub);
                pg.IsPublisher = true;
                pg.Items.Add(new ReplicationItem
                {
                    Kind = ReplicationItemKind.Publication,
                    Title = $"{l.PublisherDb}: {l.Publication}",
                    Detail = $"to {sub.Label}{(string.IsNullOrEmpty(l.SubscriberDb) ? "" : " · " + l.SubscriberDb)}",
                    Status = status,
                    StatusText = statusText
                });

                var sg = Ensure(sub);
                sg.IsSubscriber = true;
                sg.Items.Add(new ReplicationItem
                {
                    Kind = ReplicationItemKind.Subscription,
                    Title = $"{l.SubscriberDb}",
                    Detail = $"from {pub.Label}{(string.IsNullOrEmpty(l.PublisherDb) ? "" : " · " + l.PublisherDb)}: {l.Publication}",
                    Status = status,
                    StatusText = statusText
                });
            }

            foreach (var g in groups.Values)
            {
                if (g.IsDistributor)
                {
                    var served = links.Count(l => string.Equals(
                        ServerNames.Normalize(l.Distributor), g.Key, StringComparison.Ordinal));
                    g.Items.Insert(0, new ReplicationItem
                    {
                        Kind = ReplicationItemKind.DistributorRole,
                        Title = "Distribution database",
                        Detail = served == 1 ? "1 link recorded here" : $"{served} links recorded here",
                        Status = "unknown",
                        StatusText = ""
                    });
                }
            }

            // Resolved instances first (they are actionable), then the unresolved buckets.
            return groups.Values
                .OrderBy(g => g.NameResolved ? 0 : 1)
                .ThenBy(g => g.Display, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Participants named in the topology that are not in the connection catalogue.
        /// Only instances with a REAL server name are offered: an unresolved id is not a name
        /// you can connect to, and offering it would put the same fabricated value back into
        /// the server store the map just stopped printing. Those are returned separately so
        /// the count still adds up on screen.
        /// </summary>
        /// <param name="fabricated">True when <paramref name="groups"/> came from the synthetic
        /// sample topology. It is carried onto every candidate, because a name SQLTriage invented
        /// must not be offered to the add dialog or probed as if it were discovered.</param>
        public static ParticipantCandidates FindAddable(
            IReadOnlyList<ReplicationInstanceGroup> groups,
            IEnumerable<string> catalogueServers,
            IReadOnlyDictionary<string, (string Distributor, string ConnectionId)>? provenance = null,
            bool fabricated = false)
        {
            var catalogue = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in catalogueServers)
            {
                var k = ServerNames.Normalize(c);
                if (k.Length > 0) catalogue.Add(k);
            }

            var result = new ParticipantCandidates { Fabricated = fabricated };
            foreach (var g in groups)
            {
                if (!g.NameResolved)
                {
                    result.NotNameable.Add(g.Display);
                    continue;
                }
                if (catalogue.Contains(g.Key)) continue;

                var via = provenance != null && provenance.TryGetValue(g.Key, out var p)
                    ? p
                    : ("", "");
                result.Addable.Add(new ParticipantCandidate
                {
                    Server = g.Display,
                    Key = g.Key,
                    ViaDistributor = via.Item1,
                    ViaConnectionId = via.Item2,
                    Fabricated = fabricated
                });
            }
            return result;
        }
    }
}
