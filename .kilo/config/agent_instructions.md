# Kilo Agent Instructions — SQLTriage Project

**Scope:** These instructions load once per Kilo session and apply to all subsequent tasks in this workspace.

## Token Budget & Efficiency

- **Max context per task:** 12,000 tokens (Kilo will plan batches accordingly)
- **Prefer glob over recursive directory listing** — efficient discovery
- **Batch independent read calls** into single tool use block
- **When editing, include only 5–10 context lines** — never whole file unless asked
- **Use grep first, then read slices** — find exact line numbers before reading large files

## File Modification Rules

### C# Files (`.cs`)
1. First line must be basmalah: `/* In the name of God, the Merciful, the Compassionate */`
2. Nullable reference types enabled — fix CS8602/CS8604 when editing
3. Use existing DI patterns from `App.xaml.cs` — singleton for services, scoped for page models
4. Preserve existing code style (indentation, naming, using statements)
5. Add XML docs on public methods only if missing (do not remove existing docs)

### Razor Files (`.razor`)
1. First line must be basmalah comment: `<!--/* In the name of God, the Merciful, the Compassionate */-->`
2. Keep `@code` block at bottom; markup at top
3. Preserve existing CSS class usage; do NOT introduce Tailwind classes

### Markdown Files (`.md`)
1. First line: HTML comment basmalah (same as Razor)
2. Keep heading hierarchy (H1 → H2 → H3) consistent

### SQL Files (`.sql`)
1. Never embed SQL in C# strings — always load from `Data/Sql/**/*.sql` via `SqlQueryRepository`
2. Keep queries as static files; modify only via `SqlQueryRepository.Reload()` mechanism

## Architecture Patterns

### Service Registration (in `App.xaml.cs`)
```csharp
services.AddSingleton<SqlQueryRepository>();
services.AddScoped<IGovernanceService, GovernanceService>();
services.AddSingleton<ChartTheme>();
services.AddSingleton<IFindingTranslator, FindingTranslator>();
```

### Single-Writer Queue Pattern (AuditLog, etc.)
```csharp
private readonly BlockingCollection<AuditEvent> _queue = new();
private async Task WriterLoop(CancellationToken ct) {
    foreach (var ev in _queue.GetConsumingEnumerable(ct)) {
        // flush to checkpoint / external sink
    }
}
```

### Argon2id Hashing (RbacService)
```csharp
var salt = RandomNumberGenerator.GetBytes(16);
var subkey = KeyDerivation.Pbkdf2(password, salt, 100000, 32);
// Store: { Salt=Base64(salt), Subkey=Base64(subkey), Iterations=100000 }
```

### ApexCharts Integration (ChartTheme pattern)
```csharp
<ApexChart TItem="TimeSeriesPoint"
           Options="ChartTheme.GetOptions<TimeSeriesPoint>(Title, YAxisUnit)"
           Series="series"
           XAxisType="XAxisType.Datetime">
    <ApexPointSeries ... />
</ApexChart>
```

## Quality Gates

Before marking any task complete, verify:
- [ ] Basmalah header present on file (first line, exact text)
- [ ] No new build warnings introduced (`dotnet build` clean)
- [ ] Existing code style preserved (spaces vs tabs, brace placement, using order)
- [ ] No Tailwind classes introduced (use existing `app.css` selectors)
- [ ] SQL remains in external files, not embedded
- [ ] Unit tests added/updated if touching service logic (target 80% coverage)

## Project-Specific Knowledge

- **`SqliteCacheStore`** — WAL mode, 2-week retention, delta-fetch. Do not modify cache eviction logic.
- **`CredentialProtector`** — AES-256-GCM + DPAPI. Keep for connection strings only; not for passwords.
- **`VulnerabilityAssessmentService`** — already maps VA checks to categories; leverage it, don't duplicate.
- **`AlertEvaluationService`** — IQR baseline; keep alert logic there, not in Governance.
- **`AutoUpdateService`** — Squirrel.Windows; leave intact.
- **`wwwroot/css/app.css`** — 7500-line design system; keep CSS variables, no rewrite.

## What Kilo Should NOT Do

- Do not add new `// TODO` comments unless user explicitly requests
- Do not ask "shall I..." unless user explicitly requests confirmation
- Do not rewrite whole files unless asked — prefer surgical `edit` with minimal context
- Do not introduce external dependencies (NuGet) without user approval
- Do not modify `CLAUDE.md` or `kilo.json` unless instructed

---

**Last updated:** 2026-04-18 by system initialization (DECISIONS D01–D12 loaded)
