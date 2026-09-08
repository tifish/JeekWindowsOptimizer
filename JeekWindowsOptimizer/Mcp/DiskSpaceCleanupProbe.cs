namespace JeekWindowsOptimizer.Mcp;

/// <summary>Deterministic checks driven through this worktree's Debug MCP.</summary>
internal static class DiskSpaceCleanupProbe
{
    public static async Task<string> RunAsync(string scenario)
    {
        if (scenario == "selection") return await SelectionAsync();
        if (scenario == "pnpm") return await PnpmStoreProbe.RunAsync();
        if (scenario == "developer") return await DeveloperCacheProbe.RunAsync();
        if (scenario == "lcu") return await LcuAsync();
        if (scenario == "installer_baseline") return await InstallerBaselineAsync();
        if (scenario == "graphics") return await FixedDirectoryAsync();
        if (scenario == "drivers")
        {
            var packages = DriverStoreCleanup.Parse("Published Name: oem1.inf\nOriginal Name: gpu.inf\nProvider Name: Vendor\nClass Name: Display\nClass GUID: {4d36e968-e325-11ce-bfc1-08002be10318}\nClass Version: 2.0\nDriver Version: 01/01/2025 1.2.0.0\n\n发布名称: oem2.inf\n原始名称: gpu.inf\n提供商名称: Vendor\n类名: Display\n类 GUID: {4d36e968-e325-11ce-bfc1-08002be10318}\n驱动程序版本: 01/01/2026 1.10.0.0");
            Require(packages.Count == 2, "English and Chinese parsing");
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Require(DriverStoreCleanup.SelectOld(packages, used).Single().Published == "oem1.inf", "numeric version order");
            used.Add("OEM1.INF");
            Require(DriverStoreCleanup.SelectOld(packages, used).Count == 0, "in-use protected");
            used.Clear();
            Require(DriverStoreCleanup.SelectOld([packages[0], packages[1] with { Provider = "Other" }], used).Count == 0, "provider isolation");
            Require(DriverStoreCleanup.SelectOld([packages[0], packages[1] with { Architecture = "arm64" }], used).Count == 0, "architecture isolation");
            Require(DriverStoreCleanup.SelectOld([packages[0], packages[1] with { Version = packages[0].Version }], used).Count == 0, "equal versions retained");
            Require(DriverStoreCleanup.DeleteArguments("oem1.inf") == "/delete-driver oem1.inf", "no force or uninstall");
            Require(!new OldDriversCleanupItem().IsChecked, "driver rollback opt-in");
            return "PASS drivers: localized records, numeric versions, in-use/provider/architecture guards, equal versions, no force, opt-in";
        }
        if (scenario == "hibernation")
        {
            var mode = "Full";
            var calls = new List<string>();
            var fail = false;
            var hibernation = new HibernationDiskSpaceItem(() => (mode == "Off" ? 0 : mode == "Full" ? 1000 : 400, mode), (command, _) =>
            {
                calls.Add(command);
                if (fail && command == "/h /type reduced") throw new IOException("Injected powercfg failure");
                if (command == "/h off") mode = "Off";
                if (command == "/h on" || command == "/h /type full") mode = "Full";
                if (command == "/h /type reduced") mode = "Reduced";
                return Task.FromResult("");
            });
            Require((await hibernation.SetModeAsync("Reduced")).FreedBytes == 600 && hibernation.Mode == "Reduced", "reduce measurement");
            Require(calls.SequenceEqual(HibernationDiskSpaceItem.Commands("Reduced")), "reset custom size before reducing");
            Require((await hibernation.SetModeAsync("Off")).Succeeded && hibernation.SizeBytes == 0, "off");
            Require((await hibernation.SetModeAsync("Full")).Succeeded && hibernation.SizeBytes == 1000, "restore from off");
            fail = true;
            Require(!(await hibernation.SetModeAsync("Reduced")).Succeeded && hibernation.Mode == "Full" && hibernation.State == DiskSpaceItemState.Failed, "partial failure reconciled");
            return "PASS hibernation: reduce/off/restore, measured sizes, custom size reset, partial failure";
        }
        if (scenario == "shadows")
        {
            Require(ShadowCopyStorage.ParseUsedBytes("Used space: 2 GB (3%)") == 2147483648L, "native size parser");
            var now = DateTime.Now;
            var newest = new ShadowCopyStorage.Snapshot("new", now);
            var old = new ShadowCopyStorage.Snapshot("old", now.AddDays(-1));
            Require(ShadowCopyStorage.KeepNewest([old, newest]).SequenceEqual([old]), "keep latest");
            Require(ShadowCopyStorage.KeepNewest([newest]).Count == 0 && ShadowCopyStorage.KeepNewest([]).Count == 0, "zero/one snapshot");
            Require(ShadowCopyStorage.KeepNewest([newest, old with { Created = now }]).Count == 1, "tied dates retain one");
            Require(!new ShadowCopiesCleanupItem().IsChecked, "restore history opt-in");
            return "PASS shadows: newest retained, zero/one snapshot, tied dates, opt-in";
        }
        if (scenario == "browser")
            return await BrowserAsync();
        if (scenario == "nuget")
            return await NuGetAsync();
        if (scenario == "user_dumps")
            return await UserDumpsAsync();
        if (scenario != "accuracy")
            throw new ArgumentException("Unknown scenario: " + scenario);

        var item = new MeasurementItem();
        await item.RefreshAsync(); // Old UI estimate is deliberately different.
        Require(await item.CleanAsync() == 60 && item.IsFreedBytesKnown, "fresh measurement");
        item.FailRescan = true;
        Require(await item.CleanAsync() == 0 && !item.IsFreedBytesKnown
            && item.SizeBytes is null && item.State == DiskSpaceItemState.Failed
            && item.FreedBytes == 0, "rescan failure and stale result reset");
        item.FailRescan = false;
        item.CancelClean = true;
        Require(await item.CleanAsync() == 0 && !item.IsFreedBytesKnown
            && item.SizeBytes is null && item.State == DiskSpaceItemState.Failed, "cancellation");
        item.CancelClean = false;
        item.FailClean = true;
        Require(await item.CleanAsync() == 0 && !item.IsFreedBytesKnown
            && item.State == DiskSpaceItemState.Failed, "cleanup exception");
        item.FailClean = false;
        Require(await item.CleanAsync() == 60 && item.State == DiskSpaceItemState.Done,
            "successful retry");
        return "PASS accuracy: fresh measurement, rescan failure, stale result reset, cancellation, cleanup exception, retry";
    }

