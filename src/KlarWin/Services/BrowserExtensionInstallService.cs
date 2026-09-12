using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace KlarWin.Services;

public sealed record InstalledBrowser(
    string Id,
    string DisplayName,
    string ExePath,
    string PolicyRegistryPath);

public static class BrowserDetectionService
{
    public static IReadOnlyList<InstalledBrowser> DetectChromiumBrowsers()
    {
        var list = new List<InstalledBrowser>();

        TryAdd(list, "chrome", "Google Chrome",
            @"Software\Policies\Google\Chrome\ExtensionInstallForcelist",
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
            ]);

        TryAdd(list, "edge", "Microsoft Edge",
            @"Software\Policies\Microsoft\Edge\ExtensionInstallForcelist",
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe")
            ]);

        TryAdd(list, "brave", "Brave",
            @"Software\Policies\BraveSoftware\Brave\ExtensionInstallForcelist",
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BraveSoftware", "Brave-Browser", "Application", "brave.exe")
            ]);

        TryAdd(list, "chromium", "Chromium",
            @"Software\Policies\Chromium\ExtensionInstallForcelist",
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chromium", "Application", "chrome.exe")
            ]);

        return list;
    }

    private static void TryAdd(
        List<InstalledBrowser> list,
        string id,
        string displayName,
        string policyPath,
        IEnumerable<string> candidates)
    {
        foreach (var path in candidates)
        {
            try
            {
                if (!File.Exists(path)) continue;
                list.Add(new InstalledBrowser(id, displayName, path, policyPath));
                return;
            }
            catch
            {
                // skip
            }
        }
    }
}

public static class BrowserExtensionInstallService
{
    public const string StableExtensionId = "jjamdgbcfclmlpmnfblalkdliomnickp";
    public const string UpdateManifestUrl = "https://ganz-soft.de/klarwin/extension/updates.xml";
    public const string UpdateManifestUrlMirror = "https://dg.ganz-soft.de/klarwin/extension/updates.xml";

    public static (bool Ok, string Message) EnableForBrowsers(IEnumerable<InstalledBrowser> browsers)
    {
        var selected = browsers.ToList();
        if (selected.Count == 0)
            return (false, "Kein Browser ausgewählt.");

        try
        {
            BrowserDownloadBridge.EnsureExtensionFilesCopied();
        }
        catch (Exception ex)
        {
            return (false, "Erweiterungsdateien konnten nicht bereitgestellt werden:\n" + UserFriendlyErrors.Translate(ex));
        }

        var (hostOk, hostMsg) = BrowserDownloadBridge.RegisterNativeHost(StableExtensionId);
        if (!hostOk)
            return (false, hostMsg);

        var forceValue = $"{StableExtensionId};{UpdateManifestUrl}";
        var okNames = new List<string>();
        var deniedNames = new List<string>();
        var shortcutNames = new List<string>();

        foreach (var browser in selected)
        {
            if (TryWriteForceInstall(browser.PolicyRegistryPath, forceValue, out var regError))
            {
                okNames.Add(browser.DisplayName);
            }
            else
            {
                deniedNames.Add(browser.DisplayName);
                if (TryCreateLoadExtensionShortcut(browser))
                    shortcutNames.Add(browser.DisplayName);
                DiagnosticLogService.Info(
                    $"ExtensionInstallForcelist verweigert für {browser.DisplayName}: {regError}");
            }
        }

        if (okNames.Count > 0 && deniedNames.Count == 0)
        {
            return (true,
                "Erweiterung für: " + string.Join(", ", okNames) + ".\n\n" +
                "Bitte die ausgewählten Browser einmal komplett schließen und neu öffnen.\n" +
                "Die KlarWin-Erweiterung wird dann automatisch installiert.\n\n" +
                hostMsg);
        }

        if (okNames.Count > 0)
        {
            return (true,
                "Teilweise eingerichtet.\n\n" +
                "Per Richtlinie: " + string.Join(", ", okNames) + " (Browser neu starten).\n" +
                "Gesperrt (Richtlinie): " + string.Join(", ", deniedNames) + ".\n" +
                (shortcutNames.Count > 0
                    ? "Desktop-Verknüpfung „KlarWin + …“ angelegt — damit startet der Browser mit geladener Erweiterung.\n"
                    : "Bitte für die gesperrten Browser „Manuell (Entpackt)“ nutzen.\n") +
                "\n" + hostMsg);
        }

        // Policy completely blocked — still OK if shortcuts or manual path works
        var sb = "Die Windows-Richtlinie für automatische Installation ist gesperrt " +
                 "(oft durch Firmen-/Sicherheitsrichtlinien).\n\n";
        if (shortcutNames.Count > 0)
        {
            sb += "Stattdessen: Desktop-Verknüpfung „KlarWin + Browser“ angelegt.\n" +
                  "Darüber starten — dann ist die Erweiterung geladen.\n\n" +
                  "Betroffen: " + string.Join(", ", shortcutNames) + "\n\n";
            return (true, sb + hostMsg);
        }

        sb += "Bitte „Manuell (Entpackt)“ verwenden:\n" +
              BrowserDownloadBridge.ExtensionInstallDir + "\n\n" + hostMsg;
        return (false, sb);
    }

