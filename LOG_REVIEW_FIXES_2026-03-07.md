# Log Review & Fixes - 2026-03-07

## Issues Found in Logs

### ✅ FIXED: pmemory.buffer_pool - DBNull Cast Error
**Error:** `Object cannot be cast from DBNull to other types.`  
**Location:** DynamicDashboard.razor:732  
**Root Cause:** Query returned NULL value, code tried to convert DBNull to double without checking  
**Fix Applied:** Added DBNull check before conversion:
```csharp
if (dt.Rows.Count > 0 && dt.Rows[0]["Value"] != DBNull.Value)
{
    var val = Convert.ToDouble(dt.Rows[0]["Value"]);
    // ...
}
```

### ⚠️ EXPECTED: PerformanceMonitor Database Tables Missing
**Errors:** Multiple "Invalid object name" errors for:
- `collect.default_trace_events`
- `collect.HealthParser_SystemHealth`
- `collect.HealthParser_SevereErrors`
- `report.server_configuration_changes`
- `collect.blocking_BlockedProcessReport`
- `report.expensive_queries_today`
- `collect.query_stats`

**Status:** These are EXPECTED errors - PerformanceMonitor database is not deployed  
**Action Required:** Deploy PerformanceMonitor database if you want to use these dashboards  
**Affected Dashboards:**
- pevents (Performance Events)
- pquery (Query Performance)
- pmemory_analysis (Memory Analysis)

## Summary of All Fixes Applied Today

### 1. SQL Query Syntax Fixes
- ✅ Fixed @TopRows parameter (QueryExecutor.cs)
- ✅ Fixed pmemory.buffer_pool query (added AS [Value] alias)
- ✅ Fixed 5 CTE queries (added SET TRANSACTION ISOLATION LEVEL)

### 2. Razor Component Fixes
- ✅ Fixed DashboardEditor.razor (@onchange conflicts with @bind)

### 3. Runtime Error Fixes
- ✅ Fixed DBNull cast error in DynamicDashboard.razor

## Test Results

### ✅ Working Dashboards
- Repository
- Instance Overview
- Long Queries
- Wait Events
- Live Monitor
- Query Store
- Memory (pmemory) - now fixed!

### ⚠️ Dashboards with Expected Errors (Missing PerformanceMonitor DB)
- Performance Events (pevents)
- Query Performance (pquery)
- Memory Analysis (pmemory_analysis)

## Recommendations

### Immediate
1. ✅ All SQL syntax errors are fixed
2. ✅ All runtime errors are fixed
3. ✅ Application is stable and working

### Optional
1. Deploy PerformanceMonitor database if you need:
   - Performance event tracking
   - Query performance analysis
   - Advanced memory analysis
   
2. The application gracefully handles missing databases - panels show warnings but don't crash

## Files Modified

1. `Data/QueryExecutor.cs` - Added @TopRows parameter
2. `Config/dashboard-config.json` - Fixed 6 SQL queries
3. `Pages/DashboardEditor.razor` - Removed @onchange conflicts
4. `Components/Shared/DynamicDashboard.razor` - Added DBNull check

## Build Status

✅ **Build:** SUCCESS  
⚠️ **Warnings:** 13 (nullable reference warnings - non-critical)  
❌ **Errors:** 0  

## Conclusion

All critical issues have been resolved. The application is now stable and working correctly. The remaining errors in the logs are expected and relate to optional PerformanceMonitor database tables that are not deployed.

The application handles these gracefully by:
- Logging the errors
- Showing warnings in the UI
- Continuing to function normally for other dashboards