    internal static void Require(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException("FAIL: " + name);
    }

    private static async Task<string> BrowserAsync()
    {
        var root = Path.Join(Path.GetTempPath(), "JeekCleanupProbe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var data = Path.Join(root, "User Data");
            string Write(string relative)
            {
                var path = Path.Join(data, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, new byte[100]);
                return path;
            }
            Write(@"Default\Cache\Cache_Data\entry");
            Write(@"Profile 1\Code Cache\js\entry");
            var locked = Write(@"Default\Cache\locked");
            var keep = new[] { Write(@"Default\Network\Cookies"), Write(@"Default\Login Data"),
                Write(@"Default\History"), Write(@"Default\Local Storage\data"),
                Write(@"Unrecognized\Cache\data") };
            var running = true;
            var item = new BrowserCacheCleanupItem("Edge", data, () => running);
            await item.RefreshAsync();
            Require(item.SizeBytes == 300, "multiple profiles and exact cache allowlist");
            await item.CleanAsync();
            Require(item.State == DiskSpaceItemState.Failed && File.Exists(locked), "running browser blocked");
            running = false;
            using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await item.CleanAsync();
                Require(item.State == DiskSpaceItemState.Failed && item.SizeBytes == 100,
                    "locked cache reports incomplete");
            }
            await item.CleanAsync();
            Require(item.State == DiskSpaceItemState.Done && item.SizeBytes == 0
                && keep.All(File.Exists), "retry and preserve profile data");
            var outside = Path.Join(root, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllBytes(Path.Join(outside, "keep"), new byte[50]);
            Directory.CreateSymbolicLink(Path.Join(data, "Profile 2"), outside);
            Directory.CreateDirectory(Path.Join(data, "Profile 3"));
            Directory.CreateSymbolicLink(Path.Join(data, "Profile 3", "Cache"), outside);
            await item.RefreshAsync();
            await item.CleanAsync();
            Require(item.SizeBytes == 0 && File.Exists(Path.Join(outside, "keep")), "linked profiles and caches skipped");
            return "PASS browser: multiple profiles, cache allowlist, running guard, locked file, retry, preserved data, reparse points";
        }
        finally { FileSystemCleaner.DeleteDirectory(root); }
    }

