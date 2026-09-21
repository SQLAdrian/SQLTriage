/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ApexCharts;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SQLTriage.Data.Models;

namespace SQLTriage.Data
{
    public class DiagnosticScriptRunner
    {
        private readonly ServerConnectionManager _connectionManager;
        private readonly ILogger<DiagnosticScriptRunner> _logger;
        private readonly Services.AzureBlobExportService? _blobExport;
        // Optional: when supplied (DI), per-script connections are rented from the
        // shared pool so parallel multi-server runs stay under its caps
        // (MaxPerServer / MaxGlobal). Null in tests / non-DI paths → direct open.
        private readonly SqlConnectionPoolService? _pool;
        private List<ScriptConfiguration>? _scriptConfigurations;
        private readonly object _lock = new();

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

        public DiagnosticScriptRunner(ServerConnectionManager connectionManager, ILogger<DiagnosticScriptRunner> logger, Services.AzureBlobExportService? blobExport = null, SqlConnectionPoolService? pool = null)
        {
            _connectionManager = connectionManager;
            _logger = logger;
            _blobExport = blobExport;
            _pool = pool;
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

                    // An install UPGRADED over an older one keeps its own config file
                    // (installer/SQLTriage.iss ships this path onlyifdoesntexist), so shipped
                    // entries added since that install never arrive on their own. Ruling
                    // 2026-08-23 #4: add the missing ones here, before the read, and never touch
                    // anything already in the file. Announces and returns on any problem rather
                    // than throwing or replacing the store.
                    ScriptConfigurationMigrator.EnsureShippedEntries(configPath, _logger);

                    // The same delivery gap, for a value that CHANGED rather than an entry that was
                    // added. The sp_Blitz output query shipped since the repo's first commit produced
                    // a CSV the Export Pack could not read (DECISIONS 2026-08-24); fixing only the
                    // shipped file would have fixed only installs that never had the problem. This
                    // replaces that query where the installed file still holds it VERBATIM as we
                    // shipped it, and leaves an operator's edit alone.
                    ScriptConfigurationMigrator.RepairSupersededOutputQueries(configPath, _logger);

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

            // Install-batch failures are tolerated (version differences) but must not
            // vanish: if the run later fails overall, the first one is usually the root
            // cause (e.g. CREATE PROCEDURE dying on a case-sensitive collation).
            var batchWarnings = new List<string>();

            // Connection to MASTER database for audit scripts. When a shared pool is
            // available (DI), rent from it so parallel multi-server runs stay under the
            // pool caps; otherwise open directly. Either way it is released in `finally`.
            var masterConnectionString = connection.GetConnectionString(targetServer, "master");
            SqlConnection dbConnection;
            bool fromPool = _pool != null;
            if (fromPool)
            {
                dbConnection = (SqlConnection)await _pool!.GetConnectionAsync(masterConnectionString, cancellationToken);
            }
            else
            {
                dbConnection = new SqlConnection(masterConnectionString);
                await dbConnection.OpenAsync(cancellationToken);
            }

            try
            {
                // S-1: apply the same session-safety options the check lane uses (READ UNCOMMITTED
                // + LOCK_TIMEOUT) so a diagnostic script cannot block a busy production server.
                // Applied once to the session rather than prepended per batch: the script batches
                // below are CREATE/ALTER PROCEDURE, which must be the first statement in a batch.
                await SqlSessionSafety.ApplyAsync(dbConnection, cancellationToken: cancellationToken);

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

                // Load and validate the script file.
                //
                // RESOLVED BESIDE THE BINARY, NOT AGAINST THE WORKING DIRECTORY. Until 2026-08-24
                // this read Path.Combine("scripts", config.ScriptPath), i.e. CWD-relative, and it
                // was the only CWD-relative file read in shipping code. Two of the app's own launch
                // vectors start the process with a working directory of C:\Windows\System32 - the
                // HKCU Run autostart the installer writes, and the Windows service - so on those
                // paths the read could never resolve however the installer shipped. Proved
                // 2026-08-24 by InstallerScriptResolutionLiveTests: from a working directory with
                // no scripts folder the sp_Blitz entry returned Success=false, "Script not found:
                // scripts\sp_Blitz.sql".
                //
                // BaseDirectory is what the rest of the app already uses for exactly this folder:
                // ExportPackRunner.cs:703 resolves scripts\identity_manifest.sql there, and
                // AutoUpdateService.cs:791/:842 CREATE AND FILL AppContext.BaseDirectory\scripts -
                // the updater was writing a directory this reader never looked in.
                //
                // NO CWD FALLBACK, deliberately. Searched WITH THE PATHSPEC, because without one the
                // same query also matches a comment line in the test tree and returns two hits:
                //   git log --all -S "Path.Combine(\"scripts\", config.ScriptPath)" \
                //     -- Data/DiagnosticScriptRunner.cs
                // That returns exactly one hit, the initial squashed commit c3e91cd, so the CWD form
                // has no documented reason to exist; and a fallback would let whatever directory the
                // operator happened to launch from decide which SQL runs against a client's server.
                var scriptPath = Path.Combine(AppContext.BaseDirectory, "scripts", config.ScriptPath);

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

                _logger.LogInformation("Script {Name} passed safety validation for {Server}", config.Name, LogAnon.S(targetServer));
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
                        batchWarnings.Add(ex.Message);
                        _logger.LogWarning(ex, "Batch execution warning in script {Name} on {Server}", config.Name, LogAnon.S(targetServer));
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
                            config.Name, LogAnon.S(targetServer), shouldRunExecParams ? 1 : 0);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "ExecutionTest failed for {Name} on {Server} — defaulting to run ExecutionParameters",
                            config.Name, LogAnon.S(targetServer));
                        shouldRunExecParams = true;
                    }
                }

                // Execute the stored procedure / execution parameters.
                //
                // ⚠ A failure here is FATAL to the run, and until 2026-08-23 it was not. The catch
                // below recorded a WARNING and execution fell through to SqlQueryForOutput. For every
                // config whose output query reads a PERSISTED table by its latest timestamp — the
                // shipped sp_Blitz entry does exactly that, WHERE CheckDate = (SELECT MAX(CheckDate))
                // — that meant a server where the EXEC failed exported the PREVIOUS run's rows under
                // today's file name and reported Success = true. Stale data presented as fresh is
                // worse than a visible failure, and it is invisible precisely on the servers where
                // something is wrong (missing prerequisite, permissions, timeout). So the run now
                // fails closed: no output query, no CSV, Success stays false, and FullAudit's
                // post-run modal raises it as an Error instead of a Warning.
                bool execParamsFailed = false;
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
                        execParamsFailed = true;
                        result.ErrorMessage = $"ExecutionParameters failed: {ex.Message}";
                        _logger.LogError(ex, "ExecutionParameters failed for script {Name} on {Server}. Failing the run closed (no output query, no CSV) so the previous run's rows cannot be exported as this run's", config.Name, LogAnon.S(targetServer));
                    }
                }
                else if (!shouldRunExecParams)
                {
                    result.StatusMessage = "✓ Loaded previous execution";
                    result.ReusedPreviousExecution = true;
                    _logger.LogInformation("ExecutionParameters skipped for {Name} on {Server} (ExecutionTest returned ToRun=0)",
                        config.Name, LogAnon.S(targetServer));
                }

                // Select the results. The rule is one line and it is unit-tested as a truth table,
                // because the live proof of it can only run against a real instance.
                bool shouldRunOutputQuery =
                    ShouldRunOutputQuery(execParamsFailed, shouldRunExecParams, config.ExportToCsv);
                if (shouldRunOutputQuery && !string.IsNullOrEmpty(config.SqlQueryForOutput))
                {
                    using var command = dbConnection.CreateCommand();
                    command.CommandText = config.SqlQueryForOutput;
                    command.CommandTimeout = config.TimeoutSeconds;
                    command.CommandType = CommandType.Text;

                    using var reader = await command.ExecuteReaderAsync(cancellationToken);

                    // Selecting the set and reading the rows are ONE call on purpose. They were two
                    // for an afternoon, and deleting the advance from the call site left every unit
                    // test green because the truth table was testing a function nothing was obliged
                    // to use - the exact bypass shape the config-migration lane was bitten by. As
                    // one member, a reader that skips the advance cannot also read the rows.
                    result.Results = await ReadOutputRowsAsync(
                        reader, config.OutputResultSetIndex, config.Name, cancellationToken);
                    result.RowsAffected = result.Results.Count;
                }

                stopwatch.Stop();
                result.ExecutionTime = stopwatch.Elapsed;

                if (execParamsFailed)
                {
                    // Deliberately NOT exporting and NOT succeeding. result.ErrorMessage already
                    // carries the root cause and result.Results is empty, so nothing downstream can
                    // mistake a stale table for this run's output.
                    _logger.LogError("Script {Name} FAILED on {Server} after {ElapsedMs}ms: {Error}",
                        config.Name, LogAnon.S(targetServer), stopwatch.ElapsedMilliseconds, result.ErrorMessage);
                }
                else
                {
                    ExportScriptToCsv(result);

                    result.Success = true;

                    // A run that skipped the EXEC did not execute, and the log line said it did. The
                    // rows are the previous execution's and the CSV carries their real CheckDate, so
                    // the data was never wrong; the sentence was. Name the kind of run.
                    if (result.ReusedPreviousExecution)
                        _logger.LogInformation("Script {Name} on {Server} reused the PREVIOUS execution's rows in {ElapsedMs}ms ({Rows} rows). No EXEC ran: ExecutionTest returned ToRun=0. The exported rows carry their own original CheckDate.",
                            config.Name, LogAnon.S(targetServer), stopwatch.ElapsedMilliseconds, result.RowsAffected);
                    else
                        _logger.LogInformation("Script {Name} executed successfully on {Server} in {ElapsedMs}ms ({Rows} rows)",
                            config.Name, LogAnon.S(targetServer), stopwatch.ElapsedMilliseconds, result.RowsAffected);
                }
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
                _logger.LogWarning("Script {Name} execution cancelled on {Server}", config.Name, LogAnon.S(targetServer));
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                result.ExecutionTime = stopwatch.Elapsed;
                // Compose, never overwrite: an earlier ExecutionParameters failure (already
                // in ErrorMessage) or a failed install batch is the root cause; this
                // exception is typically only its downstream symptom (e.g. "Invalid object
                // name '<output table>'" after the proc never installed/ran).
                var parts = new List<string>();
                if (batchWarnings.Count > 0)
                    parts.Add($"Install batch failed ({batchWarnings.Count}x, first): {batchWarnings[0]}");
                if (!string.IsNullOrEmpty(result.ErrorMessage))
                    parts.Add(result.ErrorMessage);
                parts.Add(ex.Message);
                result.ErrorMessage = string.Join(" | ", parts);
                _logger.LogError(ex, "Error executing script {Name} on {Server}", config.Name, LogAnon.S(targetServer));
            }
            finally
            {
                // Return the rented connection to the pool, or dispose the direct one.
                if (fromPool)
                    _pool!.ReturnConnection(dbConnection, masterConnectionString);
                else
                    dbConnection.Dispose();
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

        /// <summary>
        /// Whether <see cref="ExportToCsv"/> puts its own ServerName column in front of the result's
        /// own columns.
        ///
        /// <para>The writer has always prefixed every row with the CONNECTION TARGET - the string the
        /// operator typed, ".\NEW2022" - in a column called ServerName, so a CSV records which server
        /// it came from. That is right for a result set that does not name its own server and wrong
        /// for one that does. sp_Blitz's output table HAS a ServerName column (the name the instance
        /// reports about itself, "MSI\NEW2022"), so the file carried TWO columns called ServerName and
        /// nothing downstream could resolve either by name. The Export Pack's sp_Blitz descriptor
        /// refused the file outright, and every pack built between 2026-07-23 and 2026-08-24 shipped
        /// WITHOUT sp_Blitz because of it (proved live, DECISIONS 2026-08-24).</para>
        ///
        /// <para>So the column is prepended only when the result does not already carry one. Census
        /// over the shipped Config/script-configurations.json, pinned by
        /// <c>BlitzCsvContractTests.sp_Blitz_is_the_only_shipped_entry_whose_output_query_returns_a_ServerName_column</c>:
        /// sp_Blitz is the only shipped entry whose STATIC select list returns a ServerName column -
        /// SQLDBA.ORG.sp_triage and stpChecklist_Seguranca name theirs SQLInstance, sp_ineachdb and
        /// Check_BP_Servers ship no output query at all. sp_PerfCheck is EXEC-only and has no static
        /// select list for this census to read; its result columns are a runtime fact, not something
        /// this file can examine (<c>BlitzCsvContractTests.The_EXEC_only_entry_has_no_select_list_by_design_not_by_parse_failure</c>
        /// names that case explicitly rather than letting it look like the others).
        /// <b>The census was hollow for months.</b> Until 2026-08-24 the select-list parser required a
        /// literal single space on each side of "FROM" and silently returned empty for anything laid
        /// out across lines - which is exactly SQLDBA.ORG.sp_triage's shape, so its select list was
        /// never actually read and the "sp_Blitz is the only carrier" claim was true by construction of
        /// an empty result, not by measurement. Fixed the same day
        /// (<c>BlitzCsvContractTests.IsFromKeywordAt</c>, whole-word match instead of a literal
        /// six-character substring); mutating sp_triage's outer select list to add a ServerName column
        /// now turns the census test red, and
        /// <c>BlitzCsvContractTests.At_least_three_shipped_entries_produce_a_parsed_select_list</c>
        /// guards against the parser going quietly back to returning empty for everything. So exactly
        /// one file's shape changes and no other's. A user-added script whose own query returns
        /// ServerName now keeps its value instead of gaining a duplicate column it could not be read
        /// by.</para>
        ///
        /// <para>The rule lives in code and not in the config on purpose: a config property would
        /// have to reach every installed config to take effect, and installer/SQLTriage.iss ships
        /// that file <c>onlyifdoesntexist</c>. Binaries always arrive.</para>
        ///
        /// <para>Internal (InternalsVisibleTo SQLTriage.Tests) so the decision is measured on its own
        /// rather than only through a writer that needs a whole result set to answer it.</para>
        /// </summary>
        internal static bool ShouldPrependServerName(IEnumerable<string>? resultColumns)
            => resultColumns is null
               || !resultColumns.Any(c => string.Equals(c, "ServerName", StringComparison.OrdinalIgnoreCase));

        public string ExportToCsv(ScriptExecutionResult result)
        {
            if (result.Results == null || result.Results.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();

            // Headers come from the first row; the connection target goes in front only when the
            // result does not already name its server (see ShouldPrependServerName).
            var columns = result.Results.First().Keys.ToList();
            var prependServerName = ShouldPrependServerName(columns);
            var headers = prependServerName
                ? new List<string> { "ServerName" }.Concat(columns).ToList()
                : columns;
            sb.AppendLine(string.Join(",", headers.Select(h => $"\"{h}\"")));

            foreach (var row in result.Results)
            {
                var values = new List<string>();
                if (prependServerName)
                    values.Add($"\"{result.ServerName}\"");

                foreach (var h in columns)
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

        /// <summary>
        /// Whether SqlQueryForOutput may run at all.
        ///
        /// <list type="bullet">
        /// <item>ExecutionParameters FAILED -&gt; never. Whatever the output table holds is NOT this
        /// run's, and for any config whose output query selects the latest persisted rows - the
        /// shipped sp_Blitz entry does, WHERE CheckDate = (SELECT MAX(CheckDate)) - running it would
        /// return the PREVIOUS run's audit and export it under today's file name.</item>
        /// <item>ExecutionParameters ran, or was empty -&gt; always.</item>
        /// <item>ExecutionParameters was skipped because ExecutionTest returned ToRun=0 -&gt; only when
        /// the config exports to CSV, which is the deliberate "load the previous execution" path.</item>
        /// </list>
        ///
        /// <para>Internal (InternalsVisibleTo SQLTriage.Tests) rather than inline, so the first
        /// clause is measured in CI. The live end-to-end proof needs a real SQL instance and is
        /// therefore skipped there; a rule that only a skipped test measures is not measured.</para>
        /// </summary>
        internal static bool ShouldRunOutputQuery(bool execParamsFailed, bool shouldRunExecParams, bool exportToCsv)
            => !execParamsFailed && (shouldRunExecParams || exportToCsv);

        /// <summary>
        /// Reads the rows of the result set the config asks to export: advance, then read, in one
        /// member, because the call site must not be able to do the second without the first.
        ///
        /// <para>WHY ONE MEMBER. The advance started life as its own call in ExecuteScriptAsync
        /// with its own truth table. Deleting that call left the whole suite green, because a
        /// tested function is not a used function: the rows still came back, they were just the
        /// wrong set. That is the bypass shape ScriptConfigurationMigratorTests was written for
        /// after the fence lane hit it. Folded together, the unit test below exercises the same
        /// code path the runner takes, so removing the advance turns CI red without a live
        /// instance.</para>
        ///
        /// <para>WHAT THE FOLD DOES NOT CLOSE. Deleting the CALL to this member from
        /// ExecuteScriptAsync is invisible to any unit test of the member itself, and it stayed
        /// green across the whole suite when it was measured. RuleCallSiteTests reads the call site
        /// as text and asserts result.Results is assigned from here exactly once;
        /// PerfCheckLiveSmokeTests catches the same deletion end to end, and is skipped unless an
        /// instance is named.</para>
        /// </summary>
        internal static async Task<List<Dictionary<string, object>>> ReadOutputRowsAsync(
            DbDataReader reader,
            int outputResultSetIndex,
            string scriptName,
            CancellationToken cancellationToken)
        {
            await AdvanceToOutputResultSetAsync(reader, outputResultSetIndex, scriptName, cancellationToken);

            var rows = new List<Dictionary<string, object>>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, object>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null! : reader.GetValue(i);
                }
                rows.Add(row);
            }

            return rows;
        }

        /// <summary>
        /// Moves <paramref name="reader"/> onto the result set the config asks to export, and
        /// throws when that set is not there.
        ///
        /// <para>THE RULE IS FAIL CLOSED, and the reason is the house defect class: a wrong answer
        /// that looks like a right one. If the output query comes back with fewer sets than the
        /// entry claims, the only alternative to throwing is to export whatever set the reader
        /// happens to be sitting on, which for sp_PerfCheck is a two-column server banner exported
        /// under the name of a performance audit. That CSV would be shipped, parsed and filed as
        /// this run's findings. The exception is caught by ExecuteScriptAsync's generic handler,
        /// which composes the message and leaves Success = false, so Full Audit raises it as an
        /// Error rather than a Warning.</para>
        ///
        /// <para>Static and internal so the decision is unit-tested as a truth table in CI. The
        /// live proof needs a real instance and a real two-set procedure, which only
        /// PerfCheckLiveSmokeTests has.</para>
        /// </summary>
        internal static async Task AdvanceToOutputResultSetAsync(
            DbDataReader reader,
            int outputResultSetIndex,
            string scriptName,
            CancellationToken cancellationToken)
        {
            if (outputResultSetIndex < 0)
            {
                throw new InvalidOperationException(
                    $"'{scriptName}' has OutputResultSetIndex {outputResultSetIndex}. Result sets are "
                    + "counted from 0 and a negative index names no set at all, so there is nothing "
                    + "safe to export.");
            }

            for (int advanced = 0; advanced < outputResultSetIndex; advanced++)
            {
                if (!await reader.NextResultAsync(cancellationToken))
                {
                    throw new InvalidOperationException(
                        $"'{scriptName}' is configured to export result set {outputResultSetIndex} of "
                        + $"its output query, but the query returned only {advanced + 1}. Exporting an "
                        + "earlier set instead would ship the wrong columns under this script's name, "
                        + "so the run fails here rather than producing a plausible CSV.");
                }
            }
        }

        /// <summary>
        /// Whether Full Audit should raise a "No-Rows" issue for a script that succeeded and
        /// returned nothing.
        ///
        /// <para>Zero rows means two opposite things depending on the script. For sp_Blitz and
        /// sp_triage the output query reads a table the run has just written, so an empty result is
        /// a broken export and the operator has to know. For sp_PerfCheck the rows ARE the problems
        /// found, so an empty result is a clean bill of health, and flagging it every run would
        /// train the operator to dismiss this modal unread. <see
        /// cref="Models.ScriptConfiguration.EmptyResultIsNormal"/> is which of the two a given entry
        /// is; it defaults to false, so nothing that predates it changes behaviour.</para>
        ///
        /// <para>Here rather than inline in Pages/FullAudit.razor so CI measures the rule. A
        /// decision that only exists inside a Blazor component method is a decision no test reaches.
        /// That buys the rule and not its use: reverting the call site to the old inline predicate
        /// left the whole suite green, and nothing renders Full Audit in CI or in the live harness,
        /// so the operator would have been told every clean server was broken with no signal
        /// anywhere. RuleCallSiteTests reads Pages/FullAudit.razor as text and asserts the guard on
        /// the "No-Rows" issue is this member. A text tripwire proves the call is written, not that
        /// it runs, and it is the only instrument that reaches this call site at all.</para>
        /// </summary>
        internal static bool ShouldRaiseNoRowsIssue(ScriptConfiguration? config, int rowsAffected)
            => config is not null
               && config.ExportToCsv
               && !config.EmptyResultIsNormal
               && rowsAffected == 0;

        /// <summary>
        /// The given path if nothing is there, else the same name with " (2)", " (3)" and so on before
        /// the extension. Never returns a path that exists, so an export cannot destroy an earlier one.
        ///
        /// <para>Internal (InternalsVisibleTo SQLTriage.Tests) so the no-overwrite rule is measured on
        /// its own. It gives up after 99 attempts and returns a name carrying a millisecond suffix,
        /// which cannot collide in practice and still cannot silently overwrite: the caller writes it
        /// only after this returns.</para>
        /// </summary>
        internal static string NextFreeExportPath(string desiredPath, out bool collided)
        {
            collided = false;
            if (!File.Exists(desiredPath)) return desiredPath;

            collided = true;
            var dir  = Path.GetDirectoryName(desiredPath) ?? "";
            var stem = Path.GetFileNameWithoutExtension(desiredPath);
            var ext  = Path.GetExtension(desiredPath);
            for (var n = 2; n <= 99; n++)
            {
                var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
                if (!File.Exists(candidate)) return candidate;
            }
            return Path.Combine(dir, $"{stem}_{DateTime.Now:fffffff}{ext}");
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

            // CSV only — the unconditional parallel .json twin was retired (Phase D / D3,
            // decision 2026-06-30): nothing in the app or pipeline consumed it, and it doubled
            // the PII-bearing files in output/. (FullAudit's AutoExportAuditJson is separate:
            // an opt-in user setting, off by default.)
            //
            // ⚠ THE STAMP IS THIS EXPORT'S, NOT THE PROCESS'S. It used to be a field initialised at
            // construction, so every export a runner instance ever made carried the timestamp of when
            // the runner was BUILT. Two exports of one script against one server in one process
            // therefore resolved to one file name and the second silently destroyed the first: proved
            // live, two exports produced ONE file, the survivor held run two, no log line, no warning.
            // A long-lived host makes that worse the longer it runs, because the stamp keeps aging
            // while the data keeps changing.
            var csvFileName = Path.Combine(outputFolder,
                $"{sanitizedServerName}_{sanitizedScriptName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

            // Same second, same server, same script: still a collision, and an overwrite here would be
            // the same silent destruction wearing a shorter window. Take the next free name and say so.
            csvFileName = NextFreeExportPath(csvFileName, out var collided);
            if (collided)
                _logger.LogWarning("An export for {Script} on {Server} already existed for this second; writing {FileName} instead of overwriting it.",
                    result.ScriptName, LogAnon.S(result.ServerName), Path.GetFileName(csvFileName));

            try
            {
                // Write CSV directly - no need to re-read the file afterward
                File.WriteAllText(csvFileName, csv, Encoding.UTF8);
                _logger.LogInformation("CSV exported to {FileName}", csvFileName);

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
