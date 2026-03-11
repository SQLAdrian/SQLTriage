# Reset Build Number for Production Release
# Run this when creating a new production release version

param(
    [Parameter(Mandatory=$false)]
    [string]$NewVersion
)

$versionFile = Join-Path $PSScriptRoot "version.json"

if (Test-Path $versionFile) {
    $json = Get-Content $versionFile -Raw | ConvertFrom-Json
    
    # Update version if provided
    if ($NewVersion) {
        $json.version = $NewVersion
        Write-Host "Version updated to: $NewVersion" -ForegroundColor Green
    }
    
    # Reset build number to 0 for production releases
    $json.buildNumber = 0
    $json.buildDate = Get-Date -Format "yyyy-MM-dd"
    $json.releaseType = "Production"
    
    # Save back to file
    $json | ConvertTo-Json | Set-Content $versionFile
    
    Write-Host "Build number reset to 0 for production release" -ForegroundColor Green
    Write-Host "Release version: $($json.version)" -ForegroundColor Cyan
    Write-Host "Build date: $($json.buildDate)" -ForegroundColor Cyan
} else {
    Write-Host "version.json not found!" -ForegroundColor Red
}
