# Quick Fix Reference

## What Was Fixed?

### ✓ Issue 1: Missing @TopRows Parameter
- **Error:** "Must declare the scalar variable @TopRows"
- **Fix:** Added @TopRows parameter to QueryExecutor
- **File:** Data/QueryExecutor.cs
- **Panels Affected:** live.sessions, live.blocking

### ✓ Issue 2: Missing Column Alias
- **Error:** "Column 'Value' does not belong to table"
- **Fix:** Added AS [Value] to buffer pool query
- **File:** Config/dashboard-config.json
- **Panel Affected:** pmemory.buffer_pool

### ✓ Issue 3: CTE Syntax Errors
- **Error:** "Incorrect syntax near the keyword 'with'"
- **Fix:** Added SET TRANSACTION ISOLATION LEVEL before CTEs
- **File:** Config/dashboard-config.json
- **Panels Affected:** 
  - waits.details
  - qs.topcpu
  - qs.topduration
  - qs.planvariation
  - qs.regressed

## How to Test?

1. **Build the application:**
   ```bash
   dotnet build
   ```

2. **Run the application:**
   ```bash
   dotnet run
   ```

3. **Test each dashboard:**
   - Live Monitor → Check sessions panel
   - Memory → Check buffer pool panel
   - Query Store → Check all panels
   - Wait Events → Check details panel

## Rollback if Needed

```bash
# Restore config backup
copy Config\dashboard-config.json.backup Config\dashboard-config.json

# Rebuild
dotnet build
```

## Still Need to Deploy?

If you see "Invalid object name" errors:

1. **Deploy SQLWATCH:**
   ```sql
   -- Run these scripts in order:
   SQLWATCH_db\01_CreateSQLWATCHDB.sql
   SQLWATCH_db\02_PostSQLWATCHDBcreate.sql
   ```

2. **Or use the app:**
   - Go to Database Deploy page
   - Click "Deploy SQLWATCH"

## Files Changed

- ✓ Data/QueryExecutor.cs (1 line added)
- ✓ Config/dashboard-config.json (6 queries fixed)
- ✓ Backup created: dashboard-config.json.backup

## Success Criteria

✓ No "@TopRows" errors  
✓ No "Column 'Value'" errors  
✓ No "Incorrect syntax near 'with'" errors  
✓ All dashboards load without SQL syntax errors  

## Need Help?

See detailed documentation:
- SQL_FIXES_SUMMARY.md - Complete implementation details
- LOG_ISSUES_ANALYSIS.md - Original analysis and root causes
