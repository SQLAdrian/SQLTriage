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

**Cause:** An HMAC-SHA256 chain link is invalid — a record was deleted, modified, or inserted out of sequence. The record's signing key **was available** and the signature still did not match. That is the tamper signal.

BROKEN is also reported (since 2026-08-01) when an entry names a signing key that cannot be resolved **and** cannot be genuine key loss: a key id this installation never held and has never seen on this chain, one that had already been superseded before the entry was written, or a run bracketed by the same key on both sides. Genuine key loss takes out one contiguous stretch of the chain at once; a lone dark entry among live ones is a rewritten `KeyId` field, not a lost key. The first failing record ID names which of those fired.

BROKEN is also reported when the out-of-band tail checkpoint says entries were **removed**: `.chain-anchor` deleted while its regime marker shows one was established, or an anchored tail signature that no longer appears in the latest segment. A chain whose surviving entries all verify is still not intact if entries were taken out of it. An anchor that is *present but unreadable* is **not** in this bucket — see *Audit Chain Provenance Indeterminate*, cause 2.

> If the verifier reports **UNVERIFIABLE** or **INDETERMINATE** rather than BROKEN, this is not the right playbook — see *Audit Chain Unverifiable* or *Audit Chain Provenance Indeterminate* below. Verification distinguishes all three deliberately; do not treat them alike.

> Before quoting a BROKEN verdict as proof that someone tampered, read *What this chain does and does not guarantee* below: a minted anchor naming a foreign key can flip an **innocent** store to BROKEN, and that has been measured.

1. Do not modify or delete any audit records.
2. Note the first failing record ID from the banner.
3. Determine whether the break was caused by: a deployment gone wrong (rollback?), direct database manipulation, or a bug in the audit writer.
4. If direct manipulation is suspected, escalate to {{leadership}} immediately and preserve the SQLite file as forensic evidence (copy to `{{evidence-store-path}}/audit-chain-break-YYYY-MM-DD.db`).
5. Remediate per the root cause. Do not re-run chain generation to paper over a break.
6. Acknowledge, MarkRootCaused, and Close the alert with full notes.

---

### Audit Chain Unverifiable

**Trigger:** The AuditLogViewer shows the amber banner "Some audit entries are unverifiable", or a verification run reports status **UNVERIFIABLE**.

**Cause:** One or more entries were signed with an HMAC key that is no longer available on this machine, so their integrity can be neither confirmed nor denied. **No entry with an available signing key was found to have been altered** — the unverifiable entries themselves were never checked, so nothing is claimed about their contents either way. This is an evidence gap, not an attack.

Reaching this verdict requires the key id named to leave a **trace** in this installation's own provenance records: the succession list or seen-key set in `.chain-anchor`, or an `hmac.key.<id>` archive file. Where no such record survives at all, the verdict is INDETERMINATE instead and nothing may be claimed in either direction — see *Audit Chain Provenance Indeterminate* below.

