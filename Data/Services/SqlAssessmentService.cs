/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Data.SqlClient;
using SqlHealthAssessment.Data;
using SqlHealthAssessment.Data.Services.Assessment;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.Management.Assessment;
using Microsoft.SqlServer.Management.Common;
using System.IO;
using System.Text.Json;

namespace SqlHealthAssessment.Data.Services;

/// <summary>
/// Service for running SQL Vulnerability Assessment using Microsoft SQL Assessment API
/// Uses AssessmentEngine with ruleset.json for full check coverage
/// Supports SQL, PowerShell, WMI, and Registry checks
/// </summary>
public class SqlAssessmentService
{
    private readonly ILogger<SqlAssessmentService> _logger;
    private readonly ServerConnectionManager _connectionManager;
    private readonly AzureBlobExportService? _blobExport;

    // Track total checks executed
    public int TotalChecksRun { get; private set; }
    public int TotalChecksPassed { get; private set; }
    public int TotalChecksFailed { get; private set; }

    private readonly List<AssessmentCheckDefinition> _checkDefinitions;
    private readonly string _rulesetPath;
    private Dictionary<string, AssessmentCheckDefinition> _checkDefById = new(StringComparer.OrdinalIgnoreCase);

    public SqlAssessmentService(ILogger<SqlAssessmentService> logger, ServerConnectionManager connectionManager, AzureBlobExportService? blobExport = null)
    {
        _logger = logger;
        _connectionManager = connectionManager;
        _blobExport = blobExport;

        // Find the ruleset.json path
        _rulesetPath = FindRulesetPath();

        // Load check definitions from ruleset.json or fallback
        _checkDefinitions = LoadCheckDefinitions();
        _checkDefById = _checkDefinitions
            .Where(d => !string.IsNullOrEmpty(d.CheckId))
            .GroupBy(d => d.CheckId)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation("Initialized with {Count} check definitions from {Path}",
            _checkDefinitions.Count, _rulesetPath);
    }

