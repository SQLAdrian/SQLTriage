# Deploy Checklist

Before first regulated-buyer deployment, complete every item below.

## Configuration

- [ ] Set `Continuity:RecoveryPointObjectiveMinutes` per buyer contract (default 15)
- [ ] Set `Continuity:RecoveryTimeObjectiveMinutes` per buyer contract (default 60)
- [ ] Confirm `Audit:HmacKeyMaxAgeDays` matches your rotation policy (default 365)
- [ ] Confirm `Rbac:AccessReviewDays` matches your access-review cadence (default 90)
- [ ] Confirm `Audit:RetentionDays` matches your audit-log retention SLA (default 90; check regulatory requirement)
- [x] Code-signing certificate obtained (Certum cloud, expires 2027-07-20). ⚠ The key is HSM-resident and non-exportable, so there are **no** `CODESIGN_CERT_BASE64` / `CODESIGN_CERT_PASSWORD` secrets and **CI cannot sign** — releases are signed **locally** via `publish-release.ps1 -SigningThumbprint <sha1>` with SimplySign Desktop logged in. Runbook: `.handoff/CODESIGN_LAUNCH_CHECKLIST.md`. (Unsigned binaries are SmartScreen-blocked.)

## Compliance templates

For each of `docs/compliance/*.md`, fill in every `{{placeholder}}`:
- [ ] `access-review-procedure.md`: reviewer, backup reviewer, evidence path, retention years
- [ ] `incident-response-runbook.md`: oncall matrix, comms channel, HMAC backup path
- [ ] `vendor-dependency-register.md`: last-reviewed dates, license confirmations, additional vendors

## Backup procedures

- [ ] Document HMAC key backup path; ensure encrypted with DPAPI LocalMachine scope key (config/.credential-key)
- [ ] Document SQLite stores backup procedure (online vs offline)
- [ ] Verify audit log retention purge timer is configured (24h interval)
- [ ] Schedule weekly full-chain verification

## Operator handoff

- [ ] Print and brief operators on IR runbook severity ladder
- [ ] Test the access-review CSV export workflow once before go-live
- [ ] Test the chain-tamper banner by intentionally corrupting a test segment (rollback after)
- [ ] Confirm at least one operator has acknowledged the IR runbook

## First-week monitoring

- [ ] Audit-chain verify cron has fired and PASSED
- [ ] No `ChainBroken` warnings in logs
- [ ] No `AuditFlushFailover` events
- [ ] Config-drift report is empty (or expected drift documented)
