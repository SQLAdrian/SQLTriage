/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;

namespace SQLTriage.Data
{
    public class QueryDefinition
    {
        public string SqlServer { get; set; } = "";
        /// <summary>
        /// Optional fallback query for SQL Server versions below 2019 (major version &lt; 15).
        /// When set, this query is used instead of SqlServer when the connected instance is SQL 2017 or earlier.
        /// </summary>
        public string? SqlServerLegacy { get; set; }
    }

    public class QueryStore
    {
        private readonly DashboardConfigService _configService;

        public QueryStore(DashboardConfigService configService)
        {
            _configService = configService;
        }

        /// <summary>
        /// Retrieves the SQL query text for the given query ID and data source type.
        /// Delegates to DashboardConfigService which reads from dashboard-config.json.
        /// </summary>
        public string GetQuery(string queryId, string dataSourceType)
        {
            return _configService.GetQuery(queryId, dataSourceType);
        }

        /// <summary>
        /// Checks whether a query ID exists in the config.
        /// </summary>
        public bool HasQuery(string queryId) => _configService.HasQuery(queryId);
    }
}
