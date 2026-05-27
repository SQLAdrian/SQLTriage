using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SQLTriage.Data;
using SQLTriage.Data.Models;
using SQLTriage.Data.Scheduling;
using SQLTriage.Data.Services;
namespace SQLTriage.Pages;
public partial class Settings
{
    // ── L2: A1.2 — Uptime ────────────────────────────────────────────────
    private double? _uptimePercent30d;
    private string? _uptimeMessage;
    private bool _uptimeSuccess;

    private void ComputeUptime30d()
    {
        try
        {
            var to = DateTime.UtcNow;
            var from = to.AddDays(-30);
            _uptimePercent30d = UptimeTracker.GetUptimePercent(from, to);
            _uptimeMessage = null;
        }
        catch (Exception ex)
        {
            _uptimeMessage = $"Failed: {ex.Message}";
            _uptimeSuccess = false;
        }
    }

    private void ExportUptimeSnapshot()
    {
        if (!_uptimePercent30d.HasValue) return;
        try
        {
            var to = DateTime.UtcNow;
            var from = to.AddDays(-30);
            var downloadsDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var exportDir = Path.Combine(downloadsDir, "Downloads");
            if (!Directory.Exists(exportDir)) exportDir = AppDomain.CurrentDomain.BaseDirectory;
            var fileName = $"UptimeSnapshot_{to:yyyyMMdd_HHmmss}.csv";
            var filePath = Path.Combine(exportDir, fileName);
            var csv = $"from_utc,to_utc,uptime_percent\r\n{from:o},{to:o},{_uptimePercent30d.Value:F4}\r\n";
            File.WriteAllText(filePath, csv, System.Text.Encoding.UTF8);
            AuditLog.LogUptimeSnapshotExported(ReviewerName, _uptimePercent30d.Value, from, to);
            _uptimeMessage = $"Saved: {filePath}";
            _uptimeSuccess = true;
        }
        catch (Exception ex)
        {
            _uptimeMessage = $"Export failed: {ex.Message}";
            _uptimeSuccess = false;
        }
    }

    // ── L3: CP-2 — Continuity ────────────────────────────────────────────
    private string? _drTestMessage;

