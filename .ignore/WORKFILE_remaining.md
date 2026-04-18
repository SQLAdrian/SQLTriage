<!-- In the name of God, the Merciful, the Compassionate -->
<!-- Bismillah ar-Rahman ar-Raheem -->

# SQLTriage — Remaining Work

**Repo:** SQLAdrian/SqlHealthAssessment  
**Current version:** 0.85.2 (build 1177)  
**Target brand:** SQLTriage — Lightweight SQL Server Monitoring & Governance Tool  
**Stack:** Blazor Hybrid WPF (.NET 8, net8.0-windows), single-exe, SQLite cache, Serilog  
**Key paths:** `Pages/` (37 .razor pages), `Data/Services/`, `Components/Shared/`, `Data/Caching/`  
**Do not touch:** SQL queries (user owns SQL), `.claude/docs/` for CSS patterns, `app.css` (7500 lines — grep don't read)

---

## 🕌 Codebase Signature: Basmalah on Every File

**Non-negotiable requirement:** Every single code file in this project must begin with the basmalah header. This is the project's **intent lock** — it declares that this work is an act of charity (sadaqah jariyah), built for the pleasure of Allah, not for personal gain or exploitation.

### Header format per file type:

**.cs files (C#):**
```csharp
/* In the name of God, the Merciful, the Compassionate */
// Bismillah ar-Rahman ar-Raheem
```

**.razor files (Blazor):**
```razor
<!--/* In the name of God, the Merciful, the Compassionate */-->
<!-- Bismillah ar-Rahman ar-Raheem -->
```

**.css/JS/HTML/JSON/MD files:**
```css
/* In the name of God, the Merciful, the Compassionate */
/* Bismillah ar-Rahman ar-Raheem */
```

**.config files (.json, .csproj, .sln):**
Add as first comment line (where supported) OR include in commit message if file cannot have comments.

**Why this matters:**
- It's not decorative — it's the **license plate** of the project. Every file carries the intent.
- It prevents "mission drift" — when you're tired, you see the header and remember **why** you're building this.
- It's a **barakah** (blessing) invocation — asking for divine guidance in a complex technical undertaking.
- It signals to contributors: this is not a profit-driven tool. It's a gift. If you don't respect that, don't contribute.

**Enforcement:**
- Add a pre-commit hook (`.git/hooks/pre-commit`) that greps for basmalah in staged files
- If missing, reject commit with message: "Basmalah required — this is a sadaqah, not a product."
- Alternatively, add to CI pipeline: check for header presence, fail build if absent (with豁免 for binary files)

**Do not remove this requirement** under any circumstances. The project loses its soul without it.

---

## 📚 Related Documents

This worklist is part of a 4-document set:

| Document | Purpose | Path |
|----------|---------|------|
| **Strategic Blueprint** | Market positioning, governance narrative, executive messaging | `.ignore/SQLTriage_Strategic_Blueprint.md` |
| **Product Requirements** | Detailed specs: Governance Dashboard, Report Export, Error Standards, DB schema | `.ignore/SQLTriage_PRD.md` |
| **Release Checklist** | 200+ validation items across 7 phases, gates, beta plan | `.ignore/SQLTriage_Release_Checklist.md` |
| **Task Worklist** *(this file)* | Implementation tasks with files, steps, priorities | `WORKFILE_remaining.md` |

**Read order:** Blueprint → PRD → Worklist → Checklist (strategy → specs → execution → validation)

---

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

### 🔴 CRITICAL BLOCKERS — Must fix before public release
---

#### 1. Fix build system blockers
**Files:** `lib/PerformanceStudio/` (submodule), entire `.csproj`
**Problems:**
- PerformanceStudio submodule checkout fails in CI/build
- 200+ CA1416 Windows-only API warnings
- Null reference warnings throughout
**How:**
1. Submodule: Reset to known good commit, pin hash, remove broken auto-update from build target
2. CA1416: Add `[SupportedOSPlatform("windows")]` to Program.cs, or suppress in csproj with `<NoWarn>CA1416</NoWarn>`
3. Null safety: Enable nullable reference types, fix CS8602/CS8604 warnings
**Why:** Build must be clean for any serious adoption

---

#### 2. Brand unification — rename to SQLTriage
**Files:** Everywhere — `.sln`, `.csproj`, `App.xaml`, `MainWindow.xaml`, all `.razor`, `README.md`, website, GitHub settings
**Problem:** Website/external branding says "SQLTriage", codebase says "SqlHealthAssessment"; need unified "SQLTriage" brand
**How:**
1. **Rename solution/project:** `SQLTriage.sln` → `SQLTriage.sln`
2. **Rename csproj:** output assembly and root namespace to `SQLTriage`
3. **Replace all hardcoded** "SqlHealthAssessment" / "SQLTriage" strings in UI/pages with "SQLTriage"
4. **Update README** title, badges, navigation references
5. **Update website** (`docs/index.md`) to use SQLTriage throughout
6. **Update release asset names**, installer names, shortcuts
7. **Update app branding:** title bar, about page, favicon
**Why:** Single consistent brand is essential for recognition and trust
**Target brand:** "SQLTriage — Lightweight SQL Server Monitoring & Governance Tool"
**Tagline hierarchy:**
- H1: SQLTriage
- H2: Reduce audit preparation from days to minutes
- Body: No installation. No agents. Safe for production.

---

#### 3. Version bump to actual v1.0.0
**Files:** `Config/version.json`, `README.md` header, `App.xaml.cs` title, about page
**Problem:** README claims "v1.0 (2026)" but code is 0.85.2 build 1177
**How:**
1. Set `"version": "1.0.0"` in `Config/version.json`
2. Reset `buildNumber` to 0 or keep sequential? Decision needed
3. Update title bar string in `MainWindow.xaml.cs`
4. Update About page (`Pages/About.razor`) version display
5. Update README badge from `v0.85.x` to `v1.0.0`
**Why:** Claims version 1.0 while running 0.85 is deceptive

---

#### 4. Add missing screenshots to repository
**Files:** Create `/docs/screenshots/`, link from website if needed
**Problem:** Website shows 16 screenshots; repo `screenshots/` directory is empty
**How:**
1. Create current UI screenshots matching website captions:
   - 1-addserver.jpg
   - 2-whats-new.jpg
   - 3-wait-state.jpg
   - 4-live-sessions.jpg
   - 5-database-health.jpg
   - 6-instance-overview.jpg
   - 7-environment-map.jpg
   - 8-servers-easily-monitor-50-instances-or-more.jpg
   - 9-run-full-sql-audits.jpg
   - 10-microsoft-sql-vulnerability-assessment.jpg
   - 11-upload-results-securely-to-azure-blob-storage.jpg
   - 12-multiple-notification-channels.jpg
   - 13-maturity-roadmap.jpg
   - 14-alerting.jpg
   - 15-query-plan-viewer.jpg
   - 16-risk-register-compliance-mapping-ISO-SOC2-NIST.jpg
2. Add demo.gif to `/docs/` (already exists per WORKFILE, verify path)
3. Commit screenshots; update `docs/index.md` image paths if needed
**Why:** Missing screenshots makes project look abandoned/vaporware

---

#### 5. Documentation path fixes and placeholder cleanup
**Files:** `README.md`, `DEPLOYMENT_GUIDE.md`, any `*.md` with "coming soon"
**Problems:**
- README references `appsettings.json` at root; actual path is `Config/appsettings.json`
- "Screenshots coming soon" placeholder still present
- Deployment guide mentions MSI installer that does not exist
**How:**
1. Grep all markdown for `appsettings.json` and fix paths
2. Grep all markdown for "coming soon" and replace with actual state or remove
3. Grep all markdown for "MSI" and either create MSI (see next) or remove mention
4. Verify all internal links (e.g. `[DEPLOYMENT_GUIDE.md](DEPLOYMENT_GUIDE.md)`) resolve correctly
**Why:** Wrong documentation causes immediate user frustration

---

### PRIORITY 1 — Quick wins, self-contained

---

#### 6. Background refresh thread + "Refresh now" spinner
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

#### 7. Rate-limit status bar badge
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

#### 8. Dashboard JSON schema validation — inline error + Reset to default
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

#### 9. PDF/Excel export for tabular audit results
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

### PRIORITY 1 — Quick wins, self-contained

---

### PRIORITY 1.5 — Installer & deployment (high effort, blocking release)
---

#### 10. Build MSI installer with silent install
**Tool:** WiX 4 or Advanced Installer
**Problem:** Only ZIP distribution exists; enterprise expects MSI with Add/Remove Programs
**How:**
1. Create WiX project that packages published output
2. Add silent install: `msiexec /i SQLTriage.msi /quiet /qn`
3. Detect prerequisites: WebView2, .NET 8 Desktop Runtime; offer to install
4. Register Windows Service properly if user selects service mode
5. Add upgrade logic (detect previous version, uninstall old, install new)
6. Sign MSI with code signing certificate (defer to Item 32 if cert not ready)
**Output:** `SQLTriage-v1.0.0-win-x64.msi`

---

#### 11. Clarify WebView2/server mode fallback in all documentation
**Files:** `README.md`, `DEPLOYMENT_GUIDE.md`, `QUICKSTART.md`, `Components/Layout/MainLayout.razor`
**Problem:** Documentation doesn't explain WebView2 is OPTIONAL — server mode (browser UI) is the planned fallback, not an error
**How:**
1. Add "No WebView2? No problem" section in README: explains automatic Kestrel fallback with browser
2. Update QUICKSTART to show both paths: WebView2 UI vs Server Mode (browser)
3. Add inline banner in `MainLayout.razor` when running in server mode: "Running in Server Mode — WebView2 not detected"
4. Explicitly state: "This app runs in a web browser by default if WebView2 is unavailable" — it's a feature, not a bug
**Why:** Users think WebView2 is a blocker; it's actually graceful degradation
**Note:** Do NOT bundle WebView2 — server mode is the intended primary deployment for servers

---

#### 12. Code signing and SmartScreen compliance (deferrable to v1.0.1)
**Status:** Workflow exists (see Item 32), user action required to buy cert
**How once cert obtained:**
1. User exports PFX from `certlm.msc`
2. Adds `CODESIGN_CERT_BASE64` and `CODESIGN_CERT_PASSWORD` to GitHub Secrets
3. Release workflow signs all binaries and zips automatically
4. Verify: `signtool verify /pa SQLTriage.exe`
**Impact:** Without signing, Windows SmartScreen shows "Unknown publisher" warnings. Not a blocker for v1.0 if documented.

---

### PRIORITY 2 — Medium effort

---

#### 13. FAQ expansion + Support channel link
**Files:** `QUICKSTART.md`, `docs/index.md`, any in-app Help page
**Items to add to FAQ:**
- Alert threshold tuning: "Why is my alert firing constantly?" → explain IQR baseline, how to adjust `NextAlertDelayMinutes`, how to use dry-run mode
- SQLWATCH is optional: many pages work without it; only historical dashboards need it
- Service mode setup: step-by-step, port 5000 default, Windows Service install via installer
- Credential migration: explain the new `.lmcreds` export in Settings → Server Credentials
- Debug log location: `logs/app-YYYYMMDD.log` next to the exe

**Support channel:**
1. Enable GitHub Discussions on the repo (Settings → Features → Discussions)
2. Add a "Get Help" link in the nav or About page pointing to GitHub Discussions URL
3. Optional: add a "Report a bug" button in the About page

---

#### 14. Query Plan Viewer — ServerVersion/ServerEdition probe
**File:** `Pages/QueryPlanViewer.razor`, `Components/Shared/QueryPlanModal.razor`
**Problem:** `ServerVersion` and `ServerEdition` parameters not populated; ONLINE/RESUMABLE checkboxes always enabled regardless of SQL Server version
**How:**
1. On page load, run: `SELECT SERVERPROPERTY('ProductVersion'), SERVERPROPERTY('EngineEdition')`
2. Parse major version (e.g. `15` = SQL 2019, `16` = SQL 2022)
3. Disable ONLINE checkbox if version < 9 (SQL 2005) or edition = Express (EngineEdition = 4)
4. Disable RESUMABLE checkbox if version < 14 (SQL 2017)
5. Use `ConnectionHealthService.IsAzureSql(serverName)` to detect Azure SQL and disable relevant options

---

#### 15. draw.io / SVG export of Environment View
**File:** `Pages/EnvironmentView.razor` (or EnvironmentMap.razor)
**Problem:** Topology graph rendered in canvas/SVG via D3/custom JS; users want export for documentation
**How:**
1. Check rendering: `grep -n "canvas\|svg\|d3\|topology\|force" Pages/EnvironmentView.razor`
2. If SVG: add `exportTopologySvg()` that serializes SVG and triggers download via `blazorDownloadFile`
3. If Canvas: use `canvas.toDataURL('image/png')` for PNG export, or redraw onto hidden SVG
4. For draw.io XML: generate `mxCell` elements from same data model used to render graph

---

#### 16. SQL Server CPU & Latency Benchmark
**Context:** Initial SQL at `C:/temp/proc_stats_enriched.sql`
**Files:** Create `Data/Services/BenchmarkService.cs`, new page `Pages/Benchmark.razor`
**How:**
1. Benchmark queries are safe read-only DMVs plus arithmetic — run inside SQL Server
2. Add queries to `BenchmarkService.RunBenchmarkAsync(string serverName)`
3. Store results via `liveQueriesCacheStore.UpsertStatValueAsync()`
4. New page `/benchmark` with "Run benchmark" button per server and comparison table
5. Add scheduler delay + signal wait queries to detect vCPU steal
6. Ratings (baselines, adjust after real-world testing):
   - Integer arithmetic: <100ms fast, 100–500ms normal, >500ms degraded
   - String ops: <200ms fast, >1s degraded
   - Signal wait pct >25% = likely hypervisor contention

---

### PRIORITY 2.25 — Governance & Business Value (high impact, medium effort)
---

#### 17. Governance & Risk Assessment Dashboard
**Files:** New `Pages/Governance.razor`, new `GovernanceService.cs`
**Problem:** No executive-level summary showing risk posture and business impact for IT managers/CxOs
**How:**
1. Create `GovernanceService` that aggregates:
   - **Compliance score:** Configuration drift PASS/WARN/FAIL (aligned with SOC2/HIPAA/ISO 27001 best practices)
   - **Cost efficiency score:** CPU utilization analysis, licensing optimization opportunities
   - **Operational maturity percentage** (0-100 based on best-practice coverage)
   - **Business impact assessment:** Downtime exposure, audit readiness, cost optimization tier
2. Dashboard displays **Executive Snapshot** table:
   ```
   Overall Risk Level: MODERATE
   Operational Maturity: 81% (Hardened)
   Downtime Exposure: HIGH (backup validation gaps)
   Audit Readiness: PARTIAL (monitoring coverage incomplete)
   Cost Optimization: MEDIUM (CPU under-utilization detected)
   ```
3. Generate **Risk Register** with columns: Risk ID, Theme, Impact, Business Impact, Recommended Action
4. Link each finding back to specific checks in VA/health checks
**Why:** "Reduce audit preparation from days to minutes" — provides CxO-ready evidence for budget approvals
**Strategic value:** Transforms tool from DBA utility to enterprise governance platform

---

#### 18. Governance Report Export (PDF evidence generator)
**Files:** Extend `Data/Services/PrintService.cs`, new `Pages/GovernanceReport.razor`
**Problem:** Need structured audit evidence, not raw data; auditors want PASS/FAIL summary
**How:**
1. Build report with sections:
   - Executive Snapshot (Risk Level, Maturity Score, Business Impact)
   - Configuration Drift Summary (aligned with SOC2/HIPAA best practices — reframed as "aligned with" not "maps to")
   - Security Risks (excessive permissions, privileged accounts checklist)
   - Cost Efficiency Insights (CPU waste, licensing optimization opportunities)
   - Priority Action Plan (Top 10 fixes ranked by business ROI)
2. Export to branded PDF with SQLTriage header
3. Include **"Cost of Downtime"** calculator (user enters revenue/hr, tool estimates risk exposure)
4. Add **"Sample Governance Report"** download on website for sales enablement
**Why:** This PDF is the **budget trigger** — shows immediate ROI to decision-makers

---

#### 19. Error messages with business/risk context
**Files:** All exception catches in `App.xaml.cs`, `MainWindow.xaml.cs`, all page `.razor` files
**Problem:** Generic "An error occurred" messages; no connection to business risk
**How:**
1. Create `ErrorMessages.cs` with templates per error type
2. Each message includes:
   - **What happened** (clear, non-technical)
   - **Why it matters** (root cause in plain English)
   - **How to fix** (1-3 numbered steps, T-SQL if relevant)
   - **Governance impact:** e.g., "This affects Audit Readiness — Monitoring Activities control"
3. Add "Copy error details" button for logs
4. Log full exception always; show friendly message to user
**Example transformation:**
- Before: "SQL Agent not running"
- After: "SQL Agent is stopped → Alerts disabled → Compliance gap: Monitoring Activities control will FAIL audit"
**Why:** Connects operational issues to governance outcomes; increases perceived value

---

### PRIORITY 2.5 — Onboarding and UX improvements (high value, medium effort)
---

#### 20. First-run wizard → Instant Quick Check experience (triage-first UX)
**Files:** `Pages/Onboarding.razor`, `Pages/QuickCheck.razor` (or reuse existing), `Data/Services/QuickCheckService.cs`
**Problem:** Current model: user adds server → empty dashboard → must manually run checks. Wrong. **Triage-first UX:** user gets immediate value <60 seconds from EXE launch.
**How:**
1. **On EXE launch** (no servers configured): direct to `/onboarding` (auto-redirect)
2. **Step 1 — Auto-detect:** `SqlDataSourceEnumerator` scans network for SQL instances; also allow manual entry. Pre-populate server list.
3. **Step 2 — Connect & Quick Check (combined):**
   - Checkbox: "✓ Run Quick Check immediately after connecting" (default: **checked**)
   - After server added, connection automatically tested
   - Upon successful connect, navigate directly to `/quick-check` or `/dashboard?view=quickcheck`
   - Kick off parallel execution of 240+ checks (existing VA query set)
   - Show live progress: "Running checks... 142/240" with cancel button
4. **Step 3 — Results in <60 seconds:** Results page shows color-coded PASS/WARN/FAIL summary, grouped by category. Critical findings trigger toast notification.
5. ** THEN** offer: "Get ongoing monitoring? Deploy SQLWATCH for historical trends" (optional step)
6. Store `onboardingComplete=true` to skip next time
**Why:** "Triage first, monitoring second." User gets immediate audit evidence value before committing to platform. Matches real DBA workflow: "I need to know what's broken NOW, not set up monitoring."
**UX principle:** First action **must** produce visible result within 60 seconds. No configuration, no learning curve, no "dashboard is empty" confusion.

---

#### 21. Intelligent permission checker with actionable guidance
**Files:** New service `PermissionCheckService.cs`, UI in Onboarding or Server Add dialog
**Problem:** "Test Connection" fails with "Login failed" or "Permission denied" — no guidance
**How:**
1. On connection test failure, run lightweight permission probe queries:
   - `SELECT IS_SRVROLEMEMBER('sysadmin')` — shows if sysadmin
   - `SELECT HAS_PERMS_BY_NAME('master', 'DATABASE', 'VIEW SERVER STATE')`
   - `SELECT IS_MEMBER('db_owner')` on SQLWATCH DB if exists
2. Map missing perms to exact T-SQL:
   - `GRANT VIEW SERVER STATE TO [user]`
   - `GRANT VIEW DATABASE STATE TO [user]`
   - `ALTER ROLE db_owner ADD MEMBER [user]` (if SQLWATCH)
3. Show in UI: "Missing: VIEW SERVER STATE. Run this on the SQL Server:" with copy button
**Why:** Most common support question is "why can't I connect?"

---

#### 22. Remove all required manual JSON editing
**Files:** `Pages/Settings.razor`, `Data/UserSettingsService.cs`, `Data/Services/DashboardConfigService.cs`
**Problem:** Some settings still require editing `appsettings.json` or `dashboard-config.json` by hand
**How:**
1. Audit which settings are not in Settings page UI
2. Add UI controls (toggles, numeric inputs, text boxes) for every remaining config key
3. For advanced JSON editing (power users), keep the editor but pre-validate with Item 3 (Dashboard JSON validation)
4. Document every setting in tooltips
**Why:** General public does not know how to edit JSON files safely

---

### PRIORITY 2.5 — AI/ML & Advanced Analytics (differentiator)
---

#### 23. ~~AI/ML-driven anomaly detection & predictive capacity planning~~ (DEFERRED to v1.1)
**Files:** New `Data/Sql/AnomalyDetection/` folder, `Data/Services/PredictiveService.cs`, `Data/Models/CapacityForecast.cs`, `Services/ChartTheme.cs`, `Pages/Capacity.razor`, `Components/Shared/ForecastChart.razor`, `Hubs/AnomalyHub.cs`
**Status:** Scope trimmed — full ONNX pipeline deferred to v1.1 per pre-mortem velocity triggers. PRD spec retained; models will be trained offline and placed in `Data/Models/` when ready.
**v1.1 plan:**
- Train on historical `sp_triage` dataset (~600 servers × years) using Python scikit-learn
- Export to ONNX; inference via `Microsoft.ML.OnnxRuntime` (offline, no cloud dependency)
- Queries tagged `trend_analysis` in `Data/Sql/` or `consolidated_checks.sql`
- SignalR `AnomalyHub` for real-time streaming alerts
- Forecast charts with dashed overlays and confidence bands via `ChartTheme`
**Keep in PRD:** Full spec preserved in §5 of `SQLTriage_PRD.md`. Revisit after v1.0 Must-haves stabilize.

---

#### 24. ~~UI Theme Refresh — "Rolls Royce" design system (Tailwind CSS)~~ (DEFERRED to v1.1)
**Files:** `tailwind.config.js` (new), `wwwroot/css/tailwind.css` (new), component class overrides in all `.razor` files, `Components/Shared/*` styling updates
**Status:** Scope trimmed — full Tailwind migration deferred to v1.1 per pre-mortem velocity triggers.
**v1.0 interim:** Use ApexCharts default dark theme; create `ChartTheme.cs` singleton helper (Task 38) to centralize Rolls Royce palette (emerald/amber/slate) and Playfair headings for charts only.
**v1.1 plan:**
- Install Tailwind (`npm install -D tailwindcss postcss autoprefixer`)
- Configure `tailwind.config.js` with Rolls Royce palette (slate/stone, emerald accent, Playfair + Inter fonts)
- Create `wwwroot/css/tailwind.css` with imports
- Gradual component migration from `app.css` to Tailwind classes
- Glassmorphism panels, smooth `300ms ease-out` transitions, soft layered shadows
- Keep `app.css` for backward compatibility during migration
**Keep in PRD:** Full spec preserved in §5.12 of `SQLTriage_PRD.md`.

---

### PRIORITY 2.6 — Enterprise & quality prerequisites (must-do for v1.0)
---

#### 25. RBAC implementation — compliance-ready access control
**Files:** `Data/Services/RbacService.cs`, `Data/Models/UserRole.cs`, UI pages
**Problem:** `IRbacService` interface exists but implementation incomplete; UI wiring exists but backend not functional; needed for SOC2 compliance (Access Control principle)
**How:**
1. Implement role store (SQLite table or JSON in config folder)
2. Define roles:
   - **Admin** (full system access)
   - **DBA** (full operational access, no user management)
   - **ReadOnly** (view-only, no changes)
   - **Auditor** (read audit log only — separate from data access)
   - **Operator** (acknowledge alerts only)
3. Add page guards: use existing authorization pattern in `App.xaml.cs` circuit handler
4. Add user management UI: create/edit users, assign roles, optional AD group mapping
5. Store password hashes using **Argon2id** (`Microsoft.AspNetCore.Cryptography.KeyDerivation`): `KeyDerivation.Pbkdf2` with random 128-bit salt, 100K iterations, 256-bit subkey; store `{ salt, subkey, iterationCount }` per user. `CredentialProtector` is for encrypting stored secrets (connection strings), NOT passwords.
6. **Audit all role changes:** who granted what role to whom, when — exportable for auditors
**Why:** Access control is mandatory for SOC2/HIPAA; "Auditor" role specifically for compliance reviews

---

#### 26. AD/LDAP authentication integration
**Files:** New `ActiveDirectoryAuthenticationService.cs`, login page integration
**Problem:** No enterprise authentication; only local user/pass — limits enterprise adoption
**How:**
1. Use `System.DirectoryServices.Protocols` (cross-platform LDAP) or Windows Integrated auth
2. Config: domain controller, base DN, optional service account
3. Login page: add "Use Windows Authentication" (Kerberos/NTLM via current user) OR LDAP bind with username/password
4. Map AD groups to SQLTriage roles (e.g., `DOMAIN\SQLAdmins` → DBA role, `DOMAIN\Auditors` → Auditor role)
**Why:** Enterprise won't federate without domain authentication; ties to compliance (access control audit evidence)

---

#### 27. GPO ADMX templates for enterprise deployment
**Files:** `Deployment/ADMX/SQLTriage.admx`, `SQLTriage.adml`
**Problem:** No Group Policy support; deployment guide mentions GPO but no templates exist
**How:**
1. Define registry-based policy: `HKLM\Software\Policies\SQLTriage\`
2. Policy keys: `DefaultConnectionString`, `LogPath`, `DisableAutoUpdate`, `ForceServiceMode`, `MaxRefreshInterval`, `AuditLogRetentionDays`
3. Include ADMX/ADML in MSI; document GPO import steps in deployment guide
4. Test with `gpupdate /force` and verify registry application
**Why:** Enterprises with 100+ DBAs require GPO for consistent, auditable configuration management

---

#### 28. Tamper-proof audit logging (compliance requirement)
**Files:** `Data/Services/AuditLogService.cs`
**Problem:** Current audit log is plain SQLite/JSON; can be silently modified — fails SOC2 tamper-evidence requirement
**How:**
1. Option A (HMAC chain): Each entry signed with HMAC-SHA256 using key from DPAPI; chain entries together (hash of previous entry included); validate chain on startup
2. Option B (append-only file): Open log file with `FileOptions.WriteThrough`, never seek backwards; Windows-only but tamper-evident
3. Optional forwarder: send audit events to Windows Event Log, syslog server, or SIEM via HTTPS
4. Add "Export audit trail for period" button in UI (for auditors)
**Why:** Compliance (SOC2, GDPR, ISO) requires non-repudiable audit trails; ties to Governance Report export

---

#### 29. Fix CI/CD pipeline permanently
**Files:** `.github/workflows/*.yml`
**Problem:** Submodule checkout fails; builds break regularly
**How:**
1. Use `actions/checkout@v4` with `submodules: recursive` and `fetch-depth: 0`
2. Pin PerformanceStudio to specific commit SHA (remove `--remote` on build target)
3. Add workflow to validate submodule is clean before allowing PR merge
4. Fail build on warnings after CA1416 fixed: add `-warnaserror` to dotnet build
5. Add matrix build: windows-latest (required), ubuntu-latest (if cross-platform work done)
**Why:** Broken CI prevents contributions and signals poor project health

---

#### 30. Unit test suite — 80% coverage minimum
**Files:** New `SQLTriage.Tests/` project (xUnit)
**Problem:** Zero tests; changes are risky
**How:**
1. Create `SQLTriage.Tests.csproj` targeting net8.0
2. Start with easy, no-DB tests: `RateLimiter`, `UserSettingsService`, `CredentialProtector` round-trip, `DashboardConfigService.Validate()`
3. For DB-dependent code, use `Microsoft.Data.Sqlite` in-memory for cache layer tests
4. Mock `IDbConnectionFactory` and `IDbConnection` with Moq for service tests
5. Set up Coverlet + Codecov; enforce 80% coverage gate in CI
6. Add test run to PR check requirement
**Why:** No tests = no confidence to refactor or add features safely

---

### PRIORITY 2.7 — Core Translation Substrate & Governance Engine (Must-haves)
---

#### 31. IFindingTranslator service — 3-audience translation substrate
**Files:** New `Data/Services/FindingTranslator.cs`, `Data/Models/Translation/` (FindingDba, FindingItManager, FindingExecutive)
**Problem:** GovernanceService aggregates scores but does not translate findings into audience-specific language. This is the core differentiator: DBA → IT Manager → CFO translation.
**How:**
1. Define translation models:
   - `FindingDba`: technical details, T-SQL snippets, wait stats, index DMVs, error numbers
   - `FindingItManager`: SLA impact, resource planning, root cause category, remediation effort estimate (hours/days)
   - `FindingExecutive`: business risk (High/Medium/Low), cost exposure (USD/hr downtime), compliance flags (SOC2/HIPAA control IDs), 1-sentence plain-language summary
2. `IFindingTranslator.Translate(Guid findingId)` returns `TranslationResult { Dba, ItManager, Executive }`
3. Populate from existing VA check metadata in `Data/Sql/queries.json` — each check has `audienceTags` array
4. Tie to Governance scoring: `executive.riskScore` feeds overall risk level
5. RBAC integration: users only see translations matching their role
6. Cache translations in `SqliteCacheStore` with TTL (recompute on VA re-run)
**Why:** "Saved clients >$50M" through translation. Without this layer, tool is just another DBA utility, not a governance platform.
**Files to create:**
- `Data/Models/Translation/FindingDba.cs`
- `Data/Models/Translation/FindingItManager.cs`
- `Data/Models/Translation/FindingExecutive.cs`
- `Data/Services/FindingTranslator.cs`

---

#### 32. GovernanceService redesign — capped scoring, vector weights, JSON-editable
**Files:** `Data/Services/GovernanceService.cs`, new `Config/governance-weights.json`
**Problem:** Current scoring algorithm unbounded; single P1 can dominate scorecard; weights not tunable without code change.
**How:**
1. **Cap critical failures:** Each P1 (Service Down) max 40 points; P2 max 25; P3 max 10
2. **Per-framework vector:**
   - Security 30% (excessive perms, audit gaps)
   - Performance 25% (wait stats, CPU spikes)
   - Reliability 25% (backup failures, AG health)
   - Cost 15% (licensing optimization, CPU under-utilization)
   - Compliance 5% (policy drift)
3. Weights stored in `Config/governance-weights.json`:
   ```json
   { "Security": 0.30, "Performance": 0.25, "Reliability": 0.25, "Cost": 0.15, "Compliance": 0.05 }
   ```
4. `GovernanceService` reloads weights on file change (FileSystemWatcher)
5. `GovernanceReport` includes: Overall Score (0-100), Category breakdown, Top 5 risk drivers, Trend arrow (↑/→/↓)
6. Maturity roadmap uses capped Quick Check subset score (Task 20)
**Why:** Prevents score distortion, allows product to tune emphasis without recompiling, aligns with multi-stakeholder governance model.

---

#### 33. AuditLogService — single-writer queue, DPAPI-machine, 4 KB checkpoint, Event Log mirror
**Files:** `Data/Services/AuditLogService.cs`, Windows Event Log source registration
**Problem:** Concurrent logging race conditions; no tamper evidence; cannot recover after crash; no OS-level forensic trail.
**How:**
1. **Single-writer queue:** `BlockingCollection<AuditEvent> _queue` with single consumer `Task`
2. **Checkpoint files:** Flush to `%PROGRAMDATA%\SQLTriage\audit\{yyyy-MM-dd}.log.enc` every 50 events OR 5 seconds (whichever first)
3. **Encryption:** DPAPI-machine (`ProtectedData.Protect(..., DataProtectionScope.LocalMachine)`)
4. **Event Log mirror:** Write each event as Information-level entry to Windows Application log, Source "SQLTriage-Audit"; includes findingId, userId, action, timestamp
5. **Recovery:** On startup, scan checkpoint directory; replay last un-checkpointed batch; validate HMAC chain if implemented
6. **Export:** "Export audit trail (JSON)" button for date range — decrypts and bundles for auditors
**Why:** SOC2 tamper-evidence requirement; forensic integrity; prevents silent log manipulation.

---

#### 34. QuestPDF report engine — Governance Report PDF generator
**Files:** New `Data/Services/ReportService.cs`, `Pages/GovernanceReport.razor`
**Problem:** Auditors want structured PASS/FAIL evidence, not raw data. Current PrintService limited.
**How:**
1. Add `QuestPDF` NuGet (`dotnet add package QuestPDF`)
2. `ReportService.GenerateGovernanceReport(GovernanceReport report)` returns `byte[] PDF`
3. Sections:
   - Cover: SQLTriage logo, report date, server name
   - Executive Snapshot: Overall Risk Level, Operational Maturity %, Downtime Exposure, Audit Readiness
   - Configuration Drift Summary: PASS/WARN/FAIL table mapped to SOC2/HIPAA controls (stated as "aligned with")
   - Security Risks: excessive permissions, privileged accounts checklist
   - Cost Efficiency Insights: CPU waste, licensing opportunities
   - Priority Action Plan: Top 10 fixes ranked by business ROI
   - Appendix: Raw findings table (optional)
4. Styling: Playfair Display headings, emerald/amber/slate color bands, 1-inch margins
5. Include "Cost of Downtime" calculator in PDF footer (user sets revenue/hr in UI, tool multiplies by exposure hours)
6. Add "Export PDF" button on Governance page and Quick Check results
**Why:** This PDF is the budget trigger — shows immediate ROI to decision-makers. Differentiator vs. raw data dumps.

---

#### 35. Error catalog expansion — ~60 scenarios with coverage test
**Files:** `Data/Services/ErrorCatalog.cs` (new or expand existing), `SQLTriage.Tests/ErrorCatalogTests.cs`
**Problem:** Error messages are generic; no business/risk context; no test coverage of error coverage.
**How:**
1. Catalog ~60 error scenarios across categories:
   - Connection: network unreachable, timeout, auth failure (SQL/Windows/Azure), version mismatch, encryption required
   - Query: timeout, cancellation, deadlock victim, plan generation failure, out-of-memory
   - Permission: VIEW SERVER STATE denied, database state denied, SQLWATCH DB access, Agent access
   - Data: malformed XML (system_health parse), corrupt cache file, disk full, quota exceeded
   - Azure: SAS token expired, directory-scoped SAS invalid, container not found
   - Feature: disabled by policy, concurrent modification conflict, obsolete query skipped
2. Each entry: `ErrorCode` enum, `UserMessage` (plain English), `GovernanceImpact` (e.g., "Monitoring gap — affects Audit Readiness control"), `Remediation` (1-3 steps, T-SQL if applicable)
3. `ErrorCatalog.GetMessage(ErrorCode, params)` returns formatted string
4. Unit test: iterate all ErrorCode values; assert `UserMessage.Length > 20`, `GovernanceImpact != null`, `Audience` (DBA/IT/Exec) set
**Why:** Connects operational failures to business outcomes; mandatory for translation substrate.

---

#### 36. ChartTheme singleton — Rolls Royce ApexCharts palette helper
**Files:** New `Data/Services/ChartTheme.cs`, integration in all `Components/Shared/Chart*.razor`
**Problem:** Charts use inconsistent colors; no unified styling; Rolls Royce branding missing from visualizations.
**How:**
1. `ChartTheme` singleton (registered as `Singleton` in DI):
   - `GetOptions(ChartType type)` returns `ApexChartBaseOptions` configured
2. **Palette:**
   - Emerald (good): `#10B981` → `#34D399` gradient
   - Amber (warn): `#F59E0B` → `#FBBF24` gradient
   - Slate (neutral): `#475569` → `#94A3B8`
   - Rose (critical): `#E11D48` → `#F43F5E`
3. **Typography:** Title font `Playfair Display` (serif) 18px, `#1E293B`; axis labels `Inter` 11px `#64748B`
4. **Defaults:** Grid dashed light slate, smooth bezier curves (`curveType: 'smooth'`), tooltip shared, legend top-right
5. **Integrations:** `TimeSeriesChart`, `StatCard` sparklines, `GovernanceChart`, any new chart component
**Why:** First impression matters. Charts are the most-viewed visual element; must convey premium quality.

---

### PRIORITY 3 — Large sessions / post-v1.0 features
---

#### 37. Documentation Generator + Installation Helper
**Status:** Design phase — user has docx templates as reference
**Planned pages:** `/documentation` (generate SQL Server state docs from live DMV data), `/installation-helper` (guided hardening steps)
**Approach:**
1. Look at the docx templates to understand what data is needed
2. The app already collects most of this: instance config (sp_configure), disk/memory, AG status, backup history, security findings from VA
3. Documentation page: assemble into structured view with export to PDF via PrintService
4. Installation Helper: wizard-style page that checks current config against best practices and gives a checklist
5. dbatools.io integration: dbatools has a REST API — use HttpClient to call it for additional checks. The `AutoUpdateService._httpClient` pattern is the model.
**Why:** Documentation generator aligns with Governance theme — produces audit-ready evidence

---

#### 38. Code Signing — USER ACTION REQUIRED
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

#### 39. Public release posting plan
**When ready** (after screenshots/GIF and code signing):  
- **r/SQLServer**: title "I built a free SQL Server monitoring tool for Windows DBAs — feedback welcome"
- **r/sysadmin**: focus on the Windows Service mode + alerting + no-agent angle
- **SQLServerCentral.com**: submit an article, not just a link — they prefer content
- **dev.to**: technical post about the Blazor Hybrid WPF architecture (unusual stack, good engagement)
- **Hacker News Show HN**: brief, technical, honest about current state

---

## 🎯 SCOPE TRIAGE — Must/Should/Could for v1.0

Based on pre-mortem analysis (`.ignore/pre-mortem_1.md`) and Opus external review (`.ignore/OPUS_ANALYSIS_COMPLETE_2026-04-18.md`), use this framework when behind schedule:

### MUST-HAVE (v1.0 baseline — ship without these = not v1.0)
- Tasks 1–5: Brand/version/build blockers (can't ship with old name)
- Task 6: Background refresh (UX hygiene)
- Task 8: All settings via UI (non-negotiable for public release)
- Task 25: RBAC basic (Admin/DBA/ReadOnly/Auditor — SOC2 compliance requirement; Argon2id password hashing, not CredentialProtector encryption) — use Argon2id hashing, not CredentialProtector
- Task 20: **Instant Quick Check** — value hook: ≤60 s from EXE launch to results (curated ~40-check subset tagged "quick", not full 489-check VA)
- Task 33: Audit logging single-writer queue + checkpoint + Event Log mirror (compliance)
- Task 31: `IFindingTranslator` service (3-audience translation substrate — core differentiator)
- Task 32: Governance scoring redesign (capped, vector weights, JSON-editable via `Config/governance-weights.json`)
- Task 35: Expanded error catalog (~60 scenarios + coverage test in `SQLTriage.Tests/ErrorCatalogTests`)
- Task 34: QuestPDF report engine (Governance Report PDF — 3-page executive summary with Cost of Downtime calculator)
- Task 30: Unit test 80% coverage minimum (quality gate)
- Task 36: ChartTheme singleton (Rolls Royce ApexCharts palette — emerald/amber/slate, Playfair headings)

### SHOULD-HAVE (v1.0 stretch goals — cut if >2 weeks behind)
- Task 18: Governance Dashboard (simplified: Risk Level + Maturity % only, full Risk Register defer to v1.1)
- Task 34: Governance Report PDF (3-page, simplified layout; Cost of Downtime calculator v1.1)
- Task 19: Error messages with governance impact (strategic positioning)
- Task 13: FAQ + Support (documentation is must, FAQ is should)
- Task 36: ChartTheme singleton integration (already new task in Must-have; if integration burden high, downgrade to Should)

### COULD-HAVE (defer to v1.1 without announcement)
- Task 23: AI/ML (full ONNX pipeline — DEFERRED; keep spec in PRD §5)
- Task 24: UI Theme Refresh (Tailwind migration — DEFERRED; use ApexCharts default dark theme in v1.0)
- Task 26: AD/LDAP integration (local auth only for v1.0)
- Task 27: GPO ADMX templates (Enterprise tier feature, not v1.0)
- Task 29: CI/CD polish (broken pipeline acceptable if manual build works)
- Task 37: Documentation Generator (post-v1.0)
- Task 38: Code signing (defer to v1.0.1, document SmartScreen warning)
- Task 39: Release posting (manual is fine, automated workflow v1.1)
- Task 28: Deadlock viewer (XML parsing — Should-have if easy, else defer)
- Task 35: ForecastService (simplified linear regression only — Should-have; multivariate to v1.1)
- Tasks 7, 14–16, 21: Low-impact enhancements

**Decision trigger:** If Week 4 velocity < 80% of plan (≤ 3 tasks completed), immediately downgrade all Could-haves and 2 Should-haves to v1.1. Communicate: "v1.0 focused on core stability and compliance foundation."

**Architecture decision:** Follow Option D (in-place hardening) — build on existing 80% complete codebase, not fresh rewrite (Opus recommendation, saves ~2 weeks).

---

## 🔬 PRE-MORTEM VALIDATION CHECKLIST

Before each major milestone, run this checklist. If ≥3 items are YES, project is at risk:

- [ ] **Scope creep detected:** Have ≥2 new feature ideas been added since planning? → **Yes = Risk**
- [ ] **Velocity behind:** Are we >20% behind schedule at Week 4? → **Yes = Risk**
- [ ] **No user feedback:** Have we shown working software to ≥1 external DBA? → **No = Risk**
- [ ] **Design debates >1 week:** Has any service design discussion lasted >3 days without code? → **Yes = Risk**
- [ ] **Polish before function:** Are we working on UI theme before core features work? → **Yes = Risk**
- [ ] **Pre-mortem triggers met:** Any 3+ pre-mortem risk factors active? → **Yes = STOP, reassess**

**If 3+ checked:** Call emergency scope trim meeting. Drop to Must-only baseline. Ship MVP in 4 weeks.

---

## 🕌 Codebase Signature: Basmalah on Every File

**Non-negotiable requirement:** Every single code file in this project must begin with the basmalah header. This is the project's **intent lock** — it declares that this work is an act of charity (sadaqah jariyah), built for the pleasure of Allah, not for personal gain or exploitation.

### Header format per file type:

**.cs files (C#):**
```csharp
/* In the name of God, the Merciful, the Compassionate */
// Bismillah ar-Rahman ar-Raheem
```

**.razor files (Blazor):**
```razor
<!--/* In the name of God, the Merciful, the Compassionate */-->
<!-- Bismillah ar-Rahman ar-Raheem -->
```

**.css/JS/HTML/JSON/MD files:**
```css
/* In the name of God, the Merciful, the Compassionate */
/* Bismillah ar-Rahman ar-Raheem */
```

**.config files (.json, .csproj, .sln):**
Add as first comment line (where supported) OR include in commit message if file cannot have comments.

**Why this matters:**
- It's not decorative — it's the **license plate** of the project. Every file carries the intent.
- It prevents "mission drift" — when you're tired, you see the header and remember **why** you're building this.
- It's a **barakah** (blessing) invocation — asking for divine guidance in a complex technical undertaking.
- It signals to contributors: this is not a profit-driven tool. It's a gift. If you don't respect that, don't contribute.

**Enforcement:**
- Add a pre-commit hook (`.git/hooks/pre-commit`) that greps for basmalah in staged files
- If missing, reject commit with message: "Basmalah required — this is a sadaqah, not a product."
- Alternatively, add to CI pipeline: check for header presence, fail build if absent (with豁免 for binary files)

**Do not remove this requirement** under any circumstances. The project loses its soul without it.

---

## 📦 USER FEEDBACK CLARIFICATIONS

Based on user feedback during worklist creation:

**WebView2:**  
It is NOT required. Server Mode (browser UI via Kestrel) is the primary intended fallback. Do NOT bundle WebView2. Documentation should clearly state this is a graceful degradation path, not an error. Server mode works perfectly for headless/server environments.

**Code signing:**  
Can be deferred to v1.0.1. Not a showstopper for v1.0 release. SmartScreen will show warnings, but enterprise deployments can add to trusted publishers. Document this limitation honestly.

**Manual JSON editing removal:**  
All configuration MUST be available via UI. Users should never open a text editor. Settings page needs complete coverage of every config key with tooltips and validation.

**Test suite:**  
Zero-tests is unacceptable for v1.0. Add minimum 80% coverage before release.

**Build warnings:**  
Clean build required. Suppress CA1416 properly or fix platform-specific code. No build warnings at release.

**RBAC stub:**  
Cannot claim to have RBAC if the interface is empty. Either fully implement or mark as "coming in v1.1" and remove from UI/README.

**Governance positioning:**  
All features must tie back to business outcomes: Audit Readiness, Cost Optimization, Downtime Prevention. Error messages should state governance impact. Reports should be PASS/FAIL for auditors. This is the strategic differentiation.

**Brand name:**  
Final brand is **SQLTriage** — "Lightweight SQL Server Monitoring & Governance Tool". Rename everything: solution, project, EXE, docs, website. Tagline: "Reduce audit preparation from days to minutes."

**Basmalah on every file (project signature):**  
Every .cs, .razor, .css, .js, .md, .json, .csproj, .sln file must begin with the basmalah header:
```csharp
/* In the name of God, the Merciful, the Compassionate */
// Bismillah ar-Rahman ar-Raheem
```
This is the project's intent lock — declares it is an act of charity (sadaqah jariyah) for the pleasure of Allah, not a profit-driven product. Enforce via pre-commit hook or CI check. Do not remove.

**Tailwind CSS:**  
Approved for Rolls Royce UI theme. Install as dev dependency (`npm install -D tailwindcss`). Configure custom palette (slate/stone, emerald accent), fonts (Playfair Display + Inter), extended spacing/radius. Keep `app.css` for backward compatibility during migration; gradually migrate components to Tailwind classes.

**AI/ML Predictive Analytics:**  
Predictive models trained offline using scikit-learn on historical `sp_triage` data (600 servers × years). Exported to ONNX format for local inference with `Microsoft.ML.OnnxRuntime`. Models stored in `Data/Models/` with versioning. Queries for trend data tagged `trend_analysis` (either separate files in `Data/Sql/AnomalyDetection/` or sections in `consolidated_checks.sql`). Forecasts are advisory — always display confidence level and allow manual override. Include accuracy validation on holdout dataset (target CPU MAPE <15%).

---

## 🏛️ LONGEVITY DESIGN PRINCIPLES (Rolls Royce = 15-year lifespan)

**Goal:** Build SQLTriage to last 15 years with only "oil and tyre changes" (SQL script updates, minor config tweaks). No rewrites.

### 0. **Basmalah Intent Lock** (non-negotiable)
- Every file must begin with basmalah header (see section above). This is the project's **soul** — it declares this is sadaqah jariyah (continuous charity), built **solely for the pleasure of Allah**, not for profit or exploitation.
- The header prevents mission drift: when tired, you see it and remember **why** you're building.
- It signals to all contributors: this is a gift, not a product. If you don't respect that, don't touch the code.
- **Enforcement:** Pre-commit hook rejects commits without basmalah. CI fails if basmalah missing from any source file.

### 1. **Declarative, Editable Configuration Architecture**
- All business logic lives in **editable text files** — `.sql` files for queries, `.json` or `.yaml` for rules, thresholds, and policies
- No logic or decision rules should be hardcoded in C# unless absolutely necessary (performance-critical inner loops only)
- To adapt to new SQL Server versions or business requirements, edit the text file and call `Reload()` — no recompilation needed
- Version configuration files (SQL + JSON/YAML) alongside app in Git so changes are tracked independently of code
- **Testing:** Validate all query files against SQL Server 2016, 2019, 2022 quarterly; JSON schema validation on startup

### 2. **Stable Public Interfaces**
- `IService` interfaces never change without major version bump
- Database schema migrations are **additive only** (new tables/columns, never remove)
- Public API (if added) is versioned from day 1 (`/api/v1/`)

### 3. **Backward Compatibility by Default**
- Old `.sql` files continue to work even if new ones added
- Old config file versions auto-upgraded with backup of original
- Single-EXE deployment means no "DLL hell" — all dependencies self-contained

### 4. **Minimal External Dependencies**
- ONNX models are data files — swap without code change
- No cloud SDKs (Azure, AWS) in core — optional plugins only
- No JavaScript frameworks that deprecate (ApexCharts is stable, charting is secondary)

### 5. **Diagnostic-First Embedding**
- Every critical path has structured logging (Serilog)
- All errors include correlation IDs for support
- Self-diagnostics page (`/diagnostics`) shows: database connectivity, cache health, model versions, last query timestamps
- **Supportability:** If user calls with issue, you can ask "What does /diagnostics show?" and triage in 5 minutes

### 6. **Incremental Upgrade Path**
- v1.0 → v1.1: optional features, backward-compatible schema
- v1.x → v2.0: major version only when SQL Server EOL forces break (e.g., drop SQL 2016 support)
- **Never break working deployments** without explicit opt-in migration flag

### 7. **Documentation as Code**
- `QUICKSTART.md` and `DEPLOYMENT_GUIDE.md` live in repo, versioned with code
- In-app help pages pull from same Markdown sources (via embedded resource)
- Every setting has tooltip with link to online docs (stable URL)

---

## ARCHITECTURE NOTES FOR OTHER LLMS

### Project structure
```
SQLTriage.sln
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
│   ├── Models/               ← ONNX ML models (capacity_v1.onnx, anomaly_v1.onnx)
│   ├── Sql/
│   │   ├── HealthChecks/
│   │   ├── AnomalyDetection/ ← Trend tags (or use consolidated_checks.sql tags)
│   │   └── queries.json      ← Metadata (optional)
│   ├── Caching/
│   │   └── SqliteCacheStore.cs ← SQLite WAL cache
│   └── Services/
│       ├── AlertEvaluationService.cs
│       ├── ConnectionHealthService.cs
│       ├── NotificationChannelService.cs
│       ├── RbacService.cs
│       ├── PrintService.cs
│       ├── PredictiveService.cs  ← AI/ML inference (ONNX)
│       └── ... (20 services total)
├── Config/
│   ├── version.json          ← { version, buildNumber, buildDate, whatsnew[] }
│   ├── dashboard-config.json ← Panel layout
│   └── appsettings.json
├── tailwind.config.js        ← Tailwind design system (Rolls Royce theme)
├── wwwroot/
│   ├── css/
│   │   ├── app.css           ← Legacy design system (7500 lines)
│   │   └── tailwind.css      ← Tailwind utilities + overrides
│   └── scripts/
│       ├── download.js       ← blazorDownloadFile() helper
│       └── app.js
└── Deployment/
    └── ADMX/                 ← Group Policy templates (future)
```

### Key conventions
- C# files: `/* In the name of God, the Merciful, the Compassionate */` header
- Razor files: `<!--/* In the name of God, the Merciful, the Compassionate */-->` header
- DI: nullable optional params `Service? svc = null` — services may not be available
- Background tasks: `_ = Task.Run(async () => { ... })` pattern
- Credentials: always `CredentialProtector.Encrypt/Decrypt` — never store plaintext
- Connections: always specify database (`"master"` for DMV queries, not default)
- **Tailwind CSS** for utility-first styling + `app.css` legacy compatibility layer
- No `<` in Razor `@code` switch statements — Razor reads it as HTML; use if/else
- Do not write SQL queries — user owns all SQL

### Build
```bash
dotnet build SQLTriage.sln
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
./increment-build.ps1   # bumps Config/version.json buildNumber only, no tags
# To release: git tag v1.0.0 && git push origin v1.0.0
```

### CSS design tokens (key ones — Rolls Royce palette)
```css
--accent: #10b981;           /* emerald-500 — muted success green */
--accent-muted: #059669;     /* emerald-600 — hover/active */
--bg-primary: #0f172a;       /* slate-900 — main backgrounds */
--bg-secondary: #1e293b;     /* slate-800 — cards, panels */
--bg-tertiary: #334155;      /* slate-700 — inputs, disabled */
--bg-hover: #475569;         /* slate-600 — hover states */
--text-primary: #f8fafc;     /* slate-50 — headings, body */
--text-secondary: #94a3b8;   /* slate-400 — muted labels */
--border: #334155;           /* slate-700 — panel borders */
--border-subtle: #1e293b;    /* slate-800 — inner dividers */
--green: #22c55e;            /* green-500 — success (legacy compatibility) */
--red: #ef4444;              /* red-500 — errors (softened) */
--orange: #f59e0b;           /* amber-500 — warnings (softened) */
--blue: #3b82f6;             /* blue-500 — info/links */
```
**Typography:** Headings use `Playfair Display` (serif), body uses `Inter` (sans-serif). Import via Google Fonts or embed locally.
**Motion:** All transitions `300ms ease-out`. Shadows soft and layered (`0 4px 6px -1px rgba(0,0,0,0.4)`).
**Note:** Tailwind utility classes replace most direct CSS var usage; keep `app.css` for backward compatibility during migration.

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
3. Build after changes: `dotnet build SQLTriage.sln -c Release --no-restore`
4. Fix all errors before committing
5. Commit with: `git commit -m "feat/fix: description\n\nCo-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>"`
6. Do NOT push unless explicitly asked
7. Do NOT create new .md documentation files unless asked
8. Do NOT add features beyond what is described
