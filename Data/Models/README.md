<!-- In the name of God, the Merciful, the Compassionate -->

# Models

POCOs and DTOs. No behaviour, no service dependencies — just data shapes serialised to/from SQLite, JSON, the UI, or DMV results.

## Common families

| Group | Files |
|-------|-------|
| **Audit / checks** | SqlCheck, CheckConfiguration, CheckResult, CheckStatus, PlaybookCheck, ScriptConfiguration[ViewModel] |
| **Findings** | BlitzFinding, FindingTranslation, ErrorCatalogEntry |
| **Sessions / blocking** | SessionInfo, BlockingEvent, BlockingInfo, BlockingNode |
| **Server / connection** | ServerConnection, ServerDiagnosticInfo, ServerEnvironment, ServerHealthStatus, ServerDocSnapshot |
| **Metrics / time-series** | TimeSeriesPoint, QueryPerformanceMetrics, StatValue, StatStackValue, PanelTrace |
| **Alerts / scheduling / RBAC** | AlertConfiguration, AlertTemplateConfig, NotificationChannelConfig, ScheduledTaskModels, RbacModels |
| **Dashboards / reports** | DashboardConfig, DashboardFilter, ReportPageConfig, PdfExportSettings |
| **Auth / config / cache DTOs** | AuthenticationTypes, BPScriptConfig, CacheDataTableDto, MaintenanceResult |

## Conventions

- Nullable reference types enabled — required vs optional encoded in declarations
- JSON contracts use System.Text.Json with default property naming (PascalCase)
- DateTime is UTC unless name ends in `Local`