    private void RecordDrTest()
    {
        try
        {
            // Write today's date into appsettings.json under Continuity:LastDrTestDate.
            var appsettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            var json = File.ReadAllText(appsettingsPath);
            var config = JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new();

            string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            if (!config.ContainsKey("Continuity"))
                config["Continuity"] = new Dictionary<string, object>();

            // The value may deserialise as JsonElement; replace with plain dict.
            config["Continuity"] = new Dictionary<string, object>
            {
                ["RecoveryPointObjectiveMinutes"] = Configuration.GetValue<int>("Continuity:RecoveryPointObjectiveMinutes", 0),
                ["RecoveryTimeObjectiveMinutes"] = Configuration.GetValue<int>("Continuity:RecoveryTimeObjectiveMinutes", 0),
                ["LastDrTestDate"] = today
            };

            File.WriteAllText(appsettingsPath,
                JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

            AuditLog.LogDrTestRecorded(ReviewerName, $"DR test recorded via Settings on {today}");
            _drTestMessage = $"DR test recorded: {today}";
        }
        catch (Exception ex)
        {
            _drTestMessage = $"Failed: {ex.Message}";
        }
        StateHasChanged();
    }

    // ── L4: CM-3 — Config Baseline ───────────────────────────────────────
    private bool _showRebaselineConfirm;
    private string? _rebaselineMessage;

    private void ConfirmRebaseline()
    {
        _showRebaselineConfirm = false;
        try
        {
            ConfigBaselineSvc.ReBaseline(ReviewerName);
            _rebaselineMessage = "Baseline updated — current config accepted as new reference.";
        }
        catch (Exception ex)
        {
            _rebaselineMessage = $"Re-baseline failed: {ex.Message}";
        }
        StateHasChanged();
    }

    // ── L6: AU-9 — HMAC Key Rotation ─────────────────────────────────────
    private bool _showRotateConfirm;
    private string? _rotateMessage;
    private bool _rotateSuccess;

    private void ConfirmRotateHmacKey()
    {
        _showRotateConfirm = false;
        try
        {
            AuditLog.RotateHmacKey(ReviewerName, Configuration);
            _rotateMessage = "HMAC key rotated. Chain-anchor entry appended. New key is active.";
            _rotateSuccess = true;
        }
        catch (Exception ex)
        {
            _rotateMessage = $"Rotation failed: {ex.Message}";
            _rotateSuccess = false;
        }
        StateHasChanged();
    }
    private string? TestResult;
    private bool TestSuccess;
    private string SelectedDataSource = "SqlServer";
    private bool RestartRequired;
    private string SqlServerConnectionString = "";
    private bool TrustServerCertificate = false;
    private bool _isEditingConnectionString = false;
    private string? SaveResult;
    private bool SaveSuccess;
    private bool ShowDiagnosticPane;
    private bool _debugLogging;
    private bool _anonymiseServerNames;
    private bool _useV2PlanIcons;
    private bool _noPantsMode;
    private bool _experimentalMode;
    private string DefaultDashboardId = "";
    private string? DashboardSaveResult;
    private string? ConfigValidationResult;
    private bool ConfigValidationSuccess;
    private List<string> ConfigErrors = new();
    private bool IsCheckingUpdates;
    private string? UpdateCheckResult;
    private bool UpdateAvailable;
    private string _updateProxyUrl = "";
    private UpdateInfo? UpdateInfo;
    private bool _downloading;
    private int _downloadProgress;
    private bool IsFlushingCache;
    private string? FlushCacheResult;
    private bool FlushCacheSuccess;
    private int _chartDataPointCap = 2000;

    // ── Query Concurrency ──
    private int _maxHeavy = 5;
    private int _maxLight = 10;
    private string? _concurrencyMessage;

    // ── Cache Metrics ──
    private bool _cacheMetricsLoading = false;
    private SQLTriage.Data.Services.CacheMetricsService.SessionMetrics _sessionMetrics = new();
    private double _hourlyCachePercentage;
    private double _dailyCachePercentage;
    private int _hourlyTotalQueries;
    private int _dailyTotalQueries;
    private int _hourlyDataPoints;
    private int _dailyDataPoints;
    private SQLTriage.Data.Services.CacheMetricsService? _cacheMetricsService;

    // ── Credential Export/Import ──
    private string _credsExportPassphrase = "";
    private string _credsExportConfirm = "";
    private string _credsImportPassphrase = "";
    private string? _credsFileContent;
    private bool _credsExporting;
    private bool _credsImporting;
    private string? _credsMessage;
    private bool _credsSuccess;
    private bool IsRunningMaintenance;
    private string? MaintenanceResult;
    private bool MaintenanceSuccess;
    private string CacheFileSize = "N/A";

    private int _zoomLevel = 150;
    private string _selectedTheme = "default";

    // Azure Blob Export
    private string _azureAuthMode = "connectionstring";
    private string? _azureConnStr;
    private string? _azureSasToken;
    private string? _azureAccountName;
    private string _azureContainer = "SQLTriage";
    private string? _azurePrefix;
    private string _azureUploadMethod = "sdk";
    private string? _azureAzCopyPath;
    private bool _azureCompress;
    private bool _azureAutoUpload;
    private bool _hasExistingAzureConnStr;
    private bool _hasExistingAzureSas;
    private bool _testingAzure;
    private string? _azureResult;
    private bool _azureSuccess;
    private bool _showAzureDiag;
    private Dictionary<string, string>? _azureDiagData;
    private bool _uploadingOutput;
    private bool _showUploadConfirm;
    private bool _showSqldbaConsent;
    private List<string> _outputFiles = new();

    // Auto-Export
    private bool _autoExportAuditCsv;
    private bool _autoExportAuditJson;
    private bool _autoExportAuditPdf;
    private bool _autoExportQuickCheckCsv;
    private bool _autoExportQuickCheckPdf;
    private bool _autoExportVaCsv;
    private bool _autoExportVaPdf;

    // Alert Baseline
    private bool _alertBaselineEnabled;
    private bool _alertBaselinePerServer;

    // Threshold Highlighting
    private bool _thresholdsEnabled;
    private int _thresholdCpuMs;
    private int _thresholdWaitTimeMs;
    private int _thresholdMemoryMb;
    private int _thresholdReadsKb;
    private int _thresholdWritesKb;
    private int _thresholdDurationMs;
    private string? _thresholdSaveMessage;

    // Notifications
    private bool _showReleaseNotesOnUpdate;

    // Preview features
    private bool _showMaturityRoadmap;
    private bool _noPantsModeActive;
    private bool _experimentalModeActive;
    private bool _enablePerfInspector;

    // Tuning knobs
    private int _globalConcurrency = 45;
    private int _perServerConcurrency = 13;
    private int _channelCapacity = 1000;
    private int _defaultTimeoutSeconds = 60;
    private int _maxPanelsInFlight = 10;
    private int _renderCoalesceMs = 50;
    private bool _preloadFromCacheOnInit = true;
    private int _batchWriteSize = 50;
    private int _batchFlushMs = 100;
    private int _hotTierSlidingSeconds = 90;

    // Remediation cost reporting
    private bool _showRemediationCost = true;
    private string _remediationRate = "295";
    private string _complianceTier = "None";
    private string _consultancyName = "";
    private string _engagementDuration = "";
    private double _monthlyOpexPerServerNZD = 400.0;

    private void LoadSettings()
    {
        ShowDiagnosticPane = UserSettings.GetShowDiagnosticPane();
        _debugLogging = UserSettings.GetDebugLogging();
        _anonymiseServerNames = UserSettings.GetAnonymiseServerNames();
        LogAnon.Enabled = _anonymiseServerNames;
        _useV2PlanIcons = UserSettings.GetUseV2PlanIcons();
        _noPantsMode = UserSettings.GetNoPantsMode();
        _experimentalMode = UserSettings.GetExperimentalMode();
        _alertBaselineEnabled = UserSettings.GetAlertBaselineEnabled();
        _alertBaselinePerServer = UserSettings.GetAlertBaselinePerServer();
        _thresholdsEnabled = UserSettings.GetThresholdsEnabled();
        _thresholdCpuMs = UserSettings.GetThresholdCpuMs();
        _thresholdWaitTimeMs = UserSettings.GetThresholdWaitTimeMs();
        _thresholdMemoryMb = UserSettings.GetThresholdMemoryMb();
        _thresholdReadsKb = UserSettings.GetThresholdReadsKb();
        _thresholdWritesKb = UserSettings.GetThresholdWritesKb();
        _thresholdDurationMs = UserSettings.GetThresholdDurationMs();
        DefaultDashboardId = UserSettings.GetDefaultDashboardId();
        _zoomLevel = UserSettings.GetZoomLevel();
        _selectedTheme = UserSettings.GetSelectedTheme();
        _showRemediationCost = UserSettings.GetShowRemediationCost();
        _remediationRate = ((int)UserSettings.GetRemediationHourlyRate()).ToString();
        _complianceTier = UserSettings.GetComplianceTier();
        _consultancyName = UserSettings.GetConsultancyName();
        _engagementDuration = UserSettings.GetEngagementDuration();
        _monthlyOpexPerServerNZD = UserSettings.GetMonthlyOpexPerServerNZD();
    }

    private void ToggleShowRemediationCost(ChangeEventArgs e)
    {
        _showRemediationCost = (bool)(e.Value ?? false);
        UserSettings.SetShowRemediationCost(_showRemediationCost);
    }

    private void OnRemediationRateChanged(ChangeEventArgs e)
    {
        _remediationRate = e.Value?.ToString() ?? "295";
        if (double.TryParse(_remediationRate, out var rate))
            UserSettings.SetRemediationHourlyRate(rate);
    }

    private void OnComplianceTierChanged(ChangeEventArgs e)
    {
        _complianceTier = e.Value?.ToString() ?? "None";
        UserSettings.SetComplianceTier(_complianceTier);
    }

    private void OnConsultancyNameChanged(ChangeEventArgs e)
    {
        _consultancyName = e.Value?.ToString() ?? "";
        UserSettings.SetConsultancyName(_consultancyName);
    }

    private void OnEngagementDurationChanged(ChangeEventArgs e)
    {
        _engagementDuration = e.Value?.ToString() ?? "";
        UserSettings.SetEngagementDuration(_engagementDuration);
    }

    private void OnMonthlyOpexChanged(ChangeEventArgs e)
    {
        if (double.TryParse(e.Value?.ToString(), out var opex))
        {
            _monthlyOpexPerServerNZD = opex;
            UserSettings.SetMonthlyOpexPerServerNZD(opex);
        }
    }

    protected override void OnInitialized()
    {
        // Load configuration
        SelectedDataSource = Configuration["DataSource"] ?? "SqlServer";
        var connStr = Configuration.GetConnectionString("SqlServer") ?? "";
        TrustServerCertificate = Configuration.GetValue<bool>("TrustServerCertificate", false);

        // Mask the password in the connection string for display
        SqlServerConnectionString = MaskConnectionStringPassword(connStr);
        // Load user settings
        LoadSettings();

        // Get cache file size
        UpdateCacheFileSize();

        _chartDataPointCap = UserSettings.GetChartDataPointCap();
        _maxHeavy = UserSettings.GetMaxHeavyConcurrent();
        _maxLight = UserSettings.GetMaxLightConcurrent();
        _updateProxyUrl = UserSettings.GetUpdateProxyUrl() ?? "";
        _enablePerfInspector = UserSettings.GetEnablePerfInspector();

        // Set performance inspector enabled
        var perfService = (PerformanceInspectorService?)App.Services?.GetService(typeof(PerformanceInspectorService));
        perfService?.SetEnabled(_enablePerfInspector);

        // Load tuning values from config
        _globalConcurrency = Configuration.GetValue<int>("Orchestrator:GlobalConcurrency", 5);
        _perServerConcurrency = Configuration.GetValue<int>("Orchestrator:PerServerConcurrency", 3);
        _channelCapacity = Configuration.GetValue<int>("Orchestrator:ChannelCapacity", 1000);
        _defaultTimeoutSeconds = Configuration.GetValue<int>("Orchestrator:DefaultTimeoutSeconds", 60);
        _maxPanelsInFlight = Configuration.GetValue<int>("Dashboard:MaxPanelsInFlight", 10);
        _renderCoalesceMs = Configuration.GetValue<int>("Dashboard:RenderCoalesceMs", 50);
        _preloadFromCacheOnInit = Configuration.GetValue<bool>("Dashboard:PreloadFromCacheOnInit", true);
        _batchWriteSize = Configuration.GetValue<int>("Cache:BatchWriteSize", 50);
        _batchFlushMs = Configuration.GetValue<int>("Cache:BatchFlushMs", 100);
        _hotTierSlidingSeconds = Configuration.GetValue<int>("Cache:HotTierSlidingSeconds", 90);

        // Load cache metrics service and current session stats
        _cacheMetricsService = CacheMetrics;
        _sessionMetrics = _cacheMetricsService?.GetCurrentSessionMetrics() ?? new SQLTriage.Data.Services.CacheMetricsService.SessionMetrics();

        // Load cache metrics service and current session stats
        _cacheMetricsService = CacheMetrics;
        _sessionMetrics = _cacheMetricsService?.GetCurrentSessionMetrics() ?? new SQLTriage.Data.Services.CacheMetricsService.SessionMetrics();

        // Load Azure Blob config
        _azureAuthMode = BlobExport.AuthMode == "sastoken" ? "sastoken" : "connectionstring";
        _azureContainer = BlobExport.ContainerName ?? "SQLTriage";
        _azurePrefix = BlobExport.BlobPrefix;
        _azureUploadMethod = BlobExport.UploadMethod ?? "sdk";
        _azureAzCopyPath = BlobExport.AzCopyPath;
        _azureCompress = BlobExport.CompressUploads;
        _azureAutoUpload = BlobExport.AutoUploadCsvs;
        _azureAccountName = BlobExport.StorageAccountName;
        _hasExistingAzureConnStr = BlobExport.AuthMode == "connectionstring" && BlobExport.IsConfigured;
        _hasExistingAzureSas = BlobExport.AuthMode == "sastoken" && BlobExport.IsConfigured;

        // Load auto-export settings
        var s = UserSettings.GetSettings();
        _autoExportAuditCsv = s.AutoExportAuditCsv;
        _autoExportAuditJson = s.AutoExportAuditJson;
        _autoExportAuditPdf = s.AutoExportAuditPdf;
        _autoExportQuickCheckCsv = s.AutoExportQuickCheckCsv;
        _autoExportQuickCheckPdf = s.AutoExportQuickCheckPdf;
        _autoExportVaCsv = s.AutoExportVulnerabilityAssessmentCsv;
        _autoExportVaPdf = s.AutoExportVulnerabilityAssessmentPdf;

        // Load notification settings
        _showReleaseNotesOnUpdate = s.ShowReleaseNotesOnUpdate;

        // Load preview feature settings
        _showMaturityRoadmap = s.ShowMaturityRoadmap;
        _noPantsModeActive = s.NoPantsMode;
        _experimentalModeActive = s.ExperimentalMode;

        // Load RBAC settings
        LoadRbacSettings();

        // Load initial cache metrics (async, fire-and-forget)
        _ = Task.Run(RefreshCacheMetrics);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        // Scroll spy: highlight the rail link whose section is currently in view.
        // Uses IntersectionObserver so it's free of scroll-event throttling.
        try
        {
            await JS.InvokeVoidAsync("eval", @"
                (() => {
                    const links = document.querySelectorAll('.settings-rail a[href^=""#""]');
                    if (!links.length) return;
                    const byId = new Map();
                    links.forEach(a => byId.set(a.getAttribute('href').slice(1), a));
                    const setActive = (id) => {
                        links.forEach(a => a.classList.remove('active'));
                        const a = byId.get(id);
                        if (a) a.classList.add('active');
                    };
                    const groups = Array.from(document.querySelectorAll('.settings-group[id]'));
                    if (!groups.length) return;
                    const scrollRoot = document.querySelector('.app-content');
                    const io = new IntersectionObserver((entries) => {
                        // Pick the entry closest to the top that is intersecting.
                        const visible = entries
                            .filter(e => e.isIntersecting)
                            .sort((a, b) => a.boundingClientRect.top - b.boundingClientRect.top);
                        if (visible.length) setActive(visible[0].target.id);
                    }, { root: scrollRoot, rootMargin: '-10% 0px -70% 0px', threshold: 0 });
                    groups.forEach(g => io.observe(g));
                    // Initial highlight: first group
                    setActive(groups[0].id);
                    // Anchor links default to window scroll, but the actual scroll
                    // container in this app is .app-content — intercept and scroll
                    // that container instead so smooth-scroll + active highlight work.
                    links.forEach(a => {
                        a.addEventListener('click', (e) => {
                            const id = a.getAttribute('href').slice(1);
                            const target = document.getElementById(id);
                            if (!target || !scrollRoot) return;
                            e.preventDefault();
                            const rootRect = scrollRoot.getBoundingClientRect();
                            const targetRect = target.getBoundingClientRect();
                            const top = scrollRoot.scrollTop + (targetRect.top - rootRect.top) - 16;
                            scrollRoot.scrollTo({ top, behavior: 'smooth' });
                            setActive(id);
                        });
                    });
                })();
            ");
        }
        catch { /* JS may not be ready in pre-render */ }
    }

    private void SaveUpdateProxy()
    {
        var oldProxy = UserSettings.GetUpdateProxyUrl();
        UserSettings.SetUpdateProxyUrl(_updateProxyUrl);
        UpdateService.SetManualProxyUrl(_updateProxyUrl);
        AuditLog.LogConfigurationChange("Settings", "updated", oldProxy, _updateProxyUrl, ReviewerName);
        Toast.ShowSuccess(string.IsNullOrWhiteSpace(_updateProxyUrl)
            ? "Proxy cleared — using system proxy settings."
            : $"Proxy saved: {_updateProxyUrl}");
    }

    private void OnChartCapChanged(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out var cap))
        {
            _chartDataPointCap = cap;
            UserSettings.SetChartDataPointCap(cap);
        }
    }

