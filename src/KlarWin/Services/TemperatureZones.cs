namespace KlarWin.Services;

public enum HealthLevel
{
    Unknown = 0,
    Ok = 1,
    Watch = 2,
    Critical = 3
}

public static class TemperatureZones
{
    public static (double Warn, double Crit) Thresholds(bool isGpu, bool isBoard = false)
    {
        if (isBoard) return (70, 85);
        if (isGpu) return (80, 90);
        return (75, 90);
    }

    public static HealthLevel LevelForCelsius(double celsius, bool isGpu, bool isBoard = false)
    {
        var (warn, crit) = Thresholds(isGpu, isBoard);
        if (celsius >= crit) return HealthLevel.Critical;
        if (celsius >= warn) return HealthLevel.Watch;
        return HealthLevel.Ok;
    }

    public static string LevelLabel(HealthLevel level) => level switch
    {
        HealthLevel.Ok => "Grün",
        HealthLevel.Watch => "Gelb",
        HealthLevel.Critical => "Rot",
        _ => "Unbekannt"
    };

    public static string OverallLabel(HealthLevel level) => level switch
    {
        HealthLevel.Ok => "In Ordnung",
        HealthLevel.Watch => "Beobachten",
        HealthLevel.Critical => "Auffällig",
        _ => "Unbekannt"
    };

    public static System.Windows.Media.Color LevelColor(HealthLevel level) => level switch
    {
        HealthLevel.Ok => System.Windows.Media.Color.FromRgb(0x2F, 0x6A, 0x4A),
        HealthLevel.Watch => System.Windows.Media.Color.FromRgb(0xC9, 0x9A, 0x2E),
        HealthLevel.Critical => System.Windows.Media.Color.FromRgb(0xB8, 0x3A, 0x3A),
        _ => System.Windows.Media.Color.FromRgb(0x6B, 0x7C, 0x75)
    };

    public static double BarPercentForCelsius(double celsius, bool isGpu, bool isBoard = false)
    {
        var (_, crit) = Thresholds(isGpu, isBoard);
        var scaleMax = Math.Max(crit + 10, 100);
        return Math.Clamp(100.0 * celsius / scaleMax, 0, 100);
    }

    public static bool IsGpuSensor(string sensorName) =>
        sensorName.Contains("GPU", StringComparison.OrdinalIgnoreCase)
        || sensorName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
        || sensorName.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
        || sensorName.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
        || sensorName.Contains("Graphics", StringComparison.OrdinalIgnoreCase)
        || sensorName.Contains("RTX", StringComparison.OrdinalIgnoreCase)
        || sensorName.Contains("GTX", StringComparison.OrdinalIgnoreCase);

    public static bool IsBoardSensor(string sensorName) =>
        sensorName.Contains("Motherboard", StringComparison.OrdinalIgnoreCase)
        || sensorName.Contains("Mainboard", StringComparison.OrdinalIgnoreCase)
        || sensorName.Contains("Chipset", StringComparison.OrdinalIgnoreCase)
        || sensorName.Contains("PCH", StringComparison.OrdinalIgnoreCase)
        || sensorName.Contains("VRM", StringComparison.OrdinalIgnoreCase)
        || sensorName.Contains("System", StringComparison.OrdinalIgnoreCase)
        || sensorName.Contains("SOC", StringComparison.OrdinalIgnoreCase);

    public static bool IsCpuSensor(string sensorName) =>
        !IsGpuSensor(sensorName)
        && !IsBoardSensor(sensorName)
        && (sensorName.Contains("CPU", StringComparison.OrdinalIgnoreCase)
            || sensorName.Contains("Core", StringComparison.OrdinalIgnoreCase)
            || sensorName.Contains("Package", StringComparison.OrdinalIgnoreCase)
            || sensorName.Contains("P-Core", StringComparison.OrdinalIgnoreCase)
            || sensorName.Contains("E-Core", StringComparison.OrdinalIgnoreCase)
            || sensorName.Contains("Thermal", StringComparison.OrdinalIgnoreCase));
}
