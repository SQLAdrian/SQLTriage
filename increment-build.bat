@echo off
REM Increment build number before debug build
powershell -ExecutionPolicy Bypass -File "%~dp0increment-build.ps1"
pause
