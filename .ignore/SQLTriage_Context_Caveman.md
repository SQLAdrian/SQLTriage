<!-- Bismillah ar-Rahman ar-Raheem -->
# SQLTriage — Ctx (v0.2 — ultra-caveman)

**Use:** Load once per chat. Replaces PRD/strategy. Update post-gate.

**Meta:** C#/Blazor/WPF/WebView2/SQLite. Win-only. GPLv3. SQLAdrian/LiveMonitor

---

## State
W0 done. Rename:SHA→SQLTriage complete. Basmalah:100% .md. Attrib:Claude,Kilo,Cline,AWS,Grok. Build:pending. Theme:def/rr/amg ready. Week:1 start. ICheckRunner, ChartTheme integ.

---

## Arch
Opt D (In-Place Harden). Keep 80% codebase. Add: ICheckRunner (abstraction), Governance (capped), Translator (rules-templates), AuditLog (queue+HMAC), RBAC (Argon2id), ChartTheme, ThemeSwitcher(3). DI reg App.xaml.cs.

---

## Stack
Runtime:.NET8 net8.0-windows WPF+BlazorHybrid
UI:Blazor+Razor/WebView2 + ApexCharts. CSS custom (Tailwind mv v1.1)
DB:SQLite WAL (cache) + SQL Server 2016+ (target)
Auth:Windows + opt SQL. RBAC pend.
Log:Serilog file daily
Cfg:JSON /Config
Rpt:QuestPDF PDF
CI:GitHub Actions (codeql, InnoSetup release)
Inst:Inno Setup (MSI opt)

---

## Concepts
Check: SQL health/VA rule Config/queries.json. Tags: quick(≤60s)|full.
ICheckRunner: RunSubsetAsync(quick|full|custom). 55s/300s timeout.
Governance: Capped P1≤40 cat≤60 tot≤100. Wgts Sec30% Perf25% Rel25% Cost15% Comp5%. Hot-reload JSON.
Translator: Rules-engine → exec summary. 40 Quick. Versioned cache.
AuditLog: Queue+checkpoint. SQLite+EventLog. HMAC tamper. 30d ret.
RBAC: Roles:Admin,DBA,Auditor,ReadOnly. Argon2id (Konscious). Bootstrap:1st-run Win user→Admin.
ChartTheme: [data-theme] CSS attr. Pers vars: rad(2–16px), trans(80–500ms), shadow, glass(0–12px). Apex palettes per theme.

---

## Files
/Config: queries.json(60+), governance-weights.json, control_mappings.json, appsettings.json, version.json
/Data/Services: UserSettingsService, ChartThemeService, ICheckRunner, Governance, IFindingTranslator, AuditLog, Rbac
/Components/Shared: StatCard, TimeSeriesChart, BlockingTreeViewer
/Pages: Settings(Theme dd), QuickCheck, FullAudit, About
/wwwroot/css/app.css: 217KB theme sys, all var()
/installer: SQLTriage.iss
/.ignore: PRD, Strat, WORKFILE, DECISIONS×15, PRE-MORTEM3, OpusAna, Review/

---

## Cfg
Theme: def|rr|amg
Quick timeout: 55s (60s−5s buf)
Full timeout: 300s (cfg queries.json)
Cache: SQLite WAL maint4h
Audit: ON 30d tamperON
RBAC: W3
Governance: W2
Translator: W2

---

## Tasks (W1–W6)
W1: SqlQueryRepository (load queries.json, quick filter), ChartTheme palette hook, tag 40 Quick, verify ≤60s test
W2: GovernanceService (capped+hot-reload), IFindingTranslator (templates+cache inv), ErrorCatalog(60), equiv test QuickvsFull drift<5%
W3: AuditLogService (queue+checkpoint), RBAC (Argon2id+guards), Onboarding S1 (Admin bootstrap)
W4: ReportService+QuestPDF (3pg), RoleGuard all pages, Startup chain validation (tamper), Gate2 review
W5–W6: Polish (err,a11y,perf), MSI(opt), screens×16, RC

---

## Constraints
Build: dotnet build -warnaserror (post-W1 T1)
OS: Win10/11, Server2016+ (WebView2 req)
SQL: SQL Server 2016+ (Azure SQL MI ok, Azure DB part)
Mem: 200MB base + cache (def 500MB)
DB: Agentless — std SQL connections only

---

## AI Quickref
Edit: +basmalah header. Follow CLAUDE.md. Co-Authored-By trailer. No SQL unless asked. Preserve patterns, fix gaps, no rewrite.
Comm: No pleasantries. Concise. Code + "Done." Explain only if "Why?".
Test: Build OK. dotnet test (if tests). Smoke test launch.
Style: PascalCase methods/props; camelCase locals. XML docs public. Nullable on. Async suffix.

---

## Zip (Current)
W0: rename done, basmalah100%, theme CSS ready, ChartThemeService reg'd, MainWindow theme inject, Settings dd wired, CSS var sweep 386, build pend.
W1 next: SqlQueryRepository + Quick tags + ChartTheme Apex palette hook.

---

## Decisions (D01–D15)
D01: Opt D (In-Place Harden) — no rewrite
D02: SqlQueryRepository abstraction (ICheckRunner) — sep Quick/Full
D03: Governance capped (P1≤40,cat≤60,tot≤100)
D04: governance-weights.json hot-reload (IOptionsMonitor)
D05: Argon2id (Konscious) for RBAC pwds
D06: AuditLog single-writer + checkpoint
D07: QuestPDF reports (no live chart embed)
D08: ICheckRunner timeout: 55s Quick / 300s Full
D09: Basmalah Intent Lock — every file Islamic header
D10: Translator rules-engine (40 Quick templates) + versioned cache
D11: AuditLog startup HMAC check — tamper → refuse writes
D12: Translator cache key: (findingId,translatorVer,weightsHash)
D13: ChartTheme CSS vars + personality (rad/trans/shadow)
D14: ThemeSwitcher UI (def/rr/amg)
D15: RoleGuard + Admin bootstrap (Onboarding S1)

---

## Links
PRD:.ignore/SQLTriage_PRD.md
Strat:.ignore/DEVELOPMENT_STRATEGY.md
Tasks:.ignore/WORKFILE_remaining.md
Checklist:.ignore/SQLTriage_Release_Checklist.md
Decisions:.ignore/DECISIONS/D01-D15.md
PreMortem:.ignore/PRE-MORTEM_PASS3.md
Opus:.ignore/OPUS_ANALYSIS_COMPLETE_2026-04-18.md
Contrib:CONTRIBUTORS.md
Build:DEPLOYMENT_GUIDE.md

---

Tokens: ~320. Update post-gate. — Adrian
