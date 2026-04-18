/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Executes PowerShell commands locally and captures output.
    /// Uses pwsh.exe (PS 7+) with fallback to powershell.exe (Windows PS 5.1).
    /// Results are returned as JSON-parsed DataTables or raw text.
    /// </summary>
    public class PowerShellService
    {
        private readonly ILogger<PowerShellService> _logger;
        private string? _dbatoolsPath;
        private string? _resolvedPwsh;

        public PowerShellService(ILogger<PowerShellService> logger)
        {
            _logger = logger;

            // dbatools module path relative to app directory
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidate = Path.Combine(appDir, "dbatools");
            _dbatoolsPath = Directory.Exists(candidate) ? candidate : null;

            ResolvePowerShell();
        }

        /// <summary>Whether a PowerShell executable was found on the system.</summary>
        public bool IsPowerShellAvailable => _resolvedPwsh != null;

        /// <summary>Whether the bundled dbatools module folder exists.</summary>
        public bool IsDbatoolsAvailable => _dbatoolsPath != null;

        /// <summary>Path to the resolved PowerShell executable.</summary>
        public string? PowerShellPath => _resolvedPwsh;

        /// <summary>Path to the bundled dbatools module folder.</summary>
        public string? DbatoolsPath => _dbatoolsPath;

        /// <summary>
        /// Refreshes dbatools availability check (e.g. after downloading).
        /// </summary>
        public void RefreshDbatoolsStatus()
        {
            var candidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dbatools");
            _dbatoolsPath = Directory.Exists(candidate) ? candidate : null;
        }

        private void ResolvePowerShell()
        {
            // Prefer pwsh (PowerShell 7+), fall back to powershell.exe (5.1)
            foreach (var exe in new[] { "pwsh.exe", "pwsh", "powershell.exe" })
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = "-NoProfile -Command \"Write-Output 'ok'\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var proc = Process.Start(psi);
                    if (proc == null) continue;
                    proc.WaitForExit(5000);
                    if (proc.ExitCode == 0)
                    {
                        _resolvedPwsh = exe;
                        _logger.LogInformation("PowerShell resolved: {Exe}", exe);
                        return;
                    }
                }
                catch
                {
                    // Not available, try next
                }
            }

            _logger.LogWarning("No PowerShell executable found on this system");
        }

        /// <summary>
        /// Executes a PowerShell command and returns the output as a DataTable.
        /// The command output is piped through ConvertTo-Json for structured parsing.
        /// </summary>
        public async Task<PowerShellResult> ExecuteAsDataTableAsync(
            string command,
            bool importDbatools = false,
            int timeoutSeconds = 120,
            CancellationToken cancellationToken = default)
        {
            var result = await ExecuteRawAsync(command, asJson: true, importDbatools: importDbatools, timeoutSeconds: timeoutSeconds, cancellationToken: cancellationToken);

            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
                return result;

            try
            {
                result.Data = ParseJsonToDataTable(result.Output);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse PowerShell JSON output to DataTable");
                result.ParseError = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Executes a PowerShell command and returns raw text output.
        /// </summary>
        public async Task<PowerShellResult> ExecuteAsTextAsync(
            string command,
            bool importDbatools = false,
            int timeoutSeconds = 120,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteRawAsync(command, asJson: false, importDbatools: importDbatools, timeoutSeconds: timeoutSeconds, cancellationToken: cancellationToken);
        }

        private async Task<PowerShellResult> ExecuteRawAsync(
            string command,
            bool asJson,
            bool importDbatools,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            if (_resolvedPwsh == null)
                return new PowerShellResult { Success = false, Error = "PowerShell is not available on this system." };

            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'Stop'");

            if (importDbatools && _dbatoolsPath != null)
            {
                // Import dbatools.library first (dependency), then dbatools
                var libPath = FindSubModule(_dbatoolsPath, "dbatools.library");
                if (libPath != null)
                    sb.AppendLine($"Import-Module '{libPath}' -Force");
                sb.AppendLine($"Import-Module '{FindSubModule(_dbatoolsPath, "dbatools") ?? _dbatoolsPath}' -Force");
            }

            if (asJson)
                sb.AppendLine(command + " | ConvertTo-Json -Depth 4 -Compress");
            else
                sb.AppendLine(command);

            var script = sb.ToString();

            _logger.LogDebug("Executing PowerShell: {Script}", script.Length > 200 ? script[..200] + "..." : script);

            var psi = new ProcessStartInfo
            {
                FileName = _resolvedPwsh,
                Arguments = "-NoProfile -NoLogo -NonInteractive -Command -",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var result = new PowerShellResult();

            try
            {
                using var proc = new Process { StartInfo = psi };
                proc.Start();

                // Write script to stdin
                await proc.StandardInput.WriteAsync(script);
                proc.StandardInput.Close();

                // Read output and error in parallel
                var outputTask = proc.StandardOutput.ReadToEndAsync();
                var errorTask = proc.StandardError.ReadToEndAsync();

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

                try
                {
                    await proc.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    result.Success = false;
                    result.Error = cancellationToken.IsCancellationRequested
                        ? "Command was cancelled."
                        : $"Command timed out after {timeoutSeconds} seconds.";
                    return result;
                }

                result.Output = await outputTask;
                result.Error = await errorTask;
                result.ExitCode = proc.ExitCode;
                result.Success = proc.ExitCode == 0;

                if (!result.Success && string.IsNullOrWhiteSpace(result.Error))
                    result.Error = $"PowerShell exited with code {proc.ExitCode}";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = $"Failed to execute PowerShell: {ex.Message}";
                _logger.LogError(ex, "PowerShell execution failed");
            }

            return result;
        }

        /// <summary>
        /// Downloads/updates dbatools module to the app's dbatools folder.
        /// </summary>
        public async Task<PowerShellResult> DownloadDbatoolsAsync(CancellationToken cancellationToken = default)
        {
            var targetPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dbatools");
            Directory.CreateDirectory(targetPath);

            // -AcceptLicense is PS7+ only; use -Force which implicitly accepts on both 5.1 and 7
            var command = $"Save-Module -Name dbatools -Path '{targetPath}' -Force";
            var result = await ExecuteRawAsync(command, asJson: false, importDbatools: false, timeoutSeconds: 300, cancellationToken: cancellationToken);

            if (result.Success)
            {
                RefreshDbatoolsStatus();
                _logger.LogInformation("dbatools module downloaded to {Path}", targetPath);
            }

            return result;
        }

        private static string? FindSubModule(string basePath, string moduleName)
        {
            // Look for moduleName folder under basePath (Save-Module structure)
            var modDir = Path.Combine(basePath, moduleName);
            if (Directory.Exists(modDir))
            {
                // Find the version subfolder
                var dirs = Directory.GetDirectories(modDir);
                if (dirs.Length > 0)
                    return dirs[^1]; // Latest version folder
                return modDir;
            }
            return null;
        }

        private static DataTable ParseJsonToDataTable(string json)
        {
            var dt = new DataTable();
            json = json.Trim();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Handle single object (wrap in array)
            var items = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                : AsEnumerable(new[] { root });

            bool columnsCreated = false;

            foreach (var item in items)
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                if (!columnsCreated)
                {
                    foreach (var prop in item.EnumerateObject())
                    {
                        // Skip complex nested objects — flatten to string
                        dt.Columns.Add(prop.Name, typeof(string));
                    }
                    columnsCreated = true;
                }

                var row = dt.NewRow();
                foreach (var prop in item.EnumerateObject())
                {
                    if (!dt.Columns.Contains(prop.Name)) continue;

                    row[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString() ?? "",
                        JsonValueKind.Number => prop.Value.GetRawText(),
                        JsonValueKind.True => "True",
                        JsonValueKind.False => "False",
                        JsonValueKind.Null => "",
                        _ => prop.Value.GetRawText()
                    };
                }
                dt.Rows.Add(row);
            }

            return dt;
        }

        // Helper for single-object JSON wrapping
        private static IEnumerable<JsonElement> AsEnumerable(JsonElement[] arr)
        {
            foreach (var el in arr) yield return el;
        }
    }

    /// <summary>Result of a PowerShell command execution.</summary>
    public class PowerShellResult
    {
        public bool Success { get; set; }
        public string? Output { get; set; }
        public string? Error { get; set; }
        public string? ParseError { get; set; }
        public int ExitCode { get; set; }
        public DataTable? Data { get; set; }
    }
}
