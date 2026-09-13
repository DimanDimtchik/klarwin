using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace KlarWin.Services;

public sealed class BrowserDownloadSettings
{
    public bool Enabled { get; set; }
    /// <summary>Stable Chrome/Edge extension ID (force-install). Kept for compatibility.</summary>
    public string ExtensionId { get; set; } = BrowserExtensionInstallService.StableExtensionId;
    public List<string> EnabledBrowserIds { get; set; } = [];
    public List<BrowserDownloadRule> Rules { get; set; } = [];
}

public sealed class BrowserDownloadRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Hostname fragment, e.g. lexoffice.de or shiftbase.com</summary>
    public string HostPattern { get; set; } = "";
    public string FolderId { get; set; } = "";
    /// <summary>
    /// Optional label matching the extension popup (e.g. Arbeit / Privat).
    /// Empty = applies to every profile label in that browser profile.
    /// </summary>
    public string ProfileKey { get; set; } = "";
}

/// <summary>Registers Native Messaging + serves Chrome/Edge download relocate requests.</summary>
public static class BrowserDownloadBridge
{
    public const string HostName = "de.ganzsoft.klarwin.download";
    public const string NativeArg = "--native-messaging-host";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static string ExtensionSourceDir
    {
        get
        {
            foreach (var candidate in ExtensionSourceCandidates())
            {
                if (HasManifest(candidate))
                    return candidate;
            }

            return ExtensionInstallDir;
        }
    }

