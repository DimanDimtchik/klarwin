using System.IO;
using Microsoft.Win32;

namespace KlarWin.Services;

public sealed class AutostartCandidate
{
    public string Name { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    public string Source { get; init; } = "";
}

public sealed class AutostartCatalogService
{
    public IReadOnlyList<AutostartCandidate> DiscoverCandidates(IReadOnlyList<AutostartEntry> currentAutostart)
    {
        var running = CollectAutostartExecutables(currentAutostart);
        var byPath = new Dictionary<string, AutostartCandidate>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? name, string? exePath, string source)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(exePath))
            {
                return;
            }

            var path = exePath.Trim().Trim('"');
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!File.Exists(path))
            {
                return;
            }

            if (running.Contains(path))
            {
                return;
            }

            if (IsWindowsSystemBinary(path))
            {
                return;
            }

            name = name.Trim();
            if (name.Length == 0)
            {
                name = Path.GetFileNameWithoutExtension(path);
            }

            if (byPath.TryGetValue(path, out var existing))
            {
                if (name.Length > existing.Name.Length)
                {
                    byPath[path] = new AutostartCandidate { Name = name, ExecutablePath = path, Source = source };
                }

                return;
            }

            byPath[path] = new AutostartCandidate { Name = name, ExecutablePath = path, Source = source };
        }

        AddFromAppPaths(TryAdd);
        AddFromStartMenu(TryAdd);
        AddFromUninstallRegistry(TryAdd);

        return byPath.Values
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static HashSet<string> CollectAutostartExecutables(IReadOnlyList<AutostartEntry> entries)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.Location.Contains("Ordner", StringComparison.Ordinal))
            {
                var target = ResolveShortcutTarget(entry.Command) ?? entry.Command;
                if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
                {
                    set.Add(Path.GetFullPath(target));
                }

                continue;
            }

            var exe = AutostartService.ExtractExecutablePath(entry.Command);
            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            {
                set.Add(Path.GetFullPath(exe));
            }
        }

        return set;
    }

    private static bool IsWindowsSystemBinary(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return full.StartsWith(Path.Combine(windows, "System32"), StringComparison.OrdinalIgnoreCase)
                   || full.StartsWith(Path.Combine(windows, "SysWOW64"), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void AddFromAppPaths(Action<string?, string?, string> tryAdd)
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var appPaths = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                if (appPaths is null) continue;
                foreach (var exeName in appPaths.GetSubKeyNames())
                {
                    using var sub = appPaths.OpenSubKey(exeName);
                    var path = sub?.GetValue("")?.ToString();
                    var label = Path.GetFileNameWithoutExtension(exeName);
                    tryAdd(label, path, "Registriert (App Paths)");
                }
            }
            catch
            {
                // ignore hive access issues
            }
        }
    }

    private static void AddFromStartMenu(Action<string?, string?, string> tryAdd)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        };

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(file);
                    if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                    {
                        var target = ResolveShortcutTarget(file);
                        tryAdd(Path.GetFileNameWithoutExtension(file), target, "Startmenü");
                    }
                    else if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        tryAdd(Path.GetFileNameWithoutExtension(file), file, "Startmenü");
                    }
                }
            }
            catch
            {
                // skip unreadable start menu folders
            }
        }
    }

    private static void AddFromUninstallRegistry(Action<string?, string?, string> tryAdd)
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var path in new[]
                     {
                         @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                         @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                     })
            {
                try
                {
                    using var uninstall = hive.OpenSubKey(path);
                    if (uninstall is null) continue;
                    foreach (var subName in uninstall.GetSubKeyNames())
                    {
                        using var sub = uninstall.OpenSubKey(subName);
                        if (sub is null) continue;
                        if (sub.GetValue("SystemComponent") is int sc && sc == 1) continue;

                        var name = sub.GetValue("DisplayName")?.ToString();
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        var icon = ParseIconPath(sub.GetValue("DisplayIcon")?.ToString());
                        if (!string.IsNullOrWhiteSpace(icon))
                        {
                            tryAdd(name, icon, "Installiert");
                            continue;
                        }

                        var install = sub.GetValue("InstallLocation")?.ToString()?.Trim().Trim('"');
                        if (string.IsNullOrWhiteSpace(install) || !Directory.Exists(install)) continue;

                        foreach (var exe in Directory.EnumerateFiles(install, "*.exe", SearchOption.TopDirectoryOnly).Take(3))
                        {
                            tryAdd(name, exe, "Installiert");
                        }
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    private static string? ParseIconPath(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        displayIcon = displayIcon.Trim().Trim('"');
        var comma = displayIcon.IndexOf(',');
        if (comma > 0)
        {
            displayIcon = displayIcon[..comma].Trim().Trim('"');
        }

        return displayIcon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? displayIcon : null;
    }

    internal static string? ResolveShortcutTarget(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return File.Exists(path) ? path : null;
        }

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(path);
            string target = link.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            return null;
        }
    }
}
