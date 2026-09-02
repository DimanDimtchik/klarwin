using System.IO;
using System.Text.Json;

namespace KlarWin.Services;

public sealed record BatteryHistoryEntry(
    string Date,
    int CapacityPercent,
    int? ChargePercent,
    int? CycleCount);

public static class BatteryHistoryService
{
    private const int MaxEntries = 400;
    private static readonly object Gate = new();

    private static string HistoryPath =>
        Path.Combine(DesktopShortcutService.CanonicalInstallDirectory(), "battery-history.json");

    public static void Record(BatteryHealthInfo info)
    {
        if (!info.Present || info.WearPercent is not int capacity) return;

        lock (Gate)
        {
            var data = LoadUnsafe();
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var last = data.Entries.LastOrDefault();
            if (last is not null
                && last.Date == today
                && last.CapacityPercent == capacity
                && last.ChargePercent == info.ChargePercent
                && last.CycleCount == info.CycleCount)
            {
                return;
            }

            if (last is not null
                && last.Date == today)
            {
                data.Entries.RemoveAt(data.Entries.Count - 1);
            }

            data.Entries.Add(new BatteryHistoryEntry(today, capacity, info.ChargePercent, info.CycleCount));
            while (data.Entries.Count > MaxEntries)
            {
                data.Entries.RemoveAt(0);
            }

            SaveUnsafe(data);
        }
    }

    public static string? FormatTrendHint(int? currentCapacity)
    {
        if (currentCapacity is not int current) return null;

        BatteryHistoryEntry? baseline;
        lock (Gate)
        {
            var data = LoadUnsafe();
            baseline = data.Entries
                .Where(e => e.Date != DateTime.Now.ToString("yyyy-MM-dd"))
                .OrderByDescending(e => e.Date)
                .FirstOrDefault(e => (DateTime.Now - DateTime.Parse(e.Date)).TotalDays >= 7)
                ?? data.Entries.FirstOrDefault();
        }

        if (baseline is null || baseline.CapacityPercent == current) return null;

        var delta = current - baseline.CapacityPercent;
        var days = Math.Max(1, (int)(DateTime.Now - DateTime.Parse(baseline.Date)).TotalDays);
        var direction = delta switch
        {
            > 0 => $"+{delta}",
            < 0 => $"{delta}",
            _ => "±0"
        };
        return $"Verlauf: vor {days} Tag(en) {baseline.CapacityPercent} % → jetzt {current} % ({direction} Punkte).";
    }

    public static IReadOnlyList<BatteryHistoryEntry> ReadEntries()
    {
        lock (Gate)
        {
            return LoadUnsafe().Entries.ToList();
        }
    }

    private static BatteryHistoryFile LoadUnsafe()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return new BatteryHistoryFile();
            var json = File.ReadAllText(HistoryPath);
            return JsonSerializer.Deserialize<BatteryHistoryFile>(json) ?? new BatteryHistoryFile();
        }
        catch
        {
            return new BatteryHistoryFile();
        }
    }

    private static void SaveUnsafe(BatteryHistoryFile data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(HistoryPath, json);
    }

    private sealed class BatteryHistoryFile
    {
        public List<BatteryHistoryEntry> Entries { get; set; } = [];
    }
}
