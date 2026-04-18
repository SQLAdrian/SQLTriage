<!-- In the name of God, the Merciful, the Compassionate -->
<!-- Bismillah ar-Rahman ar-Raheem -->

# Building a New Monitoring Application - Architecture Guide

## Overview
This document extracts the core architecture and functionality from the SQL Health Assessment (LiveMonitor) application to guide development of a new monitoring application in a different framework.

---

## Current Build Status (LiveMonitor v1.x)

### Implemented Features

**Phase 1: Foundation**
- [x] Configuration system (JSON-based) - `appsettings.json`, `dashboard-config.json`
- [x] Connection manager with encryption - `ConnectionManager.cs`, `CredentialProtector.cs`
- [x] Query executor with parameterized queries - `QueryExecutor.cs`, `SqlSafetyValidator.cs`
- [x] Basic dashboard config loader - `DashboardConfigService.cs`
- [x] WPF + Blazor WebView hybrid desktop framework

**Phase 2: Core Features**
- [x] Auto-refresh service - `AutoRefreshService.cs`
- [x] Dashboard filter (time range, instance selection) - `DashboardFilter.cs`
- [x] Panel rendering (StatCard, TimeSeries, DataGrid, BarGauge, DeltaStatCard)
- [x] Query throttling service - `QueryThrottleService.cs`, `RateLimiter.cs`
- [x] Error handling and logging - `LocalLogService.cs`, Serilog

**Phase 3: Caching & Resilience**
- [x] SQLite cache store - `SqliteCacheStore.cs`, `CachingQueryExecutor.cs`
- [x] Delta-fetch for time-series
- [x] Cache eviction service - `CacheEvictionService.cs`, `SqliteMaintenanceService.cs`
- [x] Cache state tracking - `CacheStateTracker.cs`

**Phase 4: Advanced Features**
- [x] Health check service - `HealthCheckService.cs`, `CheckExecutionService.cs`
- [x] Audit logging - `AuditLogService.cs`
- [x] Rate limiting - `RateLimiter.cs`
- [x] Multi-server discovery - `GlobalInstanceSelector.cs`
- [x] Color thresholds and formatting
- [ ] Panel maximize/restore
- [x] Keyboard shortcuts - `ShortcutsDialog.razor`

**Phase 5: Polish**
- [x] Dashboard editor - `DashboardEditor.razor`
- [ ] Export functionality
- [x] Memory optimization - `MemoryMonitorService.cs`
- [ ] Performance profiling
- [ ] Unit tests

### Build Information
- **Framework**: .NET 8.0 WPF + Blazor WebView
- **Build Output**: `bin/Release/net8.0-windows/SqlHealthAssessment.dll`
- **Build Status**: ✅ Succeeded (0 errors, 21 warnings)
- **Last Build Date**: 2026-03-06

### Recent Updates (March 2026)
- Dashboard Editor preview panel now uses 100% of usable space
- TimeSeries charts in preview mode rendered at half height for better visibility
- Added FileSystemWatcher to dashboard config for auto-reload on file changes
- Session Bubble View now uses X-Y grid layout (Logical Reads + Writes on X-axis, CPU Time on Y-axis) with log scale
- Added collision detection algorithm to prevent bubble overlap in Live Sessions view
- Toast notification system for user feedback

## Application Purpose
Real-time monitoring and health assessment tool for SQL Server instances with:
- Multi-server monitoring from a single interface
- Live dashboards with configurable panels
- Historical data analysis with time-series charts
- Health checks and alerting
- Offline resilience through local caching

---

## Core Architecture Pattern

### 1. Application Structure (3-Tier)

```
┌─────────────────────────────────────────────┐
│         Presentation Layer (UI)              │
│  - Dashboard pages with dynamic panels       │
│  - Real-time data visualization             │
│  - User interaction (filters, navigation)   │
└──────────────┬──────────────────────────────┘
               │
┌──────────────▼──────────────────────────────┐
│         Business Logic Layer                 │
│  - Query execution & caching                │
│  - Auto-refresh service                     │
│  - Health check service                     │
│  - Connection management                    │
│  - Configuration management                 │
└──────┬─────────────────┬────────────────────┘
       │                 │
┌──────▼──────┐  ┌───────▼──────────┐
│  Data Source│  │  Local Cache     │
│  (SQL Server│  │  (SQLite)        │
│   SQLWATCH) │  │  WAL-mode        │
└─────────────┘  └──────────────────┘
```

**Technology Stack (Original):**
- UI: WPF + Blazor WebView (hybrid desktop)
- Backend: .NET 8 C#
- Data: SQL Server (SQLWATCH framework) + SQLite cache
- Charts: ApexCharts via Blazor-ApexCharts
- Logging: Serilog

---

## Core Components to Implement

### 2. Configuration System

#### 2.1 Application Settings (appsettings.json)
```json
{
  "RefreshIntervalSeconds": 35,
  "QueryTimeoutSeconds": 60,
  "DataRetentionDays": 7,
  "MaxQueryRows": 2000,
  "RateLimiting": {
    "Enabled": true,
    "MaxQueriesPerMinute": 50
  },
  "SessionManagement": {
    "IdleTimeoutMinutes": 60
  }
}
```

