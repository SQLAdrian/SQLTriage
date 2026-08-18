<!-- In the name of God, the Merciful, the Compassionate -->

# Services

All service files in `Data/Services/` with one-line purpose. Full catalogue at `.claude/docs/services-index.md`.

## Service Catalogue

| Service | Purpose |
|---------|---------|
| AdminAuthService | Admin authentication including TOTP validation and session management |
| AlertBaselineService | Computes alert baselines from historical metric data |
| AlertDefinitionService | CRUD for alert rule definitions with threshold and channel binding |
| AlertEvaluationService | Evaluates active alert rules against live metrics and raises alerts |
| AlertHistoryService | Persists and queries alert firing history |
| AlertTemplateService | Manages reusable alert rule templates for quick deployment |
| ApiEndpoints | Minimal-API endpoint registration for external tool integration |
| AppCircuitHandler | Blazor circuit lifecycle handler for connection tracking and cleanup |
| AppUserState | Per-circuit user state container (role, preferences, active instance) |
| AzureBlobExportService | Exports reports, logs, and check results to Azure Blob Storage |
| BenchmarkService | Runs synthetic SQL benchmarks and records latency/throughput |
| CacheMetricsService | Tracks cache-hit ratios, plan cache size, and buffer pool utilisation |
| ChartThemeService | Provides ApexCharts theme configuration (dark/light/colorblind) |
| CodeHotspotsService | Identifies high-churn, high-complexity code regions from commit history |
| ConnectionHealthService | Monitors SQL connection latency, drops, and circuit health |
| DataProtectionService | Wraps ASP.NET Data Protection API for key ring and secret encryption |
| DevBridgeService | LLM/dev HTTP API for navigation, screenshot, and eval (off by default) |
| ErrorCatalog | Centralised SQL error-code lookup with severity and remediation text |
| ExecutionPlanParser | Parses ShowPlan XML into structured operator trees with cost annotations |
| ExecutiveHealthService | Computes executive-level health score across all monitored instances |
| FindingTranslator | Translates raw check findings into human-readable remediation steps |
| ForecastService | Time-series forecasting for CPU, IO, and storage metrics |
| GovernanceHistoryService | Stores governance score snapshots for trend analysis |
| GovernanceService | Governance framework engine with policy scoring and compliance reporting |
| KeyboardShortcutService | Registers global keyboard shortcuts and routes them to handlers |
| NotificationChannelService | Manages Teams, email, and webhook notification channel configuration |
| PMInstallationService | Deploys and configures Darling PM (Performance Monitor) agents |
| PanelMetricsService | Aggregates and caches panel-level metric data for dashboards |
| PerformanceInspectorService | Collects and compares PerfMon counter snapshots |
| PowerShellService | Executes PowerShell scripts with timeout, output capture, and error handling |
| PrintService | Generates server-side PDF reports from page HTML |
| ProcessGuard | Single-instance mutex guard preventing duplicate application launches |
| ProductionReadinessGate | Pre-production checklist runner validating instance configuration |
| QuickCheckRunner | Runs the QuickCheck audit suite and returns a pass/fail summary |
| RbacService | Role-based access control with permission enumeration and guard helpers |
| ReportPageConfigService | Loads and caches per-report-page layout configuration |
| ScheduledTaskDefinitionService | CRUD for scheduled task definitions with cron expressions |
| ScheduledTaskEngine | Cron-based task scheduler that invokes registered job handlers |
| ScheduledTaskHistoryService | Persists and queries scheduled task execution history |
| SchedulerRegistryService | Registry of scheduled tasks with health monitoring and pause/resume |
| ServerModeService | Manages server connection mode (connected, local, offline) |
| SqlAssessmentService | Runs SQL Assessment API checks and aggregates results |
| SqlQueryRepository | Central repository of parameterised SQL queries used by panels |
| SqlWatchDeploymentService | Deploys and configures SQLWATCH monitoring framework |
| ThemeService | UI theme manager for dark/light mode with persistent preference |
| UnifiedCheckService | Unified check runner dispatching to SQL, PS, WMI, and registry executors |
| VulnerabilityAssessmentStateService | Caches VA scan results and tracks per-database scan status |
| WindowsServiceHost | Windows Service lifetime host for background-scheduled task execution |
| XEventService | Creates, starts, stops, and reads Extended Events sessions |

### Assessment/ Subfolder

| File | Purpose |
|------|---------|
| CheckExecutionResult | Result DTO capturing exit code, output, duration, and errors |
| PowerShellCheckExecutor | Executes PowerShell-based audit checks |
| RegistryCheckExecutor | Executes registry-key audit checks |
| SqlCheckExecutor | Executes T-SQL audit checks against a target instance |
| WmiCheckExecutor | Executes WMI-query audit checks |

## Registration

All services are registered in `App.xaml.cs` `ConfigureServices` via `services.AddSingleton<T>()` or `services.AddScoped<T>()`.

## Conventions

- Services are stateless by default; singletons via DI, scoped where per-circuit state is needed.
- Async methods return `Task<T>`; synchronous wrappers are avoided.
- Constructor injection only; services declare dependencies through their constructor signature.
- Services log errors via `ILogger<T>` and re-throw; callers handle user-facing messaging.
