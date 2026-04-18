# Memory Optimization Guide

## Overview
This consolidated document combines memory leak prevention, analysis, and recommendations for the SQL Health Assessment application. Current memory usage: ~380-600MB working set, with identified optimizations for 38-48% reduction.

## ✅ Memory Leak Prevention Status

### Core Architecture is Solid
The application already implements proper memory management patterns:

#### 1. **Event Subscriptions** ✅
- `DynamicDashboard` properly unsubscribes from all events in `Dispose()`
- `AutoRefreshService` implements `IDisposable` and disposes timer
- `LiveDashboard`, `DeltaStatCard`, `ToastContainer` all implement `IDisposable`

#### 2. **DataTable Management** ✅
- Old DataTables disposed before replacement in `LoadData()`
- All DataTables disposed in `Dispose()` method
- Dictionaries cleared to release references

#### 3. **Database Connections** ✅
- All queries use `using` statements for SqlConnection/SqlCommand
- Connection pooling managed by `SqlConnectionPoolService`
- `ArrayPool<object>` used and properly returned in `QueryExecutor`

#### 4. **Timer Management** ✅
- `AutoRefreshService` properly disposes Timer in `Dispose()`
- Uses lock for thread-safe disposal

## ⚠️ Minor Improvements Recommended

### 1. **Cache Size Limits** (Low Priority)
`CachingQueryExecutor` has no max size - could grow unbounded over days/weeks.

**Quick Fix:**
```csharp
// In SqliteCacheStore or CachingQueryExecutor
private const int MAX_CACHE_ENTRIES = 10000;

if (_cache.Count > MAX_CACHE_ENTRIES)
{
    // Remove oldest 10%
    var toRemove = _cache.OrderBy(x => x.Value.Timestamp)
                         .Take(MAX_CACHE_ENTRIES / 10);
    foreach (var item in toRemove)
        _cache.TryRemove(item.Key, out _);
}
```

### 2. **ApexCharts JS Interop** (Check if needed)
Verify ApexCharts disposes JS objects when components unmount.

**Test:**
```csharp
// In TimeSeriesChart.razor
@implements IDisposable

public void Dispose()
{
    // If ApexCharts needs explicit cleanup:
    // await JS.InvokeVoidAsync("apexcharts.destroy", chartId);
}
```

## 📊 Memory Composition Analysis

### 1. **WebView2 / Chromium Engine** (~150-200MB baseline)
- **Impact:** HIGH - Largest single component
- **Cause:** Embedded Chromium browser (Microsoft.Web.WebView2)
- **Optimization:** Limited - this is baseline overhead for Blazor WebView

### 2. **ApexCharts Rendering** (~50-100MB)
- **Impact:** HIGH - Grows with data points
- **Cause:** TimeSeriesChart components with 1000+ data points per series
- **Current Throttling:** `MaxDataPoints = 1000` per chart
- **Recommendation:** ✅ Already optimized with throttling

### 3. **DataTable Objects** (~30-80MB)
- **Impact:** MEDIUM-HIGH - Not disposed properly
- **Cause:** `_gridResults` dictionary holds DataTable references
- **Current Issue:** Only disposed in `Dispose()` and `LoadData()` start
- **Fix:** ✅ Already implemented disposal in both locations

### 4. **SQLite Cache** (~20-50MB)
- **Impact:** MEDIUM - Grows over time
- **Cause:** `SqliteCacheStore` with 5000 row limit per query
- **Current Limits:**
  - TimeSeries: 5000 rows per query+instance
  - No size-based eviction (only time-based)
- **Recommendation:** Add size-based eviction

### 5. **ConcurrentDictionary Data** (~20-40MB)
- **Impact:** MEDIUM - Panel results cached in memory
- **Cause:**
  - `_timeSeriesResults` - List<TimeSeriesPoint> per panel
  - `_gridResults` - DataTable per panel
  - `_barGaugeResults` - List<StatValue> per panel
- **Current:** Cleared on dashboard switch
- **Recommendation:** ✅ Already optimized

### 6. **String Duplication** (~10-30MB)
- **Impact:** LOW-MEDIUM - Repeated strings not interned
- **Cause:** Server names, wait types, database names repeated across data
- **Current:** `StringInterningService` exists but **NOT USED**
- **Recommendation:** ⚠️ **CRITICAL FIX** - Wire up string interning

### 7. **ArrayPool Buffers** (~5-10MB)
- **Impact:** LOW - Reusable buffers
- **Cause:** `QueryExecutor` uses `ArrayPool<object>.Shared`
- **Recommendation:** ✅ Already optimized

### 8. **JSON Serialization** (~10-20MB)
- **Impact:** LOW-MEDIUM - Temporary allocations
- **Cause:** `SqliteCacheStore.SerializeDataTable()` uses streaming
- **Recommendation:** ✅ Already optimized with Utf8JsonWriter

## 🔴 Priority Fixes

### **CRITICAL: Wire Up String Interning** (Potential 20-40MB savings)

**Problem:** `StringInterningService` exists but is never injected or used.

**Fix:**
```csharp
// In QueryExecutor.cs - Add injection
private readonly StringInterningService? _stringInterning;

public QueryExecutor(..., StringInterningService? stringInterning = null)
{
    _stringInterning = stringInterning;
}

// In ExecuteQueryAsync - Intern strings during read
while (await reader.ReadAsync(cancellationToken))
{
    reader.GetValues(buffer);
    var row = dt.NewRow();
    for (int i = 0; i < fieldCount; i++)
    {
        var val = buffer[i];
        // Intern string columns
        if (val is string str && _stringInterning != null)
        {
            row[i] = _stringInterning.InternSqlValue(str);
        }
        else
        {
            row[i] = val is DBNull ? DBNull.Value : val;
        }
    }
    dt.Rows.Add(row);
}
```