#### 2.2 Dashboard Configuration (dashboard-config.json)
JSON-based configuration defining:
- **Dashboards**: Collection of related panels
- **Panels**: Individual visualization components
- **Queries**: SQL queries for each data source type

**Key Structure:**
```json
{
  "version": 1,
  "dashboards": [
    {
      "id": "instance",
      "title": "Instance Overview",
      "enabled": true,
      "showAllOption": false,
      "panels": [...]
    }
  ],
  "supportQueries": {...}
}
```

**Panel Types:**
- `TimeSeries`: Line/area/bar charts over time
- `StatCard`: Single numeric value with color thresholds
- `DeltaStatCard`: Rate-of-change calculation
- `BarGauge`: Horizontal bar gauges
- `DataGrid`: Tabular data
- `CheckStatus`: Health check status badges
- `TextCard`: Key-value display

---

### 3. Data Layer Architecture

#### 3.1 Connection Management (ServerConnectionManager)

**Responsibilities:**
- Store multiple server connections
- Encrypt credentials (DPAPI or equivalent)
- Track connection health
- Support multiple authentication types:
  - Windows Authentication
  - SQL Authentication
  - Azure AD / MFA

**Key Methods:**
```csharp
- GetConnections() → List<ServerConnection>
- GetConnection(id) → ServerConnection
- AddConnection(connection)
- UpdateConnection(connection)
- SetCurrentServer(serverId)
- CacheDiscoveryResults() // Performance optimization
```

**ServerConnection Model:**
```csharp
{
  Id: string (GUID)
  ServerNames: string (comma/newline separated)
  Database: string
  HasSqlWatch: bool
  AuthenticationType: enum
  Username: string
  Password: string (encrypted)
  TrustServerCertificate: bool
  IsEnabled: bool
  LastConnected: DateTime
}
```

#### 3.2 Query Execution (QueryExecutor)

**Core Pattern:**
```csharp
public async Task<DataTable> ExecuteQueryAsync(
    string queryId,
    DashboardFilter filter,
    Dictionary<string, object>? additionalParams,
    CancellationToken cancellationToken)
{
    // 1. Resolve query SQL from config
    var sql = _configService.GetQuery(queryId, dataSourceType);
    
    // 2. Create connection
    using var conn = _connectionFactory.CreateConnection();
    await conn.OpenAsync(cancellationToken);
    
    // 3. Build parameterized command
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    AddFilterParameters(cmd, filter);
    
    // 4. Execute with timeout
    using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    
    // 5. Load into DataTable (use ArrayPool for efficiency)
    return LoadDataTable(reader);
}
```

**Standard Filter Parameters:**
- `@TimeFrom`: DateTime
- `@TimeTo`: DateTime
- `@SqlInstance`: string (comma-separated for multi-instance)
- `@AggMin`: int (aggregation interval)

#### 3.3 Caching Layer (CachingQueryExecutor)

**Purpose:** Offline resilience + performance optimization

**Strategy by Panel Type:**

1. **TimeSeries Panels** - Delta Fetch:
   ```
   - Store last_fetch timestamp per query+instance
   - On refresh: fetch only rows newer than last_fetch
   - Merge delta into SQLite cache
   - Serve full time window from cache
   - On SQL failure: serve stale cache data
   ```

2. **StatCard/DataGrid** - Full Replace:
   ```
   - Always try SQL Server first
   - Cache the result
   - On SQL failure: serve cached value
   ```

**Cache Schema (SQLite):**
```sql
-- Metadata table
CREATE TABLE cache_metadata (
    query_id TEXT,
    instance_key TEXT,
    last_fetch_time TEXT,
    PRIMARY KEY (query_id, instance_key)
);

-- Time-series data
CREATE TABLE cache_timeseries (
    query_id TEXT,
    instance_key TEXT,
    time TEXT,
    series TEXT,
    value REAL,
    cached_at TEXT
);

-- Snapshot data (StatCard, DataGrid)
CREATE TABLE cache_snapshot (
    query_id TEXT,
    instance_key TEXT,
    data_json TEXT,
    cached_at TEXT
);
```

**Key Methods:**
```csharp
- PrepareRefreshCycle() // Detect filter changes, invalidate if needed
- GetLastFetchTimeAsync(queryId, instanceKey)
- UpsertTimeSeriesAsync(queryId, instanceKey, data)
- TrimTimeSeriesAsync(queryId, instanceKey, cutoffTime)
- EvictOlderThanAsync(threshold)
```

---

### 4. Service Layer

#### 4.1 AutoRefreshService
```csharp
public class AutoRefreshService
{
    private Timer _timer;
    public event Action OnRefresh;
    
    public void Start() {
        _timer = new Timer(_ => OnRefresh?.Invoke(), 
                          null, _intervalMs, _intervalMs);
    }
    
    public void SetInterval(int seconds) {
        _timer?.Change(seconds * 1000, seconds * 1000);
    }
}
```

