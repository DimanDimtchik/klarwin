using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KlarWin.Services;

public sealed class BrowserProfileInfo
{
    public string BrowserName { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    public string ProfileDirectory { get; init; } = "";
    public string ProfileName { get; init; } = "";
    public string Email { get; init; } = "";

    public string Arguments =>
        $"--profile-directory=\"{ProfileDirectory}\"";

    public string DisplayName
    {
        get
        {
            var who = string.IsNullOrWhiteSpace(Email)
                ? ProfileName
                : $"{ProfileName} ({Email})";
            if (string.IsNullOrWhiteSpace(who))
                who = ProfileDirectory;
            return $"{who} — {BrowserName}";
        }
    }
}

/// <summary>Chrome / Edge / Brave user profiles for per-account autostart.</summary>
public static class BrowserProfileCatalogService
{
    private static readonly Regex ProfileDirArg = new(
        @"--profile-directory=(?:""(?<d>[^""]+)""|(?<d>\S+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<BrowserProfileInfo> ListProfiles()
    {
        var list = new List<BrowserProfileInfo>();
        foreach (var browser in KnownBrowsers())
        {
            if (!File.Exists(browser.Exe)) continue;
            foreach (var profile in ReadProfiles(browser.UserData, browser.Name, browser.Exe))
                list.Add(profile);
        }

        return list
            .OrderBy(p => p.BrowserName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => p.ProfileName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static bool IsChromiumBrowserExe(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        var name = Path.GetFileName(exePath);
        return name.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase)
               || name.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase)
               || name.Equals("brave.exe", StringComparison.OrdinalIgnoreCase)
               || name.Equals("opera.exe", StringComparison.OrdinalIgnoreCase)
               || name.Equals("vivaldi.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static string? TryFormatAutostartLabel(string commandOrShortcut)
    {
        var profileDir = ExtractProfileDirectory(commandOrShortcut);
        var exe = AutostartService.ExtractExecutablePath(commandOrShortcut);
        if (string.IsNullOrWhiteSpace(exe) && commandOrShortcut.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var target = AutostartCatalogService.ResolveShortcutTarget(commandOrShortcut);
            if (!string.IsNullOrWhiteSpace(target))
            {
                exe = AutostartService.ExtractExecutablePath(target);
                // Arguments live on the .lnk — resolve via WScript
                profileDir ??= ExtractProfileDirectoryFromShortcut(commandOrShortcut);
            }
        }

        if (string.IsNullOrWhiteSpace(profileDir) || string.IsNullOrWhiteSpace(exe))
            return null;

        var browser = GuessBrowserName(exe);
        foreach (var p in ListProfiles())
        {
            if (p.ExecutablePath.Equals(Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)
                && p.ProfileDirectory.Equals(profileDir, StringComparison.OrdinalIgnoreCase))
                return p.DisplayName;
        }

        return $"{profileDir} — {browser}";
    }

    public static string? ExtractProfileDirectory(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var m = ProfileDirArg.Match(command);
        return m.Success ? m.Groups["d"].Value.Trim() : null;
    }

    private static string? ExtractProfileDirectoryFromShortcut(string linkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(linkPath);
            string args = link.Arguments ?? "";
            return ExtractProfileDirectory(args);
        }
        catch
        {
            return null;
        }
    }

    private static string GuessBrowserName(string exe)
    {
        var n = Path.GetFileName(exe);
        if (n.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase)) return "Microsoft Edge";
        if (n.Equals("brave.exe", StringComparison.OrdinalIgnoreCase)) return "Brave";
        if (n.Equals("opera.exe", StringComparison.OrdinalIgnoreCase)) return "Opera";
        if (n.Equals("vivaldi.exe", StringComparison.OrdinalIgnoreCase)) return "Vivaldi";
        return "Google Chrome";
    }

    private static IEnumerable<(string Name, string Exe, string UserData)> KnownBrowsers()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        yield return ("Google Chrome",
            FirstExisting(
                Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe")),
            Path.Combine(local, "Google", "Chrome", "User Data"));

        yield return ("Microsoft Edge",
            FirstExisting(
                Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe")),
            Path.Combine(local, "Microsoft", "Edge", "User Data"));

        yield return ("Brave",
            FirstExisting(
                Path.Combine(pf, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(local, "BraveSoftware", "Brave-Browser", "Application", "brave.exe")),
            Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"));

        yield return ("Opera",
            FirstExisting(
                Path.Combine(pf, "Opera", "opera.exe"),
                Path.Combine(local, "Programs", "Opera", "opera.exe")),
            Path.Combine(local, "Opera Software", "Opera Stable"));

        yield return ("Vivaldi",
            FirstExisting(
                Path.Combine(pf, "Vivaldi", "Application", "vivaldi.exe"),
                Path.Combine(local, "Vivaldi", "Application", "vivaldi.exe")),
            Path.Combine(local, "Vivaldi", "User Data"));
    }

    private static string FirstExisting(params string[] paths)
    {
        foreach (var p in paths)
        {
            if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                return p;
        }

        return paths.FirstOrDefault() ?? "";
    }

    private static IEnumerable<BrowserProfileInfo> ReadProfiles(string userData, string browserName, string exe)
    {
        if (string.IsNullOrWhiteSpace(userData) || !Directory.Exists(userData) || !File.Exists(exe))
            yield break;

        var cache = ReadInfoCache(Path.Combine(userData, "Local State"));
        var dirs = new List<string>();
        if (Directory.Exists(Path.Combine(userData, "Default")))
            dirs.Add("Default");
        try
        {
            dirs.AddRange(Directory.EnumerateDirectories(userData, "Profile *")
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrWhiteSpace(n))!);
        }
        catch
        {
            // ignore
        }

        exe = Path.GetFullPath(exe);
        foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cache.TryGetValue(dir, out var meta);
            var name = meta.Name;
            if (string.IsNullOrWhiteSpace(name))
                name = dir.Equals("Default", StringComparison.OrdinalIgnoreCase) ? "Standardprofil" : dir;

            yield return new BrowserProfileInfo
            {
                BrowserName = browserName,
                ExecutablePath = exe,
                ProfileDirectory = dir,
                ProfileName = name,
                Email = meta.Email ?? ""
            };
        }
    }

    private static Dictionary<string, (string Name, string Email)> ReadInfoCache(string localStatePath)
    {
        var map = new Dictionary<string, (string Name, string Email)>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(localStatePath)) return map;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(localStatePath));
            if (!doc.RootElement.TryGetProperty("profile", out var profile)) return map;
            if (!profile.TryGetProperty("info_cache", out var cache)) return map;

            foreach (var prop in cache.EnumerateObject())
            {
                var name = prop.Value.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var email = "";
                if (prop.Value.TryGetProperty("user_name", out var u))
                    email = u.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(email) && prop.Value.TryGetProperty("gaia_name", out var g))
                    email = g.GetString() ?? "";
                map[prop.Name] = (name, email);
            }
        }
        catch
        {
            // Local State may be locked/partial
        }

        return map;
    }
}
