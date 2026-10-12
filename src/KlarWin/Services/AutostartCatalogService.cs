using System.IO;
using Microsoft.Win32;

namespace KlarWin.Services;

public sealed class AutostartCandidate
{
    public string Name { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    /// <summary>Optional process args (e.g. Chrome --profile-directory=…).</summary>
    public string Arguments { get; init; } = "";
    public string Source { get; init; } = "";

    public string IdentityKey =>
        string.IsNullOrWhiteSpace(Arguments)
            ? ExecutablePath
            : $"{ExecutablePath}|{Arguments}";
}

public sealed class AutostartCatalogService
{
    public IReadOnlyList<AutostartCandidate> DiscoverCandidates(IReadOnlyList<AutostartEntry> currentAutostart)
    {
        var already = CollectAlreadyAutostart(currentAutostart);
        var byKey = new Dictionary<string, AutostartCandidate>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? name, string? exePath, string source, string? arguments = null)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(exePath))
                return;

            var path = exePath.Trim().Trim('"');
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                if (!File.Exists(path))
                    return;
                path = Path.GetFullPath(path);
            }
            catch
            {
                return;
            }

            if (IsWindowsSystemBinary(path))
                return;

            name = name.Trim();
            if (name.Length == 0)
                name = Path.GetFileNameWithoutExtension(path);

            arguments = (arguments ?? "").Trim();
            var key = string.IsNullOrWhiteSpace(arguments) ? path : $"{path}|{arguments}";

            if (already.Identities.Contains(key) || already.Names.Contains(name))
                return;

            // Skip trivial stubs that only wrap explorer
            if (Path.GetFileName(path).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
                return;

            // Plain Chrome/Edge without profile is replaced by per-profile entries
            if (string.IsNullOrWhiteSpace(arguments) && BrowserProfileCatalogService.IsChromiumBrowserExe(path))
                return;

            if (byKey.TryGetValue(key, out var existing))
            {
                if (name.Length > existing.Name.Length)
                {
                    byKey[key] = new AutostartCandidate
                    {
                        Name = name,
                        ExecutablePath = path,
                        Arguments = arguments,
                        Source = source
                    };
                }

                return;
            }

            byKey[key] = new AutostartCandidate
            {
                Name = name,
                ExecutablePath = path,
                Arguments = arguments,
                Source = source
            };
        }

        foreach (var profile in BrowserProfileCatalogService.ListProfiles())
            TryAdd(profile.DisplayName, profile.ExecutablePath, $"{profile.BrowserName}-Profil", profile.Arguments);

        foreach (var app in InstalledAppsCatalogService.ListApps(1200))
            TryAdd(app.Name, app.ExePath, string.IsNullOrWhiteSpace(app.Source) ? "Installiert" : app.Source);

        void TryAddPlain(string? name, string? exePath, string source) => TryAdd(name, exePath, source);
        AddFromAppPaths(TryAddPlain);
        AddFromWindowsApps(TryAddPlain);
        AddFromUninstallRegistry(TryAddPlain);

        return byKey.Values
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static (HashSet<string> Identities, HashSet<string> Names) CollectAlreadyAutostart(
        IReadOnlyList<AutostartEntry> entries)
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.Name))
                names.Add(entry.Name.Trim());

            if (entry.Location.Contains("Ordner", StringComparison.Ordinal))
            {
                var link = entry.Command;
                var target = ResolveShortcutTarget(link) ?? link;
                var args = ResolveShortcutArguments(link) ?? "";
                var profile = BrowserProfileCatalogService.ExtractProfileDirectory(args)
                              ?? BrowserProfileCatalogService.ExtractProfileDirectory(target);
                TryAddIdentity(identities, AutostartService.ExtractExecutablePath(target) ?? target, args, profile);
                continue;
            }

            var exe = AutostartService.ExtractExecutablePath(entry.Command);
            var profileDir = BrowserProfileCatalogService.ExtractProfileDirectory(entry.Command);
            TryAddIdentity(identities, exe, entry.Command, profileDir);
        }

        return (identities, names);
    }

    private static void TryAddIdentity(HashSet<string> identities, string? exe, string argsOrCommand, string? profileDir)
    {
        if (string.IsNullOrWhiteSpace(exe)) return;
        try
        {
            if (!File.Exists(exe)) return;
            exe = Path.GetFullPath(exe);
        }
        catch
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(profileDir))
        {
            identities.Add($"{exe}|--profile-directory=\"{profileDir}\"");
            identities.Add($"{exe}|--profile-directory={profileDir}");
            return;
        }

        // Non-browser or browser without profile: path alone
        if (!BrowserProfileCatalogService.IsChromiumBrowserExe(exe))
            identities.Add(exe);
    }

    private static string? ResolveShortcutArguments(string linkPath)
    {
        if (!linkPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(linkPath);
            string args = link.Arguments ?? "";
            return string.IsNullOrWhiteSpace(args) ? null : args;
        }
        catch
        {
            return null;
        }
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

    private static void AddFromWindowsApps(Action<string?, string?, string> tryAdd)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps");
            if (!Directory.Exists(root)) return;

            foreach (var exe in Directory.EnumerateFiles(root, "*.exe", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(exe);
                if (name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("GameBar", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Xbox", StringComparison.OrdinalIgnoreCase))
                    continue;

                tryAdd(PrettyStoreName(name), exe, "Store / WindowsApps");
            }

            // Nested package folders (e.g. OpenAI.ChatGPT-Desktop_…\chatgpt-classic.exe)
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                try
                {
                    foreach (var exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).Take(4))
                    {
                        var leaf = Path.GetFileNameWithoutExtension(exe);
                        var folder = Path.GetFileName(dir);
                        var label = PrettyStoreName(leaf);
                        if (folder.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase)
                            || leaf.Contains("chatgpt", StringComparison.OrdinalIgnoreCase))
                            label = "ChatGPT";
                        else if (folder.Contains("BlueMail", StringComparison.OrdinalIgnoreCase))
                            label = "BlueMail";
                        else if (folder.Contains("Adobe", StringComparison.OrdinalIgnoreCase)
                                 && (folder.Contains("Express", StringComparison.OrdinalIgnoreCase)
                                     || leaf.Contains("Express", StringComparison.OrdinalIgnoreCase)))
                            label = "Adobe Express";

                        tryAdd(label, exe, "Store / WindowsApps");
                    }
                }
                catch
                {
                    // access denied on some package dirs
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private static string PrettyStoreName(string raw)
    {
        raw = raw.Replace('-', ' ').Replace('_', ' ').Trim();
        if (raw.EndsWith(" classic", StringComparison.OrdinalIgnoreCase))
            raw = raw[..^" classic".Length].Trim();
        return raw.Length == 0 ? "App" : raw;
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
                            tryAdd(name, exe, "Installiert");
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
            displayIcon = displayIcon[..comma].Trim().Trim('"');

        return displayIcon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? displayIcon : null;
    }

    internal static string? ResolveShortcutTarget(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            return File.Exists(path) ? path : null;

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