**Impact:** 20-40MB reduction for repeated server names, wait types, database names

### **HIGH: Add Size-Based Cache Eviction** (Potential 30-50MB savings)

**Problem:** SQLite cache only evicts by time, not size. Can grow unbounded.

**Fix:**
```csharp
// In SqliteCacheStore.cs - Add to UpsertTimeSeriesAsync after transaction.Commit()
await EnforceSizeLimitAsync(maxSizeBytes: 50 * 1024 * 1024); // 50MB limit
```

**Impact:** Prevents cache from exceeding 50MB

### **HIGH: Reduce TimeSeriesChart Data Points** (Potential 20-30MB savings)

**Problem:** `MaxDataPoints = 1000` per chart × multiple series × multiple charts = high memory

**Current:**
```csharp
[Parameter] public int MaxDataPoints { get; set; } = 1000;
```

**Recommendation:**
```csharp
[Parameter] public int MaxDataPoints { get; set; } = 500; // Reduce by 50%
```

**Impact:** 20-30MB reduction with minimal visual quality loss

### **MEDIUM: Clear Adjusted Data Cache** (Potential 10-15MB savings)

**Problem:** `TimeSeriesChart._adjustedDataCache` never cleared, accumulates across dashboard switches

**Fix:**
```csharp
// In TimeSeriesChart.razor - Add IDisposable
@implements IDisposable

@code {
    public void Dispose()
    {
        _adjustedDataCache?.Clear();
        _adjustedDataCache = null;
        _seriesDataCache?.Clear();
        _seriesDataCache = null;
        _distinctSeries?.Clear();
        _distinctSeries = null;
    }
}
```

**Impact:** 10-15MB freed on dashboard navigation

### **MEDIUM: Dispose DynamicPanel QueryPlanModal** (Potential 5-10MB savings)

**Problem:** `_queryPlanModal` holds XML execution plans in memory

**Fix:**
```csharp
// In DynamicPanel.razor - Add IDisposable
@implements IDisposable

@code {
    public void Dispose()
    {
        _queryPlanModal?.Dispose(); // If QueryPlanModal implements IDisposable
    }
}
```

## 🧪 Testing for Memory Leaks

### Quick Test (5 minutes)
1. Open Task Manager → Details → SqlHealthAssessment.exe
2. Note "Memory (Private Working Set)"
3. Navigate between dashboards 20 times
4. Wait 30 seconds
5. Memory should stabilize (not keep growing)

### Expected Behavior
- **Initial load:** ~380 MB
- **After navigation:** ~450-500 MB (temporary spike)
- **After 30 sec:** Returns to ~400-420 MB (GC cleanup)
- **After 20 navigations:** Should NOT exceed ~500 MB

### Red Flags
- ❌ Memory grows by 50+ MB per navigation
- ❌ Memory never drops after navigation
- ❌ Memory exceeds 800 MB after 20 navigations

## 🎯 Implementation Priority

### **Phase 1: Quick Wins (30-40% reduction)**
1. Wire up `StringInterningService` in `QueryExecutor`
2. Add `EnforceSizeLimitAsync()` call after cache writes
3. Reduce `MaxDataPoints` from 1000 → 500
4. Add `IDisposable` to `TimeSeriesChart` and `DynamicPanel`

### **Phase 2: Medium Effort (20-30% reduction)**
1. Replace ApexCharts with ChartJs.Blazor
2. Implement object pooling for TimeSeriesPoint
3. Add memory pressure monitoring
4. Optimize LINQ queries (use `Span<T>` where possible)

### **Phase 3: Major Refactor (20-30% reduction)**
1. Implement virtual scrolling for DataGrid
2. Use streaming for large datasets
3. Implement progressive data loading
4. Add memory budget enforcement

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

## 📝 Best Practices Checklist

When adding new components:

- [ ] If subscribing to events → implement `IDisposable` and unsubscribe
- [ ] If using `@ref` to large objects → null them in `Dispose()`
- [ ] If using JS interop → check if cleanup needed
- [ ] If using Timer/PeriodicTimer → dispose in `Dispose()`
- [ ] If holding DataTable → dispose in `Dispose()`
- [ ] If using SqlConnection → wrap in `using` statement

## Expected Total Savings

| Optimization | Savings | Difficulty |
|---|---|---|
| String Interning | 20-40MB | Easy |
| Size-Based Cache Eviction | 30-50MB | Easy |
| Reduce MaxDataPoints | 20-30MB | Trivial |
| Clear Chart Caches | 10-15MB | Easy |
| Dispose QueryPlanModal | 5-10MB | Easy |
| **TOTAL** | **85-145MB** | **1-2 hours** |

**Target:** 380MB → **235-295MB** working set

## 🔗 Related Documents

- [MEMORY_LEAK_CHECKLIST.md](MEMORY_LEAK_CHECKLIST.md) - Detailed audit checklist
- [MEMORY_OPTIMIZATION_ANALYSIS.md](MEMORY_OPTIMIZATION_ANALYSIS.md) - Performance analysis
- [MEMORY_OPTIMIZATION_RECOMMENDATIONS.md](MEMORY_OPTIMIZATION_RECOMMENDATIONS.md) - Optimization guide