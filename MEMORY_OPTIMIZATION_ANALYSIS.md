# Memory Optimization Analysis

**Current State:** 380MB working set, 600MB commit size after extended runtime

## Memory Composition Analysis

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

---

## Priority Fixes

### 🔴 **CRITICAL: Wire Up String Interning** (Potential 20-40MB savings)

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

---

### 🟡 **HIGH: Add Size-Based Cache Eviction** (Potential 30-50MB savings)

**Problem:** SQLite cache only evicts by time, not size. Can grow unbounded.

**Fix:**
```csharp
// In SqliteCacheStore.cs - Add to UpsertTimeSeriesAsync after transaction.Commit()
await EnforceSizeLimitAsync(maxSizeBytes: 50 * 1024 * 1024); // 50MB limit
```

**Impact:** Prevents cache from exceeding 50MB

---

### 🟡 **HIGH: Reduce TimeSeriesChart Data Points** (Potential 20-30MB savings)

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

---

### 🟢 **MEDIUM: Clear Adjusted Data Cache** (Potential 10-15MB savings)

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

---

### 🟢 **MEDIUM: Dispose DynamicPanel QueryPlanModal** (Potential 5-10MB savings)

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

---

### 🟢 **LOW: Reduce AutoRefresh Frequency** (Reduces churn, not size)

**Current:** 35 seconds (from appsettings.json)

**Recommendation:** Increase to 60 seconds for dashboards with heavy data

**Impact:** Reduces GC pressure, not direct memory savings

---

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

---

## Monitoring Recommendations

Add memory tracking to `MemoryMonitorService.cs`:

```csharp
public class MemoryStats
{
    public long WorkingSetMB { get; set; }
    public long PrivateMemoryMB { get; set; }
    public long ManagedMemoryMB { get; set; }
    public long Gen0Collections { get; set; }
    public long Gen1Collections { get; set; }
    public long Gen2Collections { get; set; }
    public int CachedDataTables { get; set; }
    public int CachedTimeSeries { get; set; }
    public long SqliteCacheSizeMB { get; set; }
}
```

Display in UI footer or Settings page.

---

## Long-Term Optimizations (Future)

1. **Virtual Scrolling for DataGrid** - Only render visible rows
2. **Lazy Load Charts** - Render charts on-demand when scrolled into view
3. **Compress TimeSeries in Cache** - Use delta encoding (already has `TimeSeriesDeltaCompressor.cs` - not used)
4. **Move to Server-Side Blazor** - Eliminates WebView2 overhead entirely (major refactor)

---

## Conclusion

**Immediate Action Items:**
1. ✅ Wire up `StringInterningService` in `QueryExecutor`
2. ✅ Add `EnforceSizeLimitAsync()` call after cache writes
3. ✅ Reduce `MaxDataPoints` from 1000 → 500
4. ✅ Add `IDisposable` to `TimeSeriesChart` and `DynamicPanel`

**Expected Result:** 235-295MB working set (38-48% reduction)
