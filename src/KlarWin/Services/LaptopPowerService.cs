using System.Management;
using Microsoft.Win32;

namespace KlarWin.Services;

/// <summary>
/// Reads and sets laptop charging/thermal settings across vendors.
/// Only trusted sources: OEM driver IOCTL, OEM WMI, documented registry paths.
/// </summary>
public static class LaptopPowerService
{
    private const string UnreadableCharging = "Akku-Lademodus: nicht ermittelbar.";
    private const string UnreadableThermal = "Kühlprofil: nicht ermittelbar.";

    public static IReadOnlyList<(string Id, string Label, string Hint)> ChargingOptions { get; } =
    [
        ("dell-charge-adaptive", "Adaptiv", "Lädt je nach Nutzung — schont den Akku, langsamer als Schnellladen."),
        ("dell-charge-express", "Schnelle Aufladung", "Akku lädt deutlich schneller am Netzteil — etwas mehr Wärme und Akkuverschleiß."),
        ("dell-charge-ac", "Immer Netzteil", "Akku bleibt am Stecker auf Ladestand — ideal am Schreibtisch, weniger Zyklen."),
        ("dell-charge-standard", "Standard", "Ausgewogenes Laden ohne Sonderregeln."),
        ("dell-charge-custom", "Benutzerdefiniert", "Eigene Laderegeln (Start-/Stopp-Schwellen).")
    ];

    public static IReadOnlyList<(string Id, string Label, string Hint)> ThermalOptions { get; } =
    [
        ("dell-thermal-cool", "Kühlen", "Lüfter stärker — Laptop bleibt kühler, etwas lauter."),
        ("dell-thermal-optimal", "Optimiert", "Balance aus Leistung, Temperatur und Lautstärke."),
        ("dell-thermal-quiet", "Ruhig", "Leiser Betrieb — Lüfter gedrosselt, Laptop kann wärmer werden."),
        ("dell-thermal-ultra", "Turbo Modus", "Maximale Leistung — Lüfter laut, Akku entlädt schneller.")
    ];

    private static string? _manufacturer;
    private static bool? _isPortable;
    private static bool? _hasBattery;

    public static string Manufacturer => _manufacturer ??= ReadManufacturer();

    public static bool IsPortableMachine() => _isPortable ??= DetectPortable();

    public static bool HasBattery() => _hasBattery ??= DetectBattery();

    public static bool SupportsExtendedPowerOptions() => IsPortableMachine();

    public static string? GetCurrentChargingModeId() => ProbeChargingMode().ModeId;

    public static string? GetCurrentThermalModeId() => ProbeThermalMode().ModeId;

    public static string FormatCurrentChargingHint()
    {
        var probe = ProbeChargingMode();
        if (probe.ModeId is null)
        {
            return UnreadableCharging;
        }

        if (probe.ModeId == "dell-charge-custom" && TryReadLenovoCustomThresholds(out var start, out var stop))
        {
            return $"Akku-Lademodus: Benutzerdefiniert (Start {start} %, Stopp {stop} %).";
        }

        var label = ChargingOptions.FirstOrDefault(o => o.Id == probe.ModeId).Label;
        return string.IsNullOrWhiteSpace(label)
            ? "Akku-Lademodus: unbekannt."
            : $"Akku-Lademodus: {label}.";
    }

    public static string FormatCurrentThermalHint()
    {
        var probe = ProbeThermalMode();
        if (probe.ModeId is null)
        {
            return UnreadableThermal;
        }

        var label = ThermalOptions.FirstOrDefault(o => o.Id == probe.ModeId).Label;
        return string.IsNullOrWhiteSpace(label)
            ? "Kühlprofil: unbekannt."
            : $"Kühlprofil: {label}.";
    }

    public static string? FormatWindowsPowerHint()
    {
        if (WindowsPowerOverlay.ReadEffectiveOverlay() is not Guid guid)
        {
            return null;
        }

        return $"Windows-Leistungsmodus: {WindowsPowerOverlay.LabelForOverlay(guid)}.";
    }

    public static string? ApplyChargingMode(string optionId)
    {
        if (LenovoEnergyBattery.TryWriteModeId(optionId)
            || TryWriteDellChargeRegistry(optionId)
            || TryWriteLenovoChargeRegistry(optionId)
            || TryWriteLenovoConservation(optionId))
        {
            return ChargingSuccessMessage(optionId);
        }

        return "Lademodus konnte auf diesem Gerät nicht gesetzt werden.";
    }

