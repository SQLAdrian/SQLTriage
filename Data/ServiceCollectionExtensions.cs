/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;
using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;
using SQLTriage.Data.Scheduling;
using SQLTriage.Data.Services;
using SQLTriage.Data.Services.Licensing;
#if !SQLT_NO_PORTAL
using SQLTriage.Data.Services.Portal;
#endif

namespace SQLTriage.Data;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all services shared between WPF Desktop (App.xaml.cs)
    /// and Kestrel Server (WindowsServiceHost.cs) modes.
    /// Call this once in each container to prevent accidental drift.
    /// </summary>
    public static IServiceCollection AddSharedServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Radzen DialogService — required by RadzenComponents in layout
        services.AddScoped<Radzen.DialogService>();
        
        // Global Server Selector Context
        services.AddScoped<IServerContextService, ServerContextService>();

        // ── Server connection infrastructure ──
        services.AddSingleton<ServerConnectionManager>();
        services.AddSingleton<IServerConnectionManager>(sp => sp.GetRequiredService<ServerConnectionManager>());
        services.AddSingleton<GlobalInstanceSelector>();

        // The instance-dropdown discovery pass. Lives outside DynamicDashboard so it can be RUN by
        // a test rather than only read by a source scanner — see SqlWatchInstanceDiscovery.
        services.AddSingleton<SqlWatchInstanceDiscovery>();

        // Paid-tier engine registrations; the implementing partial is Compile-Removed in community builds.
        AddGatedEngineServices(services);

        // no-server-idle ruling: NO implicit "Server=." local default. When no connection string is
        // configured the factory adopts the Unconfigured posture (see SqlServerConnectionFactory.IsUnconfigured).
        var connStr = configuration.GetConnectionString("SqlServer");
        var trustServerCert = configuration.GetValue<bool>("TrustServerCertificate", false);
        services.AddSingleton<IDbConnectionFactory>(sp =>
        {
            var sm = sp.GetRequiredService<ServerConnectionManager>();
            var ins = sp.GetRequiredService<GlobalInstanceSelector>();
            return new SqlServerConnectionFactory(sm, ins, connStr, trustServerCert);
        });
        services.AddSingleton<SqlServerConnectionFactory>(sp =>
        {
            var sm = sp.GetRequiredService<ServerConnectionManager>();
            var ins = sp.GetRequiredService<GlobalInstanceSelector>();
            return new SqlServerConnectionFactory(sm, ins, connStr, trustServerCert);
        });

        // ── Core infrastructure ──
        services.AddSingleton<SqlConnectionPoolService>();
        services.AddSingleton<ResilienceService>();
        services.AddSingleton<DashboardConfigService>();
        services.AddSingleton<QueryRegistry>();
        services.AddSingleton<IQueryOrchestrator, QueryOrchestrator>();
        services.AddMemoryCache();
        services.AddSingleton<ICacheHotTier, CacheHotTier>();
        services.AddSingleton<QueryExecutor>();
        services.AddScoped<DashboardDataService>();
        services.AddSingleton<AutoRefreshService>();
        services.AddSingleton<CheckRepositoryService>();
        // UI metrics services consumed by the shared Blazor layout (NavMenu injects PanelMetricsService).
        // Host-agnostic, so they belong here — registering them in AddSharedServices means BOTH the WPF
        // and the headless (--server/--service) hosts get them, instead of only the WPF host (the drift
        // that 500'd every page under the headless host on 'no registered service PanelMetricsService').
        services.AddSingleton<PerformanceInspectorService>();
        services.AddSingleton<PanelMetricsService>();
        // F-VAL temporal value engine. Effort resolver backfills per-check effort from the
        // check library by id (disk-hydrated results drop EffortHours — see pricing chain).
        services.AddSingleton<SQLTriage.Data.Services.Narration.ValueNarrativeService>(sp =>
        {
            var repo = sp.GetRequiredService<CheckRepositoryService>();
            return new SQLTriage.Data.Services.Narration.ValueNarrativeService(
                id => repo.GetCheckById(id)?.EffortHours ?? 0);
        });
        services.AddSingleton<CheckSearchService>();
        services.AddSingleton<BenchmarkService>();
        services.AddSingleton<CheckSqlStore>();   // #27 Phase A — checkId→SQL resolution seam
        services.AddSingleton<BPScriptService>();
        services.AddSingleton<ServerConfigScriptService>();   // "Apply" → Server Configuration & Hardening runner (full-edition only; script absent in community)
        // Per-circuit run state for that runner's two MULTI-INSTANCE lanes on
        // /server-configuration: the status dictionaries and the run loops that used to be plain
        // fields on the page, so navigating away no longer destroys results the operator waited
        // for. SCOPED, not singleton - see Data/Services/ServerConfigRunState.cs for why, and for
        // the limit scoped carries (a browser refresh is a new circuit).
        services.AddScoped<ServerConfigRunState>();
        // Reads each instance's SQL Agent operators and judges its Database Mail chain, so
        // /server-configuration can offer a per-instance picker instead of one free-text box, and can
        // say whether a notification to the chosen operator would actually be delivered. Scoped beside
        // the run state it fills: it holds no state of its own, and a singleton carrying its test seam
        // would owe DiLifetimeCensusTests an answer for nothing gained.
        services.AddScoped<AgentMailChainProbe>();
        services.AddSingleton<DiagnosticScriptRunner>();
        services.AddSingleton<FullAuditStateService>();
        services.AddSingleton<AuditLogService>();
        services.AddSingleton<AgentJobControlService>();
        services.AddSingleton<AssessmentRehydrationService>();  // cold-start restore of the last audit run into QuickCheckState
        services.AddSingleton<SQLTriage.Data.Services.Jobs.JobInventoryService>();  // read-only msdb job/step/schedule inventory
        services.AddSingleton<SQLTriage.Data.Services.Jobs.AgRoleResolverService>(); // read-only AG replica roles (gates job sync direction)
        services.AddSingleton<NotificationChannelService>();

        // ── Environment discovery (Dig Deeper) — read-only, ephemeral ──
        services.AddSingleton<SQLTriage.Data.Services.Discovery.AdServerLocator>();
        services.AddSingleton<SQLTriage.Data.Services.Discovery.SqlTopologyProbe>();
        services.AddSingleton<SQLTriage.Data.Services.Discovery.EnvironmentDiscoveryService>();

        // ── Alerting ──
        services.AddSingleton<AlertingService>();
        services.AddSingleton<AlertDefinitionService>();
        services.AddSingleton<AlertTemplateService>();
        services.AddSingleton<AlertHistoryService>(sp =>
            new AlertHistoryService(
                sp.GetRequiredService<ILogger<AlertHistoryService>>(),
                retentionDays: 365,
                audit: sp.GetService<AuditLogService>()));
        services.AddSingleton<AlertBaselineService>();
        services.AddSingleton<AlertEvaluationService>();
        services.AddSingleton<ServerCircuitBreakerService>();

        // ── Governance / reporting ──
        services.AddSingleton<ISqlQueryRepository, SqlQueryRepository>();
        services.AddSingleton<IFindingTranslator, FindingTranslator>();
        services.AddSingleton<IGovernanceService, GovernanceService>();
        // Phase 2: weights loaded lazily by GovernanceWeightsProvider; no longer bound via IConfiguration.
        // Phase 3: GovernanceWeightsProvider will read from IBundleAccessor once Phase 5 wires it.
        services.AddSingleton<IGovernanceWeightsProvider, GovernanceWeightsProvider>();

        // Peer-benchmark distribution (vs anonymized production baseline). Loads its
        // artifact lazily from the bundle; caches and busts on bundle change.
        services.AddSingleton<IHealthBenchmarkProvider, HealthBenchmarkProvider>();

        // ── Phase 3: Licensing (BundleAccessor + LicenseService) ──
        // BundleAccessor is registered both as the concrete type (for LicenseService to call Replace)
        // and as IBundleAccessor (for all consumers).
        services.AddSingleton<BundleAccessor>();
        services.AddSingleton<IBundleAccessor>(sp => sp.GetRequiredService<BundleAccessor>());
        services.AddSingleton<LicenseService>();
        // Corpus-DEMO gate: meters /audit corpus runs against the signed per-bundle allocation
        // (BundleFeatures.DemoCorpusInstancesPer24h). Persisted, reads N live from the bundle,
        // DevBridge unlimited on dev. Tier-agnostic — drives off the signed number, not Tier.
        services.AddSingleton<IDemoRunLedger>(sp =>
            new DemoRunLedger(
                sp.GetRequiredService<IBundleAccessor>(),
                sp.GetRequiredService<ILogger<DemoRunLedger>>()));
        // INSTANCE-SEAT gate: the persisted, HMAC-chained register of which real SQL instances this
        // licence covers. Reads the allocation LIVE from the bundle (like DemoRunLedger), so loading
        // a new licence re-licenses immediately with no restart.
        //
        // Registration ORDER is irrelevant (resolution is lazy, everything here is a singleton) even
        // though ServerConnectionManager — which takes ISeatRegister as a nullable optional param —
        // is registered further UP. No cycle exists: SeatRegister depends only on IBundleAccessor.
        services.AddSingleton<ISeatRegister>(sp =>
            new SeatRegister(
                sp.GetRequiredService<IBundleAccessor>(),
                sp.GetRequiredService<ILogger<SeatRegister>>()));
        services.AddSingleton<RagDatabaseService>();
        // Shared output-folder scanner (sp_triage / sp_Blitz CSVs). One parse path for the
        // Compliance Roadmap and the sp_Blitz dashboard.
        services.AddSingleton<IAuditOutputScanner, AuditOutputScanner>();
        // sp_Blitz dashboard: joins fired CheckIDs to the bundled corpus (+ roadmap-mapping
        // fallback) and computes the weighted-ratio health score.
        services.AddSingleton<IBlitzDashboardService, BlitzDashboardService>();

        // First-launch in-app guided tour. Scoped because it depends on
        // NavigationManager (per-circuit) and holds per-user run state.
        services.AddScoped<WelcomeTourService>();
        services.AddSingleton<GovernanceHistoryService>(sp =>
            new GovernanceHistoryService(sp.GetRequiredService<ILogger<GovernanceHistoryService>>(), retentionDays: 90));
        services.AddSingleton<LicensingEstimator>();
        services.AddSingleton<PowerEstimateService>();
        services.AddSingleton<BuildCatalogueService>();
        services.AddSingleton<QuickCheckResultStore>();
        // #84 results-import round-trip: validate/enrich/land a results JSON exported from a
        // remote (often community/redacted) capture run. Full-build-only entry points
        // (Pages/ImportResults.razor, --import CLI) are build-profile gated; the service itself
        // ships in every build like QuickCheckResultStore/CheckRepositoryService — it holds no
        // proprietary data of its own, only plumbing.
        services.AddSingleton<ImportResultsService>();
        services.AddSingleton<OwnerAssignmentStore>();
        services.AddSingleton<MissingIndexService>();
        // The SQL + reader mapping behind /index-analysis. Stateless (the connection string comes
        // in per call), so a singleton like its neighbours. Extracted from the page's @code block
        // on 2026-08-13 because a wrong reader cast in there could not be tested from anywhere.
        services.AddSingleton<IndexAnalysisService>();
        services.AddSingleton<RemediationWeightStore>();
        services.AddSingleton<RemediationTemplateStore>();
        // Ruling 1 (2026-08-25): read-only host facts (cores / NUMA nodes / RAM) behind the
        // MAXDOP, cost-threshold and max-memory pre-fills on /remediation. Stateless — the server
        // name comes in per call — so a singleton like its neighbours.
        services.AddSingleton<SQLTriage.Data.Services.Remediation.ServerSizingService>();
        // S4: reverse-index (checkId -> resolution) so a failed-check row can link straight
        // to its fix. Built once from RemediationTemplateStore.All() at construction.
        services.AddSingleton<SQLTriage.Data.Services.Remediation.CheckResolutionLookup>();
        // Gated remediation lane. Gate 2 = the bundle remediation claim (fail-closed write
        // capability; DevBridge unlocks it on a dev machine). Gate 3 = a persisted, per-server
        // credit ledger seeded from the signed MSP per-server allocation in the bundle.
        services.AddSingleton<SQLTriage.Data.Services.Remediation.IRemediationCapability,
                              SQLTriage.Data.Services.Remediation.BundleBackedRemediationCapability>();
        // Grant-layer for redeemed signed-allocation files (sits above the bundle allocation).
        services.AddSingleton<SQLTriage.Data.Services.Remediation.RemediationGrantStore>(_ =>
            new SQLTriage.Data.Services.Remediation.RemediationGrantStore());
        services.AddSingleton<SQLTriage.Data.Services.Remediation.IRemediationCreditLedger>(sp =>
            new SQLTriage.Data.Services.Remediation.PersistedRemediationCreditLedger(
                sp.GetRequiredService<SQLTriage.Data.Services.Licensing.IBundleAccessor>(),
                sp.GetRequiredService<ILogger<SQLTriage.Data.Services.Remediation.PersistedRemediationCreditLedger>>(),
                sp.GetRequiredService<SQLTriage.Data.Services.Remediation.RemediationGrantStore>()));
        // Step 5: real executor (dbatools -WhatIf + transacted SQL) and the runner
        // (the ONLY path that may execute a Remediation-classified change).
        services.AddSingleton<SQLTriage.Data.Services.Remediation.IRemediationExecutor,
                              SQLTriage.Data.Services.Remediation.DbatoolsRemediationExecutor>();
        services.AddSingleton<SQLTriage.Data.Services.Remediation.RemediationRunner>();
        // Remediation concentrate (plan item 1.1): N templates under ONE approval, over the runner
        // above — it adds no gate of its own and holds no state, so a singleton like its neighbours.
        // ⚠ As of Phase 3 its MUTATING paths are live: Pages/Remediation.razor reaches
        // ApplyBatchAsync and RollBackBatchAsync from the two armed dialogs. This comment used to
        // say only the preview path was reachable and that ApplyBatchAsync was deliberately unwired;
        // that was true in Phase 1 and false from the moment the batch got a button (corrected in
        // fix round 1, gate blocker 2).
        services.AddSingleton<SQLTriage.Data.Services.Remediation.BatchRemediationDriver>();
        // S1: deferred-verify read-back + "Verify now" (ledger-backed, no store of its own).
        services.AddSingleton<SQLTriage.Data.Services.Remediation.DeferredVerificationService>();

        // Gated host-probe lane (agentless WMI/AD via dbatools, operator creds).
        // Capability is BUNDLE-BACKED + fails closed (the HostProbe claim defaults false in every
        // real bundle until the signer sets it; DevBridge unlocks on a dev build). The WMI/AD
        // probes additionally require the operator to run elevated and fail closed (NeedsElevation)
        // otherwise — two independent fail-closed gates, never a vacuous pass.
        services.AddSingleton<SQLTriage.Data.Services.HostProbe.IHostProbeCapability,
                              SQLTriage.Data.Services.HostProbe.BundleBackedHostProbeCapability>();
        services.AddSingleton<SQLTriage.Data.Services.HostProbe.HostProbeService>();

        services.AddSingleton<ServerDocumentationService>();
        services.AddSingleton<WaitStatsHistoryService>();
        services.AddSingleton<WaitStatsService>();
        services.AddSingleton<HistoricalPerformanceService>(sp =>
            new HistoricalPerformanceService(
                sp.GetRequiredService<ILogger<HistoricalPerformanceService>>(),
                rawRetentionDays: configuration.GetValue<int>("Historical:RawRetentionDays", 14),
                hourlyRetentionDays: configuration.GetValue<int>("Historical:HourlyRetentionDays", 90),
                dailyRetentionDays: configuration.GetValue<int>("Historical:DailyRetentionDays", 365)));
        services.AddSingleton<PerformanceBaselineService>();
        services.AddSingleton<IErrorCatalog, ErrorCatalog>();
        services.AddSingleton<IQuickCheckRunner, QuickCheckRunner>();

        // ── Scheduled tasks ──
        services.AddSingleton<ScheduledTaskDefinitionService>();
        services.AddSingleton<ScheduledTaskHistoryService>();
        services.AddSingleton<ScheduledTaskEngine>();

        // ── Dashboard / session / state ──
        services.AddSingleton<HealthCheckService>();
        // #57 (2026-07-15): background collector that keeps HealthCheckService's per-server
        // metrics (CPU/memory/blocked/deadlocks/top-wait) fresh — mirrors AlertEvaluationService/
        // BlockingForensicsService's timer lifecycle. Started from App.xaml.cs / WindowsServiceHost.cs.
        services.AddSingleton<HealthMetricsCollectorService>();
        // P4-ii (2026-07-12): ExecutiveHealthService now also takes IGovernanceService +
        // CheckExecutionService (both optional ctor params, both already registered below/
        // above) so Security/Compliance can be corpus-fed. Reflection-based singleton
        // resolution walks the whole constructor graph lazily on first use, so registration
        // ORDER here doesn't matter — CheckExecutionService (registered further down) still
        // resolves correctly.
        services.AddSingleton<ExecutiveHealthService>();
        services.AddSingleton<CodeHotspotsService>();
        services.AddSingleton<CodeHotspotsCacheService>();
        services.AddSingleton<DiskIoService>();
        services.AddSingleton<RateLimiter>();
        // F6: accepted-findings baseline (must be registered before CheckExecutionService so
        // DI injects it into the greediest constructor and GetResults annotates acceptances).
        services.AddSingleton<Services.AcceptedFindingsService>();
        // #88: change-item ledger — the LOG branch beside ACCEPT. Independent SQLCipher store;
        // no dependency on CheckExecutionService (the follow-up sweep takes results as a param).
        services.AddSingleton<Services.ChangeItemService>();
        // Ruling R1 (2026-09-01): every app-issued DDL writes BOTH the tamper-evident audit chain
        // and a Change Ledger row. Registered AFTER both registers so a reordering that broke the
        // dependency would fail loudly here rather than silently journal half of each statement.
        // GetService (not GetRequiredService) on both, matching how ChangeItemService takes its own
        // audit dependency — a host missing one register still gets the other, and
        // DdlJournal.Describe() reports which are live.
        services.AddSingleton<Services.DdlJournal>(sp => new Services.DdlJournal(
            sp.GetService<AuditLogService>(),
            sp.GetService<Services.ChangeItemService>(),
            sp.GetService<ILogger<Services.DdlJournal>>()));
        services.AddSingleton<CheckExecutionService>();
        services.AddSingleton<SessionManager>();
        services.AddSingleton<UserSettingsService>();
        services.AddSingleton<IUserSettingsService>(sp => sp.GetRequiredService<UserSettingsService>());
        services.AddSingleton<IChartThemeService, ChartThemeService>();
        services.AddSingleton<BlockingHistoryService>(sp =>
            new BlockingHistoryService(
                sp.GetRequiredService<ILogger<BlockingHistoryService>>(),
                retentionDays: configuration.GetValue<int>("Blocking:RetentionDays", 30)));
        // Active forensics: on-demand server queries (deadlocks / live blocking / Query Store) +
        // a background collector that captures across all enabled instances on a timer.
        services.AddSingleton<BlockingForensicsService>();
        services.AddSingleton<SessionDataService>();
        services.AddSingleton<ToastService>();
        services.AddSingleton<LogCleanupService>();
        services.AddSingleton<MemoryMonitorService>();
        services.AddSingleton<MemoryDiagnosticsService>();
        services.AddSingleton<ConsolidationAnalysisService>();
        // ── Premium Capacity / Consolidation engine — logic sourced from the licensed bundle ──
        services.AddSingleton<Services.Capacity.IConsolidationModelProvider, Services.Capacity.ConsolidationModelProvider>();
        services.AddSingleton<Services.Capacity.PremiumConsolidationEngine>();
        // Telemetry sampler + encrypted history store (runs in app AND headless service host).
        services.AddSingleton<Services.Capacity.ConsolidationHistoryStore>();
        services.AddSingleton<Services.Capacity.ConsolidationCollector>();
        // Service catalog — single registry of services/scenarios + live state for the Services page.
        services.AddSingleton<ServiceCatalog>();
        // Feature gate — consistent HARD(licence)+SOFT(toggle) answer for "is feature X available?".
        // Reusable shell+bundle core is BundleBackedResource<T> (composed by gated providers).
        //
        // ⚠ THE REGISTRATION SEAM (GATE-02, 2026-08-08). The gate is handed its own registration
        // here, in the composition root every host passes through, and runs it on first read. It is
        // deliberately NOT a startup call in App.xaml.cs / WindowsServiceHost: it WAS, and only
        // App.xaml.cs made it, so the headless --server/--service host (the installed live service)
        // ran with an empty gate — IsLicensed false for every feature, four nav sections with zero
        // dashboard links, on the same bundle the desktop rendered 27 dashboards from. Per-host
        // duplication caused that, so a second call site is not the fix; resolvable ⇒ registered is.
        // Nothing runs at container-build time (see FeatureGate.EnsureRegistered) so this cannot
        // throw a service start into an SCM restart loop.
        services.AddSingleton<IFeatureGate>(sp =>
            new FeatureGate(gate => FeatureRegistrar.RegisterAll(sp, gate)));
        // Reads/writes buildprofile.json at the repo root — the Community Edition build profile
        // (compile-time module exclusion via buildprofile.targets). Dev working trees only.
        services.AddSingleton<BuildProfileStore>();
        services.AddSingleton<ConfigurationValidator>();
        services.AddSingleton<AutoUpdateService>();
        services.AddSingleton<DatabaseAvailabilityService>();
        services.AddSingleton<StartupService>();
        services.AddSingleton<PrintService>();
        services.AddSingleton<IPrintService>(sp => sp.GetRequiredService<PrintService>());
        services.AddSingleton<IDocumentationService, DocumentationService>();
        services.AddSingleton<ConnectionHealthService>();
        // SCOPED, deliberately. As a singleton its events were a process-wide multicast holding
        // subscriber delegates from every circuit, so one caller's Ctrl+R invoked every other
        // circuit's Run handler — which then evaluated the VICTIM's permissions and passed.
        // See KeyboardShortcutService's class doc. Guarded by DiLifetimeCensusTests.
        services.AddScoped<KeyboardShortcutService>();
        services.AddSingleton<SqlAssessmentService>();
        services.AddSingleton<MaintenanceScriptService>();
        services.AddSingleton<ReportPageConfigService>();
        // Explicit factory since 2026-09-01: XEventService now journals its five lifecycle
        // statements through DdlJournal, and the journal is optional per host.
        services.AddSingleton<XEventService>(sp => new XEventService(
            sp.GetRequiredService<ILogger<XEventService>>(),
            sp.GetRequiredService<ServerConnectionManager>(),
            sp.GetService<Services.DdlJournal>()));
        // AdminAuthService depends on InstallProvenanceService to tell a fresh install
        // (admin write path fails closed) from a pre-upgrade one (stays open + warns).
        services.AddSingleton<InstallProvenanceService>();
        services.AddSingleton<AdminAuthService>();
        services.AddSingleton<QuickCheckStateService>();
        services.AddSingleton<VulnerabilityAssessmentStateService>();
        // Per-circuit halves of the three run-page state services. SCOPED, deliberately: these
        // carry the SELECTION that a privileged run targets, so sharing them process-wide let
        // one caller retarget another caller's scan. See Data/Services/CircuitViewState.cs.
        services.AddScoped<QuickCheckViewState>();
        services.AddScoped<VulnerabilityAssessmentViewState>();
        services.AddScoped<FullAuditViewState>();
        services.AddSingleton<ReportBundleService>();
        // HA/DR & Backup Posture report — pure recomposition of corpus results, RestoreVerify
        // (MSP #9) history, and the cached ServerDocs snapshot (all registered above).
        services.AddSingleton<HaDrPostureReportService>();

