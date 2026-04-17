# publish-release.ps1
# Publishes the app and creates a GitHub release with the ZIP artifact.
# Usage: .\publish-release.ps1 [-DryRun] [-NoPush]
#
# Prerequisites: gh CLI (https://cli.github.com) authenticated with `gh auth login`

param(
    [switch]$DryRun,    # Show what would happen without doing it
    [switch]$NoPush     # Build + ZIP but skip GitHub release creation
)

$ErrorActionPreference = "Stop"

# ── Read version info ─────────────────────────────────────────────────
$versionFile = "Config\version.json"
if (-not (Test-Path $versionFile)) {
    Write-Host "ERROR: $versionFile not found. Run from project root." -ForegroundColor Red
    exit 1
}

$versionJson = Get-Content $versionFile -Raw | ConvertFrom-Json
$version     = $versionJson.version
$buildNumber = $versionJson.buildNumber
$tag         = "v$version.$buildNumber"
$releaseName = "v$version (Build $buildNumber)"

Write-Host ""
Write-Host "  Version:  $version" -ForegroundColor Cyan
Write-Host "  Build:    $buildNumber" -ForegroundColor Cyan
Write-Host "  Tag:      $tag" -ForegroundColor Cyan
Write-Host ""

# ── Publish ───────────────────────────────────────────────────────────
$publishDir = "publish\win-x64"

if ($DryRun) {
    Write-Host "[DRY RUN] Would publish to $publishDir" -ForegroundColor Yellow
    Write-Host "[DRY RUN] Would create GitHub release $tag" -ForegroundColor Yellow
    exit 0
}

Write-Host "Publishing Release build..." -ForegroundColor Green
dotnet publish SqlHealthAssessment.csproj -c Release -r win-x64 -o $publishDir
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: dotnet publish failed." -ForegroundColor Red
    exit 1
}

# Re-read version.json after build (build number was incremented during publish)
$versionJson = Get-Content $versionFile -Raw | ConvertFrom-Json
$version     = $versionJson.version
$buildNumber = $versionJson.buildNumber
$tag         = "v$version.$buildNumber"
$releaseName = "v$version (Build $buildNumber)"

Write-Host "  Published build: $buildNumber" -ForegroundColor Cyan

# ── Locate ZIP (created by csproj CreateReleaseZip target) ────────────
$zipName = "SqlHealthAssessment-v$version-build$buildNumber-win-x64.zip"
$zipPath = "release\$zipName"

if (-not (Test-Path $zipPath)) {
    Write-Host "ERROR: Expected ZIP not found at $zipPath" -ForegroundColor Red
    Write-Host "  The csproj CreateReleaseZip target should have created it during publish." -ForegroundColor Yellow
    exit 1
}

$zipSize = (Get-Item $zipPath).Length / 1MB
Write-Host "  ZIP: $zipName ($([math]::Round($zipSize, 1)) MB)" -ForegroundColor Cyan

# ── Locate Inno Setup installer (compiled by csproj BuildInstaller target) ──
# The csproj BuildInstaller target runs iscc automatically during dotnet publish.
$setupName = "LiveMonitor-v$version-build$buildNumber-Setup.exe"
$setupPath = "release\$setupName"

if (Test-Path $setupPath) {
    $setupSize = (Get-Item $setupPath).Length / 1MB
    Write-Host "  Setup: $setupName ($([math]::Round($setupSize, 1)) MB)" -ForegroundColor Cyan
} else {
    Write-Host "  WARNING: Installer not found at $setupPath — was Inno Setup 6 installed?" -ForegroundColor Yellow
    Write-Host "    Expected at C:\GitHub\Inno Setup 6\iscc.exe" -ForegroundColor DarkYellow
    $setupPath = $null
}

if ($NoPush) {
    Write-Host ""
    Write-Host "ZIP created at: $zipPath" -ForegroundColor Green
    if ($setupPath) { Write-Host "Installer created at: $setupPath" -ForegroundColor Green }
    Write-Host "Skipping GitHub release (use without -NoPush to upload)." -ForegroundColor Yellow
    exit 0
}

# ── Create GitHub Release ─────────────────────────────────────────────
Write-Host ""
Write-Host "Creating GitHub release: $tag ..." -ForegroundColor Green

# Check if tag already exists
$existingTag = git tag -l $tag 2>$null
if ($existingTag) {
    Write-Host "ERROR: Tag $tag already exists. Bump version or build number first." -ForegroundColor Red
    exit 1
}

# Generate release notes from recent commits
$lastTag = $null
try { $lastTag = git describe --tags --abbrev=0 2>&1 | Where-Object { $_ -notmatch 'fatal' } } catch {}
if ($lastTag) {
    $commitLog = git log "$lastTag..HEAD" --oneline --no-merges 2>$null
} else {
    $commitLog = git log -20 --oneline --no-merges 2>$null
}

$releaseNotes = @"
## SQL Health Assessment $releaseName

### Changes
$($commitLog | ForEach-Object { "- $_" } | Out-String)
### Install — Option A: Installer (recommended)
Download ``$setupName`` and run it. Adds a Start Menu entry, desktop shortcut, and uninstaller.
No .NET or WebView2 installation required — everything is bundled.

### Install — Option B: ZIP (portable / shared installs)
Download and extract ``$zipName`` to any folder (e.g. ``C:\Tools\LiveMonitor``).
Run ``SqlHealthAssessment.exe``. No installer needed.

### Update
Existing installations will detect this release automatically via the in-app updater.
User configuration (server connections, alerts, notification channels, dashboards) is preserved on upgrade.
"@

# Build asset list — always include ZIP; include installer if it was compiled
$assets = @($zipPath)
if ($setupPath) { $assets += $setupPath }

# Create release with gh CLI
gh release create $tag @assets `
    --title $releaseName `
    --notes $releaseNotes `
    --latest

if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: GitHub release creation failed." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Release created successfully!" -ForegroundColor Green
Write-Host "  Tag:     $tag" -ForegroundColor Cyan
Write-Host "  ZIP:     $zipName" -ForegroundColor Cyan
if ($setupPath) { Write-Host "  Setup:   $setupName" -ForegroundColor Cyan }
Write-Host "  URL:     https://github.com/SQLAdrian/SqlHealthAssessment/releases/tag/$tag" -ForegroundColor Cyan
