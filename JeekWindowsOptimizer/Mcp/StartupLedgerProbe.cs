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
                EpochScenario(Check);
                ConflictScenario(Check);
                PoisonScenario(Check);
                IdentityScenario(Check);
                BrowserAddOnScenario(Check);
                break;
            default:
                return Task.FromResult(
                    $"Unknown scenario '{scenario}'. Use merge | epoch | conflict | poison | identity | addon | all."
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
        Directory.CreateDirectory(dir);

        try
        {
            // The names the common sync tools actually produce.
            Write(dir, "StartupDecisions.sync-conflict-20260101-120000-ABCDEF.json",
                Ledger(("syncthing", StartupDecision.Deny, 5, "PC-B")));
            Write(dir, "StartupDecisions-DESKTOP-B.json",
                Ledger(("onedrive", StartupDecision.Allow, 6, "PC-B")));
            Write(dir, "StartupDecisions (PC-B's conflicted copy 2026-01-01).json",
                Ledger(("dropbox", StartupDecision.Deny, 7, "PC-B")));

            // The main file must never be treated as its own conflict copy.
            Write(dir, StartupDecisionStore.FileName,
                Ledger(("main", StartupDecision.Allow, 1, "PC-A")));

            // An unrelated file must be left alone.
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{}");

            var local = Ledger(("local", StartupDecision.Allow, 2, "PC-A"));
            var absorbed = StartupDecisionStore.AbsorbFrom(dir, local, deleteAbsorbed: true, out var merged);

            check("conflict copies were absorbed", absorbed);
            check("every sync tool's naming was recognised",
                merged.Entries.ContainsKey("syncthing")
                && merged.Entries.ContainsKey("onedrive")
                && merged.Entries.ContainsKey("dropbox"),
                string.Join(",", merged.Entries.Keys));
            check("the local decision was kept", merged.Entries.ContainsKey("local"));
            check("absorbed copies were deleted",
                Directory.GetFiles(dir, "StartupDecisions*.json").Length == 1);
            check("the main decision file was not consumed",
                File.Exists(Path.Combine(dir, StartupDecisionStore.FileName)));
            check("unrelated files were left alone",
                File.Exists(Path.Combine(dir, "settings.json")));
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
                DecidedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(clock),
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
