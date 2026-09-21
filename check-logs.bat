@echo off
REM check-logs.bat - Check application logs for errors
echo ========================================
echo Checking Application Logs for Errors
echo ========================================
echo.

REM Check if logs directory exists
if not exist "logs" (
    echo No logs directory found.
    exit /b 1
)

REM Find the most recent log file
for /f "delims=" %%i in ('dir /b /o-d logs\app-*.log 2^>nul') do (
    set "LATEST_LOG=%%i"
    goto :found
)

echo No log files found in logs directory.
exit /b 1

:found
echo Latest log file: logs\%LATEST_LOG%
echo.

echo ========================================
echo Recent ERROR entries:
echo ========================================
findstr /i "ERROR" "logs\%LATEST_LOG%" | tail -20

echo.
echo ========================================
echo Recent FATAL entries:
echo ========================================
findstr /i "FATAL" "logs\%LATEST_LOG%" | tail -10

echo.
echo ========================================
echo Recent Exception entries:
echo ========================================
findstr /i "exception" "logs\%LATEST_LOG%" | tail -10

echo.
echo ========================================
echo Last 20 log entries:
echo ========================================
tail -20 "logs\%LATEST_LOG%"