using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace JeekWindowsOptimizer;

public static class MicrosoftStore
{
    private static List<Package>? _installedPackages;

    public static List<Package> InstalledPackages
    {
        get
        {
            if (_installedPackages is null)
            {
                var packageManager = new PackageManager();
                _installedPackages = packageManager.FindPackages().ToList();
            }

            return _installedPackages!;
        }
    }

    public static Package? GetPackage(string namePrefix)
    {
        return InstalledPackages.FirstOrDefault(package => package.Id.Name.StartsWith(namePrefix));
    }
}
