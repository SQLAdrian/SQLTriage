# SQLTriage — Remaining Work

> **Diagnostic Philosophy:** Diagnose deeply → Export thoroughly → Decide manually
>
> SQLTriage is a diagnostic superset (deep-dive analysis tool), not a monitoring platform (operational automation). We enrich findings, provide prescriptive guidance, and export evidence packages — but DBA retains agency to decide and act. This differentiates from dbWatch's "Monitor → Alert → Automate → Report" closed-loop model.

**Critical Gap Type — Presentation Layer:** Many diagnostic capabilities already exist in the codebase (SQL checks, baseline calculations, health scoring) but are **not exposed in the UI**. These are **visibility gaps**, not engine gaps. Priority 0 is making the existing diagnostic data **obvious** to users.

**Repo:** SQLAdrian/SqlHealthAssessment  
**Current version:** 0.85.2 (build 1197)  
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
- Parallax background bound to monitor (not window)
- QueryPlanModal: ServerVersion/ServerEdition probe + ONLINE/RESUMABLE gating
- Release pipeline end-to-end (publish exe + ZIP + Inno installer to GitHub Release on tag push)
- F6 Background refresh spinner on Sessions (verified — was already implemented)
- Concurrency Configuration sliders Settings → Performance (verified — Heavy 1-20, Light 2-50, runtime apply via QueryOrchestrator.UpdateLimits)
- Executive Health Badge — ExecutiveHealthService wired into DI; new HealthBadge.razor component aggregates per-server scores into a single 0-100 badge with severity colour and trend arrow; placed above NavMenu's existing per-server health strip
- F5 Rate-limit status bar badge — RateLimiter wired into DI; new RateLimitBadge.razor visible only when throttling is active (amber pulse, shows query/connection counts and reset countdown); placed alongside HealthBadge in NavMenu
- D21 — `QueryLoadBalancer` deletion verified (already gone); master worklist status flipped to ✅ COMPLETED
- Baselines on Dashboards (Phase 1: static IQR overlay) — TimeSeriesChart now accepts BaselineP25/BaselineP75 parameters and renders a translucent dashed band ("Typical X-Y") via ApexCharts yAxis annotations. Caller wiring (which dashboards/panels supply the values) is the bridge work for a future session.

---

## DEFERRED — DRAFT/NON-PROD WATERMARK ON REPORTS

**Goal:** Print PDFs generated against non-production servers carry a visible
"DRAFT — non-production data" watermark across every page so they can't be
mistaken for evidence-grade reports.

**Why deferred (2026-04-27):** Requires per-server tagging of "production"
vs "non-production" classification. The app has no such concept today —
servers are added as a connection list with names + domains, no role tag.
Building this means:

1. Add `Environment` (string) field to `ServerConnection` — already exists
   as `Environment` property at line 103. Re-purpose it with a known
   vocabulary: "Production", "Staging", "Development", "Test".
2. UI on Servers page: dropdown next to each server (default = blank/unset).
3. Roadmap export: if any selected server has `Environment != "Production"`,
   apply `body.printing-active.has-draft-watermark::before` CSS that draws
   a diagonal "DRAFT" string over each page (use `position:fixed; transform:
   rotate(-30deg); opacity:0.08; font-size:120pt`).
4. Manifest.json should also carry the watermark flag so a programmatic
   verifier knows the report wasn't blessed for production.

