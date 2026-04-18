<!-- In the name of God, the Merciful, the Compassionate -->
<!-- Bismillah ar-Rahman ar-Raheem -->

# SQL Health Assessment

Blazor Hybrid WPF (.NET 8), single-exe Windows desktop. Falls back to Blazor Server if WebView2 missing.

## Search first
- Grep, don't read. `app.css` is 7500 lines; `.ignore/*.md` are huge.
- Scope to `Pages/`, `Data/Services/`, `Components/` (95% of changes).
- `.claudeignore` excludes `bin/`, `obj/`, worktrees, PDFs, SQL scripts, root-level docs.
- Don't re-read files already read this session.
- Styling/new-page work: read `.claude/docs/css-design-system.md` and `.claude/docs/patterns.md` first.

## Layout
- `MainWindow.xaml.cs` — WPF shell, BlazorWebView, zoom, DevTools
- `App.xaml.cs` — DI, Serilog, startup
- `Pages/*.razor` (37) — `@page` routes
- `Components/{Shared,Layout}/*.razor` — DynamicPanel, StatCard, DataGrid, DeadlockViewer, NavMenu
- `Data/Services/*.cs` (20) — Blob, Assessment, RBAC, Forecast, Alert, Notification
- `Data/Models/*.cs` — POCOs · `Data/Caching/*.cs` — SQLite WAL, 2-wk retention, delta-fetch
- `Config/` — appsettings, version, dashboard-config

## Conventions
- Basmalah header (non-negotiable): `.cs` → `/* In the name of God, the Merciful, the Compassionate */`; `.razor` → `<!--/* … */-->`
- Credentials: `CredentialProtector.Encrypt/Decrypt` (AES-256-GCM + DPAPI)
- Connections: explicit DB name (`"master"` for non-SQLWATCH); `HasSqlWatch` defaults `false`
- DI: nullable optional params (`Service? svc = null`)
- Background: `_ = Task.Run(async () => { … })`
- Commits: `feat:`/`fix:`/`docs:`; co-author `Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>`; branch `main` → PR `master`

## Build
```
dotnet build SqlHealthAssessment.sln
dotnet publish -c Release -r win-x64
./increment-build.ps1   # bumps Config/version.json
```
Close the running exe first (file lock).

## Key subsystems (one-liners)
- Baseline overlay: DashboardToolbar toggle → 7-day-old cache → dashed overlay on TimeSeriesChart
- Deadlock viewer: `panelType="Deadlock"` parses `system_health` XEvent XML
- Forecasting: `ForecastService` linear regression → `/capacity` (disk + CPU)
- Maturity roadmap: `/diagnostics-roadmap` maps 489 sql-checks → 5 levels via QuickCheck
- Debug logging: UserSettingsService toggle flips `LoggingLevelSwitch` at runtime

## Don't
- Use Tailwind (CSS variable design system is authoritative)
- Bulk-restyle RDL (expression-bound styles make it futile)
- Call `CreateIfNotExistsAsync` on Azure Blob (fails with directory-scoped SAS)
- Assume WebView2 is available (handle server-mode fallback)
- Hardcode SQLWATCH (some servers don't have it)
- Use `<` in Razor `@code` switch (Razor reads it as HTML; use `if/else`)
- Write or edit SQL (user owns SQL; focus on C#/Blazor)
- Commit after every change (commit only when explicitly asked)
