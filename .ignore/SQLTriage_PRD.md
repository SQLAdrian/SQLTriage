<!-- In the name of God, the Merciful, the Compassionate -->
<!-- Bismillah ar-Rahman ar-Raheem -->

# SQLTriage Product Requirements Document v1.0

**Project:** SQLTriage — The SQL–Business Translation Platform  
**Version:** 1.0  
**Status:** Draft — Implementation Plan  
**Last Updated:** 2026-04-18  

---

## 1. Philosophy & Intent

### 1.0 Foundational Statement

**What is SQLTriage really for?**

SQLTriage is not a "monitoring tool." It is a **communication substrate** — a unified, visual language that allows three distinct groups to understand each other:

1. **The SQL Specialist** (DBA, consultant, MSP technician) — speaks in waits, blocks, plan caches, trace flags
2. **The Technical Decision-Maker** (IT Manager, Infrastructure Lead) — speaks in SLAs, uptime percentages, risk tolerances
3. **The Business Decision-Maker** (CFO, CIO, Board) — speaks in dollars, risk exposure, audit compliance, licensing costs

**The problem it solves:** In every enterprise, these three groups operate in parallel universes. The DBA says "we have high CXPACKET waits." The CIO hears "technical problem" and allocates budget to a consultant. The consultant bills $25K for an index that fixes 5% of the problem. Everyone loses except the consultant.

**My personal context:** I run a SQL specialty MSP. I have personally saved clients over **US$50 million** in SQL licensing and waste by bridging this gap. This is not hyperbole — it is the direct result of translating technical findings into business language that drives action. SQLTriage is the systematization of that translation. **It is an act of charity. Its purpose is to stop the "ambulance at the bottom of the hill" and "charge like a wounded bull" strategies that bleed companies dry. This is solely for the pleasure of Allah.**

**Why this doesn't exist elsewhere:** The knowledge is locked in black-box consultancies. No one shares their secret sauce because it's their revenue stream. The result is the blind billing the blind. SQLTriage is an open-source intervention: a **complete, end-to-end translation layer** that any company can use to understand their own SQL estate without hiring a $300/hour consultant to tell them they need more memory.

**Success metric:** A CFO opens SQLTriage, sees "Your SQL estate is burning $47,000 per month in wasted licensing and risk exposure," and understands exactly what to do. No translation required.

---

## 2. Product Overview

### 1.2 Target Audience (Priority Order)
1. **SQL Server DBAs** — daily operational triage, performance troubleshooting
2. **IT Managers / Infrastructure Leads** — governance, compliance, cost oversight
3. **CxOs (CIO, CFO, CISO)** — risk quantification, audit readiness, budget justification

### 1.3 Core Value Proposition
> **"Reduce audit preparation from days to minutes."**

Secondary values:
- No installation. No agents. Safe for production.
- Instant visibility into blocking, wait stats, top resource consumers
- Automated health checks against 472+ best practices
- Executive-ready risk summaries with business impact translation

### 1.4 Triage-First UX Principle: Local Quick Check

**Fundamental premise:** The first user action **must produce visible value within 60 seconds** of EXE launch, without configuration, without reading docs.

**Scope:** This ≤60s guarantee applies to **local SQL Server connections** (round-trip latency ≤15 ms). For remote/WAN deployments (latency >15 ms), the Quick Check automatically switches to "Extended Check" mode with longer timeout budget and an explicit user consent dialog. Marketing will position this as "Local Quick Check in under 60 seconds" — accurate to the technical envelope.

**User journey (local):**
1. Download EXE (no install)
2. Launch → auto-redirect to `/onboarding` (no servers configured)
3. Auto-detect local SQL Server instances (or manual entry)
4. Checkbox: "✓ Run Quick Check immediately after connecting" (default checked)
5. Click "Connect" → server added → automatic navigation to Quick Check page
6. Parallel execution of ~40 curated checks starts immediately (DOP ≤4)
7. **≤ 60 seconds:** Results page shows color-coded PASS/WARN/FAIL summary per category; critical findings highlighted
8. CRITICAL finding triggers toast notification
9. THEN user is asked: "Want ongoing monitoring? Deploy SQLWATCH" (optional upsell)

**User journey (remote/WAN):**
- After connection, app measures RTT from first 5 checks
- If avg RTT >15 ms: banner "Remote server detected — Quick Check will take 2–3 minutes. Continue?" with "Run Extended Check" / "Skip" buttons
- On consent: DOP=2, per-check timeout 12s, global budget 90s; results marked "Extended (remote)"

**Performance targets (local, 8-core, typical OLTP):**
- Per-check timeout: 8s (configurable per check via `queries.json`; Low complexity = 8s, Medium = 12s, High = 20s)
- Global budget: 55s elapsed, then cancel in-flight, show partial + "Full VA available"
- DOP: min(ProcessorCount, 4) — capped to avoid saturating server
- Expected total: 35–42 checks × avg 0.6s (cached metadata) + 0.3s RTT = ~28–35s

**Why this order:** 
- DBAs need to know "what's broken" before they care about "what might break"
- Audit evidence requires findings — must produce findings **first**
- Governance Dashboard is meaningless without data to govern
- Monitoring (SQLWATCH) is optional enhancement, not prerequisite

**Success metric:** Time from EXE launch to Quick Check summary visible ≤ 60 seconds (P95, **local** SQL Server, RTT ≤15 ms).

**Failure mode to avoid:** User adds server → sees empty dashboard → "What now?" → churn.

---

## 3. Governance Dashboard (Task #17)

### 2.1 Purpose
Provide an at-a-glance risk posture summary for IT managers and CxOs. This is the **"CxO view"** that justifies budget and adoption.

### 2.2 Data Model

```csharp
public class ExecutiveSnapshot
{
    public string OverallRiskLevel { get; set; }           // LOW, MODERATE, HIGH, CRITICAL
    public int OperationalMaturityPercent { get; set; }     // 0-100
    public string MaturityBand { get; set; }               // Emerging → Bronze → Silver → Gold → Platinum (from governance-weights.json bands)
    public BusinessImpactAssessment BusinessImpact { get; set; }
    public DateTime AssessmentDate { get; set; }
    public string SqlServerVersion { get; set; }
    public bool IsIndicative { get; set; }                 // true = Quick Check subset, not official
}

public class BusinessImpactAssessment
{
    public string DowntimeExposure { get; set; }           // LOW, MEDIUM, HIGH, CRITICAL
    public string AuditReadiness { get; set; }             // READY, PARTIAL, FAIL
    public string CostOptimization { get; set; }           // OPTIMIZED, MEDIUM, WASTE
    public string KeyInsight { get; set; }                 // One-sentence summary
}

public class RiskRegisterEntry
{
    public string RiskId { get; set; }                     // R-01, R-02, ...
    public string RiskTheme { get; set; }                  // Data Recovery, Cyber/Privilege, Performance, ...
    public string ImpactRating { get; set; }               // CRITICAL, HIGH, MEDIUM, LOW
    public string BusinessImpact { get; set; }             // Plain English consequence
    public string RecommendedAction { get; set; }          // First step to remediate
    public List<string> AffectedChecks { get; set; }       // Links to VA check IDs
}
```

