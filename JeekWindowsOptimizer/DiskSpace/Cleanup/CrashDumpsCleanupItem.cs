namespace JeekWindowsOptimizer;

/// <summary>MEMORY.DMP, Minidump\*, and LiveKernelReports\**.</summary>
public class CrashDumpsCleanupItem : DiskSpaceCleanupItem
{
    public override string NameKey => "CrashDumpsCleanupName";
    public override string DescriptionKey => "CrashDumpsCleanupDescription";

    private static string WindowsDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static string MemoryDumpPath => Path.Join(WindowsDirectory, "MEMORY.DMP");
    private static string MinidumpPath => Path.Join(WindowsDirectory, "Minidump");
    private static string LiveKernelReportsPath => Path.Join(WindowsDirectory, "LiveKernelReports");

    protected override Task<long> ScanCore(CancellationToken cancellationToken)
    {
        var total = FileSystemCleaner.GetFileSize(MemoryDumpPath);
        total += FileSystemCleaner.GetDirectorySize(MinidumpPath, cancellationToken);
        total += FileSystemCleaner.GetDirectorySize(LiveKernelReportsPath, cancellationToken);
        return Task.FromResult(total);
    }

    protected override Task<bool> CleanCore(CancellationToken cancellationToken)
    {
        FileSystemCleaner.DeleteFile(MemoryDumpPath);
        FileSystemCleaner.DeleteDirectoryContents(MinidumpPath, cancellationToken);
        FileSystemCleaner.DeleteDirectoryContents(LiveKernelReportsPath, cancellationToken);
        return Task.FromResult(true);
    }
}
