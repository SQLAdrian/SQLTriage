# check-logs.ps1 - Check application logs for errors
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Checking Application Logs for Errors" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host

# Check if logs directory exists
if (-not (Test-Path "logs")) {
    Write-Host "No logs directory found." -ForegroundColor Red
    exit 1
}

# Find the most recent log file
$logFiles = Get-ChildItem "logs\app-*.log" | Sort-Object LastWriteTime -Descending
if ($logFiles.Count -eq 0) {
    Write-Host "No log files found in logs directory." -ForegroundColor Red
    exit 1
}

$latestLog = $logFiles[0]
Write-Host "Latest log file: $($latestLog.Name)" -ForegroundColor Green
Write-Host "Last modified: $($latestLog.LastWriteTime)" -ForegroundColor Green
Write-Host

# Read the log file content
$logContent = Get-Content $latestLog.FullName

Write-Host "========================================" -ForegroundColor Yellow
Write-Host "Recent ERROR entries:" -ForegroundColor Yellow
Write-Host "========================================" -ForegroundColor Yellow
$errorEntries = $logContent | Where-Object { $_ -match "\[ERR\]|\[ERROR\]" } | Select-Object -Last 20
if ($errorEntries) {
    $errorEntries | ForEach-Object { Write-Host $_ -ForegroundColor Red }
} else {
    Write-Host "No ERROR entries found." -ForegroundColor Green
}

Write-Host
Write-Host "========================================" -ForegroundColor Yellow
Write-Host "Recent FATAL entries:" -ForegroundColor Yellow
Write-Host "========================================" -ForegroundColor Yellow
$fatalEntries = $logContent | Where-Object { $_ -match "\[FTL\]|\[FATAL\]" } | Select-Object -Last 10
if ($fatalEntries) {
    $fatalEntries | ForEach-Object { Write-Host $_ -ForegroundColor Magenta }
} else {
    Write-Host "No FATAL entries found." -ForegroundColor Green
}

Write-Host
Write-Host "========================================" -ForegroundColor Yellow
Write-Host "Recent Exception entries:" -ForegroundColor Yellow
Write-Host "========================================" -ForegroundColor Yellow
$exceptionEntries = $logContent | Where-Object { $_ -match "exception|Exception|EXCEPTION" } | Select-Object -Last 10
if ($exceptionEntries) {
    $exceptionEntries | ForEach-Object { Write-Host $_ -ForegroundColor Red }
} else {
    Write-Host "No Exception entries found." -ForegroundColor Green
}

Write-Host
Write-Host "========================================" -ForegroundColor Yellow
Write-Host "Last 20 log entries:" -ForegroundColor Yellow
Write-Host "========================================" -ForegroundColor Yellow
$logContent | Select-Object -Last 20 | ForEach-Object { 
    if ($_ -match "\[ERR\]|\[ERROR\]|\[FTL\]|\[FATAL\]") {
        Write-Host $_ -ForegroundColor Red
    } elseif ($_ -match "\[WRN\]|\[WARN\]") {
        Write-Host $_ -ForegroundColor Yellow
    } elseif ($_ -match "\[INF\]|\[INFO\]") {
        Write-Host $_ -ForegroundColor White
    } else {
        Write-Host $_ -ForegroundColor Gray
    }
}

Write-Host
Write-Host "Log file size: $([math]::Round($latestLog.Length / 1KB, 2)) KB" -ForegroundColor Cyan
Write-Host "Total lines: $($logContent.Count)" -ForegroundColor Cyan