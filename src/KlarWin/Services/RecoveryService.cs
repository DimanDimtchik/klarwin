using System.IO;
using System.Text;

namespace KlarWin.Services;

public sealed class RecoverableItem
{
    public string DisplayName { get; init; } = "";
    public string OriginalPath { get; init; } = "";
    public string RecyclePath { get; init; } = "";
    public long SizeBytes { get; init; }
    public DateTime DeletedAt { get; init; }
}

public sealed class RecoveryService
{
    public IReadOnlyList<RecoverableItem> ListRecycleBin()
    {
        var items = new List<RecoverableItem>();
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "$Recycle.Bin"),
            @"C:\$Recycle.Bin"
        };

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var sidDir in Directory.EnumerateDirectories(root))
                {
                    ScanSidFolder(sidDir, items);
                }
            }
            catch
            {
                // access
            }
        }

        return items.OrderByDescending(i => i.DeletedAt).ToList();
    }

    public string Restore(RecoverableItem item, string? targetFolder = null)
    {
        if (!File.Exists(item.RecyclePath) && !Directory.Exists(item.RecyclePath))
        {
            return "Eintrag nicht mehr vorhanden.";
        }

        var destination = targetFolder is null
            ? (string.IsNullOrWhiteSpace(item.OriginalPath) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), item.DisplayName) : item.OriginalPath)
            : Path.Combine(targetFolder, item.DisplayName);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            destination = Path.Combine(Path.GetDirectoryName(destination)!, Path.GetFileNameWithoutExtension(destination) + "_wiederhergestellt" + Path.GetExtension(destination));
        }

        try
        {
            if (Directory.Exists(item.RecyclePath))
            {
                CopyDirectory(item.RecyclePath, destination);
            }
            else
            {
                File.Copy(item.RecyclePath, destination, overwrite: false);
            }

            TryDeleteRecyclePair(item);
            return $"Wiederhergestellt nach: {destination}";
        }
        catch (Exception ex)
        {
            return $"Fehler: {ex.Message}";
        }
    }

    private static void ScanSidFolder(string sidDir, List<RecoverableItem> items)
    {
        string[] infoFiles;
        try
        {
            infoFiles = Directory.GetFiles(sidDir, "$I*");
        }
        catch
        {
            return;
        }

        foreach (var infoFile in infoFiles)
        {
            try
            {
                var namePart = Path.GetFileName(infoFile)[2..];
                var dataFile = Path.Combine(sidDir, "$R" + namePart);
                if (!File.Exists(dataFile) && !Directory.Exists(dataFile))
                {
                    continue;
                }

                var original = ReadOriginalPath(infoFile);
                var deletedAt = File.GetLastWriteTime(infoFile);
                long size = 0;
                if (File.Exists(dataFile)) size = new FileInfo(dataFile).Length;
                var display = string.IsNullOrWhiteSpace(original) ? namePart : Path.GetFileName(original);
                items.Add(new RecoverableItem
                {
                    DisplayName = display,
                    OriginalPath = original,
                    RecyclePath = dataFile,
                    SizeBytes = size,
                    DeletedAt = deletedAt
                });
            }
            catch
            {
                // skip corrupt entries
            }
        }
    }

    private static string ReadOriginalPath(string infoFile)
    {
        var bytes = File.ReadAllBytes(infoFile);
        if (bytes.Length < 28) return "";

        // Vista+ format: header then UTF-16 path
        var version = BitConverter.ToInt64(bytes, 0);
        if (version == 2 || bytes.Length > 24)
        {
            var pathOffset = version == 2 ? 24 : 28;
            if (bytes.Length <= pathOffset) return "";
            return Encoding.Unicode.GetString(bytes, pathOffset, bytes.Length - pathOffset).TrimEnd('\0');
        }

        return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    private static void TryDeleteRecyclePair(RecoverableItem item)
    {
        try
        {
            var dir = Path.GetDirectoryName(item.RecyclePath)!;
            var name = Path.GetFileName(item.RecyclePath);
            if (name.StartsWith("$R", StringComparison.OrdinalIgnoreCase))
            {
                var info = Path.Combine(dir, "$I" + name[2..]);
                if (File.Exists(info)) File.Delete(info);
            }

            if (File.Exists(item.RecyclePath)) File.Delete(item.RecyclePath);
            else if (Directory.Exists(item.RecyclePath)) Directory.Delete(item.RecyclePath, true);
        }
        catch
        {
            // keep recycle entry if delete fails
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }
        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
