using System.Globalization;

namespace KlarWin.Services;

public enum ByteSizeKind
{
    Generic,
    Ram,
    ProcessRam,
    Vram,
    Disk,
    FolderCleanup
}

public static class ByteSizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes, ByteSizeKind kind = ByteSizeKind.Generic)
    {
        bytes = SanitizeBytes(bytes, kind);
        var unitIndex = 0;
        double value = bytes;
        while (value >= 1024 && unitIndex < Units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        var format = unitIndex >= 3 ? "0.##" : unitIndex >= 2 ? "0.##" : "0.0";
        return $"{value.ToString(format, CultureInfo.CurrentCulture)} {Units[unitIndex]}";
    }

    public static string FormatUsedOfTotal(long usedBytes, long totalBytes, bool includePercent = true)
    {
        usedBytes = SanitizeBytes(usedBytes, ByteSizeKind.Ram);
        totalBytes = SanitizeBytes(totalBytes, ByteSizeKind.Ram);
        if (totalBytes <= 0) return "—";
        usedBytes = Math.Clamp(usedBytes, 0, totalBytes);
        var pct = 100.0 * usedBytes / totalBytes;
        var text = $"{Format(usedBytes, ByteSizeKind.Ram)} von {Format(totalBytes, ByteSizeKind.Ram)}";
        return includePercent ? $"{text} ({pct.ToString("0", CultureInfo.CurrentCulture)}%)" : text;
    }

    /// <summary>Caps implausible values (e.g. junction leaks counting whole disks).</summary>
    public static long SanitizeBytes(long bytes, ByteSizeKind kind = ByteSizeKind.Generic)
    {
        if (bytes < 0) return 0;
        var max = kind switch
        {
            ByteSizeKind.Ram => 512L << 30,
            ByteSizeKind.ProcessRam => 128L << 30,
            ByteSizeKind.Vram => 128L << 30,
            ByteSizeKind.Disk => 128L << 40,
            ByteSizeKind.FolderCleanup => 64L << 30,
            _ => 64L << 40
        };

        while (bytes > max)
        {
            bytes /= 1024;
        }

        return bytes;
    }
}
