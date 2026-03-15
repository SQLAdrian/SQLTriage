/* In the name of God, the Merciful, the Compassionate */

using System.Data;
using System.Linq;
using Microsoft.Data.SqlClient;
using System.Threading.Tasks;
using SqlHealthAssessment.Data.Models;

namespace SqlHealthAssessment.Data
{
    public class SqlServerConnectionFactory : IDbConnectionFactory
    {
        private readonly ServerConnectionManager? _serverConnectionManager;
        private readonly GlobalInstanceSelector? _instanceSelector;
        private string _fallbackConnectionString;
        private bool _trustServerCertificate;

        /// <summary>
        /// Constructor for backward compatibility - creates factory without ServerConnectionManager.
        /// Uses the provided connection string directly.
        /// </summary>
        public SqlServerConnectionFactory(string connectionString, bool trustServerCertificate = false)
        {
            _fallbackConnectionString = BuildConnectionString(connectionString, trustServerCertificate);
            _trustServerCertificate = trustServerCertificate;
            _serverConnectionManager = null;
        }

        /// <summary>
        /// Constructor with ServerConnectionManager - uses the current server from the manager.
        /// </summary>
        public SqlServerConnectionFactory(ServerConnectionManager serverConnectionManager, GlobalInstanceSelector instanceSelector, string fallbackConnectionString, bool trustServerCertificate = false)
        {
            _serverConnectionManager = serverConnectionManager;
            _instanceSelector = instanceSelector;
            _fallbackConnectionString = BuildConnectionString(fallbackConnectionString, trustServerCertificate);
            _trustServerCertificate = trustServerCertificate;
        }

        private string BuildConnectionString(string baseConnectionString, bool trustServerCertificate)
        {
            var builder = new SqlConnectionStringBuilder(baseConnectionString);
            builder.TrustServerCertificate = trustServerCertificate;

            // Enterprise Polish: Set Application Name for better observability in SQL traces/Audit
            if (string.IsNullOrEmpty(builder.ApplicationName) || builder.ApplicationName == ".Net SqlClient Data Provider")
            {
                builder.ApplicationName = "SQL Health Assessment";
            }
            return builder.ConnectionString;
        }

        /// <summary>
        /// Creates a connection with a specific initial database context.
        /// </summary>
        public IDbConnection CreateConnection(string initialDatabase)
        {
            var connectionString = GetCurrentConnectionString();
            var builder = new SqlConnectionStringBuilder(connectionString);
            builder.InitialCatalog = initialDatabase;
            return new SqlConnection(builder.ConnectionString);
        }

        /// <summary>
        /// Creates a connection using the current server from ServerConnectionManager.
        /// Falls back to the configured connection string if no server is selected.
        /// </summary>
        public IDbConnection CreateConnection()
        {
            var connectionString = GetCurrentConnectionString();
            return new SqlConnection(connectionString);
        }

        public async Task<IDbConnection> CreateConnectionAsync()
        {
            var connectionString = GetCurrentConnectionString();
            var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            return connection;
        }

        /// <summary>
        /// Gets the current connection string - prioritizes ServerConnectionManager.CurrentServer,
        /// falls back to the configured connection string.
        /// </summary>
        private string GetCurrentConnectionString()
        {
            // Check GlobalInstanceSelector first (dropdown selection)
            var selectedInstance = _instanceSelector?.SelectedInstance;
            if (!string.IsNullOrEmpty(selectedInstance) && _serverConnectionManager != null)
            {
                var connections = _serverConnectionManager.GetEnabledConnections();
                foreach (var conn in connections)
                {
                    var servers = conn.GetServerList();
                    if (servers.Contains(selectedInstance, System.StringComparer.OrdinalIgnoreCase))
                    {
                        return conn.GetConnectionStringForDashboard(selectedInstance);
                    }
                }
            }

            var currentServer = _serverConnectionManager?.CurrentServer;
#if DEBUG
            System.Diagnostics.Debug.WriteLine($"[SqlServerConnectionFactory] GetCurrentConnectionString called. CurrentServer is null: {currentServer == null}");
#endif
            
            if (currentServer != null)
            {
                // Get the first server from the connection's server list
                var serverList = currentServer.GetServerList();
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[SqlServerConnectionFactory] CurrentServer found: {currentServer.ServerNames}, ServerList count: {serverList.Count}");
#endif
                if (serverList.Count > 0)
                {
                    var serverName = serverList[0];
                    var connStr = currentServer.GetConnectionStringForDashboard(serverName);
#if DEBUG
                    System.Diagnostics.Debug.WriteLine($"[SqlServerConnectionFactory] Using server-specific connection: {serverName}");
#endif
                    return connStr;
                }
            }
            else
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[SqlServerConnectionFactory] CurrentServer is null. Using fallback: {_fallbackConnectionString}");
#endif
            }

            // Fall back to configured connection string
            return _fallbackConnectionString;
        }

        /// <summary>
        /// Creates and opens a new async SQL connection.
        /// </summary>
        public async Task<SqlConnection> CreateAndOpenConnectionAsync()
        {
            var connectionString = GetCurrentConnectionString();
            var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            return connection;
        }

        /// <summary>
        /// Updates the fallback connection string without requiring a restart.
        /// </summary>
        public void UpdateConnectionString(string newConnectionString, bool trustServerCertificate)
        {
            _trustServerCertificate = trustServerCertificate;
            _fallbackConnectionString = BuildConnectionString(newConnectionString, trustServerCertificate);
        }

        public string DataSourceType => "SqlServer";

        /// <summary>
        /// Returns the server name from the current connection for display purposes.
        /// If a server is selected in ServerConnectionManager, returns that server's name.
        /// Otherwise returns the fallback server name.
        /// Does NOT expose the full connection string (which may contain credentials).
        /// </summary>
        public string ServerName
        {
            get
            {
                var currentServer = _serverConnectionManager?.CurrentServer;
                if (currentServer != null)
                {
                    var serverList = currentServer.GetServerList();
                    if (serverList.Count > 0)
                    {
                        return serverList[0];
                    }
                }

                try
                {
                    var builder = new SqlConnectionStringBuilder(_fallbackConnectionString);
                    return builder.DataSource;
                }
                catch
                {
                    return "Unknown";
                }
            }
        }

        /// <summary>
        /// Gets the current server connection if one is selected, otherwise null.
        /// </summary>
        public ServerConnection? CurrentServer => _serverConnectionManager?.CurrentServer;
    }
}
