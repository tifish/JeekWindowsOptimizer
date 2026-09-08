namespace JeekWindowsOptimizer;

/// <summary>WER's standard per-user application dumps, separate from system dumps.</summary>
public sealed class UserCrashDumpsCleanupItem : DiskSpaceCleanupItem
{
    private readonly string _directory;

    public UserCrashDumpsCleanupItem() : this(Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps")) { }

    internal UserCrashDumpsCleanupItem(string directory) => _directory = directory;

    public override string NameKey => "UserCrashDumpsCleanupName";
    public override string DescriptionKey => "UserCrashDumpsCleanupDescription";
    protected override bool DefaultChecked => false;

    private bool CanAccessDirectory => string.Equals(Path.GetPathRoot(_directory),
        DiskSpaceItemManager.SystemDriveRoot, StringComparison.OrdinalIgnoreCase)
        && FileSystemCleaner.IsPlainDirectoryPath(_directory);

    protected override Task<long> ScanCore(CancellationToken cancellationToken) => Task.FromResult(
        CanAccessDirectory ? FileSystemCleaner.GetFilesSize(_directory, "*.dmp", cancellationToken) : 0);

    protected override Task<bool> CleanCore(CancellationToken cancellationToken)
    {
        if (!CanAccessDirectory)
            return Task.FromResult(true);
        FileSystemCleaner.DeleteFiles(_directory, "*.dmp", cancellationToken);
        return Task.FromResult(FileSystemCleaner.GetFilesSize(_directory, "*.dmp", cancellationToken) == 0);
    }
}
