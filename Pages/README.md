<!-- In the name of God, the Merciful, the Compassionate -->

# Pages

Razor pages. Audit-first front door is `/cio` `/compliance-map` `/governance` `/diagnostics-roadmap` `/quickcheck` `/fullaudit`; live monitoring companions are `/dashboard` `/sessions` `/blocking` `/waitevents` `/querystore`. See `.claude/docs/pages-index.md` for a per-page summary.

## Audit-Prominence Pages

| Page | Description |
|------|-------------|
| CioDashboard | Executive health dashboard with KPI summary, governance scoring, and trend alerts |
| ComplianceMap | Framework-to-check coverage matrix showing CIS, PCI, HIPAA, GDPR alignment |
| Governance | Governance control centre with policy editor, scorecards, and remediation planner |
| DiagnosticsRoadmap | Serializable diagnostics plan builder with step-by-step execution tracking |
| QuickCheck | One-click SQL instance health assessment with pass/fail summary |
| FullAudit | Deep-dive audit engine running SQL + PowerShell + WMI checks across instances |
| VulnerabilityAssessment | Microsoft Vulnerability Assessment integration with findings drill-down |
| BestPractice | Curated BP script runner with explanation, risk score, and fix scripts |
| Checks | Individual check definition editor with validation and test-run capability |
| UnifiedChecks | Aggregate check runner bridging SQL, PowerShell, WMI, and registry executors |

## Live-Monitoring Companion Pages

| Page | Description |
|------|-------------|
| Dashboard | User-customisable dynamic dashboard with drag-drop panel editor |
| Sessions | Active session grid with kill, detail, and historical drill-down |
| Blocking | Real-time blocking chain tree with head-blocker identification |
| WaitEvents | Wait statistics analyser with category breakdown and trend charts |
| QueryStore | Query Store explorer with forced-plan regression detection |
| XEvents | Extended Events session manager with live target viewer |
| PerfInspector | PerfMon counter collector with time-series comparison charts |
| LongQueries | Running-query monitor with elapsed-time and blocking detection |
| InstanceOverview | SQL Server instance health summary from DMV snapshots |
| SchedulerHealth | Scheduler yield and CPU utilisation monitor across NUMA nodes |

## Conventions

- Routes use `RouteConstants` for compile-time safety.
- Pages inject services via `@inject` directive; constructor injection is not used in Razor.
- State is scoped to the page instance; transient data lives in `OnInitializedAsync`.
- Content-heavy pages use `@implements IDisposable` to tear down timers and subscriptions.
