<!-- In the name of God, the Merciful, the Compassionate -->
<!-- Bismillah ar-Rahman ar-Raheem -->

---
Generated: 2026-04-18
Source: OPUS_MEGA_PROMPT_COMPLETE.md (v1.0, Option D)
Pass: 1 of 3
Documents reviewed: COMMENT_20260418_091241.md, WORKFILE_remaining.md, SQLTriage_PRD.md, DEVELOPMENT_STRATEGY.md, SQLTriage_Release_Checklist.md, SQLTriage_Strategic_Blueprint.md, pre-mortem_1.md
Supersedes: prior Opus output of 09:03 (pre-lock-in)
---

# SQLTriage — Opus Architecture Review (Option D, Pass 1)

The ten COMMENT decisions land correctly. Most of what follows is gap-closure, sharpening, and execution detail — not re-litigation. Three sharp items up front:

1. **Scope contradiction — AI/ML.** COMMENT §2 lists "Anomaly detection + SignalR streaming (basic)" as Must-have #11, but the mega-prompt and PRD §5 defer the full AI/ML ONNX pipeline to v1.1. These are not reconcilable as written. **Recommendation:** drop Must-have #11 outright for v1.0. The IQR-based AlertEvaluationService already covers "basic anomaly" without SignalR; the streaming layer is pure v1.1 surface area.
2. **PRD §2.3 scoring is stale.** It still shows the old additive "+5%/+10%" algorithm. COMMENT §4 committed to capped critical-failure + per-framework vector weights. PRD must be rewritten before Gemma week 2, or Gemma will implement the wrong algorithm. **Flag as Week 0 doc-debt blocker.**
3. **Translator ↔ Governance coupling is under-specified.** PRD §7.3 says "Governance scoring uses `Executive.BusinessRisk` as input." But `Executive.BusinessRisk` is itself derived by the translator from check metadata + scores. This is circular unless we split into two phases: (a) raw check scoring → Governance vector → BusinessRisk; (b) translator consumes BusinessRisk for Executive rendering. Document the data flow explicitly or expect Gemma to ship a circular dependency.

The rest is detail.

---

## Part A — Gap Analysis (Option D)

### A.1 Translation substrate — IFindingTranslator

**Design is 80% complete.** Three audience models, cache in SqliteCacheStore, metadata pull from queries.json — all correct.

**Gaps:**
- **Data-flow ambiguity** (see §3 above). Spec the phases: `Check → GovernanceVector → BusinessRisk → Translator`. Translator is a read-only consumer of Governance outputs, not a co-producer.
- **Per-check translation authorship.** 472 VA checks × 3 audiences = 1,416 translation strings. Who writes them? If Gemma generates them from check metadata, quality will be uneven. **Recommendation:** ship v1.0 with a **rules-engine translator** (category + severity → template) for the ~40 Quick Check subset only; full-catalog translation is v1.1. Keep the interface; narrow the implementation.
- **Compliance control IDs (`Executive.ComplianceControls`).** Requires a check→control mapping table. Does not exist yet. Must be authored as `queries.json` fields, not computed. **Must-have addendum:** add `"controls": ["SOC2-CC6.1", "HIPAA-164.312"]` to queries.json schema.
- **Caching key.** TTL 1h + invalidate on VA re-run is fine, but also invalidate on `governance-weights.json` reload (weights change → BusinessRisk changes → Executive changes).
- **Empty-state rendering.** If a check produces no finding (PASS), does translator skip it or return a "no issue" object? Define explicitly — the Governance Report must show PASSes for audit trail.

### A.2 Quick Check ≤60s feasibility

**Achievable with caveats.** The constraints:
- 40 checks × average 0.8s local = 32s sequential; with DOP=4 parallelism, ≈12s. ✅
- Remote SQL (LAN, 2–5ms RTT): multiply by 1.5–2x → 18–25s. ✅
- Remote SQL (WAN, 50–200ms RTT): multiply by 4–6x → 48–75s. **At risk.**
- Cold SQLite cache: first run of the day loads 20–40MB from disk — add 2–4s.

**Required settings:**
- `CommandTimeout = 10s` per check (not 30s default).
- `ConnectionTimeout = 5s`.
- `DOP = min(Environment.ProcessorCount, 4)`.
- **Per-check kill switch:** if any check exceeds 8s, cancel it, log as "timed out — not in Quick Check summary," continue. The ≤60s promise must not be held hostage to one slow DMV.
- **Budget enforcement:** track cumulative elapsed; at 55s total, cancel any in-flight checks and emit partial results with "run full VA for complete picture" CTA.

**What query count works:** Start at 35 checks, not 40. Tag aggressively; under-promise.

**Missing piece:** no `ICheckRunner` interface spec. Gemma needs an abstraction over VulnerabilityAssessmentService that supports `RunSubsetAsync(IEnumerable<string> checkIds, TimeSpan budget, CancellationToken)`. Add to Week 1 scope.

### A.3 Governance scoring — capped + vector

