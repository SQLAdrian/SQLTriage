/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using SQLTriage.Data.Models;

namespace SQLTriage.Data
{
    /// <summary>
    /// High-level data service that provides typed dashboard data.
    /// Wraps QueryExecutor to map raw query results to domain models.
    /// </summary>
    public class DashboardDataService
    {
        private readonly QueryExecutor _executor;
        private readonly DashboardConfigService _configService;
        private readonly ServerConnectionManager _serverManager;

        public DashboardDataService(QueryExecutor executor, DashboardConfigService configService, ServerConnectionManager serverManager)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            _serverManager = serverManager ?? throw new ArgumentNullException(nameof(serverManager));
        }

        // ================================================================
        // INSTANCES
        // ================================================================

        /// <summary>
        /// Returns the list of active SQL Server instances available for monitoring.
        /// </summary>
        public async Task<string[]> GetInstancesAsync()
        {
            // Try to get from ServerManager first (populated from "servers" config)
            var servers = _serverManager.GetEnabledConnections()
                .SelectMany(c => c.GetServerList())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s)
                .ToArray();

            if (servers.Length > 0) return servers;

            // Use a minimal filter since the instances query does not use time/instance params
            var filter = new DashboardFilter();
            var dt = await _executor.ExecuteQueryAsync("instances.list", filter);
            return dt.Rows.Cast<DataRow>()
                .Select(r => r["sql_instance"]?.ToString() ?? "")
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();
        }




        /// <summary>
        /// Parses a DateTime from either a DateTime object or a string (ISO 8601 format).
        /// </summary>
        private static DateTime ParseDateTime(object? value)
        {
            if (value == null || value == DBNull.Value)
                return DateTime.MinValue;

            if (value is DateTime dt)
                return dt;

            if (DateTime.TryParse(value.ToString(), out var parsed))
                return parsed;

            return DateTime.MinValue;
        }
    }
}
