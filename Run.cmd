@echo off
setlocal
cd /d "%~dp0"

dotnet build JeekWindowsOptimizer\JeekWindowsOptimizer.csproj
if errorlevel 1 (
    pause
    exit /b 1
)

dotnet publish tools\JeekWindowsOptimizerMcp\JeekWindowsOptimizerMcp.csproj
if errorlevel 1 (
    pause
    exit /b 1
)

start "" "%~dp0bin\JeekWindowsOptimizer.exe"

endlocal
