using System.Management;

namespace JeekWindowsOptimizer;

public static class AntiVirus
{
    public static bool HasThirdPartyAntivirusInstalled()
    {
        using var searcher = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT * FROM AntiVirusProduct");
        foreach (ManagementObject av in searcher.Get())
        {
            string displayName = av["displayName"]?.ToString() ?? "";
            if (displayName != "Windows Defender")
                return true;
        }
        return false;
    }

    public static bool IsThirdPartyAntivirusInstalled(string name)
    {
        using var searcher = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT * FROM AntiVirusProduct");
        foreach (ManagementObject av in searcher.Get())
        {
            string displayName = av["displayName"]?.ToString() ?? "";
            if (displayName.Contains(name))
                return true;
        }
        return false;
    }

}