    public static string? ApplyThermalMode(string optionId)
    {
        if (DellAwccWmi.TryApplyThermalModeId(optionId)
            || TryWriteDellThermalRegistry(optionId)
            || LenovoWmiPower.TrySetSmartFanModeId(optionId))
        {
            return optionId switch
            {
                "dell-thermal-cool" => "Kühlprofil „Kühlen“ gesetzt.",
                "dell-thermal-optimal" => "Kühlprofil „Optimiert“ gesetzt.",
                "dell-thermal-quiet" => "Kühlprofil „Ruhig“ gesetzt.",
                _ => "Kühlprofil „Turbo Modus“ gesetzt."
            };
        }

        return "Kühlprofil konnte auf diesem Gerät nicht gesetzt werden.";
    }

    private enum PowerProbeSource
    {
        None,
        LenovoDriver,
        DellWmi,
        RegistryExact,
        LenovoWmi,
        LenovoPwrmgr
    }

    private readonly record struct PowerProbeResult(string? ModeId, PowerProbeSource Source);

    private static PowerProbeResult ProbeChargingMode()
    {
        if (LenovoEnergyBattery.TryReadModeId(out var lenovoDriverMode) && lenovoDriverMode is not null)
        {
            return new PowerProbeResult(lenovoDriverMode, PowerProbeSource.LenovoDriver);
        }

        foreach (var probe in BuildChargingRegistryProbes())
        {
            var value = TryReadRegistry(probe.Root, probe.Path, probe.ValueName);
            if (probe.Map(value) is string modeId)
            {
                return new PowerProbeResult(modeId, PowerProbeSource.RegistryExact);
            }
        }

        if (TryReadLenovoPwrmgrMode() is string pwrmgrMode)
        {
            return new PowerProbeResult(pwrmgrMode, PowerProbeSource.LenovoPwrmgr);
        }

        return new PowerProbeResult(null, PowerProbeSource.None);
    }

    private static PowerProbeResult ProbeThermalMode()
    {
        if (DellAwccWmi.TryReadThermalModeId(out var dellWmiMode) && dellWmiMode is not null)
        {
            return new PowerProbeResult(dellWmiMode, PowerProbeSource.DellWmi);
        }

        foreach (var probe in BuildThermalRegistryProbes())
        {
            var value = TryReadRegistry(probe.Root, probe.Path, probe.ValueName);
            if (probe.Map(value) is string modeId)
            {
                return new PowerProbeResult(modeId, PowerProbeSource.RegistryExact);
            }
        }

        if (LenovoWmiPower.TryReadSmartFanModeId(out var lenovoWmiMode) && lenovoWmiMode is not null)
        {
            return new PowerProbeResult(lenovoWmiMode, PowerProbeSource.LenovoWmi);
        }

        return new PowerProbeResult(null, PowerProbeSource.None);
    }

    private readonly record struct RegistryProbe(
        RegistryKey Root,
        string Path,
        string ValueName,
        Func<object?, string?> Map);

    private static IEnumerable<RegistryProbe> BuildChargingRegistryProbes()
    {
        yield return new(Registry.CurrentUser, @"Software\Dell\Dell Power Manager\Settings", "PrimaryBatteryChargeMode", MapDellChargeInt);
        yield return new(Registry.LocalMachine, @"Software\Dell\Dell Power Manager\Settings", "PrimaryBatteryChargeMode", MapDellChargeInt);
        yield return new(Registry.CurrentUser, @"Software\Dell\QuickSet\BatteryHealth", "ChargeMode", MapDellChargeInt);
        yield return new(Registry.LocalMachine, @"Software\Dell\QuickSet\BatteryHealth", "ChargeMode", MapDellChargeInt);
        yield return new(Registry.CurrentUser, @"Software\Lenovo\VantageService\AddinData\IdeaNotebookAddin", "BatteryChargeMode", MapLenovoChargeStringValue);
    }

    private static IEnumerable<RegistryProbe> BuildThermalRegistryProbes()
    {
        yield return new(Registry.CurrentUser, @"Software\Dell\Dell Power Manager\Settings", "ThermalMode", MapDellThermalObject);
        yield return new(Registry.LocalMachine, @"Software\Dell\Dell Power Manager\Settings", "ThermalMode", MapDellThermalObject);
        yield return new(Registry.CurrentUser, @"Software\Dell\DellOptimizer\Thermal", "Mode", MapDellThermalObject);
        yield return new(Registry.LocalMachine, @"Software\Dell\DellOptimizer\Thermal", "Mode", MapDellThermalObject);
        yield return new(Registry.LocalMachine, @"SOFTWARE\Policies\Dell\CommandPowerManager", "TMMode", MapDellThermalObject);
    }

