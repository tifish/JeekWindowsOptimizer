using System.Globalization;

namespace JeekWindowsOptimizer;

public static class ByteSize
{
    private static readonly string[] Units = ["KB", "MB", "GB", "TB"];

    public static string Format(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";

        double value = bytes;
        var unit = -1;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return $"{value.ToString(format, CultureInfo.InvariantCulture)} {Units[unit]}";
    }
}
