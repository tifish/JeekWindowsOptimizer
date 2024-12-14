using JeekTools;
using PowerManagerAPI;

namespace JeekWindowsOptimizer;

public class DisableSystemSounds : OptimizationItem
{
    public override string GroupNameKey => "System";
    public override string NameKey => "DisableSystemSoundsName";
    public override string DescriptionKey => "DisableSystemSoundsDescription";

    private readonly RegistryValue _systemSoundSchemeValue = new(@"HKEY_CURRENT_USER\AppEvents\Schemes", null);
    private const string SystemSoundsRootKey = @"HKEY_CURRENT_USER\AppEvents\Schemes\Apps";

    public DisableSystemSounds()
    {
        IsOptimized = _systemSoundSchemeValue.GetValue("") == ".None";
        IsPersonal = true;
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        _systemSoundSchemeValue.SetValue(value ? ".None" : ".Default");

        using var rootKey = RegistryHelper.OpenKey(SystemSoundsRootKey, true);
        if (rootKey == null)
            return Task.FromResult(false);

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

                var currentSoundKey = soundKey.OpenSubKey(".Current", true)
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

        return Task.FromResult(true);
    }

    public static int SleepTime
    {
        get => (int)PowerManager.GetPlanSetting(PowerManager.GetActivePlan(), SettingSubgroup.SLEEP_SUBGROUP, Setting.STANDBYIDLE, PowerSource.AC);
        set => PowerManager.SetPlanSetting(PowerManager.GetActivePlan(), SettingSubgroup.SLEEP_SUBGROUP, Setting.STANDBYIDLE, PowerSource.AC, (uint)value);
    }

    public static int HibernateTime
    {
        get => (int)PowerManager.GetPlanSetting(PowerManager.GetActivePlan(), SettingSubgroup.SLEEP_SUBGROUP, Setting.HIBERNATEIDLE, PowerSource.AC);
        set => PowerManager.SetPlanSetting(PowerManager.GetActivePlan(), SettingSubgroup.SLEEP_SUBGROUP, Setting.HIBERNATEIDLE, PowerSource.AC, (uint)value);
    }

    public static int TurnOffDisplayTime
    {
        get => (int)PowerManager.GetPlanSetting(PowerManager.GetActivePlan(), SettingSubgroup.VIDEO_SUBGROUP, Setting.VIDEOIDLE, PowerSource.AC);
        set => PowerManager.SetPlanSetting(PowerManager.GetActivePlan(), SettingSubgroup.VIDEO_SUBGROUP, Setting.VIDEOIDLE, PowerSource.AC, (uint)value);
    }
}