#### 4.2 DashboardConfigService
```csharp
public class DashboardConfigService
{
    private DashboardConfigRoot _config;
    private Dictionary<string, QueryPair> _queryCache; // O(1) lookup
    
    public string GetQuery(string queryId, string dataSourceType) {
        if (_queryCache.TryGetValue(queryId, out var pair))
            return dataSourceType == "LiveQueries" 
                ? pair.LiveQueries 
                : pair.SqlServer;
    }
    
    public void Save() { /* Serialize to JSON */ }
    public void ResetToDefault() { /* Load defaults */ }
}
```

#### 4.3 QueryThrottleService
```csharp
public class QueryThrottleService
{
    private SemaphoreSlim _heavySemaphore = new(2, 2);  // 2 concurrent heavy
    private SemaphoreSlim _lightSemaphore = new(10, 10); // 10 concurrent light
    
    public async Task<T> ExecuteAsync<T>(
        Func<Task<T>> query, 
        bool isHeavy,
        CancellationToken ct)
    {
        var sem = isHeavy ? _heavySemaphore : _lightSemaphore;
        await sem.WaitAsync(ct);
        try {
            return await query();
        } finally {
            sem.Release();
        }
    }
}
```

#### 4.4 HealthCheckService
Execute health check queries and return status:
```csharp
public class HealthCheckService
{
    public async Task<List<CheckResult>> ExecuteChecksAsync(
        string serverId, 
        List<CheckConfiguration> checks)
    {
        var results = new List<CheckResult>();
        foreach (var check in checks) {
            var result = await ExecuteCheckAsync(check);
            results.Add(result);
        }
        return results;
    }
}
```

---

### 5. UI Layer (Dashboard Pattern)

#### 5.1 DynamicDashboard Component

**Responsibilities:**
- Load dashboard definition from config
- Manage instance selection dropdown
- Handle auto-refresh lifecycle
- Coordinate panel data loading
- Display loading states and errors

**Key Lifecycle:**
```csharp
protected override async Task OnInitializedAsync()
{
    // 1. Subscribe to events
    RefreshService.OnRefresh += OnAutoRefresh;
    ConfigService.OnConfigChanged += OnConfigChanged;
    
    // 2. Load dashboard config
    Dashboard = ConfigService.Config.Dashboards
        .FirstOrDefault(d => d.Id == DashboardId);
    
    // 3. Discover instances (cached after first load)
    if (!ConnectionManager.DiscoveryCompleted)
        await DiscoverAndUpdateInstancesAsync();
    
    // 4. Initial data load
    await LoadData();
    
    // 5. Start auto-refresh
    if (AutoRefresh) RefreshService.Start();
}

private async Task LoadData()
{
    // 1. Prepare cache for this refresh cycle
    await CachingExecutor.PrepareRefreshCycle(
        DashboardId, TimeRangeMinutes, SelectedInstance);
    
    // 2. Load all panels in parallel (with throttling)
    var tasks = _enabledPanels.Select(panel =>
        QueryThrottle.ExecuteAsync(async () =>
            await LoadPanelDataAsync(panel, filter),
            isHeavy: panel.PanelType == "TimeSeries"));
    
    await Task.WhenAll(tasks);
    
    // 3. Update UI
    StateHasChanged();
}
```

#### 5.2 DynamicPanel Component

**Responsibilities:**
- Render panel based on PanelType
- Apply color thresholds
- Format values
- Handle click events (for DataGrid)

**Panel Rendering Logic:**
```csharp
@switch (Panel.PanelType)
{
    case "TimeSeries":
        <TimeSeriesChart Data="@TimeSeriesData" 
                        ChartType="@Panel.ChartType" 
                        Height="@Panel.Height" />
        break;
    
    case "StatCard":
        <StatCard Value="@StatData.Value" 
                 Unit="@Panel.StatUnit"
                 Color="@ResolveColor(StatData.Value)" />
        break;
    
    case "BarGauge":
        <BarGauge Data="@BarGaugeData" 
                 Thresholds="@Panel.ColorThresholds" />
        break;
    
    case "DataGrid":
        <DataGrid Data="@GridData" 
                 MaxRows="@Panel.DataGridMaxRows"
                 IsClickable="@Panel.DataGridIsClickable" />
        break;
}
```

---

### 6. Data Models

#### 6.1 DashboardFilter
```csharp
public class DashboardFilter
{
    public DateTime TimeFrom { get; set; }
    public DateTime TimeTo { get; set; }
    public string[] Instances { get; set; }
    public string Database { get; set; }
    public int AggregationMinutes { get; set; }
}
```

#### 6.2 TimeSeriesPoint
```csharp
public class TimeSeriesPoint
{
    public DateTime Time { get; set; }
    public string Series { get; set; }  // Line name
    public double Value { get; set; }
}
```

#### 6.3 StatValue
```csharp
public class StatValue
{
    public string Label { get; set; }
    public double Value { get; set; }
    public string Unit { get; set; }
    public string Color { get; set; }
    public string Instance { get; set; }
}
```

#### 6.4 CheckStatus
```csharp
public class CheckStatus
{
    public string Status { get; set; }  // OK, WARNING, CRITICAL
    public int Count { get; set; }
}
```