**Effort:** S once server tagging is in place; tagging itself is S → M depending
on whether you want validation rules (e.g. "warn if Production server is
named 'DEV-…'").

**Reason to defer:** premature without server tagging — would require manual
checkbox at export time which is fragile (operator forgets, ticks wrong box).
Better to add the data model first, then surface the watermark automatically.

---

## NEW WORKLIST QUICK-WINS (added 2026-04-27 from `.ignore/New Worklist/` audit)

These are real bugs/correctness fixes — small, isolated, low risk. Pick off when looking for sub-30-min wins between bigger features.

- **NW-1 Server-name validator corrupts valid names** ✅ COMPLETED 2026-04-27 — replaced blacklist with whitelist (alphanumeric + `.\-_:`); `SP-SQL01`, `db.master.contoso.com`, etc. now pass through unchanged. [ServerConnection.cs:120](Data/Models/ServerConnection.cs#L120).
- **NW-2 Cache stampede on typed `List<T>` queries** ✅ COMPLETED 2026-04-27 — extended single-flight from `_inFlightDataTable` to typed queries via `_inFlightTyped`. Type-erased to `Task` and cast on retrieval; flight key includes `typeof(T).Name` so distinct types can't collide. [CachingQueryExecutor.cs:261](Data/Caching/CachingQueryExecutor.cs#L261).
- **NW-3 LogAnon.Reset() not called when anonymisation toggled** ✅ COMPLETED 2026-04-27 — `SetAnonymiseServerNames` now calls `LogAnon.Reset()` on every change so stale aliases don't survive a toggle. [UserSettingsService.cs:336](Data/UserSettingsService.cs#L336).
- **NW-7 Remove obfuscation artefact from OSS** — **DECISION 2026-04-27: KEEP.** ConfuserEx2 pipeline is integrated into [publish-protected.ps1](publish-protected.ps1), advertised as a security feature in [About.razor:198,499,515](Pages/About.razor), and listed in [ProductionReadinessGate.cs:113](Data/Services/ProductionReadinessGate.cs#L113). Retained for future use — clients distributing hardened builds for credential-handling deployments. The OSS-credibility concern is real but is better solved by *documenting* that public GitHub Release artefacts are unobfuscated and reproducible from source, rather than removing the option.

## REMAINING ITEMS

> **Note for LLM contributors:** Every item below has been re-verified 2026-04-27. File paths are accurate as of that date. Each task has:
> - **Goal** — what shipping looks like
> - **Why** — diagnostic value or user-pain rationale
> - **Files** — exact paths (verified)
> - **Steps** — numbered, self-contained
> - **Verify** — concrete commands or visual checks to confirm completion
> - **Don't** — hard rules to avoid common pitfalls
>
> If a "Files" path no longer exists when you read this, **verify with Glob first** — don't guess. The worklist is a snapshot; the code is the truth. Always grep before editing.

---

### PRIORITY 1 — Quick wins, self-contained

---

#### 1. Background refresh thread + "Refresh now" spinner ✅ COMPLETED
**Verified 2026-04-27:** `_refreshing` field + spinner already in [Pages/Sessions.razor:155-158](Pages/Sessions.razor); fetch uses local-var pattern at line 430 (assigns `var newSessions = ...` first, then `AllSessions = newSessions`). The `_loadLock.Wait(0)` guard is preserved. **No further work required.** Original instructions retained below for reference if regression occurs.

---

#### 2. Rate-limit status bar badge ✅ COMPLETED 2026-04-27
**Verified:** `RateLimiter` registered as singleton in [App.xaml.cs](App.xaml.cs); new [Components/Shared/RateLimitBadge.razor](Components/Shared/RateLimitBadge.razor) component polls every 5s and renders only when throttling is active (amber pulse, shows query/conn counts and reset countdown); placed alongside `HealthBadge` in [Components/Layout/NavMenu.razor](Components/Layout/NavMenu.razor) under the per-server health strip. CSS in [wwwroot/css/HealthBadge.css](wwwroot/css/HealthBadge.css). **No further work required.**

---

#### 3. Dashboard JSON schema validation — inline error highlighting
**Status:** PARTIALLY DONE — `ResetToDefault()` already exists; `Validate(string json)` method missing.

**Goal:** A user editing dashboard JSON in `Pages/DashboardEditor.razor` sees a red border and an inline error message the instant their JSON is invalid. The Save button is disabled while invalid. They can click "Reset to default" to wipe their changes and reload the canonical config.

**Why:** A bad JSON edit currently throws on app restart and the user has no in-app way to recover. This makes the dashboard editor unsafe to use without a text editor side-by-side.

**Files:**
- [Data/DashboardConfigService.cs](Data/DashboardConfigService.cs) — service lives here (note: `Data/`, not `Data/Services/`). `ResetToDefault()` exists at line 364.
- [Pages/DashboardEditor.razor](Pages/DashboardEditor.razor) — the editor UI.
- [Config/dashboard-config.default.json](Config/dashboard-config.default.json) — reset target (already exists).

**Steps:**
1. **Add `ValidateJson` method** to `Data/DashboardConfigService.cs`:
   ```csharp
   /// <summary>
   /// Returns (true, null) if json deserialises into a DashboardConfig, otherwise
   /// (false, errorMessage). Used by DashboardEditor for live validation.
   /// </summary>
   public (bool valid, string? error) ValidateJson(string json)
   {
       if (string.IsNullOrWhiteSpace(json))
           return (false, "JSON is empty.");
       try
       {
           var config = System.Text.Json.JsonSerializer.Deserialize<DashboardConfig>(json,
               new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
           if (config == null)
               return (false, "JSON parsed to null.");
           return (true, null);
       }
       catch (System.Text.Json.JsonException ex)
       {
           return (false, $"Line {ex.LineNumber + 1}, position {ex.BytePositionInLine}: {ex.Message}");
       }
   }
   ```
2. **Wire it into the editor** in `Pages/DashboardEditor.razor`. Find the existing JSON `<textarea>` (grep `"@bind"` and `"json"` in the file). Add an `@oninput` handler that calls `ValidateJson` and stores the result:
   ```csharp
   private (bool valid, string? error) _validation = (true, null);
   private void OnJsonChanged(ChangeEventArgs e)
   {
       _editorJson = e.Value?.ToString() ?? "";
       _validation = DashboardConfigService.ValidateJson(_editorJson);
   }
   ```
3. **Add red border + error display** below the textarea:
   ```razor
   <textarea class="@(_validation.valid ? "" : "json-invalid")"
             @bind="_editorJson" @bind:event="oninput" @oninput="OnJsonChanged" />
   @if (!_validation.valid)
   {
       <div style="color:var(--red); font-size:12px; padding:4px 0;">
           @_validation.error
       </div>
   }
   ```
4. **Disable Save** while invalid: change the Save button to `disabled="@(!_validation.valid)"`.
5. **Add CSS** in `wwwroot/css/DashboardEditor.css` (create if missing, then `@import` it from `app.css`):
   ```css
   textarea.json-invalid { border-color: var(--red) !important; }
   ```
6. **Reset button:** wire an existing or new "Reset to default" button to call `DashboardConfigService.ResetToDefault()`.

**Verify:**
- Type `{` (just a brace) into the editor → red border appears, error shows "JSON parsed to null" or similar, Save disabled.
- Type a complete valid config → border returns to normal, Save enabled.
- Click "Reset to default" → editor content reverts to whatever `Config/dashboard-config.default.json` holds.

**Don't:**
- Don't try to schema-validate every panel field. Just round-trip through `JsonSerializer.Deserialize<DashboardConfig>`. If the deserialiser is happy, the JSON is structurally usable; deeper validation is out of scope.
- Don't replace the current JSON editor with a JSON-schema editor library. Keep dependencies minimal.

---

#### 4. PDF/Excel export for tabular audit results
**Goal:** "Export CSV" and "Export PDF" buttons appear on the toolbars of `FullAudit.razor` and `VulnerabilityAssessment.razor`. Clicking them downloads the current filtered table.

**Why:** Exports enable offline analysis, compliance archives, and importing into Excel/Power BI. Fulfils the project's "Export thoroughly" diagnostic philosophy.

**Files:**
- [Pages/FullAudit.razor](Pages/FullAudit.razor)
- [Pages/VulnerabilityAssessment.razor](Pages/VulnerabilityAssessment.razor)
- [Data/Services/PrintService.cs](Data/Services/PrintService.cs) — `PrintToPdfAsync()` already exists; reuse it.
- [Data/CsvParser.cs](Data/CsvParser.cs) — already in project, has CSV writer methods.

**Pre-flight checks:**
- `grep -n "PrintToPdfAsync\|CsvParser" Pages/FullAudit.razor` — confirm whether either is already wired.
- `grep -n "ClosedXML" SQLTriage.csproj` — confirm whether the .xlsx package was already added (skip the .xlsx step if so).

**Steps:**
1. **CSV export (no new dependency):**
   - In `FullAudit.razor` `@code`, add:
     ```csharp
     [Inject] IJSRuntime JS { get; set; } = default!;

     private async Task ExportCsvAsync()
     {
         var sb = new System.Text.StringBuilder();
         sb.AppendLine("Category,Severity,Finding,ServerName"); // adjust columns to your DataTable
         foreach (var row in _filteredRows) // whatever the page calls its rows
             sb.AppendLine($"{Csv(row.Category)},{Csv(row.Severity)},{Csv(row.Finding)},{Csv(row.ServerName)}");
         var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
         await JS.InvokeVoidAsync("blazorDownloadFile", $"audit-{DateTime.Now:yyyyMMdd-HHmm}.csv", "text/csv", bytes);
     }

     // CSV-quote: wraps in quotes and doubles internal quotes if the value
     // contains a comma, quote, or newline. RFC 4180 minimum.
     private static string Csv(string? v)
     {
         if (string.IsNullOrEmpty(v)) return "";
         if (v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return v;
         return "\"" + v.Replace("\"", "\"\"") + "\"";
     }
     ```
   - The `blazorDownloadFile` helper already exists in `wwwroot/scripts/download.js` — verify with `grep -n "blazorDownloadFile" wwwroot/scripts/download.js` before assuming.
2. **PDF export:** call `PrintService.PrintToPdfAsync()` — find the existing call site (e.g. `Pages/Governance.razor`) with `grep -n "PrintToPdfAsync" Pages/*.razor` and copy the pattern.
3. **Add toolbar buttons** to both pages:
   ```razor
   <button class="btn btn-sm btn-secondary" @onclick="ExportCsvAsync">
       <i class="fa-solid fa-file-csv"></i> Export CSV
   </button>
   <button class="btn btn-sm btn-secondary" @onclick="ExportPdfAsync">
       <i class="fa-solid fa-file-pdf"></i> Export PDF
   </button>
   ```
4. **Repeat for `VulnerabilityAssessment.razor`** — same two methods, different column set.

**Verify:**
- Click Export CSV → file downloads, opens in Excel cleanly with quoted commas/quotes preserved.
- Click Export PDF → PDF opens with rendered table and timestamp.
- Empty filter result → CSV is just the header row (don't crash).

**Don't:**
- Don't add `ClosedXML` (or any new NuGet) for .xlsx unless the user asks. Excel opens .csv natively. Skip the xlsx route in v1.
- Don't try to PDF-render the entire page; PrintService already targets a specific element by selector — use the same selector convention.
- Don't hardcode the file path. Always go through `blazorDownloadFile` so it works in both desktop and server modes.

---

### PRIORITY 2 — Medium effort

---

#### 5. FAQ expansion + Support channel link
**Goal:** A first-time user can find answers to the five most common confusion points without asking. They can also find a place to ask new questions.

**Files:**
- [QUICKSTART.md](QUICKSTART.md) — add an "FAQ" section near the bottom.
- [docs/index.md](docs/index.md) — GitHub Pages landing; add the same FAQ link in the nav.
- [Pages/About.razor](Pages/About.razor) — add a "Get Help" panel with links.

**FAQ entries to add (verbatim ok, edit if you have better wording):**

> **Q: Why is my alert firing constantly?**
> A: Alerts use IQR-based dynamic baselines. If a metric is genuinely outside its historical 25-75 percentile range, the alert fires. To tune: open the alert's edit modal, increase `NextAlertDelayMinutes` to suppress repeated firings, or enable Dry-Run mode (Settings → Alerts) to see what *would* fire without actually firing.
>
> **Q: Do I need SQLWATCH?**
> A: No. Most pages work without it. Only the long-term historical dashboards (capacity trends, week-over-week comparisons) benefit from SQLWATCH. Live diagnostics, alerts, and the query plan viewer all work without it.
>
> **Q: How do I run as a Windows Service?**
> A: Run the installer (`SQLTriage-Setup.exe`) and tick "Install as Windows Service" on the components page. Default port is 5000. Stop/start via Windows Services console as `SQLTriageService`.
>
> **Q: How do I move my saved server credentials to another machine?**
> A: Settings → Server Credentials → Export. This produces an `.lmcreds` file protected with a passphrase you choose. On the target machine, Settings → Server Credentials → Import, supply the file and passphrase.
>
> **Q: Where are the logs?**
> A: `logs/app-YYYYMMDD.log` next to the exe. Older logs are auto-rotated (kept 14 days). Set "Debug logging" in Settings to capture verbose output.

**Steps:**
1. Append the FAQ block to `QUICKSTART.md` under a new `## Frequently Asked Questions` heading.
2. Mirror it in `docs/index.md` for GitHub Pages visibility.
3. In `Pages/About.razor`, find the existing "Resources" or "Help" section (`grep -n "Get Help\|Resources\|Support" Pages/About.razor`). Add:
   ```razor
   <h4>Get Help</h4>
   <ul>
       <li><a href="https://github.com/SQLAdrian/SQLTriage/discussions" target="_blank">Ask a question (GitHub Discussions)</a></li>
       <li><a href="https://github.com/SQLAdrian/SQLTriage/issues/new?template=bug_report.md" target="_blank">Report a bug</a></li>
       <li><a href="https://github.com/SQLAdrian/SQLTriage/blob/main/QUICKSTART.md#frequently-asked-questions" target="_blank">FAQ</a></li>
   </ul>
   ```
4. Enable GitHub Discussions: repo Settings → Features → Discussions → Enable. (User action, not LLM action.)

**Verify:**
- `QUICKSTART.md` renders cleanly on GitHub (preview the markdown).
- About page in the running app shows three working links.
- Clicking the Discussions link opens the (now-enabled) Discussions tab.

**Don't:**
- Don't write a 50-question FAQ. Five high-quality answers beats fifty mediocre ones.
- Don't mirror the FAQ a third time in the in-app About page — link out instead. One source of truth.

---

#### 6. Query Plan Viewer — ServerVersion/ServerEdition probe ✅ COMPLETED 2026-04-27
**Verified:** [Data/Services/ConnectionHealthService.cs](Data/Services/ConnectionHealthService.cs) probes `ProductMajorVersion` + `Edition` alongside `EngineEdition` on first health-check; exposes `ServerCapabilities` record with `MajorVersion`, `Edition`, `IsEnterpriseClass`. [Components/Shared/QueryPlanModal.razor](Components/Shared/QueryPlanModal.razor) injects the service, calls `GetCapabilities(serverName)` on `ShowAsync`, gates ONLINE on Enterprise-class and RESUMABLE on Enterprise + SQL 2017+. Tooltips explain why a checkbox is disabled. **No further work required.**

---

#### 7. draw.io / SVG export of Environment View
**Goal:** A user viewing the topology graph at `/environment-view` can click "Export" and download (a) a self-contained `.svg` for documentation, or (b) a `.drawio` XML file they can open in draw.io for editing.

**Why:** Topology diagrams end up in change requests, runbooks, and architecture docs. Users currently have to screenshot the canvas. Native SVG/draw.io export elevates SQLTriage from "monitoring tool" to "documentation source."

**Pre-flight checks (run first to learn the rendering tech):**
```bash
ls Pages/EnvironmentView.razor
grep -nE "<svg|<canvas|d3\.|new ForceGraph|topology|force-directed" Pages/EnvironmentView.razor
ls wwwroot/scripts/environmentView.js   # likely the rendering JS
grep -nE "blazorDownloadFile|toDataURL|outerHTML" wwwroot/scripts/environmentView.js wwwroot/scripts/download.js
```

**Files (to verify):**
- [Pages/EnvironmentView.razor](Pages/EnvironmentView.razor) — the page.
- [wwwroot/scripts/environmentView.js](wwwroot/scripts/environmentView.js) — the JS that builds the graph.
- [wwwroot/scripts/download.js](wwwroot/scripts/download.js) — already exposes `window.blazorDownloadFile(filename, mimeType, bytes)`.

**Steps (path A — graph is rendered as `<svg>`):**
1. **Add a JS export function** to `environmentView.js`:
   ```javascript
   window.environmentView.exportSvg = function () {
       var svg = document.querySelector('#topology-svg'); // adjust selector
       if (!svg) return null;
       var clone = svg.cloneNode(true);
       // Inline computed styles so the SVG looks the same outside the app's CSS
       var serializer = new XMLSerializer();
       var svgString = '<?xml version="1.0" standalone="no"?>\n' + serializer.serializeToString(clone);
       return svgString;
   };
   ```
2. **Call it from Razor** and download:
   ```csharp
   private async Task ExportSvgAsync()
   {
       var svgString = await JS.InvokeAsync<string>("environmentView.exportSvg");
       if (string.IsNullOrEmpty(svgString)) return;
       var bytes = System.Text.Encoding.UTF8.GetBytes(svgString);
       await JS.InvokeVoidAsync("blazorDownloadFile",
           $"topology-{DateTime.Now:yyyyMMdd}.svg", "image/svg+xml", bytes);
   }
   ```
3. **Toolbar button:** `<button @onclick="ExportSvgAsync">Export SVG</button>`.

**Steps (path B — graph is rendered on `<canvas>`):**
1. **Add to `environmentView.js`:**
   ```javascript
   window.environmentView.exportPng = function () {
       var canvas = document.querySelector('#topology-canvas');
       if (!canvas) return null;
       return canvas.toDataURL('image/png');
   };
   ```
2. **Razor side:**
   ```csharp
   private async Task ExportPngAsync()
   {
       var dataUrl = await JS.InvokeAsync<string>("environmentView.exportPng");
       if (string.IsNullOrEmpty(dataUrl) || !dataUrl.StartsWith("data:image/png;base64,")) return;
       var base64 = dataUrl.Substring("data:image/png;base64,".Length);
       var bytes = Convert.FromBase64String(base64);
       await JS.InvokeVoidAsync("blazorDownloadFile",
           $"topology-{DateTime.Now:yyyyMMdd}.png", "image/png", bytes);
   }
   ```

**Steps (draw.io XML export — defer if SVG/PNG is enough):**
- draw.io's `.drawio` format is XML wrapping `<mxCell>` elements. Each server node = one `<mxCell vertex="1">` with x/y/width/height in `<mxGeometry>`; each edge = one `<mxCell edge="1">` referencing source/target IDs.
- Generate this from the same data model the JS uses to lay out nodes.
- Recommended: ship SVG export first, only add `.drawio` if a user actually asks. SVG opens in draw.io anyway via "File → Import".

**Verify:**
- Click Export SVG (or PNG) → file downloads, opens in a browser tab and looks like the on-screen topology.
- Open the SVG in draw.io ("File → Import → SVG") — should display correctly.

**Don't:**
- Don't try to inline raster images (server icons) into the SVG — keep them as `<image href="...">` and accept that the SVG references the running app's URLs. If full portability matters, base64-encode the icons; that's a v2 polish task.
- Don't write a JSON-to-mxGraph converter from scratch unless the user asks. SVG covers 95% of the use case.

---

#### 8. SQL Server CPU & Latency Benchmark (Item 15) - FOR NEXT PHASE. SKIP FOR NOW
**Context:** Initial SQL at `C:/temp/proc_stats_enriched.sql`  
**File to create:** `Data/Services/BenchmarkService.cs`, new page `Pages/Benchmark.razor`  
**Diagnostic value:** Quantitative performance benchmarking across servers — identifies hardware/VM bottlenecks, hypervisor contention, cross-instance performance differences. Adds objective metrics to subjective "wait stats" analysis.  
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
**Diagnostic value:** Auto-generates comprehensive server documentation (configuration, security, performance) from live diagnostics — saves hours of manual inventory. Installation helper provides pre-deployment checklist.  
**Planned pages:** `/documentation` (generate SQL Server state docs from live DMV data), `/installation-helper` (guided hardening steps)  
**Approach:**
1. Start by looking at the docx templates to understand what data is needed
2. The app already collects most of this: instance config (sp_configure), disk/memory, AG status, backup history, security findings from VA
3. Documentation page: assemble these into a structured view with export to PDF via PrintService
4. Installation Helper: a wizard-style page that checks current config against best practices and gives a checklist
5. dbatools.io integration: dbatools has a REST API — use HttpClient to call it for additional checks. The `AutoUpdateService._httpClient` pattern is the model.

---

#### 10. Code Signing (Item 20) — USER ACTION REQUIRED  - FOR NEXT PHASE. SKIP FOR NOW
**Rationale:** Builds trust in diagnostic tool — ensures users the executable hasn't been tampered with. Critical for tool that inspects production databases.
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
**Rationale:** Position SQLTriage as diagnostic specialist, not general monitor. Emphasize unique differentiators: no-agent, interactive plans, offline-capable, Windows-native.
**When ready** (after screenshots/GIF and code signing):  
- **r/SQLServer**: Title "SQLTriage — Deep diagnostic tool for SQL Server (free, no agents, interactive plans)" — focus on query plan viewer, blocking chains, VA findings export.
- **r/sysadmin**: Emphasize Windows Service mode + no network footprint + portable single-exe.
- **SQLServerCentral.com**: Article titled "Why I Built a Diagnostic-First SQL Server Tool" — contrasts with monitoring platforms; explains "Diagnose deeply → Export thoroughly → Decide manually" philosophy.
- **dev.to**: Technical deep-dive on Blazor Hybrid WPF (unusual combination) + SQLite cache strategy + interactive SVG plan rendering.
- **Hacker News Show HN**: "SQLTriage: Desktop SQL Server diagnostic tool with interactive execution plans, blocking analysis, and vulnerability assessment — no agents, MIT license" — be honest about scope (SQL Server only), highlight open-source.

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

## Competitive Context: dbWatch Comparison

**Philosophical Split:**
| Aspect | dbWatch | SQLTriage (target) |
|--------|---------|-------------------|
| Core model | Monitor → Alert → Automate → Report (closed loop) | Diagnose deeply → Export thoroughly → Decide manually (open loop) |
| Action | Automated jobs, scheduled reports, threshold alerts | Prescriptive guidance, manual trigger, user agency |
| Value prop | Operational efficiency (save DBA time via automation) | Diagnostic depth (find root cause faster with richer data) |
| Target user | Enterprise DBA teams managing hundreds of instances | Windows DBA/consultant diagnosing specific issues |
| Deployment | Server-agent, multi-platform | Single-exe desktop, SQL Server only |

**Implication for development:** We can match/exceed dbWatch on diagnostic richness (history, forensics, compliance evidence) while deliberately not building the automation layer. This is a feature, not a gap.

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
**Rationale:** Clean project identity before public launch. Diagnostic tools need professional presentation to be taken seriously.
- Move all "scar" files (MEMORY_LEAK_STATUS.md, FOOTPRINT_REDUCTION_GUIDE.md, LOG_ISSUES_ANALYSIS.md, DACPAC_REMOVAL_SUMMARY.md, REFACTORING_RECOMMENDATIONS.md, UI_MODERNIZATION_PLAN.md, PROJECT_GAP_ANALYSIS.md) to `.ignore/ROASTME/` or `/docs/internal/`.
- Update project/solution names from "SqlHealthAssessment" to "SQLTriage" consistently in .csproj, .sln, and code.

### README Rewrite (Today)
**Rationale:** First impression establishes diagnostic positioning — "deep analysis, your decisions" — vs. dbWatch's automation message. Include dbWatch comparison table with "diagnostic-only" vs. "operational platform" distinction.
- Replace README.md with polished version: Hero section with badges, embedded screenshots grid (16 images), complete comparison table (add dbwatch column), Loom demo link, remove self-references.
- Upload screenshots to `/screenshots/` folder and embed in README.
**Diagnostic branding emphasis:** "No agents, no cloud, no automation — just deep SQL Server diagnostics on your machine."

### Marketing Boost (This Week)
**Rationale:** Attract SQL Server community by highlighting unique value: desktop app with interactive plan viewer, blocking kill, and no agent footprint.
- Add repo topics: sql-server-monitoring, dba-tools, performance-tuning, blazor-wpf, open-source-sql.
- Record 60-90s Loom demo of interactive plan viewer V2 + blocking kill + Quick Check.
- Add badges: .NET 8 • Blazor • No Agent • GPL-3.0.
**Messaging note:** Frame as "DBA's pocket diagnostic tool" — like a stethoscope, not a robotic surgeon.

### Feature Additions (v0.86 Prep)
- **Maintenance Recommendation Engine** (reframed from "Scheduled Maintenance Operations"): Add `Pages/MaintenanceAdvisor.razor` — analyzes index fragmentation, outdated stats, missing indexes, integrity issues → generates **review-ready T-SQL scripts** (REBUILD/REORGANIZE/UPDATE STATISTICS/DBCC). User reviews and executes manually. **No automation** — diagnostic output only. Uses Quartz.NET only if user wants scheduled generation (see item 369). Philosophy: Diagnose maintenance needs → Export fix scripts → Manual review/execute.
- **Management Templates** (reframed as Diagnostic Templates): Add `Pages/TemplateLibrary.razor` with predefined diagnostic templates (Wait Stats analysis, Blocking investigation, Performance baseline, Security audit) as JSON in `templates/`. Templates define: which queries to run, how to render results, what thresholds to apply. Allows apply/customize/export. Philosophy: "Diagnose with expert guidance" — templates encode diagnostic workflows, not automated actions.

### Positioning Shift
**Core message:** SQLTriage is a **diagnostic tool**, not a monitoring platform. Competes with dbWatch by going deep on SQL Server internals, not broad on automation. Users choose SQLTriage when they need to **investigate** (blocking, plan analysis, security findings), not when they need to **respond** (alerts, scheduled jobs). This justifies single-platform focus (SQL Server depth > multi-platform breadth).
- Stop "lightweight alternative" — position as "Portable, no-agent desktop diagnostic weapon for SQL Server DBAs — single exe, service mode, real interactive plans, no automation overhead."

### Critical Strategic Gaps (From Feedback)
**Note 2026-04-21:** Gap list updated with **diagnostic-first reframing**. Original dbWatch feature names retained for reference, but descriptions now align with "Diagnose deeply → Export thoroughly → Decide manually" mantra. Automation-leaning features converted to recommendation/reporting modes.
**Note:** All features below are filtered through diagnostic philosophy: "Diagnose deeply → Export thoroughly → Decide manually". We provide rich diagnostic output and prescriptive guidance, but DBA retains agency to review and act.

1. **Maintenance Recommendation Engine** (HIGH priority): [Reframed from "Automated Maintenance Execution"] Generate T-SQL scripts (index rebuild/reorganize, UPDATE STATISTICS, DBCC CHECKDB) based on diagnostic analysis. Scripts include explanations, risk notes, and rollback guidance. User reviews and executes manually. Effort: 2-3 weeks (script generation + validation).
2. **Historical Performance Repository** (HIGH priority): Extend SQLite schema to store aggregated metrics (hourly/daily rollups of wait stats, session counts, resource usage). Retention: 6-12 months configurable. Enables trend analysis, "when did this start?" diagnostics, and baseline comparisons. Effort: 1-2 weeks (schema + backfill + UI rollup views).
3. **Compliance Framework** (MEDIUM-HIGH priority): Map existing 489 VA checks to industry standards (CIS, PCI, NIST, GDPR). Generate compliance scorecard dashboard (percentage compliant by control family). Export audit-ready PDF packages with evidence (query text, plan screenshot, finding details). **No automated remediation** — just diagnostic reporting. Effort: 3-4 weeks (mapping research + scoring engine + report templates).
4. **Threshold-Based Filtering & Highlighting** (MEDIUM priority): [Reframed from "Threshold-Based Alerting"] Add UI controls to filter sessions/metrics by configured thresholds (CPU > X%, wait time > Y ms). Highlight rows that exceed thresholds with color coding. **No alerts, no emails, no always-on monitoring** — purely a triage aid during active diagnosis. User sets thresholds in Settings; IQR outlier detection remains as auto-detection complement. Effort: 1 week.
5. **Multi-Tenant / MSP Features** (LOW-MEDIUM priority): Add "Environment" abstraction to group servers (Dev/Prod/CustomerA/CustomerB). Multi-server health rollup dashboard. Template deployment for connection strings and common dashboard layouts. **No user isolation or billing** — purely organizational for consultants managing multiple clients. Effort: 4-6 weeks.
6. **Advanced Blocking Analysis** (MEDIUM priority): Store blocking events in history table. Timeline view showing blocking chain evolution. "Top Blocking Offenders" report (sessions that blocked others most over past 24h). Include SQL text for blocking and blocked statements. Effort: 2 weeks (event capture + timeline UI + report).
7. **Health Score & Risk Rating** (MEDIUM priority): Compute weighted index (0-100) from: performance degradation trends, compliance gaps, security findings, resource saturation, blocking frequency. Executive summary panel on dashboard. Tooltips explain score composition. Enables quick "is this server healthy?" answer. Effort: 1 week.
8. **Diagnostic Report Packages** (MEDIUM priority): [Reframed from "Scheduled & Automated Reporting"] One-click generation of: (a) Executive Summary (health score + top 5 risks + trend graphs), (b) DBA Handoff Package (full findings + connection details + known issues), (c) Audit Evidence (VA findings with screenshots/plans). **Optional scheduling** via internal timer (Quartz.NET) to generate reports at configured times and drop to disk — no email delivery, just file output. Effort: 1-2 weeks.
9. **Configuration Snapshot & Diff** (MEDIUM-LOW priority): [Reframed from "Configuration Drift Detection"] Manual "Save Baseline" captures current sp_configure + surface area config. "Compare to Baseline" highlights changes (additions/removals/modifications) with color-coded diff. No continuous monitoring — user-triggered diagnostic for post-change verification (e.g., after patch/upgrade). Effort: 2 weeks.
10. **Performance Baselines & Anomaly Detection** (LOW-MEDIUM priority): [Reframed from "Performance Benchmarks & Baselines"] Learn typical values per server (weekly pattern, hourly profile). Z-score anomaly detection flags metrics deviating >2σ from learned baseline. "Baseline period" configurable (e.g., "use last 30 days as normal"). Suppressed during known maintenance windows. Effort: 2-3 weeks (ML-lite training + anomaly scoring).

### Implementation Roadmap (Aligned to Diagnostic Philosophy)
**Note:** Phases built around diagnostic capabilities; automation/scheduling kept minimal and user-triggered only.

**Pre-phase — Presentation Gap Audit (P0 — before coding new features)**

Many diagnostic capabilities already exist in the engine (SQL queries, VA checks, health checks) but lack **presentation layer integration**. These are **work items** to make existing diagnostic data visible and actionable:

| Existing Infrastructure | Missing Presentation | Work Item |
|------------------------|---------------------|-----------|
| `SqlAssessmentService` (489 VA checks in `ruleset.json`) | No compliance mapping to CIS/PCI/NIST; no scorecard dashboard; VA page only shows raw findings | **Add Compliance Mapping Layer** — map check IDs to frameworks; add framework selector; compute compliance % per control family; color-coded scorecard on VA page |
| `AlertBaselineService` + `alert_baseline_stats` table | Baseline data only used for alerts; no UI to view "normal range" for any metric; no trend overlay on charts | **Expose Baselines on Dashboards** — add shaded p25-p75 band to all time-series charts; tooltip shows "normal range"; configurable baseline window (7d/30d/90d) |
| `HealthCheckService` + `ServerHealthStatus` | Health page exists but no single overall score (0-100); not shown on dashboard/home; no trend arrow | **Add Executive Health Badge** — compute weighted 0-100 score; display prominently on home page and sessions header; show ↑↓ vs yesterday |
| Session filters (HideSleeping, ShowOnlyBlocked, HideLowIO) | Only boolean filters; no numeric thresholds (CPU > X, Wait > Y ms) | **Add Numeric Threshold Filters** — Settings → Thresholds section; slider/input for CPU, memory, wait time; highlight rows exceeding thresholds |
| `PrintService.PrintToPdfAsync()` | PDF export exists on some pages but not all; no scheduled generation despite UserSettings having `VaScheduledPdfEnabled`/`RoadmapScheduledPdfEnabled` | **Wire Scheduler + Add Report Bundles** — implement background job that reads schedule settings, generates PDFs, saves to `%APPDATA%\SQLTriage\Reports\`; add Executive Summary, DBA Handoff, Audit Evidence bundles |
| Index fragmentation check in `ruleset.json` (PERF004) | Finding shows "high fragmentation" but no script to rebuild; no per-index recommendation | **Maintenance Script Generator** — page that lists fragmented indexes with `ALTER INDEX` scripts; includes rollback notes; copy-to-clipboard |
| Blocking queries in `SessionDataService` | Live blocking chain shows SPIDs but not the actual SQL causing block; no history | **Blocking Forensics Tab** — modal showing blocker's SQL text, plan, session info; store blocking events in new `blocking_events` table; timeline of last 24h |
| `SqliteCacheStore` with `cache_timeseries` | Raw data kept 7 days only; no rollup to monthly/quarterly for long-term capacity planning | **Time-Series Rollup Service** — daily/weekly aggregations; separate `cache_timeseries_rollup` table; query rollups when viewing >30d range |
| `DynamicDashboard` + `PreloadFromCacheAsync` | Cache preloading happens but timing/cache-hit rate is invisible; no metrics to tune performance; concurrency limits fixed at 5/10 | **Dashboard Performance Telemetry** — add Stopwatch to `LoadPanelDataAsync` and `PreloadFromCacheAsync`; log per-panel load time, cache hit/miss, SQLite read latency; expose optional UI overlay (Developer Mode) to see real-time metrics |
| `CachingQueryExecutor` + `QueryThrottleService` | Throttling limits hardcoded (MaxHeavyConcurrent=5, MaxLightConcurrent=10); not user-configurable; no visibility into current queue depth | **Concurrency Configuration** — add `MaxHeavyConcurrent` and `MaxLightConcurrent` to UserSettings; sliders in Settings → Performance (range 3-15 heavy, 5-30 light); read in `QueryThrottleService` constructor; display current active semaphore count in status bar when Developer Mode enabled |
| `CacheStateTracker` + `_hasLoadedOnce` flag | Dashboard preloads cache on first visit but no proactive warm-up; subsequent visits fast but first visit to any dashboard is cold | **Cache Warm-up on Startup** — after connection test, call `WarmCacheForDashboardAsync` for Home, QuickCheck, Health dashboards; run at low priority; respects `EnableDebugLogging=false` to avoid surprising background load |
| No panel lazy-loading | All panels load in parallel immediately; dashboard with 15+ panels overwhelms SQL even with cache preloading | **Lazy-Load Panels** (stretch) — Settings → `LazyLoadThreshold` (default 6); load first N panels synchronously, remainder after 200ms delay or when scrolled into view; reduces initial perceived load time |

**Action:** Complete all Pre-phase items **before** starting Phases 1-3. These "visibility gaps" are prerequisite to using the diagnostic data effectively.

- **Phase 1 (2-3 months)**: Historical repository (P0), Maintenance recommendation engine (P1), Health score (P1), Threshold filtering (P1), Advanced blocking (P1).
- **Phase 2 (3-4 months)**: Compliance framework (P1), Diagnostic report packages (P1), Config snapshot & diff (P2), Baselines & anomalies (P2).
- **Phase 3 (4-6 months)**: Multi-tenant environments (P2), Predictive capacity forecasting (stretch — anomaly-based, not automated response), Integration APIs (webhooks to feed findings into external ticketing systems — still diagnostic handoff, not auto-remediation).

**Guiding Principle:** Each feature must answer: "Does this help the DBA diagnose more deeply, export more thoroughly, or decide more confidently?" If yes → include. If it removes DBA agency or adds operational burden → defer to separate "SQLTriage Operator" module (future commercial add-on).

Files affected: README.md, WORKFILE_remaining.md (this file), new Pages/, templates/, .csproj, .sln.
Commit pattern: `git commit -m "feat: description\n\nCo-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>"`
**Commit note:** Use `feat:` prefix for new diagnostics (e.g., `feat: add maintenance recommendation engine`), `fix:` for bug fixes, `refactor:` for code quality. All commits must pass `dotnet build` and ideally include brief manual test steps in commit body.

---

## AI ASSISTANT / RAG WORKSTREAM (separate repo + Experimental in-app feature)

**Architecture decision (2026-04-27):** Splitting the corpus-building work from the app. Two repos:

| Repo | Where | Language | What it produces |
|---|---|---|---|
| `SQLTriage-RAG-Builder` (NEW, local-only for now) | `C:\GitHub\LiveMonitor\SQLTriage-RAG-Builder` | Python | A signed `sqltriage-corpus.db` (SQLite + sqlite-vec embeddings) |
| `SQLTriage` (this repo) | `c:\GitHub\LiveMonitor` | C# | Reads the corpus DB if present; AI features gracefully disable if absent |

**Why split:** corpus updates monthly (new MS docs, new videos transcribed); app ships every few weeks. Decoupling prevents every corpus update from forcing an app release. Different toolchains (Python ML stack vs WPF/.NET) stay clean.

**Existing raw data:** ~2GB JSON cache at `research_output/01_yaml_enhancement/web_cache/` — 587 check folders × ~14 sources each = ~8000 SQL-Server-specific articles already harvested with metadata, source URLs, and per-check linkage. Original schema includes `used_for_rag: false` flag. **This is the seed corpus.**

**Privacy/security non-negotiables:**
1. Default = local-only. Embedding model + retrieval + (optional) local LLM all run on the user's machine. No network calls.
2. Cloud LLM = Experimental setting, off by default, opt-in with explicit warning ("Sends query + retrieved passages to {provider}. Server data may be included. Do not enable for regulated data without sign-off.").
3. Server-data tool-call (LLM connecting to SQL to retrieve DMV results) = separate opt-in. AI Assistant on AND Cloud LLM on AND Server-data tool-call on are three independent locks.
4. Every cloud LLM call → AuditLogService entry (timestamp, prompt-hash, model, byte-count, user).

### Use cases (target features in `SQLTriage`)

- **AI co-pilot** (`Pages/Assistant.razor`): user types "slow waits" → mini-LLM steers them through diagnosis using retrieved corpus chunks; user can opt to "gather data" and the LLM calls a read-only DMV tool.
- **Roadmap appendix** (`Data/Services/RoadmapAppendixService.cs`): when sp_triage populates the roadmap, each failed item gets a remediation paragraph synthesised from top-K retrieved chunks (with citations); consolidated into a working document attached to the roadmap PDF.
- **"Explain this check" / "Similar checks"** (proof-of-concept first): one button on `CheckValidator.razor` that retrieves top-3 similar checks via cosine similarity. Smallest possible vertical slice to validate the loop end-to-end.

### Workstream — RAG-Builder (Python, separate repo)

- [ ] **R1. Repo scaffold** — directory structure, README, ingestion-architecture doc. *(this session, no code)*
- [ ] **R2. JSON-cache parser** — read `web_cache/{check_id}/source_*.json`, extract text, dedup, preserve source URL + check linkage
- [ ] **R3. Chunker** — split each source into ~512-token passages with overlap, preserve heading anchors and source URL
- [ ] **R4. Embedder** — `all-MiniLM-L6-v2` via sentence-transformers (local CPU), ~80MB model
- [ ] **R5. Builder** — write `sqltriage-corpus.db` with `chunks` table + `vec_chunks` virtual table (sqlite-vec)
- [ ] **R6. Validator** — duplicate detection, embedding QA (k-nearest sanity check), smoke retrieval test ("page life expectancy" → expect SQLSkills/Brent Ozar hits)
- [ ] **R7. Versioning + sign** — corpus DB has version + checksum; release as a separate downloadable asset alongside the app

### Workstream — RagService (C# in this repo, Experimental flag, opt-in)

- [ ] **A1. RagService skeleton** — open corpus DB if present at `%localappdata%\SQLTriage\corpus.db` or beside exe; load sqlite-vec extension; no-op if missing
- [ ] **A2. Local embedding model** — bundle MiniLM via `SmartComponents.LocalEmbeddings` or equivalent .NET package; **must use the same model as the builder** or vectors are garbage
- [ ] **A3. `Search(query, K)` API** — embed query, vector search top-K from corpus, return chunks with source citation + similarity score
- [ ] **A4. CheckValidator "Similar checks" button** (proof-of-concept) — minimum vertical slice; validates the whole pipeline end-to-end
- [ ] **A5. Settings → Experimental → AI Assistant** — three independent toggles (assistant, cloud LLM, server-data tool-call); explicit warnings on the latter two
- [ ] **A6. Cloud LLM client** (Anthropic/OpenAI) — only enabled when toggle is on; AuditLogService entry per call
- [ ] **A7. `Pages/Assistant.razor`** — chat interface, retrieved chunks shown as collapsible source cards with URLs
- [ ] **A8. Server-data tool** — read-only DMV query function, gated by RBAC (Operator+ only) + No-Pants
- [ ] **A9. RoadmapAppendixService** — for each failed roadmap check, RagService.Search → cloud-or-local LLM synthesis → markdown appendix

### Bridge item (this repo, eventually merge with FTS5 plan)

- [ ] **B1. Hybrid search** — once corpus DB exists, replace CheckValidator's Contains search with a hybrid: FTS5 BM25 + vector cosine, both querying the corpus DB. Smarter ranking than either alone.

### Why this is in this file but coded elsewhere

The RAG-Builder repo lives at `C:\GitHub\LiveMonitor\SQLTriage-RAG-Builder` as a sibling folder, **not** under this repo's git tree. It is in `.gitignore` and `.claudeignore`. Build/publish actions of `SQLTriage.csproj` ignore it (no content globs reach there). The corpus DB it produces ships to users **separately** from the app — as an optional download, not bundled in the installer.
