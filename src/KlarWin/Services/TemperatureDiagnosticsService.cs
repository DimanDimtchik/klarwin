using System.Text;

namespace KlarWin.Services;

public enum TemperatureProbeChannel
{
    PawnIoDriver,
    LibreHardwareMonitorLib,
    NvidiaSmi,
    AcpiThermalZone,
    PerfCounterThermal,
    OpenHardwareMonitorWmi,
    LibreHardwareMonitorWmi,
    Win32TemperatureProbe
}

public sealed record TemperatureSourceProbe(
    TemperatureProbeChannel Channel,
    string DisplayName,
    bool Available,
    int SensorCount,
    string Status,
    IReadOnlyList<string> Samples,
    bool HasCpuOrMotherboard = false);

public static class TemperatureDiagnosticsService
{
    public static IReadOnlyList<TemperatureSourceProbe> ProbeAll(LibreHardwareTemperatureReader? hardwareReader = null)
    {
        return
        [
            ProbePawnIo(),
            ProbeLibreHardware(hardwareReader),
            ProbeChannel(TemperatureProbeChannel.NvidiaSmi, "nvidia-smi (GPU)"),
            ProbeChannel(TemperatureProbeChannel.AcpiThermalZone, "ACPI Thermalzone (WMI)"),
            ProbeChannel(TemperatureProbeChannel.PerfCounterThermal, "Performance Counter Thermal"),
            ProbeChannel(TemperatureProbeChannel.OpenHardwareMonitorWmi, "Open Hardware Monitor (WMI)"),
            ProbeChannel(TemperatureProbeChannel.LibreHardwareMonitorWmi, "LibreHardwareMonitor (WMI)"),
            ProbeChannel(TemperatureProbeChannel.Win32TemperatureProbe, "Win32_TemperatureProbe")
        ];
    }

    public static string FormatReport(IReadOnlyList<TemperatureSourceProbe> probes, bool isAdministrator)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Temperatur-Diagnose — welche Quellen auf diesem PC Daten liefern:");
        sb.AppendLine();
        sb.AppendLine($"Administrator: {(isAdministrator ? "ja" : "nein")} · LibreHardwareMonitorLib 0.9.6 (PawnIO-Unterstützung)");
        if (!isAdministrator && probes.First(p => p.Channel == TemperatureProbeChannel.PawnIoDriver).Available)
        {
            sb.AppendLine("Hinweis: Mit „Adminrechte freischalten“ werden oft deutlich mehr CPU/Mainboard-Sensoren sichtbar.");
        }

        sb.AppendLine();

        foreach (var probe in probes)
        {
            var marker = probe.Available ? "✓" : "○";
            sb.AppendLine($"{marker} {probe.DisplayName}");
            sb.AppendLine($"   {probe.Status}");
            foreach (var sample in probe.Samples.Take(6))
            {
                sb.AppendLine($"   · {sample}");
            }

            if (probe.Samples.Count > 6)
            {
                sb.AppendLine($"   · … +{probe.Samples.Count - 6} weitere");
            }

            sb.AppendLine();
        }

