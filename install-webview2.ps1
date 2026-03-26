#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs Microsoft Edge WebView2 Runtime on Windows Server 2016 and later.
    
.DESCRIPTION
    This script downloads and installs the Microsoft Edge WebView2 Runtime,
    which is required for the SQL Health Assessment application to run.
    
    Supports:
    - Windows Server 2016 (Version 1607)
    - Windows Server 2019
    - Windows Server 2022
    - Windows 10/11
    
.PARAMETER Silent
    If specified, runs the installer in silent mode without user interaction.
    
.PARAMETER Force
    If specified, reinstalls WebView2 even if it's already installed.
    
.EXAMPLE
    .\install-webview2.ps1
    
.EXAMPLE
    .\install-webview2.ps1 -Silent
    
.EXAMPLE
    .\install-webview2.ps1 -Silent -Force

.NOTES
    Author: SQL Health Assessment Team
    Version: 1.0.0
    Required OS: Windows Server 2016 or later, Windows 10 1809 or later
#>

[CmdletBinding()]
param(
    [switch]$Silent,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# WebView2 Evergreen Runtime download URL
$WebView2DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703"
$InstallerPath = Join-Path $env:TEMP "WebView2Installer.exe"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  WebView2 Runtime Installer" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Check if WebView2 is already installed
function Test-WebView2Installed {
    try {
        $webView2Path = "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
        if (Test-Path $webView2Path) {
            $version = (Get-ItemProperty -Path $webView2Path -Name "pv" -ErrorAction SilentlyContinue).pv
            if ($version) {
                return @{
                    Installed = $true
                    Version = $version
                }
            }
        }
        
        # Check 64-bit registry location
        $webView2Path64 = "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
        if (Test-Path $webView2Path64) {
            $version = (Get-ItemProperty -Path $webView2Path64 -Name "pv" -ErrorAction SilentlyContinue).pv
            if ($version) {
                return @{
                    Installed = $true
                    Version = $version
                }
            }
        }
        
        return @{
            Installed = $false
            Version = $null
        }
    }
    catch {
        return @{
            Installed = $false
            Version = $null
        }
    }
}

# Check Windows version
function Test-WindowsVersion {
    $os = Get-CimInstance -ClassName Win32_OperatingSystem
    $version = [System.Version]$os.Version
    
    Write-Host "Operating System: $($os.Caption)" -ForegroundColor Gray
    Write-Host "Version: $($os.Version)" -ForegroundColor Gray
    Write-Host "Architecture: $($os.OSArchitecture)" -ForegroundColor Gray
    Write-Host ""
    
    # Windows Server 2016 is version 10.0.14393
    if ($version.Major -eq 10 -and $version.Build -ge 14393) {
        return $true
    }
    
    # Windows 10 1809 is version 10.0.17763
    if ($version.Major -eq 10 -and $version.Build -ge 17763) {
        return $true
    }
    
    # Windows 11
    if ($version.Major -ge 10 -and $version.Build -ge 22000) {
        return $true
    }
    
    return $false
}

# Main execution
Write-Host "Checking system requirements..." -ForegroundColor Yellow

if (-not (Test-WindowsVersion)) {
    Write-Host "ERROR: This system does not meet the minimum requirements." -ForegroundColor Red
    Write-Host "Windows Server 2016 (Version 1607) or later is required." -ForegroundColor Red
    exit 1
}

Write-Host "Checking for existing WebView2 installation..." -ForegroundColor Yellow
$existingInstall = Test-WebView2Installed

if ($existingInstall.Installed -and -not $Force) {
    Write-Host "WebView2 Runtime is already installed!" -ForegroundColor Green
    Write-Host "Version: $($existingInstall.Version)" -ForegroundColor Green
    Write-Host ""
    Write-Host "Use -Force parameter to reinstall." -ForegroundColor Gray
    exit 0
}

if ($existingInstall.Installed -and $Force) {
    Write-Host "Reinstalling WebView2 Runtime (version: $($existingInstall.Version))..." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Downloading WebView2 Runtime..." -ForegroundColor Yellow

try {
    # Download the installer
    $ProgressPreference = 'SilentlyContinue'  # Faster download
    Invoke-WebRequest -Uri $WebView2DownloadUrl -OutFile $InstallerPath -UseBasicParsing
    
    if (-not (Test-Path $InstallerPath)) {
        throw "Download failed - file not found"
    }
    
    $fileSize = (Get-Item $InstallerPath).Length / 1MB
    Write-Host "Downloaded: $InstallerPath ($([math]::Round($fileSize, 2)) MB)" -ForegroundColor Green
}
catch {
    Write-Host "ERROR: Failed to download WebView2 Runtime: $_" -ForegroundColor Red
    
    # Try alternative download method
    Write-Host ""
    Write-Host "Trying alternative download method..." -ForegroundColor Yellow
    
    try {
        $webClient = New-Object System.Net.WebClient
        $webClient.DownloadFile($WebView2DownloadUrl, $InstallerPath)
        
        if (Test-Path $InstallerPath) {
            $fileSize = (Get-Item $InstallerPath).Length / 1MB
            Write-Host "Downloaded: $InstallerPath ($([math]::Round($fileSize, 2)) MB)" -ForegroundColor Green
        }
    }
    catch {
        Write-Host "ERROR: Alternative download also failed: $_" -ForegroundColor Red
        Write-Host ""
        Write-Host "Please manually download WebView2 Runtime from:" -ForegroundColor Yellow
        Write-Host "https://developer.microsoft.com/en-us/microsoft-edge/webview2/" -ForegroundColor Cyan
        exit 1
    }
}

Write-Host ""
Write-Host "Installing WebView2 Runtime..." -ForegroundColor Yellow

try {
    $installArgs = "/silent /install"
    
    if ($Silent) {
        $installArgs = "/silent /install"
    }
    
    $process = Start-Process -FilePath $InstallerPath -ArgumentList $installArgs -Wait -PassThru
    
    # Clean up installer
    Remove-Item $InstallerPath -Force -ErrorAction SilentlyContinue
    
    if ($process.ExitCode -eq 0) {
        Write-Host ""
        Write-Host "========================================" -ForegroundColor Green
        Write-Host "  Installation Successful!" -ForegroundColor Green
        Write-Host "========================================" -ForegroundColor Green
        Write-Host ""
        
        # Verify installation
        Start-Sleep -Seconds 2
        $newInstall = Test-WebView2Installed
        
        if ($newInstall.Installed) {
            Write-Host "WebView2 Runtime Version: $($newInstall.Version)" -ForegroundColor Green
        }
        
        Write-Host ""
        Write-Host "You can now run the SQL Health Assessment application." -ForegroundColor Cyan
        exit 0
    }
    else {
        Write-Host "ERROR: Installation failed with exit code $($process.ExitCode)" -ForegroundColor Red
        exit $process.ExitCode
    }
}
catch {
    Write-Host "ERROR: Installation failed: $_" -ForegroundColor Red
    
    # Clean up on error
    if (Test-Path $InstallerPath) {
        Remove-Item $InstallerPath -Force -ErrorAction SilentlyContinue
    }
    
    exit 1
}
