@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

del /q "bin\*.deps.json" "bin\*.runtimeconfig.json" "bin\Libs\*" 2>nul
rd /s /q "bin\Logs" 2>nul

for %%v in (Preview Enterprise Professional Community) do (
    set msbuild="C:\Program Files\Microsoft Visual Studio\2022\%%v\MSBuild\Current\Bin\amd64\MSBuild.exe"
    if exist "!msbuild!" (
        goto :build
    )
)

echo No MSBuild found
pause
exit /b

:build
%msbuild% JeekWindowsOptimizer.sln -t:Rebuild -p:Configuration=Release
if errorlevel 1 pause

endlocal