**Capped-critical is the right call.** Open gaps:
- **"Cap 40 points" interpretation.** Per critical finding, or aggregate? If per-finding, 3 critical findings still sum to 120 → need total cap. **Clarify:** per-finding contribution capped at 40, *category* subtotal capped at its weight ceiling, *overall* score capped at 100. Three-level clamp.
- **Vector weights 30/25/25/15/5.** Security at 30% is defensible; Compliance at 5% contradicts the product's audit-prep positioning. If the tagline is "reduce audit preparation from days to minutes," Compliance should be ≥15%. **Recommendation:** 25 Security / 20 Performance / 20 Reliability / 15 Cost / 20 Compliance. Document the rationale in `governance-weights.json` as a comment field.
- **Maturity bands.** Not specified in COMMENT. Propose: 0–20 Emerging / 21–40 Bronze / 41–60 Silver / 61–80 Gold / 81–100 Platinum. Bands must also appear in JSON (reloadable) — never hardcode.
- **Quick-vs-full drift.** Running Quick Check produces a different score than full VA (narrower evidence base). Two choices: (a) show both, labeled; (b) Quick Check shows *indicative* only with disclaimer. Pick (b) for audit integrity. Governance Report (PDF) must be generated only from full-VA data.
- **Runtime reload.** `IOptionsMonitor<GovernanceWeights>` with `OnChange` callback — standard .NET pattern. File-watcher on `Config/governance-weights.json`. Confirmed viable.

### A.4 RBAC + Argon2id

**Choice:** `Konscious.Security.Cryptography.Argon2` (MIT, actively maintained, OWASP-aligned). NOT `Microsoft.AspNetCore.Cryptography.KeyDerivation.Pbkdf2` — PBKDF2 is GPU-weak by modern standards and the prompt asks for Argon2id.

**Parameters (OWASP 2024):** `memorySize=19456KB`, `iterations=2`, `parallelism=1`, `saltLength=16`, `hashLength=32`.

**Five roles (Admin/DBA/ReadOnly/Auditor/Operator) are sufficient for v1.0.** Auditor-separation is correctly scoped (read-only to audit log, no data access).

**Gaps:**
- **Initial admin bootstrap.** First-run UX must force admin creation with strong password (≥12 chars, zxcvbn ≥3). Otherwise SQLite ships with empty user table → everyone is unauthenticated. Add to Onboarding wizard as Step 1.
- **Password reset.** Not in scope? Without email infra it's tricky. For v1.0 ship a local admin-only "reset user password" page — no email.
- **Session timeout.** Not mentioned. Default to 8h idle for Admin/DBA, 4h for Auditor (shorter = less forensics exposure).
- **Page guards.** Every page needs `[Authorize(Roles = "...")]` equivalent — Blazor doesn't have this natively for hybrid apps; implement `RoleGuard.razor` component wrapping page content. Add to Week 4.

### A.5 AuditLog single-writer

**Design is sound.** `BlockingCollection<AuditEvent>` + single consumer Task + DPAPI-machine + 4KB checkpoint + Event Log mirror. The HMAC-chain race condition is solved.

**Gaps:**
- **4KB checkpoint size is too small.** Average audit record ≈ 400–800 bytes encrypted (user, timestamp, action, target, HMAC). 4KB = 5–10 records, so a checkpoint file rotates every ~30s under normal load. File handle churn and FS metadata overhead become real. **Recommendation:** make it configurable; default to 64KB (≈100–150 records). User intuition is right (configurable) per open question #3.
- **Event Log source registration requires admin once.** On first run without admin, log a warning, fall back to file-only, surface a Settings flag: "Elevate once to enable Event Log mirror." Never silent-fail.
- **Chain-validation on startup.** Spec is missing. On app start, read the last checkpoint; walk forward; if HMAC breaks, mark chain "compromised" in a flag file and refuse to write new entries until admin acknowledges. Emit a loud UI banner.
- **HMAC key rotation.** Not specified. 90-day rotation with dual-key grace window (accept old key for 7 days after rotation) is the right pattern.
- **Partial-record recovery.** On startup if last file ends mid-record (power loss mid-write), truncate to last valid HMAC and log the truncation itself as an audit event signed by the new key. Document this as "tamper-evident, not tamper-proof" in PRD §9.

### A.6 ErrorCatalog — ~60 scenarios

**Coverage is adequate for v1.0.** The 10 categories listed in COMMENT §8 cover 80% of field issues. Missing:
- SQL Server edition mismatch (Express lacks features) — user sees cryptic "not supported" error, needs governance-impact translation.
- SQLWATCH-not-installed (HasSqlWatch=false) fallbacks.
- Windows Firewall blocking SQL port (1433/custom).
- TLS/cert trust failures with Azure SQL (Trust Server Certificate).
- Long-running query cancellation (user-initiated vs timeout).

Add these 5. Target 65 scenarios.

**Test structure is correct.** xUnit theory iterating `ErrorCatalog.All` with asserts. Add a golden-file test: serialize the full catalog to JSON, compare to committed snapshot. Catches accidental deletions or field additions.

### A.7 ChartTheme integration points

All `Chart*.razor` components currently exist: `TimeSeriesChart`, `StatCard` (implicit), `BlockingTreeViewer` (not a chart). Missing:
- `GaugeChart.razor` (Governance Dashboard Maturity %).
- `BandedHeatmap.razor` (Risk Register heat-map if Risk Register ships).
- Any chart inside `DynamicPanel` that constructs options inline — audit and refactor to pull from `ChartTheme.Current`.

**Grep pre-work for Week 1:** `Grep "ApexChartOptions" --include="*.razor"` to enumerate all inline-option sites. Fix each.

### A.8 Pre-mortem alignment under Option D

Of the 10 pre-mortem failure modes, highest probability under Option D:
1. **Scope creep** (HIGH) — existing code tempts you to "just fix one more thing." COMMENT §2's 15-task cap is the control; enforce via Friday velocity check.
2. **Legacy-pattern contamination** (MEDIUM) — new services built alongside existing ones risk divergent conventions. Mitigation: `.claude/docs/patterns.md` is the authority; every new service PR must cite it.
3. **Quick Check WAN-latency failure** (MEDIUM) — see A.2. Ship a "Local-only" Quick Check first; WAN gets "Extended Check (≤3min)" fallback.
4. **Translation quality** (MEDIUM) — see A.1. Rules-engine for Quick Check subset only.
5. **Brand rename drift** (LOW–MEDIUM) — Week 0 task, pre-commit hook catches regressions.