---

### 7. Key Features to Implement

#### 7.1 Multi-Server Support
- Connection manager with encrypted credentials
- Instance dropdown on each dashboard
- "All Instances" aggregation option
- Per-server connection health tracking

#### 7.2 Offline Resilience
- SQLite cache with WAL mode
- Delta-fetch for time-series data
- Stale data banner when serving cache
- Automatic cache eviction (configurable threshold)

#### 7.3 Performance Optimizations
- Query throttling (separate pools for heavy/light queries)
- ArrayPool<T> for row reading
- Parallel panel loading with Task.WhenAll
- O(1) query lookup via dictionary cache
- Discovery result caching (avoid repeated SQL roundtrips)

#### 7.4 Rate Limiting
```csharp
public class RateLimiter
{
    private Queue<DateTime> _queryTimestamps = new();
    private int _maxQueriesPerMinute;
    
    public async Task<bool> TryAcquireAsync()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-1);
        while (_queryTimestamps.Count > 0 && 
               _queryTimestamps.Peek() < cutoff)
            _queryTimestamps.Dequeue();
        
        if (_queryTimestamps.Count >= _maxQueriesPerMinute)
            return false;
        
        _queryTimestamps.Enqueue(DateTime.UtcNow);
        return true;
    }
}
```

#### 7.5 Audit Logging
```csharp
public class AuditLogService
{
    public void LogQueryExecution(
        string queryId, 
        string server, 
        bool success, 
        TimeSpan duration, 
        int rowCount,
        string error = null)
    {
        // Write to audit log table with 90-day retention
    }
    
    public void LogApplicationStart() { }
    public void LogUserAction(string action, string details) { }
}
```

---

### 8. Dashboard Types to Implement

#### 8.1 Repository Dashboard
- Overview of all monitored servers
- Aggregate metrics (CPU, memory, disk)
- Health check status summary
- Instance count stat card

#### 8.2 Instance Overview Dashboard
- Detailed metrics for single instance
- CPU utilization time-series
- Memory usage time-series
- Disk space bar gauges
- Wait events by category
- File I/O throughput
- Blocking chains
- Session statistics

#### 8.3 Live Monitor Dashboard
- Real-time metrics (5-second refresh)
- Delta stat cards (batch requests/sec, transactions/sec)
- Active sessions grid
- Blocking chains grid
- Top resource-consuming queries

#### 8.4 Long Queries Dashboard
- Time-series of long-running query count
- Query details grid
- Query text display
- Execution plan viewer integration

#### 8.5 Wait Events Dashboard
- Wait events by category (area chart)
- Wait event details grid (filterable)
- Top wait types

---

### 9. Security Considerations

#### 9.1 Credential Protection
- Encrypt passwords using DPAPI (Windows) or equivalent
- Never store plain-text credentials
- Support for Windows Auth (no password storage)
- Support for Azure AD / MFA

#### 9.2 SQL Injection Prevention
- **Always use parameterized queries**
- Validate query IDs against whitelist
- No dynamic SQL construction from user input

#### 9.3 Connection String Security
```csharp
public string GetConnectionString(string serverName)
{
    var builder = new SqlConnectionStringBuilder
    {
        DataSource = serverName,
        InitialCatalog = Database,
        ConnectTimeout = ConnectionTimeout,
        TrustServerCertificate = TrustServerCertificate
    };
    
    if (UseWindowsAuth)
        builder.IntegratedSecurity = true;
    else {
        builder.UserID = Username;
        builder.Password = GetDecryptedPassword();
    }
    
    return builder.ConnectionString;
}
```

---

### 10. Implementation Checklist (Reference for Future Development)

> ✅ = Already implemented in current build  |  ☐ = Not yet implemented

#### Phase 1: Foundation
- [✅] Configuration system (JSON-based)
- [✅] Connection manager with encryption
- [✅] Query executor with parameterized queries
- [✅] Basic dashboard config loader
- [✅] WPF + Blazor WebView hybrid desktop framework

#### Phase 2: Core Features
- [✅] Auto-refresh service
- [✅] Dashboard filter (time range, instance selection)
- [✅] Panel rendering (StatCard, TimeSeries, DataGrid, BarGauge, DeltaStatCard)
- [✅] Query throttling service
- [✅] Error handling and logging

#### Phase 3: Caching & Resilience
- [✅] SQLite cache store
- [✅] Delta-fetch for time-series
- [✅] Cache eviction service
- [✅] Stale data detection and UI banner
- [✅] Cache invalidation on filter change

#### Phase 4: Advanced Features
- [✅] Health check service
- [✅] Audit logging
- [✅] Rate limiting
- [✅] Multi-server discovery
- [✅] Color thresholds and formatting
- [ ] Panel maximize/restore
- [✅] Keyboard shortcuts

#### Phase 5: Polish
- [✅] Theme support
- [✅] Dashboard editor
- [ ] Export functionality
- [✅] Memory optimization
- [ ] Performance profiling
- [ ] Unit tests

---

### 11. Query Pattern Examples

