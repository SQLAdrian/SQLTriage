<#
.SYNOPSIS
    Publishes SQL Health Assessment with assembly protection via ConfuserEx2.

.DESCRIPTION
    1. Publishes Release build (single-file, self-contained, R2R)
    2. Runs ConfuserEx2 to obfuscate the main assembly
    3. Replaces the assembly in the publish output with the protected version
    4. Creates the final release ZIP

.NOTES
    Prerequisites:
    - Install ConfuserEx2 CLI: dotnet tool install -g ConfuserEx.CLI
    - Or download from: https://github.com/mkaring/ConfuserEx/releases

.EXAMPLE
    .\publish-protected.ps1
    .\publish-protected.ps1 -SkipObfuscation   # publish without protection
#>

param(
    [switch]$SkipObfuscation
)

$ErrorActionPreference = "Stop"
$projectDir = $PSScriptRoot
$publishDir = Join-Path $projectDir "publish"
$protectedDir = Join-Path $projectDir "publish-protected"
$releaseDir = Join-Path $projectDir "release"

Write-Host "=== SQL Health Assessment - Protected Publish ===" -ForegroundColor Cyan

# Step 1: Clean
Write-Host "`n[1/4] Cleaning previous builds..." -ForegroundColor Yellow
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
if (Test-Path $protectedDir) { Remove-Item $protectedDir -Recurse -Force }

# Step 2: Publish
Write-Host "`n[2/4] Publishing Release build..." -ForegroundColor Yellow
dotnet publish -c Release -o $publishDir --self-contained true -r win-x64
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Publish failed." -ForegroundColor Red
    exit 1
}
Write-Host "Published to: $publishDir" -ForegroundColor Green

# Step 3: Obfuscate
if (-not $SkipObfuscation) {
    Write-Host "`n[3/4] Running ConfuserEx2 obfuscation..." -ForegroundColor Yellow

    $confuserCli = Get-Command "Confuser.CLI" -ErrorAction SilentlyContinue
    if (-not $confuserCli) {
        $confuserCli = Get-Command "ConfuserEx.CLI" -ErrorAction SilentlyContinue
    }

    if ($confuserCli) {
        # Run ConfuserEx on the published assembly
        & $confuserCli.Source (Join-Path $projectDir "confuser.crproj")

        if ($LASTEXITCODE -eq 0 -and (Test-Path (Join-Path $protectedDir "SqlHealthAssessment.dll"))) {
            # Replace the published DLL with the protected one
            Copy-Item (Join-Path $protectedDir "SqlHealthAssessment.dll") (Join-Path $publishDir "SqlHealthAssessment.dll") -Force
            Write-Host "Assembly protected successfully." -ForegroundColor Green
        } else {
            Write-Host "WARNING: ConfuserEx failed. Proceeding with unprotected assembly." -ForegroundColor Yellow
        }
    } else {
        Write-Host "WARNING: ConfuserEx CLI not found. Install with:" -ForegroundColor Yellow
        Write-Host "  dotnet tool install -g ConfuserEx.CLI" -ForegroundColor Gray
        Write-Host "Proceeding with unprotected assembly." -ForegroundColor Yellow
    }
} else {
    Write-Host "`n[3/4] Skipping obfuscation (--SkipObfuscation)" -ForegroundColor Gray
}

# Step 4: Create release ZIP
Write-Host "`n[4/4] Creating release ZIP..." -ForegroundColor Yellow
$versionJson = Get-Content (Join-Path $projectDir "Config\version.json") | ConvertFrom-Json
$version = $versionJson.version
$build = $versionJson.buildNumber
$zipName = "SqlHealthAssessment-v$version-build$build-win-x64.zip"

if (-not (Test-Path $releaseDir)) { New-Item $releaseDir -ItemType Directory | Out-Null }
$zipPath = Join-Path $releaseDir $zipName

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath -Force

$zipSize = (Get-Item $zipPath).Length / 1MB
Write-Host "`n=== Build Complete ===" -ForegroundColor Green
Write-Host "  ZIP: $zipPath" -ForegroundColor Cyan
Write-Host "  Size: $([math]::Round($zipSize, 1)) MB" -ForegroundColor Cyan
Write-Host "  Version: v$version (build $build)" -ForegroundColor Cyan

# Cleanup
if (Test-Path $protectedDir) { Remove-Item $protectedDir -Recurse -Force }
