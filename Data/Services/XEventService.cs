/* In the name of God, the Merciful, the Compassionate */

using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services;

// BM:XEventService.Class — manages Extended Events sessions and monitoring
/// <summary>
/// Service for managing Extended Events sessions and monitoring
/// </summary>
public class XEventService
{
    private readonly ILogger<XEventService> _logger;
    private readonly ServerConnectionManager _connectionManager;

    // Ruling R1 (2026-09-01): the five lifecycle statements below are app-issued DDL against a
    // monitored server, so each writes the tamper-evident audit chain AND a Change Ledger row.
    // Before this lane they journaled nothing at all — an operator could create, start and drop an
    // event session on a client's instance and no register in the product held a record of it.
    // Nullable because a host may not register the journal; every call site is null-conditional, so
    // the absence degrades the record rather than the operation.
    private readonly DdlJournal? _journal;

    /// <summary>The audit/ledger surface id shared by all five lifecycle statements.</summary>
    private const string Surface = AuditLogService.DdlSurfaces.XEventLifecycle;

    public XEventService(ILogger<XEventService> logger, ServerConnectionManager connectionManager,
        DdlJournal? journal = null)
    {
        _logger = logger;
        _connectionManager = connectionManager;
        _journal = journal;
    }

    /// <summary>
    /// The instance name to journal against, read from the connection string WITHOUT opening a
    /// connection — the attempt has to be on disk before the connection is opened, and a failure
    /// to connect is itself an outcome worth recording against a named server. Returns
    /// "unknown-server" rather than throwing: a malformed connection string must not be the reason
    /// a DDL attempt goes unjournaled.
    /// </summary>
    private static string ServerFor(string connectionString)
    {
        try
        {
            var ds = new SqlConnectionStringBuilder(connectionString).DataSource;
            return string.IsNullOrWhiteSpace(ds) ? "unknown-server" : ds;
        }
        catch
        {
            return "unknown-server";
        }
    }

    /// <summary>
    /// Journals a terminal state for one lifecycle statement. Wrapped so all five paths record
    /// through one line each, and so the failure path cannot be the one that forgets: a failed DDL
    /// is journaled as failed, never as absent.
    /// </summary>
    private Task RecordOutcomeAsync(string operation, string server, string sql, string outcome,
        string? errorMessage = null)
        => _journal?.RecordOutcomeAsync(Surface, operation, server, sql, outcome, errorMessage)
           ?? Task.CompletedTask;

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

    // ── Identifier / literal escaping (the one place session names are made safe) ──────
    //
    // A session name is an IDENTIFIER, and T-SQL has no parameter form for one, so every
    // lifecycle statement in this file interpolates the name as text. Until 2026-09-01 four of
    // the five did it raw. XEvents.razor passes NewSessionName, a free-text box the operator
    // types, so a name carrying a ']' closed its own brackets and the rest of the name became
    // T-SQL. PROVED live on MSI\NEW2022 on 2026-08-25 by typing a name containing ']': one
    // CreateSessionAsync call emitted a three-statement batch and created TWO event sessions.
    //
    // The two helpers below are the chokepoint. Every lifecycle statement routes its name
    // through BracketQuote (identifier position) or QuoteLiteral (inside a string literal), so
    // there is one escaping rule and one place to read it. Escaping, not validation: a DBA may
    // legitimately name a session anything sys.server_event_sessions accepts, and a whitelist
    // would reject working names while still needing this code for the names it allows.

    /// <summary>
    /// QUOTENAME-equivalent bracket quoting for a T-SQL identifier: every interior <c>]</c> is
    /// doubled, then the whole name is wrapped in brackets. The result is always exactly one
    /// identifier, so no name — however hostile — can close the brackets early and append a
    /// statement. Null or empty yields <c>[]</c>, which SQL Server rejects as an invalid name;
    /// that is the honest outcome for a blank session name and never a silent no-op.
    /// </summary>
    internal static string BracketQuote(string? identifier) =>
        "[" + (identifier ?? string.Empty).Replace("]", "]]") + "]";

