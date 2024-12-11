using System.Management;

namespace JeekWindowsOptimizer;

public class WindowsService(string serviceName)
{
    private readonly ManagementObject _serviceObject = new($"Win32_Service.Name=\"{serviceName}\"");

    public bool Start()
    {
        return (uint)_serviceObject.InvokeMethod("StartService", null) == 0;
    }

    public bool Stop()
    {
        return (uint)_serviceObject.InvokeMethod("StopService", null) == 0;
    }

    public bool Pause()
    {
        return (uint)_serviceObject.InvokeMethod("PauseService", null) == 0;
    }

    public bool Resume()
    {
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
        var startMode = (string)_serviceObject.GetPropertyValue("StartMode");
        return startMode switch
        {
            "Boot" => StartMode.Boot,
            "System" => StartMode.System,
            "Auto" => StartMode.Automatic,
            "Manual" => StartMode.Manual,
            "Disabled" => StartMode.Disabled,
            _ => throw new ArgumentOutOfRangeException(nameof(startMode), startMode, null),
        };
    }

    public bool SetStartMode(StartMode startMode)
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

        return (uint)_serviceObject.InvokeMethod("ChangeStartMode", [startModeString]) == 0;
    }
}
