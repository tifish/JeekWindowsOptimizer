namespace JeekWindowsOptimizer;

public class UninstallCortanaItem : OptimizationItem
{
    public override string GroupName => "卸载";
    public override string Name => "卸载 Cortana";

    public override string Description => """
                                          Cortana 是微软的语音助手，不使用可以卸载。
                                          立即生效。
                                          """;

    public UninstallCortanaItem()
    {
        HasOptimized = MicrosoftStore.GetPackage("Microsoft.549981C3F5F10") is null;

        IsInitializing = false;
    }

    public override async void HasOptimizedChanged(bool value)
    {
        if (!value)
            return;

        await JeekTools.Executor.RunAndWait("PowerShell", """-ex bypass -c "Get-AppxPackage -AllUsers Microsoft.549981C3F5F10 | Remove-AppxPackage" """);
    }
}
