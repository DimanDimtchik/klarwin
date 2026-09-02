using System.IO;

namespace KlarWin.Services;

public sealed record VolumeInfo(
    char Letter,
    string Label,
    DriveType DriveType,
    long TotalBytes,
    long FreeBytes,
    bool IsReady,
    string FileSystem);

public static class VolumeInfoService
{
    public static IReadOnlyList<VolumeInfo> ListVolumes()
    {
        var volumes = new List<VolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is DriveType.CDRom or DriveType.Unknown) continue;

            char letter;
            try
            {
                letter = drive.Name.Length >= 1 ? char.ToUpperInvariant(drive.Name[0]) : '?';
            }
            catch
            {
                continue;
            }

            if (!drive.IsReady)
            {
                volumes.Add(new VolumeInfo(letter, "", drive.DriveType, 0, 0, false, "nicht bereit"));
                continue;
            }

            long total;
            long free;
            string fs;
            string label;
            try
            {
                total = ByteSizeFormatter.SanitizeBytes(drive.TotalSize, ByteSizeKind.Disk);
                free = ByteSizeFormatter.SanitizeBytes(drive.AvailableFreeSpace, ByteSizeKind.Disk);
                fs = string.IsNullOrWhiteSpace(drive.DriveFormat) ? "—" : drive.DriveFormat;
                label = drive.VolumeLabel?.Trim() ?? "";
            }
            catch
            {
                volumes.Add(new VolumeInfo(letter, "", drive.DriveType, 0, 0, false, "—"));
                continue;
            }

            volumes.Add(new VolumeInfo(letter, label, drive.DriveType, total, free, true, fs));
        }

        return volumes
            .OrderBy(v => v.DriveType switch
            {
                DriveType.Fixed => 0,
                DriveType.Removable => 1,
                DriveType.Network => 2,
                _ => 3
            })
            .ThenBy(v => v.Letter)
            .ToList();
    }

    public static string KindLabel(DriveType type) => type switch
    {
        DriveType.Fixed => "Festplatte",
        DriveType.Removable => "Wechselmedium",
        DriveType.Network => "Netzwerk",
        DriveType.Ram => "RAM-Disk",
        _ => "Laufwerk"
    };

    public static string FormatComboLabel(VolumeInfo volume)
    {
        var kind = KindLabel(volume.DriveType);
        var label = string.IsNullOrWhiteSpace(volume.Label) ? "" : $" · {volume.Label}";
        if (!volume.IsReady)
        {
            return $"{volume.Letter}: {kind}{label} — nicht bereit";
        }

        var size = volume.TotalBytes > 0
            ? ByteSizeFormatter.Format(volume.TotalBytes, ByteSizeKind.Disk)
            : "—";
        return $"{volume.Letter}: {kind}{label} · {volume.FileSystem} · {size}";
    }

    public static VolumeInfo? DefaultSystemVolume(IReadOnlyList<VolumeInfo> volumes)
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\').ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(systemRoot) && systemRoot.Length >= 1)
        {
            var letter = systemRoot[0];
            var match = volumes.FirstOrDefault(v => v.Letter == letter && v.IsReady);
            if (match is not null) return match;
        }

        return volumes.FirstOrDefault(v => v.IsReady && v.DriveType == DriveType.Fixed)
               ?? volumes.FirstOrDefault(v => v.IsReady);
    }
}
