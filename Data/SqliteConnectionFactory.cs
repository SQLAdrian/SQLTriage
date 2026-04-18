/* In the name of God, the Merciful, the Compassionate */

using System.Data;
using Microsoft.Data.Sqlite;
using System.Threading.Tasks;

namespace SQLTriage.Data
{
    /// <summary>
    /// Connection factory for liveQueries databases.
    /// Not used by the caching layer directly (liveQueriesCacheStore manages its own connections),
    /// but available for the existing QueryPair.liveQueries query path if needed.
    /// </summary>
    public class liveQueriesConnectionFactory : IDbConnectionFactory
    {
        private readonly string _connectionString;

        public liveQueriesConnectionFactory(string connectionString)
        {
            _connectionString = connectionString;
        }

        public IDbConnection CreateConnection() => new SqliteConnection(_connectionString);
        public string DataSourceType => "liveQueries";

        public async Task<IDbConnection> CreateConnectionAsync()
        {
            var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            return connection;
        }
    }
}
