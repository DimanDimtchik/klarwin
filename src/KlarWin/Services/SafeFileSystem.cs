namespace KlarWin.Services;

using System.IO;

/// <summary>Directory walks that skip junctions/reparse points (avoids counting entire disks as „Temp“).</summary>
public static class SafeFileSystem
{
    public static bool IsReparsePoint(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>True wenn mindestens eine Datei im Ordner gelesen werden kann.</summary>
    public static bool CanReadDirectory(string path)
    {
        if (!Directory.Exists(path)) return false;
        try
        {
            using var e = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            return e.MoveNext();
        }
        catch
        {
            return false;
        }
    }

    public static (long Bytes, int Files) MeasureDirectory(string path, int maxDepth = 4)
    {
        if (!Directory.Exists(path)) return (0, 0);
        var rootFull = NormalizeRoot(path);
        long bytes = 0;
        var files = 0;
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((rootFull, 0));

        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            if (depth > maxDepth) continue;
            if (!IsInsideRoot(dir, rootFull)) continue;
            if (ShouldSkipDirectory(dir, rootFull)) continue;

            IEnumerable<string> fileEntries;
            IEnumerable<string> dirEntries;
            try
            {
                fileEntries = Directory.EnumerateFiles(dir);
                dirEntries = Directory.EnumerateDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (var file in fileEntries)
            {
                if (!IsInsideRoot(file, rootFull)) continue;
                try
                {
                    if (IsReparsePoint(file)) continue;
                    var info = new FileInfo(file);
                    if (!info.Exists) continue;
                    var len = info.Length;
                    if (len > (8L << 30)) continue;
                    if (len > 0)
                    {
                        bytes += len;
                    }

                    files++;
                }
                catch
                {
                    // locked
                }
            }

            if (depth >= maxDepth) continue;
            foreach (var sub in dirEntries)
            {
                if (!IsInsideRoot(sub, rootFull)) continue;
                if (IsReparsePoint(sub)) continue;
                if (ShouldSkipDirectory(sub, rootFull)) continue;
                queue.Enqueue((sub, depth + 1));
            }
        }

        bytes = ByteSizeFormatter.SanitizeBytes(bytes, ByteSizeKind.FolderCleanup);
        return (bytes, files);
    }

    private static bool ShouldSkipDirectory(string dir, string rootFull)
    {
        var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) return false;
        if (name.StartsWith("$", StringComparison.Ordinal)) return true;
        if (name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("Windows", StringComparison.OrdinalIgnoreCase)
            && !rootFull.Contains("Windows", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static long CleanDirectorySafe(string path, ref int filesRemoved, IProgress<string>? progress, List<string> notes, string label, int maxDepth = 4)
    {
        if (!Directory.Exists(path))
        {
            notes.Add($"{label}: Ordner nicht gefunden.");
            return 0;
        }

        long bytes = 0;
        var skipped = 0;

        foreach (var file in SafeEnumerateFiles(path, maxDepth))
        {
            try
            {
                var info = new FileInfo(file);
                var size = info.Exists ? info.Length : 0;
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
                bytes += size;
                filesRemoved++;
            }
            catch
            {
                skipped++;
            }
        }

        foreach (var directory in SafeEnumerateDirectories(path, maxDepth).OrderByDescending(d => d.Length))
        {
            if (directory.Equals(NormalizeRoot(path), StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
            catch
            {
                skipped++;
            }
        }

        progress?.Report($"{label}: {ByteSizeFormatter.Format(bytes)} frei.");
        if (skipped > 0)
        {
            notes.Add($"{label}: {skipped} Einträge in Benutzung — übersprungen.");
        }

        return bytes;
    }

    private static string NormalizeRoot(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        + Path.DirectorySeparatorChar;

    private static bool IsInsideRoot(string path, string rootFull)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root, int maxDepth)
    {
        var rootFull = NormalizeRoot(root);
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((rootFull, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            if (!IsInsideRoot(dir, rootFull)) continue;
            IEnumerable<string> files;
            IEnumerable<string> dirs;
            try
            {
                files = Directory.EnumerateFiles(dir);
                dirs = depth < maxDepth ? Directory.EnumerateDirectories(dir) : [];
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                if (IsInsideRoot(file, rootFull) && !IsReparsePoint(file))
                {
                    yield return file;
                }
            }

            foreach (var sub in dirs)
            {
                if (!IsInsideRoot(sub, rootFull) || IsReparsePoint(sub)) continue;
                queue.Enqueue((sub, depth + 1));
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root, int maxDepth)
    {
        var rootFull = NormalizeRoot(root);
        var results = new List<string> { rootFull.TrimEnd(Path.DirectorySeparatorChar) };
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((rootFull, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;
            if (!IsInsideRoot(dir, rootFull)) continue;
            IEnumerable<string> dirs;
            try
            {
                dirs = Directory.EnumerateDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (var sub in dirs)
            {
                if (!IsInsideRoot(sub, rootFull) || IsReparsePoint(sub)) continue;
                results.Add(sub);
                queue.Enqueue((sub, depth + 1));
            }
        }

        return results;
    }
}
