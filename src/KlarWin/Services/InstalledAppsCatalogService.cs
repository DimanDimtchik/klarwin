using System.IO;

namespace KlarWin.Services;

public static class InstalledAppsCatalogService
{
    public static IReadOnlyList<InstalledAppEntry> ListApps(int maxCount = 250)
    {
        var byExe = new Dictionary<string, InstalledAppEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in StartMenuFolders())
            CollectFromFolder(folder, "Startmenü", byExe);

        foreach (var folder in DesktopFolders())
            CollectFromFolder(folder, "Desktop", byExe);

        // Prefer well-known apps even if deeper than Start Menu scan
        foreach (var (name, path) in WellKnownApps())
        {
            if (!File.Exists(path)) continue;
            var key = Path.GetFullPath(path);
            if (!byExe.ContainsKey(key))
            {
                byExe[key] = new InstalledAppEntry
                {
                    Name = name,
                    ExePath = key,
                    Source = "Häufig"
                };
            }
        }

        return byExe.Values
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(maxCount)
            .ToList();
    }

    private static IEnumerable<string> StartMenuFolders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (!string.IsNullOrWhiteSpace(programs)) yield return programs;
        var commonPrograms = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        if (!string.IsNullOrWhiteSpace(commonPrograms)) yield return commonPrograms;
    }

    private static IEnumerable<string> DesktopFolders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
    }

    private static void CollectFromFolder(string folder, string source, Dictionary<string, InstalledAppEntry> byExe)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        string[] links;
        try
        {
            links = Directory.GetFiles(folder, "*.lnk", SearchOption.AllDirectories);
        }
        catch
        {
            return;
        }

        foreach (var link in links)
        {
            try
            {
                var exe = AppIconHelper.ResolveExecutable(link);
                if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) continue;
                if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsJunk(exe, Path.GetFileNameWithoutExtension(link))) continue;

                var key = Path.GetFullPath(exe);
                if (byExe.ContainsKey(key)) continue;

                var name = Path.GetFileNameWithoutExtension(link);
                if (string.IsNullOrWhiteSpace(name))
                    name = Path.GetFileNameWithoutExtension(exe);

                byExe[key] = new InstalledAppEntry
                {
                    Name = name.Trim(),
                    ExePath = key,
                    Source = source
                };
            }
            catch
            {
                // skip broken shortcuts
            }
        }
    }

    private static bool IsJunk(string exe, string shortcutName)
    {
        var file = Path.GetFileName(exe);
        if (file.Equals("uninstall.exe", StringComparison.OrdinalIgnoreCase)) return true;
        if (file.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) return true;
        if (file.Contains("setup", StringComparison.OrdinalIgnoreCase)) return true;
        if (shortcutName.Contains("deinstall", StringComparison.OrdinalIgnoreCase)) return true;
        if (shortcutName.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) return true;
        if (shortcutName.Contains("Hilfe", StringComparison.OrdinalIgnoreCase)) return true;
        if (shortcutName.Contains("Help", StringComparison.OrdinalIgnoreCase)) return true;
        if (shortcutName.Contains("Readme", StringComparison.OrdinalIgnoreCase)) return true;
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
