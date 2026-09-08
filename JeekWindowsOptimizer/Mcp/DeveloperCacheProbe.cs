namespace JeekWindowsOptimizer.Mcp;

internal static class DeveloperCacheProbe
{
    internal static async Task<string> RunAsync()
    {
        var root = Path.Join(Path.GetTempPath(), "JeekDeveloperProbe-" + Guid.NewGuid().ToString("N"));
        var user = Path.Join(root, "user");
        var local = Path.Join(root, "local");
        var roaming = Path.Join(root, "roaming");
        var shared = Path.Join(root, "shared");
        foreach (var directory in new[] { user, local, roaming, shared }) Directory.CreateDirectory(directory);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var paths = new DeveloperCachePaths(user, local, roaming, shared, name => environment.GetValueOrDefault(name));
        static void Check(bool value, string name) => DiskSpaceCleanupProbe.Require(value, name);
        string Write(string file, int bytes = 100)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, new byte[bytes]);
            return file;
        }
        try
        {
            Write(Path.Join(local, "Microsoft", "VisualStudio", "17.0_test", "ComponentModelCache", "entry"));
            Write(Path.Join(local, "Microsoft", "VisualStudio", "18.0_other", "ComponentModelCache", "entry"));
            var keep = new[]
            {
                Write(Path.Join(user, ".cargo", "bin", "cargo.exe")),
                Write(Path.Join(user, ".cargo", "registry", "src", "keep.rs")),
                Write(Path.Join(user, ".gradle", "gradle.properties")),
                Write(Path.Join(user, ".gradle", "jdks", "keep")),
                Write(Path.Join(user, ".m2", "settings-security.xml")),
                Write(Path.Join(roaming, "Code", "User", "settings.json")),
                Write(Path.Join(roaming, "Code", "User", "History", "keep")),
                Write(Path.Join(roaming, "Code", "Backups", "unsaved")),
                Write(Path.Join(local, "Microsoft", "VisualStudio", "17.0_test", "privateregistry.bin")),
                Write(Path.Join(local, "Microsoft", "VisualStudio", "unrecognized", "ComponentModelCache", "keep")),
                Write(Path.Join(shared, "Microsoft", "VisualStudio", "Packages", "_Instances", "keep")),
                Write(Path.Join(shared, "Installer", "keep.msi")),
            };
            foreach (var kind in DeveloperCachePaths.Kinds)
            {
                var targets = paths.Resolve(kind);
                Check(targets.Count > 0, kind + " has targets");
                foreach (var path in targets) Write(Path.Join(path, "entry"));
                var item = new DeveloperCacheCleanupItem(kind, paths, () => false);
                Check(!item.IsChecked && item.GroupNameKey == DeveloperCacheCleanupItem.DeveloperGroup, kind + " opt-in group");
                await item.RefreshAsync();
                Check(item.SizeBytes == targets.Count * 100, kind + " exact scan");
                await item.CleanAsync();
                Check(item.State == DiskSpaceItemState.Done && item.FreedBytes == targets.Count * 100, kind + " real cleanup");
                Check(keep.All(File.Exists), kind + " preserves non-cache data");
            }
            var pipPath = paths.Resolve("Pip").Single();
            var locked = Write(Path.Join(pipPath, "locked"));
            var pip = new DeveloperCacheCleanupItem("Pip", paths, () => false);
            using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await pip.CleanAsync();
                Check(pip.State == DiskSpaceItemState.Failed && File.Exists(locked), "locked cache is incomplete");
            }
            await pip.CleanAsync();
            Check(pip.State == DiskSpaceItemState.Done, "locked cache retry");
            Write(locked);
            var busy = new DeveloperCacheCleanupItem("Pip", paths, () => true);
            await busy.CleanAsync();
            Check(busy.State == DiskSpaceItemState.Failed && File.Exists(locked), "running tool guard");
            var outside = Path.Join(root, "outside");
            var outsideFile = Write(Path.Join(outside, "keep"));
            Directory.CreateSymbolicLink(Path.Join(pipPath, "link"), outside);
            await pip.CleanAsync();
            Check(pip.State == DiskSpaceItemState.Failed && File.Exists(outsideFile), "nested link rejected");
            Directory.Delete(Path.Join(pipPath, "link"));
            environment["PIP_CACHE_DIR"] = user;
            await pip.CleanAsync();
            Check(pip.State == DiskSpaceItemState.Failed && keep.All(File.Exists), "protected override rejected");
            environment["PIP_CACHE_DIR"] = Environment.GetFolderPath(Environment.SpecialFolder.System);
            await pip.CleanAsync();
            Check(pip.State == DiskSpaceItemState.Failed, "system subtree override rejected");
            var project = Path.Join(root, "project");
            Write(Path.Join(project, "pyproject.toml"));
            environment["PIP_CACHE_DIR"] = project;
            await pip.CleanAsync();
            Check(pip.State == DiskSpaceItemState.Failed, "project override rejected");
            environment["PIP_CACHE_DIR"] = Path.Join(root, "custom pip");
            Check(paths.Resolve("Pip").Single() == environment["PIP_CACHE_DIR"], "pip environment override");
            environment["GRADLE_USER_HOME"] = Path.Join(root, "custom gradle");
            Check(paths.Resolve("Gradle").Single() == Path.Join(environment["GRADLE_USER_HOME"], "caches"), "Gradle home override");
            environment["CARGO_HOME"] = Path.Join(root, "custom cargo");
            Check(paths.Resolve("Cargo").Single().EndsWith(@"custom cargo\registry\cache"), "Cargo home override");
            environment["GOCACHE"] = "off";
            Check(paths.Resolve("GoBuild").Count == 0, "disabled Go cache");
            File.WriteAllText(Path.Join(user, ".npmrc"), "//registry.example/:_authToken=not-a-real-secret\ncache=" + Path.Join(root, "npm custom") + "\n");
            Check(paths.Resolve("Npm").All(p => p.StartsWith(Path.Join(root, "npm custom"))), "npm user config");
            environment["npm_config_cache"] = Path.Join(root, "npm env");
            Check(paths.Resolve("Npx").Single() == Path.Join(root, "npm env", "_npx"), "npm environment precedence and npx isolation");
            File.WriteAllText(Path.Join(user, ".m2", "settings.xml"), "<settings xmlns=\"http://maven.apache.org/SETTINGS/1.0.0\"><localRepository>${user.home}/custom-maven</localRepository></settings>");
            Check(paths.Resolve("Maven").Single() == Path.Join(user, "custom-maven"), "Maven namespace and home expansion");
            var items = DiskSpaceItemManager.CreateItems();
            Check(items.Select(i => i.NameKey).Distinct().Count() == items.Count, "no duplicate rows");
            var developer = items.Where(i => i.GroupNameKey == DeveloperCacheCleanupItem.DeveloperGroup).Cast<DiskSpaceCleanupItem>().ToList();
            Check(developer.Count == DeveloperCachePaths.Kinds.Length + 3 && developer.All(i => !i.IsChecked), "NuGet included; entire developer group opt-in");
            return "PASS developer: all 12 caches measured/cleaned in isolation; settings, sources, IDE backups and installer metadata preserved; locked files/retry, process guard, links, protected/project overrides, npm/Maven configuration, environment paths, unique rows and default opt-in";
        }
        finally { FileSystemCleaner.DeleteDirectory(root); }
    }
}
