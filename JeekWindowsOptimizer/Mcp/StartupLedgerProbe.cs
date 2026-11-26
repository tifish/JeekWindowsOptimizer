using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JeekWindowsOptimizer.Startup;
using Microsoft.Win32;

namespace JeekWindowsOptimizer.Mcp;

/// <summary>
///     Regression checks for the parts of the startup feature that would corrupt data silently:
///     the cross-machine merge, the reset epoch, the unreadable-file guard, and the identity keys
///     that decide whether a remembered choice still matches after an update or on another computer.
///     <para>
///         Everything here runs on synthetic ledgers, a temporary folder, and a scratch class id
///         of our own under HKCU. No real startup entry is read or changed, and the real decision
///         file is never touched.
///     </para>
/// </summary>
public static class StartupLedgerProbe
{
    /// <summary>Records one check. A delegate type, not Action, so the detail argument can default.</summary>
    private delegate void CheckFn(string name, bool passed, string detail = "");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Task<string> RunAsync(string scenario)
    {
        var report = new StringBuilder();
        var failures = 0;

        void Check(string name, bool passed, string detail = "")
        {
            if (!passed)
                failures++;
            report.Append(passed ? "PASS  " : "FAIL  ").Append(name);
            if (detail.Length > 0)
                report.Append("  [").Append(detail).Append(']');
            report.AppendLine();
        }

        switch (scenario)
        {
            case "merge":
                MergeScenario(Check);
                break;
            case "shards":
                ShardsScenario(Check);
                break;
            case "epoch":
                EpochScenario(Check);
                break;
            case "conflict":
                ConflictScenario(Check);
                break;
            case "poison":
                PoisonScenario(Check);
                break;
            case "identity":
                IdentityScenario(Check);
                break;
            case "addon":
                BrowserAddOnScenario(Check);
                break;
            case "all":
                MergeScenario(Check);
                ShardsScenario(Check);
                EpochScenario(Check);
                ConflictScenario(Check);
                PoisonScenario(Check);
                IdentityScenario(Check);
                BrowserAddOnScenario(Check);
                break;
            default:
                return Task.FromResult(
                    $"Unknown scenario '{scenario}'. Use merge | shards | epoch | conflict | poison | identity | addon | all."
                );
        }

        report.AppendLine();
        report.AppendLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
        return Task.FromResult(report.ToString());
    }

    // ---------- Scenarios ----------

    private static void MergeScenario(CheckFn check)
    {
        // Two machines each decided something the other never saw. Both must survive: this is the
        // case a whole-file or per-property merge gets wrong.
        var a = Ledger(("app.a", StartupDecision.Allow, 1, "PC-A"));
        var b = Ledger(("app.b", StartupDecision.Deny, 1, "PC-B"));

        var merged = StartupDecisionLedger.Merge(a, b);
        check("disjoint decisions from two machines both survive", merged.Entries.Count == 2,
            $"count={merged.Entries.Count}");

        // Same key, different answers. The higher Lamport counter is the later decision.
        var older = Ledger(("shared", StartupDecision.Allow, 3, "PC-A"));
        var newer = Ledger(("shared", StartupDecision.Deny, 7, "PC-B"));
        var conflict = StartupDecisionLedger.Merge(older, newer);
        check("higher logical clock wins a conflict",
            conflict.Entries["shared"].Decision == StartupDecision.Deny,
            conflict.Entries["shared"].Decision.ToString());

        // Merge is commutative: the file on disk and the copy in memory must agree on a winner
        // regardless of which side is which.
        var reversed = StartupDecisionLedger.Merge(newer, older);
        check("merge is commutative",
            reversed.Entries["shared"].Decision == conflict.Entries["shared"].Decision);

        // A machine whose wall clock is years wrong must not win on that alone.
        var skewed = Ledger(("shared", StartupDecision.Allow, 2, "PC-BADCLOCK"));
        skewed.Entries["shared"].DecidedAtUtc = DateTime.UtcNow.AddYears(5);
        var trusted = Ledger(("shared", StartupDecision.Deny, 9, "PC-A"));
        var skewResult = StartupDecisionLedger.Merge(skewed, trusted);
        check("a wrong wall clock does not beat the logical clock",
            skewResult.Entries["shared"].Decision == StartupDecision.Deny,
            skewResult.Entries["shared"].Machine ?? "");

        // Merging the same ledger twice must not change it: the watcher sees our own writes.
        var idempotent = StartupDecisionLedger.Merge(conflict, conflict.Clone());
        check("merging a ledger with itself is idempotent",
            Serialize(idempotent) == Serialize(conflict));

        // No tombstones by design: a key deleted on one side comes back from the other, which is
        // what "remember a decision forever" means.
        var withEntry = Ledger(("kept", StartupDecision.Allow, 4, "PC-A"));
        var withoutEntry = new StartupDecisionLedger { Clock = 9 };
        var resurrected = StartupDecisionLedger.Merge(withoutEntry, withEntry);
        check("a decision missing on one side is restored from the other",
            resurrected.Entries.ContainsKey("kept"));

        // Fields a newer build wrote must survive a round trip through this build.
        var future = Ledger(("future", StartupDecision.Allow, 1, "PC-A"));
        var json = Serialize(future).Replace(
            "\"Machine\": \"PC-A\"",
            "\"Machine\": \"PC-A\",\n      \"FutureField\": 42"
        );
        var reparsed = JsonSerializer.Deserialize<StartupDecisionLedger>(json, JsonOptions)!;
        var roundTripped = Serialize(reparsed);
        check("unknown fields from a newer build are preserved",
            roundTripped.Contains("FutureField"));
    }

