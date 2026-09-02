using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KlarWin.Services;

public static class AppIconHelper
{
    private static readonly ConcurrentDictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiLargeIcon = 0;
    private const uint ShgfiUseFileAttributes = 0x10;
    private const uint FileAttributeNormal = 0x80;

    public static ImageSource? Load(string? pathOrCommand) => LoadSafe(pathOrCommand);

    public static ImageSource? LoadSafe(string? pathOrCommand)
    {
        if (string.IsNullOrWhiteSpace(pathOrCommand)) return null;

        var cacheKey = pathOrCommand.Trim();
        if (IconCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        ImageSource? loaded = null;
        try
        {
            foreach (var candidate in GetIconCandidates(pathOrCommand))
            {
                var icon = TryLoadIconFromPath(candidate);
                if (icon is not null)
                {
                    loaded = icon;
                    break;
                }
            }
        }
        catch
        {
            // optional
        }

        IconCache[cacheKey] = loaded;
        return loaded;
    }

    private static IEnumerable<string> GetIconCandidates(string? pathOrCommand)
    {
        if (string.IsNullOrWhiteSpace(pathOrCommand)) yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateCommandCandidates(pathOrCommand))
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var full = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (!seen.Add(full)) continue;
            if (File.Exists(full)) yield return full;
        }
    }

    private static IEnumerable<string> EnumerateCommandCandidates(string pathOrCommand)
    {
        var text = Environment.ExpandEnvironmentVariables(pathOrCommand.Trim());

        if (File.Exists(text))
        {
            yield return text;
            if (text.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var target = ResolveShortcutTarget(text);
                if (!string.IsNullOrWhiteSpace(target)) yield return target;
            }

            yield break;
        }

        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end > 1)
            {
                yield return text[1..end];
            }
        }

        var space = text.IndexOf(' ');
        var first = (space > 0 ? text[..space] : text).Trim('"');
        if (!string.IsNullOrWhiteSpace(first))
        {
            yield return first;
        }

        var exe = ResolveExecutable(pathOrCommand);
        if (!string.IsNullOrWhiteSpace(exe))
        {
            yield return exe;
        }
    }

    private static ImageSource? TryLoadIconFromPath(string path)
    {
        var info = new Shfileinfo();
        var flags = ShgfiIcon | ShgfiLargeIcon;
        if (!File.Exists(path))
        {
            flags |= ShgfiUseFileAttributes;
        }

        var result = SHGetFileInfo(path, FileAttributeNormal, ref info, (uint)Marshal.SizeOf(info), flags);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            if (source.CanFreeze)
            {
                source.Freeze();
            }

            return source;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    public static string ResolveExecutable(string? pathOrCommand)
    {
        if (string.IsNullOrWhiteSpace(pathOrCommand)) return "";

        var text = Environment.ExpandEnvironmentVariables(pathOrCommand.Trim().Trim('"'));
        if (File.Exists(text))
        {
            return text.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                ? ResolveShortcutTarget(text)
                : text;
        }

        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end > 1)
            {
                var candidate = text[1..end];
                if (File.Exists(candidate)) return candidate;
            }
        }

        var space = text.IndexOf(' ');
        var first = space > 0 ? text[..space].Trim('"') : text;
        if (File.Exists(first)) return first;

        if (!first.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var withExe = first + ".exe";
            if (File.Exists(withExe)) return withExe;
        }

        return "";
    }

    private static string ResolveShortcutTarget(string lnkPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(lnkPath);
            if (bytes.Length < 76) return "";

            var flags = BitConverter.ToUInt32(bytes, 20);
            var hasLinkInfo = (flags & 0x2) != 0;
            var hasRelative = (flags & 0x4) != 0;
            var hasWorkingDir = (flags & 0x10) != 0;
            var hasArguments = (flags & 0x20) != 0;
            var hasIcon = (flags & 0x40) != 0;
            var hasUnicode = (flags & 0x80) != 0;

            var offset = 76;
            if (hasRelative) offset += SkipStringData(bytes, offset, hasUnicode);
            if (hasWorkingDir) offset += SkipStringData(bytes, offset, hasUnicode);
            if (hasArguments) offset += SkipStringData(bytes, offset, hasUnicode);
            if (hasIcon) offset += SkipStringData(bytes, offset, hasUnicode);

            if (hasLinkInfo && offset + 8 <= bytes.Length)
            {
                var linkInfoSize = BitConverter.ToInt32(bytes, offset);
                if (linkInfoSize >= 28 && offset + linkInfoSize <= bytes.Length)
                {
                    var localBase = BitConverter.ToInt32(bytes, offset + 16);
                    if (localBase > 0 && offset + localBase + 1 < bytes.Length)
                    {
                        var start = offset + localBase;
                        var end = start;
                        while (end < bytes.Length && bytes[end] != 0) end++;
                        var path = Encoding.Default.GetString(bytes, start, end - start);
                        if (!string.IsNullOrWhiteSpace(path)) return Environment.ExpandEnvironmentVariables(path);
                    }
                }
            }
        }
        catch
        {
            // ignore malformed shortcuts
        }

        return "";
    }

    private static int SkipStringData(byte[] bytes, int offset, bool unicode)
    {
        if (unicode)
        {
            var pos = offset;
            while (pos + 1 < bytes.Length)
            {
                if (bytes[pos] == 0 && bytes[pos + 1] == 0) return pos - offset + 2;
                pos += 2;
            }

            return bytes.Length - offset;
        }

        var end = offset;
        while (end < bytes.Length && bytes[end] != 0) end++;
        return end - offset + 1;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref Shfileinfo psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Shfileinfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }
}
