# Incident Response Runbook

**Control mapping:** SOC2 CC7.2, CC7.3, CC7.4, CC7.5 · NIST IR-4, IR-5, IR-6
**Why this exists:** Auditors require documented evidence that the team knows what to do when an alert fires, who is responsible, and that incidents are logged and closed with root cause. Without this, CC7.x is a finding.

---

## Purpose

Define the response path for every alert severity that SQLTriage can raise, from initial triage through root-cause recording and closure. All closure actions use the in-app lifecycle so the audit trail is complete.

---

## Severity Ladder

Mirrors SQLTriage's native audit-event severities.

| Level | Definition |
|---|---|
| **Fatal** | System cannot function; data integrity at risk. Immediate action. |
| **Critical** | Production target or core subsystem degraded; SLA impact imminent. |
| **Error** | Condition requires same-day remediation; no immediate SLA breach. |
| **Warning** | Degraded but tolerable; review next business day. |
| **Info** | Informational only; no action required. |

---

## Escalation Matrix

| Severity | First responder | Escalate if unresolved (15 min) | Escalate if unresolved (1 hr) | Comms channel |
|---|---|---|---|---|
| **Fatal** | {{primary-oncall}} | {{secondary-oncall}} | {{leadership}} | {{comms-channel}} |
| **Critical** | {{primary-oncall}} | {{secondary-oncall}} | {{leadership}} | {{comms-channel}} |
| **Error** | {{primary-oncall}} | {{secondary-oncall}} | — | {{comms-channel}} |
| **Warning** | Log only | — | — | Review at standup |
| **Info** | No action | — | — | — |

Fill in oncall names/handles and communication channel (Slack, Teams, PagerDuty, etc.) before first production deployment.

---

## Standard Incident Lifecycle

All incidents follow this sequence. Every step that touches SQLTriage produces an audit-log entry.

1. **Detect** — Alert appears on the Alerts NOC page.
2. **Acknowledge** — Responder clicks the **Acknowledge** button on the alert row (or **Acknowledge All** for bulk). This timestamps the acknowledgement and records the acknowledging user in the audit log.
3. **Triage** — Consult the relevant playbook below.
4. **Remediate** — Execute the playbook steps.
5. **Root-cause** — Once the root cause is understood, call `MarkRootCausedAsync` via the in-app action (or directly through `AlertHistoryService.MarkRootCausedAsync`) to record notes.
6. **Close** — Click close / call `AlertHistoryService.CloseAsync`. Alert moves to the Resolved/Acknowledged panel (visible for 24 hours post-closure).
7. **Post-incident note** — If the incident was Error or above, record a one-paragraph summary in `docs/compliance/sign-off-log.md` referencing the alert ID.

---

## Playbooks

### Audit Chain Break Detected

**Trigger:** The AuditLogViewer banner fires: "Audit chain integrity failure detected."

**Cause:** An HMAC-SHA256 chain link is invalid — a record was deleted, modified, or inserted out of sequence.

1. Do not modify or delete any audit records.
2. Note the first failing record ID from the banner.
3. Determine whether the break was caused by: a deployment gone wrong (rollback?), direct database manipulation, or a bug in the audit writer.
4. If direct manipulation is suspected, escalate to {{leadership}} immediately and preserve the SQLite file as forensic evidence (copy to `{{evidence-store-path}}/audit-chain-break-YYYY-MM-DD.db`).
5. Remediate per the root cause. Do not re-run chain generation to paper over a break.
6. Acknowledge, MarkRootCaused, and Close the alert with full notes.

---

### SQLite Store Corrupted

**Trigger:** Application fails to open the local cache after an upgrade or unexpected shutdown.

1. Stop the application.
2. Copy the corrupt `.db` file to `{{evidence-store-path}}/corrupt-cache-YYYY-MM-DD.db` before taking any other action.
3. Delete or rename the corrupt file. SQLTriage will recreate an empty store on next start.
4. Allow the delta-fetch cycle to repopulate data (typically completes within one polling interval).
5. Verify the cache is opening cleanly in logs (Serilog output).
6. If corruption recurs, check disk health and available space before re-opening.

---

### HMAC Key File Deleted or Missing

**Trigger:** Application error on startup: HMAC key cannot be loaded; or audit-chain verification fails on all records simultaneously.

1. Do not attempt to regenerate a new key and backfill — this would invalidate the entire existing audit chain, which is a compliance event in itself.
2. Restore the key from the secure backup at `{{hmac-key-backup-path}}`.
3. If no backup exists, escalate to {{leadership}} immediately. This is a data-integrity incident.
4. Document the key loss event in `docs/compliance/sign-off-log.md`.
5. Post-recovery: verify chain integrity via the AuditLogViewer; confirm zero breaks before returning to normal operation.

---

### Server Circuit-Breaker Tripped (>30-Minute Outage)

**Trigger:** A monitored production SQL Server has had its Polly circuit-breaker in open state for more than 30 minutes, meaning SQLTriage cannot connect.

1. Check whether the SQL Server itself is reachable from the host machine (network/firewall).
2. Check SQL Server error log for service outage, failover, or credential expiry.
3. If a planned maintenance window: no escalation needed; note in sign-off log if it triggered a Critical alert outside expected windows.
4. If unplanned: follow the escalation matrix above at the **Critical** level.
5. Once the server is reachable, SQLTriage's circuit-breaker resets automatically on the next polling cycle. Confirm by checking the server tile on the dashboard.
6. Acknowledge and Close the alert with notes on duration and root cause.

---

### Failover Audit Directory In Use (Primary Audit Write Failing Repeatedly)

**Trigger:** Logs show repeated write failures to the primary audit path; the application has switched to the failover audit directory.

1. Check the primary audit path for disk-full, permissions error, or file lock.
2. Do not delete audit files to free space — archive to `{{evidence-store-path}}` first.
3. Resolve the root cause on the primary path.
4. Confirm the application has reverted to the primary path (check Serilog output for "audit directory restored" or similar).
5. Verify that no audit records were lost during the failover window by inspecting record timestamps around the event.
6. Acknowledge and Close the alert.

---

## In-App Lifecycle Methods (for operators)

These methods are available on `AlertHistoryService` and are exercised by the corresponding UI actions on the Alerts NOC page:

- `AcknowledgeAsync(alertId, role)` — Acknowledge button
- `MarkRootCausedAsync(alertId, user, notes)` — root-cause recording
- `CloseAsync(alertId, user, notes)` — close/resolve

All three emit audit-log entries. Do not bypass them by writing directly to the database.
