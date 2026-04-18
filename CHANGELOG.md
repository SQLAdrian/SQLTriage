<!-- In the name of God, the Merciful, the Compassionate -->
<!-- Bismillah ar-Rahman ar-Raheem -->

# Changelog

All notable changes to SQL Health Assessment are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).
Versioning follows [Semantic Versioning](https://semver.org/).

---

## [0.85.2] — 2026-04-14

**Query Plan V2 — multi-statement tabs, 4-column pane, collapse/expand, cost highlights.**

### Added
- **Statement tab bar always visible** — tab bar now renders for single-statement plans too; always shown above the plan canvas; tabs clipped by `overflow:hidden` fixed by moving the bar outside `#qp-container` as a sibling in `.query-plan-modal-body`
- **Tab cost heat colours** — statement tabs with ≥50% batch cost get a red background tint + bold red cost %, 20–49% amber tint, mirroring operator cost highlighting so the most expensive statement stands out immediately
- **Operator detail pane — 4-column grid** — pane widened from 420 px to 660 px; short property rows are paired two-per-row (label | value | label | value); wide rows (predicates, index DDL, Object, etc.) span all four columns
- **Index DDL — Copy DDL button always shown** — index action bar now appears whenever a suggested index exists regardless of No-Pants mode; always includes a **Copy DDL** button; **Execute on server** button only appears when No-Pants + ConnectionId are both active
- **Tree node collapse/expand** — nodes with children show a `−` / `+` toggle on their left edge; clicking hides/shows all descendant nodes and their connecting edges; edges tagged with `data-edge-source`/`data-edge-target` for targeted hide
- **Summary section chevrons** — every summary panel section (Parameters, Wait Stats, Statistics Used, SET Options, Trace Flags) is now individually collapsible via a `▼` / `▶` chevron; clicking the section title toggles it; sections default open

### Fixed
- **No-Pants options bar CSS** — added `.plan-options-panel` CSS rule with `flex-shrink:0`; `.query-plan-modal-body` changed to `overflow:hidden` to prevent double-scroll; `.plan-controls` search bar gets `flex-shrink:0`

---

## [0.85.2] — 2026-04-09

**PM Health dashboard, parallel Full Audit, enhanced blocking, and reliability fixes.**

### Added
- **PM Health & Diagnostics dashboard** — 12-panel dashboard built on Erik Darling PerformanceMonitor `report.*` views: critical issues summary, collection health, daily summary (CPU/memory/waits), CPU spikes, memory pressure events, plan cache bloat stat + detail grid, memory grant pressure, CPU scheduler runnable queue StatCard, parameter sniffing (PSP) detection, file I/O latency by drive, blocking chain analysis
- **FinOps panels** — Database Resource Cost (logical reads + CPU per database), Provisioning Assessment (allocated vs used space), and Peak Hours analysis added to the Database Health dashboard
- **Full Audit — Sequential / Parallel execution** — inline toggle on the run-all-servers toolbar; Parallel mode fans out per-server runs with a configurable stagger delay (default 5 s), confirmation modal with amber bolt warning; Sequential mode shows a plain confirmation; both modes update the UI thread-safely via `InvokeAsync`
- **Live Monitor — Runnable Tasks StatCard** — scheduler runnable queue depth added to the Live Monitor dashboard
- **TempDB contention analysis panel** — `report.tempdb_contention_analysis` surfaced on the Live TempDB dashboard

### Changed
- **Blocking panel — exact statement extraction** — uses `statement_start_offset` / `statement_end_offset` from `sys.dm_exec_requests` to show the precise blocking statement rather than the full batch; `query_plan_xml` column added so the blocking query's plan opens in the Query Plan V2 viewer on click
- **Query Plan V2 — Operator Cost display** — merged separate "Relative Cost %" row into the "Operator Cost" row (`0.003821 (14.0%)`) to match SSMS / Azure Data Studio format; leftmost root operator always shows 100.0%

### Fixed
- **DataGrid table height jitter** — dashboard DataGrid panels now cap at 3× their configured `height` in pixels with `overflow-y: auto` and a sticky `<thead>`; prevents page-layout shift when row counts change between refreshes
- **Settings — Preview Features checkbox disabled** — `_noPantsModeActive` and `_experimentalModeActive` were not updated by the toggle handlers, leaving the dependent checkbox perpetually disabled; both fields now sync on change
- **Maturity Roadmap nav link not updating live** — `UserSettingsService.SetShowMaturityRoadmap` now fires `OnShowMaturityRoadmapChanged`; `NavMenu` subscribes and calls `StateHasChanged` so the nav entry appears/disappears immediately without a page reload
- **Update applier — old PowerShell fallback** — `WriteUpdateApplierScript` now uses a 3-tier extraction chain: `Expand-Archive` (PS 5+) → .NET `[System.IO.Compression.ZipFile]` → `Shell.Application` COM object; covers Server 2008 R2 / PS 2 targets
- **Update — server mode / air-gap manual import** — Service Management page now includes a Manual Update section with an `<InputFile>` component (max 500 MB); uploaded ZIP is piped to `AutoUpdateService.StageFromFileAsync` and staged identically to an auto-downloaded update
- **SQLite cache — eviction scan performance** — added `CREATE INDEX IF NOT EXISTS` on `fetched_at` for all 6 cache tables (`cache_timeseries`, `cache_stat`, `cache_bargauge`, `cache_datatable`, `cache_checkstatus`, `cache_metadata`); eliminates full-table scans on the 5-minute eviction cycle

---

## [0.82.0] — 2026-04-02

**Diagnostics Maturity Roadmap, multi-file audit aggregation, experimental mode, and UX improvements.**

### Added
- **Diagnostics Maturity Roadmap** — maps sp_triage (`SectionID`) and sp_Blitz (`CheckID`) CSV output to a 5-level maturity framework (Foundation → Hardened → Performant → Observable → Optimised); category tag badges on each check row; PDF export
- **Multi-file audit selection** — checkbox list replaces single dropdown; select multiple servers/files simultaneously; maturity scores aggregated across all selected files
- **sp_Blitz CSV support** — output folder now detects `*blitz*.csv` files in addition to `*sp_triage*.csv`; uses `CheckID` and `ServerName` columns automatically
- **Items Impacted counter** — total occurrence count of fired checks (mapped, non-informational) shown in the maturity summary panel
- **Experimental mode toggle** — amber flask icon in nav footer; gates preview features (Plan Analyzer, BP Scripts, Capacity Planning, notification channels); persisted to user settings; global event broadcast
- **WhatsApp notification channel** — added to multi-channel alerting

### Changed
- **Nav order** — Tools section moved directly under Audits
- **Azure Blob connection test** — three-tier probe (GetProperties → ListBlobs → write probe) handles write-only and directory-scoped SAS tokens without false 403 failures
- **Taskbar icon** — minimising no longer hides app from taskbar; tray-hide is tray-menu only
- **PDF export** — removed dark cover page that caused blank/black first page in WebView2; export now starts directly with maturity content

### Fixed
- **sp_Blitz files not appearing** — `ServerName` column lookup (was looking for `SQLInstance`); date column index 4 (empty header)
- **Items Impacted inflation** — previously summed all CSV rows including unmapped and informational IDs; now only counts IDs present in roadmap-mapping.json after exclusions
- **Build version stamping** — `StampVersionFromJson` now runs `AfterTargets="IncrementBuildNumber"` to guarantee it reads the incremented value

---

## [0.80.0] — 2026-03-26

**Enterprise monitoring, multi-channel alerting, and server management release.**

### Added
- **Replication Monitor dashboard** — publications, subscriptions, undistributed commands, and distribution agent history from `distribution` database DMVs
- **Always On AG dashboard** — AG health summary, replica status, database sync state with redo/send queue sizes, send queue trend (TimeSeries), redo queue trend (TimeSeries), AG listeners
- **SQL Agent Job Monitor dashboard** — 4 stat cards (running/failed/succeeded/disabled), all-jobs overview with last outcome and next run, failure detail (step-level), long-running jobs, job schedules
- **Slack notification channel** — incoming webhook with Block Kit attachments, color-coded by severity
- **Generic Webhook channel** — HTTP POST with optional Bearer token and custom headers
- **PagerDuty channel** — Events API v2 with dedup key and severity mapping
- **ServiceNow channel** — REST Table API incident creation with urgency/impact mapping
- **Server tagging** — comma-separated tags per connection, displayed as badges on server cards
- **Server environment labels** — Production, Staging, Development, QA, DR dropdown with color-coded badges
- **Server filter bar** — click-to-filter by environment or tag across the server inventory
- **Blazor Circuit Handler** — logs circuit open/close/reconnect/disconnect with active circuit count for server-mode diagnostics
- **SQLDBA.ORG quick-setup** — one-click Azure Blob Export configuration with consent modal listing shared data categories
- **Browser print fallback** — `window.print()` PDF export when running in server mode (no WebView2)

### Changed
- **Connection pool default → 50** — increased from 20 for better multi-circuit concurrency in server mode
- **Script timeout default → 900 s** — increased from 300 s to support long-running audit scripts (sp_Blitz, sp_triage)
- **Full Audit row limits removed** — diagnostic scripts now return all rows without truncation
- **Async void handlers eliminated** — CacheEvictionService timer and memory-pressure callbacks now delegate to proper `async Task` methods
- **Teams Adaptive Card** — fixed `$schema` key using Dictionary approach (C# identifiers can't start with `$`)
- **System tray icon** — now extracted from running exe via `Icon.ExtractAssociatedIcon` to match window/taskbar icon
- **About page** — added Alerting & Notifications, Server Management feature cards; added Replication, Always On AG, Job Monitor to dashboard table
- **README** — added Alerting & Notifications, Server Management, Always On AG, Replication, Job Monitor sections

### Fixed
- **Zoom event leak** — `MainWindow.xaml.cs` now unsubscribes from `OnZoomChanged` on window close
- **Server mode DI crash** — added missing service registrations for `AlertDefinitionService`, `AlertHistoryService`, `AlertEvaluationService`, `ScheduledTaskDefinitionService`, `ScheduledTaskHistoryService`, `ScheduledTaskEngine`
- **PDF export in server mode** — "WebView not available" error resolved with browser print fallback across Quick Check, Vulnerability Assessment, Sessions, and Dashboard Toolbar

---

## [0.79.0] — 2026-03-23

**Build 616.** Azure Blob integration, UI consistency overhaul, and reliability fixes.

### Added
- **Azure Blob Export — connection diagnostics modal** — displays SAS token parameters (version, services, permissions, expiry), endpoint URL, auth mode, and signature (truncated) for troubleshooting 403/400 errors
- **Azure Blob Export — toast notifications** — real-time success/failure feedback for auto-uploaded audit CSVs (previously logged silently)
- **Azure Blob Export — AzCopy stdout capture** — AzCopy v10 writes errors to stdout, not stderr; now captures both streams concurrently to surface error details
- **Azure Blob Export — directory-scoped SAS support** — removed `CreateIfNotExistsAsync` calls that fail with narrow SAS scopes; falls back from `GetPropertiesAsync` to blob listing
- **Azure Blob Export — container name slash splitting** — `raw/ready` auto-splits into container `raw` with blob prefix `ready/`
- **Settings page — 2-column layout** — small settings groups rendered in a CSS grid for better information density; wide sections (Cache, Azure Blob, Alerting) span full width below
- **Settings page — Azure Blob diagnostics button** — one-click access to the connection diagnostics modal from the Azure Blob settings group
- **SQL Auth error hint** — Servers and Settings pages now show a descriptive hint when SQL Server is configured for Windows-only auth mode
- **Guide page — Alerting & Service Management cards** — new navigation cards for the Alerting Config and Service & Updates pages

### Changed
- **AlertingConfig page** — converted from Tailwind CSS to app's native CSS design system (`.settings-page`, `.settings-group`, `.settings-field`, `.settings-btn`, `.severity-badge`)
- **ServiceManagement page** — converted from Tailwind CSS to app's native CSS design system with CSS variable–based status indicators
- **`MaxCacheSizeBytes` → `MaxCacheSizeMB`** — config key renamed across `appsettings.Production.json`, `appsettings.Development.json`, `CacheEvictionService`, and `CachingQueryExecutor`; values now specified in megabytes
- **Azure SDK container client** — changed from `new BlobContainerClient(sasUri)` to `BlobServiceClient` approach, fixing `InvalidUri` (400) errors
- **AzCopy error capture** — concurrent stdout/stderr reads to prevent process deadlock
- **Startup zoom** — default zoom level changed from 200% to 150% in `user-settings.json`
- **About page** — added Azure Blob Export feature card; updated User Experience section
- **Removed ExportData page** — standalone Azure export page was redundant with Settings auto-upload; removed from nav and codebase

### Fixed
- **Full Audit progress bar** — fixed calculation that showed 200% with single-server runs (1-based index math); clamped to `Math.Min(pct, 100)`
- **Doubled blob name** — `UploadLocalCsvAsync` no longer re-decorates filenames through `BuildBlobName`; prepends blob prefix directly
- **IAsyncDisposable crash on exit** — `App.xaml.cs` now tries `DisposeAsync()` before falling back to `IDisposable.Dispose()`, fixing `ServerModeService` shutdown error
- **Edit server "already exists"** — duplicate-name check moved into the add-new branch only, unblocking edits of existing servers
- **Azure Blob hostname normalization** — strips `.blob.core.windows.net` suffix from account name input

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

## [0.5.0] — 2025-01-20

**WPF shell with embedded SQL scripts and a proper connection manager.**

The PowerShell wrapper was outgrown. Moved to a proper .NET 8 WPF application hosting a simple WinForms-style
grid. SQL scripts are compiled as embedded resources. First version where a non-DBA could plausibly run it.

### Added
- WPF window with DataGrid result display
- Hardcoded set of embedded SQL health scripts (blocking, top CPU, missing indexes, wait stats)
- Server picker dropdown — remembers last used connection in `appsettings.json`
- "Run All" button executes each script in sequence and tabs the results
- Copy-to-clipboard on grid selection
- Basic error surface: red status bar on SQL connection failure

### Changed
- Connection credentials moved out of the script body into app config
- Scripts no longer need editing before running — server name is entered once in the UI

---

## [0.4.0] — 2024-08-10

**Dynamic script folders — drop a `.sql` file in, it appears in the menu.**

Scripts outgrew the single file. Introduced a `scripts/` folder convention: any `.sql` file placed there
shows up in the PowerShell menu automatically. Categories driven by filename prefix (`blocking_`, `memory_`, etc.).
Shared by the team via a network share for the first time.

### Added
- Script discovery from a configurable `$ScriptsFolder` path (local or UNC)
- Category grouping by filename prefix in the menu
- `--server` and `--database` CLI parameters so scripts can be called from SSMS external tools
- Basic HTML report export (`Export-HtmlReport`) — opens in the default browser
- `config.json` for connection defaults (replaces hardcoded variables at top of script)

### Changed
- Parameterised all scripts — `$ServerName` / `$DatabaseName` injected at runtime, no per-script editing required
- Output now shows elapsed time per script

---

## [0.3.0] — 2023-05-14

**PowerShell rewrite — menus, colours, and shareable scripts.**

The `.sql`-in-a-bat file approach hit its ceiling. Rewrote as a PowerShell `.ps1` leveraging
`Invoke-Sqlcmd`. First version shared with colleagues; ran from a USB stick at client sites.

### Added
- Interactive numbered menu: choose a health check to run
- Colour-coded output (`Write-Host` with `-ForegroundColor`) — red for blocking, yellow for warnings
- `Test-Connection` pre-check before attempting SQL queries
- Blocking chain output with lead-blocker identification
- Top-10 queries by CPU, by reads, by duration
- Wait statistics summary with category grouping
- Missing index recommendations from `sys.dm_db_missing_index_details`
- `.\healthcheck.ps1 -Server MYSERVER -Quiet` for unattended CSV output

### Changed
- Moved from `sqlcmd.exe` subprocess calls to native `Invoke-Sqlcmd`
- All queries parameterised — no string concatenation with server/db names

---

## [0.2.0] — 2021-11-03

**Batch file upgraded — multiple queries, basic formatting, `sqlcmd` modes.**

Still a `.bat` / `.sql` pair, but grown past a single query. Introduced `sqlcmd` variables so the
server name could be passed on the command line. Shared internally as an attachment to a wiki page.

### Added
- Five separate named queries: blocking sessions, top CPU, long-running queries, missing indexes, last backup status
- `sqlcmd -v Server=$(SERVER)` variable substitution — server name no longer hardcoded in the file
- `-W` flag strips trailing whitespace from output
- `-s","` CSV mode option for piping results into Excel
- Simple separator headers between query sections (`--- BLOCKING ---`, `--- TOP CPU ---`)

### Changed
- Moved from a single monolithic query to separate named sections
- Output redirected to timestamped log file (`results_YYYYMMDD_HHMMSS.txt`) in addition to console

---

## [0.1.0] — 2019-06-01

**In the beginning there was a `.sql` file and a batch script to run it.**

A single `health_check.sql` dropped in a shared folder, wrapped in `run.bat` that called `sqlcmd.exe`.
Ran on one server. Results printed to the console window and vanished when you closed it. The server
name was hardcoded on line 3 of the batch file. It worked. It was enough. For a while.

These were never trivial scripts. From day one the queries drew on `sys.dm_exec_requests`,
`sys.dm_os_wait_stats`, `sys.dm_db_missing_index_details`, `sys.dm_exec_query_stats`, and the
full sp_Blitz and sp_triage community scripts. A thousand lines of SQL that had been refined through
years of client engagements — the friction was never the diagnostics, it was the ceremony of running them.

### Added
- `health_check.sql` — ~1 200 lines covering: blocking chains (lead blocker + waiters), top-25 queries by CPU / logical reads / duration, wait stats with category mapping, missing index recommendations with impact score, unused index candidates, last backup per database (full + log), VLF counts, auto-growth events, database file free space, SQL Agent job failures (last 24 h), and a server configuration review (MAXDOP, cost threshold, memory settings, instant file initialisation)
- `sp_triage.sql` and `sp_Blitz.sql` — community scripts included verbatim; run automatically as part of the full-check sequence
- `run.bat` — calls `sqlcmd.exe -S MYSERVER -d master -i health_check.sql -o results_<timestamp>.txt`; server name set once at the top of the file
- Output to both console and a timestamped results file so findings survived closing the window

---



