using LibreHardwareMonitor.Hardware;
using LhmPawnIo = LibreHardwareMonitor.PawnIo.PawnIo;

namespace KlarWin.Services;

/// <summary>
/// Reads CPU/GPU/motherboard temperatures via LibreHardwareMonitorLib (same stack as HWiNFO/HWMonitor).
/// </summary>
public sealed class LibreHardwareTemperatureReader : IDisposable
{
    private Computer? _computer;
    private bool _openFailed;

    public bool OpenFailed => _openFailed;
    public int LastSensorCount { get; private set; }
    public bool HasCpuOrMotherboardSensor { get; private set; }

    public static bool IsPawnIoInstalled
    {
        get
        {
            try
            {
                return LhmPawnIo.IsInstalled;
            }
            catch
            {
                return false;
            }
        }
    }

    public static string PawnIoVersionText()
    {
        try
        {
            var version = LhmPawnIo.Version;
            return version?.ToString() ?? "Version unbekannt";
        }
        catch
        {
            return "Version unbekannt";
        }
    }

    public IReadOnlyList<TemperatureSensor> Read()
    {
        if (_openFailed)
        {
            LastSensorCount = 0;
            HasCpuOrMotherboardSensor = false;
            return [];
        }

        try
        {
            EnsureOpen();
            if (_computer is null)
            {
                LastSensorCount = 0;
                HasCpuOrMotherboardSensor = false;
                return [];
            }

            _computer.Accept(new UpdateVisitor());
            var sensors = new List<TemperatureSensor>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visitedHardware = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasCpuOrBoard = false;
            foreach (var hardware in _computer.Hardware)
            {
                CollectHardware(hardware, sensors, seen, visitedHardware, ref hasCpuOrBoard);
            }

            LastSensorCount = sensors.Count;
            HasCpuOrMotherboardSensor = hasCpuOrBoard;
            return sensors;
        }
        catch
        {
            _openFailed = true;
            LastSensorCount = 0;
            HasCpuOrMotherboardSensor = false;
            return [];
        }
    }

    private void EnsureOpen()
    {
        if (_computer is not null || _openFailed)
        {
            return;
        }

        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsMemoryEnabled = false,
            IsStorageEnabled = false,
            IsNetworkEnabled = false,
            IsPsuEnabled = false
        };
        _computer.Open();
    }

    private static void CollectHardware(
        IHardware hardware,
        List<TemperatureSensor> sensors,
        HashSet<string> seen,
        HashSet<string> visitedHardware,
        ref bool hasCpuOrBoard)
    {
        var hardwareId = hardware.Identifier.ToString();
        if (!visitedHardware.Add(hardwareId)) return;

        foreach (var sub in hardware.SubHardware)
        {
            CollectHardware(sub, sensors, seen, visitedHardware, ref hasCpuOrBoard);
        }

        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType != SensorType.Temperature) continue;
            if (sensor.Value is not float value || value is < -40 or > 150) continue;

            var id = $"lhm-{hardware.Identifier}-{sensor.Index}";
            if (!seen.Add(id)) continue;

            var shortName = string.IsNullOrWhiteSpace(sensor.Name) ? "Temperatur" : sensor.Name;
            var hardwareName = hardware.Name.Trim();
            var label = hardwareName.Equals(shortName, StringComparison.OrdinalIgnoreCase)
                ? hardwareName
                : $"{hardwareName} — {shortName}";

            sensors.Add(new TemperatureSensor
            {
                Id = id,
                Name = label,
                Celsius = Math.Round(value, 1)
            });

            if (hardware.HardwareType is HardwareType.Cpu or HardwareType.Motherboard)
            {
                hasCpuOrBoard = true;
            }
        }
    }

    public void Dispose()
    {
        if (_computer is null) return;
        try
        {
            _computer.Close();
        }
        catch
        {
            // ignore shutdown errors
        }

        _computer = null;
    }

    private sealed class UpdateVisitor : IVisitor
    {
        private readonly HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);

        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            var id = hardware.Identifier.ToString();
            if (!_visited.Add(id)) return;

            hardware.Update();
            foreach (var sub in hardware.SubHardware)
            {
                sub.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor) { }

        public void VisitParameter(IParameter parameter) { }
    }
}