    private static async Task<string> NuGetAsync()
    {
        var root = Path.Join(Path.GetTempPath(), "JeekCleanupProbe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var http = Path.Join(root, "http cache");
            var packages = Path.Join(root, "packages");
            Directory.CreateDirectory(http);
            Directory.CreateDirectory(packages);
            var httpFile = Path.Join(http, "sample.dat");
            var packageFile = Path.Join(packages, "sample.nupkg");
            File.WriteAllBytes(httpFile, new byte[100]);
            File.WriteAllBytes(packageFile, new byte[200]);
            var cache = new NuGetCache(new Dictionary<string, string>
            {
                ["NUGET_HTTP_CACHE_PATH"] = http,
                ["NUGET_PACKAGES"] = packages,
                ["NUGET_SCRATCH"] = Path.Join(root, "scratch"),
            }, root);
            var httpItem = new NuGetCacheCleanupItem(false, cache);
            var packagesItem = new NuGetCacheCleanupItem(true, cache);
            Require(!httpItem.IsChecked && !packagesItem.IsChecked, "developer caches opt-in");
            await httpItem.RefreshAsync();
            await packagesItem.RefreshAsync();
            Require(httpItem.SizeBytes == 100 && packagesItem.SizeBytes == 200, "CLI resolves overridden paths with spaces");
            await httpItem.CleanAsync();
            Require(httpItem.State == DiskSpaceItemState.Done && httpItem.FreedBytes == 100
                && !File.Exists(httpFile) && File.Exists(packageFile), "HTTP cleanup leaves packages");
            using (File.Open(packageFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await packagesItem.CleanAsync();
                Require(packagesItem.State == DiskSpaceItemState.Failed && !packagesItem.IsFreedBytesKnown
                    && File.Exists(packageFile), "CLI nonzero exit with locked package");
            }
            await packagesItem.CleanAsync();
            Require(packagesItem.State == DiskSpaceItemState.Done && !File.Exists(packageFile), "package retry");
            Directory.CreateDirectory(http);
            Directory.CreateDirectory(packages);
            Directory.CreateSymbolicLink(Path.Join(http, "link"), packages);
            var rejectedLink = false;
            try { NuGetCache.ValidatePath(http); }
            catch (IOException) { rejectedLink = true; }
            Require(rejectedLink, "linked cache rejected before external deletion");
            var rejectedRoot = false;
            try { NuGetCache.ValidatePath(Path.GetPathRoot(root)!); }
            catch (IOException) { rejectedRoot = true; }
            Require(rejectedRoot, "drive root rejected");
            return "PASS nuget: real CLI, overridden paths, separate caches, package opt-in, locked file error, retry, links and root guard";
        }
        finally { FileSystemCleaner.DeleteDirectory(root); }
    }

    private static async Task<string> UserDumpsAsync()
    {
        var root = Path.Join(Path.GetTempPath(), "JeekCleanupProbe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var dumps = Path.Join(root, "CrashDumps");
            Directory.CreateDirectory(dumps);
            var dump = Path.Join(dumps, "app.DMP");
            var locked = Path.Join(dumps, "locked.dmp");
            var keep = Path.Join(dumps, "notes.txt");
            var nested = Path.Join(dumps, "nested");
            Directory.CreateDirectory(nested);
            File.WriteAllBytes(dump, new byte[100]);
            File.SetAttributes(dump, FileAttributes.ReadOnly);
            File.WriteAllBytes(locked, new byte[200]);
            File.WriteAllBytes(keep, new byte[500]);
            File.WriteAllBytes(Path.Join(nested, "keep.dmp"), new byte[500]);
            var item = new UserCrashDumpsCleanupItem(dumps);
            Require(!item.IsChecked, "diagnostic data opt-in");
            await item.RefreshAsync();
            Require(item.SizeBytes == 300, "only top-level dump files");
            using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await item.CleanAsync();
                Require(item.State == DiskSpaceItemState.Failed && item.SizeBytes == 200
                    && !File.Exists(dump), "read-only removed and locked dump retained");
            }
            await item.CleanAsync();
            Require(item.State == DiskSpaceItemState.Done && item.SizeBytes == 0
                && File.Exists(keep) && File.Exists(Path.Join(nested, "keep.dmp")), "retry and preserve other files");
            var linked = Path.Join(root, "LinkedDumps");
            Directory.CreateSymbolicLink(linked, nested);
            var linkedItem = new UserCrashDumpsCleanupItem(linked);
            await linkedItem.CleanAsync();
            Require(linkedItem.SizeBytes == 0 && File.Exists(Path.Join(nested, "keep.dmp")), "linked dump directory skipped");
            var absentItem = new UserCrashDumpsCleanupItem(Path.Join(root, "missing"));
            await absentItem.CleanAsync();
            Require(absentItem.State == DiskSpaceItemState.Done && absentItem.SizeBytes == 0, "missing directory");
            return "PASS user_dumps: exact extension, top-level only, diagnostic opt-in, read-only and locked files, retry, links, missing directory";
        }
        finally { FileSystemCleaner.DeleteDirectory(root); }
    }

