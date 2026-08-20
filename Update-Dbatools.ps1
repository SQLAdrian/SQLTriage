# Update-Dbatools.ps1
# Downloads/updates the dbatools PowerShell module into the app's .\dbatools folder.
# Run this script manually or call it from the publish pipeline to keep dbatools current.
#
# Usage:
#   .\Update-Dbatools.ps1                          # downloads to .\dbatools next to the script
#   .\Update-Dbatools.ps1 -TargetPath "C:\MyApp"   # downloads to C:\MyApp\dbatools

param(
    [string]$TargetPath = $PSScriptRoot
)

$modulePath = Join-Path $TargetPath "dbatools"

Write-Host "dbatools updater" -ForegroundColor Cyan
Write-Host "Target: $modulePath" -ForegroundColor Gray

# Check for internet connectivity
try {
    $null = [System.Net.Dns]::GetHostEntry("www.powershellgallery.com")
} catch {
    Write-Warning "Cannot reach PowerShell Gallery. Check internet connectivity."
    exit 1
}

# Ensure NuGet provider is available (needed for Save-Module on some systems)
if (-not (Get-PackageProvider -Name NuGet -ErrorAction SilentlyContinue)) {
    Write-Host "Installing NuGet provider..." -ForegroundColor Yellow
    Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Scope CurrentUser | Out-Null
}

# Create target directory
if (-not (Test-Path $modulePath)) {
    New-Item -ItemType Directory -Path $modulePath -Force | Out-Null
    Write-Host "Created $modulePath" -ForegroundColor Green
}

Write-Host "Downloading dbatools (this may take a few minutes — ~160 MB)..." -ForegroundColor Yellow

try {
    Save-Module -Name dbatools -Path $modulePath -Force -ErrorAction Stop
    Write-Host "dbatools downloaded successfully." -ForegroundColor Green

    # Show what was downloaded
    $versions = Get-ChildItem (Join-Path $modulePath "dbatools") -Directory -ErrorAction SilentlyContinue
    if ($versions) {
        Write-Host "Installed version: $($versions[-1].Name)" -ForegroundColor Cyan
    }
} catch {
    Write-Error "Failed to download dbatools: $_"
    exit 1
}

Write-Host "Done. Restart the app to use the updated module." -ForegroundColor Green