### 2.3 Scoring Algorithm (capped + vector weights + JSON-editable)

**Design principle:** Prevent single finding from dominating scorecard; allow product to tune emphasis without recompilation; translate raw check results into business-weighted maturity.

**Governance-weights.json schema (`Config/governance-weights.json`):**
```json
{
  "categories": {
    "Security": 0.25,
    "Performance": 0.20,
    "Reliability": 0.20,
    "Cost": 0.15,
    "Compliance": 0.20
  },
  "caps": {
    "perFinding": 40,
    "perCategory": 100,
    "overall": 100
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

**Algorithm (three-level clamp):**

1. **Per-finding contribution** capped at `caps.perFinding` (40 for CRITICAL, 25 for HIGH, 10 for MEDIUM, 5 for LOW, 0 for INFO). Information-priority findings are surfaced in the translator but contribute nothing to governance score.
2. **Per-category subtotal:** Sum capped per-finding scores within category, then clamp to `caps.perCategory` (100) multiplied by category weight
3. **Overall score:** Average of category subtotals (already ≤ 100 by construction), clamp to `caps.overall` (100)
4. **Maturity band:** Lookup overall score in `bands` mapping (reloadable via `IOptionsMonitor<GovernanceWeights>`)

**Scoring binds to check_id, not category rollup:**
- Each finding's contribution is driven by its **primary check_id** (e.g., `check_id: 1` — Max Server Memory). The `category` field in the source YAML is the **category mapping block** that rolls that check_id into one of the 5 governance dimensions (Security/Performance/Reliability/Cost/Compliance).
- `governance-weights.json` holds only the **5 dimension weights + caps + bands** — it does NOT enumerate check IDs. The check→category mapping lives with the check itself (`consolidated_checks.sql` YAML `category:` field), keeping scoring authoritative-per-check.
- Per-check overrides (rare) live in `queries.json` as `governance: { categoryWeight?, perFindingCap? }` — used only when a specific check needs boosted/dampened weight (e.g., backup validation always CRITICAL regardless of YAML priority).
- Mapping population strategy: an agent pass interprets each of the 343 check YAML blocks and emits the `queries.json` entry (check_id → file, category, severity, quick-tag). The mature YAML metadata is the source of truth; `queries.json` is the derived runtime index.

**Check-count reconciliation (343 vs 489):**
- `consolidated_checks.sql` contains 343 YAML-tagged checks at v1.0. The historical 489 figure included broken/deprecated/duplicate checks culled during consolidation.
- Each `queries.json` entry carries a `status: working | broken | deprecated` flag. Only `status: working` checks load at startup; broken checks are retained for reference but skipped.
- No single "correct" count — the runtime count is `count(status == working)`, which the Settings page surfaces.

**Quick Check vs Full VA:**
- `GovernanceService.ComputeIndicativeAsync(quickCheckResults)` returns `GovernanceReport { IsIndicative = true, Score, Band, Disclaimer = "Indicative only — run Full VA for official score" }`
- `GovernanceService.ComputeFullAsync(vaResults)` returns `IsIndicative = false` — used for Governance Report PDF export and audit evidence
- Quick Check subset is curated `"quick": true` checks from `Config/queries.json` (~40 items); contributes to indicative maturity but flagged separately

**Example:** 3 critical findings in Security category:
- Raw per-finding: 40 + 40 + 40 = 120
- Per-category cap: Security weight 0.25 → max category contribution = 25
- Final: Security subtotal clamped to 25, averaged with other categories → overall score reflects the cap

**Compliance note:** Weights and bands live in JSON, not code. `GovernanceService` watches file changes; on reload, cached `GovernanceReport` invalidates. Settings UI can expose weight sliders (v1.1); v1.0 weights are file-editable only.

**Translator data flow (non-circular):**
```
VA Check Result → GovernanceService (ComputeFullAsync) → 
  GovernanceReport { OverallScore, PerCategory, BusinessRiskBand } → 
  IFindingTranslator.Translate(findingId) consumes BusinessRiskBand (read-only), does not modify Governance state.
```
**Key:** Translator is a consumer, not a producer, of BusinessRisk. No circular dependency.

### 2.4 UI Layout

```
┌─────────────────────────────────────────────────────────────┐
│  SQLTriage Governance Dashboard                              │
├─────────────────────────────────────────────────────────────┤
│  Overall Risk Level: [MODERATE]  Maturity: 81% (Hardened)  │
│  Downtime Exposure: [HIGH]       Audit: [PARTIAL]           │
│  Cost Optimization: [MEDIUM]     Insight: Stable foundation │
│                    but lacks proactive observability        │
├─────────────────────────────────────────────────────────────┤
│  Risk Register                                               │
│  ┌─────┬──────────────┬─────────┬───────────────────────┐ │
│  │ ID  │ Theme        │ Impact  │ Business Impact      │ │
│  ├─────┼──────────────┼─────────┼───────────────────────┤ │
│  │ R-01│ Data Recovery│ CRITICAL│ Permanent data loss  │ │
│  │ R-03│ Cyber/Priv   │ HIGH    │ Breach exposure      │ │
│  │ R-08│ Performance  │ MEDIUM  │ Capacity drift       │ │
│  └─────┴──────────────┴─────────┴───────────────────────┘ │
├─────────────────────────────────────────────────────────────┤
│  [Export to PDF]  [View Detailed Findings]  [Configure]   │
└─────────────────────────────────────────────────────────────┘
```

---

## 4. Governance Report Export (Task #18)

### 3.1 Report Structure (PDF, 2-3 pages)

**Page 1 — Executive Summary**
```
SQLTriage Governance Report
Generated: 2026-04-18 14:32 UTC
Server: sql-prod-01 | Version: SQL Server 2022

