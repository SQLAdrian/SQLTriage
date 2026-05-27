---
layout: default
title: SQLTriage — Security Whitepaper
description: How SQLTriage handles credentials, authentication, audit integrity, and access control. Written for procurement reviewers, compliance officers, and the DBAs whose boss needs an answer.
---
<!-- In the name of God, the Merciful, the Compassionate -->
<!-- Bismillah ar-Rahman ar-Raheem -->

# Security Whitepaper

This page describes how SQLTriage stores credentials, authenticates users, records
audit events, and limits what each role can do. It is written for the reviewer
whose job is to sign off — not for a fellow engineer. Every claim below cites the
file in the public repository that backs it.

## 1. Threat model

SQLTriage runs on a DBA's desktop. It reads from SQL Server instances using
connection credentials the DBA already holds. The trust boundary is the
operator's workstation.

What SQLTriage protects against:

- A stolen `.lmcreds` export being read on a different machine.
- A user account with the on-disk credential store but no DPAPI key reading
  saved passwords.
- An attacker silently editing, deleting, or reordering rows in the audit log
  without leaving evidence.
- An attacker enumerating valid usernames by timing the login form.
- A non-admin operator changing settings, managing users, or running scripts.

What SQLTriage explicitly does **not** do:

- It is not a SIEM. The audit log records what SQLTriage did, not what every
  monitored SQL Server did.
- It is not a vulnerability scanner for the host operating system.
- It does not manage certificate lifecycles for the SQL Servers it monitors.
- It does not install an agent on those servers — every check is a SELECT
  against DMVs from the operator's machine.
- It does not call home. There is no telemetry endpoint, no licence server,
  no update beacon. Network traffic during an audit is to the SQL Servers
  named in the connection list, and nothing else.

It is a single-tenant Windows desktop tool whose security posture is bounded
by the workstation it runs on.

## 2. Credential handling

Three layers, increasing in scope:

