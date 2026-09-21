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
        // 10s: a genuinely-dead/unreachable server should fail fast (~10s), not hang the
        // operator. Connect timeout only (query/command timeouts are governed separately).
        // The assessment connect is a SINGLE attempt — a failure surfaces via RunAssessment's
        // friendly-failure catch (not a retry); dashboard queries separately retry through the
        // resilience pipeline. Keep this short so dead servers fail fast rather than inflating it.
        public int ConnectionTimeout { get; set; } = 10;

        [JsonPropertyName("LastConnected")]
        public DateTime? LastConnected { get; set; }

        [JsonPropertyName("IsConnected")]
        public bool IsConnected { get; set; }

        /// <summary>
        /// True only when <see cref="IsConnected"/>, <see cref="SuccessfulServers"/> and
        /// <see cref="LastConnected"/> were written by a probe in THIS process.
        /// <para>
        /// pages-r1-03: all three fields persist to server-connections.json and survive a restart,
        /// and the tray row, the /servers hero and the per-connection card rendered them as
        /// CURRENT. Proved on this box at hunt time: a store carrying IsConnected:true with
        /// LastConnected 39 days earlier painted a green "connected" card for an instance whose
        /// SQL service was stopped, with no timestamp anywhere on it. Deliberately [JsonIgnore] -
        /// a value read back from disk is the record of an old probe, so it must load as false.
        /// </para>
        /// </summary>
        [JsonIgnore]
        public bool StatusMeasuredThisSession { get; set; }

        [JsonPropertyName("SuccessfulServers")]
        public List<string> SuccessfulServers { get; set; } = new();

        [JsonPropertyName("IsEnabled")]
        public bool IsEnabled { get; set; } = true;

        [JsonPropertyName("Environment")]
        public string? Environment { get; set; }

        [JsonPropertyName("Tags")]
        public List<string> Tags { get; set; } = new();

        /// <summary>
        /// Per-connection opt-in for reporting urgent events (outage, recovery, critical alert) to
        /// the client portal. Adrian's ruling 2026-07-31: this is OPT-IN, per connection, and the
        /// default is OFF — an estate that adds a server does not thereby start telling a client
        /// about it.
        ///
        /// <para>Backwards compatible by construction: the property is absent from every
        /// server-connections.json written before this shipped, and an absent JSON property leaves
        /// the CLR default (false). An existing install therefore reports nothing until an operator
        /// turns a connection on, which is the safe direction.</para>
        ///
        /// <para>This flag gates EMISSION only. Health polling, the up/down dashboards and alert
        /// evaluation run for every enabled connection regardless — a client-reporting preference
        /// must never quietly stop the DBA's own instruments.</para>
        /// </summary>
        [JsonPropertyName("PortalUrgentEvents")]
        public bool PortalUrgentEvents { get; set; } = false;

        public int GetServerCount() => GetServerList().Count;

        /// <summary>
        /// The addresses this connection covers. Splitting is delegated to
        /// <see cref="SQLTriage.Data.Services.ServerAddress.SplitList"/> — 2026-07-21: this used to
        /// split on ',' alongside newline/semicolon, which severed "host\instance,port" into a host
        /// AND a bare port. Because this method feeds ConnectionManager.GetEnabledServerNames, the
        /// port half then propagated estate-wide as a SERVER: it drew its own /dba health card
        /// scoring Resource 0/100, and it entered the /cio equal-weight estate mean, which is how a
        /// one-server estate reported "Equal-weight mean across 4 assessed servers" at 38/Critical
        /// while the single real server read 76/Healthy.
        /// </summary>
        public List<string> GetServerList() =>
            SQLTriage.Data.Services.ServerAddress.SplitList(ServerNames)
                .Select(s => ValidateServerName(s))  // Validate and sanitize each server name
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

        /// <summary>
        /// Validates a server name. Uses a *whitelist* of legal SQL Server
        /// host/instance characters — alphanumeric, dot (FQDN separator),
        /// backslash (instance separator), dash, underscore, and BOTH port
        /// separators — colon and comma. Anything else is dropped.
        ///
        /// The comma was added 2026-07-21. Until then GetServerList split on
        /// ',' before calling here, so a comma never reached this whitelist;
        /// now that an address arrives whole, omitting ',' would silently weld
        /// "SQL01,56510" into "SQL0156510" — a worse failure than the split it
        /// replaced. Comma is legal SQL Server addressing syntax and belongs here.
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
                    c == '.' || c == '\\' || c == '-' || c == '_' || c == ':' || c == ',')
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
                MultiSubnetFailover = MultiSubnetFailover,
                ApplicationName = "SQLTriage",
                Pooling = true,
                MaxPoolSize = 100,
                MinPoolSize = 0
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