#### 11.1 Time-Series Query (SQL Server)
```sql
SELECT
    h.report_time AS [Time],
    m.counter_name AS [Series],
    d.cntr_value_calculated AS [Value]
FROM dbo.sqlwatch_logger_perf_os_performance_counters d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.sql_instance = d.sql_instance
INNER JOIN dbo.sqlwatch_meta_performance_counter m WITH (NOLOCK)
    ON m.sql_instance = d.sql_instance
    AND m.performance_counter_id = d.performance_counter_id
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
    AND m.counter_name IN ('Batch Requests/Sec', 'Transactions/sec')
ORDER BY h.report_time
```

#### 11.2 StatCard Query
```sql
SELECT TOP 1
    d.memory_utilization_percentage AS [Value]
FROM dbo.sqlwatch_logger_perf_os_process_memory d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_logger_snapshot_header h WITH (NOLOCK)
    ON h.snapshot_time = d.snapshot_time
    AND h.sql_instance = d.sql_instance
WHERE h.report_time BETWEEN @TimeFrom AND @TimeTo
    AND d.sql_instance IN (SELECT value FROM STRING_SPLIT(@SqlInstance, ','))
ORDER BY h.report_time DESC
```

#### 11.3 BarGauge Query
```sql
SELECT
    v.volume_name AS [Label],
    CAST((1.0 - (1.0 * d.volume_free_space_bytes / d.volume_total_space_bytes)) * 100.0 AS DECIMAL(5,1)) AS [Value],
    '%' AS [Unit],
    d.sql_instance AS [Instance]
FROM dbo.sqlwatch_logger_disk_utilisation_volume d WITH (NOLOCK)
INNER JOIN dbo.sqlwatch_meta_os_volume v WITH (NOLOCK)
    ON v.sql_instance = d.sql_instance
    AND v.sqlwatch_volume_id = d.sqlwatch_volume_id
WHERE d.snapshot_time = (SELECT MAX(snapshot_time) FROM dbo.sqlwatch_logger_snapshot_header)
```

---

### 12. Performance Targets

- **Dashboard Load Time**: < 2 seconds for initial load
- **Refresh Cycle**: < 1 second for incremental refresh
- **Memory Usage**: < 500 MB for typical workload
- **Query Timeout**: 60 seconds default (configurable)
- **Max Concurrent Queries**: 10 light + 2 heavy
- **Cache Size**: < 100 MB (configurable)
- **UI Responsiveness**: No blocking on main thread

---

### 13. Testing Strategy

#### Unit Tests
- Configuration loading/saving
- Query parameter building
- Cache key generation
- Color threshold evaluation
- Filter change detection

#### Integration Tests
- Query execution against test database
- Cache read/write operations
- Connection manager CRUD operations
- Dashboard config validation

#### Performance Tests
- Parallel panel loading
- Cache eviction under load
- Memory leak detection
- Query throttling effectiveness

---

### 14. Deployment Considerations

#### Desktop Application
- Self-contained executable (include runtime)
- Portable mode (all config in app directory)
- Auto-update mechanism
- Installer with prerequisites check

#### Configuration Files
- `appsettings.json` - Application settings
- `dashboard-config.json` - Dashboard definitions
- `server-connections.json` - Encrypted server list
- `user-settings.json` - User preferences

#### Database Requirements
- SQL Server 2016+ for data source
- SQLWATCH framework installed (or equivalent)
- Monitoring account with VIEW SERVER STATE permission

---

## Summary

This architecture provides:
1. **Scalability**: Multi-server monitoring from single interface
2. **Resilience**: Offline operation via local cache
3. **Performance**: Query throttling, parallel loading, delta-fetch
4. **Flexibility**: JSON-based configuration, pluggable panel types
5. **Security**: Encrypted credentials, parameterized queries, audit logging

The key innovation is the **caching layer with delta-fetch** for time-series data, which provides both performance optimization and offline resilience without complex state management.

---

# Avalonia Port Planning

## Project Goals

**Objective**: Create a modern, cross-platform version of SQL Health Assessment using Avalonia UI to compare with the WPF+Blazor implementation.

**Target Platforms**:
- ✅ Windows (primary)
- ✅ Linux (secondary)
- ✅ macOS (tertiary)

**Success Criteria**:
- Feature parity with WPF version
- Native look and feel on each platform
- Performance comparable or better
- Smaller binary size
- Easier deployment

---

## Avalonia vs WPF+Blazor Comparison

| Aspect | WPF + Blazor | Avalonia |
|--------|--------------|----------|
| **Platform** | Windows only | Cross-platform |
| **UI Framework** | Hybrid (WPF + Web) | Native XAML |
| **Rendering** | WebView2 (Chromium) | Skia (GPU-accelerated) |
| **Binary Size** | 50-80 MB | 30-50 MB |
| **Startup Time** | ~2-3 seconds | ~1-2 seconds |
| **Memory Usage** | 150-300 MB | 80-150 MB |
| **Charts** | ApexCharts (JS) | LiveCharts2 / ScottPlot |
| **Styling** | CSS | XAML Styles |
| **Hot Reload** | Limited | Excellent |
| **Learning Curve** | Medium (2 frameworks) | Low (XAML only) |
| **Maturity** | Very mature | Mature (v11+) |

