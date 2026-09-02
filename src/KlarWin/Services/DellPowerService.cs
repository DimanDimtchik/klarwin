namespace KlarWin.Services;

/// <summary>Backward-compatible alias — use <see cref="LaptopPowerService"/>.</summary>
public static class DellPowerService
{
    public static bool IsDellMachine() =>
        LaptopPowerService.Manufacturer.Contains("Dell", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<(string Id, string Label, string Hint)> ChargingOptions =>
        LaptopPowerService.ChargingOptions;

    public static IReadOnlyList<(string Id, string Label, string Hint)> ThermalOptions =>
        LaptopPowerService.ThermalOptions;

    public static string? GetCurrentChargingModeId() => LaptopPowerService.GetCurrentChargingModeId();

    public static string? GetCurrentThermalModeId() => LaptopPowerService.GetCurrentThermalModeId();

    public static string FormatCurrentChargingHint() => LaptopPowerService.FormatCurrentChargingHint();

    public static string FormatCurrentThermalHint() => LaptopPowerService.FormatCurrentThermalHint();

    public static string? ApplyChargingMode(string optionId) => LaptopPowerService.ApplyChargingMode(optionId);

    public static string? ApplyThermalMode(string optionId) => LaptopPowerService.ApplyThermalMode(optionId);
}
