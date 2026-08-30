using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace KlarWin.Services;

public sealed class CleanupResult
{
    public long BytesRemoved { get; init; }
    public int FilesRemoved { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed class CleanupService
{
    public CleanupResult CleanNow(IProgress<string>? progress = null)
    {
        long bytes = 0;
        var files = 0;
        var notes = new List<string>();

        bytes += CleanDirectory(Path.GetTempPath(), ref files, progress, notes, "Benutzer-Temp");
        var windowsTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        bytes += CleanDirectory(windowsTemp, ref files, progress, notes, "Windows-Temp");
        bytes += CleanDeliveryOptimization(ref files, progress, notes);
        EmptyRecycleBin(progress, notes);

        return new CleanupResult
        {
            BytesRemoved = bytes,
            FilesRemoved = files,
            Notes = notes
        };
    }

    private static long CleanDirectory(string path, ref int files, IProgress<string>? progress, List<string> notes, string label)
    {
        if (!Directory.Exists(path))
        {
            notes.Add($"{label}: Ordner nicht gefunden.");
            return 0;
        }

        long bytes = 0;
        var removed = 0;
        var skipped = 0;

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var info = new FileInfo(file);
                var size = info.Exists ? info.Length : 0;
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
                bytes += size;
                removed++;
                files++;
            }
            catch
            {
                skipped++;
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(path))
        {
            try
            {
                bytes += DeleteTree(directory, ref files, ref skipped);
                removed++;
            }
            catch
            {
                skipped++;
            }
        }

        progress?.Report($"{label}: {removed} Einträge gelöscht.");
        if (skipped > 0)
        {
            notes.Add($"{label}: {skipped} Dateien waren in Benutzung und wurden übersprungen.");
        }

        return bytes;
    }

    private static long DeleteTree(string path, ref int files, ref int skipped)
    {
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(file);
                var size = info.Exists ? info.Length : 0;
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
                bytes += size;
                files++;
            }
            catch
            {
                skipped++;
            }
        }

        try
        {
            Directory.Delete(path, true);
        }
        catch
        {
            skipped++;
        }

        return bytes;
    }

    private static long CleanDeliveryOptimization(ref int files, IProgress<string>? progress, List<string> notes)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "DeliveryOptimization", "Cache");
        if (!Directory.Exists(path))
        {
            return 0;
        }

        var bytes = CleanDirectory(path, ref files, progress, notes, "Download-Cache");
        return bytes;
    }

    private static void EmptyRecycleBin(IProgress<string>? progress, List<string> notes)
    {
        const uint noConfirmation = 0x00000001;
        const uint noProgressUi = 0x00000002;
        const uint noSound = 0x00000004;
        var result = SHEmptyRecycleBin(IntPtr.Zero, null, noConfirmation | noProgressUi | noSound);
        if (result == 0 || result == -2147418113)
        {
            progress?.Report("Papierkorb geleert.");
            notes.Add("Papierkorb geleert.");
            return;
        }

        notes.Add("Papierkorb konnte nicht vollständig geleert werden.");
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double value = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.0} {units[unit]}";
    }

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);
}
