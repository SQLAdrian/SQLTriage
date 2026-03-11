# Footprint Reduction - Quick Reference

## Current Size: 350 MB → Target: 120 MB (66% reduction)

## ⚡ One-Command Solution

```powershell
.\optimize-footprint.ps1
```

This script automatically:
- ✅ Removes unused NuGet packages (ReportViewer, Console sink)
- ✅ Enables assembly trimming (-50 MB)
- ✅ Enables single-file publish with compression (-30 MB)
- ✅ Compresses Deploy scripts to .zip (-100 MB)
- ✅ Removes bundled .NET runtime (-28 MB)

## 📋 Manual Steps (If Preferred)

### 1. Edit SqlHealthAssessment.csproj

**Remove these lines:**
```xml
<PackageReference Include="ReportViewerCore.NETCore" Version="15.*" ExcludeAssets="all" PrivateAssets="all" />
<PackageReference Include="Serilog.Sinks.Console" Version="6.*" />
```

**Update Release PropertyGroup:**
```xml
<PropertyGroup Condition="'$(Configuration)' == 'Release'">
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
```

### 2. Compress Deploy Folders

```powershell
Compress-Archive -Path Deploy\SQLWATCH_db\* -DestinationPath Deploy\SQLWATCH.zip
Compress-Archive -Path Deploy\PerformanceMonitor_db\* -DestinationPath Deploy\PerformanceMonitor.zip
Remove-Item Deploy\SQLWATCH_db -Recurse
Remove-Item Deploy\PerformanceMonitor_db -Recurse
```

### 3. Remove Bundled Runtime

```powershell
Remove-Item Runtimes\dotnet-runtime-8.0.24-win-x64.exe
```

### 4. Rebuild

```powershell
dotnet clean
dotnet publish -c Release -r win-x64 --self-contained false
```

## ⚠️ Important Notes

1. **Users need .NET 8 Desktop Runtime**
   - Download: https://dotnet.microsoft.com/download/dotnet/8.0
   - Most Windows users already have it

2. **Deploy scripts are now .zip files**
   - Extract on first use
   - Add extraction code to deployment service

3. **Single-file publish**
   - Slower first startup (extraction)
   - Faster subsequent startups

## 🧪 Testing Checklist

After optimization, test:
- [ ] Application starts successfully
- [ ] All dashboards load
- [ ] SQLWATCH deployment works (extract from .zip)
- [ ] Query execution works
- [ ] Session monitoring works
- [ ] File size is ~120 MB or less

## 📊 Size Breakdown

| Component | Before | After | Saved |
|-----------|--------|-------|-------|
| Framework DLLs | 176 MB | 126 MB | 50 MB |
| Deploy Scripts | 145 MB | 45 MB | 100 MB |
| .NET Runtime | 28 MB | 0 MB | 28 MB |
| Compression | - | -30 MB | 30 MB |
| **Total** | **350 MB** | **120 MB** | **208 MB** |

## 🔄 Rollback

If issues occur:
```powershell
# Restore backup
Copy-Item SqlHealthAssessment.csproj.backup SqlHealthAssessment.csproj
dotnet clean
dotnet build
```

## 📚 Full Documentation

See [FOOTPRINT_REDUCTION_GUIDE.md](FOOTPRINT_REDUCTION_GUIDE.md) for detailed explanations and advanced options.
