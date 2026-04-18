/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data
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
        private HttpClient _httpClient;
        private string _currentVersion = "1.0.0";
        private int _buildNumber = 0;
        private string _updateCheckUrl = "https://api.github.com/repos/SQLAdrian/SQLTriage/releases/latest";
        private string? _manualProxyUrl;

        /// <summary>Path to the staged update ZIP, ready to apply on exit.</summary>
        public string? StagedUpdatePath { get; private set; }

        /// <summary>True if an update has been downloaded and is waiting to be applied.</summary>
        public bool HasStagedUpdate => !string.IsNullOrEmpty(StagedUpdatePath) && File.Exists(StagedUpdatePath);

        /// <summary>Cached result from the last background update check. Null if not checked yet.</summary>
        public (bool Available, UpdateInfo? Info)? LastCheckResult { get; private set; }

        /// <summary>True if a newer version was found in the last background check.</summary>
        public bool IsUpdateAvailable => LastCheckResult?.Available == true;

        /// <summary>Human-readable error from the last failed update check. Null if last check succeeded.</summary>
        public string? LastCheckError { get; private set; }

        /// <summary>Fires on the thread pool when a background check finds a newer version.</summary>
        public event Action<UpdateInfo>? OnUpdateAvailable;

        public AutoUpdateService(ILogger<AutoUpdateService> logger)
        {
            _logger = logger;
            _httpClient = BuildHttpClient(null);
            LoadVersionInfo();
        }

        /// <summary>
        /// Sets (or clears) a manual proxy URL override. Rebuilds the HttpClient.
        /// Pass null or empty to revert to system proxy auto-detection.
        /// </summary>
        public void SetManualProxyUrl(string? proxyUrl)
        {
            _manualProxyUrl = string.IsNullOrWhiteSpace(proxyUrl) ? null : proxyUrl.Trim();
            _httpClient.Dispose();
            _httpClient = BuildHttpClient(_manualProxyUrl);
            _logger.LogInformation("Update proxy set to: {Proxy}", _manualProxyUrl ?? "(system default)");
        }

        private static HttpClient BuildHttpClient(string? manualProxyUrl)
        {
            var handler = new HttpClientHandler { UseProxy = true };

            if (!string.IsNullOrWhiteSpace(manualProxyUrl))
            {
                handler.Proxy = new WebProxy(manualProxyUrl, true);
            }
            // else: UseProxy=true + null Proxy = use system/IE proxy settings automatically

            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Add("User-Agent", "SQLTriage-SQLTriage");
            return client;
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
            LastCheckError = null;
            try
            {
                _logger.LogInformation("Checking for updates at {Url}", _updateCheckUrl);
                var response = await _httpClient.GetStringAsync(_updateCheckUrl);
                var release = JsonDocument.Parse(response).RootElement;

                var tagName = release.GetProperty("tag_name").GetString() ?? "";
                // Strip common prefixes: "v1.2.3", "Release-1.2.3", "Release"
                var latestVersionRaw = tagName
                    .Replace("Release-", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("Release", "", StringComparison.OrdinalIgnoreCase)
                    .TrimStart('v', '-', ' ');

                // Extract build number from tag suffix "-buildNNNN" or "-bNNNN" before stripping
                int latestBuild = 0;
                var buildMatch = System.Text.RegularExpressions.Regex.Match(
                    latestVersionRaw, @"-(?:build|b)(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (buildMatch.Success)
                    int.TryParse(buildMatch.Groups[1].Value, out latestBuild);

                // Strip build suffix so version comparison works on clean "0.85.2"
                var latestVersion = System.Text.RegularExpressions.Regex.Replace(
                    latestVersionRaw, @"[-_](?:build|b)\d+.*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

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

                if (IsNewerVersion(latestVersion, _currentVersion, latestBuild, _buildNumber))
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
            catch (HttpRequestException ex)
            {
                LastCheckError = BuildHttpError(ex);
                _logger.LogError(ex, "Failed to check for updates (HTTP): {Error}", LastCheckError);
                return (false, null);
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || !ex.CancellationToken.IsCancellationRequested)
            {
                LastCheckError = "Request timed out after 30 seconds. Check your network connection or configure a proxy.";
                _logger.LogError(ex, "Update check timed out");
                return (false, null);
            }
            catch (Exception ex)
            {
                LastCheckError = $"Unexpected error: {ex.Message}";
                _logger.LogError(ex, "Failed to check for updates");
                return (false, null);
            }
        }

        private static string BuildHttpError(HttpRequestException ex)
        {
            // Provide actionable detail based on the inner exception type
            var inner = ex.InnerException;
            if (inner is System.Net.Sockets.SocketException sock)
                return $"Network error ({sock.SocketErrorCode}): {sock.Message}. " +
                       "Possible causes: no internet access, DNS failure, or proxy required.";

            if (inner?.Message.Contains("proxy", StringComparison.OrdinalIgnoreCase) == true ||
                ex.Message.Contains("407", StringComparison.Ordinal))
                return "Proxy authentication required (HTTP 407). " +
                       "Configure a proxy URL in Settings → Updates.";

            if (ex.StatusCode == HttpStatusCode.Forbidden)
                return "GitHub API rate-limited (HTTP 403). Try again in a few minutes.";

            if (ex.StatusCode != null)
                return $"HTTP {(int)ex.StatusCode}: {ex.Message}";

            return ex.Message;
        }

        /// <summary>
        /// Starts a one-shot background update check. Fires OnUpdateAvailable if a newer version is found.
        /// Safe to call multiple times — subsequent calls are no-ops if a check is already cached.
        /// </summary>
        public void StartBackgroundCheck()
        {
            if (LastCheckResult.HasValue)
                return; // already checked this session

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(15)); // don't check immediately on startup
                try
                {
                    var result = await CheckForUpdatesAsync();
                    LastCheckResult = result;
                    if (result.Available && result.Info != null)
                        OnUpdateAvailable?.Invoke(result.Info);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Background update check failed");
                }
            });
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
        /// Stages an update from a ZIP file the user has already downloaded manually.
        /// Copies it to the update-staging folder and writes the applier script.
        /// </summary>
        public async Task<bool> StageFromFileAsync(Stream zipStream, string fileName)
        {
            try
            {
                var stagingDir = Path.Combine(AppContext.BaseDirectory, "update-staging");
                Directory.CreateDirectory(stagingDir);

                var zipPath = Path.Combine(stagingDir, "update.zip");
                _logger.LogInformation("Staging manual update from uploaded file: {FileName}", fileName);

                await using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
                await zipStream.CopyToAsync(fileStream);

                StagedUpdatePath = zipPath;
                WriteUpdateApplierScript(stagingDir);
                _logger.LogInformation("Manual update staged: {Path}", zipPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stage manual update");
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
            var extractDir = Path.Combine(stagingDir, "extracted");
            var logPath = Path.Combine(stagingDir, "update.log");
            var scriptPath = Path.Combine(stagingDir, "apply-update.cmd");
            var exeName = "SQLTriage.exe";
            var exePath = Path.Combine(appDir, exeName);

            // Build backup/restore lines for each protected config file.
            // Using string concatenation (not interpolation) for paths so % signs in the
            // batch script template don't interfere with C# string interpolation.
            var nl = "\r\n";
            var backupLines = string.Join(nl, ProtectedConfigFiles.Select(f =>
                "if exist \"" + appDir + "\\" + f + "\" copy /Y \"" + appDir + "\\" + f + "\" \"" + backupDir + "\\" + Path.GetFileName(f) + "\" >>\"" + logPath + "\" 2>&1"));
            var restoreLines = string.Join(nl, ProtectedConfigFiles.Select(f =>
                "if exist \"" + backupDir + "\\" + Path.GetFileName(f) + "\" copy /Y \"" + backupDir + "\\" + Path.GetFileName(f) + "\" \"" + appDir + "\\" + f + "\" >>\"" + logPath + "\" 2>&1"));

            // Build the script using StringBuilder — avoids C# interpolation conflicts with
            // batch tokens like %date%, %time%, %%D, %errorlevel%, etc.
            var sb = new System.Text.StringBuilder();
            sb.Append("@echo off").Append(nl);
            sb.Append("setlocal").Append(nl);
            sb.Append("set LOG=\"" + logPath + "\"").Append(nl);
            sb.Append("echo [%date% %time%] SQLTriage Update Applier started >> %LOG%").Append(nl);
            sb.Append(nl);
            sb.Append("echo Waiting for application to close...").Append(nl);
            sb.Append(":wait").Append(nl);
            sb.Append("timeout /t 2 /nobreak >nul").Append(nl);
            sb.Append("tasklist /FI \"IMAGENAME eq " + exeName + "\" 2>NUL | find /I \"" + exeName + "\" >NUL").Append(nl);
            sb.Append("if not errorlevel 1 goto wait").Append(nl);
            sb.Append("echo [%date% %time%] Application closed. >> %LOG%").Append(nl);
            sb.Append(nl);
            sb.Append("echo Backing up user configuration...").Append(nl);
            sb.Append("if not exist \"" + backupDir + "\" mkdir \"" + backupDir + "\"").Append(nl);
            sb.Append(backupLines).Append(nl);
            sb.Append("echo [%date% %time%] Config backup done. >> %LOG%").Append(nl);
            sb.Append(nl);
            sb.Append("echo Extracting update to temp folder...").Append(nl);
            sb.Append("if exist \"" + extractDir + "\" rmdir /s /q \"" + extractDir + "\"").Append(nl);
            sb.Append("mkdir \"" + extractDir + "\"").Append(nl);
            // Tier 1: PowerShell 5+ Expand-Archive (Windows 10 / Server 2016+)
            sb.Append("powershell -NoProfile -ExecutionPolicy Bypass -Command \"Expand-Archive -Path '" + zipPath + "' -DestinationPath '" + extractDir + "' -Force\" >> %LOG% 2>&1").Append(nl);
            sb.Append("if not errorlevel 1 goto :extract_ok").Append(nl);
            // Tier 2: .NET ZipFile class — works on any PS version that ships with .NET 4.5+ (Server 2012 R2+)
            sb.Append("echo [%date% %time%] Expand-Archive not available, trying .NET ZipFile... >> %LOG%").Append(nl);
            sb.Append("powershell -NoProfile -ExecutionPolicy Bypass -Command \"Add-Type -AssemblyName System.IO.Compression.FileSystem; [System.IO.Compression.ZipFile]::ExtractToDirectory('" + zipPath + "', '" + extractDir + "')\" >> %LOG% 2>&1").Append(nl);
            sb.Append("if not errorlevel 1 goto :extract_ok").Append(nl);
            // Tier 3: Shell.Application COM — available on every Windows version (CopyHere is async, poll until done)
            sb.Append("echo [%date% %time%] ZipFile not available, trying Shell.Application COM... >> %LOG%").Append(nl);
            sb.Append("powershell -NoProfile -ExecutionPolicy Bypass -Command \"$sh = New-Object -ComObject Shell.Application; $zip = $sh.NameSpace('" + zipPath + "'); $dst = $sh.NameSpace('" + extractDir + "'); $count = $zip.Items().Count; $dst.CopyHere($zip.Items(), 20); $w = 0; while ((Get-ChildItem -Recurse '" + extractDir + "' | Measure-Object).Count -lt $count -and $w -lt 120) { Start-Sleep 1; $w++ }\" >> %LOG% 2>&1").Append(nl);
            sb.Append("if errorlevel 1 (").Append(nl);
            sb.Append("    echo [%date% %time%] ERROR: All extraction methods failed. >> %LOG%").Append(nl);
            sb.Append("    echo Update failed. See \"" + logPath + "\" for details.").Append(nl);
            sb.Append("    pause").Append(nl);
            sb.Append("    exit /b 1").Append(nl);
            sb.Append(")").Append(nl);
            sb.Append(":extract_ok").Append(nl);
            sb.Append("echo [%date% %time%] Extraction complete. >> %LOG%").Append(nl);
            sb.Append(nl);
            // If the ZIP had a single nested folder (e.g. publish\), use that as source
            sb.Append("set SRC=\"" + extractDir + "\"").Append(nl);
            sb.Append("for /d %%D in (\"" + extractDir + "\\*\") do set SUBDIR=%%D").Append(nl);
            sb.Append("if exist \"%SUBDIR%\\" + exeName + "\" set SRC=\"%SUBDIR%\"").Append(nl);
            sb.Append(nl);
            sb.Append("echo Copying files to application directory...").Append(nl);
            sb.Append("robocopy %SRC% \"" + appDir + "\" /E /IS /IT /NFL /NDL /NJH /NJS >> %LOG% 2>&1").Append(nl);
            sb.Append("if errorlevel 8 (").Append(nl);
            sb.Append("    echo [%date% %time%] ERROR: robocopy failed. >> %LOG%").Append(nl);
            sb.Append("    echo Update failed. See \"" + logPath + "\" for details.").Append(nl);
            sb.Append("    pause").Append(nl);
            sb.Append("    exit /b 1").Append(nl);
            sb.Append(")").Append(nl);
            sb.Append("echo [%date% %time%] Files copied. >> %LOG%").Append(nl);
            sb.Append(nl);
            sb.Append("echo Restoring user configuration...").Append(nl);
            sb.Append(restoreLines).Append(nl);
            sb.Append("echo [%date% %time%] Config restore done. >> %LOG%").Append(nl);
            sb.Append(nl);
            sb.Append("echo Cleaning up...").Append(nl);
            sb.Append("rmdir /s /q \"" + extractDir + "\" 2>nul").Append(nl);
            sb.Append("del \"" + zipPath + "\" 2>nul").Append(nl);
            sb.Append("rmdir /s /q \"" + backupDir + "\" 2>nul").Append(nl);
            sb.Append(nl);
            sb.Append("echo [%date% %time%] Update applied successfully. >> %LOG%").Append(nl);
            sb.Append("echo Update applied. Restarting application...").Append(nl);
            sb.Append("start \"\" \"" + exePath + "\"").Append(nl);
            sb.Append("timeout /t 3 /nobreak >nul").Append(nl);
            sb.Append("endlocal").Append(nl);
            // Self-delete: (goto) redirects to a non-existent label, closing the script
            // handle so the file can be deleted by the last command.
            sb.Append("(goto) 2>nul & del \"" + scriptPath + "\"").Append(nl);

            File.WriteAllText(scriptPath, sb.ToString());
            _logger.LogInformation("Update applier script written to {Path}", scriptPath);
        }

        private bool IsNewerVersion(string latest, string current,
            int latestBuild = 0, int currentBuild = 0)
        {
            if (string.IsNullOrWhiteSpace(latest)) return false;

            var latestParts  = latest.Split('.');
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

            if (latestParts.Length != currentParts.Length)
                return latestParts.Length > currentParts.Length;

            // Versions are equal — compare build numbers if available
            if (latestBuild > 0 && currentBuild > 0)
                return latestBuild > currentBuild;

            return false;
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
                // e.g. https://api.github.com/repos/SQLAdrian/SQLTriage/releases/latest
                //    → https://api.github.com/repos/SQLAdrian/SQLTriage/contents/scripts
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
