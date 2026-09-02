using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace KlarWin.Services;

public sealed class RecoverableItem
{
    public string DisplayName { get; init; } = "";
    public string OriginalPath { get; init; } = "";
    public string RecyclePath { get; init; } = "";
    public long SizeBytes { get; init; }
    public DateTime DeletedAt { get; init; }
    public string Source { get; init; } = "Papierkorb";
    public bool CanRestore => !string.IsNullOrWhiteSpace(RecyclePath) && (File.Exists(RecyclePath) || Directory.Exists(RecyclePath));
}

public sealed class RecoveryService
{
    private const int UsnFileDelete = 0x00000200;

    public IReadOnlyList<RecoverableItem> ListRecycleBin()
    {
        var items = new List<RecoverableItem>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            var root = Path.Combine(drive.Name, "$Recycle.Bin");
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

    /// <summary>
    /// Reads NTFS USN journal for recent FILE_DELETE entries (Shift+Entf, etc.).
    /// Returns hints only — files are not restorable from KlarWin without deep carving.
    /// </summary>
    public IReadOnlyList<RecoverableItem> ScanUsnDeletions(string driveLetter = "C", int maxEntries = 400)
    {
        var items = new List<RecoverableItem>();
        driveLetter = driveLetter.TrimEnd(':').ToUpperInvariant();
        var drive = driveLetter + ":";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "fsutil.exe"),
                Arguments = $"usn readjournal {drive} csv",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.Unicode
            };

            using var process = Process.Start(psi);
            if (process is null) return items;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15000);
            if (process.ExitCode != 0) return items;

            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (items.Count >= maxEntries) break;
                if (!line.Contains("FileDelete", StringComparison.OrdinalIgnoreCase)
                    && (UsnFileDelete & ParseReason(line)) == 0)
                {
                    continue;
                }

                var path = ExtractCsvField(line, "FileName") ?? ExtractCsvField(line, "Name");
                if (string.IsNullOrWhiteSpace(path)) continue;
                path = path.Trim('"');
                if (path.Contains("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)) continue;
                if (path.StartsWith("$", StringComparison.Ordinal)) continue;

                var stamp = ExtractCsvField(line, "TimeStamp") ?? ExtractCsvField(line, "Timestamp");
                var deleted = DateTime.TryParse(stamp, out var dt) ? dt : DateTime.Now;

                var display = Path.GetFileName(path);
                if (string.IsNullOrWhiteSpace(display)) display = path;

                if (items.Any(i => i.OriginalPath.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;

                items.Add(new RecoverableItem
                {
                    DisplayName = display,
                    OriginalPath = path,
                    RecyclePath = "",
                    SizeBytes = 0,
                    DeletedAt = deleted,
                    Source = "NTFS-Protokoll (Hinweis)"
                });
            }
        }
        catch
        {
            // fsutil needs admin on some systems
        }

        return items.OrderByDescending(i => i.DeletedAt).ToList();
    }

    public string Restore(RecoverableItem item, string? targetFolder = null)
    {
        if (!item.CanRestore)
        {
            return "Dieser Eintrag ist nur ein Hinweis aus dem NTFS-Protokoll — die Datei liegt nicht mehr im Papierkorb. " +
                   "Wiederherstellung nur mit Spezialtools (z. B. Recuva, Windows File Recovery), solange der Speicher nicht überschrieben wurde.";
        }

        if (!File.Exists(item.RecyclePath) && !Directory.Exists(item.RecyclePath))
        {
            return "Eintrag nicht mehr vorhanden.";
        }

        var destination = targetFolder is null
            ? (string.IsNullOrWhiteSpace(item.OriginalPath) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), item.DisplayName) : item.OriginalPath)
            : Path.Combine(targetFolder, item.DisplayName);

        var destDir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(destDir)) Directory.CreateDirectory(destDir);
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            destination = Path.Combine(destDir ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Path.GetFileNameWithoutExtension(destination) + "_wiederhergestellt" + Path.GetExtension(destination));
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

    private static int ParseReason(string line)
    {
        var reasonText = ExtractCsvField(line, "Reason");
        if (string.IsNullOrWhiteSpace(reasonText)) return 0;
        reasonText = reasonText.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        return int.TryParse(reasonText, System.Globalization.NumberStyles.HexNumber, null, out var hex)
            ? hex
            : 0;
    }

    private static string? ExtractCsvField(string line, string fieldName)
    {
        var match = Regex.Match(line, $@"{Regex.Escape(fieldName)},(?:""([^""]*)""|([^,\r\n]*))", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
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

                var (original, deletedAt, sizeFromInfo) = ReadRecycleInfo(infoFile);
                long size = sizeFromInfo;
                if (size <= 0 && File.Exists(dataFile))
                {
                    size = new FileInfo(dataFile).Length;
                }
                else if (size <= 0 && Directory.Exists(dataFile))
                {
                    size = MeasureRecycleEntry(dataFile);
                }

                var display = string.IsNullOrWhiteSpace(original) ? namePart : Path.GetFileName(original);
                if (string.IsNullOrWhiteSpace(display)) display = namePart;
                items.Add(new RecoverableItem
                {
                    DisplayName = display,
                    OriginalPath = original,
                    RecyclePath = dataFile,
                    SizeBytes = size,
                    DeletedAt = deletedAt,
                    Source = "Papierkorb"
                });
            }
            catch
            {
                // skip corrupt entries
            }
        }
    }

    private static long MeasureRecycleEntry(string dataFile)
    {
        if (File.Exists(dataFile))
        {
            try { return new FileInfo(dataFile).Length; } catch { return 0; }
        }

        if (!Directory.Exists(dataFile)) return 0;
        var (bytes, _) = SafeFileSystem.MeasureDirectory(dataFile, maxDepth: 8);
        return bytes;
    }

    private static (string Original, DateTime DeletedAt, long SizeBytes) ReadRecycleInfo(string infoFile)
    {
        var bytes = File.ReadAllBytes(infoFile);
        if (bytes.Length < 24)
        {
            return (ReadOriginalPath(infoFile), File.GetLastWriteTime(infoFile), 0);
        }

        var version = BitConverter.ToInt64(bytes, 0);
        long size = 0;
        var deletedAt = File.GetLastWriteTime(infoFile);
        if (version is 1 or 2 && bytes.Length >= 24)
        {
            size = BitConverter.ToInt64(bytes, 8);
            if (bytes.Length >= 16)
            {
                try
                {
                    deletedAt = DateTime.FromFileTimeUtc(BitConverter.ToInt64(bytes, 16)).ToLocalTime();
                }
                catch
                {
                    deletedAt = File.GetLastWriteTime(infoFile);
                }
            }
        }

        return (ReadOriginalPath(infoFile), deletedAt, size);
    }

    private static string ReadOriginalPath(string infoFile)
    {
        var bytes = File.ReadAllBytes(infoFile);
        if (bytes.Length < 28) return "";

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