┌─────────────────────────────────────────────────────────────┐
│ EXECUTIVE SNAPSHOT                                           │
│ Risk Level:        MODERATE [🟡]                            │
│ Operational Maturity: 81% (Hardened) [🟢]                   │
│ Downtime Exposure:  HIGH [🔴]                               │
│ Audit Readiness:   PARTIAL [🟡]                             │
│ Cost Optimization: MEDIUM [🟡]                              │
│                                                             │
│ KEY INSIGHT: This environment has a stable foundation       │
│ but lacks the proactive observability required for full     │
│ audit compliance and cost-efficient scaling.                │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│ BUSINESS IMPACT ANALYSIS                                     │
│                                                             │
│  Downtime Exposure: HIGH                                    │
│  → Estimated cost per hour: $125,000                        │
│  → Risk exposure (30-day window): $45,000,000               │
│  → Primary driver: Backup validation gaps (R-01)            │
│                                                             │
│  Audit Readiness: PARTIAL                                    │
│  → SOC2 Monitoring Activities: FAIL (missing alerts)        │
│  → SOC2 Access Control: PASS                                │
│  → HIPAA Audit Logging: PARTIAL                             │
│                                                             │
│  Cost Optimization: MEDIUM                                   │
│  → CPU utilization avg: 14% (target: 30-70%)                │
│  → Potential annual savings: $18,000 (license optimization) │
└─────────────────────────────────────────────────────────────┘
```

**Page 2 — Configuration Drift & Security Risks**
```
┌─────────────────────────────────────────────────────────────┐
│ CONFIGURATION DRIFT SUMMARY                                  │
│ Framework: SOC2 / ISO 27001 Best Practices                  │
├─────────────────────────────────────────────────────────────┤
│ Control                  Status  Details                    │
│ ─────────────────────────────────────────────────────────── │
│ System DBs on C: Drive   [PASS]  All system DBs on D:      │
│ Max Server Memory        [FAIL]  Not configured (unlimited)│
│ TempDB file count        [WARN]  1 file (recommended: 4-8) │
│ Query Store enabled      [PASS]  Enabled on 12/12 DBs      │
│ Latest CU installed      [FAIL]  3 servers behind latest  │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│ SECURITY RISKS                                              │
│ R-03: Over-privileged Accounts (HIGH)                       │
│   • 2 logins with sysadmin: LocalSystem, DOMAIN\AdminTeam   │
│   • 1 SQL login with ENCRYPTION privilege                  │
│   → Action: Review and remove unnecessary privileges        │
└─────────────────────────────────────────────────────────────┘
```

**Page 3 — Priority Action Plan & Cost of Downtime**
```
┌─────────────────────────────────────────────────────────────┐
│ TOP 10 PRIORITY ACTION PLAN (Next 30 Days)                  │
├─────────────────────────────────────────────────────────────┤
│ #  Action Item                         Impact    Effort      │
│ ─────────────────────────────────────────────────────────── │
│ 1  Validate all backups (R-01)         CRITICAL  2 hours    │
│ 2  Configure SQL Agent alerts          HIGH      1 hour     │
│ 3  Replace LocalSystem accounts        HIGH      4 hours    │
│ 4  Set Max Server Memory              MEDIUM    30 min     │
│ 5  Enable Query Store on prod DBs      MEDIUM    1 hour     │
│ ... (ranked by business ROI)                                │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│ COST OF DOWNTIME CALCULATOR                                  │
│                                                             │
│  Assumed revenue per hour: $125,000                          │
│  Estimated recovery time: 4 hours                            │
│  Single incident cost: $500,000                              │
│  Annualized risk exposure (30% probability): $150,000       │
│                                                             │
│  💡 Fixing backup gaps (R-01) reduces exposure by $100,000  │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│ REPORT FOOTER                                                │
│ Generated by SQLTriage v1.0 | sqladrian.github.io            │
│ This report provides actionable evidence for SQL Server      │
│ risk, performance, and compliance posture at scale.         │
└─────────────────────────────────────────────────────────────┘
```

### 3.2 PDF Generation Requirements (Task 34 — QuestPDF)
- **Library:** `QuestPDF` (MIT, fluent API, no COM dependencies)
- **Render:** `ReportService.GenerateGovernanceReport(GovernanceReport)` returns `byte[] PDF`
- **Page size:** A4, portrait, 1-inch margins
- **Header:** SQLTriage logo + report title + generation timestamp
- **Footer:** Page numbers, "Confidential — Internal Use Only"
- **Color scheme:** GREEN=PASS/OPTIMIZED, AMBER=WARN/PARTIAL, RED=FAIL/CRITICAL/WASTE
- **Page size:** A4, portrait
- **Header:** SQLTriage logo + report title + generation timestamp
- **Footer:** Page numbers, "Confidential — Internal Use"
- **Color scheme:**
  - GREEN: PASS / LOW / OPTIMIZED
  - AMBER: WARN / MEDIUM / PARTIAL
  - RED: FAIL / HIGH / CRITICAL / WASTE
- **Branding:** SQLTriage header on every page; no "SqlHealthAssessment" remnants

### 3.3 "Sample Governance Report" Download
- Place static sample PDF at: `wwwroot/samples/SQLTriage-Governance-Sample.pdf`
- Link on website homepage: "Download Sample Report"
- Content: fictional but realistic data showing all PASS/WARN/FAIL states

---

## 5. AI/ML Predictive Analytics (Task #23) — DEFERRED to v1.1

**Status:** Full ONNX pipeline deferred to v1.1 per pre-mortem velocity triggers and scope triage (`.ignore/WORKFILE_remaining.md` Must/Should/Could framework). Complete specification retained below for v1.1 implementation. Do not implement in v1.0.

---

### 4.1 Purpose
All data drawn from existing `SqliteCacheStore` (30-day rolling window):
- CPU utilization % (5-minute averages)
- Blocking duration and session count
- Wait stats percentages (CXPACKET, LCK_M_X, etc.)
- Database file growth rates (MB/day)
- Backup success/failure counts
- Query plan cache churn rate

**Query Organization:**  
Anomaly/queries for trend analysis can be stored either:
- As separate `.sql` files in `Data/Sql/AnomalyDetection/`, **or**
- As tagged sections in a unified `consolidated_checks.sql` (use `-- TAG: trend_analysis` comments) alongside unified alert scripts.
A `manifest.json` maps each metric to its SQL source and model configuration.

### 4.3 Model Architecture

**Offline Training (Python notebook, run quarterly or on major release):**
```
Train on historical sp_triage dataset (~600 servers × years):
- LinearRegression → CPU 7-day forecast
- ExponentialSmoothing → Blocking trend
- IsolationForest → Anomaly detection (deviation from baseline)
Export each to ONNX (.onnx files)
```

**Inference (C# at runtime):**
- Load `.onnx` models with `Microsoft.ML.OnnxRuntime`
- Feed last 30 days of cached metrics
- Output predictions with confidence intervals (68% / 95%)

### 4.4 PredictiveService API

```csharp
public interface IPredictiveService
{
    CapacityForecast GetCapacityForecast(string serverName, int days = 7);
    AnomalyAlert[] GetActiveAnomalies(string serverName);
    IReadOnlyList<PredictionMetric> GetRecentPredictions(string serverName, int hours = 168);
}

public class CapacityForecast
{
    public string ServerName { get; set; }
    public DateTime GeneratedAt { get; set; }
    public List<ForecastPoint> Points { get; set; }     // Date + predicted value
    public ConfidenceBand Confidence { get; set; }       // Upper/lower bounds
    public string RecommendedAction { get; set; }
    public AlertTrigger[] Triggers { get; set; }         // Any threshold crossed
}

