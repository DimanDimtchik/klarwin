using System.IO;

namespace KlarWin.Services;

public sealed class CleanupPreviewItem
{
    public string Label { get; init; } = "";
    public long Bytes { get; init; }
    public int Files { get; init; }
}

public sealed class CleanupPreview
{
    public IReadOnlyList<CleanupPreviewItem> Items { get; init; } = [];
    public long TotalBytes => Items.Sum(i => i.Bytes);
    public int TotalFiles => Items.Sum(i => i.Files);
}

public sealed partial class CleanupService
{
    public CleanupPreview Preview()
    {
        var items = new List<CleanupPreviewItem>
        {
            Measure("Benutzer-Temp", Path.GetTempPath()),
            Measure("Windows-Temp", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")),
            Measure("Download-Cache", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "DeliveryOptimization", "Cache"))
        };
        return new CleanupPreview { Items = items.Where(i => i.Files > 0 || i.Bytes > 0).ToList() };
    }

    private static CleanupPreviewItem Measure(string label, string path)
    {
        long bytes = 0;
        var files = 0;
        if (!Directory.Exists(path))
        {
            return new CleanupPreviewItem { Label = label, Bytes = 0, Files = 0 };
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    bytes += new FileInfo(file).Length;
                    files++;
                }
                catch
                {
                    // locked
                }
            }
        }
        catch
        {
            // access
        }

        return new CleanupPreviewItem { Label = label, Bytes = bytes, Files = files };
    }
}
