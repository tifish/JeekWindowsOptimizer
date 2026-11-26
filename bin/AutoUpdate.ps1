param(
    [Parameter(Position = 0)]
    [string]$StageDir,
    [string]$InstallDir = $PSScriptRoot,
    [string]$AppName = "JeekWindowsOptimizer",
    [int]$GracefulWaitSeconds = 5,
    [int]$CloseWaitSeconds = 5,
    [switch]$NoRestart
)

$ErrorActionPreference = "Stop"

# The app has already downloaded, extracted, and verified the update package
# into a staging folder before launching this script. All that remains here is
# the short critical window: wait for instances in this install directory to
# exit, swap the files, and restart.

if ([string]::IsNullOrWhiteSpace($StageDir)) {
    Exit 1
}

$stageDir = [System.IO.Path]::GetFullPath($StageDir)
$installDir = [System.IO.Path]::GetFullPath($InstallDir)
$installPrefix = $installDir.TrimEnd('\', '/') + '\'
$exePath = Join-Path $installDir "$AppName.exe"

try {
    $Host.UI.RawUI.WindowTitle = "$AppName Updater"
} catch {}

function Get-ProcessExecutablePath {
    param([System.Diagnostics.Process]$Process)

    try {
        $path = $Process.Path
        if (-not [string]::IsNullOrWhiteSpace($path)) {
            return [System.IO.Path]::GetFullPath($path)
        }
    } catch {}

    try {
        $wmi = Get-CimInstance Win32_Process -Filter "ProcessId = $($Process.Id)" -ErrorAction SilentlyContinue
        if ($wmi -and -not [string]::IsNullOrWhiteSpace($wmi.ExecutablePath)) {
            return [System.IO.Path]::GetFullPath($wmi.ExecutablePath)
        }
    } catch {}

    return $null
}

function Get-InstallAppProcesses {
    $matched = @()
    Get-Process -Name $AppName -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.Id -eq $PID) {
            return
        }
        $procPath = Get-ProcessExecutablePath -Process $_
        if ($procPath -and $procPath.StartsWith($installPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            $matched += $_
        }
    }
    return $matched
}

function Format-ProcessSummary {
    param([System.Diagnostics.Process[]]$Processes)

    return ($Processes | ForEach-Object {
        $started = ""
        try { $started = " (started $($_.StartTime.ToString('yyyy-MM-dd HH:mm:ss')))" } catch {}
        "PID $($_.Id)$started"
    }) -join ", "
}

function Wait-UntilInstallProcessesExit {
    param(
        [int]$TimeoutSeconds,
        [int]$ReportAfterSeconds = -1
    )

    $deadline = [DateTime]::UtcNow.AddSeconds([Math]::Max(0, $TimeoutSeconds))
    $reportAt = if ($ReportAfterSeconds -ge 0) { [DateTime]::UtcNow.AddSeconds($ReportAfterSeconds) } else { [DateTime]::MaxValue }
    $lastReported = ""

    while ($true) {
        $running = @(Get-InstallAppProcesses)
        if ($running.Count -eq 0) {
            return @()
        }

        if ([DateTime]::UtcNow -ge $reportAt) {
            $summary = Format-ProcessSummary -Processes $running
            if ($summary -ne $lastReported) {
                Write-Host "  Still running in ${installDir}: $summary" -ForegroundColor Yellow
                $lastReported = $summary
            }
        }

        if ([DateTime]::UtcNow -ge $deadline) {
            return $running
        }

        Start-Sleep -Milliseconds 250
    }
}

Write-Host "================================================================"
Write-Host " $AppName - Auto Update"
Write-Host "================================================================"
Write-Host ""
Write-Host "Please keep this window open. The app will restart automatically"
Write-Host "when the update is finished."
Write-Host ""

