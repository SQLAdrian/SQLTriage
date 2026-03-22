# SQL Health Assessment

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE.txt)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-0078d4.svg)](https://github.com/SQLAdrian/SqlHealthAssessment/releases)
[![SQL Server 2016+](https://img.shields.io/badge/SQL%20Server-2016%2B-red.svg)](https://www.microsoft.com/en-us/sql-server)

> Free, open-source SQL Server monitoring and health assessment for Windows DBAs.
> Built on the battle-tested [SQLWATCH](https://github.com/marcingminski/sqlwatch) framework.

SQL Health Assessment is a desktop application for monitoring multiple SQL Server instances in real time. It gives you live dashboards, health checks, blocking analysis, query execution plans, wait-event statistics, and comprehensive audit reports — all from a single, self-contained Windows executable.

---

## Features

### Real-Time Monitoring
- **Live Dashboards** — configurable panels for sessions, wait events, query stats, and memory pressure
- **Multi-Server Support** — switch between servers or view aggregated data across all instances
- **Auto-Refresh** — configurable polling interval with idle-mode throttling
- **Session Viewer** — active sessions with blocking chain analysis and kill capability

### Health & Analysis
- **Quick Check** (`Ctrl+Q`) — instant assessment: CPU, memory, blocking, missing/unused indexes
- **Full Audit** (`Ctrl+4`) — deep-dive covering configuration, security, backups, fragmentation
- **Vulnerability Assessment** — SQL Server security assessment with exportable results
- **Interactive Execution Plan Viewer (V2)** — graphical plan with hover detail pane (object path, cost breakdown, predicate, copy button); root operator always shows 100%; pane fades after 1.5 s with hover-cancel
- **Long Query Detection** — surface queries exceeding configurable duration thresholds
- **Wait Statistics** — categorised wait event history and trends, including locking-waits %

### Performance & Reliability
- **SQLite WAL-mode cache** — panels serve cached data when SQL Server is temporarily unreachable
- **Delta-fetch for time-series** — only new data points are fetched on each refresh
- **Two-tier query throttling** — protects the monitored server from excessive load
- **Zero-allocation row reading** — `ArrayPool<T>` and streaming JSON serialisation
- **Cancellable dashboard loads** — switch servers or hit Cancel to abort in-flight queries instantly
- **Memory pressure monitoring** — background service alerts and evicts cache under high memory load
- **Server GC + concurrent GC** — tuned for optimal throughput in `runtimeconfig`

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

### Developer Experience
- **JSON-based dashboard editor** — add, reorder, drag-and-drop panels without code changes
- **10 UI themes** — dark and light options, switchable at runtime
- **Keyboard shortcuts** — full keyboard navigation (`Ctrl+1–9`, `?` to show help)
- **Serilog structured logging** — 30-day rolling logs with configurable verbosity
- **Configurable alerting** — rules, notification channels, and severity thresholds

---

## Screenshots

> *Screenshots coming soon. Contributions welcome — see [CONTRIBUTING.md](CONTRIBUTING.md).*

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
| Live TempDB | TempDB usage, version store, long transactions | — |
| Vulnerability Assessment | SQL Server security assessment results | — |
| Checks | Automated health check results | — |
| Quick Check | Instant health snapshot | `Ctrl+Q` |

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
│  │  AlertingService · HealthCheckService                   │  │
│  │  AzureBlobExportService · ServerModeService             │  │
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

## License

SQL Health Assessment is released under the [GNU General Public License v3.0](LICENSE.txt).

You are free to use, modify, and distribute this software under the terms of the GPL v3.
