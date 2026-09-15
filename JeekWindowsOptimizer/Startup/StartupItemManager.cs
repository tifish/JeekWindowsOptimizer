using System.Diagnostics;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer.Startup;

/// <summary>Everything one sweep produced.</summary>
public sealed record StartupScanResult(
    List<StartupItem> Items,
    bool BaselinePending,
    TimeSpan Duration
);

/// <summary>
///     Runs the scanners, attaches signatures and remembered decisions, and hands back the rows the
///     Startup tab shows. Scanning only reads; nothing here changes the system.
/// </summary>
public static class StartupItemManager
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(StartupItemManager));

    /// <summary>The order the groups appear in, from the most user-facing to the most technical.</summary>
    public static readonly StartupItemKind[] KindOrder =
    [
        StartupItemKind.LogonRegistry,
        StartupItemKind.RunOnce,
        StartupItemKind.StartupFolder,
        StartupItemKind.ScheduledTask,
        StartupItemKind.ExplorerExtension,
        StartupItemKind.InternetExplorerExtension,
        StartupItemKind.Service,
        StartupItemKind.Driver,
    ];

    public static async Task<StartupScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var raw = await Task.Run(
            () =>
            {
                // The scanners are independent and each is dominated by registry or COM latency.
                var tasks = new[]
                {
                    Task.Run(StartupRegistryScanner.Scan, cancellationToken),
                    Task.Run(StartupFolderScanner.Scan, cancellationToken),
                    Task.Run(ScheduledTaskScanner.Scan, cancellationToken),
                    Task.Run(ServiceRegistryScanner.Scan, cancellationToken),
                    Task.Run(ShellExtensionScanner.Scan, cancellationToken),
                };

                Task.WaitAll(tasks, cancellationToken);
                return tasks.SelectMany(task => task.Result).ToList();
            },
            cancellationToken
        );

        var items = await Task.Run(() => Enrich(raw, cancellationToken), cancellationToken);

        stopwatch.Stop();
        Log.ZLogInformation(
            $"Startup scan produced {items.Count} items in {stopwatch.ElapsedMilliseconds} ms"
        );

        return new StartupScanResult(items, !StartupLocalState.HasBaseline, stopwatch.Elapsed);
    }

    private static List<StartupItem> Enrich(List<RawStartupEntry> raw, CancellationToken cancellationToken)
    {
        // Signature verification is the expensive part of a sweep: it hashes each file and, for the
        // catalog-signed majority under the Windows folder, searches the catalog store. Results are
        // cached by path plus write time, so only the first sweep on a machine pays in full.
        var signatures = new Dictionary<string, FileSignature>(StringComparer.OrdinalIgnoreCase);
        var paths = raw.Select(entry => Expand(entry.ImagePath))
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Parallel.ForEach(
            paths,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount),
            },
            path =>
            {
                var signature = FileSignatureInfo.Get(path);
                lock (signatures)
                    signatures[path] = signature;
            }
        );

        var decisions = StartupDecisionStore.Snapshot();

        var items = new List<StartupItem>(raw.Count);

        foreach (var entry in raw)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var expanded = Expand(entry.ImagePath);
            var signature = expanded.Length > 0 && signatures.TryGetValue(expanded, out var found)
                ? found
                : FileSignature.None;

            var signerKind = signature.Trust switch
            {
                SignatureTrust.Valid when signature.IsWindowsComponent => StartupSignerKind.Windows,
                SignatureTrust.Valid when signature.IsMicrosoft => StartupSignerKind.Microsoft,
                SignatureTrust.Valid => StartupSignerKind.ThirdParty,
                SignatureTrust.Invalid => StartupSignerKind.Invalid,
                _ => StartupSignerKind.Unsigned,
            };

            var key = StartupIdentity.ComputeKey(
                entry.Kind,
                entry.Location,
                entry.Name,
                entry.ImagePath
            );
            var looseKey = StartupIdentity.ComputeLooseKey(
                entry.Kind,
                entry.Name,
                entry.ImagePath,
                signature.SignerName
            );

            var item = new StartupItem
            {
                Kind = entry.Kind,
                Key = key,
                LooseKey = looseKey,
                Name = entry.Name,
                Location = entry.Location,
                Command = entry.Command,
                ImagePath = expanded,
                Description = ReadFileDescription(expanded),
                HookPoints = entry.HookPoints,
                Handles = entry.Handles,
                Publisher = signature.SignerName,
                SignerKind = signerKind,
                IsOrphaned = expanded.Length > 0 && !File.Exists(expanded),
                CanToggle = entry.CanToggle,
                ReadOnlyReasonKey = entry.ReadOnlyReasonKey,
                RestoreState = entry.RestoreState,
                IsEnabled = entry.IsEnabled,
            };

            items.Add(item);
        }

        // The loose index lets a decision made on another machine match a program installed in a
        // different place here. It is built only once every item is known, because a decision whose
        // exact key belongs to something present on this machine is that item's decision and must
        // not also be lent to others.
        var looseIndex = BuildLooseIndex(decisions, items.Select(item => item.Key));
        foreach (var item in items)
            ApplyDecision(item, decisions, looseIndex);

        StartupLocalState.RecordSeen(items.Select(item => item.Key));

        return
        [
            .. items
                .OrderBy(item => Array.IndexOf(KindOrder, item.Kind))
                .ThenByDescending(item => item.IsPending)
                .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        ];
    }

    /// <summary>
    ///     Decisions reachable by loose key. Two exclusions keep it from guessing: a decision whose
    ///     exact key is present on this machine already has its item, and a loose key claimed by
    ///     decisions that disagree says nothing useful.
    /// </summary>
    internal static Dictionary<string, StartupDecisionEntry?> BuildLooseIndex(
        Dictionary<string, StartupDecisionEntry> decisions,
        IEnumerable<string> presentKeys
    )
    {
        var index = new Dictionary<string, StartupDecisionEntry?>(StringComparer.Ordinal);
        var present = new HashSet<string>(presentKeys, StringComparer.Ordinal);

        foreach (var (key, entry) in decisions)
        {
            if (present.Contains(key))
                continue;

            var parts = key.Split('|');
            if (!Enum.TryParse<StartupItemKind>(parts[0], out var kind))
                continue;

            // Older entries predate the stored name and image path. Recover them from the key and
            // command line, but only when the key splits unambiguously into its four segments.
            var entryName = entry.EntryName ?? (parts.Length == 4 ? parts[2] : null);
            var imagePath = entry.ImagePath ?? StartupIdentity.ExtractImagePath(entry.Command);
            var looseKey = StartupIdentity.ComputeLooseKey(kind, entryName, imagePath, entry.Publisher);
            if (looseKey.Length == 0)
                continue;

            if (index.TryGetValue(looseKey, out var existing))
            {
                // Two different decisions share this loose key, or the same one does. Only keep it
                // when they agree; otherwise a loose match cannot say anything useful.
                if (existing is not null && existing.Decision != entry.Decision)
                    index[looseKey] = null;
                continue;
            }

            index[looseKey] = entry;
        }

        return index;
    }

    private static void ApplyDecision(
        StartupItem item,
        Dictionary<string, StartupDecisionEntry> decisions,
        Dictionary<string, StartupDecisionEntry?> looseIndex
    )
    {
        if (decisions.TryGetValue(item.Key, out var exact))
        {
            item.Decision = exact.Decision;
            item.DecidedOnMachine = exact.Machine;
            item.DecidedAtUtc = exact.DecidedAtUtc;
            item.DecisionSource = string.Equals(
                exact.Machine,
                Environment.MachineName,
                StringComparison.OrdinalIgnoreCase
            )
                ? StartupDecisionSource.ThisMachine
                : StartupDecisionSource.OtherMachine;
            return;
        }

        if (item.LooseKey.Length > 0
            && looseIndex.TryGetValue(item.LooseKey, out var loose)
            && loose is not null)
        {
            item.Decision = loose.Decision;
            item.DecidedOnMachine = loose.Machine;
            item.DecidedAtUtc = loose.DecidedAtUtc;
            item.DecisionSource = StartupDecisionSource.LooseMatch;
            return;
        }

        item.Decision = null;
        item.DecisionSource = StartupDecisionSource.None;
    }

    /// <summary>
    ///     The absolute path of an entry's file. Shares <see cref="StartupIdentity.ExpandPath" /> with
    ///     the key computation so the file that gets signature-checked is the one the key names.
    /// </summary>
    private static string Expand(string path) => StartupIdentity.ExpandPath(path);

    private static string? ReadFileDescription(string path)
    {
        try
        {
            if (path.Length == 0 || !File.Exists(path))
                return null;

            var info = FileVersionInfo.GetVersionInfo(path);
            var description = info.FileDescription?.Trim();
            return string.IsNullOrWhiteSpace(description) ? null : description;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Turns the current state of a fresh machine into decisions: what runs today is allowed,
    ///     what is already switched off is denied.
    ///     <para>
    ///         Without this step the first run on a machine with years of installed software would
    ///         present hundreds of rows all needing confirmation, and the user would click through
    ///         them without reading. Windows' own entries are left out: they are hidden anyway, and
    ///         recording a decision for each of several hundred of them would bury the handful of
    ///         entries that actually matter.
    ///     </para>
    /// </summary>
    public static int AcceptBaseline(IEnumerable<StartupItem> items)
    {
        var decisions = items
            .Where(item => !item.IsWindowsEntry && item.IsPending)
            .Select(item =>
                (
                    item.Key,
                    item.IsEnabled ? StartupDecision.Allow : StartupDecision.Deny,
                    item.ToSnapshot()
                )
            )
            .ToList();

        StartupDecisionStore.DecideMany(decisions);
        StartupDecisionStore.Flush();
        StartupLocalState.MarkBaselineTaken();

        return decisions.Count;
    }
}