> **Corrected 2026-08-01 (rounds 4 and 5).** This paragraph previously said that reaching UNVERIFIABLE *"requires surviving key-provenance evidence that the key id named is one this installation actually held."* **That was false**, and it was printed on a client-facing document. All three traces live in the audit-log directory under the same permissions as the `.jsonl` they vouch for.
>
> Round 4 then said the trace was hardened so that an archive file "must now hold something shaped like a key this service wrote". True, but **round 4's account of what that bought was still too generous, and it is corrected here.** Measured 2026-08-01 on a fully healthy install — head entry's `Message` altered, its `KeyId` rewritten to a forged id, one plant per row:
>
> | Planted as `hmac.key.<forged-id>` | Verdict | Signature failures | Forged id learned into the anchor? | Verdict after deleting the plant |
> | --- | --- | --- | --- | --- |
> | nothing | **BROKEN** | 1 | no | BROKEN |
> | 1 arbitrary byte | **BROKEN** | 1 | no | BROKEN |
> | 32 bytes of junk | **BROKEN** | 1 | no | BROKEN |
> | the 20 header bytes, and nothing else | UNVERIFIABLE | 0 | **yes** | **UNVERIFIABLE** |
> | a copy of a real key blob | UNVERIFIABLE | 0 | **yes** | **UNVERIFIABLE** |
> | any foreign DPAPI blob | UNVERIFIABLE | 0 | **yes** | **UNVERIFIABLE** |
>
> Only the **one-byte route** was closed. **The permanent laundering was not removed**: the fourth row is a file containing nothing but the twenty constant bytes that open every DPAPI blob — bytes that are published in this product's own source comments and pinned by its own test — and it still buys the benign verdict, still writes the forged id into the anchor's seen-key set, and still leaves the verdict benign after the planted file is deleted again. The cost of laundering a forged key id moved from **1 arbitrary byte to 20 published constant bytes**. Nothing else about it changed.
>
> Read UNVERIFIABLE as *"the forged-`KeyId` shortcut was not taken carelessly"*, never as *"this key id is proven genuine"* and never as *"a forged id cannot be laundered here"*.

The overwhelmingly common cause is that the signing key was replaced. Since 2026-08-01 the key is DPAPI-wrapped with `LocalMachine` scope, so a service-account change can no longer orphan it. Blobs written by earlier builds used `CurrentUser` scope and are bound to the Windows identity that wrote them; what happens to each of them on upgrade differs by file, and the earlier blanket claim that they are "re-wrapped in place the first time that same identity reads them" was over-broad:

