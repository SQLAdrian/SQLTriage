<!-- In the name of God, the Merciful, the Compassionate -->

# Tests

xUnit test projects. Full guide at `Tests/TESTING_GUIDE.md` — read that first.

| Project | Scope |
|---------|-------|
| SQLTriage.Tests | Pure unit + property tests (no live SQL). Loads ~~~ tests. |
| TestData/ | Golden files, sample inputs, fixtures |

There is no separate integration project. `SQLTriage.Tests.Integration` existed as a
scaffold from 2026-04 to 2026-08-10 and was deleted: it never held a single `[Fact]`, was
in no `.sln` and no CI path, and so had never run. Live-SQL work belongs in
`SQLTriage.Tests` behind an arming attribute -- see `Tests/SQLTriage.Tests/Portal/
ArmedLiveFactAttribute.cs`, which makes an unarmed live test SKIP rather than pass.

## Running

```powershell
dotnet test SQLTriage.sln                              # all unit tests
dotnet test --filter FullyQualifiedName~Governance     # subset
```

CI is informational only (per memory `feedback_ci_is_not_the_gate`) — local-green is the real gate.

## Test-seam pattern (canonical)

For services that load from a hardcoded `AppContext.BaseDirectory` path, add a second public constructor taking the path explicitly. Tests use `IDisposable` + a temp dir. See memory `feedback_test_seam_pattern` and `RoadmapMappingClassificationTests` for the reference shape.

## Quarantined tests

If a test is `Skip = "reason"`, the reason MUST be backed by a memory note explaining the root cause + the design fix that would unblock it (see `project_orchestrator_priority_test_debt` for the canonical example).
