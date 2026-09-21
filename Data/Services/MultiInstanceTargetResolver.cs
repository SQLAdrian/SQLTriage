/* In the name of God, the Merciful, the Compassionate */

using SQLTriage.Data.Models;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Builds the distinct, ordered set of instance targets a multi-instance run offers for
    /// selection: the union of every given connection's server list, deduplicated by server name.
    ///
    /// <para>Extracted out of the /server-configuration multi-instance picker (rather than left as
    /// a private method inside that page's <c>@code</c> block) so the dedup rule is directly
    /// testable without standing up a Blazor component. The first connection to name a server owns
    /// it here, the same rule <see cref="SqlServerConnectionFactory.GetCurrentConnectionString"/>
    /// and <c>/performance-report</c>'s instance picker already apply elsewhere in this app.</para>
    /// </summary>
    public static class MultiInstanceTargetResolver
    {
        /// <summary>One resolved target: the server name and the connection that owns it.</summary>
        public sealed record Target(string ServerName, ServerConnection Owner);

        /// <summary>
        /// Distinct union of <paramref name="connections"/>' server lists, ordered by first
        /// appearance. Case-insensitive on the server name (SQL Server host/instance names are not
        /// case-sensitive), and duplicates keep the FIRST connection that named them.
        /// </summary>
        public static List<Target> ResolveDistinctTargets(IEnumerable<ServerConnection> connections)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<Target>();

            foreach (var conn in connections)
            {
                foreach (var name in conn.GetServerList())
                {
                    if (!seen.Add(name)) continue;
                    result.Add(new Target(name, conn));
                }
            }

            return result;
        }
    }
}
