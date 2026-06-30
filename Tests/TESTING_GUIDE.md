# Testing Guide

## Running tests

From repo root:

```
dotnet test SQLTriage.sln -c Release --no-build
```

CI runs the same command on push/PR.

## Test suites

| Project | Purpose | Test count |
|---|---|---|
| `Tests/SQLTriage.Tests/` | Unit + integration tests across all services | 130+ |

## Service coverage

| Service | Test file | Coverage focus |
|---|---|---|
| RbacService | RbacServiceTests.cs | Permission matrix, Argon2id verify, role lifecycle |
| AuditLogService | AuditLogServiceTests.cs | HMAC chain integrity, tamper detection |
| BuildCatalogueService | BuildCatalogueServiceTests.cs | FromJson seam, deterministic via injected referenceDate |
| WaitCategoryClassifier | WaitCategoryClassifierTests.cs | Prefix family classification |
| CredentialPorter | CredentialPorterTests.cs | AES-256-GCM + PBKDF2 round-trip |
| GovernanceService | GovernanceServiceTests.cs | Weighted-ratio scoring model |

## Adding new tests

- xUnit `[Fact]` / `[Theory]` style
- Real services (no mocks) — drive via test-seam constructors
- Temp dirs per test with `IDisposable` cleanup
- Avoid `Thread.Sleep` for timing assertions; use injected clock / explicit timestamps instead
- Avoid `DateTime.UtcNow` in test bounds; pass fixed reference date

## Known coverage gaps

See architect review notes; priority targets for next batch:
- SqliteCipherHelper migration path
- AuditLogService flush failover dir
- ServerCircuitBreakerService back-off transitions
- HMAC key rotation chain continuity
- ConfigBaselineService drift detection
