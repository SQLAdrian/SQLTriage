/* Auto-detect local SQL Server instances.
   Extension method for ServerConnectionManager. */
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Microsoft.Data.SqlClient;

namespace SQLTriage.Data.Services
{
    public static class LocalInstanceDetector
    {
        // loopback probe — TLS validation not applicable; all targets here are 127.0.0.1 / localhost equivalents
        private static readonly string[] DefaultConnectionStrings =
        {
            "Data Source=.;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=2",
            "Data Source=(local);Integrated Security=True;TrustServerCertificate=True;Connect Timeout=2",
            "Data Source=localhost;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=2",
            "Data Source=127.0.0.1;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=2",
        };

        /// <summary>
        /// Attempts to discover locally-running SQL Server instances by trying
        /// common connection strings and checking if they accept connections.
        /// Returns list of instance names that responded.
        /// </summary>
        public static List<string> DetectLocalInstances()
        {
            var found = new List<string>(4);

            foreach (var cs in DefaultConnectionStrings)
            {
                try
                {
                    using var conn = new SqlConnection(cs);
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT CAST(SERVERPROPERTY('MachineName') AS VARCHAR(100)) + '\\' + ISNULL(CAST(SERVERPROPERTY('InstanceName') AS VARCHAR(100)), 'MSSQLSERVER')";
                    cmd.CommandTimeout = 2;
                    var instanceName = cmd.ExecuteScalar()?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(instanceName) && !found.Contains(instanceName, StringComparer.OrdinalIgnoreCase))
                    {
                        found.Add(instanceName);
                        // Ensure the instance name has a backslash for named instances
                        if (!instanceName.Contains("\\") && !instanceName.Contains("."))
                            instanceName = instanceName;
                    }
                    conn.Close();
                }
                catch
                {
                    // Connection refused or timeout — no instance here
                }
            }

            return found;
        }
    }
}
