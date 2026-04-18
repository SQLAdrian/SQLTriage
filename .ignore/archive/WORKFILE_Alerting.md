<!-- In the name of God, the Merciful, the Compassionate -->
<!-- Bismillah ar-Rahman ar-Raheem -->

# Alerting Feature - Working File

**Created:** 2026-03-23
**Status:** Phase 1 - Alert Catalog Definition
**Research source:** C:\temp\SQL_Server_Monitoring_Tools_Alerting_Research.md

---

## Research Summary

Analyzed the top 5 enterprise SQL Server monitoring tools:
- **IDERA SQL Diagnostic Manager** — 131 alert IDs, most granular
- **Quest Spotlight** — 140+ alarms, best TempDB coverage
- **Redgate SQL Monitor** — ~45 alerts, best-documented defaults
- **SolarWinds DPA** — Wait-time anomaly focus, ~50+ alerts
- **Datadog** — Metric-driven, build-your-own, 600+ notification integrations

---

## Phase Plan

| Phase | Description | Status |
|-------|-------------|--------|
| 1 | Define consolidated alert catalog (this file) | IN PROGRESS |
| 2 | Create `alert-definitions.json` with all alerts | TODO |
| 3 | Build alert engine (evaluation, frequency, channels) | TODO |
| 4 | UI: Alert configuration page | TODO |
| 5 | Notification channel integration | TODO |

---

## Consolidated Alert Catalog

Deduplicated across all 5 tools. Each alert has:
- **ID** — unique key for JSON
- **Category** — grouping
- **Severity** — default level (Info / Low / Medium / High / Critical)
- **Default Threshold** — industry-standard starting value
- **Default Frequency** — how often to evaluate
- **Sources** — which tools include this alert (R=Redgate, I=IDERA, Q=Quest, S=SolarWinds, D=Datadog)

---

### Category 1: Performance (CPU & Queries)

| # | Alert ID | Name | Default Threshold | Severity | Frequency | Sources |
|---|----------|------|-------------------|----------|-----------|---------|
| 1 | `cpu_sql_usage` | SQL Server CPU Usage | > 90% for 5 min | High | 60s | R,I,Q,S,D |
| 2 | `cpu_os_usage` | OS/Host CPU Usage | > 95% for 5 min | High | 60s | R,I,Q,S,D |
| 3 | `long_running_query` | Long-Running Query | > 300s (5 min) | Medium | 60s | R,Q,S,D |
| 4 | `query_recompilations` | Excessive Recompilations | > 10/sec | Low | 300s | Q,D |
| 5 | `batch_requests` | Batch Requests Anomaly | Baseline deviation | Info | 300s | S,D |
| 6 | `io_stall_time` | I/O Stall Time | > 100ms avg | Medium | 60s | I,Q |
| 7 | `page_splits` | Excessive Page Splits | > 100/sec | Low | 300s | D |
| 8 | `wait_time_anomaly` | Wait Time Anomaly | Baseline deviation | Medium | 300s | S |
| 9 | `sql_response_time` | SQL Server Response Time | > 2000ms | Medium | 60s | I |

### Category 2: Memory

| # | Alert ID | Name | Default Threshold | Severity | Frequency | Sources |
|---|----------|------|-------------------|----------|-----------|---------|
| 10 | `page_life_expectancy` | Page Life Expectancy | < 300s | High | 60s | R,I,Q,S,D |
| 11 | `buffer_cache_hit` | Buffer Cache Hit Ratio | < 90% | Medium | 300s | I,S,D |
| 12 | `os_memory_usage` | OS Memory Usage | > 95% | High | 60s | R,I,Q,D |
| 13 | `sql_memory_usage` | SQL Server Memory Usage | > 95% of max | Medium | 300s | I |
| 14 | `os_paging` | OS Paging (Swapping) | > 10/sec | Medium | 60s | I |

### Category 3: Storage & Disk

