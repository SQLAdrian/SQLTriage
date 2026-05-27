/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using SQLTriage.Data;

namespace SQLTriage.Data.Models
{
    public class ServerConnection
    {
        [JsonPropertyName("Id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [JsonPropertyName("ServerNames")]
        public string ServerNames { get; set; } = string.Empty;

        [JsonPropertyName("Database")]
        public string Database { get; set; } = "master";

        [JsonPropertyName("HasSqlWatch")]
        public bool HasSqlWatch { get; set; } = false;

        [JsonPropertyName("UseWindowsAuthentication")]
        public bool UseWindowsAuthentication { get; set; } = true;

        [JsonPropertyName("AuthenticationType")]
        public string? AuthenticationType { get; set; }

        [JsonIgnore]
        public string EffectiveAuthType => AuthenticationType
            ?? (UseWindowsAuthentication ? AuthenticationTypes.Windows : AuthenticationTypes.SqlServer);

        /// <summary>
        /// Validates if the connection has all required credentials for the selected authentication type.
        /// Returns null if valid, or an error message if invalid.
        /// </summary>
        public string? ValidateCredentials()
        {
            if (EffectiveAuthType == AuthenticationTypes.SqlServer)
            {
                if (string.IsNullOrWhiteSpace(Username))
                {
                    return "Username is required for SQL Server Authentication";
                }
                if (string.IsNullOrEmpty(GetDecryptedPassword()))
                {
                    return "Password is required for SQL Server Authentication";
                }
            }
            return null;
        }

        [JsonIgnore]
        public string AuthenticationDisplay => EffectiveAuthType switch
        {
            AuthenticationTypes.EntraMFA => "Microsoft Entra MFA",
            AuthenticationTypes.SqlServer => "SQL Authentication",
            _ => "Windows Authentication"
        };

        [JsonPropertyName("Username")]
        public string? Username { get; set; }

        [JsonPropertyName("TrustServerCertificate")]
        public bool TrustServerCertificate { get; set; } = false;

        [JsonPropertyName("Password")]
        public string? Password { get; set; }

        public void SetPassword(string? plainTextPassword)
        {
            if (string.IsNullOrEmpty(plainTextPassword))
            {
                Password = null;
                return;
            }
            Password = CredentialProtector.Encrypt(plainTextPassword);
        }

        public string GetDecryptedPassword() => CredentialProtector.Decrypt(Password);

        [JsonPropertyName("MultiSubnetFailover")]
        public bool MultiSubnetFailover { get; set; } = false;

        [JsonPropertyName("ConnectionTimeout")]
        public int ConnectionTimeout { get; set; } = 15;

        [JsonPropertyName("LastConnected")]
        public DateTime? LastConnected { get; set; }

        [JsonPropertyName("IsConnected")]
        public bool IsConnected { get; set; }

        [JsonPropertyName("SuccessfulServers")]
        public List<string> SuccessfulServers { get; set; } = new();

        [JsonPropertyName("IsEnabled")]
        public bool IsEnabled { get; set; } = true;

        [JsonPropertyName("Environment")]
        public string? Environment { get; set; }

        [JsonPropertyName("Tags")]
        public List<string> Tags { get; set; } = new();

        public int GetServerCount() => GetServerList().Count;

        public List<string> GetServerList() =>
            ServerNames.Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => ValidateServerName(s))  // Validate and sanitize each server name
                .ToList();

        /// <summary>
        /// Validates a server name. Uses a *whitelist* of legal SQL Server
        /// host/instance characters — alphanumeric, dot (FQDN separator),
        /// backslash (instance separator), dash, underscore, and colon
        /// (port separator). Anything else is dropped.
        ///
        /// Previously this used a blacklist of strings like "sp_", "xp_",
        /// "@@", "--" etc. and called `.Replace(...)` for each. That
        /// corrupted legitimate names — `SP-SQL01` became `-SQL01`,
        /// `db.master.contoso.com` became `db..contoso.com`, and any host
        /// with `--` in it was silently mutilated. The blacklist was also
        /// the wrong defence for SQL injection: server names flow into
        /// connection strings, not query text, so the right protection is
        /// the SqlConnectionStringBuilder, which we use elsewhere — not
        /// string-mangling at parse time.
        /// </summary>
        private static string ValidateServerName(string serverName)
        {
            if (string.IsNullOrWhiteSpace(serverName))
                return string.Empty;

            // Limit length
            if (serverName.Length > 100)
                serverName = serverName.Substring(0, 100);

            // Whitelist: keep only characters that legitimately appear in a
            // SQL Server host or named-instance specifier. Drop everything
            // else silently — this protects against newlines, control chars,
            // and paste-from-Word artefacts without mangling valid names.
            var sb = new System.Text.StringBuilder(serverName.Length);
            foreach (var c in serverName)
            {
                if (char.IsLetterOrDigit(c) ||
                    c == '.' || c == '\\' || c == '-' || c == '_' || c == ':')
                {
                    sb.Append(c);
                }
            }
            return sb.ToString().Trim();
        }

        public string GetConnectionString(string serverName) => GetConnectionString(serverName, HasSqlWatch ? "SQLWATCH" : "master");

        public string GetConnectionStringForDashboard(string serverName) => GetConnectionString(serverName, HasSqlWatch ? "SQLWATCH" : "master");

        public string GetConnectionString(string serverName, string database)
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
            {
                DataSource = serverName,
                InitialCatalog = database,
                ConnectTimeout = ConnectionTimeout,
                PersistSecurityInfo = false,
                TrustServerCertificate = TrustServerCertificate,
                MultiSubnetFailover = MultiSubnetFailover
            };

            switch (EffectiveAuthType)
            {
                case AuthenticationTypes.EntraMFA:
                    builder.IntegratedSecurity = false;
                    builder.Authentication = Microsoft.Data.SqlClient.SqlAuthenticationMethod.ActiveDirectoryInteractive;
                    if (!string.IsNullOrWhiteSpace(Username))
                        builder.UserID = Username;
                    break;

                case AuthenticationTypes.SqlServer:
                    // Validate SQL Server authentication credentials
                    if (string.IsNullOrWhiteSpace(Username))
                    {
                        throw new InvalidOperationException("Username is required for SQL Server Authentication. " +
                            "Please either: 1) Enable Windows Authentication, or 2) Enter a Username for SQL Server Authentication.");
                    }
                    builder.IntegratedSecurity = false;
                    builder.UserID = Username;
                    builder.Password = GetDecryptedPassword();
                    break;

                default:
                    builder.IntegratedSecurity = true;
                    break;
            }

            return builder.ConnectionString;
        }
    }
}
