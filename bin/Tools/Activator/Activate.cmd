@echo off
setlocal

"%~dp0UnRAR.exe" x -y "%~dp0Activator.rar" -pE29E94A1 "%temp%"
for %%f in ("%temp%\*Activator*.exe") do (
    "%%f" /smart
    del /f /q "%%f"
)

endlocal
