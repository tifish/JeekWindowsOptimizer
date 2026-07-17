using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer;

/// <summary>
/// Task Scheduler 2.0 COM helper for enable/disable and existence checks.
/// Locale-independent (does not parse schtasks text output).
/// </summary>
public static class WindowsScheduledTask
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(WindowsScheduledTask));

    public enum SetEnabledResult
    {
        Success,
        NotFound,
        AccessDenied,
        Failed,
    }

    public static bool Exists(string fullTaskPath)
    {
        return TryGetTask(fullTaskPath, out var task, out var folder, out var service)
            && Cleanup(task, folder, service);
    }

    public static bool IsDisabled(string fullTaskPath)
    {
        return TryGetEnabled(fullTaskPath, out var enabled) && !enabled;
    }

    public static bool IsEnabled(string fullTaskPath)
    {
        return TryGetEnabled(fullTaskPath, out var enabled) && enabled;
    }

    /// <summary>
    /// Returns false when the current process clearly cannot change the task
    /// (e.g. DACL grants admins only read/execute). Used to skip protected tasks.
    /// </summary>
    public static bool CanModify(string fullTaskPath)
    {
        if (!TryGetTask(fullTaskPath, out var task, out var folder, out var service) || task is null)
            return false;

        try
        {
            // Fast path: if already disabled, we only care about re-enable later;
            // still require write-ish rights, but many tasks allow enable/disable via Enabled.
            var xml = GetComProperty<string>(task, "Xml");
            if (string.IsNullOrEmpty(xml))
                return true;

            // Look for an SDDL SecurityDescriptor in the task XML.
            // Example that blocks admins from writing:
            // D:PAI(A;;FRFX;;;BA)(A;;GA;;;SY)...
            const string marker = "<SecurityDescriptor>";
            var start = xml.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return true;

            start += marker.Length;
            var end = xml.IndexOf("</SecurityDescriptor>", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
                return true;

            var sddl = xml[start..end].Trim();
            if (string.IsNullOrEmpty(sddl))
                return true;

            return SddlAllowsCurrentUserWrite(sddl);
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to inspect permissions for scheduled task {fullTaskPath}");
            // Optimistic: allow attempt; TrySetEnabled will report AccessDenied if needed.
            return true;
        }
        finally
        {
            Cleanup(task, folder, service);
        }
    }

    public static bool SetEnabled(string fullTaskPath, bool enabled)
    {
        return TrySetEnabled(fullTaskPath, enabled) == SetEnabledResult.Success;
    }

    public static SetEnabledResult TrySetEnabled(string fullTaskPath, bool enabled)
    {
        if (!TryGetTask(fullTaskPath, out var task, out var folder, out var service) || task is null)
            return SetEnabledResult.NotFound;

        try
        {
            SetComProperty(task, "Enabled", enabled);
            return GetComProperty<bool>(task, "Enabled") == enabled
                ? SetEnabledResult.Success
                : SetEnabledResult.Failed;
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            return SetEnabledResult.AccessDenied;
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to set Enabled={enabled} on scheduled task {fullTaskPath}");
            return SetEnabledResult.Failed;
        }
        finally
        {
            Cleanup(task, folder, service);
        }
    }

    private static bool SddlAllowsCurrentUserWrite(string sddl)
    {
        try
        {
            var security = new RawSecurityDescriptor(sddl);
            if (security.DiscretionaryAcl is null)
                return true;

            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);

            // Collect allow/deny write-ish rights for SIDs the current user belongs to.
            var allowed = false;
            foreach (var ace in security.DiscretionaryAcl)
            {
                if (ace is not CommonAce common)
                    continue;

                if (!principal.IsInRole(common.SecurityIdentifier))
                    continue;

                // FILE_WRITE_DATA / GENERIC_WRITE / GENERIC_ALL / WRITE_DAC roughly.
                const int writeMask =
                    unchecked((int)0x40000000) // GENERIC_WRITE
                    | unchecked((int)0x10000000) // GENERIC_ALL
                    | 0x00000002 // FILE_WRITE_DATA
                    | 0x00040000 // WRITE_DAC
                    | 0x000F01FF; // FILE_ALL_ACCESS

                var hasWrite = (common.AccessMask & writeMask) != 0;
                if (!hasWrite)
                    continue;

                if (common.AceType == AceType.AccessDenied)
                    return false;
                if (common.AceType == AceType.AccessAllowed)
                    allowed = true;
            }

            // If BA only has FRFX (read/execute), allowed stays false.
            return allowed;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsAccessDenied(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (e is UnauthorizedAccessException)
                return true;
            if (e is COMException com && com.HResult == unchecked((int)0x80070005))
                return true;
            if (
                e.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("拒绝访问", StringComparison.Ordinal)
            )
                return true;
        }

        return false;
    }

    private static bool TryGetEnabled(string fullTaskPath, out bool enabled)
    {
        enabled = false;
        if (!TryGetTask(fullTaskPath, out var task, out var folder, out var service) || task is null)
            return false;

        try
        {
            enabled = GetComProperty<bool>(task, "Enabled");
            return true;
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to read Enabled on scheduled task {fullTaskPath}");
            return false;
        }
        finally
        {
            Cleanup(task, folder, service);
        }
    }

    private static bool TryGetTask(
        string fullTaskPath,
        out object? task,
        out object? folder,
        out object? service
    )
    {
        task = null;
        folder = null;
        service = null;

        if (string.IsNullOrWhiteSpace(fullTaskPath))
            return false;

        try
        {
            var serviceType =
                Type.GetTypeFromProgID("Schedule.Service")
                ?? throw new InvalidOperationException("Schedule.Service COM ProgID not found");
            service =
                Activator.CreateInstance(serviceType)
                ?? throw new InvalidOperationException("Failed to create Schedule.Service");

            InvokeCom(service, "Connect");

            SplitTaskPath(fullTaskPath, out var folderPath, out var taskName);
            folder = InvokeCom(service, "GetFolder", folderPath);
            if (folder is null)
                return false;

            task = InvokeCom(folder, "GetTask", taskName);
            return task is not null;
        }
        catch (Exception ex) when (IsTaskMissing(ex))
        {
            // Task or folder not present on this Windows SKU/version — expected.
            Cleanup(task, folder, service);
            task = null;
            folder = null;
            service = null;
            return false;
        }
        catch (Exception ex)
        {
            Log.ZLogWarning(ex, $"Failed to open scheduled task {fullTaskPath}");
            Cleanup(task, folder, service);
            task = null;
            folder = null;
            service = null;
            return false;
        }
    }

    private static bool IsTaskMissing(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (e is COMException com)
            {
                // ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND
                if (com.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003))
                    return true;
            }

            // Some interop layers only surface the message.
            if (
                e.Message.Contains("cannot find the file", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("找不到", StringComparison.Ordinal)
            )
                return true;
        }

        return false;
    }

    private static void SplitTaskPath(string fullTaskPath, out string folderPath, out string taskName)
    {
        var path = fullTaskPath.Replace('/', '\\').Trim();
        if (!path.StartsWith('\\'))
            path = "\\" + path;

        var lastSlash = path.LastIndexOf('\\');
        if (lastSlash <= 0)
        {
            folderPath = "\\";
            taskName = path.TrimStart('\\');
            return;
        }

        folderPath = path[..lastSlash];
        if (string.IsNullOrEmpty(folderPath))
            folderPath = "\\";
        taskName = path[(lastSlash + 1)..];
    }

    private static object? InvokeCom(object target, string method, params object[] args)
    {
        return target
            .GetType()
            .InvokeMember(
                method,
                System.Reflection.BindingFlags.InvokeMethod,
                binder: null,
                target: target,
                args: args
            );
    }

    private static T GetComProperty<T>(object target, string propertyName)
    {
        var value = target
            .GetType()
            .InvokeMember(
                propertyName,
                System.Reflection.BindingFlags.GetProperty,
                binder: null,
                target: target,
                args: null
            );
        return (T)value!;
    }

    private static void SetComProperty(object target, string propertyName, object value)
    {
        target
            .GetType()
            .InvokeMember(
                propertyName,
                System.Reflection.BindingFlags.SetProperty,
                binder: null,
                target: target,
                args: [value]
            );
    }

    private static bool Cleanup(object? task, object? folder, object? service)
    {
        ReleaseCom(task);
        ReleaseCom(folder);
        ReleaseCom(service);
        return true;
    }

    private static void ReleaseCom(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
            Marshal.FinalReleaseComObject(comObject);
    }
}
