# SQL Health Assessment

Blazor Hybrid WPF (.NET 8). Single-exe Windows desktop. Falls back to Blazor Server when WebView2 unavailable.

## Search
- Grep, don't read. `app.css` = 7500 lines.
- Scope to `Pages/`, `Data/Services/`, `Components/` — 95% of changes.
- `.claudeignore` blocks root docs, SQL scripts, `bin/`, `obj/`, worktrees, PDFs.
- Don't re-read files already read this session.

## Architecture
```
MainWindow.xaml.cs        → WPF shell, BlazorWebView, zoom, DevTools
App.xaml.cs               → DI, Serilog, startup, error handling
Pages/*.razor (37)        → @page routes (CapacityPlanning, DiagnosticsRoadmap, …)
Components/Shared/*.razor → DynamicPanel, StatCard, DataGrid, DeadlockViewer
Components/Layout/*.razor → NavMenu, MainLayout, DashboardToolbar
Data/Services/*.cs (20)   → Azure Blob, Assessment, ServerMode, RBAC, ForecastService
Data/Models/*.cs          → POCOs
Data/Caching/*.cs         → SQLite WAL cache, delta-fetch, 2-week retention, eviction
Config/                   → appsettings, version, dashboard-config
```
CSS/patterns: `.claude/docs/css-design-system.md`, `.claude/docs/patterns.md` — read only when styling/adding pages.

## Conventions
- .cs: `/* In the name of God, the Merciful, the Compassionate */`
- .razor: `<!--/* In the name of God, the Merciful, the Compassionate */-->`
- Credentials: `CredentialProtector.Encrypt/Decrypt` (AES-256-GCM, DPAPI)
- Connections: explicit DB name (`"master"` for non-SQLWATCH). `HasSqlWatch` defaults `false`.
- DI: nullable optional params (`Service? svc = null`)
- Background: `_ = Task.Run(async () => { … })`

## Build
```
dotnet build SqlHealthAssessment.sln
dotnet publish -c Release -r win-x64
./increment-build.ps1   # bumps Config/version.json
```
Close app first — exe lock blocks copy.

## Git
Prefix: `feat:` `fix:` `docs:` · Co-author: `Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>`
Branch: `main` → PR target: `master` · Don't commit: `.env`, creds, PDFs, `bin/`, `obj/`

## Key Subsystems
- **Baseline overlay**: DashboardToolbar toggle → DynamicDashboard fetches 7-day-old cache → TimeSeriesChart dashed overlay
- **Deadlock viewer**: panelType `"Deadlock"` → DeadlockViewer parses `system_health` XEvent XML
- **Forecasting**: ForecastService (linear regression) → `/capacity` shows disk + CPU trends
- **Maturity roadmap**: `/diagnostics-roadmap` maps 489 sql-checks.json checks → 5 maturity levels via QuickCheck
- **Debug logging**: UserSettingsService toggle → `LoggingLevelSwitch` flips at runtime (no restart)

## Don't
- Tailwind — uses CSS variable design system
- Bulk-restyle RDL — expression-bound styles make it futile
- `CreateIfNotExistsAsync` on Azure Blob — fails with directory-scoped SAS
- Assume WebView2 available — handle server mode fallback
- Hardcode SQLWATCH connection — some servers don't have it
- `<` in Razor `@code` switch — Razor reads it as HTML; use if/else
- LLM-generated SQL — user owns SQL; focus on C#/Blazor
