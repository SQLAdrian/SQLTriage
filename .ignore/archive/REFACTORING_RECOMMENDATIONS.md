<!-- In the name of God, the Merciful, the Compassionate -->
<!-- Bismillah ar-Rahman ar-Raheem -->

# Refactoring Implementation Guide

> **For AI Assistants**: This document provides step-by-step instructions to refactor the SQL Health Assessment application. Each task is self-contained with clear inputs, outputs, and validation steps.

## Project Context

**Repository**: C:\GitHub\LiveMonitor
**Language**: C# (.NET 8)
**Current State**: 38 service files in Data/ folder with overlapping responsibilities
**Goal**: Reduce to 28 services by consolidating duplicate functionality

## Analysis Summary

**Current State:**
- 38 service files in Data layer
- Multiple overlapping responsibilities
- Some services doing similar things
- Opportunity to consolidate and simplify

---

## Implementation Tasks

> **Instructions for AI**: Execute tasks in order. Each task includes:
> - **Files to modify**: Exact file paths
> - **Changes required**: Specific code changes
> - **Validation**: How to verify success
> - **Rollback**: How to undo if needed

---

## 🔴 High Priority - Remove/Consolidate

### TASK 1: Merge Query-Related Services (Save 3 files)

**Estimated Time**: 2 hours  
**Risk Level**: Low  
**Prerequisites**: None

**Files Involved:**
- `C:\GitHub\LiveMonitor\Data\QueryExecutor.cs` (keep, rename)
- `C:\GitHub\LiveMonitor\Data\DashboardDataService.cs` (delete)
- `C:\GitHub\LiveMonitor\Data\QueryStore.cs` (check if exists, delete if unused)

**Step-by-Step Instructions:**

1. **Check if QueryStore.cs exists:**
   ```bash
   # Run in C:\GitHub\LiveMonitor
   dir Data\QueryStore.cs
   ```
   - If NOT FOUND: Skip to step 2
   - If FOUND: Search for usages:
     ```bash
     findstr /s /i "QueryStore" *.cs *.razor
     ```
   - If no usages found: Delete the file

2. **Find all usages of DashboardDataService:**
   ```bash
   findstr /s /i "DashboardDataService" *.cs *.razor
   ```
   
3. **For each usage found, replace with direct QueryExecutor call:**
   
   **Before:**
   ```csharp
   private readonly DashboardDataService _dataService;
   var instances = await _dataService.GetInstancesAsync();
   ```
   
   **After:**
   ```csharp
   private readonly QueryExecutor _queryExecutor;
   var dt = await _queryExecutor.ExecuteQueryAsync("instances.list", filter);
   var instances = dt.Rows.Cast<DataRow>()
       .Select(r => r["sql_instance"]?.ToString() ?? "")
       .ToArray();
   ```

4. **Remove DashboardDataService from DI registration:**
   - Open `C:\GitHub\LiveMonitor\App.xaml.cs`
   - Find line: `services.AddScoped<DashboardDataService>();`
   - Delete this line

5. **Delete the file:**
   ```bash
   del Data\DashboardDataService.cs
   ```

**Validation:**
```bash
# Build the project
dotnet build

# Should succeed with 0 errors
# Run the app and test dashboard loading
```

**Rollback:**
```bash
git checkout Data/DashboardDataService.cs
git checkout App.xaml.cs
```

**Impact:** -1 to -2 files, clearer API

---

---

### TASK 2: Remove LocalLogService (Save 1 file)

**Estimated Time**: 30 minutes  
**Risk Level**: Very Low  
**Prerequisites**: None

**Files Involved:**
- `C:\GitHub\LiveMonitor\Data\LocalLogService.cs` (delete)
- All files that use LocalLogService (modify)

**Step-by-Step Instructions:**

1. **Find all usages:**
   ```bash
   findstr /s /i "LocalLogService" *.cs *.razor
   ```

2. **Replace each usage with Serilog:**
   
   **Before:**
   ```csharp
   private readonly LocalLogService _logger;
   _logger.Log("Something happened");
   ```
   
   **After:**
   ```csharp
   using Serilog;
   Log.Information("Something happened");
   ```

3. **Remove from DI registration:**
   - Open `C:\GitHub\LiveMonitor\App.xaml.cs`
   - Find and delete: `services.AddSingleton<LocalLogService>();`

4. **Delete the file:**
   ```bash
   del Data\LocalLogService.cs
   ```

