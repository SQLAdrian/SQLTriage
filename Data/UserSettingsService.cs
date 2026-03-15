/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Text.Json;

namespace SqlHealthAssessment.Data
{
    /// <summary>
    /// Service for managing user settings that persist across sessions.
    /// Stores settings in a JSON file in the application directory.
    /// </summary>
    public class UserSettingsService
    {
        private readonly string _settingsFilePath;
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
            public double TimezoneOffsetHours { get; set; } = 13; // Default to UTC+13 (NZST)
            public string SelectedTheme { get; set; } = "cyberpunk";
            public int RefreshIntervalSeconds { get; set; } = 35;
            public bool AutoRefresh { get; set; } = true;
            public int DefaultTimeRangeMinutes { get; set; } = 60;
            public bool ShowDiagnosticPane { get; set; } = false;
            public string DefaultDashboardId { get; set; } = "";
            /// <summary>Data source: "sqlwatch" or "pm" (PerformanceMonitor)</summary>
            public string DataSource { get; set; } = "master";
        }

        /// <summary>
        /// Load settings from file or return defaults
        /// </summary>
        private UserSettings LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    var json = File.ReadAllText(_settingsFilePath);
                    var settings = JsonSerializer.Deserialize<UserSettings>(json);
                    if (settings != null)
                    {
                        return settings;
                    }
                }
            }
            catch
            {
                // If there's any error, return defaults
            }

            return new UserSettings();
        }

        /// <summary>
        /// Save settings to file
        /// </summary>
        public void SaveSettings()
        {
            try
            {
                var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(_settingsFilePath, json);
            }
            catch
            {
                // Silently fail if unable to save
            }
        }

        /// <summary>
        /// Get current timezone offset
        /// </summary>
        public double GetTimezoneOffset()
        {
            return _settings.TimezoneOffsetHours;
        }

        /// <summary>
        /// Set timezone offset and save
        /// </summary>
        public void SetTimezoneOffset(double offset)
        {
            _settings.TimezoneOffsetHours = offset;
            SaveSettings();
        }

        /// <summary>
        /// Get selected theme
        /// </summary>
        public string GetSelectedTheme()
        {
            return _settings.SelectedTheme;
        }

        /// <summary>
        /// Set selected theme and save
        /// </summary>
        public void SetSelectedTheme(string theme)
        {
            _settings.SelectedTheme = theme;
            SaveSettings();
        }

        /// <summary>
        /// Get refresh interval
        /// </summary>
        public int GetRefreshInterval()
        {
            return _settings.RefreshIntervalSeconds;
        }

        /// <summary>
        /// Set refresh interval and save
        /// </summary>
        public void SetRefreshInterval(int seconds)
        {
            _settings.RefreshIntervalSeconds = seconds;
            SaveSettings();
        }

        /// <summary>
        /// Get auto refresh setting
        /// </summary>
        public bool GetAutoRefresh()
        {
            return _settings.AutoRefresh;
        }

        /// <summary>
        /// Set auto refresh and save
        /// </summary>
        public void SetAutoRefresh(bool enabled)
        {
            _settings.AutoRefresh = enabled;
            SaveSettings();
        }

        /// <summary>
        /// Get default time range
        /// </summary>
        public int GetDefaultTimeRange()
        {
            return _settings.DefaultTimeRangeMinutes;
        }

        /// <summary>
        /// Set default time range and save
        /// </summary>
        public void SetDefaultTimeRange(int minutes)
        {
            _settings.DefaultTimeRangeMinutes = minutes;
            SaveSettings();
        }

        /// <summary>
        /// Get show diagnostic pane setting
        /// </summary>
        public bool GetShowDiagnosticPane()
        {
            return _settings.ShowDiagnosticPane;
        }

        /// <summary>
        /// Set show diagnostic pane and save
        /// </summary>
        public void SetShowDiagnosticPane(bool enabled)
        {
            _settings.ShowDiagnosticPane = enabled;
            SaveSettings();
        }

        /// <summary>
        /// Get default dashboard ID
        /// </summary>
        public string GetDefaultDashboardId()
        {
            return _settings.DefaultDashboardId;
        }

        /// <summary>
        /// Set default dashboard ID and save
        /// </summary>
        public void SetDefaultDashboardId(string dashboardId)
        {
            _settings.DefaultDashboardId = dashboardId;
            SaveSettings();
        }

        /// <summary>
        /// Get data source setting
        /// </summary>
        public string GetDataSource()
        {
            return _settings.DataSource;
        }

        /// <summary>
        /// Set data source and save
        /// </summary>
        public void SetDataSource(string source)
        {
            _settings.DataSource = source;
            SaveSettings();
        }
    }
}
