/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using SQLTriage.Data.Models;

namespace SQLTriage.Data
{
    /// <summary>
    /// Outcome of a connection mutation. Returned (never thrown) so the FIVE call sites that mutate
    /// connections — Servers.razor, ConnectionDialog.razor, EnvironmentView.razor, Onboarding.razor
    /// and Settings.razor.cs (config import) — can render an honest reason instead of the operator
    /// watching a click do nothing.
    ///
    /// A refusal is NEVER a silent no-op: <see cref="Reason"/> is always populated when
    /// <see cref="Succeeded"/> is false.
    /// </summary>
    public sealed record ConnectionChangeResult(bool Succeeded, string? Reason)
    {
        public static readonly ConnectionChangeResult Ok = new(true, null);
        public static ConnectionChangeResult Refused(string reason) => new(false, reason);
    }

    /// <summary>
    /// Abstraction over ServerConnectionManager — enables testing without live SQL connections.
    /// </summary>
    public interface IServerConnectionManager
    {
        event Action? OnConnectionChanged;

        List<ServerConnection> GetConnections();
        List<ServerConnection> GetEnabledConnections();
        string[] GetEnabledServerNames();
        ServerConnection? GetConnection(string id);
        ServerConnection? GetDefaultConnection();
        ServerConnection? CurrentServer { get; }

        // These return a result rather than void so a licence refusal reaches the operator. Existing
        // callers that ignore the return value still compile unchanged (C# allows discarding a
        // return), which is why this was a safe signature change across all five surfaces.
        ConnectionChangeResult AddConnection(ServerConnection connection);
        ConnectionChangeResult UpdateConnection(ServerConnection connection);
        ConnectionChangeResult RemoveConnection(string id);
        /// <summary>
        /// Retargets the PROCESS-WIDE current server. Every screen open in this process reads it,
        /// so the write needs a <see cref="ConnectionRetargetGrant"/> saying who is asking — see
        /// that type for why the decision is a parameter rather than a guard at the call sites.
        /// Returns the outcome; it never throws and it is never a silent no-op.
        /// </summary>
        ConnectionRetargetOutcome SetCurrentServer(string? serverId, ConnectionRetargetGrant grant);
        void UpdateSuccessfulServers(string connectionId, List<string> successfulServers);

        bool DiscoveryCompleted { get; }

        (string[] instances,
         Dictionary<string, string> instanceToConnId,
         HashSet<string> connectionsWithSqlWatch) GetDiscoveryCache();

        void CacheDiscoveryResults(
            string[] instances,
            Dictionary<string, string> instanceToConnId,
            HashSet<string> connectionsWithSqlWatch);
    }
}
