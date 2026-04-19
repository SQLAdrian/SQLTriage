---
name: Session Vocabulary
description: Active abbreviations for input compression — auto-updated by LLM each session
type: reference
---

# Session Vocabulary

**Rules for the LLM:**
- When you see a shorthand token below in user input, expand it using this table before reasoning.
- If you CANNOT expand a shorthand (not in table AND can't infer from context), flag it: `[unknown shorthand: XYZ]` and ask the user.
- When you notice a phrase repeating ≥3 times in a session, propose an abbreviation: add a row under "Proposed" with `[PROPOSED]` tag.
- Prefer abbreviations that are 1-2 BPE tokens. Uppercase acronyms usually tokenize well (e.g. USS = 1 token, UsrStSrv = 3 tokens).

## Active abbreviations

| Shorthand | Full phrase | Type | Uses this session | Status |
|-----------|-------------|------|-------------------|--------|
|-----------|-------------|-------------------|--------|
| wl | worklist |  | 0 | active |
| nxt | what is next |  | 0 | active |
| USS | UserSettingsService | ACT | 0 | active |
| QPv2 | QueryPlanV2 | SRC | 0 | active |
| dcfg | dashboard-config.json |  | 0 | active |
| QC | QuickCheck | CHK | 0 | active |
| VA | VulnerabilityAssessment | CHK | 0 | active |
| dlk | deadlock |  | 0 | active |
| dmv | dynamic management view |  | 0 | active |
| xevent | extended events |  | 0 | active |
| SRV | ServerModeService | ACT | 0 | active |
| AEV | AlertEvaluationService | ACT | 0 | active |
| ADF | AlertDefinitionService | ACT | 0 | active |
| AHS | AlertHistoryService | ACT | 0 | active |
| ABS | AlertBaselineService | ACT | 0 | active |
| NCS | NotificationChannelService | ACT | 0 | active |
| TDF | ScheduledTaskDefinitionService | ACT | 0 | active |
| THS | ScheduledTaskHistoryService | ACT | 0 | active |
| RPC | ReportPageConfigService | ACT | 0 | active |
| ABE | AzureBlobExportService | ACT | 0 | active |
| SAS | SqlAssessmentService | ACT | 0 | active |
| SWD | SqlWatchDeploymentService | ACT | 0 | active |
| WIN | WindowsServiceHost | ACT | 0 | active |
| EPP | ExecutionPlanParser |  | 0 | active |
| HCS | HealthCheckService | CHK | 0 | active |
| ACH | AppCircuitHandler | ACT | 0 | active |
| CES | CheckExecutionService | ACT | 0 | active |
| CDS | DashboardConfigService | ACT | 0 | active |
| FAS | FullAuditStateService | ACT | 0 | active |
| VAS | VulnerabilityAssessmentStateService | ACT | 0 | active |
| XES | XEventService | ACT | 0 | active |
| CEC | CacheEvictionService | ACT | 0 | active |
| SES | sys.dm_exec_sessions | SRC | 0 | active |
| REQ | sys.dm_exec_requests | SRC | 0 | active |
| QST | sys.dm_exec_query_stats | SRC | 0 | active |
| QPL | sys.dm_exec_query_plan | SRC | 0 | active |
| CON | sys.dm_exec_connections | SRC | 0 | active |
| WST | sys.dm_os_wait_stats | SRC | 0 | active |
| CPL | sys.dm_exec_cached_plans | SRC | 0 | active |
| TRX | sys.dm_tran_active_transactions | SRC | 0 | active |
| IUS | sys.dm_db_index_usage_stats | SRC | 0 | active |
| MID | sys.dm_db_missing_index_details | SRC | 0 | active |
| WIA | sp_WhoIsActive | SRC | 0 | active |
| SWP | dbo.usp_sqlwatch_logger_performance | ACT | 0 | active |
| SWR | dbo.usp_sqlwatch_logger_requests_and_sessions | ACT | 0 | active |
| SWX | dbo.usp_sqlwatch_logger_xes_blockers | ACT | 0 | active |
| SWI | dbo.usp_sqlwatch_internal_add_performance_counter | ACT | 0 | active |
| QMG | sys.dm_exec_query_memory_grants | SRC | 0 | active |
| OPC | sys.dm_os_performance_counters | SRC | 0 | active |
| EPF | Execution plan parsing failed |  | 0 | active |


## Proposed (awaiting ≥3 uses to promote to active)

_(LLM adds rows here when a phrase repeats ≥3 times. After 3 uses in "Proposed", move the row to Active above.)_
