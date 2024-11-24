@echo off
setlocal
cd /d "%~dp0"

del /q "bin\*.deps.json" "bin\*.runtimeconfig.json" "bin\Libs\*" "bin\Logs"

"C:\Program Files\Microsoft Visual Studio\2022\Preview\MSBuild\Current\Bin\amd64\MSBuild.exe" JeekWindowsOptimizer.sln -t:Rebuild -p:Configuration=Release
@REM dotnet build JeekWindowsOptimizer.sln -c Release

endlocal
