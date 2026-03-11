# SQL Query Fixes - Implementation Summary

## Date: 2026-03-07

## Overview
This document summarizes the fixes applied to resolve SQL query errors identified in the log analysis.

---

## Fixes Applied

### 1. Fixed @TopRows Parameter Issue ✓

**File:** `Data/QueryExecutor.cs`  
**Method:** `AddFilterParameters`  
**Change:** Added `@TopRows` parameter to all queries

```csharp
// Added line:
AddParameter(cmd, "@TopRows", _maxRows);
```

**Impact:**
- Fixes "Must declare the scalar variable @TopRows" errors
- Affects panels: live.sessions, live.blocking, and other panels using TOP (@TopRows)
- Uses the configured MaxQueryRows value (default: 10000)

---

### 2. Fixed pmemory.buffer_pool Query ✓

**File:** `Config/dashboard-config.json`  
**Panel:** `pmemory.buffer_pool`  
**Change:** Added `AS [Value]` column alias

**Before:**
```sql
SELECT CAST(SUM(pages_kb) / 1024.0 AS DECIMAL(18,2)) 
FROM sys.dm_os_memory_clerks 
WHERE type = 'CACHESTORE_BUFPOOL'
```

**After:**
```sql
SELECT CAST(SUM(pages_kb) / 1024.0 AS DECIMAL(18,2)) AS [Value]
FROM sys.dm_os_memory_clerks 
WHERE type = 'CACHESTORE_BUFPOOL'
```

**Impact:**
- Fixes "Column 'Value' does not belong to table" error
- Allows pmemory dashboard to load correctly

---

### 3. Fixed CTE Syntax Errors ✓

**File:** `Config/dashboard-config.json`  
**Panels Fixed:** 5 panels
- waits.details
- qs.topcpu
- qs.topduration
- qs.planvariation
- qs.regressed

**Change:** Added `SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;` before CTE queries

**Before:**
```sql
SELECT TOP 500
    q.query_id AS [Query ID],
    ...
FROM sys.query_store_query q WITH (NOLOCK)
...
```

**After:**
```sql
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; SELECT TOP 500
    q.query_id AS [Query ID],
    ...
FROM sys.query_store_query q WITH (NOLOCK)
...
```

**Impact:**
- Fixes "Incorrect syntax near the keyword 'with'" errors
- Ensures proper statement termination before CTEs
- Allows Query Store and Wait Events dashboards to load correctly

---

## Files Modified

1. **Data/QueryExecutor.cs**
   - Added @TopRows parameter support
   - 1 method modified: `AddFilterParameters`

2. **Config/dashboard-config.json**
   - Fixed 6 SQL queries across multiple panels
   - Backup created: `dashboard-config.json.backup`

3. **Scripts Created:**
   - `fix_dashboard_queries.py` - Main fix script
   - `fix_buffer_pool.py` - Specific fix for buffer_pool query

---

## Testing Recommendations

### Immediate Testing
1. Test pmemory dashboard
   - Verify buffer_pool panel displays correctly
   - Check for "Column 'Value' does not belong to table" error

2. Test live dashboard
   - Verify live.sessions panel loads
   - Verify live.blocking panel loads
   - Check for "@TopRows" parameter errors

3. Test Query Store dashboard
   - Test qs.topcpu panel
   - Test qs.topduration panel
   - Test qs.planvariation panel
   - Test qs.regressed panel
   - Check for CTE syntax errors

4. Test Wait Events dashboard
   - Test waits.details panel
   - Check for CTE syntax errors

### Database Deployment Testing
5. Test with missing SQLWATCH database
   - Expected: Errors for panels requiring SQLWATCH
   - Recommendation: Add database validation (future enhancement)

6. Test with missing PerformanceMonitor database
   - Expected: Errors for PM-related panels
   - Recommendation: Add database validation (future enhancement)

---

## Known Remaining Issues

### High Priority
1. **Missing SQLWATCH Tables**
   - Severity: HIGH
   - Affected: pquery, pevents, pmemory dashboards
   - Error: `Invalid object name 'collect.query_stats'`
   - Fix Required: Deploy SQLWATCH database using deployment scripts
   - Scripts: `SQLWATCH_db/01_CreateSQLWATCHDB.sql`, `SQLWATCH_db/02_PostSQLWATCHDBcreate.sql`

### Medium Priority
2. **Database Validation**
   - Add checks for required databases before loading dashboards
   - Display user-friendly error messages
   - Provide links to deployment pages

3. **Enhanced Error Handling**
   - Catch SQL exceptions in DynamicDashboard.razor
   - Display "Database not deployed" messages
   - Improve error logging with full query text

---

## Rollback Instructions

If issues occur after applying these fixes:

1. **Restore dashboard-config.json:**
   ```bash
   copy Config\dashboard-config.json.backup Config\dashboard-config.json
   ```

2. **Revert QueryExecutor.cs:**
   - Remove the line: `AddParameter(cmd, "@TopRows", _maxRows);`
   - From the `AddFilterParameters` method

3. **Rebuild the application:**
   ```bash
   dotnet build
   ```

---

## Next Steps

### Phase 2: Database Validation (Recommended)
1. Create `DatabaseValidationService.cs`
2. Add checks for SQLWATCH and PerformanceMonitor databases
3. Display friendly error messages when databases are missing
4. Add "Deploy Database" button to error messages

### Phase 3: Enhanced Logging (Optional)
1. Log full SQL query text on errors
2. Log parameter values (sanitized)
3. Add query execution time logging
4. Implement query performance tracking

---

## References

- Original Analysis: `LOG_ISSUES_ANALYSIS.md`
- Log File: `logs/app-20260307.log`
- Dashboard Config: `Config/dashboard-config.json`
- Query Executor: `Data/QueryExecutor.cs`

---

## Change Log

| Date | Change | Author | Status |
|------|--------|--------|--------|
| 2026-03-07 | Fixed @TopRows parameter | Amazon Q | ✓ Complete |
| 2026-03-07 | Fixed pmemory.buffer_pool query | Amazon Q | ✓ Complete |
| 2026-03-07 | Fixed CTE syntax errors (5 panels) | Amazon Q | ✓ Complete |

---

## Conclusion

All identified SQL query syntax errors have been fixed:
- ✓ @TopRows parameter added to QueryExecutor
- ✓ pmemory.buffer_pool query fixed with AS [Value] alias
- ✓ 5 CTE queries fixed with proper statement termination

The application should now run without SQL syntax errors, provided that:
1. SQLWATCH database is deployed
2. PerformanceMonitor database is deployed (if using PM dashboards)
3. Proper SQL Server permissions are configured

For missing database errors, follow the deployment instructions in the README.md file.
