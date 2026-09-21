/* In the name of God, the Merciful, the Compassionate */

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
        /// <summary>
        /// Front door for the two top-bar pickers. Takes the caller's
        /// <see cref="SQLTriage.Data.ConnectionRetargetGrant"/> because the write it makes lands on
        /// a SINGLETON — see <see cref="SQLTriage.Data.ConnectionRetargetGrant"/> for why the
        /// decision travels with the call instead of being checked here.
        /// </summary>
        Task<SQLTriage.Data.ConnectionRetargetOutcome> SetServerAsync(
            string? serverId, SQLTriage.Data.ConnectionRetargetGrant grant);

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

        /// <summary>
        /// Asks the manager first and only records what it accepted.
        ///
        /// <para><b>The order is the point.</b> This service is AddScoped and its
        /// <see cref="CurrentServerId"/> is what the top-bar picker RENDERS, while the connection
        /// the queries actually use is the manager's singleton. Writing this field before knowing
        /// whether the manager took the write is how a control comes to show a server the
        /// application is not connected to — the same defect class as a refusal notice that says
        /// nothing changed on a load that changed it. On a refusal this adopts whatever actually
        /// holds, so the picker states the truth rather than the request.</para>
        /// </summary>
        public Task<SQLTriage.Data.ConnectionRetargetOutcome> SetServerAsync(
            string? serverId, SQLTriage.Data.ConnectionRetargetGrant grant)
        {
            var outcome = _connectionManager.SetCurrentServer(serverId, grant);

            var landed = outcome.Applied ? serverId : outcome.CurrentServerId;
            if (CurrentServerId != landed)
            {
                CurrentServerId = landed;
                // When the server changes, reset database to master
                CurrentDatabase = "master";
                OnServerChanged?.Invoke();
                OnDatabaseChanged?.Invoke();
            }

            return Task.FromResult(outcome);
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
