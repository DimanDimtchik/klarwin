using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace KlarWin.Services;

public static class InstalledAppsCatalogService
{
    public static IReadOnlyList<InstalledAppEntry> ListApps(int maxCount = 5000)
    {
        var byExe = new Dictionary<string, InstalledAppEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in StartMenuFolders())
            CollectShortcutsFromFolder(folder, "Startmenü", byExe);

        foreach (var folder in DesktopFolders())
        {
            CollectShortcutsFromFolder(folder, "Desktop", byExe);
            CollectExeFilesFromFolder(folder, "Desktop", byExe, recursive: false);
        }

        foreach (var folder in PinnedShortcutFolders())
            CollectShortcutsFromFolder(folder, "Angeheftet", byExe);

        CollectFromLocalPrograms(byExe);
        CollectFromUninstallRegistry(byExe);

        foreach (var (name, path) in WellKnownApps())
            TryAdd(byExe, name, path, "Häufig");

        var limit = Math.Max(50, maxCount);
        return byExe.Values
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .ToList();
    }

    private static IEnumerable<string> StartMenuFolders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
    }

    private static IEnumerable<string> DesktopFolders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
    }

    private static IEnumerable<string> PinnedShortcutFolders()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(appData, "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");
        yield return Path.Combine(appData, "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "ImplicitAppShortcuts");
        yield return Path.Combine(appData, "Microsoft", "Internet Explorer", "Quick Launch");
    }

    private static void CollectShortcutsFromFolder(string folder, string source, Dictionary<string, InstalledAppEntry> byExe)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        foreach (var link in EnumerateFilesSafe(folder, "*.lnk"))
        {
            try
            {
                var target = ResolveShortcutTarget(link);
                if (string.IsNullOrWhiteSpace(target)) continue;

                var exe = AppIconHelper.ResolveExecutable(target);
                if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) continue;
                if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                var shortcutName = Path.GetFileNameWithoutExtension(link);
                if (IsJunk(exe, shortcutName)) continue;

                var name = string.IsNullOrWhiteSpace(shortcutName)
                    ? Path.GetFileNameWithoutExtension(exe)
                    : shortcutName;

                TryAdd(byExe, name.Trim(), exe, source);
            }
            catch
            {
                // skip broken shortcuts
            }
        }
    }

    private static void CollectExeFilesFromFolder(
        string folder,
        string source,
        Dictionary<string, InstalledAppEntry> byExe,
        bool recursive)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        IEnumerable<string> files = Array.Empty<string>();
        try
        {
            files = recursive
                ? EnumerateFilesSafe(folder, "*.exe")
                : Directory.EnumerateFiles(folder, "*.exe");
        }
        catch
        {
            return;
        }

        foreach (var exe in files)
        {
            try
            {
                if (!File.Exists(exe)) continue;
                var leaf = Path.GetFileName(exe);
                if (IsJunkFileName(leaf)) continue;
                var name = AppFriendlyName.DescribeExecutable(exe);
                if (string.IsNullOrWhiteSpace(name))
                    name = Path.GetFileNameWithoutExtension(exe);
                if (IsJunk(exe, name)) continue;
                TryAdd(byExe, name, exe, source);
            }
            catch
            {
                // skip
            }
        }
    }

    /// <summary>
    /// Many Electron/user apps live only under %LocalAppData%\Programs (Discord, Slack, Notion, …)
    /// and never appear in the classic Uninstall registry or Start Menu until first launch.
    /// </summary>
    private static void CollectFromLocalPrograms(Dictionary<string, InstalledAppEntry> byExe)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs");
        if (!Directory.Exists(root)) return;

        // Direct exes in Programs root
        CollectExeFilesFromFolder(root, "LocalAppData\\Programs", byExe, recursive: false);

        IEnumerable<string> dirs = Array.Empty<string>();
        try { dirs = Directory.EnumerateDirectories(root); }
        catch { return; }

        foreach (var dir in dirs)
        {
            try
            {
                var folderName = Path.GetFileName(dir);
                var preferred = PickPreferredExe(dir, folderName);
                if (!string.IsNullOrWhiteSpace(preferred))
                {
                    var label = AppFriendlyName.DescribeExecutable(preferred);
                    if (string.IsNullOrWhiteSpace(label) || label.Length < 2)
                        label = folderName;
                    TryAdd(byExe, label, preferred, "LocalAppData\\Programs");
                }

                // One level deeper (e.g. vendor\app\app.exe)
                foreach (var sub in Directory.EnumerateDirectories(dir).Take(12))
                {
                    var subPreferred = PickPreferredExe(sub, Path.GetFileName(sub));
                    if (string.IsNullOrWhiteSpace(subPreferred)) continue;
                    var label = AppFriendlyName.DescribeExecutable(subPreferred);
                    if (string.IsNullOrWhiteSpace(label) || label.Length < 2)
                        label = Path.GetFileName(sub);
                    TryAdd(byExe, label, subPreferred, "LocalAppData\\Programs");
                }
            }
            catch
            {
                // access denied
            }
        }
    }

    private static string? PickPreferredExe(string directory, string hintName)
    {
        string[] exes;
        try
        {
            exes = Directory.GetFiles(directory, "*.exe", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return null;
        }

        if (exes.Length == 0) return null;

        var usable = exes
            .Where(e => !IsJunkFileName(Path.GetFileName(e)))
            .ToList();
        if (usable.Count == 0) return null;

        var hint = (hintName ?? "").Replace(" ", "", StringComparison.Ordinal);
        var byName = usable.FirstOrDefault(e =>
        {
            var leaf = Path.GetFileNameWithoutExtension(e).Replace(" ", "", StringComparison.Ordinal);
            return leaf.Equals(hint, StringComparison.OrdinalIgnoreCase)
                   || hint.Contains(leaf, StringComparison.OrdinalIgnoreCase)
                   || leaf.Contains(hint, StringComparison.OrdinalIgnoreCase);
        });
        if (!string.IsNullOrWhiteSpace(byName)) return Path.GetFullPath(byName);

        // Prefer larger main binaries over tiny helpers/crashpad stubs
        return usable
            .Select(e =>
            {
                long size = 0;
                try { size = new FileInfo(e).Length; } catch { /* ignore */ }
                return (Path: e, Size: size);
            })
            .OrderByDescending(x => x.Size)
            .Select(x => Path.GetFullPath(x.Path))
            .FirstOrDefault();
    }

    private static void CollectFromUninstallRegistry(Dictionary<string, InstalledAppEntry> byExe)
    {
        string[] roots =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        ];

        foreach (var root in roots)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(root);
                if (key is null) continue;
                foreach (var subName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = key.OpenSubKey(subName);
                        if (sub is null) continue;
                        var displayName = sub.GetValue("DisplayName")?.ToString()?.Trim();
                        if (string.IsNullOrWhiteSpace(displayName)) continue;
                        if (sub.GetValue("SystemComponent") is int sc && sc == 1) continue;

                        var exe = FindExeFromUninstall(sub);
                        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) continue;
                        if (IsJunk(exe, displayName)) continue;
                        TryAdd(byExe, displayName, exe, "Installiert");
                    }
                    catch
                    {
                        // skip
                    }
                }
            }
            catch
            {
                // skip hive
            }
        }

        try
        {
            using var userKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (userKey is null) return;
            foreach (var subName in userKey.GetSubKeyNames())
            {
                try
                {
                    using var sub = userKey.OpenSubKey(subName);
                    if (sub is null) continue;
                    var displayName = sub.GetValue("DisplayName")?.ToString()?.Trim();
                    if (string.IsNullOrWhiteSpace(displayName)) continue;
                    var exe = FindExeFromUninstall(sub);
                    if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) continue;
                    if (IsJunk(exe, displayName)) continue;
                    TryAdd(byExe, displayName, exe, "Installiert");
                }
                catch
                {
                    // skip
                }
            }
        }
        catch
        {
            // optional
        }
    }

    private static string FindExeFromUninstall(RegistryKey sub)
    {
        var displayIcon = sub.GetValue("DisplayIcon")?.ToString() ?? "";
        if (!string.IsNullOrWhiteSpace(displayIcon))
        {
            var iconPath = displayIcon.Split(',')[0].Trim().Trim('"');
            if (iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(iconPath))
                return Path.GetFullPath(iconPath);
        }

        var location = sub.GetValue("InstallLocation")?.ToString()?.Trim().Trim('"') ?? "";
        if (!string.IsNullOrWhiteSpace(location) && Directory.Exists(location))
        {
            var preferred = PickPreferredExe(location, sub.GetValue("DisplayName")?.ToString() ?? "");
            if (!string.IsNullOrWhiteSpace(preferred)) return preferred;
        }

        return "";
    }

    private static void TryAdd(Dictionary<string, InstalledAppEntry> byExe, string name, string exePath, string source)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return;
            var key = Path.GetFullPath(exePath);
            if (byExe.ContainsKey(key)) return;
            byExe[key] = new InstalledAppEntry
            {
                Name = name,
                ExePath = key,
                Source = source
            };
        }
        catch
        {
            // ignore
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, string pattern)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> files = Array.Empty<string>();
            try { files = Directory.EnumerateFiles(dir, pattern); }
            catch { /* access denied */ }

            foreach (var file in files)
                yield return file;

            IEnumerable<string> subs = Array.Empty<string>();
            try { subs = Directory.EnumerateDirectories(dir); }
            catch { /* access denied */ }

            foreach (var sub in subs)
                stack.Push(sub);
        }
    }

    /// <summary>Resolve .lnk via WScript.Shell (reliable for modern shortcuts).</summary>
    public static string ResolveShortcutTarget(string lnkPath)
    {
        if (string.IsNullOrWhiteSpace(lnkPath) || !File.Exists(lnkPath)) return "";
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return AppIconHelper.ResolveExecutable(lnkPath);

            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic shortcut = shell.CreateShortcut(lnkPath);
                try
                {
                    string target = shortcut.TargetPath ?? "";
                    if (!string.IsNullOrWhiteSpace(target))
                        return Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
                }
                finally
                {
                    Marshal.FinalReleaseComObject(shortcut);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
        catch
        {
            // fallback to lightweight parser
        }

        return AppIconHelper.ResolveExecutable(lnkPath);
    }

    private static bool IsJunkFileName(string file)
    {
        if (file.Equals("uninstall.exe", StringComparison.OrdinalIgnoreCase)) return true;
        if (file.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) return true;
        if (file.Equals("setup.exe", StringComparison.OrdinalIgnoreCase)) return true;
        if (file.Equals("installer.exe", StringComparison.OrdinalIgnoreCase)) return true;
        if (file.Equals("update.exe", StringComparison.OrdinalIgnoreCase)) return true;
        if (file.Equals("crashpad_handler.exe", StringComparison.OrdinalIgnoreCase)) return true;
        if (file.Equals("elevation_service.exe", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsJunk(string exe, string shortcutName)
    {
        var file = Path.GetFileName(exe);
        if (IsJunkFileName(file)) return true;
        if (shortcutName.Contains("deinstall", StringComparison.OrdinalIgnoreCase)) return true;
        if (shortcutName.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) return true;
        if (shortcutName.Contains("deinstallieren", StringComparison.OrdinalIgnoreCase)) return true;
        // Only treat clear uninstall wording as junk — not generic "entfernen" / "Hilfe"
        // (those substrings hid legitimate apps from the Autostart picker).
        if (shortcutName.Contains("Readme", StringComparison.OrdinalIgnoreCase)) return true;
        if (shortcutName.Contains("Release Notes", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static IEnumerable<(string Name, string Path)> WellKnownApps()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        yield return ("Google Chrome", Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"));
        yield return ("Google Chrome", Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"));
        yield return ("Microsoft Edge", Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe"));
        yield return ("Firefox", Path.Combine(pf, "Mozilla Firefox", "firefox.exe"));
        yield return ("Cursor", Path.Combine(local, "Programs", "cursor", "Cursor.exe"));
        yield return ("Visual Studio Code", Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"));
        yield return ("Notepad", Path.Combine(Environment.SystemDirectory, "notepad.exe"));
        yield return ("Rechner", Path.Combine(Environment.SystemDirectory, "calc.exe"));
    }
}
