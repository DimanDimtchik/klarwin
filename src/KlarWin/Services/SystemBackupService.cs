using System.Diagnostics;
using System.IO;
using System.Management;
using Microsoft.Win32;

namespace KlarWin.Services;

public static class SystemBackupService
{
    public static bool IsSystemRestoreEnabled()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT DisableSR FROM Win32_SystemRestore");
            foreach (var obj in searcher.Get())
            {
                if (obj["DisableSR"] is bool disabled)
                {
                    return !disabled;
                }
            }
        }
        catch
        {
            // ignore
        }

        return true;
    }

    public static (bool Success, string Message) CreateRestorePoint(string description)
    {
        if (!ShortcutOverlayService.IsAdministrator())
        {
            return (false, "Administratorrechte nötig — oben rechts „Adminrechte freischalten“.");
        }

        if (!IsSystemRestoreEnabled())
        {
            return (false, "Systemwiederherstellung ist auf diesem PC deaktiviert. Windows-Einstellungen → System → Info → Systemschutz prüfen.");
        }

        try
        {
            using var restore = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
            using var inParams = restore.GetMethodParameters("CreateRestorePoint");
            inParams["Description"] = description;
            inParams["RestorePointType"] = 12;
            inParams["EventType"] = 100;
            restore.InvokeMethod("CreateRestorePoint", inParams, null);
            return (true, "Wiederherstellungspunkt wurde erstellt.");
        }
        catch (Exception ex)
        {
            return (false, UserFriendlyErrors.Translate(ex));
        }
    }

    public static void OpenInstallMediaDownloadPage()
    {
        var url = DetectWindows11OrNewer()
            ? "https://www.microsoft.com/software-download/windows11"
            : "https://www.microsoft.com/software-download/windows10";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public static void OpenSystemProtectionSettings()
    {
        Process.Start(new ProcessStartInfo("SystemPropertiesProtection.exe") { UseShellExecute = true });
    }

    public static bool TryLaunchMediaCreationTool()
    {
        foreach (var path in FindMediaCreationToolPaths())
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }
            catch
            {
                // try next
            }
        }

        return false;
    }

    private static IEnumerable<string> FindMediaCreationToolPaths()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        yield return Path.Combine(downloads, "MediaCreationTool.exe");
        yield return Path.Combine(downloads, "MediaCreationTool22H2.exe");
        yield return Path.Combine(downloads, "MediaCreationTool_Win11_23H2.exe");

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Path.Combine(desktop, "MediaCreationTool.exe");
    }

    private static bool DetectWindows11OrNewer()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key?.GetValue("CurrentBuild") is string buildStr && int.TryParse(buildStr, out var build))
            {
                return build >= 22000;
            }
        }
        catch
        {
            // ignore
        }

        return Environment.OSVersion.Version.Build >= 22000;
    }
}
