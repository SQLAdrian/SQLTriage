/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data
{
    public class UpdateInfo
    {
        public string Version { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        /// <summary>URL of the detached signature asset (&lt;zip&gt;.sig) for the download. Empty if the release has no signature.</summary>
        public string SignatureUrl { get; set; } = "";
        public string ReleaseNotes { get; set; } = "";
        public DateTime ReleasedAt { get; set; }
        public bool IsRequired { get; set; }
    }

    public class AutoUpdateService
    {
        /// <summary>
        /// Canonical, compile-time update endpoint. The value read from version.json
        /// is honoured ONLY if it targets this same host (see <see cref="LoadVersionInfo"/>),
        /// so a tampered version.json cannot repoint the updater at a hostile server.
        /// </summary>
        private const string PinnedUpdateHost = "api.github.com";
        private const string DefaultUpdateCheckUrl =
            "https://api.github.com/repos/SQLAdrian/SQLTriage/releases/latest";

        // Staging filenames (under <BaseDirectory>/update-staging).
        private const string StagedZipFileName = "update.zip";
        private const string StagedSignatureFileName = "update.zip.sig";

        private readonly ILogger<AutoUpdateService> _logger;
        private readonly Services.Updates.UpdateSignatureVerifier _signatureVerifier;
        private HttpClient _httpClient;
        private string _currentVersion = "1.0.0";
        private int _buildNumber = 0;
        private string _updateCheckUrl = DefaultUpdateCheckUrl;
        private string? _manualProxyUrl;

        /// <summary>
        /// Master kill-switch for the entire update subsystem (auto + manual + script updates).
        /// Read from configuration key "Updates:Enabled" (default true). Client/test builds ship
        /// this false so the updater is fully inert. Every network/exec entry point fail-closes
        /// when this is false.
        /// </summary>
        private readonly bool _updatesEnabled;

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

        public AutoUpdateService(
            ILogger<AutoUpdateService> logger,
            IConfiguration? configuration = null,
            Services.Updates.UpdateSignatureVerifier? signatureVerifier = null)
        {
            _logger = logger;
            // Stateless helper (static key cache); newing it is fine when DI doesn't supply one.
            _signatureVerifier = signatureVerifier ?? new Services.Updates.UpdateSignatureVerifier();
            // Default true so production keeps auto-updating; client/test packages set
            // "Updates:Enabled": false in config/appsettings.json to ship the updater inert.
            _updatesEnabled = configuration?.GetValue("Updates:Enabled", true) ?? true;
            if (!_updatesEnabled)
                _logger.LogInformation("[AutoUpdate] Update subsystem disabled by configuration (Updates:Enabled=false).");
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
                    {
                        var candidate = url.GetString();
                        // Endpoint pinning: only honour the file value if it targets the pinned
                        // host. This removes the file-tamper repoint vector — an attacker who can
                        // write version.json cannot redirect the updater to a hostile server.
                        if (!string.IsNullOrWhiteSpace(candidate) && IsPinnedHost(candidate))
                            _updateCheckUrl = candidate;
                        else if (!string.IsNullOrWhiteSpace(candidate))
                            _logger.LogWarning(
                                "[AutoUpdate] Ignoring updateCheckUrl '{Url}' from version.json — host not pinned ({Pinned}). Using default.",
                                candidate, PinnedUpdateHost);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load version.json, using defaults");
            }
        }

        /// <summary>
        /// True if <paramref name="candidateUrl"/> is a well-formed absolute HTTPS URL whose host
        /// equals the pinned update host. Used to reject attacker-supplied endpoints.
        /// </summary>
        private static bool IsPinnedHost(string candidateUrl)
        {
            return Uri.TryCreate(candidateUrl, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && string.Equals(uri.Host, PinnedUpdateHost, StringComparison.OrdinalIgnoreCase);
        }

        public async Task<(bool Available, UpdateInfo? Info)> CheckForUpdatesAsync()
        {
            LastCheckError = null;
            if (!_updatesEnabled)
            {
                _logger.LogInformation("[AutoUpdate] Updates disabled by configuration — skipping update check.");
                return (false, null);
            }
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
                var zipUrl = "";
                var exeUrl = "";
                var signatureUrl = "";

                if (release.TryGetProperty("assets", out var assets))
                {
                    // Single pass: capture the ZIP, the EXE fallback, and the detached
                    // signature (.sig). We can't break early — the .sig asset may be listed
                    // after the .zip, and the signature is mandatory for the update to apply.
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.GetProperty("name").GetString() ?? "";
                        var url = asset.GetProperty("browser_download_url").GetString() ?? "";

                        if (name.EndsWith(".sig", StringComparison.OrdinalIgnoreCase))
                            signatureUrl = url;
                        else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            zipUrl = url;
                        else if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            exeUrl = url;
                    }
                    // Prefer the ZIP; fall back to the EXE asset.
                    downloadUrl = !string.IsNullOrEmpty(zipUrl) ? zipUrl : exeUrl;
                }

                if (IsNewerVersion(latestVersion, _currentVersion, latestBuild, _buildNumber))
                {
                    var updateInfo = new UpdateInfo
                    {
                        Version = latestVersion,
                        DownloadUrl = downloadUrl,
                        SignatureUrl = signatureUrl,
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
            if (!_updatesEnabled)
            {
                _logger.LogInformation("[AutoUpdate] Updates disabled by configuration — skipping background check.");
                return;
            }
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
        /// Downloads the update ZIP and its detached signature to a staging folder, then
        /// verifies the signature against the embedded trusted public key. The update is only
        /// staged (and the applier script written) if verification succeeds. On any failure —
        /// including a missing/invalid signature — the download is deleted and false is returned.
        /// Returns true only when a verified update is staged.
        /// </summary>
        public async Task<bool> DownloadUpdateAsync(string downloadUrl, string signatureUrl, IProgress<int>? progress = null)
        {
            if (!_updatesEnabled)
            {
                _logger.LogWarning("[AutoUpdate] Updates disabled by configuration — refusing to download.");
                return false;
            }

            // Reject up-front if the release carried no signature asset — without it the
            // download can never be verified, so there is no point fetching it.
            if (string.IsNullOrWhiteSpace(signatureUrl))
            {
                LastCheckError = "Update rejected: the release has no signature (.sig) asset. " +
                                 "Refusing to download an unverifiable update.";
                _logger.LogError("[AutoUpdate] No signature URL for update — refusing to download.");
                return false;
            }

            var stagingDir = Path.Combine(AppContext.BaseDirectory, "update-staging");
            var zipPath = Path.Combine(stagingDir, StagedZipFileName);
            var sigPath = Path.Combine(stagingDir, StagedSignatureFileName);
            try
            {
                Directory.CreateDirectory(stagingDir);
                _logger.LogInformation("Downloading update from {Url} to {Path}", downloadUrl, zipPath);

                using (var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
                {
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
                    _logger.LogInformation("Update ZIP downloaded: {Size:N0} bytes", downloadedBytes);
                }

                // Fetch the detached signature.
                var sigBytes = await _httpClient.GetByteArrayAsync(signatureUrl);
                await File.WriteAllBytesAsync(sigPath, sigBytes);

                // Verify BEFORE staging. Hard fail on any mismatch — never stage unverified bytes.
                if (!_signatureVerifier.VerifyFile(zipPath, sigPath))
                {
                    CleanupStaging(stagingDir);
                    StagedUpdatePath = null;
                    LastCheckError = _signatureVerifier.IsTrustedKeyConfigured
                        ? "Update signature verification FAILED. The download was tampered with or not signed by SQLTriage. Update aborted."
                        : "Update signing key not configured in this build. Updates cannot be verified and are disabled.";
                    _logger.LogError("[AutoUpdate] Signature verification failed for {Path} — download discarded.", zipPath);
                    progress?.Report(0);
                    return false;
                }

                progress?.Report(100);
                StagedUpdatePath = zipPath;
                _logger.LogInformation("[AutoUpdate] Update downloaded and signature VERIFIED — staging.");

                // Write the applier script only after verification succeeds.
                WriteUpdateApplierScript(stagingDir);

                return true;
            }
            catch (Exception ex)
            {
                // Never leave a partial/unverified download staged.
                CleanupStaging(stagingDir);
                StagedUpdatePath = null;
                _logger.LogError(ex, "Failed to download update");
                return false;
            }
        }

        /// <summary>Deletes the staged ZIP, signature, and applier script (best effort).</summary>
        private void CleanupStaging(string stagingDir)
        {
            foreach (var f in new[] { StagedZipFileName, StagedSignatureFileName, "apply-update.cmd" })
            {
                try
                {
                    var p = Path.Combine(stagingDir, f);
                    if (File.Exists(p)) File.Delete(p);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[AutoUpdate] Could not delete staged file {File}", f);
                }
            }
        }

        /// <summary>
        /// Stages an update from a ZIP file the user has already downloaded manually.
        /// Copies it to the update-staging folder and writes the applier script.
        /// </summary>
        public async Task<bool> StageFromFileAsync(Stream zipStream, string fileName, Stream signatureStream)
        {
            if (!_updatesEnabled)
            {
                _logger.LogWarning("[AutoUpdate] Updates disabled by configuration — refusing to stage manual update.");
                return false;
            }
            if (signatureStream == null)
            {
                LastCheckError = "A signature (.sig) file is required to stage a manual update. " +
                                 "Unsigned updates are not accepted.";
                _logger.LogError("[AutoUpdate] Manual update rejected — no signature provided.");
                return false;
            }

            var stagingDir = Path.Combine(AppContext.BaseDirectory, "update-staging");
            var zipPath = Path.Combine(stagingDir, StagedZipFileName);
            var sigPath = Path.Combine(stagingDir, StagedSignatureFileName);
            try
            {
                Directory.CreateDirectory(stagingDir);
                _logger.LogInformation("Staging manual update from uploaded file: {FileName}", fileName);

                await using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                {
                    await zipStream.CopyToAsync(fileStream);
                }
                await using (var sigFileStream = new FileStream(sigPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                {
                    await signatureStream.CopyToAsync(sigFileStream);
                }

                // Same hard-fail verification as the auto path — no override.
                if (!_signatureVerifier.VerifyFile(zipPath, sigPath))
                {
                    CleanupStaging(stagingDir);
                    StagedUpdatePath = null;
                    LastCheckError = _signatureVerifier.IsTrustedKeyConfigured
                        ? "Manual update signature verification FAILED. The file was tampered with or not signed by SQLTriage. Update aborted."
                        : "Update signing key not configured in this build. Updates cannot be verified and are disabled.";
                    _logger.LogError("[AutoUpdate] Manual update signature verification failed — discarded.");
                    return false;
                }

                StagedUpdatePath = zipPath;
                WriteUpdateApplierScript(stagingDir);
                _logger.LogInformation("[AutoUpdate] Manual update staged and signature VERIFIED: {Path}", zipPath);
                return true;
            }
            catch (Exception ex)
            {
                CleanupStaging(stagingDir);
                StagedUpdatePath = null;
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
            if (!_updatesEnabled)
            {
                _logger.LogWarning("[AutoUpdate] Updates disabled by configuration — refusing to apply staged update.");
                return;
            }
            if (!HasStagedUpdate) return;

            try
            {
                var stagingDir = Path.GetDirectoryName(StagedUpdatePath!)!;

                // Re-verify immediately before launching. The staged ZIP has been sitting on disk
                // (potentially across sessions) since download; this closes the TOCTOU window where
                // an attacker swaps the staged bytes between verify-on-download and apply-on-exit.
                var sigPath = Path.Combine(stagingDir, StagedSignatureFileName);
                if (!_signatureVerifier.VerifyFile(StagedUpdatePath!, sigPath))
                {
                    _logger.LogError("[AutoUpdate] Staged update failed re-verification at apply time — aborting and discarding.");
                    CleanupStaging(stagingDir);
                    StagedUpdatePath = null;
                    return;
                }

                // Phase 1: prefer the managed out-of-process updater (updater\SQLTriageUpdater.exe).
                // It runs from a sibling dir so it can swap the install dir without file-lock fights,
                // preserves user config, and rolls back if the new build fails to start. Falls back to
                // the legacy .cmd applier when the updater isn't present (zero regression).
                var updaterExe = Path.Combine(AppContext.BaseDirectory, "updater", "SQLTriageUpdater.exe");
                if (File.Exists(updaterExe))
                {
                    var appDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
                    _logger.LogInformation("[AutoUpdate] Signature re-verified — launching managed external updater: {Updater}", updaterExe);
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = updaterExe,
                        Arguments = $"--apply --staging \"{stagingDir}\" --target \"{appDir}\" --pid {Environment.ProcessId} --exe SQLTriage.exe",
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(updaterExe)!,
                    });
                    return;
                }

                var scriptPath = Path.Combine(stagingDir, "apply-update.cmd");
                if (File.Exists(scriptPath))
                {
                    _logger.LogInformation("[AutoUpdate] Signature re-verified — launching legacy update applier: {Script}", scriptPath);
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
            if (!_updatesEnabled)
            {
                _logger.LogInformation("[AutoUpdate] Updates disabled by configuration — skipping script-update check.");
                return results;
            }
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
            if (!_updatesEnabled)
            {
                _logger.LogWarning("[AutoUpdate] Updates disabled by configuration — refusing to download script updates.");
                return (0, 0);
            }
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
