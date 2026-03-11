@echo off
REM DeployPublish.bat - Publishes the application to the .\publish folder
REM Usage: Run this script from the project root directory

echo ========================================
echo SqlHealthAssessment Deploy Script
echo ========================================
echo.

REM Step 0: Clean publish folder
echo [0/4] Cleaning publish folder...
if exist "publish" rmdir /S /Q "publish"
mkdir publish

echo.

REM Step 1: Deploy SQLWATCH database
echo [1/4] Deploying SQLWATCH database...
sqlcmd -S . -E -i "SQLWATCH_db\01_CreateSQLWATCHDB.sql"
if errorlevel 1 (
    echo ERROR: SQLWATCH database creation failed!
    exit /b 1
)
sqlcmd -S . -E -d SQLWATCH -i "SQLWATCH_db\02_PostSQLWATCHDBcreate.sql"
if errorlevel 1 (
    echo ERROR: SQLWATCH post-creation scripts failed!
    exit /b 1
)
echo SQLWATCH database deployed successfully.
echo.

REM Step 2: Restore and publish the application
echo [2/4] Restoring and publishing application...
dotnet restore SqlHealthAssessment.csproj -r win-x64
if errorlevel 1 (
    echo ERROR: Restore failed!
    exit /b 1
)
dotnet publish SqlHealthAssessment.csproj -c Release -r win-x64 --self-contained true -o ./publish
if errorlevel 1 (
    echo ERROR: Publish failed!
    exit /b 1
)
echo.

REM Step 3: Verify deployment
echo [3/4] Verifying SQLWATCH deployment...
sqlcmd -S . -E -d SQLWATCH -Q "SELECT COUNT(*) FROM sys.tables WHERE name LIKE 'sqlwatch%%'" -h -1
if errorlevel 1 (
    echo WARNING: Could not verify SQLWATCH tables
)
echo.

echo ========================================
echo Deployment complete!
echo Database: SQLWATCH deployed
echo Output location: .\publish
echo ========================================
echo.
echo To run the application:
echo   cd publish
echo   SqlHealthAssessment.exe
echo.

REM Optional: List the final structure
echo Final publish folder structure:
tree /F publish 2>nul || dir /B publish