public class AnomalyAlert
{
    public string AlertId { get; set; }                  // ANOM-001, etc.
    public string MetricName { get; set; }               // "cpu_percent", "blocking_minutes"
    public double CurrentValue { get; set; }
    public double ExpectedBaseline { get; set; }
    public double ZScore { get; set; }                   // Standard deviations from norm
    public string Severity { get; set; }                 // LOW/MEDIUM/HIGH/CRITICAL
    public string BusinessImpact { get; set; }
}
```

### 4.5 Dashboard Widget

On main Dashboard (top cards area), add a **"7-Day Forecast"** panel:
```
┌─────────────────────────────────────┐
│  Capacity Forecast (7 days)          │
│  CPU:  65% → 78% [↑13%] 🔴          │
│  Blocking: 12min → 34min [↑183%] 🔴 │
│  Storage: 45% → 52% [🟢]            │
│  → No critical anomalies detected   │
└─────────────────────────────────────┘
```
- Color-coded: green (stable), amber (degrading), red (critical trend)
- Tooltip on each metric shows sparkline chart (history + dashed forecast)
- Clickthrough to `/capacity` page for full analysis

### 4.6 Capacity Planning Page (`/capacity`)

Full-page view with:
1. **Multi-metric timeline chart** — actual (solid) + forecast (dashed) + confidence band (shaded)
2. **Storage exhaustion countdown** — "Drive D: will fill in 12 days (predicted)"
3. **Top rising risks** — list of metrics trending toward threshold, sorted by severity
4. **"What-if" scenario toggle** — "If growth continues at current rate..."
5. **Historical accuracy chart** — show past forecast vs actual to build trust

### 4.7 Alerting Integration

`NotificationChannelService` supports new **PredictiveAlert** type:
- Trigger when forecast crosses threshold in ≤ N days
- Example: "CPU predicted > 85% within 5 days" → email to DBA team
- Configurable per-server: enable/disable predictive alerts, sensitivity (conservative/balanced/aggressive)

### 4.8 Real-Time Anomaly Streaming

For immediate anomaly notification, implement a **SignalR hub** (`AnomalyHub`):
- `PredictiveService` pushes `AnomalyEvent` objects to hub when anomaly score > threshold
- Blazor clients (Dashboard, Capacity page) subscribe and display toast notifications
- Charts highlight anomaly points with red dot markers; tooltip shows business impact message
- Uses existing SignalR infrastructure (Blazor Server already has circuit support)

### 4.9 Implementation Phases

**Phase 1 (Week 5):** Linear CPU forecast only (simplest model, highest value)
**Phase 2 (Week 6):** Blocking trend + anomaly detection
**Phase 3 (Week 8+):** Storage growth, plan regression, model accuracy dashboard

### 4.10 Technical Requirements
- **ONNX Runtime NuGet:** `Microsoft.ML.OnnxRuntime` (no Python needed at runtime)
- **Models stored:** `Data/Models/{modelname}_v{version}.onnx`
- **Model metadata:** `Data/Models/manifest.json` — maps server → model version, training date, accuracy metrics
- **Fallback:** If model missing or fails, service returns "forecast unavailable" — never crashes
- **Cache:** Forecasts cached for 1 hour; regenerate on `_sqlRepo.Reload()` or manual refresh

### 4.11 Success Criteria
- [ ] CPU 7-day forecast MAPE (mean absolute percentage error) < 15% on validation set
- [ ] Storage exhaustion predictions within ±2 days accuracy
- [ ] Anomaly detection precision > 80%, recall > 70%
- [ ] Dashboard widget loads in < 500ms
- [ ] Predictive alerts fire within 5 minutes of data crossing threshold

### 4.12 Chart Theme Specification

**Library:** `Blazor-ApexCharts` v3+ (already used in current codebase; supports time-series, area, donut, bar charts).

**Theme goal:** Rolls Royce aesthetic — muted slate palette, glassmorphism transparency, smooth 300ms animations, Playfair Display headings, generous whitespace.

**ChartTheme.cs** singleton service provides:

```csharp
public static class ChartTheme
{
    // Premium muted palette (emerald/amber/slate dominance)
    public static readonly string[] SeriesColors = 
        { "#10b981", "#f59e0b", "#3b82f6", "#ef4444", "#8b5cf6", "#06b6d4" };

    // Background & grid
    public const string Background = "transparent";
    public const string GridColor = "#334155";      // slate-700
    public const string GridStyle = "4";             // dashed gridlines
    public const string TextColor = "#94a3b8";       // slate-400

    // Typography
    public const string FontFamily = "'Inter', sans-serif";

    // Forecast overlay (7-day prediction)
    public const int ForecastStrokeWidth = 2;
    public const string ForecastDashArray = "5";     // dashed line
    public const string ConfidenceFill = "rgba(16, 185, 129, 0.15)"; // emerald, 15% opacity

    // Animation
    public const int AnimationDurationMs = 800;
    public const string Easing = "ease-in-out";

    public static ApexChartOptions<T> GetOptions<T>(string title, string yAxisLabel = "")
    {
        return new ApexChartOptions<T>
        {
            Chart = new Chart
            {
                Background = Background,
                ForeColor = TextColor,
                Toolbar = new Toolbar { Show = false },
                Zoom = new Zoom { Enabled = true },
                Animations = new Animations { Enabled = true, Easing = (Easing)Enum.Parse(typeof(Easing), Easing.Replace("-", "").Capitalize()), Speed = AnimationDurationMs }
            },
            Theme = new Theme { Mode = Mode.Dark },
            Title = new ApexChartTitle
            {
                Text = title,
                Align = "center",
                Style = new ApexChartTitleStyle
                {
                    Color = "#f8fafc",
                    FontSize = "18px",
                    FontFamily = "'Playfair Display', serif",
                    FontWeight = "600"
                }
            },
            Xaxis = new XAxis
            {
                Type = XAxisType.Datetime,
                Labels = new XAxisLabels
                {
                    Style = new XAxisLabelsStyle { Colors = TextColor },
                    DatetimeFormatter = new DatetimeFormatter { Hour = "HH:mm", Day = "MMM dd" }
                }
            },
            Yaxis = new List<YAxis> { new YAxis
                {
                    Title = new YAxisTitle { Text = yAxisLabel },
                    Labels = new YAxisLabels
                    {
                        Style = new YAxisLabelsStyle { Colors = TextColor },
                        Formatter = @"function(val) { return val ? val.toFixed(1) + '%' : ''; }"
                    }
                }
            },
            Stroke = new Stroke
            {
                Width = new List<double> { 3, 2, 0 },
                Curve = Curve.Smooth,
                DashArray = new List<double> { 0, 5, 0 }  // actual (solid), forecast (dashed), confidence (solid)
            },
            Grid = new Grid
            {
                BorderColor = GridColor,
                StrokeDashArray = 4,
                Row = new GridRow { Colors = new[] { "transparent", "rgba(30, 41, 59, 0.2)" } }
            },
            DataLabels = new DataLabels { Enabled = false },
            Colors = SeriesColors,
            Markers = new Markers
            {
                Size = 4,
                StrokeWidth = 2,
                StrokeColor = "#0f172a",
                FillOpacity = 0.9,
                Shape = MarkerShape.Circle,
                Hover = new MarkersHover { Size = 6 }
            },
            Tooltip = new Tooltip
            {
                Theme = Mode.Dark,
                X = new TooltipX { Format = "yyyy-MM-dd HH:mm" },
                Y = new TooltipY
                {
                    Formatter = @"function(value, { seriesIndex, w }) {
                        var meta = w.globals.seriesMeta[seriesIndex];
                        if (meta && meta.anomaly) {
                            return '⚠️ ' + value.toFixed(2) + '%\n' + meta.message;
                        }
                        return value.toFixed(2) + '%';
                    }"
                }
            },
            Legend = new Legend
            {
                Position = LegendPosition.Bottom,
                FontSize = "12px",
                Labels = new LegendLabels { Colors = TextColor },
                ItemMargin = new LegendItemMargin { Horizontal = 15 }
            }
        };
    }
}
```

**Application to existing components:**

- **`TimeSeriesChart.razor`**: Replace inline `GetChartOptions()` with `ChartTheme.GetOptions<TimeSeriesPoint>(Title, YAxisUnit)`. Preserve existing logic for timezone adjustment, baseline merging, data capping.
- **`DonutChart.razor`** / **`HorizontalBarChart.razor`**: Apply same color palette via `Options.Colors = ChartTheme.SeriesColors`.
- **Forecast series:** Set `StrokeDashArray = 5` and label with " (forecast)" suffix (already done for baseline).
- **Confidence band:** Use `SeriesType.Area` with `FillOpacity = 0.15` and `Color = "#10b981"`.

**SignalR live updates:**
- `AnomalyHub` pushes `AnomalyEvent` to connected clients
- `Capacity.razor` injects `IHubContext<AnomalyHub>` and calls `chart.UpdateSeriesAsync()` to append new anomaly point without re-rendering entire chart
- Anomaly points set `Metadata = new { anomaly = true, message = "Business impact text" }` for tooltip display

**Performance targets:**
- Chart render: < 300 ms for 30-day history + 7-day forecast (2000 data points max)
- Animation: 300–800 ms ease-out
- SignalR → chart update: < 50 ms latency

---

## 6. Error Message Standards (Task #19)

### 4.1 Message Template
```
[ERROR/WARNING/INFO] Title: Clear, non-technical summary

