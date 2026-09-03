namespace JeekWindowsOptimizer;

/// <summary>
///     Servicing logs (CBS, DISM) and Windows Error Reporting queues. CBS persist
///     logs are a known runaway: when the cab compression fails they pile up to
///     tens of gigabytes.
/// </summary>
public class WindowsLogsCleanupItem : DiskSpaceCleanupItem
{
    public override string NameKey => "WindowsLogsCleanupName";
    public override string DescriptionKey => "WindowsLogsCleanupDescription";

    public static IReadOnlyList<string> LogDirectories
    {
        get
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var programData = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData
            );
            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData
            );

            return
            [
                Path.Join(windows, @"Logs\CBS"),
                Path.Join(windows, @"Logs\DISM"),
                Path.Join(windows, @"Logs\WindowsUpdate"),
                Path.Join(programData, @"Microsoft\Windows\WER\ReportQueue"),
                Path.Join(programData, @"Microsoft\Windows\WER\ReportArchive"),
                Path.Join(programData, @"Microsoft\Windows\WER\Temp"),
                Path.Join(localAppData, @"Microsoft\Windows\WER\ReportQueue"),
                Path.Join(localAppData, @"Microsoft\Windows\WER\ReportArchive"),
                Path.Join(localAppData, @"Microsoft\Windows\WER\Temp"),
            ];
        }
    }

    protected override Task<long> ScanCore(CancellationToken cancellationToken)
    {
        return Task.FromResult(
            LogDirectories.Sum(path => FileSystemCleaner.GetDirectorySize(path, cancellationToken))
        );
    }

    protected override Task<bool> CleanCore(CancellationToken cancellationToken)
    {
        foreach (var path in LogDirectories)
            FileSystemCleaner.DeleteDirectoryContents(path, cancellationToken);
        return Task.FromResult(true);
    }
}
