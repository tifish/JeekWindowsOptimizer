@echo off
setlocal

fsutil dirty query %systemdrive% >nul 2>&1
if errorlevel 1 (
    set "JWO_SETUP_FILE=%~f0"
    powershell.exe -NoProfile -Command "$ErrorActionPreference='Stop'; exit (Start-Process -FilePath $env:JWO_SETUP_FILE -Verb RunAs -PassThru -Wait).ExitCode"
    if errorlevel 1 exit /b 1
    exit /b 0
)

echo Installing .NET...
set "JWO_PROGRAM_FILES=%ProgramFiles%"
if defined ProgramW6432 set "JWO_PROGRAM_FILES=%ProgramW6432%"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0dotnet-install.ps1" -Channel 10.0 -Runtime dotnet -Architecture x64 -InstallDir "%JWO_PROGRAM_FILES%\dotnet"
if errorlevel 1 exit /b %errorlevel%

echo Starting application...
start "" "%~dp0JeekWindowsOptimizer.exe"

endlocal
