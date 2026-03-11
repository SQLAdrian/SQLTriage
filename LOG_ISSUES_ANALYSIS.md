# Log Issues Analysis & Fixes

## Date: 2026-03-07
## Log File: app-20260307.log

---

## Critical Issues Found

### 1. Missing SQLWATCH Tables (Multiple Dashboards)
**Severity:** HIGH  
**Affected Dashboards:** pquery, pevents, pmemory  
**Error:** `Invalid object name 'collect.query_stats'`, `collect.HealthParser_SevereErrors'`, etc.

**Root Cause:** SQLWATCH database tables are missing or not deployed properly.

**Fix Required:**
- Deploy SQLWATCH database using the deployment scripts
- Run: `SQLWATCH_db\01_CreateSQLWATCHDB.sql`
- Run: `SQLWATCH_db\02_PostSQLWATCHDBcreate.sql`

---

### 2. Missing @TopRows Parameter Declaration
**Severity:** HIGH  
**Affected Panels:** live.sessions, waits.details, querystore panels  
**Error:** `Must declare the scalar variable "@TopRows"`

**Root Cause:** SQL queries reference @TopRows parameter but it's not being passed or declared.

**Affected Queries:**
- `live.sessions` - Line in log: "Must declare the scalar variable \"@TopRows\""
- `waits.details` - Multiple syntax errors related to missing parameter
- `qs.topcpu`, `qs.topduration`, `qs.planvariation`, `qs.regressed`

**Fix Required:** The queries need @TopRows parameter to be properly declared and passed.

---

### 3. Column Mapping Error in pmemory.buffer_pool
**Severity:** MEDIUM  
**Error:** `Column 'Value' does not belong to table`

**Root Cause:** The SQL query for pmemory.buffer_pool panel returns data without a 'Value' column, but the code expects it.

**Current Query:**
```sql
SELECT CAST(SUM(pages_kb) / 1024.0 AS DECIMAL(18,2)) 
FROM sys.dm_os_memory_clerks 
WHERE type = 'CACHESTORE_BUFPOOL'
```

**Fix Required:** Add column alias:
```sql
SELECT CAST(SUM(pages_kb) / 1024.0 AS DECIMAL(18,2)) AS [Value]
FROM sys.dm_os_memory_clerks 
WHERE type = 'CACHESTORE_BUFPOOL'
```

---

### 4. SQL Syntax Errors - Missing Semicolons Before CTEs
**Severity:** HIGH  
**Error:** `Incorrect syntax near the keyword 'with'`

**Root Cause:** Common Table Expressions (CTEs) require the previous statement to be terminated with a semicolon.

**Affected Queries:**
- waits.details
- qs.topcpu
- qs.topduration
- qs.planvariation
- qs.regressed

**Fix Required:** Add semicolons before WITH clauses or add SET TRANSACTION ISOLATION LEVEL statements.

---

## Recommended Actions

### Immediate Fixes (Priority 1)

1. **Fix pmemory.buffer_pool query** - Add AS [Value] alias
2. **Deploy SQLWATCH database** - Run deployment scripts
3. **Fix @TopRows parameter handling** - Ensure parameter is passed to queries

### Short-term Fixes (Priority 2)

4. **Add semicolons to CTE queries** - Fix syntax errors in wait and query store dashboards
5. **Verify PerformanceMonitor database** - Ensure collect.* tables exist

### Long-term Improvements (Priority 3)

6. **Add better error handling** - Gracefully handle missing tables
7. **Add database validation** - Check for required tables before loading dashboards
8. **Improve logging** - Add more context to SQL errors

---

## Files Requiring Changes

1. `Config/dashboard-config.json` - Fix SQL queries with missing column aliases
2. `Data/QueryExecutor.cs` - Ensure @TopRows parameter is properly passed
3. `Components/Shared/DynamicDashboard.razor` - Improve error handling for missing tables

---

## Implementation Plan

### Phase 1: Fix SQL Query Issues (Immediate)

1. **Fix pmemory.buffer_pool query** - Add column alias
   - File: `Config/dashboard-config.json`
   - Line: Search for "pmemory.buffer_pool"
   - Change: Add `AS [Value]` to the SELECT statement

2. **Fix @TopRows parameter** - Add parameter to QueryExecutor
   - File: `Data/QueryExecutor.cs`
   - Method: `AddFilterParameters`
   - Add: `AddParameter(cmd, "@TopRows", 500);` // Default value

3. **Add semicolons before CTEs**
   - File: `Config/dashboard-config.json`
   - Panels: waits.details, qs.topcpu, qs.topduration, qs.planvariation, qs.regressed
   - Change: Add `;` before `WITH` clauses or add `SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;` at the start

### Phase 2: Database Validation (Short-term)

4. **Add database existence checks**
   - Create new service: `DatabaseValidationService.cs`
   - Check for SQLWATCH and PerformanceMonitor databases before loading dashboards
   - Display user-friendly error messages

5. **Improve error handling in DynamicDashboard.razor**
   - Catch SQL exceptions
   - Display "Database not deployed" message
   - Provide link to deployment page

### Phase 3: Enhanced Logging (Long-term)

6. **Add detailed SQL error logging**
   - Log full SQL query text on error
   - Log parameter values
   - Log connection string (sanitized)

## Testing Checklist

- [ ] Deploy SQLWATCH database
- [ ] Test pmemory dashboard
- [ ] Test live.sessions panel
- [ ] Test waits.details panel  
- [ ] Test querystore dashboard
- [ ] Test pevents dashboard
- [ ] Test pquery dashboard
- [ ] Verify no errors in logs after fixes
- [ ] Test with missing SQLWATCH database (should show friendly error)
- [ ] Test with missing PerformanceMonitor database (should show friendly error)

---

## Implementation Status

### Completed
- [x] Analysis of log issues
- [x] Root cause identification
- [x] Implementation plan created
- [x] Fix 1: pmemory.buffer_pool query - Added AS [Value] alias
- [x] Fix 2: @TopRows parameter - Added to QueryExecutor.AddFilterParameters
- [x] Fix 3: CTE semicolons - Added SET TRANSACTION ISOLATION LEVEL before CTEs

### In Progress
- [ ] Database validation service
- [ ] Enhanced error handling

### Pending
- [ ] Improved logging
- [ ] User-friendly error messages for missing databases

---

## Notes

- Most errors occur when switching between dashboards that require SQLWATCH
- The application handles missing data gracefully in some cases (repository dashboard works)
- Some panels are disabled (enabled: false) which prevents errors
- The @TopRows parameter is used in live.sessions and live.blocking queries
- CTE queries need proper statement termination to avoid syntax errors
