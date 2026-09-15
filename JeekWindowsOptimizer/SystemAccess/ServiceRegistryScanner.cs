using System.Runtime.InteropServices;
using System.Text;
using JeekTools;
using JeekWindowsOptimizer.Startup;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ZLogger;

namespace JeekWindowsOptimizer;

/// <summary>
///     Enumerates services and drivers straight from
///     <c>HKLM\SYSTEM\CurrentControlSet\Services</c>. WMI is avoided here on purpose: a machine has
///     several hundred of these, a <c>Win32_Service</c> query costs far more per row, and it omits
///     drivers and user-service templates entirely.
/// </summary>
public static partial class ServiceRegistryScanner
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(ServiceRegistryScanner));

    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";

    // Service type bits from the SERVICE_* constants.
    private const int KernelDriver = 0x01;
    private const int FileSystemDriver = 0x02;
    private const int Recognizer = 0x08;
    private const int OwnProcess = 0x10;
    private const int ShareProcess = 0x20;
    private const int UserOwnProcess = 0x50;
    private const int UserShareProcess = 0x60;

    private const int StartBoot = 0;
    private const int StartSystem = 1;
    private const int StartDisabled = 4;

    public static List<RawStartupEntry> Scan()
    {
        var entries = new List<RawStartupEntry>();

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(ServicesPath);
            if (root is null)
                return entries;

            var names = root.GetSubKeyNames();
            var templates = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

            foreach (var name in names)
            {
                try
                {
                    // Per-user service instances (e.g. CDPUserSvc_4a2b1) are spawned from a
                    // template and come and go with each logon session. Deciding on the template
                    // is what the user means; the instances would churn the ledger forever.
                    if (IsUserServiceInstance(name, templates))
                        continue;

                    using var key = root.OpenSubKey(name);
                    if (key is null)
                        continue;

                    if (key.GetValue("Type") is not int type)
                        continue;

                    var isDriver = type is KernelDriver or FileSystemDriver or Recognizer;
                    var isService = (type & (OwnProcess | ShareProcess)) != 0
                        || type is UserOwnProcess or UserShareProcess;
                    if (!isDriver && !isService)
                        continue;

                    var start = key.GetValue("Start") as int? ?? StartDisabled;
                    var registeredImagePath = key.GetValue(
                        "ImagePath",
                        null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames
                    ) as string ?? "";

                    // Many drivers, Windows' own among them, register no ImagePath at all; the
                    // loader then uses System32\drivers\<service name>.sys by convention. Without
                    // this default the entry has no file, so it can neither be shown in Explorer
                    // nor signature-checked, and Windows drivers end up listed as unsigned.
                    var imagePath = isDriver && string.IsNullOrWhiteSpace(registeredImagePath)
                        ? $@"System32\drivers\{name}.sys"
                        : registeredImagePath;

                    var displayName = ResolveIndirectString(key.GetValue("DisplayName") as string);

                    // Boot- and system-start drivers load before almost everything else. Turning one
                    // off can leave a machine that will not boot, and no allow-list is worth that,
                    // so they are listed for visibility but never toggled.
                    var critical = isDriver && start is StartBoot or StartSystem;

                    entries.Add(
                        new RawStartupEntry
                        {
                            Kind = isDriver ? StartupItemKind.Driver : StartupItemKind.Service,
                            Name = name,
                            Location = $@"HKLM\{ServicesPath}\{name}",
                            Command = registeredImagePath,
                            ImagePath = StartupIdentity.ExtractImagePath(imagePath),
                            IsEnabled = start != StartDisabled,
                            CanToggle = !critical,
                            ReadOnlyReasonKey = critical ? "StartupStatusBootCritical" : null,
                            // Remember the exact original start type so allowing an item again puts
                            // it back to Manual or Automatic as it was, not to a guessed value.
                            RestoreState = start.ToString(),
                        }
                    );
                }
                catch (Exception ex)
                {
                    Log.ZLogWarning(ex, $"Failed to read service registration {name}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to enumerate services");
        }

        return entries;
    }

    /// <summary>
    ///     True for names like <c>CDPUserSvc_4a2b1</c> whose template <c>CDPUserSvc</c> also exists.
    /// </summary>
    private static bool IsUserServiceInstance(string name, HashSet<string> allNames)
    {
        var underscore = name.LastIndexOf('_');
        if (underscore <= 0 || underscore == name.Length - 1)
            return false;

        var suffix = name[(underscore + 1)..];
        if (!suffix.All(char.IsAsciiHexDigit))
            return false;

        return allNames.Contains(name[..underscore]);
    }

    /// <summary>
    ///     Resolves a display name of the form <c>@%SystemRoot%\system32\foo.dll,-123</c> into real
    ///     text. Falls back to the raw value, which is still better than showing nothing.
    /// </summary>
    public static string? ResolveIndirectString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value[0] != '@')
            return value;

        try
        {
            var buffer = new StringBuilder(1024);
            if (SHLoadIndirectString(value, buffer, (uint)buffer.Capacity, 0) == 0)
            {
                var resolved = buffer.ToString();
                if (resolved.Length > 0)
                    return resolved;
            }
        }
        catch
        {
            // Fall through to the raw value.
        }

        return value;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHLoadIndirectString(
        string pszSource,
        StringBuilder pszOutBuf,
        uint cchOutBuf,
        nint ppvReserved
    );

    /// <summary>Applies a start type, returning false with a reason when it could not be written.</summary>
    public static bool SetStartMode(string serviceName, int start, out string? error)
    {
        error = null;

        try
        {
            using var service = new WindowsService(serviceName);
            if (service.SetStartMode((WindowsService.StartMode)start))
                return true;

            error = "The start type could not be changed.";
            return false;
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to set start mode for service {serviceName}");
            error = ex.Message;
            return false;
        }
    }
}
