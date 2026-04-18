/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Text;
using System.Threading.Tasks;
using SQLTriage.Data.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace SQLTriage.Data
{
    /// <summary>
    /// Service for creating and managing liveQueries tables based on SQL Server query results.
    /// All tables include a server_name column for multi-server filtering.
    /// </summary>
    public class liveQueriesTableService
    {
        private readonly string _SqliteConnectionString;

        public liveQueriesTableService()
        {
            var dbPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SQLTriage.db");
            _SqliteConnectionString = $"Data Source={dbPath}";
        }

        // ──────────────────────── Query Testing with Results ──────────────

        /// <summary>
        /// Executes a SQL Server query and returns up to <paramref name="maxRows"/> rows
        /// as a DataTable. Optionally binds @TimeFrom, @TimeTo, @SqlInstance from a DashboardFilter.
        /// </summary>
        public async Task<(bool Success, string Message, DataTable? Results)> ExecuteSqlServerQueryAsync(
            IDbConnectionFactory connectionFactory, string query, DashboardFilter? filter = null, int maxRows = 50)
        {
            if (string.IsNullOrWhiteSpace(query))
                return (false, "Query is empty", null);

            try
            {
                using var connection = await connectionFactory.CreateConnectionAsync();

                DbCommand cmd = connectionFactory.DataSourceType == "SqlServer"
                    ? (DbCommand)(SqlCommand)connection.CreateCommand()
                    : (DbCommand)(SqliteCommand)connection.CreateCommand();

                cmd.CommandText = query;
                cmd.CommandTimeout = 30;

                // Add dashboard filter parameters if filter is provided and query uses them
                if (filter != null)
                {
                    bool hasTimeFrom = query.Contains("@TimeFrom", StringComparison.OrdinalIgnoreCase);
                    bool hasTimeTo = query.Contains("@TimeTo", StringComparison.OrdinalIgnoreCase);
                    bool hasSqlInstance = query.Contains("@SqlInstance", StringComparison.OrdinalIgnoreCase);

                    if (cmd is SqlCommand sqlCmd)
                    {
                        if (hasTimeFrom)
                            sqlCmd.Parameters.AddWithValue("@TimeFrom", filter.TimeFrom.ToString("yyyy-MM-dd HH:mm:ss"));
                        if (hasTimeTo)
                            sqlCmd.Parameters.AddWithValue("@TimeTo", filter.TimeTo.ToString("yyyy-MM-dd HH:mm:ss"));
                        if (hasSqlInstance)
                            sqlCmd.Parameters.AddWithValue("@SqlInstance", filter.Instances.Length > 0 ? string.Join(",", filter.Instances) : "");
                    }
                    else if (cmd is SqliteCommand liteCmd)
                    {
                        if (hasTimeFrom)
                            liteCmd.Parameters.AddWithValue("@TimeFrom", filter.TimeFrom.ToString("yyyy-MM-dd HH:mm:ss"));
                        if (hasTimeTo)
                            liteCmd.Parameters.AddWithValue("@TimeTo", filter.TimeTo.ToString("yyyy-MM-dd HH:mm:ss"));
                        if (hasSqlInstance)
                            liteCmd.Parameters.AddWithValue("@SqlInstance", filter.Instances.Length > 0 ? string.Join(",", filter.Instances) : "");
                    }
                }

                using var reader = await cmd.ExecuteReaderAsync();
                var dt = new DataTable();

                for (int i = 0; i < reader.FieldCount; i++)
                    dt.Columns.Add(reader.GetName(i), typeof(string));

                int rowCount = 0;
                while (await reader.ReadAsync() && rowCount < maxRows)
                {
                    var row = dt.NewRow();
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        var val = reader.GetValue(i);
                        row[i] = val == DBNull.Value ? "(NULL)" : val.ToString() ?? "";
                    }
                    dt.Rows.Add(row);
                    rowCount++;
                }

                var truncated = reader.HasRows && rowCount >= maxRows ? $" (showing first {maxRows})" : "";
                return (true, $"OK — {rowCount} row(s) returned{truncated}.", dt);
            }
            catch (Exception ex)
            {
                return (false, $"Error: {ex.Message}", null);
            }
        }

        /// <summary>
        /// Executes a query against the local SQLTriage.db liveQueries database and
        /// returns up to <paramref name="maxRows"/> rows as a DataTable.
        /// </summary>
        public async Task<(bool Success, string Message, DataTable? Results)> ExecuteliveQueriesQueryAsync(
            string query, int maxRows = 50)
        {
            if (string.IsNullOrWhiteSpace(query))
                return (false, "Query is empty", null);

            try
            {
                using var conn = new SqliteConnection(_SqliteConnectionString);
                await conn.OpenAsync();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = query;
                cmd.CommandTimeout = 30;

                using var reader = await cmd.ExecuteReaderAsync();
                var dt = new DataTable();

                for (int i = 0; i < reader.FieldCount; i++)
                {
                    dt.Columns.Add(reader.GetName(i), typeof(string));
                }

                int rowCount = 0;
                while (await reader.ReadAsync() && rowCount < maxRows)
                {
                    var row = dt.NewRow();
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        var val = reader.GetValue(i);
                        row[i] = val == DBNull.Value ? "(NULL)" : val.ToString() ?? "";
                    }
                    dt.Rows.Add(row);
                    rowCount++;
                }

                var truncated = reader.HasRows && rowCount >= maxRows ? $" (showing first {maxRows})" : "";
                return (true, $"OK — {rowCount} row(s) returned{truncated}.", dt);
            }
            catch (Exception ex)
            {
                return (false, $"Error: {ex.Message}", null);
            }
        }

        /// <summary>
        /// Generates the CREATE TABLE DDL that will be used for a panel's liveQueries table,
        /// based on the SQL Server query schema. Returns the DDL string and any ALTER
        /// statements if the table already exists with missing columns.
        /// </summary>
        public async Task<(bool Success, string Message, string? Ddl)> GenerateCreateTableDdlAsync(
            IDbConnectionFactory connectionFactory, string panelId, string sqlServerQuery)
        {
            if (string.IsNullOrWhiteSpace(panelId))
                return (false, "Panel ID is required", null);
            if (string.IsNullOrWhiteSpace(sqlServerQuery))
                return (false, "SQL Server query is required", null);

            var tableName = SanitizeTableName(panelId);
            if (string.IsNullOrEmpty(tableName))
                return (false, "Panel ID produces an empty table name after sanitization", null);

            try
            {
                var (success, message, columns) = await TestSqlServerQueryAsync(connectionFactory, sqlServerQuery);
                if (!success || columns == null || columns.Count == 0)
                    return (false, $"Schema probe failed: {message}", null);

                // Add system columns
                columns.Insert(0, new ColumnInfo { ColumnName = "server_name", DataType = "string", IsNullable = false, MaxLength = 256 });
                columns.Add(new ColumnInfo { ColumnName = "collection_time", DataType = "datetime", IsNullable = false, MaxLength = 0 });

                var sb = new StringBuilder();

                // Check if table already exists
                using var liveQueriesConn = new SqliteConnection(_SqliteConnectionString);
                await liveQueriesConn.OpenAsync();

                using var checkCmd = liveQueriesConn.CreateCommand();
                checkCmd.CommandText = $"SELECT name FROM liveQueries_master WHERE type='table' AND name='{tableName}'";
                var exists = await checkCmd.ExecuteScalarAsync();

                if (exists == null)
                {
                    // Full CREATE TABLE
                    sb.AppendLine($"CREATE TABLE {tableName} (");
                    for (int i = 0; i < columns.Count; i++)
                    {
                        var col = columns[i];
                        var liveQueriesType = GetliveQueriesType(col.DataType, col.MaxLength);
                        var nullable = col.IsNullable ? "" : " NOT NULL";
                        var comma = i < columns.Count - 1 ? "," : "";
                        sb.AppendLine($"    {col.ColumnName} {liveQueriesType}{nullable}{comma}");
                    }
                    sb.AppendLine(");");
                }
                else
                {
                    // Table exists — show ALTER statements for missing columns
                    using var pragmaCmd = liveQueriesConn.CreateCommand();
                    pragmaCmd.CommandText = $"PRAGMA table_info({tableName})";
                    using var pragmaReader = await pragmaCmd.ExecuteReaderAsync();

                    var existingColumns = new HashSet<string>();
                    while (await pragmaReader.ReadAsync())
                        existingColumns.Add(pragmaReader.GetString(1));

                    bool anyMissing = false;
                    sb.AppendLine($"-- Table '{tableName}' already exists.");
                    foreach (var col in columns)
                    {
                        if (!existingColumns.Contains(col.ColumnName))
                        {
                            var liveQueriesType = GetliveQueriesType(col.DataType, col.MaxLength);
                            var nullable = col.IsNullable ? "" : " NOT NULL";
                            sb.AppendLine($"ALTER TABLE {tableName} ADD COLUMN {col.ColumnName} {liveQueriesType}{nullable};");
                            anyMissing = true;
                        }
                    }
                    if (!anyMissing)
                    {
                        sb.AppendLine($"-- All {columns.Count} columns are up to date. No changes needed.");
                    }
                }

                return (true, "DDL generated successfully.", sb.ToString());
            }
            catch (Exception ex)
            {
                return (false, $"Error: {ex.Message}", null);
            }
        }

        // ──────────────────────── Automatic Table Provisioning ──────────────

        /// <summary>
        /// Ensures the liveQueries table for a panel exists and its schema matches the
        /// SQL Server query.  Creates the table if it doesn't exist, or adds any
        /// missing columns if it does.  Schema-only — does NOT insert data.
        /// </summary>
        public async Task<(bool Success, string Message)> EnsureTableForPanelAsync(
            IDbConnectionFactory connectionFactory,
            PanelDefinition panel)
        {
            if (panel == null || string.IsNullOrWhiteSpace(panel.Id))
                return (false, "Panel or panel ID is null");

            if (string.IsNullOrWhiteSpace(panel.Query.SqlServer))
                return (false, $"Panel '{panel.Id}' has no SQL Server query — skipped");

            var tableName = SanitizeTableName(panel.Id);
            if (string.IsNullOrEmpty(tableName))
                return (false, $"Panel '{panel.Id}' produces an empty table name after sanitization");

            try
            {
                // Probe SQL Server for column schema (SchemaOnly — no data transferred)
                var (success, message, columns) = await TestSqlServerQueryAsync(connectionFactory, panel.Query.SqlServer);

                if (!success || columns == null || columns.Count == 0)
                    return (false, $"Panel '{panel.Id}': {message}");

                // Prepend server_name, append collection_time
                columns.Insert(0, new ColumnInfo
                {
                    ColumnName = "server_name",
                    DataType = "string",
                    IsNullable = false,
                    MaxLength = 256
                });
                columns.Add(new ColumnInfo
                {
                    ColumnName = "collection_time",
                    DataType = "datetime",
                    IsNullable = false,
                    MaxLength = 0
                });

                // Create or update the table schema only (no data)
                await CreateOrUpdateTableAsync(tableName, columns);

                return (true, $"Table '{tableName}' ensured with {columns.Count} columns.");
            }
            catch (Exception ex)
            {
                return (false, $"Panel '{panel.Id}': {ex.Message}");
            }
        }

        /// <summary>
        /// Ensures liveQueries tables exist for every panel that has a SQL Server query
        /// across all dashboards in the configuration.  Best-effort: individual
        /// panel failures are collected and returned but do not stop processing
        /// of remaining panels.
        /// </summary>
        public async Task<(int Succeeded, int Failed, List<string> Errors)> EnsureTablesForAllPanelsAsync(
            IDbConnectionFactory connectionFactory,
            DashboardConfigRoot config)
        {
            int succeeded = 0;
            int failed = 0;
            var errors = new List<string>();

            foreach (var dashboard in config.Dashboards)
            {
                foreach (var panel in dashboard.Panels)
                {
                    // Only process panels that have a SQL Server query and an ID
                    if (string.IsNullOrWhiteSpace(panel.Query.SqlServer))
                        continue;
                    if (string.IsNullOrWhiteSpace(panel.Id))
                        continue;

                    var (success, message) = await EnsureTableForPanelAsync(connectionFactory, panel);
                    if (success)
                    {
                        succeeded++;
                    }
                    else
                    {
                        failed++;
                        errors.Add(message);
                    }
                }
            }

            return (succeeded, failed, errors);
        }

        /// <summary>
        /// Test a SQL Server query and return information about the result set.
        /// </summary>
        public async Task<(bool Success, string Message, List<ColumnInfo>? Columns)> TestSqlServerQueryAsync(
            IDbConnectionFactory connectionFactory,
            string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return (false, "Query is empty", null);
            }

            try
            {
                using var connection = await connectionFactory.CreateConnectionAsync();

                DbCommand? cmd = null;
                if (connectionFactory.DataSourceType == "SqlServer")
                {
                    cmd = (SqlCommand)connection.CreateCommand();
                }
                else
                {
                    cmd = (SqliteCommand)connection.CreateCommand();
                }

                cmd.CommandText = query;
                cmd.CommandTimeout = 30;

                // Get schema information without executing the full query
                using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SchemaOnly);

                var columns = new List<ColumnInfo>();
                var schemaTable = reader.GetColumnSchema();

                foreach (var column in schemaTable)
                {
                    columns.Add(new ColumnInfo
                    {
                        ColumnName = column.ColumnName ?? "",
                        DataType = column.DataType?.Name ?? "unknown",
                        IsNullable = column.AllowDBNull ?? true,
                        MaxLength = column.ColumnSize ?? 0
                    });
                }

                return (true, $"Query OK. Found {columns.Count} columns.", columns);
            }
            catch (Exception ex)
            {
                return (false, $"Error: {ex.Message}", null);
            }
        }

        /// <summary>
        /// Validate a liveQueries query and create/update the table if needed.
        /// Uses the panel ID as the table name.
        /// </summary>
        public async Task<(bool Success, string Message)> ValidateAndCreateTableAsync(
            IDbConnectionFactory sqlServerConnectionFactory,
            string tableName,
            string sqlServerQuery,
            string serverName)
        {
            if (string.IsNullOrWhiteSpace(tableName))
            {
                return (false, "Table name is required");
            }

            // Sanitize table name - only allow alphanumeric and underscore
            tableName = SanitizeTableName(tableName);

            try
            {
                // First, test the SQL Server query to get the schema
                var (success, message, columns) = await TestSqlServerQueryAsync(sqlServerConnectionFactory, sqlServerQuery);

                if (!success || columns == null || columns.Count == 0)
                {
                    return (false, $"SQL Server query failed: {message}");
                }

                // Add server_name column for filtering
                columns.Insert(0, new ColumnInfo
                {
                    ColumnName = "server_name",
                    DataType = "string",
                    IsNullable = false,
                    MaxLength = 256
                });

                // Add collection_time column for time-series data
                columns.Add(new ColumnInfo
                {
                    ColumnName = "collection_time",
                    DataType = "datetime",
                    IsNullable = false,
                    MaxLength = 0
                });

                // Create or update the liveQueries table
                await CreateOrUpdateTableAsync(tableName, columns);

                // Execute the SQL Server query and insert data into liveQueries
                await InsertDataFromSqlServerAsync(sqlServerConnectionFactory, tableName, sqlServerQuery, serverName);

                return (true, $"Table '{tableName}' created/updated successfully with {columns.Count} columns.");
            }
            catch (Exception ex)
            {
                return (false, $"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Create or update a liveQueries table based on column definitions.
        /// </summary>
        private async Task CreateOrUpdateTableAsync(string tableName, List<ColumnInfo> columns)
        {
            using var SqliteConnection = new SqliteConnection(_SqliteConnectionString);
            await SqliteConnection.OpenAsync();

            // Check if table exists
            var checkTableSql = $"SELECT name FROM liveQueries_master WHERE type='table' AND name='{tableName}'";
            using var checkCmd = SqliteConnection.CreateCommand();
            checkCmd.CommandText = checkTableSql;
            var existingTable = await checkCmd.ExecuteScalarAsync();

            string createTableSql;

            if (existingTable == null)
            {
                // Create new table
                var columnDefs = new StringBuilder();
                foreach (var col in columns)
                {
                    var liveQueriesType = GetliveQueriesType(col.DataType, col.MaxLength);
                    var nullable = col.IsNullable ? "" : " NOT NULL";
                    columnDefs.AppendLine($"    {col.ColumnName} {liveQueriesType}{nullable},");
                }

                createTableSql = $@"
                    CREATE TABLE IF NOT EXISTS {tableName} (
                        {columnDefs.ToString().TrimEnd(',', '\n')}
                    )";
            }
            else
            {
                // Table exists - add missing columns
                var alterStatements = new List<string>();

                // Get existing columns
                var pragmaCmd = SqliteConnection.CreateCommand();
                pragmaCmd.CommandText = $"PRAGMA table_info({tableName})";
                using var pragmaReader = await pragmaCmd.ExecuteReaderAsync();

                var existingColumns = new HashSet<string>();
                while (await pragmaReader.ReadAsync())
                {
                    existingColumns.Add(pragmaReader.GetString(1));
                }

                // Add missing columns
                foreach (var col in columns)
                {
                    if (!existingColumns.Contains(col.ColumnName))
                    {
                        var liveQueriesType = GetliveQueriesType(col.DataType, col.MaxLength);
                        var nullable = col.IsNullable ? "" : " NOT NULL";
                        alterStatements.Add($"ALTER TABLE {tableName} ADD COLUMN {col.ColumnName} {liveQueriesType}{nullable}");
                    }
                }

                if (alterStatements.Count > 0)
                {
                    foreach (var alterSql in alterStatements)
                    {
                        using var alterCmd = SqliteConnection.CreateCommand();
                        alterCmd.CommandText = alterSql;
                        await alterCmd.ExecuteNonQueryAsync();
                    }
                }

                return;
            }

            using var createCmd = SqliteConnection.CreateCommand();
            createCmd.CommandText = createTableSql;
            await createCmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Execute SQL Server query and insert results into liveQueries table.
        /// </summary>
        private async Task InsertDataFromSqlServerAsync(
            IDbConnectionFactory sqlServerConnectionFactory,
            string tableName,
            string sqlServerQuery,
            string serverName)
        {
            using var sqlConnection = await sqlServerConnectionFactory.CreateConnectionAsync();

            using var sqlCmd = (SqlCommand)sqlConnection.CreateCommand();
            sqlCmd.CommandText = sqlServerQuery;
            sqlCmd.CommandTimeout = 60;

            using var reader = await sqlCmd.ExecuteReaderAsync();

            // Get column names
            var columnNames = new List<string>();
            var schemaTable = reader.GetColumnSchema();
            columnNames.Add("server_name"); // Add server_name first
            foreach (var col in schemaTable)
            {
                columnNames.Add(col.ColumnName ?? "unknown");
            }
            columnNames.Add("collection_time"); // Add collection_time last

            // Insert data into liveQueries
            using var SqliteConnection = new SqliteConnection(_SqliteConnectionString);
            await SqliteConnection.OpenAsync();

            while (await reader.ReadAsync())
            {
                var values = new List<string>();
                values.Add($"'{SanitizeSqlValue(serverName)}'"); // server_name

                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.GetValue(i);
                    if (value == DBNull.Value || value == null)
                    {
                        values.Add("NULL");
                    }
                    else if (value is string strValue)
                    {
                        values.Add($"'{SanitizeSqlValue(strValue)}'");
                    }
                    else if (value is DateTime dtValue)
                    {
                        values.Add($"'{dtValue:yyyy-MM-dd HH:mm:ss}'");
                    }
                    else
                    {
                        values.Add(value.ToString() ?? "NULL");
                    }
                }

                values.Add($"'{DateTime.Now:yyyy-MM-dd HH:mm:ss}'"); // collection_time

                var insertSql = $"INSERT INTO {tableName} ({string.Join(",", columnNames)}) VALUES ({string.Join(",", values)})";

                using var insertCmd = SqliteConnection.CreateCommand();
                insertCmd.CommandText = insertSql;
                await insertCmd.ExecuteNonQueryAsync();
            }
        }

        /// <summary>
        /// Get liveQueries data type from SQL Server data type.
        /// </summary>
        private string GetliveQueriesType(string dataType, int maxLength)
        {
            var dt = dataType?.ToLowerInvariant() ?? "";

            if (dt.Contains("int") || dt.Contains("bigint") || dt.Contains("smallint") || dt.Contains("tinyint"))
                return "INTEGER";
            if (dt.Contains("decimal") || dt.Contains("numeric") || dt.Contains("float") || dt.Contains("real") || dt.Contains("money"))
                return "REAL";
            if (dt.Contains("date") || dt.Contains("time"))
                return "TEXT";
            if (dt.Contains("binary") || dt.Contains("image"))
                return "BLOB";

            // Default to TEXT for strings
            return "TEXT";
        }

        /// <summary>
        /// Sanitize table name to prevent SQL injection.
        /// </summary>
        private string SanitizeTableName(string tableName)
        {
            var result = new StringBuilder();
            foreach (var c in tableName)
            {
                if (char.IsLetterOrDigit(c) || c == '_')
                    result.Append(c);
            }
            return result.ToString();
        }

        /// <summary>
        /// Sanitize SQL value to prevent SQL injection.
        /// </summary>
        private string SanitizeSqlValue(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            return value.Replace("'", "''");
        }
    }

    public class ColumnInfo
    {
        public string ColumnName { get; set; } = "";
        public string DataType { get; set; } = "";
        public bool IsNullable { get; set; } = true;
        public int MaxLength { get; set; } = 0;
    }
}
