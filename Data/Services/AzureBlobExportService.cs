/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Exports query results and assessment data as CSV files to Azure Blob Storage.
    /// Supports connection string and SAS token authentication.
    /// CSV files are organized by date and export type in the blob container.
    /// </summary>
    public class AzureBlobExportService
    {
        private readonly ILogger<AzureBlobExportService> _logger;
        private readonly IConfiguration _configuration;
        private readonly AuditLogService? _auditLog;

        // Configuration — loaded from appsettings, updatable at runtime
        private string? _connectionString;
        private string? _sasToken;
        private string? _storageAccountName;
        private string? _containerName;
        private string? _blobPrefix;
        private string _uploadMethod = "sdk"; // "sdk" or "azcopy"
        private string? _azcopyPath;
        private bool _isConfigured;
        private bool _compressUploads;
        private bool _autoUploadCsvs;

        /// <summary>
        /// Strips ".blob.core.windows.net" suffix if the user pasted the full hostname
        /// instead of just the storage account name.
        /// </summary>
        private static string NormalizeAccountName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return name ?? "";
            name = name.Trim();
            const string suffix = ".blob.core.windows.net";
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                name = name[..^suffix.Length];
            return name;
        }

        public bool IsConfigured => _isConfigured;
        public string? ContainerName => _containerName;
        public string? BlobPrefix => _blobPrefix;
        public bool CompressUploads { get => _compressUploads; set => _compressUploads = value; }
        public string UploadMethod { get => _uploadMethod; set => _uploadMethod = value; }
        public string? AzCopyPath { get => _azcopyPath; set => _azcopyPath = value; }
        public string? StorageAccountName => _storageAccountName;
        public bool AutoUploadCsvs { get => _autoUploadCsvs; set => _autoUploadCsvs = value; }

        /// <summary>
        /// Returns the auth mode: "connectionstring", "sastoken", or "none".
        /// </summary>
        public string AuthMode
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_connectionString)) return "connectionstring";
                if (!string.IsNullOrWhiteSpace(_sasToken) && !string.IsNullOrWhiteSpace(_storageAccountName)) return "sastoken";
                return "none";
            }
        }

        public event Action? OnConfigChanged;

        public AzureBlobExportService(
            ILogger<AzureBlobExportService> logger,
            IConfiguration configuration,
            AuditLogService? auditLog = null)
        {
            _logger = logger;
            _configuration = configuration;
            _auditLog = auditLog;
            LoadConfiguration();
        }

        private void LoadConfiguration()
        {
            _connectionString = DecryptIfNeeded(_configuration["AzureBlobExport:ConnectionString"]);
            _sasToken = DecryptIfNeeded(_configuration["AzureBlobExport:SasToken"]);
            _storageAccountName = NormalizeAccountName(_configuration["AzureBlobExport:StorageAccountName"]);
            var rawContainer = _configuration["AzureBlobExport:ContainerName"] ?? "SQLTriage";
            var configPrefix = _configuration["AzureBlobExport:BlobPrefix"] ?? "";
            rawContainer = rawContainer.Trim('/');
            var slashIdx = rawContainer.IndexOf('/');
            if (slashIdx > 0)
            {
                _containerName = rawContainer[..slashIdx];
                var extraPath = rawContainer[(slashIdx + 1)..].TrimEnd('/');
                _blobPrefix = string.IsNullOrEmpty(configPrefix) ? extraPath : $"{extraPath}/{configPrefix.TrimStart('/')}";
            }
            else
            {
                _containerName = rawContainer;
                _blobPrefix = configPrefix;
            }
            _compressUploads = string.Equals(_configuration["AzureBlobExport:CompressUploads"], "true", StringComparison.OrdinalIgnoreCase);
            _uploadMethod = _configuration["AzureBlobExport:UploadMethod"] ?? "sdk";
            _azcopyPath = _configuration["AzureBlobExport:AzCopyPath"] ?? "";
            _autoUploadCsvs = string.Equals(_configuration["AzureBlobExport:AutoUploadCsvs"], "true", StringComparison.OrdinalIgnoreCase);

            _isConfigured = !string.IsNullOrWhiteSpace(_connectionString)
                || (!string.IsNullOrWhiteSpace(_sasToken) && !string.IsNullOrWhiteSpace(_storageAccountName));

            if (_isConfigured)
                _logger.LogInformation("Azure Blob Export configured: auth={AuthMode}, container={Container}, method={Method}",
                    AuthMode, _containerName, _uploadMethod);
            else
                _logger.LogDebug("Azure Blob Export not configured");
        }

        /// <summary>
        /// Updates the Azure Blob configuration at runtime.
        /// Credentials should be passed decrypted — they are stored encrypted in the config file by the caller.
        /// </summary>
        public void Configure(string? connectionString, string? sasToken, string? storageAccountName,
            string containerName, string? blobPrefix = null)
        {
            // null means "keep existing" — only overwrite if a value is explicitly provided
            if (connectionString != null) _connectionString = connectionString;
            if (sasToken != null) _sasToken = sasToken;
            if (storageAccountName != null) _storageAccountName = NormalizeAccountName(storageAccountName);
            containerName = string.IsNullOrWhiteSpace(containerName) ? "SQLTriage" : containerName.Trim('/');
            // If the user entered "container/path", split into container + prefix
            var slashIndex = containerName.IndexOf('/');
            if (slashIndex > 0)
            {
                _containerName = containerName[..slashIndex];
                // Append the path portion to the blob prefix
                var extraPath = containerName[(slashIndex + 1)..].TrimEnd('/');
                _blobPrefix = string.IsNullOrEmpty(blobPrefix)
                    ? extraPath
                    : $"{extraPath}/{blobPrefix?.TrimStart('/')}";
            }
            else
            {
                _containerName = containerName;
                _blobPrefix = blobPrefix ?? "";
            }
            _isConfigured = !string.IsNullOrWhiteSpace(_connectionString)
                || (!string.IsNullOrWhiteSpace(_sasToken) && !string.IsNullOrWhiteSpace(_storageAccountName));

            _logger.LogInformation("Azure Blob Export reconfigured: auth={AuthMode}, container={Container}", AuthMode, _containerName);
            OnConfigChanged?.Invoke();
        }

        /// <summary>
        /// Persists the current configuration to appsettings.json with credentials encrypted.
        /// </summary>
        public void SaveToConfig()
        {
            try
            {
                var configPath = Path.Combine(AppContext.BaseDirectory, "Config", "appsettings.json");
                if (!File.Exists(configPath)) return;

                var json = File.ReadAllText(configPath);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = new Dictionary<string, object>();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Name == "AzureBlobExport")
                    {
                        root[prop.Name] = new Dictionary<string, object>
                        {
                            ["ConnectionString"] = !string.IsNullOrEmpty(_connectionString) ? CredentialProtector.Encrypt(_connectionString) : "",
                            ["SasToken"] = !string.IsNullOrEmpty(_sasToken) ? CredentialProtector.Encrypt(_sasToken) : "",
                            ["StorageAccountName"] = _storageAccountName ?? "",
                            ["ContainerName"] = _containerName ?? "SQLTriage",
                            ["BlobPrefix"] = _blobPrefix ?? "",
                            ["CompressUploads"] = _compressUploads.ToString().ToLower(),
                            ["UploadMethod"] = _uploadMethod,
                            ["AzCopyPath"] = _azcopyPath ?? "",
                            ["AutoUploadCsvs"] = _autoUploadCsvs.ToString().ToLower()
                        };
                    }
                    else
                    {
                        root[prop.Name] = prop.Value;
                    }
                }

                var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = false };
                var updatedJson = System.Text.Json.JsonSerializer.Serialize(root, options);
                File.WriteAllText(configPath, updatedJson);
                _logger.LogInformation("Azure Blob Export config saved to appsettings.json (credentials encrypted)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save Azure Blob Export config");
            }
        }

        /// <summary>
        /// Tests connectivity to Azure Blob Storage by attempting to access the container.
        /// Creates the container if it doesn't exist.
        /// </summary>
        public async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default)
        {
            if (!_isConfigured)
                return (false, "Azure Blob Storage is not configured. Provide a connection string or SAS token.");

            // AzCopy test: just check the exe exists
            if (_uploadMethod == "azcopy")
            {
                var azPath = ResolveAzCopyPath();
                if (azPath == null)
                    return (false, "AzCopy.exe not found. Specify the path or ensure it is on your system PATH.");
                return (true, $"AzCopy found at: {azPath}. Auth mode: {AuthMode}");
            }

            try
            {
                var containerClient = GetContainerClient();

                // 1. Try GetProperties — works with full account/container SAS
                try
                {
                    var properties = await containerClient.GetPropertiesAsync(cancellationToken: ct);
                    return (true, $"Connected to container '{_containerName}' ({AuthMode}). Last modified: {properties.Value.LastModified:u}");
                }
                catch (Azure.RequestFailedException) { /* SAS may not have read/list — try next */ }

                // 2. Try listing — works with list-scoped SAS
                try
                {
                    var prefix = string.IsNullOrEmpty(_blobPrefix) ? null : _blobPrefix.TrimEnd('/') + "/";
                    int count = 0;
                    await foreach (var blob in containerClient.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, ct))
                    {
                        count++;
                        if (count >= 1) break;
                    }
                    return (true, $"Connected to container '{_containerName}'{(prefix != null ? $"/{_blobPrefix}" : "")} ({AuthMode}). Listed {count} blob(s).");
                }
                catch (Azure.RequestFailedException) { /* SAS may be write-only — try probe upload */ }

                // 3. Write-only SAS: upload a tiny probe blob then delete it
                var probeName = string.IsNullOrEmpty(_blobPrefix)
                    ? ".test-probe"
                    : _blobPrefix.TrimEnd('/') + "/.test-probe";
                var blobClient = containerClient.GetBlobClient(probeName);
                using var probeStream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes("probe"));
                await blobClient.UploadAsync(probeStream, overwrite: true, ct);
                try { await blobClient.DeleteIfExistsAsync(cancellationToken: ct); } catch { /* best-effort */ }
                return (true, $"Connected to container '{_containerName}' ({AuthMode}). Write access confirmed.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Azure Blob connection test failed");
                return (false, $"Connection failed: {ex.Message}");
            }
        }

        private BlobContainerClient GetContainerClient()
        {
            if (!string.IsNullOrWhiteSpace(_connectionString))
            {
                var client = new BlobServiceClient(_connectionString);
                return client.GetBlobContainerClient(_containerName);
            }

            // SAS token auth — use BlobServiceClient so the container name isn't doubled
            var serviceUri = new Uri($"https://{_storageAccountName}.blob.core.windows.net?{_sasToken!.TrimStart('?')}");
            var serviceClient = new BlobServiceClient(serviceUri);
            return serviceClient.GetBlobContainerClient(_containerName);
        }

        // ──────────────── Export Methods ────────────────

        /// <summary>
        /// Exports a DataTable as a CSV file to Azure Blob Storage.
        /// Returns the blob URI on success.
        /// </summary>
        public async Task<ExportResult> ExportDataTableAsync(
            DataTable data,
            string exportName,
            string? serverName = null,
            CancellationToken ct = default)
        {
            if (!_isConfigured)
                return ExportResult.Fail("Azure Blob Storage is not configured.");

            var csv = DataTableToCsv(data);
            var blobName = BuildBlobName(exportName, serverName);

            return await UploadCsvAsync(csv, blobName, exportName, data.Rows.Count, ct);
        }

        /// <summary>
        /// Exports a list of key-value results (e.g., Quick Check, audit results) as CSV.
        /// </summary>
        public async Task<ExportResult> ExportKeyValueResultsAsync(
            List<(string Category, string Check, string Status, string Detail)> results,
            string exportName,
            string? serverName = null,
            CancellationToken ct = default)
        {
            if (!_isConfigured)
                return ExportResult.Fail("Azure Blob Storage is not configured.");

            var sb = new StringBuilder();
            sb.AppendLine("Category,Check,Status,Detail");
            foreach (var (category, check, status, detail) in results)
            {
                sb.AppendLine($"{EscapeCsv(category)},{EscapeCsv(check)},{EscapeCsv(status)},{EscapeCsv(detail)}");
            }

            var blobName = BuildBlobName(exportName, serverName);
            return await UploadCsvAsync(sb.ToString(), blobName, exportName, results.Count, ct);
        }

        /// <summary>
        /// Exports raw CSV content (pre-formatted) to Azure Blob Storage.
        /// </summary>
        public async Task<ExportResult> ExportRawCsvAsync(
            string csvContent,
            string exportName,
            int rowCount = 0,
            string? serverName = null,
            CancellationToken ct = default)
        {
            if (!_isConfigured)
                return ExportResult.Fail("Azure Blob Storage is not configured.");

            var blobName = BuildBlobName(exportName, serverName);
            return await UploadCsvAsync(csvContent, blobName, exportName, rowCount, ct);
        }

        /// <summary>
        /// Uploads a single CSV file from disk to Azure Blob Storage.
        /// Called as a fire-and-forget step after local CSV generation.
        /// Returns silently on any error — never throws.
        /// </summary>
        public async Task<ExportResult> UploadLocalCsvAsync(string filePath, string? serverName = null, CancellationToken ct = default)
        {
            if (!_isConfigured || !_autoUploadCsvs)
                return ExportResult.Fail("Auto-upload not enabled or Azure not configured.");

            try
            {
                if (!File.Exists(filePath))
                    return ExportResult.Fail($"File not found: {filePath}");

                var csvContent = await File.ReadAllTextAsync(filePath, ct);
                var exportName = Path.GetFileNameWithoutExtension(filePath);
                var rowCount = csvContent.Split('\n').Length - 2; // rough: minus header and trailing newline

                // Local filename already contains server + timestamp — just prepend the blob prefix
                var fileName = Path.GetFileName(filePath);
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(_blobPrefix))
                    parts.Add(_blobPrefix.TrimEnd('/'));
                parts.Add(fileName);
                var blobName = string.Join("/", parts);
                var result = await UploadCsvAsync(csvContent, blobName, exportName, Math.Max(0, rowCount), ct);

                if (result.Success)
                    _logger.LogInformation("Auto-uploaded {FileName} to Azure Blob: {BlobName}", Path.GetFileName(filePath), result.BlobName);
                else
                    _logger.LogWarning("Auto-upload failed for {FileName}: {Message}", Path.GetFileName(filePath), result.Message);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto-upload failed for {FileName} (likely firewall/network issue)", Path.GetFileName(filePath));
                return ExportResult.Fail($"Upload failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Batch-uploads all CSV files from the output folder to Azure Blob Storage.
        /// Designed to run after an audit sequence completes. Never throws.
        /// Returns a summary of successes/failures for notification purposes.
        /// </summary>
        public async Task<(int Uploaded, int Failed, List<string> Errors)> UploadOutputFolderCsvsAsync(
            IEnumerable<string> csvFilePaths, string? serverName = null, CancellationToken ct = default)
        {
            int uploaded = 0, failed = 0;
            var errors = new List<string>();

            if (!_isConfigured || !_autoUploadCsvs)
            {
                errors.Add("Auto-upload not enabled or Azure not configured.");
                return (0, 0, errors);
            }

            foreach (var filePath in csvFilePaths)
            {
                if (ct.IsCancellationRequested) break;

                var result = await UploadLocalCsvAsync(filePath, serverName, ct);
                if (result.Success)
                    uploaded++;
                else
                {
                    failed++;
                    errors.Add($"{Path.GetFileName(filePath)}: {result.Message}");
                }
            }

            if (uploaded > 0)
                _logger.LogInformation("Azure auto-upload batch complete: {Uploaded} uploaded, {Failed} failed", uploaded, failed);
            if (failed > 0)
                _logger.LogWarning("Azure auto-upload: {Failed} file(s) failed — {Errors}", failed, string.Join("; ", errors));

            return (uploaded, failed, errors);
        }

        /// <summary>
        /// Lists recent exports in the blob container.
        /// </summary>
        public async Task<List<BlobExportInfo>> ListExportsAsync(int maxResults = 50, CancellationToken ct = default)
        {
            if (!_isConfigured)
                return new List<BlobExportInfo>();

            var results = new List<BlobExportInfo>();

            try
            {
                var containerClient = GetContainerClient();

                var prefix = string.IsNullOrEmpty(_blobPrefix) ? "" : _blobPrefix.TrimEnd('/') + "/";

                await foreach (var blob in containerClient.GetBlobsAsync(
                    traits: BlobTraits.Metadata, states: BlobStates.None,
                    prefix: prefix, cancellationToken: ct))
                {
                    results.Add(new BlobExportInfo
                    {
                        Name = blob.Name,
                        Size = blob.Properties.ContentLength ?? 0,
                        LastModified = blob.Properties.LastModified?.UtcDateTime ?? DateTime.MinValue,
                        ContentType = blob.Properties.ContentType
                    });

                    if (results.Count >= maxResults) break;
                }

                // Sort by most recent first
                results.Sort((a, b) => b.LastModified.CompareTo(a.LastModified));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list blob exports");
            }

            return results;
        }

        /// <summary>
        /// Deletes a specific export blob.
        /// </summary>
        public async Task<bool> DeleteExportAsync(string blobName, CancellationToken ct = default)
        {
            if (!_isConfigured) return false;

            try
            {
                var containerClient = GetContainerClient();
                var blobClient = containerClient.GetBlobClient(blobName);
                var response = await blobClient.DeleteIfExistsAsync(cancellationToken: ct);
                return response.Value;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete blob {BlobName}", blobName);
                return false;
            }
        }

        // ──────────────── Internal Helpers ────────────────

        private async Task<ExportResult> UploadCsvAsync(
            string csvContent, string blobName, string exportName, int rowCount, CancellationToken ct)
        {
            try
            {
                var rawBytes = Encoding.UTF8.GetBytes(csvContent);
                byte[] uploadBytes;

                if (_compressUploads)
                {
                    uploadBytes = GzipCompress(rawBytes);
                    blobName += ".gz";
                }
                else
                {
                    uploadBytes = rawBytes;
                }

                if (_uploadMethod == "azcopy")
                    return await UploadViaAzCopyAsync(uploadBytes, blobName, exportName, rowCount, rawBytes.Length, ct);

                return await UploadViaSdkAsync(uploadBytes, blobName, exportName, rowCount, rawBytes.Length, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upload CSV to blob {BlobName}", blobName);
                return ExportResult.Fail($"Upload failed: {ex.Message}");
            }
        }

        private async Task<ExportResult> UploadViaSdkAsync(
            byte[] uploadBytes, string blobName, string exportName, int rowCount, long originalSize, CancellationToken ct)
        {
            var containerClient = GetContainerClient();
            var blobClient = containerClient.GetBlobClient(blobName);
            using var stream = new MemoryStream(uploadBytes);

            var headers = new BlobHttpHeaders
            {
                ContentType = "text/csv",
                ContentEncoding = _compressUploads ? "gzip" : "utf-8"
            };

            await blobClient.UploadAsync(stream, new BlobUploadOptions
            {
                HttpHeaders = headers,
                Metadata = new Dictionary<string, string>
                {
                    ["exportName"] = exportName,
                    ["rowCount"] = rowCount.ToString(),
                    ["exportedAt"] = DateTime.UtcNow.ToString("o"),
                    ["machineName"] = Environment.MachineName,
                    ["appVersion"] = GetAppVersion(),
                    ["compressed"] = _compressUploads.ToString(),
                    ["originalSizeBytes"] = originalSize.ToString()
                }
            }, ct);

            var uri = blobClient.Uri.ToString();
            return BuildResult(blobName, exportName, rowCount, uploadBytes.Length, originalSize, uri);
        }

        private async Task<ExportResult> UploadViaAzCopyAsync(
            byte[] uploadBytes, string blobName, string exportName, int rowCount, long originalSize, CancellationToken ct)
        {
            var azPath = ResolveAzCopyPath()
                ?? throw new InvalidOperationException("AzCopy.exe not found. Set the path in Azure Blob settings.");

            // Write to a temp file
            var tempFile = Path.Combine(Path.GetTempPath(), $"sqlha-export-{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllBytesAsync(tempFile, uploadBytes, ct);

                // Build the destination URL
                string destUrl;
                if (!string.IsNullOrWhiteSpace(_connectionString))
                {
                    // Parse account name from connection string
                    var match = System.Text.RegularExpressions.Regex.Match(
                        _connectionString, @"AccountName=([^;]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    var account = match.Success ? match.Groups[1].Value : throw new InvalidOperationException("Cannot parse AccountName from connection string for AzCopy");
                    // Extract account key for SAS generation — or use the connection string directly isn't possible with azcopy
                    // AzCopy with connection string needs env var AZURE_STORAGE_CONNECTION_STRING
                    destUrl = $"https://{account}.blob.core.windows.net/{_containerName}/{blobName}";
                }
                else
                {
                    destUrl = $"https://{_storageAccountName}.blob.core.windows.net/{_containerName}/{blobName}?{_sasToken!.TrimStart('?')}";
                }

                _logger.LogInformation("AzCopy: {AzPath} copy \"{TempFile}\" \"{DestUrl}\"",
                    azPath, tempFile, destUrl.Split('?')[0] + "?<sas-redacted>");

                var psi = new System.Diagnostics.ProcessStartInfo(azPath, $"copy \"{tempFile}\" \"{destUrl}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                // If using connection string, pass via env var
                if (!string.IsNullOrWhiteSpace(_connectionString))
                    psi.Environment["AZURE_STORAGE_CONNECTION_STRING"] = _connectionString;

                using var proc = System.Diagnostics.Process.Start(psi)
                    ?? throw new InvalidOperationException("Failed to start AzCopy process");
                // Read stdout and stderr concurrently to avoid deadlock
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync(ct);
                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                if (proc.ExitCode != 0)
                {
                    // AzCopy v10 writes errors to stdout, not stderr
                    var detail = !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout;
                    _logger.LogWarning("AzCopy exit code {Code}.\nStdOut: {StdOut}\nStdErr: {StdErr}", proc.ExitCode, stdout, stderr);
                    return ExportResult.Fail($"AzCopy failed (exit {proc.ExitCode}): {detail}");
                }

                return BuildResult(blobName, exportName, rowCount, uploadBytes.Length, originalSize, destUrl.Split('?')[0]);
            }
            finally
            {
                try { File.Delete(tempFile); } catch (Exception ex) { _logger.LogDebug(ex, "[AzureBlob] Failed to delete temp file: {Path}", tempFile); }
            }
        }

        private ExportResult BuildResult(string blobName, string exportName, int rowCount, long uploadSize, long originalSize, string uri)
        {
            var ratio = _compressUploads
                ? $" (compressed {FormatSize(originalSize)} -> {FormatSize(uploadSize)}, {(1.0 - (double)uploadSize / originalSize) * 100:F0}% saved)"
                : "";

            _logger.LogInformation("Exported {ExportName} to blob {BlobName} ({Rows} rows, {Size} bytes{Ratio})",
                exportName, blobName, rowCount, uploadSize, ratio);

            _auditLog?.LogExportOperation("AzureBlob", blobName, true);

            return new ExportResult
            {
                Success = true,
                BlobName = blobName,
                BlobUri = uri,
                RowCount = rowCount,
                SizeBytes = uploadSize,
                Message = _compressUploads
                    ? $"Exported {rowCount} rows to {blobName} ({FormatSize(originalSize)} -> {FormatSize(uploadSize)} compressed)"
                    : $"Exported {rowCount} rows to {blobName}"
            };
        }

        private string? ResolveAzCopyPath()
        {
            // User-configured path first
            if (!string.IsNullOrWhiteSpace(_azcopyPath) && File.Exists(_azcopyPath))
                return _azcopyPath;

            // Check common locations
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "azcopy.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SDKs", "Azure", "AzCopy", "azcopy.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Azure", "AzCopy", "azcopy.exe")
            };
            foreach (var path in candidates)
                if (File.Exists(path)) return path;

            // Check PATH
            try
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var full = Path.Combine(dir.Trim(), "azcopy.exe");
                    if (File.Exists(full)) return full;
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "[AzureBlob] Error scanning PATH for azcopy"); }

            return null;
        }

        private static byte[] GzipCompress(byte[] data)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }

        private static string FormatSize(long bytes)
        {
            if (bytes > 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
            if (bytes > 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes} B";
        }

        private string BuildBlobName(string exportName, string? serverName)
        {
            var date = DateTime.UtcNow;
            var safeName = SanitizeBlobName(exportName);
            var safeServer = string.IsNullOrEmpty(serverName) ? "default" : SanitizeBlobName(serverName);
            var timestamp = date.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(_blobPrefix))
                parts.Add(_blobPrefix.TrimEnd('/'));
            parts.Add($"{safeName}_{safeServer}_{timestamp}.csv");

            return string.Join("/", parts);
        }

        private static string SanitizeBlobName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')
                    sb.Append(c);
                else
                    sb.Append('_');
            }
            return sb.ToString();
        }

        // ──────────────── CSV Generation ────────────────

        /// <summary>
        /// Converts a DataTable to a CSV string with proper escaping.
        /// </summary>
        public static string DataTableToCsv(DataTable table)
        {
            var sb = new StringBuilder();

            // Header row
            for (int i = 0; i < table.Columns.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(EscapeCsv(table.Columns[i].ColumnName));
            }
            sb.AppendLine();

            // Data rows
            foreach (DataRow row in table.Rows)
            {
                for (int i = 0; i < table.Columns.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var val = row[i];
                    if (val == null || val == DBNull.Value)
                        sb.Append("");
                    else
                        sb.Append(EscapeCsv(val.ToString() ?? ""));
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static string EscapeCsv(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            return value;
        }

        private static string GetAppVersion()
        {
            try
            {
                var versionPath = Path.Combine(AppContext.BaseDirectory, "Config", "version.json");
                if (File.Exists(versionPath))
                {
                    var json = File.ReadAllText(versionPath);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    return doc.RootElement.GetProperty("version").GetString() ?? "unknown";
                }
            }
            catch (Exception ex) { Serilog.Log.Debug(ex, "[AzureBlob] Failed to read version.json"); }
            return "unknown";
        }

        /// <summary>
        /// Returns a dictionary of connection diagnostics (no secrets) for troubleshooting.
        /// </summary>
        public Dictionary<string, string> GetDiagnostics()
        {
            var diag = new Dictionary<string, string>
            {
                ["Auth Mode"] = AuthMode,
                ["Is Configured"] = _isConfigured.ToString(),
                ["Container Name"] = _containerName ?? "(null)",
                ["Blob Prefix"] = string.IsNullOrEmpty(_blobPrefix) ? "(none)" : _blobPrefix,
                ["Upload Method"] = _uploadMethod,
                ["Compress Uploads"] = _compressUploads.ToString(),
                ["Auto Upload CSVs"] = _autoUploadCsvs.ToString()
            };

            if (AuthMode == "connectionstring" && !string.IsNullOrWhiteSpace(_connectionString))
            {
                // Extract account name from connection string without revealing the key
                var match = System.Text.RegularExpressions.Regex.Match(
                    _connectionString, @"AccountName=([^;]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                diag["Account Name (from ConnStr)"] = match.Success ? match.Groups[1].Value : "(could not parse)";

                var endpointMatch = System.Text.RegularExpressions.Regex.Match(
                    _connectionString, @"BlobEndpoint=([^;]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (endpointMatch.Success)
                    diag["Blob Endpoint (from ConnStr)"] = endpointMatch.Groups[1].Value;

                var protocolMatch = System.Text.RegularExpressions.Regex.Match(
                    _connectionString, @"DefaultEndpointsProtocol=([^;]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                diag["Protocol"] = protocolMatch.Success ? protocolMatch.Groups[1].Value : "(default)";

                diag["Account Key Present"] = _connectionString.Contains("AccountKey=", StringComparison.OrdinalIgnoreCase) ? "Yes" : "No";
                diag["SharedAccessSignature in ConnStr"] = _connectionString.Contains("SharedAccessSignature=", StringComparison.OrdinalIgnoreCase) ? "Yes" : "No";
            }
            else if (AuthMode == "sastoken")
            {
                diag["Storage Account Name"] = _storageAccountName ?? "(null)";
                diag["Constructed Endpoint"] = $"https://{_storageAccountName}.blob.core.windows.net/{_containerName}";

                if (!string.IsNullOrWhiteSpace(_sasToken))
                {
                    // Parse SAS token parameters (these are not secret — only the sig is)
                    var token = _sasToken.TrimStart('?');
                    foreach (var param in token.Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = param.Split('=', 2);
                        if (parts.Length != 2) continue;
                        var key = parts[0];
                        var val = parts[1];
                        var label = key switch
                        {
                            "sv" => "SAS API Version (sv)",
                            "ss" => "SAS Services (ss)",
                            "srt" => "SAS Resource Types (srt)",
                            "sp" => "SAS Permissions (sp)",
                            "se" => "SAS Expiry (se)",
                            "st" => "SAS Start (st)",
                            "spr" => "SAS Protocol (spr)",
                            "sig" => "SAS Signature (sig)",
                            "sr" => "SAS Resource (sr)",
                            _ => $"SAS {key}"
                        };
                        // Mask the signature
                        diag[label] = key == "sig" ? val[..Math.Min(8, val.Length)] + "..." : Uri.UnescapeDataString(val);
                    }
                }
                else
                {
                    diag["SAS Token"] = "(empty)";
                }
            }
            else
            {
                diag["Note"] = "No credentials configured";
            }

            if (_uploadMethod == "azcopy")
            {
                var azPath = ResolveAzCopyPath();
                diag["AzCopy Path"] = azPath ?? "(not found)";
            }

            return diag;
        }

        private static string DecryptIfNeeded(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (CredentialProtector.IsEncrypted(value))
                return CredentialProtector.Decrypt(value);
            return value;
        }
    }

    public class ExportResult
    {
        public bool Success { get; set; }
        public string? BlobName { get; set; }
        public string? BlobUri { get; set; }
        public int RowCount { get; set; }
        public long SizeBytes { get; set; }
        public string Message { get; set; } = "";

        public static ExportResult Fail(string message) => new() { Success = false, Message = message };
    }

    public class BlobExportInfo
    {
        public string Name { get; set; } = "";
        public long Size { get; set; }
        public DateTime LastModified { get; set; }
        public string? ContentType { get; set; }
    }
}
