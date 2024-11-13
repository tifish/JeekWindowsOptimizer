@echo off
setlocal
cd /d "%~dp0"

del /q "bin\*.deps.json" "bin\*.runtimeconfig.json" "bin\Libs\*"
dotnet build JeekWindowsOptimizer.sln -c Release

endlocal
