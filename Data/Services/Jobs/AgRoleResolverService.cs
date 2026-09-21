/* In the name of God, the Merciful, the Compassionate */
/*
 * Resolves which availability-group replica currently holds PRIMARY.
 *
 * Read-only. One query against sys.availability_replicas + availability_groups, left-joined to
 * sys.dm_hadr_availability_replica_states for the live role (same shape LicensingEstimator uses
 * for its seat maths — that was the only place in the app reading role_desc).
 *
 * WHY THIS IS ITS OWN SERVICE, and why it refuses loudly: job sync writes to the SECONDARY,
 * driven by what it read from the PRIMARY. Get the direction wrong and you overwrite the live
 * side's jobs with the standby's. Every ambiguity here therefore resolves to "refuse", not to a
 * best guess:
 *   - roles must be read FROM the primary candidate (a secondary's DMV view can be stale),
 *   - source must be PRIMARY and target SECONDARY *within the same AG*,
 *   - any UNKNOWN role disables the operation entirely.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;
using SQLTriage.Data.Models.Jobs;

namespace SQLTriage.Data.Services.Jobs
{
    public class AgRoleResolverService
    {
        private readonly ServerConnectionManager _connections;
        private readonly ILogger<AgRoleResolverService> _logger;

        public AgRoleResolverService(ServerConnectionManager connections, ILogger<AgRoleResolverService> logger)
        {
            _connections = connections;
            _logger = logger;
        }

        /// <summary>
        /// Every AG replica this instance can see, with its current role. Empty when the instance
        /// is unreachable or hosts no availability groups — callers treat empty as "cannot sync".
        /// </summary>
        public async Task<IReadOnlyList<AgReplicaRole>> GetReplicaRolesAsync(
            string serverName, CancellationToken ct = default)
        {
            var conn = ResolveConnection(serverName);
            if (conn is null) return Array.Empty<AgReplicaRole>();

            var roles = new List<AgReplicaRole>();
            try
            {
                using var sqlConn = new SqlConnection(conn.GetConnectionString(serverName, "master"));
                await sqlConn.OpenAsync(ct).ConfigureAwait(false);
                using var cmd = new SqlCommand(ReplicaRolesSql, sqlConn) { CommandTimeout = 10 };
                using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await rdr.ReadAsync(ct).ConfigureAwait(false))
                {
                    roles.Add(new AgReplicaRole
                    {
                        AgName = rdr.GetString(rdr.GetOrdinal("AgName")),
                        ReplicaServerName = rdr.GetString(rdr.GetOrdinal("ReplicaServerName")),
                        Role = ParseRole(rdr.GetString(rdr.GetOrdinal("RoleDesc"))),
                        IsLocal = rdr.GetInt32(rdr.GetOrdinal("IsLocal")) == 1
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AG replica role read failed for {Server}", serverName);
                return Array.Empty<AgReplicaRole>();
            }

            return roles;
        }

        /// <summary>
        /// Decides whether jobs may be synced <paramref name="source"/> -> <paramref name="target"/>.
        /// Returns a refusal reason, or null when the direction is provably safe.
        /// <para>
        /// Roles are read from <paramref name="source"/> deliberately: if it turns out not to be
        /// the primary, that read is exactly what tells us so.
        /// </para>
        /// </summary>
        public async Task<string?> RefusalReasonAsync(string source, string target, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                return "Pick both a primary (source) and a secondary (target) replica.";

            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                return "Source and target are the same instance.";

            var roles = await GetReplicaRolesAsync(source, ct).ConfigureAwait(false);
            if (roles.Count == 0)
                return $"Could not read availability-group roles from '{source}'. It may be unreachable, or host no availability groups.";

            var src = roles.FirstOrDefault(r => Same(r.ReplicaServerName, source));
            var tgt = roles.FirstOrDefault(r => Same(r.ReplicaServerName, target));

            if (src is null) return $"'{source}' is not a replica in any availability group visible from itself.";
            if (tgt is null) return $"'{target}' is not a replica in any availability group visible from '{source}'.";

            if (!string.Equals(src.AgName, tgt.AgName, StringComparison.OrdinalIgnoreCase))
                return $"'{source}' and '{target}' are in different availability groups ('{src.AgName}' vs '{tgt.AgName}').";

            // UNKNOWN means the replica state DMV had no row — commonly a replica that is down.
            // Syncing on a guess about which side is live is precisely the wrong risk to take.
            if (src.Role == AgRole.Unknown || tgt.Role == AgRole.Unknown)
                return "One or both replicas report an UNKNOWN role (the replica may be down). Refusing to guess which side is primary.";

            if (src.Role != AgRole.Primary)
                return $"'{source}' is currently {src.Role.ToString().ToUpperInvariant()}, not the primary. Sync only ever runs primary -> secondary.";

            if (tgt.Role != AgRole.Secondary)
                return $"'{target}' is currently {tgt.Role.ToString().ToUpperInvariant()}, not a secondary. Refusing to write to it.";

            return null;
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static AgRole ParseRole(string roleDesc) => roleDesc?.Trim().ToUpperInvariant() switch
        {
            "PRIMARY" => AgRole.Primary,
            "SECONDARY" => AgRole.Secondary,
            _ => AgRole.Unknown
        };

        private ServerConnection? ResolveConnection(string instance)
        {
            if (string.IsNullOrWhiteSpace(instance)) return null;
            foreach (var c in _connections.GetConnections())
                foreach (var s in c.GetServerList())
                    if (string.Equals(s, instance, StringComparison.OrdinalIgnoreCase))
                        return c;
            return null;
        }

        internal const string ReplicaRolesSql = @"
SELECT  ag.name                          AS AgName,
        ar.replica_server_name           AS ReplicaServerName,
        ISNULL(ars.role_desc, 'UNKNOWN') AS RoleDesc,
        CAST(ISNULL(ars.is_local, 0) AS int) AS IsLocal
FROM    sys.availability_replicas AS ar
JOIN    sys.availability_groups   AS ag ON ag.group_id = ar.group_id
LEFT JOIN sys.dm_hadr_availability_replica_states AS ars ON ars.replica_id = ar.replica_id;";
    }
}