| # | Alert ID | Name | Default Threshold | Severity | Frequency | Sources |
|---|----------|------|-------------------|----------|-----------|---------|
| 15 | `database_space_full` | Database Space Full | > 90% used | High | 300s | R,I,Q,S,D |
| 16 | `log_space_full` | Transaction Log Full | > 90% used | High | 300s | I,Q,S,D |
| 17 | `disk_space_low` | Disk Space Low | < 2 GB free | High | 300s | R,I,Q,D |
| 18 | `data_file_autogrow` | Data File Auto-Growth | Any event | Low | 60s | I,Q |
| 19 | `log_file_autogrow` | Log File Auto-Growth | Any event | Medium | 60s | I,Q |
| 20 | `vlf_count` | Virtual Log File Count | > 1000 | Medium | 3600s | R,Q,S |
| 21 | `filegroup_space` | Filegroup Space Full | > 90% used | High | 300s | I |
| 22 | `disk_latency_read` | Disk Read Latency | > 50ms avg | Medium | 60s | I |
| 23 | `disk_latency_write` | Disk Write Latency | > 50ms avg | Medium | 60s | I |

### Category 4: TempDB

| # | Alert ID | Name | Default Threshold | Severity | Frequency | Sources |
|---|----------|------|-------------------|----------|-----------|---------|
| 24 | `tempdb_space_used` | TempDB Space Used | > 80% | Medium | 300s | I,Q,D |
| 25 | `tempdb_contention` | TempDB Contention | > 100ms latch wait | Medium | 60s | I,Q |
| 26 | `tempdb_autogrow` | TempDB Auto-Growth | Any event | Low | 60s | Q |
| 27 | `tempdb_files_unequal` | TempDB Files Different Sizes | Any mismatch | Low | 3600s | Q |
| 28 | `tempdb_version_store` | Version Store Size | > 1 GB | Medium | 300s | I,Q,R |

### Category 5: Blocking & Locking

| # | Alert ID | Name | Default Threshold | Severity | Frequency | Sources |
|---|----------|------|-------------------|----------|-----------|---------|
| 29 | `deadlock` | Deadlock Detected | Any occurrence | High | 30s | R,I,Q,S,D |
| 30 | `deadlock_rate` | Deadlock Rate | > 5/min | High | 60s | R,D |
| 31 | `blocking_process` | Blocking Process | > 60s duration | Medium | 30s | R,I,Q,S,D |
| 32 | `blocked_sessions_count` | Blocked Session Count | > 5 sessions | Medium | 60s | I,D |

### Category 6: Availability & Services

| # | Alert ID | Name | Default Threshold | Severity | Frequency | Sources |
|---|----------|------|-------------------|----------|-----------|---------|
| 33 | `instance_unreachable` | Instance Unreachable | 30s timeout | Critical | 30s | R,I,S,D |
| 34 | `database_unavailable` | Database Unavailable | 30s timeout | Critical | 30s | R,I,Q |
| 35 | `agent_stopped` | SQL Agent Stopped | Service not running | High | 60s | R,I,Q |
| 36 | `agent_job_failure` | SQL Agent Job Failed | Any failure | Medium | 60s | R,I,Q,S,D |
| 37 | `agent_job_long_running` | SQL Agent Job Long Running | > 200% of avg duration | Low | 300s | R,I,S |
| 38 | `agent_job_completion` | SQL Agent Job Completion | Notify on completion | Info | 60s | I |
| 39 | `fulltext_stopped` | Full-Text Search Stopped | Service not running | Medium | 300s | R,I,Q |
| 40 | `dtc_stopped` | DTC Service Stopped | Service not running | Medium | 300s | I,Q |
| 41 | `browser_stopped` | SQL Browser Stopped | Service not running | Low | 300s | R,I |
| 42 | `ssis_stopped` | SSIS Service Stopped | Service not running | Medium | 300s | R,Q |

### Category 7: Backup & Recovery

| # | Alert ID | Name | Default Threshold | Severity | Frequency | Sources |
|---|----------|------|-------------------|----------|-----------|---------|
| 43 | `backup_full_overdue` | Full Backup Overdue | > 7 days | High | 3600s | R,I,Q,S |
| 44 | `backup_diff_overdue` | Differential Backup Overdue | > 24 hours | Medium | 3600s | R,Q |
| 45 | `backup_log_overdue` | Log Backup Overdue | > 60 min | Medium | 3600s | R,Q |
| 46 | `integrity_check_overdue` | Integrity Check Overdue | > 14 days | Medium | 3600s | R |

### Category 8: High Availability (AG & Mirroring)

