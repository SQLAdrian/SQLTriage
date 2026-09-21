<!-- In the name of God, the Merciful, the Compassionate -->

# Scheduling

Query-orchestration primitives that govern when/how monitoring queries run against monitored SQL instances. Distinct from `ScheduledTaskEngine` in `Data/Services/` (which is the high-level cron-style task scheduler).

| File | Purpose |
|------|---------|
| IQueryOrchestrator / QueryOrchestrator | Priority queue (P0–P4) dispatcher with global + per-server concurrency slot acquisition |
| QueryRegistry | Static registry of monitoring queries by id |
| QueryMetadata | Per-query knobs — priority, cadence, target scope, timeout |
| QueryScheduler | Cadence loop that hands work to the orchestrator |
| SqlHasher | Deterministic SHA-256 over normalised SQL (CRLF→LF + trim) — also used by tamper-checksum gate (see memory `project_query_tamper_checksum_2026-05-26`) |

The CPU-pressure probe service that used to sit in this folder was deleted on 2026-09-07 under the no-server-idle ruling (dev main `118feec`): nothing called it. The dispatcher has no CPU guard — it gates on the global and per-server concurrency semaphores only.

Priority invariant: P0 work must dispatch before P4 — see memory `project_orchestrator_priority_test_debt` + the dequeue-order test seam.
