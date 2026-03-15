# Changelog

All notable changes to SQL Health Assessment are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).
Versioning follows [Semantic Versioning](https://semver.org/).

---

## [0.78.6] — 2026-03-13

**Build 282.** Post-release feature expansion and query plan overhaul.

### Added
- **Live Index Health dashboard** (`liveindexes`) — missing index count stat card, high-impact missing indexes grid, indexes with >30% fragmentation, unused index candidates
- **Backup Health dashboard** (`backuphealth`) — databases without a full backup in 7 days, databases without a log backup in 1 hour, last full backup per database, recent backup history grid
- **Live Query Stats dashboard** (`livequerystats`) — plan cache hit ratio, plan count, top queries by CPU / logical reads / duration
- **Live Jobs dashboard** (`livejobs`) — currently running jobs count, failed jobs in last 24 hours, running job detail grid, failure history, all-jobs status grid
- **Live TempDB dashboard** (`livetempdb`) — used MB stat card, version store MB, long-running transaction count, per-file usage, top session consumers, long-transaction detail
- **Locking Waits % stat card** added to Live Wait Stats dashboard — `LCK%` waits as a percentage of total waits, with 10% / 30% colour thresholds
- **Vulnerability Assessment page** — dedicated page for SQL Server security assessment results
- **Query Plan V2 detail pane** — replaces CSS-only tooltip; single shared `position:absolute` pane per canvas at `top:8px; right:8px`; hover-interactable; full object path (database.schema.table), `EstimateExecutions`, individual cost components, predicate text, and warnings
- **Query Plan V2 — root node shows 100%** — the leftmost operator always displays 100.0% cumulative cost regardless of floating-point rounding
- **Query Plan V2 — Copy button** — `fa-solid fa-copy` button in the pane header copies the node details to the clipboard; icon flips to `fa-check` for 1.8 s on success
- **Query Plan V2 — fade on exit** — pane fades out over 1.5 s after the cursor leaves the node; hovering over the pane cancels the fade and restores full opacity
- **Query Plan V2 — extended XML fields** — `EstimateExecutions`, `ObjectDb`, `ObjectSchema`, and `Predicate` extracted by `ExecutionPlanParser` and surfaced in the detail pane
- **Performance Monitor — execution plan viewer** — `query_plan_xml` column in the Expensive Queries grid now shows the plan-badge icon and opens the Query Plan V2 viewer on click (extends `DynamicPanel` candidate-column detection to `query_plan_xml`, `query_plan`, `plan_xml`)
- **Cancellable dashboard loads** — `DynamicDashboard` now uses a `CancellationTokenSource` pattern (`StartLoad()` / `CancelLoad()`); switching servers or triggering a refresh cancels any in-flight queries
- **Cancel button in loading bar** — `fa-solid fa-xmark` button in the dashboard loading indicator allows the user to abort the current load

### Fixed
- `SessionDataService` — broken brace structure left by a partial refactor caused `CS1501` / `CS1513` compile errors; replaced with a clean ternary connection-factory pattern

---

## [1.0.0] — 2026-02-24

**First public production release.** Build 104.

### Added
- **Interactive execution plan viewer** — graphical SQL Server execution plan rendering with node-level cost tooltips, powered by `html-query-plan`
- **Query Plan Modal** — click any query row with a cached plan badge (📋) to open the visual plan in a full-screen overlay
- **Full Audit page** (`Ctrl+4`) — comprehensive deep-dive covering configuration review, security assessment, backup verification, and index fragmentation
- **Quick Check** (`Ctrl+Q`) — instant health snapshot with configurable severity filtering (Critical / Warning / Info)
- **Blocking chain analysis** — real-time blocking tree with lead blocker identification
- **Wait event statistics** — historical wait categories with trend charts
- **Long query detection** — configurable threshold with query text and duration details
- **Dashboard Editor** (`Ctrl+E`) — in-app JSON editor for customising panel layouts without restarting
- **10 UI themes** — switchable at runtime via the theme selector
- **Keyboard shortcut system** — `Ctrl+1–9` for dashboards, `?` for help overlay, `Esc` to close modals
- **Panel maximise / full-screen** — expand any panel to fill the window
- **Delta-fetch for time-series** — subsequent refreshes only retrieve new data points
- **SQLite WAL-mode cache** — offline resilience; panels serve stale data when SQL Server is unreachable
- **Scheduled SQLite maintenance** — automatic VACUUM and OPTIMIZE every 4 hours; integrity check every 6 runs
- **Two-tier query throttle** — separate semaphore limits for heavy and light queries
- **Rate limiter** — configurable max queries per minute with optional popup warning
- **Session idle detection** — polling rate drops automatically when the application window is inactive
- **Memory pressure monitor** — background service that alerts and optionally evicts cache under high memory load
- **DPAPI credential encryption** — connection passwords encrypted at rest via Windows Data Protection API
- **MFA / Azure AD authentication** — modern auth support via `Azure.Identity` (`DefaultAzureCredential`)
- **Audit log** — JSONL-format query execution and user action log, 90-day configurable retention
- **Auto-update check** — notifies when a newer GitHub release is available
- **Automated SQLWATCH deployment** — deploy the SQLWATCH dacpac to a new server directly from the app
- **Multi-server support** — add unlimited server connections; switch via instance dropdown or "All Instances" aggregate view
- **Repository dashboard** — status card for every monitored server on one screen
- **Toast notification system** — non-blocking feedback for background operations
- **Serilog structured logging** — 30-day rolling file logs with configurable verbosity per namespace

### Changed
- Dashboard configuration moved to `dashboard-config.json` (fully editable, hot-reloaded on save)
- Query executor now streams results using `Utf8JsonWriter` to reduce peak memory during large fetches
- Row reading refactored to use `ArrayPool<T>` — eliminates per-row allocations on hot paths
- Cache writes use per-query locks instead of a global lock, reducing write contention
- Server GC + concurrent GC enabled in `runtimeconfig.template.json` for optimal throughput

### Security
- All user-supplied query parameters use `SqlParameter` — no string interpolation in SQL paths
- Connection strings with `enc:` prefix are decrypted at runtime; plain-text passwords are warned against in the UI
- `TrustServerCertificate` defaults to `true` for ease of first use; UI surfaces a warning and a toggle

---

## [0.9.0] — 2025-12-01

### Added
- **CachingQueryExecutor** — wraps the base executor with transparent SQLite read-through / write-behind caching
- **CacheStateTracker** — tracks per-panel cache freshness and surfaces stale-data indicators in the UI
- **CacheEvictionService** — background service that enforces `MaxCacheSizeBytes` and `CacheEvictionHours` limits
- **DynamicDashboard / DynamicPanel** — generic panel renderer driven entirely by `dashboard-config.json`
- **StatCard, BarGauge, DeltaStatCard** — new panel types for KPI-style displays
- **TimeSeriesChart** — ApexCharts-based panel with configurable colour thresholds
- **DataGrid** — sortable, filterable grid panel with export-to-CSV

### Changed
- Navigation restructured into collapsible sidebar
- Dashboard toolbar added with instance selector, refresh controls, and theme picker

---

## [0.8.0] — 2025-09-15

### Added
- **Health check framework** — configurable SQL checks with threshold-based pass/warn/fail evaluation
- **Checks page** — results table with severity colouring and drill-down details
- **Bulk-edit checks** — update thresholds for multiple checks at once
- **AlertingService** — evaluates check results and raises UI alerts on threshold breaches
- **DiagnosticScriptRunner** — executes bundled SQL diagnostic scripts (sp_Blitz, sp_triage, checklists)

### Changed
- `QueryExecutor` made injectable; all callers now use the `IDbConnectionFactory` abstraction

---

## [0.7.0] — 2025-07-01

### Added
- **Sessions page** — active session list with session bubble visualisation
- **SessionBubbleView** — colour-coded session map grouped by wait type
- **SessionDetailPanel** — per-session detail with query text, wait info, and kill action
- **Live Monitor dashboard** — combined sessions + top queries + wait stats view
- **ConnectionDialog** — server connection setup with test-connection flow
- **CredentialProtector** — DPAPI-based encryption/decryption for stored passwords

---

## [0.6.0] — 2025-04-15

### Added
- Initial WPF + Blazor WebView shell
- `QueryExecutor` with async SQL Server connectivity
- `DashboardConfigService` — loads and caches `dashboard-config.json` with O(1) query lookup
- `AutoRefreshService` — timer-based panel refresh with configurable interval
- `ConnectionManager` — in-memory server connection registry
- Serilog wired to file sink (`logs/app-YYYYMMDD.log`)
- `appsettings.json` configuration loading
- Dark theme baseline CSS

---

[1.0.0]: https://github.com/SQLAdrian/SqlHealthAssessment/releases/tag/v1.0.0
[0.9.0]: https://github.com/SQLAdrian/SqlHealthAssessment/releases/tag/v0.9.0
[0.8.0]: https://github.com/SQLAdrian/SqlHealthAssessment/releases/tag/v0.8.0
[0.7.0]: https://github.com/SQLAdrian/SqlHealthAssessment/releases/tag/v0.7.0
[0.6.0]: https://github.com/SQLAdrian/SqlHealthAssessment/releases/tag/v0.6.0
