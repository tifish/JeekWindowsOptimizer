using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Text.Json.Nodes;
using Jeek.Avalonia.Localization;
using JeekTools;

namespace JeekWindowsOptimizer;

/// <summary>Offline relocation of Docker's managed WSL VHD. Docker keeps its configured path.</summary>
internal static class DockerDiskStorage
{
    internal const string DiskName = "docker_data.vhdx";

    internal static string? Discover()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new List<string> { Path.Combine(local, "Docker", "wsl") };
        var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Docker");
        var file = new[] { "settings-store.json", "settings.json" }.Select(n => Path.Combine(settings, n)).FirstOrDefault(File.Exists);
        if (file is not null)
        {
            // Only read the path whitelist. Never return or log Docker settings (which can contain secrets).
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file)) is JsonObject json)
                    foreach (var key in new[] { "dataFolder", "DataFolder", "customWslDistroDir", "CustomWslDistroDir" })
                        if (json[key] is JsonValue value && value.TryGetValue<string>(out var path)
                            && !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
                            roots.Insert(0, path);
            }
            catch (System.Text.Json.JsonException) { }
        }
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var directory in new[] { Path.Combine(root, "disk"), Path.Combine(root, "data"), root })
                if (File.Exists(Path.Combine(directory, DiskName))
                    || File.Exists(Path.Combine(directory + ".jeek-backup", DiskName)))
                    return WslStorage.Normalize(directory);
        return null;
    }

    internal static string Resolve(string anchor) => JunctionPoint.Exists(anchor)
        ? WslStorage.Normalize(JunctionPoint.GetTarget(anchor)) : WslStorage.Normalize(anchor);

    internal static long Measure(string source)
    {
        if (!FileSystemCleaner.IsPlainDirectoryPath(source))
            throw new IOException(Localizer.Get("VirtualDiskMigrationInvalidPath"));
        var entries = new DirectoryInfo(source).GetFileSystemInfos();
        if (entries.Length != 1 || entries[0] is not FileInfo file
            || !file.Name.Equals(DiskName, StringComparison.OrdinalIgnoreCase)
            || (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Encrypted)) != 0)
            throw new IOException(Localizer.Get("DockerMigrationLayoutUnsupported"));
        return file.Length;
    }

    internal static void Validate(string anchor, string expectedSource, string target, bool restore)
    {
        var source = Resolve(anchor);
        if (!source.Equals(expectedSource, StringComparison.OrdinalIgnoreCase))
            throw new IOException(Localizer.Get("DiskSpaceQueuedSourceChanged"));
        if (!FileSystemCleaner.IsPlainDirectoryPath(Path.GetDirectoryName(anchor)!)
            || Directory.Exists(anchor + ".jeek-backup") || File.Exists(anchor + ".jeek-backup"))
            throw new IOException(string.Format(Localizer.Get("DockerMigrationRecoveryRequired"), anchor + ".jeek-backup"));
        var bytes = Measure(source);
        // On restore the destination is occupied by our existing junction. Copy to a fresh sibling first.
        if (restore)
        {
            if (!JunctionPoint.Exists(anchor) || !target.Equals(anchor, StringComparison.OrdinalIgnoreCase))
                throw new IOException(Localizer.Get("VirtualDiskMigrationInvalidPath"));
            WslStorage.ValidateTarget(source, anchor + ".jeek-restore", bytes);
        }
        else WslStorage.ValidateTarget(source, target, bytes);
    }

    internal static async Task MoveAsync(string anchor, string expectedSource, string target, bool restore,
        Action ensureStopped, IProgress<string>? progress, CancellationToken ct, Action? beforeSwitch = null,
        Action? afterAnchorMoved = null)
    {
        ensureStopped();
        Validate(anchor, expectedSource, target, restore);
        var source = Resolve(anchor);
        var destination = restore ? anchor + ".jeek-restore" : target;
        var disk = Path.Combine(source, DiskName);
        var copy = Path.Combine(destination, DiskName);
        var backup = anchor + ".jeek-backup";
        var prepared = anchor + ".jeek-link-" + Guid.NewGuid().ToString("N");
        var oldWasLink = JunctionPoint.Exists(anchor);
        var switched = false;
        var movedAnchor = false;
        var createdDestination = false;
        var createdCopy = false;
        var directorySecurity = new DirectoryInfo(source).GetAccessControl(AccessControlSections.Access);
        var diskSecurity = new FileInfo(disk).GetAccessControl(AccessControlSections.Access);
        // A data-drive root may grant other accounts access. Preserve the original effective DACL.
        directorySecurity.SetAccessRuleProtection(true, true);
        diskSecurity.SetAccessRuleProtection(true, true);
        try
        {
            // Deny readers/writers while permitting our own rename/delete at commit.
            await using var input = new FileStream(disk, FileMode.Open, FileAccess.Read, FileShare.Delete, 1024 * 1024, true);
            ensureStopped();
            // Atomically claim a new destination; CreateDirectory alone also succeeds on existing paths.
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var claim = destination + ".jeek-claim-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(claim);
            try { Directory.Move(claim, destination); }
            finally { if (Directory.Exists(claim)) Directory.Delete(claim); }
            createdDestination = true;
            new DirectoryInfo(destination).SetAccessControl(directorySecurity);
            await using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Delete, 1024 * 1024, true))
            {
                createdCopy = true;
                progress?.Report(Localizer.Get("DockerMigrationCopying"));
                await input.CopyToAsync(output, ct);
                output.Flush(true);
                progress?.Report(Localizer.Get("DockerMigrationVerifying"));
                input.Position = 0;
                output.Position = 0;
                var originalHash = await SHA256.HashDataAsync(input, ct);
                var copyHash = await SHA256.HashDataAsync(output, ct);
                if (!originalHash.SequenceEqual(copyHash))
                    throw new IOException(Localizer.Get("VirtualDiskMigrationVerificationFailed"));
                ct.ThrowIfCancellationRequested();
                ensureStopped();
                if (Measure(source) != input.Length || !Resolve(anchor).Equals(source, StringComparison.OrdinalIgnoreCase))
                    throw new IOException(Localizer.Get("DiskSpaceQueuedSourceChanged"));
                beforeSwitch?.Invoke();
                if (!restore) JunctionPoint.Create(prepared, destination, false);
                // Windows cannot rename a directory while a descendant has an open handle,
                // even with FILE_SHARE_DELETE. Release only after flush and byte verification.
                await input.DisposeAsync();
                await output.DisposeAsync();
                new FileInfo(copy).SetAccessControl(diskSecurity);
                ensureStopped();
                Directory.Move(anchor, backup);
                movedAnchor = true;
                try
                {
                    afterAnchorMoved?.Invoke();
                    Directory.Move(restore ? destination : prepared, anchor);
                    switched = true;
                }
                catch
                {
                    Directory.Move(backup, anchor);
                    movedAnchor = false;
                    throw;
                }
            }
            // Delete only the verified source disk, never recursively delete a discovered directory.
            File.Delete(Path.Combine(oldWasLink ? source : backup, DiskName));
            if (oldWasLink)
            {
                Directory.Delete(source);
                JunctionPoint.Delete(backup);
            }
            else Directory.Delete(backup);
        }
        catch (Exception ex) when (switched)
        {
            // New storage is authoritative. Never roll it back after it can have been opened by Docker.
            throw new IOException(string.Format(Localizer.Get("DockerMigrationCleanupRequired"), source, backup), ex);
        }
        finally
        {
            if (JunctionPoint.Exists(prepared)) JunctionPoint.Delete(prepared);
            if (!switched && !movedAnchor && createdDestination)
            {
                if (createdCopy && File.Exists(copy)) File.Delete(copy);
                if (Directory.Exists(destination)) Directory.Delete(destination);
            }
        }
    }
}
