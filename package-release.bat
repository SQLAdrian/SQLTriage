@echo off
REM Package release build for GitHub
echo Building Release...
dotnet publish -c Release

echo.
echo Packaging for GitHub Release...
powershell -ExecutionPolicy Bypass -File "%~dp0package-release.ps1"
pause
