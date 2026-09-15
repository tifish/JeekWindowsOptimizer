using System.Text;
using System.Text.RegularExpressions;

namespace JeekWindowsOptimizer.Startup;

/// <summary>
///     Turns a startup entry into a key that is stable across machines and across the software's own
///     updates, because the decision ledger is keyed by it and keeps entries forever.
///     <para>
///         Two keys are produced. The strict key identifies the exact registration. The loose key
///         drops everything machine- and location-specific and is only consulted when the strict key
///         misses, so a decision made on one machine can still be offered on another where the
///         program sits somewhere else.
///     </para>
/// </summary>
public static partial class StartupIdentity
{
    /// <summary>Longest first: %LocalAppData% must win over %UserProfile%.</summary>
    private static readonly (string Token, string Path)[] PathTokens = BuildPathTokens();

    private static (string, string)[] BuildPathTokens()
    {
        var tokens = new List<(string Token, string Path)>();

        void Add(string token, string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
                tokens.Add((token, path.TrimEnd('\\')));
        }

        Add("%localappdata%", Folder(Environment.SpecialFolder.LocalApplicationData));
        Add("%appdata%", Folder(Environment.SpecialFolder.ApplicationData));
        Add("%commonprogramfiles(x86)%", Environment.GetEnvironmentVariable("CommonProgramFiles(x86)") ?? "");
        Add("%commonprogramfiles%", Environment.GetEnvironmentVariable("CommonProgramFiles") ?? "");
        Add("%programfiles(x86)%", Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "");
        Add("%programfiles%", Environment.GetEnvironmentVariable("ProgramW6432")
            ?? Environment.GetEnvironmentVariable("ProgramFiles") ?? "");
        Add("%programdata%", Folder(Environment.SpecialFolder.CommonApplicationData));
        Add("%systemroot%", Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows");
        Add("%userprofile%", Folder(Environment.SpecialFolder.UserProfile));
        Add("%systemdrive%", Environment.GetEnvironmentVariable("SystemDrive") ?? "C:");

        return [.. tokens.OrderByDescending(t => t.Path.Length)];
    }

    private static string Folder(Environment.SpecialFolder folder)
    {
        try
        {
            return Environment.GetFolderPath(folder);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    ///     Resolves the many shapes a startup entry's path arrives in into one absolute path:
    ///     native object paths, environment variables, driver paths relative to the Windows folder,
    ///     and bare executable names.
    /// </summary>
    public static string ExpandPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        var value = path.Trim().Trim('"');
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";

        // Native object paths from service ImagePath values.
        if (value.StartsWith(@"\??\", StringComparison.Ordinal))
            value = value[4..];
        else if (value.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            value = Path.Combine(systemRoot, value[12..]);

        try
        {
            value = Environment.ExpandEnvironmentVariables(value);
        }
        catch
        {
            // Keep the raw text if expansion fails.
        }

        if (!Path.IsPathRooted(value) && value.Length > 0)
        {
            value = value.Contains('\\') || value.Contains('/')
                // Driver ImagePath values are relative to the Windows directory.
                ? Path.Combine(systemRoot, value)
                // A bare name is what the shell would look up, not a file in the Windows folder.
                // Resolving it against SystemRoot invented a path that does not exist, which made
                // every such entry look orphaned.
                : ResolveBareName(value, systemRoot);
        }

        // An unresolved bare name must stay a bare name. Running it through GetFullPath would
        // resolve it against this process's working directory and invent a path inside the app's
        // own folder, which then reads as a real but missing file.
        if (!Path.IsPathRooted(value))
            return value;

        try
        {
            return Path.GetFullPath(value);
        }
        catch
        {
            return value;
        }
    }

    /// <summary>Looks a bare executable name up the way the shell would: System32, then PATH.</summary>
    private static string ResolveBareName(string name, string systemRoot)
    {
        try
        {
            var system32 = Path.Combine(systemRoot, "System32", name);
            if (File.Exists(system32))
                return system32;

            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch
        {
            // Fall through and keep the bare name.
        }

        return name;
    }

    /// <summary>
    ///     Rewrites a path into a machine-independent form: known folders become tokens, and path
    ///     segments that are only a version number, a GUID or a hash become placeholders, so an
    ///     update that moves a program from <c>app\1.2.3\app.exe</c> to <c>app\1.2.4\app.exe</c>
    ///     does not read as a brand new entry needing confirmation.
    /// </summary>
    public static string NormalizePath(string? path)
    {
        var value = ExpandPath(path);
        if (value.Length == 0)
            return "";

        value = value.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

        foreach (var (token, tokenPath) in PathTokens)
        {
            var lower = tokenPath.ToLowerInvariant();
            if (value.Equals(lower, StringComparison.Ordinal))
                return token;
            if (value.StartsWith(lower + "\\", StringComparison.Ordinal))
            {
                value = token + value[lower.Length..];
                break;
            }
        }

        return CollapseVolatileSegments(value);
    }

    private static string CollapseVolatileSegments(string path)
    {
        var segments = path.Split('\\');
        for (var i = 0; i < segments.Length; i++)
        {
            // Never touch the final segment: that is the file name and it identifies the program.
            if (i == segments.Length - 1)
                break;

            var segment = segments[i];
            if (segment.Length == 0 || segment.StartsWith('%'))
                continue;

            if (GuidSegment().IsMatch(segment))
                segments[i] = "#guid";
            else if (VersionSegment().IsMatch(segment))
                segments[i] = "#ver";
            else if (HashSegment().IsMatch(segment))
                segments[i] = "#hash";
        }

        return string.Join('\\', segments);
    }

    [GeneratedRegex(@"^\{?[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}?$", RegexOptions.IgnoreCase)]
    private static partial Regex GuidSegment();

    [GeneratedRegex(@"^v?\d+(\.\d+)+([-_+.][0-9a-z]+)*$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionSegment();

    [GeneratedRegex(@"^[0-9a-f]{16,}$", RegexOptions.IgnoreCase)]
    private static partial Regex HashSegment();

    /// <summary>
    ///     Best-effort extraction of the executable a command line runs. Handles quoting, and steps
    ///     through the usual launchers so the entry is attributed to the DLL or script that actually
    ///     does the work rather than to rundll32 or cmd.
    /// </summary>
    public static string ExtractImagePath(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return "";

        var (head, rest) = SplitFirstArgument(commandLine.Trim());
        if (head.Length == 0)
            return "";

        var fileName = Path.GetFileName(head).ToLowerInvariant();

        switch (fileName)
        {
            case "rundll32.exe":
            case "rundll32":
            {
                // rundll32 [switches] <dll>,<entry point>. The switches matter: "/d" in front of
                // the DLL is common in Windows' own tasks, and taking it as the target produced a
                // nonsense path.
                var (target, _) = SplitFirstArgument(SkipSwitches(rest));
                var comma = target.IndexOf(',');
                if (comma >= 0)
                    target = target[..comma];
                return Sane(target) ? target : head;
            }
            case "cmd.exe":
            case "cmd":
            {
                // "cmd /c start /b FanControl.exe" is FanControl, not cmd. Without this every
                // cmd-launched entry would share one identity, and a decision recorded for one
                // could be offered for another.
                var inner = SkipSwitches(rest);
                if (inner.StartsWith("start", StringComparison.OrdinalIgnoreCase)
                    && (inner.Length == 5 || char.IsWhiteSpace(inner[5])))
                    inner = SkipSwitches(inner[5..]);

                var target = ExtractImagePath(inner);
                return Sane(target) ? target : head;
            }
            case "regsvr32.exe":
            case "mshta.exe":
            case "wscript.exe":
            case "cscript.exe":
            case "powershell.exe":
            case "pwsh.exe":
            {
                var (target, _) = SplitFirstArgument(SkipSwitches(rest));
                return Sane(target) ? target : head;
            }
            default:
                return head;
        }
    }

    /// <summary>
    ///     Rejects a "target" that is really a leftover switch or an empty string. Attributing an
    ///     entry to something like <c>/d</c> would put nonsense in its permanent ledger key.
    /// </summary>
    private static bool Sane(string target) =>
        target.Length > 0 && !target.StartsWith('/') && !target.StartsWith('-');

    private static string SkipSwitches(string arguments)
    {
        var remaining = arguments.TrimStart();
        while (remaining.StartsWith('-') || remaining.StartsWith('/'))
        {
            var (_, rest) = SplitFirstArgument(remaining);
            if (rest.Length == remaining.Length)
                break;
            remaining = rest.TrimStart();
        }

        return remaining;
    }

    private static (string Head, string Remainder) SplitFirstArgument(string value)
    {
        var text = value.TrimStart();
        if (text.Length == 0)
            return ("", "");

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end < 0 ? (text[1..], "") : (text[1..end], text[(end + 1)..].TrimStart());
        }

        // An unquoted path with spaces and no arguments is the whole string. The prefix walk below
        // only ever tests text up to a space, so without this a command such as
        // C:\Program Files\Npcap\CheckStatus.bat would be cut down to C:\Program.
        if (LooksLikeExistingFile(text))
            return (text, "");

        // Unquoted paths with spaces are common ("C:\Program Files\App\app.exe -run"). Extend the
        // candidate across spaces while a longer prefix still names an existing file.
        var space = text.IndexOf(' ');
        if (space < 0)
            return (text, "");

        var candidate = text[..space];
        var probe = space;
        while (probe >= 0)
        {
            var expanded = text[..probe];
            if (LooksLikeExistingFile(expanded))
                candidate = expanded;
            probe = text.IndexOf(' ', probe + 1);
        }

        return (candidate, text[candidate.Length..].TrimStart());
    }

    private static bool LooksLikeExistingFile(string candidate)
    {
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(candidate.Trim('"'));
            return expanded.Length > 0 && File.Exists(expanded);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     The strict key: kind, registration location, entry name and normalized image path. Any of
    ///     those changing is a different entry that must be confirmed again, which is the point:
    ///     a new binary taking over an existing Run value should not inherit its permission.
    /// </summary>
    public static string ComputeKey(
        StartupItemKind kind,
        string location,
        string name,
        string? imagePath
    )
    {
        var builder = new StringBuilder();
        builder.Append(kind).Append('|');
        builder.Append(location.ToLowerInvariant()).Append('|');
        builder.Append(name.ToLowerInvariant()).Append('|');
        builder.Append(NormalizePath(imagePath));
        return builder.ToString();
    }

    /// <summary>
    ///     The loose key: kind, file name and publisher only. Used as a fallback so a decision made
    ///     on another machine still matches when the program is installed elsewhere. A loose match
    ///     is surfaced to the user rather than applied silently.
    /// </summary>
    public static string ComputeLooseKey(
        StartupItemKind kind,
        string? entryName,
        string? imagePath,
        string? publisher
    )
    {
        // The entry name stays in. Without it, two registrations of one executable, such as the
        // gupdate and gupdatem services that both run GoogleUpdate.exe, share a loose key, and a
        // decision about one gets applied to its sibling. What legitimately differs between
        // machines is where the program is installed, not what its registration is called.
        if (string.IsNullOrWhiteSpace(entryName))
            return "";

        var fileName = "";
        try
        {
            var normalized = NormalizePath(imagePath);
            fileName = Path.GetFileName(normalized);
        }
        catch
        {
            // Leave empty; the loose key is then too weak to match and simply never hits.
        }

        if (fileName.Length == 0)
            return "";

        return $"~|{kind}|{entryName.Trim().ToLowerInvariant()}|{fileName}|{(publisher ?? "").ToLowerInvariant()}";
    }
}
