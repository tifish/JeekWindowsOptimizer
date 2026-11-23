namespace JeekWindowsOptimizer;

public sealed class PowerPlanSettingItem : OptimizationItem
{
    private readonly string _nameKey;
    private readonly string _descriptionKey;

    public IReadOnlyList<PowerPlanSettingSpec> Specs { get; }

    public override string GroupNameKey => "System";
    public override string NameKey => _nameKey;
    public override string DescriptionKey => _descriptionKey;

    private PowerPlanSettingItem(
        string nameKey,
        string descriptionKey,
        OptimizationItemCategory category,
        params PowerPlanSettingSpec[] specs
    )
    {
        _nameKey = nameKey;
        _descriptionKey = descriptionKey;
        Category = category;
        Specs = specs;
    }

    public static IEnumerable<PowerPlanSettingItem> CreateAcOptimizationItems()
    {
        yield return new PowerPlanSettingItem(
            "DisablePcieAspmName",
            "DisablePcieAspmDescription",
            OptimizationItemCategory.Default,
            PowerPlanSettings.PcieAspm
        );
        yield return new PowerPlanSettingItem(
            "DisableUsbPowerSavingName",
            "DisableUsbPowerSavingDescription",
            OptimizationItemCategory.Default,
            PowerPlanSettings.UsbSelectiveSuspend,
            PowerPlanSettings.Usb3LinkPowerManagement
        );
        yield return new PowerPlanSettingItem(
            "DisableAhciLinkPowerManagementName",
            "DisableAhciLinkPowerManagementDescription",
            OptimizationItemCategory.Default,
            PowerPlanSettings.AhciLinkPowerManagement
        );
        yield return new PowerPlanSettingItem(
            "DisableNvmeDeepPowerSavingName",
            "DisableNvmeDeepPowerSavingDescription",
            OptimizationItemCategory.Default,
            PowerPlanSettings.NvmeNoppme,
            PowerPlanSettings.PrimaryNvmeLatencyTolerance,
            PowerPlanSettings.SecondaryNvmeLatencyTolerance
        );
        yield return new PowerPlanSettingItem(
            "DisableWirelessPowerSavingName",
            "DisableWirelessPowerSavingDescription",
            OptimizationItemCategory.Default,
            PowerPlanSettings.WirelessPowerSaving
        );
    }

    public static PowerPlanSettingItem CreateDisableDiskIdleItem()
    {
        return new PowerPlanSettingItem(
            "DisableDiskIdleName",
            "DisableDiskIdleDescription",
            OptimizationItemCategory.Personal,
            PowerPlanSettings.DiskIdleTimeout
        );
    }

    public override async Task Initialize()
    {
        IsOptimized = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            () => PowerPlanSettings.AreAllOptimized(Specs)
        );
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        return OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.ExclusiveBackground,
            () => PowerPlanSettings.Apply(Specs, value)
        );
    }
}
