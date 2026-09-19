using JeekWindowsOptimizer.Startup;
using Microsoft.Win32;

namespace JeekWindowsOptimizer;

/// <summary>
///     Resolves a COM class id to the file that implements it.
///     <para>
///         Startup entries often name a CLSID rather than a program: an Explorer extension is a
///         registered handler, and most of Windows' own scheduled tasks run a COM handler instead of
///         an executable. Without this step those entries have no file to check, so they cannot be
///         attributed to a publisher and end up looking unsigned.
///     </para>
/// </summary>
public static class ComServerRegistry
{
    /// <summary>The in-process server DLL for a CLSID, or null when it has none.</summary>
    public static string? ResolveInProcServer(string? clsid) => Resolve(clsid, ["InProcServer32"]);

    /// <summary>
    ///     The implementing file for a CLSID, in-process first and then out-of-process. Out-of-process
    ///     servers register a command line, so the executable is extracted from it.
    /// </summary>
    public static string? ResolveServer(string? clsid) =>
        Resolve(clsid, ["InProcServer32", "LocalServer32"]) ?? ResolveServiceHost(clsid);

    /// <summary>
    ///     The file behind a class that a service hosts. Such classes register neither server key,
    ///     only an AppID whose LocalService names the service, so the service's DLL (or its
    ///     executable, for a service with its own process) is the implementing file.
    /// </summary>
    private static string? ResolveServiceHost(string? clsid)
    {
        var normalized = Normalize(clsid);
        if (normalized.Length == 0)
            return null;

        try
        {
            using var classKey = Registry.ClassesRoot.OpenSubKey($@"CLSID\{normalized}");
            var appId = Normalize(classKey?.GetValue("AppID") as string);
            if (appId.Length == 0)
                return null;

            using var appKey = Registry.ClassesRoot.OpenSubKey($@"AppID\{appId}");
            if (appKey?.GetValue("LocalService") is not string service || string.IsNullOrWhiteSpace(service))
                return null;

            using var serviceKey = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{service.Trim()}"
            );
            if (serviceKey is null)
                return null;

            using var parameters = serviceKey.OpenSubKey("Parameters");
            var dll = parameters?.GetValue("ServiceDll") as string ?? serviceKey.GetValue("ServiceDll") as string;
            if (!string.IsNullOrWhiteSpace(dll))
                return Environment.ExpandEnvironmentVariables(dll.Trim()).Trim('"');

            return serviceKey.GetValue("ImagePath") is string imagePath && !string.IsNullOrWhiteSpace(imagePath)
                ? StartupIdentity.ExtractImagePath(Environment.ExpandEnvironmentVariables(imagePath.Trim()))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? Resolve(string? clsid, string[] serverKeys)
    {
        var normalized = Normalize(clsid);
        if (normalized.Length == 0)
            return null;

        // 64-bit Explorer only loads 64-bit servers, but the 32-bit view still matters for the file
        // dialogs of 32-bit apps and for WOW64 task handlers, so both views are consulted.
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var serverKey in serverKeys)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(
                    $@"Software\Classes\CLSID\{normalized}\{serverKey}"
                );
                if (
                    key?.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                    is not string value
                    || string.IsNullOrWhiteSpace(value)
                )
                    continue;

                var expanded = Environment.ExpandEnvironmentVariables(value.Trim());

                // LocalServer32 holds a command line such as "C:\...\thing.exe" -Embedding.
                return serverKey == "LocalServer32"
                    ? StartupIdentity.ExtractImagePath(expanded)
                    : expanded.Trim('"');
            }
            catch
            {
                // Try the next view, hive or server kind.
            }
        }

        return null;
    }

    /// <summary>The display name registered for a CLSID, with indirect strings resolved.</summary>
    public static string? ResolveName(string? clsid)
    {
        var normalized = Normalize(clsid);
        if (normalized.Length == 0)
            return null;

        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{normalized}");
            return ServiceRegistryScanner.ResolveIndirectString(key?.GetValue("") as string);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Canonical braced upper-case form, which is what the registry and block lists use.</summary>
    public static string Normalize(string? clsid)
    {
        var trimmed = clsid?.Trim() ?? "";
        if (trimmed.Length == 0)
            return "";

        // Some registrations omit the braces; Explorer's Blocked list requires them.
        if (!trimmed.StartsWith('{'))
            trimmed = "{" + trimmed;
        if (!trimmed.EndsWith('}'))
            trimmed += "}";

        return Guid.TryParse(trimmed, out var guid) ? guid.ToString("B").ToUpperInvariant() : "";
    }
}
