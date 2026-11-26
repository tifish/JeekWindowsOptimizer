using System.Globalization;
using System.Xml;
using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer.Startup;

/// <summary>Task Scheduler 2.0 trigger kinds (<c>TASK_TRIGGER_TYPE2</c>).</summary>
public enum ScheduledTaskTriggerKind
{
    Unknown = -1,
    Event = 0,
    Time = 1,
    Daily = 2,
    Weekly = 3,
    Monthly = 4,
    MonthlyDayOfWeek = 5,
    Idle = 6,
    Registration = 7,
    Boot = 8,
    Logon = 9,
    SessionStateChange = 11,
}

/// <summary>Task Scheduler 2.0 session state change kinds (<c>TASK_SESSION_STATE_CHANGE_TYPE</c>).</summary>
public enum ScheduledTaskSessionChange
{
    Unknown = 0,
    ConsoleConnect = 1,
    ConsoleDisconnect = 2,
    RemoteConnect = 3,
    RemoteDisconnect = 4,
    SessionLock = 7,
    SessionUnlock = 8,
}

/// <summary>Structured representation of one Task Scheduler trigger so it can be re-localized on language switch.</summary>
public sealed record ScheduledTaskTriggerInfo
{
    public required ScheduledTaskTriggerKind Kind { get; init; }
    public bool Enabled { get; init; } = true;
    public ScheduledTaskSessionChange SessionChange { get; init; }
    public string? UserId { get; init; }
    public string? DelayIso { get; init; }
    public string? StartBoundary { get; init; }
    public string? RepetitionIntervalIso { get; init; }
    public int Interval { get; init; }

    public string FormatLocalized()
    {
        var text = FormatCore();

        if (FormatIsoDuration(DelayIso) is { } delay)
            text = string.Format(Localizer.Get("StartupTaskTriggerDelay"), text, delay);

        if (FormatIsoDuration(RepetitionIntervalIso) is { } repeat)
            text = string.Format(Localizer.Get("StartupTaskTriggerRepeat"), text, repeat);

        if (!Enabled)
            text = string.Format(Localizer.Get("StartupTaskTriggerDisabled"), text);

        return text;
    }

    private string FormatCore()
    {
        var timeOfDay = FormatTimeOfDay(StartBoundary);

        return Kind switch
        {
            ScheduledTaskTriggerKind.Boot => Localizer.Get("StartupTaskTriggerBoot"),
            ScheduledTaskTriggerKind.Logon => string.IsNullOrWhiteSpace(UserId)
                ? Localizer.Get("StartupTaskTriggerLogon")
                : string.Format(Localizer.Get("StartupTaskTriggerLogonUser"), UserId),
            ScheduledTaskTriggerKind.Registration => Localizer.Get("StartupTaskTriggerRegistration"),
            ScheduledTaskTriggerKind.SessionStateChange => SessionChange switch
            {
                ScheduledTaskSessionChange.ConsoleConnect => Localizer.Get("StartupTaskTriggerConsoleConnect"),
                ScheduledTaskSessionChange.ConsoleDisconnect => Localizer.Get("StartupTaskTriggerConsoleDisconnect"),
                ScheduledTaskSessionChange.RemoteConnect => Localizer.Get("StartupTaskTriggerRemoteConnect"),
                ScheduledTaskSessionChange.RemoteDisconnect => Localizer.Get("StartupTaskTriggerRemoteDisconnect"),
                ScheduledTaskSessionChange.SessionLock => Localizer.Get("StartupTaskTriggerSessionLock"),
                ScheduledTaskSessionChange.SessionUnlock => Localizer.Get("StartupTaskTriggerSessionUnlock"),
                _ => Localizer.Get("StartupTaskTriggerSessionChange"),
            },
            ScheduledTaskTriggerKind.Idle => Localizer.Get("StartupTaskTriggerIdle"),
            ScheduledTaskTriggerKind.Event => Localizer.Get("StartupTaskTriggerEvent"),
            ScheduledTaskTriggerKind.Time => FormatStartDateTime(StartBoundary) is { } dt
                ? string.Format(Localizer.Get("StartupTaskTriggerTimeAt"), dt)
                : Localizer.Get("StartupTaskTriggerTime"),
            ScheduledTaskTriggerKind.Daily => Interval > 1
                ? timeOfDay is not null
                    ? string.Format(Localizer.Get("StartupTaskTriggerEveryDaysAt"), Interval, timeOfDay)
                    : string.Format(Localizer.Get("StartupTaskTriggerEveryDays"), Interval)
                : timeOfDay is not null
                    ? string.Format(Localizer.Get("StartupTaskTriggerDailyAt"), timeOfDay)
                    : Localizer.Get("StartupTaskTriggerDaily"),
            ScheduledTaskTriggerKind.Weekly => Interval > 1
                ? timeOfDay is not null
                    ? string.Format(Localizer.Get("StartupTaskTriggerEveryWeeksAt"), Interval, timeOfDay)
                    : string.Format(Localizer.Get("StartupTaskTriggerEveryWeeks"), Interval)
                : timeOfDay is not null
                    ? string.Format(Localizer.Get("StartupTaskTriggerWeeklyAt"), timeOfDay)
                    : Localizer.Get("StartupTaskTriggerWeekly"),
            ScheduledTaskTriggerKind.Monthly or ScheduledTaskTriggerKind.MonthlyDayOfWeek => timeOfDay is not null
                ? string.Format(Localizer.Get("StartupTaskTriggerMonthlyAt"), timeOfDay)
                : Localizer.Get("StartupTaskTriggerMonthly"),
            _ => Localizer.Get("StartupTaskTriggerEvent"),
        };
    }

