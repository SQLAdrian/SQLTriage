/* In the name of God, the Merciful, the Compassionate */

using ApexCharts;
using SqlHealthAssessment.Data.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SqlHealthAssessment.Data
{
    public class DiagnosticScriptRunner
    {
        private readonly ServerConnectionManager _connectionManager;
        private readonly ILogger<DiagnosticScriptRunner> _logger;
        private readonly Services.AzureBlobExportService? _blobExport;
        private List<ScriptConfiguration>? _scriptConfigurations;
        private readonly object _lock = new();
        private string FiletimeStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        /// <summary>
        /// Raised when an Azure Blob auto-upload completes (success or failure).
        /// Args: (fileName, success, message)
        /// </summary>
        public event Action<string, bool, string>? OnBlobUploadResult;

        /// <summary>
        /// Maximum number of scripts to execute concurrently when running all enabled scripts.
        /// </summary>
        public int MaxConcurrency { get; set; } = 3;

        private static readonly JsonSerializerOptions ScriptConfigSerializerOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public DiagnosticScriptRunner(ServerConnectionManager connectionManager, ILogger<DiagnosticScriptRunner> logger, Services.AzureBlobExportService? blobExport = null)
        {
            _connectionManager = connectionManager;
            _logger = logger;
            _blobExport = blobExport;
        }

        public List<ScriptConfiguration> LoadScriptConfigurations()
        {
            lock (_lock)
            {
                if (_scriptConfigurations != null)
                    return _scriptConfigurations;

                try
                {
                    var configPath = Path.Combine(AppContext.BaseDirectory, "Config", "script-configurations.json");
                    if (!File.Exists(configPath))
                    {
                        configPath = Path.Combine(AppContext.BaseDirectory, "script-configurations.json");
                    }
                    if (!File.Exists(configPath))
                    {
                        _logger.LogWarning("Script configurations file not found at {Path}", configPath);
                        _scriptConfigurations = new List<ScriptConfiguration>();
                        return _scriptConfigurations;
                    }

                    var json = File.ReadAllText(configPath);
                    _scriptConfigurations = JsonSerializer.Deserialize<List<ScriptConfiguration>>(json, ScriptConfigSerializerOptions)
                        ?? new List<ScriptConfiguration>();

                    // Sort by ExecutionOrder for display and execution
                    _scriptConfigurations = _scriptConfigurations
                        .OrderBy(s => s.ExecutionOrder)
                        .ToList();

                    _logger.LogInformation("Loaded {Count} script configurations", _scriptConfigurations.Count);
                    return _scriptConfigurations;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error loading script configurations");
                    _scriptConfigurations = new List<ScriptConfiguration>();
                    return _scriptConfigurations;
                }
            }
        }

        /// <summary>
        /// Saves script configurations back to the JSON file.
        /// </summary>
        public void SaveScriptConfigurations(List<ScriptConfiguration> configurations)
        {
            lock (_lock)
            {
                try
                {
                    var configPath = Path.Combine(AppContext.BaseDirectory, "Config", "script-configurations.json");
                    var json = JsonSerializer.Serialize(configurations, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    });
                    File.WriteAllText(configPath, json);
                    
                    // Update cached version
                    _scriptConfigurations = configurations.OrderBy(s => s.ExecutionOrder).ToList();
                    
                    _logger.LogInformation("Saved {Count} script configurations", configurations.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error saving script configurations");
                    throw;
                }
            }
        }

        public ScriptConfiguration? GetScriptConfiguration(string id)
        {
            var configs = LoadScriptConfigurations();
            return configs.FirstOrDefault(c => c.Id == id);
        }

        public async Task<ScriptExecutionResult> ExecuteScriptAsync(
            ScriptConfiguration config,
            ServerConnection connection,
            string targetServer,
            CancellationToken cancellationToken = default)
        {
            var result = new ScriptExecutionResult
            {
                ScriptName = config.Name,
                ServerName = targetServer,
                Success = false
            };

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // Create connection to MASTER database for audit scripts
                var masterConnectionString = connection.GetConnectionString(targetServer, "master");
                using var dbConnection = new SqlConnection(masterConnectionString);
                await dbConnection.OpenAsync(cancellationToken);

                // If ExecutionParameters references a stored procedure, check whether it exists.
                // If it is missing, the ScriptPath installation script will create it below —
                // do NOT return early; just log and continue so the install script runs first.
                if (!string.IsNullOrEmpty(config.ExecutionParameters))
                {
                    var procedureName = ExtractProcedureName(config.ExecutionParameters);
                    if (!string.IsNullOrEmpty(procedureName))
                    {
                        var procExists = await ProcedureExistsAsync(dbConnection, procedureName, cancellationToken);
                        if (!procExists)
                        {
                            _logger.LogInformation(
                                "Procedure '{Procedure}' not found on {Server} — installing from '{ScriptPath}' first",
                                procedureName, targetServer, config.ScriptPath);
                        }
                    }
                }

                // Load and validate the script file
                var scriptPath = Path.Combine("scripts", config.ScriptPath);
                
                // Validate script path to prevent path traversal attacks
                if (!IsValidScriptPath(config.ScriptPath))
                {
                    throw new InvalidOperationException($"Invalid script path detected: {config.ScriptPath}");
                }
                
                if (!File.Exists(scriptPath))
                    throw new FileNotFoundException($"Script not found: {scriptPath}");

                var scriptContent = await File.ReadAllTextAsync(scriptPath, cancellationToken);

                // --- SQL SAFETY VALIDATION ---
                // Validate the script content before execution
                SqlSafetyValidator.ValidateOrThrow(scriptContent, config.Name);

                // Validate database scope
                var scopeResult = SqlSafetyValidator.ValidateDatabaseScope(scriptContent);
                if (!scopeResult.IsSafe)
                {
                    throw new SqlSafetyException(
                        $"Script '{config.Name}' blocked: {scopeResult.Reason}",
                        config.Name,
                        scopeResult.Reason);
                }

                // Also validate ExecutionTest, ExecutionParameters and SqlQueryForOutput
                if (!string.IsNullOrEmpty(config.ExecutionTest))
                {
                    SqlSafetyValidator.ValidateOrThrow(config.ExecutionTest, $"{config.Name} (ExecutionTest)");
                }
                if (!string.IsNullOrEmpty(config.ExecutionParameters))
                {
                    SqlSafetyValidator.ValidateOrThrow(config.ExecutionParameters, $"{config.Name} (ExecutionParameters)");
                }
                if (!string.IsNullOrEmpty(config.SqlQueryForOutput))
                {
                    SqlSafetyValidator.ValidateOrThrow(config.SqlQueryForOutput, $"{config.Name} (SqlQueryForOutput)");
                }

                _logger.LogInformation("Script {Name} passed safety validation for {Server}", config.Name, targetServer);
                // --- END SQL SAFETY VALIDATION ---

                // Split script on GO batch separators using regex (GO must be on its own line)
                var batches = System.Text.RegularExpressions.Regex.Split(
                    scriptContent,
                    @"^\s*GO\s*$",
                    System.Text.RegularExpressions.RegexOptions.Multiline |
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                foreach (string batch in batches)
                {
                    string trimmed = batch.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed))
                        continue;

                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        using var command = dbConnection.CreateCommand();
                        command.CommandText = batch;
                        command.CommandTimeout = config.TimeoutSeconds;
                        command.CommandType = CommandType.Text;
                        using var reader = await command.ExecuteReaderAsync(cancellationToken);
                        // Don't need results from script creation
                    }
                    catch (SqlException ex) when (ex.Number == 2714 || ex.Number == 15233)
                    {
                        // 2714 = Object already exists, 15233 = Property doesn't exist
                        // Continue with next batch
                    }
                    catch (OperationCanceledException)
                    {
                        throw; // Re-throw cancellation
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Batch execution warning in script {Name} on {Server}", config.Name, targetServer);
                        // Log but continue - some batches may fail due to version differences
                    }
                }

                // Run ExecutionTest to decide whether ExecutionParameters should run
                bool shouldRunExecParams = true;
                if (!string.IsNullOrEmpty(config.ExecutionTest))
                {
                    try
                    {
                        using var testCmd = dbConnection.CreateCommand();
                        testCmd.CommandText = config.ExecutionTest;
                        testCmd.CommandTimeout = config.TimeoutSeconds;
                        testCmd.CommandType = CommandType.Text;
                        using var testReader = await testCmd.ExecuteReaderAsync(cancellationToken);

                        if (await testReader.ReadAsync(cancellationToken))
                        {
                            var toRunOrdinal = testReader.GetOrdinal("ToRun");
                            var toRun = Convert.ToInt32(testReader.GetValue(toRunOrdinal));
                            shouldRunExecParams = toRun == 1;
                        }

                        _logger.LogInformation("ExecutionTest for {Name} on {Server}: ToRun={ToRun}",
                            config.Name, targetServer, shouldRunExecParams ? 1 : 0);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "ExecutionTest failed for {Name} on {Server} — defaulting to run ExecutionParameters",
                            config.Name, targetServer);
                        shouldRunExecParams = true;
                    }
                }

                // Execute the stored procedure / execution parameters
                // A failure here is non-fatal — we still attempt SqlQueryForOutput below
                if (shouldRunExecParams && !string.IsNullOrEmpty(config.ExecutionParameters))
                {
                    try
                    {
                        using var command = dbConnection.CreateCommand();
                        command.CommandText = config.ExecutionParameters;
                        command.CommandTimeout = config.TimeoutSeconds;
                        command.CommandType = CommandType.Text;
                        using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        result.ErrorMessage = $"ExecutionParameters warning: {ex.Message}";
                        _logger.LogWarning(ex, "ExecutionParameters failed for script {Name} on {Server} — continuing to SqlQueryForOutput", config.Name, targetServer);
                    }
                }
                else if (!shouldRunExecParams)
                {
                    _logger.LogInformation("ExecutionParameters skipped for {Name} on {Server} (ExecutionTest returned ToRun=0)",
                        config.Name, targetServer);
                }

                // Select the results:
                // - If ExecutionParameters ran (or was empty): always run SqlQueryForOutput
                // - If ExecutionParameters was skipped (ToRun=0): only run SqlQueryForOutput when ExportToCsv is true
                bool shouldRunOutputQuery = shouldRunExecParams || config.ExportToCsv;
                if (shouldRunOutputQuery && !string.IsNullOrEmpty(config.SqlQueryForOutput))
                {
                    using var command = dbConnection.CreateCommand();
                    command.CommandText = config.SqlQueryForOutput;
                    command.CommandTimeout = config.TimeoutSeconds;
                    command.CommandType = CommandType.Text;

                    using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    result.Results = new List<Dictionary<string, object>>();

                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var row = new Dictionary<string, object>();
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            row[reader.GetName(i)] = reader.IsDBNull(i) ? null! : reader.GetValue(i);
                        }
                        result.Results.Add(row);
                        result.RowsAffected++;
                    }
                }

                stopwatch.Stop();
                result.ExecutionTime = stopwatch.Elapsed;
                ExportScriptToCsv(result);

                result.Success = true;

                _logger.LogInformation("Script {Name} executed successfully on {Server} in {ElapsedMs}ms ({Rows} rows)",
                    config.Name, targetServer, stopwatch.ElapsedMilliseconds, result.RowsAffected);
            }
            catch (SqlSafetyException ex)
            {
                stopwatch.Stop();
                result.ExecutionTime = stopwatch.Elapsed;
                result.ErrorMessage = $"BLOCKED: {ex.BlockedReason}";
                _logger.LogError("Script {Name} blocked by safety validator: {Reason}", config.Name, ex.BlockedReason);
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                result.ExecutionTime = stopwatch.Elapsed;
                result.ErrorMessage = "Execution was cancelled.";
                _logger.LogWarning("Script {Name} execution cancelled on {Server}", config.Name, targetServer);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                result.ExecutionTime = stopwatch.Elapsed;
                result.ErrorMessage = ex.Message;
                _logger.LogError(ex, "Error executing script {Name} on {Server}", config.Name, targetServer);
            }

            return result;
        }

        public async Task<List<ScriptExecutionResult>> ExecuteAllEnabledScriptsAsync(
            ServerConnection connection,
            string targetServer,
            CancellationToken cancellationToken = default)
        {
            var configs = LoadScriptConfigurations()
                .Where(c => c.Enabled)
                .OrderBy(c => c.ExecutionOrder)
                .ToList();

            // Use SemaphoreSlim for concurrent execution with a limit
            using var semaphore = new SemaphoreSlim(MaxConcurrency);
            var tasks = configs.Select(async config =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    return await ExecuteScriptAsync(config, connection, targetServer, cancellationToken);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var results = await Task.WhenAll(tasks);
            return results.ToList();
        }

        public string ExportToCsv(ScriptExecutionResult result)
        {
            if (result.Results == null || result.Results.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();

            // Get headers from first row and add ServerName as first column
            var headers = result.Results.First().Keys.ToList();
            headers.Insert(0, "ServerName");
            sb.AppendLine(string.Join(",", headers.Select(h => $"\"{h}\"")));

            // Add data rows with ServerName as first column
            foreach (var row in result.Results)
            {
                var values = new List<string> { $"\"{result.ServerName}\"" };
                foreach (var h in headers.Skip(1))
                {
                    if (row.TryGetValue(h, out var value))
                    {
                        values.Add($"\"{value?.ToString()?.Replace("\"", "\"\"") ?? ""}\"");
                    }
                    else
                    {
                        values.Add("\"\"");
                    }
                }
                sb.AppendLine(string.Join(",", values));
            }

            return sb.ToString();
        }

        private void ExportScriptToCsv(ScriptExecutionResult result)
        {
            if (result.Results == null || result.Results.Count == 0)
                return;

            var csv = ExportToCsv(result);
            var outputFolder = Path.Combine(AppContext.BaseDirectory, "output");

            // Ensure output directory exists
            if (!Directory.Exists(outputFolder))
                Directory.CreateDirectory(outputFolder);

            // Sanitize file name to prevent path traversal and invalid characters
            var sanitizedServerName = SanitizeFileName(result.ServerName);
            var sanitizedScriptName = SanitizeFileName(result.ScriptName);
            
            var csvFileName = Path.Combine(outputFolder,
                $"{sanitizedServerName}_{sanitizedScriptName}_{FiletimeStamp}.csv");
            
            var jsonFileName = Path.Combine(outputFolder,
                $"{sanitizedServerName}_{sanitizedScriptName}_{FiletimeStamp}.json");

            try
            {
                // Write CSV directly - no need to re-read the file afterward
                File.WriteAllText(csvFileName, csv, Encoding.UTF8);
                _logger.LogInformation("CSV exported to {FileName}", csvFileName);

                // Also export JSON
                var json = ExportToJson(result);
                File.WriteAllText(jsonFileName, json, Encoding.UTF8);
                _logger.LogInformation("JSON exported to {FileName}", jsonFileName);

                // Auto-upload to Azure if enabled — fire-and-forget, never fails the export
                if (_blobExport is { IsConfigured: true, AutoUploadCsvs: true })
                {
                    var fileName = Path.GetFileName(csvFileName);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var uploadResult = await _blobExport.UploadLocalCsvAsync(csvFileName, result.ServerName);
                            if (uploadResult.Success)
                                OnBlobUploadResult?.Invoke(fileName, true, uploadResult.Message);
                            else
                                OnBlobUploadResult?.Invoke(fileName, false, uploadResult.Message);
                        }
                        catch (Exception uploadEx)
                        {
                            _logger.LogWarning(uploadEx, "Azure auto-upload failed for {FileName} (non-blocking)", csvFileName);
                            OnBlobUploadResult?.Invoke(fileName, false, uploadEx.Message);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error exporting files to {FileName}", csvFileName);
            }
        }

        /// <summary>
        /// Exports the script execution result to JSON format.
        /// </summary>
        public string ExportToJson(ScriptExecutionResult result)
        {
            if (result.Results == null || result.Results.Count == 0)
                return string.Empty;

            // Create a structured JSON output with metadata
            var jsonOutput = new
            {
                metadata = new
                {
                    serverName = result.ServerName,
                    scriptName = result.ScriptName,
                    executionTime = result.ExecutionTime.ToString(),
                    executedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    rowCount = result.Results.Count
                },
                results = result.Results
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            return JsonSerializer.Serialize(jsonOutput, options);
        }

        public void DownloadCsv(string csvContent, string fileName)
        {
            var bytes = Encoding.UTF8.GetBytes(csvContent);
            var base64 = Convert.ToBase64String(bytes);

            // In Blazor, we'll return the bytes and let the UI handle download
            // This is a placeholder for the actual download logic
            _logger.LogInformation("CSV content generated for {FileName}, {Size} bytes", fileName, csvContent.Length);
        }

        private string? ExtractProcedureName(string sql)
        {
            // Try to extract procedure name from patterns like:
            // EXEC [dbo].[sp_Blitz] @Param1 = 1
            // EXEC dbo.stpSecurity_Checklist
            // EXEC  [dbo].[sp_triage(c)]

            var match = System.Text.RegularExpressions.Regex.Match(
                sql,
                @"EXEC\s+(?:\[?\w+\]?\.?\[?)([^\s\]@]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            return match.Success ? match.Groups[1].Value.Trim('[', ']') : null;
        }

        private async Task<bool> ProcedureExistsAsync(SqlConnection connection, string procedureName, CancellationToken cancellationToken)
        {
            try
            {
                // First try without schema
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT COUNT(*) FROM sys.objects
                    WHERE type = 'P' AND name = @ProcName
                    AND is_ms_shipped = 0";
                command.Parameters.AddWithValue("@ProcName", procedureName);
                var count = (int)(await command.ExecuteScalarAsync(cancellationToken))!;

                if (count > 0) return true;

                // Try with common schema prefixes
                foreach (var schema in new[] { "dbo", "guest", "sys" })
                {
                    using var cmd2 = connection.CreateCommand();
                    cmd2.CommandText = @"
                        SELECT COUNT(*) FROM sys.objects o
                        INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
                        WHERE o.type = 'P' AND o.name = @ProcName
                        AND s.name = @Schema AND o.is_ms_shipped = 0";
                    cmd2.Parameters.AddWithValue("@ProcName", procedureName);
                    cmd2.Parameters.AddWithValue("@Schema", schema);
                    if ((int)(await cmd2.ExecuteScalarAsync(cancellationToken))! > 0) return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error checking if procedure {Proc} exists", procedureName);
                return true; // If we can't check, assume it exists to avoid false negatives
            }
        }

        /// <summary>
        /// Validates script path to prevent path traversal attacks
        /// </summary>
        internal static bool IsValidScriptPath(string scriptPath)
        {
            if (string.IsNullOrWhiteSpace(scriptPath))
                return false;

            var normalizedPath = scriptPath.Replace('\\', '/').ToLowerInvariant();

            if (normalizedPath.Contains("../") || normalizedPath.Contains("..\\"))
                return false;

            if (normalizedPath.StartsWith("/") || normalizedPath.StartsWith("c:"))
                return false;

            var extension = Path.GetExtension(scriptPath).ToLowerInvariant();
            if (extension != ".sql" && extension != ".txt")
                return false;

            return true;
        }

        /// <summary>
        /// Sanitizes a file name to prevent path traversal and invalid characters
        /// </summary>
        private static string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return "unnamed";

            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = fileName;

            foreach (var c in invalidChars)
                sanitized = sanitized.Replace(c, '_');

            sanitized = sanitized.Replace("../", "_").Replace("..\\", "_");

            if (sanitized.Length > 100)
                sanitized = sanitized.Substring(0, 100);

            return sanitized;
        }
    }
}
