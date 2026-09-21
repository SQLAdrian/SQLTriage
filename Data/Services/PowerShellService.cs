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
    // BM:PowerShellService.Class — executes local PowerShell commands and captures output
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

            // dbatools module path relative to app directory. Honest about contents: see
            // ResolveDbatoolsPath — folder existence alone is not "module available".
            _dbatoolsPath = ResolveDbatoolsPath();

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
            _dbatoolsPath = ResolveDbatoolsPath();
        }

        /// <summary>
        /// Resolves the dbatools module folder, honest about contents. The folder existing is NOT
        /// enough: <see cref="DownloadDbatoolsAsync"/> creates the folder before it runs Save-Module,
        /// and a failed download leaves it empty. Folder-existence alone used to drive a green
        /// "dbatools module found" tick with the Download button hidden, stranding the operator with
        /// no retry. The folder counts only when it exists AND actually holds the module.
        /// </summary>
        private static string? ResolveDbatoolsPath()
        {
            var candidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dbatools");
            return DbatoolsFolderHasModule(candidate) ? candidate : null;
        }

        /// <summary>True only when <paramref name="candidate"/> exists and is not empty. Save-Module
        /// lays the module out beneath this folder, so a non-empty folder means the module is there;
        /// an empty folder is the failed-download debris this check exists to reject.</summary>
        internal static bool DbatoolsFolderHasModule(string candidate) =>
            Directory.Exists(candidate) && Directory.GetFileSystemEntries(candidate).Length > 0;

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

            // pages-r2-05: this early return is still correct - there is no JSON to parse - but it
            // is NOT "no results". result.Error may hold the whole account of why stdout is blank
            // (dbatools writes "could not connect" / "access denied" to standard error while the
            // host still exits 0). PowerShellRunReporting.DescribeEmptyResult is what the page
            // renders in place of the old bare "No results returned."
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
                return result;

            try
            {
                result.Data = ParseJsonToDataTable(result.Output, out var dropped);
                result.DroppedColumns = dropped;
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

            if (importDbatools)
            {
                // Trust the server certificate for dbatools connections. Modern SQL Server
                // (2022/2025) forces connection encryption by default; without this, dbatools
                // fails the TLS chain ("certificate chain ... not trusted") on instances using
                // a self-signed cert — matching the app's own ADO.NET TrustServerCertificate=true.
                // Applies whether dbatools is bundled or auto-loaded from a system module path.
                sb.AppendLine("try { Set-DbatoolsConfig -FullName sql.connection.trustcert -Value $true -ErrorAction Stop } catch {}");
                sb.AppendLine("try { Set-DbatoolsConfig -FullName sql.connection.encrypt -Value $false -ErrorAction Stop } catch {}");
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
        /// The dbatools version this repo is verified against, pinned so the app's PowerShell
        /// dependency cannot change without a commit.
        ///
        /// <para>2.8.4, PowerShell Gallery, 2026-07-31. dbatools 2.8.0 shipped breaking changes to
        /// Install-DbaMaintenanceSolution's parameters and dropped SQL 2005 support. The app calls
        /// neither, so it is believed unaffected and that is UNTESTED here - which is exactly the
        /// point: a floating Save-Module would have taken 2.8.0 the day it landed and nobody would
        /// have looked.</para>
        ///
        /// <para>⚠ TWO SITES, ONE DECISION. Update-Dbatools.ps1 carries the same pin for the manual
        /// and publish-pipeline path. Move both in the same commit.</para>
        /// </summary>
        public const string PinnedDbatoolsVersion = "2.8.4";

        /// <summary>
        /// The exact PowerShell the in-app download button runs. Extracted from
        /// <see cref="DownloadDbatoolsAsync"/> on 2026-08-23 for ONE reason: it is the half of the
        /// two-site pin a test could not see. The tripwire asserted the pin in Update-Dbatools.ps1
        /// and nothing else, so deleting "-RequiredVersion" from THIS command left the whole suite
        /// green — proved by mutation, C:\temp\frk-refresh\verify\v-h1.log.
        ///
        /// <para>-AcceptLicense is PS7+ only; -Force implicitly accepts on both 5.1 and 7.
        /// -RequiredVersion PINS the module: without it Save-Module takes whatever PowerShell
        /// Gallery serves that day, so the app's PowerShell dependency drifts with no commit and no
        /// review. See <see cref="PinnedDbatoolsVersion"/> for the version and how to move it.</para>
        /// </summary>
        internal static string BuildDbatoolsSaveModuleCommand(string targetPath) =>
            $"Save-Module -Name dbatools -Path '{targetPath}' -RequiredVersion {PinnedDbatoolsVersion} -Force";

        /// <summary>
        /// Downloads/updates dbatools module to the app's dbatools folder.
        /// </summary>
        public async Task<PowerShellResult> DownloadDbatoolsAsync(CancellationToken cancellationToken = default)
        {
            var targetPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dbatools");
            Directory.CreateDirectory(targetPath);

            var command = BuildDbatoolsSaveModuleCommand(targetPath);
            var result = await ExecuteRawAsync(command, asJson: false, importDbatools: false, timeoutSeconds: 300, cancellationToken: cancellationToken);

            if (result.Success)
            {
                RefreshDbatoolsStatus();
                _logger.LogInformation("dbatools module downloaded to {Path}", targetPath);
            }
            else
            {
                // Directory.CreateDirectory above ran before Save-Module. A failed download therefore
                // leaves the folder behind, empty. Remove the empty folder so the availability status
                // stays honest and the Download (retry) button stays visible.
                TryRemoveEmptyFolder(targetPath);
                RefreshDbatoolsStatus();
                _logger.LogWarning("dbatools download failed; left no partial folder at {Path}", targetPath);
            }

            return result;
        }

        /// <summary>Deletes <paramref name="path"/> only when it exists and is empty. Best-effort:
        /// the content-aware availability check is the real guard, this just clears the debris.</summary>
        private static void TryRemoveEmptyFolder(string path)
        {
            try
            {
                if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0)
                    Directory.Delete(path);
            }
            catch { /* best-effort */ }
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

        /// <summary>
        /// Parses the command's JSON into a grid, carrying EVERY property the command emitted.
        ///
        /// <para>pages-r1-09 (honesty hunt, 2026-08-28): this built its column set from the FIRST
        /// object only and then ran `if (!dt.Columns.Contains(prop.Name)) continue;` over the
        /// rest, so any property absent from object one was dropped from the grid AND from the CSV
        /// the operator carries away, with no warning and no ParseError - so the raw-text fallback
        /// on the page never fired either. Proved at hunt time and re-proved at HEAD on 2026-08-28
        /// through the app's own host and argument shape: `@([pscustomobject]@{A=1},
        /// [pscustomobject]@{A=2;B=3}) | ConvertTo-Json -Depth 4 -Compress` exits 0 with stdout
        /// exactly `[{"A":1},{"A":2,"B":3}]`, so ConvertTo-Json does NOT normalise property sets
        /// across objects and heterogeneous keys genuinely reach this method.</para>
        ///
        /// <para>The column set is now the union of every object's properties, in first-seen
        /// order, so the grid and the export carry what the command actually returned. Anything
        /// still unrepresentable is NAMED in <paramref name="dropped"/> rather than skipped in
        /// silence - a table that cannot show a value must at least say so.</para>
        /// </summary>
        internal static DataTable ParseJsonToDataTable(string json, out List<string> dropped)
        {
            var dt = new DataTable();
            dropped = new List<string>();
            json = json.Trim();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Materialise once: the union pass and the row pass both need to walk the objects, and
            // JsonDocument enumerators are forward-only.
            var items = new List<JsonElement>();
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.Object) items.Add(el.Clone());
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                items.Add(root.Clone());
            }

            // Pass 1 - the column union, in first-seen order.
            foreach (var item in items)
            {
                foreach (var prop in item.EnumerateObject())
                {
                    if (dt.Columns.Contains(prop.Name)) continue;
                    dt.Columns.Add(prop.Name, typeof(string));
                }
            }

            // Pass 2 - the rows. A property missing from an object leaves that cell empty, which
            // is the honest rendering of "this object did not carry it".
            foreach (var item in items)
            {
                var row = dt.NewRow();
                foreach (var prop in item.EnumerateObject())
                {
                    if (!dt.Columns.Contains(prop.Name))
                    {
                        // Only reachable for a duplicate key within one object, which the union
                        // pass collapsed. Name it; never drop it in silence.
                        if (!dropped.Contains(prop.Name)) dropped.Add(prop.Name);
                        continue;
                    }

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

        /// <summary>
        /// Properties the command returned that the grid could not represent. Empty in every
        /// shape the parser can carry, which since pages-r1-09 is every heterogeneous object set
        /// ConvertTo-Json produces. Exists so that a future loss is NAMED rather than silent.
        /// </summary>
        public List<string> DroppedColumns { get; set; } = new();
    }
}
