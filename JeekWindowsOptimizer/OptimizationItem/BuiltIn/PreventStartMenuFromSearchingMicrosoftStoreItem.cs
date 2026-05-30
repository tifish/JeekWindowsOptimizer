using System.Diagnostics;
using JeekTools;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ZLogger;

namespace JeekWindowsOptimizer;

public class PreventStartMenuFromSearchingMicrosoftStoreItem : OptimizationItem
{
    private static readonly ILogger Log =
        LogManager.CreateLogger<PreventStartMenuFromSearchingMicrosoftStoreItem>();

    private const string AppServicePackageIdSubKey =
        @"Software\Classes\Extensions\ContractId\Windows.AppService\PackageId";
    private const string StorePackageName = "Microsoft.WindowsStore";
    private const string StorePackageNamePrefix = "Microsoft.WindowsStore_";
    private const string WindowsSearchActivatableClassId =
        "App.AppX5v3n0vdt2m8tz7k72r2yar8avh3wc5ew.mca";
    private const string WindowsSearchServiceName = "microsoft-store-windowssearch";
    private const string DisabledWindowsSearchServiceName =
        "disabled-microsoft-store-windowssearch";

    public override string GroupNameKey => "StartMenu";
    public override string NameKey => "PreventStartMenuFromSearchingMicrosoftStoreName";
    public override string DescriptionKey =>
        "PreventStartMenuFromSearchingMicrosoftStoreDescription";

    public override Task Initialize()
    {
        IsOptimized = !HasWindowsSearchAppService();
        return Task.CompletedTask;
    }

    protected override async Task<bool> IsOptimizedChanging(bool value)
    {
        try
        {
            if (value)
                DisableWindowsSearchAppServices();
            else
                await RestoreWindowsSearchAppServices();

            RestartSearchShellProcesses();
            return value ? !HasWindowsSearchAppService() : HasWindowsSearchAppService();
        }
        catch (Exception ex)
        {
            Log.ZLogError(ex, $"Failed to change Microsoft Store search in Start menu");
            return false;
        }
    }

    private static bool HasWindowsSearchAppService()
    {
        return FindStoreAppServices(WindowsSearchServiceName).Count > 0;
    }

    private static void DisableWindowsSearchAppServices()
    {
        foreach (var registration in FindStoreAppServices(WindowsSearchServiceName))
            SetServiceName(registration, DisabledWindowsSearchServiceName);
    }

    private static async Task RestoreWindowsSearchAppServices()
    {
        foreach (var registration in FindStoreAppServices(DisabledWindowsSearchServiceName))
            SetServiceName(registration, WindowsSearchServiceName);

        if (HasWindowsSearchAppService())
            return;

        var packageId =
            GetStorePackageIds().FirstOrDefault()
            ?? await MicrosoftStore.GetPackageFullName(StorePackageName);
        if (!string.IsNullOrEmpty(packageId))
            CreateWindowsSearchAppService(packageId);
    }

    private static void SetServiceName(
        StoreAppServiceRegistration registration,
        string serviceName
    )
    {
        using var customPropertiesKey = Registry.CurrentUser.OpenSubKey(
            $@"{registration.PackageSubKeyPath}\ActivatableClassId\{registration.ActivatableClassId}\CustomProperties",
            true
        );
        customPropertiesKey?.SetValue("ServiceName", serviceName, RegistryValueKind.String);
    }

    private static void CreateWindowsSearchAppService(string packageId)
    {
        using var classKey = Registry.CurrentUser.CreateSubKey(
            $@"{AppServicePackageIdSubKey}\{packageId}\ActivatableClassId\{WindowsSearchActivatableClassId}",
            true
        );
        if (classKey == null)
            return;

        classKey.SetValue("Vendor", "Microsoft Corporation", RegistryValueKind.String);
        classKey.SetValue(
            "Icon",
            $@"@{{{packageId}?ms-resource://Microsoft.WindowsStore/Files/Assets/AppTiles/StoreMedTile.png}}",
            RegistryValueKind.String
        );
        classKey.SetValue("DisplayName", "Microsoft Store", RegistryValueKind.String);
        classKey.SetValue(
            "Description",
            $@"@{{{packageId}?ms-resource://Microsoft.WindowsStore/Resources/StoreDescription}}",
            RegistryValueKind.String
        );

        using var customPropertiesKey = classKey.CreateSubKey("CustomProperties", true);
        if (customPropertiesKey == null)
            return;

        customPropertiesKey.SetValue("SupportsRemoteSystems", 0, RegistryValueKind.DWord);
        customPropertiesKey.SetValue(
            "ServiceName",
            WindowsSearchServiceName,
            RegistryValueKind.String
        );
    }

    private static IEnumerable<string> GetStorePackageIds()
    {
        using var packageIdsKey = Registry.CurrentUser.OpenSubKey(AppServicePackageIdSubKey);
        if (packageIdsKey == null)
            yield break;

        foreach (var packageId in packageIdsKey.GetSubKeyNames())
        {
            if (packageId.StartsWith(StorePackageNamePrefix, StringComparison.OrdinalIgnoreCase))
                yield return packageId;
        }
    }

    private static List<StoreAppServiceRegistration> FindStoreAppServices(params string[] names)
    {
        var registrations = new List<StoreAppServiceRegistration>();
        var serviceNames = names.ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var packageIdsKey = Registry.CurrentUser.OpenSubKey(AppServicePackageIdSubKey);
        if (packageIdsKey == null)
            return registrations;

        foreach (var packageId in GetStorePackageIds())
        {
            var packageSubKeyPath = $@"{AppServicePackageIdSubKey}\{packageId}";
            using var classIdsKey = Registry.CurrentUser.OpenSubKey(
                $@"{packageSubKeyPath}\ActivatableClassId"
            );
            if (classIdsKey == null)
                continue;

            // The class id changes with Store versions, so match the stable service name.
            foreach (var classId in classIdsKey.GetSubKeyNames())
            {
                using var customPropertiesKey = Registry.CurrentUser.OpenSubKey(
                    $@"{packageSubKeyPath}\ActivatableClassId\{classId}\CustomProperties"
                );

                if (
                    customPropertiesKey?.GetValue("ServiceName") is string serviceName
                    && serviceNames.Contains(serviceName)
                )
                    registrations.Add(new StoreAppServiceRegistration(packageSubKeyPath, classId));
            }
        }

        return registrations;
    }

    private static void RestartSearchShellProcesses()
    {
        foreach (var processName in new[] { "SearchHost", "SearchApp", "StartMenuExperienceHost" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception ex)
                    {
                        Log.ZLogError(ex, $"Failed to restart {processName}");
                    }
                }
            }
        }
    }

    private sealed record StoreAppServiceRegistration(
        string PackageSubKeyPath,
        string ActivatableClassId
    );
}
