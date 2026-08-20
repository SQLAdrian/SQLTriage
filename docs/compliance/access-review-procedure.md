# Access Review Procedure

**Control mapping:** SOC2 CC6.3 · NIST AC-2(j)
**Why this exists:** Auditors require documented evidence that user access is periodically reviewed, that stale accounts are removed, and that each review is signed and retained. Without this document, CC6.3 is a finding on Day 1.

---

## Purpose

Ensure that every SQLTriage RBAC account retains only the access it needs. Accounts that are no longer required are removed promptly. Each review cycle produces a signed, retained evidence artefact.

---

## Scope

- SQLTriage RBAC user list (Settings → Users section).
- Monitored SQL Server logins recorded in SQLTriage server configurations, where applicable.
- Any service or integration accounts granted a SQLTriage role.

---

## Cadence

**Quarterly** (every 90 days, configurable via `Rbac:AccessReviewDays` in `appsettings.json`).

SQLTriage surfaces a banner in Settings → Users when any account has not been reviewed within the configured window. The banner fires automatically; no manual calendar reminder is needed as the primary trigger.

Ad-hoc reviews are required within 5 business days of:
- An employee or contractor departure.
- A role change that reduces the need for elevated access.
- A security incident involving credential compromise.

---

## Reviewer Role

**Reviewer:** {{reviewer-name-and-title}}
**Backup reviewer:** {{backup-reviewer-name-and-title}}
**Approver (if different):** {{approver-name-and-title}}

At the current stage of the organisation, the reviewer is typically the owner or designated security lead.

---

## Procedure

1. Open the SQLTriage application. Navigate to **Settings → Users**.
2. If the 90-day banner is displayed, note the count of overdue accounts.
3. For each account in the user list:
   a. Confirm the user still requires access and that their assigned role is correct.
   b. If access is no longer required: click **Remove**. This action is audit-logged with timestamp and reviewer identity.
   c. If access continues to be required: click **Mark access reviewed**. This updates `LastReviewedAt` and is audit-logged.
4. Once all accounts are processed, click **Export access-review report** to download the CSV.
5. Save the CSV to `{{evidence-store-path}}` using the filename format: `access-review-YYYY-QN.csv` (e.g. `access-review-2026-Q2.csv`).
6. Sign off using one of the two methods below (see Sign-Off).

---

## Evidence Retention

Retain all access-review CSVs and sign-off records for **{{retention-years}} years**, in accordance with your buyer contracts and applicable regulatory requirements (SOC2 Type II typically requires 1 year minimum; FedRAMP requires 3 years).

Store evidence at: `{{evidence-store-path}}`

---

## Exception Handling

If a user account was missed during the review (e.g. discovered after the CSV was exported):

1. Perform the missed step (Remove or Mark reviewed) immediately.
2. Export a revised CSV with the suffix `-rev1`: `access-review-YYYY-QN-rev1.csv`.
3. Note the exception and corrective action in `docs/compliance/sign-off-log.md` under the same quarter entry.
4. If the missed account had inappropriate access, treat it as a security incident and follow the Incident Response Runbook.

---

## Sign-Off

After saving the evidence CSV, record completion using **one** of:

- **Physical print:** Print the CSV, sign and date it, file in the compliance binder.
- **Digital log:** Open `docs/compliance/sign-off-log.md` and append an entry:

  ```
  | YYYY-QN | access-review | {{reviewer-name}} | YYYY-MM-DD | access-review-YYYY-QN.csv |
  ```

Both the Remove and Mark-reviewed actions are independently audit-logged inside SQLTriage; the sign-off record confirms the review was deliberate and complete, not just a series of button clicks.