    /// <summary>
    /// Escapes a value for use INSIDE a T-SQL single-quoted string literal by doubling every
    /// interior <c>'</c>. Used for the .xel filename, which carries the session name as literal
    /// text rather than as an identifier — a different quoting context, and a <c>]</c> is
    /// harmless there while a <c>'</c> is not. The caller supplies the surrounding quotes.
    /// </summary>
    internal static string QuoteLiteral(string? value) =>
        (value ?? string.Empty).Replace("'", "''");

    /// <summary>
    /// Builds the CREATE EVENT SESSION statement issued by <see cref="CreateSessionAsync"/>.
    ///
    /// ⚠ THE PREDICATE BELONGS INSIDE THE EVENT'S OWN PARENTHESES. T-SQL accepts
    /// <c>ADD EVENT sqlserver.error_reported(WHERE severity &gt;= 17)</c> and rejects a bare
    /// <c>WHERE</c> line placed between ADD EVENT and ADD TARGET. Until 2026-08-25 this method's
    /// body emitted the bare form, so every deploy button that passed a predicate failed at parse
    /// time with "Msg 156, Incorrect syntax near the keyword 'WHERE'" and created nothing. PROVED
    /// live against MSI\NEW2022 on 2026-08-25: the old shape returned Msg 156/102/319 and left
    /// <c>sys.server_event_sessions</c> with zero matching rows; the shape below created and
    /// started both sessions. The predicate-free button (Deadlock) always worked, which is why the
    /// defect survived: two of the three buttons were broken and the third was the one people tried.
    ///
    /// Extracted from <see cref="CreateSessionAsync"/> so the emitted text is assertable without a
    /// SQL Server. A test that can only reach this SQL through an open connection is a test nobody
    /// runs in CI, and that is how the bare WHERE shipped.
    ///
    /// <para><b>ESCAPED SINCE 2026-09-01 (ruling F-D).</b> <paramref name="sessionName"/> now
    /// goes through <see cref="BracketQuote"/> in identifier position and
    /// <see cref="QuoteLiteral"/> inside the .xel filename literal, so a hostile name stays one
    /// identifier and one literal. Before that it was interpolated raw into both, and it is NOT a
    /// hardcoded literal on every path: the Alerts deploy buttons pass constants, but XEvents.razor
    /// passes <c>NewSessionName</c>, a free-text input the operator types. PROVED live on
    /// MSI\NEW2022 on 2026-08-25 by typing a name containing <c>]</c>: this method emitted a
    /// three-statement batch, the injected SELECT ran, and one CreateSessionAsync call created TWO
    /// event sessions. It was NOT privilege escalation and NOT reachable in a community build —
    /// the caller is gated on <c>run_scripts</c>, the permission that already grants arbitrary
    /// T-SQL, and buildprofile.targets Content-Removes Pages/XEvents.razor from community — which
    /// is why it was a bonus find rather than the lane's headline. An earlier lane report claimed
    /// every caller passed a literal, which was false. The escaping is pinned by
    /// XEventIdentifierEscapingTests, which asserts the emitted text for all five lifecycle
    /// statements and keeps a control that flags the pre-fix shape.</para>
    /// </summary>
    internal static string BuildCreateSessionSql(string sessionName, string eventName, string predicate = "")
    {
        var eventClause = string.IsNullOrWhiteSpace(predicate)
            ? $"sqlserver.{eventName}"
            : $"sqlserver.{eventName}(WHERE {predicate})";

        return $@"
                CREATE EVENT SESSION {BracketQuote(sessionName)} ON SERVER
                ADD EVENT {eventClause}
                ADD TARGET package0.event_file(SET filename = '{QuoteLiteral(sessionName)}.xel', max_file_size = 10, max_rollover_files = 5)
                WITH (MAX_DISPATCH_LATENCY = 1 SECONDS, STARTUP_STATE = OFF)";
    }

