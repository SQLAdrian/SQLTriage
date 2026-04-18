/* In the name of God, the Merciful, the Compassionate */

/*
 * Ported from PerformanceMonitor-main/InstallerGui/Services/InstallationService.cs
 * Copyright (c) 2026 Erik Darling, Darling Data LLC — MIT License
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SQLTriage.Data.Services
{
    public class PMInstallationProgress
    {
        public string Message { get; set; } = string.Empty;
        public string Status { get; set; } = "Info"; // Info, Success, Error, Warning
        public int? CurrentStep { get; set; }
        public int? TotalSteps { get; set; }
        public int? ProgressPercent { get; set; }
    }

    public class PMServerInfo
    {
        public string ServerName { get; set; } = string.Empty;
        public string SqlServerVersion { get; set; } = string.Empty;
        public string SqlServerEdition { get; set; } = string.Empty;
        public bool IsConnected { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class PMInstallationResult
    {
        public bool Success { get; set; }
        public int FilesSucceeded { get; set; }
        public int FilesFailed { get; set; }
        public List<(string FileName, string ErrorMessage)> Errors { get; } = new();
        public List<(string Message, string Status)> LogMessages { get; } = new();
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public string? ReportPath { get; set; }
    }

    public class PMInstallationService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private bool _disposed;

        private static readonly Regex SqlFilePattern = new(
            @"^\d{2}[a-z]?_.*\.sql$",
            RegexOptions.Compiled);

        private static readonly Regex SqlCmdDirectivePattern = new(
            @"^:r\s+.*$",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex GoBatchSplitter = new(
            @"^\s*GO\s*(?:--[^\r\n]*)?\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

        private static readonly char[] NewLineChars = { '\r', '\n' };

        public PMInstallationService()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        public static string BuildConnectionString(
            string server,
            bool useWindowsAuth,
            string? username = null,
            string? password = null,
            string encryption = "Mandatory",
            bool trustCertificate = false)
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = "master",
                TrustServerCertificate = trustCertificate,
                IntegratedSecurity = useWindowsAuth,
                ApplicationName = "SQLTriage"
            };

            builder.Encrypt = encryption switch
            {
                "Strict" => SqlConnectionEncryptOption.Strict,
                "Optional" => SqlConnectionEncryptOption.Optional,
                _ => SqlConnectionEncryptOption.Mandatory
            };

            if (!useWindowsAuth)
            {
                builder.UserID = username;
                builder.Password = password;
            }

            return builder.ConnectionString;
        }

        public static async Task<PMServerInfo> TestConnectionAsync(
            string connectionString,
            CancellationToken cancellationToken = default)
        {
            var info = new PMServerInfo();
            try
            {
                using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
                info.IsConnected = true;

                using var cmd = new SqlCommand(
                    "SELECT @@VERSION, SERVERPROPERTY('Edition'), @@SERVERNAME;", conn);
                using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    info.SqlServerVersion = reader.GetString(0);
                    info.SqlServerEdition = reader.GetString(1);
                    info.ServerName = reader.GetString(2);
                }
            }
            catch (Exception ex)
            {
                info.IsConnected = false;
                info.ErrorMessage = ex.Message;
                if (ex.InnerException != null)
                    info.ErrorMessage += $"\n{ex.InnerException.Message}";
            }
            return info;
        }

        /// <summary>
        /// Looks for install SQL files in Deploy/PerformanceMonitor_db/install/ under the app base directory.
        /// Returns (sqlDirectory, monitorRootDirectory, orderedSqlFiles).
        /// </summary>
        public static (string? SqlDirectory, string? MonitorRootDir, List<string> SqlFiles) FindInstallationFiles()
        {
            string appBase = AppDomain.CurrentDomain.BaseDirectory;

            string pmRoot = Path.Combine(appBase, "Deploy", "PerformanceMonitor_db");

            // Primary: <appBase>/Deploy/PerformanceMonitor_db/install/
            string primaryInstall = Path.Combine(pmRoot, "install");
            if (Directory.Exists(primaryInstall))
            {
                var files = GetInstallFiles(primaryInstall);
                if (files.Count > 0)
                    return (primaryInstall, pmRoot, files);
            }

            // Secondary: <appBase>/Deploy/PerformanceMonitor_db/install/passed/
            // (scripts land here when installer marks them as already applied)
            string passedInstall = Path.Combine(pmRoot, "install", "passed");
            if (Directory.Exists(passedInstall))
            {
                var files = GetInstallFiles(passedInstall);
                if (files.Count > 0)
                    return (passedInstall, pmRoot, files);
            }

            // Fallback: <appBase>/Deploy/PerformanceMonitor_db/ (flat structure)
            if (Directory.Exists(pmRoot))
            {
                var files = GetInstallFiles(pmRoot);
                if (files.Count > 0)
                    return (pmRoot, pmRoot, files);
            }

            return (null, null, new List<string>());
        }

        private static List<string> GetInstallFiles(string directory) =>
            Directory.GetFiles(directory, "*.sql")
                .Where(f =>
                {
                    string name = Path.GetFileName(f);
                    return SqlFilePattern.IsMatch(name)
                        && !name.StartsWith("97_", StringComparison.Ordinal)
                        && !name.StartsWith("99_", StringComparison.Ordinal);
                })
                .OrderBy(f => Path.GetFileName(f))
                .ToList();

        public static async Task<string?> GetInstalledVersionAsync(
            string connectionString,
            CancellationToken cancellationToken = default)
        {
            try
            {
                using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

                using var dbCheck = new SqlCommand(
                    "SELECT database_id FROM sys.databases WHERE name = N'PerformanceMonitor';", conn);
                var dbExists = await dbCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (dbExists == null || dbExists == DBNull.Value) return null;

                using var tblCheck = new SqlCommand(
                    "USE PerformanceMonitor; SELECT OBJECT_ID(N'config.installation_history', N'U');", conn);
                var tblExists = await tblCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (tblExists == null || tblExists == DBNull.Value) return null;

                using var verCmd = new SqlCommand(@"
                    SELECT TOP 1 installer_version
                    FROM PerformanceMonitor.config.installation_history
                    WHERE installation_status = 'SUCCESS'
                    ORDER BY installation_date DESC;", conn);
                var ver = await verCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return (ver != null && ver != DBNull.Value) ? ver.ToString() : null;
            }
            catch { return null; }
        }

        public static List<PMUpgradeInfo> GetApplicableUpgrades(
            string monitorRootDirectory,
            string? currentVersion,
            string targetVersion)
        {
            var upgrades = new List<PMUpgradeInfo>();
            if (currentVersion == null) return upgrades;

            string upgradesDir = Path.Combine(monitorRootDirectory, "upgrades");
            if (!Directory.Exists(upgradesDir)) return upgrades;

            if (!Version.TryParse(currentVersion, out var curRaw)) return upgrades;
            if (!Version.TryParse(targetVersion, out var tgtRaw)) return upgrades;

            var cur = new Version(curRaw.Major, curRaw.Minor, Math.Max(0, curRaw.Build));
            var tgt = new Version(tgtRaw.Major, tgtRaw.Minor, Math.Max(0, tgtRaw.Build));

            return Directory.GetDirectories(upgradesDir)
                .Select(d => new PMUpgradeInfo { Path = d, FolderName = Path.GetFileName(d) })
                .Where(x => x.FolderName.Contains("-to-", StringComparison.Ordinal))
                .Select(x =>
                {
                    var parts = x.FolderName.Split("-to-");
                    x.FromVersion = Version.TryParse(parts[0], out var f) ? f : null;
                    x.ToVersion = parts.Length > 1 && Version.TryParse(parts[1], out var t) ? t : null;
                    return x;
                })
                .Where(x => x.FromVersion != null && x.ToVersion != null)
                .Where(x => x.FromVersion >= cur && x.ToVersion <= tgt)
                .OrderBy(x => x.FromVersion)
                .Where(x => File.Exists(Path.Combine(x.Path, "upgrade.txt")))
                .ToList();
        }

        public static async Task CleanInstallAsync(
            string connectionString,
            IProgress<PMInstallationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            progress?.Report(new PMInstallationProgress { Message = "Performing clean install...", Status = "Info" });

            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            // Stop traces if they exist
            try
            {
                using var traceCmd = new SqlCommand(
                    "EXECUTE PerformanceMonitor.collect.trace_management_collector @action = 'STOP';", conn)
                { CommandTimeout = 60 };
                await traceCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                progress?.Report(new PMInstallationProgress { Message = "Stopped existing traces", Status = "Success" });
            }
            catch (SqlException) { /* DB or proc doesn't exist */ }

            const string cleanupSql = @"
