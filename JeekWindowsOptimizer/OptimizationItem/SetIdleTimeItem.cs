using PowerManagerAPI;

namespace JeekWindowsOptimizer;

public class SetIdleTimeItem : OptimizationItem
{
    public override string GroupNameKey => "System";
    public override string NameKey => "SetIdleTimeName";
    public override string DescriptionKey => "SetIdleTimeDescription";

    public SetIdleTimeItem()
    {
        IsOptimized = SleepTime == 0 && HibernateTime == 0 && TurnOffDisplayTime == 30 * 60;
        IsPersonal = true;
    }

    protected override Task<bool> IsOptimizedChanging(bool value)
    {
        if (!value)
            return Task.FromResult(false);

        SleepTime = 0;
        HibernateTime = 0;
        TurnOffDisplayTime = 30 * 60;
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
