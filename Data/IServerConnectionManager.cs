/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using SQLTriage.Data.Models;

namespace SQLTriage.Data
{
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

        void AddConnection(ServerConnection connection);
        void UpdateConnection(ServerConnection connection);
        void RemoveConnection(string id);
        void SetCurrentServer(string? serverId);
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
