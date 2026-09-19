using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer;

public static class MicrosoftStore
{
    private static readonly ILogger Log = LogManager.CreateLogger<MainViewModel>();

    private static Task? _initialization;

    /// <summary>
    /// Starts the one-time session setup and package snapshot; later calls return the same task,
    /// so it can run alongside other startup detection.
    /// </summary>
    public static Task Initialize() => _initialization ??= InitializeCore(recordTiming: true);

    /// <summary>
    /// Replace the cached package list before re-detecting optimization items; retries the
    /// session setup if it failed at startup.
    /// </summary>
    public static async Task RefreshSnapshot()
    {
        await Initialize();
        _initialization = InitializeCore(recordTiming: false);
        await _initialization;
    }

    /// <summary>How long the startup session setup and package snapshot took; for diagnostics.</summary>
    public static long InitializeMilliseconds { get; private set; }

    private static bool _sessionReady;

    private static async Task InitializeCore(bool recordTiming)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await InitializeSessionAndSnapshot();
        }
        finally
        {
            if (recordTiming)
                InitializeMilliseconds = stopwatch.ElapsedMilliseconds;
        }
    }

    private static async Task InitializeSessionAndSnapshot()
    {
        await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            async () =>
            {
                if (!_sessionReady)
                {
                    try
                    {
                        PowerShellService.Commands.Clear();
                        await PowerShellService
                            .AddCommand("Set-ExecutionPolicy")
                            .AddParameter("Scope", "Process")
                            .AddParameter("ExecutionPolicy", "Bypass")
                            .InvokeAsync();

                        PowerShellService.Commands.Clear();
                        PowerShellService.Streams.ClearStreams();
                        await PowerShellService
                            .AddCommand("Import-Module")
                            .AddParameter("Name", "AppX")
                            .AddParameter("UseWindowsPowerShell")
                            .InvokeAsync();
                        if (PowerShellService.HadErrors)
                            Log.ZLogError(
                                $"Failed to import the AppX module: {string.Join("; ", PowerShellService.Streams.Error)}"
                            );
                        else
                            _sessionReady = true;
                    }
                    catch (Exception e)
                    {
                        Log.ZLogError(e, $"Failed to set up the AppX session");
                    }
                }

                // A failed refresh must fall back to live queries, never an old snapshot.
                _installedPackageNames = null;
                try
                {
                    // One snapshot for all items: each Get-AppxPackage call costs ~0.2 s.
                    PowerShellService.Commands.Clear();
                    PowerShellService.Streams.ClearStreams();
                    // Project names inside the Windows PowerShell compatibility session:
                    // shipping full package objects back takes ~2 s instead of ~0.2 s.
                    PowerShellService.AddScript(
                        $"Invoke-Command -Session ({CompatSession}) "
                            + "{ Get-AppxPackage | ForEach-Object Name }"
                    );
                    var results = await PowerShellService.InvokeAsync();
                    // A partial list would report missing packages as uninstalled.
                    if (PowerShellService.HadErrors || results.Count == 0)
                        Log.ZLogWarning($"Package list unavailable, falling back to per-package queries");
                    else
                        _installedPackageNames = new HashSet<string>(
                            results
                                .Select(result => result?.BaseObject as string)
                                .OfType<string>(),
                            StringComparer.OrdinalIgnoreCase
                        );
                }
                catch (Exception e)
                {
                    Log.ZLogError(e, $"Failed to list installed packages");
                }
            }
        );
    }

    // Detection and removal both cover the current user only: other accounts are left to
    // their owners, and -AllUsers would also list the Staged copy Windows keeps for new
    // accounts, so an uninstalled package would still look installed.
    private const string CompatSession = "Get-PSSession -Name WinPSCompatSession";

    private static HashSet<string>? _installedPackageNames;

    /// <summary>
    /// Checks the snapshot taken by <see cref="Initialize"/>; falls back to a live query
    /// when the snapshot is unavailable.
    /// </summary>
    public static async Task<bool> IsPackageInstalled(string packageName)
    {
        await Initialize();
        var snapshot = _installedPackageNames;
        return snapshot is not null
            ? snapshot.Contains(packageName)
            : await HasPackage(packageName);
    }

    /// <summary>Runs a script block with one <c>$name</c> argument inside the compatibility session.</summary>
    private static void AddSessionScript(string scriptBlock, string packageName)
    {
        PowerShellService.Commands.Clear();
        PowerShellService.Streams.ClearStreams();
        PowerShellService
            .AddScript(
                $"param($name) Invoke-Command -Session ({CompatSession}) -ArgumentList $name "
                    + $"-ScriptBlock {{ param($name) {scriptBlock} }}"
            )
            .AddParameter("name", packageName);
    }

    /// <summary>
    /// Snapshot membership and live check for the current user, plus every account's install
    /// state and any provisioned copy for context; for diagnostics.
    /// </summary>
    public static async Task<string> Describe(string packageName)
    {
        await Initialize();
        var snapshot = _installedPackageNames;
        var live = await HasPackage(packageName);
        var states = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            async () =>
            {
                AddSessionScript(
                    "Get-AppxPackage -AllUsers -Name $name | ForEach-Object { $p = $_; "
                        + "$p.PackageUserInformation | ForEach-Object { "
                        + "\"$($p.PackageFullName) $($_.UserSecurityId.Sid) $($_.InstallState)\" } }; "
                        + "Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq $name | "
                        + "ForEach-Object { \"provisioned $($_.PackageName)\" }",
                    packageName
                );
                return string.Join(
                    Environment.NewLine,
                    (await PowerShellService.InvokeAsync()).Select(result => $"  {result}")
                );
            }
        );
        return $"snapshot={(snapshot is null ? "unavailable" : snapshot.Contains(packageName))} "
            + $"installed_live={live}{Environment.NewLine}{states}";
    }

    /// <summary>
    /// Live check for the current user. Throws when the query fails: reporting "not installed"
    /// would mark the item optimized.
    /// </summary>
    public static async Task<bool> HasPackage(string packageName)
    {
        return await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            async () =>
            {
                AddSessionScript("@(Get-AppxPackage -Name $name).Count -gt 0", packageName);
                var results = await PowerShellService.InvokeAsync();
                if (PowerShellService.HadErrors || results.Count == 0)
                    throw new InvalidOperationException(
                        $"Failed to check if package {packageName} exists: "
                            + string.Join("; ", PowerShellService.Streams.Error)
                    );
                return results[0]?.BaseObject is true;
            }
        );
    }

    public static async Task UninstallPackage(string packageName)
    {
        await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            async () =>
            {
                AddSessionScript("Get-AppxPackage -Name $name | Remove-AppxPackage", packageName);
                await PowerShellService.InvokeAsync();
                foreach (var error in PowerShellService.Streams.Error)
                    Log.ZLogWarning($"Uninstalling {packageName}: {error}");
            }
        );
    }
}