What happened?
  Plain English description of the failure.

Why did it happen?
  Root cause (technical but understandable).

How do I fix it?
  1. Step one
  2. Step two
  3. Step three (include T-SQL if applicable)

Governance Impact:
  [Audit Readiness] — This affects the Monitoring Activities control
  [Cost] — Unoptimized queries may increase cloud spend by ~$X/month
  [Risk] — Exposes organization to R-03: Privileged Account risk
```

### 4.2 Error Catalog (Sample Entries)

| Error Type | What Happened | Why It Matters | Fix Steps | Governance Impact |
|------------|---------------|----------------|-----------|-------------------|
| **SQL Agent Stopped** | SQL Agent is not running | Alerts disabled → monitoring gap → compliance FAIL | 1. `net start SQLAgent$INSTANCE`<br>2. Set to Automatic startup | Monitoring Activities control fails (SOC2) |
| **Connection Failed** | Cannot connect to server | No visibility into server state | 1. Verify server name<br>2. Check firewall<br>3. Confirm credentials | N/A (operational) |
| **Backup Not Found** | No recent backups for database | RPO/RTO failure → data loss risk | 1. Run full backup<br>2. Enable backup job<br>3. Verify restore | Data Recovery Risk (R-01) — CRITICAL |
| **High CPU (Sustained)** | CPU > 90% for 10+ minutes | Performance degradation, cost spike | 1. Identify top queries<br>2. Review missing indexes<br>3. Check plan cache | Performance Risk (R-08) — Increased cloud spend |
| **Permission Denied** | User lacks VIEW SERVER STATE | Cannot monitor server | Run: `GRANT VIEW SERVER STATE TO [user]` | Access Control gap — SOC2 fail |

### 4.3 Implementation Pattern
```csharp
// In every catch block:
catch (Exception ex) when (ex is SqlException or InvalidOperationException)
{
    var error = ErrorCatalog.GetError(ex);
    var message = $@"{error.Summary}

What: {error.What}
Why: {error.Why}
Fix:
{error.FixSteps}

Governance Impact: {error.GovernanceImpact}";

    _logger.LogError(ex, error.Why);
    await ShowMessageBoxAsync(message, "SQLTriage");
}
```

---

## 7. IFindingTranslator Service — 3-Audience Translation Substrate (Task #31)

### 6.1 Purpose

SQLTriage's core differentiator is **translation**: converting technical SQL findings into language each audience understands.

**Outputs per finding:**
- `FindingDba`: Technical details, T-SQL snippets, wait stats, index DMVs, error numbers, repro steps
- `FindingItManager`: SLA impact, remediation effort estimate (hours/days), root cause category, affected systems
- `FindingExecutive`: One-sentence plain-language summary, business risk (LOW/MEDIUM/HIGH/CRITICAL), cost exposure (USD/hr downtime), compliance flags (SOC2 control IDs), priority rationale

### 6.2 Data Model

```csharp
public class FindingTranslation
{
    public Guid FindingId { get; set; }
    public FindingDba Dba { get; set; }
    public FindingItManager ItManager { get; set; }
    public FindingExecutive Executive { get; set; }
}

public class FindingDba
{
    public string CheckId { get; set; }           // e.g., "VA-207"
    public string Title { get; set; }             // "MAXDOP recommendation"
    public string TechnicalDetails { get; set; }  // DMV query results, wait stats
    public string TSqlRemediation { get; set; }   // EXACT T-SQL to run
    public Dictionary<string, object> RawData { get; set; }
}

public class FindingItManager
{
    public string BusinessCategory { get; set; }  // "Performance", "Security", "Reliability"
    public string SlaImpact { get; set; }         // "Degraded response time (P2 incident risk)"
    public string RemediationEffort { get; set; } // "2 hours (DBA)" or "4 hours (consultant)"
    public bool RequiresChangeControl { get; set; }
    public string RelatedFindings { get; set; }   // Comma-separated check IDs
}

