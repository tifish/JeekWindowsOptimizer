@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

del /q "bin\*.deps.json" "bin\*.runtimeconfig.json" "bin\Libs\*" 2>nul
rd /s /q "bin\Logs" 2>nul

rem Publish the main app (NetBeauty + ReadyToRun). The csproj also publishes the
rem single-file MCP adapter into bin\ so the release zip always includes it.
dotnet publish --configuration Release JeekWindowsOptimizer\JeekWindowsOptimizer.csproj
if errorlevel 1 pause

del /q /s bin\*.pdb 2>nul

endlocal
