/* In the name of God, the Merciful, the Compassionate */

using System;
using SqlHealthAssessment.Data;

namespace SqlHealthAssessment.Data.Services
{
    /// <summary>
    /// Abstraction over UserSettingsService — enables testing and future alternative implementations.
    /// </summary>
    public interface IUserSettingsService
    {
        UserSettingsService.UserSettings GetSettings();

        string GetSelectedTheme();
        void SetSelectedTheme(string theme);

        int GetRefreshInterval();
        void SetRefreshInterval(int seconds);

        bool GetAutoRefresh();
        void SetAutoRefresh(bool enabled);

        int GetDefaultTimeRange();
        void SetDefaultTimeRange(int minutes);

        bool GetShowDiagnosticPane();
        void SetShowDiagnosticPane(bool enabled);

        string GetDefaultDashboardId();
        void SetDefaultDashboardId(string dashboardId);

        string GetDataSource();
        void SetDataSource(string source);

        int GetZoomLevel();
        void SetZoomLevel(int zoomPercent);

        bool GetDebugLogging();
        void SetDebugLogging(bool enabled);

        bool GetAnonymiseServerNames();
        void SetAnonymiseServerNames(bool enabled);

        bool GetUseV2PlanIcons();
        void SetUseV2PlanIcons(bool enabled);

        bool GetShowVaQueries();
        void SetShowVaQueries(bool enabled);

        bool GetNoPantsMode();
        void SetNoPantsMode(bool enabled);

        bool GetNoPantsDisclaimerAccepted();
        void SetNoPantsDisclaimerAccepted(bool accepted);

        bool GetExperimentalMode();
        void SetExperimentalMode(bool enabled);

        bool GetOnboardingComplete();
        void SetOnboardingComplete(bool complete);

        string GetLastSeenVersion();
        void SetLastSeenVersion(string version);

        void UpdateAutoExportSettings(
            bool auditCsv, bool auditJson, bool auditPdf,
            bool quickCheckCsv, bool quickCheckPdf,
            bool vaCsv, bool vaPdf);

        UserSettingsService.UserSettings.VaScheduleSnapshot GetVaSchedule();
        void SaveVaSchedule(bool enabled, string type, string time, int dayOfWeek, int dayOfMonth);
        void UpdateVaScheduleLastRun(DateTime utcNow);

        UserSettingsService.UserSettings.RoadmapScheduleSnapshot GetRoadmapSchedule();
        void SaveRoadmapSchedule(bool enabled, string type, string time, int dayOfWeek, int dayOfMonth, bool splitByDomain);
        void UpdateRoadmapScheduleLastRun(DateTime utcNow);

        event Action<int>? OnZoomChanged;
        event Action<bool>? OnDebugLoggingChanged;
        event Action<bool>? OnNoPantsModeChanged;
        event Action<bool>? OnExperimentalModeChanged;
        event Action<bool>? OnShowMaturityRoadmapChanged;
    }
}
