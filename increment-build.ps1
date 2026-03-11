# increment-build.ps1
$versionFile = "Config\version.json"
$json = Get-Content $versionFile -Raw | ConvertFrom-Json
$json.buildNumber = [int]$json.buildNumber + 1
$json.buildDate = Get-Date -Format "yyyy-MM-dd"
$json | ConvertTo-Json | Set-Content $versionFile
Write-Host "Build incremented to: $($json.buildNumber)" -ForegroundColor Green
