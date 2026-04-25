<!-- In the name of God, the Merciful, the Compassionate -->
<!-- Bismillah ar-Rahman ar-Raheem -->

# SQLTriage

Blazor Hybrid WPF (.NET 8), single-exe Windows desktop. Falls back to Blazor Server if WebView2 missing.

## Search first
- Grep, don't read. `app.css` is 7500 lines; `.ignore/*.md` are huge.
- Scope to `Pages/`, `Data/Services/`, `Components/` (95% of changes).
- `.claudeignore` excludes `bin/`, `obj/`, worktrees, PDFs, SQL scripts, root-level docs.
- Don't re-read files already read this session.
- Styling/new-page work: read `.claude/docs/css-design-system.md` and `.claude/docs/patterns.md` first.

## Context discipline (read before every session)

### Index-first policy
Before reading source files, check if an index covers your task:

| Before you touch… | Read this index first |
|---|---|
| Styling, themes, CSS tokens | `.claude/docs/css-design-system.md` |
| Any file under `Data/Services/` | `.claude/docs/services-index.md` |
| Any file under `Pages/` | `.claude/docs/pages-index.md` |
| Any file under `Components/Shared/` | `.claude/docs/components-index.md` |
| Data catalogues / config registries | `.claude/docs/checks-catalog.md` |
| Coding style, commit rules, naming | `.claude/docs/conventions.md` |
| Architectural patterns, idioms | `.claude/docs/patterns.md` |
| Project state, scope, decisions, weeks remaining | `.ignore/SQLTriage_Context_Caveman.md` |

**Hard rule:** If the task can be answered from an index, **do not read the source files**. Grep the index, then read only the single file you need to edit.

### Anchor-driven lookup (staleness-proof)
Every index row includes an `Anchor` column. This is NOT a source-file marker — it's a **search hint**. To find current code:
1. Use the Anchor value to construct a grep pattern: `grep -n "EvaluateAllAsync|OnInitializedAsync|<purpose keyword>" <file>`
2. `Read` the file starting at the returned line (± ~20 lines for context)
3. If grep returns nothing (marker pattern stale), broaden search: `grep -rn "<ClassName>|<method stub>" <file>` or semantic intent keywords from the index row

This guarantees the LLM interrogates live code, not stale index line numbers, even if index summaries are out of date.

### File-read discipline
- Never `Read` a file >200 lines without `offset`+`limit`. Target the section.
- Never re-read a file already present in this session's tool results — grep the existing output instead.
- Always pass `head_limit` on `Grep`. Default 20. Bump deliberately.
- Prefer `output_mode: "count"` or `"files_with_matches"` before `"content"`.

### Post-compact behaviour
After a context compact, do NOT restate project state. Authoritative sources:
- `.claude/docs/README.md` (index of indexes)
- Any `.ignore/*_Context_*.md` file (project snapshot, if present)
- `memory/*.md` (locked decisions)

If the summary would re-derive these, say "state per indexes + memory" instead.

### Chat output cap
- Default response ≤40 words. Explain only when asked.
- No preambles ("Let me…", "I'll check…"). State what you did; show the diff.
- No trailing summaries of what you just changed — the diff is visible.

### Subagent policy
- For "find every reference to X across N files" or "audit Y across the repo": spawn an Explore subagent with a short self-contained brief. Its summary (~500 tok) replaces ~5k of raw grep in main context.
- Only spawn when explicitly asked or when the search will return >50 hits.

## For AI Assistants: Context Navigation (llmck)

This repo includes **llmck** — an index-first context engineering system that helps LLMs navigate large codebases efficiently.

**Start here when using AI:**
1. Read `.claude/docs/README.md` — navigation guide
2. Read `.claude/docs/services-index.md` — all 34 services mapped
3. Then grep/read specific files (code-files-only: `.cs`, `.xaml`, `.razor`, `.md` only)

**Never read:** `.json`, `.sql`, `.xml`, `.csv`, `.config`, `.resx` (infrastructure files, not logic)

**Generate/validate indexes locally:**
```bash
pip install -e https://github.com/afsultan/llm-context-kit.git
python -m llmck sync       # regenerate indexes
python -m llmck validate   # check for drift
```

See: https://github.com/afsultan/llm-context-kit

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
dotnet build SQLTriage.sln
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

## Model / thinking level
Flag if current model seems mismatched — one line only, no interruption:
- **Sonnet default** — routine tasks: file edits, rename, grep, boilerplate, test writing, CI fixes.
- **Sonnet + extended thinking** — ICheckRunner budget logic, AuditLog HMAC chain, concurrency, security primitives.
- **Opus** — gate reviews (Wk 2/4/6/8), architectural decisions that conflict with `.ignore/OPUS_ANALYSIS_COMPLETE_*` or `memory/project_sqltriage_v1_lockin.md`, new scope proposals.

**Trigger:** if a task touches >3 interdependent services, changes a locked decision (see lock-in memory), or involves security-critical code — note "this may warrant Opus" once and continue.

## Don't
- Use Tailwind (CSS variable design system is authoritative)
- Bulk-restyle RDL (expression-bound styles make it futile)
- Call `CreateIfNotExistsAsync` on Azure Blob (fails with directory-scoped SAS)
- Assume WebView2 is available (handle server-mode fallback)
- Hardcode SQLWATCH (some servers don't have it)
- Use `<` in Razor `@code` switch (Razor reads it as HTML; use `if/else`)
- Write or edit SQL (user owns SQL; focus on C#/Blazor)
- Commit after every change (commit only when explicitly asked)
- Embed `<style>` blocks in `.razor` pages — put CSS in `wwwroot\css\<PageName>.css` and add `@import '<PageName>.css';` at the top of `wwwroot\css\app.css` (after the basmalah header)

## Working from worklists / handoff docs
1. **Claimed ≠ implemented.** Every ✅ in a worklist is a claim. Before building on it, grep/read the named file and confirm the code matches. If claim and code disagree, trust the code and flip status back to pending.
2. **Don't expand scope from handoff docs.** If a doc says "5 roles" but the model has 3, reimplement the 3 faithfully. Add the other 2 only when explicitly asked.

## Code quality rules
3. **Warning budget.** After any change, `dotnet build` warning count must not increase. If it does, fix or explain in one line before finishing.
4. **OS-gated APIs.** Windows-only APIs (`EventLog`, registry, DPAPI, WMI) go behind `[SupportedOSPlatform("windows")]` helpers. Pass cross-platform enums across the boundary. Do not sprinkle `OperatingSystem.IsWindows()` at call sites.
5. **Constant-time on secrets.** Password, HMAC, and token comparisons use `CryptographicOperations.FixedTimeEquals`. Never `==` or `SequenceEqual`.