    private string FindRulesetPath()
    {
        // Try multiple locations - include project root and output directories
        var baseDirs = new[]
        {
            AppDomain.CurrentDomain.BaseDirectory,
            Directory.GetCurrentDirectory(),
            Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..")),
            Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".."))
        };

        var fileNames = new[] { "config/ruleset.json", "Config/ruleset.json", "ruleset.json" };

        foreach (var baseDir in baseDirs)
        {
            foreach (var fileName in fileNames)
            {
                var path = Path.Combine(baseDir, fileName);
                _logger.LogDebug("Checking for ruleset at: {Path}", path);
                if (File.Exists(path))
                {
                    _logger.LogInformation("Found ruleset at: {Path}", path);
                    return path;
                }
            }
        }

        var defaultPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", "ruleset.json");
        _logger.LogWarning("Ruleset not found in any location, using default: {Path}", defaultPath);
        return defaultPath;
    }

    private List<AssessmentCheckDefinition> LoadCheckDefinitions()
    {
        var checks = new List<AssessmentCheckDefinition>();

        try
        {
            if (File.Exists(_rulesetPath))
            {
                _logger.LogInformation("Loading ruleset from: {Path}", _rulesetPath);

                var json = File.ReadAllText(_rulesetPath);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Build probe lookup: name -> first implementation with executable content
                var probeMap = new Dictionary<string, ImplementationDefinition>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("probes", out var probesEl))
                {
                    foreach (var probeProp in probesEl.EnumerateObject())
                    {
                        foreach (var probeItem in probeProp.Value.EnumerateArray())
                        {
                            if (!probeItem.TryGetProperty("implementation", out var implEl)) continue;
                            var impl = new ImplementationDefinition
                            {
                                Query = implEl.TryGetProperty("query", out var q) ? GetStringOrJoinArray(q) : null,
                                Sql = implEl.TryGetProperty("sql", out var s) ? GetStringOrJoinArray(s) : null,
                                PowerShell = implEl.TryGetProperty("powerShell", out var ps) ? GetStringOrJoinArray(ps) : null,
                                Wmi = implEl.TryGetProperty("wmi", out var w) ? GetStringOrJoinArray(w) : null,
                                Registry = implEl.TryGetProperty("registry", out var r) ? GetStringOrJoinArray(r) : null,
                            };
                            if (impl.Query != null || impl.Sql != null || impl.PowerShell != null ||
                                impl.Wmi != null || impl.Registry != null)
                            {
                                probeMap.TryAdd(probeProp.Name, impl);
                                break; // first executable implementation wins
                            }
                        }
                    }
                }

                if (!root.TryGetProperty("rules", out var rulesEl))
                {
                    _logger.LogWarning("No 'rules' array found in ruleset.json");
                }
                else
                {
                    int ruleCount = 0;
                    foreach (var ruleEl in rulesEl.EnumerateArray())
                    {
                        // Only process "definition" items (skip "override", "probe", etc.)
                        var itemType = ruleEl.TryGetProperty("itemType", out var itEl) ? itEl.GetString() ?? "" : "";
                        if (!itemType.Equals("definition", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrEmpty(itemType))
                            continue;
                        ruleCount++;

                        // id can be a string or an array — take first string value
                        string checkId = "";
                        if (ruleEl.TryGetProperty("id", out var idEl))
                            checkId = idEl.ValueKind == System.Text.Json.JsonValueKind.Array
                                ? (idEl.EnumerateArray().FirstOrDefault().GetString() ?? "")
                                : idEl.GetString() ?? "";

                        var displayName = ruleEl.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";
                        var description = ruleEl.TryGetProperty("description", out var ds) ? ds.GetString() ?? "" : "";
                        var helpLink = ruleEl.TryGetProperty("helpLink", out var hl) ? hl.GetString() ?? "" : "";
                        var level = ruleEl.TryGetProperty("level", out var lv) ? lv.GetString() : null;
                        var targetType = ruleEl.TryGetProperty("target", out var tg) &&
                                          tg.TryGetProperty("type", out var tt) ? tt.GetString() ?? "Server" : "Server";

                        var tags = new List<string>();
                        if (ruleEl.TryGetProperty("tags", out var tagsEl))
                            foreach (var t in tagsEl.EnumerateArray())
                                if (t.GetString() is string ts) tags.Add(ts);

                        var checkDef = new AssessmentCheckDefinition
                        {
                            CheckId = string.IsNullOrEmpty(checkId) ? displayName : checkId,
                            DisplayName = displayName,
                            Description = description,
                            Category = GetCategoryFromTags(tags),
                            Severity = MapSeverityFromLevel(level) ?? MapSeverityFromString(tags),
                            HelpLink = helpLink,
                            TargetType = targetType,
                            ImplementationType = "Info"
                        };

                        // Walk probes to find first executable implementation
                        if (ruleEl.TryGetProperty("probes", out var probeListEl))
                        {
                            foreach (var probeEl in probeListEl.EnumerateArray())
                            {
                                // Each probe entry is either a string name or an object with "id"
                                string probeName = probeEl.ValueKind == System.Text.Json.JsonValueKind.String
                                    ? probeEl.GetString() ?? ""
                                    : probeEl.TryGetProperty("id", out var pid) ? pid.GetString() ?? "" : "";

                                if (!string.IsNullOrEmpty(probeName) && probeMap.TryGetValue(probeName, out var impl))
                                {
                                    var sql = impl.Sql ?? impl.Query;
                                    if (sql != null) { checkDef.Sql = sql; checkDef.ImplementationType = "Sql"; break; }
                                    if (impl.PowerShell != null) { checkDef.PowerShell = impl.PowerShell; checkDef.ImplementationType = "PowerShell"; break; }
                                    if (impl.Wmi != null) { checkDef.Wmi = impl.Wmi; checkDef.ImplementationType = "Wmi"; break; }
                                    if (impl.Registry != null) { checkDef.Registry = impl.Registry; checkDef.ImplementationType = "Registry"; break; }
                                }
                            }
                        }

                        checks.Add(checkDef);
                    }

                    _logger.LogInformation("Found {RuleCount} definition rules in ruleset.json", ruleCount);
                }

                var sqlCount = checks.Count(c => c.ImplementationType == "Sql");
                var psCount = checks.Count(c => c.ImplementationType == "PowerShell");
                var wmiCount = checks.Count(c => c.ImplementationType == "Wmi");
                var regCount = checks.Count(c => c.ImplementationType == "Registry");

                _logger.LogWarning("RULESET PARSING: Loaded {TotalChecks} checks (SQL: {SqlCount}, PS: {PsCount}, WMI: {WmiCount}, Reg: {RegCount})",
                    checks.Count, sqlCount, psCount, wmiCount, regCount);

                _logger.LogInformation("Loaded {Count} check definitions from ruleset.json (SQL: {SqlCount}, PowerShell: {PsCount}, WMI: {WmiCount}, Registry: {RegCount})",
                    checks.Count, sqlCount, psCount, wmiCount, regCount);
            }
            else
            {
                _logger.LogWarning("Ruleset file not found at {Path}, using fallback checks", _rulesetPath);
                checks = GetComprehensiveChecks();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading check definitions, using fallback");
            checks = GetComprehensiveChecks();
        }

        return checks;
    }

    private static string? GetStringOrJoinArray(System.Text.Json.JsonElement el) =>
        el.ValueKind == System.Text.Json.JsonValueKind.Array
            ? string.Join("\n", el.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0))
            : el.ValueKind == System.Text.Json.JsonValueKind.String ? el.GetString() : null;

    private static string? MapSeverityFromLevel(string? level) => level?.ToLowerInvariant() switch
    {
        "critical" or "high" => "Error",
        "medium" or "warning" => "Warning",
        "low" or "information" or "info" => "Information",
        _ => null
    };

    private static string GetCategoryFromTags(List<string>? tags)
    {
        if (tags == null || tags.Count == 0) return "General";
        // Prefer known category tags; fall back to first tag
        var known = new[] { "Security", "Performance", "Availability", "Configuration", "BestPractices", "Information" };
        foreach (var k in known)
            if (tags.Any(t => t.Equals(k, StringComparison.OrdinalIgnoreCase))) return k;
        return tags[0];
    }

    private string MapSeverityFromString(List<string>? tags)
    {
        if (tags == null) return "Warning";

        foreach (var tag in tags)
        {
            var lowerTag = tag.ToLower();
            if (lowerTag.Contains("critical") || lowerTag.Contains("high") || lowerTag.Contains("error")) return "Error";
            if (lowerTag.Contains("medium") || lowerTag.Contains("warning")) return "Warning";
            if (lowerTag.Contains("low") || lowerTag.Contains("information") || lowerTag.Contains("info")) return "Information";
        }

        return "Warning";
    }

    private List<AssessmentCheckDefinition> GetComprehensiveChecks()
    {
        return new List<AssessmentCheckDefinition>
        {
            // Security checks
            new() { CheckId = "SEC001", DisplayName = "Mixed Mode Authentication", Category = "Security", Severity = "Error", Description = "SQL Server running with weak authentication", Sql = @"SELECT 'Mixed Mode Authentication is enabled' AS Message, @@SERVERNAME AS TargetName WHERE SERVERPROPERTY('IsMixedModeAuthentication') = 1" },
            new() { CheckId = "SEC002", DisplayName = "Public Network Access", Category = "Security", Severity = "Warning", Description = "Server is accessible via IP address", Sql = @"SELECT 'Server is accessible via IP address' AS Message, @@SERVERNAME AS TargetName WHERE EXISTS (SELECT 1 FROM sys.dm_exec_connections WHERE client_net_address LIKE '%.%.%.%')" },
            new() { CheckId = "SEC003", DisplayName = "Guest User Access", Category = "Security", Severity = "Warning", Description = "Guest user exists in user databases", Sql = @"SELECT DISTINCT 'Guest user exists in ' + d.name AS Message, d.name AS TargetName FROM sys.databases d WHERE d.name NOT IN ('master','tempdb','model','msdb') AND HAS_DBACCESS(d.name) = 1" },
            new() { CheckId = "SEC004", DisplayName = "Service Account", Category = "Security", Severity = "Warning", Description = "SQL Server service running as local system", Sql = @"SELECT 'Service running as NT AUTHORITY\SYSTEM' AS Message, @@SERVERNAME AS TargetName WHERE SERVICE_NAME() LIKE '%SYSTEM%'" },
            new() { CheckId = "SEC005", DisplayName = "Orphaned Users", Category = "Security", Severity = "Warning", Description = "Orphaned users exist", Sql = @"SELECT 'Orphaned user: ' + dp.name AS Message, dp.name AS TargetName FROM sys.database_principals dp LEFT JOIN sys.server_principals sp ON dp.sid = sp.sid WHERE dp.type IN ('S','U') AND sp.sid IS NULL" },
            
            // Configuration checks
            new() { CheckId = "CFG001", DisplayName = "MAXDOP Setting", Category = "Configuration", Severity = "Warning", Description = "MAXDOP not set optimally", Sql = @"SELECT 'MAXDOP may not be optimal' AS Message, CAST(value_in_use AS VARCHAR) AS TargetName FROM sys.configurations WHERE name = 'max degree of parallelism' AND value_in_use > 8" },
            new() { CheckId = "CFG002", DisplayName = "Memory Configuration", Category = "Configuration", Severity = "Warning", Description = "Max server memory not configured", Sql = @"SELECT 'Max server memory not configured' AS Message, @@SERVERNAME AS TargetName FROM sys.configurations WHERE name = 'max server memory (mb)' AND value_in_use = 2147483647" },
            new() { CheckId = "CFG003", DisplayName = "SQL Server Version", Category = "Configuration", Severity = "Information", Description = "SQL Server version outdated", Sql = @"SELECT 'SQL Server version: ' + CAST(SERVERPROPERTY('ProductVersion') AS VARCHAR) AS Message, @@SERVERNAME AS TargetName WHERE CAST(SERVERPROPERTY('ProductVersion') AS VARCHAR) < '15.0.2000'" },
            new() { CheckId = "CFG004", DisplayName = "Target Recovery Interval", Category = "Configuration", Severity = "Warning", Description = "Target recovery interval not set", Sql = @"SELECT 'Target recovery interval may be too high' AS Message, value_in_use AS TargetName FROM sys.configurations WHERE name = 'target recovery interval (seconds)' AND value_in_use > 300" },
            new() { CheckId = "CFG005", DisplayName = "SQL Server Agent", Category = "Configuration", Severity = "Warning", Description = "SQL Server Agent not running", Sql = @"SELECT 'SQL Server Agent is not running' AS Message, @@SERVERNAME AS TargetName WHERE (SELECT COUNT(*) FROM sys.dm_server_services WHERE servicename LIKE '%Agent%' AND status_desc != 'Running') > 0" },
            
            // Performance checks
            new() { CheckId = "PERF001", DisplayName = "Auto-Close", Category = "Performance", Severity = "Warning", Description = "Auto-close enabled", Sql = @"SELECT 'Auto-close is enabled' AS Message, d.name AS TargetName FROM sys.databases d WHERE d.is_auto_close_on = 1 AND d.name NOT IN ('tempdb')" },
            new() { CheckId = "PERF002", DisplayName = "Auto-Shrink", Category = "Performance", Severity = "Warning", Description = "Auto-shrink enabled", Sql = @"SELECT 'Auto-shrink is enabled' AS Message, d.name AS TargetName FROM sys.databases d WHERE d.is_auto_shrink_on = 1" },
            new() { CheckId = "PERF003", DisplayName = "VLF Count", Category = "Performance", Severity = "Information", Description = "High VLF count", Sql = @"SELECT 'High VLF count: ' + CAST(fc.Count AS VARCHAR) AS Message, d.name AS TargetName FROM sys.databases d CROSS APPLY (SELECT COUNT(*) AS Count FROM sys.dm_db_log_info(d.database_id)) fc WHERE fc.Count > 50" },
            new() { CheckId = "PERF004", DisplayName = "Index Fragmentation", Category = "Performance", Severity = "Warning", Description = "High index fragmentation", Sql = @"SELECT 'High index fragmentation on: ' + OBJECT_NAME(ips.object_id) AS Message, OBJECT_NAME(ips.object_id) AS TargetName FROM sys.dm_db_index_physical_stats(NULL, NULL, NULL, NULL, 'DETAILED') ips WHERE ips.avg_fragmentation_in_percent > 40 AND ips.page_count > 1000 AND ips.index_id > 0" },
            new() { CheckId = "PERF005", DisplayName = "Statistics", Category = "Performance", Severity = "Information", Description = "Statistics not updated recently", Sql = @"SELECT 'Statistics outdated on: ' + OBJECT_NAME(s.object_id) AS Message, OBJECT_NAME(s.object_id) AS TargetName FROM sys.objects s CROSS APPLY sys.dm_db_stats_properties(s.object_id, s.index_id) sp WHERE s.type = 'U' AND sp.last_updated < DATEADD(day, -30, GETDATE())" },
            new() { CheckId = "PERF006", DisplayName = "Missing Indexes", Category = "Performance", Severity = "Information", Description = "Missing indexes detected", Sql = @"SELECT 'Missing index on ' + OBJECT_NAME(mid.object_id) + ' - ' + mid.statement AS Message, OBJECT_NAME(mid.object_id) AS TargetName FROM sys.dm_db_missing_index_details mid WHERE mid.object_id > 0" },
            
            // Availability checks
            new() { CheckId = "AVAIL001", DisplayName = "Backup Status", Category = "Availability", Severity = "Warning", Description = "No database backups in last 7 days", Sql = @"SELECT 'No backup for: ' + d.name AS Message, d.name AS TargetName FROM sys.databases d WHERE d.state = 0 AND d.name NOT IN ('tempdb') AND NOT EXISTS (SELECT 1 FROM msdb.dbo.backupset WHERE database_name = d.name AND backup_start_date > DATEADD(day, -7, GETDATE()))" },
            new() { CheckId = "AVAIL002", DisplayName = "Recovery Model", Category = "Availability", Severity = "Information", Description = "Database not in FULL recovery model", Sql = @"SELECT 'Simple recovery model: ' + d.name AS Message, d.name AS TargetName FROM sys.databases d WHERE d.recovery_model = 3 AND d.name NOT IN ('tempdb','model')" },
            new() { CheckId = "AVAIL003", DisplayName = "Database Integrity", Category = "Availability", Severity = "Warning", Description = "Possible database corruption", Sql = @"SELECT 'Database integrity warning: ' + d.name AS Message, d.name AS TargetName FROM sys.databases d WHERE d.state != 0" },
            
            // Best Practice checks
            new() { CheckId = "BP001", DisplayName = "Heap Tables", Category = "Best Practice", Severity = "Warning", Description = "Heaps without nonclustered indexes", Sql = @"SELECT 'Heap table without nonclustered index: ' + t.name AS Message, t.name AS TargetName FROM sys.tables t INNER JOIN sys.indexes i ON t.object_id = i.object_id AND i.type = 0 WHERE t.is_ms_shipped = 0" },
            new() { CheckId = "BP002", DisplayName = "Tables Without Clustered Index", Category = "Best Practice", Severity = "Information", Description = "Tables without clustered index", Sql = @"SELECT 'Table without clustered index: ' + t.name AS Message, t.name AS TargetName FROM sys.tables t WHERE t.is_ms_shipped = 0 AND t.object_id NOT IN (SELECT object_id FROM sys.indexes WHERE type = 1)" },
            new() { CheckId = "BP003", DisplayName = "Foreign Key Indexes", Category = "Best Practice", Severity = "Warning", Description = "Foreign keys without indexes", Sql = @"SELECT 'FK without covering index: ' + fk.name AS Message, fk.name AS TargetName FROM sys.foreign_keys fk WHERE NOT EXISTS (SELECT 1 FROM sys.index_columns ic WHERE ic.object_id = fk.parent_object_id AND ic.key_ordinal > 0)" }
        };
    }

    /// <summary>
    /// Run SQL Assessment using the Microsoft SQL Assessment Engine.
    /// Falls back to custom checks if the Engine is unavailable.
    /// </summary>
    public async Task<AssessmentSummary> RunServerAssessmentAsync(string connectionString)
    {
        var summary = new AssessmentSummary();

        try
        {
            TotalChecksRun = 0;
            TotalChecksPassed = 0;
            TotalChecksFailed = 0;

            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            var serverName = await GetServerNameAsync(connection);
            var serverInfo = await GetServerInfoAsync(connection);

            _logger.LogInformation("Running SQL Assessment Engine against {Server}...", LogAnon.S(serverName));

            // ── Primary path: use the Microsoft SQL Assessment Engine ────────
            bool engineSucceeded = false;
            try
            {
                var serverConn = new Microsoft.SqlServer.Management.Common.ServerConnection(connection);
                var smoServer = new Microsoft.SqlServer.Management.Smo.Server(serverConn);

                // GetAssessmentResults is an extension method on SqlSmoObject.
                // Run server-level checks first, then database-level checks in parallel batches.
                // Each parallel task gets its own SqlConnection since SMO objects are not thread-safe.
                const int dbBatchSize = 4;

                // Server-level assessment + enumerate database names (sequential, shared connection)
                var (serverResults, dbNames) = await Task.Run(() =>
                {
                    var sr = smoServer.GetAssessmentResults().ToList();
                    var names = new List<string>();
                    foreach (Microsoft.SqlServer.Management.Smo.Database db in smoServer.Databases)
                    {
                        if (!db.IsSystemObject && db.Status == Microsoft.SqlServer.Management.Smo.DatabaseStatus.Normal)
                            names.Add(db.Name);
                    }
                    return (sr, names);
                });

                _logger.LogInformation("Running database assessments for {Count} databases (parallel batch={Batch})", dbNames.Count, dbBatchSize);

                // Parallel database assessments — each task owns its own connection
                var semaphore = new SemaphoreSlim(dbBatchSize);
                var dbTasks = dbNames.Select(async dbName =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        return await Task.Run(() =>
                        {
                            using var dbConn = new SqlConnection(connectionString);
                            dbConn.Open();
                            var dbSvrConn = new Microsoft.SqlServer.Management.Common.ServerConnection(dbConn);
                            var dbSmo = new Microsoft.SqlServer.Management.Smo.Server(dbSvrConn);
                            var database = dbSmo.Databases[dbName];
                            if (database == null) return (dbName, Enumerable.Empty<IAssessmentResult>().ToList());
                            return (dbName, database.GetAssessmentResults().ToList());
                        });
                    }
                    catch (Exception dbEx)
                    {
                        _logger.LogDebug("DB assessment skipped for {Db}: {Msg}", dbName, dbEx.Message);
                        return (dbName, new List<IAssessmentResult>());
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                var dbResultSets = await Task.WhenAll(dbTasks);

                var engineResults = serverResults;
                foreach (var (_, dbResults) in dbResultSets)
                    engineResults.AddRange(dbResults);
                engineSucceeded = true;

                _logger.LogInformation("Assessment Engine returned {Count} results (server + databases)", engineResults.Count);

                foreach (var r in engineResults)
                {
                    TotalChecksRun++;
                    // r.Check is IAssessmentItem — use reflection to get Id since the interface isn't directly typed
                    var check = r.Check;
                    var checkId = check.GetType().GetProperty("Id")?.GetValue(check)?.ToString()
                                   ?? check.ToString() ?? "";
                    var displayName = check.GetType().GetProperty("DisplayName")?.GetValue(check)?.ToString()
                                   ?? check.GetType().GetProperty("Name")?.GetValue(check)?.ToString()
                                   ?? checkId;
                    var description = check.GetType().GetProperty("Description")?.GetValue(check)?.ToString() ?? "";
                    var remediation = check.GetType().GetProperty("Remediation")?.GetValue(check)?.ToString()
                                    ?? check.GetType().GetProperty("FixScript")?.GetValue(check)?.ToString()
                                    ?? check.GetType().GetProperty("RemediationScript")?.GetValue(check)?.ToString()
                                    ?? "";
                    var tags = check.GetType().GetProperty("Tags")?.GetValue(check);
                    var category = tags is System.Collections.IEnumerable tagEnum
                                        ? string.Join(", ", tagEnum.Cast<object>().Take(3).Select(t => t.ToString()))
                                        : "";

                    var rType = r.GetType();
                    // "Kind" is the IAssessmentResult interface property (AssessmentResultType enum).
                    // "Severity" may also exist on the concrete type. Use Enum.GetName to get the
                    // named value rather than a numeric string if it hasn't been resolved yet.
                    var severityVal = rType.GetProperty("Kind")?.GetValue(r)
                                  ?? rType.GetProperty("Severity")?.GetValue(r);
                    var severityStr = severityVal == null ? "Information"
                                    : severityVal.GetType().IsEnum
                                        ? (Enum.GetName(severityVal.GetType(), severityVal) ?? severityVal.ToString() ?? "Information")
                                        : (severityVal.ToString() ?? "Information");
                    var helpLink = rType.GetProperty("HelpLink")?.GetValue(r)?.ToString() ?? "";
                    bool passed = severityStr is "Ok" or "Pass" or "Note" or "Information";

                    _logger.LogDebug("Result [{CheckId}] raw severity type={Type} value={Value} → mapped={Mapped}",
                        checkId, severityVal?.GetType()?.Name ?? "null", severityStr, MapEngineSeverity(severityStr));

                    var foundInRuleset = _checkDefById.TryGetValue(checkId, out var defForResult);
                    if (!foundInRuleset)
                        _logger.LogDebug("Engine check not in ruleset.json: [{CheckId}] \"{DisplayName}\" tags={Category}",
                            checkId, displayName, category);

                    var result = new AssessmentResult
                    {
                        CheckId = checkId,
                        DisplayName = displayName,
                        Message = r.Message,
                        Severity = MapEngineSeverity(severityStr),
                        TargetName = r.TargetPath,
                        TargetType = r.TargetType.ToString(),
                        Category = category,
                        Description = description,
                        Remediation = remediation,
                        HelpLink = helpLink,
                        Status = passed ? "Passed" : "Failed",
                        RawSeverity = severityStr,
                        SqlQuery = defForResult?.Sql ?? "",
                        ImplementationType = defForResult?.ImplementationType ?? ""
                    };

                    summary.Results.Add(result);
                    if (passed) TotalChecksPassed++; else TotalChecksFailed++;
                }

                // Log a summary of engine checks not covered by ruleset.json (visible in app-.log at Debug level)
                var noRulesetIds = summary.Results
                    .Where(res => string.IsNullOrEmpty(res.SqlQuery) && string.IsNullOrEmpty(res.ImplementationType))
                    .Select(res => res.CheckId)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToList();
                if (noRulesetIds.Count > 0)
                    _logger.LogInformation(
                        "{Count} engine check(s) have no ruleset.json entry (SqlQuery unavailable): {Ids}",
                        noRulesetIds.Count, string.Join(", ", noRulesetIds));
            }
            catch (Exception engineEx)
            {
                _logger.LogWarning(engineEx, "Assessment Engine failed, falling back to custom checks");
            }

            // ── Fallback path: custom SQL + PS + WMI + Registry checks ──────
            if (!engineSucceeded)
            {
                var localMachineName = Environment.MachineName;

                foreach (var check in _checkDefinitions)
                {
                    TotalChecksRun++;
                    try
                    {
                        CheckExecutionResult execResult;
                        switch (check.ImplementationType)
                        {
                            case "Sql":
                                execResult = await SqlCheckExecutor.RunAsync(connection, check, serverName, _logger);
                                break;
                            case "PowerShell":
                                execResult = await PowerShellCheckExecutor.RunAsync(check, localMachineName, _logger);
                                break;
                            case "Wmi":
                                execResult = await WmiCheckExecutor.RunAsync(check, localMachineName, _logger);
                                break;
                            case "Registry":
                                execResult = RegistryCheckExecutor.Run(check, localMachineName, _logger);
                                break;
                            case "Info":
                                AddInfoResult(check, serverName, summary);
                                continue;
                            default:
                                _logger.LogDebug("Unknown implementation type {Type} for {CheckId}", check.ImplementationType, check.CheckId);
                                continue;
                        }
                        if (!execResult.Skipped)
                        {
                            summary.Results.AddRange(execResult.Results);
                            if (execResult.Passed) TotalChecksPassed++;
                            else TotalChecksFailed++;
                        }
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        _logger.LogWarning(ex, "Permission denied: {CheckId}", check.CheckId);
                        AddPermissionDeniedResult(check, summary);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error running {CheckId}", check.CheckId);
                        AddErrorResult(check, ex.Message, summary);
                    }
                }
            }

            summary.TotalChecks = TotalChecksRun;
            summary.PassedChecks = TotalChecksPassed;
            summary.FailedChecks = TotalChecksFailed;

            // Supplement with DMV-based missing index audit
            await RunMissingIndexAuditAsync(connectionString, summary);

            // Stamp every result with the server identity gathered at the start
            foreach (var r in summary.Results)
            {
                r.ThisServer = serverInfo.ThisServer;
                r.ThisDomain = serverInfo.ThisDomain;
                r.IsSQLAzure = serverInfo.IsSQLAzure;
                r.IsSQLMI = serverInfo.IsSQLMI;
                r.UTCDateTime = serverInfo.UTCDateTime;
            }

            await SaveCsvToOutputFolderAsync(summary, serverName);

            _logger.LogInformation("Assessment complete. Total: {Total}, Passed: {Passed}, Failed: {Failed}",
                summary.TotalChecks, summary.PassedChecks, summary.FailedChecks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running SQL Vulnerability Assessment");
            throw;
        }

        return summary;
    }

    private static string MapEngineSeverity(string status) => status switch
    {
        // Microsoft SQL Assessment API risk levels
        "High" => "Error",
        "Medium" => "Warning",
        "Low" => "Information",
        "Information" => "Pass",
        // Legacy / fallback values
        "Critical" => "Error",
        "Error" => "Error",
        "Warning" => "Warning",
        "Ok" => "Pass",
        "Note" => "Information",
        _ => "Information"
    };

    /// <summary>
    /// Queries sys.dm_db_missing_index_details to surface missing indexes with CREATE INDEX DDL.
    /// Called as a supplementary pass after the main Assessment Engine run.
    /// </summary>
    private async Task RunMissingIndexAuditAsync(string connectionString, AssessmentSummary summary)
    {
        try
        {
            const string sql = @"
SELECT
    d.database_id,
    DB_NAME(d.database_id)               AS DatabaseName,
    d.object_id,
    OBJECT_NAME(d.object_id, d.database_id) AS TableName,
    d.equality_columns,
    d.inequality_columns,
    d.included_columns,
    gs.avg_user_impact                   AS AvgImpact,
    gs.unique_compiles                   AS Compiles,
    gs.user_seeks + gs.user_scans        AS UsageCount
FROM sys.dm_db_missing_index_details  d
JOIN sys.dm_db_missing_index_groups   g  ON d.index_handle = g.index_handle
JOIN sys.dm_db_missing_index_group_stats gs ON g.index_group_handle = gs.group_handle
WHERE d.database_id > 4                 -- exclude system DBs
ORDER BY gs.avg_user_impact DESC;";

            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
            using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var db = reader["DatabaseName"]?.ToString() ?? "";
                var table = reader["TableName"]?.ToString() ?? db;
                var eq = reader["equality_columns"]?.ToString() ?? "";
                var ineq = reader["inequality_columns"]?.ToString() ?? "";
                var inc = reader["included_columns"]?.ToString() ?? "";
                var impact = Convert.ToDouble(reader["AvgImpact"]);
                var usage = Convert.ToInt64(reader["UsageCount"]);

                // Build CREATE INDEX DDL
                var keyColumns = string.Join(", ",
                    new[] { eq, ineq }.Where(s => !string.IsNullOrEmpty(s)));
                var includeClause = string.IsNullOrEmpty(inc) ? "" : $"\r\nINCLUDE ({inc})";
                var indexName = $"IX_Missing_{table}_{Guid.NewGuid().ToString("N")[..6]}";
                var ddl = $"CREATE NONCLUSTERED INDEX [{indexName}]\r\n"
                        + $"ON [{db}].[dbo].[{table}] ({keyColumns}){includeClause};";

                var result = new AssessmentResult
                {
                    CheckId = "MissingIndex",
                    DisplayName = $"Missing Index on {db}.{table}",
                    Message = $"Missing index on {db}.{table} (Impact: {impact:F4}%, Usage: {usage:N0})",
                    Severity = impact >= 80 ? "Error" : impact >= 40 ? "Warning" : "Information",
                    TargetName = $"Database[@Name='{db}']/Table[@Name='{table}']",
                    TargetType = "Table",
                    Category = "Performance, Indexes",
                    Description = $"SQL Server identified a missing index on {db}.{table} with average query impact {impact:F2}%. Equality: {eq}. Inequality: {ineq}. Includes: {inc}.",
                    Remediation = ddl,
                    Status = "Failed",
                    RawSeverity = impact >= 80 ? "High" : impact >= 40 ? "Medium" : "Low"
                };

                summary.Results.Add(result);
                TotalChecksRun++;
                TotalChecksFailed++;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Missing index audit skipped: {Msg}", ex.Message);
        }
    }


    private void AddInfoResult(AssessmentCheckDefinition check, string targetName, AssessmentSummary summary)
    {
        summary.Results.Add(new AssessmentResult
        {
            CheckId = check.CheckId,
            Message = "Assessed via Microsoft SQL Assessment API (composite rule)",
            Severity = "Information",
            TargetName = targetName,
            TargetType = check.TargetType,
            Category = check.Category,
            Description = check.Description,
            HelpLink = check.HelpLink,
            Status = "Passed"
        });
        TotalChecksPassed++;
    }

    private void AddSkippedResult(AssessmentCheckDefinition check, string reason, AssessmentSummary summary)
    {
        var skippedResult = new AssessmentResult
        {
            CheckId = check.CheckId,
            Message = $"Skipped: {reason}",
            Severity = "Information",
            TargetName = "N/A",
            TargetType = check.TargetType,
            Category = check.Category,
            Description = check.Description,
            HelpLink = check.HelpLink,
            Status = "Skipped"
        };

        summary.Results.Add(skippedResult);
    }

    private void AddPermissionDeniedResult(AssessmentCheckDefinition check, AssessmentSummary summary)
    {
        var errorResult = new AssessmentResult
        {
            CheckId = check.CheckId,
            Message = "Permission denied - try running as administrator",
            Severity = "Warning",
            TargetName = Environment.MachineName,
            TargetType = "LocalMachine",
            Category = check.Category,
            Description = check.Description,
            HelpLink = check.HelpLink,
            Status = "Error"
        };

        summary.Results.Add(errorResult);
        TotalChecksFailed++;
    }

    private void AddErrorResult(AssessmentCheckDefinition check, string errorMessage, AssessmentSummary summary)
    {
        var errorResult = new AssessmentResult
        {
            CheckId = check.CheckId,
            Message = $"Check error: {errorMessage}",
            Severity = "Information",
            TargetName = Environment.MachineName,
            TargetType = check.TargetType,
            Category = check.Category,
            Description = check.Description,
            HelpLink = check.HelpLink,
            Status = "Error"
        };

        summary.Results.Add(errorResult);
    }

    private async Task<string> GetServerNameAsync(SqlConnection connection)
    {
        try
        {
            using var cmd = new SqlCommand("SELECT @@SERVERNAME", connection);
            var result = await cmd.ExecuteScalarAsync();
            return result?.ToString() ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    private record ServerInfo(string? ThisServer, string? ThisDomain, bool IsSQLAzure, bool IsSQLMI, string UTCDateTime);

    private async Task<ServerInfo> GetServerInfoAsync(SqlConnection connection)
    {
        const string sql = @"
DECLARE @ThisDomain [NVARCHAR](100);
DECLARE @dynamicSQL NVARCHAR(4000);
DECLARE @IsSQLAzure BIT = 0;
DECLARE @IsSQLMI    BIT = 0;
IF (SELECT ServerProperty('EngineEdition')) = 5 SET @IsSQLAzure = 1;
IF (SELECT ServerProperty('EngineEdition')) = 8 SET @IsSQLMI    = 1;

DECLARE @ThisServer [NVARCHAR](500);
DECLARE @CharToCheck [NVARCHAR](5) = CHAR(92);

IF (SELECT CHARINDEX(@CharToCheck, @@SERVERNAME)) > 0
    SELECT @ThisServer = @@SERVERNAME;
IF (SELECT CHARINDEX(@CharToCheck, @@SERVERNAME)) = 0
    SELECT @ThisServer = CAST(SERVERPROPERTY('ComputerNamePhysicalNetBIOS') AS [NVARCHAR](500));
IF @IsSQLAzure = 1 OR @IsSQLMI = 1
    SELECT @ThisServer = REPLACE(@@SERVERNAME, '', '');

IF @IsSQLAzure = 0 OR @IsSQLMI = 0
BEGIN
    BEGIN TRY
        DECLARE @ThisDomainTable TABLE (ThisDomain [NVARCHAR](100));
        SET @dynamicSQL = N'
DECLARE @ThisDomain [NVARCHAR](100);
EXEC master.dbo.xp_regread ''HKEY_LOCAL_MACHINE'', ''SYSTEM\CurrentControlSet\services\Tcpip\Parameters'', N''Domain'', @ThisDomain OUTPUT;
SELECT @ThisDomain;';
        INSERT @ThisDomainTable EXEC sp_executesql @dynamicSQL;
        SELECT @ThisDomain = ThisDomain FROM @ThisDomainTable;
    END TRY
    BEGIN CATCH
        -- Silently ignore (Azure / permission denied)
    END CATCH
END

IF @IsSQLAzure = 1 OR @IsSQLMI = 1
    SELECT @ThisDomain = RIGHT(SYSTEM_USER, LEN(SYSTEM_USER) - CHARINDEX('@', SYSTEM_USER));

SET @ThisDomain = ISNULL(@ThisDomain, DEFAULT_DOMAIN());

SELECT @ThisDomain   [ThisDomain],
       @ThisServer   [ThisServer],
       @IsSQLAzure   [IsSQLAzure],
       @IsSQLMI      [IsSQLMI],
       CONVERT(VARCHAR, GETUTCDATE(), 120) [UTCDateTime];";

        try
        {
            using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 30 };
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new ServerInfo(
                    reader["ThisServer"]?.ToString(),
                    reader["ThisDomain"]?.ToString(),
                    Convert.ToBoolean(reader["IsSQLAzure"]),
                    Convert.ToBoolean(reader["IsSQLMI"]),
                    reader["UTCDateTime"]?.ToString() ?? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetServerInfoAsync failed — server identity will be omitted");
        }
        return new ServerInfo(null, null, false, false, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    /// <summary>
    /// Generate remediation SQL script for all failed assessments
    /// </summary>
    public string GenerateRemediationScript(AssessmentSummary summary)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("-- SQL Vulnerability Assessment - Remediation Script");
        sb.AppendLine($"-- Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"-- Total Checks: {summary.TotalChecks}");
        sb.AppendLine($"-- Passed: {summary.PassedChecks}, Failed: {summary.FailedChecks}");
        sb.AppendLine("-- ===============================================");
        sb.AppendLine();

        var failedResults = summary.Results.Where(r => r.Status == "Failed").ToList();

        var criticalResults = failedResults.Where(r => r.Severity == "Error").ToList();
        var warningResults = failedResults.Where(r => r.Severity == "Warning").ToList();
        var infoResults = failedResults.Where(r => r.Severity == "Information").ToList();

        void WriteSection(string header, List<AssessmentResult> items)
        {
            if (!items.Any()) return;
            sb.AppendLine();
            sb.AppendLine($"-- {header}");
            sb.AppendLine("-- ===============================================");
            foreach (var result in items)
            {
                sb.AppendLine($"-- Check  : {result.CheckId} — {result.DisplayName}");
                sb.AppendLine($"-- Target : {result.TargetName} ({result.TargetType})");
                sb.AppendLine($"-- Message: {result.Message}");
                if (!string.IsNullOrEmpty(result.Description))
                    sb.AppendLine($"-- Detail : {result.Description}");

                // Remediation SQL from the engine is rarely populated — fall back to the
                // advisory Message text, which is always the actionable recommendation.
                var remediationText = !string.IsNullOrWhiteSpace(result.Remediation)
                    ? result.Remediation.Trim()
                    : result.Message.Trim();

                sb.AppendLine($"-- Action : {remediationText}");
                if (!string.IsNullOrEmpty(result.HelpLink))
                    sb.AppendLine($"-- Ref    : {result.HelpLink}");

                sb.AppendLine();
            }
        }

        WriteSection("HIGH SEVERITY — REQUIRED FIXES", criticalResults);
        WriteSection("MEDIUM SEVERITY — RECOMMENDED FIXES", warningResults);
        WriteSection("LOW SEVERITY — INFORMATIONAL", infoResults);

        return sb.ToString();
    }

    /// <summary>
    /// Set to true to include the SqlQuery column in CSV exports.
    /// Internal flag — not exposed as a user setting.
    /// </summary>
    private const bool ExportSqlQuery = true;

    /// <summary>
    /// Export results to CSV
    /// </summary>
    public string ExportToCsv(AssessmentSummary summary)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine(ExportSqlQuery
            ? "CheckId,DisplayName,Message,Severity,RawSeverity,TargetName,TargetType,Category,Description,Remediation,HelpLink,Status,ImplementationType,SqlQuery,ThisServer,ThisDomain,IsSQLAzure,IsSQLMI,UTCDateTime"
            : "CheckId,DisplayName,Message,Severity,RawSeverity,TargetName,TargetType,Category,Description,Remediation,HelpLink,Status,ThisServer,ThisDomain,IsSQLAzure,IsSQLMI,UTCDateTime");

        foreach (var result in summary.Results)
        {
            var row = $"\"{EscapeCsv(result.CheckId)}\",\"{EscapeCsv(result.DisplayName)}\",\"{EscapeCsv(result.Message)}\",\"{result.Severity}\",\"{result.RawSeverity}\",\"{EscapeCsv(result.TargetName)}\",\"{result.TargetType}\",\"{EscapeCsv(result.Category)}\",\"{EscapeCsv(result.Description)}\",\"{EscapeCsv(result.Remediation)}\",\"{result.HelpLink}\",\"{result.Status}\"";

            if (ExportSqlQuery)
                row += $",\"{result.ImplementationType}\",\"{EscapeCsv(result.SqlQuery)}\"";

            row += $",\"{EscapeCsv(result.ThisServer ?? "")}\",\"{EscapeCsv(result.ThisDomain ?? "")}\",\"{(result.IsSQLAzure.HasValue ? (result.IsSQLAzure.Value ? "1" : "0") : "")}\",\"{(result.IsSQLMI.HasValue ? (result.IsSQLMI.Value ? "1" : "0") : "")}\",\"{EscapeCsv(result.UTCDateTime ?? "")}\"";

            sb.AppendLine(row);
        }

        return sb.ToString();
    }

    private string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        // Collapse newlines so multi-line values (e.g. SQL queries) don't break single-line CSV rows
        value = value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        return value.Replace("\"", "\"\"");
    }

    /// <summary>
    /// Automatically save CSV results to output folder when assessment finishes
    /// </summary>
    private async Task SaveCsvToOutputFolderAsync(AssessmentSummary summary, string serverName)
    {
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var outputDir = Path.Combine(baseDir, "output");

            // Ensure the output directory exists
            if (!Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
                // Brief delay to allow filesystem to settle
                await Task.Delay(100);
            }

            // Final verification before writing
            if (!Directory.Exists(outputDir))
            {
                _logger.LogError("Failed to create output directory: {OutputDir}", outputDir);
                return;
            }

            var fileName = $"VulnerabilityAssessment_{SanitizeFileName(serverName)}_{DateTime.Now:yyyy-MM-dd-HHmmss}.csv";
            var filePath = Path.Combine(outputDir, fileName);

            var csv = ExportToCsv(summary);
            await File.WriteAllTextAsync(filePath, csv);

            _logger.LogInformation("CSV export saved to: {FilePath}", filePath);

            // Auto-upload to Azure if enabled — fire-and-forget, never fails the export
            if (_blobExport is { IsConfigured: true, AutoUploadCsvs: true })
            {
                _ = Task.Run(async () =>
                {
                    try { await _blobExport.UploadLocalCsvAsync(filePath, serverName); }
                    catch (Exception uploadEx)
                    {
                        _logger.LogWarning(uploadEx, "Azure auto-upload failed for {FileName} (non-blocking)", fileName);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to auto-export CSV to output folder");
        }
    }

    /// <summary>
    /// Sanitize a string for use as a file name by removing invalid characters
    /// </summary>
    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "unknown";

        // Characters invalid in Windows file names: < > : " / \ | ? *
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName.Where(c => !invalidChars.Contains(c)).ToArray());

        // Replace multiple spaces/underscores with single underscore
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, @"[\s_]+", "_");

        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    // ── Multi-server orchestration ────────────────────────────────────────────

    /// <summary>
    /// Runs assessment across multiple (connection, serverName) pairs in parallel batches,
    /// then merges the results into a single deduplicated catch-all summary.
    /// Checks that fire on multiple servers/databases are collapsed into one row;
    /// the TargetName accumulates all affected instances.
    ///   e.g. Server[@Name='MSI; PROD']/Database[@Name='DB1; DB2']
    /// One consolidated CSV is written to .\output\ when all servers finish.
    /// </summary>
    public async Task<AssessmentSummary> RunMultiServerAssessmentAsync(
        IReadOnlyList<(string ConnectionString, string ServerName)> targets,
        int parallelism = 3,
        IProgress<(int Completed, int Total, string CurrentServer)>? progress = null)
    {
        var allSummaries = new List<AssessmentSummary>(targets.Count);
        var semaphore = new SemaphoreSlim(parallelism);
        var lockObj = new object();
        int completed = 0;

        var tasks = targets.Select(async target =>
        {
            await semaphore.WaitAsync();
            try
            {
                progress?.Report((completed, targets.Count, target.ServerName));
                var summary = await RunServerAssessmentAsync(target.ConnectionString);
                lock (lockObj)
                {
                    allSummaries.Add(summary);
                    completed++;
                }
                progress?.Report((completed, targets.Count, target.ServerName));
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        var merged = MergeResults(allSummaries);

        // Save one consolidated CSV for the whole run
        var serverLabel = targets.Count == 1
            ? SanitizeFileName(targets[0].ServerName)
            : $"MultiServer_{targets.Count}";
        await SaveCsvToOutputFolderAsync(merged, serverLabel);

        return merged;
    }

    /// <summary>
    /// Deduplicates results from multiple per-server summaries into a single catch-all summary.
    ///
    /// Merge key: (CheckId, Message) — Message discriminates checks that fire once per object
    /// (e.g. BackupTables fires a separate row per backup table, each with a distinct Message).
    ///
    /// For rows with the same key:
    ///   • Server-level checks: Server[@Name='MSI; PROD']
    ///   • Database-level checks: Server[@Name='MSI; PROD']/Database[@Name='DB1; DB2']
    ///     — when the same database name appears on two different servers it is listed once per
    ///       server segment so the reader can tell which server owns which database.
    /// </summary>
    public static AssessmentSummary MergeResults(IEnumerable<AssessmentSummary> summaries)
    {
        // (CheckId, Message) → accumulated parsed target state
        // Tuple comparer: case-insensitive on both elements
        var tupleComparer = new TupleOrdinalIgnoreCaseComparer();
        var mergeMap = new Dictionary<(string, string), MergeEntry>(tupleComparer);

        foreach (var summary in summaries)
        {
            foreach (var r in summary.Results)
            {
                var key = (r.CheckId, r.Message);

                if (!mergeMap.TryGetValue(key, out var entry))
                {
                    entry = new MergeEntry(r);
                    mergeMap[key] = entry;
                }
                else
                {
                    entry.Merge(r.TargetName);
                }
            }
        }

        var merged = new AssessmentSummary();
        foreach (var entry in mergeMap.Values)
        {
            var result = entry.ToResult();
            merged.Results.Add(result);
            merged.TotalChecks++;
            if (result.Status == "Passed") merged.PassedChecks++;
            else merged.FailedChecks++;
        }

        return merged;
    }

    // ── TargetName XPath-style accumulator ───────────────────────────────────

    /// <summary>
    /// Parses and accumulates XPath-style SQL Assessment target paths.
    ///
    /// Supported forms:
    ///   Server[@Name='MSI']
    ///   Server[@Name='MSI']/Database[@Name='Capitec_PowerBI']
    ///
    /// When the same check fires for multiple servers/databases the names are
    /// appended within the existing @Name attribute, separated by "; ".
    /// The final rendered form is always valid enough for display purposes:
    ///   Server[@Name='MSI; PROD']/Database[@Name='DB1; DB2']
    ///
    /// Database names are tracked per-server so the mapping is never ambiguous.
    /// </summary>
    private sealed class MergeEntry
    {
        private readonly AssessmentResult _prototype;

        // Ordered list of server names seen (preserves first-seen order)
        private readonly List<string> _servers = new();

        // Per-server: ordered list of database names seen on that server (or empty = server-level check)
        private readonly Dictionary<string, List<string>> _serverDbs =
            new(StringComparer.OrdinalIgnoreCase);

        internal MergeEntry(AssessmentResult prototype)
        {
            _prototype = prototype;
            ParseAndAccumulate(prototype.TargetName);
        }

        internal void Merge(string targetName) => ParseAndAccumulate(targetName);

        private void ParseAndAccumulate(string targetName)
        {
            if (string.IsNullOrWhiteSpace(targetName))
                return;

            ParseTarget(targetName, out var serverName, out var dbName);

            if (serverName == null)
                return;

            if (!_serverDbs.TryGetValue(serverName, out var dbs))
            {
                _servers.Add(serverName);
                dbs = new List<string>();
                _serverDbs[serverName] = dbs;
            }

            if (dbName != null && !dbs.Contains(dbName, StringComparer.OrdinalIgnoreCase))
                dbs.Add(dbName);
        }

        internal AssessmentResult ToResult()
        {
            var r = new AssessmentResult
            {
                CheckId = _prototype.CheckId,
                DisplayName = _prototype.DisplayName,
                Message = _prototype.Message,
                Severity = _prototype.Severity,
                RawSeverity = _prototype.RawSeverity,
                TargetType = _prototype.TargetType,
                Category = _prototype.Category,
                Description = _prototype.Description,
                Remediation = _prototype.Remediation,
                HelpLink = _prototype.HelpLink,
                Status = _prototype.Status,
                TargetName = BuildTargetName(),
                ImplementationType = _prototype.ImplementationType,
                SqlQuery = _prototype.SqlQuery,
                // ThisServer is intentionally null for multi-server merged results
                ThisDomain = _prototype.ThisDomain,
                IsSQLAzure = _prototype.IsSQLAzure,
                IsSQLMI = _prototype.IsSQLMI,
                UTCDateTime = _prototype.UTCDateTime,
            };
            return r;
        }

        private string BuildTargetName()
        {
            // All servers share the same set of database names → collapsed target
            //   Server[@Name='MSI; PROD']/Database[@Name='DB1; DB2']
            // If databases differ per server, build one segment per server:
            //   Server[@Name='MSI']/Database[@Name='DB1']; Server[@Name='PROD']/Database[@Name='DB2']

            // Check if all servers have identical database lists (or all server-level)
            var allDbs = _serverDbs.Values.Select(d => string.Join("|", d)).Distinct().ToList();

            if (allDbs.Count <= 1)
            {
                // Uniform: collapse into a single XPath expression
                var serverPart = $"Server[@Name='{string.Join("; ", _servers)}']";
                var dbs = _serverDbs.Values.FirstOrDefault(d => d.Count > 0);
                return dbs != null && dbs.Count > 0
                    ? $"{serverPart}/Database[@Name='{string.Join("; ", dbs)}']"
                    : serverPart;
            }
            else
            {
                // Non-uniform: one segment per server
                var segments = _servers.Select(srv =>
                {
                    var dbs = _serverDbs[srv];
                    return dbs.Count > 0
                        ? $"Server[@Name='{srv}']/Database[@Name='{string.Join("; ", dbs)}']"
                        : $"Server[@Name='{srv}']";
                });
                return string.Join("; ", segments);
            }
        }

        /// <summary>
        /// Extracts server name and optional database name from an XPath-style target path.
        ///   "Server[@Name='MSI']"                           → ("MSI", null)
        ///   "Server[@Name='MSI']/Database[@Name='MyDB']"    → ("MSI", "MyDB")
        /// </summary>
        private static void ParseTarget(string target, out string? serverName, out string? dbName)
        {
            serverName = null;
            dbName = null;

            // Extract Server name
            var sStart = target.IndexOf("Server[@Name='", StringComparison.Ordinal);
            if (sStart < 0) return;
            sStart += "Server[@Name='".Length;
            var sEnd = target.IndexOf("'", sStart, StringComparison.Ordinal);
            if (sEnd < 0) return;
            serverName = target[sStart..sEnd];

            // Extract Database name if present
            var dStart = target.IndexOf("/Database[@Name='", sEnd, StringComparison.Ordinal);
            if (dStart < 0) return;
            dStart += "/Database[@Name='".Length;
            var dEnd = target.IndexOf("'", dStart, StringComparison.Ordinal);
            if (dEnd < 0) return;
            dbName = target[dStart..dEnd];
        }
    }
}

// Tuple equality comparer used by MergeResults
file sealed class TupleOrdinalIgnoreCaseComparer : IEqualityComparer<(string, string)>
{
    public bool Equals((string, string) x, (string, string) y) =>
        string.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode((string, string) obj) =>
        HashCode.Combine(
            obj.Item1?.ToUpperInvariant() ?? "",
            obj.Item2?.ToUpperInvariant() ?? "");
}

// Model classes
internal class AssessmentCheckDefinition
{
    public string CheckId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public string Severity { get; set; } = "";
    public string HelpLink { get; set; } = "";
    public string Sql { get; set; } = "";
    public string PowerShell { get; set; } = "";
    public string Wmi { get; set; } = "";
    public string Registry { get; set; } = "";
    public string TargetType { get; set; } = "";
    public string ImplementationType { get; set; } = "Sql"; // Sql, PowerShell, Wmi, Registry
}

public class AssessmentResult
{
    public string CheckId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Message { get; set; } = "";
    public string Severity { get; set; } = "";
    public string TargetName { get; set; } = "";
    public string TargetType { get; set; } = "";
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";
    public string HelpLink { get; set; } = "";
    public string Remediation { get; set; } = "";
    public string Status { get; set; } = "";
    public string RawSeverity { get; set; } = "";
    public string SqlQuery { get; set; } = "";
    public string ImplementationType { get; set; } = "";
    // Server identity — populated per-server; NULL in consolidated multi-server exports
    public string? ThisServer { get; set; }
    public string? ThisDomain { get; set; }
    public bool? IsSQLAzure { get; set; }
    public bool? IsSQLMI { get; set; }
    public string? UTCDateTime { get; set; }
}

public class AssessmentSummary
{
    public List<AssessmentResult> Results { get; set; } = new();
    public int TotalChecks { get; set; }
    public int PassedChecks { get; set; }
    public int FailedChecks { get; set; }
}

// Holds an executable implementation extracted from a probe entry
internal class ImplementationDefinition
{
    public string? Sql { get; set; }
    public string? Query { get; set; }
    public string? PowerShell { get; set; }
    public string? Wmi { get; set; }
    public string? Registry { get; set; }
}
