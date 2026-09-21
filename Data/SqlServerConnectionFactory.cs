/* In the name of God, the Merciful, the Compassionate */

using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SQLTriage.Data.Models;

namespace SQLTriage.Data
{
    public class SqlServerConnectionFactory : IDbConnectionFactory
    {
        private readonly ServerConnectionManager? _serverConnectionManager;
        private readonly GlobalInstanceSelector? _instanceSelector;
        private byte[] _encryptedFallbackConnStr;   // AES-256-GCM encrypted in memory
        private readonly byte[] _memKey;             // Per-instance ephemeral key
        private bool _trustServerCertificate;
        // no-server-idle ruling: there is NO implicit "Server=." local default. A fallback connection
        // string is only present when one was explicitly configured (appsettings ConnectionStrings:SqlServer).
        // When false AND no server is configured in the manager, the factory is Unconfigured.
        private bool _hasFallback;

        /// <summary>
        /// Constructor for backward compatibility - creates factory without ServerConnectionManager.
        /// Uses the provided connection string directly.
        /// </summary>
        public SqlServerConnectionFactory(string? connectionString, bool trustServerCertificate = false)
        {
            _memKey = new byte[32];
            RandomNumberGenerator.Fill(_memKey);
            _trustServerCertificate = trustServerCertificate;
            _serverConnectionManager = null;
            SetFallback(connectionString, trustServerCertificate);
        }

        /// <summary>
        /// Constructor with ServerConnectionManager - uses the current server from the manager.
        /// </summary>
        public SqlServerConnectionFactory(ServerConnectionManager serverConnectionManager, GlobalInstanceSelector instanceSelector, string? fallbackConnectionString, bool trustServerCertificate = false)
        {
            _memKey = new byte[32];
            RandomNumberGenerator.Fill(_memKey);
            _serverConnectionManager = serverConnectionManager;
            _instanceSelector = instanceSelector;
            _trustServerCertificate = trustServerCertificate;
            SetFallback(fallbackConnectionString, trustServerCertificate);
        }

        /// <summary>
        /// Stores (or clears) the fallback connection string. An empty/whitespace value leaves the
        /// factory with NO fallback — the no-server-idle posture — rather than a local "Server=." default.
        /// </summary>
        private void SetFallback(string? connectionString, bool trustServerCertificate)
        {
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                _encryptedFallbackConnStr = MemEncrypt(BuildConnectionString(connectionString, trustServerCertificate));
                _hasFallback = true;
            }
            else
            {
                _encryptedFallbackConnStr = System.Array.Empty<byte>();
                _hasFallback = false;
            }
        }

        private string BuildConnectionString(string baseConnectionString, bool trustServerCertificate)
        {
            var builder = new SqlConnectionStringBuilder(baseConnectionString);
            builder.TrustServerCertificate = trustServerCertificate;

            // Enterprise Polish: Set Application Name for better observability in SQL traces/Audit
            if (string.IsNullOrEmpty(builder.ApplicationName) || builder.ApplicationName == ".Net SqlClient Data Provider")
            {
                builder.ApplicationName = "SQLTriage";
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
        /// Connection strings are decrypted only at the moment of use and never logged.
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

            if (currentServer != null)
            {
                var serverList = currentServer.GetServerList();
                if (serverList.Count > 0)
                {
                    var serverName = serverList[0];
                    return currentServer.GetConnectionStringForDashboard(serverName);
                }
            }

            // No instance selected and no current server. Historically this returned a hard-coded
            // "Server=.;Database=SQLWATCH;Integrated Security=true;" LOCAL default; the no-server-idle
            // ruling removes that — SQLTriage has NO implicit local instance. Resolution order now:
            //   1. an explicit fallback connection string, if one was configured;
            //   2. the first enabled connection, if any server is configured but none is current yet;
            //   3. otherwise the factory is Unconfigured — callers must guard on IsUnconfigured and
            //      not open a connection. Throwing here (instead of connecting to ".") is deliberate:
            //      it fails BEFORE any network attempt, so an unguarded caller surfaces a clear error
            //      rather than silently probing localhost.
            if (_hasFallback)
                return MemDecrypt(_encryptedFallbackConnStr);

            var enabled = _serverConnectionManager?.GetEnabledConnections();
            if (enabled != null && enabled.Count > 0)
            {
                var serverList = enabled[0].GetServerList();
                if (serverList.Count > 0)
                    return enabled[0].GetConnectionStringForDashboard(serverList[0]);
            }

            throw new System.InvalidOperationException(
                "SQLTriage has no server configured. Add a server on the Servers page before opening a connection.");
        }

        /// <summary>
        /// True when the factory can resolve NO connection: no fallback connection string was
        /// configured AND the ServerConnectionManager has no enabled connection. This is the
        /// no-server-idle posture — background collectors short-circuit rather than open a connection,
        /// and <see cref="CreateConnection()"/> throws instead of falling back to a local "Server=."
        /// default. It is the single source of truth for "zero configured servers", shared by the
        /// connection-resolution tail above and by every loop guard.
        /// </summary>
        public bool IsUnconfigured
        {
            get
            {
                if (_hasFallback) return false;
                var enabled = _serverConnectionManager?.GetEnabledConnections();
                return enabled == null || enabled.Count == 0;
            }
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
        /// The new value is immediately encrypted in memory.
        /// </summary>
        public void UpdateConnectionString(string? newConnectionString, bool trustServerCertificate)
        {
            _trustServerCertificate = trustServerCertificate;
            SetFallback(newConnectionString, trustServerCertificate);
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
                    var builder = new SqlConnectionStringBuilder(MemDecrypt(_encryptedFallbackConnStr));
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

        // ──────────────── In-Memory AES-256-GCM Encryption ──────────────

        private byte[] MemEncrypt(string plainText)
        {
            var plain = Encoding.UTF8.GetBytes(plainText);
            var result = AesGcmHelper.Encrypt(plain, _memKey);
            CryptographicOperations.ZeroMemory(plain);
            return result;
        }

        private string MemDecrypt(byte[] blob)
        {
            var plain = AesGcmHelper.Decrypt(blob, _memKey);
            var result = Encoding.UTF8.GetString(plain);
            CryptographicOperations.ZeroMemory(plain);
            return result;
        }

        /// <summary>
        /// Scrubs a connection string for safe logging — removes Password and User ID values.
        /// </summary>
        internal static string ScrubForLog(string connectionString)
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(connectionString);
                if (!string.IsNullOrEmpty(builder.Password))
                    builder.Password = "********";
                if (!string.IsNullOrEmpty(builder.UserID))
                    builder.UserID = "****";
                return builder.ConnectionString;
            }
            catch
            {
                return "[scrubbed]";
            }
        }
    }
}
