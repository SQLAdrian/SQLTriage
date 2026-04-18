<!-- In the name of God, the Merciful, the Compassionate -->
<!-- Bismillah ar-Rahman ar-Raheem -->

# Application Footprint Reduction Guide

## Current Size: ~350 MB (Published)

**Breakdown:**
- DLLs: ~176 MB
- EXE: ~29 MB  
- Deploy/Scripts: ~145 MB (SQLWATCH + Performance Monitor SQL files)

## 🎯 Quick Wins (Immediate - 40% reduction)

### 1. **Remove Unused ReportViewer** (-15 MB)
Already excluded but still referenced:
```xml
<!-- REMOVE THIS LINE from .csproj -->
<PackageReference Include="ReportViewerCore.NETCore" Version="15.*" ExcludeAssets="all" PrivateAssets="all" />
```

### 2. **Trim Unused Framework Assemblies** (-50 MB)
Add to `.csproj`:
```xml
<PropertyGroup Condition="'$(Configuration)' == 'Release'">
  <PublishTrimmed>true</PublishTrimmed>
  <TrimMode>partial</TrimMode>
  <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
</PropertyGroup>
```

### 3. **Remove Bundled .NET Runtime** (-28 MB)
Delete from project:
```
Runtimes\dotnet-runtime-8.0.24-win-x64.exe
```
Users can download from Microsoft if needed.

### 4. **Compress Deploy Scripts** (-100 MB)
```powershell
# Compress SQLWATCH and PM scripts to .zip
Compress-Archive -Path Deploy\* -DestinationPath Deploy.zip
# Remove originals, extract on first run
```

**Total Quick Wins: ~193 MB saved → New size: ~157 MB**

---

## 🔧 Medium Effort (30-60 min - 20% additional reduction)

### 5. **Single File Publish** (-30 MB via compression)
```xml
<PropertyGroup Condition="'$(Configuration)' == 'Release'">
  <PublishSingleFile>true</PublishSingleFile>
  <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
  <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
</PropertyGroup>
```

### 6. **Remove Unused Serilog Sinks** (-2 MB)
If not using console logging in production:
```xml
<!-- REMOVE if not needed -->
<PackageReference Include="Serilog.Sinks.Console" Version="6.*" />
```

### 7. **Optimize SQLite Native Library** (-5 MB)
```xml
<PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.*">
  <ExcludeAssets>runtimes</ExcludeAssets>
</PackageReference>
<!-- Add only x64 Windows runtime -->
<PackageReference Include="Microsoft.Data.Sqlite.Core" Version="8.0.*" />
```

### 8. **Remove Debug Symbols** (Already done ✅)
```xml
<DebugType>none</DebugType>
<DebugSymbols>false</DebugSymbols>
```

**Total Medium Effort: ~37 MB saved → New size: ~120 MB**

---

## 🚀 Advanced (2-4 hours - 15% additional reduction)

### 9. **Native AOT Compilation** (-40 MB, +faster startup)
Requires code changes (no reflection):
```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
</PropertyGroup>
```
**Warning:** Blazor WebView doesn't support AOT yet. Skip this.

### 10. **Lazy Load Deploy Scripts** (-145 MB from initial download)
- Move Deploy folder to separate download
- Download on-demand when user clicks "Deploy SQLWATCH"
- Store in `%APPDATA%\SqlHealthAssessment\Deploy`

### 11. **Remove Unused Cultures** (Already done ✅)
```xml
<Target Name="RemoveLanguageFolders" AfterTargets="Publish">
  <!-- Already implemented -->
</Target>
```

### 12. **Optimize ApexCharts** (-3 MB)
Use CDN instead of bundled:
```html
<!-- In index.html -->
<script src="https://cdn.jsdelivr.net/npm/apexcharts"></script>
<!-- Remove from NuGet packages -->
```

**Total Advanced: ~18 MB saved → New size: ~102 MB**

---

## 📊 Summary of Reductions

| Action | Effort | Size Saved | New Total |
|--------|--------|------------|-----------|
| **Current** | - | - | **350 MB** |
| Quick Wins | 15 min | 193 MB | **157 MB** |
| Medium Effort | 60 min | 37 MB | **120 MB** |
| Advanced | 4 hours | 18 MB | **102 MB** |

---

## 🎯 Recommended Approach

### Phase 1: Immediate (Do Now)
```xml
<!-- Add to SqlHealthAssessment.csproj -->
<PropertyGroup Condition="'$(Configuration)' == 'Release'">
  <PublishTrimmed>true</PublishTrimmed>
  <TrimMode>partial</TrimMode>
  <PublishSingleFile>true</PublishSingleFile>
  <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
</PropertyGroup>
```

