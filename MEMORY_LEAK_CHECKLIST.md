# Memory Leak Prevention Checklist

## ✅ Already Implemented

### 1. **IDisposable on Components with Event Subscriptions**
- ✅ `DynamicDashboard.razor` - Unsubscribes from RefreshService, ConfigService, InstanceSelector
- ✅ Disposes DataTables in `_gridResults`
- ✅ Clears all ConcurrentDictionaries

### 2. **DataTable Disposal**
- ✅ `LoadData()` disposes old DataTables before replacing
- ✅ `Dispose()` disposes all remaining DataTables

### 3. **Connection Pooling**
- ✅ Uses `using` statements for SqlConnection/SqlCommand
- ✅ SqlConnectionPoolService manages connection lifecycle

### 4. **ArrayPool for Query Results**
- ✅ `QueryExecutor.ExecuteQueryAsync` uses `ArrayPool<object>.Shared` with proper return

## ⚠️ Potential Issues to Check

### 1. **Event Handler Leaks**
Check these components for missing unsubscribe:
```bash
# Find all event subscriptions
findstr /S /C:"+=" *.razor *.cs | findstr "OnRefresh\|OnChanged\|OnClick"
```

### 2. **Timer/Periodic Service Leaks**
- ✅ `AutoRefreshService` - Uses `PeriodicTimer` (auto-disposed)
- ⚠️ Check if any components subscribe but don't unsubscribe

### 3. **Large Object Retention**
- ⚠️ `ConcurrentDictionary` entries never removed (grows unbounded)
- ⚠️ `_timeSeriesResults` can hold large datasets

### 4. **Blazor Component References**
- ⚠️ `@ref` fields should be nulled in Dispose if they hold large data

## 🔧 Quick Fixes Needed

### Fix 1: Add IDisposable to Components with Event Subscriptions
```csharp
@implements IDisposable

public void Dispose()
{
    ServiceWithEvent.OnEvent -= Handler;
}
```

### Fix 2: Null Large References in Dispose
```csharp
public void Dispose()
{
    _largeDataStructure?.Clear();
    _largeDataStructure = null;
}
```

### Fix 3: Limit Cache Size
```csharp
// In CachingQueryExecutor or similar
if (_cache.Count > MAX_ENTRIES)
{
    var oldest = _cache.OrderBy(x => x.Value.Timestamp).First();
    _cache.TryRemove(oldest.Key, out _);
}
```

## 🧪 Testing for Memory Leaks

### 1. **Visual Studio Diagnostic Tools**
```
Debug → Performance Profiler → .NET Object Allocation Tracking
```
- Navigate between dashboards 10 times
- Check if memory keeps growing

### 2. **Manual GC Test**
```csharp
// Add to test page
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
var mem = GC.GetTotalMemory(true) / 1024 / 1024;
Console.WriteLine($"Memory: {mem} MB");
```

### 3. **dotMemory (JetBrains)**
- Take snapshot before navigation
- Navigate between dashboards
- Take snapshot after
- Compare retained objects

## 📋 Components to Audit

Priority order:

1. ✅ **DynamicDashboard.razor** - Already has proper Dispose
2. ⚠️ **DynamicPanel.razor** - Check if needs Dispose
3. ⚠️ **TimeSeriesChart.razor** - ApexCharts JS interop
4. ⚠️ **DataGrid.razor** - Large DataTable references
5. ⚠️ **QueryPlanModal.razor** - JS interop for plan viewer
6. ⚠️ **SessionBubbleView.razor** - Real-time updates
7. ⚠️ **AutoRefreshService** - Timer disposal
8. ⚠️ **CachingQueryExecutor** - Unbounded cache growth

## 🎯 Action Items

### Immediate (Critical)
- [ ] Audit all `.razor` files for event subscriptions without unsubscribe
- [ ] Add size limits to `CachingQueryExecutor` cache
- [ ] Verify ApexCharts JS objects are disposed

### Short-term (Important)
- [ ] Add memory profiling to CI/CD
- [ ] Create automated memory leak test
- [ ] Document disposal patterns in CONTRIBUTING.md

### Long-term (Nice to have)
- [ ] Implement weak event pattern for cross-component events
- [ ] Add memory usage dashboard panel
- [ ] Set up continuous memory monitoring

## 🔍 Quick Audit Commands

```powershell
# Find all event subscriptions
Get-ChildItem -Recurse -Include *.razor,*.cs | Select-String "\+=" | Select-String "On[A-Z]"

# Find IDisposable implementations
Get-ChildItem -Recurse -Include *.razor,*.cs | Select-String "IDisposable"

# Find @ref usage (potential retention)
Get-ChildItem -Recurse -Include *.razor | Select-String "@ref"

# Find JS interop (potential leaks)
Get-ChildItem -Recurse -Include *.razor,*.cs | Select-String "IJSRuntime|InvokeAsync"
```
