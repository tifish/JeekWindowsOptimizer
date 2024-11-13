using JeekTools;

namespace JeekWindowsOptimizer;

public class UseClassicalContextMenuItem : OptimizationItem
{
    public override string GroupName => "文件资源管理器";
    public override string Name => "使用 Win10 经典右键菜单";
    public override string Description => """
                                          Windows 11 默认右键菜单少了许多软件的功能，兼容性不佳。
                                          重启资源管理器生效。
                                          """;

    private readonly RegistryValue _registryValue = new(
        @"HKEY_CURRENT_USER\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32",
        "");

    public UseClassicalContextMenuItem()
    {
        ShouldRestartExplorer = true;

        IsOptimized = _registryValue.HasKey();

        IsInitializing = false;
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
