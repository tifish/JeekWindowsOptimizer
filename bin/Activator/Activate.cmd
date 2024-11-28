@echo off
setlocal

"%~dp0UnRAR.exe" x -y "%~dp0HEU_KMS_Activator.rar" -pE29E94A1 "%temp%"
for %%f in ("%temp%\HEU_KMS_Activator*.exe") do (
    "%%f" /smart
    del /f /q "%%f"
)

endlocal
