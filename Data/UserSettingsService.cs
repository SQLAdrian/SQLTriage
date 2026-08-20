/* In the name of God, the Merciful, the Compassionate */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SQLTriage.Data
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

        // ── Audit concurrency bounds (single source of truth) ──
        // Consumed by CheckExecutionService (the throttle), Settings.razor (the persisted
        // default) and QuickCheck.razor (the per-run override), so all three agree on the
        // range and on what "unchanged behaviour" means.
        // The ceiling is 16 by Adrian's ruling (2026-07-19). Config/appsettings.json's
        // ConnectionPool.MaxPerServer was raised 13 -> 16 in the same change so the desktop
        // lane's connection pool cannot cap a value the UI offers.
        /// <summary>Lowest audit concurrency the UI accepts. 1 = fully serialized queries.</summary>
        public const int AuditConcurrencyMin = 1;
        /// <summary>Highest audit concurrency the UI accepts.</summary>
        public const int AuditConcurrencyMax = 16;
        /// <summary>Audit concurrency used when nothing is stored — the pre-setting constant.</summary>
        public const int AuditConcurrencyDefault = 7;

        /// <summary>
        /// The environment variable a HOST is pointed elsewhere with. Absent or blank => the settings
        /// file is <c>%APPDATA%\SQLTriage\user-settings.json</c>, byte for byte the path this service
        /// has always bound; set => the directory named holds this host's <c>user-settings.json</c>.
        ///
        /// <para><b>Why it exists (2026-08-10).</b> Both DI registrations
        /// (<c>ServiceCollectionExtensions.AddSharedServices</c> and
        /// <c>WindowsServiceHost.RegisterAllServices</c>) use the parameterless constructor, and the path
        /// seam beside it is <c>internal</c>. A RUNNING host could therefore only ever store licence
        /// activation in the live per-user install, so no agent could ever activate a bundle in a
        /// throwaway build output and RENDER a licensed page — four gates in a row recorded that gap.
        /// The variable closes it without moving production one byte.</para>
        ///
        /// <para><b>This is not %APPDATA% redirection.</b> <see cref="RealUserProfileGuard"/> is right
        /// that redirecting <c>%APPDATA%</c> does nothing, because
        /// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> goes through the shell
        /// API. This variable is read by this code and composed into a path, so it is the same kind of
        /// seam as the constructor parameter, reachable from a host that cannot pass one.</para>
        ///
        /// <para><b>Fail-safe.</b> Every failure mode lands on the production default: unset, blank,
        /// whitespace, a value that will not compose into a path, and a value that composes but names
        /// a location this host cannot hold a settings file in. There is no state in which this
        /// variable causes settings to be read from somewhere unintended and none in which it can
        /// silently lose them.</para>
        ///
        /// <para><b>Composability is not usability</b> (MEASURED 2026-08-11, and the reason
        /// <see cref="CanHoldSettingsFile"/> exists). <see cref="Path.GetFullPath(string)"/> ACCEPTS
        /// <c>|||&lt;&gt;:invalid</c> and a value naming an existing FILE, so a composability check
        /// passed both and the failure landed one line later, in
        /// <see cref="Directory.CreateDirectory(string)"/>, as an unhandled <see cref="IOException"/>
        /// at the composition root: a plain console host resolving this service through
        /// <c>AddSharedServices</c> did not start. Resolution therefore PREPARES the location it is
        /// about to return, because attempting it is the only honest test of it.</para>
        /// </summary>
        internal const string SettingsDirectoryVariable = "SQLTRIAGE_SETTINGS_DIR";

        /// <summary>
        /// The settings file a host with no explicit path binds: <see cref="SettingsDirectoryVariable"/>'s
        /// directory when it names one this host can actually use, otherwise
        /// <see cref="DefaultSettingsFilePath"/>.
        ///
        /// <para>This is the one place resolution touches the disk, deliberately — see
        /// <see cref="CanHoldSettingsFile"/>. It touches ONLY a directory the variable itself named,
        /// and never the real profile: a variable pointed back at <see cref="DefaultSettingsFilePath"/>
        /// short-circuits to the default before any probe, so the guard in the constructor still fires
        /// on an untouched directory exactly as it did before.</para>
        /// </summary>
        internal static string ResolveSettingsFilePath()
        {
            string? configured = ConfiguredSettingsFilePath();
            if (configured is null) return DefaultSettingsFilePath();

            string @default = DefaultSettingsFilePath();
            if (string.Equals(configured, @default, StringComparison.OrdinalIgnoreCase))
                return @default;

            return CanHoldSettingsFile(configured) ? configured : @default;
        }

        /// <summary>
        /// Can this host actually keep <paramref name="settingsFilePath"/> — i.e. does its directory
        /// exist, or can it be made? Attempting the directory IS the check, because no string test
        /// separates the classes that fail: <c>|||&lt;&gt;:invalid</c> and a value naming an existing
        /// file both compose cleanly and both throw on creation (measured 2026-08-11).
        ///
        /// <para><b>What it does NOT prove:</b> that a WRITE will succeed. A directory that exists but
        /// refuses a write still fails at save time, exactly as it did before this seam — that is
        /// <c>SaveSettings</c>'s business, unchanged. This answers the narrower question the fail-safe
        /// claim rests on: can the host START here.</para>
        /// </summary>
        private static bool CanHoldSettingsFile(string settingsFilePath)
        {
            try
            {
                string? directory = Path.GetDirectoryName(settingsFilePath);
                if (string.IsNullOrEmpty(directory)) return false;
                Directory.CreateDirectory(directory);

                // A directory sitting where the settings FILE belongs would fail every read and write.
                return !Directory.Exists(settingsFilePath);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The real per-user profile path: <c>%APPDATA%\SQLTriage\user-settings.json</c>.</summary>
        internal static string DefaultSettingsFilePath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SQLTriage",
            "user-settings.json");

        /// <summary>
        /// The contained settings file <see cref="SettingsDirectoryVariable"/> COMPOSES to, or null
        /// when the variable is unset, blank, or will not compose at all. Nothing here throws: a host
        /// that cannot read the variable must still start, on the default path. Whether the composed
        /// path is one this host can USE is a separate question, answered by
        /// <see cref="CanHoldSettingsFile"/> — the two are not the same, and treating them as one is
        /// what let a malformed value crash a host at the composition root.
        /// </summary>
        private static string? ConfiguredSettingsFilePath()
        {
            string? directory;
            try
            {
                directory = Environment.GetEnvironmentVariable(SettingsDirectoryVariable);
            }
            catch (Exception)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(directory)) return null;

            try
            {
                return Path.Combine(Path.GetFullPath(directory.Trim()), "user-settings.json");
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Production constructor. Binds <c>%APPDATA%\SQLTriage\user-settings.json</c> unless
        /// <see cref="SettingsDirectoryVariable"/> points this host at a contained location. With the
        /// variable absent the bound path is byte-identical to what it has always been, guard included.
        /// </summary>
        public UserSettingsService() : this(null) { }

        /// <summary>
        /// Test seam (InternalsVisibleTo SQLTriage.Tests): lets a test point this service at its own
        /// temp file instead of the operator's real profile.
        ///
        /// <para><b>Why this exists.</b> Measured 2026-08-04: fourteen call sites across seven test
        /// files used the parameterless constructor, so the suite bound the developer's real
        /// <c>%APPDATA%\SQLTriage\user-settings.json</c>. Two of those files called
        /// <c>ClearLicense()</c> and <c>TryActivate(...)</c>, which WRITE — a test-fixture licence
        /// named <c>TEST_CLIENT_NEVER_PROD</c> was written into the real file and later cleared
        /// again, and the file was observed oscillating between two states for a whole session.
        /// Nobody noticed for weeks. <c>%APPDATA%</c> redirection does not sandbox .NET —
        /// <see cref="Environment.SpecialFolder.ApplicationData"/> resolves through the shell API —
        /// so REDIRECTING that variable cannot fix this, and a path this code composes is the only
        /// seam that works. (<see cref="SettingsDirectoryVariable"/>, added 2026-08-10 for hosts that
        /// cannot pass a parameter, is the same kind of seam: a value this code reads and composes,
        /// not a redirection the shell API resolves around.)</para>
        ///
        /// <para>Pass null for the production path. Mirrors
        /// <c>AdminAuthService</c>'s <c>configPathOverride</c> and <c>RbacService</c>'s explicit-path
        /// constructor.</para>
        /// </summary>
        /// <param name="settingsFilePathOverride">
        /// Absolute path to the settings JSON this instance should own, or null for
        /// <c>%APPDATA%\SQLTriage\user-settings.json</c>.
        /// </param>
        internal UserSettingsService(string? settingsFilePathOverride)
        {
            // The resolution happens FIRST and the guard is then applied to what was resolved, rather
            // than to the default path on the way past. A host pointed elsewhere by
            // SettingsDirectoryVariable must not trip the guard; a host pointed BACK at the real
            // profile by that same variable must still trip it, and comparing the resolved path is
            // what makes both true without a second rule.
            _settingsFilePath = settingsFilePathOverride ?? ResolveSettingsFilePath();

            if (settingsFilePathOverride is null
                && string.Equals(_settingsFilePath, DefaultSettingsFilePath(), StringComparison.OrdinalIgnoreCase))
            {
                // Runtime chokepoint — see RealUserProfileGuard. Throws only under a test host, and
                // only on this default path; an explicit path is never second-guessed.
                RealUserProfileGuard.RefuseRealProfileUnderTest(
                    nameof(UserSettingsService), _settingsFilePath,
                    "new UserSettingsService(Path.Combine(myTempDir, \"user-settings.json\"))");
            }

            var dir = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            _settings = LoadSettings();
        }

        /// <summary>
        /// The settings file this instance actually owns.
        ///
        /// <para>Exists so that <c>UserSettingsLicenseExtensions</c> — which rewrites the License
        /// section of the same file through its own <see cref="System.Text.Json.Nodes.JsonNode"/>
        /// writer — asks THIS object where its file is rather than re-deriving the path. Before
        /// 2026-08-04 that helper took a <see cref="UserSettingsService"/> and discarded it
        /// (the parameter was literally named <c>_</c>), recomputing <c>%APPDATA%</c> itself. A
        /// seam on this class alone would therefore have left every licence write still landing on
        /// the operator's real profile — green tests, moving file.</para>
        /// </summary>
        internal string SettingsFilePath => _settingsFilePath;

        /// <summary>
        /// User settings that persist across sessions
        /// </summary>
        public class UserSettings
        {
            public int RefreshIntervalSeconds { get; set; } = 15;
            public bool AutoRefresh { get; set; } = true;
            public int DefaultTimeRangeMinutes { get; set; } = 60;
            public bool ShowDiagnosticPane { get; set; } = false;
            public string DefaultDashboardId { get; set; } = "";
            /// <summary>Which view the home route ("/") renders: "hero" (marketing landing),
            /// "guide" (the Guide page), or "dashboard" (Repository Dashboard). Default "hero".</summary>
            public string LandingView { get; set; } = "hero";
            /// <summary>Data source: "sqlwatch" or "pm" (PerformanceMonitor)</summary>
            public string DataSource { get; set; } = "master";
            /// <summary>Radzen Blazor UI theme name (e.g. "dark", "material3", "fluent-dark")</summary>
            public string RadzenUiTheme { get; set; } = "dark";
            /// <summary>WebView2 zoom level as a percentage (e.g. 100, 125, 150). Default 150.</summary>
            public int ZoomLevel { get; set; } = 150;

            // ── UX / Appearance ──
            /// <summary>Enable UI animations (e.g. terminal typing effect). Off = blazing fast.</summary>
            public bool EnableAnimations { get; set; } = false;
            /// <summary>When true, pages yield before heavy initialisation so the UI renders a
            /// skeleton screen instantly. Feels snappier but keeps more concurrent state in
            /// memory. Turn off on memory-constrained servers (&lt;500 MB budget).</summary>
            public bool FastAppLoad { get; set; } = false;

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

            // ── Accessibility ──
            /// <summary>When true, status indicators use a colorblind-safe palette (blue/yellow/orange) instead of red/green.</summary>
            public bool ColorBlindMode { get; set; } = false;
            public bool NarrationMode { get; set; } = false;

            // ── Report identity ──
            /// <summary>Company name shown on exported reports. Blank = none.</summary>
            public string ReportCompanyName { get; set; } = "";
            /// <summary>Operator / author name on reports. Blank = the local Windows username (without the computer name).</summary>
            public string ReportOperatorName { get; set; } = "";
            /// <summary>When true, exported reports carry a DRAFT watermark for non-production servers. Default OFF.</summary>
            public bool ReportDraftWatermark { get; set; } = false;

            // ── SoD preferred-state baseline (paid-only SoD Permissions Matrix, slice 2) ──
            /// <summary>Absolute path to the per-client preferred-state SoD baseline JSON the SoD
            /// Permissions Matrix diffs live collections against. Blank = no baseline configured (the
            /// page/PDF then render an honest "not configured" state, never a clean diff). Only ever set
            /// from the gated SoD page; unused in the community build (that page is compiled out).</summary>
            public string SodPreferredBaselinePath { get; set; } = "";

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

            // ── Performance Inspector ──
            /// <summary>When true, enables performance tracing for dashboard loads.</summary>
            public bool EnablePerfInspector { get; set; } = true;

            // ── Query Concurrency ──
            /// <summary>Maximum concurrent heavy queries (TimeSeries panels). Default 5. Range 1-20.</summary>
            public int MaxHeavyConcurrent { get; set; } = 5;
            /// <summary>Maximum concurrent light queries (StatCard, BarGauge, CheckStatus, DataGrid). Default 10. Range 2-50.</summary>
            public int MaxLightConcurrent { get; set; } = 10;
            /// <summary>Maximum concurrent queries per individual server. Default 3. Range 1-10.</summary>
            public int MaxConcurrentPerServer { get; set; } = 3;
            /// <summary>
            /// Concurrent queries per instance during an AUDIT run (CheckExecutionService's
            /// per-instance throttle). Range 1-16, default 7 — 7 is the value that shipped as a
            /// hard-coded constant before this setting existed, so an upgraded install with no
            /// stored value behaves exactly as it did before. Distinct from
            /// <see cref="MaxConcurrentPerServer"/>, which governs the live dashboard orchestrator.
            /// </summary>
            public int AuditMaxConcurrentPerInstance { get; set; } = AuditConcurrencyDefault;
            /// <summary>When true, temporarily raises concurrency limits during burst periods.</summary>
            public bool EnableBurstMode { get; set; } = false;
            /// <summary>Multiplier applied to concurrency limits during burst mode. Default 2.0. Range 1.0-5.0.</summary>
            public double BurstConcurrencyMultiplier { get; set; } = 2.0;
            /// <summary>Duration of burst mode in seconds. Default 60. Range 10-300.</summary>
            public int BurstDurationSec { get; set; } = 60;

            // ── Cache ──
            /// <summary>Maximum data points returned per chart series from the SQLite cache. Lower = less memory, faster render.</summary>
            public int ChartDataPointCap { get; set; } = 2000;

            // ── Welcome tour ──
            /// <summary>True once the user has either taken or dismissed the first-launch
            /// welcome tour. Suppresses the CTA toast on subsequent launches. Users can
            /// re-trigger the tour from Settings.</summary>
            public bool HasSeenWelcomeTour { get; set; } = false;

            // ── Remediation Cost Estimate ──
            /// <summary>When true, CIO Dashboard displays the estimated remediation cost block. Off hides the costing entirely.</summary>
            public bool ShowRemediationCost { get; set; } = true;
            /// <summary>Hourly rate (USD) used to compute the raw technical labour line.</summary>
            public double RemediationHourlyRate { get; set; } = 295.0;
            /// <summary>Compliance tier driving the project-cost multiplier. One of: "None", "SOC2", "ISO27001", "PCI", "SOX", "HIPAA".</summary>
            public string ComplianceTier { get; set; } = "None";
            /// <summary>Optional consultancy brand displayed in the dashboard conversion footer. Blank hides the footer entirely.</summary>
            public string ConsultancyName { get; set; } = "";
            /// <summary>Typical engagement duration string (e.g. "1-2 weeks"). Shown alongside ConsultancyName. Hidden if blank.</summary>
            public string EngagementDuration { get; set; } = "";
            /// <summary>Monthly OPEX per server (NZD) — running costs + ops management + support services. Drives 3-year TCO calculation.</summary>
            public double MonthlyOpexPerServerNZD { get; set; } = 400.0;

            // ── Threshold-Based Highlighting (triage aid; not monitoring) ──
            /// <summary>When true, rows/bubbles exceeding any configured threshold are highlighted. Off by default — opt-in.</summary>
            public bool ThresholdsEnabled { get; set; } = false;
            /// <summary>CPU time threshold in milliseconds. Sessions with CpuTime &gt;= this value are highlighted. 0 = disabled.</summary>
            public int ThresholdCpuMs { get; set; } = 5000;
            /// <summary>Wait time threshold in milliseconds. Sessions with WaitTime &gt;= this value are highlighted. 0 = disabled.</summary>
            public int ThresholdWaitTimeMs { get; set; } = 1000;
            /// <summary>Memory threshold in MB. Sessions with MemoryUsageKB / 1024 &gt;= this value are highlighted. 0 = disabled.</summary>
            public int ThresholdMemoryMb { get; set; } = 0;
            /// <summary>Logical reads threshold in KB. Sessions with LogicalReads &gt;= this value are highlighted. 0 = disabled.</summary>
            public int ThresholdReadsKb { get; set; } = 0;
            /// <summary>Writes threshold in KB. Sessions with Writes &gt;= this value are highlighted. 0 = disabled.</summary>
            public int ThresholdWritesKb { get; set; } = 0;
            /// <summary>Total elapsed time threshold in milliseconds. Sessions with TotalElapsedTime &gt;= this value are highlighted. 0 = disabled.</summary>
            public int ThresholdDurationMs { get; set; } = 5000;

            // ── Updates ──
            /// <summary>Optional HTTP/HTTPS proxy URL for update checks. Null = use system proxy.</summary>
            public string? UpdateProxyUrl { get; set; }

            // ── Alert Baseline ──
            /// <summary>When true, alert evaluation collects baseline samples and applies IQR-based dynamic thresholds.</summary>
            public bool AlertBaselineEnabled { get; set; } = true;
            /// <summary>When true, baseline thresholds are computed per-server rather than globally across all servers.</summary>
            public bool AlertBaselinePerServer { get; set; } = true;

            // ── Notifications ──
            /// <summary>When false, toast notifications are suppressed (the nav notifications switch is off).</summary>
            public bool NotificationsEnabled { get; set; } = true;

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

            /// <summary>
            /// Top-level keys in user-settings.json that this class does not model — today just
            /// "License", which <c>UserSettingsLicenseExtensions</c> writes directly. Serializing
            /// the POCO used to drop them, so saving any setting deactivated an installed licence.
            /// Refreshed from disk on every save (see <see cref="ReloadForeignSections"/>) because
            /// that writer bypasses this service.
            /// </summary>
            [JsonExtensionData]
            public Dictionary<string, JsonElement> ForeignSections { get; set; } = new();
        }

        /// <summary>
        /// JSON names of every property this POCO owns. Anything else found in the file is
        /// foreign data to be preserved verbatim.
        /// </summary>
        private static readonly HashSet<string> KnownPropertyNames =
            typeof(UserSettings).GetProperties()
                .Where(p => p.Name != nameof(UserSettings.ForeignSections))
                .Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal);

        /// <summary>
        /// How user-settings.json came off disk. See <see cref="SaveSettings"/> for why this file is
        /// guarded at the grade of a credential store rather than of a preferences file.
        /// </summary>
        private ConfigLoadOutcome _load = ConfigLoadOutcome.Missing;

        private string? _quarantine;

        /// <summary>
        /// True when user-settings.json exists and did not load — so every setting this service
        /// reports is a built-in default rather than the user's, and saves are refused.
        /// </summary>
        public bool IsStoreDamaged { get { lock (_lock) return _load is ConfigLoadOutcome.Unreadable or ConfigLoadOutcome.Empty; } }

        /// <summary>What to do about it. The ONE register — never a locally written sentence.</summary>
        public string DescribeStoreRecovery()
        {
            lock (_lock) return ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine);
        }

        private UserSettings LoadSettings()
            => ConfigFileHelper.Load<UserSettings>(_settingsFilePath, null, out _load, out _quarantine);

        /// <summary>
        /// Persists the settings object. <b>REFUSED when user-settings.json exists and did not
        /// load</b>, and the reason is not the settings.
        ///
        /// <para>This file is not only preferences. It carries the top-level <c>License</c> section —
        /// the client name and the DPAPI-wrapped licence key — written by a different writer
        /// (<c>UserSettingsLicenseExtensions</c>) and carried through here as
        /// <see cref="UserSettings.ForeignSections"/>. That carry-through is exactly what breaks
        /// under damage: <see cref="ReloadForeignSections"/> re-reads the raw file to recover
        /// unmodelled keys, its parse throws on a damaged one, and its documented fallback is to
        /// "keep whatever we already hold" — which, when the load ALSO failed, is an empty set. So
        /// the save writes a file with no License section at all. Setting the theme deactivates the
        /// install, and overwrites the file that still held the key.</para>
        ///
        /// <para>That is the same defect this method was already fixed for once: serialising the
        /// POCO over the whole file used to delete the License section outright. The fallback added
        /// then is right in its own terms — "losing a licence to a transient read failure would be
        /// worse than a stale carry-over" — it just cannot see the case where there is no carry-over
        /// to be stale. Refusing the write is that same judgement applied one state further out.</para>
        ///
        /// <para>Deliberately still <c>void</c>: 69 setters in this class call it, none of them
        /// reports a result to anything today, and giving them all a return value would be a
        /// mechanical change across every settings surface for no reader. The refusal is loud in the
        /// log; <see cref="IsStoreDamaged"/> is there for a surface that wants to say it.</para>
        /// </summary>
        public void SaveSettings()
        {
            lock (_lock)
            {
                if (ConfigFileHelper.WouldOverwriteUnreadStore(_load, StoreWriteIntent.FromLoadedStore))
                {
                    Serilog.Log.Warning(
                        "[UserSettings] Refused to write {Path}: it exists and did not load ({Outcome}), so the "
                        + "settings in memory are built-in defaults and the file's unmodelled sections — the "
                        + "License, holding this install's client name and wrapped licence key — could not be read "
                        + "back either. Writing would deactivate the install. The file is unchanged. {Recovery}",
                        _settingsFilePath, _load,
                        ConfigFileHelper.DescribeStoreRecovery(_load, _quarantine));
                    return;
                }

                try
                {
                    ReloadForeignSections(_settings);
                    ConfigFileHelper.Save(_settingsFilePath, _settings);

                    // The file is now this object, written whole — so an install whose settings file
                    // was repaired on disk and re-read starts saving again. Not set when the write
                    // throws: the damage stands and must keep refusing.
                    _load = ConfigLoadOutcome.Loaded;
                    _quarantine = null;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[UserSettings] Save failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Replaces <see cref="UserSettings.ForeignSections"/> with whatever unmodelled top-level
        /// keys are on disk right now. Replace rather than merge, so a section another writer has
        /// removed (e.g. ClearLicense) is not resurrected by the next save. Leaves the existing
        /// set untouched if the file cannot be read — losing a licence to a transient read failure
        /// would be worse than a stale carry-over.
        /// </summary>
        private void ReloadForeignSections(UserSettings target)
        {
            try
            {
                if (!File.Exists(_settingsFilePath))
                {
                    target.ForeignSections.Clear();
                    return;
                }

                var json = File.ReadAllText(_settingsFilePath);
                if (string.IsNullOrWhiteSpace(json)) { target.ForeignSections.Clear(); return; }

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

                var onDisk = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (KnownPropertyNames.Contains(prop.Name)) continue;
                    onDisk[prop.Name] = prop.Value.Clone();
                }
                target.ForeignSections = onDisk;
            }
            catch (Exception ex)
            {
                // Keep whatever we already hold; do not blank the file's foreign sections.
                System.Diagnostics.Debug.WriteLine($"[UserSettings] Could not re-read foreign sections: {ex.Message}");
            }
        }

        /// <summary>
        /// Resets every modelled setting to its factory default and saves. The License section is
        /// not a modelled setting and is carried through by the save — resetting preferences does
        /// not deactivate the install. Use ClearLicense to remove a licence.
        /// </summary>
        public void ResetToDefaults()
        {
            lock (_lock)
            {
                _settings = new UserSettings();
                SaveSettings();
            }
            // Fire mode-change events so NavMenu toggles update immediately
            OnNoPantsModeChanged?.Invoke(false);
            OnExperimentalModeChanged?.Invoke(false);
        }

        public string GetRadzenUiTheme() { lock (_lock) return _settings.RadzenUiTheme; }

        public void SetRadzenUiTheme(string theme)
        {
            lock (_lock) _settings.RadzenUiTheme = theme;
            SaveSettings();
        }

        // ── UX / Appearance ──
        public bool GetEnableAnimations() { lock (_lock) return _settings.EnableAnimations; }
        public void SetEnableAnimations(bool enabled)
        {
            lock (_lock) _settings.EnableAnimations = enabled;
            SaveSettings();
        }

        // ── Fast App Load ──
        public bool GetFastAppLoad() { lock (_lock) return _settings.FastAppLoad; }
        public void SetFastAppLoad(bool enabled)
        {
            lock (_lock) _settings.FastAppLoad = enabled;
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

        public string GetLandingView() { lock (_lock) return string.IsNullOrWhiteSpace(_settings.LandingView) ? "hero" : _settings.LandingView; }

        public void SetLandingView(string view)
        {
            lock (_lock) _settings.LandingView = view;
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

            // When the user disables anonymisation, drop the existing alias
            // map so previously-anonymised log entries don't keep their old
            // aliases on subsequent runs. Symmetric reset on enable too —
            // forces a fresh allocation of aliases rather than reusing
            // stale ones from a prior session.
            LogAnon.Reset();
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
        public bool GetNoPantsMode() { lock (_lock) return BuildModules.Community ? false : _settings.NoPantsMode; }

        public void SetNoPantsMode(bool enabled)
        {
            if (BuildModules.Community) return;
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

        // ── Color-Blind Mode ──
        public bool GetColorBlindMode() { lock (_lock) return _settings.ColorBlindMode; }
        public void SetColorBlindMode(bool enabled)
        {
            lock (_lock) _settings.ColorBlindMode = enabled;
            SaveSettings();
            OnColorBlindModeChanged?.Invoke(enabled);
        }
        public event Action<bool>? OnColorBlindModeChanged;

        // ── Narration Mode (progressive disclosure: opinionated commentary on demand) ──
        public bool GetNarrationMode() { lock (_lock) return _settings.NarrationMode; }
        public void SetNarrationMode(bool enabled)
        {
            lock (_lock) _settings.NarrationMode = enabled;
            SaveSettings();
            OnNarrationModeChanged?.Invoke(enabled);
        }
        public event Action<bool>? OnNarrationModeChanged;

        // ── Report identity ──
        public string GetReportCompanyName()  { lock (_lock) return _settings.ReportCompanyName ?? ""; }
        public void   SetReportCompanyName(string v) { lock (_lock) _settings.ReportCompanyName = v ?? ""; SaveSettings(); }
        public string GetReportOperatorName() { lock (_lock) return _settings.ReportOperatorName ?? ""; }
        public void   SetReportOperatorName(string v) { lock (_lock) _settings.ReportOperatorName = v ?? ""; SaveSettings(); }
        public bool   GetReportDraftWatermark()  { lock (_lock) return _settings.ReportDraftWatermark; }
        public void   SetReportDraftWatermark(bool v) { lock (_lock) _settings.ReportDraftWatermark = v; SaveSettings(); }

        // ── SoD preferred-state baseline path (paid-only; community returns blank — the SoD page that
        //    sets it is compiled out there, mirroring the NoPantsMode community guard above). ──
        public string GetSodPreferredBaselinePath() { lock (_lock) return BuildModules.Community ? "" : (_settings.SodPreferredBaselinePath ?? ""); }
        public void   SetSodPreferredBaselinePath(string v) { lock (_lock) _settings.SodPreferredBaselinePath = v ?? ""; SaveSettings(); }

        // ── Alert Baseline ──
        public bool GetAlertBaselineEnabled() { lock (_lock) return _settings.AlertBaselineEnabled; }
        public void SetAlertBaselineEnabled(bool enabled)
        {
            lock (_lock) _settings.AlertBaselineEnabled = enabled;
            SaveSettings();
            OnAlertBaselineEnabledChanged?.Invoke(enabled);
        }
        public event Action<bool>? OnAlertBaselineEnabledChanged;

        public bool GetAlertBaselinePerServer() { lock (_lock) return _settings.AlertBaselinePerServer; }
        public void SetAlertBaselinePerServer(bool enabled)
        {
            lock (_lock) _settings.AlertBaselinePerServer = enabled;
            SaveSettings();
        }

        // ── Experimental Mode ──
        public bool GetExperimentalMode() { lock (_lock) return BuildModules.Community ? false : _settings.ExperimentalMode; }

        public void SetExperimentalMode(bool enabled)
        {
            if (BuildModules.Community) return;
            lock (_lock) _settings.ExperimentalMode = enabled;
            SaveSettings();
            OnExperimentalModeChanged?.Invoke(enabled);
        }

        /// <summary>Fired when experimental mode is toggled.</summary>
        public event Action<bool>? OnExperimentalModeChanged;

        // ── Notifications ──
        public bool GetNotificationsEnabled() { lock (_lock) return _settings.NotificationsEnabled; }

        public void SetNotificationsEnabled(bool enabled)
        {
            lock (_lock) _settings.NotificationsEnabled = enabled;
            SaveSettings();
            OnNotificationsEnabledChanged?.Invoke(enabled);
        }

        /// <summary>Fired when the notifications switch is toggled.</summary>
        public event Action<bool>? OnNotificationsEnabledChanged;

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

        // ── Performance Inspector ──
        public bool GetEnablePerfInspector() { lock (_lock) return _settings.EnablePerfInspector; }
        public void SetEnablePerfInspector(bool enabled)
        {
            lock (_lock) _settings.EnablePerfInspector = enabled;
            SaveSettings();
        }

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

        // The getters below clamp as well as their setters. A hand-edited or older
        // user-settings.json otherwise feeds these values straight into SemaphoreSlim and
        // Take() calls, where 0 or a negative throws at DI/construction time.
        public int GetChartDataPointCap() { lock (_lock) return Math.Clamp(_settings.ChartDataPointCap, 500, 10000); }
        public void SetChartDataPointCap(int cap) { lock (_lock) _settings.ChartDataPointCap = Math.Clamp(cap, 500, 10000); SaveSettings(); }

        public bool GetShowRemediationCost() { lock (_lock) return _settings.ShowRemediationCost; }
        public void SetShowRemediationCost(bool enabled) { lock (_lock) _settings.ShowRemediationCost = enabled; SaveSettings(); }

        public bool GetHasSeenWelcomeTour() { lock (_lock) return _settings.HasSeenWelcomeTour; }
        public void SetHasSeenWelcomeTour(bool seen) { lock (_lock) _settings.HasSeenWelcomeTour = seen; SaveSettings(); }

        public double GetRemediationHourlyRate() { lock (_lock) return _settings.RemediationHourlyRate; }
        public void SetRemediationHourlyRate(double rate) { lock (_lock) _settings.RemediationHourlyRate = Math.Clamp(rate, 50.0, 1000.0); SaveSettings(); }

        public string GetComplianceTier() { lock (_lock) return _settings.ComplianceTier; }
        public void SetComplianceTier(string tier) { lock (_lock) _settings.ComplianceTier = string.IsNullOrWhiteSpace(tier) ? "None" : tier; SaveSettings(); }

        public string GetConsultancyName() { lock (_lock) return _settings.ConsultancyName; }
        public void SetConsultancyName(string name) { lock (_lock) _settings.ConsultancyName = name ?? ""; SaveSettings(); }

        public string GetEngagementDuration() { lock (_lock) return _settings.EngagementDuration; }
        public void SetEngagementDuration(string duration) { lock (_lock) _settings.EngagementDuration = duration ?? ""; SaveSettings(); }

        public double GetMonthlyOpexPerServerNZD() { lock (_lock) return _settings.MonthlyOpexPerServerNZD; }
        public void SetMonthlyOpexPerServerNZD(double opex) { lock (_lock) _settings.MonthlyOpexPerServerNZD = Math.Clamp(opex, 0, 10000); SaveSettings(); }

        public int GetMaxHeavyConcurrent() { lock (_lock) return Math.Clamp(_settings.MaxHeavyConcurrent, 1, 30); }
        public void SetMaxHeavyConcurrent(int limit) { lock (_lock) _settings.MaxHeavyConcurrent = Math.Clamp(limit, 1, 30); SaveSettings(); }

        public int GetMaxLightConcurrent() { lock (_lock) return Math.Clamp(_settings.MaxLightConcurrent, 2, 50); }
        public void SetMaxLightConcurrent(int limit) { lock (_lock) _settings.MaxLightConcurrent = Math.Clamp(limit, 2, 50); SaveSettings(); }

        public int GetMaxConcurrentPerServer() { lock (_lock) return Math.Clamp(_settings.MaxConcurrentPerServer, 1, 10); }
        public void SetMaxConcurrentPerServer(int limit) { lock (_lock) _settings.MaxConcurrentPerServer = Math.Clamp(limit, 1, 10); SaveSettings(); }

        /// <summary>
        /// Audit concurrency (queries per instance). The getter clamps as well as the setter so a
        /// hand-edited or older user-settings.json cannot push the throttle outside the supported
        /// range.
        /// </summary>
        public int GetAuditMaxConcurrentPerInstance()
        {
            lock (_lock) return Math.Clamp(_settings.AuditMaxConcurrentPerInstance, AuditConcurrencyMin, AuditConcurrencyMax);
        }
        public void SetAuditMaxConcurrentPerInstance(int limit)
        {
            lock (_lock) _settings.AuditMaxConcurrentPerInstance = Math.Clamp(limit, AuditConcurrencyMin, AuditConcurrencyMax);
            SaveSettings();
        }

        public bool GetEnableBurstMode() { lock (_lock) return _settings.EnableBurstMode; }
        public void SetEnableBurstMode(bool enabled) { lock (_lock) _settings.EnableBurstMode = enabled; SaveSettings(); }

        public double GetBurstConcurrencyMultiplier() { lock (_lock) return Math.Clamp(_settings.BurstConcurrencyMultiplier, 1.0, 5.0); }
        public void SetBurstConcurrencyMultiplier(double mult) { lock (_lock) _settings.BurstConcurrencyMultiplier = Math.Clamp(mult, 1.0, 5.0); SaveSettings(); }

        public int GetBurstDurationSec() { lock (_lock) return Math.Clamp(_settings.BurstDurationSec, 10, 300); }
        public void SetBurstDurationSec(int sec) { lock (_lock) _settings.BurstDurationSec = Math.Clamp(sec, 10, 300); SaveSettings(); }

        public string? GetUpdateProxyUrl() { lock (_lock) return _settings.UpdateProxyUrl; }
        public void SetUpdateProxyUrl(string? url) { lock (_lock) _settings.UpdateProxyUrl = string.IsNullOrWhiteSpace(url) ? null : url.Trim(); SaveSettings(); }

        // ── Threshold-Based Highlighting ──
        public bool GetThresholdsEnabled() { lock (_lock) return _settings.ThresholdsEnabled; }
        public void SetThresholdsEnabled(bool enabled) { lock (_lock) _settings.ThresholdsEnabled = enabled; SaveSettings(); }

        public int GetThresholdCpuMs() { lock (_lock) return _settings.ThresholdCpuMs; }
        public void SetThresholdCpuMs(int ms) { lock (_lock) _settings.ThresholdCpuMs = Math.Max(0, ms); SaveSettings(); }

        public int GetThresholdWaitTimeMs() { lock (_lock) return _settings.ThresholdWaitTimeMs; }
        public void SetThresholdWaitTimeMs(int ms) { lock (_lock) _settings.ThresholdWaitTimeMs = Math.Max(0, ms); SaveSettings(); }

        public int GetThresholdMemoryMb() { lock (_lock) return _settings.ThresholdMemoryMb; }
        public void SetThresholdMemoryMb(int mb) { lock (_lock) _settings.ThresholdMemoryMb = Math.Max(0, mb); SaveSettings(); }

        public int GetThresholdReadsKb() { lock (_lock) return _settings.ThresholdReadsKb; }
        public void SetThresholdReadsKb(int kb) { lock (_lock) _settings.ThresholdReadsKb = Math.Max(0, kb); SaveSettings(); }

        public int GetThresholdWritesKb() { lock (_lock) return _settings.ThresholdWritesKb; }
        public void SetThresholdWritesKb(int kb) { lock (_lock) _settings.ThresholdWritesKb = Math.Max(0, kb); SaveSettings(); }

        public int GetThresholdDurationMs() { lock (_lock) return _settings.ThresholdDurationMs; }
        public void SetThresholdDurationMs(int ms) { lock (_lock) _settings.ThresholdDurationMs = Math.Max(0, ms); SaveSettings(); }

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
