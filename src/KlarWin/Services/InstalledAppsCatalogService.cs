using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace KlarWin.Services;

public static class InstalledAppsCatalogService
{
    public static IReadOnlyList<InstalledAppEntry> ListApps(int maxCount = 800)
    {
        var byExe = new Dictionary<string, InstalledAppEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in StartMenuFolders())
            CollectShortcutsFromFolder(folder, "Startmenü", byExe);

        foreach (var folder in DesktopFolders())
            CollectShortcutsFromFolder(folder, "Desktop", byExe);

        CollectFromUninstallRegistry(byExe);

        foreach (var (name, path) in WellKnownApps())
            TryAdd(byExe, name, path, "Häufig");

        return byExe.Values
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Max(50, maxCount))
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
            try
            {
                var exes = Directory.GetFiles(location, "*.exe", SearchOption.TopDirectoryOnly);
                var preferred = exes.FirstOrDefault(e => !IsJunkFileName(Path.GetFileName(e)));
                if (!string.IsNullOrWhiteSpace(preferred)) return Path.GetFullPath(preferred);
            }
            catch
            {
                // ignore
            }
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
        return false;
    }

    private static bool IsJunk(string exe, string shortcutName)
    {
        var file = Path.GetFileName(exe);
        if (IsJunkFileName(file)) return true;
        if (shortcutName.Contains("deinstall", StringComparison.OrdinalIgnoreCase)) return true;
        if (shortcutName.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) return true;
        if (shortcutName.Contains("deinstallieren", StringComparison.OrdinalIgnoreCase)) return true;
        if (shortcutName.Contains("entfernen", StringComparison.OrdinalIgnoreCase)) return true;
        if (shortcutName.Contains("Hilfe", StringComparison.OrdinalIgnoreCase)) return true;
        if (shortcutName.Equals("Help", StringComparison.OrdinalIgnoreCase)) return true;
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
