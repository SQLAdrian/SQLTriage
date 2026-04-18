<!-- In the name of God, the Merciful, the Compassionate -->
<!-- Bismillah ar-Rahman ar-Raheem -->

# Simplest Native Windows Implementation

## Recommended Approach: .NET 8 WPF + WebView2

**Why this is simplest for Windows Server 2016+:**
- Single executable deployment
- Native Windows performance
- Built-in WebView2 for modern UI
- No IIS/web server required
- Works offline by default
- .NET 8 runtime is small and stable

---

## Technology Stack

```
┌─────────────────────────────────────┐
│   WPF Window (MainWindow.xaml)      │
│   ┌─────────────────────────────┐   │
│   │  WebView2 Control           │   │
│   │  (HTML/CSS/JavaScript UI)   │   │
│   │  - Chart.js for charts      │   │
│   │  - Vanilla JS (no framework)│   │
│   └─────────────────────────────┘   │
│                                     │
│   C# Backend Services               │
│   - Query execution                 │
│   - SQLite caching                  │
│   - JSON API for WebView2           │
└─────────────────────────────────────┘
```

**Components:**
- **WPF**: Native Windows window host
- **WebView2**: Embedded Chromium for UI (HTML/CSS/JS)
- **.NET 8**: Backend logic
- **SQLite**: Local cache (single file)
- **Chart.js**: Simple charting library
- **No web framework**: Pure HTML/JS

---

## Project Structure

```
MyMonitorApp/
├── MyMonitorApp.csproj          # .NET 8 WPF project
├── MainWindow.xaml              # WPF window with WebView2
├── MainWindow.xaml.cs           # Window code-behind
├── App.xaml.cs                  # Application startup
├── appsettings.json             # Configuration
├── Services/
│   ├── QueryService.cs          # SQL query execution
│   ├── CacheService.cs          # SQLite caching
│   ├── ConfigService.cs         # Config management
│   └── WebApiService.cs         # JSON API for WebView2
├── Models/
│   ├── ServerConnection.cs
│   ├── DashboardConfig.cs
│   └── DataModels.cs
└── wwwroot/                     # Web UI files
    ├── index.html               # Main UI
    ├── app.js                   # JavaScript logic
    ├── style.css                # Styling
    └── chart.min.js             # Chart.js library
```

---

## Minimal Implementation Steps

### Step 1: Create WPF Project

```xml
<!-- MyMonitorApp.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Web.WebView2" Version="1.0.*" />
    <PackageReference Include="Microsoft.Data.SqlClient" Version="5.*" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="8.*" />
    <PackageReference Include="System.Text.Json" Version="8.*" />
  </ItemGroup>

  <ItemGroup>
    <None Update="wwwroot\**\*" CopyToOutputDirectory="PreserveNewest" />
    <None Update="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

### Step 2: Main Window (WPF Host)

```xml
<!-- MainWindow.xaml -->
<Window x:Class="MyMonitorApp.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:wv2="clr-namespace:Microsoft.Web.WebView2.Wpf;assembly=Microsoft.Web.WebView2.Wpf"
        Title="SQL Monitor" Height="800" Width="1400"
        WindowState="Maximized">
    <Grid>
        <wv2:WebView2 Name="webView" />
    </Grid>
</Window>
```

```csharp
// MainWindow.xaml.cs
using Microsoft.Web.WebView2.Core;
using System.Windows;

namespace MyMonitorApp
{
    public partial class MainWindow : Window
    {
        private WebApiService _api;

        public MainWindow()
        {
            InitializeComponent();
            InitializeAsync();
        }

        private async void InitializeAsync()
        {
            await webView.EnsureCoreWebView2Async();
            
            // Expose C# API to JavaScript
            _api = new WebApiService();
            webView.CoreWebView2.AddHostObjectToScript("api", _api);
            
            // Load UI
            var htmlPath = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, 
                "wwwroot", "index.html");
            webView.CoreWebView2.Navigate(htmlPath);
        }
    }
}
```

### Step 3: Backend Services

```csharp
// Services/QueryService.cs
using Microsoft.Data.SqlClient;
using System.Data;

public class QueryService
{
    private readonly string _connectionString;

    public QueryService(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<DataTable> ExecuteQueryAsync(string sql, Dictionary<string, object> parameters)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        
        using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = 60;
        
        foreach (var param in parameters)
            cmd.Parameters.AddWithValue(param.Key, param.Value);
        
        var dt = new DataTable();
        using var reader = await cmd.ExecuteReaderAsync();
        dt.Load(reader);
        return dt;
    }
}
```

```csharp
// Services/CacheService.cs
using Microsoft.Data.Sqlite;

