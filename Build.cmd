@echo off
setlocal
cd /d "%~dp0"

rem Builds the app into bin\. The main project also publishes the single-file MCP
rem adapter beside it (rename-first so a leftover side-by-side process cannot lock
rem the publish). Agents must not launch bin\JeekWindowsOptimizerMcp.exe — use the
rem fixed LocalAppData path or JeekWindowsOptimizerDebugMcp.cmd.
dotnet build JeekWindowsOptimizer\JeekWindowsOptimizer.csproj
if errorlevel 1 pause

endlocal
