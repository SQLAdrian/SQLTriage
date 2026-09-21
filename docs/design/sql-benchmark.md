# SQL Benchmark Service — Design Doc

**Status:** Existing `BenchmarkService.cs` and `Benchmark.razor` are substantially implemented (not stubs). This doc captures the remaining gaps and the design for the interface + `IBenchmarkService` abstraction that was scaffolded as worklist item P2 (lines 391-406).

**IMPORTANT:** Before implementing, re-read `Data/Services/BenchmarkService.cs`. The class already implements the core benchmark SQL and cache storage. The worklist item is about:
1. Introducing `IBenchmarkService` for DI (currently the concrete class is injected directly)
2. Adding `VCpuStealResult` and `BenchmarkRating` enum (not present in the current file)
3. Adding `GetRecentResultsAsync` (historical comparison across servers)
4. Audit logging per run
5. Cross-server comparison UI on `/benchmark`

---

## 1. Goal

Provide a read-only SQL Server performance benchmark that measures CPU arithmetic throughput, string operation speed, memory DMV latency, and hypervisor scheduling pressure (signal wait %) via safe T-SQL. Results are stored in the SQLite cache for trending and presented in a sortable cross-server comparison table. vCPU steal detection (scheduler delay + signal wait analysis) flags over-provisioned VM hosts. Each benchmark run is audit-logged.

The worklist (lines 396-406) intended this as a first-line tool for diagnosing "this server feels slow" without requiring elevated permissions or schema changes.

---

## 2. Benchmark SQL Queries

The core benchmark SQL is already written in `BenchmarkService.cs` (existing implementation). The next session should NOT re-derive these queries — read the file first. The four queries are:

- **CPU integer**: 1M iteration `WHILE` loop with arithmetic accumulator — measures raw CPU throughput via T-SQL engine
- **String ops**: 10K string concatenation then `REVERSE(LEN())` — measures string/memory interaction
- **Memory DMV latency**: `COUNT(*)` across three large DMVs + aggregation over `sys.dm_exec_query_stats` — proxy for buffer pool and memory bus latency
- **Signal wait %**: ratio of `signal_wait_time_ms / wait_time_ms` across `sys.dm_os_wait_stats` with idle waits excluded — primary hypervisor contention indicator

All four queries exist verbatim in `BenchmarkService.cs`. Do not duplicate them. Extract into private static `readonly string` constants when refactoring to the interface pattern.

---

## 3. Scheduler Delay + Signal Wait for vCPU Steal Detection

The existing `GetCpuSchedulerDelayAsync` queries `sys.dm_os_schedulers` joined to `sys.dm_os_scheduler_runnable_tasks`. The result feeds `BenchmarkResult.CpuSchedulerDelayMs`.

To produce a separate `VCpuStealResult`:

```csharp
public sealed record VCpuStealResult
{
    public string ServerName { get; init; } = "";
    public double SignalWaitPercent { get; init; }   // from dm_os_wait_stats
    public double AvgSchedulerDelayMs { get; init; } // from dm_os_schedulers
    public int RunnableQueueDepth { get; init; }     // avg runnable_tasks_count
    public VCpuStealSeverity Severity { get; init; }
    public string Interpretation { get; init; } = "";
}

public enum VCpuStealSeverity { None, Low, Moderate, High }
```

Detection logic:

| Signal Wait % | Runnable Queue | Interpretation |
|---|---|---|
| < 5 | < 1 | None — no contention |
| 5–15 | < 2 | Low — normal for busy OLTP |
| 15–25 | 2–4 | Moderate — investigate host CPU allocation |
| > 25 | > 4 | High — strong vCPU steal signal; escalate to hypervisor admin |

---

## 4. Rating Baselines

From worklist line 401 ("use these rough baselines — adjust after real-world testing"):

```csharp
public enum BenchmarkRating { Excellent, Good, Marginal, Poor }
```

| Metric | Excellent | Good | Marginal | Poor |
|---|---|---|---|---|
| CPU integer (ms) | < 50 | 50–150 | 150–500 | > 500 |
| String ops (ms) | < 100 | 100–300 | 300–1000 | > 1000 |
| Memory DMV latency (ms) | < 50 | 50–200 | 200–500 | > 500 |
| Signal wait % | < 5 | 5–15 | 15–25 | > 25 |

The existing `BenchmarkResult.CpuRating`, `StringOpsRating`, `HypervisorRating` computed properties use a 3-tier Fast/Normal/Degraded scale. Migrate these to `BenchmarkRating` enum when introducing the interface.

---

## 5. Storage

Results are already stored via `liveQueriesCacheStore.UpsertStatValueAsync` in the existing `BenchmarkService.StoreBenchmarkResultsAsync` (see `BenchmarkService.cs` lines 242-269 for the exact call pattern). The dictionary keys used are:

