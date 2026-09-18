namespace JeekWindowsOptimizer;

internal static class ActivatorFiles
{
    public static string DirectoryPath => Path.Join(AppContext.BaseDirectory, "Tools", "Activator");

    public static bool IsAvailable(string entryPoint) =>
        File.Exists(Path.Join(DirectoryPath, entryPoint))
        && File.Exists(Path.Join(DirectoryPath, "Activator.rar"))
        && File.Exists(Path.Join(DirectoryPath, "UnRAR.exe"));
}