public class FindingExecutive
{
    public string PlainLanguageSummary { get; set; }   // "Your server configuration risks data loss during outages"
    public string BusinessRisk { get; set; }           // CRITICAL/HIGH/MEDIUM/LOW — derived from Governance per-finding score band, NOT invented by translator
    public decimal? EstimatedMonthlyCost { get; set; } // Nullable — calculated where possible
    public string[] ComplianceControls { get; set; }   // ["SOC2-CC6.1", "HIPAA-164.312"] — from queries.json "controls" array
    public string RecommendedAction { get; set; }      // "Fund a 2-hour DBA engagement to fix MAXDOP"
}
```

### 6.3 Implementation

**Data flow (non-circular):**
1. `VulnerabilityAssessmentService` runs checks → produces `CheckResult` objects (severity, category, raw data)
2. `GovernanceService.ComputeFullAsync()` consumes CheckResults → applies capped scoring + vector weights → produces `GovernanceReport` (overall score + maturity band) AND per-finding `BusinessRiskBand` (`"CRITICAL"|"HIGH"|"MEDIUM"|"LOW"`) derived from capped per-finding contribution
3. `IFindingTranslator.TranslateAsync(findingId)` consumes: (a) CheckResult raw data, (b) QueryMetadata (from `SqlQueryRepository`), (c) BusinessRiskBand (from Governance) — **translator is read-only consumer, does not affect Governance state**

**Cache invalidation:**
- Translation cached with key `(findingId, translatorVersion, governanceWeightsVersion)`
- Invalidate on: VA re-run (finding data changes), governance-weights.json reload (weights change → BusinessRiskBand may change)
- TTL: 1 hour (hard)

**Compliance controls mapping:** Each query in `Config/queries.json` includes `"controls": ["SOC2-CC6.1", "HIPAA-164.312"]` array; translator copies into `FindingExecutive.ComplianceControls` verbatim. No computation.

`IFindingTranslator.TranslateAsync(Guid findingId, TargetAudience? filter = null)`
- Returns `FindingTranslation` with all three audience renderings
- Caches in `SqliteCacheStore` (TTL: 1 hour or on VA re-run)
- Pulls VA check metadata from `Config/queries.json` — each query has `audienceTags: ["dba","it","exec"]`
- RBAC filters visible translations per user role correlating to audience

**Why this layer exists:** Without translation, findings are noise to 2/3 of decision-makers. Translation substrate is the **"sadaqah jariyah" engine** — it's what turns consultant-billed savings into self-served value.

---

## 6.1 ICheckRunner Interface (New — Week 1 Abstraction)

---

## 7. ICheckRunner Interface & Quick Check Orchestration

### 7.1 Purpose

Abstract check execution to support:
- Full VA (all checks)
- Quick Check (subset tagged `"quick": true`)
- Custom selection (e.g., "run only Security checks")
- Budget enforcement (time-boxing)

### 7.2 Interface

```csharp
public interface ICheckRunner
{
    /// <summary>
    /// Run a subset of checks within a time budget.
    /// </summary>
    /// <param name="checkIds">Explicit list of check IDs to run. If empty, uses default subset (all quick-tagged checks).</param>
    /// <param name="budget">Time budget — execution stops when cumulative elapsed >= budget, cancels in-flight checks gracefully.</param>
    /// <param name="cancellationToken">Global cancellation token (app shutdown).</param>
    /// <returns>Aggregate result with per-check outcomes, execution summary, and any timeouts.</returns>
    Task<CheckRunResult> RunSubsetAsync(IEnumerable<string> checkIds, TimeSpan budget, CancellationToken cancellationToken);

    /// <summary>
    /// Run all checks (full Vulnerability Assessment).
    /// </summary>
    Task<CheckRunResult> RunAllAsync(CancellationToken cancellationToken);
}

public class CheckRunResult
{
    public List<CheckResult> Results { get; set; } = new();
    public int CompletedCount { get; set; }
    public int TimedOutCount { get; set; }
    public TimeSpan TotalElapsed { get; set; }
    public bool BudgetExceeded { get; set; }      // true if we hit the time limit
    public string[] Warnings { get; set; } = Array.Empty<string>();
}
```

### 7.3 Implementation Notes

**Default Quick Check subset:** If `checkIds` is empty, `RunSubsetAsync` queries `SqlQueryRepository.GetByTag("quick")` (cached list of ~40 check IDs).

**Concurrency control:** SemaphoreSlim with `maxDegree = min(Environment.ProcessorCount, 4)`. Per-check timeout: read from `queries.json` entry `timeoutSec` (default 8s). Individual check cancellable via linked CancellationToken.

**Budget enforcement:** Track `Stopwatch.StartNew()`; before launching each check, check `elapsed.TotalMilliseconds`. If remaining budget < per-check expected (heuristic: last 3 runs median), skip remaining and mark `BudgetExceeded = true`. At budget=55s for 60s SLA, leave 5s buffer for UI update.

**Slow DMV handling:** If any check exceeds its `timeoutSec` without yielding, cancel it, log warning, increment `TimedOutCount`, continue. Quick Check never waits for a single hung query.

**WAN mode fallback:** If average connection RTT > 50ms (detected from first 5 checks), automatically switch to `maxDegree=2` and increase per-check timeout to 12s, warn user "Extended Check mode activated (remote server)" — user consent required to proceed beyond 60s.

---

## 8. ChartTheme Specification (Task #36)

### 7.1 Philosophy

Charts are the most-viewed UI element. They must convey Rolls Royce quality: muted palette, smooth motion, clear hierarchy.

### 7.2 ChartTheme.cs Singleton

```csharp
public class ChartTheme
{
    public static ChartTheme Current { get; } = new ChartTheme();

    // Premium muted palette
    public string[] SeriesColors { get; } = { "#10b981", "#f59e0b", "#3b82f6", "#ef4444", "#8b5cf6", "#06b6d4" };
    public string Background { get; } = "transparent";
    public string GridColor { get; } = "#334155";      // slate-700
    public string GridStyle { get; } = "4";             // dashed
    public string TextColor { get; } = "#94a3b8";       // slate-400
    public string FontFamily { get; } = "'Inter', sans-serif";

    // Forecast overlay
    public int ForecastStrokeWidth { get; } = 2;
    public string ForecastDashArray { get; } = "5";
    public string ConfidenceFill { get; } = "rgba(16, 185, 129, 0.15)";

    public ApexChartOptions<T> GetOptions<T>(string title, string yAxisLabel = "")
    {
        // Returns fully configured ApexCharts options with Playfair Display title, smooth curves, dashed forecast support
    }
}
```

### 7.3 Integration Points

- `TimeSeriesChart.razor` — replace inline `GetChartOptions()` with `ChartTheme.GetOptions<TimeSeriesPoint>()`
- `StatCard.razor` sparklines — apply `SeriesColors[0]` with rounded caps
- `Governance/Dashboard.razor` donut charts — use emerald/amber/rose palette
- Any new chart component must call `ChartTheme.GetOptions()`; hardcoded colors rejected

### 7.4 Visual Spec

| Element | Value |
|---------|-------|
| Title font | Playfair Display, 18px, #f8fafc, weight 600 |
| Axis labels | Inter, 11px, #64748B |
| Grid lines | Dashed, #334155, 1px |
| Animation | 300–800 ms ease-out |
| Forecast series | Dashed line (5px), stroke width 2px |
| Confidence band | Area series, fill opacity 0.15 |
| Hover marker | +2px radius, white stroke 2px |

---

## 9. QuestPDF Report Engine (Task #34)

### 5.1 Interface
```csharp
public interface IGovernanceService
{
    ExecutiveSnapshot GetExecutiveSnapshot(string serverName);
    List<RiskRegisterEntry> GetRiskRegister(string serverName);
    GovernanceReport GenerateReport(string serverName, ReportOptions options);
    ComplianceScore GetComplianceScore(string serverName, string framework); // SOC2, HIPAA, ISO
    CostEfficiencyAnalysis GetCostEfficiency(string serverName);
}

public interface IPermissionCheckService
{
    PermissionValidationResult ValidateConnectionPermissions(string connectionString);
}

public interface IAuditLogService
{
    void Log(AuditEntry entry);           // HMAC-signed
    List<AuditEntry> Export(DateTime from, DateTime to);
    bool VerifyIntegrity();               // Validate HMAC chain
}
```

### 5.2 Caching Strategy
- Governance snapshot cached for 15 minutes ( Dashboard )
- Risk register refreshed on-demand
- Report generation is real-time (no cache — always fresh)

---

## 8. Database Schema Additions

### 6.1 RBAC Tables
```sql
CREATE TABLE Roles (
    RoleId INT PRIMARY KEY IDENTITY,
    Name NVARCHAR(50) UNIQUE NOT NULL,        -- Admin, DBA, ReadOnly, Auditor, Operator
    Description NVARCHAR(255),
    CreatedAt DATETIME DEFAULT GETUTCDATE()
);

