<!-- In the name of God, the Merciful, the Compassionate -->
<!-- Bismillah ar-Rahman ar-Raheem -->

# SQLTriage Development Strategy (Option D — In-Place Hardening)

**Approved approach:** Build on existing 80% complete codebase. Incremental hardening, not fresh bootstrap.

**Rationale:** Option D saves ~2 weeks vs. Option C fresh start. Existing codebase is battle-tested; regression risk lower than greenfield risk. Opus external review strongly recommends against fresh start (65% confidence it's wrong). Preserve integration, fix gaps, implement missing must-haves.

**Pre-mortem validated:** See `.ignore/pre-mortem_1.md`. Scope trimmed to 13–15 Must-haves v1.0; defer AI/ML and UI theme to v1.1.

---

## 🎯 Strategic Direction: Hardening, Not Rebuilding

### Why In-Place Hardening (Option D) Wins

| Factor | Fresh Start (Option C) | In-Place Hardening (Option D) |
|--------|----------------------|-------------------------------|
| Bootstrap time | 2 weeks (copy + adapt) | 0 — already done |
| Regression risk | High (untested new code) | Low (battle-tested codebase) |
| Build warnings | Zero (clean slate) | Fix existing ~200 CA1416 (1–2 days) |
| Brand symmetry | Correct from file 1 | Rename tasks (1 week, already started) |
| Gemma comprehension | Small, focused | Medium (existing complexity) |
| Time to MVP | 6–8 weeks | 4 weeks (Must-haves only) |
| Technical debt repaid | None carried forward | Existing debt addressed incrementally |
| **Total effort** | +2 weeks | **baseline** |

**Decision:** Hardening wins. Existing code works; fix gaps, not rewrite.

---

## 📦 Remaining Gap Analysis (What's Missing for v1.0)

### Must-Have Services to Implement

| Service | Status | Files to Create/Modify |
|---------|--------|------------------------|
| `IFindingTranslator` | ❌ Missing | `Data/Services/FindingTranslator.cs`, `Data/Models/Translation/*.cs` |
| `GovernanceService` (revised scoring) | ⚠️ Partial | Update `Data/Services/GovernanceService.cs`, add `Config/governance-weights.json` |
| `AuditLogService` (single-writer + checkpoint) | ⚠️ Basic | Upgrade `Data/Services/AuditLogService.cs` |
| `ReportService` (QuestPDF) | ❌ Missing | `Data/Services/ReportService.cs`, `Pages/GovernanceReport.razor` |
| `ErrorCatalog` (60 scenarios + test) | ❌ Missing | `Data/Services/ErrorCatalog.cs`, `SQLTriage.Tests/ErrorCatalogTests.cs` |
| `ChartTheme` singleton | ❌ Missing | `Data/Services/ChartTheme.cs`, update `Components/Shared/Chart*.razor` |
| `SqlQueryRepository` (hot-reload) | ⚠️ Partial | `Data/Services/SqlQueryRepository.cs`, `Data/Sql/queries.json` |
| RBAC Argon2id | ⚠️ Stub | Update `Data/Services/RbacService.cs` implementation |
| Quick Check UX (curated subset) | ⚠️ Wireframe | Update `Pages/Onboarding.razor`, `Pages/QuickCheck.razor` |

### What to Leave Untouched (Already Works)
- ✅ `SqliteCacheStore` (WAL, 2-week retention, delta-fetch)
- ✅ `SessionDataService` (live sessions DMV)
- ✅ `AlertEvaluationService` (IQR baseline)
- ✅ `NotificationChannelService` (email/Teams/webhook)
- ✅ `CredentialProtector` (AES-256-GCM + DPAPI)
- ✅ `AutoUpdateService` (Squirrel logic)
- ✅ Blazor-ApexCharts integration (just update via ChartTheme)

---

## 📋 NuGet Dependencies to Add

| Package | Purpose | Version |
|---------|---------|---------|
| `QuestPDF` | PDF report generation (Governance Report) | latest stable (2024.x) |
| `Microsoft.ML.OnnxRuntime` | ONNX inference (v1.1 AI/ML) | defer to v1.1 |
| `ClosedXML` | Excel export (optional, CSV already works) | if needed |
| `Coverlet.Collector` | Test coverage | already present? verify |
| `Moq` | Unit test mocking | already present? verify |

**Note:** Do not add AI/ML packages until v1.1. `QuestPDF` is the only Must-have addition.

---

## 🏗️ Development Phases (Option D — 4 Weeks to MVP)

### Week 0: Prep (1–2 days)
1. **Build cleanup**
   - Fix CA1416 warnings: add `[SupportedOSPlatform("windows")]` to Program-equivalent, or suppress in `.csproj` with `<NoWarn>CA1416</NoWarn>`
   - Enable nullable reference types; fix CS8602/CS8604
   - Verify clean `dotnet build` with zero warnings
2. **Brand completion**
   - Rename solution/project to `SQLTriage`
   - Update all hardcoded "SqlHealthAssessment"/"SQLTriage" strings in UI
   - Update `Config/version.json` to `1.0.0`
3. **Basmalah enforcement**
   - Add pre-commit hook `.git/hooks/pre-commit` to check for basmalah header on staged `.cs`/`.razor` files
   - CI step to scan for header presence (fail if missing)

### Week 1: Core Infrastructure
4. `SqlQueryRepository` + `Data/Sql/queries.json` metadata + `"quick": true` tagging on ~40 checks
5. `ChartTheme.cs` singleton + integrate into all `Chart*.razor` components
6. `GovernanceService` redesign: capped critical-failure scoring, vector weights from `Config/governance-weights.json`
7. `RBACService` Argon2id hashing (replace CredentialProtector for passwords)

### Week 2: Translation & Reporting
8. `IFindingTranslator` implementation — DBA/IT/Executive models + translation logic
9. `ReportService` QuestPDF integration — 3-page Governance Report PDF
10. `ErrorCatalog` expansion to ~60 scenarios with governance impact tags
11. `ErrorCatalogTests` coverage validation

### Week 3: UX & Audit
12. Onboarding wizard → Instant Quick Check (≤60s pipeline)
13. `AuditLogService` single-writer queue, 4 KB checkpoint, Event Log mirror
14. Settings page — no manual JSON editing (100% UI coverage)
15. Unit test suite ≥80% (Coverlet + xUnit + Moq)

### Week 4: Polish & Stabilization
16. Fix remaining bugs; performance profiling
17. Quick Check timing validation (P95 ≤60s measured)
18. Beta smoke test on clean Windows VM
19. README/website copy rewrite ("Governance & Translation Platform")
20. v1.0 release candidate

---

## 🔬 Pre-Mortem Guardrails (Option D Specific)

### Velocity Triggers (check Fridays)

| Week | Expected Completed | If behind… |
|------|-------------------|------------|
| 1 | Tasks 4–7 (4 tasks) | Cut AI/ML discussions; focus on Must-haves |
| 2 | Tasks 8–11 (4 tasks) | Defer Tailwind migration (v1.1), keep ChartTheme charts only |
| 3 | Tasks 12–15 (4 tasks) | Trim Error catalog to 40 scenarios (still comprehensive) |
| 4 | Tasks 16–20 (5 tasks) | Freeze scope; only bug fixes; no new features |

**If Week 4 < 80% velocity:** Release with Must-haves only (Governance Dashboard simplified, Report PDF basic, no should-haves). Communicate: "v1.0 focused on core stability and compliance foundation — advanced features in v1.1."

---

## 📐 SQL as JSON Architecture (Keep)

Same as Option C plan — this is orthogonal to bootstrap approach.

**`SqlQueryRepository`** loads all `.sql` files from `Data/Sql/` at runtime into a dictionary. Services call `_sqlRepo.Get("BackupValidation")`. Hot-reload via `_sqlRepo.Reload()`.

**Structure:**
```
Data/Sql/
├── HealthChecks/
├── WaitStats/
├── Sessions/
├── AnomalyDetection/       ← v1.1
└── queries.json            ← Metadata (category, severity, "quick" tag)
```

**`queries.json` metadata:**
```json
{
  "BackupValidation": {
    "file": "HealthChecks/BackupValidation.sql",
    "description": "Validates backup existence and recency",
    "category": "Backup",
    "severity": "HIGH",
    "quick": true
  }
}
```

---

## 🗂️ Task Mapping (Must/Should/Could for v1.0)

### Must-Have (non-negotiable, 13–15 tasks)
1. Build system clean (CA1416, null safety)
2. Brand unification (SQLTriage everywhere)
3. Version bump to 1.0.0
4. Screenshots in repo (match website)
5. SqlQueryRepository + `queries.json` with `"quick"` tags
6. ChartTheme singleton (charts only; component styling deferred)
7. Quick Check UX (≤60s EXE → results)
8. IFindingTranslator service (3-audience output)
9. GovernanceService revised (capped, vector weights JSON)
10. RBAC Argon2id (Admin/DBA/ReadOnly/Auditor/Operator)
11. AuditLog single-writer + checkpoint + Event Log
12. ErrorCatalog ~60 scenarios + tests
13. QuestPDF ReportService (3-page Governance PDF)
14. Onboarding wizard (auto-detect + connect + Quick Check)
15. Unit tests 80% coverage

### Should-Have (stretch, cut if behind)
- Governance Dashboard (Risk Level + Maturity % only; Risk Register defer)
- Error messages with governance impact (strategic but can be basic v1.0)
- ChartTheme full integration (if integration burden high, defer component-by-component)
- ForecastService linear regression (CPU only; multivariate v1.1)

### Could-Have (defer to v1.1)
- AI/ML full ONNX pipeline (anomaly detection, multi-metric, SignalR streaming)
- UI Theme Refresh (Tailwind migration; ApexCharts default dark in v1.0)
- AD/LDAP integration (local auth only)
- GPO ADMX templates
- Documentation Generator page
- Code signing automation (v1.0.1)

---

## 💰 Cost Comparison (Option D vs Option C)

| Phase | Option C (Fresh) | Option D (Hardening) |
|-------|-----------------|---------------------|
| Bootstrap | 2 weeks (copy + adapt) | 0 |
| Design (Opus) | 2–3 calls ($20–30) | 1–2 calls ($10–15) |
| Implementation | 25 tasks (Gemma) | 15 tasks (Gemma) |
| Testing | Full regression (new) | Spot regression (existing coverage) |
| **Total cost** | $30–45 | **$15–25** |
| **Timeline to MVP** | 6–8 weeks | **4 weeks** |

**Option D is faster and cheaper** by ~2 weeks and ~$15–20. Quality higher due to retained integration.

---

## ⚠️ Risks & Mitigations (Option D)

| Risk | Impact | Mitigation |
|------|--------|------------|
| Legacy architecture constraints | Medium | Follow Must-have tasks; refactor only what's needed |
| Build warnings persist | High | Week 0 dedicated cleanup; treat as blocker |
| Brand rename incomplete | Medium | Pre-commit hook to grep for "SqlHealthAssessment"/"SQLTriage" |
| Baseline overlay complexity | Low | Existing code works; reuse, don't rewrite |
| Scope creep | High | Pre-mortem triggers; freeze after Week 2 |

---

## 🎯 Success Criteria (v1.0 Option D)

**Functional:**
- [ ] Build: `dotnet build SQLTriage.sln` produces zero warnings
- [ ] EXE launches → onboarding → Quick Check results ≤60s (P95)
- [ ] Governance Dashboard renders capped score + Risk Level
- [ ] Governance Report PDF exports (3 pages)
- [ ] `IFindingTranslator.Translate()` returns DBA/IT/Executive outputs
- [ ] AuditLog validates HMAC/checkpoint chain on startup
- [ ] `ErrorCatalog` coverage ≥60 scenarios with governance impact
- [ ] All `.cs`/`.razor` files begin with basmalah header (enforced)

**Quality:**
- [ ] Unit test coverage ≥80%
- [ ] No unhandled exceptions in 30-minute continuous run
- [ ] Fast queries: Quick Check completes ≤60s on local SQL instance
- [ ] Memory stable: <500 MB idle with 10 servers

**Release:**
- [ ] Website/docs updated to SQLTriage brand
- [ ] 16 screenshots captured and committed
- [ ] Sample Governance Report PDF published
- [ ] v1.0.0 tagged and released

---

## 📅 Upcoming Opus Review Points

1. **Architecture validation** — confirm `IFindingTranslator` design aligns with translation substrate philosophy
2. `GovernanceService` scoring algorithm review — verify capped critical-failure logic
3. `AuditLogService` single-writer + checkpoint design review
4. Final v1.0 scope sign-off — Must-haves locked

**Attach this file + `.ignore/COMMENT_20260418_091241.md` to next Opus prompt.**

---

**File updated:** `.ignore/DEVELOPMENT_STRATEGY.md` now reflects Option D (In-Place Hardening) as the chosen path.

