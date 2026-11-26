using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Jeek.Avalonia.Localization;
using JeekTools;
using MsBox.Avalonia.ViewModels;

namespace JeekWindowsOptimizer.Mcp;

internal static class StorageMoveProbe
{
    internal static async Task<string> RunAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var fixture = Directory.CreateTempSubdirectory("JwoStorageMove-");
        var source = Path.Combine(fixture.FullName, "source");
        var dest = Path.Combine(fixture.FullName, "destination");
        const string first = "a.json";
        var last = Path.Combine("nested", "b.json");
        try
        {
            foreach (var root in new[] { source, dest })
            {
                Directory.CreateDirectory(Path.Combine(root, "nested"));
                File.WriteAllText(Path.Combine(root, first), root);
                File.WriteAllText(Path.Combine(root, last), root);
            }
            var unique = Path.Combine(source, "unique.json");
            File.WriteAllText(unique, "unique");

            // First approve an overwrite, then close the second dialog without choosing.
            var moved = await SettingsStorage.TryMoveConfigRootAsync(source, dest, (from, to) =>
                ChooseAsync(from, to, Path.GetFileName(to) == first ? "StorageConflictUseSource" : null));
            Require(!moved && File.Exists(unique), "Closing a later prompt must cancel the whole move.");
            foreach (var root in new[] { source, dest })
                Require(File.ReadAllText(Path.Combine(root, first)) == root && File.ReadAllText(Path.Combine(root, last)) == root,
                    "Cancellation modified an original file.");

            // Another process creates a collision after the first prompt.
            var unexpected = Path.Combine(dest, "unique.json");
            try
            {
                await SettingsStorage.TryMoveConfigRootAsync(source, dest, (_, _) =>
                {
                    File.WriteAllText(unexpected, "new destination");
                    return Task.FromResult(ConfigFileConflictChoice.UseSource);
                });
                throw new InvalidOperationException("An unapproved conflict was accepted.");
            }
            catch (IOException) when (File.ReadAllText(unexpected) == "new destination" && File.Exists(unique)) { }
            File.Delete(unexpected);

            // Force a replacement failure. The old target must survive.
            using (File.Open(Path.Combine(dest, first), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try
                {
                    await SettingsStorage.TryMoveConfigRootAsync(source, dest, (_, _) =>
                        Task.FromResult(ConfigFileConflictChoice.UseSource));
                    throw new InvalidOperationException("Locked destination was replaced.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Require(File.ReadAllText(Path.Combine(source, first)) == source
                        && File.ReadAllText(Path.Combine(dest, first)) == dest, "Failed replacement lost an original.");
                }
            }

            moved = await SettingsStorage.TryMoveConfigRootAsync(source, dest, (from, to) =>
                ChooseAsync(from, to, Path.GetFileName(to) == first ? "StorageConflictUseSource" : "StorageConflictKeepDestination"));
            Require(moved && !Directory.Exists(source) && File.ReadAllText(Path.Combine(dest, first)) == source
                && File.ReadAllText(Path.Combine(dest, last)) == dest && File.ReadAllText(unexpected) == "unique",
                "The migrated directory did not contain the selected versions.");
            return "PASS: actual conflict dialogs, mixed choices, cancel before moving, unexpected conflict, locked target and retry.";
        }
        finally
        {
            fixture.Delete(recursive: true);
        }

        async Task<ConfigFileConflictChoice> ChooseAsync(string from, string to, string? buttonKey)
        {
            var existingWindows = desktop.Windows.ToHashSet();
            var result = MainViewModel.ChooseStorageConflictAsync(from, to);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            var dialog = desktop.Windows.Single(w => !existingWindows.Contains(w) && w.DataContext is MsBoxCustomViewModel);
            try
            {
                var model = (MsBoxCustomViewModel)dialog.DataContext!;
                Require(model.ContentMessage.Contains(from) && model.ContentMessage.Contains(to), "Missing conflict paths.");
                if (buttonKey is null)
                    dialog.Close();
                else
                    model.ButtonClick(Localizer.Get(buttonKey));
                return await result.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally
            {
                if (dialog.IsVisible) dialog.Close();
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
