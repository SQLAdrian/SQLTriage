/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Builds the Instances dropdown by probing every enabled connection for SQLWATCH.
    ///
    /// <para><b>Lifted out of DynamicDashboard on 2026-08-07, and the reason is the whole point of
    /// this class.</b> The loop lived in a .razor file, and a .razor file cannot be exercised by
    /// this repo's test suite — so the only instruments pointed at it were source scanners, and a
    /// source scanner is what missed the ungated <c>SetCurrentServer</c> inside it for two rounds.
    /// Here the loop can be RUN, against two enabled connections, and asked what it did to the
    /// process-wide current server.</para>
    ///
    /// <para><b>Discovery never moves the current server, for any caller.</b> That is an invariant
    /// of this class rather than a permission check, and it is the stronger statement: reading what
    /// instances exist is not an act on any of them, so there is no role for which walking the
    /// singleton across the estate would be correct. The probe is handed the connection it is to
    /// use, which is what the old loop was trying to express by writing a singleton.</para>
    ///
    /// <para><b>Strategy per connection</b> (unchanged from the loop this replaces):</para>
    /// <list type="bullet">
    /// <item>SQLWATCH found → use the discovered <c>@@SERVERNAME</c> values, so <c>@SqlInstance</c>
    ///   filters match what SQLWATCH stored, and record the connection as SQLWATCH-bearing.</item>
    /// <item>SQLWATCH absent, or the probe failed → fall back to the user-configured server names so
    ///   the entry still appears in the dropdown.</item>
    /// </list>
    /// </summary>
    public sealed class SqlWatchInstanceDiscovery
    {
        private readonly IServerConnectionManager _connections;

        public SqlWatchInstanceDiscovery(IServerConnectionManager connections)
        {
            _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        }

        /// <summary>What one discovery pass found.</summary>
        /// <param name="Instances">Distinct instance names for the dropdown, in discovery order.</param>
        /// <param name="InstanceToConnectionId">Discovered SQLWATCH instance name → connection id.</param>
        /// <param name="ConnectionsWithSqlWatch">Ids of the connections that answered with SQLWATCH data.</param>
        public sealed record Result(
            string[] Instances,
            Dictionary<string, string> InstanceToConnectionId,
            HashSet<string> ConnectionsWithSqlWatch);

        /// <summary>
        /// Probes every enabled connection.
        /// </summary>
        /// <param name="probe">
        /// Asks ONE connection for its SQLWATCH instance names. Passed in rather than injected so
        /// this class carries no SQL dependency and a test can substitute a probe that records what
        /// the process-wide current server was at the moment of each call. Production hands it
        /// <c>QueryExecutor.GetSqlWatchInstanceNamesAsync(connection, …)</c>.
        /// </param>
        public async Task<Result> DiscoverAsync(
            Func<ServerConnection, CancellationToken, Task<List<string>>> probe,
            CancellationToken cancellationToken = default)
        {
            if (probe == null) throw new ArgumentNullException(nameof(probe));

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var withSqlWatch = new HashSet<string>();
            var allInstances = new List<string>();

            foreach (var conn in _connections.GetEnabledConnections())
            {
                // Always probe regardless of the HasSqlWatch flag — the flag may be stale (false on
                // a server where SQLWATCH was installed after the connection was saved).
                List<string> discovered;
                try
                {
                    discovered = await probe(conn, cancellationToken) ?? new List<string>();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    discovered = new List<string>();
                }

                if (discovered.Count > 0)
                {
                    foreach (var inst in discovered)
                        map[inst] = conn.Id;
                    allInstances.AddRange(discovered);
                    withSqlWatch.Add(conn.Id);
                    continue; // use discovered @@SERVERNAME names, not configured names
                }

                allInstances.AddRange(conn.GetServerList());
            }

            return new Result(
                allInstances.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                map,
                withSqlWatch);
        }
    }
}
