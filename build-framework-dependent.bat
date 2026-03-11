@echo off
REM Build script for SQL Health Assessment - Framework-Dependent (Smallest)
REM Requires .NET 8 Desktop Runtime on target machines
REM Expected size: 35-40 MB

echo Building SQL Health Assessment (Framework-Dependent)...
echo.

dotnet publish SqlHealthAssessment.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained false ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o publish\framework-dependent

echo.
echo Build complete! Output: publish\framework-dependent\
echo Size: ~35-40 MB
echo Prerequisites: .NET 8 Desktop Runtime required on target machines
echo.
pause
