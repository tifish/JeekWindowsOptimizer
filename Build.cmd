@echo off
setlocal
cd /d "%~dp0"

rem Release build into bin\ (nested Runtime\ layout). Cleans stale outputs first so
rem dependency churn does not leave orphan DLLs. Strips PDBs for a shippable tree.
rem The main project also publishes the single-file MCP adapter into bin\Runtime\
rem (rename-first so a leftover side-by-side process cannot lock the publish).
rem Agents must not launch bin\Runtime\JeekWindowsOptimizerMcp.exe - use the fixed
rem LocalAppData path or JeekWindowsOptimizerDebugMcp.cmd.
del /q "bin\*.deps.json" "bin\*.runtimeconfig.json" "bin\*.dll" "bin\*.pdb" "bin\JeekWindowsOptimizerMcp.exe" 2>nul
rd /s /q "bin\Runtime" 2>nul
rd /s /q "bin\Libs" 2>nul

dotnet build --configuration Release JeekWindowsOptimizer\JeekWindowsOptimizer.csproj
if errorlevel 1 exit /b %errorlevel%

del /q /s bin\*.pdb 2>nul

endlocal
