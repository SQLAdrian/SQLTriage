namespace SQLTriage.Data;

/// <summary>
/// Centralised route constants. Use these instead of hardcoded strings
/// in Navigation.NavigateTo and NavLink href attributes.
///
/// Community-edition note: constants whose page belongs to a gated build-profile module
/// (see buildprofile.json / BuildModules) are marked below. The constants themselves stay
/// in every build — they're bare strings shared code may reference — but every RENDERED
/// link or NavigateTo to a gated route must sit behind the matching BuildModules const,
/// otherwise it's a dead link in a community build.
/// </summary>
public static class RouteConstants
{
    // ── Landing ──
    public const string Guide = "/guide";
    public const string Health = "/health";
    public const string About = "/about";
    public const string Login = "/login";

    // ── Dashboards ──
    public const string DashboardLive = "/dashboard/live";
    public const string DashboardInstance = "/dashboard/instance";
    public const string DashboardLiveWaits = "/dashboard/livewaits";
    public const string DashboardLongQueries = "/dashboard/longqueries";
    public const string DashboardQueryStore = "/dashboard/querystore";
    public const string DashboardLiveIndexes = "/dashboard/liveindexes";
    public const string DashboardSecurity = "/dashboard/security";
    public const string DashboardPmHealth = "/dashboard/pmhealth";
    public const string DashboardRepository = "/dashboard/repository";
    public const string DashboardSessions = "/dashboard/sessions";
    public static string Dashboard(string id) => $"/dashboard/{id}";

    // ── Diagnostics ──
    public const string PerformanceTrends = "/trends";
    public const string DiagnosticsRoadmap = "/diagnostics-roadmap";
    public const string VulnerabilityAssessment = "/vulnerabilityassessment";
    public const string Benchmark = "/benchmark";
    // Symbol kept as QuickCheck for code-compat; surface renamed to "Audit Assessment", route to /audit (2026-05-18).
    public const string QuickCheck = "/audit";
    public const string Capacity = "/capacity";
    public const string Consolidation = "/consolidation";    // module: premium — gate renders on BuildModules.Premium
    public const string Premium = "/premium";                // module: premium — gate renders on BuildModules.Premium
    public const string CodeHotspots = "/code-hotspots";
    public const string DiskIo = "/disk-io";
    public const string PerfInspector = "/perf-inspector";   // module: dev-tools — gate renders on BuildModules.DevTools
    public const string FullAudit = "/fullaudit";
    public const string BestPractice = "/bestpractice";
    public const string ServerConfiguration = "/server-configuration";
    public const string Environment = "/environment";
    public const string CheckValidator = "/check-validator"; // module: dev-tools — gate renders on BuildModules.DevTools

    // ── Query & Analysis ──
    public const string IndexAnalysis = "/index-analysis";
    public const string Compare = "/compare";
    public const string BlockingForensics = "/blocking-forensics";
    public const string LongQueries = "/longqueries";
    public const string Memory = "/memory";
    public const string Pmemory = "/pmemory";
    public const string PmemoryAnalysis = "/pmemory-analysis";
    public const string WaitEvents = "/waitevents";
    public const string Sessions = "/sessions";
    public const string XEvents = "/xevents";
    public const string Query = "/query";

    // ── Configuration ──
    public const string ServerConfigDiff = "/config-diff";
    public const string Pevents = "/pevents";
    public const string Pquery = "/pquery";
    public const string BpCheck = "/bpcheck";
    public const string Instance = "/instance";

    // ── Administration ──
    public const string Settings = "/settings";
    public const string Services = "/services";
    public const string BuildProfile = "/build-profile";     // module: dev-tools — gate renders on BuildModules.DevTools
    public const string Servers = "/servers";
    public const string ServerDocs = "/server-docs";
    public const string MemoryProfile = "/memory-profile";
    public const string Alerts = "/alerts";
    public const string AlertsNoc = "/alerts-noc";
    public const string AlertingConfig = "/alerting-config";
    public const string ScheduledTasks = "/scheduled-tasks";
    public const string SchedulerHealth = "/scheduler-health";
    public const string ServiceManagement = "/service-management";
    public const string DbaTools = "/dbatools";
    public const string MaintenanceRecommendations = "/maintenance-recommendations";
    public const string Onboarding = "/onboarding";

    // ── Deployment ──
    public const string DeploySqlWatch = "/deploysqlwatch";
    public const string DeployDarlingPm = "/deploydarlingpm";

    // ── Tooling ──
    public const string DashboardEditor = "/dashboard-editor";
    public const string EditAuditScripts = "/editauditscripts";
    public const string Checks = "/checks";
    public static string CheckTrend(string checkId) => $"/checks/trend/{checkId}";

    // ── Remediation ──
    public const string Playbooks = "/playbooks";
    public const string Remediation = "/remediation";

    // ── Governance ──
    public const string CioDashboard = "/cio";
    public const string BlitzDashboard = "/blitz";
    public const string Governance = "/governance";
    public const string GovernanceIndicative = "/governance?IsIndicative=true";
    public const string ComplianceMap = "/compliance-map";
    public const string GlobalCommand = "/global-command";

    // ── Audit ──
    public const string AuditLogViewer = "/audit-log";

    // ── Report Bundles ──
    public const string ReportBundles = "/reports";
    public const string AdvancedReporting = "/advanced-reporting"; // module: premium — gate renders on BuildModules.Premium

    // ── Documentation / Installation Helper (v2 scaffolds) ──
    public const string Documentation = "/documentation";
    public const string InstallationHelper = "/installation-helper";

    // ── Query Store ──
    public const string QueryStore = "/querystore";
}
