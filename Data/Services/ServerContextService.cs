using System;
using System.Threading.Tasks;

namespace SQLTriage.Data.Services
{
    public interface IServerContextService
    {
        string? CurrentServerId { get; }
        string CurrentDatabase { get; }
        event Action? OnServerChanged;
        event Action? OnDatabaseChanged;
        Task SetServerAsync(string? serverId);
        Task SetDatabaseAsync(string database);
    }

    public class ServerContextService : IServerContextService
    {
        // The top-bar "Connected to:" selector writes here, but the actual query
        // connection (SqlServerConnectionFactory.GetCurrentConnectionString) reads
        // ServerConnectionManager.CurrentServer. Both key on the same connection Id,
        // so this service is the single front door: setting the context server also
        // sets the connection manager's current server, keeping factory-backed
        // dashboards on the same server the top bar shows.
        private readonly IServerConnectionManager _connectionManager;

        public ServerContextService(IServerConnectionManager connectionManager)
        {
            _connectionManager = connectionManager;
        }

        public string? CurrentServerId { get; private set; }
        public string CurrentDatabase { get; private set; } = "master";

        public event Action? OnServerChanged;
        public event Action? OnDatabaseChanged;

        public Task SetServerAsync(string? serverId)
        {
            if (CurrentServerId != serverId)
            {
                CurrentServerId = serverId;
                // When server changes, reset database to master
                CurrentDatabase = "master";
                // Keep the query-connection state (ServerConnectionManager) aligned
                // with the top-bar selection — same connection Id space.
                _connectionManager.SetCurrentServer(serverId);
                OnServerChanged?.Invoke();
                OnDatabaseChanged?.Invoke();
            }
            return Task.CompletedTask;
        }

        public Task SetDatabaseAsync(string database)
        {
            if (CurrentDatabase != database)
            {
                CurrentDatabase = database;
                OnDatabaseChanged?.Invoke();
            }
            return Task.CompletedTask;
        }
    }
}
