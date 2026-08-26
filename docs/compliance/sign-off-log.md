# Sign-off Log

Running evidence record for all compliance reviews, incident responses, and vendor checks.

This file is filled in append-only. Never edit historical rows. Backup quarterly to `{{evidence-store-path}}`.

Auditors will read this. Each entry must include: date (UTC), reviewer, action, evidence reference, outcome.

## Access Reviews (CC6.3)

| Date | Reviewer | Quarter | Users reviewed | Removed | CSV reference | Notes |
|---|---|---|---|---|---|---|
| {{YYYY-MM-DD}} | {{name-title}} | Q{{N}} {{YYYY}} | {{count}} | {{count}} | {{path/access-review-YYYY-QN.csv}} | {{notes}} |

## Incident Responses (CC7.x)

| Date opened | Severity | Acknowledged by | Root cause | Closed | Evidence reference |
|---|---|---|---|---|---|

## Vendor Reviews (CC9.2)

| Date | Reviewer | Package/Vendor | Outcome (Approved / Reviewing / Removed) | Notes |
|---|---|---|---|---|

## Audit-Chain Verifications (AU-9)

| Date | Verification scope | Entry count | Chain status | Report reference |
|---|---|---|---|---|

## HMAC Key Rotations (SC-28)

| Date | Rotated by | Prior key age (days) | Reason | Evidence |
|---|---|---|---|---|

## DR Tests (CP-2)

| Date | RPO target | RTO target | Actual outcome | Sign-off |
|---|---|---|---|---|