        AppendRecommendations(sb, probes, isAdministrator);
        return sb.ToString().TrimEnd();
    }

    public static string BuildCompactHint(IReadOnlyList<TemperatureSensor> sensors, LibreHardwareTemperatureReader hardwareReader)
    {
        var hasCpuOrBoard = sensors.Any(s =>
            s.Id.StartsWith("lhm-", StringComparison.OrdinalIgnoreCase)
            && (s.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)
                || s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                || s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)
                || s.Name.Contains("Mainboard", StringComparison.OrdinalIgnoreCase)
                || s.Name.Contains("Motherboard", StringComparison.OrdinalIgnoreCase)
                || s.Name.Contains("System", StringComparison.OrdinalIgnoreCase)
                || s.Name.Contains("Thermalzone", StringComparison.OrdinalIgnoreCase)
                || s.Id.StartsWith("acpi-", StringComparison.OrdinalIgnoreCase)
                || s.Id.StartsWith("perf-", StringComparison.OrdinalIgnoreCase)));

        if (hasCpuOrBoard)
        {
            return "";
        }

        if (!LibreHardwareTemperatureReader.IsPawnIoInstalled)
        {
            return "CPU/Mainboard-Temperatur fehlt. Button „PawnIO installieren“ (oben) — danach KlarWin neu starten. "
                   + "Details unter „Temperatur-Diagnose“.";
        }

        if (hardwareReader.OpenFailed)
        {
            return "LibreHardwareMonitorLib konnte Hardware nicht öffnen — „Adminrechte freischalten“ und KlarWin neu starten, "
                   + "oder „Temperatur-Diagnose“ prüfen.";
        }

        if (hardwareReader.LastSensorCount == 0)
        {
            return "Keine CPU/Mainboard-Sensoren erkannt — Mainboard evtl. nicht unterstützt. "
                   + "„Temperatur-Diagnose“ zeigt, welche Quellen verfügbar sind.";
        }

        return "";
    }

    private static TemperatureSourceProbe ProbePawnIo()
    {
        try
        {
            if (!LibreHardwareTemperatureReader.IsPawnIoInstalled)
            {
                return new TemperatureSourceProbe(
                    TemperatureProbeChannel.PawnIoDriver,
                    "PawnIO-Treiber",
                    false,
                    0,
                    "Nicht installiert — für CPU/Mainboard unter Windows 10/11 oft nötig. "
                    + "In KlarWin: „PawnIO installieren“ (oben) oder https://pawnio.eu",
                    []);
            }

            var version = LibreHardwareTemperatureReader.PawnIoVersionText();
            var status = $"Installiert ({version}).";

            return new TemperatureSourceProbe(
                TemperatureProbeChannel.PawnIoDriver,
                "PawnIO-Treiber",
                true,
                0,
                status,
                []);
        }
        catch (Exception ex)
        {
            return new TemperatureSourceProbe(
                TemperatureProbeChannel.PawnIoDriver,
                "PawnIO-Treiber",
                false,
                0,
                UserFriendlyErrors.Translate(ex),
                []);
        }
    }

    private static TemperatureSourceProbe ProbeLibreHardware(LibreHardwareTemperatureReader? hardwareReader)
    {
        if (hardwareReader is null)
        {
            return new TemperatureSourceProbe(
                TemperatureProbeChannel.LibreHardwareMonitorLib,
                "LibreHardwareMonitorLib (CPU/GPU/Mainboard)",
                false,
                0,
                "Interner Reader nicht verfügbar.",
                []);
        }

        if (hardwareReader.OpenFailed)
        {
            return new TemperatureSourceProbe(
                TemperatureProbeChannel.LibreHardwareMonitorLib,
                "LibreHardwareMonitorLib (CPU/GPU/Mainboard)",
                false,
                0,
                "Hardware-Zugriff fehlgeschlagen — Adminrechte oder PawnIO prüfen.",
                []);
        }

        var sensors = hardwareReader.Read().Where(s => s.Id.StartsWith("lhm-", StringComparison.OrdinalIgnoreCase)).ToList();
        var samples = sensors
            .Where(s => s.Celsius is double)
            .Select(s => $"{s.Name}: {s.Celsius:0} °C")
            .ToList();

        var cpuMb = hardwareReader.HasCpuOrMotherboardSensor;
        var status = sensors.Count == 0
            ? "Keine Temperatur-Sensoren — PawnIO installieren oder Adminrechte testen."
            : cpuMb
                ? $"{sensors.Count} Sensor(en), inkl. CPU/Mainboard."
                : $"{sensors.Count} Sensor(en), nur GPU/Chip — keine CPU/Mainboard-Werte.";

        return new TemperatureSourceProbe(
            TemperatureProbeChannel.LibreHardwareMonitorLib,
            "LibreHardwareMonitorLib (CPU/GPU/Mainboard)",
            sensors.Count > 0,
            sensors.Count,
            status,
            samples,
            cpuMb);
    }

    private static TemperatureSourceProbe ProbeChannel(TemperatureProbeChannel channel, string displayName)
    {
        var sensors = TemperatureService.ProbeChannel(channel);
        var samples = sensors
            .Where(s => s.Celsius is double)
            .Select(s => $"{s.Name}: {s.Celsius:0} °C")
            .ToList();

        var status = sensors.Count switch
        {
            0 => channel switch
            {
                TemperatureProbeChannel.OpenHardwareMonitorWmi =>
                    "WMI-Namespace nicht erreichbar — OHM im Hintergrund mit WMI aktivieren.",
                TemperatureProbeChannel.LibreHardwareMonitorWmi =>
                    "WMI-Namespace nicht erreichbar — LibreHardwareMonitor-App mit „WMI Provider“ starten.",
                TemperatureProbeChannel.NvidiaSmi =>
                    "nvidia-smi nicht gefunden oder keine NVIDIA-GPU.",
                _ => "Keine Werte von Windows/Hardware gemeldet."
            },
            _ => $"{sensors.Count} Sensor(en) gefunden."
        };

        return new TemperatureSourceProbe(channel, displayName, sensors.Count > 0, sensors.Count, status, samples);
    }

    private static void AppendRecommendations(StringBuilder sb, IReadOnlyList<TemperatureSourceProbe> probes, bool isAdministrator)
    {
        var pawn = probes.First(p => p.Channel == TemperatureProbeChannel.PawnIoDriver);
        var lhm = probes.First(p => p.Channel == TemperatureProbeChannel.LibreHardwareMonitorLib);
        var hasGpuOnly = probes.Any(p =>
            p.Channel is TemperatureProbeChannel.NvidiaSmi or TemperatureProbeChannel.LibreHardwareMonitorLib
            && p.Available)
            && !lhm.HasCpuOrMotherboard;

        sb.AppendLine("Empfehlung:");
        if (!pawn.Available && hasGpuOnly)
        {
            sb.AppendLine("· GPU-Temperaturen funktionieren. Für CPU/Mainboard: „PawnIO installieren“ (oben), danach KlarWin neu starten.");
            if (!isAdministrator)
            {
                sb.AppendLine("· Zusätzlich „Adminrechte freischalten“ testen, falls danach noch Sensoren fehlen.");
            }

            return;
        }

        if (!pawn.Available && lhm.SensorCount == 0)
        {
            sb.AppendLine("· Keine Hardware-Sensoren — „PawnIO installieren“ (oben) oder https://pawnio.eu, dann KlarWin neu starten.");
            if (!isAdministrator)
            {
                sb.AppendLine("· „Adminrechte freischalten“ kann auf manchen PCs zusätzlich helfen.");
            }

            return;
        }

        if (lhm.HasCpuOrMotherboard)
        {
            sb.AppendLine("· CPU/Mainboard-Sensoren werden über LibreHardwareMonitorLib gelesen.");
            return;
        }

        sb.AppendLine("· KlarWin nutzt die verfügbaren Quellen oben. „Temperatur-Diagnose“ bei Änderungen erneut öffnen.");
    }
}
