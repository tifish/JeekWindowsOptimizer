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
///         <see cref="Clock" /> is a minimal Hybrid Logical Clock (UTC milliseconds, monotonically
///         advancing past the highest clock seen across all shards). Unsynced machines resolve
///         conflicts by real UTC time, while once a machine observes a skewed future clock its next
///         local edit increments past it and strictly wins. <see cref="DecidedAtUtc" /> is for
///         display and as a secondary tie-break only.
///     </para>
/// </summary>
public sealed class StartupDecisionEntry
{
    public StartupDecision Decision { get; set; }

    /// <summary>Hybrid logical clock at the moment of the decision. Higher wins a merge.</summary>
    public long Clock { get; set; }

    public DateTime DecidedAtUtc { get; set; }

    /// <summary>Machine that made the decision, shown in the UI and used as a deterministic tie-break.</summary>
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
    ///     when it rewrites a shard that is synced back to the machine running the newer build.
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
///     The on-disk decision ledger (stored per machine under <c>Config/StartupDecisions/&lt;MachineName&gt;.json</c>,
///     with legacy <c>Config/StartupDecisions.json</c> read as a fallback layer). Merging is per entry,
///     pure and commutative, so every machine reading the same set of shards converges to the exact
///     same state regardless of merge order or locale.
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

    /// <summary>Sanitized machine name that owns this shard. Omitted on legacy single-file ledgers.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Machine { get; set; }

    /// <summary>Incremented by "forget everything". On merge the lower epoch is discarded wholesale.</summary>
    public long Epoch { get; set; }

    /// <summary>Highest hybrid logical clock this ledger has seen.</summary>
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
            Machine = Machine,
            Epoch = Epoch,
            Clock = Clock,
            Extra = Extra is null ? null : new Dictionary<string, JsonElement>(Extra),
            Entries = new Dictionary<string, StartupDecisionEntry>(StringComparer.Ordinal),
        };
        foreach (var (key, entry) in Entries)
            clone.Entries[key] = entry.Clone();
        return clone;
    }

    /// <summary>Returns a copy with entries ordered ordinally by key for clean, deterministic serialization.</summary>
    public StartupDecisionLedger CloneSorted(string? ownerMachine = null)
    {
        var clone = new StartupDecisionLedger
        {
            Version = Version,
            Machine = ownerMachine ?? Machine,
            Epoch = Epoch,
            Clock = Clock,
            Extra = Extra is null ? null : new Dictionary<string, JsonElement>(Extra),
            Entries = new Dictionary<string, StartupDecisionEntry>(StringComparer.Ordinal),
        };
        foreach (var (key, entry) in Entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            clone.Entries[key] = entry.Clone();
        return clone;
    }

    /// <summary>
    ///     Next Hybrid Logical Clock value to stamp on a new decision: UTC milliseconds when wall
    ///     clock is ahead, otherwise <c>highest + 1</c> so local edits strictly beat skewed remote
    ///     clocks once observed.
    /// </summary>
    public long NextClock(long? nowUtcMsOverride = null)
    {
        var highest = GetHighestClock();
        var now = nowUtcMsOverride ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Clock = now > highest ? now : highest + 1;
        return Clock;
    }

    public long GetHighestClock()
    {
        var highest = Clock;
        foreach (var entry in Entries.Values)
            if (entry.Clock > highest)
                highest = entry.Clock;
        return highest;
    }

    public void Normalize()
    {
        if (Version < 1)
            Version = 1;
        if (Epoch < 0)
            Epoch = 0;
        if (Clock < 0)
            Clock = 0;
        if (string.IsNullOrWhiteSpace(Machine))
            Machine = null;

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
    ///     the other side is another machine's shard, a legacy file, a conflict copy, or an import.
    /// </summary>
    public static StartupDecisionLedger Merge(StartupDecisionLedger left, StartupDecisionLedger right)
    {
        left.Normalize();
        right.Normalize();

        // A "forget everything" on either side wipes the older generation entirely, while still
        // preserving the highest known clock and version.
        if (left.Epoch != right.Epoch)
        {
            var winner = (left.Epoch > right.Epoch ? left : right).Clone();
            winner.Version = Math.Max(left.Version, right.Version);
            winner.Clock = Math.Max(left.Clock, right.Clock);
            winner.Machine = null;
            winner.Normalize();
            return winner;
        }

        var merged = left.Clone();
        merged.Machine = null;
        merged.Version = Math.Max(left.Version, right.Version);
        merged.Clock = Math.Max(left.Clock, right.Clock);

        if (right.Extra is not null)
        {
            merged.Extra ??= new Dictionary<string, JsonElement>();
            foreach (var (extraKey, extraVal) in right.Extra)
                merged.Extra.TryAdd(extraKey, extraVal);
        }

        foreach (var (key, incoming) in right.Entries)
        {
            if (!merged.Entries.TryGetValue(key, out var existing) || Wins(incoming, existing))
                merged.Entries[key] = incoming.Clone();
        }

        merged.Normalize();
        return merged;
    }

    /// <summary>
    ///     Strict total order for entry-level LWW merging: HLC clock first, then wall clock,
    ///     then machine name, then decision value, so any merge order converges identically.
    /// </summary>
    internal static bool Wins(StartupDecisionEntry candidate, StartupDecisionEntry incumbent)
    {
        if (candidate.Clock != incumbent.Clock)
            return candidate.Clock > incumbent.Clock;
        if (candidate.DecidedAtUtc != incumbent.DecidedAtUtc)
            return candidate.DecidedAtUtc > incumbent.DecidedAtUtc;
        var machineCmp = string.CompareOrdinal(candidate.Machine ?? "", incumbent.Machine ?? "");
        if (machineCmp != 0)
            return machineCmp > 0;
        return candidate.Decision > incumbent.Decision;
    }
}