    private void OnHeavyConcurrencyChanged(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out var val))
        {
            _maxHeavy = val;
        }
    }

    private void OnLightConcurrencyChanged(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out var val))
        {
            _maxLight = val;
        }
    }

    private void SaveConcurrencySettings()
    {
        try
        {
            var oldHeavy = UserSettings.GetMaxHeavyConcurrent();
            var oldLight = UserSettings.GetMaxLightConcurrent();
            UserSettings.SetMaxHeavyConcurrent(_maxHeavy);
            UserSettings.SetMaxLightConcurrent(_maxLight);

            // Update QueryOrchestrator limits at runtime
            try
            {
                QueryOrchestrator?.UpdateLimits(globalConcurrency: _maxHeavy + _maxLight, perServerConcurrency: Math.Max(1, _maxHeavy));
                _concurrencyMessage = $"Concurrency limits saved: Heavy={_maxHeavy}, Light={_maxLight}. Applied immediately.";
            }
            catch (Exception ex)
            {
                _concurrencyMessage = $"Settings saved. Restart may be required to apply new limits. Error: {ex.Message}";
            }
            AuditLog.LogConfigurationChange("Settings", "updated",
                $"Concurrency: Heavy={oldHeavy}, Light={oldLight}",
                $"Concurrency: Heavy={_maxHeavy}, Light={_maxLight}",
                ReviewerName);
        }
        catch (Exception ex)
        {
            _concurrencyMessage = $"Failed to save: {ex.Message}";
        }
        StateHasChanged();
    }

    private void ResetConcurrencyDefaults()
    {
        _maxHeavy = 5;
        _maxLight = 10;
        UserSettings.SetMaxHeavyConcurrent(5);
        UserSettings.SetMaxLightConcurrent(10);
        try
        {
            QueryOrchestrator?.UpdateLimits(globalConcurrency: 15, perServerConcurrency: 5);
        }
        catch { /* ignore */ }
        _concurrencyMessage = "Concurrency reset to defaults (Heavy=5, Light=10).";
        StateHasChanged();
    }

    private async Task RefreshCacheMetrics()
    {
        if (_cacheMetricsService == null) return;

        _cacheMetricsLoading = true;
        StateHasChanged();

        try
        {
            // Current session
            _sessionMetrics = _cacheMetricsService.GetCurrentSessionMetrics();

            // Last 24 hours (hourly)
            var hourly = await _cacheMetricsService.GetHourlyMetricsAsync(24);
            _hourlyTotalQueries = hourly.Sum(m => m.TotalQueries);
            _hourlyCachePercentage = hourly.Count > 0 ? hourly.Average(m => m.GetCachePercentage()) : 0;
            _hourlyDataPoints = hourly.Count;

            // Last 7 days (daily)
            var daily = await _cacheMetricsService.GetDailyMetricsAsync(7);
            _dailyTotalQueries = daily.Sum(m => m.TotalQueries);
            _dailyCachePercentage = daily.Count > 0 ? daily.Average(m => m.GetCachePercentage()) : 0;
            _dailyDataPoints = daily.Count;
        }
        catch (Exception ex)
        {
            _concurrencyMessage = $"Failed to load metrics: {ex.Message}";
        }
        finally
        {
            _cacheMetricsLoading = false;
            StateHasChanged();
        }
    }

    private async Task OnCredsFileSelected(InputFileChangeEventArgs e)
    {
        try
        {
            using var stream = e.File.OpenReadStream(maxAllowedSize: 1024 * 1024); // 1 MB cap
            using var reader = new System.IO.StreamReader(stream);
            _credsFileContent = await reader.ReadToEndAsync();
            _credsMessage = null;
        }
        catch (Exception ex)
        {
            _credsMessage = $"Could not read file: {ex.Message}";
            _credsSuccess = false;
        }
    }

    private async Task ExportCredentials()
    {
        _credsMessage = null;
        if (string.IsNullOrWhiteSpace(_credsExportPassphrase))
        {
            _credsMessage = "Enter a passphrase."; _credsSuccess = false; return;
        }
        if (_credsExportPassphrase != _credsExportConfirm)
        {
            _credsMessage = "Passphrases do not match."; _credsSuccess = false; return;
        }

        _credsExporting = true;
        try
        {
            var connections = ConnectionManager.GetConnections();
            var json = CredentialPorter.Export(connections, _credsExportPassphrase);
            var fileName = $"SQLTriage-credentials-{DateTime.Now:yyyyMMdd-HHmm}.lmcreds";
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            var base64 = Convert.ToBase64String(bytes);
            await JS.InvokeVoidAsync("blazorDownloadFile", fileName, "application/json", base64);
            _credsMessage = $"Exported {connections.Count} server(s) to {fileName}.";
            _credsSuccess = true;
            _credsExportPassphrase = "";
            _credsExportConfirm = "";
        }
        catch (Exception ex)
        {
            _credsMessage = $"Export failed: {ex.Message}";
            _credsSuccess = false;
        }
        finally
        {
            _credsExporting = false;
        }
    }

    private async Task ImportCredentials()
    {
        _credsMessage = null;
        if (_credsFileContent == null) { _credsMessage = "Select a .lmcreds file first."; _credsSuccess = false; return; }
        if (string.IsNullOrWhiteSpace(_credsImportPassphrase)) { _credsMessage = "Enter the passphrase."; _credsSuccess = false; return; }

        _credsImporting = true;
        try
        {
            var imported = CredentialPorter.Import(_credsFileContent, _credsImportPassphrase);
            var existing = ConnectionManager.GetConnections();
            var existingIds = new System.Collections.Generic.HashSet<string>(existing.Select(c => c.Id));

            int added = 0, skipped = 0;
            foreach (var conn in imported)
            {
                if (existingIds.Contains(conn.Id)) { skipped++; continue; }
                ConnectionManager.AddConnection(conn);
                added++;
            }

            _credsMessage = $"Import complete: {added} added, {skipped} skipped (already exist).";
            _credsSuccess = true;
            _credsFileContent = null;
            _credsImportPassphrase = "";
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            _credsMessage = "Wrong passphrase — could not decrypt the file.";
            _credsSuccess = false;
        }
        catch (Exception ex)
        {
            _credsMessage = $"Import failed: {ex.Message}";
            _credsSuccess = false;
        }
        finally
        {
            _credsImporting = false;
        }
        await Task.CompletedTask;
    }

    private void OnZoomLevelChanged(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out var zoom) && zoom >= 50 && zoom <= 300)
        {
            _zoomLevel = zoom;
            UserSettings.SetZoomLevel(zoom);
        }
    }

    private void OnThemeChanged(ChangeEventArgs e)
    {
        var theme = e.Value?.ToString();
        if (!string.IsNullOrEmpty(theme) && (theme == "default" || theme == "rolls-royce" || theme == "amg"))
        {
            _selectedTheme = theme;
            UserSettings.SetSelectedTheme(theme);
        }
    }

    private void ToggleDiagnosticPane(ChangeEventArgs e)
    {
        ShowDiagnosticPane = (bool)(e.Value ?? false);
        UserSettings.SetShowDiagnosticPane(ShowDiagnosticPane);
    }

    private void ToggleDebugLogging(ChangeEventArgs e)
    {
        _debugLogging = (bool)(e.Value ?? false);
        UserSettings.SetDebugLogging(_debugLogging);
    }

    private void ResetAllSettings()
    {
        UserSettings.ResetToDefaults();
        // Reload local field cache so the UI reflects the reset values
        LoadSettings();
        Toast.ShowSuccess("App settings reset to defaults.");
    }

    private void ToggleAnonymiseServerNames(ChangeEventArgs e)
    {
        _anonymiseServerNames = (bool)(e.Value ?? false);
        UserSettings.SetAnonymiseServerNames(_anonymiseServerNames);
        LogAnon.Enabled = _anonymiseServerNames;
        if (!_anonymiseServerNames) LogAnon.Reset();
    }

    private async Task ToggleV2PlanIcons(ChangeEventArgs e)
    {
        _useV2PlanIcons = (bool)(e.Value ?? false);
        UserSettings.SetUseV2PlanIcons(_useV2PlanIcons);
        try { await JS.InvokeVoidAsync("queryPlanInteropV2.setUseV2Icons", _useV2PlanIcons); } catch { }
    }

    private void OnNoPantsModeChanged()
    {
        _noPantsMode = !_noPantsMode;
        _noPantsModeActive = _noPantsMode;
        UserSettings.SetNoPantsMode(_noPantsMode);
        // disclaimer is implicitly accepted when enabled via Settings
        if (_noPantsMode) UserSettings.SetNoPantsDisclaimerAccepted(true);
    }

    private void OnExperimentalModeChanged(ChangeEventArgs e)
    {
        _experimentalMode = (bool)(e.Value ?? false);
        _experimentalModeActive = _experimentalMode;
        UserSettings.SetExperimentalMode(_experimentalMode);
    }

    private void SaveDefaultDashboard()
    {
        var oldDashboard = UserSettings.GetDefaultDashboardId();
        UserSettings.SetDefaultDashboardId(DefaultDashboardId);
        AuditLog.LogConfigurationChange("Settings", "updated", $"DefaultDashboard={oldDashboard}", $"DefaultDashboard={DefaultDashboardId}", ReviewerName);
        DashboardSaveResult = "Default dashboard saved successfully";
        StateHasChanged();
    }



    private string MaskConnectionStringPassword(string connStr)
    {
        if (string.IsNullOrEmpty(connStr))
            return "";

        try
        {
            var builder = new SqlConnectionStringBuilder(connStr);
            if (!string.IsNullOrEmpty(builder.Password))
            {
                builder.Password = "********";
                return builder.ConnectionString;
            }
        }
        catch
        {
            // If parsing fails, return as-is
        }
        return connStr;
    }

    private void ToggleConnectionStringVisibility()
    {
        _isEditingConnectionString = !_isEditingConnectionString;
    }

    private async Task OnDataSourceChanged(ChangeEventArgs e)
    {
        var newValue = e.Value?.ToString() ?? "SqlServer";
        if (newValue == SelectedDataSource)
            return;

        SelectedDataSource = newValue;

        var appSettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
        var json = await File.ReadAllTextAsync(appSettingsPath);
        using var doc = JsonDocument.Parse(json);
        var root = new Dictionary<string, object>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name == "DataSource")
                root[prop.Name] = newValue;
            else
                root[prop.Name] = prop.Value;
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        var updatedJson = JsonSerializer.Serialize(root, options);
        await File.WriteAllTextAsync(appSettingsPath, updatedJson);

        RestartRequired = true;
    }

    //private async Task SaveConnectionString()
    //{
    //    if (string.IsNullOrWhiteSpace(SqlServerConnectionString))
    //    {
    //        SaveResult = "Connection string cannot be empty";
    //        SaveSuccess = false;
    //        return;
    //    }
    //
    //    try
    //    {
    //        var appSettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
    //        var json = await File.ReadAllTextAsync(appSettingsPath);
    //        using var doc = JsonDocument.Parse(json);
    //        
    //        // Get the original connection string to preserve the password if user entered ********
    //        var originalConnStr = doc.RootElement
    //            .GetProperty("ConnectionStrings")
    //            .GetProperty("SqlServer")
    //            .GetString() ?? "";
    //        
    //        // Parse both to check if user provided new password or kept the placeholder
    //        var newConnStr = SqlServerConnectionString;
    //        try
    //        {
    //            var newBuilder = new SqlConnectionStringBuilder(newConnStr);
    //            var originalBuilder = new SqlConnectionStringBuilder(originalConnStr);
    //            
    //            // If new password is placeholder, keep the original password
    //            if (newBuilder.Password == "********" && !string.IsNullOrEmpty(originalBuilder.Password))
    //            {
    //                newBuilder.Password = originalBuilder.Password;
    //                newConnStr = newBuilder.ConnectionString;
    //            }
    //        }
    //        catch
    //        {
    //            // If parsing fails, use what the user provided
    //        }
    //
    //        var root = new Dictionary<string, object>();
    //        foreach (var prop in doc.RootElement.EnumerateObject())
    //        {
    //            if (prop.Name == "ConnectionStrings")
    //            {
    //                var connStrings = new Dictionary<string, string>();
    //                foreach (var connProp in prop.Value.EnumerateObject())
    //                {
    //                    if (connProp.Name == "SqlServer")
    //                        connStrings[connProp.Name] = newConnStr;
    //                    else if (connProp.Name == "sqlite")
    //                        connStrings[connProp.Name] = connProp.Value.GetString() ?? "";
    //                }
    //                root[prop.Name] = connStrings;
    //            }
    //            else if (prop.Name == "DataSource")
    //            {
    //                root[prop.Name] = prop.Value.GetString() ?? "SqlServer";
    //            }
    //            else if (prop.Name == "TrustServerCertificate")
    //            {
    //                root[prop.Name] = TrustServerCertificate;
    //            }
    //            else if (prop.Name == "RefreshIntervalSeconds")
    //            {
    //                root[prop.Name] = prop.Value.GetInt32();
    //            }
    //            else if (prop.Name == "DefaultTimeRangeMinutes")
    //            {
    //                root[prop.Name] = prop.Value.GetInt32();
    //            }
    //        }
    //
    //        // Ensure TrustServerCertificate is in the config
    //        if (!root.ContainsKey("TrustServerCertificate"))
    //        {
    //            root["TrustServerCertificate"] = TrustServerCertificate;
    //        }
    //
    //        var options = new JsonSerializerOptions { WriteIndented = true };
    //        var updatedJson = JsonSerializer.Serialize(root, options);
    //        await File.WriteAllTextAsync(appSettingsPath, updatedJson);
    //
    //        SaveResult = "Connection string saved and applied. No restart required.";
    //        SaveSuccess = true;
    //        RestartRequired = false;
    //        
    //        // Apply the new connection string without restart
    //        SqlServerConnectionFactory.UpdateConnectionString(newConnStr, TrustServerCertificate);
    //        
    //        // Re-mask the password in the UI
    //        SqlServerConnectionString = MaskConnectionStringPassword(newConnStr);
    //    }
    //    catch (Exception ex)
    //    {
    //        SaveResult = $"Failed to save: {ex.Message}";
    //        SaveSuccess = false;
    //    }
    //}

    private async Task TestConnection()
    {
        TestResult = "Testing...";
        try
        {
            using var conn = ConnectionFactory.CreateConnection();
            conn.Open();
            TestResult = $"Connected successfully to {ConnectionFactory.DataSourceType}";
            TestSuccess = true;
        }
        catch (Exception ex)
        {
            TestResult = $"Connection failed: {ex.Message}";
            TestSuccess = false;
        }
        await Task.CompletedTask;
    }

    private void SaveAutoExportSettings()
    {
        UserSettings.UpdateAutoExportSettings(
            _autoExportAuditCsv, _autoExportAuditJson, _autoExportAuditPdf,
            _autoExportQuickCheckCsv, _autoExportQuickCheckPdf,
            _autoExportVaCsv, _autoExportVaPdf);
    }

    private void SaveBaselineSettings()
    {
        UserSettings.SetAlertBaselineEnabled(_alertBaselineEnabled);
        UserSettings.SetAlertBaselinePerServer(_alertBaselinePerServer);
    }

    private void SaveThresholdSettings()
    {
        UserSettings.SetThresholdsEnabled(_thresholdsEnabled);
        UserSettings.SetThresholdCpuMs(_thresholdCpuMs);
        UserSettings.SetThresholdWaitTimeMs(_thresholdWaitTimeMs);
        UserSettings.SetThresholdMemoryMb(_thresholdMemoryMb);
        UserSettings.SetThresholdReadsKb(_thresholdReadsKb);
        UserSettings.SetThresholdWritesKb(_thresholdWritesKb);
        UserSettings.SetThresholdDurationMs(_thresholdDurationMs);
        _thresholdSaveMessage = "Saved.";
    }

    private void ResetThresholdDefaults()
    {
        _thresholdCpuMs = 80000;
        _thresholdWaitTimeMs = 1000;
        _thresholdMemoryMb = 0;
        _thresholdReadsKb = 0;
        _thresholdWritesKb = 0;
        _thresholdDurationMs = 5000;
        SaveThresholdSettings();
    }

    private void SaveNotificationSettings()
    {
        UserSettings.SetShowReleaseNotesOnUpdate(_showReleaseNotesOnUpdate);
    }

    private void SavePreviewSettings()
    {
        UserSettings.SetShowMaturityRoadmap(_showMaturityRoadmap);
    }

    private void SavePerformanceSettings()
    {
        // Assuming UserSettings has a method for this
        UserSettings.SetEnablePerfInspector(_enablePerfInspector);
        // Also set the service
        var perfService = (PerformanceInspectorService?)App.Services?.GetService(typeof(PerformanceInspectorService));
        perfService?.SetEnabled(_enablePerfInspector);
    }

    private void SaveTuningSettings()
    {
        try
        {
            var appsettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            var json = File.ReadAllText(appsettingsPath);
            var config = JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new();

            // Update Orchestrator
            if (!config.ContainsKey("Orchestrator")) config["Orchestrator"] = new Dictionary<string, object>();
            var orch = (Dictionary<string, object>)config["Orchestrator"];
            orch["GlobalConcurrency"] = _globalConcurrency;
            orch["PerServerConcurrency"] = _perServerConcurrency;
            orch["ChannelCapacity"] = _channelCapacity;
            orch["DefaultTimeoutSeconds"] = _defaultTimeoutSeconds;

            // Update Dashboard
            if (!config.ContainsKey("Dashboard")) config["Dashboard"] = new Dictionary<string, object>();
            var dash = (Dictionary<string, object>)config["Dashboard"];
            dash["MaxPanelsInFlight"] = _maxPanelsInFlight;
            dash["RenderCoalesceMs"] = _renderCoalesceMs;
            dash["PreloadFromCacheOnInit"] = _preloadFromCacheOnInit;

            // Update Cache
            if (!config.ContainsKey("Cache")) config["Cache"] = new Dictionary<string, object>();
            var cache = (Dictionary<string, object>)config["Cache"];
            cache["BatchWriteSize"] = _batchWriteSize;
            cache["BatchFlushMs"] = _batchFlushMs;
            cache["HotTierSlidingSeconds"] = _hotTierSlidingSeconds;

            var updatedJson = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(appsettingsPath, updatedJson);

            Toast.ShowSuccess("Tuning settings saved. Restart may be required for some changes.");
        }
        catch (Exception ex)
        {
            Toast.ShowError($"Failed to save tuning settings: {ex.Message}");
        }
    }

    // ── RBAC ──────────────────────────────────────────────────────────────

    private bool _rbacEnabled;
    private bool _rbacRequireExplicit;
    private string _rbacDefaultRole = AppRoles.Viewer;
    private bool _rbacGoogleEnabled;
    private string _rbacGoogleClientId = string.Empty;
    private string _rbacGoogleClientSecret = string.Empty;
    private string _rbacGoogleDomain = string.Empty;
    private bool _rbacMsEnabled;
    private string _rbacMsClientId = string.Empty;
    private string _rbacMsClientSecret = string.Empty;
    private string _rbacMsDomain = string.Empty;
    private List<RbacUser> _rbacUsers = new();
    private string _newUserEmail = string.Empty;
    private string _newUserRole = AppRoles.Viewer;
    private string? _rbacMessage;
    private bool _rbacSuccess;
    private int _rbacUsersNeedingReview;
    private int _accessReviewDays = 90;

    private void LoadRbacSettings()
    {
        var cfg = RbacService.Config;
        _rbacEnabled = cfg.Enabled;
        _rbacRequireExplicit = cfg.RequireExplicitAccess;
        _rbacDefaultRole = cfg.DefaultRole;
        _rbacGoogleEnabled = cfg.Google.Enabled;
        _rbacGoogleClientId = cfg.Google.ClientId;
        _rbacGoogleDomain = cfg.Google.AllowedDomain;
        _rbacMsEnabled = cfg.Microsoft.Enabled;
        _rbacMsClientId = cfg.Microsoft.ClientId;
        _rbacMsDomain = cfg.Microsoft.AllowedDomain;
        // Secrets: never pre-populate — leave blank (placeholder shows "(unchanged)")
        _rbacUsers = RbacService.GetUsers();

        // SOC2 CC6.3: compute stale-review count
        _accessReviewDays = Configuration.GetValue<int>("Rbac:AccessReviewDays", 90);
        var reviewCutoff = DateTime.UtcNow.AddDays(-_accessReviewDays);
        _rbacUsersNeedingReview = _rbacUsers.Count(u =>
            !u.LastReviewedAt.HasValue || u.LastReviewedAt.Value < reviewCutoff);
    }

    private void SaveRbacConfig()
    {
        var existing = RbacService.Config;
        // Capture old values before mutating (CC8.2 prior-value capture).
        // Secrets are intentionally omitted from the audit record.
        var oldSummary = $"Enabled={existing.Enabled},RequireExplicit={existing.RequireExplicitAccess},DefaultRole={existing.DefaultRole}," +
                         $"GoogleEnabled={existing.Google.Enabled},GoogleClientId={existing.Google.ClientId},GoogleDomain={existing.Google.AllowedDomain}," +
                         $"MsEnabled={existing.Microsoft.Enabled},MsClientId={existing.Microsoft.ClientId},MsDomain={existing.Microsoft.AllowedDomain}";

        var newConfig = new RbacConfig
        {
            Enabled = _rbacEnabled,
            RequireExplicitAccess = _rbacRequireExplicit,
            DefaultRole = _rbacDefaultRole,
            Google = new OAuthProviderConfig
            {
                Enabled = _rbacGoogleEnabled,
                ClientId = _rbacGoogleClientId,
                // Keep existing secret if field left blank
                ClientSecret = string.IsNullOrWhiteSpace(_rbacGoogleClientSecret)
                                    ? existing.Google.ClientSecret
                                    : _rbacGoogleClientSecret,
                AllowedDomain = _rbacGoogleDomain,
            },
            Microsoft = new OAuthProviderConfig
            {
                Enabled = _rbacMsEnabled,
                ClientId = _rbacMsClientId,
                ClientSecret = string.IsNullOrWhiteSpace(_rbacMsClientSecret)
                                    ? existing.Microsoft.ClientSecret
                                    : _rbacMsClientSecret,
                AllowedDomain = _rbacMsDomain,
            },
        };
        RbacService.UpdateConfig(newConfig);

        var newSummary = $"Enabled={_rbacEnabled},RequireExplicit={_rbacRequireExplicit},DefaultRole={_rbacDefaultRole}," +
                         $"GoogleEnabled={_rbacGoogleEnabled},GoogleClientId={_rbacGoogleClientId},GoogleDomain={_rbacGoogleDomain}," +
                         $"MsEnabled={_rbacMsEnabled},MsClientId={_rbacMsClientId},MsDomain={_rbacMsDomain}";
        AuditLog.LogConfigurationChange("Settings", "updated", oldSummary, newSummary, ReviewerName);
    }

    private void SaveOAuthConfig()
    {
        SaveRbacConfig();
        _rbacMessage = "OAuth config saved.";
        _rbacSuccess = true;
    }

    private string ReviewerName => UserState.Role == AppRoles.Admin
        ? Environment.UserName
        : UserState.Role;

    private void ChangeUserRole(RbacUser user, string role)
    {
        var oldRole = user.Role;
        user.Role = role;
        RbacService.UpdateUser(user);
        AuditLog.LogUserUpdated(ReviewerName, user.Email, "Role", oldRole, role);
        _rbacUsers = RbacService.GetUsers();
    }

    private void ToggleUserEnabled(RbacUser user, bool enabled)
    {
        var oldEnabled = user.Enabled.ToString();
        user.Enabled = enabled;
        RbacService.UpdateUser(user);
        AuditLog.LogUserUpdated(ReviewerName, user.Email, "Enabled", oldEnabled, enabled.ToString());
        _rbacUsers = RbacService.GetUsers();
    }

    private void RemoveRbacUser(string id)
    {
        var existing = _rbacUsers.FirstOrDefault(u => u.Id == id);
        var formerRole = existing?.Role ?? string.Empty;
        var userName = existing?.Email ?? id;
        RbacService.RemoveUser(id);
        AuditLog.LogUserRemoved(ReviewerName, userName, formerRole);
        _rbacUsers = RbacService.GetUsers();
    }

    private void AddRbacUser()
    {
        _rbacMessage = null;
        if (string.IsNullOrWhiteSpace(_newUserEmail) || !_newUserEmail.Contains('@'))
        {
            _rbacMessage = "Enter a valid email address.";
            _rbacSuccess = false;
            return;
        }
        var emailToAdd = _newUserEmail.Trim();
        var roleToAdd = _newUserRole;
        RbacService.AddUser(new RbacUser
        {
            Email = emailToAdd,
            Role = roleToAdd,
        });
        AuditLog.LogUserAdded(ReviewerName, emailToAdd, roleToAdd);
        _newUserEmail = string.Empty;
        _rbacUsers = RbacService.GetUsers();
        _rbacMessage = "User added.";
        _rbacSuccess = true;
    }

    // SOC2 CC6.3 ─────────────────────────────────────────────────────────

    private void MarkUserAccessReviewed(RbacUser user)
    {
        user.LastReviewedAt = DateTime.UtcNow;
        user.LastReviewedBy = ReviewerName;
        RbacService.UpdateUser(user);
        AuditLog.LogUserAccessReviewed(user.Email, ReviewerName);

        _rbacUsers = RbacService.GetUsers();

        // Recompute banner count
        var reviewCutoff = DateTime.UtcNow.AddDays(-_accessReviewDays);
        _rbacUsersNeedingReview = _rbacUsers.Count(u =>
            !u.LastReviewedAt.HasValue || u.LastReviewedAt.Value < reviewCutoff);
    }

    private void ExportAccessReviewReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("user_name,role,last_reviewed_at,last_reviewed_by,days_since_review,status");

        var now = DateTime.UtcNow;
        foreach (var u in _rbacUsers)
        {
            string lastReviewedAt = u.LastReviewedAt.HasValue
                ? u.LastReviewedAt.Value.ToString("yyyy-MM-ddTHH:mm:ssZ")
                : string.Empty;
            string lastReviewedBy = u.LastReviewedBy ?? string.Empty;
            string daysSince = u.LastReviewedAt.HasValue
                ? ((int)(now - u.LastReviewedAt.Value).TotalDays).ToString()
                : string.Empty;
            string status = (!u.LastReviewedAt.HasValue)
                ? "NEVER_REVIEWED"
                : (now - u.LastReviewedAt.Value).TotalDays > _accessReviewDays ? "OVERDUE" : "OK";

            sb.AppendLine($"{CsvField(u.Email)},{CsvField(u.Role)},{lastReviewedAt},{CsvField(lastReviewedBy)},{daysSince},{status}");
        }

        var downloadsDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var exportDir = System.IO.Path.Combine(downloadsDir, "Downloads");
        if (!System.IO.Directory.Exists(exportDir))
            exportDir = AppDomain.CurrentDomain.BaseDirectory;

        var fileName = $"AccessReviewReport_{now:yyyyMMdd_HHmmssZ}.csv";
        var filePath = System.IO.Path.Combine(exportDir, fileName);
        System.IO.File.WriteAllText(filePath, sb.ToString(), System.Text.Encoding.UTF8);

        AuditLog.LogAccessReviewExported(ReviewerName, filePath);

        _rbacMessage = $"Report saved: {filePath}";
        _rbacSuccess = true;
    }

    private static string CsvField(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    private void ValidateConfig()
    {
        var (isValid, errors) = ConfigValidator.Validate();
        ConfigValidationSuccess = isValid;
        ConfigErrors = errors;
        ConfigValidationResult = isValid ? "Configuration is valid" : "Configuration validation failed";
    }

    private async Task CheckForUpdates()
    {
        IsCheckingUpdates = true;
        UpdateCheckResult = null;
        StateHasChanged();

        var (available, info) = await UpdateService.CheckForUpdatesAsync();
        UpdateAvailable = available;
        UpdateInfo = info;
        UpdateCheckResult = available ? $"Update available: Version {info?.Version}" : "You are running the latest version";

        IsCheckingUpdates = false;
        StateHasChanged();
    }

    private async Task DownloadAndApplyUpdate()
    {
        if (UpdateInfo == null || string.IsNullOrEmpty(UpdateInfo.DownloadUrl)) return;
        _downloading = true;
        _downloadProgress = 0;
        StateHasChanged();
        try
        {
            var ok = await UpdateService.DownloadUpdateAsync(
                UpdateInfo.DownloadUrl,
                new Progress<int>(p => { _downloadProgress = p; _ = InvokeAsync(StateHasChanged); }));
            if (!ok)
                UpdateCheckResult = "Download failed — check the log for details.";
        }
        finally
        {
            _downloading = false;
            StateHasChanged();
        }
    }

    private async Task FlushCache()
    {
        IsFlushingCache = true;
        FlushCacheResult = null;
        StateHasChanged();

        try
        {
            var cacheStore = App.Services?.GetService<SQLTriage.Data.Caching.liveQueriesCacheStore>();
            if (cacheStore != null)
            {
                await cacheStore.InvalidateAllAsync();
                FlushCacheResult = "Cache flushed successfully. All dashboard data will be recreated on next refresh.";
                FlushCacheSuccess = true;
                UpdateCacheFileSize();
            }
            else
            {
                FlushCacheResult = "Cache service not available.";
                FlushCacheSuccess = false;
            }
        }
        catch (Exception ex)
        {
            FlushCacheResult = $"Failed to flush cache: {ex.Message}";
            FlushCacheSuccess = false;
        }
        finally
        {
            IsFlushingCache = false;
            StateHasChanged();
        }
    }

    private void UpdateCacheFileSize()
    {
        try
        {
            var dbPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SQLTriage-cache.db");
            if (System.IO.File.Exists(dbPath))
            {
                var fileInfo = new System.IO.FileInfo(dbPath);
                var sizeMB = fileInfo.Length / 1024.0 / 1024.0;
                CacheFileSize = sizeMB < 1 ? $"{(fileInfo.Length / 1024.0):N1} KB" : $"{sizeMB:N1} MB";
            }
            else
            {
                CacheFileSize = "No cache file";
            }
        }
        catch
        {
            CacheFileSize = "N/A";
        }
    }

    private async Task RunMaintenance()
    {
        IsRunningMaintenance = true;
        MaintenanceResult = null;
        StateHasChanged();

        try
        {
            var dbPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SQLTriage-cache.db");
            if (!System.IO.File.Exists(dbPath))
            {
                MaintenanceResult = "No cache file to maintain.";
                MaintenanceSuccess = true;
                return;
            }

            var sizeBefore = new System.IO.FileInfo(dbPath).Length;
            var connStr = $"Data Source={dbPath}";

            using (var conn = await SQLTriage.Data.SqliteCipherHelper.OpenEncryptedAsync(connStr))
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA optimize;";
                    await cmd.ExecuteNonQueryAsync();
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "ANALYZE;";
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using (var conn = await SQLTriage.Data.SqliteCipherHelper.OpenEncryptedAsync(connStr))
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "VACUUM;";
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            var sizeAfter = new System.IO.FileInfo(dbPath).Length;
            var savedMB = (sizeBefore - sizeAfter) / 1024.0 / 1024.0;

            UpdateCacheFileSize();
            MaintenanceResult = $"Maintenance completed. Reclaimed {savedMB:N1} MB.";
            MaintenanceSuccess = true;
        }
        catch (Exception ex)
        {
            MaintenanceResult = $"Maintenance failed: {ex.Message}";
            MaintenanceSuccess = false;
        }
        finally
        {
            IsRunningMaintenance = false;
            StateHasChanged();
        }
    }

    private void SaveAzureConfig()
    {
        _azureResult = null;
        try
        {
            // Capture old Azure config before any mutations (CC8.2 prior-value capture; credentials omitted).
            var oldAzureSummary = $"AuthMode={BlobExport.AuthMode},Container={BlobExport.ContainerName},Method={BlobExport.UploadMethod},Compress={BlobExport.CompressUploads},AutoUpload={BlobExport.AutoUploadCsvs}";

            // null = keep existing credential, empty string = clear it
            string? connStr = _azureAuthMode == "connectionstring"
                ? (string.IsNullOrWhiteSpace(_azureConnStr) && _hasExistingAzureConnStr ? null : _azureConnStr)
                : "";  // switching to SAS — clear connection string

            string? sasToken = _azureAuthMode == "sastoken"
                ? (string.IsNullOrWhiteSpace(_azureSasToken) && _hasExistingAzureSas ? null : _azureSasToken)
                : "";  // switching to connstr — clear SAS

            BlobExport.Configure(connStr, sasToken, _azureAccountName, _azureContainer, _azurePrefix);
            BlobExport.UploadMethod = _azureUploadMethod;
            BlobExport.AzCopyPath = _azureAzCopyPath;
            BlobExport.CompressUploads = _azureCompress;
            BlobExport.AutoUploadCsvs = _azureAutoUpload;
            BlobExport.SaveToConfig();

            _hasExistingAzureConnStr = BlobExport.AuthMode == "connectionstring" && BlobExport.IsConfigured;
            _hasExistingAzureSas = BlobExport.AuthMode == "sastoken" && BlobExport.IsConfigured;

            _azureSuccess = true;
            _azureResult = "Azure Blob configuration saved (credentials encrypted).";
            var newAzureSummary = $"AuthMode={_azureAuthMode},Container={_azureContainer},Method={_azureUploadMethod},Compress={_azureCompress},AutoUpload={_azureAutoUpload}";
            AuditLog.LogConfigurationChange("Settings", "updated", oldAzureSummary, newAzureSummary, ReviewerName);
        }
        catch (Exception ex)
        {
            _azureSuccess = false;
            _azureResult = $"Failed to save: {ex.Message}";
        }
        StateHasChanged();
    }

    private void ApplySqldbaDefaults()
    {
        _showSqldbaConsent = false;
        _azureAuthMode = "sastoken";
        _azureAccountName = "sqldbaorgstorage";
        _azureSasToken = "sp=acw&st=2023-04-05T21:18:07Z&se=2033-06-04T05:18:07Z&spr=https&sv=2021-12-02&sr=d&sig=zCRXJULTwR6aTB5%2FBvt0T7dX98avVCafRtLOJzCT0y0%3D&sdd=1";
        _azureContainer = "raw";
        _azurePrefix = "ready";
        _azureUploadMethod = "sdk";
        _azureCompress = true;
        _azureAutoUpload = true;

        SaveAzureConfig();
        StateHasChanged();
    }

    private void ShowAzureDiagnostics()
    {
        SaveAzureConfig(); // ensure latest config is applied
        _azureDiagData = BlobExport.GetDiagnostics();
        _showAzureDiag = true;
        StateHasChanged();
    }

    private async Task TestAzureConnection()
    {
        _testingAzure = true;
        _azureResult = null;
        StateHasChanged();

        try
        {
            // Save first to ensure config is current
            SaveAzureConfig();

            var (success, message) = await BlobExport.TestConnectionAsync();
            _azureSuccess = success;
            _azureResult = message;
        }
        catch (Exception ex)
        {
            _azureSuccess = false;
            _azureResult = $"Test failed: {ex.Message}";
        }
        finally
        {
            _testingAzure = false;
            StateHasChanged();
        }
    }

    private void ScanOutputForUpload()
    {
        var outputDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output");
        if (!System.IO.Directory.Exists(outputDir))
        {
            _azureResult = "No output folder found. Run an audit or assessment first to generate output files.";
            _azureSuccess = false;
            return;
        }

        _outputFiles = System.IO.Directory.GetFiles(outputDir, "*.*")
            .OrderByDescending(f => new System.IO.FileInfo(f).LastWriteTime)
            .ToList();

        if (_outputFiles.Count == 0)
        {
            _azureResult = "Output folder is empty. Run an audit or assessment first.";
            _azureSuccess = false;
            return;
        }

        _showUploadConfirm = true;
    }

    private async Task ConfirmUploadOutput()
    {
        _showUploadConfirm = false;
        _uploadingOutput = true;
        _azureResult = null;
        StateHasChanged();

        try
        {
            if (BlobExport == null || !BlobExport.IsConfigured)
            {
                _azureResult = "Azure Blob Storage is not configured. Save your Azure config first.";
                _azureSuccess = false;
                return;
            }

            int uploaded = 0, failed = 0;
            var errors = new List<string>();

            foreach (var filePath in _outputFiles)
            {
                try
                {
                    var fileName = System.IO.Path.GetFileName(filePath);
                    var content = await System.IO.File.ReadAllTextAsync(filePath);
                    var lineCount = content.Split('\n').Length;

                    var result = await BlobExport.ExportRawCsvAsync(content, fileName, lineCount);
                    if (result.Success)
                    {
                        uploaded++;
                        Toast?.ShowSuccess($"Uploaded: {fileName}");
                    }
                    else
                    {
                        failed++;
                        errors.Add($"{fileName}: {result.Message}");
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    errors.Add($"{System.IO.Path.GetFileName(filePath)}: {ex.Message}");
                }
            }

            _azureSuccess = failed == 0;
            _azureResult = $"Upload complete: {uploaded} succeeded, {failed} failed.";
            if (errors.Count > 0)
                _azureResult += $"\nErrors: {string.Join("; ", errors)}";
        }
        catch (Exception ex)
        {
            _azureResult = $"Upload failed: {ex.Message}";
            _azureSuccess = false;
        }
        finally
        {
            _uploadingOutput = false;
            StateHasChanged();
        }
    }
}
