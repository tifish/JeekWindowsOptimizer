$ErrorActionPreference = "Stop"
$appName = "JeekWindowsOptimizer"

if ($args.Count -eq 0) {
    Exit 1
}

# The app has already downloaded, extracted and validated the update package.
$stageDir = $args[0]
$installDir = $PSScriptRoot
$stageRoot = Split-Path -Parent $stageDir

$Host.UI.RawUI.WindowTitle = "$appName Updater"

Write-Host "================================================================"
Write-Host " $appName - Auto Update"
Write-Host "================================================================"
Write-Host ""
Write-Host "Please keep this window open. The app will restart automatically"
Write-Host "when the update is finished."
Write-Host ""

try {
    if (-not (Test-Path -LiteralPath (Join-Path $stageDir "$appName.exe"))) {
        Write-Host "Staged update package is missing $appName.exe." -ForegroundColor Red
        Start-Sleep -Seconds 5
        Exit 1
    }

    Write-Host "[1/3] Waiting for $appName to exit..."
    Get-Process -Name $appName -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $_.WaitForExit()
        }
        catch {}
    }

    Write-Host "[2/3] Installing files..."
    # Preserve portable user data, logs, and the updater itself.
    $preserveNames = @("Config", "Logs", "AutoUpdate.ps1")
    Get-ChildItem -LiteralPath $installDir -Force -ErrorAction SilentlyContinue |
    Where-Object { $preserveNames -notcontains $_.Name } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    Copy-Item -Path (Join-Path $stageDir "*") -Destination $installDir -Recurse -Force

    Remove-Item -Recurse -Force -LiteralPath $stageRoot -ErrorAction SilentlyContinue

    Write-Host "[3/3] Restarting $appName..."
    $exePath = Join-Path $installDir "$appName.exe"
    if (Test-Path -LiteralPath $exePath) {
        Start-Process -FilePath $exePath
    }

    Write-Host ""
    Write-Host "Update completed." -ForegroundColor Green
}
catch {
    Write-Host ""
    Write-Host "Update failed: $($_.Exception.Message)" -ForegroundColor Red
    Start-Sleep -Seconds 5
    Exit 1
}