---

## Pre-Development Preparations

### 1. Environment Setup

#### Required Tools
```bash
# Install .NET 8 SDK
winget install Microsoft.DotNet.SDK.8

# Install Avalonia templates
dotnet new install Avalonia.Templates

# Install Avalonia for Visual Studio (optional)
# VS Extension: Avalonia for Visual Studio

# Install JetBrains Rider (recommended for Avalonia)
# Better XAML intellisense and hot reload
```

#### Project Structure
```
SqlHealthAssessment.Avalonia/
├── SqlHealthAssessment.Avalonia.sln
├── src/
│   ├── SqlHealthAssessment.Avalonia/          # Main UI project
│   │   ├── Views/                             # XAML views
│   │   ├── ViewModels/                        # MVVM view models
│   │   ├── Controls/                          # Custom controls
│   │   ├── Converters/                        # Value converters
│   │   ├── Styles/                            # XAML styles
│   │   └── Assets/                            # Images, fonts
│   ├── SqlHealthAssessment.Core/              # Shared business logic
│   │   ├── Services/                          # Reuse from WPF version
│   │   ├── Models/                            # Reuse from WPF version
│   │   └── Data/                              # Reuse from WPF version
│   └── SqlHealthAssessment.Tests/             # Unit tests
└── docs/
    └── avalonia-migration-notes.md
```

### 2. Architecture Decisions

#### UI Pattern: MVVM (Model-View-ViewModel)

**Why MVVM for Avalonia:**
- Native pattern for XAML frameworks
- Better testability than code-behind
- Cleaner separation of concerns
- ReactiveUI integration (Avalonia's default)

**Comparison with Current WPF+Blazor:**
```
WPF+Blazor (Current)          Avalonia (New)
─────────────────────         ───────────────
Blazor Component              → ViewModel + View (XAML)
@code { }                     → ViewModel class
StateHasChanged()             → INotifyPropertyChanged / ReactiveUI
JavaScript interop            → Direct C# binding
ApexCharts                    → LiveCharts2 / ScottPlot
```

#### Charting Library Selection

**Option 1: LiveCharts2** (Recommended)
- ✅ Native Avalonia support
- ✅ MVVM-friendly
- ✅ Good performance
- ✅ Modern API
- ❌ Smaller feature set than ApexCharts

**Option 2: ScottPlot**
- ✅ Excellent performance
- ✅ Scientific plotting features
- ✅ Cross-platform
- ❌ Less "dashboard-like" styling
- ❌ More code required for interactivity

**Option 3: OxyPlot**
- ✅ Mature and stable
- ✅ Good documentation
- ❌ Older API design
- ❌ Less active development

**Decision: Use LiveCharts2** for dashboard-style charts with ScottPlot for specialized views.

### 3. Code Reuse Strategy

#### What to Reuse (90% of backend)

**Directly Reusable (No Changes):**
- ✅ `QueryExecutor.cs`
- ✅ `CachingQueryExecutor.cs`
- ✅ `SqliteCacheStore.cs`
- ✅ `ServerConnectionManager.cs`
- ✅ `DashboardConfigService.cs`
- ✅ `AutoRefreshService.cs`
- ✅ `QueryThrottleService.cs`
- ✅ `HealthCheckService.cs`
- ✅ `AuditLogService.cs`
- ✅ All Models (`ServerConnection`, `DashboardFilter`, etc.)

**Needs Adaptation:**
- ⚠️ `CredentialProtector.cs` - DPAPI is Windows-only, need cross-platform alternative
- ⚠️ `LocalLogService.cs` - Path handling for Linux/macOS

**Complete Rewrite:**
- ❌ All Blazor `.razor` components → Avalonia XAML Views + ViewModels
- ❌ `MainWindow.xaml` (WPF) → `MainWindow.axaml` (Avalonia)
- ❌ `App.xaml` (WPF) → `App.axaml` (Avalonia)

#### Shared Core Library

**Create `SqlHealthAssessment.Core` project:**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  
  <ItemGroup>
    <PackageReference Include="Microsoft.Data.SqlClient" Version="5.*" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="8.*" />
    <PackageReference Include="Serilog" Version="4.*" />
  </ItemGroup>
</Project>
```

Move all services and models to this shared project. Both WPF and Avalonia versions reference it.

### 4. Cross-Platform Considerations

#### Credential Storage

**Windows (DPAPI):**
```csharp
ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser)
```

**Linux/macOS Alternative:**
```csharp
// Use libsecret (Linux) or Keychain (macOS)
// NuGet: Tmds.DBus (Linux) or KeychainSharp (macOS)

// Or use cross-platform encryption:
public class CrossPlatformCredentialProtector
{
    public static string Encrypt(string plainText)
    {
        if (OperatingSystem.IsWindows())
            return EncryptWindows(plainText);
        else if (OperatingSystem.IsLinux())
            return EncryptLinux(plainText);
        else if (OperatingSystem.IsMacOS())
            return EncryptMacOS(plainText);
        else
            throw new PlatformNotSupportedException();
    }
}
```

#### File Paths

**Current (Windows-specific):**
```csharp
var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
```

**Cross-Platform:**
```csharp
var appData = Environment.GetFolderPath(
    Environment.SpecialFolder.ApplicationData,
    Environment.SpecialFolderOption.Create);