try {
    if (-not (Test-Path -LiteralPath (Join-Path $stageDir "$AppName.exe"))) {
        throw "Staged update package is missing $AppName.exe: $stageDir"
    }

    Write-Host "[1/3] Waiting for $AppName in $installDir to exit..."
    # Only wait for instances running from this install directory so Debug or
    # other-directory instances never block the update.
    $remaining = @(Wait-UntilInstallProcessesExit -TimeoutSeconds $GracefulWaitSeconds -ReportAfterSeconds ([Math]::Min(2, $GracefulWaitSeconds)))

    if ($remaining.Count -gt 0) {
        Write-Host "  Requesting remaining instance(s) to close: $(Format-ProcessSummary -Processes $remaining)" -ForegroundColor Yellow
        foreach ($proc in $remaining) {
            try { $null = $proc.CloseMainWindow() } catch {}
        }
        $remaining = @(Wait-UntilInstallProcessesExit -TimeoutSeconds $CloseWaitSeconds)
    }

    if ($remaining.Count -gt 0) {
        Write-Host "  Stopping unresponsive instance(s): $(Format-ProcessSummary -Processes $remaining)" -ForegroundColor Yellow
        foreach ($proc in $remaining) {
            try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch {}
        }
        $remaining = @(Wait-UntilInstallProcessesExit -TimeoutSeconds 10)
    }

    if ($remaining.Count -gt 0) {
        throw "Timed out waiting for $AppName in $installDir to exit: $(Format-ProcessSummary -Processes $remaining)"
    }

    Write-Host "[2/3] Installing files..."
    # Preserve portable user data, logs, this script so it is not deleted mid-run,
    # and the user-supplied Tools\Activator (not shipped in the package).
    # Matches install.ps1's robocopy /XD list.
    $preserveNames = @("Config", "Logs", "AutoUpdate.ps1")
    $maxAttempts = 5
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        try {
            Get-ChildItem -LiteralPath $installDir -Force -ErrorAction SilentlyContinue |
                Where-Object { $preserveNames -notcontains $_.Name } |
                ForEach-Object {
                    if ($_.PSIsContainer -and $_.Name -eq "Tools") {
                        Get-ChildItem -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue |
                            Where-Object { $_.Name -ne "Activator" } |
                            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
                    } else {
                        Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
                    }
                }

            Copy-Item -Path (Join-Path $stageDir "*") -Destination $installDir -Recurse -Force
            break
        }
        catch {
            if ($attempt -ge $maxAttempts) {
                throw
            }
            Start-Sleep -Milliseconds 300
        }
    }

    # Remove the update root the app created (typically
    # %LOCALAPPDATA%\JeekWindowsOptimizer\Update containing package\ + version.txt).
    # Fall back to deleting only the package folder for legacy layouts.
    $updateRoot = Split-Path -Parent $stageDir
    $updateRootName = Split-Path -Leaf $updateRoot
    if ($updateRootName -eq "Update" -or $updateRootName -eq "$AppName-update") {
        Remove-Item -Recurse -Force -LiteralPath $updateRoot -ErrorAction SilentlyContinue
    } else {
        Remove-Item -Recurse -Force -LiteralPath $stageDir -ErrorAction SilentlyContinue
    }

    Write-Host "[3/3] Restarting $AppName..."
    if (-not $NoRestart -and (Test-Path -LiteralPath $exePath)) {
        Start-Process -FilePath $exePath -WorkingDirectory $installDir
    }

    Write-Host ""
    Write-Host "Update completed." -ForegroundColor Green
}
catch {
    Write-Host ""
    Write-Host "Update failed: $($_.Exception.Message)" -ForegroundColor Red
    # Best effort: bring the app back even if the install failed.
    if (-not $NoRestart -and (Test-Path -LiteralPath $exePath)) {
        Start-Process -FilePath $exePath -WorkingDirectory $installDir
    }
    if (-not $NoRestart) {
        Start-Sleep -Seconds 5
    }
    Exit 1
}