Failure modes 6–10 (tooling, installer, marketing) are release-phase issues, not development-phase.

### A.9 Acceptance criteria

PRD §11 coverage check against Must-haves:
- ✅ Clean build, Quick Check ≤60s, Governance score renders, PDF exports, Translator returns 3 outputs, AuditLog validates, ErrorCatalog ≥60, Basmalah on all files.
- ❌ **Missing:** RBAC admin bootstrap completes (first-run), session timeout works, role guards on all pages.
- ❌ **Missing:** AuditLog chain-validation on startup detects tampering (insert a bad HMAC row in test, expect refusal).
- ❌ **Missing:** Per-check timeout honored (inject slow DMV in test, verify Quick Check still finishes ≤60s).
- ❌ **Missing:** `governance-weights.json` hot-reload (edit file, verify next Governance render uses new weights).

Add these five criteria to PRD §11 before Week 4.

---

## Part B — Design Enhancements

### B.1 SqlQueryRepository

**Do file-watcher.** `FileSystemWatcher` on `Data/Sql/` with 500ms debounce → `Reload()`. Manual reload via Settings page as fallback. Both, not either.

**queries.json schema (final):**
```jsonc
{
  "VA-207": {
    "file": "HealthChecks/MaxDopRecommendation.sql",
    "description": "MAXDOP configuration vs workload",
    "category": "Performance",
    "severity": "MEDIUM",
    "quick": true,
    "audience": ["dba", "it"],          // Exec only if business-visible
    "governance": { "Performance": 0.4 }, // Override default category weight
    "controls": ["SOC2-CC7.2"],         // Compliance mapping
    "timeoutSec": 8                     // Per-check override
  }
}
```