var appFolder = Path.Combine(appData, "SqlHealthAssessment");
var configPath = Path.Combine(appFolder, "config.json");
```

#### SQL Server Connectivity

**Linux/macOS Considerations:**
- ✅ `Microsoft.Data.SqlClient` works on all platforms
- ⚠️ Windows Authentication requires Kerberos setup on Linux
- ✅ SQL Authentication works everywhere
- ✅ Azure AD authentication works everywhere

### 5. Migration Roadmap

#### Phase 1: Foundation (Week 1-2)
- [ ] Create Avalonia project structure
- [ ] Extract shared code to `SqlHealthAssessment.Core`
- [ ] Implement cross-platform credential storage
- [ ] Create base MVVM infrastructure
- [ ] Setup dependency injection
- [ ] Port configuration system

#### Phase 2: Core UI (Week 3-4)
- [ ] Main window with navigation
- [ ] Server connection dialog
- [ ] Dashboard toolbar (time range, instance selector)
- [ ] Basic panel rendering (StatCard)
- [ ] LiveCharts2 integration
- [ ] Theme system (light/dark)

#### Phase 3: Dashboard Panels (Week 5-6)
- [ ] TimeSeries panel with LiveCharts2
- [ ] DataGrid panel
- [ ] BarGauge panel
- [ ] DeltaStatCard panel
- [ ] CheckStatus panel
- [ ] TextCard panel

#### Phase 4: Advanced Features (Week 7-8)
- [ ] Auto-refresh service integration
- [ ] Caching layer integration
- [ ] Health check execution
- [ ] Query plan viewer
- [ ] Session bubble view
- [ ] Dashboard editor

#### Phase 5: Polish & Testing (Week 9-10)
- [ ] Keyboard shortcuts
- [ ] Error handling and logging
- [ ] Performance optimization
- [ ] Cross-platform testing (Windows/Linux/macOS)
- [ ] Packaging and deployment
- [ ] Documentation

### 6. Key Technical Challenges

#### Challenge 1: Chart Migration (ApexCharts → LiveCharts2)

**Current ApexCharts Configuration:**
```javascript
{
  chart: { type: 'line', height: 300 },
  series: [{ name: 'CPU', data: [...] }],
  xaxis: { type: 'datetime' }
}
```

**Avalonia LiveCharts2 Equivalent:**
```csharp
public class TimeSeriesViewModel : ViewModelBase
{
    public ISeries[] Series { get; set; } = new ISeries[]
    {
        new LineSeries<TimeSeriesPoint>
        {
            Values = _data,
            Name = "CPU",
            Mapping = (point, index) => new(point.Time.Ticks, point.Value)
        }
    };
    
    public Axis[] XAxes { get; set; } = new Axis[]
    {
        new Axis { Labeler = value => new DateTime((long)value).ToString("HH:mm") }
    };
}
```

#### Challenge 2: Dynamic Panel System

**Current Blazor Approach:**
```razor
@switch (Panel.PanelType)
{
    case "TimeSeries":
        <TimeSeriesChart Data="@TimeSeriesData" />
        break;
    case "StatCard":
        <StatCard Value="@StatData.Value" />
        break;
}
```

**Avalonia MVVM Approach:**
```xml
<ContentControl Content="{Binding CurrentPanel}">
  <ContentControl.DataTemplates>
    <DataTemplate DataType="{x:Type vm:TimeSeriesViewModel}">
      <views:TimeSeriesView />
    </DataTemplate>
    <DataTemplate DataType="{x:Type vm:StatCardViewModel}">
      <views:StatCardView />
    </DataTemplate>
  </ContentControl.DataTemplates>
</ContentControl>
```

#### Challenge 3: Real-Time Updates

**Current Blazor:**
```csharp
await InvokeAsync(StateHasChanged);
```

**Avalonia ReactiveUI:**
```csharp
public class DashboardViewModel : ReactiveObject
{
    private double _cpuValue;
    public double CpuValue
    {
        get => _cpuValue;
        set => this.RaiseAndSetIfChanged(ref _cpuValue, value);
    }
    
    // Auto-refresh
    Observable.Timer(TimeSpan.Zero, TimeSpan.FromSeconds(5))
        .Subscribe(_ => RefreshData());
}
```

### 7. Development Tools & Resources

#### Recommended IDE
- **JetBrains Rider** (best Avalonia support)
- Visual Studio 2022 (good, but less XAML intellisense)
- VS Code with Avalonia extension (lightweight)

#### Essential NuGet Packages
```xml
<!-- UI Framework -->
<PackageReference Include="Avalonia" Version="11.*" />
<PackageReference Include="Avalonia.Desktop" Version="11.*" />
<PackageReference Include="Avalonia.Themes.Fluent" Version="11.*" />
<PackageReference Include="Avalonia.ReactiveUI" Version="11.*" />

