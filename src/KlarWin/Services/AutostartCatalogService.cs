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

            arguments = (arguments ?? "").Trim();
            var isExplorer = Path.GetFileName(path).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase);
            var isStoreAppsFolderLaunch = isExplorer
                && arguments.Contains("shell:AppsFolder", StringComparison.OrdinalIgnoreCase);

            // Store/UWP apps (BlueMail etc.) launch via explorer + shell:AppsFolder\AUMID —
            // do not treat those as junk explorer stubs or System32 binaries.
            if (!isStoreAppsFolderLaunch && IsWindowsSystemBinary(path))
                return;

            name = name.Trim();
            if (name.Length == 0)
                name = Path.GetFileNameWithoutExtension(path);

            var key = string.IsNullOrWhiteSpace(arguments) ? path : $"{path}|{arguments}";

            // Only hide by path (+ args), never by display name — same title for different
            // EXEs (or a generic Autostart name) previously hid unrelated apps from the picker.
            if (already.Contains(key))
                return;

            // Skip trivial stubs that only wrap explorer (keep Store AppsFolder launches)
            if (isExplorer && !isStoreAppsFolderLaunch)
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

        // High ceiling: alphabetical Take(1200) previously dropped late-alphabet apps.
        foreach (var app in InstalledAppsCatalogService.ListApps(8000))
            TryAdd(app.Name, app.ExePath, string.IsNullOrWhiteSpace(app.Source) ? "Installiert" : app.Source);

        void TryAddPlain(string? name, string? exePath, string source) => TryAdd(name, exePath, source);
        AddFromAppPaths(TryAddPlain);
        AddFromWindowsApps(TryAddPlain);
        AddFromUninstallRegistry(TryAddPlain);
        AddFromLocalPrograms(TryAddPlain);
        // Store/UWP (BlueMail etc.): real EXE lives under protected Program Files\WindowsApps;
        // shell:AppsFolder is the reliable launch path Autostart can use.
        AddFromAppsFolder(TryAdd);
        AddFromProgramFilesWindowsApps(TryAddPlain);

        return byKey.Values
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static HashSet<string> CollectAlreadyAutostart(IReadOnlyList<AutostartEntry> entries)
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
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

        return identities;
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

        // Store AppsFolder shortcuts: identity is explorer.exe + shell:AppsFolder\AUMID
        var appsFolder = ExtractAppsFolderArgument(argsOrCommand);
        if (!string.IsNullOrWhiteSpace(appsFolder))
        {
            identities.Add($"{exe}|{appsFolder}");
            identities.Add($"{exe}|shell:AppsFolder\\{StripAppsFolderPrefix(appsFolder)}");
            return;
        }

        // Non-browser or browser without profile: path alone
        if (!BrowserProfileCatalogService.IsChromiumBrowserExe(exe))
            identities.Add(exe);
    }

    private static string? ExtractAppsFolderArgument(string argsOrCommand)
    {
        if (string.IsNullOrWhiteSpace(argsOrCommand)) return null;
        var idx = argsOrCommand.IndexOf("shell:AppsFolder", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        return argsOrCommand[idx..].Trim().Trim('"');
    }

    private static string StripAppsFolderPrefix(string appsFolderArg)
    {
        const string prefix = "shell:AppsFolder\\";
        const string prefixSlash = "shell:AppsFolder/";
        if (appsFolderArg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return appsFolderArg[prefix.Length..];
        if (appsFolderArg.StartsWith(prefixSlash, StringComparison.OrdinalIgnoreCase))
            return appsFolderArg[prefixSlash.Length..];
        return appsFolderArg;
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
                    foreach (var exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).Take(16))
                    {
                        var leaf = Path.GetFileNameWithoutExtension(exe);
                        var folder = Path.GetFileName(dir);
                        if (leaf.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
                            || leaf.Equals("crashpad_handler", StringComparison.OrdinalIgnoreCase))
                            continue;

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
                        else
                        {
                            var fromMeta = AppFriendlyName.DescribeExecutable(exe);
                            if (!string.IsNullOrWhiteSpace(fromMeta) && fromMeta.Length > label.Length)
                                label = fromMeta;
                        }

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

    /// <summary>Direct scan of %LocalAppData%\Programs — same source InstalledApps uses, kept here as a safety net.</summary>
    private static void AddFromLocalPrograms(Action<string?, string?, string> tryAdd)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs");
        if (!Directory.Exists(root)) return;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                try
                {
                    var folder = Path.GetFileName(dir);
                    foreach (var exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).Take(8))
                    {
                        var leaf = Path.GetFileName(exe);
                        if (leaf.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
                            || leaf.Equals("crashpad_handler.exe", StringComparison.OrdinalIgnoreCase)
                            || leaf.Equals("update.exe", StringComparison.OrdinalIgnoreCase)
                            || leaf.Equals("setup.exe", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var label = AppFriendlyName.DescribeExecutable(exe);
                        if (string.IsNullOrWhiteSpace(label) || label.Length < 2)
                            label = folder;
                        tryAdd(label, exe, "LocalAppData\\Programs");
                    }
                }
                catch
                {
                    // access denied
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Enumerate Microsoft Store / packaged apps via shell:AppsFolder.
    /// Needed for apps like BlueMail whose EXE sits in ACL-protected
    /// C:\Program Files\WindowsApps\…\app\ and is not a normal Start-Menu target.
    /// </summary>
    private static void AddFromAppsFolder(Action<string?, string?, string, string?> tryAdd)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic folder = shell.Namespace("shell:AppsFolder");
            if (folder is null) return;

            var explorer = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "explorer.exe");
            if (!File.Exists(explorer)) return;

            foreach (dynamic item in folder.Items())
            {
                try
                {
                    string name = (item.Name as string)?.Trim() ?? "";
                    string itemPath = (item.Path as string)?.Trim() ?? "";
                    if (name.Length == 0 || itemPath.Length == 0) continue;
                    if (IsJunkAppsFolderEntry(name, itemPath)) continue;

                    var args = itemPath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                        ? itemPath
                        : "shell:AppsFolder\\" + itemPath.TrimStart('\\', '/');

                    // Friendly names for known Store packages whose AUMID is cryptic
                    if (itemPath.Contains("BlueMail", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("BlueMail", StringComparison.OrdinalIgnoreCase))
                        name = "BlueMail";

                    tryAdd(name, explorer, "Microsoft Store", args);
                }
                catch
                {
                    // skip broken shell items
                }
            }
        }
        catch
        {
            // Shell AppsFolder unavailable
        }
    }

    private static bool IsJunkAppsFolderEntry(string name, string path)
    {
        // Keep real apps; drop host/runtime shells that are useless as Autostart targets.
        if (name.Equals("Microsoft Edge", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("Runtime Broker", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("Shell Experience", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Contains("Microsoft.Windows.ShellExperienceHost", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Contains("Microsoft.AAD.BrokerPlugin", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Contains("MicrosoftWindows.Client.CBS", StringComparison.OrdinalIgnoreCase)
            && name.Contains("Windows-Sicherheit", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    /// <summary>
    /// Best-effort: find BlueMail.exe under Program Files\WindowsApps (often access-denied,
    /// but some machines allow listing package folders that match by name).
    /// </summary>
    private static void AddFromProgramFilesWindowsApps(Action<string?, string?, string> tryAdd)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "WindowsApps");
            if (!Directory.Exists(root)) return;

            IEnumerable<string> dirs;
            try
            {
                dirs = Directory.EnumerateDirectories(root, "*BlueMail*");
            }
            catch
            {
                return;
            }

            foreach (var dir in dirs)
            {
                try
                {
                    foreach (var exe in Directory.EnumerateFiles(dir, "BlueMail.exe", SearchOption.AllDirectories).Take(2))
                        tryAdd("BlueMail", exe, "Store (Program Files\\WindowsApps)");
                }
                catch
                {
                    // ACL on package contents
                }
            }
        }
        catch
        {
            // ignore
        }
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
