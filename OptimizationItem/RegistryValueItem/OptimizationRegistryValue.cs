using JeekTools;

namespace JeekWindowsOptimizer;

public abstract class OptimizationRegistryValue(string keyPath, string valueName, bool shouldTurnOffTamperProtection, bool shouldUpdateGroupPolicy, bool shouldReboot)
{
    public abstract bool HasOptimized { get; set; }
    protected RegistryValue Value = new(keyPath, valueName);
}

public class OptimizationRegistryIntValue(
    string keyPath,
    string valueName,
    int defaultValue,
    int optimizingValue,
    bool deleteDefaultValue,
    bool shouldTurnOffTamperProtection,
    bool shouldUpdateGroupPolicy,
    bool shouldReboot)
    : OptimizationRegistryValue(keyPath, valueName, shouldTurnOffTamperProtection, shouldUpdateGroupPolicy, shouldReboot)
{
    public override bool HasOptimized
    {
        get => Value.GetValue(defaultValue) == optimizingValue;
        set
        {
            if (value)
                Value.SetValue(optimizingValue);
            else if (deleteDefaultValue)
                Value.DeleteValue();
            else
                Value.SetValue(defaultValue);
        }
    }
}

public class OptimizationRegistryStringValue(
    string keyPath,
    string valueName,
    string defaultValue,
    string optimizingValue,
    bool deleteDefaultValue,
    bool shouldTurnOffTamperProtection,
    bool shouldUpdateGroupPolicy,
    bool shouldReboot)
    : OptimizationRegistryValue(keyPath, valueName, shouldTurnOffTamperProtection, shouldUpdateGroupPolicy, shouldReboot)
{
    public override bool HasOptimized
    {
        get => Value.GetValue(defaultValue) == optimizingValue;
        set
        {
            if (value)
                Value.SetValue(optimizingValue);
            else if (deleteDefaultValue)
                Value.DeleteValue();
            else
                Value.SetValue(defaultValue);
        }
    }
}

public class OptimizationRegistryBinaryValue(
    string keyPath,
    string valueName,
    byte[] defaultValue,
    byte[] optimizingValue,
    bool deleteDefaultValue,
    bool shouldTurnOffTamperProtection,
    bool shouldUpdateGroupPolicy,
    bool shouldReboot)
    : OptimizationRegistryValue(keyPath, valueName, shouldTurnOffTamperProtection, shouldUpdateGroupPolicy, shouldReboot)
{
    public override bool HasOptimized
    {
        get => Value.GetBinaryValue(defaultValue) == optimizingValue;
        set
        {
            if (value)
                Value.SetBinaryValue(optimizingValue);
            else if (deleteDefaultValue)
                Value.DeleteValue();
            else
                Value.SetBinaryValue(defaultValue);
        }
    }
}