public class CacheService
{
    private readonly string _dbPath;

    public CacheService()
    {
        _dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyMonitorApp", "cache.db");
        
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath));
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS cache_data (
                query_id TEXT,
                instance TEXT,
                data_json TEXT,
                cached_at TEXT,
                PRIMARY KEY (query_id, instance)
            )";
        cmd.ExecuteNonQuery();
    }

    public async Task<string> GetCachedDataAsync(string queryId, string instance)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data_json FROM cache_data WHERE query_id = @id AND instance = @inst";
        cmd.Parameters.AddWithValue("@id", queryId);
        cmd.Parameters.AddWithValue("@inst", instance);
        
        var result = await cmd.ExecuteScalarAsync();
        return result?.ToString();
    }

    public async Task SetCachedDataAsync(string queryId, string instance, string json)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO cache_data (query_id, instance, data_json, cached_at)
            VALUES (@id, @inst, @json, @time)";
        cmd.Parameters.AddWithValue("@id", queryId);
        cmd.Parameters.AddWithValue("@inst", instance);
        cmd.Parameters.AddWithValue("@json", json);
        cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("o"));
        
        await cmd.ExecuteNonQueryAsync();
    }
}
```

```csharp
// Services/WebApiService.cs
using System.Runtime.InteropServices;
using System.Text.Json;

[ClassInterface(ClassInterfaceType.AutoDual)]
[ComVisible(true)]
public class WebApiService
{
    private readonly QueryService _queryService;
    private readonly CacheService _cacheService;

    public WebApiService()
    {
        var connString = "Server=.;Database=SQLWATCH;Integrated Security=true;";
        _queryService = new QueryService(connString);
        _cacheService = new CacheService();
    }

