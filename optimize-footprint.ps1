# optimize-footprint.ps1
# Reduces application footprint from ~350 MB to ~120 MB

Write-Host "SQL Health Assessment - Footprint Optimization" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan
Write-Host ""

# 1. Backup .csproj
Write-Host "[1/5] Backing up .csproj..." -ForegroundColor Yellow
Copy-Item "SqlHealthAssessment.csproj" "SqlHealthAssessment.csproj.backup" -Force
Write-Host "  ✓ Backup created" -ForegroundColor Green

# 2. Update .csproj
Write-Host "[2/5] Optimizing .csproj..." -ForegroundColor Yellow
$csproj = Get-Content "SqlHealthAssessment.csproj" -Raw

# Remove unused packages
$csproj = $csproj -replace '<PackageReference Include="ReportViewerCore\.NETCore"[^>]*/>(\r?\n)?',''
$csproj = $csproj -replace '<PackageReference Include="Serilog\.Sinks\.Console"[^>]*/>(\r?\n)?',''

# Update Release configuration
$newReleaseConfig = @'
  <!-- Release Build Optimizations -->
  <PropertyGroup Condition="'$(Configuration)' == 'Release'">
    <PublishReadyToRun>true</PublishReadyToRun>
    <PublishReadyToRunShowWarnings>true</PublishReadyToRunShowWarnings>
    <DebugType>none</DebugType>
    <DebugSymbols>false</DebugSymbols>
    <Optimize>true</Optimize>
    <SelfContained>false</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
    <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
    <PublishTrimmed>true</PublishTrimmed>
    <TrimMode>partial</TrimMode>
    <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
  </PropertyGroup>
'@

$csproj = $csproj -replace '(?s)<!-- Release Build Optimizations -->.*?</PropertyGroup>', $newReleaseConfig
Set-Content "SqlHealthAssessment.csproj" $csproj -NoNewline
Write-Host "  ✓ Removed unused packages" -ForegroundColor Green
Write-Host "  ✓ Enabled trimming and single-file publish" -ForegroundColor Green

# 3. Compress Deploy folders
Write-Host "[3/5] Compressing deployment scripts..." -ForegroundColor Yellow
if (Test-Path "Deploy\SQLWATCH_db") {
    Compress-Archive -Path "Deploy\SQLWATCH_db\*" -DestinationPath "Deploy\SQLWATCH.zip" -Force
    Remove-Item "Deploy\SQLWATCH_db" -Recurse -Force
    Write-Host "  ✓ SQLWATCH scripts compressed" -ForegroundColor Green
}
if (Test-Path "Deploy\PerformanceMonitor_db") {
    Compress-Archive -Path "Deploy\PerformanceMonitor_db\*" -DestinationPath "Deploy\PerformanceMonitor.zip" -Force
    Remove-Item "Deploy\PerformanceMonitor_db" -Recurse -Force
    Write-Host "  ✓ Performance Monitor scripts compressed" -ForegroundColor Green
}

# 4. Remove bundled runtime
Write-Host "[4/5] Removing bundled .NET runtime..." -ForegroundColor Yellow
if (Test-Path "Runtimes\dotnet-runtime-8.0.24-win-x64.exe") {
    Remove-Item "Runtimes\dotnet-runtime-8.0.24-win-x64.exe" -Force
    Write-Host "  ✓ Bundled runtime removed (users install separately)" -ForegroundColor Green
}

# 5. Clean and rebuild
Write-Host "[5/5] Rebuilding optimized release..." -ForegroundColor Yellow
dotnet clean -c Release | Out-Null
dotnet publish -c Release -r win-x64 --self-contained false -o "publish-optimized"

if ($LASTEXITCODE -eq 0) {
    Write-Host "  ✓ Build successful" -ForegroundColor Green
    
    # Calculate size
    $size = (Get-ChildItem -Recurse "publish-optimized" | Measure-Object -Property Length -Sum).Sum / 1MB
    $originalSize = 350
    $reduction = $originalSize - $size
    $percent = [math]::Round(($reduction / $originalSize) * 100, 1)
    
    Write-Host ""
    Write-Host "================================================" -ForegroundColor Cyan
    Write-Host "Optimization Complete!" -ForegroundColor Green
    Write-Host "================================================" -ForegroundColor Cyan
    Write-Host "Original size:  $originalSize MB" -ForegroundColor White
    Write-Host "Optimized size: $([math]::Round($size, 1)) MB" -ForegroundColor Green
    Write-Host "Reduction:      $([math]::Round($reduction, 1)) MB ($percent%)" -ForegroundColor Green
    Write-Host ""
    Write-Host "Output: publish-optimized\" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Note: Users must have .NET 8 Desktop Runtime installed" -ForegroundColor Yellow
    Write-Host "Download: https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Cyan
} else {
    Write-Host "  ✗ Build failed" -ForegroundColor Red
    Write-Host "  Restoring backup..." -ForegroundColor Yellow
    Copy-Item "SqlHealthAssessment.csproj.backup" "SqlHealthAssessment.csproj" -Force
    Write-Host "  ✓ Backup restored" -ForegroundColor Green
}
