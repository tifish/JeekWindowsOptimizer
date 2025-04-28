namespace JeekWindowsOptimizer;

public class DisableWindowsDefenderPUAProtectionItem : OptimizationItem
{
    public override string GroupNameKey => "Kernel";
    public override string NameKey => "DisableWindowsDefenderPUAProtectionName";

    public override string DescriptionKey => "DisableWindowsDefenderPUAProtectionDescription";

    public override async Task Initialize()
    {
        PowerShellService.Commands.Clear();
        PowerShellService.AddCommand("Get-MpPreference").AddCommand("Select-Object").AddParameter("ExpandProperty", "PUAProtection");
        var result = await PowerShellService.InvokeAsync();
        IsOptimized = (byte)result.First().BaseObject == 0;
    }

    protected override async Task<bool> IsOptimizedChanging(bool value)
    {
        PowerShellService.Commands.Clear();
        PowerShellService.AddCommand("Set-MpPreference").AddParameter("PUAProtection", value ? 0 : 1);
        await PowerShellService.InvokeAsync();
        return true;
    }
}
