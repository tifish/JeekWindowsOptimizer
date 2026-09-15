using CommunityToolkit.Mvvm.ComponentModel;
using Jeek.Avalonia.Localization;

namespace JeekWindowsOptimizer.Startup;

/// <summary>Which autostart mechanism an item comes from. Also the tab's grouping.</summary>
public enum StartupItemKind
{
    /// <summary>
    ///     Run values under HKLM and HKCU, including the 32-bit views, and the policy-driven
    ///     Run key.
    /// </summary>
    /// <remarks>
    ///     Deliberately not renamed to <c>Run</c>: this member's name is the first segment of every
    ///     ledger key, and the ledger is meant to keep decisions forever. Renaming it would orphan
    ///     every decision already recorded against a Run entry.
    /// </remarks>
    LogonRegistry,

    /// <summary>
    ///     RunOnce values, which Windows deletes after running them once. They are listed for
    ///     visibility but cannot be switched: there is no StartupApproved flag for them, and the
    ///     entry is gone by the next logon anyway.
    /// </summary>
    RunOnce,

    /// <summary>Shortcuts and programs in the per-user and all-users Startup folders.</summary>
    StartupFolder,

    /// <summary>Tasks with a logon, boot or startup trigger.</summary>
    ScheduledTask,

    /// <summary>Win32 and user-mode services.</summary>
    Service,

    /// <summary>Kernel, file system and recognizer drivers.</summary>
    Driver,

    /// <summary>A File Explorer in-process extension, listed once per DLL rather than once per hook.</summary>
    ExplorerExtension,

    /// <summary>
    ///     A browser add-on: Browser Helper Object, toolbar, Explorer bar, URL search hook or
    ///     protocol handler. Separate from <see cref="ExplorerExtension" /> because a different
    ///     host loads it and a different switch turns it off.
    /// </summary>
    InternetExplorerExtension,
}

/// <summary>How trustworthy the code behind an item is, from its Authenticode signature.</summary>
public enum StartupSignerKind
{
    /// <summary>No valid signature at all.</summary>
    Unsigned,

    /// <summary>Signed, but the chain did not verify (expired, revoked, untrusted root).</summary>
    Invalid,

    /// <summary>A third-party publisher with a valid signature.</summary>
    ThirdParty,

    /// <summary>Microsoft, but not a Windows system component (Office, OneDrive, Teams, Edge).</summary>
    Microsoft,

    /// <summary>A Windows system component. Hidden by default.</summary>
    Windows,
}

/// <summary>Where an item's decision came from, so the UI can say whether the user decided it here.</summary>
public enum StartupDecisionSource
{
    /// <summary>Never decided; the item is waiting for confirmation.</summary>
    None,

    /// <summary>Decided on this machine.</summary>
    ThisMachine,

    /// <summary>Decided on another machine and synced in.</summary>
    OtherMachine,

    /// <summary>No exact match; a decision for the same program in another location was found.</summary>
    LooseMatch,
}

/// <summary>
///     One row on the Startup tab. Unlike an optimization item there is no fixed "optimized" state:
///     the item carries what the system currently does (<see cref="IsEnabled" />) and what the user
///     decided (<see cref="Decision" />), and the two can disagree, which is exactly what the tab
///     exists to surface.
/// </summary>
public partial class StartupItem : ObservableObject
{
    public required StartupItemKind Kind { get; init; }

    /// <summary>Strict ledger key. See <see cref="StartupIdentity.ComputeKey" />.</summary>
    public required string Key { get; init; }

    /// <summary>Fallback ledger key used only when <see cref="Key" /> misses.</summary>
    public string LooseKey { get; init; } = "";

    /// <summary>Entry name: the registry value name, file name, task name or service name.</summary>
    public required string Name { get; init; }

    /// <summary>Where it is registered, shown verbatim so the user can go look.</summary>
    public required string Location { get; init; }

    /// <summary>Full command line, or the DLL path for an Explorer extension.</summary>
    public string Command { get; init; } = "";

    public string ImagePath { get; init; } = "";

    /// <summary>Display name from the file's version info, when it has one.</summary>
    public string? Description { get; init; }

    /// <summary>For an Explorer extension, the hook points this one DLL registers.</summary>
    public IReadOnlyList<string> HookPoints { get; init; } = [];

    /// <summary>Internal identifiers the toggle needs, e.g. the CLSIDs behind an Explorer DLL.</summary>
    public IReadOnlyList<string> Handles { get; init; } = [];

    public string? Publisher { get; init; }

    public StartupSignerKind SignerKind { get; init; } = StartupSignerKind.Unsigned;

    /// <summary>True when the item's file is missing. The decision is still remembered.</summary>
    public bool IsOrphaned { get; init; }

    /// <summary>False for entries this build refuses to touch, such as boot-start drivers.</summary>
    public bool CanToggle { get; init; } = true;

    /// <summary>Why <see cref="CanToggle" /> is false, shown as the row's status.</summary>
    public string? ReadOnlyReasonKey { get; init; }

    /// <summary>Extra state the toggle needs to put things back exactly as they were.</summary>
    public string? RestoreState { get; init; }

    /// <summary>True when the item runs at startup right now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompliant))]
    [NotifyPropertyChangedFor(nameof(StatusKey))]
    public partial bool IsEnabled { get; set; }