**Validation:**
```bash
dotnet build
# Check logs folder for Serilog output
dir logs
```

**Rollback:**
```bash
git checkout Data/LocalLogService.cs
git checkout App.xaml.cs
```

**Impact:** -1 file, standardized logging

---

### TASK 3: Remove SqlSafetyValidator (Save 1 file)
Ignore

---

### TASK 4: Consolidate Connection Factories (Optional)

**Estimated Time**: 1 hour  
**Risk Level**: Medium  
**Prerequisites**: Tasks 1-3 completed

---

**Files Involved:**
- `C:\GitHub\LiveMonitor\Data\IDbConnectionFactory.cs`
- `C:\GitHub\LiveMonitor\Data\SqlServerConnectionFactory.cs`
- `C:\GitHub\LiveMonitor\Data\SqliteConnectionFactory.cs`

**Decision Required:**
- **Option A**: Keep as-is (interface provides flexibility)
- **Option B**: Remove interface (simpler, but less flexible)

**Recommendation**: Keep as-is unless you need to reduce complexity further.

---

### TASK 5: Merge Session Services (Save 1 file)

**Estimated Time**: 2 hours  
**Risk Level**: Medium  
**Prerequisites**: Tasks 1-3 completed

**Files Involved:**
- `C:\GitHub\LiveMonitor\Data\SessionManager.cs` (keep, rename to SessionService.cs)
- `C:\GitHub\LiveMonitor\Data\SessionDataService.cs` (merge into SessionManager, then delete)

**Step-by-Step Instructions:**

1. **Read both files:**
   ```bash
   type Data\SessionManager.cs > session_merge.txt
   type Data\SessionDataService.cs >> session_merge.txt
   ```

2. **Create new SessionService.cs:**
   - Copy SessionManager.cs to SessionService.cs
   - Add all public methods from SessionDataService.cs
   - Merge constructor dependencies
   - Update class name to SessionService

3. **Update all usages:**
   ```bash
   # Find usages
   findstr /s /i "SessionManager\|SessionDataService" *.cs *.razor
   ```
   - Replace `SessionManager` → `SessionService`
   - Replace `SessionDataService` → `SessionService`

4. **Update DI registration in App.xaml.cs:**
   ```csharp
   // Before
   services.AddSingleton<SessionManager>();
   services.AddSingleton<SessionDataService>();
   
   // After
   services.AddSingleton<SessionService>();
   ```

5. **Delete old files:**
   ```bash
   del Data\SessionManager.cs
   del Data\SessionDataService.cs
   ```

**Validation:**
```bash
dotnet build
# Test session-related features in the app
```

**Rollback:**
```bash
git checkout Data/SessionManager.cs Data/SessionDataService.cs
git checkout App.xaml.cs
del Data/SessionService.cs
```

**Impact:** -1 file, clearer ownership

---

---

### TASK 6: Consolidate Check Services (Save 1 file)

**Estimated Time**: 2 hours  
**Risk Level**: Medium  
**Prerequisites**: Tasks 1-5 completed

**Files Involved:**
- `C:\GitHub\LiveMonitor\Data\HealthCheckService.cs` (keep, expand)
- `C:\GitHub\LiveMonitor\Data\CheckExecutionService.cs` (merge, then delete)
- `C:\GitHub\LiveMonitor\Data\CheckRepositoryService.cs` (evaluate)

**Step-by-Step Instructions:**

1. **Compare implementations:**
   ```bash
   type Data\HealthCheckService.cs > check_compare.txt
   type Data\CheckExecutionService.cs >> check_compare.txt
   type Data\CheckRepositoryService.cs >> check_compare.txt
   ```

2. **Identify duplicate methods:**
   - Look for methods that execute health checks
   - Determine which implementation is more complete

3. **Merge into HealthCheckService.cs:**
   - Keep the most complete implementation
   - Add any unique methods from other services
   - If CheckRepositoryService only loads config, merge it too

4. **Update all usages:**
   ```bash
   findstr /s /i "CheckExecutionService\|CheckRepositoryService" *.cs *.razor
   ```

5. **Update DI registration:**
   ```csharp
   // Before
   services.AddSingleton<HealthCheckService>();
   services.AddSingleton<CheckExecutionService>();
   services.AddSingleton<CheckRepositoryService>();
   
   // After
   services.AddSingleton<HealthCheckService>();
   ```