| # | Alert ID | Name | Default Threshold | Severity | Frequency | Sources |
|---|----------|------|-------------------|----------|-----------|---------|
| 47 | `ag_failover` | AG Failover Detected | Any event | High | 30s | R,I,S |
| 48 | `ag_replica_unhealthy` | AG Replica Not Healthy | 30s duration | Critical | 30s | R,I |
| 49 | `ag_sync_behind` | AG Replication Falling Behind | > 100 MB for 120s | Medium | 60s | R,I |
| 50 | `ag_estimated_data_loss` | AG Estimated Data Loss | > 60s | High | 60s | I |
| 51 | `ag_redo_queue` | AG Redo Queue Size | > 100 MB | Medium | 60s | I |
| 52 | `ag_listener_offline` | AG Listener Offline | 30s duration | Critical | 30s | R |
| 53 | `cluster_failover` | Cluster Failover | Any event | High | 30s | R,I,Q |
| 54 | `mirror_status_change` | Mirroring Status Change | Any change | Medium | 60s | I,Q,S |

### Category 9: Error Logging & Security

| # | Alert ID | Name | Default Threshold | Severity | Frequency | Sources |
|---|----------|------|-------------------|----------|-----------|---------|
| 55 | `error_log_severity` | Error Log Entry (Sev >= 17) | Severity >= 17 | High | 60s | R,I,Q,S |
| 56 | `error_log_fatal` | Error Log Fatal Entry | Severity >= 20 | Critical | 30s | R,Q |
| 57 | `logon_failure` | Login Failure Detected | Any failure | Low | 300s | R,Q |
| 58 | `config_change` | Configuration Change | Any change | Medium | 300s | R,S |
| 59 | `io_error` | SQL I/O Error | Any error | Critical | 30s | I,Q |

### Category 10: Index & Maintenance

| # | Alert ID | Name | Default Threshold | Severity | Frequency | Sources |
|---|----------|------|-------------------|----------|-----------|---------|
| 60 | `index_fragmentation` | Index Fragmentation | > 30% (>1000 pages) | Low | 86400s | R,I,Q |
| 61 | `connection_count` | User Connections High | > 80% of max | Medium | 60s | R,I,Q,D |

---

## Notification Channels to Support

Based on cross-tool analysis, ranked by adoption:

| Priority | Channel | Implementation | Notes |
|----------|---------|---------------|-------|
| P0 | Email (SMTP) | Built-in | Every tool supports this |
| P0 | Webhook (generic) | HTTP POST with JSON | Covers Teams, Slack, PagerDuty, ServiceNow, etc. |
| P1 | Slack | Webhook URL | Native feel via Incoming Webhook |
| P1 | Microsoft Teams | Webhook URL | Via Incoming Webhook connector |
| P2 | Windows Event Log | EventLog.WriteEntry | On-prem integration |
| P2 | SQL Script / SP | Execute T-SQL | Self-healing / auto-remediation |
| P3 | PagerDuty | REST API | Incident management |
| P3 | SNMP Trap | UDP trap | Legacy monitoring integration |

---

## Alert Configuration Model

Each alert instance will support:

```
{
  "alertId": "cpu_sql_usage",
  "enabled": true,
  "severity": "High",
  "thresholds": {
    "warning": 80,
    "critical": 90
  },
  "durationSeconds": 300,
  "frequencySeconds": 60,
  "channels": ["email", "slack"],
  "servers": ["*"],           // or specific server names
  "databases": ["*"],         // for DB-scoped alerts
  "suppressionMinutes": 30,   // don't re-alert within this window
  "schedule": {
    "enabled": true,
    "days": ["Mon","Tue","Wed","Thu","Fri"],
    "startTime": "08:00",
    "endTime": "18:00"
  }
}
```

---

## Alert Evaluation Queries (Draft)

These are the T-SQL queries the alert engine will run. To be refined in Phase 2.

### CPU
```sql
-- cpu_sql_usage / cpu_os_usage
SELECT
    record.value('(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]', 'int') AS sql_cpu,
    100 - record.value('(./Record/SchedulerMonitorEvent/SystemHealth/SystemIdle)[1]', 'int') AS os_cpu
FROM (
    SELECT TOP 1 CONVERT(XML, record) AS record
    FROM sys.dm_os_ring_buffers
    WHERE ring_buffer_type = N'RING_BUFFER_SCHEDULER_MONITOR'
    ORDER BY timestamp DESC
) AS x;
```

