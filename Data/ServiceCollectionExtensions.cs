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
        services.AddSingleton<CheckSqlStore>();   // #27 Phase A — checkId→SQL resolution seam
        services.AddSingleton<BPScriptService>();
        services.AddSingleton<DiagnosticScriptRunner>();
        services.AddSingleton<FullAuditStateService>();
        services.AddSingleton<AuditLogService>();
        services.AddSingleton<NotificationChannelService>();

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

        // ── Phase 3: Licensing (BundleAccessor + LicenseService) ──
        // BundleAccessor is registered both as the concrete type (for LicenseService to call Replace)
        // and as IBundleAccessor (for all consumers).
        services.AddSingleton<BundleAccessor>();
        services.AddSingleton<IBundleAccessor>(sp => sp.GetRequiredService<BundleAccessor>());
        services.AddSingleton<LicenseService>();
        services.AddSingleton<RagDatabaseService>();

        // ── Phase 6: sp_BLITZ CSV ingest ──
        services.AddSingleton<SpBlitzCache>();
        services.AddSingleton<SpBlitzImportService>();
        services.AddSingleton<AuditOutputScanner>();
        services.AddSingleton<GovernanceHistoryService>(sp =>
            new GovernanceHistoryService(sp.GetRequiredService<ILogger<GovernanceHistoryService>>(), retentionDays: 90));
        services.AddSingleton<RemediationCostEstimator>();
        services.AddSingleton<LicensingEstimator>();
        services.AddSingleton<BuildCatalogueService>();
        services.AddSingleton<QuickCheckResultStore>();
        services.AddSingleton<RemediationWeightStore>();
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
        services.AddSingleton<SessionDataService>();
        services.AddSingleton<ToastService>();
        services.AddSingleton<LogCleanupService>();
        services.AddSingleton<MemoryMonitorService>();
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

        // ── Compliance Framework v1 (Strategic Gap #3) ──
        services.AddSingleton<ComplianceMappingService>();
        services.AddSingleton<ComplianceScoreService>();

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
