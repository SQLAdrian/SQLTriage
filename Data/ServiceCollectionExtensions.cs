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
#if ENABLE_MCP
using SQLTriage.Mcp;
#endif

namespace SQLTriage.Data;

public static class ServiceCollectionExtensions
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

        var connStr = configuration.GetConnectionString("SqlServer") ?? "Server=.;Database=SQLWATCH;Integrated Security=true;";
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
        services.AddSingleton<DiagnosticScriptRunner>();
        services.AddSingleton<FullAuditStateService>();
        services.AddSingleton<AuditLogService>();
        services.AddSingleton<AgentJobControlService>();
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
        services.AddSingleton<OwnerAssignmentStore>();
        services.AddSingleton<MissingIndexService>();
        services.AddSingleton<RemediationWeightStore>();
        services.AddSingleton<RemediationTemplateStore>();
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
        services.AddSingleton<ExecutiveHealthService>();
        services.AddSingleton<CodeHotspotsService>();
        services.AddSingleton<CodeHotspotsCacheService>();
        services.AddSingleton<DiskIoService>();
        services.AddSingleton<RateLimiter>();
        services.AddSingleton<CheckExecutionService>();
        services.AddSingleton<liveQueriesTableService>();
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
        services.AddSingleton<IFeatureGate, FeatureGate>();
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
        services.AddSingleton<KeyboardShortcutService>();
        services.AddSingleton<SqlAssessmentService>();
        services.AddSingleton<MaintenanceScriptService>();
        services.AddSingleton<ReportPageConfigService>();
        services.AddSingleton<XEventService>();
        services.AddSingleton<AdminAuthService>();
        services.AddSingleton<QuickCheckStateService>();
        services.AddSingleton<VulnerabilityAssessmentStateService>();
        services.AddSingleton<ReportBundleService>();

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

        // ── Read-only MCP surface (Mcp/) — additive & inert: registers the
        //    projection service + SDK tool catalogue, starts no transport.
        //    Depends on CheckRepositoryService + ComplianceMappingService above.
        //    Internal-only (D2): fenced out of the community/public build. ──
#if ENABLE_MCP
        services.AddSqlTriageMcpReadOnly();
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
        services.AddScoped<AppUserState>();
        services.AddSingleton<LocalLogService>();
        services.AddSingleton<PowerShellService>();

        // ── liveQueries caching layer ──
        services.AddSingleton<liveQueriesCacheStore>();
        services.AddSingleton<CacheStateTracker>();
        services.AddSingleton<CachingQueryExecutor>();
        services.AddSingleton<CacheEvictionService>();
        services.AddSingleton<liveQueriesMaintenanceService>();
        services.AddSingleton<CacheMetricsService>();

        return services;
    }
}
