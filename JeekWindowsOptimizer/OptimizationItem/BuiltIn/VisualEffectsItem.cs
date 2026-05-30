using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekWindowsOptimizer;

public class VisualEffectsItem : OptimizationItem
{
    private static readonly ILogger Log = LogManager.CreateLogger<VisualEffectsItem>();

    public override Task Initialize()
    {
        IsOptimized = Disabled;
        return Task.CompletedTask;
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        try
        {
            Disabled = value;
            var isApplied = Disabled;
            return Task.FromResult(value ? isApplied : !isApplied);
        }
        catch (Exception ex)
        {
            Log.ZLogError(ex, $"Failed to change visual effects preset");
            return Task.FromResult(false);
        }
    }

    public override string GroupNameKey => "Display";

    public override string NameKey => "DisableVisualEffectsName";

    public override string DescriptionKey => "DisableVisualEffectsDescription";

    public bool Disabled
    {
        get => WindowsVisualEffects.IsOptimizedPresetApplied();
        set
        {
            if (value)
                WindowsVisualEffects.ApplyOptimizedPreset();
            else
                WindowsVisualEffects.ApplyDefaultPreset();
        }
    }
}
