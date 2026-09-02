using System.Management;

namespace KlarWin.Services;

public sealed record DiskSmartStatus(
    string Device,
    bool PredictFailure,
    string Detail,
    HealthLevel Level);

public static class DiskSmartHealthService
{
    public static IReadOnlyList<DiskSmartStatus> ReadAll()
    {
        var results = new List<DiskSmartStatus>();
        results.AddRange(ReadFailurePredictStatus());
        results.AddRange(ReadDiskDriveStatus());
        return Deduplicate(results);
    }

    private static List<DiskSmartStatus> ReadFailurePredictStatus()
    {
        var results = new List<DiskSmartStatus>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT InstanceName, PredictFailure, Reason FROM MSStorageDriver_FailurePredictStatus");
            foreach (var obj in searcher.Get())
            {
                var predict = obj["PredictFailure"] is bool b && b;
                var reason = obj["Reason"]?.ToString()?.Trim();
                var instance = obj["InstanceName"]?.ToString()?.Trim() ?? "Laufwerk";
                var label = SimplifyInstanceName(instance);
                var detail = predict
                    ? string.IsNullOrWhiteSpace(reason)
                        ? "SMART meldet Ausfallrisiko"
                        : $"SMART: {reason}"
                    : "SMART: kein Ausfallrisiko gemeldet";
                results.Add(new DiskSmartStatus(
                    label,
                    predict,
                    detail,
                    predict ? HealthLevel.Critical : HealthLevel.Ok));
            }
        }
        catch
        {
            // SMART WMI optional (Admin/Rechte)
        }

        return results;
    }

    private static List<DiskSmartStatus> ReadDiskDriveStatus()
    {
        var results = new List<DiskSmartStatus>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Model, Status, DeviceID FROM Win32_DiskDrive");
            foreach (var obj in searcher.Get())
            {
                var model = obj["Model"]?.ToString()?.Trim();
                var status = obj["Status"]?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(model)) continue;
                var ok = string.IsNullOrWhiteSpace(status)
                         || status.Equals("OK", StringComparison.OrdinalIgnoreCase);
                results.Add(new DiskSmartStatus(
                    model,
                    !ok,
                    ok ? "Status: OK" : $"Status: {status}",
                    ok ? HealthLevel.Ok : HealthLevel.Watch));
            }
        }
        catch
        {
            // optional
        }

        return results;
    }

    private static List<DiskSmartStatus> Deduplicate(IReadOnlyList<DiskSmartStatus> items)
    {
        var map = new Dictionary<string, DiskSmartStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (!map.TryGetValue(item.Device, out var existing)
                || item.Level > existing.Level)
            {
                map[item.Device] = item;
            }
        }

        return map.Values.OrderByDescending(d => d.Level).ToList();
    }

    private static string SimplifyInstanceName(string instance)
    {
        var slash = instance.LastIndexOf('\\');
        return slash >= 0 && slash < instance.Length - 1
            ? instance[(slash + 1)..]
            : instance;
    }
}