CREATE TABLE Users (
    UserId INT PRIMARY KEY IDENTITY,
    Username NVARCHAR(100) UNIQUE NOT NULL,
    PasswordHash NVARCHAR(256) NOT NULL,       -- Argon2id hash (subkey)
    PasswordSalt NVARCHAR(64) NOT NULL,        -- 128-bit random salt
    PasswordIterations INT NOT NULL DEFAULT 100000,  -- Argon2id iterations
    RoleId INT FOREIGN KEY REFERENCES Roles(RoleId),
    Active BIT DEFAULT 1,
    LastLogin DATETIME NULL,
    CreatedAt DATETIME DEFAULT GETUTCDATE()
);

CREATE TABLE UserAudit (
    AuditId INT PRIMARY KEY IDENTITY,
    ActorUserId INT FOREIGN KEY REFERENCES Users(UserId),
    Action NVARCHAR(100),                      -- "RoleAssigned", "UserCreated", "Login"
    TargetUserId INT NULL,
    Details NVARCHAR(MAX),
    IpAddress NVARCHAR(45),
    UserAgent NVARCHAR(255),
    Timestamp DATETIME DEFAULT GETUTCDATE()
);
```

### 6.2 Audit Log Table (Tamper-Proof)
```sql
CREATE TABLE AuditLog (
    AuditId INT PRIMARY KEY IDENTITY,
    Timestamp DATETIME DEFAULT GETUTCDATE(),
    Username NVARCHAR(100),
    Action NVARCHAR(100),
    Resource NVARCHAR(200),
    Details NVARCHAR(MAX),
    -- HMAC chain fields:
    PreviousHash NVARCHAR(64) NULL,           -- SHA256 of previous row
    CurrentHash NVARCHAR(64) NOT NULL,        -- SHA256(Timestamp+Action+PrevHash)
    Signature NVARCHAR(256) NOT NULL          -- HMAC-SHA256(CurrentHash, DPAPI key)
);
```

### 6.3 Governance Findings Cache (optional)
```sql
CREATE TABLE GovernanceFindings (
    FindingId INT PRIMARY KEY IDENTITY,
    ServerName NVARCHAR(200) NOT NULL,
    Framework NVARCHAR(50),                    -- SOC2, HIPAA, ISO27001
    Control NVARCHAR(100),                     -- Access Control, Monitoring
    Status NVARCHAR(20),                       -- PASS, WARN, FAIL
    Evidence NVARCHAR(MAX),                    -- Query result or check output
    LastChecked DATETIME,
    Indexed ON (ServerName, Framework)
);
```

---

## 9. Compliance Alignment Matrix

### 7.1 International Standards

| SQLTriage Feature | SOC2 CC | HIPAA | ISO 27001 | NIST CSF |
|-------------------|---------|-------|-----------|----------|
| **RBAC + Auditor role** | Access Control (CC6.1) | Access Control (164.312) | A.9.2.1-3 | PR.AC |
| **Tamper-proof audit log** | Monitoring Activities (CC7.2) | Audit Controls (164.312) | A.12.4.1 | AU.PR |
| **Configuration drift detection** | Change Management (CC8.1) | Audit controls | A.12.1.2 | CM.PS |
| **Governance Report export** | Evidence collection | Audit evidence | A.18.2.3 | PR.IP |
| **Error messages with risk context** | System monitoring | Audit logging | A.12.4.3 | AU.RA |
| **AD/LDAP integration** | Logical access | User auth | A.9.4.2 | IA.AT |
| **GPO deployment** | Configuration management | Audit config | A.12.1.1 | CM.PS |

**Claim wording:** *"SQLTriage helps maintain controls aligned with SOC2, HIPAA, and ISO 27001 frameworks."*

### 7.2 Regional Compliance Frameworks

| Region / Framework | Full Name | SQLTriage Alignment | Key Controls Covered | Reference |
|--------------------|-----------|---------------------|---------------------|-----------|
| **🇺🇸 USA** | SOC 2 Type II | ✅ Aligned | CC6.1 (Access), CC7.2 (Monitoring), CC8.1 (Change) | [AICPA SOC 2](https://www.aicpa.org/interestareas/frc/assuranceadvisoryservices/aicpasoc2report.html) |
| **🇺🇸 USA** | HIPAA / HITECH | ✅ Aligned | 164.312 (Access Control), 164.312 (Audit Controls) | [HHS HIPAA](https://www.hhs.gov/hipaa/for-professionals/security/index.html) |
| **🇺🇸 USA** | NIST CSF 2.0 | ✅ Aligned | PR.AC, AU.PR, CM.PS | [NIST CSF](https://www.nist.gov/cyberframework) |
| **🇪🇺 EU** | GDPR | ✅ Aligned | Art 32 (Security), Art 33 (Breach Notification) | [GDPR Official](https://gdpr-info.eu/) |
| **🇿🇦 South Africa** | POPIA | ✅ Aligned | Security measures, breach reporting | [POPIA Official](https://www.gov.za/issues/popia) |
| **🇦🇺 Australia** | CPS 234 (APRA) | ✅ Aligned | ISM controls, audit logging | [APRA CPS 234](https://www.apra.gov.au/prudential-standard-cps-234-information-security) |
| **🇦🇺 Australia** | NZISM (NZ) | ✅ Aligned | PCL — Privileged Access, Audit | [NZISM](https://www.gcsb.govt.nz/security-of-government-systems/nzism/) |
| **🇬🇧 UK** | UK GDPR | ✅ Aligned | Art 32 security obligations | [ICO UK GDPR](https://ico.org.uk/for-organisations/guide-to-data-protection/uk-gdpr-principles-and-accountability/) |
| **🇨🇦 Canada** | PIPEDA | ✅ Aligned | Security safeguards, breach reporting | [PIPEDA](https://www.priv.gc.ca/en/privacy-topics/privacy-laws-in-canada/the-personal-information-protection-and-electronic-documents-act-pipeda/) |
| **🇮🇳 India** | DPDP Act 2023 | ✅ Aligned | Data protection, breach notification | [Digital India](https://digitalindia.gov.in/) |
| **🇸🇬 Singapore** | PDPA | ✅ Aligned | Protection obligation, breach notification | [PDPA](https://www.pdpc.gov.sg/overview-of-pdpa/the-legislation/personal-data-protection-act) |
| **🇯🇵 Japan** | APPI | ✅ Aligned | Anonymization, security | [PPC Japan](https://www.ppc.go.jp/en/) |

**Note:** SQLTriage does NOT certify compliance. It provides evidence and alignment to help organizations meet these frameworks' technical control requirements. Implementers remain responsible for their own compliance.

### 7.3 SOC2 Type II Certification Path (v2.0+ Goal)

**Current state (v1.0):** Tool provides evidence, not certified.

**Target (v2.0–v2.5):** Support customers pursuing SOC2 certification.

#### Certification Readiness Checklist (SQLTriage as evidence provider):

1. **Audit Trail Completeness** ✅ v1.0
   - Tamper-proof logging with HMAC chain
   - User action logging (who did what, when)
   - Exportable audit trail for 90+ days

2. **Access Control Evidence** ✅ v1.0
   - RBAC with role assignment logs
   - AD/LDAP integration (v1.5)
   - Privileged account monitoring

3. **Change Management Evidence** ✅ v1.0
   - Configuration drift detection
   - Historical baseline comparisons
   - Approval workflow trail (future)

4. **System Monitoring Evidence** ✅ v1.0
   - Alert history with acknowledgment
   - Incident correlation to findings
   - Uptime/downtime tracking

5. **Risk Assessment Documentation** ✅ v1.0
   - Governance Report with risk ratings
   - Business impact analysis
   - Remediation tracking

#### To become "SOC2-ready" as a tool provider (v2.0):
- [ ] Third-party penetration test report (annual)
- [ ] Internal controls documentation (your own org's SOC2 if offering SaaS)
- [ ] Vendor security questionnaire (VSA, SIG) completed
- [ ] Data processing agreement (DPA) available for customers
- [ ] Incident response plan documented and tested

**Strategic note:** You do NOT need SOC2 certification for your tool to be used in SOC2 audits. Your customers use SQLTriage as *evidence* of their own controls. However, if you offer a **hosted SaaS** version (v2.0+), then you'll need your own SOC2 certification.

---

## 10. Performance Requirements

| Metric | Target | Measurement |
|--------|--------|-------------|
| Dashboard load time | < 2 seconds | With 50 connected servers |
| Report generation | < 30 seconds | Full 3-page PDF |
| Onboarding flow | < 5 minutes | From launch to first data |
| **Quick Check time-to-first-result** | **< 60 seconds** | **EXE launch → Quick Check summary visible (P95, local SQL, RTT ≤15 ms)** |
| Memory footprint | < 500 MB | Idle with 10 servers connected |
| CPU overhead | < 2% | During normal polling cycles |

---

## 11. Acceptance Criteria

**Definition of Done for v1.0:**

- [ ] Governance Dashboard loads without errors, displays Risk Level + Maturity % with capped scoring (GovernanceService)
- [ ] QuestPDF report engine integrated; Governance Report PDF exports (3-page, Playfair headings, Cost of Downtime calculator, native QuestPDF charts)
- [ ] Error catalog implemented with 65 scenarios; each has BusinessRisk/GovernanceImpact/Remediation/Audience; `ErrorCatalogTests` coverage ≥ 95% + golden-file match
- [ ] RBAC uses Konscious Argon2id password hashing (not CredentialProtector); Admin/DBA/ReadOnly/Auditor/Operator roles functional; RoleGuard component protects pages; admin bootstrap in Onboarding step 1 completes; session timeout enforces (Admin/DBA 8h, Auditor 4h)
- [ ] AuditLogService uses single-writer queue + 64 KB (configurable) checkpoint files + DPAPI-machine HMAC + Windows Event Log mirror (graceful fallback); startup chain validation detects tampering and refuses writes until admin acknowledges; HMAC key rotation 90d + 7d grace
- [ ] IFindingTranslator service returns DBA/IT/Executive renditions per finding using rules-engine for Quick Check subset (~40 checks); translations cached keyed by (findingId, translatorVersion, weightsVersion); invalidate on VA re-run or weights reload
- [ ] ChartTheme singleton applied to all charts (TimeSeriesChart, StatCard, GaugeChart, any BandedHeatmap); Rolls Royce emerald/amber/slate/rose palette + Playfair headings
- [ ] **Quick Check:** EXE launch → onboard → Quick Check results visible ≤ 60 seconds (P95, local SQL Server); per-check timeout 8s respected; budget cut at 55s; slow checks cancelled without preventing results
- [ ] **Quick Check:** Shows PASS/WARN/FAIL counts per category; CRITICAL findings trigger toast notification; results marked IsIndicative=true with disclaimer chip
- [ ] `Config/governance-weights.json` loaded by GovernanceService via IOptionsMonitor; hot-reload triggers Governance + Translator cache invalidation; UI weight editor optional v1.1
- [ ] `Config/queries.json` metadata includes `"quick": true`, `"audience": ["dba","it","exec"]`, `"controls": [...]`, `"governance": { categoryWeight }`, `"timeoutSec"` for all checks; hot-reload survives malformed JSON gracefully
- [ ] `ICheckRunner` abstraction exists and is used by both Quick Check and Full VA; `RunSubsetAsync(checkIds, budget, ct)` honors time budget and per-check timeouts
- [ ] Unit test coverage ≥ 80% across all services (Coverlet); integration tests in SQLTriage.Tests.Integration gated behind SQLTRIAGE_INTEGRATION env var
- [ ] Basmalah header present on every .cs/.razor/.css/.js/.md file (pre-commit hook active, CI lint passing)
- [ ] Website copy revised to "SQLTriage — Governance & Translation Platform"; 16 screenshots captured & committed; sample Governance Report PDF hosted
- [ ] 5 beta testers confirm: "I can see why my manager would care about this" (translation layer validated)
- [ ] **AuditLog tamper recovery:** when startup chain validation detects HMAC mismatch, app enters read-only mode (banner visible, dashboard accessible, Report export + RBAC changes disabled); Admin can acknowledge via Settings (requires Admin role + reason string + password re-entry ≥ zxcvbn 3); acknowledgement logged to Windows Event Log independent of tampered chain; app resumes writes after ack; no data loss
- [ ] INFO severity findings surface in IFindingTranslator output (DBA/IT/Executive renditions) but contribute 0 points to GovernanceService score and do not appear in Maturity band calculation
- [ ] No P1 bugs open at release candidate; clean build (zero warnings); single-exe publish (win-x64) succeeds

---

## 12. Out of Scope (v1.0)

**Deferred to v1.1:**
- Full AI/ML ONNX pipeline (anomaly detection, multi-variate forecasts, SignalR streaming) — spec retained in §5 for v1.1
- Tailwind UI theme refresh / full Rolls Royce migration — ChartTheme singleton applies to charts only; component styling uses existing `app.css` in v1.0
- AD/LDAP enterprise authentication (local auth only for v1.0)
- GPO ADMX templates (Enterprise tier post-v1.0)
- Advanced multivariate forecasting (CPU only linear regression may be Should-have)
- Documentation Generator page (post-v1.0)
- Code signing automated workflow (manual release acceptable; defer to v1.0.1)

**Never in scope:**
- Certified compliance (SOC2 stamp) — tool provides evidence only
- Cloud-native SaaS offering — v1.0 is desktop single-EXE
- Multi-tenancy — single-tenant per installation
- Custom framework builder — fixed set: SOC2, HIPAA, ISO 27001, NIST CSF
- White-label branding — SQLTriage brand is fixed

---

**Document version:** 1.0  
**Next review:** After v1.0 beta feedback  
**Approval:** [Pending]
