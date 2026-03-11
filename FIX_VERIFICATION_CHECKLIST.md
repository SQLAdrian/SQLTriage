# Fix Verification Checklist

## Pre-Testing Setup

- [ ] Backup created: `Config/dashboard-config.json.backup` exists
- [ ] Application builds successfully: `dotnet build`
- [ ] No compilation errors
- [ ] SQL Server is running and accessible
- [ ] Connection configured in application

---

## Test 1: @TopRows Parameter Fix

### Live Monitor Dashboard - Sessions Panel

- [ ] Navigate to Live Monitor dashboard
- [ ] Locate "Active Sessions" panel
- [ ] Panel loads without errors
- [ ] No "@TopRows" error in logs
- [ ] Session data displays correctly
- [ ] Top N rows are limited (default 10000)

### Live Monitor Dashboard - Blocking Panel

- [ ] Locate "Blocking Chains" panel
- [ ] Panel loads without errors
- [ ] No "@TopRows" error in logs
- [ ] Blocking data displays (if any blocking exists)

**Expected Result:** ✓ No "Must declare the scalar variable @TopRows" errors

---

## Test 2: Buffer Pool Query Fix

### Memory Dashboard - Buffer Pool Panel

- [ ] Navigate to Memory (pmemory) dashboard
- [ ] Locate "Buffer Pool" panel
- [ ] Panel loads without errors
- [ ] No "Column 'Value'" error in logs
- [ ] Memory value displays correctly
- [ ] Value is in MB format
- [ ] Value is a decimal number

**Expected Result:** ✓ No "Column 'Value' does not belong to table" errors

---

## Test 3: CTE Syntax Fixes

### Wait Events Dashboard - Details Panel

- [ ] Navigate to Wait Events dashboard
- [ ] Locate "Wait Event Details" panel
- [ ] Panel loads without errors
- [ ] No "Incorrect syntax near 'with'" error
- [ ] Wait statistics display correctly
- [ ] Data grid shows wait categories

### Query Store Dashboard - All Panels

- [ ] Navigate to Query Store dashboard
- [ ] Test "Top CPU Queries" panel
  - [ ] Loads without errors
  - [ ] No CTE syntax errors
  - [ ] Query data displays
- [ ] Test "Top Duration Queries" panel
  - [ ] Loads without errors
  - [ ] No CTE syntax errors
  - [ ] Query data displays
- [ ] Test "High Plan Variation" panel
  - [ ] Loads without errors
  - [ ] No CTE syntax errors
  - [ ] Plan variation data displays
- [ ] Test "Regressed Queries" panel
  - [ ] Loads without errors
  - [ ] No CTE syntax errors
  - [ ] Regression data displays

**Expected Result:** ✓ No "Incorrect syntax near the keyword 'with'" errors

---

## Test 4: Log File Verification

### Check Application Logs

- [ ] Open latest log file: `logs/app-YYYYMMDD.log`
- [ ] Search for "@TopRows" errors → Should find NONE
- [ ] Search for "Column 'Value'" errors → Should find NONE
- [ ] Search for "Incorrect syntax near 'with'" → Should find NONE
- [ ] Search for "Must declare" errors → Should find NONE
- [ ] Verify successful query executions logged

**Expected Result:** ✓ No SQL syntax errors in logs

---

## Test 5: Dashboard Navigation

### Test All Dashboards

- [ ] Repository dashboard loads
- [ ] Instance Overview dashboard loads
- [ ] Live Monitor dashboard loads
- [ ] Wait Events dashboard loads
- [ ] Query Store dashboard loads
- [ ] Memory dashboard loads
- [ ] Performance Monitor dashboard loads
- [ ] Long Queries dashboard loads
- [ ] Sessions dashboard loads

**Expected Result:** ✓ All dashboards load without SQL syntax errors

---

## Test 6: Database Dependency Check

### SQLWATCH Database Required

If SQLWATCH is NOT deployed:
- [ ] Panels requiring SQLWATCH show appropriate errors
- [ ] Error messages are clear (not syntax errors)
- [ ] Application doesn't crash

If SQLWATCH IS deployed:
- [ ] All SQLWATCH-dependent panels load correctly
- [ ] No "Invalid object name" errors for SQLWATCH tables

### PerformanceMonitor Database Required

If PerformanceMonitor is NOT deployed:
- [ ] PM panels show appropriate errors
- [ ] Error messages are clear (not syntax errors)
- [ ] Application doesn't crash

If PerformanceMonitor IS deployed:
- [ ] All PM-dependent panels load correctly
- [ ] No "Invalid object name" errors for PM tables

---

## Test 7: Performance Check

### Query Execution

- [ ] Queries execute within reasonable time
- [ ] No timeout errors
- [ ] No excessive memory usage
- [ ] Application remains responsive

### Data Display

- [ ] Charts render correctly
- [ ] Data grids populate
- [ ] Stat cards show values
- [ ] Time series data displays

---

## Rollback Test (Optional)

### If Issues Found

- [ ] Stop application
- [ ] Restore backup: `copy Config\dashboard-config.json.backup Config\dashboard-config.json`
- [ ] Revert QueryExecutor.cs changes
- [ ] Rebuild: `dotnet build`
- [ ] Verify application runs with old configuration
- [ ] Document issues found

---

## Sign-Off

### Testing Completed By

- **Tester Name:** _________________
- **Date:** _________________
- **Environment:** _________________
- **SQL Server Version:** _________________

### Results Summary

- [ ] All tests passed
- [ ] Some tests failed (document below)
- [ ] Rollback required

### Issues Found (if any)

```
Issue 1:
Description:
Steps to reproduce:
Expected:
Actual:

Issue 2:
Description:
Steps to reproduce:
Expected:
Actual:
```

### Approval

- [ ] Fixes verified and approved for production
- [ ] Additional testing required
- [ ] Fixes need revision

**Approved By:** _________________  
**Date:** _________________

---

## Quick Reference

### Success Indicators
✓ No SQL syntax errors in logs  
✓ All panels load without errors  
✓ Data displays correctly  
✓ Application remains stable  

### Failure Indicators
✗ SQL syntax errors still present  
✗ Panels fail to load  
✗ Application crashes  
✗ Data doesn't display  

### Next Steps if Tests Fail
1. Check log files for specific errors
2. Verify database deployment status
3. Review SQL_FIXES_SUMMARY.md
4. Contact support if needed

---

## Additional Notes

_Use this space for any additional observations or comments during testing_

```
Notes:




```