    private static string? FormatTimeOfDay(string? boundary)
    {
        if (string.IsNullOrWhiteSpace(boundary))
            return null;

        if (DateTimeOffset.TryParse(boundary, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto))
            return dto.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);

        return null;
    }

    private static string? FormatStartDateTime(string? boundary)
    {
        if (string.IsNullOrWhiteSpace(boundary))
            return null;

        if (DateTimeOffset.TryParse(boundary, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto))
            return dto.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        return null;
    }

    internal static string? FormatIsoDuration(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso))
            return null;

        try
        {
            var span = XmlConvert.ToTimeSpan(iso);
            if (span <= TimeSpan.Zero)
                return null;

            var parts = new List<string>(4);
            if (span.Days > 0)
                parts.Add(string.Format(Localizer.Get("StartupDurationDay"), span.Days));
            if (span.Hours > 0)
                parts.Add(string.Format(Localizer.Get("StartupDurationHour"), span.Hours));
            if (span.Minutes > 0)
                parts.Add(string.Format(Localizer.Get("StartupDurationMinute"), span.Minutes));
            if (span.Seconds > 0 && parts.Count < 2)
                parts.Add(string.Format(Localizer.Get("StartupDurationSecond"), span.Seconds));

            return parts.Count > 0 ? string.Join(" ", parts) : null;
        }
        catch
        {
            return null;
        }
    }

    public static string FormatList(IReadOnlyList<ScheduledTaskTriggerInfo> triggers)
    {
        if (triggers.Count == 0)
            return "";

        var formatted = new List<string>(triggers.Count);
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var trigger in triggers)
        {
            var text = trigger.FormatLocalized();
            if (text.Length > 0 && seen.Add(text))
                formatted.Add(text);
        }

        return string.Join(Localizer.Get("StartupTaskTriggerSeparator"), formatted);
    }
}

/// <summary>Windows service startup type from <c>HKLM\SYSTEM\CurrentControlSet\Services\&lt;name&gt;\Start</c>.</summary>
public enum StartupServiceStartMode
{
    Boot = 0,
    System = 1,
    Automatic = 2,
    Manual = 3,
    Disabled = 4,
}

/// <summary>Live running state of a Windows service from the Service Control Manager.</summary>
public enum StartupServiceRunState
{
    Unknown = 0,
    Stopped,
    StartPending,
    StopPending,
    Running,
    ContinuePending,
    PausePending,
    Paused,
}

/// <summary>Live service startup mode and running state read from the registry and SCM.</summary>
public sealed record ServiceRuntimeSnapshot(
    StartupServiceStartMode StartMode,
    bool DelayedAutoStart,
    bool TriggerStart,
    StartupServiceRunState RunState
);
