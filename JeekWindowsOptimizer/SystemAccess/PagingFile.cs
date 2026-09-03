using System.Management;

namespace JeekWindowsOptimizer;

/// <summary>
///     Paging file configuration through WMI. Settings take effect after a reboot;
///     <see cref="GetUsage" /> reports the files that exist right now.
/// </summary>
public static class PagingFile
{
    public readonly record struct Setting(string Path, uint InitialSizeMb, uint MaximumSizeMb)
    {
        public bool IsSystemManaged => InitialSizeMb == 0 && MaximumSizeMb == 0;
    }

    public readonly record struct Usage(string Path, long AllocatedBytes, long CurrentUsageBytes);

    public readonly record struct State(
        bool AutomaticallyManaged,
        IReadOnlyList<Setting> Settings,
        IReadOnlyList<Usage> Usages
    );

    public static State GetState()
    {
        return new State(IsAutomaticallyManaged(), GetSettings(), GetUsage());
    }

    public static bool IsAutomaticallyManaged()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem"
            );
            foreach (ManagementObject system in searcher.Get())
            {
                using (system)
                    return system["AutomaticManagedPagefile"] is true;
            }
        }
        catch
        {
            // WMI unavailable; assume the Windows default.
        }

        return true;
    }

    public static IReadOnlyList<Setting> GetSettings()
    {
        var settings = new List<Setting>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, InitialSize, MaximumSize FROM Win32_PageFileSetting"
            );
            foreach (ManagementObject setting in searcher.Get())
            {
                using (setting)
                {
                    settings.Add(
                        new Setting(
                            setting["Name"] as string ?? "",
                            Convert.ToUInt32(setting["InitialSize"] ?? 0u),
                            Convert.ToUInt32(setting["MaximumSize"] ?? 0u)
                        )
                    );
                }
            }
        }
        catch
        {
            // WMI unavailable.
        }

        return settings;
    }

    public static IReadOnlyList<Usage> GetUsage()
    {
        var usages = new List<Usage>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, AllocatedBaseSize, CurrentUsage FROM Win32_PageFileUsage"
            );
            foreach (ManagementObject usage in searcher.Get())
            {
                using (usage)
                {
                    usages.Add(
                        new Usage(
                            usage["Name"] as string ?? "",
                            Convert.ToInt64(usage["AllocatedBaseSize"] ?? 0u) * 1024 * 1024,
                            Convert.ToInt64(usage["CurrentUsage"] ?? 0u) * 1024 * 1024
                        )
                    );
                }
            }
        }
        catch
        {
            // WMI unavailable.
        }

        return usages;
    }

    /// <summary>
    ///     Replaces every paging file with a single system-managed one on
    ///     <paramref name="driveRoot" /> (e.g. <c>D:\</c>). Automatic management is
    ///     turned off first because it forces the file back onto the system drive.
    /// </summary>
    public static void MoveTo(string driveRoot)
    {
        var target = Path.Join(Path.GetPathRoot(driveRoot) ?? driveRoot, "pagefile.sys");

        using (
            var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_ComputerSystem"
            )
        )
        {
            foreach (ManagementObject system in searcher.Get())
            {
                using (system)
                {
                    if (system["AutomaticManagedPagefile"] is true)
                    {
                        system["AutomaticManagedPagefile"] = false;
                        system.Put();
                    }
                }
            }
        }

        using (
            var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PageFileSetting")
        )
        {
            foreach (ManagementObject setting in searcher.Get())
            {
                using (setting)
                {
                    var name = setting["Name"] as string ?? "";
                    if (string.Equals(name, target, StringComparison.OrdinalIgnoreCase))
                    {
                        // Keep it but make sure it is system managed.
                        setting["InitialSize"] = 0u;
                        setting["MaximumSize"] = 0u;
                        setting.Put();
                        return;
                    }

                    setting.Delete();
                }
            }
        }

        using var pageFileClass = new ManagementClass("Win32_PageFileSetting");
        using var instance = pageFileClass.CreateInstance();
        instance["Name"] = target;
        instance["InitialSize"] = 0u;
        instance["MaximumSize"] = 0u;
        instance.Put();
    }
}
