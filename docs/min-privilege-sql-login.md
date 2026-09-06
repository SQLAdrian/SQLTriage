---
layout: default
title: SQLTriage — Minimum-Privilege SQL Login
description: How to grant SQLTriage the least permissions it needs, by feature. Avoids the sysadmin-by-default shortcut that creates an audit finding on day one.
---
<!-- In the name of God, the Merciful, the Compassionate -->
<!-- Bismillah ar-Rahman ar-Raheem -->

# Minimum-Privilege SQL Login for SQLTriage

## Rationale

SQLTriage is positioned as an audit-first monitoring tool for regulated environments (SOC2, FedRAMP, PCI-DSS). Granting `sysadmin` to a monitoring account is a common first-install shortcut that creates an audit finding on day one. A least-privilege account limits blast radius if credentials are compromised, satisfies most compliance frameworks' "minimum necessary access" requirement, and demonstrates to auditors that the tool itself respects the principle it enforces.

## Permissions by Feature

| Feature | Required Permission | Scope |
|---|---|---|
| Core DMV polling (sessions, waits, blocking) | `VIEW SERVER STATE` | Server |
| Object and index metadata | `VIEW ANY DEFINITION` | Server |
| Connection authentication | `CONNECT SQL` | Server |
| Backup chain inspection | `db_backupoperator` role membership on `msdb`, OR `sysadmin` for cross-DB coverage | msdb / Server |
| DBCC CHECKDB recency (`dm_db_index_physical_stats`) | `VIEW DATABASE STATE` on each monitored user DB | Per database |
| Wait stats (`dm_os_wait_stats`) | `VIEW SERVER STATE` | Server |
| Linked-server inspection (`sys.servers`, `sys.linked_logins`) | `VIEW SERVER STATE` | Server |
| HADR / Availability Group state | `VIEW SERVER STATE` | Server |
| Audit spec inspection (`sys.server_audits`, `sys.server_audit_specifications`) | `CONTROL SERVER` or `ALTER ANY SERVER AUDIT` | Server |
| Reading user data in monitored databases | **None — SQLTriage never reads user data** | N/A |

## T-SQL: Create the Monitoring Account

```sql
-- Least-privilege login for SQLTriage monitoring
-- Replace {{strong-password}} with a strong password before running.
CREATE LOGIN [sqltriage_monitor] WITH PASSWORD = '{{strong-password}}';

-- Server-level grants (minimum for DMV polling, metadata, and connection)
GRANT VIEW SERVER STATE    TO [sqltriage_monitor];
GRANT VIEW ANY DEFINITION  TO [sqltriage_monitor];
GRANT CONNECT SQL          TO [sqltriage_monitor];

-- Optional: backup chain inspection without sysadmin
-- (grants visibility into msdb backup history only)
USE msdb;
ALTER ROLE [db_backupoperator] ADD MEMBER [sqltriage_monitor];
GO

-- Optional: DBCC CHECKDB recency per database
-- Run for each database you want SQLTriage to inspect
USE [YourDatabase];
GRANT VIEW DATABASE STATE TO [sqltriage_monitor];
GO

-- Optional grants for full feature coverage
-- (sysadmin only required for backup chain across ALL databases
--  when per-DB db_backupoperator membership is not practical)
-- ALTER SERVER ROLE [sysadmin] ADD MEMBER [sqltriage_monitor];
```

## Notes

- **Audit chain inspection** (`sys.server_audits`) requires `CONTROL SERVER` or `ALTER ANY SERVER AUDIT`. If your compliance posture does not permit this, audit-spec features will show "Insufficient permissions" and skip gracefully.
- **SQLTriage never writes to monitored databases.** It is read-only by design. No `INSERT`, `UPDATE`, `DELETE`, or DDL permissions are needed on any monitored database.
- **Linked-server inspection** uses `sys.servers` and `sys.linked_logins`, both covered by `VIEW SERVER STATE`.
- **HADR / AG state** (`sys.availability_groups`, `sys.dm_hadr_availability_replica_states`) requires `VIEW SERVER STATE`.
- **Dedicated login per environment.** Use a separate `sqltriage_monitor` login for each monitored environment (dev, staging, production). This limits the impact of a credential leak to a single environment.
- **Rotate quarterly.** Rotate the monitoring account password every 90 days to align with standard credential hygiene and most SOC2 control requirements. SQLTriage stores credentials encrypted (AES-256-GCM + DPAPI); updating them via **Settings → Server Credentials** triggers no downtime.
- **Windows Authentication alternative.** A domain service account with the same server-level grants works identically and eliminates password rotation overhead. Grant permissions to the domain account instead of a SQL login.