    private static string? MapDellChargeInt(object? raw) =>
        TryParseInt(raw) is int v ? MapDellChargeValue(v) : null;

    private static string? MapDellChargeValue(int value) => value switch
    {
        0 => "dell-charge-standard",
        1 => "dell-charge-adaptive",
        2 => "dell-charge-express",
        3 => "dell-charge-ac",
        4 => "dell-charge-custom",
        _ => null
    };

    private static string? MapLenovoChargeStringValue(object? raw)
    {
        var text = raw?.ToString()?.Trim();
        return text switch
        {
            "Normal" => "dell-charge-standard",
            "Quick" => "dell-charge-express",
            "Storage" => "dell-charge-ac",
            "Rapid" => "dell-charge-express",
            "Conservation" => "dell-charge-ac",
            _ => null
        };
    }

    private static string? MapDellThermalObject(object? raw) =>
        TryParseInt(raw) is int v ? MapDellThermalInt(v) : null;

    private static string? MapDellThermalInt(int value) => value switch
    {
        0 => "dell-thermal-optimal",
        1 => "dell-thermal-cool",
        2 => "dell-thermal-quiet",
        3 => "dell-thermal-ultra",
        _ => null
    };

    private static string? TryReadLenovoPwrmgrMode()
    {
        foreach (var path in new[]
                 {
                     @"SOFTWARE\Wow6432Node\Lenovo\PWRMGRV\Data",
                     @"SOFTWARE\Lenovo\PWRMGRV\Data"
                 })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key is not null && ReadPwrmgrModeFromKey(key) is string mode)
                {
                    return mode;
                }
            }
            catch
            {
                // try next
            }
        }

        try
        {
            using var conf = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Wow6432Node\Lenovo\PWRMGRV\ConfKeys\Data");
            if (conf is null) return null;
            foreach (var subName in conf.GetSubKeyNames())
            {
                using var sub = conf.OpenSubKey(subName);
                if (sub is not null && ReadPwrmgrModeFromKey(sub) is string subMode)
                {
                    return subMode;
                }
            }
        }
        catch
        {
            // optional
        }

        return null;
    }

    private static string? ReadPwrmgrModeFromKey(RegistryKey key)
    {
        var stopControl = TryParseInt(key.GetValue("ChargeStopControl"));
        var stopPercent = TryParseInt(key.GetValue("ChargeStopPercentage"));
        var startControl = TryParseInt(key.GetValue("ChargeStartControl"));
        var startPercent = TryParseInt(key.GetValue("ChargeStartPercentage"));

        if (stopControl == 1 && stopPercent is int stop && stop is > 0 and < 100)
        {
            if (startControl == 1 && startPercent is int start && start > 0 && start < stop)
            {
                return "dell-charge-custom";
            }

            return "dell-charge-ac";
        }

        return null;
    }

    private static bool TryReadLenovoCustomThresholds(out int startPercent, out int stopPercent)
    {
        startPercent = 0;
        stopPercent = 0;

        foreach (var path in new[]
                 {
                     @"SOFTWARE\Wow6432Node\Lenovo\PWRMGRV\Data",
                     @"SOFTWARE\Lenovo\PWRMGRV\Data"
                 })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key is null) continue;
                if (TryReadCustomThresholdsFromKey(key, out startPercent, out stopPercent))
                {
                    return true;
                }
            }
            catch
            {
                // try next
            }
        }

        return false;
    }

    private static bool TryReadCustomThresholdsFromKey(RegistryKey key, out int startPercent, out int stopPercent)
    {
        startPercent = 0;
        stopPercent = 0;
        var startControl = TryParseInt(key.GetValue("ChargeStartControl"));
        var stopControl = TryParseInt(key.GetValue("ChargeStopControl"));
        var start = TryParseInt(key.GetValue("ChargeStartPercentage"));
        var stop = TryParseInt(key.GetValue("ChargeStopPercentage"));

        if (startControl != 1 || stopControl != 1 || start is not int s || stop is not int t || s <= 0 || t <= s)
        {
            return false;
        }

        startPercent = s;
        stopPercent = t;
        return true;
    }

    private static bool TryWriteDellChargeRegistry(string optionId)
    {
        var value = optionId switch
        {
            "dell-charge-adaptive" => 1,
            "dell-charge-express" => 2,
            "dell-charge-ac" => 3,
            "dell-charge-standard" => 0,
            "dell-charge-custom" => 4,
            _ => (int?)null
        };
        if (value is null) return false;

        return TryWriteRegistryInt(@"Software\Dell\Dell Power Manager\Settings", "PrimaryBatteryChargeMode", value.Value)
               || TryWriteRegistryInt(@"Software\Dell\QuickSet\BatteryHealth", "ChargeMode", value.Value);
    }

    private static bool TryWriteDellThermalRegistry(string optionId)
    {
        var value = optionId switch
        {
            "dell-thermal-cool" => 1,
            "dell-thermal-optimal" => 0,
            "dell-thermal-quiet" => 2,
            "dell-thermal-ultra" => 3,
            _ => (int?)null
        };
        if (value is null) return false;

        return TryWriteRegistryInt(@"Software\Dell\Dell Power Manager\Settings", "ThermalMode", value.Value)
               || TryWriteRegistryInt(@"Software\Dell\DellOptimizer\Thermal", "Mode", value.Value)
               || TryWriteRegistryInt(@"SOFTWARE\Policies\Dell\CommandPowerManager", "TMMode", value.Value);
    }

    private static bool TryWriteLenovoChargeRegistry(string optionId)
    {
        var text = optionId switch
        {
            "dell-charge-standard" or "dell-charge-adaptive" => "Normal",
            "dell-charge-express" => "Quick",
            "dell-charge-ac" => "Storage",
            _ => null
        };
        if (text is null) return false;
        return TryWriteRegistryString(@"Software\Lenovo\VantageService\AddinData\IdeaNotebookAddin", "BatteryChargeMode", text);
    }

    private static bool TryWriteLenovoConservation(string optionId)
    {
        if (optionId is not ("dell-charge-ac" or "dell-charge-standard"))
        {
            return false;
        }

        foreach (var path in new[]
                 {
                     @"SOFTWARE\Wow6432Node\Lenovo\PWRMGRV\Data",
                     @"SOFTWARE\Lenovo\PWRMGRV\Data"
                 })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path, true);
                if (key is null) continue;

                if (optionId == "dell-charge-ac")
                {
                    key.SetValue("ChargeStopControl", 1, RegistryValueKind.DWord);
                    key.SetValue("ChargeStopPercentage", 80, RegistryValueKind.DWord);
                    key.SetValue("ChargeStartControl", 1, RegistryValueKind.DWord);
                    key.SetValue("ChargeStartPercentage", 75, RegistryValueKind.DWord);
                }
                else
                {
                    key.SetValue("ChargeStopControl", 0, RegistryValueKind.DWord);
                    key.SetValue("ChargeStartControl", 0, RegistryValueKind.DWord);
                }

                return true;
            }
            catch
            {
                // try next
            }
        }

        return false;
    }

    private static string ChargingSuccessMessage(string optionId) => optionId switch
    {
        "dell-charge-adaptive" => "Lademodus „Adaptiv“ gesetzt.",
        "dell-charge-express" => "Lademodus „Schnelle Aufladung“ gesetzt.",
        "dell-charge-ac" => "Lademodus „Immer Netzteil“ gesetzt.",
        "dell-charge-standard" => "Lademodus „Standard“ gesetzt.",
        _ => "Lademodus „Benutzerdefiniert“ gesetzt."
    };

    private static object? TryReadRegistry(RegistryKey root, string path, string name)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name);
        }
        catch
        {
            return null;
        }
    }

    private static int? TryParseInt(object? raw)
    {
        if (raw is int i) return i;
        if (raw is not null && int.TryParse(raw.ToString(), out var parsed)) return parsed;
        return null;
    }

    private static bool TryWriteRegistryInt(string path, string name, int value)
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.CreateSubKey(path, true);
                if (key is null) continue;
                key.SetValue(name, value, RegistryValueKind.DWord);
                return true;
            }
            catch
            {
                // try next
            }
        }

        return false;
    }

    private static bool TryWriteRegistryString(string path, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(path, true);
            if (key is null) return false;
            key.SetValue(name, value, RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadManufacturer()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer FROM Win32_ComputerSystem");
            foreach (var obj in searcher.Get())
            {
                var maker = obj["Manufacturer"]?.ToString()?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(maker)) return maker;
            }
        }
        catch
        {
            // WMI optional
        }

        return "";
    }

    private static bool DetectBattery()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT BatteryStatus FROM Win32_Battery");
            foreach (var _ in searcher.Get())
            {
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static bool DetectPortable()
    {
        if (DetectBattery()) return true;

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
            foreach (var obj in searcher.Get())
            {
                if (obj["ChassisTypes"] is ushort[] types)
                {
                    if (types.Any(t => t is 8 or 9 or 10 or 14 or 31))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }
}