**At rest, on this machine.** Saved SQL Server passwords are encrypted with
AES-256-GCM. The 256-bit key is generated once per machine, stored in
`config/.credential-key`, and the key file itself is wrapped with DPAPI at
`LocalMachine` scope, so any user or service on that machine can use it but the
file is useless if copied elsewhere. See
[`Data/CredentialProtector.cs`](https://github.com/SQLAdrian/SQLTriage/blob/main/Data/CredentialProtector.cs)
lines 93–162. A legacy DPAPI `CurrentUser` format is still decrypted on read for
backward compatibility but no longer used for new writes.

**Portable export.** To move a server list between machines, SQLTriage can
emit a `.lmcreds` bundle. The user supplies a passphrase; SQLTriage derives a
256-bit AES key with PBKDF2-HMAC-SHA256 — **310,000 iterations**, **32-byte
random salt** — then re-encrypts each password with AES-256-GCM under that
derived key. The iteration count matches the OWASP 2023 minimum recommendation
for PBKDF2-SHA-256. See
[`Data/CredentialPorter.cs`](https://github.com/SQLAdrian/SQLTriage/blob/main/Data/CredentialPorter.cs)
lines 26–28 and 172–178. On import, the passphrase decrypts each password and
SQLTriage immediately re-encrypts it with the target machine's AES key, so the
bundle never sits decrypted in cache.

**In transit, in memory.** Decrypted passwords exist only for the moment a
connection is opened. They are not logged, not serialised, and not written to
the SQLite cache.

## 3. Local password authentication

Server Mode supports local-account passwords in addition to OAuth. Passwords
are hashed with **Argon2id** at the parameters OWASP recommended in 2024:

| Parameter            | Value           |
|----------------------|-----------------|
| Memory               | 19,456 KiB (19 MiB) |
| Iterations           | 2               |
| Parallelism          | 1               |
| Salt                 | 16 bytes random |
| Hash output          | 32 bytes        |

Hashes are stored self-describing: `argon2id$v=19$m=19456,t=2,p=1$<salt>$<hash>`.
Verification uses `CryptographicOperations.FixedTimeEquals` so a wrong password
cannot be distinguished from a wrong username by response timing. On a missing
account, the verifier runs the same Argon2 computation against a synthetic hash
so the work performed is the same shape whether the email exists or not. See
[`Data/Services/RbacService.cs`](https://github.com/SQLAdrian/SQLTriage/blob/main/Data/Services/RbacService.cs)
lines 251–311 and 345–367.

## 4. Tamper-evident audit log

Every authentication, permission change, script execution, and credential
operation is written to a tamper-evident chain. Each entry stores the
HMAC-SHA256 signature of `(previous-entry-signature || canonical(this-entry))`,
keyed with a 32-byte HMAC key persisted next to the log file. Any edit,
deletion, or reorder breaks the chain from that point forward. On startup,
SQLTriage walks the chain and records a `.chain-break-marker` if verification
fails, so the next operator and the next auditor both see the break.

Entries at severity `Critical` or `Error` are also mirrored to the Windows
Event Log (source `SQLTriage-Audit`, log `Application`), so a SIEM or EDR
already tailing the Event Log picks them up without integrating with SQLTriage.

Log files rotate at **64 KiB** to keep the chain segments small enough to scan
quickly; the rotation does not break the chain because each segment seeds from
the previous segment's last signature.

See [`Data/AuditLogService.cs`](https://github.com/SQLAdrian/SQLTriage/blob/main/Data/AuditLogService.cs)
— rotation threshold line 42; HMAC computation lines 533–555; Event Log
mirroring lines 476–481; startup chain verification lines 136–185.

Two operational notes worth being explicit about: the 32-byte HMAC key is held
in a file with the `Hidden` NTFS attribute rather than DPAPI-wrapped, so its
confidentiality relies on file-system ACLs on the SQLTriage data directory
(line 103–108). If that file is deleted or the bytes are insufficient on read,
a new key is generated and the chain restarts from the next entry — visible in
the log itself. Treat the data directory's ACLs as part of the audit posture.

## 5. Role-based access control

Three roles, with the permission matrix enforced in one place
([`RbacService.HasPermission`](https://github.com/SQLAdrian/SQLTriage/blob/main/Data/Services/RbacService.cs)
lines 221–242):

| Permission           | Admin | Operator | Viewer |
|----------------------|:-----:|:--------:|:------:|
| view_dashboard       |   Y   |    Y     |   Y    |
| view_results         |   Y   |    Y     |   Y    |
| view_audit_log       |   Y   |    Y     |   Y    |
| execute_checks       |   Y   |    Y     |        |
| run_scripts          |   Y   |    Y     |        |
| export_data          |   Y   |    Y     |        |
| acknowledge_alerts   |   Y   |    Y     |        |
| settings             |   Y   |          |        |
| manage_servers       |   Y   |          |        |
| manage_users         |   Y   |          |        |
| manage_alerts        |   Y   |          |        |

Unknown permissions default to admin-only.

In the default desktop mode (WPF + WebView2), the local Windows user is
treated as Admin — it is a single-user application and the operator already
owns the connection credentials. In **Server Mode**, where the same UI is
served over Kestrel for browser access, authentication is enforced through
cookie-backed OAuth via **Google** and **Microsoft** identity providers (see
[`ServerModeService.cs`](https://github.com/SQLAdrian/SQLTriage/blob/main/Data/Services/ServerModeService.cs)
lines 1–10 and 455–470). OAuth client secrets are themselves encrypted at rest
using `CredentialProtector.Encrypt` before being persisted to the RBAC config
(lines 71–77).

## 6. Defence in depth

- **Single-file, signed-where-possible distribution.** Release builds publish
  as a single self-contained executable (`SQLTriage.csproj` lines 92–95) and
  the production release pipeline runs ConfuserEx2 over the main assembly
  (`publish-protected.ps1`). Obfuscation is not a security control on its
  own; it raises the cost of casual reverse engineering, nothing more.
- **No external network calls during audit.** Checks query DMVs over the
  existing SQL connection. There is no telemetry, no licence check, no auto-
  update beacon, and no third-party API in the audit hot path.
- **No agent on SQL Server.** SQLTriage does not write to monitored databases
  and does not require any installed component on the SQL Server host.
- **Framework-mapped findings.** Each audit check carries citations to the
  framework controls it satisfies — NIST 800-53, CIS, DISA STIG, and others
  where applicable. Coverage is uneven across the corpus today; checks
  without a mapping say so rather than inventing one.
- **WAL durability.** The local SQLite cache runs in WAL mode and the WAL is
  checkpointed (`PRAGMA wal_checkpoint(FULL)`) on application exit so an
  abrupt shutdown does not corrupt the cache (`App.xaml.cs` line 332–348).

## 7. Test coverage on the security-critical paths

The security primitives are covered by unit tests that run on every build:

| Module                                  | Test file                       | Test count |
|-----------------------------------------|---------------------------------|-----------:|
| `AuditLogService` (chain + tamper)      | `AuditLogServiceTests.cs`       | 30 |
| `RbacService` (Argon2id + matrix)       | `RbacServiceTests.cs`           | 47 |
| `CredentialPorter` (PBKDF2 + GCM)       | `CredentialPorterTests.cs`      | 26 |
| `CredentialProtector` (AES-GCM + DPAPI) | `CredentialProtectorTests.cs`   | 10 |
| `BuildCatalogueService`                 | `BuildCatalogueServiceTests.cs` | 32 |

Tamper detection in particular is exercised against deletion, reorder, single-
byte mutation, signature substitution, and key replacement — the cases an
auditor will ask about. The full suite lives in
[`Tests/SQLTriage.Tests/`](https://github.com/SQLAdrian/SQLTriage/tree/main/Tests/SQLTriage.Tests).

## 8. Out of scope

A whitepaper that overclaims is worse than one that is candid, so to be
explicit about what SQLTriage is not:

- It is not a replacement for a SIEM. It tells you what SQLTriage did; your
  SIEM tells you what every other system did.
- It is not a host vulnerability scanner. It audits SQL Server configuration,
  not the Windows host.
- It does not manage certificate or key lifecycles for the SQL Servers it
  monitors. Whether `TrustServerCertificate` is on or off is the operator's
  choice on a per-connection basis.
- It does not federate identity. Server Mode supports Google and Microsoft
  OAuth; SAML and arbitrary OIDC providers are not implemented today.
- It does not encrypt the on-disk SQLite cache itself. Sensitive material
  (passwords, OAuth secrets) is encrypted before being written; non-sensitive
  cached query results are not.

If any of these gaps blocks a deployment, raise it on the GitHub issue tracker
— they are deliberate scope decisions, not oversights, but the priorities can
change with a credible use case behind them.

---

*Last reviewed against the codebase: 2026-05-15. Specific line numbers in this
document refer to the source as of that date; the file references will remain
correct even as line numbers drift.*