    private static IEnumerable<string> ExtensionSourceCandidates()
    {
        string? exeDir = null;
        try
        {
            var exe = ExePath;
            if (!string.IsNullOrWhiteSpace(exe))
                exeDir = Path.GetDirectoryName(exe);
        }
        catch { /* ignore */ }

        if (!string.IsNullOrWhiteSpace(exeDir))
            yield return Path.Combine(exeDir, "browser-extension");

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KlarWin", "browser-extension");

        yield return Path.Combine(AppContext.BaseDirectory, "browser-extension");

        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "browser-extension", "klarwin-download"));
    }

    private static bool HasManifest(string dir) =>
        !string.IsNullOrWhiteSpace(dir)
        && Directory.Exists(dir)
        && File.Exists(Path.Combine(dir, "manifest.json"));

    public static string ExtensionInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KlarWin", "browser-extension");

    public static string ExePath
    {
        get
        {
            try
            {
                var p = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(p) && File.Exists(p)) return p;
            }
            catch { /* ignore */ }

            return Path.Combine(AppContext.BaseDirectory, "KlarWin.exe");
        }
    }

    public static void EnsureExtensionFilesCopied()
    {
        var dst = ExtensionInstallDir;
        Directory.CreateDirectory(dst);

        foreach (var src in ExtensionSourceCandidates())
        {
            if (!HasManifest(src)) continue;
            if (PathsEqual(src, dst)) break;
            CopyTree(src, dst);
            if (HasManifest(dst)) return;
        }

        if (HasManifest(dst)) return;
        ExtractEmbeddedExtensionZip(dst);
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\', '/'),
                Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void CopyTree(string src, string dst)
    {
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void ExtractEmbeddedExtensionZip(string dst)
    {
        var asm = typeof(BrowserDownloadBridge).Assembly;
        Stream? stream = asm.GetManifestResourceStream("KlarWin.Assets.browser-extension.zip");
        if (stream is null)
        {
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("browser-extension.zip", StringComparison.OrdinalIgnoreCase));
            if (name is null) return;
            stream = asm.GetManifestResourceStream(name);
        }

        if (stream is null) return;
        using (stream)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "klarwin-ext-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                using (var fs = File.Create(tmp))
                    stream.CopyTo(fs);
                System.IO.Compression.ZipFile.ExtractToDirectory(tmp, dst, overwriteFiles: true);
            }
            finally
            {
                try { File.Delete(tmp); } catch { /* ignore */ }
            }
        }
    }
    public static (bool Ok, string Message) RegisterNativeHost(string? extensionId)
    {
        if (string.IsNullOrWhiteSpace(extensionId))
            return (false, "Bitte zuerst die Extension-ID aus chrome://extensions bzw. edge://extensions eintragen.");

        var id = extensionId.Trim();
        EnsureExtensionFilesCopied();

        var hostJsonDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KlarWin");
        Directory.CreateDirectory(hostJsonDir);
        var hostJsonPath = Path.Combine(hostJsonDir, $"{HostName}.json");
        var launcherPath = Path.Combine(hostJsonDir, $"{HostName}.cmd");
        // Chrome Native Messaging allows no argv — use a tiny launcher.
        File.WriteAllText(launcherPath,
            "@echo off\r\n" +
            $"\"{ExePath}\" {NativeArg}\r\n");

        var manifest = new
        {
            name = HostName,
            description = "KlarWin Download-Ziele",
            path = launcherPath,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{id}/" }
        };
        File.WriteAllText(hostJsonPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        WriteNativeHostKey(@"Software\Google\Chrome\NativeMessagingHosts", hostJsonPath);
        WriteNativeHostKey(@"Software\Microsoft\Edge\NativeMessagingHosts", hostJsonPath);

        return (true,
            "Native Messaging registriert für Chrome und Edge.\n\n" +
            $"Host: {HostName}\nExtension-ID: {id}\n\n" +
            "Erweiterung aus diesem Ordner als „Entpackt laden“:\n" + ExtensionInstallDir);
    }

    public static void UnregisterNativeHost()
    {
        try
        {
            using var chrome = Registry.CurrentUser.OpenSubKey(@"Software\Google\Chrome\NativeMessagingHosts", true);
            chrome?.DeleteValue(HostName, false);
            chrome?.DeleteSubKeyTree(HostName, false);
        }
        catch { /* optional */ }

        try
        {
            using var edge = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Edge\NativeMessagingHosts", true);
            edge?.DeleteValue(HostName, false);
            edge?.DeleteSubKeyTree(HostName, false);
        }
        catch { /* optional */ }

        // Chrome expects a subkey named after the host with default value = path to json
        DeleteHostSubKey(@"Software\Google\Chrome\NativeMessagingHosts");
        DeleteHostSubKey(@"Software\Microsoft\Edge\NativeMessagingHosts");
    }

    private static void WriteNativeHostKey(string parentPath, string hostJsonPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{parentPath}\{HostName}");
        key.SetValue("", hostJsonPath, RegistryValueKind.String);
    }

    private static void DeleteHostSubKey(string parentPath)
    {
        try
        {
            using var parent = Registry.CurrentUser.OpenSubKey(parentPath, true);
            parent?.DeleteSubKeyTree(HostName, false);
        }
        catch { /* ignore */ }
    }

    public static void OpenExtensionFolder()
    {
        EnsureExtensionFilesCopied();
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{ExtensionInstallDir}\"",
            UseShellExecute = true
        });
    }

    public static TransferFolderTarget? ResolveFolder(
        KlarWinSettings settings,
        string? url,
        string? referrer,
        string? profileKey)
    {
        if (!settings.BrowserDownload.Enabled || settings.BrowserDownload.Rules.Count == 0)
            return null;

        var host = ExtractHost(url) ?? ExtractHost(referrer) ?? "";
        if (string.IsNullOrEmpty(host)) return null;

        var profile = (profileKey ?? "").Trim();
        foreach (var rule in settings.BrowserDownload.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.HostPattern)) continue;
            if (!HostMatches(host, rule.HostPattern)) continue;
            if (!string.IsNullOrWhiteSpace(rule.ProfileKey)
                && !rule.ProfileKey.Trim().Equals(profile, StringComparison.OrdinalIgnoreCase))
                continue;

            var folder = settings.TransferFolders.FirstOrDefault(f => f.Id == rule.FolderId);
            if (folder is not null && TransferFolderService.DirectoryExists(folder.Path))
                return folder;
        }

        return null;
    }

    public static bool HostMatches(string host, string pattern)
    {
        host = host.Trim().ToLowerInvariant();
        pattern = pattern.Trim().ToLowerInvariant();
        if (pattern.StartsWith("*."))
            return host.EndsWith(pattern[1..], StringComparison.Ordinal)
                || host.Equals(pattern[2..], StringComparison.Ordinal);
        return host == pattern || host.EndsWith("." + pattern, StringComparison.Ordinal);
    }

    public static string? ExtractHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
            return uri.Host;
        }
        catch
        {
            return null;
        }
    }

    public static (bool Ok, string Message) RelocateDownload(string sourceFile, TransferFolderTarget folder)
    {
        if (!File.Exists(sourceFile))
            return (false, "Download-Datei nicht gefunden.");

        var destDir = TransferFolderService.ResolvePath(folder.Path);
        if (string.IsNullOrWhiteSpace(destDir))
            return (false, "Zielordner fehlt.");

        try
        {
            if (!Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            var name = Path.GetFileName(sourceFile);
            var dest = Path.Combine(destDir, name);
            if (File.Exists(dest))
            {
                var stem = Path.GetFileNameWithoutExtension(name);
                var ext = Path.GetExtension(name);
                dest = Path.Combine(destDir, $"{stem}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
            }

            File.Move(sourceFile, dest);
            return (true, $"Verschoben nach {folder.Name}: {dest}");
        }
        catch (Exception ex)
        {
            return (false, UserFriendlyErrors.Translate(ex));
        }
    }

    /// <summary>Chrome Native Messaging stdio loop (no UI).</summary>
    public static int RunNativeMessagingHost()
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();
            while (true)
            {
                var msg = ReadMessage(stdin);
                if (msg is null) break;

                using var doc = JsonDocument.Parse(msg);
                var root = doc.RootElement;
                var action = root.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";

                object response;
                var settings = KlarWinSettings.Load();

                if (action.Equals("ping", StringComparison.OrdinalIgnoreCase))
                {
                    response = new { ok = true, version = UpdateService.CurrentVersion, enabled = settings.BrowserDownload.Enabled };
                }
                else if (action.Equals("getRules", StringComparison.OrdinalIgnoreCase))
                {
                    response = new
                    {
                        ok = true,
                        enabled = settings.BrowserDownload.Enabled,
                        rules = settings.BrowserDownload.Rules.Select(r => new
                        {
                            r.Id,
                            hostPattern = r.HostPattern,
                            profileKey = r.ProfileKey,
                            folderName = settings.TransferFolders.FirstOrDefault(f => f.Id == r.FolderId)?.Name ?? ""
                        })
                    };
                }
                else if (action.Equals("relocate", StringComparison.OrdinalIgnoreCase))
                {
                    var filePath = root.TryGetProperty("filePath", out var fp) ? fp.GetString() : null;
                    var url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
                    var referrer = root.TryGetProperty("referrer", out var r) ? r.GetString() : null;
                    var profileKey = root.TryGetProperty("profileKey", out var pk) ? pk.GetString() : null;

                    if (!settings.BrowserDownload.Enabled)
                    {
                        response = new { ok = false, message = "Browser-Download in KlarWin ist aus." };
                    }
                    else if (string.IsNullOrWhiteSpace(filePath))
                    {
                        response = new { ok = false, message = "Kein Dateipfad." };
                    }
                    else
                    {
                        var folder = ResolveFolder(settings, url, referrer, profileKey);
                        if (folder is null)
                            response = new { ok = true, moved = false, message = "Keine passende Regel." };
                        else
                        {
                            var (ok, message) = RelocateDownload(filePath, folder);
                            response = new { ok, moved = ok, message, folder = folder.Name };
                        }
                    }
                }
                else
                {
                    response = new { ok = false, message = $"Unbekannte Aktion: {action}" };
                }

                WriteMessage(stdout, JsonSerializer.Serialize(response, JsonOpts));
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Error("Native Messaging Host", ex);
            return 1;
        }

        return 0;
    }

    private static string? ReadMessage(Stream stdin)
    {
        var lenBuf = new byte[4];
        var read = stdin.Read(lenBuf, 0, 4);
        if (read == 0) return null;
        if (read < 4) return null;
        var len = BitConverter.ToInt32(lenBuf, 0);
        if (len <= 0 || len > 1024 * 1024) return null;
        var buf = new byte[len];
        var off = 0;
        while (off < len)
        {
            var n = stdin.Read(buf, off, len - off);
            if (n <= 0) return null;
            off += n;
        }

        return Encoding.UTF8.GetString(buf);
    }

    private static void WriteMessage(Stream stdout, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        stdout.Write(BitConverter.GetBytes(bytes.Length), 0, 4);
        stdout.Write(bytes, 0, bytes.Length);
        stdout.Flush();
    }
}