namespace JeekWindowsOptimizer;

public class TestItem : OptimizationItem
{
    public override string GroupName => "测试";
    public override string Name => "禁用 Windows 实时防病毒";

    public override string Description => """
                                          Windows 实时防病毒会影响所有文件访问的性能，建议禁用，之后定期手动扫描病毒。
                                          立即生效。
                                          """;

    public override Task<bool> OnHasOptimizedChanging(bool value)
    {
        IsInitializing = false;
        return Task.FromResult(true);
    }
}
