using System.IO;
using System.Text.Json;

namespace KlarWin.Services;

/// <summary>
/// Remembers software updates that failed for lack of elevation, so KlarWin can
/// continue them after a UAC relaunch as administrator.
/// </summary>
public static class PendingSoftwareUpdateResume
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KlarWin",
            "pending-software-updates.json");

    public sealed class PendingBatch
    {
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public List<PendingItem> Items { get; set; } = [];
    }

    public sealed class PendingItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string CurrentVersion { get; set; } = "";
        public string AvailableVersion { get; set; } = "";
        public string Kind { get; set; } = "Programm";
        public string Source { get; set; } = "winget";
        public string PackageSource { get; set; } = "winget";
    }

    public static void Save(IEnumerable<SoftwareUpdateItem> items)
    {
        var list = items
            .Where(i => !string.IsNullOrWhiteSpace(i.Id))
            .Select(i => new PendingItem
            {
                Id = i.Id,
                Name = i.Name,
                CurrentVersion = i.CurrentVersion,
                AvailableVersion = i.AvailableVersion,
                Kind = i.Kind,
                Source = i.Source,
                PackageSource = i.PackageSource
            })
            .ToList();

        if (list.Count == 0)
        {
            Clear();
            return;
        }

        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        var batch = new PendingBatch { CreatedUtc = DateTime.UtcNow, Items = list };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(batch, JsonOptions));
    }

    public static IReadOnlyList<SoftwareUpdateItem>? TryTake()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var json = File.ReadAllText(FilePath);
            Clear();
            var batch = JsonSerializer.Deserialize<PendingBatch>(json);
            if (batch?.Items is not { Count: > 0 }) return null;

            // Ignore stale leftovers (> 24 h)
            if (batch.CreatedUtc < DateTime.UtcNow.AddHours(-24))
                return null;

            return batch.Items
                .Where(i => !string.IsNullOrWhiteSpace(i.Id))
                .Select(i => new SoftwareUpdateItem
                {
                    Id = i.Id,
                    Name = string.IsNullOrWhiteSpace(i.Name) ? i.Id : i.Name,
                    CurrentVersion = i.CurrentVersion,
                    AvailableVersion = i.AvailableVersion,
                    Kind = string.IsNullOrWhiteSpace(i.Kind) ? "Programm" : i.Kind,
                    Source = string.IsNullOrWhiteSpace(i.Source) ? "winget" : i.Source,
                    PackageSource = string.IsNullOrWhiteSpace(i.PackageSource) ? "winget" : i.PackageSource
                })
                .ToList();
        }
        catch
        {
            Clear();
            return null;
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch
        {
            // ignore
        }
    }
}