    private static void ShardsScenario(CheckFn check)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "JeekStartupShards_" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(root);

        try
        {
            // 1. Legacy Config/StartupDecisions.json fallback is read seamlessly without being modified.
            var legacyLedger = Ledger(
                ("legacy1", StartupDecision.Allow, 1, "OLD-PC"),
                ("shared", StartupDecision.Allow, 2, "OLD-PC")
            );
            var legacyPath = Path.Combine(root, StartupDecisionStore.LegacyFileName);
            Write(root, StartupDecisionStore.LegacyFileName, legacyLedger);
            var legacyBefore = File.ReadAllText(legacyPath);

            var pcA = new StartupDecisionShardStore(() => root, "PC-A", enableBackgroundTimers: false);
            var pcB = new StartupDecisionShardStore(() => root, "PC-B", enableBackgroundTimers: false);
            pcA.Load();
            pcB.Load();

            check("legacy single file loaded on PC-A",
                pcA.Find("legacy1")?.Decision == StartupDecision.Allow
                && pcA.Find("shared")?.Decision == StartupDecision.Allow);

            // Flush with no local changes must not create a shard or trigger sync churn.
            pcA.Flush();
            check("no-op flush does not create an empty shard", !File.Exists(pcA.OwnShardPath));

            // 2. Truly offline concurrent disjoint edits across PC-A (folder A) and PC-B (folder B),
            // then synced into one folder -> zero conflicts, both preserved.
            var rootB = Path.Combine(root, "offline-b");
            Directory.CreateDirectory(rootB);
            File.Copy(legacyPath, Path.Combine(rootB, StartupDecisionStore.LegacyFileName));
            var pcBOffline = new StartupDecisionShardStore(() => rootB, "PC-B", enableBackgroundTimers: false);
            pcBOffline.Load();

            check("PC-A records decision 'app.a'",
                pcA.Decide("app.a", StartupDecision.Allow, new StartupDecisionSnapshot("LogonRegistry", "AppA", null, "a.exe")));
            pcA.Flush();

            check("PC-B records decision 'app.b' offline",
                pcBOffline.Decide("app.b", StartupDecision.Deny, new StartupDecisionSnapshot("Service", "AppB", null, "b.exe")));
            pcBOffline.Flush();

            var shardsDir = Path.Combine(root, StartupDecisionStore.ShardsDirName);
            var shardA = Path.Combine(shardsDir, "PC-A.json");
            var shardB = Path.Combine(shardsDir, "PC-B.json");
            // Simulate file sync copying PC-B.json into root/StartupDecisions/.
            File.Copy(pcBOffline.OwnShardPath, shardB);
            check("PC-A wrote only its own shard", File.Exists(shardA));
            check("PC-B wrote only its own shard", File.Exists(shardB));
            check("legacy single file remained untouched",
                File.ReadAllText(legacyPath) == legacyBefore);

            var shardAWriteBeforeReload = File.GetLastWriteTimeUtc(shardA);
            var shardBWriteBeforeReload = File.GetLastWriteTimeUtc(shardB);
            var changedA = pcA.ReloadFromDisk(notify: false);
            var changedB = pcB.ReloadFromDisk(notify: false);
            check("PC-A merged both disjoint decisions",
                changedA
                && pcA.Find("app.a")?.Decision == StartupDecision.Allow
                && pcA.Find("app.b")?.Decision == StartupDecision.Deny
                && pcA.Find("legacy1")?.Decision == StartupDecision.Allow);
            check("PC-B merged both disjoint decisions",
                changedB
                && pcB.Find("app.a")?.Decision == StartupDecision.Allow
                && pcB.Find("app.b")?.Decision == StartupDecision.Deny
                && pcB.Find("legacy1")?.Decision == StartupDecision.Allow);
            check("reloading does not rewrite shards (no sync echo)",
                File.GetLastWriteTimeUtc(shardA) == shardAWriteBeforeReload
                && File.GetLastWriteTimeUtc(shardB) == shardBWriteBeforeReload);

            // 3. HLC clock skew resilience: remote PC-C shard 10 minutes in the future.
            var futureTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 600_000;
            var pcCLedger = Ledger(("shared", StartupDecision.Deny, futureTs, "PC-C"));
            pcCLedger.Machine = "PC-C";
            Write(shardsDir, "PC-C.json", pcCLedger);

            // Even if PC-A decides BEFORE the watcher reloads PC-C.json, Flush() must bump the
            // local edit's HLC strictly past PC-C's skewed clock.
            pcA.Decide("shared", StartupDecision.Allow, new StartupDecisionSnapshot("LogonRegistry", "Shared", null, "s.exe"));
            pcA.Flush();

            pcB.ReloadFromDisk(notify: false);
            var resolvedShared = pcB.Find("shared");
            check("HLC local edit strictly beats skewed future remote shard",
                resolvedShared is not null
                && resolvedShared.Decision == StartupDecision.Allow
                && resolvedShared.Clock > futureTs,
                resolvedShared is null ? "null" : $"{resolvedShared.Decision} clock={resolvedShared.Clock}");

            // 4. Epoch reset (ForgetAll) across shards and legacy file.
            check("PC-A forgets all decisions", pcA.ForgetAll());
            pcA.Flush();
            pcB.ReloadFromDisk(notify: false);
            check("ForgetAll epoch beats legacy file and other shards on PC-B",
                pcB.Count == 0 && pcB.Epoch == 1,
                $"count={pcB.Count} epoch={pcB.Epoch}");

            pcB.Decide("after.reset", StartupDecision.Allow, new StartupDecisionSnapshot("Service", "AfterReset", null, "r.exe"));
            pcB.Flush();
            pcA.ReloadFromDisk(notify: false);
            check("decisions made after ForgetAll propagate cleanly across shards",
                pcA.Count == 1 && pcA.Find("after.reset")?.Decision == StartupDecision.Allow,
                $"count={pcA.Count}");

            // 5. Storage switch without moving files: carry over local decisions and merge with destination.
            var destRoot = Path.Combine(root, "dest");
            Directory.CreateDirectory(destRoot);
            var destShards = Path.Combine(destRoot, StartupDecisionStore.ShardsDirName);
            Directory.CreateDirectory(destShards);
            var remoteDestLedger = Ledger(("dest.only", StartupDecision.Deny, 10, "PC-DEST"));
            remoteDestLedger.Epoch = 1;
            remoteDestLedger.Machine = "PC-DEST";
            Write(destShards, "PC-DEST.json", remoteDestLedger);

            var activeDir = root;
            var switched = new StartupDecisionShardStore(() => activeDir, "PC-A", enableBackgroundTimers: false);
            switched.Load();
            activeDir = destRoot;
            switched.SyncWithCurrentLocation(notify: false);
            check("storage switch without move merges local and destination shards",
                switched.Find("after.reset")?.Decision == StartupDecision.Allow
                && switched.Find("dest.only")?.Decision == StartupDecision.Deny
                && File.Exists(Path.Combine(destShards, "PC-A.json")));

            // Empty store switching to destination with existing shards loads them without writing an empty shard.
            var emptyDir = Path.Combine(root, "empty-src");
            Directory.CreateDirectory(emptyDir);
            var emptyActive = emptyDir;
            var emptyStore = new StartupDecisionShardStore(() => emptyActive, "PC-EMPTY", enableBackgroundTimers: false);
            emptyStore.Load();
            emptyActive = destRoot;
            emptyStore.SyncWithCurrentLocation(notify: false);
            check("empty store switch loads destination shards without creating empty shard",
                emptyStore.Find("dest.only")?.Decision == StartupDecision.Deny
                && !File.Exists(Path.Combine(destShards, "PC-EMPTY.json")));

            // 6. Machine name sanitization guards invalid chars and Windows reserved names.
            check("machine name sanitization replaces invalid chars and guards reserved names",
                StartupDecisionStore.SanitizeMachineName(" PC/A:1 ") == "PC_A_1"
                && StartupDecisionStore.SanitizeMachineName("CON") == "_CON"
                && StartupDecisionStore.SanitizeMachineName("   ") == "default");

            // 7. Live RoamingConfigWatcher detects remote shards, ignores .tmp and own self-writes.
            var watchRoot = Path.Combine(root, "watch");
            Directory.CreateDirectory(watchRoot);
            var watchStore = new StartupDecisionShardStore(() => watchRoot, "PC-LOCAL", enableBackgroundTimers: false);
            watchStore.Load();

            var watchTriggers = 0;
            var marker = new object();
            using (var watcher = new RoamingConfigWatcher(
                       TimeSpan.FromMilliseconds(250),
                       (rel, changeType) => watchStore.IsInterestingChange(rel, changeType) ? [marker] : [],
                       (targets, overflow) =>
                       {
                           if (overflow || targets.Contains(marker))
                               Interlocked.Increment(ref watchTriggers);
                       }))
            {
                watcher.Watch(watchRoot);
                Thread.Sleep(250);

                // Self-write to own shard should be suppressed by the self-write window.
                watchStore.Decide("self.item", StartupDecision.Allow, new StartupDecisionSnapshot("Service", "Self", null, "self.exe"));
                watchStore.Flush();
                Thread.Sleep(600);
                var afterSelfWrite = Volatile.Read(ref watchTriggers);

                // Remote shard arriving in StartupDecisions/ must trigger the watcher.
                var watchShardsDir = Path.Combine(watchRoot, StartupDecisionStore.ShardsDirName);
                Write(watchShardsDir, "REMOTE-PC.json", Ledger(("remote.item", StartupDecision.Deny, 5, "REMOTE-PC")));
                Thread.Sleep(700);
                var afterRemote = Volatile.Read(ref watchTriggers);

                // .tmp file inside StartupDecisions/ must be ignored.
                File.WriteAllText(Path.Combine(watchShardsDir, "REMOTE-PC.json.123.tmp"), "{}");
                Thread.Sleep(600);
                var afterTmp = Volatile.Read(ref watchTriggers);

                check("watcher suppresses own shard self-write", afterSelfWrite == 0, $"triggers={afterSelfWrite}");
                check("watcher fires on remote shard in StartupDecisions/", afterRemote >= 1, $"triggers={afterRemote}");
                check("watcher ignores .tmp files in StartupDecisions/", afterTmp == afterRemote, $"{afterTmp} == {afterRemote}");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Ignore temp cleanup failure.
            }
        }
    }

    private static void EpochScenario(CheckFn check)
    {
        var populated = Ledger(
            ("a", StartupDecision.Allow, 1, "PC-A"),
            ("b", StartupDecision.Deny, 2, "PC-A")
        );

        // "Forget everything" bumps the epoch and clears the entries.
        var reset = populated.Clone();
        reset.Epoch++;
        reset.Entries.Clear();

        // The other machine still holds the whole old generation. Without an epoch it would simply
        // sync every forgotten decision straight back.
        var merged = StartupDecisionLedger.Merge(reset, populated);
        check("a reset is not undone by a machine holding the old entries",
            merged.Entries.Count == 0 && merged.Epoch == populated.Epoch + 1,
            $"count={merged.Entries.Count} epoch={merged.Epoch}");

        var reversed = StartupDecisionLedger.Merge(populated, reset);
        check("the epoch wins whichever side it is on", reversed.Entries.Count == 0);

        // Decisions made after the reset survive the same merge.
        var afterReset = reset.Clone();
        afterReset.Entries["c"] = new StartupDecisionEntry
        {
            Decision = StartupDecision.Allow,
            Clock = 1,
            DecidedAtUtc = DateTime.UtcNow,
            Machine = "PC-A",
        };
        var withNew = StartupDecisionLedger.Merge(afterReset, populated);
        check("decisions recorded after a reset survive",
            withNew.Entries.Count == 1 && withNew.Entries.ContainsKey("c"),
            $"count={withNew.Entries.Count}");
    }

    private static void ConflictScenario(CheckFn check)
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "JeekStartupProbe_" + Guid.NewGuid().ToString("N")[..8]
        );
        var shardsDir = Path.Combine(dir, StartupDecisionStore.ShardsDirName);
        Directory.CreateDirectory(shardsDir);

        try
        {
            // Legacy conflict copies in Config/ (e.g. left over from before migrating to shards).
            Write(dir, "StartupDecisions.sync-conflict-20260101-120000-ABCDEF.json",
                Ledger(("syncthing", StartupDecision.Deny, 5, "PC-B")));
            Write(dir, "StartupDecisions-DESKTOP-B.json",
                Ledger(("onedrive", StartupDecision.Allow, 6, "PC-B")));
            Write(dir, "StartupDecisions (PC-B's conflicted copy 2026-01-01).json",
                Ledger(("dropbox", StartupDecision.Deny, 7, "PC-B")));

            // Legacy primary file.
            Write(dir, StartupDecisionStore.LegacyFileName,
                Ledger(("legacy", StartupDecision.Allow, 1, "PC-A")));

            // Conflict copy inside StartupDecisions/ (e.g. two machines sharing one hostname).
            Write(shardsDir, "PC-A.sync-conflict-20260102-120000-XYZ.json",
                Ledger(("shard-conflict", StartupDecision.Deny, 8, "PC-A")));
            Write(shardsDir, "PC-A.json",
                Ledger(("local", StartupDecision.Allow, 2, "PC-A")));

            // An unrelated file must be left alone and ignored.
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{}");

            var store = new StartupDecisionShardStore(() => dir, "PC-A", enableBackgroundTimers: false);
            store.Load();

            check("legacy conflict copies from every sync tool were merged",
                store.Find("syncthing")?.Decision == StartupDecision.Deny
                && store.Find("onedrive")?.Decision == StartupDecision.Allow
                && store.Find("dropbox")?.Decision == StartupDecision.Deny,
                string.Join(",", store.Snapshot().Keys));
            check("conflict copy inside StartupDecisions/ was merged",
                store.Find("shard-conflict")?.Decision == StartupDecision.Deny);
            check("legacy primary and local shard decisions were kept",
                store.Find("legacy")?.Decision == StartupDecision.Allow
                && store.Find("local")?.Decision == StartupDecision.Allow);
            check("read-only merge leaves unrelated and legacy files intact",
                File.Exists(Path.Combine(dir, "settings.json"))
                && File.Exists(Path.Combine(dir, StartupDecisionStore.LegacyFileName)));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // A leftover temp folder is not worth failing the probe over.
            }
        }
    }

    private static void PoisonScenario(CheckFn check)
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "JeekStartupProbe_" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(dir);

        try
        {
            var missing = Path.Combine(dir, "does-not-exist.json");
            var found = StartupDecisionStore.TryReadLedgerFile(missing, out _, out var missingError);
            check("a missing file is not an error", !found && missingError is null);

            // A sync tool can briefly expose a zero-length file while replacing it. Treating that
            // as an empty ledger and writing it back would wipe every decision.
            var empty = Path.Combine(dir, "empty.json");
            File.WriteAllText(empty, "");
            check("an empty file reports an error rather than an empty ledger",
                !StartupDecisionStore.TryReadLedgerFile(empty, out _, out var emptyError)
                && emptyError is not null,
                emptyError ?? "");

            var garbage = Path.Combine(dir, "garbage.json");
            File.WriteAllText(garbage, "{ this is not json");
            check("unparsable content reports an error",
                !StartupDecisionStore.TryReadLedgerFile(garbage, out _, out var garbageError)
                && garbageError is not null);

            var good = Path.Combine(dir, "good.json");
            Write(dir, "good.json", Ledger(("ok", StartupDecision.Allow, 1, "PC-A")));
            check("a good file still reads",
                StartupDecisionStore.TryReadLedgerFile(good, out var ledger, out _)
                && ledger!.Entries.ContainsKey("ok"));

            // A file from a newer build is readable; the store refuses to rewrite it elsewhere.
            var future = Ledger(("ok", StartupDecision.Allow, 1, "PC-A"));
            future.Version = StartupDecisionLedger.CurrentVersion + 1;
            Write(dir, "future.json", future);
            check("a newer file version is readable and reports its version",
                StartupDecisionStore.TryReadLedgerFile(Path.Combine(dir, "future.json"), out var futureLedger, out _)
                && futureLedger!.Version > StartupDecisionLedger.CurrentVersion);

            // Store-level poison checks:
            // (a) Corrupt own shard blocks writes.
            var ownBadDir = Path.Combine(dir, "own-bad");
            var ownBadShards = Path.Combine(ownBadDir, StartupDecisionStore.ShardsDirName);
            Directory.CreateDirectory(ownBadShards);
            File.WriteAllText(Path.Combine(ownBadShards, "PC-A.json"), "{ broken");
            var ownBadStore = new StartupDecisionShardStore(() => ownBadDir, "PC-A", enableBackgroundTimers: false);
            ownBadStore.Load();
            check("corrupt own shard poisons store and refuses writes",
                ownBadStore.IsPoisoned
                && !ownBadStore.Decide("x", StartupDecision.Allow, new StartupDecisionSnapshot("Service", "X", null, "x.exe")));

            // (b) Future version in any remote shard blocks writes.
            var futureDir = Path.Combine(dir, "future-shard");
            var futureShards = Path.Combine(futureDir, StartupDecisionStore.ShardsDirName);
            Directory.CreateDirectory(futureShards);
            Write(futureShards, "PC-B.json", future);
            var futureStore = new StartupDecisionShardStore(() => futureDir, "PC-A", enableBackgroundTimers: false);
            futureStore.Load();
            check("future version in a remote shard poisons writes while still reading entries",
                futureStore.IsPoisoned && futureStore.Find("ok")?.Decision == StartupDecision.Allow);

            // (c) Corrupt remote shard does not brick own shard writes; healthy shards still merge.
            var remoteBadDir = Path.Combine(dir, "remote-bad");
            var remoteBadShards = Path.Combine(remoteBadDir, StartupDecisionStore.ShardsDirName);
            Directory.CreateDirectory(remoteBadShards);
            Write(remoteBadShards, "PC-A.json", Ledger(("mine", StartupDecision.Allow, 1, "PC-A")));
            File.WriteAllText(Path.Combine(remoteBadShards, "PC-B.json"), "{ half synced");
            var remoteBadStore = new StartupDecisionShardStore(() => remoteBadDir, "PC-A", enableBackgroundTimers: false);
            remoteBadStore.Load();
            check("corrupt remote shard does not poison own shard",
                !remoteBadStore.IsPoisoned
                && remoteBadStore.Find("mine")?.Decision == StartupDecision.Allow
                && remoteBadStore.Decide("new.local", StartupDecision.Deny, new StartupDecisionSnapshot("Service", "New", null, "n.exe")));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Ignore.
            }
        }
    }

    private static void IdentityScenario(CheckFn check)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";

        // A path under the user profile must not carry the user name into the key, or nothing would
        // match on a second computer.
        var underProfile = StartupIdentity.NormalizePath(Path.Combine(localAppData, "Vendor", "app.exe"));
        check("user folders become machine-independent tokens",
            underProfile.StartsWith("%localappdata%", StringComparison.Ordinal)
            && !underProfile.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase),
            underProfile);

        var underWindows = StartupIdentity.NormalizePath(Path.Combine(systemRoot, "system32", "svchost.exe"));
        check("the Windows folder becomes a token",
            underWindows.StartsWith("%systemroot%", StringComparison.Ordinal), underWindows);

        // An update that only changes a version folder must not read as a brand new entry.
        var v1 = StartupIdentity.NormalizePath(@"C:\Program Files\App\1.2.3\app.exe");
        var v2 = StartupIdentity.NormalizePath(@"C:\Program Files\App\1.2.4\app.exe");
        check("a version folder does not change the identity", v1 == v2, v1);

        var g1 = StartupIdentity.NormalizePath(@"C:\ProgramData\{3F2504E0-4F89-11D3-9A0C-0305E82C3301}\app.exe");
        var g2 = StartupIdentity.NormalizePath(@"C:\ProgramData\{A1B2C3D4-1111-2222-3333-444455556666}\app.exe");
        check("a GUID folder does not change the identity", g1 == g2, g1);

        // The file name itself must still matter, or two different programs would share a decision.
        var other = StartupIdentity.NormalizePath(@"C:\Program Files\App\1.2.4\other.exe");
        check("the file name still distinguishes two programs", v2 != other);

        // Service ImagePath values arrive in native or relative form.
        var native = StartupIdentity.NormalizePath(@"\??\C:\Windows\system32\drivers\x.sys");
        var relative = StartupIdentity.NormalizePath(@"system32\drivers\x.sys");
        check("native and relative driver paths normalize alike", native == relative, native);

        // Quoted and unquoted command lines must give the same program.
        var quoted = StartupIdentity.ExtractImagePath("\"C:\\Program Files\\App\\app.exe\" --background");
        check("a quoted command line yields the executable",
            quoted.EndsWith(@"App\app.exe", StringComparison.OrdinalIgnoreCase), quoted);

        // The entry should be attributed to the DLL, not to the launcher.
        var viaRundll = StartupIdentity.ExtractImagePath(@"rundll32.exe C:\Vendor\payload.dll,EntryPoint");
        check("rundll32 is resolved to the DLL it loads",
            viaRundll.EndsWith("payload.dll", StringComparison.OrdinalIgnoreCase), viaRundll);

        // A different publisher on the same file name must not inherit a decision.
        var vendorKey = StartupIdentity.ComputeLooseKey(
            StartupItemKind.LogonRegistry, "Updater", @"C:\Vendor\updater.exe", "Vendor Ltd");
        var impostorKey = StartupIdentity.ComputeLooseKey(
            StartupItemKind.LogonRegistry, "Updater", @"C:\Elsewhere\updater.exe", "Someone Else");
        check("the loose key separates publishers", vendorKey != impostorKey, vendorKey);

        var sameVendorElsewhere = StartupIdentity.ComputeLooseKey(
            StartupItemKind.LogonRegistry, "Updater", @"D:\Apps\Vendor\updater.exe", "Vendor Ltd");
        check("the loose key matches the same program installed elsewhere",
            vendorKey == sameVendorElsewhere, sameVendorElsewhere);

        // A launcher must not swallow the identity of the program it starts. Without this, every
        // cmd-launched entry would share one key and one loose key.
        var viaCmd = StartupIdentity.ExtractImagePath("cmd.exe /C start /B FanControl.exe");
        check("cmd is resolved to the program it starts",
            viaCmd.EndsWith("FanControl.exe", StringComparison.OrdinalIgnoreCase), viaCmd);

        // An unquoted path with spaces and no arguments must survive whole.
        var unquoted = StartupIdentity.ExtractImagePath(
            Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows",
                "System32", "cmd.exe"));
        check("an unquoted path is not cut at a space",
            unquoted.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase), unquoted);

        var withSpaces = StartupIdentity.ExtractImagePath(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\Does Not Exist\x.bat");
        check("an unresolvable unquoted path keeps its first segment only when it must",
            withSpaces.Length > 0, withSpaces);

        // Windows' own tasks put switches before the DLL; taking one as the target produced a
        // nonsense path that then became part of a permanent key.
        var rundllSwitched = StartupIdentity.ExtractImagePath(
            @"""%windir%\system32\rundll32.exe"" /d acproxy.dll,PerformAutochkOperations");
        check("rundll32 switches are skipped before the DLL",
            rundllSwitched.EndsWith("acproxy.dll", StringComparison.OrdinalIgnoreCase), rundllSwitched);

        var cmdA = StartupIdentity.ComputeLooseKey(
            StartupItemKind.ScheduledTask, "Task", StartupIdentity.ExtractImagePath("cmd.exe /c AppOne.exe"), null);
        var cmdB = StartupIdentity.ComputeLooseKey(
            StartupItemKind.ScheduledTask, "Task", StartupIdentity.ExtractImagePath("cmd.exe /c AppTwo.exe"), null);
        check("two cmd-launched entries do not share a key", cmdA != cmdB, cmdA + " vs " + cmdB);

        // A bare executable name is looked up the way the shell would, not assumed to sit directly
        // in the Windows folder, which invented a path that never exists.
        var bare = StartupIdentity.ExpandPath("cmd.exe");
        check("a bare executable name resolves to a real file", File.Exists(bare), bare);

        // A driver path really is relative to the Windows folder, and must stay that way.
        var driverRelative = StartupIdentity.ExpandPath(@"system32\drivers\x.sys");
        check("a relative driver path stays relative to the Windows folder",
            driverRelative.EndsWith(@"system32\drivers\x.sys", StringComparison.OrdinalIgnoreCase),
            driverRelative);

        // Two registrations of one executable are different entries. gupdate and gupdatem both run
        // GoogleUpdate.exe from the same publisher; a decision about one must not reach the other.
        var gupdate = StartupIdentity.ComputeLooseKey(
            StartupItemKind.Service, "gupdate", @"C:\Program Files (x86)\Google\Update\GoogleUpdate.exe", "Google LLC");
        var gupdatem = StartupIdentity.ComputeLooseKey(
            StartupItemKind.Service, "gupdatem", @"C:\Program Files (x86)\Google\Update\GoogleUpdate.exe", "Google LLC");
        check("sibling registrations of one executable do not share a loose key", gupdate != gupdatem);

        // A decision whose exact key is present here belongs to that item and is not lent out.
        var exactHere = "Service|hklm\\x|updater|%programfiles%\\vendor\\updater.exe";
        var fromElsewhere = "Service|hklm\\x|updater|d:\\apps\\vendor\\updater.exe";
        var decisions = new Dictionary<string, StartupDecisionEntry>(StringComparer.Ordinal)
        {
            [exactHere] = new() { Decision = StartupDecision.Deny, Publisher = "Vendor", EntryName = "updater",
                ImagePath = @"C:\Program Files\Vendor\updater.exe" },
        };
        var lent = StartupItemManager.BuildLooseIndex(decisions, [exactHere]);
        check("a decision for an item present here is not used as a loose match", lent.Count == 0,
            $"count={lent.Count}");
        var notLent = StartupItemManager.BuildLooseIndex(decisions, [fromElsewhere]);
        check("a decision for an item absent here is available as a loose match", notLent.Count == 1,
            $"count={notLent.Count}");

        // A new binary taking over an existing Run value must be confirmed again.
        var original = StartupIdentity.ComputeKey(
            StartupItemKind.LogonRegistry, @"HKCU\...\Run", "Updater", @"C:\Vendor\updater.exe");
        var hijacked = StartupIdentity.ComputeKey(
            StartupItemKind.LogonRegistry, @"HKCU\...\Run", "Updater", @"C:\Temp\evil.exe");
        check("a different binary under the same value name is a different key",
            original != hijacked);
    }

    /// <summary>
    ///     The browser add-on switch, exercised against a class id that exists only for this test.
    ///     <para>
    ///         The bit preservation is the point. Real add-ons carry other flags (0x400 is common),
    ///         and writing the whole value instead of setting one bit would silently discard them.
    ///     </para>
    /// </summary>
    private static void BrowserAddOnScenario(CheckFn check)
    {
        const string settingsPath = @"Software\Microsoft\Windows\CurrentVersion\Ext\Settings";
        var clsid = Guid.NewGuid().ToString("B").ToUpperInvariant();
        var keyPath = $@"{settingsPath}\{clsid}";

        try
        {
            // An add-on carrying an unrelated flag, exactly like a real one.
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true))
                key!.SetValue("Flags", 1024, RegistryValueKind.DWord);

            check("an add-on with unrelated flags reads as enabled",
                BrowserAddOnRegistry.IsEnabled(clsid));

            check("disabling an add-on succeeds",
                BrowserAddOnRegistry.SetEnabled(clsid, false, out var disableError), disableError ?? "");
            check("a disabled add-on reads as disabled", !BrowserAddOnRegistry.IsEnabled(clsid));
            check("disabling preserves the other flag bits",
                ReadFlags(keyPath) == 1025, ReadFlags(keyPath).ToString());

            check("enabling an add-on succeeds",
                BrowserAddOnRegistry.SetEnabled(clsid, true, out var enableError), enableError ?? "");
            check("an enabled add-on reads as enabled", BrowserAddOnRegistry.IsEnabled(clsid));
            check("enabling restores the original flags exactly",
                ReadFlags(keyPath) == 1024, ReadFlags(keyPath).ToString());

            // An add-on nobody ever switched off has no key at all and must count as enabled.
            var unknown = Guid.NewGuid().ToString("B").ToUpperInvariant();
            check("an add-on with no settings key counts as enabled",
                BrowserAddOnRegistry.IsEnabled(unknown));

            check("an empty class id list is refused",
                !BrowserAddOnRegistry.SetEnabled("", false, out _));
        }
        finally
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            }
            catch
            {
                // A leftover scratch key is not worth failing the probe over.
            }
        }
    }

    private static int ReadFlags(string keyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue("Flags") as int? ?? -1;
    }

    // ---------- Helpers ----------

    private static StartupDecisionLedger Ledger(
        params (string Key, StartupDecision Decision, long Clock, string Machine)[] entries
    )
    {
        var ledger = new StartupDecisionLedger();
        foreach (var (key, decision, clock, machine) in entries)
        {
            ledger.Entries[key] = new StartupDecisionEntry
            {
                Decision = decision,
                Clock = clock,
                DecidedAtUtc = clock > 1_000_000_000_000L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(clock).UtcDateTime
                    : new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(clock),
                Machine = machine,
                Command = key + ".exe",
            };
            if (clock > ledger.Clock)
                ledger.Clock = clock;
        }

        return ledger;
    }

    private static void Write(string dir, string name, StartupDecisionLedger ledger) =>
        File.WriteAllText(Path.Combine(dir, name), Serialize(ledger));

    private static string Serialize(StartupDecisionLedger ledger) =>
        JsonSerializer.Serialize(ledger, JsonOptions);
}