    public static void DisableForAllKnownBrowsers()
    {
        foreach (var browser in BrowserDetectionService.DetectChromiumBrowsers())
            ClearForceInstall(browser.PolicyRegistryPath);

        ClearForceInstall(@"Software\Policies\Google\Chrome\ExtensionInstallForcelist");
        ClearForceInstall(@"Software\Policies\Microsoft\Edge\ExtensionInstallForcelist");
        ClearForceInstall(@"Software\Policies\BraveSoftware\Brave\ExtensionInstallForcelist");
        ClearForceInstall(@"Software\Policies\Chromium\ExtensionInstallForcelist");

        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            foreach (var file in Directory.EnumerateFiles(desktop, "KlarWin + *.lnk"))
                File.Delete(file);
        }
        catch
        {
            // optional
        }
    }

    private static bool TryWriteForceInstall(string policySubPath, string value, out string error)
    {
        error = "";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(policySubPath, writable: true);
            if (key is null)
            {
                error = "Registry-Schlüssel konnte nicht geöffnet werden.";
                return false;
            }

            key.SetValue("1", value, RegistryValueKind.String);
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (SecurityException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void ClearForceInstall(string policySubPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(policySubPath, writable: true);
            if (key is null) return;
            var val = key.GetValue("1") as string;
            if (val is not null && val.StartsWith(StableExtensionId, StringComparison.OrdinalIgnoreCase))
                key.DeleteValue("1", throwOnMissingValue: false);
        }
        catch
        {
            // optional
        }
    }

    /// <summary>
    /// Fallback when policy registry is locked: desktop shortcut with --load-extension.
    /// </summary>
    private static bool TryCreateLoadExtensionShortcut(InstalledBrowser browser)
    {
        try
        {
            BrowserDownloadBridge.EnsureExtensionFilesCopied();
            var extDir = BrowserDownloadBridge.ExtensionInstallDir;
            if (!File.Exists(Path.Combine(extDir, "manifest.json")))
                return false;

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var linkPath = Path.Combine(desktop, $"KlarWin + {browser.DisplayName}.lnk");
            var args = $"--load-extension=\"{extDir}\"";
            var script =
                "$ws = New-Object -ComObject WScript.Shell; " +
                $"$s = $ws.CreateShortcut('{linkPath.Replace("'", "''")}'); " +
                $"$s.TargetPath = '{browser.ExePath.Replace("'", "''")}'; " +
                $"$s.Arguments = '{args.Replace("'", "''")}'; " +
                $"$s.WorkingDirectory = '{Path.GetDirectoryName(browser.ExePath)?.Replace("'", "''") ?? ""}'; " +
                "$s.Description = 'Chrome/Edge mit KlarWin-Erweiterung'; " +
                "$s.Save()";
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            })?.WaitForExit(8000);
            return File.Exists(linkPath);
        }
        catch
        {
            return false;
        }
    }

    public static void TryCloseBrowsers(IEnumerable<InstalledBrowser> browsers)
    {
        foreach (var browser in browsers.DistinctBy(b => b.Id))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(browser.ExePath);
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { p.CloseMainWindow(); } catch { /* ignore */ }
                }
            }
            catch
            {
                // ignore
            }
        }
    }
}
