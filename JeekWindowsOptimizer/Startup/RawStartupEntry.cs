namespace JeekWindowsOptimizer.Startup;

/// <summary>
///     What a scanner reports before signatures and remembered decisions are attached.
///     Scanners only read; <see cref="StartupItemManager" /> turns these into <see cref="StartupItem" />.
/// </summary>
public sealed record RawStartupEntry
{
    public required StartupItemKind Kind { get; init; }

    /// <summary>Registry value name, file name, service name or task name.</summary>
    public required string Name { get; init; }

    /// <summary>
    ///     Optional display name supplied by the registration itself (e.g. a service's registry
    ///     <c>DisplayName</c>). Preferred over the target executable's file description so services
    ///     hosted by shared wrappers like <c>svchost.exe</c> or <c>nssm.exe</c> keep distinct names.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>Where it is registered, in a form the user can go and look at.</summary>
    public required string Location { get; init; }

    /// <summary>Full command line, or the DLL path for an Explorer extension.</summary>
    public string Command { get; init; } = "";

    /// <summary>The executable or DLL the entry actually runs, extracted from the command line.</summary>
    public string ImagePath { get; init; } = "";

    /// <summary>Whether this entry runs at startup right now.</summary>
    public required bool IsEnabled { get; init; }

    public bool CanToggle { get; init; } = true;

    /// <summary>Localization key explaining why the entry cannot be toggled.</summary>
    public string? ReadOnlyReasonKey { get; init; }

    /// <summary>Opaque state the toggle needs to restore the original configuration exactly.</summary>
    public string? RestoreState { get; init; }

    /// <summary>For an Explorer extension: the hook points this DLL registers.</summary>
    public IReadOnlyList<string> HookPoints { get; init; } = [];

    /// <summary>Identifiers the toggle needs, e.g. the CLSIDs behind an Explorer DLL.</summary>
    public IReadOnlyList<string> Handles { get; init; } = [];

    /// <summary>
    ///     How to remove this registration if its target file is missing. Null for protected
    ///     entries (Group Policy runs, protected tasks, boot-critical drivers).
    /// </summary>
    public StartupDeleteTarget? DeleteTarget { get; init; }

    /// <summary>For a scheduled task: structured trigger definitions describing when it runs.</summary>
    public IReadOnlyList<ScheduledTaskTriggerInfo> TaskTriggers { get; init; } = [];

    /// <summary>For a Windows service: its configured startup type and live running state.</summary>
    public ServiceRuntimeSnapshot? ServiceRuntime { get; init; }
}
