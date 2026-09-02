using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace KlarWin.Services;

/// <summary>Papierkorb-Inhalt wie Windows Explorer (SHQueryRecycleBin), plus reservierte Maximalgröße aus Registry.</summary>
public static class RecycleBinService
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 8)]
    private struct SHQueryRbInfo
    {
        public int CbSize;
        public long I64Size;
        public long I64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQueryRbInfo pSHQueryRBInfo);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string lpszVolumeMountPoint,
        StringBuilder lpszVolumeName,
        int cchBufferLength);

    public static (long Bytes, int Items) MeasureContent()
    {
        long bytes = 0;
        long items = 0;
        var anyDrive = false;

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network)) continue;

            var root = drive.RootDirectory.FullName;
            if (!root.EndsWith('\\')) root += '\\';

            var info = new SHQueryRbInfo { CbSize = Marshal.SizeOf<SHQueryRbInfo>() };
            if (SHQueryRecycleBin(root, ref info) != 0) continue;

            anyDrive = true;
            bytes += Math.Max(0, info.I64Size);
            items += Math.Max(0, info.I64NumItems);
        }

        if (anyDrive && (bytes > 0 || items > 0))
        {
            return (bytes, ClampItems(items));
        }

        if (anyDrive)
        {
            return (0, 0);
        }

        return FallbackFromRecovery();
    }

    private static (long Bytes, int Items) FallbackFromRecovery()
    {
        var list = new RecoveryService().ListRecycleBin();
        if (list.Count == 0) return (0, 0);
        return (list.Sum(i => Math.Max(0, i.SizeBytes)), list.Count);
    }

    public static string BuildHint()
    {
        var maxParts = new List<string>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            var letter = drive.Name.TrimEnd('\\');
            var maxMb = ReadMaxSizeMegabytes(drive.Name);
            if (maxMb is > 0)
            {
                var maxBytes = maxMb.Value * 1024L * 1024L;
                maxParts.Add($"{letter} max. {ByteSizeFormatter.Format(maxBytes, ByteSizeKind.FolderCleanup)}");
            }
        }

        if (maxParts.Count == 0)
        {
            return "Aktueller Papierkorb-Inhalt — wie in Windows Explorer";
        }

        return $"Aktueller Inhalt (beim Leeren frei) · Reserviert: {string.Join(", ", maxParts)} — wie in Papierkorb-Eigenschaften";
    }

    private static int? ReadMaxSizeMegabytes(string driveRoot)
    {
        var mountPoint = driveRoot.EndsWith('\\') ? driveRoot : driveRoot + '\\';
        var volumeName = new StringBuilder(1024);
        if (!GetVolumeNameForVolumeMountPoint(mountPoint, volumeName, volumeName.Capacity))
        {
            return null;
        }

        const string volumePrefix = @"\\?\Volume";
        var volumeKey = volumeName.ToString().TrimEnd('\\');
        if (volumeKey.StartsWith(volumePrefix, StringComparison.OrdinalIgnoreCase))
        {
            volumeKey = volumeKey[volumePrefix.Length..];
        }

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var volumes = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume");
                if (volumes is null) continue;

                using var sub = volumes.OpenSubKey(volumeKey);
                if (sub?.GetValue("NRecycleBinSize") is int mb && mb > 0)
                {
                    return mb;
                }
            }
            catch
            {
                // optional
            }
        }

        return null;
    }

    private static int ClampItems(long items) => (int)Math.Min(items, int.MaxValue);
}
