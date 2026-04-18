# LiveMonitor — Free SQL Server Monitoring for Windows DBAs

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE.txt)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-0078d4.svg)](https://github.com/SQLAdrian/SqlHealthAssessment/releases)
[![SQL Server 2016+](https://img.shields.io/badge/SQL%20Server-2016%2B-red.svg)](https://www.microsoft.com/en-us/sql-server)
[![Version](https://img.shields.io/badge/version-0.85.2-blue.svg)](https://github.com/SQLAdrian/SqlHealthAssessment/releases/latest)
[![Contributors: Human+AI](https://img.shields.io/badge/contributors-human_%2B_ai-FF6B6B?style=flat-square)](CONTRIBUTORS.md)

> **Free, open-source SQL Server monitoring — no agents, no per-server licensing, single exe.**
> A lightweight alternative to SolarWinds DPA, Redgate SQL Monitor, SentryOne, and Idera SQL Diagnostic Manager.

**LiveMonitor** (formerly *SQL Health Assessment*) is a Windows desktop application (Blazor UI hosted in WPF) that monitors multiple SQL Server instances in real time. It ships as a single executable and can also run as a headless Windows Service for 24/7 monitoring with remote browser access.

No software is installed on your SQL Servers. Everything runs from your workstation or a dedicated monitoring host.

> **Version 0.85.2** — production-quality, actively developed. Suitable for production DBA use. See [CHANGELOG.md](CHANGELOG.md) for what changed.

---

## Quick Start

> **Requires:** Windows 10 1809+ · SQL Server 2016+  
> .NET runtime and WebView2 are bundled — no prerequisites to install.

```
1. Download LiveMonitor.exe from the Releases page
2. Run it — no installation needed
3. Go to Servers → Add Server, enter your SQL Server name, click Test → Save
4. Open Live Monitor (Ctrl+2) to see live sessions, wait stats, and top queries
5. Run a Quick Check (Ctrl+Q) for an instant health snapshot
```

First time? The built-in onboarding wizard walks you through the setup in 3 steps.  
Need SQLWATCH for historical dashboards? Go to **Database Deploy** — it handles the deployment for you.

**[→ Full Quick Start Guide (5 min read)](QUICKSTART.md)**

---

## Demo

![SQL Health Assessment demo](docs/demo.gif)

## Screenshots

| | |
|---|---|
| ![Add Server](docs/screenshots/1-addserver.jpg) | ![Live Sessions](docs/screenshots/4-live-sessions.jpg) |
| ![Instance Overview](docs/screenshots/6-instance-overview.jpg) | ![Alerting](docs/screenshots/14-alerting.jpg) |
| ![Query Plan Viewer](docs/screenshots/15-query-plan-viewer.jpg) | ![Maturity Roadmap](docs/screenshots/13-maturity-roadmap.jpg) |
| ![Environment Map](docs/screenshots/7-environment-map.jpg) | ![Vulnerability Assessment](docs/screenshots/10-microsoft-sql-vulnerability-assessment.jpg) |

[See all 16 screenshots →](https://sqladrian.github.io/SqlHealthAssessment/)

---

## Why LiveMonitor?

| | LiveMonitor | sp_Blitz | SQLWATCH | SolarWinds DPA |
|---|---|---|---|---|
| Cost | Free | Free | Free | $$$/ server |
| UI | ✅ Desktop + browser | ❌ SSMS only | ✅ Grafana | ✅ Web |
| Execution plan viewer | ✅ Interactive | ❌ | ❌ | ✅ |
| Alerting + notifications | ✅ 7 channels | ❌ | Limited | ✅ |
| Runs as Windows Service | ✅ | ❌ | Partial | ✅ |
| Agent on SQL Server | ❌ Not required | ❌ | ✅ Required | ✅ Required |
| Historical dashboards | ✅ (with SQLWATCH) | ❌ | ✅ | ✅ |
| Azure SQL MI support | ✅ | Limited | Limited | ✅ |

---

## Who is this for?

- **DBAs** who need a free monitoring tool without complex infrastructure
- **Consultants** who want a portable tool to assess SQL Server health at client sites
- **Teams on SQL Server 2016–2022** (on-premises, Azure VM, or Azure SQL Managed Instance)
- **Organizations** looking for an open-source alternative to expensive commercial monitors

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
- **Maintenance windows** — suppress non-critical alerts on a schedule or with a manual toggle
- **Toast flood control** — when 5+ alerts fire within 5 seconds, a mute banner appears with a "Mute 5 min" option
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
- **Background update check** — checks GitHub releases on startup; shows a banner when a newer version is available
- **Auto-update** — Silent, delta-based updates from GitHub releases; 3-tier extraction fallback (`Expand-Archive` → .NET `ZipFile` → `Shell.Application` COM) for older Windows Server targets
- **Manual ZIP import** — server mode / air-gap: upload a downloaded release ZIP directly via the Service Management page
- **GitHub Actions CI/CD** — Automated build, test, and release on version tags

### Data Export
- **Azure Blob Storage integration** — auto-upload audit CSVs with dual-path upload (Azure SDK + AzCopy fallback)
- **SAS token & User Delegation SAS** — supports account-level and directory-scoped SAS tokens
- **Connection diagnostics** — built-in modal to troubleshoot Azure Blob authentication issues
- **Toast notifications** — real-time upload success/failure feedback

### Security & Access Control
- **AES-256-GCM credential encryption** — machine-scoped DPAPI key file + authenticated encryption; works for interactive and service accounts
- **Ephemeral session keys** — dashboard results encrypted with a per-process key (never persisted)
- **MFA / Azure AD authentication** — supports modern auth via `Azure.Identity`
- **RBAC (Role-Based Access Control)** — Admin / Operator / Viewer roles in server mode; OAuth via Google or Microsoft; auto-assign or explicit user list; configured in Settings → Access Control
- **Parameterised queries only** — SQL injection prevention throughout
- **Assembly obfuscation** — ConfuserEx2 (anti-tamper, anti-debug, string encryption). The source code is fully open — the obfuscation protects the compiled binary from trivial tampering, not the source. Build from source for an unobfuscated binary.
- **Audit log** — full query-execution and user-action trail (90-day retention)
- **Rate limiting** — configurable max queries per minute with optional UI warnings
- **Windows Service mode** — headless deployment with Kestrel HTTPS support; UAC elevation prompt for service install/uninstall

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

## Requirements

| | Minimum | Recommended |
|---|---|---|
| **OS** | Windows 10 (1809+) | Windows 11 / Server 2022 |
| **RAM** | 4 GB | 8 GB |
| **Disk** | 500 MB | 2 GB (logs + cache) |
| **SQL Server** | SQL Server 2016 | SQL Server 2019+ |

**.NET runtime and WebView2 are bundled in the download** — there is nothing to install separately. The app runs as a self-contained executable on any supported Windows version.

WebView2 enhances the UI experience when available but is not required — the app falls back to Blazor Server mode automatically.

> Download size is ~120 MB (optimised single-file) or ~350 MB (full with all native binaries). See [FOOTPRINT_REDUCTION_QUICK.md](FOOTPRINT_REDUCTION_QUICK.md) for size options.

### SQL Server Permissions

The monitoring account needs `VIEW SERVER STATE` and `VIEW DATABASE STATE` — standard read-only DMV access. No `sysadmin`, no agent installation on the SQL Server.

---

## Installation

Download the latest release from the [Releases page](https://github.com/SQLAdrian/SqlHealthAssessment/releases).
Two options are available:

- **`LiveMonitor-Setup.exe`** — guided Inno Setup installer with optional components and a "Launch now" checkbox
- **`LiveMonitor.zip`** — extract to any folder (e.g. `C:\Tools\LiveMonitor`) and run `LiveMonitor.exe`

Both are self-contained. No .NET runtime or WebView2 installation required.

> **First run:** The onboarding wizard appears automatically. It walks you through adding a server, running a Quick Check, and optionally deploying SQLWATCH.

### SQLWATCH (optional — enhances historical dashboards)

LiveMonitor works immediately without SQLWATCH. Adding SQLWATCH to your SQL Server unlocks additional dashboards: Instance Overview trends, PM Health & Diagnostics, Wait Statistics history, and more.

To deploy from within the app: **Database Deploy** → enter credentials → **Deploy**.
Manual: deploy `Dacpacs\SQLWATCH.dacpac` via SSMS or run the scripts in `SQLWATCH_db\` in order.

For SQLWATCH dashboards the monitoring account additionally needs `db_owner` on the SQLWATCH database (or equivalent read rights).

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
| Environment Map | Force-directed network topology: SQL instances → hosts → applications; scan, merge physical hosts | — |
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
| [ola.hallengren.com](https://ola.hallengren.com) | Ola Hallengren | SQL Server Maintenance Solution — backup, integrity check, index optimisation |
| [Glenn Berry's DMV Queries](https://www.sqlskills.com/blogs/glenn/) | Glenn Berry | Diagnostic queries for SQL Server health and configuration |
| [sqlserver-kit](https://github.com/ktaranov/sqlserver-kit) | Konstantin Taranov | SQL Server scripts, stored procedures, and best practices collection |
| [Wayne Sheffield Blog](https://blog.waynesheffield.com) | Wayne Sheffield | SQL Server scripts and best practices |
| [TracyBoggiano.com](https://tracyboggiano.com) | Tracy Boggiano | SQL Server performance and monitoring scripts |
| [SQLMag / Tim Ford](https://www.sqlmag.com) | Tim Ford | SQL Server scripts and tooling |
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

**Q: Is this production-ready?**
A: Yes — current version is 0.85.2, actively used in production environments. The version will reach 1.0 once the installer, RBAC, and documentation are complete. See [CHANGELOG.md](CHANGELOG.md) for stability notes.

**Q: What's the difference between "LiveMonitor" and "SQL Health Assessment"?**
A: LiveMonitor is the product name. SQL Health Assessment is the internal project/repo name. Both refer to the same tool — the branding is being unified under LiveMonitor.

**Q: Can I monitor Azure SQL Database or Azure SQL Managed Instance?**
A: Azure SQL Managed Instance is supported. Azure SQL Database (PaaS) has limited support — some DMV-based checks require server-level permissions not available in PaaS.

**Q: Is this a replacement for SSMS (SQL Server Management Studio)?**
A: No — it complements SSMS. LiveMonitor focuses on monitoring, health checks, and performance analysis. Use SSMS for query authoring, schema management, and administration tasks.

**Q: Can I run this as a Windows Service for 24/7 monitoring?**
A: Yes — the app supports headless Windows Service mode with Kestrel HTTPS for remote dashboard access. Go to Service Management to install/uninstall (UAC prompt triggers automatically if needed).

**Q: Are credentials stored securely?**
A: Yes — all passwords are encrypted using AES-256-GCM with a machine-scoped DPAPI key. Credentials are tied to the machine and cannot be decrypted on another computer.

**Q: How does this compare to commercial tools like SolarWinds DPA or Redgate SQL Monitor?**
A: LiveMonitor is free and open-source with no licensing costs. It covers core monitoring, health checks, execution plans, and wait stats. Commercial tools may offer deeper historical trending, mobile apps, or cloud-hosted dashboards, but this tool handles the majority of day-to-day DBA monitoring needs.

**Q: Something is wrong — how do I get help?**
A: Use the **Report / Feedback** button in the app sidebar, or open a [GitHub Issue](https://github.com/SQLAdrian/SqlHealthAssessment/issues). For email support: [adrian@sqldba.org](mailto:adrian@sqldba.org).

---

## License

SQL Health Assessment is released under the [GNU General Public License v3.0](LICENSE.txt).

You are free to use, modify, and distribute this software under the terms of the GPL v3.

---

<sub>**Keywords:** SQL Server monitoring tool, free SQL Server health check, open source DBA tool, SQL Server performance monitor, execution plan viewer, wait statistics analyzer, blocking chain analysis, SQL vulnerability assessment, sp_Blitz GUI, SQLWATCH dashboard, SQL Server audit tool, database health assessment, SQL Server real-time monitoring, .NET 8 Blazor WPF, multi-server SQL monitoring, SQL Server security assessment, alternative to SolarWinds DPA, alternative to Redgate SQL Monitor, alternative to SentryOne, alternative to Idera SQL Diagnostic Manager</sub>