| File | Upgraded to LocalMachine when |
| --- | --- |
| `hmac.key` (the live key) | On startup, on the read path — requires the original identity to still be running. |
| `hmac.key.<current-id>` (the current key's archive) | On startup, unconditionally, rewritten from the key material already held in memory. Corrected 2026-08-01 (round 3): nothing used to read this file, so it stayed `CurrentUser` indefinitely and would have gone dark at the next identity change. |
| `hmac.key.<older-id>` (prior keys' archives) | Only when verification actually reads one, and only while the identity that wrote it is running. Their material exists nowhere but inside the blob, so nothing else is possible. |
| `.chain-anchor` | On the next flush — it is rewritten wholesale every time, so it self-upgrades. |

A key already orphaned under a previous identity is **not** recovered by any of that — it prevents recurrence only. Look for a preceding `[AUDIT] HMAC KEY REPLACED` error in the Serilog output and an `HmacKeyReplacedUnreadable` entry on the chain itself.

1. Do not modify or delete any audit records, and do not delete the preserved key blob.
2. Confirm the diagnosis: check for `hmac.key.unreadable-<timestamp>` beside `hmac.key` in the audit-log directory. Its presence means the previous key could not be unwrapped and was preserved rather than destroyed.
3. Establish what changed: compare the Windows identity the service runs as now against the identity in effect when the affected entries were written (the `User` field on those entries). A service-account change is the usual answer.
4. If the original identity can be restored, the preserved blob can be unwrapped under it and the archived key recovered. Do this before writing the incident off as unrecoverable.
5. If the original identity is gone, the affected entries stay permanently unverifiable. Record the gap — the affected range, the unresolvable key id, and the cause — in `docs/compliance/sign-off-log.md`. Do **not** report the range as verified, and do **not** report it as tampered.
6. Verify that entries written since the key replacement verify cleanly (the verifier examines the whole chain, so a genuine break after the gap is still reported as BROKEN).
7. If no key-management event explains the gap, escalate: an unexplained missing key is itself a security concern.

---

### Audit Chain Provenance Indeterminate

**Trigger:** The AuditLogViewer shows the red banner "Audit chain integrity **cannot be determined** for some entries", or a verification run reports status **INDETERMINATE**. On the chain itself: an `AuditChainIndeterminate` entry at severity **Error**.

**Cause:** One or more entries name a signing key that is not available on this machine — *and* the key-provenance record that would show whether that key id is one this installation ever held is missing too. The service therefore has no evidence in either direction.

> **This verdict claims nothing.** It does not say the entries are intact. It does not say they were altered. Both remain consistent with what was observed. Do **not** report the affected range as verified, do **not** report it as tampered, and do **not** close it as routine key management — that last one is exactly the false comfort this state exists to prevent. UNVERIFIABLE is the verdict for a key gap the service can *evidence*; this is the verdict for one it cannot.

Three things put an installation in this state:

1. **First launch after upgrading to the 2026-08-01 build.** Anchors written by earlier builds carry no provenance ledger. This is benign, expected and self-clearing: the first flush writes a ledger and the next launch judges the same entries on evidence. It surfaces at all only if the chain already contains entries with an unresolvable key.
2. **The anchor was lost along with the key.** A service-account change that orphaned a `CurrentUser`-wrapped key orphaned the `CurrentUser`-wrapped anchor beside it. This is the shape of the live 2026-08-01 incident: key, archive and anchor all *present but unreadable*, nothing altered.

   **Corrected 2026-08-01 (round 4).** Until this round the startup check reported that exact shape as **BROKEN** — "chain anchor is present but could not be read … possible tampering" — and handed the operator the tampering playbook, while a verification run on the same bytes returned INDETERMINATE with zero broken entries. The startup banner and the verifier contradicted each other, and the banner contradicted this runbook. An unreadable anchor is now reported as INDETERMINATE, which is what it is: the service cannot read the record it needs, so it cannot conclude anything. It is *not* benign either — deliberate corruption looks identical — so it stays an Error-severity finding that must be closed on evidence.
3. **The anchor and its regime marker were deleted together.** Deleting only `.chain-anchor` is caught as truncation; deleting `.chain-anchor.regime` with it suppresses that check. Both files sit in the audit-log directory under the same ACL as the `.jsonl`, so anyone who can edit the log can do this. If nothing in change control explains their absence, treat it as a security incident, not an ops mishap.
4. **A key archive was tidied up too early.** `hmac.key.<id>` files are evidence, not clutter. See the warning below.

> ### ⚠ Never delete `hmac.key.*` files as maintenance
>
> A genuine key loss stays a benign UNVERIFIABLE only while *some* trace of the lost key id survives. The seen-key list in the anchor is the trace that is meant to outlive the archive files — but an id only reaches that list on a launch where the classifier already accepted it, which needs a provenance ledger, which the **first launch after the 2026-08-01 upgrade does not have**. The safe window therefore opens at the **second** launch after the upgrade, not before it.
>
> Measured: a genuine loss whose archive file was also gone at any point before that — including deletions made long *after* the upgrade but before the second launch — reports INDETERMINATE at launch 1 and then **permanent BROKEN** from launch 2 onward. That is a false tamper verdict on an innocent chain, caused by nothing more than housekeeping. Round 3 of this work described the window as "deleted before the upgrade", which was wrong and too narrow.
>
> Preserve the whole audit-log directory. Retention pruning applies to `audit-*.jsonl` segments only.

**Steps**

1. Do not modify or delete any audit records, key files or anchor files. Preserve the whole audit-log directory.
2. Establish which of the three causes applies, in that order. Cause 1 is the only benign one, and it is benign only if the upgrade is genuinely what changed — check the build number against the deployment record, not against memory.
3. If cause 1: relaunch the service and re-run verification. The verdict resolves to INTACT, UNVERIFIABLE or BROKEN on evidence. Whichever it is, that is the real answer — record it and follow the matching playbook.
4. If cause 2 or 3: escalate to {{leadership}}. Copy the audit-log directory to `{{evidence-store-path}}/audit-chain-indeterminate-YYYY-MM-DD/` before anything else touches it.
5. Reconstruct the affected range from an independent source if one exists — the Windows Event Log mirror (Critical entries only), the portal's published summaries, or a backup of the log directory taken before the anchor went missing. An independent copy is the only thing that can settle this either way.
6. Record the outcome in `docs/compliance/sign-off-log.md`: the affected range, the key id named, which cause was established, and — if it could not be settled — an explicit statement that the range is **unverified and undetermined**. That wording matters to an auditor; "unverifiable" does not mean the same thing.
7. Acknowledge, MarkRootCaused and Close the alert with full notes.

---

### What this chain does and does not guarantee

Read this before quoting any verdict above to a client or an auditor. Everything here was **measured** on this build, not reasoned about.

**The one-sentence version: an attacker who can write the audit-log directory can manufacture any verdict, including INTACT.**

Every artefact a verdict rests on lives in that one directory, under the same permissions as the `audit-*.jsonl` files themselves:

| Artefact | What it is relied on for | Who can write it |
| --- | --- | --- |
| `audit-*.jsonl` | the records | anyone with write access to the directory (conceded by the threat model) |
| `hmac.key`, `hmac.key.<id>` | the signing keys, and the archive trace that corroborates a lost key id | same |
| `.chain-anchor` | the tail checkpoint **and** the key-provenance ledger | same |
| `.chain-anchor.regime` | proof that an anchor must exist | same |

The anchor is DPAPI-wrapped, which is worth having but is not a boundary here: the entropy is a plain string literal in the shipped `SQLTriage.dll`, and since 2026-08-01 the wrap is `LocalMachine` scope, so **any local process can mint a valid anchor**. Measured both directions — a minted ledger naming a forged key buys UNVERIFIABLE on a store that really was tampered with, and a minted ledger naming a foreign key flips an **innocent** store to BROKEN.

**The full-forgery path (pre-existing, open, not fixed).** Reproduced on the `791d2cb` production binary, so it predates the 2026-08-01 audit-honesty work. An attacker with write access to the directory can:

1. plant 32 arbitrary bytes as `hmac.key.<any-id>` — the key reader accepts a raw 32-byte file as key material;
2. rewrite an entry and re-sign it under those bytes in the v1 canonical form, naming `<any-id>` in its `KeyId`;
3. mint a `.chain-anchor` whose tail matches.

Verification then reports **`INTACT` — "All N entries verified against their signing keys"** — with no alarm, on that launch and every launch after it. Nothing in the current design detects this, and the hardening added in round 4 (an `hmac.key.<id>` archive must now hold something shaped like a key this service wrote, rather than any file at all) does **not** address it. That hardening closes the *one-byte* route to laundering a forged key id past the *provenance* check and nothing else: measured 2026-08-01, a file holding only the twenty constant DPAPI header bytes still buys the benign verdict, is still learned into the anchor, and still survives deletion of the plant. Its price went from 1 byte to 20 published bytes — see the table under *Audit Chain Unverifiable*.

Closing this is an architectural decision, not a code fix: it needs a root of trust the attacker cannot write — an off-box signed mirror, an HSM/KMS-held signing key, or remote attestation of the chain tail. It is recorded here rather than patched so that no future reader mistakes the round-4 hardening for a solution.

**So what is the chain actually worth?**

- It is **strong against accidental corruption and against an unprivileged or careless actor.** A record edited in place without also re-signing it, re-keying it and re-minting the anchor is caught immediately and named.
- It is **an evidence trail, not a control.** Its value is that tampering must be *thorough and deliberate* to go unnoticed, and that the Windows Event Log mirror (Critical entries, admin-cleared only) and any off-box copy sit outside the attacker's reach.
- It is **not** proof of integrity against anyone holding write access to the audit-log directory. Restricting that ACL, and shipping copies off the box, is what actually carries that weight — not the verdict word.

Any statement made to a client about audit-trail integrity must be consistent with this section.

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