    private static async Task<string> LcuAsync()
    {
        var root = Path.Join(Path.GetTempPath(), "JeekLcuProbe-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Join(root, "servicing", "LCU");
        Directory.CreateDirectory(cache);
        try
        {
            var file = Path.Join(cache, "package");
            var keep = Path.Join(root, "servicing", "keep");
            File.WriteAllBytes(file, new byte[321]);
            File.WriteAllBytes(keep, new byte[100]);
            var pending = true;
            var busy = false;
            var boot = DateTime.UtcNow.AddHours(1);
            var lcu = new LcuCleanupItem(root, () => pending, () => busy, () => boot);
            await lcu.RefreshAsync();
            Require(lcu.SizeBytes == 321 && lcu.NeedsReboot && lcu.ReclaimableBytes == 0 && !lcu.IsChecked, "pending restart visible, not reclaimable");
            await lcu.CleanAsync();
            Require(lcu.State == DiskSpaceItemState.Failed && File.Exists(file), "pending guard at execution");
            pending = false;
            busy = true;
            await lcu.RefreshAsync();
            Require(!lcu.CanClean && lcu.ServicingBusy, "servicing blocked");
            await lcu.CleanAsync();
            Require(File.Exists(file) && lcu.State == DiskSpaceItemState.Failed, "servicing execution guard");
            busy = false;
            boot = DateTime.UtcNow.AddHours(-1);
            await lcu.RefreshAsync();
            Require(lcu.NeedsReboot, "new staging requires restart");
            boot = DateTime.UtcNow.AddHours(1);
            await lcu.RefreshAsync();
            Require(lcu.CanClean && lcu.ReclaimableBytes == 321, "post-restart eligible");
            pending = true; // State changed after scan.
            await lcu.CleanAsync();
            Require(File.Exists(file) && lcu.State == DiskSpaceItemState.Failed, "stale scan revalidated");
            pending = false;
            await lcu.CleanAsync();
            Require(lcu.State == DiskSpaceItemState.Done && lcu.FreedBytes == 321 && File.Exists(keep), "post-restart cleanup limited to LCU");
            return "PASS lcu: pending reboot, servicing activity, current-boot staging, stale scan guard, successful retry, sibling preserved";
        }
        finally { FileSystemCleaner.DeleteDirectory(root); }
    }

    private static async Task<string> InstallerBaselineAsync()
    {
        var root = Path.Join(Path.GetTempPath(), "JeekInstallerProbe-" + Guid.NewGuid().ToString("N"));
        var installer = Path.Join(root, "Installer");
        var cache = Path.Join(installer, "$PatchCache$");
        Directory.CreateDirectory(cache);
        try
        {
            var baseline = Path.Join(cache, "baseline");
            var msi = Path.Join(installer, "keep.msi");
            var msp = Path.Join(installer, "keep.msp");
            File.WriteAllBytes(baseline, new byte[123]);
            File.WriteAllBytes(msi, new byte[50]);
            File.WriteAllBytes(msp, new byte[60]);
            var busy = true;
            var cacheItem = new InstallerBaselineCleanupItem(root, () => busy);
            await cacheItem.RefreshAsync();
            Require(cacheItem.SizeBytes == 123 && !cacheItem.IsChecked, "exact baseline size and opt-in");
            await cacheItem.CleanAsync();
            Require(cacheItem.State == DiskSpaceItemState.Failed && File.Exists(baseline), "installer activity guard");
            busy = false;
            await cacheItem.CleanAsync();
            Require(cacheItem.State == DiskSpaceItemState.Done && cacheItem.FreedBytes == 123
                && File.Exists(msi) && File.Exists(msp), "only baseline removed; MSI/MSP preserved");
            Directory.Delete(cache);
            Directory.CreateSymbolicLink(cache, installer);
            await cacheItem.CleanAsync();
            Require(cacheItem.State == DiskSpaceItemState.Failed && File.Exists(msi), "redirected cache blocked");
            return "PASS installer_baseline: exact directory, opt-in, active installer guard, MSI/MSP preserved, link blocked";
        }
        finally { FileSystemCleaner.DeleteDirectory(root); }
    }

    private static async Task<string> FixedDirectoryAsync()
    {
        var root = Path.Join(Path.GetTempPath(), "JeekGraphicsProbe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var kind in new[] { "NvidiaDownloader", "NvidiaInstaller", "NvidiaRoot", "AmdRoot" })
            {
                var path = Path.Join(root, kind);
                Directory.CreateDirectory(path);
                var locked = Path.Join(path, "locked");
                var keep = Path.Join(root, "keep");
                File.WriteAllBytes(locked, new byte[100]);
                File.WriteAllBytes(keep, new byte[200]);
                var cache = new GraphicsInstallerCleanupItem(kind, path);
                await cache.RefreshAsync();
                Require(cache.SizeBytes == 100 && !cache.IsChecked, "size and opt-in");
                using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    await cache.CleanAsync();
                    Require(cache.State == DiskSpaceItemState.Failed && File.Exists(locked), "locked file incomplete");
                }
                await cache.CleanAsync();
                Require(cache.State == DiskSpaceItemState.Done && cache.FreedBytes == 100 && File.Exists(keep), "allowlist cleanup and retry");
                var outside = Path.Join(root, "outside");
                Directory.CreateDirectory(outside);
                File.WriteAllBytes(Path.Join(outside, "keep"), new byte[50]);
                Directory.CreateSymbolicLink(Path.Join(path, "link"), outside);
                await cache.CleanAsync();
                Require(File.Exists(Path.Join(outside, "keep")), "nested link preserved");
            }
            return "PASS graphics: all four paths, opt-in, measured size, locked files, retry, siblings and link targets preserved";
        }
        finally { FileSystemCleaner.DeleteDirectory(root); }
    }

