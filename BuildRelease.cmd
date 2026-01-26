@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

del /q "bin\*.deps.json" "bin\*.runtimeconfig.json" "bin\Libs\*" 2>nul
rd /s /q "bin\Logs" 2>nul

dotnet build --configuration Release JeekWindowsOptimizer.sln
if errorlevel 1 pause

endlocal
