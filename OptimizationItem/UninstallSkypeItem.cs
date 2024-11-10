using System.CodeDom.Compiler;

namespace JeekWindowsOptimizer;

public class UninstallSkypeItem : OptimizationItem
{
    public override string GroupName => "卸载";
    public override string Name => "卸载 Skype";

    public override string Description => """
                                          Skype 是微软的即时通讯工具，不使用可以卸载。
                                          立即生效。
                                          """;

    public UninstallSkypeItem()
    {
        HasOptimized = MicrosoftStore.GetPackage("Microsoft.SkypeApp") is null;

        IsInitializing = false;
    }

    public override async void HasOptimizedChanged(bool value)
    {
        if (!value)
            return;

        await JeekTools.Executor.RunAndWait("PowerShell", """-ex bypass -c "Get-AppxPackage -AllUsers Microsoft.SkypeApp | Remove-AppxPackage" """);
    }
}
