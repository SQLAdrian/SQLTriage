/* In the name of God, the Merciful, the Compassionate */
/*
 * ServerSizingService — reads the three host facts a per-server recommendation is computed from
 * (logical processors, NUMA nodes, physical RAM). Read-only, one statement, no writes anywhere.
 *
 * Ruling 1 (DECISIONS 2026-08-25 17:33): MAXDOP, cost threshold for parallelism and max server
 * memory pre-fill with the standard published recommendation. Two of the three depend on the host,
 * so the host has to be read. This is that read, kept out of the page for the same reason
 * MissingIndexService is: a reader cast in a .razor @code block cannot be tested from anywhere.
 *
 * Fails to nothing, deliberately. A missing connection, a denied VIEW SERVER STATE, a dropped
 * socket: every one returns ServerSizingFacts.None, which produces NO recommendation and pre-fills
 * NOTHING. The lane's honest "the operator supplies the target" state is what a failed read falls
 * back to, so a read failure can never manufacture a number.
 */

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services.Remediation
{
    public sealed class ServerSizingService
    {
        private readonly IServerConnectionManager _connections;
        private readonly ILogger<ServerSizingService> _logger;

        public ServerSizingService(IServerConnectionManager connections, ILogger<ServerSizingService> logger)
        {
            _connections = connections;
            _logger = logger;
        }

        /// <summary>
        /// Reads <see cref="RemediationRecommendedValues.SizingQuery"/> against one server. Never
        /// throws and never returns null: an unreadable server yields
        /// <see cref="ServerSizingFacts.None"/>, which recommends nothing.
        /// </summary>
        public async Task<ServerSizingFacts> ReadAsync(string serverNameOrId, CancellationToken ct = default)
        {
            var connString = ResolveConnectionString(serverNameOrId);
            if (connString == null) return ServerSizingFacts.None;

            try
            {
                using var conn = new SqlConnection(connString);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using var cmd = new SqlCommand(RemediationRecommendedValues.SizingQuery, conn) { CommandTimeout = 15 };
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return ServerSizingFacts.None;

                return new ServerSizingFacts(
                    ReadInt(reader, "LogicalCpuCount"),
                    ReadInt(reader, "NumaNodeCount"),
                    ReadInt(reader, "PhysicalMemoryMb"));
            }
            catch (Exception ex)
            {
                // Debug, not Warning: a server this app cannot read the sizing DMVs on is a normal
                // permissions shape (VIEW SERVER STATE is not granted everywhere), and the visible
                // consequence — nothing is pre-filled — is already the honest answer on screen.
                _logger.LogDebug(ex, "Could not read host sizing facts for '{Server}'", serverNameOrId);
                return ServerSizingFacts.None;
            }
        }

        private static int? ReadInt(SqlDataReader reader, string column)
        {
            try
            {
                var ordinal = reader.GetOrdinal(column);
                if (reader.IsDBNull(ordinal)) return null;
                var value = Convert.ToInt64(reader.GetValue(ordinal));
                return value is > 0 and <= int.MaxValue ? (int)value : null;
            }
            catch { return null; }
        }

        // Same resolver shape as MissingIndexService: accept a registered connection id or a
        // server name, and read from master.
        private string? ResolveConnectionString(string serverNameOrId)
        {
            if (string.IsNullOrWhiteSpace(serverNameOrId)) return null;
            var conn = _connections.GetConnection(serverNameOrId)
                       ?? _connections.GetConnections()
                            .Find(c => c.GetServerList().Exists(s =>
                                string.Equals(s, serverNameOrId, StringComparison.OrdinalIgnoreCase)));
            if (conn == null) return null;
            var servers = conn.GetServerList();
            var server = servers.FirstOrDefault(s => string.Equals(s, serverNameOrId, StringComparison.OrdinalIgnoreCase))
                         ?? (servers.Count > 0 ? servers[0] : serverNameOrId);
            return conn.GetConnectionString(server, "master");
        }
    }
}
