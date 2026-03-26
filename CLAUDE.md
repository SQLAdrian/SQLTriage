# SQL Health Assessment

Blazor Hybrid WPF app (.NET 8). Single-exe Windows desktop. Falls back to Blazor Server when WebView2 unavailable.

## Search Strategy
- Grep, don't read. `app.css` is 7500 lines — grep for the class you need.
- Scope searches to `Pages/`, `Data/Services/`, `Components/` — 95% of changes happen there.
- `.claudeignore` blocks 34 root docs, SQL scripts, `bin/`, `obj/`, worktrees, PDFs.

## Architecture
```
MainWindow.xaml.cs        → WPF shell, dynamic BlazorWebView, zoom, DevTools
App.xaml.cs               → DI, Serilog, startup, error handling
Pages/*.razor (35)        → @page routes
Components/Shared/*.razor → DynamicPanel, StatCard, DataGrid
Components/Layout/*.razor → NavMenu, MainLayout, DashboardToolbar
Data/Services/*.cs (19)   → Azure Blob, Assessment, ServerMode, RBAC
Data/Models/*.cs          → POCOs
Data/Caching/*.cs         → SQLite WAL cache, delta-fetch, eviction
Config/                   → appsettings, version, dashboard-config
```
CSS/patterns docs: `.claude/docs/css-design-system.md`, `.claude/docs/patterns.md` — read only when styling or adding pages.

## Conventions
- .cs header: `/* In the name of God, the Merciful, the Compassionate */`
- .razor header: `<!--/* In the name of God, the Merciful, the Compassionate */-->`
- Credentials: `CredentialProtector.Encrypt/Decrypt` (AES-256-GCM, machine-bound DPAPI)
- Connections: explicit DB name (`"master"` for non-SQLWATCH). `HasSqlWatch` defaults `false`.
- DI: nullable optional params (`Service? svc = null`)
- Background: `_ = Task.Run(async () => { ... })`

## Build
```
dotnet build SqlHealthAssessment.sln
dotnet publish -c Release -r win-x64
./increment-build.ps1                  # bumps Config/version.json
```
Close running app first — exe lock blocks copy.

## Git
Prefix: `feat:`, `fix:`, `docs:` · Co-author: `Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>`
Branch: `main` (default), PR target: `master` · Don't commit: `.env`, creds, PDFs, `bin/`, `obj/`

## Don't
- Tailwind CSS — project uses CSS variable design system
- Bulk-restyle RDL reports — expression-bound styles make it futile
- `CreateIfNotExistsAsync` on Azure Blob — fails with directory-scoped SAS
- Assume WebView2 available — handle server mode fallback
- Hardcode SQLWATCH connection — some servers don't have it
- Read full large files — grep first
