using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace KlarWin.Services;

/// <summary>
/// Windows 10/11 power slider (Best battery / Balanced / Best performance) via powrprof.dll.
/// Works on virtually all PCs and correlates with thermal/performance behaviour.
/// </summary>
public static class WindowsPowerOverlay
{
    public static readonly Guid BestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    public static readonly Guid Balanced = Guid.Empty;
    public static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    private const string OverlayKeyPath = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
    private const string OverlayAcValue = "ActiveOverlayAcPowerScheme";

    [DllImport("powrprof.dll", EntryPoint = "PowerGetEffectiveOverlayScheme")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid effectiveGuid);

    [DllImport("powrprof.dll", EntryPoint = "PowerSetActiveOverlayScheme")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlayGuid);

    public static Guid? ReadEffectiveOverlay()
    {
        try
        {
            if (PowerGetEffectiveOverlayScheme(out var guid) == 0)
            {
                return guid;
            }
        }
        catch
        {
            // powrprof optional on very old systems
        }

        return ReadOverlayFromRegistry();
    }

    public static bool TrySetOverlay(Guid guid) =>
        PowerSetActiveOverlayScheme(guid) == 0;

    public static string? MapOverlayToThermalModeId(Guid guid)
    {
        if (guid == BestEfficiency) return "dell-thermal-quiet";
        if (guid == BestPerformance) return "dell-thermal-ultra";
        if (guid == Balanced) return "dell-thermal-optimal";
        return null;
    }

    public static Guid? MapThermalModeIdToOverlay(string optionId) => optionId switch
    {
        "dell-thermal-quiet" => BestEfficiency,
        "dell-thermal-optimal" => Balanced,
        "dell-thermal-cool" => BestEfficiency,
        "dell-thermal-ultra" => BestPerformance,
        _ => null
    };

    public static string LabelForOverlay(Guid guid)
    {
        if (guid == BestEfficiency) return "Strom sparen";
        if (guid == BestPerformance) return "Beste Leistung";
        if (guid == Balanced) return "Ausgewogen";
        return "Unbekannt";
    }

    private static Guid? ReadOverlayFromRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(OverlayKeyPath);
            var raw = key?.GetValue(OverlayAcValue)?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return Guid.TryParse(raw, out var guid) ? guid : null;
        }
        catch
        {
            return null;
        }
    }
}
