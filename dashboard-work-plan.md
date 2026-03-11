# Dashboard Description Analysis - Work Plan

## Approach
1. Read dashboard-config.json in chunks (2-3 dashboards at a time)
2. For each dashboard:
   - Analyze panel SQL queries
   - Generate panel descriptions based on query purpose
   - Create dashboard summary
3. Check reference repos for missing dashboards
4. Output results to new config file

## Batch 1: SQLWATCH Dashboards (instance, longqueries, waitevents)
Status: READY TO START

## Batch 2: Master Dashboards (live, sessions, blocking, querystore)
Status: PENDING

## Batch 3: PerformanceMonitor Dashboards (pmemory, pmemory-analysis, pwaits, etc.)
Status: PENDING

## Batch 4: Review & Missing Dashboards
Status: PENDING

## Output Files
- dashboard-config-updated.json (main output)
- toadddashboard.json (new suggestions)
- dashboard-analysis-progress.md (tracking)
