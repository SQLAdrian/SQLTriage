# SQL Health Assessment — Free SQL Server Monitoring & DBA Tool

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE.txt)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-0078d4.svg)](https://github.com/SQLAdrian/SqlHealthAssessment/releases)
[![SQL Server 2016+](https://img.shields.io/badge/SQL%20Server-2016%2B-red.svg)](https://www.microsoft.com/en-us/sql-server)

> Free, open-source SQL Server audit, health assessment, and performance analysis tool for Windows DBAs. v1.0.0 (2026)
> A lightweight alternative to expensive commercial SQL monitoring tools like SolarWinds DPA, Redgate SQL Monitor, SentryOne, and Idera SQL Diagnostic Manager.

**SQL Health Assessment** is a self-contained Windows desktop application that monitors multiple SQL Server instances in real time. It provides live dashboards, health checks, blocking chain analysis, interactive query execution plan viewing, wait-event statistics, vulnerability assessments, and comprehensive audit reports — all from a single executable with no agent installation required on your SQL Servers.

Built on the battle-tested [SQLWATCH](https://github.com/marcingminski/sqlwatch) and [Erik Darling's PerformanceMonitor](https://github.com/erikdarlingdata/DarlingData) frameworks, it combines the best open-source SQL Server diagnostic scripts ([sp_Blitz](https://github.com/BrentOzarULTD/SQL-Server-First-Responder-Kit), [sp_triage](https://sqldba.org), [MadeiraToolbox](https://github.com/MadeiraData/MadeiraToolbox), [TigerToolbox](https://github.com/microsoft/tigertoolbox)) into a single tool with a modern UI.

### Who is this for?

- **SQL Server DBAs** who need a free monitoring tool that doesn't require complex infrastructure
- **Teams running SQL Server 2016–2022** (on-premises, Azure VM, or Azure SQL Managed Instance)
- **Consultants** who need a portable tool to quickly assess SQL Server health at client sites
- **Organizations** looking for an open-source alternative to SolarWinds, Redgate, SentryOne, or Idera

### What problems does it solve?

- **"What's happening on my SQL Server right now?"** — Live dashboards show active sessions, blocking chains, top queries, and wait stats in real time
- **"Why is my SQL Server slow?"** — Interactive execution plan viewer, wait statistics trends, and long-running query detection
- **"Is my SQL Server healthy?"** — Quick Check (30 seconds) and Full Audit (comprehensive) with exportable results
- **"Is my SQL Server secure?"** — Microsoft SQL Vulnerability Assessment with 500+ security checks
- **"How do I monitor multiple servers?"** — Multi-server support with aggregated views and per-instance drill-down
- **"How do I export audit results to the cloud?"** — Azure Blob Storage integration with automatic CSV upload

---

## Features

### Real-Time Monitoring
- **Live Dashboards** — configurable panels for sessions, wait events, query stats, and memory pressure
- **Multi-Server Support** — switch between servers or view aggregated data across all instances
- **Always On AG Dashboard** — replica sync status, redo/send queue trends, listener configuration, failover readiness
- **Replication Monitor** — publications, subscriptions, undistributed commands, distribution agent history
- **SQL Agent Job Monitor** — running/failed/succeeded job counts, failure detail, schedules, long-running job tracking
- **Auto-Refresh** — configurable polling interval with idle-mode throttling
- **Session Viewer** — active sessions with blocking chain analysis and kill capability

### Health & Analysis
- **Quick Check** (`Ctrl+Q`) — instant assessment: CPU, memory, blocking, missing/unused indexes
- **Full Audit** (`Ctrl+4`) — deep-dive covering configuration, security, backups, fragmentation; Sequential or Parallel execution across multiple servers with configurable stagger delay and confirmation modal
- **Vulnerability Assessment** — SQL Server security assessment with exportable results
- **Diagnostics Maturity Roadmap** — maps sp_triage and sp_Blitz audit output to a 5-level maturity framework (Foundation → Hardened → Performant → Observable → Optimised); multi-server, multi-file, PDF export
- **Interactive Execution Plan Viewer (V2)** — graphical plan with hover detail pane (object path, accurate per-operator cost %, predicate, copy button); root operator always shows 100%; pane fades after 1.5 s with hover-cancel; accessible from blocking chains and expensive query grids
- **Long Query Detection** — surface queries exceeding configurable duration thresholds
- **Wait Statistics** — categorised wait event history and trends, including locking-waits %
- **PM Health & Diagnostics** — dedicated dashboard surfacing Erik Darling PerformanceMonitor `report.*` views: critical issues, CPU spikes, memory pressure events, plan cache bloat, memory grant pressure, scheduler runnable queue, parameter sniffing (PSP), file I/O latency, blocking chains with exact statement extraction, and FinOps cost/provisioning/peak-hours analysis

### Alerting & Notifications
- **69 Custom Alerts** — Performance, memory, storage, HA, security with SQL 2016+ compatibility
- **Extended Events Integration** — Real-time deadlock/error monitoring via XE sessions
- **Timer-based evaluation** — 30-second alert cycle with configurable severity thresholds and cooldowns
- **7 notification channels** — Email (SMTP), Microsoft Teams, Slack, generic Webhooks, PagerDuty, ServiceNow, WhatsApp
- **Alert history** — SQLite-backed alert log with acknowledgement and auto-resolution
- **Scheduled tasks** — automated task engine with CSV export, Azure Blob upload, and email delivery
- **No-Pants Mode** — Safety toggle for destructive actions (e.g., kill sessions)

### Performance & Reliability
- **SQLite WAL-mode cache** — panels serve cached data when SQL Server is temporarily unreachable; indexed `fetched_at` on all 6 cache tables for fast eviction scans
- **Delta-fetch for time-series** — only new data points are fetched on each refresh
- **Two-tier query throttling** — protects the monitored server from excessive load
- **Zero-allocation row reading** — `ArrayPool<T>` and streaming JSON serialisation
- **Cancellable dashboard loads** — switch servers or hit Cancel to abort in-flight queries instantly
- **Memory pressure monitoring** — background service alerts and evicts cache under high memory load
- **Server GC + concurrent GC** — tuned for optimal throughput in `runtimeconfig`
- **DataGrid max-height overflow** — dashboard tables cap at 3× their configured panel height with sticky headers; prevents page-layout jitter on result-count changes

### Updates & Maintenance
- **Squirrel.Windows Auto-Updates** — Silent, delta-based updates from GitHub releases; 3-tier extraction fallback (`Expand-Archive` → .NET `ZipFile` → `Shell.Application` COM) for older Windows Server targets
- **Manual ZIP import** — server mode / air-gap: upload a downloaded release ZIP directly via the Service Management page
- **GitHub Actions CI/CD** — Automated build, test, and release on version tags

### Data Export
- **Azure Blob Storage integration** — auto-upload audit CSVs with dual-path upload (Azure SDK + AzCopy fallback)
- **SAS token & User Delegation SAS** — supports account-level and directory-scoped SAS tokens
- **Connection diagnostics** — built-in modal to troubleshoot Azure Blob authentication issues
- **Toast notifications** — real-time upload success/failure feedback

### Security
- **AES-256-GCM credential encryption** — machine-scoped DPAPI key file + authenticated encryption; works for interactive and service accounts
- **Ephemeral session keys** — dashboard results encrypted with a per-process key (never persisted)
- **MFA / Azure AD authentication** — supports modern auth via `Azure.Identity`
- **Parameterised queries only** — SQL injection prevention throughout
- **Assembly obfuscation** — ConfuserEx2 (anti-tamper, anti-debug, string encryption)
- **Audit log** — full query-execution and user-action trail (90-day retention)
- **Rate limiting** — configurable max queries per minute with optional UI warnings
- **Windows Service mode** — headless deployment with Kestrel HTTPS support

### Server Management
- **Server tagging** — assign tags (e.g. `finance`, `critical`, `east-us`) and environment labels (Production, Staging, Dev, QA, DR)
- **Filter by tag or environment** — click-to-filter across the server inventory
- **Connection pool** — configurable pool (default 50 connections) with idle cleanup and overflow handling
- **Blazor Server circuit monitoring** — tracks active circuits with lifecycle logging for long-running server-mode deployments

### Developer Experience
- **JSON-based dashboard editor** — add, reorder, drag-and-drop panels without code changes
- **Keyboard shortcuts** — full keyboard navigation (`Ctrl+1–9`, `?` to show help)
- **Serilog structured logging** — 30-day rolling logs with configurable verbosity

---

## Screenshots

> *Screenshots coming soon — live dashboards, execution plan viewer, vulnerability assessment, and audit reports. Contributions welcome — see [CONTRIBUTING.md](CONTRIBUTING.md).*

---

## Requirements

| | Minimum | Recommended |
|---|---|---|
| **OS** | Windows 10 (1809+) | Windows 11 / Server 2022 |
| **RAM** | 4 GB | 8 GB |
| **Disk** | 500 MB | 2 GB (logs + cache) |
| **Runtime** | .NET 8.0 Desktop Runtime | (bundled installer included) |
| **SQL Server** | SQL Server 2016 | SQL Server 2019+ |

> **Note:** The application download is ~120 MB (optimized) or ~350 MB (full). See [FOOTPRINT_REDUCTION_QUICK.md](FOOTPRINT_REDUCTION_QUICK.md) for size optimization options.

### SQL Server Permissions

The monitoring account needs:
- `VIEW SERVER STATE`
- `VIEW DATABASE STATE`
- `db_owner` on the SQLWATCH database (or equivalent read rights)

---

## Quick Start

### 1. Download

Grab the latest release from the [Releases page](https://github.com/SQLAdrian/SqlHealthAssessment/releases).
Extract the ZIP to a folder, e.g. `C:\Tools\SqlHealthAssessment`.

### 2. Deploy SQLWATCH (first time only)

SQLWATCH is the metrics-collection layer that lives in your SQL Server.

**Option A — from within the app (recommended):**
1. Launch `SqlHealthAssessment.exe`
2. Go to **Database Deploy** in the navigation
3. Enter your server details and click **Deploy**

**Option B — manual SQL scripts:**
```sql
-- Run in order against your target server:
-- 1. SQLWATCH_db\01_CreateSQLWATCHDB.sql
-- 2. SQLWATCH_db\02_PostSQLWATCHDBcreate.sql
```

Or deploy `Dacpacs\SQLWATCH.dacpac` via SSMS (right-click Databases → Deploy Data-tier Application).

### 3. Connect and Monitor

1. Go to **Servers** → **Add Server**
2. Enter the server name and choose Windows or SQL authentication
3. Click **Test Connection**, then **Save**
4. Navigate to **Live Monitor** or **Instance Overview** to start monitoring

---

## Dashboards

| Dashboard | Description | Shortcut |
|---|---|---|
| Repository | Status overview of all monitored servers | `Ctrl+1` |
| Live Monitor | Sessions, waits, top queries, execution plans | `Ctrl+2` |
| Instance Overview | Detailed per-instance metrics | `Ctrl+3` |
| Full Audit | Comprehensive deep-dive assessment | `Ctrl+4` |
| Long Queries | Queries exceeding duration threshold | — |
| Wait Events | Wait statistics and trends | — |
| Blocking | Blocking chains and deadlock analysis | — |
| Performance Monitor | Erik Darling's diagnostic query dashboards | — |
| Live Wait Stats | Wait category breakdown with locking-waits % | — |
| Live Index Health | Missing indexes, fragmentation, unused indexes | — |
| Backup Health | Databases overdue for full / log backups, history | — |
| Live Query Stats | Plan cache hit ratio, top CPU / IO / duration queries | — |
| Live Jobs | Running jobs, failures (24 h), all-jobs status | — |
| Live TempDB | TempDB usage, version store, long transactions, contention analysis | — |
| Replication | Publications, subscriptions, undistributed commands, agent history | — |
| Always On AG | AG health, replica sync, redo/send queue trends, listeners | — |
| Job Monitor | Agent jobs overview, failure detail, schedules, long-running jobs | — |
| PM Health & Diagnostics | Erik Darling PerformanceMonitor — critical issues, collection health, CPU spikes, memory pressure, plan cache bloat, memory grant pressure, scheduler pressure, parameter sniffing, I/O latency, blocking chains, FinOps database cost/provisioning/peak-hours | — |
| Vulnerability Assessment | SQL Server security assessment results | — |
| Checks | Automated health check results | — |
| Quick Check | Instant health snapshot | `Ctrl+Q` |
| Diagnostics Maturity Roadmap | Multi-server maturity scoring from sp_triage / sp_Blitz output, 5-level framework, PDF export | — |

Press `?` at any time to see the full keyboard shortcut reference.

---

## Configuration

All settings live in `appsettings.json` alongside the executable. The most common options:

```json
{
  "RefreshIntervalSeconds": 35,
  "QueryTimeoutSeconds": 60,
  "DataRetentionDays": 7,
  "MaxQueryRows": 2000,
  "RateLimiting": {
    "Enabled": true,
    "MaxQueriesPerMinute": 50
  }
}
```

Dashboard layouts are defined in `dashboard-config.json` and can be edited directly in the application under **Dashboard Editor** (`Ctrl+E`).

See the [Deployment Guide](DEPLOYMENT_GUIDE.md) for the full configuration reference, including enterprise deployment (GPO, SCCM/Intune), security hardening, and backup/recovery procedures.

---

## Architecture

```
┌──────────────────────────────────────────────────────────────┐
│                   WPF Window (MainWindow)                     │
│  ┌────────────────────────────────────────────────────────┐  │
│  │              Blazor WebView (UI Layer)                  │  │
│  │  Pages · Components · DynamicPanel · DynamicDashboard  │  │
│  │  NavMenu · DashboardToolbar · Toast Notifications      │  │
│  └─────────────────────┬──────────────────────────────────┘  │
│                        │                                      │
│  ┌─────────────────────▼──────────────────────────────────┐  │
│  │               Data Services (.NET 8)                    │  │
│  │  QueryExecutor · CachingQueryExecutor                   │  │
│  │  DashboardConfigService · AutoRefreshService            │  │
│  │  DiagnosticScriptRunner · SqlAssessmentService          │  │
│  │  AlertingService · NotificationChannelService            │  │
│  │  AzureBlobExportService · ServerModeService             │  │
│  │  ScheduledTaskEngine · AppCircuitHandler                │  │
│  │  CredentialProtector (AES-256-GCM + DPAPI)              │  │
│  └───────┬──────────────┬──────────────┬─────────────────┘   │
│          │              │              │                      │
│  ┌───────▼───────┐ ┌────▼──────────┐ ┌▼───────────────────┐  │
│  │  SQL Server   │ │ SQLite Cache  │ │  Azure Blob Storage │  │
│  │  master /     │ │ WAL-mode .db  │ │  SDK + AzCopy       │  │
│  │  SQLWATCH /   │ │ Delta-fetch   │ │  SAS / Delegation   │  │
│  │  PerfMonitor  │ │ Offline mode  │ │  Auto-upload CSVs   │  │
│  └───────────────┘ └───────────────┘ └─────────────────────┘  │
└──────────────────────────────────────────────────────────────┘
```

**Tech stack:** .NET 8 · WPF · Blazor WebView · ApexCharts · Radzen · Microsoft.Data.SqlClient · SQLite · Serilog · Azure.Identity · Azure.Storage.Blobs · DacFx · ConfuserEx2

---

## Documentation

- [Deployment Guide](DEPLOYMENT_GUIDE.md) — installation, configuration, enterprise deployment
- [Changelog](CHANGELOG.md) — version history and release notes
- [Contributing](CONTRIBUTING.md) — how to contribute
- [Testing Guide](Tests/TESTING_GUIDE.md) — running and writing tests

---

## Built With

This project stands on the shoulders of giants:

| Project | Author | Role |
|---|---|---|
| [SQLWATCH](https://github.com/marcingminski/sqlwatch) | Marcin Gminski | SQL Server monitoring framework — the data foundation |
| [sp_Blitz](https://github.com/BrentOzarULTD/SQL-Server-First-Responder-Kit) | Brent Ozar Unlimited | SQL Server First Responder Kit — health check scripts |
| [sqldba.org](https://sqldba.org) | Adrian Sullivan | sp_triage — SQL health check scripts |
| [PerformanceMonitor](https://github.com/erikdarlingdata/DarlingData) | Erik Darling | Performance Monitor framework and diagnostic queries |
| [MadeiraToolbox](https://github.com/MadeiraData/MadeiraToolbox) | Eitan Blumin | SQL Server maintenance, diagnostics, and best-practice scripts |
| [TigerToolbox](https://github.com/microsoft/tigertoolbox) | Pedro Lopes (Microsoft) | Collection of SQL Server tools and utilities |
| [html-query-plan](https://github.com/JustinPealing/html-query-plan) | Justin Pealing | Interactive graphical SQL execution plan viewer |
| [Blazor-ApexCharts](https://github.com/apexcharts/Blazor-ApexCharts) | ApexCharts Team | Rich interactive charts and time-series visualisations |
| [Serilog](https://github.com/serilog/serilog) | Serilog Contributors | Structured diagnostic logging for .NET |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | Microsoft / .NET Foundation | Local SQLite caching layer |

## Development

This project was developed with assistance from AI coding assistants:

- **Claude (Anthropic)** — Primary pair-programming partner: architecture design, code generation, feature development, optimisation strategies, and ongoing maintenance
- **Amazon Q Developer** — Code review, refactoring, performance analysis
- **Gemini (Google)** — Architecture design, feature development
- **Kilo (Codeium)** — Real-time code completion and suggestions
- **Cline** — Local LLM access, code completion, code review
- **LM Studio** — Local LLM host (Deepseek R1, Gemma 3, Qwen 3.5, GLM 4.7 Flash)

---

## Contributing

Contributions are welcome! Whether it's a bug report, a new dashboard panel, a SQL diagnostic script, or a UI improvement — all help is appreciated.

See [CONTRIBUTING.md](CONTRIBUTING.md) to get started.

## Support

Need help? Check out [SUPPORT.md](SUPPORT.md) for documentation, common issues, and community resources.

## Security

Found a security vulnerability? Please review our [Security Policy](SECURITY.md) for responsible disclosure procedures.

---

## Frequently Asked Questions

**Q: Does this require installing anything on the SQL Server?**
A: No agent is needed on the SQL Server. The app connects remotely via standard SQL connections. Optionally, you can deploy the SQLWATCH database for historical metrics collection.

**Q: Can I monitor Azure SQL Database or Azure SQL Managed Instance?**
A: Azure SQL Managed Instance is supported. Azure SQL Database (PaaS) has limited support — some DMV-based checks require server-level permissions not available in PaaS.

**Q: Is this a replacement for SSMS (SQL Server Management Studio)?**
A: No — it complements SSMS. SQL Health Assessment focuses on monitoring, health checks, and performance analysis. Use SSMS for query authoring, schema management, and administration tasks.

**Q: Can I run this as a Windows Service for 24/7 monitoring?**
A: Yes — the app supports headless Windows Service mode with Kestrel HTTPS for remote dashboard access.

**Q: Are credentials stored securely?**
A: Yes — all passwords are encrypted using AES-256-GCM with a machine-scoped DPAPI key. Credentials are tied to the machine and cannot be decrypted on another computer.

**Q: How does this compare to commercial tools like SolarWinds DPA or Redgate SQL Monitor?**
A: SQL Health Assessment is free and open-source with no licensing costs. It covers core monitoring, health checks, execution plans, and wait stats. Commercial tools may offer deeper historical trending, mobile apps, or cloud-hosted dashboards, but this tool handles the majority of day-to-day DBA monitoring needs.

---

## License

SQL Health Assessment is released under the [GNU General Public License v3.0](LICENSE.txt).

You are free to use, modify, and distribute this software under the terms of the GPL v3.

---

<sub>**Keywords:** SQL Server monitoring tool, free SQL Server health check, open source DBA tool, SQL Server performance monitor, execution plan viewer, wait statistics analyzer, blocking chain analysis, SQL vulnerability assessment, sp_Blitz GUI, SQLWATCH dashboard, SQL Server audit tool, database health assessment, SQL Server real-time monitoring, .NET 8 Blazor WPF, multi-server SQL monitoring, SQL Server security assessment, alternative to SolarWinds DPA, alternative to Redgate SQL Monitor, alternative to SentryOne, alternative to Idera SQL Diagnostic Manager</sub>
