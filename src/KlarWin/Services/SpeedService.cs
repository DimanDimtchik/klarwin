using System.Diagnostics;
using Microsoft.Win32;

namespace KlarWin.Services;

public sealed class SpeedOptions
{
    public string? PowerPlanGuid { get; init; }
    public bool HighPerformance { get; init; }
    public bool BalancedPlan { get; init; }
    public bool ScreenAcAlwaysOn { get; init; }
    public int? ScreenTimeoutAcMinutes { get; init; }
    public int? ScreenTimeoutDcMinutes { get; init; }
    public bool FlushDns { get; init; }
    public bool ReduceAllAnimations { get; init; }
    public bool DisableWindowAnimations { get; init; }
    public bool DisableTaskbarAnimations { get; init; }
    public bool DisablePeek { get; init; }
    public IReadOnlyList<string> DellChargingModes { get; init; } = [];
    public IReadOnlyList<string> DellThermalModes { get; init; } = [];
}

public sealed class SpeedResult
{
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed class SpeedService
{
    private const string HighPerformanceGuid = PowerPlanService.HighPerformanceGuid;
    private const string BalancedGuid = PowerPlanService.BalancedGuid;

    public static IReadOnlyList<(string Id, string Label, string Hint, bool Default)> MiscOptions { get; } =
    [
        ("screen-ac-never", "Bildschirm am Netzteil: immer an", "Verhindert automatisches Abschalten bei angeschlossenem Netzteil", false),
        ("dns", "Internet-Adressen aktualisieren", "Leert den DNS-Cache — hilft wenn Webseiten nicht laden (harmlos)", true),
        ("fx-all", "Alle Animationen reduzieren", "Windows wirkt schneller, weniger Übergangseffekte", false),
        ("fx-window", "Fenster-Animationen ausschalten", "Minimieren und Maximieren ohne Bewegung", false),
        ("fx-taskbar", "Taskleisten-Animationen ausschalten", "Weniger Bewegung in der Taskleiste", false),
        ("fx-peek", "Desktop-Vorschau (Peek) ausschalten", "Kein Durchschauen zum Desktop beim Hover", false)
    ];

    public static IEnumerable<(string Id, string Label, string Hint, bool Default)> BuildOptions(bool includeLaptopPower)
    {
        foreach (var plan in PowerPlanService.ListPlans())
        {
            yield return ($"plan-{plan.Guid}", $"Energieplan: {plan.Name}", plan.IsActive ? "Aktuell aktiv" : "Windows-Energieplan", false);
        }

        if (includeLaptopPower)
        {
            foreach (var option in LaptopPowerService.ChargingOptions)
            {
                yield return (option.Id, option.Label, option.Hint, false);
            }

            foreach (var option in LaptopPowerService.ThermalOptions)
            {
                yield return (option.Id, option.Label, option.Hint, false);
            }
        }

        foreach (var option in MiscOptions)
        {
            yield return option;
        }
    }

    public SpeedResult Apply(SpeedOptions options)
    {
        var notes = new List<string>();

        if (!string.IsNullOrWhiteSpace(options.PowerPlanGuid))
        {
            notes.Add(PowerPlanService.ActivatePlanDetailed(options.PowerPlanGuid).Message);
        }

        if (options.HighPerformance)
        {
            notes.Add(PowerPlanService.ActivatePlanDetailed(HighPerformanceGuid).Message);
        }

        if (options.BalancedPlan)
        {
            notes.Add(PowerPlanService.ActivatePlanDetailed(BalancedGuid).Message);
        }

        foreach (var charge in options.DellChargingModes)
        {
            var note = LaptopPowerService.ApplyChargingMode(charge);
            if (!string.IsNullOrWhiteSpace(note)) notes.Add(note);
        }

        foreach (var thermal in options.DellThermalModes)
        {
            var note = LaptopPowerService.ApplyThermalMode(thermal);
            if (!string.IsNullOrWhiteSpace(note)) notes.Add(note);
        }

        if (options.ScreenAcAlwaysOn) SetScreenTimeout(acMinutes: 0, dcMinutes: null, notes);
        if (options.ScreenTimeoutAcMinutes is int ac) SetScreenTimeout(acMinutes: ac, dcMinutes: null, notes);
        if (options.ScreenTimeoutDcMinutes is int dc) SetScreenTimeout(acMinutes: null, dcMinutes: dc, notes);
        if (options.FlushDns) FlushDns(notes);
        if (options.ReduceAllAnimations) ReduceAllAnimations(notes);
        if (options.DisableWindowAnimations) SetAdvanced("WindowAnimations", 0, notes, "Fenster-Animationen aus.");
        if (options.DisableTaskbarAnimations) SetAdvanced("TaskbarAnimations", 0, notes, "Taskleisten-Animationen aus.");
        if (options.DisablePeek) SetAdvanced("DisablePreviewDesktop", 1, notes, "Desktop-Vorschau aus.");
        if (notes.Count == 0) notes.Add("Nichts ausgewählt.");
        return new SpeedResult { Notes = notes };
    }

    private static void SetScreenTimeout(int? acMinutes, int? dcMinutes, List<string> notes)
    {
        if (acMinutes is int ac)
        {
            var code = Run("powercfg", $"/change monitor-timeout-ac {ac}");
            notes.Add(code == 0
                ? ac == 0
                    ? "Bildschirm am Netzteil: bleibt an."
                    : $"Bildschirm am Netzteil: schaltet nach {ac} Min. ab."
                : "Bildschirm-Timeout (Netzteil) konnte nicht gesetzt werden — ggf. als Administrator starten.");
        }

        if (dcMinutes is int dc)
        {
            var code = Run("powercfg", $"/change monitor-timeout-dc {dc}");
            notes.Add(code == 0
                ? dc == 0
                    ? "Bildschirm im Akkubetrieb: bleibt an."
                    : $"Bildschirm im Akkubetrieb: schaltet nach {dc} Min. ab."
                : "Bildschirm-Timeout (Akku) konnte nicht gesetzt werden — ggf. als Administrator starten.");
        }
    }

    private static void FlushDns(List<string> notes)
    {
        var code = Run("ipconfig", "/flushdns");
        notes.Add(code == 0
            ? "Internet-Adressen (DNS-Cache) aktualisiert."
            : "DNS-Cache konnte nicht geleert werden.");
    }

    private static void ReduceAllAnimations(List<string> notes)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", true)
                ?? Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects");
            key.SetValue("VisualFXSetting", 2, RegistryValueKind.DWord);
            notes.Add("Visuelle Effekte auf „Beste Leistung“ gestellt. Einmal abmelden für volle Wirkung.");
        }
        catch (Exception ex)
        {
            notes.Add($"Animationen: {UserFriendlyErrors.Translate(ex)}");
        }
    }

    private static void SetAdvanced(string valueName, int value, List<string> notes, string success)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", true)
                ?? Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            key.SetValue(valueName, value, RegistryValueKind.DWord);
            notes.Add(success);
        }
        catch (Exception ex)
        {
            notes.Add(UserFriendlyErrors.Translate(ex));
        }
    }

    private static int Run(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        });
        if (process is null) return 1;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(8000);
        return process.ExitCode == 0 ? 0 : process.ExitCode;
    }

    private static string RunCaptureOutput(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            });
            if (process is null) return "";
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(8000);
            return output;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Liest Bildschirm-Timeout in Minuten (0 = nie aus).</summary>
    public static (int? AcMinutes, int? DcMinutes) GetCurrentScreenTimeouts()
    {
        var output = RunCaptureOutput("powercfg", "/query SCHEME_CURRENT SUB_VIDEO VIDEOIDLE");
        if (string.IsNullOrWhiteSpace(output)) return (null, null);

        int? ac = null;
        int? dc = null;
        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Contains("Wechselstrom", StringComparison.OrdinalIgnoreCase)
                || line.Contains("AC setting", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Wechselstromeinstellung", StringComparison.OrdinalIgnoreCase))
            {
                ac = ParsePowerCfgSecondsLine(line);
            }
            else if (line.Contains("Gleichstrom", StringComparison.OrdinalIgnoreCase)
                     || line.Contains("DC setting", StringComparison.OrdinalIgnoreCase)
                     || line.Contains("Gleichstromeinstellung", StringComparison.OrdinalIgnoreCase))
            {
                dc = ParsePowerCfgSecondsLine(line);
            }
        }

        return (ac, dc);
    }

    private static int? ParsePowerCfgSecondsLine(string line)
    {
        var hexIndex = line.LastIndexOf("0x", StringComparison.OrdinalIgnoreCase);
        if (hexIndex < 0) return null;
        var hex = line[(hexIndex + 2)..].Trim();
        if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var seconds)) return null;
        if (seconds == 0) return 0;
        return Math.Max(1, (int)Math.Round(seconds / 60.0));
    }
}
