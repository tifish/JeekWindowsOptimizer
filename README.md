# JeekWindowsOptimizer

A Windows optimization and maintenance tool featuring performance, stability, and usability tweaks, antivirus detection, system-drive cleanup and folder/WSL/Docker relocation, whitelist-based startup management, and a built-in diagnostics toolbox.

## Install

Run in PowerShell:

```powershell
irm https://raw.githubusercontent.com/tifish/JeekWindowsOptimizer/main/install.ps1 | iex
```

Mirror for mainland China:

```powershell
irm https://ghfast.top/https://raw.githubusercontent.com/tifish/JeekWindowsOptimizer/main/install.ps1 | iex
```

The installer downloads the latest release, installs to `%LOCALAPPDATA%\Programs\JeekWindowsOptimizer`, creates a Start Menu shortcut, and starts the app. It writes nothing to the registry.

## Uninstall

Quit the app, then delete `%LOCALAPPDATA%\Programs\JeekWindowsOptimizer` and the Start Menu shortcut.