**Location: `Config/queries.json`, not `Data/Sql/queries.json`** (open question #4). Rationale: `Data/Sql/` is pure SQL files; metadata is configuration; Settings-page edit-ability is a goal.

**Hot-reload caveat:** if the file is malformed mid-edit (user saving in nano), FileSystemWatcher fires before the write completes. Catch `JsonException`, keep the old dictionary, log, surface as yellow banner. Never crash on bad JSON.

### B.2 GovernanceService

- **Per-category cap first, overall cap second.** Run vector: sum check-scores within category (each capped at 40 for critical), clamp subtotal to category weight, sum subtotals (already ≤100 by construction). No "+5% if X" arithmetic — only multiplicative weights.
- **Quick vs full drift:** separate methods. `ComputeIndicativeAsync(QuickCheckResults)` returns `GovernanceReport { IsIndicative = true }`. `ComputeFullAsync(VaResults)` returns `IsIndicative = false`. Governance Dashboard renders a "(Indicative — run Full VA for final score)" chip when Indicative.
- **Maturity bands:** put in `governance-weights.json` under `"bands"` key; never hardcode.
- **Finding cache:** persist `GovernanceReport` to SqliteCacheStore keyed by `(serverId, vaRunId)`. Invalidates naturally when VA re-runs.

### B.3 IFindingTranslator

- **Cache per-finding, not per-refresh.** Key: `(findingId, translatorVersion)`. `translatorVersion` is a compiled-in constant that bumps when template rules change — invalidates all translations on upgrade without explicit DB migration.
- **Recompute on:** VA re-run (finding data changes), governance-weights reload (Executive.BusinessRisk depends on scores).
- **Link `FindingExecutive.BusinessRisk` to Governance risk level:** yes, but derive it from per-finding score × category weight mapped to CRITICAL/HIGH/MEDIUM/LOW bands — *not* from the overall score. Documented in PRD §7.3 as the authoritative chain.
- **Filter enum:** `TranslateAsync(findingId, TargetAudience? filter)` — `filter=null` returns all three; `filter=Exec` returns only Executive. Callers avoid paying translation cost they don't need.

### B.4 AuditLogService

- **DPAPI-machine key, 90-day rotation, 7-day dual-key grace.** Key stored in `%PROGRAMDATA%\SQLTriage\audit\hmac.key` (DPAPI-machine encrypted).
- **Checkpoint naming:** `audit_YYYYMMDD_NNNN.enc` where NNNN increments per rotation trigger. Rotate by size (64KB default), not by hour — avoids file proliferation on quiet days.
- **Partial-file recovery:** startup reads each checkpoint, validates HMAC chain, on failure truncates and logs the truncation as a signed audit event. Bullet-pointed in PRD §6.2.
- **Event Log fallback:** try-register source; if denied, set `_eventLogEnabled=false`, surface banner, continue. Never crash.
- **Graceful shutdown:** on `IHostApplicationLifetime.ApplicationStopping`, drain queue, flush, fsync. 2-second budget; if exceeded, log "audit log flush incomplete" to Event Log and proceed (crash-safety over completeness).

### B.5 QuestPDF ReportService

- **Pages:** (1) Cover + Executive Snapshot, (2) Risk Register + Config Drift, (3) Action Plan + Cost-of-Downtime. Appendix page 4 optional for raw findings (user toggle).
- **Margins 1"; Playfair Display headings (embed font, MIT); Inter body; emerald/amber/rose severity bands.**
- **Cost of Downtime calculator:** position on page 3 as a widget with assumptions footnoted. Inputs: hourly revenue, affected user count, MTTR assumption. Defaults editable per-server in Settings. Output: "Estimated exposure: $X/hr × Y hrs MTTR = $Z per incident."
- **Chart embedding:** `ApexCharts.ToBase64Image()` does NOT exist in Blazor-ApexCharts; rendering to image requires a headless JS call. Simpler: use QuestPDF native bar/gauge primitives for v1.0, defer live-chart embeds to v1.1. **This is a meaningful scope reduction — confirm with user.**

---

## Part C — 8-Week Execution Plan (Option D)

DEVELOPMENT_STRATEGY §Phases shows a 4-week plan; the prompt asks for 8 weeks. Reconcile as **4-week implementation + 4-week stabilization/beta/release**.

| Wk | Owner | Deliverables |
|----|-------|--------------|
| 0 | Opus | Doc-debt sweep: rewrite PRD §2.3 scoring, spec Translator ↔ Governance data flow, spec ICheckRunner, lock governance-weights.json schema. Sign off Translator + Governance + AuditLog designs. |
| 0.5 | Gemma | Build cleanup (CA1416, nullable), brand rename, version bump, basmalah pre-commit hook. |
| 1 | Gemma | SqlQueryRepository + queries.json (Config/) + quick tags + ICheckRunner. ChartTheme singleton + integrate Chart*.razor. |
| 2 | Gemma | GovernanceService revised (capped+vector+JSON reload). Quick Check UX wiring (≤60s budget enforcement). **Gate 1.** |
| 3 | Gemma | IFindingTranslator (rules-engine, Quick subset). ErrorCatalog 65 scenarios + tests. |
| 4 | Opus + Gemma | Opus gate review (Translator output quality, governance drift handling, PDF page specs). Gemma: AuditLogService single-writer + checkpoint + Event Log. RBAC Argon2id + Auditor role + RoleGuard. **Gate 2.** |
| 5 | Gemma | QuestPDF ReportService (3 pages + Cost-of-Downtime). Onboarding wizard (admin bootstrap + Quick Check kick-off). |
| 6 | Gemma | Unit tests ≥80%, integration tests, golden-file tests for ErrorCatalog. Settings UI for weights/catalog edits. **Gate 3.** |
| 7 | Both | Beta smoke test on clean Win10/Win11 VMs. Fix P1 bugs only. Scope freeze. |
| 8 | Both | Release candidate. Acceptance criteria validation. Basmalah audit. Screenshots. **Gate 4 → v1.0 tag.** |

**Opus checkpoints:** Wk 0 (design), Wk 4 (mid-review), Wk 8 (release gate). Three calls, ~$15–25 total.

---

## Part D — Gate Review Prompts

### Gate 1 (Week 2)
```
Gate 1 — SqlQueryRepository + ChartTheme + Quick Check tagging.

Artifacts delivered:
- Data/Services/SqlQueryRepository.cs (+ FileSystemWatcher reload)
- Config/queries.json (~40 checks tagged quick:true, audience, controls, governance overrides)
- Data/Services/ChartTheme.cs singleton
- All Chart*.razor pulling from ChartTheme.Current
- Data/Services/ICheckRunner.cs + QuickCheckRunner.cs with budget enforcement

Validate against PRD §8, DEVELOPMENT_STRATEGY Week 1, COMMENT §9/§10:
1. Does Quick Check complete ≤60s on local SQL with 40 checks? Timing evidence?
2. Are ChartTheme colors (emerald/amber/slate/rose) applied consistently? Screenshot diff vs pre-change?
3. Does queries.json hot-reload survive malformed JSON without crash?
4. ICheckRunner abstraction — can it accept subset by tag or explicit list?

Sign off OR list blockers with severity. Do not proceed to Week 3 if any P1 blocker open.
```

### Gate 2 (Week 4)
```
Gate 2 — GovernanceService + IFindingTranslator + ErrorCatalog.

Artifacts delivered:
- Data/Services/GovernanceService.cs (capped per-finding + per-category + overall; vector weights from Config/governance-weights.json; maturity bands configured; hot-reload via IOptionsMonitor)
- Data/Services/FindingTranslator.cs + FindingDba/ItManager/Executive models; rules-engine covering Quick Check subset
- Data/Services/ErrorCatalog.cs (65 scenarios) + SQLTriage.Tests/ErrorCatalogTests.cs (golden-file + field coverage)
- Data flow: Check → GovernanceVector → BusinessRisk → Translator (non-circular, documented)

Validate against PRD §2.3 (rewritten), §7, §4:
1. Does capped-scoring clamp at all three levels (finding/category/overall)?
2. Does translator return all three audiences with non-empty, non-generic content for the 40 Quick Check scenarios?
3. Do ErrorCatalog tests assert ≥95% field coverage + golden-file match?
4. Does governance-weights.json hot-reload trigger a translation cache bust?

Sign off OR list blockers. Opus call here is the highest-value gate — translator quality is the differentiator.
```

### Gate 3 (Week 6)
```
Gate 3 — AuditLog + RBAC Argon2id + ReportService.

Artifacts delivered:
- Data/Services/AuditLogService.cs (single-writer BlockingCollection<AuditEvent>, DPAPI-machine HMAC, 64KB checkpoint rotation, Event Log mirror with graceful fallback, startup chain validation)
- Data/Services/RbacService.cs (Argon2id via Konscious.Security.Cryptography; 5 roles; admin bootstrap in Onboarding)
- Components/Shared/RoleGuard.razor wrapping protected pages
- Data/Services/ReportService.cs (QuestPDF; 3 pages; Cost-of-Downtime widget; no live chart embeds — native primitives)
- Pages/GovernanceReport.razor export action

Validate against PRD §9, §6.2, §6.3:
1. Does AuditLog refuse to write new entries if startup chain validation fails? Test with tampered checkpoint.
2. Does Argon2id verify match known-good hash vectors? (OWASP test vectors)
3. Does QuestPDF render in <3s for a 50-finding report? Memory stable?
4. Does RoleGuard prevent Auditor from seeing data pages but allow audit-log page?

Sign off OR list blockers.
```

### Gate 4 (Week 8)
```
Gate 4 — Release candidate v1.0.

Artifacts delivered:
- All Must-haves per COMMENT §2 complete (minus deferred item #11)
- Unit tests ≥80% coverage (Coverlet report attached)
- Basmalah header on every .cs/.razor/.css/.js/.md file (pre-commit hook active, CI lint passing)
- Clean build (zero warnings), single-exe publish (win-x64)
- Beta smoke test completed on Win10 + Win11 clean VMs (results attached)
- 16 screenshots captured; website copy updated to SQLTriage brand
- Sample Governance Report PDF published

Validate against PRD §11 (updated) + SQLTriage_Release_Checklist.md Phase 7:
1. EXE launch → Quick Check results ≤60s (P95, measured on target hardware)?
2. All acceptance criteria from §11 pass? List any ❌.
3. Any P1 bugs open? (v1.0 blocks on zero P1s.)
4. Basmalah audit: grep every tracked text file; report count of compliant/non-compliant.

Sign off for v1.0 tag OR list blockers. This is the release gate.
```

---

## Part E — Top 10 Gemma Implementation Prompts

Each prompt is designed for Gemma 4 9B. Keep them blunt and paste-ready.

### Prompt 1 — Build Cleanup (Wk 0.5)
```
Task: Make SQLTriage.sln build with zero warnings.

Steps:
1. Open SQLTriage.csproj (and any sub-projects). Add <Nullable>enable</Nullable> and <TreatWarningsAsErrors>false</TreatWarningsAsErrors> (keep warnings visible but not blocking for now).
2. Add [assembly: SupportedOSPlatform("windows")] to AssemblyInfo.cs OR set <SupportedOSPlatformVersion>10.0.17763.0</SupportedOSPlatformVersion> in csproj. Goal: silence CA1416.
3. Run `dotnet build` and iterate on CS8602/CS8604 (null dereference, possible null arg). Fix by either `?.` access or null-guard; do NOT add `!` null-forgiving unless you can prove non-null.
4. After all warnings cleared, flip <TreatWarningsAsErrors>true</TreatWarningsAsErrors>.

Quality checks:
- `dotnet build` output ends with "0 Warning(s), 0 Error(s)".
- No `!` operators added without a comment explaining provable non-null.
- Basmalah header intact on every file you touched.

Do NOT: refactor architecture, rename anything, add new features.
```

### Prompt 2 — SqlQueryRepository + queries.json (Wk 1)
```
Task: Implement Data/Services/SqlQueryRepository.cs — runtime loader for all Data/Sql/**/*.sql files, with hot-reload via FileSystemWatcher and metadata from Config/queries.json.

Files to create:
- Data/Services/SqlQueryRepository.cs (interface ISqlQueryRepository with Get(id), GetAll(), GetByTag(tag), Reload())
- Config/queries.json (see schema below)
- Data/Models/QueryMetadata.cs (POCO matching schema)
- Register in App.xaml.cs DI as singleton

Schema for Config/queries.json entries:
{ "VA-001": {
    "file": "HealthChecks/BackupValidation.sql",
    "description": "...",
    "category": "Reliability",
    "severity": "HIGH",
    "quick": true,
    "audience": ["dba","it","exec"],
    "governance": { "Reliability": 0.5 },
    "controls": ["SOC2-CC7.4"],
    "timeoutSec": 8
} }

Behavior:
- On construction: load all .sql files (file path from queries.json), read text, populate dictionary.
- FileSystemWatcher on Data/Sql/ and Config/queries.json — debounce 500ms, call Reload() atomically (build new dict, swap).
- Reload() catches JsonException: keep existing dict, log warning, do not throw.
- Thread-safe Get() via ImmutableDictionary.

Quality checks:
- Unit test: seed 3 .sql files + queries.json, verify Get() returns content.
- Unit test: edit queries.json mid-test with malformed JSON, verify old dict still returned, warning logged.
- Unit test: Reload() called concurrently from 10 threads, no exceptions.

Basmalah header on every new file.
```

### Prompt 3 — ChartTheme singleton (Wk 1)
```
Task: Implement Data/Services/ChartTheme.cs + integrate into every Chart*.razor.

Palette (per PRD §7.2):
- Success/good: #10b981 → #34d399
- Warning/degraded: #f59e0b → #fbbf24
- Critical/failed: #e11d48 → #f43f5e
- Neutral/text: #475569 → #94a3b8
- Grid: #334155 (slate-700)
- Background: "transparent"
- Headings: Playfair Display, 18px, slate-800

Files:
- Data/Services/ChartTheme.cs — static class with properties above + Current singleton + helpers FormatSeries(values), TitleOptions(title), TooltipOptions()
- Update: Components/Shared/TimeSeriesChart.razor, StatCard.razor, GaugeChart.razor (create if missing), and any *.razor grepped from `ApexChartOptions` matches
- Register in DI as singleton

Quality checks:
- Grep for "new ApexChartOptions" across .razor — zero inline constructions after task; all pulled from ChartTheme.Current.
- Visual check: run app, all charts use emerald/amber palette.
- Screenshot diff committed for visual review.

Basmalah header on every new/touched file.
```

### Prompt 4 — GovernanceService revised (Wk 2)
```
Task: Rewrite Data/Services/GovernanceService.cs using capped + vector weights loaded from Config/governance-weights.json.

governance-weights.json schema:
{
  "categories": { "Security": 0.25, "Performance": 0.20, "Reliability": 0.20, "Cost": 0.15, "Compliance": 0.20 },
  "caps": { "perFinding": 40, "perCategory": 100, "overall": 100 },
  "bands": { "Emerging": [0,20], "Bronze": [21,40], "Silver": [41,60], "Gold": [61,80], "Platinum": [81,100] }
}

Algorithm:
1. For each check result: rawScore = severity × categoryWeight. Cap at caps.perFinding.
2. Group by category. Sum per category. Cap at caps.perCategory × category weight × 100.
3. Sum across categories. Cap at caps.overall.
4. Map to band via bands[]. Return GovernanceReport { Score, Band, PerCategory, IsIndicative, TranslatorVersion }.

Files:
- Data/Services/GovernanceService.cs (ComputeFullAsync, ComputeIndicativeAsync)
- Data/Models/GovernanceReport.cs
- Config/governance-weights.json
- Use IOptionsMonitor<GovernanceWeights> for hot-reload

Quality checks:
- Unit test: 3 critical findings → overall score ≤ 60 (not 120).
- Unit test: edit weights file → next call uses new weights (IOptionsMonitor.OnChange fires).
- Unit test: ComputeIndicativeAsync marks IsIndicative=true.

Basmalah header on every new file.
```

### Prompt 5 — IFindingTranslator (Wk 3)
```
Task: Implement Data/Services/FindingTranslator.cs with rules-engine translation for the 40 Quick Check scenarios only (v1.0 scope).

Models (Data/Models/Translation/):
- FindingTranslation { Guid FindingId; FindingDba Dba; FindingItManager ItManager; FindingExecutive Executive }
- FindingDba { string CheckId; string Title; string TechnicalDetails; string TSqlRemediation; Dictionary<string,object> RawData }
- FindingItManager { string BusinessCategory; string SlaImpact; string RemediationEffort; bool RequiresChangeControl; string RelatedFindings }
- FindingExecutive { string PlainLanguageSummary; string BusinessRisk; decimal? EstimatedMonthlyCost; string[] ComplianceControls; string RecommendedAction }

Rules engine:
- Input: CheckResult (from ICheckRunner) + QueryMetadata (from SqlQueryRepository) + GovernanceReport (for BusinessRisk mapping)
- For each of 40 Quick Check IDs, a static template in code mapping (category, severity, raw data) → three audience renderings
- Template format: string interpolation with {placeholders} filled from CheckResult.RawData
- BusinessRisk derived from per-finding score band, NOT overall governance score

Caching: key = (findingId, translatorVersion). Invalidate on VA re-run or governance-weights reload.

Quality checks:
- Unit test: for each of 40 check IDs, translator returns non-null, non-empty Dba/ItManager/Executive with content length > 20 chars each.
- Unit test: same findingId called twice returns cached result (mock cache verifies no re-compute).
- Unit test: governance-weights change → subsequent Translate call bypasses cache.

Basmalah header on every new file. Do NOT author translations for the full 472-check catalog — Quick subset only.
```

### Prompt 6 — ErrorCatalog + Tests (Wk 3)
```
Task: Expand Data/Services/ErrorCatalog.cs to 65 scenarios with governance impact, and write SQLTriage.Tests/ErrorCatalogTests.cs.

ErrorCatalog entry shape:
{ Id: int, Code: string, UserMessage: string, GovernanceImpact: string, Remediation: string, Audience: {Dba,It,Exec}, RelatedChecks: string[] }

Scenarios to cover (65 total):
- 12 Connection: network unreachable, auth failed, timeout, version mismatch, firewall block, TLS/cert failure, named-pipe denied, Azure SQL firewall, cross-subscription, SPN missing, encryption required, login disabled
- 8 Query: timeout, cancelled, deadlock victim, resource governor kill, query memory exceeded, plan compile error, parameter sniffing bad plan, transaction abort
- 8 Permission: SELECT denied, EXECUTE denied, VIEW SERVER STATE denied, VIEW DATABASE STATE denied, ALTER ANY denied, sysadmin required, db_owner required, cross-db permission missing
- 6 Credential: invalid SQL login, invalid Windows, invalid Azure AD, expired password, MFA required, CMK unavailable
- 6 Data shape: malformed XML, unexpected NULL, truncation, collation mismatch, type mismatch, empty result
- 6 Resource: disk full, tempdb full, log full, memory pressure, CPU saturation, worker starvation
- 5 Cache/Storage: corrupt SQLite, WAL mode lost, DPAPI unavailable, blob SAS expired, blob 403
- 5 Config: feature off (CDC/Query Store), SQLWATCH missing, edition mismatch (Express), agent stopped, linked server broken
- 5 Runtime: unhandled exception, OOM, UI thread blocked, GC pressure, app lock timeout
- 4 Edge: first-run no admin user, governance-weights.json missing, queries.json malformed, audit chain broken

ErrorCatalogTests.cs (xUnit):
- [Theory] over ErrorCatalog.All: assert Id>0, Code matches [A-Z]{3}-\d{3}, UserMessage.Length>20, GovernanceImpact.Length>15, Remediation.Length>20, Audience not empty.
- [Fact] GoldenFile: serialize ErrorCatalog.All to JSON, compare to TestData/error-catalog.golden.json. Fail = schema drift or accidental deletion.

Quality checks:
- Coverage: 100% of ErrorCatalog entries pass field-coverage test.
- Golden file committed; any addition/removal requires explicit update.

Basmalah header on all files.
```

### Prompt 7 — AuditLogService single-writer (Wk 4)
```
Task: Rewrite Data/Services/AuditLogService.cs with single-writer queue, DPAPI-machine HMAC, 64KB rotating checkpoints, Event Log mirror, startup chain validation.

Implementation:
- Public API: WriteAsync(AuditEvent evt) — non-blocking enqueue, returns immediately.
- Internal BlockingCollection<AuditEvent> + single Task consumer started in constructor.
- Consumer: HMAC-SHA256 chain (PreviousHash → CurrentHash → Signature); write encrypted record to current checkpoint file.
- Rotate when current file ≥ 64KB: finalize (fsync), rename audit_YYYYMMDD_NNNN.enc, open new file.
- Also write plain-text summary to Windows Event Log (Source: "SQLTriage-Audit"). On registration failure: set _eventLogEnabled=false, log warning, continue.
- DPAPI-machine key stored at %PROGRAMDATA%\SQLTriage\audit\hmac.key. Rotation every 90 days; accept old key for 7-day grace window.
- Startup: read all checkpoint files in order, validate chain, on HMAC break write "audit chain compromised" flag file and refuse further writes until admin acks via Settings page.
- Graceful shutdown (IHostApplicationLifetime.ApplicationStopping): drain queue, flush, fsync with 2s budget.

Quality checks:
- Unit test: 1000 concurrent WriteAsync calls → all persisted, chain valid.
- Unit test: inject bad HMAC in checkpoint, restart → service refuses writes, banner surfaces.
- Unit test: power-loss simulation (truncate last checkpoint mid-record) → startup truncates to last valid, logs truncation event.
- Unit test: Event Log registration fails (sim) → file-only mode, no crash.

Basmalah header. NuGet: no new packages (System.Security.Cryptography + DPAPI are BCL).
```

### Prompt 8 — RBAC Argon2id (Wk 4)
```
Task: Replace CredentialProtector-based password handling in Data/Services/RbacService.cs with Argon2id via Konscious.Security.Cryptography.Argon2.

NuGet: add Konscious.Security.Cryptography.Argon2 (MIT license).

OWASP 2024 parameters:
- memorySize=19456 (KB), iterations=2, parallelism=1, saltLength=16, hashLength=32

SQLite schema migration (Data/Rbac/rbac.db):
- users.password_hash (TEXT, base64)
- users.password_salt (TEXT, base64)
- users.argon2_params (TEXT, JSON {m,t,p,len})
- Migration path: detect existing CredentialProtector-stored passwords, force password reset on next login for those users. Mark column password_legacy=1 until reset.

Files:
- Update Data/Services/RbacService.cs: CreateUser(password), ValidateCredentials(user, password), ChangePassword(user, newPassword)
- Add Data/Migrations/Rbac_002_argon2.sql
- Add 5 roles (Admin/DBA/ReadOnly/Auditor/Operator) seed data
- Add Components/Shared/RoleGuard.razor wrapping protected page content ([Parameter] RequiredRole)

Onboarding integration: Pages/Onboarding.razor first step forces admin creation with password length ≥12 + zxcvbn score ≥3 (use Zxcvbn.Core NuGet).

Quality checks:
- Unit test: OWASP test vector — known password + known salt → expected hash.
- Unit test: ValidateCredentials with wrong password returns false in <500ms.
- Unit test: RoleGuard with RequiredRole="Admin" blocks ReadOnly user.
- Integration test: first-run without admin → Onboarding forces admin creation before any other page accessible.

Basmalah header.
```

### Prompt 9 — QuestPDF ReportService (Wk 5)
```
Task: Implement Data/Services/ReportService.cs using QuestPDF to generate a 3-page Governance Report PDF.

NuGet: QuestPDF (latest 2024.x, MIT — ensure you accept community license in code: QuestPDF.Settings.License = LicenseType.Community;).

API:
- Task<byte[]> GenerateGovernanceReportAsync(Guid serverId, Guid vaRunId)
- Reads GovernanceReport from SqliteCacheStore, FindingTranslations from SqliteCacheStore, server config from ConnectionManager.

Page layout:
- Page 1: Cover (SQLTriage logo, server name, date) + Executive Snapshot (overall score + band + top 3 BusinessRisk items as bullets from FindingExecutive.PlainLanguageSummary).
- Page 2: Risk Register table (Finding ID, Category, BusinessRisk, Summary, Recommended Action). Config Drift section (top 10 highest-impact findings with FindingItManager.SlaImpact).
- Page 3: Action Plan (grouped by RemediationEffort) + Cost of Downtime widget (inputs: hourly revenue, MTTR, affected users; output: $ exposure).

Styling:
- Fonts: Playfair Display headings (embed TTF from wwwroot/fonts/), Inter body.
- 1-inch margins.
- Severity bands: emerald/amber/rose backgrounds for PASS/WARN/FAIL.
- No live chart embeds for v1.0 — use QuestPDF primitives (bars, gauges) from summary numbers.

Files:
- Data/Services/ReportService.cs
- Pages/GovernanceReport.razor (export button → download PDF)
- wwwroot/fonts/PlayfairDisplay-Regular.ttf, Inter-Regular.ttf

Quality checks:
- Unit test: generate report for 50-finding fixture → byte[] not empty, parseable as PDF, 3 pages.
- Perf test: report generation <3s, memory <200MB peak.
- Visual inspection: PDF opens in Adobe Reader + SumatraPDF + browser viewer; fonts render.

Basmalah header.
```

### Prompt 10 — Onboarding + Quick Check UX (Wk 5)
```
Task: Wire Pages/Onboarding.razor to a Quick Check pipeline completing ≤60s from EXE launch.

Flow:
1. App launches → if no admin user, Onboarding Step 1 forces admin creation (RbacService + zxcvbn).
2. Step 2: auto-detect local SQL instances via SqlLocalDb + network scan (optional).
3. Step 3: user picks instance, enters credentials (CredentialProtector).
4. Step 4: "Run Quick Check now? (recommended)" checkbox default ON.
5. On confirm: kick off ICheckRunner.RunSubsetAsync(quickCheckIds, TimeSpan.FromSeconds(55), token).
6. UI: progress bar (X of 40 checks), cancel button (graceful).
7. On complete: route to /governance with IsIndicative=true; show "(Indicative — run Full VA for final score)" chip.
8. Tray icon "Triage Now" menu item → same pipeline.

Budget enforcement (ICheckRunner implementation):
- Per-check timeout 8s default (per-query override via queries.json timeoutSec).
- Global budget 55s tracked cumulative; at 55s cancel in-flight, emit partial results.
- DOP = min(Environment.ProcessorCount, 4).

Files:
- Update Pages/Onboarding.razor
- Data/Services/QuickCheckRunner.cs implementing ICheckRunner
- Update MainWindow.xaml.cs tray icon menu

Quality checks:
- Integration test: seed 40 mock checks each 1s → pipeline finishes ≤40s, all results returned.
- Integration test: seed 40 mock checks, one taking 30s → pipeline cancels slow check at 8s, finishes ≤50s, reports "1 check timed out."
- Manual: clean Windows VM, install MSI, launch → Quick Check results ≤60s P95.

Basmalah header.
```

---

## Part F — Additional Recommendations

### NuGet additions
- **Required:** `QuestPDF`, `Konscious.Security.Cryptography.Argon2`, `Zxcvbn.Core`.
- **Optional but cheap:** `Polly` (already present? verify) for ICheckRunner retry-with-timeout.
- **Testing:** verify `xUnit`, `Moq`, `Coverlet.Collector` present; add `FluentAssertions` (better assertion ergonomics, MIT).
- **Do NOT add** for v1.0: `Microsoft.ML.OnnxRuntime`, `TailwindCSS.Build`, `Serilog.Sinks.SignalR`, any LDAP package.

### Test strategy
- **xUnit + FluentAssertions + Moq + Coverlet.** Target 80% line coverage, 70% branch.
- **Mock at service boundaries only.** Never mock SqliteCacheStore (use in-memory DB); never mock FileSystemWatcher (use TempPath fixture).
- **Integration tests** in separate `SQLTriage.Tests.Integration` project, require live SQL (LocalDB acceptable). Gated behind env var `SQLTRIAGE_INTEGRATION=1` so CI skips when unavailable.
- **Golden-file tests** for ErrorCatalog, queries.json schema, governance-weights.json schema.
- **No UI automation** for v1.0 — too expensive vs payoff. Manual smoke on VMs.

### CI/CD fixes
- Submodule init fix: `.gitmodules` — ensure lib/PerformanceStudio URL uses HTTPS (not SSH) for GitHub Actions anonymous clone.
- Pipeline steps: (1) restore, (2) build with `/warnaserror`, (3) unit tests, (4) Basmalah lint (grep first 5 lines of every tracked *.cs/*.razor for the header), (5) publish single-exe, (6) upload artifact.
- Defer: code signing (v1.0.1), Squirrel release automation (v1.1).

### Code signing
- Defer to v1.0.1 as agreed. Document SmartScreen warning in README ("First launch will warn — click More info → Run anyway"). Don't hide this; users respect transparency.

### Basmalah enforcement (pre-commit hook)
```bash
#!/usr/bin/env bash
# .git/hooks/pre-commit
missing=0
for f in $(git diff --cached --name-only --diff-filter=ACM | grep -E '\.(cs|razor|css|js|md)$'); do
  head -n 3 "$f" | grep -qE "In the name of God, the Merciful, the Compassionate|بسم الله" || {
    echo "MISSING BASMALAH: $f"
    missing=1
  }
done
[ $missing -eq 0 ] || { echo "Commit blocked. Add basmalah header to the files above."; exit 1; }
```
Install via `tools/install-hooks.ps1` run on first clone. CI runs equivalent check; mismatch fails build.

### Other considerations
- **Release Checklist (200 items) is thorough but bottom-heavy on marketing.** Move "website copy updated" and "screenshots captured" to Week 7, not Week 8 — they need buffer for iteration.
- **Beta program.** Strategic Blueprint §GTM proposes 5 design partners. At 4 weeks of implementation + 4 weeks stabilization, beta *starts* Week 6 not Week 7. Recruit partners during Weeks 0–2 so they're lined up when Gate 3 passes.
- **Sample Governance Report PDF as marketing artifact.** Generate from a realistic-looking synthetic dataset (not a real customer). Host on marketing site before v1.0 tag; it's the single best GTM proof-point.
- **Cost-of-Downtime inputs are sensitive.** Default all to zero and show "Configure in Settings → Cost Assumptions" rather than guessing. Users enter numbers once, persist per-server.
- **Observe pre-mortem trigger at Week 3.** If fewer than 10 of the 15 Must-haves are "in progress or done" by end of Week 3, invoke the scope-trim playbook: simplify Governance Dashboard to Risk Level + Maturity % only, defer Cost-of-Downtime widget, cut Auditor role (merge into ReadOnly + audit-log page).

### Open questions answered
1. **IFindingTranslator abstraction vs GovernanceService fold-in?** Keep it separate. Governance produces scores; Translator renders language. Two responsibilities, two services.
2. **QuestPDF in v1.0 or defer?** Keep in v1.0. It's the audit-prep deliverable; without the PDF the tagline is unbacked.
3. **4KB checkpoint size?** Too small. Use 64KB default, configurable via Settings.
4. **queries.json location — Data/Sql/ or Config/?** `Config/`. It's runtime configuration, not SQL content. The Settings page will edit it.

---

**End of Pass 1 — ready for review.**
