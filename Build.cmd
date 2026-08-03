@echo off
setlocal
cd /d "%~dp0"

rem Release build into bin\. Cleans stale outputs first so NetBeauty / dependency
rem churn does not leave orphan DLLs. Strips PDBs for a shippable tree.
rem The main project also publishes the single-file MCP adapter beside the app
rem (rename-first so a leftover side-by-side process cannot lock the publish).
rem Agents must not launch bin\JeekWindowsOptimizerMcp.exe — use the fixed
rem LocalAppData path or JeekWindowsOptimizerDebugMcp.cmd.
del /q "bin\*.deps.json" "bin\*.runtimeconfig.json" "bin\*.dll" "bin\*.pdb" "bin\Libs\*" 2>nul
rd /s /q "bin\Logs" 2>nul

dotnet build --configuration Release JeekWindowsOptimizer\JeekWindowsOptimizer.csproj
if errorlevel 1 pause

del /q /s bin\*.pdb 2>nul

endlocal
