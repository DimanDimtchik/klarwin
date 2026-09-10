using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace KlarWin.Services;

/// <summary>Adds/removes KlarWin in the current-user Windows Run key for Startlayout-on-login.</summary>
public static class StartLayoutAutostartHelper
{
    public const string RunValueName = "KlarWin Startlayout";

    public static string ResolveExePath()
    {
        var canonical = DesktopShortcutService.CanonicalInstallExePath();
        if (File.Exists(canonical)) return canonical;
        return Environment.ProcessPath
               ?? Process.GetCurrentProcess().MainModule?.FileName
               ?? "";
    }

    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue(RunValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    public static string EnsureRegistered()
    {
        var exe = ResolveExePath();
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            return "KlarWin.exe nicht gefunden — Autostart nicht gesetzt.";

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)
                            ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            key.SetValue(RunValueName, $"\"{exe}\"");
            return $"KlarWin startet mit Windows ({exe}).";
        }
        catch (Exception ex)
        {
            return "Autostart konnte nicht gesetzt werden: " + ex.Message;
        }
    }

    public static string RemoveRegistration()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key is null) return "Kein Autostart-Eintrag vorhanden.";
            if (key.GetValue(RunValueName) is null) return "Kein KlarWin-Startlayout-Autostart vorhanden.";
            key.DeleteValue(RunValueName, false);
            return "KlarWin-Startlayout aus dem Windows-Autostart entfernt.";
        }
        catch (Exception ex)
        {
            return "Autostart konnte nicht entfernt werden: " + ex.Message;
        }
    }
}