USE msdb;
IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'PerformanceMonitor - Collection')
    EXECUTE msdb.dbo.sp_delete_job @job_name = N'PerformanceMonitor - Collection';
IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'PerformanceMonitor - Data Retention')
    EXECUTE msdb.dbo.sp_delete_job @job_name = N'PerformanceMonitor - Data Retention';
USE master;
IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'PerformanceMonitor')
BEGIN
    ALTER DATABASE PerformanceMonitor SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE PerformanceMonitor;
END;";

            using var cmd = new SqlCommand(cleanupSql, conn) { CommandTimeout = 60 };
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            progress?.Report(new PMInstallationProgress
            {
                Message = "Clean install completed (jobs and database removed)",
                Status = "Success"
            });
        }

        public static async Task<PMInstallationResult> ExecuteInstallationAsync(
            string connectionString,
            List<string> sqlFiles,
            bool cleanInstall,
            bool resetSchedule = false,
            IProgress<PMInstallationProgress>? progress = null,
            Func<Task>? preValidationAction = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(sqlFiles);

            var result = new PMInstallationResult { StartTime = DateTime.Now };

            if (cleanInstall)
            {
                try
                {
                    await CleanInstallAsync(connectionString, progress, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    progress?.Report(new PMInstallationProgress { Message = $"CLEAN INSTALL FAILED: {ex.Message}", Status = "Error" });
                    progress?.Report(new PMInstallationProgress { Message = "Installation aborted.", Status = "Error" });
                    result.EndTime = DateTime.Now;
                    result.Success = false;
                    result.FilesFailed = 1;
                    result.Errors.Add(("Clean Install", ex.Message));
                    return result;
                }
            }

            progress?.Report(new PMInstallationProgress
            {
                Message = "Starting installation...",
                Status = "Info",
                CurrentStep = 0,
                TotalSteps = sqlFiles.Count,
                ProgressPercent = 0
            });

            bool preValidationRan = false;

            for (int i = 0; i < sqlFiles.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string sqlFile = sqlFiles[i];
                string fileName = Path.GetFileName(sqlFile);

                if (!preValidationRan && preValidationAction != null
                    && fileName.StartsWith("98_", StringComparison.Ordinal))
                {
                    preValidationRan = true;
                    await preValidationAction().ConfigureAwait(false);
                }

                progress?.Report(new PMInstallationProgress
                {
                    Message = $"Executing {fileName}...",
                    Status = "Info",
                    CurrentStep = i + 1,
                    TotalSteps = sqlFiles.Count,
                    ProgressPercent = (int)(((i + 1) / (double)sqlFiles.Count) * 100)
                });

                try
                {
                    string sqlContent = await File.ReadAllTextAsync(sqlFile, cancellationToken).ConfigureAwait(false);

                    if (resetSchedule && fileName.StartsWith("04_", StringComparison.Ordinal))
                    {
                        sqlContent = "TRUNCATE TABLE [PerformanceMonitor].[config].[collection_schedule];\nGO\n" + sqlContent;
                        progress?.Report(new PMInstallationProgress { Message = "Resetting schedule to defaults...", Status = "Info" });
                    }

                    sqlContent = SqlCmdDirectivePattern.Replace(sqlContent, "");

                    using var conn = new SqlConnection(connectionString);
                    await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

                    string[] batches = GoBatchSplitter.Split(sqlContent);
                    int batchNum = 0;

                    foreach (string batch in batches)
                    {
                        string trimmed = batch.Trim();
                        if (string.IsNullOrWhiteSpace(trimmed)) continue;
                        batchNum++;

                        using var cmd = new SqlCommand(trimmed, conn) { CommandTimeout = 300 };
                        try
                        {
                            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (SqlException ex)
                        {
                            string preview = trimmed.Length > 500
                                ? trimmed[..500] + $"... [+{trimmed.Length - 500} chars]"
                                : trimmed;
                            throw new InvalidOperationException(
                                $"Batch {batchNum} failed:\n{preview}\n\nError: {ex.Message}", ex);
                        }
                    }

                    progress?.Report(new PMInstallationProgress
                    {
                        Message = $"{fileName} - Success",
                        Status = "Success",
                        CurrentStep = i + 1,
                        TotalSteps = sqlFiles.Count,
                        ProgressPercent = (int)(((i + 1) / (double)sqlFiles.Count) * 100)
                    });
                    result.FilesSucceeded++;
                }
                catch (Exception ex)
                {
                    progress?.Report(new PMInstallationProgress
                    {
                        Message = $"{fileName} - FAILED: {ex.Message}",
                        Status = "Error",
                        CurrentStep = i + 1,
                        TotalSteps = sqlFiles.Count
                    });
                    result.FilesFailed++;
                    result.Errors.Add((fileName, ex.Message));

                    if (fileName.StartsWith("01_", StringComparison.Ordinal) ||
                        fileName.StartsWith("02_", StringComparison.Ordinal) ||
                        fileName.StartsWith("03_", StringComparison.Ordinal))
                    {
                        progress?.Report(new PMInstallationProgress
                        { Message = "Critical file failed — aborting.", Status = "Error" });
                        break;
                    }
                }
            }

            result.EndTime = DateTime.Now;
            bool onlyQuerySnapshotsFailed = result.FilesFailed == 1
                && result.Errors.Any(e => e.FileName.Contains("query_snapshots", StringComparison.OrdinalIgnoreCase));
            result.Success = result.FilesFailed == 0 || onlyQuerySnapshotsFailed;
            return result;
        }

        public async Task<int> InstallDependenciesAsync(
            string connectionString,
            IProgress<PMInstallationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var deps = new List<(string Name, string Url)>
            {
                ("sp_WhoIsActive",
                 "https://raw.githubusercontent.com/amachanic/sp_whoisactive/refs/heads/master/sp_WhoIsActive.sql"),
                ("DarlingData (sp_HealthParser, sp_HumanEventsBlockViewer)",
                 "https://raw.githubusercontent.com/erikdarlingdata/DarlingData/main/Install-All/DarlingData.sql"),
                ("First Responder Kit (sp_BlitzLock)",
                 "https://raw.githubusercontent.com/BrentOzarULTD/SQL-Server-First-Responder-Kit/refs/heads/main/Install-All-Scripts.sql")
            };

            progress?.Report(new PMInstallationProgress { Message = "Installing community dependencies...", Status = "Info" });
            int successCount = 0;

            foreach (var (name, url) in deps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new PMInstallationProgress { Message = $"Downloading {name}...", Status = "Info" });

                try
                {
                    string sql = await DownloadWithRetryAsync(url, progress, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);

                    if (string.IsNullOrWhiteSpace(sql))
                    {
                        progress?.Report(new PMInstallationProgress { Message = $"{name} - FAILED (empty response)", Status = "Error" });
                        continue;
                    }

                    using var conn = new SqlConnection(connectionString);
                    await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

                    using (var useDb = new SqlCommand("USE PerformanceMonitor;", conn))
                        await useDb.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                    foreach (string batch in GoBatchSplitter.Split(sql))
                    {
                        string trimmed = batch.Trim();
                        if (string.IsNullOrWhiteSpace(trimmed)) continue;
                        using var cmd = new SqlCommand(trimmed, conn) { CommandTimeout = 120 };
                        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }

                    progress?.Report(new PMInstallationProgress { Message = $"{name} - Installed", Status = "Success" });
                    successCount++;
                }
                catch (HttpRequestException ex)
                {
                    progress?.Report(new PMInstallationProgress { Message = $"{name} - Download failed: {ex.Message}", Status = "Error" });
                }
                catch (Exception ex)
                {
                    progress?.Report(new PMInstallationProgress { Message = $"{name} - Failed: {ex.Message}", Status = "Error" });
                }
            }

            progress?.Report(new PMInstallationProgress
            {
                Message = $"Dependencies: {successCount}/{deps.Count} installed",
                Status = successCount == deps.Count ? "Success" : "Warning"
            });
            return successCount;
        }

        public static async Task<(int Succeeded, int Failed)> RunValidationAsync(
            string connectionString,
            IProgress<PMInstallationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            progress?.Report(new PMInstallationProgress { Message = "Running initial collection to validate...", Status = "Info" });

            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            using (var cmd = new SqlCommand(
                "EXECUTE PerformanceMonitor.collect.scheduled_master_collector @force_run_all = 1, @debug = 0;", conn)
            { CommandTimeout = 300 })
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            progress?.Report(new PMInstallationProgress { Message = "Master collector completed", Status = "Success" });

            int succeeded = 0, failed = 0;
            using (var cmd = new SqlCommand(@"
                SELECT
                    success_count = COUNT_BIG(DISTINCT CASE WHEN collection_status = 'SUCCESS' THEN collector_name END),
                    error_count   = SUM(CASE WHEN collection_status = 'ERROR' THEN 1 ELSE 0 END)
                FROM PerformanceMonitor.config.collection_log;", conn))
            using (var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    succeeded = r.IsDBNull(0) ? 0 : (int)r.GetInt64(0);
                    failed = r.IsDBNull(1) ? 0 : r.GetInt32(1);
                }
            }

            progress?.Report(new PMInstallationProgress
            {
                Message = $"Validation: {succeeded} collectors OK, {failed} failed",
                Status = failed == 0 ? "Success" : "Warning"
            });

            if (failed > 0)
            {
                using var cmd = new SqlCommand(@"
                    SELECT collector_name, error_message
                    FROM PerformanceMonitor.config.collection_log
                    WHERE collection_status = 'ERROR'
                    ORDER BY collection_time DESC;", conn);
                using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    string n = r["collector_name"]?.ToString() ?? "";
                    string e = r["error_message"] == DBNull.Value ? "(no message)" : r["error_message"]?.ToString() ?? "";
                    progress?.Report(new PMInstallationProgress { Message = $"  {n}: {e}", Status = "Error" });
                }
            }

            return (succeeded, failed);
        }

        public static async Task<bool> RunTroubleshootingAsync(
            string connectionString,
            string sqlDirectory,
            IProgress<PMInstallationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            bool hasErrors = false;
            try
            {
                string scriptPath = Path.Combine(sqlDirectory, "99_installer_troubleshooting.sql");
                if (!File.Exists(scriptPath))
                {
                    string? parent = Directory.GetParent(sqlDirectory)?.FullName;
                    if (parent != null)
                    {
                        string alt = Path.Combine(parent, "install", "99_installer_troubleshooting.sql");
                        if (File.Exists(alt)) scriptPath = alt;
                    }
                }

                if (!File.Exists(scriptPath))
                {
                    progress?.Report(new PMInstallationProgress
                    { Message = "Troubleshooting script not found.", Status = "Error" });
                    return false;
                }

                progress?.Report(new PMInstallationProgress { Message = "Running diagnostics...", Status = "Info" });

                string content = await File.ReadAllTextAsync(scriptPath, cancellationToken).ConfigureAwait(false);
                content = SqlCmdDirectivePattern.Replace(content, string.Empty);

                using var conn = new SqlConnection(connectionString);
                conn.InfoMessage += (_, e) =>
                {
                    string msg = e.Message;
                    string status = msg.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) ? "Error"
                                  : msg.Contains("[WARN]", StringComparison.OrdinalIgnoreCase) ? "Warning"
                                  : msg.Contains("[OK]", StringComparison.OrdinalIgnoreCase) ? "Success"
                                  : "Info";
                    if (status == "Error") hasErrors = true;
                    progress?.Report(new PMInstallationProgress { Message = msg, Status = status });
                };

                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

                foreach (string batch in GoBatchSplitter.Split(content))
                {
                    string trimmed = batch.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed)) continue;
                    cancellationToken.ThrowIfCancellationRequested();

                    using var cmd = new SqlCommand(trimmed, conn) { CommandTimeout = 120 };
                    try { await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
                    catch (SqlException ex)
                    {
                        progress?.Report(new PMInstallationProgress { Message = $"SQL Error: {ex.Message}", Status = "Error" });
                        hasErrors = true;
                    }

                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                }

                return !hasErrors;
            }
            catch (Exception ex)
            {
                progress?.Report(new PMInstallationProgress { Message = $"Diagnostics failed: {ex.Message}", Status = "Error" });
                return false;
            }
        }

        /// <summary>
        /// Runs every .sql file in every subfolder of the upgrades directory, ordered by folder name ASC
        /// then file name ASC within each folder. Does not rely on upgrade.txt manifests.
        /// </summary>
        public static async Task<(int TotalSuccess, int TotalFailed, int UpgradeCount)> ExecuteAllUpgradeScriptsAsync(
            string monitorRootDirectory,
            string connectionString,
            IProgress<PMInstallationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            string upgradesDir = Path.Combine(monitorRootDirectory, "upgrades");
            if (!Directory.Exists(upgradesDir))
            {
                progress?.Report(new PMInstallationProgress { Message = "No upgrades directory found.", Status = "Warning" });
                return (0, 0, 0);
            }

            var folders = Directory.GetDirectories(upgradesDir)
                .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (folders.Count == 0)
            {
                progress?.Report(new PMInstallationProgress { Message = "No upgrade folders found.", Status = "Info" });
                return (0, 0, 0);
            }

            progress?.Report(new PMInstallationProgress
            { Message = $"Found {folders.Count} upgrade folder(s) to apply", Status = "Info" });

            int totalSuccess = 0, totalFailed = 0;
            foreach (var folder in folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (s, f) = await ExecuteUpgradeFolderAsync(folder, connectionString, progress, cancellationToken)
                    .ConfigureAwait(false);
                totalSuccess += s;
                totalFailed += f;
            }

            return (totalSuccess, totalFailed, folders.Count);
        }

        private static async Task<(int, int)> ExecuteUpgradeFolderAsync(
            string upgradeFolder,
            string connectionString,
            IProgress<PMInstallationProgress>? progress,
            CancellationToken cancellationToken)
        {
            int success = 0, failed = 0;
            string upgradeName = Path.GetFileName(upgradeFolder);

            var sqlFiles = Directory.GetFiles(upgradeFolder, "*.sql")
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (sqlFiles.Count == 0)
            {
                progress?.Report(new PMInstallationProgress
                { Message = $"  {upgradeName}: no SQL files — skipped", Status = "Warning" });
                return (0, 0);
            }

            progress?.Report(new PMInstallationProgress
            { Message = $"Applying upgrade: {upgradeName} ({sqlFiles.Count} script(s))", Status = "Info" });

            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            foreach (var filePath in sqlFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string fileName = Path.GetFileName(filePath);

                try
                {
                    string sql = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
                    sql = SqlCmdDirectivePattern.Replace(sql, "");

                    int batchNum = 0;
                    foreach (string batch in GoBatchSplitter.Split(sql))
                    {
                        string trimmed = batch.Trim();
                        if (string.IsNullOrWhiteSpace(trimmed)) continue;
                        batchNum++;
                        using var cmd = new SqlCommand(trimmed, conn) { CommandTimeout = 300 };
                        try { await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
                        catch (SqlException ex)
                        {
                            string preview = trimmed.Length > 500 ? trimmed[..500] + "..." : trimmed;
                            throw new InvalidOperationException(
                                $"Batch {batchNum} failed:\n{preview}\n\nError: {ex.Message}", ex);
                        }
                    }

                    progress?.Report(new PMInstallationProgress { Message = $"  {fileName} - Success", Status = "Success" });
                    success++;
                }
                catch (Exception ex)
                {
                    progress?.Report(new PMInstallationProgress
                    { Message = $"  {fileName} - FAILED: {ex.Message}", Status = "Error" });
                    failed++;
                }
            }

            progress?.Report(new PMInstallationProgress
            {
                Message = $"Upgrade {upgradeName}: {success} OK, {failed} failed",
                Status = failed == 0 ? "Success" : "Warning"
            });

            return (success, failed);
        }

        public static async Task<(int TotalSuccess, int TotalFailed, int UpgradeCount)> ExecuteAllUpgradesAsync(
            string monitorRootDirectory,
            string connectionString,
            string? currentVersion,
            string targetVersion,
            IProgress<PMInstallationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            int totalSuccess = 0, totalFailed = 0;
            var upgrades = GetApplicableUpgrades(monitorRootDirectory, currentVersion, targetVersion);
            if (upgrades.Count == 0) return (0, 0, 0);

            progress?.Report(new PMInstallationProgress
            { Message = $"Found {upgrades.Count} upgrade(s) to apply", Status = "Info" });

            foreach (var upgrade in upgrades)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (s, f) = await ExecuteUpgradeFolderAsync(upgrade.Path, connectionString, progress, cancellationToken)
                    .ConfigureAwait(false);
                totalSuccess += s;
                totalFailed += f;
            }

            return (totalSuccess, totalFailed, upgrades.Count);
        }

        public static string GenerateSummaryReport(
            string serverName,
            string sqlServerVersion,
            string sqlServerEdition,
            string installerVersion,
            PMInstallationResult result)
        {
            ArgumentNullException.ThrowIfNull(result);
            var duration = result.EndTime - result.StartTime;
            string timestamp = result.StartTime.ToString("yyyyMMdd_HHmmss");
            string safeServer = serverName.Replace("\\", "_", StringComparison.Ordinal);
            string fileName = $"PerformanceMonitor_Install_{safeServer}_{timestamp}.txt";
            string reportPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fileName);

            var sb = new StringBuilder();
            sb.AppendLine("================================================================================");
            sb.AppendLine("Performance Monitor Installation Report");
            sb.AppendLine("================================================================================");
            sb.AppendLine();
            sb.AppendLine("INSTALLATION SUMMARY");
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine($"Status:        {(result.Success ? "SUCCESS" : "FAILED")}");
            sb.AppendLine($"Start Time:    {result.StartTime:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"End Time:      {result.EndTime:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Duration:      {duration.TotalSeconds:F1} seconds");
            sb.AppendLine($"Files OK:      {result.FilesSucceeded}");
            sb.AppendLine($"Files Failed:  {result.FilesFailed}");
            sb.AppendLine();
            sb.AppendLine("SERVER INFORMATION");
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine($"Server:        {serverName}");
            sb.AppendLine($"Edition:       {sqlServerEdition}");
            foreach (var line in sqlServerVersion.Split(NewLineChars, StringSplitOptions.RemoveEmptyEntries))
                sb.AppendLine($"  {line.Trim()}");
            sb.AppendLine();
            sb.AppendLine("INSTALLER");
            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine($"Version:       {installerVersion}");
            sb.AppendLine($"Machine:       {Environment.MachineName}");
            sb.AppendLine($"User:          {Environment.UserName}");
            sb.AppendLine();

            if (result.Errors.Count > 0)
            {
                sb.AppendLine("ERRORS");
                sb.AppendLine("--------------------------------------------------------------------------------");
                foreach (var (file, error) in result.Errors)
                {
                    sb.AppendLine($"File:  {file}");
                    sb.AppendLine($"Error: {(error.Length > 500 ? error[..500] + "..." : error)}");
                    sb.AppendLine();
                }
            }

            if (result.LogMessages.Count > 0)
            {
                sb.AppendLine("DETAILED LOG");
                sb.AppendLine("--------------------------------------------------------------------------------");
                foreach (var (message, status) in result.LogMessages)
                {
                    string prefix = status switch
                    {
                        "Success" => "[OK] ",
                        "Error" => "[ERROR] ",
                        "Warning" => "[WARN] ",
                        _ => ""
                    };
                    sb.AppendLine($"{prefix}{message}");
                }
            }

            sb.AppendLine("================================================================================");
            sb.AppendLine($"Copyright (c) {DateTime.Now.Year} Darling Data, LLC — MIT License");
            sb.AppendLine("================================================================================");

            File.WriteAllText(reportPath, sb.ToString());
            return reportPath;
        }

        private async Task<string> DownloadWithRetryAsync(
            string url,
            IProgress<PMInstallationProgress>? progress,
            int maxRetries = 3,
            CancellationToken cancellationToken = default)
        {
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try { return await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false); }
                catch (HttpRequestException) when (attempt < maxRetries)
                {
                    int delaySec = (int)Math.Pow(2, attempt);
                    progress?.Report(new PMInstallationProgress
                    { Message = $"Retrying in {delaySec}s ({attempt}/{maxRetries})...", Status = "Warning" });
                    await Task.Delay(delaySec * 1000, cancellationToken).ConfigureAwait(false);
                }
            }
            return await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _httpClient?.Dispose();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }
    }

    public class PMUpgradeInfo
    {
        public string Path { get; set; } = string.Empty;
        public string FolderName { get; set; } = string.Empty;
        public Version? FromVersion { get; set; }
        public Version? ToVersion { get; set; }
    }
}
