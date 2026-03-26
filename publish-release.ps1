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

if ($NoPush) {
    Write-Host ""
    Write-Host "ZIP created at: $zipPath" -ForegroundColor Green
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
### Install
Download and extract the ZIP to your desired location. Run ``SqlHealthAssessment.exe``.

### Update
Existing installations will detect this release automatically via the in-app updater.
User configuration files (server connections, alerts, notification channels, scheduled tasks, dashboard customizations) are preserved during update.
"@

# Create release with gh CLI
gh release create $tag $zipPath `
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
Write-Host "  Asset:   $zipName" -ForegroundColor Cyan
Write-Host "  URL:     https://github.com/SQLAdrian/SqlHealthAssessment/releases/tag/$tag" -ForegroundColor Cyan
