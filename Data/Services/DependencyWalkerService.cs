/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Text;

namespace SqlHealthAssessment.Data.Services;

/// <summary>
/// Service for analyzing object dependencies and generating safe drop scripts
/// </summary>
public class DependencyWalkerService
{
    private readonly ILogger<DependencyWalkerService> _logger;
    private readonly ServerConnectionManager _connectionManager;

    public DependencyWalkerService(ILogger<DependencyWalkerService> logger, ServerConnectionManager connectionManager)
    {
        _logger = logger;
        _connectionManager = connectionManager;
    }

    /// <summary>
    /// Get all objects that depend on a given object (what depends on this)
    /// </summary>
    public async Task<List<DependencyInfo>> GetDependentsAsync(string connectionString, string databaseName, string schemaName, string objectName)
    {
        var dependencies = new List<DependencyInfo>();

        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var sql = $@"
                USE [{databaseName}];
                SELECT 
                    OBJECT_SCHEMA_NAME(d.object_id) AS DependentSchema,
                    OBJECT_NAME(d.object_id) AS DependentName,
                    d.object_id,
                    o.type_desc AS DependentType,
                    CASE 
                        WHEN d.class = 0 THEN 'Object or Column'
                        WHEN d.class = 1 THEN 'Object or Column (Referenced)'
                        WHEN d.class = 2 THEN 'User-defined Type'
                        WHEN d.class = 3 THEN 'XML Schema Collection'
                        WHEN d.class = 4 THEN 'Partition Function'
                        ELSE 'Other'
                    END AS DependencyClass
                FROM sys.sql_dependencies d
                INNER JOIN sys.objects o ON d.object_id = o.object_id
                WHERE d.referenced_major_id = OBJECT_ID('[{schemaName}].[{objectName}]')
                ORDER BY OBJECT_SCHEMA_NAME(d.object_id), OBJECT_NAME(d.object_id)";

            using var cmd = new SqlCommand(sql, connection);
            cmd.CommandTimeout = 60;

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                dependencies.Add(new DependencyInfo
                {
                    SchemaName = reader.GetString(0),
                    ObjectName = reader.GetString(1),
                    ObjectId = reader.GetInt32(2),
                    ObjectType = reader.GetString(3),
                    DependencyType = "DependsOn", // This object depends on the target
                    DependencyClass = reader.GetString(4)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting dependents for {Schema}.{Object}", schemaName, objectName);
        }

        return dependencies;
    }

    /// <summary>
    /// Get all objects that a given object depends on (what this depends on)
    /// </summary>
    public async Task<List<DependencyInfo>> GetDependenciesAsync(string connectionString, string databaseName, string schemaName, string objectName)
    {
        var dependencies = new List<DependencyInfo>();

        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var sql = $@"
                USE [{databaseName}];
                SELECT 
                    OBJECT_SCHEMA_NAME(d.referenced_major_id) AS ReferencedSchema,
                    OBJECT_NAME(d.referenced_major_id) AS ReferencedName,
                    d.referenced_major_id,
                    ISNULL(ro.type_desc, 'UNKNOWN') AS ReferencedType,
                    CASE 
                        WHEN d.class = 0 THEN 'Object or Column'
                        WHEN d.class = 1 THEN 'Object or Column (Referencing)'
                        WHEN d.class = 2 THEN 'User-defined Type'
                        WHEN d.class = 3 THEN 'XML Schema Collection'
                        WHEN d.class = 4 THEN 'Partition Function'
                        ELSE 'Other'
                    END AS DependencyClass
                FROM sys.sql_dependencies d
                LEFT JOIN sys.objects ro ON d.referenced_major_id = ro.object_id
                WHERE d.object_id = OBJECT_ID('[{schemaName}].[{objectName}]')
                AND d.referenced_major_id IS NOT NULL
                ORDER BY OBJECT_SCHEMA_NAME(d.referenced_major_id), OBJECT_NAME(d.referenced_major_id)";

            using var cmd = new SqlCommand(sql, connection);
            cmd.CommandTimeout = 60;

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                dependencies.Add(new DependencyInfo
                {
                    SchemaName = reader.IsDBNull(0) ? "" : reader.GetString(0),
                    ObjectName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    ObjectId = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                    ObjectType = reader.GetString(3),
                    DependencyType = "Referenced", // This object references these
                    DependencyClass = reader.GetString(4)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting dependencies for {Schema}.{Object}", schemaName, objectName);
        }

        return dependencies;
    }

    /// <summary>
    /// Get full dependency tree (recursive)
    /// </summary>
    public async Task<DependencyTree> GetDependencyTreeAsync(string connectionString, string databaseName, string schemaName, string objectName, int maxDepth = 10)
    {
        var tree = new DependencyTree
        {
            RootObject = new SchemaObject
            {
                SchemaName = schemaName,
                ObjectName = objectName,
                ObjectType = "Unknown"
            },
            Depth = 0
        };

        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            // First get the object's type
            var typeSql = $@"
                USE [{databaseName}];
                SELECT type_desc FROM sys.objects WHERE object_id = OBJECT_ID('[{schemaName}].[{objectName}]')";

            using var typeCmd = new SqlCommand(typeSql, connection);
            var typeResult = await typeCmd.ExecuteScalarAsync();
            if (typeResult != null)
            {
                tree.RootObject.ObjectType = typeResult.ToString() ?? "Unknown";
            }

            // Get dependencies (what this object depends on)
            tree.Children = await GetDependencyTreeRecursiveAsync(connection, databaseName, schemaName, objectName, 1, maxDepth);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting dependency tree for {Schema}.{Object}", schemaName, objectName);
            tree.ErrorMessage = ex.Message;
        }

        return tree;
    }

    private async Task<List<DependencyTree>> GetDependencyTreeRecursiveAsync(SqlConnection connection, string databaseName, string parentSchema, string parentName, int currentDepth, int maxDepth)
    {
        var children = new List<DependencyTree>();

        if (currentDepth >= maxDepth)
            return children;

        var sql = $@"
            USE [{databaseName}];
            SELECT 
                OBJECT_SCHEMA_NAME(d.referenced_major_id) AS RefSchema,
                OBJECT_NAME(d.referenced_major_id) AS RefName,
                ISNULL(ro.type_desc, 'UNKNOWN') AS RefType
            FROM sys.sql_dependencies d
            LEFT JOIN sys.objects ro ON d.referenced_major_id = ro.object_id
            WHERE d.object_id = OBJECT_ID('[{parentSchema}].[{parentName}]')
            AND d.referenced_major_id IS NOT NULL
            GROUP BY OBJECT_SCHEMA_NAME(d.referenced_major_id), OBJECT_NAME(d.referenced_major_id), ro.type_desc";

        using var cmd = new SqlCommand(sql, connection);
        using var reader = await cmd.ExecuteReaderAsync();

        var referencedObjects = new List<(string Schema, string Name, string Type)>();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(0) && !reader.IsDBNull(1))
            {
                referencedObjects.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }
        await reader.CloseAsync();

        foreach (var refObj in referencedObjects)
        {
            var childTree = new DependencyTree
            {
                RootObject = new SchemaObject
                {
                    SchemaName = refObj.Schema,
                    ObjectName = refObj.Name,
                    ObjectType = refObj.Type
                },
                Depth = currentDepth
            };

            // Recursively get children if it's a supported object type
            if (refObj.Type.Contains("PROCEDURE") || refObj.Type.Contains("VIEW") || refObj.Type.Contains("FUNCTION"))
            {
                childTree.Children = await GetDependencyTreeRecursiveAsync(connection, databaseName, refObj.Schema, refObj.Name, currentDepth + 1, maxDepth);
            }

            children.Add(childTree);
        }

        return children;
    }

