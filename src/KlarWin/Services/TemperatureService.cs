using System.Management;

namespace KlarWin.Services;

public sealed class TemperatureSensor
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public double? Celsius { get; init; }
}

public static class TemperatureService
{
    public static IReadOnlyList<TemperatureSensor> Read(LibreHardwareTemperatureReader? hardwareReader = null)
    {
        var sensors = new List<TemperatureSensor>();
        Merge(sensors, NvidiaSmiReader.Read());
        var lhm = hardwareReader?.Read() ?? [];
        if (sensors.Any(s => s.Id.StartsWith("nvidia-smi", StringComparison.OrdinalIgnoreCase)))
        {
            lhm = lhm.Where(s => !s.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        Merge(sensors, lhm);
        ReadAcpiZones(sensors);
        ReadPerfCounters(sensors);
        ReadHardwareMonitorWmi(sensors, @"root\OpenHardwareMonitor");
        ReadHardwareMonitorWmi(sensors, @"root\LibreHardwareMonitor");
        ReadTemperatureProbes(sensors);
        return sensors;
    }

    public static IReadOnlyList<TemperatureSensor> ProbeChannel(TemperatureProbeChannel channel)
    {
        var sensors = new List<TemperatureSensor>();
        switch (channel)
        {
            case TemperatureProbeChannel.NvidiaSmi:
                Merge(sensors, NvidiaSmiReader.Read());
                break;
            case TemperatureProbeChannel.AcpiThermalZone:
                ReadAcpiZones(sensors);
                break;
            case TemperatureProbeChannel.PerfCounterThermal:
                ReadPerfCounters(sensors);
                break;
            case TemperatureProbeChannel.OpenHardwareMonitorWmi:
                ReadHardwareMonitorWmi(sensors, @"root\OpenHardwareMonitor");
                break;
            case TemperatureProbeChannel.LibreHardwareMonitorWmi:
                ReadHardwareMonitorWmi(sensors, @"root\LibreHardwareMonitor");
                break;
            case TemperatureProbeChannel.Win32TemperatureProbe:
                ReadTemperatureProbes(sensors);
                break;
        }

        return sensors;
    }

    public static double? PrimaryCpuCelsius(IReadOnlyList<TemperatureSensor> sensors)
    {
        foreach (var sensor in sensors)
        {
            if (sensor.Celsius is not double c) continue;
            if (sensor.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)
                || sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                || sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)
                || sensor.Name.Contains("Thermal", StringComparison.OrdinalIgnoreCase))
            {
                return c;
            }
        }

        return sensors.FirstOrDefault(s => s.Celsius is double)?.Celsius;
    }

    public static string FormatUnavailableMessage(bool pawnIoInstalled = false)
    {
        var pawnHint = pawnIoInstalled
            ? ""
            : "\n\nFür CPU/Mainboard: Button „PawnIO installieren“ in der Leistungs-Ansicht — danach KlarWin neu starten.";

        return
            "Keine Temperaturwerte verfügbar.\n\n" +
            "KlarWin nutzt LibreHardwareMonitorLib 0.9.6 (PawnIO), nvidia-smi (GPU) und Windows-WMI. " +
            "„Temperatur-Diagnose“ zeigt, welche Quellen auf diesem PC funktionieren." +
            pawnHint +
            "\n\nGrün = ok · Gelb = beobachten · Rot = auffällig";
    }

    private static void Merge(List<TemperatureSensor> target, IReadOnlyList<TemperatureSensor> source)
    {
        foreach (var sensor in source)
        {
            if (string.IsNullOrWhiteSpace(sensor.Id)) continue;
            if (target.Any(s => s.Id.Equals(sensor.Id, StringComparison.OrdinalIgnoreCase))) continue;
            target.Add(sensor);
        }
    }

    private static void AddSensor(List<TemperatureSensor> sensors, string id, string name, double celsius)
    {
        if (sensors.Any(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) return;
        sensors.Add(new TemperatureSensor
        {
            Id = id,
            Name = name,
            Celsius = Math.Round(celsius, 1)
        });
    }

    private static void ReadAcpiZones(List<TemperatureSensor> sensors)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM MSAcpi_ThermalZoneTemperature");
            var index = 0;
            foreach (var obj in searcher.Get())
            {
                if (obj["CurrentTemperature"] is not uint raw || raw == 0) continue;
                var celsius = raw / 10.0 - 273.15;
                if (celsius is < -40 or > 150) continue;
                AddSensor(sensors, $"acpi-{index}", index == 0 ? "Thermalzone (ACPI)" : $"Thermalzone {index + 1}", celsius);
                index++;
            }
        }
        catch
        {
            // WMI optional
        }
    }

    private static void ReadPerfCounters(List<TemperatureSensor> sensors)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, CurrentTemperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");
            foreach (var obj in searcher.Get())
            {
                if (obj["CurrentTemperature"] is not uint raw || raw == 0) continue;
                var celsius = raw / 10.0 - 273.15;
                if (celsius is < -40 or > 150) continue;
                var name = obj["Name"]?.ToString()?.Trim();
                var label = string.IsNullOrWhiteSpace(name) ? "Thermalzone (Performance)" : name;
                AddSensor(sensors, $"perf-{label}", label, celsius);
            }
        }
        catch
        {
            // optional
        }
    }

    private static void ReadHardwareMonitorWmi(List<TemperatureSensor> sensors, string wmiNamespace)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                wmiNamespace,
                "SELECT Name, Value, Parent, Identifier FROM Sensor WHERE SensorType='Temperature'");
            foreach (var obj in searcher.Get())
            {
                var celsius = obj["Value"] switch
                {
                    float f => (double)f,
                    double d => d,
                    int i => i,
                    uint u => u,
                    short s => s,
                    _ => double.NaN
                };

                if (double.IsNaN(celsius) || celsius is < -40 or > 150) continue;
                var parent = obj["Parent"]?.ToString()?.Trim();
                var name = obj["Name"]?.ToString()?.Trim() ?? "Temperatur";
                var label = string.IsNullOrWhiteSpace(parent) ? name : $"{parent} — {name}";
                var idRaw = obj["Identifier"]?.ToString()?.Trim();
                var id = string.IsNullOrWhiteSpace(idRaw)
                    ? $"{wmiNamespace}|{label}"
                    : $"{wmiNamespace}|{idRaw}";
                AddSensor(sensors, id, label, celsius);
            }
        }
        catch
        {
            // Monitor not running or WMI disabled
        }
    }

    private static void ReadTemperatureProbes(List<TemperatureSensor> sensors)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, CurrentReading, Description FROM Win32_TemperatureProbe");
            foreach (var obj in searcher.Get())
            {
                if (obj["CurrentReading"] is not short raw || raw <= 0) continue;
                var celsius = raw / 10.0;
                if (celsius is < -40 or > 150) continue;
                var name = obj["Description"]?.ToString()?.Trim()
                           ?? obj["Name"]?.ToString()?.Trim()
                           ?? "Temperatursonde";
                AddSensor(sensors, $"probe-{name}", name, celsius);
            }
        }
        catch
        {
            // optional
        }
    }
}
