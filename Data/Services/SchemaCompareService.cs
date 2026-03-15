/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Text;

namespace SqlHealthAssessment.Data.Services;

/// <summary>
/// Service for comparing database schemas and generating diff scripts
/// </summary>
public class SchemaCompareService
{
    private readonly ILogger<SchemaCompareService> _logger;
    private readonly ServerConnectionManager _connectionManager;

    public SchemaCompareService(ILogger<SchemaCompareService> logger, ServerConnectionManager connectionManager)
    {
        _logger = logger;
        _connectionManager = connectionManager;
    }

    /// <summary>
    /// Get all objects from a database
    /// </summary>
    public async Task<List<SchemaObject>> GetSchemaObjectsAsync(string connectionString, string databaseName)
    {
        var objects = new List<SchemaObject>();

        // Validate database name to prevent SQL injection
        if (!IsValidDatabaseName(databaseName))
        {
            _logger.LogWarning("Invalid database name provided: {DatabaseName}", databaseName);
            return objects;
        }

        try
        {
            // First, switch to the target database using parameterized approach
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            // Get tables - validate and use safe query
            var tablesSql = @"
                SELECT 
                    'U' AS ObjectType,
                    s.name AS SchemaName,
                    t.name AS ObjectName,
                    t.create_date AS CreateDate,
                    t.modify_date AS ModifyDate
                FROM sys.tables t
                INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
                WHERE t.is_ms_shipped = 0
                ORDER BY s.name, t.name";

            using var cmd = new SqlCommand(tablesSql, connection);
            cmd.CommandTimeout = 60;

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                objects.Add(new SchemaObject
                {
                    ObjectType = "Table",
                    SchemaName = reader.GetString(1),
                    ObjectName = reader.GetString(2),
                    CreateDate = reader.GetDateTime(3),
                    ModifyDate = reader.GetDateTime(4)
                });
            }
            await reader.CloseAsync();

            // Get views - use separate query without USE statement
            var viewsSql = @"
                SELECT 
                    'V' AS ObjectType,
                    s.name AS SchemaName,
                    v.name AS ObjectName,
                    v.create_date AS CreateDate,
                    v.modify_date AS ModifyDate
                FROM sys.views v
                INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
                WHERE v.is_ms_shipped = 0
                ORDER BY s.name, v.name";

            using var cmdViews = new SqlCommand(viewsSql, connection);
            using var readerViews = await cmdViews.ExecuteReaderAsync();
            while (await readerViews.ReadAsync())
            {
                objects.Add(new SchemaObject
                {
                    ObjectType = "View",
                    SchemaName = readerViews.GetString(1),
                    ObjectName = readerViews.GetString(2),
                    CreateDate = readerViews.GetDateTime(3),
                    ModifyDate = readerViews.GetDateTime(4)
                });
            }
            await readerViews.CloseAsync();

            // Get stored procedures - use separate query without USE statement
            var procsSql = @"
                SELECT 
                    'P' AS ObjectType,
                    s.name AS SchemaName,
                    p.name AS ObjectName,
                    p.create_date AS CreateDate,
                    p.modify_date AS ModifyDate
                FROM sys.procedures p
                INNER JOIN sys.schemas s ON p.schema_id = s.schema_id
                WHERE p.is_ms_shipped = 0
                ORDER BY s.name, p.name";

            using var cmdProcs = new SqlCommand(procsSql, connection);
            using var readerProcs = await cmdProcs.ExecuteReaderAsync();
            while (await readerProcs.ReadAsync())
            {
                objects.Add(new SchemaObject
                {
                    ObjectType = "StoredProcedure",
                    SchemaName = readerProcs.GetString(1),
                    ObjectName = readerProcs.GetString(2),
                    CreateDate = readerProcs.GetDateTime(3),
                    ModifyDate = readerProcs.GetDateTime(4)
                });
            }
            await readerProcs.CloseAsync();

            // Get functions - use separate query without USE statement
            var funcsSql = @"
                SELECT 
                    'FN' AS ObjectType,
                    s.name AS SchemaName,
                    o.name AS ObjectName,
                    o.create_date AS CreateDate,
                    o.modify_date AS ModifyDate
                FROM sys.objects o
                INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE o.type IN ('FN', 'IF', 'TF') AND o.is_ms_shipped = 0
                ORDER BY s.name, o.name";

            using var cmdFuncs = new SqlCommand(funcsSql, connection);
            using var readerFuncs = await cmdFuncs.ExecuteReaderAsync();
            while (await readerFuncs.ReadAsync())
            {
                objects.Add(new SchemaObject
                {
                    ObjectType = "Function",
                    SchemaName = readerFuncs.GetString(1),
                    ObjectName = readerFuncs.GetString(2),
                    CreateDate = readerFuncs.GetDateTime(3),
                    ModifyDate = readerFuncs.GetDateTime(4)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting schema objects from {Database}", databaseName);
        }

        return objects;
    }

    /// <summary>
    /// Compare two databases and return differences
    /// </summary>
    public async Task<SchemaComparisonResult> CompareDatabasesAsync(string sourceConnectionString, string targetConnectionString, string sourceDb, string targetDb)
    {
        var result = new SchemaComparisonResult
        {
            SourceDatabase = sourceDb,
            TargetDatabase = targetDb
        };

        try
        {
            var sourceObjects = await GetSchemaObjectsAsync(sourceConnectionString, sourceDb);
            var targetObjects = await GetSchemaObjectsAsync(targetConnectionString, targetDb);

            var sourceDict = sourceObjects.ToDictionary(o => $"{o.SchemaName}.{o.ObjectName}");
            var targetDict = targetObjects.ToDictionary(o => $"{o.SchemaName}.{o.ObjectName}");

            // Find objects only in source (new in target)
            foreach (var source in sourceObjects)
            {
                var key = $"{source.SchemaName}.{source.ObjectName}";
                if (!targetDict.ContainsKey(key))
                {
                    result.Differences.Add(new SchemaDifference
                    {
                        ObjectName = key,
                        ObjectType = source.ObjectType,
                        DifferenceType = "OnlyInSource",
                        SourceObject = source,
                        TargetObject = null
                    });
                }
                else if (source.ModifyDate > targetDict[key].ModifyDate)
                {
                    result.Differences.Add(new SchemaDifference
                    {
                        ObjectName = key,
                        ObjectType = source.ObjectType,
                        DifferenceType = "Modified",
                        SourceObject = source,
                        TargetObject = targetDict[key]
                    });
                }
            }

            // Find objects only in target (deleted in source)
            foreach (var target in targetObjects)
            {
                var key = $"{target.SchemaName}.{target.ObjectName}";
                if (!sourceDict.ContainsKey(key))
                {
                    result.Differences.Add(new SchemaDifference
                    {
                        ObjectName = key,
                        ObjectType = target.ObjectType,
                        DifferenceType = "OnlyInTarget",
                        SourceObject = null,
                        TargetObject = target
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error comparing databases {SourceDb} and {TargetDb}", sourceDb, targetDb);
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Generate ALTER script to sync target to source
    /// </summary>
    public string GenerateSyncScript(SchemaComparisonResult result)
    {
        var sb = new StringBuilder();

        sb.AppendLine("-- Schema Synchronization Script");
        sb.AppendLine($"-- Source: {result.SourceDatabase}");
        sb.AppendLine($"-- Target: {result.TargetDatabase}");
        sb.AppendLine($"-- Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("-- ===============================================");
        sb.AppendLine();

        foreach (var diff in result.Differences.OrderBy(d => d.ObjectType).ThenBy(d => d.ObjectName))
        {
            switch (diff.DifferenceType)
            {
                case "OnlyInSource":
                    sb.AppendLine($"-- CREATE {diff.ObjectType}: {diff.ObjectName}");
                    sb.AppendLine($"-- TODO: Add CREATE statement for [{diff.SchemaName}].[{diff.ObjectName}]");
                    sb.AppendLine();
                    break;

                case "OnlyInTarget":
                    sb.AppendLine($"-- DROP {diff.ObjectType}: {diff.ObjectName}");
                    sb.AppendLine($"-- WARNING: Object exists only in target database");
                    sb.AppendLine($"-- DROP {diff.ObjectType} [{diff.SchemaName}].[{diff.ObjectName}];");
                    sb.AppendLine();
                    break;

                case "Modified":
                    sb.AppendLine($"-- ALTER {diff.ObjectType}: {diff.ObjectName}");
                    sb.AppendLine($"-- Source modified: {diff.SourceObject?.ModifyDate}");
                    sb.AppendLine($"-- Target modified: {diff.TargetObject?.ModifyDate}");
                    sb.AppendLine($"-- TODO: Add ALTER statement for [{diff.SchemaName}].[{diff.ObjectName}]");
                    sb.AppendLine();
                    break;
            }
        }

        if (!result.Differences.Any())
        {
            sb.AppendLine("-- Databases are in sync - no differences found.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Get object definition (CREATE script)
    /// </summary>
    public async Task<string> GetObjectDefinitionAsync(string connectionString, string databaseName, string schemaName, string objectName, string objectType)
    {
        // Validate inputs to prevent SQL injection
        if (!IsValidDatabaseName(databaseName) || !IsValidSchemaName(schemaName) || !IsValidObjectName(objectName))
        {
            _logger.LogWarning("Invalid parameters provided to GetObjectDefinitionAsync");
            return "-- Error: Invalid parameters";
        }

        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            // Use sp_executesql with proper parameterization
            var sql = @"USE @DatabaseName; SELECT OBJECT_DEFINITION(OBJECT_ID(@ObjectName))";

            using var cmd = new SqlCommand(sql, connection);
            cmd.CommandTimeout = 30;
            cmd.Parameters.Add(new SqlParameter("@DatabaseName", databaseName));
            cmd.Parameters.Add(new SqlParameter("@ObjectName", $"[{schemaName}].[{objectName}]"));

            var result = await cmd.ExecuteScalarAsync();
            return result?.ToString() ?? $"-- Could not retrieve definition for {schemaName}.{objectName}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting definition for {Schema}.{Object}", schemaName, objectName);
            return $"-- Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Get list of databases on a server
    /// </summary>
    public async Task<List<string>> GetDatabasesAsync(string connectionString)
    {
        var databases = new List<string>();

        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var sql = @"
                SELECT name 
                FROM sys.databases 
                WHERE state = 0 
                AND name NOT IN ('master', 'tempdb', 'model', 'msdb')
                ORDER BY name";

            using var cmd = new SqlCommand(sql, connection);
            using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                databases.Add(reader.GetString(0));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting database list");
        }

        return databases;
    }

    /// <summary>
    /// Validates database name to prevent SQL injection.
    /// Only allows alphanumeric characters, underscores, and hyphens (max 128 chars).
    /// </summary>
    private static bool IsValidDatabaseName(string databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName) || databaseName.Length > 128)
            return false;

        return System.Text.RegularExpressions.Regex.IsMatch(databaseName, @"^[a-zA-Z0-9_\\-]+$");
    }

    /// <summary>
    /// Validates schema name to prevent SQL injection
    /// </summary>
    private static bool IsValidSchemaName(string schemaName)
    {
        if (string.IsNullOrWhiteSpace(schemaName))
            return false;
        return System.Text.RegularExpressions.Regex.IsMatch(schemaName, @"^[a-zA-Z0-9_]+$");
    }

    /// <summary>
    /// Validates object name to prevent SQL injection
    /// </summary>
    private static bool IsValidObjectName(string objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName))
            return false;
        return System.Text.RegularExpressions.Regex.IsMatch(objectName, @"^[a-zA-Z0-9_]+$");
    }
}

/// <summary>
/// Schema object model
/// </summary>
public class SchemaObject
{
    public string ObjectType { get; set; } = string.Empty;
    public string SchemaName { get; set; } = string.Empty;
    public string ObjectName { get; set; } = string.Empty;
    public DateTime CreateDate { get; set; }
    public DateTime ModifyDate { get; set; }
    public string FullName => $"{SchemaName}.{ObjectName}";
}

/// <summary>
/// Schema comparison result
/// </summary>
public class SchemaComparisonResult
{
    public string SourceDatabase { get; set; } = string.Empty;
    public string TargetDatabase { get; set; } = string.Empty;
    public List<SchemaDifference> Differences { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Schema difference
/// </summary>
public class SchemaDifference
{
    public string ObjectName { get; set; } = string.Empty;
    public string ObjectType { get; set; } = string.Empty;
    public string DifferenceType { get; set; } = string.Empty; // OnlyInSource, OnlyInTarget, Modified
    public SchemaObject? SourceObject { get; set; }
    public SchemaObject? TargetObject { get; set; }
    public string SchemaName => SourceObject?.SchemaName ?? TargetObject?.SchemaName ?? "";
}
