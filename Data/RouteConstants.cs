/* In the name of God, the Merciful, the Compassionate */

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
    // D-0a: gated-module route consts are #if-fenced so their string literals never reach
    // the community assembly's metadata (a C# `const` is emitted into type metadata regardless
    // of use-site pruning — the fingerprint leak this fixes). In `full` builds nothing changes.
#if !SQLT_NO_PREMIUM
    public const string Consolidation = "/consolidation";    // module: premium — gate renders on BuildModules.Premium
    public const string Premium = "/premium";                // module: premium — gate renders on BuildModules.Premium
#endif
    public const string CodeHotspots = "/code-hotspots";
    public const string DiskIo = "/disk-io";
    public const string FullAudit = "/fullaudit";
#if !SQLT_NO_PREMIUM
    public const string BestPractice = "/bestpractice";              // module: premium — gate renders on BuildModules.Premium
    public const string ServerConfiguration = "/server-configuration"; // module: premium — gate renders on BuildModules.Premium
#endif
    public const string Environment = "/environment";
#if !SQLT_NO_DEVTOOLS
    public const string CheckValidator = "/check-validator"; // module: dev-tools — gate renders on BuildModules.DevTools
    public const string AccessSurface = "/access-surface";   // module: dev-tools — gate renders on BuildModules.DevTools
    public const string SodMatrix = "/sod-matrix";           // module: dev-tools — gate renders on BuildModules.DevTools
    // Offboarding permission trace: one principal, every server, everything they hold and own.
    public const string OffboardingTrace = "/offboarding-trace"; // module: dev-tools — gate renders on BuildModules.DevTools
    // #84 results-import round-trip: full/dev-build-only — the community/redacted edition is the
    // capture tool (exports results JSON), the full build imports + reports. Same fence as the
    // other dev-tools routes above.
    public const string ImportResults = "/import-results";   // module: dev-tools — gate renders on BuildModules.DevTools
#endif
#if SQLT_PERFREPORT
    // Performance Analysis Report (SPEC 2026-07-16): full-PRIVATE-build-only consulting deliverable.
    // #if-fenced so the route literal never reaches ANY non-private binary (community, paid/client,
    // or a plain full build) — one tier stricter than the dev-tools/premium fences above, which
    // still emit in the default full build. Present only when built with -p:SQLTriagePrivate=true.
    public const string PerformanceReport = "/performance-report";   // module: perf-report — gate renders on BuildModules.PerfReport
#endif

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
    public const string ChangedObjects = "/changed-objects";
    public const string Pevents = "/pevents";
    public const string Pquery = "/pquery";
    public const string BpCheck = "/bpcheck";
    public const string Instance = "/instance";

    // ── Administration ──
    public const string Settings = "/settings";
    public const string Services = "/services";
#if !SQLT_NO_DEVTOOLS
    public const string BuildProfile = "/build-profile";     // module: dev-tools — gate renders on BuildModules.DevTools
#endif
    public const string Servers = "/servers";
    public const string ServerDocs = "/server-docs";
    public const string AppMetrics = "/app-metrics";   // unified in-app memory + GC + perf metrics (was MemoryProfile + PerfInspector)
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
    // UNBUNDLED 2026-07-21 (client safety). The SQLWATCH and Darling PerformanceMonitor
    // deploy routes and their pages/services are GONE, not gated: Darling PerformanceMonitor
    // caused two client outages in two months plus a near-third (memory >2GB, high CPU, a
    // ~600GB self-inflicted database blowout). Shipping the ability to deploy it means we
    // own the outage.
    //
    // The constants are deleted rather than kept-and-unused on purpose: any re-introduced
    // link or NavigateTo now fails to COMPILE instead of silently becoming a live button.
    // SQLTriage still READS SQLWATCH/PerformanceMonitor databases if a DBA installed them
    // independently — only the install/deploy path is removed.

    // ── Tooling ──
    public const string DashboardEditor = "/dashboard-editor";
    public const string EditAuditScripts = "/editauditscripts";
    public const string Checks = "/checks";
    public static string CheckTrend(string checkId) => $"/checks/trend/{checkId}";

    // ── Remediation ──
    public const string Playbooks = "/playbooks";
    // The Server Configuration feature set (Adrian's ruling 2026-08-05): licence-bound, so the
    // three pages are Content-Removed from community alongside ServerConfiguration.razor. Same
    // D-0a fence as the other premium routes — a const's literal reaches type metadata whether or
    // not any call site survives pruning, so the symbol itself has to be gated. Gate every
    // rendered link on BuildModules.Premium (and the runtime licence on ServerConfigSuiteGate).
#if !SQLT_NO_PREMIUM
    public const string Remediation = "/remediation";           // module: premium
    public const string AgentJobGuard = "/agent-job-guard";     // module: premium
    public const string AgentJobSync = "/agent-job-sync";       // module: premium
#endif

    // ── Governance ──
    public const string CioDashboard = "/cio";
    public const string DbaDashboard = "/dba";
    public const string BlitzDashboard = "/blitz";
    public const string Governance = "/governance";
    public const string GovernanceIndicative = "/governance?IsIndicative=true";
    public const string ComplianceMap = "/compliance-map";
    public const string ControlHierarchy = "/compliance-tree";
    public const string GlobalCommand = "/global-command";

    // ── Audit ──
    public const string AuditLogViewer = "/audit-log";

    // ── Change ledger (#88 — the LOG branch of accept-or-log) ──
    public const string ChangeLedger = "/change-ledger";

    // ── Report Bundles ──
    public const string ReportBundles = "/reports";
#if !SQLT_NO_PREMIUM
    public const string AdvancedReporting = "/advanced-reporting"; // module: premium — gate renders on BuildModules.Premium
#endif

    // ── Documentation / Installation Helper (v2 scaffolds) ──
    public const string Documentation = "/documentation";
    public const string InstallationHelper = "/installation-helper";

    // ── Query Store ──
    public const string QueryStore = "/querystore";

    // ── Portal (hosted publish surface) ──
    // D-0a-style fence: the const's literal is emitted into type metadata regardless of
    // use-site pruning, so gate it behind the module symbol so it never reaches the
    // community assembly. Gate every rendered link on BuildModules.Portal.
#if !SQLT_NO_PORTAL
    public const string PortalPublish = "/portal-publish";   // module: portal — gate renders on BuildModules.Portal
    public const string PortalStatus = "/portal-status";     // module: portal — gate renders on BuildModules.Portal
    // Export Pack (E3) lives at Pages/Portal/ExportPack.razor, so it inherits the PORTAL exclusion,
    // not the premium one — even though its nav entry sits in the premium section, which is why the
    // NavMenu block that renders it carries BOTH fences.
    public const string ExportPack = "/export-pack";          // module: portal — gate renders on BuildModules.Portal
#endif
}
