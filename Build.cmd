@echo off
setlocal
cd /d "%~dp0"

dotnet build JeekWindowsOptimizer.sln
if errorlevel 1 pause

rem The MCP stdio adapter ships beside the app as a single file, so NetBeauty
rem never sees its runtimeconfig and the next app build cannot fail over libloader.dll.
dotnet publish tools\JeekWindowsOptimizerMcp\JeekWindowsOptimizerMcp.csproj
if errorlevel 1 pause

endlocal
