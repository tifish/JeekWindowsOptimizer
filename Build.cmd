@echo off
setlocal
cd /d "%~dp0"

dotnet build JeekWindowsOptimizer.sln
if errorlevel 1 pause

endlocal
