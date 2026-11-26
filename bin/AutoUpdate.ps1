param(
    [Parameter(Position = 0)]
    [string]$StageDir,
    [string]$InstallDir = "",
    [ValidatePattern('^[\w.-]+$')]
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

# Resolve InstallDir in the script body rather than as a param() default:
# in Windows PowerShell 5.1, $PSScriptRoot is empty while param defaults are
# evaluated under `powershell.exe -File`.
if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    $InstallDir = $PSScriptRoot
}
if ([string]::IsNullOrWhiteSpace($InstallDir) -and $MyInvocation.MyCommand.Path) {
    $InstallDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    $InstallDir = (Get-Location).Path
}

$installDir = [System.IO.Path]::GetFullPath($InstallDir)
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
        if ($procPath -eq $exePath) {
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

$installStarted = $false
try {
    $stageDir = [System.IO.Path]::GetFullPath($StageDir)
    $stagePrefix = $stageDir.TrimEnd('\', '/') + '\'
    $installPrefix = $installDir.TrimEnd('\', '/') + '\'
    foreach ($dir in @($stagePrefix, $installPrefix)) {
        if ($dir.TrimEnd('\') -eq [System.IO.Path]::GetPathRoot($dir).TrimEnd('\')) {
            throw "The update package and installation directories must not be volume roots."
        }
    }
    if ($stagePrefix.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        $installPrefix.StartsWith($stagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The update package and installation directories must be separate."
    }

    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
        throw "The installation directory is missing $AppName.exe."
    }
    foreach ($required in @("$AppName.exe", "Runtime\$AppName.dll", "Runtime\$AppName.runtimeconfig.json", "Runtime\${AppName}Mcp.exe", "Data\Languages.tab")) {
        if (-not (Test-Path -LiteralPath (Join-Path $stageDir $required) -PathType Leaf)) {
            throw "Staged update package is missing ${required}: $stageDir"
        }
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
    # Keep user-owned directories. Copy changed files and prune obsolete ones,
    # retaining usable installed files until their replacements are available.
    $installStarted = $true
    robocopy $stageDir $installDir /MIR /XJ /R:3 /W:1 /XD Config Logs Activator /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "Failed to copy files (robocopy exit code $LASTEXITCODE)."
    }

    # Remove the staged package and its known sidecars. Preserve sibling files.
    $stageDir = $stagePrefix.TrimEnd('\')
    $updateRoot = Split-Path -Parent $stageDir
    $updateRootName = Split-Path -Leaf $updateRoot
    Remove-Item -Recurse -Force -LiteralPath $stageDir -ErrorAction SilentlyContinue
    if ((Split-Path -Leaf $stageDir) -eq 'package' -and
        ($updateRootName -eq "Update" -or $updateRootName -eq "$AppName-update")) {
        foreach ($file in @('version.txt', "$AppName.zip")) {
            $sidecar = Join-Path $updateRoot $file
            if (Test-Path -LiteralPath $sidecar -PathType Leaf) {
                Remove-Item -LiteralPath $sidecar -Force -ErrorAction SilentlyContinue
            }
        }
        try { [System.IO.Directory]::Delete($updateRoot) } catch [System.IO.IOException] {}
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
    if ($installStarted -and -not $NoRestart -and (Test-Path -LiteralPath $exePath)) {
        Start-Process -FilePath $exePath -WorkingDirectory $installDir
    }
    if (-not $NoRestart) {
        Start-Sleep -Seconds 5
    }
    Exit 1
}
