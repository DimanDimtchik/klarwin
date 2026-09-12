using System.Diagnostics;
using System.IO;
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

        BrowserDownloadBridge.EnsureExtensionFilesCopied();
        var (hostOk, hostMsg) = BrowserDownloadBridge.RegisterNativeHost(StableExtensionId);
        if (!hostOk)
            return (false, hostMsg);

        var forceValue = $"{StableExtensionId};{UpdateManifestUrl}";
        var names = new List<string>();
        foreach (var browser in selected)
        {
            WriteForceInstall(browser.PolicyRegistryPath, forceValue);
            names.Add(browser.DisplayName);
        }

        return (true,
            "Erweiterung für: " + string.Join(", ", names) + ".\n\n" +
            "Bitte die ausgewählten Browser einmal komplett schließen und neu öffnen.\n" +
            "Die KlarWin-Erweiterung wird dann automatisch installiert (Richtlinie).\n\n" +
            hostMsg);
    }

    public static void DisableForAllKnownBrowsers()
    {
        foreach (var browser in BrowserDetectionService.DetectChromiumBrowsers())
            ClearForceInstall(browser.PolicyRegistryPath);

        // Also clear even if browser was uninstalled
        ClearForceInstall(@"Software\Policies\Google\Chrome\ExtensionInstallForcelist");
        ClearForceInstall(@"Software\Policies\Microsoft\Edge\ExtensionInstallForcelist");
        ClearForceInstall(@"Software\Policies\BraveSoftware\Brave\ExtensionInstallForcelist");
        ClearForceInstall(@"Software\Policies\Chromium\ExtensionInstallForcelist");
    }

    private static void WriteForceInstall(string policySubPath, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(policySubPath);
        // Keep KlarWin entry as "1"; leave other enterprise entries alone if numbered higher
        key.SetValue("1", value, RegistryValueKind.String);
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
