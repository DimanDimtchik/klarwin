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

public sealed partial class CleanupService
{
    public CleanupResult CleanNow(CleanupSelection selection, IProgress<string>? progress = null)
    {
        long bytes = 0;
        var files = 0;
        var notes = new List<string>();

        if (selection.UserTemp)
        {
            bytes += SafeFileSystem.CleanDirectorySafe(Path.GetTempPath(), ref files, progress, notes, "Benutzer-Temp");
        }

        if (selection.WindowsTemp)
        {
            var windowsTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
            bytes += SafeFileSystem.CleanDirectorySafe(windowsTemp, ref files, progress, notes, "Windows-Temp");
        }

        if (selection.DeliveryCache)
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "DeliveryOptimization", "Cache");
            bytes += SafeFileSystem.CleanDirectorySafe(path, ref files, progress, notes, "Download-Cache");
        }

        if (selection.RecycleBin)
        {
            EmptyRecycleBin(progress, notes);
        }

        if (selection.UserDownloads)
        {
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            bytes += SafeFileSystem.CleanDirectorySafe(downloads, ref files, progress, notes, "Download-Ordner");
        }

        if (!selection.UserTemp && !selection.WindowsTemp && !selection.DeliveryCache && !selection.RecycleBin && !selection.UserDownloads)
        {
            notes.Add("Nichts ausgewählt.");
        }

        return new CleanupResult
        {
            BytesRemoved = bytes,
            FilesRemoved = files,
            Notes = notes
        };
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

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);
}
