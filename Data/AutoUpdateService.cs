/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SqlHealthAssessment.Data
{
    public class UpdateInfo
    {
        public string Version { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string ReleaseNotes { get; set; } = "";
        public DateTime ReleasedAt { get; set; }
        public bool IsRequired { get; set; }
    }

    public class AutoUpdateService
    {
        private readonly ILogger<AutoUpdateService> _logger;
        private readonly HttpClient _httpClient;
        private string _currentVersion = "1.0.0";
        private int _buildNumber = 0;
        private string _updateCheckUrl = "https://api.github.com/repos/SQLAdrian/SqlHealthAssessment/releases/latest";

        /// <summary>Path to the staged update ZIP, ready to apply on exit.</summary>
        public string? StagedUpdatePath { get; private set; }

        /// <summary>True if an update has been downloaded and is waiting to be applied.</summary>
        public bool HasStagedUpdate => !string.IsNullOrEmpty(StagedUpdatePath) && File.Exists(StagedUpdatePath);

        public AutoUpdateService(ILogger<AutoUpdateService> logger)
        {
            _logger = logger;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "SqlHealthAssessment");
            LoadVersionInfo();
        }

        private void LoadVersionInfo()
        {
            try
            {
                var versionPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "version.json");
                if (!File.Exists(versionPath))
                    versionPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", "version.json");

                if (File.Exists(versionPath))
                {
                    var json = File.ReadAllText(versionPath);
                    var doc = JsonDocument.Parse(json);
                    _currentVersion = doc.RootElement.GetProperty("version").GetString() ?? "1.0.0";
                    if (doc.RootElement.TryGetProperty("buildNumber", out var buildNum))
                        _buildNumber = buildNum.GetInt32();
                    if (doc.RootElement.TryGetProperty("updateCheckUrl", out var url))
                        _updateCheckUrl = url.GetString() ?? _updateCheckUrl;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load version.json, using defaults");
            }
        }

        public async Task<(bool Available, UpdateInfo? Info)> CheckForUpdatesAsync()
        {
            try
            {
                _logger.LogInformation("Checking for updates at {Url}", _updateCheckUrl);
                var response = await _httpClient.GetStringAsync(_updateCheckUrl);
                var release = JsonDocument.Parse(response).RootElement;

                var tagName = release.GetProperty("tag_name").GetString() ?? "";
                // Strip common prefixes: "v1.2.3", "Release-1.2.3", "Release"
                var latestVersion = tagName
                    .Replace("Release-", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("Release", "", StringComparison.OrdinalIgnoreCase)
                    .TrimStart('v', '-', ' ');

                var downloadUrl = "";

                if (release.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.GetProperty("name").GetString() ?? "";
                        // Prefer .zip over .exe
                        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                            break;
                        }
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(downloadUrl))
                        {
                            downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                        }
                    }
                }

                if (IsNewerVersion(latestVersion, _currentVersion))
                {
                    var updateInfo = new UpdateInfo
                    {
                        Version = latestVersion,
                        DownloadUrl = downloadUrl,
                        ReleaseNotes = release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                        ReleasedAt = release.TryGetProperty("published_at", out var pubAt) ? pubAt.GetDateTime() : DateTime.Now
                    };

                    _logger.LogInformation("Update available: v{Version} (current: v{Current})", latestVersion, _currentVersion);
                    return (true, updateInfo);
                }

                _logger.LogInformation("No update available. Current: v{Current}, Latest: v{Latest}", _currentVersion, latestVersion);
                return (false, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check for updates");
                return (false, null);
            }
        }

        /// <summary>
        /// Downloads the update ZIP to a staging folder. Returns true if successful.
        /// </summary>
        public async Task<bool> DownloadUpdateAsync(string downloadUrl, IProgress<int>? progress = null)
        {
            try
            {
                var stagingDir = Path.Combine(AppContext.BaseDirectory, "update-staging");
                Directory.CreateDirectory(stagingDir);

                var zipPath = Path.Combine(stagingDir, "update.zip");
                _logger.LogInformation("Downloading update from {Url} to {Path}", downloadUrl, zipPath);

                using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1;
                long downloadedBytes = 0;

                await using var contentStream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                var buffer = new byte[8192];
                int bytesRead;
                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    downloadedBytes += bytesRead;

                    if (totalBytes > 0)
                    {
                        var pct = (int)(downloadedBytes * 100 / totalBytes);
                        progress?.Report(pct);
                    }
                }

                progress?.Report(100);
                StagedUpdatePath = zipPath;
                _logger.LogInformation("Update downloaded successfully: {Size:N0} bytes", downloadedBytes);

                // Write a small script that will apply the update after the app exits
                WriteUpdateApplierScript(stagingDir);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download update");
                return false;
            }
        }

        /// <summary>
        /// Creates a batch script that extracts the update ZIP over the app directory.
        /// Called on app exit if an update is staged.
        /// </summary>
        public void ApplyUpdateOnExit()
        {
            if (!HasStagedUpdate) return;

            try
            {
                var scriptPath = Path.Combine(Path.GetDirectoryName(StagedUpdatePath!)!, "apply-update.cmd");
                if (File.Exists(scriptPath))
                {
                    _logger.LogInformation("Launching update applier: {Script}", scriptPath);
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c \"{scriptPath}\"",
                        UseShellExecute = true,
                        CreateNoWindow = false,
                        WindowStyle = ProcessWindowStyle.Minimized
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to launch update applier");
            }
        }

        /// <summary>
        /// Config files that contain user data and must survive updates.
        /// These are backed up before extraction and restored afterwards.
        /// </summary>
        private static readonly string[] ProtectedConfigFiles = new[]
        {
            "config\\server-connections.json",
            "config\\alert-definitions.json",
            "config\\notification-channels.json",
            "config\\scheduled-tasks.json",
            "config\\user-settings.json",
            "config\\appsettings.json",
            "config\\dashboard-config.json",
        };

        private void WriteUpdateApplierScript(string stagingDir)
        {
            var appDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            var zipPath = Path.Combine(stagingDir, "update.zip");
            var backupDir = Path.Combine(stagingDir, "config-backup");
            var scriptPath = Path.Combine(stagingDir, "apply-update.cmd");
            var exeName = "SqlHealthAssessment.exe";

            // Build backup/restore lines for each protected config file
            var backupLines = string.Join("\r\n", ProtectedConfigFiles.Select(f =>
                $"if exist \"{appDir}\\{f}\" copy /Y \"{appDir}\\{f}\" \"{backupDir}\\{Path.GetFileName(f)}\" >nul"));
            var restoreLines = string.Join("\r\n", ProtectedConfigFiles.Select(f =>
                $"if exist \"{backupDir}\\{Path.GetFileName(f)}\" copy /Y \"{backupDir}\\{Path.GetFileName(f)}\" \"{appDir}\\{f}\" >nul"));

            // Batch script: wait for app to close, backup configs, extract ZIP, restore configs, restart, clean up
            var script = $@"@echo off
echo SQL Health Assessment — Applying Update...
echo Waiting for application to close...
:wait
timeout /t 2 /nobreak >nul
tasklist /FI ""IMAGENAME eq {exeName}"" 2>NUL | find /I ""{exeName}"" >NUL
if not errorlevel 1 goto wait

echo Backing up user configuration...
if not exist ""{backupDir}"" mkdir ""{backupDir}""
{backupLines}

echo Extracting update...
powershell -NoProfile -Command ""Expand-Archive -Path '{zipPath}' -DestinationPath '{appDir}' -Force""
if errorlevel 1 (
    echo ERROR: Failed to extract update.
    pause
    exit /b 1
)

echo Restoring user configuration...
{restoreLines}

echo Update applied successfully.
echo Cleaning up...
del ""{zipPath}"" 2>nul
rmdir /s /q ""{backupDir}"" 2>nul

echo Restarting application...
start """" ""{Path.Combine(appDir, exeName)}""
echo Done.
timeout /t 3 /nobreak >nul
del ""{scriptPath}"" 2>nul
";
            File.WriteAllText(scriptPath, script);
            _logger.LogInformation("Update applier script written to {Path}", scriptPath);
        }

        private bool IsNewerVersion(string latest, string current)
        {
            if (string.IsNullOrWhiteSpace(latest)) return false;

            var latestParts = latest.Split('.');
            var currentParts = current.Split('.');

            for (int i = 0; i < Math.Min(latestParts.Length, currentParts.Length); i++)
            {
                if (int.TryParse(latestParts[i], out var latestNum) &&
                    int.TryParse(currentParts[i], out var currentNum))
                {
                    if (latestNum > currentNum) return true;
                    if (latestNum < currentNum) return false;
                }
            }

            return latestParts.Length > currentParts.Length;
        }

        public string GetCurrentVersion() => _buildNumber > 0 ? $"{_currentVersion}.{_buildNumber}" : _currentVersion;

        // ──────────────── Script Updates ────────────────

        /// <summary>
        /// Checks the GitHub repo's scripts/ folder for updated .sql files.
        /// Compares remote SHA against local file SHA to detect changes.
        /// Returns a list of files that differ or are missing locally.
        /// </summary>
        public async Task<List<ScriptUpdateInfo>> CheckForScriptUpdatesAsync()
        {
            var results = new List<ScriptUpdateInfo>();
            try
            {
                // Derive the Contents API URL from the releases URL
                // e.g. https://api.github.com/repos/SQLAdrian/SqlHealthAssessment/releases/latest
                //    → https://api.github.com/repos/SQLAdrian/SqlHealthAssessment/contents/scripts
                var repoBase = _updateCheckUrl;
                var releasesIdx = repoBase.IndexOf("/releases/", StringComparison.OrdinalIgnoreCase);
                if (releasesIdx < 0)
                {
                    _logger.LogWarning("Cannot derive repo URL from updateCheckUrl: {Url}", _updateCheckUrl);
                    return results;
                }
                var contentsUrl = repoBase[..releasesIdx] + "/contents/scripts";

                _logger.LogInformation("Checking for script updates at {Url}", contentsUrl);
                var response = await _httpClient.GetStringAsync(contentsUrl);
                var files = JsonDocument.Parse(response).RootElement;

                var localScriptsDir = Path.Combine(AppContext.BaseDirectory, "scripts");
                Directory.CreateDirectory(localScriptsDir);

                foreach (var file in files.EnumerateArray())
                {
                    var name = file.GetProperty("name").GetString() ?? "";
                    if (!name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var remoteSha = file.GetProperty("sha").GetString() ?? "";
                    var downloadUrl = file.GetProperty("download_url").GetString() ?? "";
                    var remoteSize = file.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;

                    var localPath = Path.Combine(localScriptsDir, name);
                    var localExists = File.Exists(localPath);
                    var localSha = localExists ? ComputeGitBlobSha(localPath) : "";

                    if (!localExists || !string.Equals(remoteSha, localSha, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new ScriptUpdateInfo
                        {
                            FileName = name,
                            DownloadUrl = downloadUrl,
                            RemoteSha = remoteSha,
                            LocalExists = localExists,
                            RemoteSize = remoteSize
                        });
                    }
                }

                _logger.LogInformation("Script update check: {Updated} of {Total} files need updating",
                    results.Count, files.GetArrayLength());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check for script updates");
            }
            return results;
        }

        /// <summary>
        /// Downloads updated script files to the local scripts/ folder.
        /// </summary>
        public async Task<(int Succeeded, int Failed)> DownloadScriptUpdatesAsync(
            List<ScriptUpdateInfo> updates, IProgress<int>? progress = null)
        {
            var localScriptsDir = Path.Combine(AppContext.BaseDirectory, "scripts");
            Directory.CreateDirectory(localScriptsDir);

            int succeeded = 0, failed = 0;
            for (int i = 0; i < updates.Count; i++)
            {
                var update = updates[i];
                try
                {
                    var content = await _httpClient.GetStringAsync(update.DownloadUrl);
                    var localPath = Path.Combine(localScriptsDir, update.FileName);
                    await File.WriteAllTextAsync(localPath, content);
                    succeeded++;
                    _logger.LogInformation("Updated script: {FileName}", update.FileName);
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogError(ex, "Failed to download script: {FileName}", update.FileName);
                }

                progress?.Report((i + 1) * 100 / updates.Count);
            }

            return (succeeded, failed);
        }

        /// <summary>
        /// Computes the Git blob SHA1 for a local file (same algorithm GitHub uses).
        /// Format: SHA1("blob {size}\0{content}")
        /// </summary>
        private static string ComputeGitBlobSha(string filePath)
        {
            var content = File.ReadAllBytes(filePath);
            var header = System.Text.Encoding.UTF8.GetBytes($"blob {content.Length}\0");
            var combined = new byte[header.Length + content.Length];
            Buffer.BlockCopy(header, 0, combined, 0, header.Length);
            Buffer.BlockCopy(content, 0, combined, header.Length, content.Length);

            using var sha1 = System.Security.Cryptography.SHA1.Create();
            var hash = sha1.ComputeHash(combined);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }

    public class ScriptUpdateInfo
    {
        public string FileName { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string RemoteSha { get; set; } = "";
        public bool LocalExists { get; set; }
        public long RemoteSize { get; set; }
    }
}
