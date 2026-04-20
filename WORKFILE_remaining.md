# LiveMonitor — Remaining Work

**Repo:** SQLAdrian/SqlHealthAssessment  
**Current version:** 0.85.2 (build 1177)  
**Stack:** Blazor Hybrid WPF (.NET 8, net8.0-windows), single-exe, SQLite cache, Serilog  
**Key paths:** `Pages/` (37 .razor pages), `Data/Services/`, `Components/Shared/`, `Data/Caching/`  
**Do not touch:** SQL queries (user owns SQL), `.claude/docs/` for CSS patterns, `app.css` (7500 lines — grep don't read)

---

## DONE THIS SESSION (do not re-do)

- Maintenance mode global banner in MainLayout
- Live Monitor session prefetch on connection
- Chart data point cap slider in Settings
- Credential export/import (.lmcreds with PBKDF2 passphrase)
- Proxy-aware auto-update + detailed error messages
- Release workflow fix (increment-build.ps1 no longer pushes tags)
- GitHub Pages fully populated (badges, download cards, 16 screenshots, demo.gif)
- RBAC UI wiring (Settings → Access Control, Login page, page guards)
- DPI-aware manifest (PerMonitorV2)
- QUICKSTART.md
- Check Validator search + category chips

---

## REMAINING ITEMS

### PRIORITY 1 — Quick wins, self-contained

---

#### 1. Background refresh thread + "Refresh now" spinner
**File:** `Pages/Sessions.razor`  
**Problem:** `LoadSessions()` sets `IsLoading = true` which blanks the whole table. Data fetch should happen on a background thread with the old data still visible; only swap the table when new data arrives.  
**How:**
1. Add `_backgroundSessions List<SessionInfo>` field alongside `AllSessions`
2. In `LoadSessions()`, remove `IsLoading = true` at the top. Fetch into a local `newSessions` variable
3. Only after fetch completes: `AllSessions = newSessions; LastRefreshTime = ...; await InvokeAsync(StateHasChanged)`
4. Add a spinner icon next to the refresh timestamp that shows while the fetch is in-flight:
   ```razor
   @if (_refreshing)
   {
       <i class="fa-solid fa-spinner fa-spin" style="font-size:11px;color:var(--text-secondary);"></i>
   }
   ```
5. Add `private bool _refreshing;` — set to true at start of `LoadSessions()`, false at end (in finally block)
6. The `_loadLock.Wait(0)` guard is already in place — don't remove it

**Result:** Table never flashes blank. Spinner shows in the toolbar. Users can keep scrolling during refresh.

---

#### 2. Rate-limit status bar badge
**Files:** `Data/RateLimiter.cs`, `Components/Layout/NavMenu.razor` (or `MainLayout.razor`)  
**Problem:** `RateLimiter` is defined but not registered in DI and not shown anywhere.  
**How:**
1. Register `RateLimiter` as singleton in `App.xaml.cs`:
   ```csharp
   services.AddSingleton<RateLimiter>();
   ```
2. In `QueryExecutor.cs` (or wherever queries run), inject `RateLimiter` and call `TryAcquire()` before executing
3. In `MainLayout.razor`, inject `RateLimiter` and add a small badge in the bottom-right corner:
   ```razor
   @if (_rateLimitActive)
   {
       <div style="position:fixed;bottom:8px;right:12px;z-index:1500;
                   background:#7c2d00;color:#ffb347;font-size:11px;
                   padding:3px 8px;border-radius:4px;font-family:monospace;">
           ⚠ throttled
       </div>
   }
   ```
4. Poll `RateLimiter.IsRateLimited` on a 5s timer in MainLayout (same pattern as maintenance banner timer)
5. Subscribe to `RateLimiter.RateLimitExceeded` event to show immediately when triggered

**Note:** `RateLimiter` has `IsRateLimited`, `CurrentQueryCount`, `MaxQueriesPerMinute`, `GetTimeToReset()`, `RateLimitExceeded` event. Check `Data/RateLimiter.cs` for the full API before wiring.

---

#### 3. Dashboard JSON schema validation — inline error + Reset to default
**Files:** `Pages/DashboardEditor.razor`, `Data/Services/DashboardConfigService.cs`  
**Problem:** If `dashboard-config.json` gets malformed, the app silently fails or throws. No inline validation in the editor.  
**How:**
1. In `DashboardConfigService`, add a `Validate(string json)` method that:
   - Tries `JsonSerializer.Deserialize<DashboardConfig>(json)`
   - Returns `(bool valid, string? error)`
2. In `DashboardEditor.razor`, on the JSON text area `@oninput`:
   - Call `Validate()` and show a red border + error message below the editor if invalid
   - Disable the Save button while JSON is invalid
3. Add a "Reset to default" button that calls a new `DashboardConfigService.ResetToDefault()` method — copies the embedded default config from `Config/dashboard-config.json` (ship a `dashboard-config.default.json` alongside it)
4. The default file path: `Config/dashboard-config.default.json` — copy this from the current `Config/dashboard-config.json` as the baseline

---

#### 4. PDF/Excel export for tabular audit results
**Files:** `Pages/FullAudit.razor`, `Pages/VulnerabilityAssessment.razor`, `Data/Services/PrintService.cs`  
**Problem:** PDF export exists via `PrintService` for some pages. Excel export is missing entirely.  
**How for Excel (no new NuGet required — use CSV as Excel-compatible output):**
1. Add a `ExportToExcelAsync(DataTable data, string fileName)` helper in a new `ExcelExportService.cs` or inline in the page
2. Use `CsvHelper` (already in project? check) or write a simple TSV/CSV writer
3. If `CsvHelper` is not present, use the existing `CsvParser` pattern — it already writes CSV
4. For true Excel (.xlsx): add `ClosedXML` NuGet (`dotnet add package ClosedXML`) — it's MIT, lightweight, no COM dependency
5. For PDF: `PrintService.PrintToPdfAsync()` already exists — wire it to the audit results table the same way it's wired on other pages
6. Add "Export CSV" and "Export PDF" buttons to `FullAudit.razor` and `VulnerabilityAssessment.razor` toolbar

**CsvHelper check:** `grep -r "CsvHelper\|CsvParser" Data/` to see what's already there.

---

### PRIORITY 2 — Medium effort

---

#### 5. FAQ expansion + Support channel link
**Files:** `QUICKSTART.md`, `docs/index.md`, any in-app Help page  
**Items to add to FAQ:**
- Alert threshold tuning: "Why is my alert firing constantly?" → explain IQR baseline, how to adjust `NextAlertDelayMinutes`, how to use dry-run mode
- SQLWATCH is optional: many pages work without it; only historical dashboards need it
- Service mode setup: step-by-step, port 5000 default, Windows Service install via installer
- Credential migration: explain the new `.lmcreds` export in Settings → Server Credentials
- Debug log location: `logs/app-YYYYMMDD.log` next to the exe

**Support channel:**
1. Enable GitHub Discussions on the repo (Settings → Features → Discussions)
2. Add a "Get Help" link in the nav or About page pointing to `https://github.com/SQLAdrian/SqlHealthAssessment/discussions`
3. Optional: add a "Report a bug" button in the About page that opens `https://github.com/SQLAdrian/SqlHealthAssessment/issues/new?template=bug_report.md`

---

#### 6. Query Plan Viewer — ServerVersion/ServerEdition probe
**File:** `Pages/QueryPlanViewer.razor` (or wherever the plan modal is shown), `Components/Shared/QueryPlanModal.razor`  
**Problem:** `ServerVersion` and `ServerEdition` parameters are not populated at runtime, so the ONLINE/RESUMABLE index rebuild checkboxes are always enabled regardless of SQL Server version.  
**How:**
1. On page load, run: `SELECT SERVERPROPERTY('ProductVersion'), SERVERPROPERTY('EngineEdition')`
2. Parse major version (e.g. `15` = SQL 2019, `16` = SQL 2022)
3. Disable ONLINE checkbox if version < 9 (SQL 2005) or edition = Express (EngineEdition = 4)
4. Disable RESUMABLE checkbox if version < 14 (SQL 2017)
5. The `ConnectionHealthService.IsAzureSql(serverName)` method already exists — use it to detect Azure SQL and disable relevant options

---

#### 7. draw.io / SVG export of Environment View 
**File:** `Pages/EnvironmentView.razor` (or similar), the force-directed graph JS  
**Problem:** The topology graph is rendered in a `<canvas>` or SVG via D3/custom JS. Users want to export it as a draw.io-compatible XML or plain SVG for documentation.  
**How:**
1. Check how the graph is rendered: `grep -n "canvas\|svg\|d3\|topology\|force" Pages/EnvironmentView.razor`
2. If SVG: add a JS function `exportTopologySvg()` that serialises the SVG element to a string and triggers a download via `blazorDownloadFile` (already in `wwwroot/scripts/download.js`)
3. If Canvas: use `canvas.toDataURL('image/png')` for a PNG export, or redraw onto a hidden SVG
4. For draw.io XML: the format is straightforward — each server = `<mxCell>` with `style="shape=..."`, edges = connection cells. Generate from the same data model used to render the graph

---

#### 8. SQL Server CPU & Latency Benchmark (Item 15) - FOR NEXT PHASE. SKIP FOR NOW
**Context:** Initial SQL at `C:/temp/proc_stats_enriched.sql`  
**File to create:** `Data/Services/BenchmarkService.cs`, new page `Pages/Benchmark.razor`  
**How:**
1. The SQL benchmark runs inside SQL Server — it's safe read-only DMV queries plus arithmetic
2. Add the benchmark queries to `Data/Services/BenchmarkService.cs` with a `RunBenchmarkAsync(string serverName)` method
3. Results go into the SQLite cache via `liveQueriesCacheStore.UpsertStatValueAsync()`
4. New page at `/benchmark` with a "Run benchmark" button per server and a comparison table
5. Add scheduler delay + signal wait queries (already written in the worklist memory) to detect vCPU steal
6. Ratings: use these rough baselines (adjust after real-world testing):
   - Integer arithmetic: < 100ms = fast, 100–500ms = normal, > 500ms = degraded
   - String ops: < 200ms = fast, > 1s = degraded
   - Signal wait pct > 25% = likely hypervisor contention

---

### PRIORITY 3 — Larger sessions

---

#### 9. Documentation Generator + Installation Helper  - FOR NEXT PHASE. SKIP FOR NOW
**Status:** Design phase — user has docx templates as reference  
**Planned pages:** `/documentation` (generate SQL Server state docs from live DMV data), `/installation-helper` (guided hardening steps)  
**Approach:**
1. Start by looking at the docx templates to understand what data is needed
2. The app already collects most of this: instance config (sp_configure), disk/memory, AG status, backup history, security findings from VA
3. Documentation page: assemble these into a structured view with export to PDF via PrintService
4. Installation Helper: a wizard-style page that checks current config against best practices and gives a checklist
5. dbatools.io integration: dbatools has a REST API — use HttpClient to call it for additional checks. The `AutoUpdateService._httpClient` pattern is the model.

---

#### 10. Code Signing (Item 20) — USER ACTION REQUIRED  - FOR NEXT PHASE. SKIP FOR NOW
**Status:** Workflow is written and waiting. User needs to buy the cert.  
**Steps:**
1. Buy Certum OV cert at certum.eu (~$60/yr, individual validation, 1–3 days)
2. Install cert, export `.pfx` from `certlm.msc → Personal → Certificates → right-click → Export → PKCS#12`
3. Encode and add to GitHub Secrets:
   ```powershell
   [Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx")) | clip
   ```
   - Secret name: `CODESIGN_CERT_BASE64`
   - Secret name: `CODESIGN_CERT_PASSWORD`
4. Trigger a release: `git tag v0.85.3 && git push origin v0.85.3`
5. The `.github/workflows/release.yml` handles everything else automatically

---

#### 11. Public release posting plan
**When ready** (after screenshots/GIF and code signing):  
- **r/SQLServer**: title "I built a free SQL Server monitoring tool for Windows DBAs — feedback welcome"
- **r/sysadmin**: focus on the Windows Service mode + alerting + no-agent angle
- **SQLServerCentral.com**: submit an article, not just a link — they prefer content
- **dev.to**: technical post about the Blazor Hybrid WPF architecture (unusual stack, good engagement)
- **Hacker News Show HN**: brief, technical, honest about current state

---

## ARCHITECTURE NOTES FOR OTHER LLMS

### Project structure
```
SqlHealthAssessment.sln
├── App.xaml.cs               ← DI registration, startup, error handling
├── MainWindow.xaml.cs        ← WPF shell, BlazorWebView host
├── Pages/*.razor             ← @page routes (37 pages)
├── Components/Shared/*.razor ← Reusable components
├── Components/Layout/
│   ├── MainLayout.razor      ← App shell, banners, router
│   └── NavMenu.razor         ← Sidebar navigation
├── Data/
│   ├── ConnectionManager.cs  ← ServerConnectionManager
│   ├── UserSettingsService.cs ← All user prefs (user-settings.json)
│   ├── AutoUpdateService.cs  ← Update check, download, apply
│   ├── CredentialPorter.cs   ← Export/import credentials
│   ├── SessionDataService.cs ← Live sessions DMV queries
│   ├── Caching/
│   │   └── SqliteCacheStore.cs ← SQLite WAL cache
│   └── Services/
│       ├── AlertEvaluationService.cs
│       ├── ConnectionHealthService.cs
│       ├── NotificationChannelService.cs
│       ├── RbacService.cs
│       ├── PrintService.cs
│       └── ... (20 services total)
├── Config/
│   ├── version.json          ← { version, buildNumber, buildDate, whatsnew[] }
│   ├── dashboard-config.json ← Panel layout
│   └── appsettings.json
└── wwwroot/
    └── scripts/
        ├── download.js       ← blazorDownloadFile() helper
        └── app.js
```

### Key conventions
- C# files: `/* In the name of God, the Merciful, the Compassionate */` header
- Razor files: `<!--/* In the name of God, the Merciful, the Compassionate */-->` header
- DI: nullable optional params `Service? svc = null` — services may not be available
- Background tasks: `_ = Task.Run(async () => { ... })` pattern
- Credentials: always `CredentialProtector.Encrypt/Decrypt` — never store plaintext
- Connections: always specify database (`"master"` for DMV queries, not default)
- No Tailwind — uses CSS variable design system (`var(--accent)`, `var(--bg-secondary)`, etc.)
- No `<` in Razor `@code` switch statements — Razor reads it as HTML; use if/else
- Do not write SQL queries — user owns all SQL

### Build
```bash
dotnet build SqlHealthAssessment.sln
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
./increment-build.ps1   # bumps Config/version.json buildNumber only, no tags
# To release: git tag v0.85.3 && git push origin v0.85.3
```

### CSS design tokens (key ones)
```css
var(--accent)          /* green #00ff00 */
var(--bg-secondary)    /* dark panel background */
var(--bg-hover)        /* slightly lighter hover */
var(--border)          /* border color */
var(--text-secondary)  /* muted text */
var(--green)           /* success */
var(--red)             /* error */
var(--orange)          /* warning */
var(--blue)            /* info/link */
```

### MainLayout banner pattern (reference implementation)
Two banners already exist in `Components/Layout/MainLayout.razor`:
1. Update available — green accent, `position:fixed;top:0`
2. Maintenance mode — amber `#7c5a00`, stacks below update banner

Pattern for adding a new banner:
```razor
@if (_conditionActive)
{
    <div style="position:fixed;top:0;left:0;right:0;z-index:2000;
                background:COLOR;color:TEXT_COLOR;
                display:flex;align-items:center;justify-content:center;gap:12px;
                padding:7px 16px;font-size:13px;box-shadow:0 2px 6px rgba(0,0,0,0.3);">
        <i class="fa-solid fa-ICON"></i>
        <span>MESSAGE</span>
        <a href="/target-page" style="color:TEXT_COLOR;font-weight:600;text-decoration:underline;">Action →</a>
    </div>
    <div style="height:36px;"></div>
}
```
Timer polling pattern (30s):
```csharp
_timer = new System.Timers.Timer(30_000);
_timer.Elapsed += (_, _) => _ = InvokeAsync(() => { RefreshState(); StateHasChanged(); });
_timer.AutoReset = true;
_timer.Start();
// In Dispose(): _timer?.Stop(); _timer?.Dispose();
```

### Settings page pattern
All settings follow the same structure. To add a new setting:
1. Add property to `UserSettings` class in `Data/UserSettingsService.cs`
2. Add `Get/Set` methods to `UserSettingsService`
3. Add field + load in `LoadSettings()` in `Pages/Settings.razor`
4. Add UI in the appropriate `<div class="settings-group">` section

### Alert evaluation gate
`AlertEvaluationService` already checks `AlertWindowConfig.ShouldFire(alert.AlwaysAlert)` at line ~170.
`NotificationChannelService.GetAlertWindows()` returns the current config.
`NotificationChannelService.UpdateAlertWindows(config)` saves changes.
Manual maintenance: set `config.MaintenanceActiveUntil = DateTime.Now.AddMinutes(N)` then call `UpdateAlertWindows`.

---

## WHAT GOOD OUTPUT LOOKS LIKE

For each coding task above:
1. Read the file(s) mentioned before editing
2. Use `grep` / `Glob` to verify class/method names before referencing them
3. Build after changes: `dotnet build SqlHealthAssessment.sln -c Release --no-restore`
4. Fix all errors before committing
5. Commit with: `git commit -m "feat/fix: description\n\nCo-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>"`
6. Do NOT push unless explicitly asked
7. Do NOT create new .md documentation files unless asked
8. Do NOT add features beyond what is described

---

## New Tasks from Grok Audits (SQLTriage Revival)

### Repo Hygiene (Today)
- Move all "scar" files (MEMORY_LEAK_STATUS.md, FOOTPRINT_REDUCTION_GUIDE.md, LOG_ISSUES_ANALYSIS.md, DACPAC_REMOVAL_SUMMARY.md, REFACTORING_RECOMMENDATIONS.md, UI_MODERNIZATION_PLAN.md, PROJECT_GAP_ANALYSIS.md) to `.ignore/ROASTME/` or `/docs/internal/`.
- Update project/solution names from "SqlHealthAssessment" to "SQLTriage" consistently in .csproj, .sln, and code.

### README Rewrite (Today)
- Replace README.md with polished version: Hero section with badges, embedded screenshots grid (16 images), complete comparison table (add dbwatch column), Loom demo link, remove self-references.
- Upload screenshots to `/screenshots/` folder and embed in README.

### Marketing Boost (This Week)
- Add repo topics: sql-server-monitoring, dba-tools, performance-tuning, blazor-wpf, open-source-sql.
- Record 60-90s Loom demo of interactive plan viewer V2 + blocking kill + Quick Check.
- Add badges: .NET 8 • Blazor • No Agent • GPL-3.0.

### Feature Additions (v0.86 Prep)
- **Scheduled Maintenance Operations**: Add `Pages/MaintenanceScheduler.razor` for cron jobs (index rebuild, stats update, CHECKDB, backups) targeting connected servers. Use Quartz.NET for scheduling.
- **Management Templates**: Add `Pages/ManagementTemplates.razor` with predefined templates (performance, security, maintenance) as JSON/YAML in `templates/`. Allow apply/customize/export.

### Positioning Shift
- Stop "lightweight alternative" — position as "Portable, no-agent desktop monitoring weapon for DBAs and consultants — single exe, service mode, real interactive plans."

### Critical Strategic Gaps (From Feedback)
1. **Automated Maintenance Execution** (HIGH): Add MaintenanceJobService with template library for backups, index rebuilds, DBCC, integrity checks. Prioritization: HIGH. Effort: 2-3 weeks.
2. **Historical Performance Repository** (HIGH): Extend SQLite for aggregated metrics, time-series rollups (hourly/daily summaries). Prioritization: HIGH. Effort: 1-2 weeks.
3. **Compliance Framework** (MEDIUM-HIGH): Map VA checks to CIS/PCI/NIST; add compliance score dashboard; PDF reports. Prioritization: MEDIUM-HIGH. Effort: 3-4 weeks.
4. **Threshold-Based Alerting** (MEDIUM): Add threshold config to UserSettings; replace IQR with configured limits. Prioritization: MEDIUM. Effort: 1 week.
5. **Multi-Tenant / MSP Features** (LOW-MEDIUM): Add environment abstraction, template deployment, multi-server rollup. Prioritization: LOW-MEDIUM. Effort: 4-6 weeks.
6. **Advanced Blocking Analysis** (MEDIUM): Store blocking events; timeline view; top blockers report. Prioritization: MEDIUM. Effort: 2 weeks.
7. **Health Score & Risk Rating** (MEDIUM): Compute weighted health index; trend arrow; executive summary. Prioritization: MEDIUM. Effort: 1 week.
8. **Scheduled & Automated Reporting** (MEDIUM): Add report scheduler; SMTP; subscriptions. Prioritization: MEDIUM. Effort: 1-2 weeks.
9. **Configuration Drift Detection** (MEDIUM-LOW): Store sp_configure snapshots; diff view; alerts. Prioritization: MEDIUM-LOW. Effort: 2 weeks.
10. **Performance Benchmarks & Baselines** (LOW-MEDIUM): Learn typical values; z-score anomalies. Prioritization: LOW-MEDIUM. Effort: 2-3 weeks.

### Implementation Roadmap
- **Phase 1 (2-3 months)**: Maintenance automation, historical metrics, threshold alerts, reporting, health score.
- **Phase 2 (3-4 months)**: Compliance, drift detection, advanced blocking, service mode.
- **Phase 3 (4-6 months)**: Multi-tenant, benchmarks, predictions.

Files affected: README.md, WORKFILE_remaining.md (this file), new Pages/, templates/, .csproj, .sln.
Commit pattern: `git commit -m "feat: description\n\nCo-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>"`