    /// <summary>
    /// Generate safe drop script (checks dependencies first)
    /// </summary>
    public async Task<string> GenerateSafeDropScriptAsync(string connectionString, string databaseName, string schemaName, string objectName)
    {
        var sb = new StringBuilder();

        sb.AppendLine("-- Safe Drop Script");
        sb.AppendLine($"-- Object: [{schemaName}].[{objectName}]");
        sb.AppendLine($"-- Database: {databaseName}");
        sb.AppendLine($"-- Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("-- ===============================================");
        sb.AppendLine();

        // Check for dependents
        var dependents = await GetDependentsAsync(connectionString, databaseName, schemaName, objectName);

        if (dependents.Any())
        {
            sb.AppendLine("-- WARNING: The following objects depend on this object:");
            sb.AppendLine("-- You must drop or modify these objects first!");
            sb.AppendLine();
            foreach (var dep in dependents)
            {
                sb.AppendLine($"-- DROP would break: [{dep.SchemaName}].[{dep.ObjectName}] ({dep.ObjectType})");
            }
            sb.AppendLine();
            sb.AppendLine("-- TODO: Manually review and drop dependent objects first");
            sb.AppendLine("-- Then run:");
        }
        else
        {
            sb.AppendLine("-- No dependent objects found. Safe to drop.");
            sb.AppendLine();
        }

        // Get object type for proper drop statement
        var objType = "OBJECT";
        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var typeSql = $@"
                USE [{databaseName}];
                SELECT type FROM sys.objects WHERE object_id = OBJECT_ID('[{schemaName}].[{objectName}]')";

            using var typeCmd = new SqlCommand(typeSql, connection);
            var typeResult = await typeCmd.ExecuteScalarAsync();
            if (typeResult != null)
            {
                objType = typeResult.ToString() ?? "OBJECT";
            }
        }
        catch { }

        var dropKeyword = objType switch
        {
            "U" => "TABLE",
            "V" => "VIEW", 
            "P" => "PROCEDURE",
            "FN" => "FUNCTION",
            "IF" => "FUNCTION",
            "TF" => "FUNCTION",
            "TR" => "TRIGGER",
            _ => "OBJECT"
        };

        sb.AppendLine($"DROP {dropKeyword} [{schemaName}].[{objectName}];");
        sb.AppendLine();
        sb.AppendLine("-- GO");

        return sb.ToString();
    }

