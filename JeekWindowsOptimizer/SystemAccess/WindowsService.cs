using System.Management;
using Microsoft.Win32;

namespace JeekWindowsOptimizer;

public class WindowsService(string serviceName) : IDisposable
{
    private readonly string _serviceName = serviceName;
    private readonly ManagementObject _serviceObject = new(
        $"Win32_Service.Name=\"{EscapeWmiString(serviceName)}\""
    );
    private bool? _wmiExists;

    public void Dispose()
    {
        _serviceObject.Dispose();
    }

    /// <summary>
    /// Finds installed service names matching a prefix (e.g. <c>CDPUserSvc</c> matches
    /// the template and <c>CDPUserSvc_xxxxx</c> per-user instances).
    /// </summary>
    public static List<string> FindNamesByPrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return [];

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // User-service templates (e.g. CDPUserSvc) often do not appear in Win32_Service.
        if (RegistryKeyExists(prefix))
            names.Add(prefix);

        try
        {
            var likePattern = EscapeWqlLike(prefix) + "%";
            using var searcher = new ManagementObjectSearcher(
                $"SELECT Name FROM Win32_Service WHERE Name LIKE '{likePattern}'"
            );
            using var results = searcher.Get();

            foreach (ManagementBaseObject result in results)
            {
                using (result)
                {
                    if (result["Name"] is string name && !string.IsNullOrEmpty(name))
                        names.Add(name);
                }
            }
        }
        catch
        {
            // Fall through to registry enumeration.
        }

        // Registry covers templates and instances WMI may miss.
        try
        {
            using var servicesKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services"
            );
            if (servicesKey is not null)
            {
                foreach (var name in servicesKey.GetSubKeyNames())
                {
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        names.Add(name);
                }
            }
        }
        catch
        {
            // Ignore registry enumeration failures.
        }

        var list = names.ToList();
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    private static string EscapeWmiString(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string EscapeWqlLike(string value) =>
        value.Replace("\\", "\\\\").Replace("'", "\\'").Replace("%", "[%]");

    private static string ServiceRegistryPath(string name) =>
        $@"SYSTEM\CurrentControlSet\Services\{name}";

    private static bool RegistryKeyExists(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(ServiceRegistryPath(name));
        return key is not null;
    }

    private bool WmiExists()
    {
        if (_wmiExists.HasValue)
            return _wmiExists.Value;

        try
        {
            _serviceObject.Get();
            _wmiExists = true;
        }
        catch
        {
            _wmiExists = false;
        }

        return _wmiExists.Value;
    }

    public bool Exists()
    {
        return WmiExists() || RegistryKeyExists(_serviceName);
    }

    public bool Start()
    {
        if (!WmiExists())
            return false;

        try
        {
            return (uint)_serviceObject.InvokeMethod("StartService", null) == 0;
        }
        catch
        {
            return false;
        }
    }

    public bool Stop()
    {
        if (!WmiExists())
            return true; // Nothing running that WMI can see.

        try
        {
            return (uint)_serviceObject.InvokeMethod("StopService", null) == 0;
        }
        catch
        {
            return false;
        }
    }

    public bool Pause()
    {
        if (!WmiExists())
            return false;

        return (uint)_serviceObject.InvokeMethod("PauseService", null) == 0;
    }

    public bool Resume()
    {
        if (!WmiExists())
            return false;

        return (uint)_serviceObject.InvokeMethod("ResumeService", null) == 0;
    }

    public void Restart()
    {
        Stop();
        Start();
    }

    public enum StartMode
    {
        Boot = 0,
        System = 1,
        Automatic = 2,
        Manual = 3,
        Disabled = 4,
    }

    public StartMode GetStartMode()
    {
        // Prefer the registry Start value: WMI does not always reflect registry edits for
        // per-user service instances until reboot, and templates may not appear in WMI.
        var fromRegistry = GetStartModeFromRegistry();
        if (fromRegistry.HasValue)
            return fromRegistry.Value;

        if (WmiExists())
        {
            try
            {
                var startMode = (string)_serviceObject.GetPropertyValue("StartMode");
                return startMode switch
                {
                    "Boot" => StartMode.Boot,
                    "System" => StartMode.System,
                    "Auto" => StartMode.Automatic,
                    "Manual" => StartMode.Manual,
                    "Disabled" => StartMode.Disabled,
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(startMode),
                        startMode,
                        null
                    ),
                };
            }
            catch
            {
                // Fall through.
            }
        }

        throw new InvalidOperationException(
            $"Cannot read start mode for service '{_serviceName}'."
        );
    }

    public bool SetStartMode(StartMode startMode)
    {
        // Prefer WMI when it works (normal Win32 services).
        if (WmiExists())
        {
            try
            {
                var startModeString = startMode switch
                {
                    StartMode.Boot => "Boot",
                    StartMode.System => "System",
                    StartMode.Automatic => "Automatic",
                    StartMode.Manual => "Manual",
                    StartMode.Disabled => "Disabled",
                    _ => throw new ArgumentOutOfRangeException(nameof(startMode), startMode, null),
                };

                if ((uint)_serviceObject.InvokeMethod("ChangeStartMode", [startModeString]) == 0)
                    return true;
            }
            catch
            {
                // Fall through to registry (needed for per-user service instances).
            }
        }

        // Registry is required for user-service templates (not in Win32_Service) and
        // instances that reject ChangeStartMode with ERROR_INVALID_PARAMETER (21).
        return SetStartModeViaRegistry(startMode);
    }

    private StartMode? GetStartModeFromRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceRegistryPath(_serviceName));
            if (key?.GetValue("Start") is int start)
            {
                return start switch
                {
                    0 => StartMode.Boot,
                    1 => StartMode.System,
                    2 => StartMode.Automatic,
                    3 => StartMode.Manual,
                    4 => StartMode.Disabled,
                    _ => null,
                };
            }
        }
        catch
        {
            // Ignore.
        }

        return null;
    }

    private bool SetStartModeViaRegistry(StartMode startMode)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                ServiceRegistryPath(_serviceName),
                writable: true
            );
            if (key is null)
                return false;

            var startValue = startMode switch
            {
                StartMode.Boot => 0,
                StartMode.System => 1,
                StartMode.Automatic => 2,
                StartMode.Manual => 3,
                StartMode.Disabled => 4,
                _ => throw new ArgumentOutOfRangeException(nameof(startMode), startMode, null),
            };

            key.SetValue("Start", startValue, RegistryValueKind.DWord);
            return GetStartModeFromRegistry() == startMode;
        }
        catch
        {
            return false;
        }
    }
}
