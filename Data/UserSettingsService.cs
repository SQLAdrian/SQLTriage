/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Text.Json;

namespace SqlHealthAssessment.Data
{
    /// <summary>
    /// Service for managing user settings that persist across sessions.
    /// Stores settings in a JSON file in the application directory.
    /// Thread-safe: all reads/writes are protected by a lock.
    /// </summary>
    public class UserSettingsService
    {
        private readonly string _settingsFilePath;
        private readonly object _lock = new();
        private UserSettings _settings;

        public UserSettingsService()
        {
            _settingsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "user-settings.json");
            _settings = LoadSettings();
        }

        /// <summary>
        /// User settings that persist across sessions
        /// </summary>
        public class UserSettings
        {
            public string SelectedTheme { get; set; } = "cyberpunk";
            public int RefreshIntervalSeconds { get; set; } = 15;
            public bool AutoRefresh { get; set; } = true;
            public int DefaultTimeRangeMinutes { get; set; } = 60;
            public bool ShowDiagnosticPane { get; set; } = false;
            public string DefaultDashboardId { get; set; } = "";
            /// <summary>Data source: "sqlwatch" or "pm" (PerformanceMonitor)</summary>
            public string DataSource { get; set; } = "master";
            /// <summary>Radzen Blazor UI theme name (e.g. "dark", "material3", "fluent-dark")</summary>
            public string RadzenUiTheme { get; set; } = "dark";
            /// <summary>WebView2 zoom level as a percentage (e.g. 100, 125, 150). Default 150.</summary>
            public int ZoomLevel { get; set; } = 150;

            // ── Auto-Export Settings ──
            public bool AutoExportAuditCsv { get; set; } = true;
            public bool AutoExportAuditJson { get; set; } = false;
            public bool AutoExportAuditPdf { get; set; } = false;
            public bool AutoExportQuickCheckCsv { get; set; } = true;
            public bool AutoExportQuickCheckPdf { get; set; } = true;
            public bool AutoExportVulnerabilityAssessmentCsv { get; set; } = true;
            public bool AutoExportVulnerabilityAssessmentPdf { get; set; } = false;

            // ── Diagnostics ──
            /// <summary>When true, silent catch blocks emit warnings to the log file. No restart required.</summary>
            public bool EnableDebugLogging { get; set; } = false;
        }

        private UserSettings LoadSettings() => ConfigFileHelper.Load<UserSettings>(_settingsFilePath);

        public void SaveSettings()
        {
            lock (_lock)
            {
                try { ConfigFileHelper.Save(_settingsFilePath, _settings); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[UserSettings] Save failed: {ex.Message}");
                }
            }
        }

        public string GetRadzenUiTheme() { lock (_lock) return _settings.RadzenUiTheme; }

        public void SetRadzenUiTheme(string theme)
        {
            lock (_lock) _settings.RadzenUiTheme = theme;
            SaveSettings();
        }

        public string GetSelectedTheme() { lock (_lock) return _settings.SelectedTheme; }

        public void SetSelectedTheme(string theme)
        {
            lock (_lock) _settings.SelectedTheme = theme;
            SaveSettings();
        }

        public int GetRefreshInterval() { lock (_lock) return _settings.RefreshIntervalSeconds; }

        public void SetRefreshInterval(int seconds)
        {
            lock (_lock) _settings.RefreshIntervalSeconds = seconds;
            SaveSettings();
        }

        public bool GetAutoRefresh() { lock (_lock) return _settings.AutoRefresh; }

        public void SetAutoRefresh(bool enabled)
        {
            lock (_lock) _settings.AutoRefresh = enabled;
            SaveSettings();
        }

        public int GetDefaultTimeRange() { lock (_lock) return _settings.DefaultTimeRangeMinutes; }

        public void SetDefaultTimeRange(int minutes)
        {
            lock (_lock) _settings.DefaultTimeRangeMinutes = minutes;
            SaveSettings();
        }

        public bool GetShowDiagnosticPane() { lock (_lock) return _settings.ShowDiagnosticPane; }

        public void SetShowDiagnosticPane(bool enabled)
        {
            lock (_lock) _settings.ShowDiagnosticPane = enabled;
            SaveSettings();
        }

        public string GetDefaultDashboardId() { lock (_lock) return _settings.DefaultDashboardId; }

        public void SetDefaultDashboardId(string dashboardId)
        {
            lock (_lock) _settings.DefaultDashboardId = dashboardId;
            SaveSettings();
        }

        public string GetDataSource() { lock (_lock) return _settings.DataSource; }

        public void SetDataSource(string source)
        {
            lock (_lock) _settings.DataSource = source;
            SaveSettings();
        }

        public int GetZoomLevel() { lock (_lock) return _settings.ZoomLevel; }

        public void SetZoomLevel(int zoomPercent)
        {
            lock (_lock) _settings.ZoomLevel = zoomPercent;
            SaveSettings();
            OnZoomChanged?.Invoke(zoomPercent);
        }

        /// <summary>
        /// Fired when zoom level changes so MainWindow can apply it to WebView2.
        /// </summary>
        public event Action<int>? OnZoomChanged;

        // ── Debug Logging ──
        public bool GetDebugLogging() { lock (_lock) return _settings.EnableDebugLogging; }

        public void SetDebugLogging(bool enabled)
        {
            lock (_lock) _settings.EnableDebugLogging = enabled;
            SaveSettings();
            OnDebugLoggingChanged?.Invoke(enabled);
        }

        /// <summary>Fired when debug logging is toggled so App can adjust Serilog level at runtime.</summary>
        public event Action<bool>? OnDebugLoggingChanged;

        // ── Auto-Export Accessors ──
        public UserSettings GetSettings() { lock (_lock) return _settings; }

        public void UpdateAutoExportSettings(
            bool auditCsv, bool auditJson, bool auditPdf,
            bool quickCheckCsv, bool quickCheckPdf,
            bool vaCsv, bool vaPdf)
        {
            lock (_lock)
            {
                _settings.AutoExportAuditCsv = auditCsv;
                _settings.AutoExportAuditJson = auditJson;
                _settings.AutoExportAuditPdf = auditPdf;
                _settings.AutoExportQuickCheckCsv = quickCheckCsv;
                _settings.AutoExportQuickCheckPdf = quickCheckPdf;
                _settings.AutoExportVulnerabilityAssessmentCsv = vaCsv;
                _settings.AutoExportVulnerabilityAssessmentPdf = vaPdf;
            }
            SaveSettings();
        }
    }
}
