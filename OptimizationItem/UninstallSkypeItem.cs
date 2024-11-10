namespace JeekWindowsOptimizer;

public class UninstallSkypeItem : OptimizationItem
{
    public override string GroupName => "卸载";
    public override string Name => "卸载 Skype";

    public override string Description => """
                                          Skype 是微软的即时通讯工具，不使用可以卸载。
                                          立即生效。
                                          """;

    private const string PackageName = "Microsoft.SkypeApp";

    public async Task<UninstallSkypeItem> Initialize()
    {
        HasOptimized = !await MicrosoftStore.HasPackage(PackageName);

        IsInitializing = false;

        return this;
    }

    public override async void HasOptimizedChanged(bool value)
    {
        if (!value)
            return;

        await MicrosoftStore.UninstallPackage(PackageName);
    }
}