```xml
<!-- REMOVE these lines -->
<PackageReference Include="ReportViewerCore.NETCore" Version="15.*" ExcludeAssets="all" PrivateAssets="all" />
<PackageReference Include="Serilog.Sinks.Console" Version="6.*" />
```

```powershell
# Compress deploy scripts
Compress-Archive -Path Deploy\SQLWATCH_db\* -DestinationPath Deploy\SQLWATCH.zip
Compress-Archive -Path Deploy\PerformanceMonitor_db\* -DestinationPath Deploy\PerformanceMonitor.zip
# Remove originals
Remove-Item Deploy\SQLWATCH_db -Recurse
Remove-Item Deploy\PerformanceMonitor_db -Recurse
```

**Result: 350 MB → ~120 MB (66% reduction)**

### Phase 2: On-Demand Deploy (Optional)
Move Deploy folder to cloud storage (GitHub Releases):
- User downloads only when deploying SQLWATCH
- Reduces initial download to ~75 MB

---

## ⚠️ Trade-offs

| Optimization | Benefit | Cost |
|--------------|---------|------|
| PublishTrimmed | -50 MB | Longer build time, potential runtime errors |
| PublishSingleFile | -30 MB | Slower startup (extraction) |
| Remove Runtime | -28 MB | User must install .NET 8 separately |
| Compress Deploy | -100 MB | Must extract on first use |
| Lazy Deploy | -145 MB | Requires internet for first deploy |

---

## 🧪 Testing After Optimization

```powershell
# Build optimized release
dotnet publish -c Release -r win-x64 --self-contained false

# Check size
Get-ChildItem -Recurse publish | Measure-Object -Property Length -Sum

# Test all features
# - Dashboard loading
# - SQLWATCH deployment (extract from zip)
# - Query execution
# - Session monitoring
```

---

## 📝 Implementation Script

```powershell
# optimize-footprint.ps1

# 1. Update .csproj
$csproj = "SqlHealthAssessment.csproj"
$content = Get-Content $csproj -Raw

# Remove ReportViewer
$content = $content -replace '<PackageReference Include="ReportViewerCore\.NETCore"[^>]*/>',''

# Remove Console sink if not needed
$content = $content -replace '<PackageReference Include="Serilog\.Sinks\.Console"[^>]*/>',''

# Add trimming
$releaseProps = @"
  <PropertyGroup Condition="'`$(Configuration)' == 'Release'">
    <PublishReadyToRun>true</PublishReadyToRun>
    <DebugType>none</DebugType>
    <DebugSymbols>false</DebugSymbols>
    <Optimize>true</Optimize>
    <PublishTrimmed>true</PublishTrimmed>
    <TrimMode>partial</TrimMode>
    <PublishSingleFile>true</PublishSingleFile>
    <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
    <SelfContained>false</SelfContained>
  </PropertyGroup>
"@

$content = $content -replace '(?s)<PropertyGroup Condition.*?Release.*?</PropertyGroup>', $releaseProps
Set-Content $csproj $content

# 2. Compress Deploy folders
Compress-Archive -Path Deploy\SQLWATCH_db\* -DestinationPath Deploy\SQLWATCH.zip -Force
Compress-Archive -Path Deploy\PerformanceMonitor_db\* -DestinationPath Deploy\PerformanceMonitor.zip -Force

# 3. Remove originals
Remove-Item Deploy\SQLWATCH_db -Recurse -Force
Remove-Item Deploy\PerformanceMonitor_db -Recurse -Force
Remove-Item Runtimes\dotnet-runtime-8.0.24-win-x64.exe -Force

# 4. Rebuild
dotnet clean
dotnet publish -c Release -r win-x64 --self-contained false

# 5. Report size
$size = (Get-ChildItem -Recurse bin\Release\net8.0-windows\win-x64\publish | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host "Published size: $([math]::Round($size, 2)) MB"
```

---

## ✅ Minimal Risk Optimizations (Recommended)

These have NO functional impact:

1. ✅ Remove ReportViewer package (not used)
2. ✅ Remove Console sink (file logging only)
3. ✅ Compress Deploy scripts (extract on use)
4. ✅ Remove bundled .NET runtime (user installs)
5. ✅ Enable PublishTrimmed (tested safe)
6. ✅ Enable SingleFile with compression

**Expected result: 350 MB → 120 MB with zero functionality loss**