    public string ExecuteQuery(string queryId, string paramsJson)
    {
        try
        {
            var parameters = JsonSerializer.Deserialize<Dictionary<string, object>>(paramsJson);
            var sql = GetQuerySql(queryId); // Load from config
            
            var result = _queryService.ExecuteQueryAsync(sql, parameters).Result;
            var json = DataTableToJson(result);
            
            // Cache the result
            _cacheService.SetCachedDataAsync(queryId, parameters["@SqlInstance"].ToString(), json).Wait();
            
            return json;
        }
        catch (Exception ex)
        {
            // Try cache on failure
            var cached = _cacheService.GetCachedDataAsync(queryId, "default").Result;
            if (cached != null)
                return cached;
            
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private string DataTableToJson(DataTable dt)
    {
        var rows = new List<Dictionary<string, object>>();
        foreach (DataRow row in dt.Rows)
        {
            var dict = new Dictionary<string, object>();
            foreach (DataColumn col in dt.Columns)
                dict[col.ColumnName] = row[col];
            rows.Add(dict);
        }
        return JsonSerializer.Serialize(rows);
    }

    private string GetQuerySql(string queryId)
    {
        // Load from dashboard-config.json
        var queries = new Dictionary<string, string>
        {
            ["cpu"] = "SELECT TOP 1 CONVERT(XML,record).value('(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]','int') AS Value FROM sys.dm_os_ring_buffers WHERE ring_buffer_type='RING_BUFFER_SCHEDULER_MONITOR' ORDER BY timestamp DESC",
            ["memory"] = "SELECT TOP 1 memory_utilization_percentage AS Value FROM sys.dm_os_process_memory"
        };
        return queries[queryId];
    }
}
```

### Step 4: Web UI (HTML/JS)

```html
<!-- wwwroot/index.html -->
<!DOCTYPE html>
<html>
<head>
    <meta charset="utf-8">
    <title>SQL Monitor</title>
    <link rel="stylesheet" href="style.css">
    <script src="chart.min.js"></script>
</head>
<body>
    <div class="header">
        <h1>SQL Server Monitor</h1>
        <button onclick="refresh()">Refresh</button>
    </div>

    <div class="dashboard">
        <div class="stat-card">
            <h3>CPU %</h3>
            <div id="cpu-value" class="stat-value">--</div>
        </div>
        
        <div class="stat-card">
            <h3>Memory %</h3>
            <div id="memory-value" class="stat-value">--</div>
        </div>

        <div class="chart-card">
            <h3>CPU History</h3>
            <canvas id="cpu-chart"></canvas>
        </div>
    </div>

    <script src="app.js"></script>
</body>
</html>
```

```javascript
// wwwroot/app.js
let cpuChart;

async function executeQuery(queryId, params) {
    const paramsJson = JSON.stringify(params);
    const resultJson = await chrome.webview.hostObjects.api.ExecuteQuery(queryId, paramsJson);
    return JSON.parse(resultJson);
}

async function loadCpuStat() {
    const data = await executeQuery('cpu', { '@SqlInstance': '.' });
    if (data.length > 0) {
        document.getElementById('cpu-value').textContent = data[0].Value + '%';
    }
}

async function loadMemoryStat() {
    const data = await executeQuery('memory', { '@SqlInstance': '.' });
    if (data.length > 0) {
        document.getElementById('memory-value').textContent = data[0].Value + '%';
    }
}

function initCharts() {
    const ctx = document.getElementById('cpu-chart').getContext('2d');
    cpuChart = new Chart(ctx, {
        type: 'line',
        data: {
            labels: [],
            datasets: [{
                label: 'CPU %',
                data: [],
                borderColor: '#2196f3',
                tension: 0.1
            }]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false
        }
    });
}

async function refresh() {
    await loadCpuStat();
    await loadMemoryStat();
}

// Initialize
window.addEventListener('load', () => {
    initCharts();
    refresh();
    setInterval(refresh, 5000); // Auto-refresh every 5 seconds
});
```

```css
/* wwwroot/style.css */
* { margin: 0; padding: 0; box-sizing: border-box; }

body {
    font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;
    background: #1e1e1e;
    color: #fff;
}

.header {
    background: #252526;
    padding: 15px 20px;
    display: flex;
    justify-content: space-between;
    align-items: center;
    border-bottom: 1px solid #3e3e42;
}

.header h1 { font-size: 20px; }

button {
    background: #0e639c;
    color: #fff;
    border: none;
    padding: 8px 16px;
    border-radius: 4px;
    cursor: pointer;
}

button:hover { background: #1177bb; }

.dashboard {
    padding: 20px;
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(300px, 1fr));
    gap: 20px;
}

.stat-card, .chart-card {
    background: #252526;
    border: 1px solid #3e3e42;
    border-radius: 4px;
    padding: 20px;
}

.stat-value {
    font-size: 48px;
    font-weight: bold;
    color: #2196f3;
    margin-top: 10px;
}

.chart-card canvas {
    height: 300px !important;
}
```

---

## Build & Deploy

### Build Single Executable

```powershell
# Build self-contained single-file executable
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

# Output: bin\Release\net8.0-windows\win-x64\publish\MyMonitorApp.exe
```

### Deploy to Windows Server 2016+

1. Copy `MyMonitorApp.exe` to server
2. Copy `appsettings.json` to same folder
3. Run `MyMonitorApp.exe`

**No installation required!**

---

## Advantages of This Approach

✅ **Single EXE**: Everything bundled (except .NET runtime if not self-contained)  
✅ **Native Performance**: WPF is native Windows  
✅ **Modern UI**: WebView2 = Chromium engine  
✅ **Offline First**: SQLite cache built-in  
✅ **No Web Server**: No IIS, no ports, no firewall rules  
✅ **Simple Deployment**: Copy and run  
✅ **Windows Server 2016+ Compatible**: .NET 8 supports it  
✅ **Small Size**: ~50-80 MB self-contained  

---

## Alternative: Even Simpler (Console + HTML Report)

If you want **absolute simplest**:

```csharp
// Program.cs - Console app that generates HTML report
using Microsoft.Data.SqlClient;
using System.Text;

var conn = new SqlConnection("Server=.;Database=master;Integrated Security=true;");
await conn.OpenAsync();

var html = new StringBuilder();
html.Append("<html><body><h1>SQL Server Report</h1>");

// CPU
var cmd = new SqlCommand("SELECT TOP 1 CONVERT(XML,record).value('(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]','int') AS Value FROM sys.dm_os_ring_buffers WHERE ring_buffer_type='RING_BUFFER_SCHEDULER_MONITOR' ORDER BY timestamp DESC", conn);
var cpu = await cmd.ExecuteScalarAsync();
html.Append($"<p>CPU: {cpu}%</p>");

// Memory
cmd.CommandText = "SELECT physical_memory_kb/1024/1024 AS Value FROM sys.dm_os_sys_info";
var memory = await cmd.ExecuteScalarAsync();
html.Append($"<p>Memory: {memory} GB</p>");

html.Append("</body></html>");

File.WriteAllText("report.html", html.ToString());
System.Diagnostics.Process.Start("explorer.exe", "report.html");
```

Run with: `dotnet run` or build to EXE.

---

## Recommendation

**Use WPF + WebView2 approach** because:
- Still very simple (4 files + HTML)
- Professional UI
- Real-time updates
- Extensible
- Native Windows experience

The console approach is only suitable for one-time reports, not monitoring.

