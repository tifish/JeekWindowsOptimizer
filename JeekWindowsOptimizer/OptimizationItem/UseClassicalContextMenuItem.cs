using JeekTools;

namespace JeekWindowsOptimizer;

public class UseClassicalContextMenuItem : OptimizationItem
{
    public override string GroupNameKey => "Explorer";
    public override string NameKey => "UseClassicalContextMenuName";
    public override string DescriptionKey => "UseClassicalContextMenuDescription";

    private readonly RegistryValue _registryValue = new(
        @"HKEY_CURRENT_USER\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32",
        "");

    public override Task Initialize()
    {
        ShouldRestartExplorer = true;
        IsOptimized = _registryValue.HasKey();
        return Task.CompletedTask;
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        if (value)
            _registryValue.SetValue("");
        else
            _registryValue.DeleteKey();

        return Task.FromResult(value);
    }
}
