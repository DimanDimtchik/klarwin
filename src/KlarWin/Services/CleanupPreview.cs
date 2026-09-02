using System.IO;

namespace KlarWin.Services;

public sealed class CleanupPreviewItem
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Hint { get; init; } = "";
    public long Bytes { get; init; }
    public int Files { get; init; }
    public bool DefaultSelected { get; init; } = true;
}

public sealed class CleanupPreview
{
    public IReadOnlyList<CleanupPreviewItem> Items { get; init; } = [];
    public long TotalBytes => Items.Sum(i => i.Bytes);
    public int TotalFiles => Items.Sum(i => i.Files);
}

public sealed class CleanupSelection
{
    public bool UserTemp { get; init; }
    public bool WindowsTemp { get; init; }
    public bool DeliveryCache { get; init; }
    public bool RecycleBin { get; init; }
    public bool UserDownloads { get; init; }

    public static CleanupSelection FromIds(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new CleanupSelection
        {
            UserTemp = set.Contains("user-temp"),
            WindowsTemp = set.Contains("windows-temp"),
            DeliveryCache = set.Contains("delivery-cache"),
            RecycleBin = set.Contains("recycle-bin"),
            UserDownloads = set.Contains("user-downloads")
        };
    }
}

public sealed partial class CleanupService
{
    public static IReadOnlyList<CleanupPreviewItem> AllCategories { get; } =
    [
        new() { Id = "user-temp", Label = "Benutzer-Temp", Hint = "%TEMP% — oft Cache von Programmen", DefaultSelected = true },
        new() { Id = "windows-temp", Label = "Windows-Temp", Hint = "C:\\Windows\\Temp (System)", DefaultSelected = false },
        new() { Id = "delivery-cache", Label = "Download-Cache", Hint = "Windows Update / Delivery Optimization", DefaultSelected = false },
        new() { Id = "recycle-bin", Label = "Papierkorb leeren", Hint = "Alles im Papierkorb endgültig entfernen", DefaultSelected = true },
        new() { Id = "user-downloads", Label = "Download-Ordner", Hint = "Downloads\\ — Installer, Bilder, ZIPs (nur markierte löschen)", DefaultSelected = false }
    ];

    public CleanupPreview Preview()
    {
        var items = new List<CleanupPreviewItem>();
        foreach (var cat in AllCategories)
        {
            var (bytes, files) = cat.Id switch
            {
                "user-temp" => SafeFileSystem.MeasureDirectory(Path.GetTempPath(), maxDepth: 3),
                "windows-temp" => SafeFileSystem.MeasureDirectory(WindowsTempPath()),
                "delivery-cache" => SafeFileSystem.MeasureDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "DeliveryOptimization", "Cache")),
                "recycle-bin" => RecycleBinService.MeasureContent(),
                "user-downloads" => SafeFileSystem.MeasureDirectory(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                    maxDepth: 2),
                _ => (0L, 0)
            };
            var hint = cat.Id switch
            {
                "recycle-bin" => RecycleBinService.BuildHint(),
                "windows-temp" => BuildWindowsTempHint(bytes, files),
                _ => cat.Hint
            };
            items.Add(new CleanupPreviewItem
            {
                Id = cat.Id,
                Label = cat.Label,
                Hint = hint,
                Bytes = bytes,
                Files = files,
                DefaultSelected = cat.DefaultSelected
            });
        }

        return new CleanupPreview { Items = items };
    }

    private static string WindowsTempPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");

    private static string BuildWindowsTempHint(long bytes, int files)
    {
        var path = WindowsTempPath();
        if (!Directory.Exists(path))
        {
            return "C:\\Windows\\Temp — Ordner nicht gefunden.";
        }

        if (files == 0 && bytes == 0 && !SafeFileSystem.CanReadDirectory(path))
        {
            return "C:\\Windows\\Temp — Windows blockiert den Zugriff ohne Administrator. " +
                   "Oben rechts „Adminrechte freischalten“, dann „Vorschau aktualisieren“.";
        }

        if (files == 0 && bytes == 0)
        {
            return "C:\\Windows\\Temp — derzeit leer oder nicht lesbar.";
        }

        return "C:\\Windows\\Temp (System) — nur sichtbar, was Windows zulässt.";
    }

    [Obsolete("Use ByteSizeFormatter.Format")]
    public static string FormatBytes(long bytes) => ByteSizeFormatter.Format(bytes);
}