#if !SQLT_NO_DEVTOOLS
        // ── Risk Assessment report (consultant SSRS replacement) — NEVER ships to
        //    community/public. Sources are Compile-Removed by buildprofile.targets
        //    when SQLTExcludeDevTools is set, so this block must be symbol-fenced to
        //    match (the referenced types are absent in that build). ──
        services.AddScoped<SQLTriage.Data.Services.RiskReport.IRiskAssessmentSource,
                           SQLTriage.Data.Services.RiskReport.LiveRiskAssessmentSource>();
        services.AddScoped<SQLTriage.Data.Services.RiskReport.RiskAssessmentService>();
#endif

        // ── Compliance Framework v1 (Strategic Gap #3) ──
        services.AddSingleton<ComplianceMappingService>();
        services.AddSingleton<ComplianceScoreService>();

        // ── Control Hierarchy (Framework Tree) — builds the per-framework control
        //    forest from CheckRepositoryService.FrameworkMappings + catalogue metadata,
        //    cached against the catalogue's integrity hash. Ships in every profile
        //    (no BuildModules gate). Verdicts join per render from QuickCheckStateService. ──
        services.AddSingleton<SQLTriage.Data.Services.FrameworkTree.FrameworkTreeService>();

        // ── Read-only MCP surface (Mcp/) — deregistered from DI 2026-08-20 (D2
        //    holds: sources stay, ruling is scaffold-only until a live-data MCP
        //    increment revives it). The old #if ENABLE_MCP callsite was already
        //    permanently off (no build ever defines ENABLE_MCP); this removes the
        //    callsite itself rather than relying on an undefined symbol staying
        //    undefined. To revive: call SQLTriage.Mcp.McpServiceRegistration
        //    .AddSqlTriageMcpReadOnly(services) here, add the using, and see
        //    Mcp/README.md for the hosting story (no transport is wired). ──

        // ── Gated portal runtime services. The registration body lives in
        //    Data/Services/Portal/PortalServiceRegistration.cs, Compile-Removed from the
        //    community build (SQLTExcludePortal) — so this always-compiled callsite names
        //    only the neutral extension method.
        //    The fence is load-bearing: in community the extension does not exist and an
        //    unfenced call breaks the build (fail-closed by design). ──