    private static async Task<string> SelectionAsync()
    {
        var defaults = new SelectionItem();
        Require(!defaults.IsChecked, "default opt-in");
        await defaults.RefreshAsync();
        Require(defaults.IsChecked, "first scan default still works");
        var settings = new RoamingSettings
        {
            DiskSpaceCleanupSelections = new() { ["savedOff"] = false, ["savedOn"] = true }
        };
        var restored = System.Text.Json.JsonSerializer.Deserialize<RoamingSettings>(
            System.Text.Json.JsonSerializer.Serialize(settings))!;
        foreach (var choice in restored.DiskSpaceCleanupSelections!.Values)
        {
            var item = new SelectionItem();
            item.RestoreCheckedState(choice);
            await item.RefreshAsync();
            await item.RefreshAsync();
            Require(item.IsChecked == choice, "saved choice survives first scan and rescan");
        }
        var manual = new SelectionItem();
        manual.ToggleChecked();
        manual.ToggleChecked();
        await manual.RefreshAsync();
        Require(!manual.IsChecked, "manual choice before scan preserved");
        Require(System.Text.Json.JsonSerializer.Deserialize<RoamingSettings>("{}")!
            .DiskSpaceCleanupSelections is null, "old settings retain defaults");
        Require(DiskSpaceItemManager.CreateItems().OfType<DiskSpaceCleanupItem>()
            .Where(item => item.GroupNameKey == "DiskSpaceDeveloperCleanup").All(item => !item.IsChecked),
            "new developer items remain opt-in");
        return "PASS selection: settings roundtrip, defaults, restored checked/unchecked, first scan, rescan, manual choice";
    }

    private sealed class SelectionItem : DiskSpaceCleanupItem
    {
        public override string NameKey => "SelectionProbe";
        public override string DescriptionKey => "SelectionProbe";
        protected override bool DefaultChecked => false;
        protected override bool? AutoCheckAfterScan => true;
        protected override Task<long> ScanCore(CancellationToken cancellationToken) => Task.FromResult(0L);
        protected override Task<bool> CleanCore(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MeasurementItem : DiskSpaceCleanupItem
    {
        public override string NameKey => "TempFilesCleanupName";
        public override string DescriptionKey => "TempFilesCleanupDescription";
        public bool FailRescan { get; set; }
        public bool CancelClean { get; set; }
        public bool FailClean { get; set; }
        private bool _afterClean;

        protected override Task<long> ScanCore(CancellationToken cancellationToken)
        {
            if (State == DiskSpaceItemState.Scanning)
                return Task.FromResult(1000L);
            if (_afterClean)
            {
                _afterClean = false;
                if (FailRescan)
                    throw new IOException("Injected rescan failure");
                return Task.FromResult(40L);
            }
            return Task.FromResult(100L);
        }

        protected override Task<bool> CleanCore(CancellationToken cancellationToken)
        {
            if (CancelClean)
                throw new OperationCanceledException();
            if (FailClean)
                throw new IOException("Injected cleanup failure");
            _afterClean = true;
            return Task.FromResult(true);
        }
    }
}
