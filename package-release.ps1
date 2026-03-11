# Package Release for GitHub
# Run this after building in Release mode to create a distributable package

param(
    [Parameter(Mandatory=$false)]
    [string]$Version
)

$projectDir = $PSScriptRoot
$publishDir = Join-Path $projectDir "bin\Release\net8.0-windows\publish"
$outputDir = Join-Path $projectDir "releases"

# Get version from version.json if not provided
if (-not $Version) {
    $versionFile = Join-Path $projectDir "version.json"
    if (Test-Path $versionFile) {
        $json = Get-Content $versionFile -Raw | ConvertFrom-Json
        $Version = $json.version
    } else {
        $Version = "1.0.0"
    }
}

Write-Host "Packaging SqlHealthAssessment v$Version for GitHub Release..." -ForegroundColor Cyan

# Create releases directory if it doesn't exist
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir | Out-Null
}

# Check if publish directory exists
if (-not (Test-Path $publishDir)) {
    Write-Host "ERROR: Publish directory not found. Please build in Release mode first:" -ForegroundColor Red
    Write-Host "  dotnet publish -c Release" -ForegroundColor Yellow
    exit 1
}

# Create zip file
$zipName = "SqlHealthAssessment-v$Version-win-x64.zip"
$zipPath = Join-Path $outputDir $zipName

Write-Host "Creating package: $zipName" -ForegroundColor Green

# Remove old zip if exists
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

# Create zip
Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath -CompressionLevel Optimal

$zipSize = (Get-Item $zipPath).Length / 1MB
Write-Host "Package created successfully: $([math]::Round($zipSize, 2)) MB" -ForegroundColor Green
Write-Host "Location: $zipPath" -ForegroundColor Cyan

Write-Host "`nNext steps:" -ForegroundColor Yellow
Write-Host "1. Go to: https://github.com/SQLAdrian/SqlHealthAssessment/releases/new" -ForegroundColor White
Write-Host "2. Tag version: v$Version" -ForegroundColor White
Write-Host "3. Release title: SqlHealthAssessment v$Version" -ForegroundColor White
Write-Host "4. Upload: $zipPath" -ForegroundColor White
Write-Host "5. Add release notes and publish" -ForegroundColor White
