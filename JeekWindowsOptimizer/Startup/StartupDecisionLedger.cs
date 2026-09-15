using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeekWindowsOptimizer.Startup;

/// <summary>What the user decided about a startup entry. Absence of an entry means "not decided".</summary>
public enum StartupDecision
{
    Deny = 0,
    Allow = 1,
}

/// <summary>
///     One remembered decision. Entries are never removed: an item that no longer exists on this
///     machine keeps its decision so it applies again if it comes back, or on another machine.
///     <para>
///         <see cref="Clock" /> is a Lamport counter, not a timestamp: it is the tie-breaker when two
///         machines decided the same key without seeing each other. A wall clock cannot be trusted
///         for that, because a machine with a dead CMOS battery would win or lose every conflict
///         forever. <see cref="DecidedAtUtc" /> is for display and as a secondary tie-break only.
///     </para>
/// </summary>
public sealed class StartupDecisionEntry
{
    public StartupDecision Decision { get; set; }

    /// <summary>Lamport counter at the moment of the decision. Higher wins a merge.</summary>
    public long Clock { get; set; }

    public DateTime DecidedAtUtc { get; set; }

    /// <summary>Machine that made the decision, shown in the UI and used as a final deterministic tie-break.</summary>
    public string? Machine { get; set; }

    // ---- Display snapshot, so an item absent from this machine can still be listed. ----

    public string? Kind { get; set; }
    public string? Name { get; set; }
    public string? Publisher { get; set; }
    public string? Command { get; set; }

    /// <summary>
    ///     The raw entry name (Run value, service name, task path). Stored separately because the
    ///     loose key needs it, and recovering it by splitting the key breaks on names containing '|'.
    /// </summary>
    public string? EntryName { get; set; }

    /// <summary>The file the entry ran, which is not always derivable from the command line.</summary>
    public string? ImagePath { get; set; }

    /// <summary>
    ///     Fields written by a newer app version. Kept so an older build cannot silently drop them
    ///     when it rewrites a file that is synced back to the machine running the newer build.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public StartupDecisionEntry Clone() =>
        new()
        {
            Decision = Decision,
            Clock = Clock,
            DecidedAtUtc = DecidedAtUtc,
            Machine = Machine,
            Kind = Kind,
            Name = Name,
            Publisher = Publisher,
            Command = Command,
            EntryName = EntryName,
            ImagePath = ImagePath,
            Extra = Extra is null ? null : new Dictionary<string, JsonElement>(Extra),
        };
}

/// <summary>
///     The on-disk decision ledger. Designed to be synced between machines by an ordinary file
///     sync tool, so merging is per entry rather than per file: two machines that each decided
///     different items must both keep their decisions.
///     <para>
///         There are no tombstones. An absent key already means "not decided", and the whole point
///         of the ledger is to never forget a decision, so re-appearing is the wanted behaviour.
///         Starting over is a file-level <see cref="Epoch" /> bump instead.
///     </para>
/// </summary>
public sealed class StartupDecisionLedger
{
    /// <summary>Bumped only for a breaking layout change. A file from the future is read but never written.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Incremented by "forget everything". On merge the lower epoch is discarded wholesale.</summary>
    public long Epoch { get; set; }

    /// <summary>Highest Lamport counter this file has seen.</summary>
    public long Clock { get; set; }

    public Dictionary<string, StartupDecisionEntry> Entries { get; set; } =
        new(StringComparer.Ordinal);

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public StartupDecisionLedger Clone()
    {
        var clone = new StartupDecisionLedger
        {
            Version = Version,
            Epoch = Epoch,
            Clock = Clock,
            Extra = Extra is null ? null : new Dictionary<string, JsonElement>(Extra),
            Entries = new Dictionary<string, StartupDecisionEntry>(StringComparer.Ordinal),
        };
        foreach (var (key, entry) in Entries)
            clone.Entries[key] = entry.Clone();
        return clone;
    }

    /// <summary>Next Lamport counter to stamp on a new decision.</summary>
    public long NextClock()
    {
        var highest = Clock;
        foreach (var entry in Entries.Values)
            if (entry.Clock > highest)
                highest = entry.Clock;
        Clock = highest + 1;
        return Clock;
    }

    public void Normalize()
    {
        if (Version < 1)
            Version = 1;
        if (Epoch < 0)
            Epoch = 0;

        Entries ??= new Dictionary<string, StartupDecisionEntry>(StringComparer.Ordinal);
        if (Entries.Comparer != StringComparer.Ordinal)
            Entries = new Dictionary<string, StartupDecisionEntry>(Entries, StringComparer.Ordinal);

        foreach (var key in Entries.Keys.Where(string.IsNullOrWhiteSpace).ToList())
            Entries.Remove(key);

        foreach (var entry in Entries.Values)
        {
            if (!Enum.IsDefined(entry.Decision))
                entry.Decision = StartupDecision.Deny;
            if (entry.Clock < 0)
                entry.Clock = 0;
            if (entry.Clock > Clock)
                Clock = entry.Clock;
        }
    }

    /// <summary>
    ///     Merges two ledgers into a new one. Pure and commutative, so it does not matter whether
    ///     the other side is the file on disk, a sync tool's conflict copy, or an import.
    /// </summary>
    public static StartupDecisionLedger Merge(StartupDecisionLedger left, StartupDecisionLedger right)
    {
        left.Normalize();
        right.Normalize();

        // A "forget everything" on either side wipes the older generation entirely.
        if (left.Epoch != right.Epoch)
            return (left.Epoch > right.Epoch ? left : right).Clone();

        var merged = left.Clone();
        merged.Version = Math.Max(left.Version, right.Version);
        merged.Clock = Math.Max(left.Clock, right.Clock);

        foreach (var (key, incoming) in right.Entries)
        {
            if (!merged.Entries.TryGetValue(key, out var existing) || Wins(incoming, existing))
                merged.Entries[key] = incoming.Clone();
        }

        merged.Normalize();
        return merged;
    }

    /// <summary>Lamport counter first, then wall clock, then machine name so the result is deterministic.</summary>
    private static bool Wins(StartupDecisionEntry candidate, StartupDecisionEntry incumbent)
    {
        if (candidate.Clock != incumbent.Clock)
            return candidate.Clock > incumbent.Clock;
        if (candidate.DecidedAtUtc != incumbent.DecidedAtUtc)
            return candidate.DecidedAtUtc > incumbent.DecidedAtUtc;
        return string.CompareOrdinal(candidate.Machine ?? "", incumbent.Machine ?? "") > 0;
    }
}