<!-- Charts -->
<PackageReference Include="LiveChartsCore.SkiaSharpView.Avalonia" Version="2.*" />
<PackageReference Include="ScottPlot.Avalonia" Version="5.*" />

<!-- Data (reuse from Core) -->
<PackageReference Include="Microsoft.Data.SqlClient" Version="5.*" />
<PackageReference Include="Microsoft.Data.Sqlite" Version="8.*" />

<!-- Utilities -->
<PackageReference Include="Serilog.Sinks.File" Version="6.*" />
<PackageReference Include="System.Text.Json" Version="8.*" />
```

#### Learning Resources
- [Avalonia Docs](https://docs.avaloniaui.net/)
- [Avalonia Samples](https://github.com/AvaloniaUI/Avalonia.Samples)
- [LiveCharts2 Docs](https://livecharts.dev/)
- [ReactiveUI Docs](https://www.reactiveui.net/)

### 8. Testing Strategy

#### Unit Tests (Reuse from WPF)
- ✅ All service layer tests work as-is
- ✅ Model tests work as-is
- ✅ Configuration tests work as-is

#### UI Tests (New)
```csharp
[Fact]
public async Task StatCard_DisplaysCorrectValue()
{
    var vm = new StatCardViewModel { Value = 75.5, Unit = "%" };
    var window = new Window { Content = new StatCardView { DataContext = vm } };
    
    // Avalonia headless testing
    await window.ShowDialog(null);
    
    Assert.Equal("75.5%", vm.FormattedValue);
}
```

#### Cross-Platform Testing
```yaml
# GitHub Actions
strategy:
  matrix:
    os: [windows-latest, ubuntu-latest, macos-latest]
runs-on: ${{ matrix.os }}
steps:
  - run: dotnet test
```

### 9. Deployment

#### Single-File Executable
```bash
# Windows
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true

# Linux
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true

# macOS
dotnet publish -c Release -r osx-x64 --self-contained -p:PublishSingleFile=true
```

#### Binary Size Comparison
- WPF + Blazor: ~80 MB (Windows only)
- Avalonia: ~35 MB per platform

### 10. Success Metrics

**Performance:**
- [ ] Startup time < 2 seconds
- [ ] Dashboard load < 1 second
- [ ] Memory usage < 150 MB
- [ ] Smooth 60 FPS animations

**Functionality:**
- [ ] All dashboards working
- [ ] All panel types rendering
- [ ] Caching system functional
- [ ] Multi-server support
- [ ] Cross-platform compatibility

**Code Quality:**
- [ ] 80%+ code reuse from Core library
- [ ] Clean MVVM separation
- [ ] Unit test coverage > 70%
- [ ] No platform-specific hacks

---

## Next Steps

1. **Create prototype** (1-2 days)
   - Basic Avalonia window
   - Single StatCard panel
   - Connect to SQL Server
   - Verify cross-platform build

2. **Extract Core library** (2-3 days)
   - Move services to shared project
   - Fix platform-specific code
   - Update WPF version to use Core
   - Verify both versions still work

3. **Implement MVVM infrastructure** (3-4 days)
   - Base ViewModel class
   - Navigation service
   - Dependency injection
   - Configuration binding

4. **Port first dashboard** (5-7 days)
   - Repository dashboard
   - All panel types
   - Auto-refresh
   - Compare with WPF version

5. **Iterate and expand** (ongoing)
   - Port remaining dashboards
   - Add Avalonia-specific features
   - Performance optimization
   - Cross-platform testing

---

## Decision Log

| Date | Decision | Rationale |
|------|----------|----------|
| 2026-03-06 | Use Avalonia UI | Cross-platform, modern, XAML-based |
| 2026-03-06 | LiveCharts2 for charts | Native Avalonia support, MVVM-friendly |
| 2026-03-06 | Extract Core library | Maximize code reuse between WPF and Avalonia |
| 2026-03-06 | ReactiveUI for MVVM | Built into Avalonia, modern reactive patterns |
| TBD | Credential storage approach | Need to evaluate libsecret vs custom encryption |
| TBD | Packaging strategy | AppImage (Linux), DMG (macOS), MSI (Windows) |

---

## Questions to Resolve

- [ ] How to handle Windows-only features (DPAPI, SSPI auth)?
- [ ] Should we maintain feature parity or create platform-specific builds?
- [ ] What's the minimum supported OS versions? (Windows 10+, Ubuntu 20.04+, macOS 11+?)
- [ ] Do we need a web version (Avalonia.Browser)?
- [ ] How to handle SQL Server connection on Linux (Kerberos setup)?
- [ ] Should we bundle .NET runtime or require separate install?

---

## Avalonia Project Kickoff Checklist

- [ ] Install Avalonia templates
- [ ] Create solution structure
- [ ] Setup Git repository (separate or monorepo?)
- [ ] Create Core library project
- [ ] Create Avalonia UI project
- [ ] Setup CI/CD pipeline
- [ ] Document architecture decisions
- [ ] Create prototype with single panel
- [ ] Test on all target platforms
- [ ] Review and approve migration plan

