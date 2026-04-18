/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Threading.Tasks;

namespace SQLTriage.Data
{
    /// <summary>
    /// Handles application startup logic including server configuration prompts.
    /// </summary>
    public class StartupService
    {
        private readonly ServerConnectionManager _connectionManager;

        public StartupService(ServerConnectionManager connectionManager)
        {
            _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        }

        /// <summary>
        /// Checks if the application needs initial server setup.
        /// Returns true if no servers are configured.
        /// </summary>
        public bool RequiresServerSetup()
        {
            var connections = _connectionManager.GetConnections();
            return connections.Count == 0;
        }

        /// <summary>
        /// Event fired when the application should prompt for server setup.
        /// </summary>
        public event Action? OnServerSetupRequired;

        /// <summary>
        /// Triggers the server setup prompt if needed.
        /// </summary>
        public void CheckAndPromptServerSetup()
        {
            if (RequiresServerSetup())
            {
                OnServerSetupRequired?.Invoke();
            }
        }
    }
}