6. **Delete merged files:**
   ```bash
   del Data\CheckExecutionService.cs
   # Only if merged:
   del Data\CheckRepositoryService.cs
   ```

**Validation:**
```bash
dotnet build
# Test health check execution in the app
```

**Rollback:**
```bash
git checkout Data/HealthCheckService.cs Data/CheckExecutionService.cs Data/CheckRepositoryService.cs
git checkout App.xaml.cs
```

**Impact:** -1 to -2 files, no confusion

---

### TASK 7: Merge Throttling Services (Save 1 file)

**Estimated Time**: 1.5 hours  
**Risk Level**: High (performance-critical)  
**Prerequisites**: Tasks 1-6 completed

**Files Involved:**
- `C:\GitHub\LiveMonitor\Data\QueryThrottleService.cs` (keep, expand)
- `C:\GitHub\LiveMonitor\Data\RateLimiter.cs` (merge, then delete)

**Step-by-Step Instructions:**

1. **Read both implementations:**
   ```bash
   type Data\QueryThrottleService.cs > throttle_merge.txt
   type Data\RateLimiter.cs >> throttle_merge.txt
   ```

2. **Add RateLimiter logic to QueryThrottleService:**
   ```csharp
   public class QueryThrottleService
   {
       private SemaphoreSlim _heavySemaphore = new(2, 2);
       private SemaphoreSlim _lightSemaphore = new(10, 10);
       
       // Add from RateLimiter:
       private Queue<DateTime> _queryTimestamps = new();
       private int _maxQueriesPerMinute;
       
       public async Task<T> ExecuteAsync<T>(
           Func<Task<T>> query, 
           bool isHeavy,
           CancellationToken ct)
       {
           // Check rate limit first
           if (!await TryAcquireRateLimitAsync())
               throw new RateLimitExceededException();
           
           // Then apply semaphore throttling
           var sem = isHeavy ? _heavySemaphore : _lightSemaphore;
           await sem.WaitAsync(ct);
           try {
               return await query();
           } finally {
               sem.Release();
           }
       }
       
       private async Task<bool> TryAcquireRateLimitAsync()
       {
           // Copy logic from RateLimiter.cs
       }
   }
   ```

3. **Update all RateLimiter usages:**
   ```bash
   findstr /s /i "RateLimiter" *.cs *.razor
   ```
   - Replace with QueryThrottleService

4. **Update DI registration:**
   ```csharp
   // Before
   services.AddSingleton<QueryThrottleService>();
   services.AddSingleton<RateLimiter>();
   
   // After
   services.AddSingleton<QueryThrottleService>();
   ```

5. **Delete RateLimiter.cs:**
   ```bash
   del Data\RateLimiter.cs
   ```

**Validation:**
```bash
dotnet build
# IMPORTANT: Test under load
# - Open multiple dashboards
# - Verify throttling still works
# - Check for rate limit errors
```

**Rollback:**
```bash
git checkout Data/QueryThrottleService.cs Data/RateLimiter.cs
git checkout App.xaml.cs
```

**Impact:** -1 file, unified throttling

---

### TASK 8: Merge Deployment Services (Save 1 file)

**Estimated Time**: 1 hour  
**Risk Level**: Low  
**Prerequisites**: None (independent task)

**Files Involved:**
- `C:\GitHub\LiveMonitor\Data\Services\SqlWatchDeploymentService.cs` (keep, rename)
- `C:\GitHub\LiveMonitor\Data\Services\PMInstallationService.cs` (merge, then delete)

**Step-by-Step Instructions:**

1. **Create new DatabaseDeploymentService.cs:**
   ```csharp
   public class DatabaseDeploymentService
   {
       // From SqlWatchDeploymentService
       public async Task<bool> DeploySqlWatchAsync(string serverName) { }
       
       // From PMInstallationService
       public async Task<bool> DeployPerformanceMonitorAsync(string serverName) { }
       
       // Shared logic
       private async Task<bool> ExecuteDacpacAsync(string dacpacPath, string serverName) { }
   }
   ```

2. **Update all usages:**
   ```bash
   findstr /s /i "SqlWatchDeploymentService\|PMInstallationService" *.cs *.razor
   ```

3. **Update DI registration:**
   ```csharp
   // Before
   services.AddSingleton<SqlWatchDeploymentService>();
   services.AddSingleton<PMInstallationService>();
   
   // After
   services.AddSingleton<DatabaseDeploymentService>();
   ```

