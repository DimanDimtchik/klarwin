using System.Diagnostics;
using System.IO;
using System.Management;

namespace KlarWin.Services;

public sealed record UsbFileEntry(string Name, bool IsDirectory, long SizeBytes);

public sealed record UsbDriveInfo(
    char Letter,
    string Label,
    string FileSystem,
    long TotalBytes,
    long FreeBytes,
    bool IsReady);

public static class UsbStickService
{
    public static IReadOnlyList<UsbDriveInfo> ListRemovableDrives()
    {
        var drives = new List<UsbDriveInfo>();
        CollectFromDriveInfo(drives);
        CollectFromWmiVolumes(drives);
        return drives
            .OrderBy(d => d.Letter)
            .ToList();
    }

    public static IReadOnlyList<UsbFileEntry> ListRootEntries(char letter, int maxEntries = 40)
    {
        var root = $"{char.ToUpperInvariant(letter)}:\\";
        if (!Directory.Exists(root))
        {
            return [];
        }

        var entries = new List<UsbFileEntry>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir.TrimEnd('\\'));
                if (string.IsNullOrWhiteSpace(name)) continue;
                entries.Add(new UsbFileEntry(name, true, 0));
                if (entries.Count >= maxEntries) return entries;
            }

            foreach (var file in Directory.EnumerateFiles(root))
            {
                var name = Path.GetFileName(file);
                long size = 0;
                try
                {
                    size = new FileInfo(file).Length;
                }
                catch
                {
                    // skip size
                }

                entries.Add(new UsbFileEntry(name, false, size));
                if (entries.Count >= maxEntries) return entries;
            }
        }
        catch
        {
            // access denied or unreadable fs
        }

        return entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string FormatDriveSummary(UsbDriveInfo drive)
    {
        if (!drive.IsReady || drive.TotalBytes <= 0)
        {
            return $"Laufwerk {drive.Letter}: — {drive.FileSystem}";
        }

        var used = drive.TotalBytes - drive.FreeBytes;
        return
            $"Laufwerk {drive.Letter}: {drive.FileSystem} · " +
            $"Belegt {ByteSizeFormatter.Format(used, ByteSizeKind.Disk)} · " +
            $"Frei {ByteSizeFormatter.Format(drive.FreeBytes, ByteSizeKind.Disk)} · " +
            $"Gesamt {ByteSizeFormatter.Format(drive.TotalBytes, ByteSizeKind.Disk)}";
    }

    public static (bool Success, string Message) FormatDrive(char driveLetter, string fileSystem)
    {
        if (!ShortcutOverlayService.IsAdministrator())
        {
            return (false, "Administratorrechte nötig — oben rechts „Adminrechte freischalten“.");
        }

        var fs = fileSystem.Trim().ToUpperInvariant();
        if (fs is not ("NTFS" or "EXFAT" or "FAT32"))
        {
            return (false, "Unbekanntes Dateisystem. Bitte NTFS, exFAT oder FAT32 wählen.");
        }

        var letter = char.ToUpperInvariant(driveLetter);
        if (letter is 'A' or 'B' or 'C')
        {
            return (false, "Systemlaufwerke A:, B: und C: können nicht formatiert werden.");
        }

        var root = $"{letter}:\\";
        if (!Directory.Exists(root))
        {
            return (false, $"Laufwerk {letter}: ist nicht bereit oder hat keinen Buchstaben.");
        }

        DriveInfo driveInfo;
        try
        {
            driveInfo = new DriveInfo(root);
        }
        catch (Exception ex)
        {
            return (false, UserFriendlyErrors.Translate(ex));
        }

        if (driveInfo.DriveType != DriveType.Removable)
        {
            return (false, $"Laufwerk {letter}: ist kein Wechseldatenträger (USB-Stick/SD). Aus Sicherheitsgründen nur USB-Sticks erlaubt.");
        }

        if (fs == "FAT32" && driveInfo.TotalSize > 32L * 1024 * 1024 * 1024)
        {
            return (false, "FAT32 ist unter Windows auf Sticks über 32 GB oft nicht möglich. Bitte exFAT oder NTFS wählen — oder für ein Windows-Installationsmedium das Microsoft Media Creation Tool verwenden.");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c format {letter}: /FS:{fs} /Q /V:USBSTICK /Y",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, "Format-Befehl konnte nicht gestartet werden.");
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(120_000);
            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(error) ? output : error;
                return (false, string.IsNullOrWhiteSpace(detail)
                    ? $"Formatierung fehlgeschlagen (Code {process.ExitCode})."
                    : detail.Trim());
            }

            return (true, $"Laufwerk {letter}: wurde als {fs} formatiert (Volume „USBSTICK“).");
        }
        catch (Exception ex)
        {
            return (false, UserFriendlyErrors.Translate(ex));
        }
    }

    private static void CollectFromDriveInfo(List<UsbDriveInfo> drives)
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Removable) continue;
            if (drive.Name.Length < 2) continue;
            var letter = char.ToUpperInvariant(drive.Name[0]);
            if (drives.Any(d => d.Letter == letter)) continue;

            drives.Add(BuildEntry(letter, drive.IsReady ? drive : null));
        }
    }

    private static void CollectFromWmiVolumes(List<UsbDriveInfo> drives)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DriveLetter, Label, FileSystem, Capacity, FreeSpace FROM Win32_Volume WHERE DriveType = 2");
            foreach (var obj in searcher.Get())
            {
                var letterRaw = obj["DriveLetter"]?.ToString()?.Trim();
                if (string.IsNullOrEmpty(letterRaw) || letterRaw.Length < 1) continue;
                var letter = char.ToUpperInvariant(letterRaw[0]);
                if (drives.Any(d => d.Letter == letter)) continue;

                var label = obj["Label"]?.ToString()?.Trim() ?? "";
                var fileSystem = obj["FileSystem"]?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(fileSystem)) fileSystem = "RAW / unbekannt";
                long total = obj["Capacity"] is ulong cap ? (long)cap : 0;
                long free = obj["FreeSpace"] is ulong freeSpace ? (long)freeSpace : 0;

                drives.Add(new UsbDriveInfo(letter, label, fileSystem, total, free, true));
            }
        }
        catch
        {
            // optional
        }
    }

    private static UsbDriveInfo BuildEntry(char letter, DriveInfo? drive)
    {
        if (drive is null || !drive.IsReady)
        {
            return new UsbDriveInfo(letter, "", "nicht eingebunden", 0, 0, false);
        }

        var fs = string.IsNullOrWhiteSpace(drive.DriveFormat) ? "RAW / unbekannt" : drive.DriveFormat;
        return new UsbDriveInfo(
            letter,
            drive.VolumeLabel,
            fs,
            drive.TotalSize,
            drive.AvailableFreeSpace,
            true);
    }
}
