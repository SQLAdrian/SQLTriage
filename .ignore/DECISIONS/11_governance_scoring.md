<!-- In the name of God, the Merciful, the Compassionate -->
<!-- Bismillah ar-Rahman ar-Raheem -->

# D11 — Governance Scoring: Capped + Vector + JSON Weights (Revised per Opus)

**Date:** 2026-04-18
**Updated:** 2026-04-18
**Decision:** GovernanceService scoring algorithm: three-level clamp (per-finding cap → per-category cap → overall cap), per-category vector weights stored in `Config/governance-weights.json`, maturity bands in same JSON, hot-reload via `IOptionsMonitor`, Quick Check produces `IsIndicative=true` results separate from Full VA official score.

**Algorithm (concrete, non-negotiable order):**

1. **Per-finding raw score:** `basePoints` derived from severity:
   - P1 (Critical/Service Down) → 40
   - P2 (High) → 25
   - P3 (Medium) → 10
   - P4 (Low/Info) → 5

2. **Per-finding contribution:** `contribution = basePoints × categoryWeight` (from vector). **Cap per finding to 40** (hard ceiling per Opus §A.3).

3. **Per-category subtotal:** Sum contributions within category. **Cap subtotal to 100** (ensures no category exceeds its weighted maximum).

4. **Overall score:** Average of category subtotals (already bounded ≤100 each) → `Math.Min(100, average)`. Clamp to 100.

5. **Maturity band:** Lookup final score in `governance-weights.json["bands"]` mapping (Emerging 0–20, Bronze 21–40, Silver 41–60, Gold 61–80, Platinum 81–100). Bands are reloadable with weights file.

**governance-weights.json schema (final):**
```jsonc
{
  "categories": {
    "Security": 0.25,
    "Performance": 0.20,
    "Reliability": 0.20,
    "Cost": 0.15,
    "Compliance": 0.20        // Raised from 5%→20% to align with "audit preparation" positioning
  },
  "caps": {
    "perFinding": 40,         // Max contribution from any single finding
    "perCategory": 100,       // Max subtotal per category
    "overall": 100            // Hard ceiling on 0–100 score
  },
  "bands": {
    "Emerging": [0, 20],
    "Bronze": [21, 40],
    "Silver": [41, 60],
    "Gold": [61, 80],
    "Platinum": [81, 100]
  }
}
```

**Quick Check vs Full VA separation:**
- `GovernanceService.ComputeIndicativeAsync(IEnumerable<CheckResult> quickResults)` → returns `GovernanceReport { IsIndicative = true, Score, Band, Disclaimer = "Indicative only — run Full VA for official score" }`
- `GovernanceService.ComputeFullAsync(IEnumerable<CheckResult> fullVaResults)` → `IsIndicative = false` — used for Governance Report PDF export and audit evidence
- Quick Check subset: ~40 checks with `"quick": true` in `Config/queries.json`; **Quick Check results never flow to PDF report** (PDF requires Full VA)

**Translator integration (non-circular data flow):**
```
Check Result (raw) → GovernanceService.ComputeFullAsync() → GovernanceReport (overall score + band)
                            ↓
                 per-finding BusinessRiskBand (derived from capped per-finding contribution)
                            ↓
            IFindingTranslator.Translate(findingId) reads BusinessRiskBand (does NOT write it)
```
**Critical:** Translator consumes Governance output; translator does NOT produce BusinessRisk. No circular dependency.

**Hot-reload:** `IOptionsMonitor<GovernanceWeights>` watches `Config/governance-weights.json`; on change → invalidate cached `GovernanceReport` and all `FindingTranslation` entries (key includes weights version). Next Governance render auto-recomputes.

**Thread safety — `ComputeCore` must use `ReaderWriterLockSlim` (Pass 2 Part B):**
- `GovernanceService` holds a `private readonly ReaderWriterLockSlim _weightsLock`.
- `ComputeCore(results, isIndicative)` acquires `_weightsLock.EnterReadLock()` for its duration — prevents mid-compute weight swap when `IOptionsMonitor.OnChange` fires concurrently.
- `OnChange` callback acquires `_weightsLock.EnterWriteLock()` before swapping the in-memory `GovernanceWeights` instance and clearing caches.
- Same pattern applies to `SqlQueryRepository` on `queries.json` reload (its own `ReaderWriterLockSlim`).
- **Do NOT use `lock(obj)`** — read-heavy workload benefits from concurrent reads; writes (reload) are rare.

**Why Compliance 20%:** Product tagline is "reduce audit preparation from days to minutes." Alignment with SOC2/HIPAA/ISO controls is core value, not afterthought. 20% weight reflects positioning without overwhelming Security/Reliability.

**Sources:** COMMENT D11 originally; Opus §A.3 (three-level clamp, revised weights, maturity bands JSON, Quick vs Full drift, hot-reload pattern); PRD §2.3 rewrite applied; WORKFILE Task 32 updated

