using System.IO;
using System.Text;

namespace KlarWin.Services;

public static class DesktopShortcutService
{
    public static string DesktopShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "KlarWin.lnk");

    public static string StartMenuShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "KlarWin.lnk");

    public static bool DesktopShortcutExists() => File.Exists(DesktopShortcutPath);

    public static string ResolveInstalledExePath()
    {
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KlarWin",
            "KlarWin.exe");
        if (File.Exists(local))
        {
            return local;
        }

        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath) && File.Exists(Environment.ProcessPath))
        {
            return Environment.ProcessPath;
        }

        return Path.Combine(AppContext.BaseDirectory, "KlarWin.exe");
    }

    public static string CanonicalInstallExePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KlarWin",
            "KlarWin.exe");

    public static string CanonicalInstallDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KlarWin");

    public static string ResolveInstallDirectory() =>
        Path.GetDirectoryName(ResolveInstalledExePath())!;

    public static void EnsureDesktopShortcut(bool visible)
    {
        if (visible)
        {
            CreateShortcuts();
        }
        else
        {
            TryDelete(DesktopShortcutPath);
        }
    }

    public static void CreateShortcuts()
    {
        var exe = CanonicalInstallExePath();
        if (!File.Exists(exe))
        {
            exe = ResolveInstalledExePath();
        }

        if (!File.Exists(exe))
        {
            throw new FileNotFoundException("KlarWin.exe nicht gefunden.", exe);
        }

        CreateShortcutsTo(exe);
    }

    public static void CreateShortcutsTo(string exePath)
    {
        var workDir = Path.GetDirectoryName(exePath)!;
        var icon = exePath + ",0";
        CreateShellLink(DesktopShortcutPath, exePath, workDir, icon, "KlarWin");
        CreateShellLink(StartMenuShortcutPath, exePath, workDir, icon, "KlarWin");
    }

    public static void EnsureShortcutsPointToCanonical()
    {
        var canonical = CanonicalInstallExePath();
        if (!File.Exists(canonical)) return;
        if (!ShortcutNeedsRepair(canonical)) return;
        CreateShortcutsTo(canonical);
    }

    public static bool ShortcutNeedsRepair(string canonicalExe)
    {
        foreach (var shortcut in new[] { DesktopShortcutPath, StartMenuShortcutPath })
        {
            if (!File.Exists(shortcut)) return true;
            var target = ReadShortcutTarget(shortcut);
            if (string.IsNullOrWhiteSpace(target)) return true;
            if (!string.Equals(
                    Path.GetFullPath(target),
                    Path.GetFullPath(canonicalExe),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string? ReadShortcutTarget(string shortcutPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            var shell = Activator.CreateInstance(shellType);
            if (shell is null) return null;
            dynamic shortcut = shell.GetType().InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                shell,
                [shortcutPath])!;
            return shortcut.TargetPath as string;
        }
        catch
        {
            return null;
        }
    }

    public static string BuildCreateShortcutCommand(string shortcutPath, string targetExe, string workDir)
    {
        var icon = targetExe + ",0";
        var script = new StringBuilder()
            .Append("$ws = New-Object -ComObject WScript.Shell; ")
            .Append("$s = $ws.CreateShortcut('").Append(EscapePs(shortcutPath)).Append("'); ")
            .Append("$s.TargetPath = '").Append(EscapePs(targetExe)).Append("'; ")
            .Append("$s.WorkingDirectory = '").Append(EscapePs(workDir)).Append("'; ")
            .Append("$s.IconLocation = '").Append(EscapePs(icon)).Append("'; ")
            .Append("$s.Description = 'KlarWin'; ")
            .Append("$s.Save()");
        return "powershell -NoProfile -ExecutionPolicy Bypass -Command \"" + script.ToString().Replace("\"", "\\\"") + "\"";
    }

    private static void CreateShellLink(string shortcutPath, string target, string workDir, string icon, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var script = new StringBuilder()
            .Append("$ws = New-Object -ComObject WScript.Shell; ")
            .Append("$s = $ws.CreateShortcut('").Append(EscapePs(shortcutPath)).Append("'); ")
            .Append("$s.TargetPath = '").Append(EscapePs(target)).Append("'; ")
            .Append("$s.WorkingDirectory = '").Append(EscapePs(workDir)).Append("'; ")
            .Append("$s.IconLocation = '").Append(EscapePs(icon)).Append("'; ")
            .Append("$s.Description = '").Append(EscapePs(description)).Append("'; ")
            .Append("$s.Save()");
        RunHiddenPowerShell(script.ToString());
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // optional
        }
    }

    private static void RunHiddenPowerShell(string command)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" + command.Replace("\"", "\\\"") + "\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = System.Diagnostics.Process.Start(psi);
        p?.WaitForExit(15000);
    }

    private static string EscapePs(string value) => value.Replace("'", "''");
}