    /// <summary>What the user decided, or null when it has never been decided.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    [NotifyPropertyChangedFor(nameof(IsAllowed))]
    [NotifyPropertyChangedFor(nameof(IsDenied))]
    [NotifyPropertyChangedFor(nameof(NeedsEnforcement))]
    [NotifyPropertyChangedFor(nameof(IsCompliant))]
    [NotifyPropertyChangedFor(nameof(StatusKey))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial StartupDecision? Decision { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DecidedOnText))]
    public partial StartupDecisionSource DecisionSource { get; set; }

    /// <summary>Machine that recorded the decision, for the "decided on" column.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DecidedOnText))]
    public partial string? DecidedOnMachine { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DecidedOnText))]
    public partial DateTime? DecidedAtUtc { get; set; }

    /// <summary>Set when the last apply attempt failed, so the row can show why.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? ErrorMessage { get; set; }

    /// <summary>
    ///     Set when the last apply succeeded but has not fully taken effect, such as a disabled
    ///     service that is still running. Shown next to the status instead of replacing it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? WarningMessage { get; set; }

    public bool IsPending => Decision is null;

    public bool IsAllowed => Decision == StartupDecision.Allow;

    public bool IsDenied => Decision == StartupDecision.Deny;

    /// <summary>True when the system state already matches the decision.</summary>
    public bool IsCompliant =>
        Decision switch
        {
            StartupDecision.Allow => IsEnabled,
            StartupDecision.Deny => !IsEnabled,
            _ => false,
        };

    /// <summary>True when a recorded Deny is not in force, which is what the tab asks the user to fix.</summary>
    public bool NeedsEnforcement => Decision == StartupDecision.Deny && IsEnabled && CanToggle;

    public bool IsWindowsEntry => SignerKind == StartupSignerKind.Windows;

    public bool IsMicrosoftEntry =>
        SignerKind is StartupSignerKind.Windows or StartupSignerKind.Microsoft;

    public string KindNameKey => KindNameKeyOf(Kind);

    public static string KindNameKeyOf(StartupItemKind kind) =>
        kind switch
        {
            StartupItemKind.LogonRegistry => "StartupKindLogonRegistry",
            StartupItemKind.RunOnce => "StartupKindRunOnce",
            StartupItemKind.StartupFolder => "StartupKindStartupFolder",
            StartupItemKind.ScheduledTask => "StartupKindScheduledTask",
            StartupItemKind.Service => "StartupKindService",
            StartupItemKind.Driver => "StartupKindDriver",
            StartupItemKind.ExplorerExtension => "StartupKindExplorerExtension",
            StartupItemKind.InternetExplorerExtension => "StartupKindInternetExplorerExtension",
            _ => "StartupKindLogonRegistry",
        };

    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? Name : Description!;

    public string PublisherText =>
        SignerKind switch
        {
            StartupSignerKind.Unsigned => Localizer.Get("StartupUnsigned"),
            StartupSignerKind.Invalid => Localizer.Get("StartupSignatureInvalid"),
            _ => Publisher ?? Localizer.Get("StartupUnsigned"),
        };

    public string StatusKey =>
        !CanToggle ? ReadOnlyReasonKey ?? "StartupStatusReadOnly"
        : Decision is null ? "StartupStatusPending"
        : Decision == StartupDecision.Allow
            ? IsEnabled
                ? "StartupStatusAllowed"
                : "StartupStatusAllowedButOff"
        : IsEnabled ? "StartupStatusDeniedNotApplied"
        : "StartupStatusDenied";

    public string StatusText
    {
        get
        {
            if (!string.IsNullOrEmpty(ErrorMessage))
                return string.Format(Localizer.Get("StartupApplyFailed"), ErrorMessage);
            if (!string.IsNullOrEmpty(WarningMessage))
                return string.Format(
                    Localizer.Get("StartupStatusWithWarning"),
                    Localizer.Get(StatusKey),
                    WarningMessage
                );
            return Localizer.Get(StatusKey);
        }
    }

    public string DecidedOnText =>
        DecisionSource switch
        {
            StartupDecisionSource.ThisMachine => "",
            StartupDecisionSource.OtherMachine => string.Format(
                Localizer.Get("StartupDecidedOnOtherMachine"),
                DecidedOnMachine ?? "?"
            ),
            StartupDecisionSource.LooseMatch => Localizer.Get("StartupDecidedByLooseMatch"),
            _ => "",
        };

    public string LocationText => Location;

    public string CommandText => Command;

    /// <summary>
    ///     False when the command line adds nothing over the location, as for an Explorer extension
    ///     whose location is the DLL itself. The row then shows the path once instead of twice.
    /// </summary>
    public bool HasDistinctCommand =>
        Command.Length > 0 && !string.Equals(Command, Location, StringComparison.OrdinalIgnoreCase);

    public string HookPointsText => string.Join(", ", HookPoints);

    public bool HasHookPoints => HookPoints.Count > 0;

    /// <summary>True when there is an actual file to show in Explorer.</summary>
    public bool CanReveal => ImagePath.Length > 0 && !IsOrphaned;

    public StartupDecisionSnapshot ToSnapshot() =>
        new(Kind.ToString(), DisplayName, Publisher, Command, Name, ImagePath);

    public void NotifyLanguageChanged()
    {
        OnPropertyChanged(nameof(PublisherText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DecidedOnText));
    }
}