4. **Delete old files:**
   ```bash
   del Data\Services\SqlWatchDeploymentService.cs
   del Data\Services\PMInstallationService.cs
   ```

**Validation:**
```bash
dotnet build
# Test database deployment feature
```

**Rollback:**
```bash
git checkout Data/Services/SqlWatchDeploymentService.cs Data/Services/PMInstallationService.cs
git checkout App.xaml.cs
del Data/Services/DatabaseDeploymentService.cs
```

**Impact:** -1 file, reusable deployment code

---

## 🟡 Low Priority - Organize

### TASK 9: Reorganize Folder Structure

**Estimated Time**: 1 hour  
**Risk Level**: Low  
**Prerequisites**: All high-priority tasks completed

**Step-by-Step Instructions:**

1. **Create new folder structure:**
   ```bash
   cd C:\GitHub\LiveMonitor\Data
   mkdir Core
   mkdir Health
   mkdir Security
   mkdir Deployment
   ```

2. **Move files to appropriate folders:**
   ```bash
   # Core services
   move QueryExecutor.cs Core\
   move DashboardConfigService.cs Core\
   move ConfigurationValidator.cs Core\
   
   # Health services
   move HealthCheckService.cs Health\
   move AlertingService.cs Health\
   
   # Security services
   move CredentialProtector.cs Security\
   move AuditLogService.cs Security\
   
   # Deployment services
   move Services\DatabaseDeploymentService.cs Deployment\
   ```

3. **Update namespaces in moved files:**
   - Change `namespace SqlHealthAssessment.Data`
   - To `namespace SqlHealthAssessment.Data.Core` (etc.)

4. **Update using statements in all files:**
   ```bash
   # Find files that reference moved services
   findstr /s /i "using SqlHealthAssessment.Data;" *.cs
   ```
   - Add specific using statements for new namespaces

**Validation:**
```bash
dotnet build
# Should succeed with 0 errors
```

**Rollback:**
```bash
# Move files back to Data/ root
move Core\*.cs .
move Health\*.cs .
move Security\*.cs .
move Deployment\*.cs Services\
rmdir Core Health Security Deployment
```

**Impact:** Better organization, easier navigation

---

## Summary of Changes

### Files Removed (Total: -10)
- ✅ DashboardDataService.cs
- ✅ QueryStore.cs (if exists)
- ✅ LocalLogService.cs
- ✅ SqlSafetyValidator.cs
- ✅ SessionDataService.cs
- ✅ CheckExecutionService.cs
- ✅ CheckRepositoryService.cs (maybe)
- ✅ RateLimiter.cs
- ✅ SqlWatchDeploymentService.cs
- ✅ PMInstallationService.cs

### Files Created
- SessionService.cs (replaces SessionManager + SessionDataService)
- DatabaseDeploymentService.cs (replaces 2 deployment services)

### Net Result
- **Before**: 38 services
- **After**: 28 services
- **Reduction**: 26%

---

## Testing Checklist

After completing all tasks, verify:

- [ ] Application builds without errors
- [ ] All dashboards load correctly
- [ ] Query execution works
- [ ] Caching still functions
- [ ] Health checks execute
- [ ] Session management works
- [ ] Database deployment works
- [ ] Throttling prevents overload
- [ ] Logging outputs to files
- [ ] No performance regression

---

## Rollback All Changes

If something goes wrong:

```bash
cd C:\GitHub\LiveMonitor
git status
git checkout .
git clean -fd
```

This will restore all files to their original state.

---

## For AI Assistants: Task Execution Template

When executing a task, follow this pattern:

1. **Announce**: "Starting TASK X: [Task Name]"
2. **Read files**: Use fsRead to examine current code
3. **Plan changes**: Explain what will be modified
4. **Execute**: Make changes using fsReplace or fsWrite
5. **Validate**: Explain how to test
6. **Report**: "TASK X completed. Files modified: [list]"

**Example:**
```
Starting TASK 2: Remove LocalLogService

1. Reading LocalLogService.cs to understand implementation...
2. Searching for all usages in codebase...
3. Found 5 usages in: DashboardService.cs, QueryExecutor.cs, ...
4. Replacing each usage with Serilog.Log.Information()...
5. Removing DI registration from App.xaml.cs...
6. Deleting LocalLogService.cs...

TASK 2 completed. Files modified:
- DashboardService.cs
- QueryExecutor.cs
- App.xaml.cs
- Deleted: LocalLogService.cs

Next: Run 'dotnet build' to validate.
```

