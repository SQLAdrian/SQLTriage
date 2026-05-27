# Architecture

## Mission

SQLTriage is an agentless, audit-first SQL Server compliance platform. Connect it to a SQL Server — no agent install on the target — and in seconds it produces a governance score, framework gap analysis (NIST SP 800-53, CIS Controls v8, SOC 2, ISO 27001, STIG), and a PDF report backed by 500+ checks. Built to last 15 years in regulated environments.

## Components

| Layer | Location | Description |
|---|---|---|
| WPF shell | `MainWindow.xaml.cs` | BlazorWebView host; zoom, DevTools, server-mode fallback if WebView2 absent |
| Pages | `Pages/` (37+) | Blazor `@page` routes — audit log, governance, capacity, deadlock viewer, query store, etc. |
| Services | `Data/Services/` (34+) | All business logic; injected via DI; each survives its neighbours failing |
| Models | `Data/Models/` | POCOs shared across services |
| SQLite caches | `Data/Caching/` (4 stores) | WAL-mode SQLite; 2-week rolling retention; delta-fetch |
| JSONL audit log | `audit-logs/` | Append-only; HMAC-SHA256 chain; tamper-evident |
| Config | `Config/` | `appsettings.json`, `version.json`, `dashboard-config.json` |

## Key flow

User opens page → page injects services via DI → services query monitored SQL Server via `SqlConnectionPoolService` → results cached in SQLite → UI renders. Background services (alerting, forecasting, audit flush) run on `Task.Run` loops independently.

## Critical invariants

- **Audit-first**: every privileged action is logged to the HMAC chain before the action completes
- **HMAC chain**: each audit entry hashes the previous entry's digest; any tampering breaks the chain and surfaces a banner in the UI
- **DPAPI-wrapped secrets**: credentials encrypted with AES-256-GCM + DPAPI via `CredentialProtector`
- **Colour-blind safe palette**: status colours use CSS vars (`--green`, `--red`, etc.) remapped by `body.colorblind-mode` (Wong 2011 palette)

## Cross-cutting services

| Service | Role |
|---|---|
| `AuditLogService` | HMAC chain write + verify; startup chain check |
| `ConfigBaselineService` | Snapshot config on first run; surface drift on subsequent runs |
| `ServerCircuitBreakerService` | Per-server back-off; isolates a failing target from the rest |

## Where to start

- **Human contributors**: this file, then `CONTRIBUTING.md`, then `docs/compliance/DEPLOY-CHECKLIST.md` for regulated deployments
