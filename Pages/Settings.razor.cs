/* In the name of God, the Merciful, the Compassionate */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// <summary>
    /// The appsettings.json this build actually loads. SQLTriage.csproj copies it to
    /// <c>&lt;BaseDirectory&gt;\config\appsettings.json</c>, and every host reads it from there
    /// (App.xaml.cs, Cli/CliAuditHost, Cli/CliImportHost, Data/Services/WindowsServiceHost).
    /// The write paths on this page previously used &lt;BaseDirectory&gt;\appsettings.json, which
    /// does not exist in a published layout.
    /// </summary>
    private static string AppSettingsPath =>
        Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json");

    private string _activeTab = "General";

    private void SetActiveTab(string tab)
    {
        _activeTab = tab;
    }

    /// <summary>
    /// The tab to open, from the query string: <c>/settings?tab=security</c>.
    ///
    /// <para><b>Why this exists.</b> Every tab on this page was selected by an <c>@onclick</c>
    /// handler and nothing else, so no tab but "General" had an address. Two consequences, both
    /// measured 2026-08-25. For the OPERATOR: the startup log, the Server Mode toggle and the
    /// activation card all say "Settings &gt; Security &amp; Access" or "Settings &gt; Full Audit",
    /// and none of those was a place that could be linked to or reopened. For EVIDENCE: a Blazor
    /// Server prerender contains only the tab the page opens on, so <c>curl http://localhost:PORT/settings</c>
    /// returned 41 KB of markup with neither "Add User" nor "Install bundle file" in it, and every
    /// claim about those controls rested on reading the markup rather than rendering it.</para>
    ///
    /// <para>It selects a tab and nothing else. It is not a permission, a filter or a data
    /// parameter: the whole page is already behind <c>IsAuthorizedWithBreakGlass("settings")</c>,
    /// and an unknown or premium-gated slug falls back to the same "General" the page opened on
    /// before, so no value here can reach a surface a click could not.</para>
    /// </summary>
    [Parameter, SupplyParameterFromQuery(Name = "tab")]
    public string? TabSlug { get; set; }

    /// <summary>
    /// The address of the Full Audit tab, written once so the surfaces that send an operator to it
    /// cannot drift from the slug map.
    ///
    /// <para><b>Why it exists.</b> The boot-time licence failure renders on the Full Audit tab and
    /// nowhere else. Measured 2026-08-25 in the state Adrian's install was actually in: the reason
    /// appeared on <c>/settings?tab=full-audit</c>, and <c>/settings</c> showed zero occurrences of
    /// it. Every in-app control that sends an operator to Settings ABOUT the licence — the
    /// bundle-not-loaded banner, the Free-tier pill, the tier badge in the nav — navigated to the
    /// slug-less address, so the sentence explaining the failure was reachable only by a person who
    /// already knew to click the Full Audit tab. It was written, and it was linked from
    /// nowhere.</para>
    ///
    /// <para>In a community build the Full Audit tab is not rendered, and
    /// <see cref="ResolveTabSlug"/> falls the slug back to General, which is the page those
    /// controls opened before. So this address is safe to use unconditionally.</para>
    /// </summary>
    internal const string FullAuditTabUrl = "/settings?tab=full-audit";

    /// <summary>
    /// Slug to rail-tab name. Every entry must name a tab the rail actually renders, and every tab
    /// the rail renders must have an entry: <see cref="SQLTriage.Tests"/>' settings-tab census
    /// re-derives both directions from the shipped markup, so a tab added to the rail without a
    /// slug fails rather than being quietly unaddressable.
    ///
    /// <para>Ordinal-ignore-case on purpose. The default comparer is case-SENSITIVE, so
    /// <c>?tab=Security</c> would have fallen back to General while <c>?tab=security</c> worked,
    /// which is the worst kind of link: right for whoever wrote it, wrong for whoever retypes it.</para>
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> TabSlugs =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["full-audit"] = "Full Audit",
            ["general"] = "General",
            ["performance"] = "Performance",
            ["diagnostics"] = "Diagnostics",
            ["security"] = "Security & Access",
            ["integrations"] = "Integrations",
            ["compliance"] = "Compliance",
        };

    /// <summary>
    /// The tab a slug opens, or null when it opens none. Static and side-effect free so the census
    /// can drive it without a page, a circuit or a DI graph.
    /// </summary>
    /// <param name="slug">The raw query-string value; null, empty and unknown all return null.</param>
    /// <param name="premium">
    /// Whether this build has the premium modules. The "Full Audit" tab is not rendered in a
    /// community build, so its slug must not select it there — a selected-but-not-rendered tab is
    /// an empty page, which reads as a broken install rather than as a missing feature.
    /// </param>
    internal static string? ResolveTabSlug(string? slug, bool premium)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        if (!TabSlugs.TryGetValue(slug.Trim(), out var tab)) return null;
        if (tab == "Full Audit" && !premium) return null;
        return tab;
    }

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
    private bool _drTestSuccess;

    private void RecordDrTest()
    {
        try
        {
            // Write today's date into appsettings.json under Continuity:LastDrTestDate.
            var appsettingsPath = AppSettingsPath;
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
            _drTestSuccess = true;
        }
        catch (Exception ex)
        {
            _drTestMessage = $"Failed — DR test NOT recorded: {ex.Message}";
            _drTestSuccess = false;
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

    // Per-dashboard factory reset (DECISIONS 2026-08-26 18:23, ruling 2). The id is what the picker
    // binds; the confirmation flag is what stands between the button and the write.
    private string RestoreDashboardId = "";
    private bool _showRestoreDashboardConfirm;
    private bool IsRestoringDashboard;
    private string? RestoreDashboardResult;
    private bool RestoreDashboardSuccess;
    private string? ConfigValidationResult;
    private bool ConfigValidationSuccess;
    private List<string> ConfigErrors = new();
    private bool IsCheckingUpdates;
    private string? UpdateCheckResult;
    private bool UpdateAvailable;
    private string _updateProxyUrl = "";
    private bool _updatesEnabledToggle;
    private int _refreshIntervalSeconds = 5;
    private int _defaultTimeRangeMinutes = 60;
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

    // ── Audit concurrency (queries per instance) ──
    private int _auditConcurrency = UserSettingsService.AuditConcurrencyDefault;
    private string? _auditConcurrencyMessage;
    private bool _auditConcurrencyError;
    // Bumped to force the number input to be rebuilt from the model after a rejected value.
    private int _auditConcurrencyInputKey;

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
    private string _backgroundStyle = "none";
    // Measured, not assumed: what the browser reports about the chosen backdrop.
    private string _backgroundStatusLine = BackgroundStatusReporter.NotMeasured;
    private string _landingView = "hero";

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

    // The "Performance Diagnostics" settings card's link. The Perf Inspector page was
    // retired 2026-07-10 and its load-waterfall folded into the ships-everywhere
    // "SQLTriage App Metrics" page (/app-metrics), where the dev-gated waterfall renders
    // in full builds. The toggle below still drives PerformanceInspectorService trace
    // collection; the link just points at the unified metrics page.
    private string PerfInspectorHref => RouteConstants.AppMetrics;

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

    // UX / Appearance
    private bool _enableAnimations;
    private bool _fastAppLoad;

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
        _landingView = UserSettings.GetLandingView();
        _enableAnimations = UserSettings.GetEnableAnimations();
        _fastAppLoad = UserSettings.GetFastAppLoad();
        _reportCompanyName = UserSettings.GetReportCompanyName();
        _reportOperatorName = UserSettings.GetReportOperatorName();
        _reportDraftWatermark = UserSettings.GetReportDraftWatermark();
        _showRemediationCost = UserSettings.GetShowRemediationCost();
        _remediationRate = ((int)UserSettings.GetRemediationHourlyRate()).ToString();
        _complianceTier = UserSettings.GetComplianceTier();
        _consultancyName = UserSettings.GetConsultancyName();
        _engagementDuration = UserSettings.GetEngagementDuration();
        _monthlyOpexPerServerNZD = UserSettings.GetMonthlyOpexPerServerNZD();
    }

    private void ToggleShowRemediationCost(bool v)
    {
        _showRemediationCost = v;
        UserSettings.SetShowRemediationCost(_showRemediationCost);
    }

    // ── Report identity ──
    private string _reportCompanyName = "";
    private string _reportOperatorName = "";
    private bool   _reportDraftWatermark;

    private void OnReportCompanyNameChanged(ChangeEventArgs e)
    {
        _reportCompanyName = e.Value?.ToString() ?? "";
        UserSettings.SetReportCompanyName(_reportCompanyName);
    }

    private void OnReportOperatorNameChanged(ChangeEventArgs e)
    {
        _reportOperatorName = e.Value?.ToString() ?? "";
        UserSettings.SetReportOperatorName(_reportOperatorName);
    }

    private void ToggleReportDraftWatermark(bool v)
    {
        _reportDraftWatermark = v;
        UserSettings.SetReportDraftWatermark(_reportDraftWatermark);
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

    /// <summary>The slug last applied, so a click is never overruled by a re-render.</summary>
    private string? _appliedTabSlug;

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        // Applied when the slug CHANGES, not on every parameter set. A rail click sets _activeTab
        // and does not touch the query string, so re-applying the slug on the next render would
        // snap the operator back to the tab their link named. Snapping a control back to a state
        // nobody chose is the exact shape of the RBAC-checkbox defect this lane fixed.
        if (string.Equals(_appliedTabSlug, TabSlug, StringComparison.Ordinal)) return;

        _appliedTabSlug = TabSlug;
        if (ResolveTabSlug(TabSlug, BuildModules.Premium) is { } tab) _activeTab = tab;
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
        _auditConcurrency = UserSettings.GetAuditMaxConcurrentPerInstance();
        _updateProxyUrl = UserSettings.GetUpdateProxyUrl() ?? "";
        _updatesEnabledToggle = UpdateService.UpdatesEnabled;
        _refreshIntervalSeconds = UserSettings.GetRefreshInterval();
        _defaultTimeRangeMinutes = UserSettings.GetDefaultTimeRange();
        _enablePerfInspector = UserSettings.GetEnablePerfInspector();

        // Set performance inspector enabled
        var perfService = (PerformanceInspectorService?)App.Services?.GetService(typeof(PerformanceInspectorService));
        perfService?.SetEnabled(_enablePerfInspector);

        // Load tuning values from config. The Orchestrator defaults come from QueryOrchestrator
        // itself: the keys are absent from the shipped appsettings.json, so any other default here
        // would show the user a number the engine is not running on — and saving would then write
        // that wrong number into the file.
        // NOTE: the simple name "QueryOrchestrator" binds to the @inject'd IQueryOrchestrator
        // property on this page, not to the type — so the constants must be fully qualified.
        _globalConcurrency = Configuration.GetValue<int>("Orchestrator:GlobalConcurrency", SQLTriage.Data.Scheduling.QueryOrchestrator.DefaultGlobalConcurrency);
        _perServerConcurrency = Configuration.GetValue<int>("Orchestrator:PerServerConcurrency", SQLTriage.Data.Scheduling.QueryOrchestrator.DefaultPerServerConcurrency);
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
        // Sync the Background dropdown with the persisted client-side choice (bgConfig.js owns it).
        try
        {
            var bg = await JS.InvokeAsync<string>("bgConfig.get");
            if (!string.IsNullOrEmpty(bg) && bg != _backgroundStyle)
            {
                _backgroundStyle = bg;
                StateHasChanged();
            }
        }
        catch { /* JS not ready */ }
        await RefreshBackgroundStatusAsync();
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

    /// <summary>
    /// The read-mutate-write this page's Updates:Enabled toggle applies to <paramref name="appsettingsPath"/>.
    /// Internal, static and path-parameterised (InternalsVisibleTo SQLTriage.Tests — the same seam
    /// convention as <c>ScheduledTaskEngine.ResolveTargets</c>) so the round trip is testable without
    /// standing up the full Settings component. Mirrors SaveTuningSettings' JsonNode/Section write:
    /// only the "Updates" section is touched, every other key survives byte-for-byte.
    /// </summary>
    internal static void WriteUpdatesEnabledToAppSettings(string appsettingsPath, bool enabled)
    {
        var json = File.ReadAllText(appsettingsPath);
        var root = JsonNode.Parse(json) as JsonObject
                   ?? throw new InvalidOperationException($"{appsettingsPath} is not a JSON object.");

        Section(root, "Updates")["Enabled"] = enabled;

        File.WriteAllText(appsettingsPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Operator-facing kill switch for the whole update subsystem (Updates:Enabled).
    /// AutoUpdateService reads this value once at construction, so the effect is next-restart,
    /// same as the other appsettings.json toggles on this page.
    /// </summary>
    private void SaveUpdatesEnabledToggle()
    {
        var previous = UpdateService.UpdatesEnabled;
        try
        {
            WriteUpdatesEnabledToAppSettings(AppSettingsPath, _updatesEnabledToggle);

            AuditLog.LogConfigurationChange("Settings", "updated",
                previous.ToString(), _updatesEnabledToggle.ToString(), ReviewerName);
            Toast.ShowSuccess(_updatesEnabledToggle
                ? "Update checks enabled. Restart required to take effect."
                : "Update checks disabled. Restart required to take effect.");
        }
        catch (Exception ex)
        {
            // The write failed, so the file still names the old setting — revert the toggle
            // rather than leave the UI showing a state that was never persisted.
            _updatesEnabledToggle = previous;
            Toast.ShowError($"Failed to save update setting: {ex.Message}");
        }
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

            int added = 0, skipped = 0, refused = 0;
            string? refusalReason = null;
            foreach (var conn in imported)
            {
                if (existingIds.Contains(conn.Id)) { skipped++; continue; }
                var result = ConnectionManager.AddConnection(conn);
                if (result.Succeeded) { added++; continue; }
                // A bulk import is the easiest way to blow past a seat count. Count the refusals
                // instead of reporting them as "added" — the import summary is the only place the
                // operator learns what actually landed.
                refused++;
                refusalReason ??= result.Reason;
            }

            _credsMessage = refused == 0
                ? $"Import complete: {added} added, {skipped} skipped (already exist)."
                : $"Import complete: {added} added, {skipped} skipped (already exist), " +
                  $"{refused} not added — {refusalReason}";
            _credsSuccess = refused == 0;
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

    /// <summary>
    /// Persists the audit concurrency default. Rejects a non-integer or out-of-range entry with an
    /// explicit message and restores the field to the stored value, rather than silently clamping
    /// it — the number is a load-bearing safety control on someone's production server, so a typo
    /// should be told, not quietly reinterpreted.
    /// </summary>
    private void OnAuditConcurrencyChanged(ChangeEventArgs e)
    {
        var raw = e.Value?.ToString();
        const int min = UserSettingsService.AuditConcurrencyMin;
        const int max = UserSettingsService.AuditConcurrencyMax;

        if (!int.TryParse(raw, out var value))
        {
            _auditConcurrencyError = true;
            _auditConcurrencyMessage = $"Concurrent queries per instance must be a whole number between {min} and {max}, got '{raw}'. Kept {_auditConcurrency}.";
            RevertAuditConcurrencyInput();
            return;
        }

        if (value < min || value > max)
        {
            _auditConcurrencyError = true;
            _auditConcurrencyMessage = $"Concurrent queries per instance must be between {min} and {max}, got {value}. Kept {_auditConcurrency}.";
            RevertAuditConcurrencyInput();
            return;
        }

        _auditConcurrency = value;
        UserSettings.SetAuditMaxConcurrentPerInstance(value);
        _auditConcurrencyError = false;
        _auditConcurrencyMessage = $"Saved: {value} concurrent {(value == 1 ? "query" : "queries")} per instance. Applies to the next audit run you start.";
    }

    /// <summary>
    /// Puts the rejected value back to the one actually in force.
    /// </summary>
    /// <remarks>
    /// Rejecting the input leaves <c>_auditConcurrency</c> unchanged, so Blazor's diff sees no
    /// change to the <c>value</c> attribute and does not touch the DOM — the browser keeps
    /// showing whatever the user typed. A 2026-07-20 UI capture caught the result: the box read
    /// <c>99</c> while the message beneath it read "Kept 7", i.e. the control asserted a value
    /// that was not in effect. Bumping the key forces the element to be recreated from the model.
    /// </remarks>
    private void RevertAuditConcurrencyInput() => _auditConcurrencyInputKey++;

    private void OnZoomLevelChanged(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out var zoom) && zoom >= 50 && zoom <= 300)
        {
            _zoomLevel = zoom;
            UserSettings.SetZoomLevel(zoom);
        }
    }

    private async Task OnAnimationsChanged(bool v)
    {
        _enableAnimations = v;
        UserSettings.SetEnableAnimations(_enableAnimations);
        // Mirror MainLayout (the canonical pattern, MainLayout.razor:292) and StatusBar:
        // persisting alone left the setting inert until the next app start.
        try
        {
            await JS.InvokeVoidAsync("eval",
                $"document.body.classList.toggle('no-animations', {(!_enableAnimations).ToString().ToLower()});");
        }
        catch { /* JS not ready */ }
    }

    private async Task OnBackgroundChanged(ChangeEventArgs e)
    {
        var style = e.Value?.ToString();
        if (string.IsNullOrEmpty(style) || (style != "none" && style != "wave" && style != "particle"))
            style = "none";
        _backgroundStyle = style;
        // The animated backdrop is a pure client-side visual owned by bgConfig.js
        // (persists in localStorage). Apply live so the change is instant.
        try { await JS.InvokeVoidAsync("bgConfig.set", style); } catch { /* JS not ready */ }
        await RefreshBackgroundStatusAsync();
    }

    /// <summary>
    /// Reads back what the browser actually has on screen and derives the Appearance
    /// section's status line from it.
    ///
    /// <para>Selecting a style is a REQUEST. Whether the canvas exists, drew anything,
    /// animates, or is buried under an opaque layer is a separate question, and the
    /// section used to answer it with a fixed sentence that assumed success. If this
    /// read fails, the line says it was not measured — it never falls back to a claim.</para>
    /// </summary>
    private async Task RefreshBackgroundStatusAsync()
    {
        try
        {
            var status = await JS.InvokeAsync<BackgroundStatus>("bgConfig.status");
            _backgroundStatusLine = BackgroundStatusReporter.Describe(status);
        }
        catch
        {
            _backgroundStatusLine = BackgroundStatusReporter.NotMeasured;
        }
        StateHasChanged();
    }

    private void OnLandingViewChanged(ChangeEventArgs e)
    {
        var view = e.Value?.ToString();
        if (view != "hero" && view != "guide" && view != "dashboard" && view != "cio" && view != "dba") view = "hero";
        _landingView = view;
        UserSettings.SetLandingView(view);
    }

    // 4d: these two used to be readonly IConfiguration reads (Configuration["..."]) — a snapshot
    // of the shipped appsettings.json, not the operator's actual preference. The dashboard
    // toolbar's own dropdowns (DashboardToolbar.razor) already read/write these through
    // UserSettingsService, so this page now edits the SAME store instead of a second, dead copy.
    private void OnRefreshIntervalSecondsChanged(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out var seconds))
        {
            _refreshIntervalSeconds = seconds;
            UserSettings.SetRefreshInterval(seconds);
        }
    }

    private void OnDefaultTimeRangeChanged(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out var minutes))
        {
            _defaultTimeRangeMinutes = minutes;
            UserSettings.SetDefaultTimeRange(minutes);
        }
    }

    private void OnFastAppLoadChanged(bool v)
    {
        _fastAppLoad = v;
        UserSettings.SetFastAppLoad(_fastAppLoad);
        Toast.ShowInfo(_fastAppLoad
            ? "Fast App Load enabled — restart the app to activate instant skeleton screens."
            : "Fast App Load disabled — restart the app to reduce memory usage.");
    }

    private void ShowWelcomeTour()
    {
        if (Tour.IsActive) return;
        Tour.Start();
    }

    private void ToggleDiagnosticPane(bool v)
    {
        ShowDiagnosticPane = v;
        UserSettings.SetShowDiagnosticPane(ShowDiagnosticPane);
    }

    private void ToggleDebugLogging(bool v)
    {
        _debugLogging = v;
        UserSettings.SetDebugLogging(_debugLogging);
    }

    private void ResetAllSettings()
    {
        UserSettings.ResetToDefaults();
        // Reload local field cache so the UI reflects the reset values
        LoadSettings();
        Toast.ShowSuccess("App settings reset to defaults.");
    }

    private void ToggleAnonymiseServerNames(bool v)
    {
        _anonymiseServerNames = v;
        UserSettings.SetAnonymiseServerNames(_anonymiseServerNames);
        LogAnon.Enabled = _anonymiseServerNames;
        if (!_anonymiseServerNames) LogAnon.Reset();
    }

    private async Task ToggleV2PlanIcons(bool v)
    {
        _useV2PlanIcons = v;
        UserSettings.SetUseV2PlanIcons(_useV2PlanIcons);
        try { await JS.InvokeVoidAsync("queryPlanInteropV2.setUseV2Icons", _useV2PlanIcons); } catch { }
    }

    // ── No-Pants Mode (relocated from the sidebar, #77c) ──────────────────
    // The switch flips the persisted flag ONLY on explicit confirmation: enabling
    // routes through the disclaimer modal and its Accept handler owns the state
    // change; Cancel/dismiss leaves the mode untouched. Disabling is immediate.
    // Guarded by the run_scripts permission (Settings itself is already Admin-only).
    private bool _showNoPantsModal;

    private void ToggleNoPantsMode(bool v)
    {
        if (!UserState.IsAuthorized("run_scripts"))
            return;

        if (v)
        {
            // Until the operator accepts the one-time disclaimer, _noPantsMode stays
            // false so the ToggleSwitch snaps back to off on the next render.
            if (!UserSettings.GetNoPantsDisclaimerAccepted())
            {
                _showNoPantsModal = true;
                return;
            }
            _noPantsMode = true;
            _noPantsModeActive = true;
            UserSettings.SetNoPantsMode(true);
            AuditLog.LogSecurityEvent(
                $"No-Pants Mode ENABLED by {Environment.UserName}",
                AuditSeverity.Warning,
                new Dictionary<string, string> { ["User"] = Environment.UserName, ["Action"] = "Enabled" });
        }
        else
        {
            _noPantsMode = false;
            _noPantsModeActive = false;
            UserSettings.SetNoPantsMode(false);
            AuditLog.LogSecurityEvent(
                $"No-Pants Mode DISABLED by {Environment.UserName}",
                AuditSeverity.Info,
                new Dictionary<string, string> { ["User"] = Environment.UserName, ["Action"] = "Disabled" });
        }
    }

    private void AcceptNoPantsDisclaimer()
    {
        _showNoPantsModal = false;
        _noPantsMode = true;
        _noPantsModeActive = true;
        UserSettings.SetNoPantsDisclaimerAccepted(true);
        UserSettings.SetNoPantsMode(true);
        AuditLog.LogSecurityEvent(
            $"No-Pants Mode disclaimer ACCEPTED and ENABLED by {Environment.UserName}",
            AuditSeverity.Warning,
            new Dictionary<string, string> { ["User"] = Environment.UserName, ["Action"] = "DisclaimerAccepted" });
    }

    private void CancelNoPantsModal()
    {
        // Explicit decline / dismiss — leave the mode exactly as it was (off).
        _showNoPantsModal = false;
        _noPantsMode = false;
        _noPantsModeActive = false;
    }

    private void OnExperimentalModeChanged(bool v)
    {
        _experimentalMode = v;
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

    /// <summary>
    /// The title the confirmation names, resolved from the SHIPPED catalogue rather than the loaded
    /// config: the picker offers dashboards the operator may have deleted, and a confirmation that
    /// could not name what it is about to replace would be worse than no confirmation.
    /// </summary>
    private string RestoreDashboardTitle
    {
        get
        {
            var match = DashboardConfig.GetRestorableDashboards()
                .FirstOrDefault(d => string.Equals(d.Id, RestoreDashboardId, StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrEmpty(match.Title) ? RestoreDashboardId : match.Title;
        }
    }

    /// <summary>
    /// Restores one dashboard to the state this build ships. Reached only from the confirmation modal,
    /// never from the button, because it replaces the operator's edits to that dashboard and cannot be
    /// undone from inside the app. The whole page is already behind
    /// <c>IsAuthorizedWithBreakGlass("settings")</c>, the same permission that gates dashboard layout
    /// editing in DynamicDashboard, so the caller who can reach this is the caller who could have made
    /// the edits it replaces.
    /// </summary>
    private void ConfirmRestoreDashboard()
    {
        _showRestoreDashboardConfirm = false;
        IsRestoringDashboard = true;
        RestoreDashboardResult = null;
        StateHasChanged();

        try
        {
            var report = DashboardConfig.ResetDashboardToShipped(RestoreDashboardId);
            RestoreDashboardResult = report.Message;
            RestoreDashboardSuccess = report.Success;

            if (report.Changed)
                AuditLog.LogConfigurationChange(
                    "DashboardConfig", "restored-to-shipped",
                    $"Dashboard={RestoreDashboardId} (operator edits)",
                    $"Dashboard={RestoreDashboardId} (shipped defaults)", ReviewerName);
        }
        catch (Exception ex)
        {
            RestoreDashboardResult = $"Failed to restore the dashboard: {ex.Message}";
            RestoreDashboardSuccess = false;
        }
        finally
        {
            IsRestoringDashboard = false;
            StateHasChanged();
        }
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

        var previousDataSource = SelectedDataSource;
        SelectedDataSource = newValue;

        try
        {
            var appSettingsPath = AppSettingsPath;
            var json = await File.ReadAllTextAsync(appSettingsPath);
            var root = JsonNode.Parse(json) as JsonObject
                       ?? throw new InvalidOperationException($"{appSettingsPath} is not a JSON object.");

            root["DataSource"] = newValue;

            await File.WriteAllTextAsync(appSettingsPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            RestartRequired = true;
        }
        catch (Exception ex)
        {
            // The write failed, so the file still names the old source — revert the dropdown
            // rather than leave the UI showing a selection that was never persisted.
            SelectedDataSource = previousDataSource;
            Toast.ShowError($"Failed to save data source: {ex.Message}");
        }
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
            var appsettingsPath = AppSettingsPath;
            var json = File.ReadAllText(appsettingsPath);

            // JsonNode, not Deserialize<Dictionary<string, object>>: the old shape cast each
            // section to Dictionary<string, object>, which only holds on the first save. Once a
            // section exists in the file it deserialises as JsonElement and the cast throws, so
            // the second edit on this card failed. Node editing also leaves keys we don't own
            // (and any other section) exactly as they were.
            var root = JsonNode.Parse(json) as JsonObject
                       ?? throw new InvalidOperationException($"{appsettingsPath} is not a JSON object.");

            var orch = Section(root, "Orchestrator");
            orch["GlobalConcurrency"] = _globalConcurrency;
            orch["PerServerConcurrency"] = _perServerConcurrency;
            orch["ChannelCapacity"] = _channelCapacity;
            orch["DefaultTimeoutSeconds"] = _defaultTimeoutSeconds;

            var dash = Section(root, "Dashboard");
            dash["MaxPanelsInFlight"] = _maxPanelsInFlight;
            dash["RenderCoalesceMs"] = _renderCoalesceMs;
            dash["PreloadFromCacheOnInit"] = _preloadFromCacheOnInit;

            var cache = Section(root, "Cache");
            cache["BatchWriteSize"] = _batchWriteSize;
            cache["BatchFlushMs"] = _batchFlushMs;
            cache["HotTierSlidingSeconds"] = _hotTierSlidingSeconds;

            File.WriteAllText(appsettingsPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            Toast.ShowSuccess("Tuning settings saved. Restart may be required for some changes.");
        }
        catch (Exception ex)
        {
            Toast.ShowError($"Failed to save tuning settings: {ex.Message}");
        }
    }

    /// <summary>Returns the named object section, creating it if the file has none.</summary>
    private static JsonObject Section(JsonObject root, string name)
    {
        if (root[name] is JsonObject existing) return existing;
        var created = new JsonObject();
        root[name] = created;
        return created;
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
    private string _newUserProvider = AuthProviders.Windows;
    private bool _rbacWindowsEnabled;
    private string _rbacWindowsDomains = string.Empty;
    private bool _rbacLocalPasswordEnabled;
    private string? _rbacMessage;
    private bool _rbacSuccess;
    private int _rbacUsersNeedingReview;
    private int _accessReviewDays = 90;

    /// <summary>
    /// What this install's access-control posture IS, in the service's own words — including, when
    /// <c>rbac-config.json</c> did not load, the fact that it cannot say what the operator wanted
    /// because every answer is a default. Rendered at the TOP of the page, above the tab rail,
    /// because the operator has no reason to open "Security &amp; Access" — a lapse is by
    /// definition something no operator action caused.
    ///
    /// <para>The page renders <c>Headline</c>, <c>Detail</c> and <c>Reasons</c> and composes
    /// nothing of its own. It used to take only the reason LIST and pair it with a hard-coded
    /// headline, which is how the banner came to assert "RBAC is switched on" in seven states
    /// where the switch had never been read.</para>
    ///
    /// <para>A PROPERTY, not a cached field, deliberately. Seven code paths on this page mutate
    /// RBAC state (config save, four user edits, add, remove) and a cached copy is seven chances
    /// to leave a banner standing after its cause was repaired, or to miss one that appeared. A
    /// banner that survives its own cause stops being read.</para>
    /// </summary>
    private RbacService.EnforcementPosture EnforcementPosture => RbacService.DescribeEnforcementPosture();

    /// <summary>
    /// Set only by <see cref="BeginRbacConfigReplacement"/> — an operator who has read that
    /// <c>rbac-config.json</c> did not load, that the values they are about to see are defaults, and
    /// that saving overwrites the file. It is the ONLY thing that turns
    /// <see cref="StoreWriteIntent.ReplaceUnreadableStore"/> on, and it is never inferred from the
    /// page's state, because "the page is showing the controls" is not the operator's consent.
    ///
    /// <para>Reset by <see cref="LoadRbacSettings"/>, so it does not survive a navigation away and
    /// back: an acknowledgement that outlives the screen it was given on is not one.</para>
    /// </summary>
    private bool _rbacConfigReplacementStarted;

    /// <summary>
    /// Whether the RBAC controls may be drawn with values in them at all. False in exactly one
    /// state: the configuration store exists, did not load, and the operator has not asked for it to
    /// be replaced — where every value the controls could show is a default of this build's and
    /// drawing it asserts a choice nobody made.
    /// </summary>
    private bool RbacControlsMayShowValues
        => !RbacService.IsConfigStoreDamaged || _rbacConfigReplacementStarted;

    /// <summary>
    /// The operator's explicit "discard the unreadable file". Deliberately does NOT save anything —
    /// it only unlocks the controls, so the values that eventually reach disk are ones they saw and
    /// chose to keep, not ones a button wrote on their behalf.
    /// </summary>
    private void BeginRbacConfigReplacement()
    {
        _rbacConfigReplacementStarted = true;
        _rbacMessage = null;
        _rbacSuccess = false;
    }

    /// <summary>
    /// The role the Add-User form starts on. Admin while the store holds NO users, the configured
    /// default afterwards.
    ///
    /// <para><b>Why.</b> The empty-state line tells the operator to add the first Admin account,
    /// and the select beside it opened on Viewer. Measured 2026-08-25: following that line verbatim
    /// added a viewer, ticking Enable RBAC was then refused for want of an Admin, and the checkbox
    /// snapped back — the same shape as the defect this lane exists to fix, in this lane's own new
    /// copy. On a cold store an Admin is the only first user that leads anywhere, because
    /// <c>DescribeEnforcementBlockers</c> refuses every config that would enforce with no enabled
    /// Admin.</para>
    ///
    /// <para>It grants nothing. It preselects a value in a dropdown the operator can change before
    /// they click, on a page already behind <c>IsAuthorizedWithBreakGlass("settings")</c>, and
    /// <c>RbacService.AddUser</c> applies the same store guards either way.</para>
    /// </summary>
    /// <param name="existingUserCount">How many users the RBAC store holds right now.</param>
    internal static string DefaultNewUserRole(int existingUserCount)
        => existingUserCount == 0 ? AppRoles.Admin : AppRoles.Viewer;

    private void LoadRbacSettings()
    {
        _rbacConfigReplacementStarted = false;
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
        _rbacWindowsEnabled = cfg.Windows.Enabled;
        _rbacWindowsDomains = string.Join(", ", cfg.Windows.AllowedDomains);
        _rbacLocalPasswordEnabled = cfg.LocalPassword.Enabled;

        // Default the Add-User provider to one that is actually configured, so the operator's
        // first attempt is not refused by the guard for a provider they never chose.
        _newUserProvider =
            cfg.Windows.Enabled ? AuthProviders.Windows
            : cfg.Google.Enabled && !string.IsNullOrWhiteSpace(cfg.Google.ClientId) ? AuthProviders.Google
            : cfg.Microsoft.Enabled && !string.IsNullOrWhiteSpace(cfg.Microsoft.ClientId) ? AuthProviders.Microsoft
            : cfg.LocalPassword.Enabled ? AuthProviders.Local
            : AuthProviders.Windows;
        // Secrets: never pre-populate — leave blank (placeholder shows "(unchanged)")
        _rbacUsers = RbacService.GetUsers();

        // And default the ROLE the same way, for the same reason: the first attempt should not be
        // one the guards refuse. See DefaultNewUserRole. This runs where the page loads its stored
        // state — on init, and after a save the store refused — so a role the operator picked
        // themselves is never overruled by a re-render.
        _newUserRole = DefaultNewUserRole(_rbacUsers.Count);

        // SOC2 CC6.3: compute stale-review count
        _accessReviewDays = Configuration.GetValue<int>("Rbac:AccessReviewDays", 90);
        var reviewCutoff = DateTime.UtcNow.AddDays(-_accessReviewDays);
        _rbacUsersNeedingReview = _rbacUsers.Count(u =>
            !u.LastReviewedAt.HasValue || u.LastReviewedAt.Value < reviewCutoff);
    }

    /// <summary>
    /// L2 of the lockout guard: builds the config the UI is about to save and refuses it if
    /// enforcing it would leave nobody — including the operator doing the saving — able to sign
    /// in. Returns null when the save may proceed.
    ///
    /// <para>L1 (RbacService.IsRbacEnforced) already fails safe by staying dormant in that
    /// state, but silently doing nothing is its own trap: "I enabled RBAC and nothing changed".
    /// This names what is missing instead.</para>
    /// </summary>
    private string? RefuseUnsafeRbacConfig(RbacConfig prospective)
    {
        // TWO refusals, and the provider one is checked even with RBAC switched OFF. A provider
        // that is enabled with no usable Client Secret makes UseAuthentication() throw on every
        // request — /settings and /auth/login included — so saving it takes the host out entirely
        // and leaves nothing to undo it from. That is not an RBAC-enforcement question, which is
        // why it is a separate call and why the message below does not say "RBAC".
        var problems = new List<string>(RbacService.DescribeProviderConfigProblems(prospective));
        problems.AddRange(RbacService.DescribeEnforcementBlockers(prospective));
        if (problems.Count == 0) return null;

        return "This configuration was not saved. " + string.Join(" ", problems);
    }

    private void SaveRbacConfig()
    {
        var existing = RbacService.Config;

        // Capture old values before mutating (CC8.2 prior-value capture).
        // Secrets are intentionally omitted from the audit record.
        //
        // The prior-value record is a CLAIM like any other, and in the damaged state the object it
        // would be read from is a default rather than a prior value — an audit line reading
        // "Enabled=false → Enabled=true" would assert that someone had it off, which is the same
        // false attribution the checkbox was making. So it says what it actually knows.
        var oldSummary = RbacService.IsConfigStoreDamaged
            ? $"(unknown: {RbacService.ConfigPath} exists and did not load, so no prior value was read)"
            : $"Enabled={existing.Enabled},RequireExplicit={existing.RequireExplicitAccess},DefaultRole={existing.DefaultRole}," +
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
            Windows = new WindowsAuthConfig
            {
                Enabled = _rbacWindowsEnabled,
                AllowedDomains = string.IsNullOrWhiteSpace(_rbacWindowsDomains)
                    ? new List<string>()
                    : _rbacWindowsDomains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                         .ToList(),
            },
            LocalPassword = new LocalPasswordConfig { Enabled = _rbacLocalPasswordEnabled },
        };

        // Refuse the save rather than persist a config that locks everyone out.
        var refusal = RefuseUnsafeRbacConfig(newConfig);
        if (refusal != null)
        {
            _rbacMessage = refusal;
            _rbacSuccess = false;
            // Snap the toggle back so the UI does not claim a state that was not saved.
            _rbacEnabled = existing.Enabled;
            return;
        }

        // The intent is the operator's acknowledgement and nothing else. If they have not given it,
        // this is the ordinary value and the service refuses the write when the store did not load.
        var intent = _rbacConfigReplacementStarted
            ? StoreWriteIntent.ReplaceUnreadableStore
            : StoreWriteIntent.FromLoadedStore;

        var saved = RbacService.UpdateConfig(newConfig, intent);
        if (saved != StoreWriteOutcome.Saved)
        {
            _rbacMessage = saved == StoreWriteOutcome.RefusedStoreUnreadable
                ? "Nothing was saved. " + RbacService.ConfigPath + " exists and did not load, so these "
                  + "controls hold this build's defaults rather than your settings, and writing them would "
                  + "overwrite the file without recording that it was ever damaged."
                // WriteFailed: the store is fine, the disk is not. No restore-the-file advice belongs
                // here, and the audit line below must not be written for a change that did not land.
                : "Nothing was saved. " + RbacService.ConfigPath + " could not be written — the disk is full, "
                  + "the file is locked, or permissions deny it. The settings shown have been put back to what "
                  + "is on disk. Check the log for the exact error, then try again.";
            _rbacSuccess = false;
            // Re-read so the fields go back to matching whatever the service is actually holding.
            LoadRbacSettings();
            return;
        }

        var newSummary = $"Enabled={_rbacEnabled},RequireExplicit={_rbacRequireExplicit},DefaultRole={_rbacDefaultRole}," +
                         $"GoogleEnabled={_rbacGoogleEnabled},GoogleClientId={_rbacGoogleClientId},GoogleDomain={_rbacGoogleDomain}," +
                         $"MsEnabled={_rbacMsEnabled},MsClientId={_rbacMsClientId},MsDomain={_rbacMsDomain}";
        AuditLog.LogConfigurationChange("Settings", "updated", oldSummary, newSummary, ReviewerName);
    }

    private void SaveOAuthConfig()
    {
        SaveRbacConfig();

        // Only claim success if SaveRbacConfig did not already report a refusal. This line used to
        // be unconditional, which would have printed "saved" over the top of the sentence saying
        // nothing had been.
        if (_rbacMessage != null && !_rbacSuccess) return;

        _rbacMessage = "OAuth config saved.";
        _rbacSuccess = true;
    }

    private string ReviewerName => UserState.Role == AppRoles.Admin
        ? Environment.UserName
        : UserState.Role;

    /// <summary>
    /// True when the change did NOT reach rbac-users.json, having said why. Every user mutation on
    /// this page goes through it before writing an audit record, because an audit line for a change
    /// that never reached the disk is a fabricated fact — the same defect as a checkbox drawn from a
    /// default, on the surface that is supposed to be the evidence.
    ///
    /// <para>It covers BOTH ways a write fails to land, which is why it is not named for the
    /// refusal. It used to test only <see cref="StoreWriteOutcome.RefusedStoreUnreadable"/>, so a
    /// thrown write — full disk, locked file, denied ACL — fell through as success and this page
    /// announced an added admin and recorded it in the audit log.</para>
    /// </summary>
    private bool UserWriteDidNotReachDisk(StoreWriteOutcome outcome)
    {
        if (outcome == StoreWriteOutcome.RefusedStoreUnreadable)
        {
            // No discard-and-re-enter button here, unlike the configuration above, and the asymmetry is
            // deliberate: a config is a dozen fields an operator can retype from this screen, while a
            // user store is principals and password hashes that cannot be re-derived from anything. A
            // button offering to replace it would be a button that deletes accounts.
            _rbacMessage = "Nothing was changed. The user store exists and did not load, so the list on this page is "
                         + "not this install's accounts — writing to it would delete every account, role and password "
                         + "on the server. " + RbacService.DescribeStoreRecovery(forConfigStore: false)
                         + " Or move it aside if you mean to start over.";
        }
        else if (outcome == StoreWriteOutcome.WriteFailed)
        {
            // The other half of "did not reach the disk", and it needs its own sentence: nothing is
            // wrong with the store, so none of the restore-the-file advice applies. This branch is
            // why the method is no longer named for the refusal — it used to return false here, and
            // the caller went on to write an audit record for a change that never happened.
            _rbacMessage = "Nothing was changed. The user store could not be written — the disk is full, the file "
                         + "is locked, or permissions deny it. The list on this page has been put back to what is "
                         + "on disk. Check the log for the exact error, then try again.";
        }
        else return false;

        _rbacSuccess = false;
        _rbacUsers = RbacService.GetUsers();
        return true;
    }

    private void ChangeUserRole(RbacUser user, string role)
    {
        var oldRole = user.Role;
        if (RefuseIfItStrandsTheInstall(u => { if (u.Id == user.Id) u.Role = role; })) return;

        user.Role = role;
        if (UserWriteDidNotReachDisk(RbacService.UpdateUser(user))) return;

        AuditLog.LogUserUpdated(ReviewerName, user.Email, "Role", oldRole, role);
        _rbacUsers = RbacService.GetUsers();
    }

    private void ToggleUserEnabled(RbacUser user, bool enabled)
    {
        var oldEnabled = user.Enabled.ToString();
        if (RefuseIfItStrandsTheInstall(u => { if (u.Id == user.Id) u.Enabled = enabled; })) return;

        user.Enabled = enabled;
        if (UserWriteDidNotReachDisk(RbacService.UpdateUser(user))) return;

        AuditLog.LogUserUpdated(ReviewerName, user.Email, "Enabled", oldEnabled, enabled.ToString());
        _rbacUsers = RbacService.GetUsers();
    }

    private void RemoveRbacUser(string id)
    {
        var existing = _rbacUsers.FirstOrDefault(u => u.Id == id);
        var formerRole = existing?.Role ?? string.Empty;
        var userName = existing?.Email ?? id;

        if (RefuseIfItStrandsTheInstall(null, removeId: id)) return;

        if (UserWriteDidNotReachDisk(RbacService.RemoveUser(id))) return;

        AuditLog.LogUserRemoved(ReviewerName, userName, formerRole);
        _rbacUsers = RbacService.GetUsers();
    }

    /// <summary>
    /// L2, applied to the user list: pre-flights a removal / disable / demotion against the
    /// CURRENT config and refuses it if it would leave the enforced install with no usable
    /// admin. Removing the last admin who can actually sign in is the same lockout as enabling
    /// RBAC with no provider, reached from the other side.
    /// </summary>
    /// <param name="mutate">Applied to a COPY of each user to build the prospective list.</param>
    /// <param name="removeId">Id to drop from the prospective list, if any.</param>
    /// <returns>True when the change was refused (and <c>_rbacMessage</c> explains why).</returns>
    private bool RefuseIfItStrandsTheInstall(Action<RbacUser>? mutate, string? removeId = null)
    {
        // Nothing to strand if RBAC is not being enforced.
        if (!RbacService.Config.Enabled) return false;

        var prospective = RbacService.GetUsers()
            .Where(u => removeId == null || u.Id != removeId)
            .Select(u => new RbacUser
            {
                Id = u.Id,
                Email = u.Email,
                DisplayName = u.DisplayName,
                Provider = u.Provider,
                Sid = u.Sid,
                Role = u.Role,
                Enabled = u.Enabled,
                PasswordHash = u.PasswordHash,
            })
            .ToList();

        if (mutate != null)
            foreach (var u in prospective) mutate(u);

        var blockers = RbacService.DescribeEnforcementBlockers(RbacService.Config, prospective);
        if (blockers.Count == 0) return false;

        _rbacMessage = "That change would lock everyone out of this server: " + string.Join(" ", blockers);
        _rbacSuccess = false;
        _rbacUsers = RbacService.GetUsers();   // re-read so the UI snaps back to the saved state
        return true;
    }

    private void AddRbacUser()
    {
        _rbacMessage = null;
        _rbacSuccess = false;

        var provider = _newUserProvider;
        var raw = (_newUserEmail ?? string.Empty).Trim();

        if (raw.Length == 0)
        {
            _rbacMessage = AuthProviders.IsWindows(provider)
                ? $@"Enter a Windows account, e.g. {Environment.MachineName}\admin.adrian or CONTOSO\adrian."
                : "Enter an email address.";
            return;
        }

        // Refusing to add a principal that can never sign in is the form's actual job. Accepting
        // the string and letting the operator discover later that the account is inert is how
        // Defect 2 turns into a lockout — Adrian typed local\admin.adrian and got told to enter
        // an email address, which was true of the field and useless as guidance.
        var cfg = RbacService.Config;
        string keyToAdd;

        if (AuthProviders.IsWindows(provider))
        {
            if (!cfg.Windows.Enabled)
            {
                _rbacMessage = "Windows authentication is switched off, so a Windows account could not sign in. "
                             + "Enable it under Access Control above, then add the account.";
                return;
            }

            var parsed = WindowsIdentityKey.Parse(raw);
            if (!parsed.Ok)
            {
                _rbacMessage = parsed.Error;
                return;
            }
            keyToAdd = parsed.Key;
        }
        else
        {
            if (!raw.Contains('@'))
            {
                _rbacMessage = "Enter a valid email address, or switch the provider to Windows to add a "
                             + $@"machine or domain account such as {Environment.MachineName}\admin.adrian.";
                return;
            }

            var providerEnabled = provider switch
            {
                AuthProviders.Google => cfg.Google.Enabled && !string.IsNullOrWhiteSpace(cfg.Google.ClientId),
                AuthProviders.Microsoft => cfg.Microsoft.Enabled && !string.IsNullOrWhiteSpace(cfg.Microsoft.ClientId),
                _ => cfg.LocalPassword.Enabled
            };
            if (!providerEnabled)
            {
                _rbacMessage = $"The {provider} sign-in method is not configured, so that account could not sign in. "
                             + "Configure it under Access Control above, then add the user.";
                return;
            }
            keyToAdd = raw.ToLowerInvariant();
        }

        if (RbacService.FindUser(provider, keyToAdd) != null)
        {
            _rbacMessage = $"{keyToAdd} is already in the list.";
            return;
        }

        var roleToAdd = _newUserRole;
        var added = RbacService.AddUser(new RbacUser
        {
            Email = keyToAdd,
            DisplayName = keyToAdd,
            Provider = AuthProviders.IsWindows(provider) ? AuthProviders.Windows : provider,
            Role = roleToAdd,
        });
        if (UserWriteDidNotReachDisk(added)) return;

        AuditLog.LogUserAdded(ReviewerName, keyToAdd, roleToAdd);
        _newUserEmail = string.Empty;
        _rbacUsers = RbacService.GetUsers();
        _rbacMessage = $"Added {keyToAdd} as {roleToAdd}.";
        _rbacSuccess = true;
    }

    // SOC2 CC6.3 ─────────────────────────────────────────────────────────

    private void MarkUserAccessReviewed(RbacUser user)
    {
        user.LastReviewedAt = DateTime.UtcNow;
        user.LastReviewedBy = ReviewerName;
        if (UserWriteDidNotReachDisk(RbacService.UpdateUser(user))) return;

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

    /// <summary>
    /// Colour for <c>UpdateCheckResult</c>. Only a check that reached the endpoint and found this
    /// build current earns green; a disabled or failed check is not reassurance.
    /// </summary>
    private string UpdateCheckResultColor => UpdateService.LastCheckState switch
    {
        UpdateCheckState.UpdateAvailable => "var(--orange)",
        UpdateCheckState.UpToDate => "var(--green)",
        UpdateCheckState.Failed => "var(--red)",
        _ => "var(--text-muted)"
    };

    private async Task CheckForUpdates()
    {
        IsCheckingUpdates = true;
        UpdateCheckResult = null;
        StateHasChanged();

        var (available, info) = await UpdateService.CheckForUpdatesAsync();
        UpdateAvailable = available;
        UpdateInfo = info;
        // Never report "latest version" off a false Available flag — that is also what a disabled
        // or failed check returns.
        UpdateCheckResult = UpdateService.LastCheckState switch
        {
            UpdateCheckState.UpdateAvailable => $"Update available: Version {info?.Version}",
            UpdateCheckState.UpToDate => "You are running the latest version",
            UpdateCheckState.Disabled => "Update checks are turned off in this build's configuration (Updates:Enabled = false). Version not checked.",
            UpdateCheckState.Failed => $"Update check failed: {UpdateService.LastCheckError ?? "see the log for details"}",
            _ => "Update check did not run."
        };

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
                UpdateInfo.SignatureUrl,
                new Progress<int>(p => { _downloadProgress = p; _ = InvokeAsync(StateHasChanged); }));
            if (!ok)
                UpdateCheckResult = UpdateService.LastCheckError ?? "Download failed — check the log for details.";
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
                // The old line said "All dashboard data will be recreated on next refresh". That was
                // never true and is now doubly untrue. TimeSeries panels fetch DELTAS, so the next
                // refresh pulls only rows newer than the flush and the earlier part of each window is
                // gone until wall-clock time refills it. And retained history (metric_history) is not a
                // cache table: InvalidateAllAsync deliberately does not touch it, so the panels that
                // retain history keep their trends. Say both.
                FlushCacheResult = "Cache flushed. Panels refetch on the next refresh, and time-series "
                    + "panels start their window again from now rather than redrawing the part that was "
                    + "flushed. Retained history is not cache and was not cleared.";
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
        _azureSasToken = "sp=acw&st=2026-06-03T08:42:01Z&se=2028-06-03T16:57:01Z&spr=https&sv=2026-02-06&sr=d&sig=KWVOzoCcM2YRUaEobNq93%2BIDGIEUjzHqNpAdTz3Fbr4%3D&sdd=1";
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

