@echo off
REM Build script for SQLTriage - Self-Contained Optimized
REM No runtime dependencies required
REM Expected size: 100-120 MB

echo Building SQLTriage (Self-Contained Optimized)...
echo.

dotnet publish SQLTriage.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -o publish\self-contained

echo.
echo Build complete! Output: publish\self-contained\
echo Size: ~100-120 MB
echo Prerequisites: None (includes .NET Runtime)
echo.
pause
