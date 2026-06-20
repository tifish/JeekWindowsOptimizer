using DotNetRun;

namespace JeekWindowsOptimizer;

public class DisableSystemSounds : OptimizationItem
{
    public override string GroupNameKey => "System";
    public override string NameKey => "DisableSystemSoundsName";
    public override string DescriptionKey => "DisableSystemSoundsDescription";

    private readonly RegistryValue _systemSoundSchemeValue = new(
        @"HKEY_CURRENT_USER\AppEvents\Schemes",
        null
    );
    private const string SystemSoundsRootKey = @"HKEY_CURRENT_USER\AppEvents\Schemes\Apps";

    public DisableSystemSounds()
    {
        Category = OptimizationItemCategory.Personal;
    }

    public override async Task Initialize()
    {
        IsOptimized = await OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            () => _systemSoundSchemeValue.GetValue("") == ".None"
        );
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        return OptimizationExecutionScheduler.RunAsync(
            OptimizationExecutionAffinity.Background,
            () =>
            {
                _systemSoundSchemeValue.SetValue(value ? ".None" : ".Default");

                using var rootKey = Reg.OpenKey(SystemSoundsRootKey, true);
                if (rootKey == null)
                    return false;

                foreach (var groupName in rootKey.GetSubKeyNames())
                {
                    var groupKey = rootKey.OpenSubKey(groupName, true);
                    if (groupKey == null)
                        continue;

                    foreach (var soundName in groupKey.GetSubKeyNames())
                    {
                        var soundKey = groupKey.OpenSubKey(soundName, true);
                        if (soundKey == null)
                            continue;

                        var currentSoundKey =
                            soundKey.OpenSubKey(".Current", true)
                            ?? soundKey.CreateSubKey(".Current");

                        if (value)
                        {
                            currentSoundKey.SetValue(null, "");
                        }
                        else
                        {
                            var defaultSound = soundKey.OpenSubKey(".Default")?.GetValue(null);
                            if (defaultSound != null)
                                currentSoundKey.SetValue(null, defaultSound);
                        }
                    }
                }

                return true;
            }
        );
    }
}
