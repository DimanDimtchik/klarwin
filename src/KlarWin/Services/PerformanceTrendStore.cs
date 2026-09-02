using System.IO;
using System.Text.Json;

namespace KlarWin.Services;

public sealed record PerformanceTrendPoint(
    long UnixUtc,
    double CpuPercent,
    double RamPercent,
    double? CpuTempCelsius);

public sealed class PerformanceTrendStore
{
    public const int MaxPoints = 1440; // 1/min × 24 h
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(1);

    private readonly List<PerformanceTrendPoint> _points = [];
    private DateTime _lastSampleUtc = DateTime.MinValue;
    private readonly object _gate = new();
    private readonly string _path;

    public PerformanceTrendStore()
    {
        _path = Path.Combine(DesktopShortcutService.CanonicalInstallDirectory(), "performance-trend.json");
        Load();
    }

    public IReadOnlyList<PerformanceTrendPoint> Points
    {
        get
        {
            lock (_gate)
            {
                return _points.ToList();
            }
        }
    }

    public void MaybeRecord(double cpuPercent, double ramPercent, double? cpuTempCelsius)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (now - _lastSampleUtc < SampleInterval && _points.Count > 0) return;
            _lastSampleUtc = now;
            _points.Add(new PerformanceTrendPoint(
                new DateTimeOffset(now).ToUnixTimeSeconds(),
                Math.Clamp(cpuPercent, 0, 100),
                Math.Clamp(ramPercent, 0, 100),
                cpuTempCelsius));

            while (_points.Count > MaxPoints)
            {
                _points.RemoveAt(0);
            }

            SaveUnsafe();
        }
    }

    public IReadOnlyList<double> CpuSeries() => Series(static p => p.CpuPercent);

    public IReadOnlyList<double> RamSeries() => Series(static p => p.RamPercent);

    public IReadOnlyList<double> CpuTempSeries() =>
        Points.Where(p => p.CpuTempCelsius is double).Select(p => p.CpuTempCelsius!.Value).ToList();

    private IReadOnlyList<double> Series(Func<PerformanceTrendPoint, double> selector)
    {
        lock (_gate)
        {
            return _points.Select(selector).ToList();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<List<PerformanceTrendPoint>>(json);
            if (loaded is null) return;
            lock (_gate)
            {
                _points.Clear();
                _points.AddRange(loaded.OrderBy(p => p.UnixUtc).TakeLast(MaxPoints));
                if (_points.Count > 0)
                {
                    _lastSampleUtc = DateTimeOffset.FromUnixTimeSeconds(_points[^1].UnixUtc).UtcDateTime;
                }
            }
        }
        catch
        {
            // start fresh
        }
    }

    private void SaveUnsafe()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(_points, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(_path, json);
        }
        catch
        {
            // ignore persistence errors
        }
    }
}
