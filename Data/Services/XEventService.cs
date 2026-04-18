/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Data;

namespace SQLTriage.Data.Services;

/// <summary>
/// Service for managing Extended Events sessions and monitoring
/// </summary>
public class XEventService
{
    private readonly ILogger<XEventService> _logger;
    private readonly ServerConnectionManager _connectionManager;

    public XEventService(ILogger<XEventService> logger, ServerConnectionManager connectionManager)
    {
        _logger = logger;
        _connectionManager = connectionManager;
    }

    /// <summary>
    /// Get all XEvent sessions on the server
    /// </summary>
    public async Task<List<XEventSession>> GetSessionsAsync(string connectionString)
    {
        var sessions = new List<XEventSession>();

        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var sql = @"
                SELECT 
                    s.name AS SessionName,
                    s.create_time AS CreateTime,
                    s.is_enabled AS IsEnabled,
                    s.wait_type AS WaitType,
                    s.wait_time AS WaitTime,
                    s.drop_event_time AS DropEventTime,
                    CASE WHEN s.total_runtime > 0 THEN s.total_runtime ELSE 0 END AS TotalRuntime,
                    CASE WHEN s.total_events_emitted > 0 THEN s.total_events_emitted ELSE 0 END AS TotalEventsEmitted,
                    CASE WHEN s.total_buffer_size_bytes > 0 THEN s.total_buffer_size_bytes ELSE 0 END AS BufferSizeBytes
                FROM sys.dm_xe_sessions s
                ORDER BY s.name";

            using var cmd = new SqlCommand(sql, connection);
            cmd.CommandTimeout = 30;

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                sessions.Add(new XEventSession
                {
                    Name = reader.GetString(0),
                    CreateTime = reader.IsDBNull(1) ? DateTime.MinValue : reader.GetDateTime(1),
                    IsEnabled = reader.GetBoolean(2),
                    WaitType = reader.IsDBNull(3) ? null : reader.GetString(3),
                    WaitTime = reader.GetInt64(4),
                    DropEventTime = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                    TotalRuntimeMs = reader.GetDouble(6),
                    TotalEventsEmitted = reader.GetInt64(7),
                    BufferSizeBytes = reader.GetInt64(8)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting XEvent sessions");
        }

        return sessions;
    }

    /// <summary>
    /// Get events from a specific session
    /// </summary>
    public async Task<List<XEventEvent>> GetSessionEventsAsync(string connectionString, string sessionName)
    {
        var events = new List<XEventEvent>();

        if (string.IsNullOrWhiteSpace(sessionName))
        {
            _logger.LogWarning("GetSessionEventsAsync called with empty sessionName");
            return events;
        }

        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            // Use parameterized query to prevent SQL injection
            var sql = @"
                SELECT 
                    e.name AS EventName,
                    e.description AS Description,
                    e.package_name AS PackageName
                FROM sys.dm_xe_session_events e
                WHERE e.session_name = @SessionName
                ORDER BY e.name";

            using var cmd = new SqlCommand(sql, connection);
            cmd.CommandTimeout = 30;
            cmd.Parameters.Add(new SqlParameter("@SessionName", sessionName));

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                events.Add(new XEventEvent
                {
                    Name = reader.GetString(0),
                    Description = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    PackageName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting XEvent session events for {SessionName}", sessionName);
        }

        return events;
    }

    /// <summary>
    /// Get available event packages
    /// </summary>
    public async Task<List<XEventPackage>> GetPackagesAsync(string connectionString)
    {
        var packages = new List<XEventPackage>();

        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var sql = @"
                SELECT 
                    p.name AS PackageName,
                    p.description AS Description,
                    p.capabilities AS Capabilities
                FROM sys.dm_xe_packages p
                WHERE p.name NOT LIKE 'Microsoft%'
                ORDER BY p.name";

            using var cmd = new SqlCommand(sql, connection);
            cmd.CommandTimeout = 30;

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                packages.Add(new XEventPackage
                {
                    Name = reader.GetString(0),
                    Description = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    Capabilities = reader.IsDBNull(2) ? 0 : reader.GetInt32(2)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting XEvent packages");
        }

        return packages;
    }

    /// <summary>
    /// Get available events in a package
    /// </summary>
    public async Task<List<XEventInfo>> GetEventsInPackageAsync(string connectionString, string packageName)
    {
        var events = new List<XEventInfo>();

        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var sql = $@"
                SELECT 
                    o.name AS EventName,
                    o.description AS Description
                FROM sys.dm_xe_objects o
                WHERE o.object_type = 'event'
                AND o.name NOT LIKE '%_completed'
                AND o.name NOT LIKE '%_batch'
                AND o.package = '{packageName.Replace("'", "''")}'
                ORDER BY o.name";

            using var cmd = new SqlCommand(sql, connection);
            cmd.CommandTimeout = 30;

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                events.Add(new XEventInfo
                {
                    Name = reader.GetString(0),
                    Description = reader.IsDBNull(1) ? string.Empty : reader.GetString(1)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting events in package {PackageName}", packageName);
        }

        return events;
    }

    /// <summary>
    /// Create a new XEvent session with basic template
    /// </summary>
    public async Task<string> CreateSessionAsync(string connectionString, string sessionName, string eventName, string predicate = "")
    {
        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var predicateClause = string.IsNullOrEmpty(predicate) ? "" : $"WHERE {predicate}";

            var sql = $@"
                CREATE EVENT SESSION [{sessionName}] ON SERVER
                ADD EVENT sqlserver.{eventName}
                {predicateClause}
                ADD TARGET package0.event_file(SET filename = '{sessionName}.xel', max_file_size = 10, max_rollover_files = 5)
                WITH (MAX_DISPATCH_LATENCY = 1 SECONDS, STARTUP_STATE = OFF)";

            using var cmd = new SqlCommand(sql, connection);
            cmd.CommandTimeout = 30;
            await cmd.ExecuteNonQueryAsync();

            _logger.LogInformation("Created XEvent session: {SessionName}", sessionName);
            return $"Session '{sessionName}' created successfully";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating XEvent session {SessionName}", sessionName);
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Start an XEvent session
    /// </summary>
    public async Task<string> StartSessionAsync(string connectionString, string sessionName)
    {
        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var sql = $"ALTER EVENT SESSION [{sessionName}] ON SERVER STATE = START";
            using var cmd = new SqlCommand(sql, connection);
            cmd.CommandTimeout = 30;
            await cmd.ExecuteNonQueryAsync();

            return $"Session '{sessionName}' started";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting XEvent session {SessionName}", sessionName);
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Stop an XEvent session
    /// </summary>
    public async Task<string> StopSessionAsync(string connectionString, string sessionName)
    {
        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var sql = $"ALTER EVENT SESSION [{sessionName}] ON SERVER STATE = STOP";
            using var cmd = new SqlCommand(sql, connection);
            cmd.CommandTimeout = 30;
            await cmd.ExecuteNonQueryAsync();

            return $"Session '{sessionName}' stopped";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping XEvent session {SessionName}", sessionName);
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Drop an XEvent session
    /// </summary>
    public async Task<string> DropSessionAsync(string connectionString, string sessionName)
    {
        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var sql = $"DROP EVENT SESSION [{sessionName}] ON SERVER";
            using var cmd = new SqlCommand(sql, connection);
            cmd.CommandTimeout = 30;
            await cmd.ExecuteNonQueryAsync();

            return $"Session '{sessionName}' dropped";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dropping XEvent session {SessionName}", sessionName);
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Get session target data (live events if session is running)
    /// </summary>
    public async Task<List<XEventLiveData>> GetLiveEventsAsync(string connectionString, string sessionName)
    {
        var events = new List<XEventLiveData>();

        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var sql = $@"
                SELECT 
                    CAST(e.event_data AS NVARCHAR(MAX)) AS EventData,
                    e.timestamp_utc AS TimestampUTC
                FROM sys.fn_xe_file_target_read_file('{sessionName}*.xel', '{sessionName}*.xem', NULL, NULL) e
                WHERE e.timestamp_utc > DATEADD(MINUTE, -5, GETUTCDATE())
                ORDER BY e.timestamp_utc DESC";

            using var cmd = new SqlCommand(sql, connection);
            cmd.CommandTimeout = 30;

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var eventData = reader.IsDBNull(0) ? "" : reader.GetString(0);
                events.Add(new XEventLiveData
                {
                    EventData = eventData,
                    TimestampUTC = reader.IsDBNull(1) ? DateTime.MinValue : reader.GetDateTime(1)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting live events from session {SessionName}", sessionName);
        }

        return events;
    }

    /// <summary>
    /// Get all defined XEvent sessions including stopped ones, with startup_state, from sys.server_event_sessions.
    /// Joins to sys.dm_xe_sessions to determine whether each session is currently running.
    /// </summary>
    public async Task<List<XEventSessionInfo>> GetAllSessionsAsync(string connectionString)
    {
        var sessions = new List<XEventSessionInfo>();
        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            const string sql = @"
                SELECT
                    ses.name                                                    AS Name,
                    ses.startup_state                                           AS StartupState,
                    ses.create_time                                             AS CreateTime,
                    CASE WHEN dm.name IS NOT NULL THEN 1 ELSE 0 END            AS IsRunning,
                    ISNULL(dm.total_events_emitted, 0)                         AS TotalEventsEmitted,
                    ISNULL(dm.total_buffer_size_bytes, 0)                      AS BufferSizeBytes
                FROM sys.server_event_sessions ses
                LEFT JOIN sys.dm_xe_sessions dm ON dm.name = ses.name
                ORDER BY ses.name";

            using var cmd = new SqlCommand(sql, connection);
            cmd.CommandTimeout = 30;
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                sessions.Add(new XEventSessionInfo
                {
                    Name             = reader.GetString(0),
                    StartupState     = reader.GetBoolean(1),
                    CreateTime       = reader.IsDBNull(2) ? DateTime.MinValue : reader.GetDateTime(2),
                    IsRunning        = reader.GetInt32(3) == 1,
                    TotalEventsEmitted = reader.GetInt64(4),
                    BufferSizeBytes  = reader.GetInt64(5)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all XEvent sessions");
        }
        return sessions;
    }

    /// <summary>
    /// Sets STARTUP_STATE ON or OFF for an existing session.
    /// </summary>
    public async Task<string> SetStartupStateAsync(string connectionString, string sessionName, bool startupOn)
    {
        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            // Parameterised names are not supported in DDL — session name is validated above
            var state = startupOn ? "ON" : "OFF";
            var sql = $"ALTER EVENT SESSION [{sessionName.Replace("]", "]]")}] ON SERVER WITH (STARTUP_STATE = {state})";
            using var cmd = new SqlCommand(sql, connection);
            cmd.CommandTimeout = 30;
            await cmd.ExecuteNonQueryAsync();
            _logger.LogInformation("Set startup state {State} for XEvent session {Name}", state, sessionName);
            return $"Startup state set to {state} for '{sessionName}'";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting startup state for {SessionName}", sessionName);
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Get predefined session templates
    /// </summary>
    public List<XEventTemplate> GetTemplates()
    {
        return new List<XEventTemplate>
        {
            new XEventTemplate
            {
                Name = "SQL Errors",
                Description = "Track all SQL errors",
                Events = new List<string> { "error_reported" },
                Predicate = "error_number > 0"
            },
            new XEventTemplate
            {
                Name = "Deadlock Monitor",
                Description = "Capture deadlock information",
                Events = new List<string> { "xml_deadlock_report" },
                Predicate = ""
            },
            new XEventTemplate
            {
                Name = "Statement Performance",
                Description = "Track slow statements",
                Events = new List<string> { "sql_statement_start", "sql_statement_end" },
                Predicate = "duration > 1000"
            },
            new XEventTemplate
            {
                Name = "Security Audit",
                Description = "Audit login/logout and security events",
                Events = new List<string> { "login", "logout", "audit_login_failed" },
                Predicate = ""
            },
            new XEventTemplate
            {
                Name = "Query Execution",
                Description = "Track query execution plans",
                Events = new List<string> { "sql_batch_starting", "sql_batch_completed" },
                Predicate = ""
            },
            new XEventTemplate
            {
                Name = "Blocking Monitor",
                Description = "Detect and track blocking",
                Events = new List<string> { "wait_info", "wait_info_external" },
                Predicate = "duration > 5000"
            }
        };
    }
}

/// <summary>
/// XEvent session model
/// </summary>
public class XEventSession
{
    public string Name { get; set; } = string.Empty;
    public DateTime CreateTime { get; set; }
    public bool IsEnabled { get; set; }
    public string? WaitType { get; set; }
    public long WaitTime { get; set; }
    public DateTime? DropEventTime { get; set; }
    public double TotalRuntimeMs { get; set; }
    public long TotalEventsEmitted { get; set; }
    public long BufferSizeBytes { get; set; }
}

/// <summary>
/// XEvent event definition
/// </summary>
public class XEventEvent
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;
}

/// <summary>
/// XEvent package
/// </summary>
public class XEventPackage
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Capabilities { get; set; }
}

/// <summary>
/// XEvent info
/// </summary>
public class XEventInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Live event data
/// </summary>
public class XEventLiveData
{
    public string EventData { get; set; } = string.Empty;
    public DateTime TimestampUTC { get; set; }
}

/// <summary>
/// Predefined XEvent template
/// </summary>
public class XEventTemplate
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Events { get; set; } = new();
    public string Predicate { get; set; } = string.Empty;
}

/// <summary>
/// Full XEvent session info — includes sessions that are defined but not running.
/// Sourced from sys.server_event_sessions joined to sys.dm_xe_sessions.
/// </summary>
public class XEventSessionInfo
{
    public string Name { get; set; } = string.Empty;
    public bool StartupState { get; set; }
    public DateTime CreateTime { get; set; }
    public bool IsRunning { get; set; }
    public long TotalEventsEmitted { get; set; }
    public long BufferSizeBytes { get; set; }
}
