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
    public class UserSettingsService : Services.IUserSettingsService
    {
        private readonly string _settingsFilePath;
        private readonly object _lock = new();
        private UserSettings _settings;

        public UserSettingsService()
        {
            var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SqlHealthAssessment");
            Directory.CreateDirectory(appDataDir);
            _settingsFilePath = Path.Combine(appDataDir, "user-settings.json");
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
            public bool AutoExportAuditCsv { get; set; } = false;
            public bool AutoExportAuditJson { get; set; } = false;
            public bool AutoExportAuditPdf { get; set; } = false;
            public bool AutoExportQuickCheckCsv { get; set; } = false;
            public bool AutoExportQuickCheckPdf { get; set; } = false;
            public bool AutoExportVulnerabilityAssessmentCsv { get; set; } = false;
            public bool AutoExportVulnerabilityAssessmentPdf { get; set; } = false;

            // ── Diagnostics ──
            /// <summary>When true, silent catch blocks emit warnings to the log file. No restart required.</summary>
            public bool EnableDebugLogging { get; set; } = false;
            /// <summary>When true, real server names are replaced with SRV-001 aliases in log output.</summary>
            public bool AnonymiseServerNames { get; set; } = false;

            // ── Query Plan Icons ──
            /// <summary>When true, uses high-res individual PNG icons for query plans. When false, uses coloured sprite sheet (v1).</summary>
            public bool UseV2PlanIcons { get; set; } = false;

            // ── VA Query Visibility ──
            /// <summary>When true, the SQL query executed for each VA check is shown inline in the results table.</summary>
            public bool ShowVaQueries { get; set; } = false;

            // ── Onboarding ──
            /// <summary>Set to true once the user completes or dismisses the first-run onboarding wizard.</summary>
            public bool OnboardingComplete { get; set; } = false;

            // ── Release Notes ──
            /// <summary>The last version for which the "What's new" modal was shown. Empty = never shown.</summary>
            public string LastSeenVersion { get; set; } = "";
            /// <summary>When true, shows the "What's new" modal automatically when the version changes.</summary>
            public bool ShowReleaseNotesOnUpdate { get; set; } = true;

            // ── No-Pants Mode ──
            /// <summary>When true, shows dangerous server-modification controls in dashboards. Off by default.</summary>
            public bool NoPantsMode { get; set; } = false;
            /// <summary>Whether the user has accepted the no-pants disclaimer at least once.</summary>
            public bool NoPantsDisclaimerAccepted { get; set; } = false;

            // ── Live Sessions Monitoring ──
            /// <summary>Auto-refresh enabled for Live Sessions page.</summary>
            public bool SessionsAutoRefresh { get; set; } = true;
            /// <summary>Refresh interval in seconds for Live Sessions page.</summary>
            public int SessionsRefreshInterval { get; set; } = 5;
            /// <summary>Hide sleeping sessions by default.</summary>
            public bool SessionsHideSleeping { get; set; } = false;
            /// <summary>Show only blocked/blocking sessions.</summary>
            public bool SessionsShowOnlyBlocked { get; set; } = false;
            /// <summary>Hide low-IO sessions.</summary>
            public bool SessionsHideLowIO { get; set; } = false;
            /// <summary>Search text for filtering sessions.</summary>
            public string SessionsSearchText { get; set; } = "";
            /// <summary>Maximum number of sessions to display.</summary>
            public int SessionsMaxDisplay { get; set; } = 500;

            // ── Experimental Mode ──
            /// <summary>When true, shows experimental/preview features that are not yet production-ready.</summary>
            public bool ExperimentalMode { get; set; } = false;
            /// <summary>When true, shows the Maturity Roadmap page in the nav. Requires No-Pants + Experimental. Off by default.</summary>
            public bool ShowMaturityRoadmap { get; set; } = false;

            // ── Vulnerability Assessment Scheduled PDF ──
            public bool VaScheduledPdfEnabled { get; set; } = false;
            public string VaScheduledPdfType { get; set; } = "Weekly";
            public string VaScheduledPdfTime { get; set; } = "07:30";
            public int VaScheduledPdfDayOfWeek { get; set; } = 1;
            public int VaScheduledPdfDayOfMonth { get; set; } = 1;
            public DateTime VaScheduledPdfLastRun { get; set; } = DateTime.MinValue;

            // ── Roadmap Scheduled PDF ──
            /// <summary>When true, the Diagnostics Roadmap page auto-exports a PDF on the configured schedule.</summary>
            public bool RoadmapScheduledPdfEnabled { get; set; } = false;
            /// <summary>Schedule type: "Daily", "Weekly", "Monthly".</summary>
            public string RoadmapScheduledPdfType { get; set; } = "Weekly";
            /// <summary>Time of day in HH:mm for the scheduled export.</summary>
            public string RoadmapScheduledPdfTime { get; set; } = "07:00";
            /// <summary>Day of week (0=Sun…6=Sat) for Weekly schedule.</summary>
            public int RoadmapScheduledPdfDayOfWeek { get; set; } = 1; // Monday
            /// <summary>Day of month (1-28) for Monthly schedule.</summary>
            public int RoadmapScheduledPdfDayOfMonth { get; set; } = 1;
            /// <summary>When true, exports one PDF per domain instead of a combined PDF.</summary>
            public bool RoadmapScheduledPdfSplitByDomain { get; set; } = false;
            /// <summary>Last time the scheduled roadmap PDF was exported (UTC).</summary>
            public DateTime RoadmapScheduledPdfLastRun { get; set; } = DateTime.MinValue;

            /// <summary>Immutable snapshot of VA schedule settings for thread-safe reads.</summary>
            public record VaScheduleSnapshot(
                bool Enabled,
                string Type,
                string Time,
                int DayOfWeek,
                int DayOfMonth,
                DateTime LastRun)
            {
                public VaScheduleSnapshot(UserSettings s) : this(
                    s.VaScheduledPdfEnabled,
                    s.VaScheduledPdfType,
                    s.VaScheduledPdfTime,
                    s.VaScheduledPdfDayOfWeek,
                    s.VaScheduledPdfDayOfMonth,
                    s.VaScheduledPdfLastRun)
                { }
            }

            /// <summary>Immutable snapshot of roadmap schedule settings for thread-safe reads.</summary>
            public record RoadmapScheduleSnapshot(
                bool Enabled,
                string Type,
                string Time,
                int DayOfWeek,
                int DayOfMonth,
                bool SplitByDomain,
                DateTime LastRun)
            {
                public RoadmapScheduleSnapshot(UserSettings s) : this(
                    s.RoadmapScheduledPdfEnabled,
                    s.RoadmapScheduledPdfType,
                    s.RoadmapScheduledPdfTime,
                    s.RoadmapScheduledPdfDayOfWeek,
                    s.RoadmapScheduledPdfDayOfMonth,
                    s.RoadmapScheduledPdfSplitByDomain,
                    s.RoadmapScheduledPdfLastRun)
                { }
            }
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

        // ── Anonymise Server Names in Logs ──
        public bool GetAnonymiseServerNames() { lock (_lock) return _settings.AnonymiseServerNames; }

        public void SetAnonymiseServerNames(bool enabled)
        {
            lock (_lock) _settings.AnonymiseServerNames = enabled;
            SaveSettings();
        }

        // ── Query Plan Icons ──
        public bool GetUseV2PlanIcons() { lock (_lock) return _settings.UseV2PlanIcons; }

        public void SetUseV2PlanIcons(bool enabled)
        {
            lock (_lock) _settings.UseV2PlanIcons = enabled;
            SaveSettings();
        }

        // ── VA Query Visibility ──
        public bool GetShowVaQueries() { lock (_lock) return _settings.ShowVaQueries; }

        public void SetShowVaQueries(bool enabled)
        {
            lock (_lock) _settings.ShowVaQueries = enabled;
            SaveSettings();
        }

        // ── No-Pants Mode ──
        public bool GetNoPantsMode() { lock (_lock) return _settings.NoPantsMode; }

        public void SetNoPantsMode(bool enabled)
        {
            lock (_lock) _settings.NoPantsMode = enabled;
            SaveSettings();
            OnNoPantsModeChanged?.Invoke(enabled);
        }

        public bool GetNoPantsDisclaimerAccepted() { lock (_lock) return _settings.NoPantsDisclaimerAccepted; }

        public void SetNoPantsDisclaimerAccepted(bool accepted)
        {
            lock (_lock) _settings.NoPantsDisclaimerAccepted = accepted;
            SaveSettings();
        }

        /// <summary>Fired when no-pants mode is toggled so dashboard components can show/hide dangerous controls.</summary>
        public event Action<bool>? OnNoPantsModeChanged;

        // ── Experimental Mode ──
        public bool GetExperimentalMode() { lock (_lock) return _settings.ExperimentalMode; }

        public void SetExperimentalMode(bool enabled)
        {
            lock (_lock) _settings.ExperimentalMode = enabled;
            SaveSettings();
            OnExperimentalModeChanged?.Invoke(enabled);
        }

        /// <summary>Fired when experimental mode is toggled.</summary>
        public event Action<bool>? OnExperimentalModeChanged;

        // ── Maturity Roadmap ──
        public bool GetShowMaturityRoadmap() { lock (_lock) return _settings.ShowMaturityRoadmap; }
        public void SetShowMaturityRoadmap(bool enabled)
        {
            lock (_lock) _settings.ShowMaturityRoadmap = enabled;
            SaveSettings();
            OnShowMaturityRoadmapChanged?.Invoke(enabled);
        }

        /// <summary>Fired when Show Maturity Roadmap is toggled.</summary>
        public event Action<bool>? OnShowMaturityRoadmapChanged;

        // ── Onboarding ──
        public bool GetOnboardingComplete() { lock (_lock) return _settings.OnboardingComplete; }
        public void SetOnboardingComplete(bool complete) { lock (_lock) _settings.OnboardingComplete = complete; SaveSettings(); }

        // ── Release Notes ──
        public string GetLastSeenVersion() { lock (_lock) return _settings.LastSeenVersion; }
        public void SetLastSeenVersion(string version) { lock (_lock) _settings.LastSeenVersion = version; SaveSettings(); }
        public bool GetShowReleaseNotesOnUpdate() { lock (_lock) return _settings.ShowReleaseNotesOnUpdate; }
        public void SetShowReleaseNotesOnUpdate(bool value) { lock (_lock) _settings.ShowReleaseNotesOnUpdate = value; SaveSettings(); }

        // ── Live Sessions Monitoring ──
        public bool GetSessionsAutoRefresh() { lock (_lock) return _settings.SessionsAutoRefresh; }
        public void SetSessionsAutoRefresh(bool value) { lock (_lock) _settings.SessionsAutoRefresh = value; SaveSettings(); }

        public int GetSessionsRefreshInterval() { lock (_lock) return _settings.SessionsRefreshInterval; }
        public void SetSessionsRefreshInterval(int seconds) { lock (_lock) _settings.SessionsRefreshInterval = seconds; SaveSettings(); }

        public bool GetSessionsHideSleeping() { lock (_lock) return _settings.SessionsHideSleeping; }
        public void SetSessionsHideSleeping(bool value) { lock (_lock) _settings.SessionsHideSleeping = value; SaveSettings(); }

        public bool GetSessionsShowOnlyBlocked() { lock (_lock) return _settings.SessionsShowOnlyBlocked; }
        public void SetSessionsShowOnlyBlocked(bool value) { lock (_lock) _settings.SessionsShowOnlyBlocked = value; SaveSettings(); }

        public bool GetSessionsHideLowIO() { lock (_lock) return _settings.SessionsHideLowIO; }
        public void SetSessionsHideLowIO(bool value) { lock (_lock) _settings.SessionsHideLowIO = value; SaveSettings(); }

        public string GetSessionsSearchText() { lock (_lock) return _settings.SessionsSearchText; }
        public void SetSessionsSearchText(string text) { lock (_lock) _settings.SessionsSearchText = text; SaveSettings(); }

        public int GetSessionsMaxDisplay() { lock (_lock) return _settings.SessionsMaxDisplay; }
        public void SetSessionsMaxDisplay(int count) { lock (_lock) _settings.SessionsMaxDisplay = count; SaveSettings(); }

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

        // ── VA Scheduled PDF ──
        public UserSettings.VaScheduleSnapshot GetVaSchedule()
        {
            lock (_lock) return new UserSettings.VaScheduleSnapshot(_settings);
        }

        public void SaveVaSchedule(bool enabled, string type, string time, int dayOfWeek, int dayOfMonth)
        {
            lock (_lock)
            {
                _settings.VaScheduledPdfEnabled = enabled;
                _settings.VaScheduledPdfType = type;
                _settings.VaScheduledPdfTime = time;
                _settings.VaScheduledPdfDayOfWeek = dayOfWeek;
                _settings.VaScheduledPdfDayOfMonth = dayOfMonth;
            }
            SaveSettings();
        }

        public void UpdateVaScheduleLastRun(DateTime utcNow)
        {
            lock (_lock) _settings.VaScheduledPdfLastRun = utcNow;
            SaveSettings();
        }

        // ── Roadmap Scheduled PDF ──
        public UserSettings.RoadmapScheduleSnapshot GetRoadmapSchedule()
        {
            lock (_lock) return new UserSettings.RoadmapScheduleSnapshot(_settings);
        }

        public void SaveRoadmapSchedule(bool enabled, string type, string time, int dayOfWeek, int dayOfMonth, bool splitByDomain)
        {
            lock (_lock)
            {
                _settings.RoadmapScheduledPdfEnabled = enabled;
                _settings.RoadmapScheduledPdfType = type;
                _settings.RoadmapScheduledPdfTime = time;
                _settings.RoadmapScheduledPdfDayOfWeek = dayOfWeek;
                _settings.RoadmapScheduledPdfDayOfMonth = dayOfMonth;
                _settings.RoadmapScheduledPdfSplitByDomain = splitByDomain;
            }
            SaveSettings();
        }

        public void UpdateRoadmapScheduleLastRun(DateTime utcNow)
        {
            lock (_lock) _settings.RoadmapScheduledPdfLastRun = utcNow;
            SaveSettings();
        }
    }
}