### Memory
```sql
-- page_life_expectancy
SELECT cntr_value FROM sys.dm_os_performance_counters
WHERE object_name LIKE '%Buffer Manager%' AND counter_name = 'Page life expectancy';

-- buffer_cache_hit
SELECT cntr_value FROM sys.dm_os_performance_counters
WHERE object_name LIKE '%Buffer Manager%' AND counter_name = 'Buffer cache hit ratio';
```

### Blocking
```sql
-- blocking_process / blocked_sessions_count
SELECT
    COUNT(*) AS blocked_count,
    MAX(wait_time / 1000) AS max_wait_seconds
FROM sys.dm_exec_requests
WHERE blocking_session_id > 0;
```

### Deadlocks
```sql
-- deadlock (from system_health XE)
SELECT COUNT(*) AS deadlock_count
FROM sys.dm_xe_session_targets st
JOIN sys.dm_xe_sessions s ON s.address = st.event_session_address
WHERE s.name = 'system_health' AND st.target_name = 'ring_buffer';
-- (parse XML for deadlock events within last N seconds)
```

### Storage
```sql
-- database_space_full / log_space_full
SELECT
    DB_NAME(database_id) AS database_name,
    type_desc,
    CAST(FILEPROPERTY(name, 'SpaceUsed') AS FLOAT) / NULLIF(size, 0) * 100 AS pct_used,
    size * 8 / 1024 AS size_mb
FROM sys.master_files;
```

### Backups
```sql
-- backup_full_overdue
SELECT
    d.name AS database_name,
    MAX(b.backup_finish_date) AS last_backup,
    DATEDIFF(HOUR, MAX(b.backup_finish_date), GETDATE()) AS hours_since
FROM sys.databases d
LEFT JOIN msdb.dbo.backupset b ON d.name = b.database_name AND b.type = 'D'
WHERE d.database_id > 4 AND d.state = 0
GROUP BY d.name
HAVING MAX(b.backup_finish_date) IS NULL
    OR DATEDIFF(HOUR, MAX(b.backup_finish_date), GETDATE()) > @ThresholdHours;
```

### Services
```sql
-- agent_stopped (requires xp_servicecontrol or DMV)
EXEC master.dbo.xp_servicecontrol 'QueryState', N'SQLServerAGENT';
```

### Agent Jobs
```sql
-- agent_job_failure
SELECT j.name, h.run_date, h.run_time, h.message
FROM msdb.dbo.sysjobs j
JOIN msdb.dbo.sysjobhistory h ON j.job_id = h.job_id
WHERE h.step_id = 0 AND h.run_status = 0
  AND CONVERT(DATETIME, STUFF(STUFF(CAST(h.run_date AS VARCHAR), 5, 0, '-'), 8, 0, '-')) > DATEADD(MINUTE, -@FrequencyMinutes, GETDATE());
```

---

## Decisions — RESOLVED

| # | Decision | Answer |
|---|----------|--------|
| 1 | Alert storage | SQLite, persistent, purge records > 1 year |
| 2 | Evaluation engine | New timer service (minutes/hours), 2-5 min default, disk checks less frequent |
| 3 | Multi-server | Yes, evaluate per-server independently |
| 4 | Acknowledgment | Toast-based (different colour), auto-ack after configurable period (default 24h), group by alertId + server |
| 5 | Auto-remediation | Yes, T-SQL scripts, opt-in per alert, **dry-run mode** before live |
| 6 | Alert history | 1 year retention, auto-ack/aging per alert (global default + per-alert override) |

## Design Rules

- **Global cooldown**: 5 minutes (don't re-fire same alert+server within window), per-alert override available
- **State machine**: `Active → Acknowledged → Resolved → (purged after 1 year)`
  - Condition clears → auto-moves to Resolved
  - Auto-ack after configurable period (default 24h) so stale alerts don't pile up
- **Grouping**: By `alertId + server` — repeated occurrences within cooldown increment `hitCount`, not new rows
- **Simplicity first**: Minimal config surface, sensible defaults, no over-customization
- **Anti-spam**: Cooldown + grouping + auto-ack prevents alert fatigue

---

## Next Steps

- [x] Review this catalog — add/remove/adjust alerts
- [x] Decide on the questions above
- [x] Generate `Config/alert-definitions.json`
- [ ] Build alert evaluation service
- [ ] Build alert notification service
- [ ] Build alert configuration UI page

