// In the name of God, the Merciful, the Compassionate
// Orchestrates environment topology discovery: AD SPN seed set + a bounded, cancellable BFS crawl over
// server-to-server topology. Ephemeral — no state persists between runs (atomic). Streams deltas so the UI
// can grow the graph live. Read-only throughout.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services.Discovery
{
    public sealed class EnvironmentDiscoveryService
    {
        private readonly AdServerLocator _ad;
        private readonly SqlTopologyProbe _probe;
        private readonly ILogger<EnvironmentDiscoveryService>? _log;

        public EnvironmentDiscoveryService(AdServerLocator ad, SqlTopologyProbe probe, ILogger<EnvironmentDiscoveryService>? log = null)
        {
            _ad = ad;
            _probe = probe;
            _log = log;
        }

        /// <summary>
        /// Discover the estate starting from <paramref name="seedServer"/> (reached via <paramref name="seed"/>'s
        /// credentials), optionally seeded with AD-enumerated servers. Calls <paramref name="onDelta"/> once per
        /// probed server (node + its edges) and once more with Done=true. All state is local to this call.
        /// </summary>
        public async Task DiscoverAsync(
            ServerConnection seed,
            string seedServer,
            DiscoveryOptions options,
            ISet<string> catalogueServers,
            Func<TopologyDelta, Task> onDelta,
            CancellationToken ct)
        {
            // Identity is the normalized name (NetBIOS, uppercase, no port); probing uses the raw
            // first-seen name so an FQDN-only DNS setup still resolves.
            var visited = new HashSet<string>(StringComparer.Ordinal);          // normalized keys
            var adServers = new HashSet<string>(StringComparer.Ordinal);        // normalized keys
            var catalogue = new HashSet<string>(StringComparer.Ordinal);        // normalized keys
            foreach (var c in catalogueServers) catalogue.Add(ServerNames.Normalize(c));
            string? truncation = null;

            // Build the depth-0 frontier: the seed + (optionally) the AD-discovered estate.
            var frontier = new List<string>();
            void TryEnqueue(string s, List<string> into)
            {
                var key = ServerNames.Normalize(s);
                if (key.Length == 0) return;
                if (visited.Contains(key)) return;
                if (visited.Count >= options.MaxServers) { truncation = $"Stopped at the {options.MaxServers}-server cap."; return; }
                visited.Add(key);
                into.Add(s.Trim());
            }

            TryEnqueue(seedServer, frontier);
            if (options.UseActiveDirectory)
            {
                foreach (var s in _ad.FindSqlServers())
                {
                    adServers.Add(ServerNames.Normalize(s));
                    TryEnqueue(s, frontier);
                }
            }

            int probed = 0;
            for (int depth = 0; depth <= options.MaxDepth && frontier.Count > 0; depth++)
            {
                ct.ThrowIfCancellationRequested();
                var nextTargets = new List<string>();
                using var gate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrency));

                var depthLocal = depth;
                var tasks = frontier.Select(async server =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try { return await _probe.ProbeAsync(seed, server, options.PerServerTimeoutSeconds, ct).ConfigureAwait(false); }
                    finally { gate.Release(); }
                }).ToList();

                foreach (var t in tasks)
                {
                    ct.ThrowIfCancellationRequested();
                    ProbeResult r;
                    try { r = await t.ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { _log?.LogDebug(ex, "probe task faulted"); continue; }

                    probed++;
                    var serverKey = ServerNames.Normalize(r.Server);
                    var node = new TopologyNode
                    {
                        Server = serverKey,
                        Reachable = r.Reachable,
                        Error = r.Error,
                        ClientCount = r.ClientCount,
                        Depth = depthLocal,
                        FromActiveDirectory = adServers.Contains(serverKey),
                        InCatalogue = catalogue.Contains(serverKey),
                    };
                    // Edge endpoints use normalized names so the graph joins up; raw targets are kept
                    // separately for the next probe round.
                    var edges = r.Edges
                        .Select(e => new TopologyEdge
                        {
                            FromServer = serverKey,
                            ToServer = ServerNames.Normalize(e.Target),
                            Kind = e.Kind,
                            Detail = e.Detail,
                        })
                        .Where(e => e.ToServer.Length > 0 && e.ToServer != serverKey)
                        .ToList();

                    await onDelta(new TopologyDelta
                    {
                        Node = node,
                        Edges = edges,
                        Progress = $"Probed {probed} server(s); frontier depth {depthLocal}.",
                    }).ConfigureAwait(false);

                    foreach (var e in r.Edges) nextTargets.Add(e.Target); // raw names — TryEnqueue dedups on the normalized key
                }

                // Build next frontier (dedup + cap).
                var nf = new List<string>();
                foreach (var tgt in nextTargets.Distinct(StringComparer.OrdinalIgnoreCase))
                    TryEnqueue(tgt, nf);
                frontier = nf;

                if (truncation != null && depth < options.MaxDepth && frontier.Count == 0) break;
            }

            if (frontier.Count > 0 && truncation == null)
                truncation = $"Stopped at the {options.MaxDepth}-hop depth limit; more servers may exist beyond it.";

            await onDelta(new TopologyDelta { Done = true, Progress = $"Discovery complete: {probed} server(s).", TruncationNote = truncation }).ConfigureAwait(false);
        }
    }
}
