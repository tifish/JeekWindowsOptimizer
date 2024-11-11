namespace JeekWindowsOptimizer;

public class UninstallCortanaItem : OptimizationItem
{
    public override string GroupName => "卸载";
    public override string Name => "卸载 Cortana";

    public override string Description => """
                                          Cortana 是微软的语音助手，不使用可以卸载。
                                          立即生效。
                                          """;

    private const string PackageName = "Microsoft.549981C3F5F10";

    public async Task<UninstallCortanaItem> Initialize()
    {
        HasOptimized = !await MicrosoftStore.HasPackage(PackageName);

        IsInitializing = false;

        return this;
    }

    public override async Task<bool> OnHasOptimizedChanging(bool value)
    {
        if (!value)
            return false;

        await MicrosoftStore.UninstallPackage(PackageName);
        return true;
    }
}