```
cpu_integer_benchmark_ms, string_ops_benchmark_ms, memory_access_benchmark_ms,
signal_wait_percentage, cpu_scheduler_delay_ms
```

`GetRecentResultsAsync` should query `liveQueriesCacheStore` for these keys across all known servers and reconstruct `BenchmarkResult` objects. Ask the next Opus session to confirm the read API on `liveQueriesCacheStore` — use `GetStatValuesAsync` or equivalent.

---

## 6. UI

`/benchmark` already renders a functional table and per-server run buttons (see `Pages/Benchmark.razor`). Remaining UI work:

- Add a **cross-server comparison** view: sortable table with one row per server, all metrics side-by-side, colour-coded via CSS vars (`--green`, `--red`, `--orange`) using the `BenchmarkRating` enum. Never hardcode hex colours.
- Add a **vCPU steal indicator** panel: show `VCpuStealResult.Severity` as a badge with the interpretation string.
- Add a **historical trend** sparkline (optional): pull last N runs from cache and render a mini chart via the existing `TimeSeriesChart` component pattern.

---

## 7. Audit Logging

Add `BenchmarkRun` to the `AuditEventType` enum in `Data/AuditLogService.cs` (append before the closing brace, with a summary comment):

```csharp
/// <summary>User triggered a performance benchmark run against a SQL Server instance.</summary>
BenchmarkRun,
```

In `IBenchmarkService.RunBenchmarkAsync`, after storing results, call:

```csharp
_auditLog.Log(AuditEventType.BenchmarkRun, AuditSeverity.Info,
    $"Benchmark run on {serverName} — rating {result.OverallRating}",
    new Dictionary<string, string> { ["Server"] = serverName, ["Rating"] = result.OverallRating.ToString() });
```

Inject `AuditLogService` into `DocumentationService` (add to constructor). The `BenchmarkResult` produced is non-sensitive; no PII gating required.

---

## 8. Test Plan

Use `SQLTriage.Tests`. Mirror fixture pattern from `AuditLogServiceTests.cs`.

- **`BenchmarkService_RunBenchmark_StoresResultsInCache`**: mock `IDbConnectionFactory` returning fake scalar values; assert `liveQueriesCacheStore` contains the five stat keys after the call.
- **`BenchmarkService_DetectVCpuSteal_HighSignalWait_ReturnsHighSeverity`**: inject reader returning signal_wait=30%, runnable=5; assert `VCpuStealSeverity.High`.
- **`BenchmarkService_DetectVCpuSteal_LowSignalWait_ReturnsNone`**: signal_wait=2%, runnable=0 → `VCpuStealSeverity.None`.
- **`BenchmarkService_GetRecentResults_ReturnsPerServerRows`**: pre-populate cache with two server entries; assert count = 2.
- **`BenchmarkService_RunBenchmark_LogsAuditEvent`**: mock `AuditLogService`; assert `BenchmarkRun` event logged after successful run.

---

## 9. Pickup-Ready Checklist for Next Opus Session

Before starting, verify:

- [ ] `Data/Services/BenchmarkService.cs` exists with substantial implementation (NOT a stub)
- [ ] `Pages/Benchmark.razor` at `/benchmark` renders the results table and per-server buttons
- [ ] No `IBenchmarkService` interface exists yet — the concrete class is injected directly in the Razor page
- [ ] `BenchmarkRating` enum does not yet exist in the codebase
- [ ] `VCpuStealResult` record does not yet exist

Implementation order:

1. Read `BenchmarkService.cs` fully before touching anything.
2. Extract `IBenchmarkService` interface with three methods (`RunBenchmarkAsync`, `DetectVCpuStealAsync`, `GetRecentResultsAsync`).
3. Add `VCpuStealResult` and `BenchmarkRating` records/enum to `BenchmarkService.cs`.
4. Update `Benchmark.razor` to inject `IBenchmarkService` instead of the concrete type.
5. Add `BenchmarkRun` to `AuditEventType` enum.
6. Implement `DetectVCpuStealAsync` using the thresholds in section 3 of this doc.
7. Implement `GetRecentResultsAsync` by reading from `liveQueriesCacheStore`.
8. Wire audit logging into `RunBenchmarkAsync`.
9. Register `IBenchmarkService → BenchmarkService` in `ServiceCollectionExtensions.cs`.
10. Add cross-server comparison table to `/benchmark` UI (section 6).
11. Write unit tests (section 8).

Adrian decision needed before step 9: confirm whether `BenchmarkService` should stay `AddSingleton` (current pattern) or move to `AddScoped` given that it opens DB connections per call. Singleton with `IDbConnectionFactory` (already injected) is correct — connections are opened and disposed per call, not held.