    /// <summary>
    /// Find objects by name pattern
    /// </summary>
    public async Task<List<SchemaObject>> SearchObjectsAsync(string connectionString, string databaseName, string searchPattern)
    {
        var objects = new List<SchemaObject>();

        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var sql = $@"
                USE [{databaseName}];
                SELECT 
                    o.type_desc AS ObjectType,
                    s.name AS SchemaName,
                    o.name AS ObjectName,
                    o.create_date AS CreateDate,
                    o.modify_date AS ModifyDate
                FROM sys.objects o
                INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE o.name LIKE '%{searchPattern.Replace("'", "''")}%'
                AND o.is_ms_shipped = 0
                AND o.type NOT IN ('IT', 'PK', 'SQ', 'UQ')
                ORDER BY o.type_desc, s.name, o.name";

            using var cmd = new SqlCommand(sql, connection);
            cmd.CommandTimeout = 30;

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                objects.Add(new SchemaObject
                {
                    ObjectType = reader.GetString(0),
                    SchemaName = reader.GetString(1),
                    ObjectName = reader.GetString(2),
                    CreateDate = reader.GetDateTime(3),
                    ModifyDate = reader.GetDateTime(4)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching objects with pattern {Pattern}", searchPattern);
        }

        return objects;
    }

    /// <summary>
    /// Check for circular dependencies
    /// </summary>
    public async Task<bool> HasCircularDependenciesAsync(string connectionString, string databaseName, string schemaName, string objectName)
    {
        try
        {
            // Simple check - look for circular reference in first level
            var directDeps = await GetDependenciesAsync(connectionString, databaseName, schemaName, objectName);
            
            foreach (var dep in directDeps)
            {
                if (string.IsNullOrEmpty(dep.SchemaName) || string.IsNullOrEmpty(dep.ObjectName))
                    continue;

                var reverseDeps = await GetDependentsAsync(connectionString, databaseName, dep.SchemaName, dep.ObjectName);
                
                if (reverseDeps.Any(r => r.SchemaName == schemaName && r.ObjectName == objectName))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking circular dependencies for {Schema}.{Object}", schemaName, objectName);
        }

        return false;
    }
}

/// <summary>
/// Dependency information
/// </summary>
public class DependencyInfo
{
    public string SchemaName { get; set; } = string.Empty;
    public string ObjectName { get; set; } = string.Empty;
    public int ObjectId { get; set; }
    public string ObjectType { get; set; } = string.Empty;
    public string DependencyType { get; set; } = string.Empty; // DependsOn, Referenced
    public string DependencyClass { get; set; } = string.Empty;
    public string FullName => $"{SchemaName}.{ObjectName}";
}

/// <summary>
/// Dependency tree node
/// </summary>
public class DependencyTree
{
    public SchemaObject RootObject { get; set; } = new();
    public int Depth { get; set; }
    public List<DependencyTree> Children { get; set; } = new();
    public string? ErrorMessage { get; set; }
}
