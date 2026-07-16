using System.Management;

namespace JeekWindowsOptimizer;

public static class AntiVirus
{
    public static bool IsMicrosoftDefenderProductName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return false;

        var name = displayName.Trim();

        if (name.Equals("Windows Defender", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.Equals("Microsoft Defender", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.Equals("Microsoft Defender Antivirus", StringComparison.OrdinalIgnoreCase))
            return true;

        // Localized / partial product names still refer to the built-in stack.
        if (name.Contains("Windows Defender", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.Contains("Microsoft Defender", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.Contains("Defender Antivirus", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public static bool HasThirdPartyAntivirusInstalled()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\SecurityCenter2",
                "SELECT displayName FROM AntiVirusProduct"
            );
            foreach (ManagementObject av in searcher.Get())
            {
                using (av)
                {
                    var displayName = av["displayName"]?.ToString();
                    if (!IsMicrosoftDefenderProductName(displayName))
                        return true;
                }
            }
        }
        catch
        {
            // SecurityCenter2 is missing on some SKUs / locked-down systems.
        }

        return false;
    }

    public static bool IsThirdPartyAntivirusInstalled(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\SecurityCenter2",
                "SELECT displayName FROM AntiVirusProduct"
            );
            foreach (ManagementObject av in searcher.Get())
            {
                using (av)
                {
                    var displayName = av["displayName"]?.ToString() ?? "";
                    if (IsMicrosoftDefenderProductName(displayName))
                        continue;
                    if (displayName.Contains(name, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch
        {
            // Ignore WMI failures; callers also use driver/service fingerprints.
        }

        return false;
    }
}