#if !SQLT_NO_PORTAL
        services.AddPortalRuntimeServices();
#endif

        // ── Compliance / SOC2 services ──
        services.AddSingleton<UptimeTrackerService>(sp =>
            new UptimeTrackerService(
                sp.GetRequiredService<ILogger<UptimeTrackerService>>(),
                startTimer: true));
        services.AddSingleton<ConfigBaselineService>(sp =>
            new ConfigBaselineService(
                sp.GetRequiredService<ILogger<ConfigBaselineService>>(),
                sp.GetService<AuditLogService>()));
        services.AddSingleton<Services.ServerConfigBaselineService>(sp =>
            new Services.ServerConfigBaselineService(
                sp.GetRequiredService<ILogger<Services.ServerConfigBaselineService>>(),
                sp.GetService<ServerConnectionManager>(),
                sp.GetService<AuditLogService>(),
                sp.GetService<IConfiguration>()));
        // Changed-objects detection (client-needs Q3): per-scan object inventory + baseline diff.
        services.AddSingleton<Services.ChangedObjectsService>();

        // ── Audit / observability ──
        // CorrelationIdAccessor stores its value in an AsyncLocal (see class doc),
        // so the singleton enricher reads it without needing a captive Scoped
        // dependency — that captive resolve crashed Blazor Server scope validation.
        services.AddScoped<CorrelationIdAccessor>();
        services.AddSingleton<ILogEventEnricher, CorrelationIdEnricher>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<ServerModeService>();
        services.AddSingleton<DataProtectionService>();
        services.AddSingleton<AzureBlobExportService>();
        services.AddSingleton<ProcessGuard>();
        services.AddSingleton<ForecastService>();
        services.AddSingleton<ProductionReadinessGate>();
        services.AddSingleton<RbacService>();
        // The narrow read-only posture handle the always-rendered shell injects instead of the
        // full RbacService (security-8): a forwarding registration so it IS the RbacService
        // singleton, never a second instance, while the shell's compile-time surface stays one
        // method wide. See IRbacEnforcementPostureAccessor.
        services.AddSingleton<IRbacEnforcementPostureAccessor>(sp => sp.GetRequiredService<RbacService>());
        services.AddScoped<AppUserState>();
        // Default: the WPF desktop container. Kestrel hosts OVERRIDE this with
        // HostEnvironmentInfo.BrowserHosted — see HostEnvironmentInfo for why the answer has to
        // be a property of the container rather than of the process.
        services.AddSingleton(HostEnvironmentInfo.Desktop);
        services.AddSingleton<LocalLogService>();
        services.AddSingleton<PowerShellService>();

        // ── liveQueries caching layer ──
        services.AddSingleton<liveQueriesCacheStore>();
        services.AddSingleton<CacheStateTracker>();
        services.AddSingleton<CachingQueryExecutor>();
        services.AddSingleton<CacheEvictionService>();
        services.AddSingleton<liveQueriesMaintenanceService>();
        services.AddSingleton<CacheMetricsService>();

        // ── Retained metric history (NOT cache — see MetricRetentionOptions) ──
        services.AddSingleton(MetricRetentionOptions.FromConfiguration(configuration));
        services.AddSingleton<MetricHistoryCollectorService>();

        return services;
    }

    // Paid-tier engine registrations. Declared with NO accessibility modifier so the C# compiler
    // elides this declaration AND every call to it whenever no implementing partial is compiled — the
    // community build (which Compile-Removes the implementing partial) then carries zero reference,
    // needs no symbol fence, and has no null-reference risk. The implementing partial lives in the
    // already-excluded engine folder.
    static partial void AddGatedEngineServices(IServiceCollection services);
}
