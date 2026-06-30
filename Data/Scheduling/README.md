<!-- In the name of God, the Merciful, the Compassionate -->

# Scheduling

Query-orchestration primitives that govern when/how monitoring queries run against monitored SQL instances. Distinct from `ScheduledTaskEngine` in `Data/Services/` (which is the high-level cron-style task scheduler).

| File | Purpose |
|------|---------|
| IQueryOrchestrator / QueryOrchestrator | Priority queue (P0–P4) dispatcher with CPU-guard + pool slot acquisition |
| QueryRegistry | Static registry of monitoring queries by id |
| QueryMetadata | Per-query knobs — priority, cadence, target scope, timeout |
| QueryScheduler | Cadence loop that hands work to the orchestrator |
| CpuProbeService | Lightweight CPU pressure probe used by the orchestrator's guard |
| SqlHasher | Deterministic SHA-256 over normalised SQL (CRLF→LF + trim) — also used by tamper-checksum gate (see memory `project_query_tamper_checksum_2026-05-26`) |

Priority invariant: P0 work must dispatch before P4 — see memory `project_orchestrator_priority_test_debt` + the dequeue-order test seam.
