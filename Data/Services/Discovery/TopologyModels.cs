// In the name of God, the Merciful, the Compassionate
// Dig Deeper — environment topology discovery models (ephemeral; held in memory per run only).
using System;
using System.Collections.Generic;

namespace SQLTriage.Data.Services.Discovery
{
    /// <summary>
    /// Canonical server-instance identity. AD SPNs yield NetBIOS short names while linked servers,
    /// AG replicas and catalogue entries often carry FQDNs, ports, or different casing — without one
    /// canonical form the same machine becomes several graph nodes and catalogue matching misfires.
    /// </summary>
    public static class ServerNames
    {
        /// <summary>HOST or HOST\INSTANCE — uppercase, no domain suffix, no port, no protocol prefix.</summary>
        public static string Normalize(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var s = name.Trim();
            if (s.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) s = s.Substring(4);

            string host = s, instance = "";
            var slash = s.IndexOf('\\');
            if (slash >= 0) { host = s.Substring(0, slash); instance = s.Substring(slash + 1); }

            // Drop a trailing port (HOST,1433 or HOST:1433) from the host part.
            var sep = host.IndexOfAny(new[] { ',', ':' });
            if (sep > 0) host = host.Substring(0, sep);

            // FQDN -> NetBIOS label, but leave IP addresses intact.
            if (!System.Net.IPAddress.TryParse(host, out _))
            {
                var dot = host.IndexOf('.');
                if (dot > 0) host = host.Substring(0, dot);
            }

            host = host.Trim().ToUpperInvariant();
            instance = instance.Trim().ToUpperInvariant();
            if (instance == "MSSQLSERVER") instance = "";   // explicit default instance == bare host
            if (host.Length == 0) return "";
            return instance.Length > 0 ? host + "\\" + instance : host;
        }
    }

    /// <summary>How two servers are related (drives edge colour + meaning in the topology graph).</summary>
    public enum EdgeKind
    {
        LinkedServer,
        ReplicationPublisher,   // discovered server publishes to / is the publisher of this one
        ReplicationSubscriber,  // discovered server subscribes from this one
        AgReplica,              // Always On availability-group replica
        MirrorPartner,          // database-mirroring partner
        LogShipPrimary,         // discovered server is the log-shipping primary for this one
        LogShipSecondary,       // discovered server is a log-shipping secondary of this one
    }

    /// <summary>A SQL Server instance node in the discovered topology.</summary>
    public sealed class TopologyNode
    {
        public string Server { get; set; } = "";
        public bool Reachable { get; set; }
        public string? Error { get; set; }
        /// <summary>Aggregate count of distinct client hosts connected (NOT rendered as individual nodes).</summary>
        public int ClientCount { get; set; }
        /// <summary>True if this server was found via AD SPN enumeration (vs reached by crawling an edge).</summary>
        public bool FromActiveDirectory { get; set; }
        /// <summary>True if this server is already in the connection catalogue.</summary>
        public bool InCatalogue { get; set; }
        public int Depth { get; set; }
    }

    /// <summary>A directed relationship between two servers (from = the probed server).</summary>
    public sealed class TopologyEdge
    {
        public string FromServer { get; set; } = "";
        public string ToServer { get; set; } = "";
        public EdgeKind Kind { get; set; }
        public string? Detail { get; set; }
    }

    /// <summary>Result of probing one server's outbound topology (read-only).</summary>
    public sealed class ProbeResult
    {
        public string Server { get; set; } = "";
        public bool Reachable { get; set; }
        public string? Error { get; set; }
        public int ClientCount { get; set; }
        public List<(string Target, EdgeKind Kind, string Detail)> Edges { get; } = new();
    }

    /// <summary>One streamed update from the crawl so the UI can grow the graph live.</summary>
    public sealed class TopologyDelta
    {
        public TopologyNode? Node { get; set; }
        public List<TopologyEdge> Edges { get; set; } = new();
        public string Progress { get; set; } = "";
        public bool Done { get; set; }
        /// <summary>Set when the crawl stopped early because a bound was hit (never silently truncate).</summary>
        public string? TruncationNote { get; set; }
    }

    /// <summary>Bounds for one discovery run (atomic; not persisted).</summary>
    public sealed class DiscoveryOptions
    {
        public bool UseActiveDirectory { get; set; } = true;
        public int MaxServers { get; set; } = 250;
        public int MaxDepth { get; set; } = 6;
        public int PerServerTimeoutSeconds { get; set; } = 10;
        public int MaxConcurrency { get; set; } = 8;
    }
}
