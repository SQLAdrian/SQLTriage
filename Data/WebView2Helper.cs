/* In the name of God, the Merciful, the Compassionate */

using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace SQLTriage.Data
{
    /// <summary>
    /// Service to detect and install WebView2 runtime on Windows Server 2016 and later.
    /// Handles the "WebView2RuntimeNotFoundException" error gracefully.
    /// </summary>
    public class WebView2Helper
    {
        private readonly ILogger<WebView2Helper>? _logger;

        // WebView2 Evergreen runtime download URL (latest stable)
        private const string WebView2DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        // Minimum required version for Windows Server 2016 compatibility
        private const string MinVersion = "86.0.664.57";

        public WebView2Helper(ILogger<WebView2Helper>? logger = null)
        {
            _logger = logger;
        }

        /// <summary>
        /// Checks if WebView2 runtime is installed and meets minimum version requirements.
        /// </summary>
        public async Task<WebView2Status> CheckWebView2StatusAsync()
        {
            try
            {
                _logger?.LogInformation("Checking WebView2 runtime status...");

                // Try to create a WebView2 environment - this will fail if runtime is not installed
                var env = await CoreWebView2Environment.CreateAsync();

                if (env == null)
                {
                    return new WebView2Status
                    {
                        IsInstalled = false,
                        Version = null,
                        ErrorMessage = "WebView2 environment creation returned null"
                    };
                }

                // Get version from the environment (most reliable) or folder scan
                string? version = null;
                try
                {
                    version = env.BrowserVersionString;
                }
                catch (Exception ex) { _logger?.LogDebug(ex, "[WebView2] Failed to get BrowserVersionString from environment"); }

                if (string.IsNullOrEmpty(version))
                {
                    try
                    {
                        var webview2Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "EdgeWebView", "Application");
                        if (Directory.Exists(webview2Path))
                        {
                            var folders = Directory.GetDirectories(webview2Path);
                            if (folders.Length > 0)
                                version = Path.GetFileName(folders[0]);
                        }
                    }
                    catch (Exception ex) { _logger?.LogDebug(ex, "[WebView2] Failed to scan EdgeWebView folder for version"); }
                }

                _logger?.LogInformation("WebView2 runtime found. Version: {Version}", version ?? "unknown");

                // If CreateAsync() succeeded, WebView2 is functional.
                // Only fail compatibility if we have a concrete version that's too old.
                var isCompatible = string.IsNullOrEmpty(version) || IsVersionCompatible(version, MinVersion);

                return new WebView2Status
                {
                    IsInstalled = true,
                    Version = version,
                    IsCompatible = isCompatible,
                    ErrorMessage = isCompatible ? null : $"Version {version} is below minimum required version {MinVersion}"
                };
            }
            catch (FileNotFoundException ex) when (ex.HResult == -2147024891) // 0x80070002
            {
                // This is the specific error from the stack trace
                _logger?.LogWarning(ex, "WebView2 runtime not found. FileNotFoundException (0x80070002)");

                return new WebView2Status
                {
                    IsInstalled = false,
                    Version = null,
                    IsCompatible = false,
                    ErrorMessage = "WebView2 runtime is not installed. Please install Microsoft Edge WebView2 Runtime.",
                    InstallRequired = true
                };
            }
            catch (WebView2RuntimeNotFoundException ex)
            {
                _logger?.LogWarning(ex, "WebView2 runtime not found - WebView2RuntimeNotFoundException");

                return new WebView2Status
                {
                    IsInstalled = false,
                    Version = null,
                    IsCompatible = false,
                    ErrorMessage = "WebView2 runtime is not installed. Please install Microsoft Edge WebView2 Runtime.",
                    InstallRequired = true
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unexpected error checking WebView2 status");

                return new WebView2Status
                {
                    IsInstalled = false,
                    Version = null,
                    IsCompatible = false,
                    ErrorMessage = $"Error checking WebView2: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Attempts to install WebView2 runtime silently.
        /// </summary>
        public async Task<InstallResult> TryInstallWebView2Async(IProgress<int>? progress = null)
        {
            try
            {
                _logger?.LogInformation("Attempting to install WebView2 runtime...");

                var tempPath = Path.Combine(Path.GetTempPath(), $"WebView2Installer_{Guid.NewGuid()}.exe");

                progress?.Report(10);

                // Download the installer
                using var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SQLTriage/1.0");

                var response = await client.GetAsync(WebView2DownloadUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;

                progress?.Report(30);

                await using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await using var stream = await response.Content.ReadAsStreamAsync();

                var buffer = new byte[8192];
                var totalRead = 0L;
                int bytesRead;

                while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, bytesRead));
                    totalRead += bytesRead;

                    if (totalBytes > 0)
                    {
                        var percent = (int)(30 + (totalRead * 40 / totalBytes));
                        progress?.Report(percent);
                    }
                }

                progress?.Report(70);
                _logger?.LogInformation("Downloaded WebView2 installer to {Path}", tempPath);

                // Run the installer silently
                var psi = new ProcessStartInfo
                {
                    FileName = tempPath,
                    Arguments = "/silent /install",
                    UseShellExecute = true,
                    Verb = "runas"
                };

                progress?.Report(80);

                using var process = Process.Start(psi);
                if (process == null)
                {
                    return new InstallResult
                    {
                        Success = false,
                        ErrorMessage = "Failed to start WebView2 installer"
                    };
                }

                await process.WaitForExitAsync();

                progress?.Report(100);

                // Clean up installer
                try { File.Delete(tempPath); } catch (Exception ex) { _logger?.LogDebug(ex, "[WebView2] Failed to delete temp installer: {Path}", tempPath); }

                _logger?.LogInformation("WebView2 installer completed with exit code {ExitCode}", process.ExitCode);

                if (process.ExitCode == 0)
                {
                    // Wait a moment for the installation to complete
                    await Task.Delay(2000);

                    // Verify installation
                    var status = await CheckWebView2StatusAsync();
                    return new InstallResult
                    {
                        Success = status.IsInstalled && status.IsCompatible,
                        Version = status.Version,
                        ErrorMessage = status.ErrorMessage
                    };
                }
                else
                {
                    return new InstallResult
                    {
                        Success = false,
                        ErrorMessage = $"Installer exited with code {process.ExitCode}"
                    };
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to install WebView2 runtime");
                return new InstallResult
                {
                    Success = false,
                    ErrorMessage = $"Installation failed: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Gets the path to the bundled WebView2 installer if it exists.
        /// </summary>
        public string? GetBundledInstallerPath()
        {
            var baseDir = AppContext.BaseDirectory;

            // Check for bundled installer in various common locations
            var possiblePaths = new[]
            {
                Path.Combine(baseDir, "WebView2Installer.exe"),
                Path.Combine(baseDir, " installers", "WebView2Installer.exe"),
                Path.Combine(baseDir, "tools", "WebView2Installer.exe")
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    _logger?.LogInformation("Found bundled WebView2 installer at {Path}", path);
                    return path;
                }
            }

            return null;
        }

        /// <summary>
        /// Gets the Windows version to check for Server 2016.
        /// </summary>
        public static string GetWindowsVersion()
        {
            try
            {
                return Environment.OSVersion.VersionString;
            }
            catch
            {
                return "Unknown";
            }
        }

        /// <summary>
        /// Checks if running on Windows Server 2016.
        /// </summary>
        public static bool IsWindowsServer2016()
        {
            var os = Environment.OSVersion;
            // Windows Server 2016 is version 10.0.14393
            return os.Platform == PlatformID.Win32NT &&
                   os.Version.Major == 10 &&
                   os.Version.Minor == 0 &&
                   os.Version.Build >= 14393 &&
                   os.Version.Build < 17763;
        }

        private static bool IsVersionCompatible(string? version, string minVersion)
        {
            if (string.IsNullOrEmpty(version))
                return false;

            try
            {
                var v1 = new Version(version.Split('-')[0]); // Handle suffixes like "86.0.664.57"
                var v2 = new Version(minVersion);
                return v1 >= v2;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Result of WebView2 status check.
    /// </summary>
    public class WebView2Status
    {
        public bool IsInstalled { get; set; }
        public string? Version { get; set; }
        public bool IsCompatible { get; set; }
        public bool InstallRequired { get; set; }
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Result of WebView2 installation attempt.
    /// </summary>
    public class InstallResult
    {
        public bool Success { get; set; }
        public string? Version { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
