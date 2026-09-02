namespace KlarWin.Services;

public sealed class LivePerformanceSample
{
    public PerformanceSnapshot Snapshot { get; init; } = new();
    public IReadOnlyList<TemperatureSensor> Temperatures { get; init; } = [];
    public double? PrimaryCpuTempCelsius { get; init; }
}

public sealed class PerformanceMonitor : IDisposable
{
    public MetricHistory CpuHistory { get; } = new(120);
    public MetricHistory RamHistory { get; } = new(120);
    public MetricHistory DiskUsedHistory { get; } = new(120);
    public MetricHistory CpuTempHistory { get; } = new(120);

    public PerformanceTrendStore TrendStore { get; } = new();

    private readonly Dictionary<string, MetricHistory> _tempHistories = new(StringComparer.OrdinalIgnoreCase);
    private readonly PerformanceService _performance = new();
    private readonly LibreHardwareTemperatureReader _hardwareTemps = new();

    public MetricHistory TempHistoryFor(string sensorId)
    {
        if (!_tempHistories.TryGetValue(sensorId, out var history))
        {
            history = new MetricHistory(120);
            _tempHistories[sensorId] = history;
        }

        return history;
    }

    public IReadOnlyDictionary<string, MetricHistory> TempHistories => _tempHistories;

    public LibreHardwareTemperatureReader HardwareTemperatureReader => _hardwareTemps;

    public LivePerformanceSample Sample()
    {
        var snapshot = _performance.Capture();
        var temperatures = TemperatureService.Read(_hardwareTemps);
        var cpuTemp = TemperatureService.PrimaryCpuCelsius(temperatures);

        CpuHistory.Add(snapshot.CpuPercent);
        RamHistory.Add(snapshot.RamPercent);
        DiskUsedHistory.Add(100.0 - snapshot.DiskFreePercent);
        if (cpuTemp is double temp)
        {
            CpuTempHistory.Add(temp);
        }

        TrendStore.MaybeRecord(snapshot.CpuPercent, snapshot.RamPercent, cpuTemp);

        foreach (var sensor in temperatures)
        {
            if (sensor.Celsius is double c && !string.IsNullOrWhiteSpace(sensor.Id))
            {
                TempHistoryFor(sensor.Id).Add(c);
            }
        }

        return new LivePerformanceSample
        {
            Snapshot = snapshot,
            Temperatures = temperatures,
            PrimaryCpuTempCelsius = cpuTemp
        };
    }

    public void Dispose()
    {
        _hardwareTemps.Dispose();
        _performance.Dispose();
    }
}