    /// <summary>
    /// Create a new XEvent session with basic template
    /// </summary>
    public async Task<string> CreateSessionAsync(string connectionString, string sessionName, string eventName, string predicate = "")
    {
        // Statement built and journaled BEFORE the connection is opened. Building first is what
        // lets the attempt record carry the exact text; journaling first is what makes "no
        // statement executes unjournaled" true rather than aspirational.
        var sql = BuildCreateSessionSql(sessionName, eventName, predicate);
        var server = ServerFor(connectionString);
        _journal?.RecordAttempt(Surface, "create", server, sql, $"SessionName={sessionName}; Event={eventName}");

        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            using var cmd = new SqlCommand(sql, connection);
            cmd.CommandTimeout = 30;
            await cmd.ExecuteNonQueryAsync();

            await RecordOutcomeAsync("create", server, sql, AuditLogService.DdlOutcomes.Succeeded);
            _logger.LogInformation("Created XEvent session: {SessionName}", sessionName);
            return $"Session '{sessionName}' created successfully";
        }
        catch (Exception ex)
        {
            await RecordOutcomeAsync("create", server, sql, AuditLogService.DdlOutcomes.Failed, ex.Message);
            _logger.LogError(ex, "Error creating XEvent session {SessionName}", sessionName);
            return $"Error: {ex.Message}";
        }
    }


    /// <summary>
    /// Builds the statement issued by <see cref="StartSessionAsync"/>. Extracted for the same
    /// reason <see cref="BuildCreateSessionSql"/> was: SQL only reachable through an open
    /// connection is SQL no CI run ever reads, and that is the condition under which both the
    /// bare WHERE and the unescaped identifier shipped.
    /// </summary>
    internal static string BuildStartSessionSql(string sessionName) =>
        $"ALTER EVENT SESSION {BracketQuote(sessionName)} ON SERVER STATE = START";

    /// <summary>Builds the statement issued by <see cref="StopSessionAsync"/>.</summary>
    internal static string BuildStopSessionSql(string sessionName) =>
        $"ALTER EVENT SESSION {BracketQuote(sessionName)} ON SERVER STATE = STOP";

    /// <summary>Builds the statement issued by <see cref="DropSessionAsync"/>.</summary>
    internal static string BuildDropSessionSql(string sessionName) =>
        $"DROP EVENT SESSION {BracketQuote(sessionName)} ON SERVER";

    /// <summary>
    /// Builds the statement issued by <see cref="SetStartupStateAsync"/>. The state is derived
    /// from a bool here, never interpolated from caller text, so ON/OFF is the only thing that
    /// can land in that position.
    /// </summary>
    internal static string BuildStartupStateSql(string sessionName, bool startupOn) =>
        $"ALTER EVENT SESSION {BracketQuote(sessionName)} ON SERVER WITH (STARTUP_STATE = {(startupOn ? "ON" : "OFF")})";

    /// <summary>
    /// Start an XEvent session
    /// </summary>
    public async Task<string> StartSessionAsync(string connectionString, string sessionName)
    {
        var sql = BuildStartSessionSql(sessionName);
        var server = ServerFor(connectionString);
        _journal?.RecordAttempt(Surface, "start", server, sql, $"SessionName={sessionName}");

        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            using var cmd = new SqlCommand(sql, connection);
            cmd.CommandTimeout = 30;
            await cmd.ExecuteNonQueryAsync();

            await RecordOutcomeAsync("start", server, sql, AuditLogService.DdlOutcomes.Succeeded);
            return $"Session '{sessionName}' started";
        }
        catch (Exception ex)
        {
            await RecordOutcomeAsync("start", server, sql, AuditLogService.DdlOutcomes.Failed, ex.Message);
            _logger.LogError(ex, "Error starting XEvent session {SessionName}", sessionName);
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Stop an XEvent session
    /// </summary>
    public async Task<string> StopSessionAsync(string connectionString, string sessionName)
    {
        var sql = BuildStopSessionSql(sessionName);
        var server = ServerFor(connectionString);
        _journal?.RecordAttempt(Surface, "stop", server, sql, $"SessionName={sessionName}");

        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            using var cmd = new SqlCommand(sql, connection);
            cmd.CommandTimeout = 30;
            await cmd.ExecuteNonQueryAsync();

            await RecordOutcomeAsync("stop", server, sql, AuditLogService.DdlOutcomes.Succeeded);
            return $"Session '{sessionName}' stopped";
        }
        catch (Exception ex)
        {
            await RecordOutcomeAsync("stop", server, sql, AuditLogService.DdlOutcomes.Failed, ex.Message);
            _logger.LogError(ex, "Error stopping XEvent session {SessionName}", sessionName);
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Drop an XEvent session
    /// </summary>
    public async Task<string> DropSessionAsync(string connectionString, string sessionName)
    {
        var sql = BuildDropSessionSql(sessionName);
        var server = ServerFor(connectionString);
        _journal?.RecordAttempt(Surface, "drop", server, sql, $"SessionName={sessionName}");

        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            using var cmd = new SqlCommand(sql, connection);
            cmd.CommandTimeout = 30;
            await cmd.ExecuteNonQueryAsync();

            await RecordOutcomeAsync("drop", server, sql, AuditLogService.DdlOutcomes.Succeeded);
            return $"Session '{sessionName}' dropped";
        }
        catch (Exception ex)
        {
            await RecordOutcomeAsync("drop", server, sql, AuditLogService.DdlOutcomes.Failed, ex.Message);
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

            // Same free-text session name, same escaping chokepoint. This is a READ, not DDL, so
            // it sits outside the five lifecycle statements the F-D ruling names — but it is the
            // same identifier reaching a string literal, and leaving one raw interpolation behind
            // would make "escaped at the chokepoint" false.
            var xelName = QuoteLiteral(sessionName);
            var sql = $@"
                SELECT
                    CAST(e.event_data AS NVARCHAR(MAX)) AS EventData,
                    e.timestamp_utc AS TimestampUTC
                FROM sys.fn_xe_file_target_read_file('{xelName}*.xel', '{xelName}*.xem', NULL, NULL) e
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
                    Name = reader.GetString(0),
                    StartupState = reader.GetBoolean(1),
                    CreateTime = reader.IsDBNull(2) ? DateTime.MinValue : reader.GetDateTime(2),
                    IsRunning = reader.GetInt32(3) == 1,
                    TotalEventsEmitted = reader.GetInt64(4),
                    BufferSizeBytes = reader.GetInt64(5)
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
        // T-SQL has no parameter form for an identifier, so the name is bracket-quoted at the
        // shared chokepoint instead. This path already doubled its ']' inline before 2026-09-01;
        // it now shares the helper the other four lifecycle statements use.
        var state = startupOn ? "ON" : "OFF";
        var sql = BuildStartupStateSql(sessionName, startupOn);
        var server = ServerFor(connectionString);
        // A startup-state flip persists across a service restart, so it is the one lifecycle change
        // an operator is most likely to inherit without knowing who made it. Journaled like the rest.
        _journal?.RecordAttempt(Surface, "startup-state", server, sql,
            $"SessionName={sessionName}; StartupState={state}");

        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            using var cmd = new SqlCommand(sql, connection);
            cmd.CommandTimeout = 30;
            await cmd.ExecuteNonQueryAsync();
            await RecordOutcomeAsync("startup-state", server, sql, AuditLogService.DdlOutcomes.Succeeded);
            _logger.LogInformation("Set startup state {State} for XEvent session {Name}", state, sessionName);
            return $"Startup state set to {state} for '{sessionName}'";
        }
        catch (Exception ex)
        {
            await RecordOutcomeAsync("startup-state", server, sql, AuditLogService.DdlOutcomes.Failed, ex.Message);
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
