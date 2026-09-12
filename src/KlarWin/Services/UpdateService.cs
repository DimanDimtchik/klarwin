using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace KlarWin.Services;

public sealed class UpdateInfo
{
    public string CurrentVersion { get; init; } = "0.0.0";
    public string LatestVersion { get; init; } = "0.0.0";
    public bool UpdateAvailable { get; init; }
    public string Notes { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public string Message { get; init; } = "";
}

public sealed class UpdateService
{
    public const string ManifestUrlPrimary = "https://ganz-soft.de/klarwin/version.json";
    public const string ManifestUrlFallback = "https://dg.ganz-soft.de/klarwin/version.json";
    public const string ManifestUrlGithub = "https://github.com/DimanDimtchik/klarwin/releases/latest/download/version.json";

    public const string DownloadUrlPrimary = "https://ganz-soft.de/klarwin/KlarWin-Setup.zip";
    public const string DownloadUrlMirror = "https://dg.ganz-soft.de/klarwin/KlarWin-Setup.zip";

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
    }

    public static string UpdateLogPath =>
        Path.Combine(DesktopShortcutService.CanonicalInstallDirectory(), "update.log");

    public static string CurrentVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public async Task<UpdateInfo> CheckAsync(CancellationToken cancellationToken = default)
    {
        var current = CurrentVersion;
        try
        {
            var json = await GetManifestAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var latest = root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            var url = root.TryGetProperty("url", out var u) ? NormalizeDownloadUrl(u.GetString() ?? "") : "";
            var notes = root.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(latest))
            {
                return new UpdateInfo { CurrentVersion = current, Message = "Versionsangabe fehlt auf dem Server." };
            }

            var available = IsNewer(latest, current);
            return new UpdateInfo
            {
                CurrentVersion = current,
                LatestVersion = latest,
                UpdateAvailable = available,
                Notes = notes,
                DownloadUrl = string.IsNullOrWhiteSpace(url)
                    ? DownloadUrlPrimary
                    : url,
                Message = available
                    ? $"Update {latest} verfügbar (aktuell {current})."
                    : $"KlarWin ist aktuell ({current})."
            };
        }
        catch (Exception ex)
        {
            return new UpdateInfo
            {
                CurrentVersion = current,
                Message = "Update-Prüfung fehlgeschlagen: " + ex.Message
            };
        }
    }

    public async Task<string> DownloadAndPrepareRestartAsync(UpdateInfo info, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!info.UpdateAvailable || string.IsNullOrWhiteSpace(info.DownloadUrl))
        {
            return info.Message;
        }

        var targetExe = DesktopShortcutService.CanonicalInstallExePath();
        var targetDir = DesktopShortcutService.CanonicalInstallDirectory();
        Directory.CreateDirectory(targetDir);

        var work = Path.Combine(Path.GetTempPath(), "KlarWinUpdate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var zipPath = Path.Combine(work, "KlarWin-Setup.zip");
        var logPath = UpdateLogPath;

        try
        {
            string? staged = null;
            Exception? lastError = null;
            foreach (var downloadUrl in BuildDownloadCandidates(info))
            {
                try
                {
                    progress?.Report($"Update wird geladen …\n{downloadUrl}");
                    AppendLog(logPath, "Lade: " + downloadUrl);

                    await DownloadZipAsync(downloadUrl, zipPath, cancellationToken);

                    var zipInfo = new FileInfo(zipPath);
                    if (zipInfo.Length < 100_000)
                    {
                        AppendLog(logPath, "Paket zu klein, nächste Quelle …");
                        continue;
                    }

                    progress?.Report("Paket wird entpackt …");
                    var extractDir = Path.Combine(work, "extract");
                    if (Directory.Exists(extractDir))
                    {
                        Directory.Delete(extractDir, true);
                    }

                    Directory.CreateDirectory(extractDir);
                    ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
                    var newExe = Directory.EnumerateFiles(extractDir, "KlarWin.exe", SearchOption.AllDirectories)
                        .FirstOrDefault(f => !f.Contains(".new", StringComparison.OrdinalIgnoreCase));
                    if (newExe is null)
                    {
                        AppendLog(logPath, "Kein KlarWin.exe im Paket.");
                        continue;
                    }

                    if (!ExeMatchesExpectedVersion(newExe, info.LatestVersion))
                    {
                        var found = ReadExeVersion(newExe);
                        AppendLog(logPath, $"Paket-Version {found} passt nicht zu {info.LatestVersion}, nächste Quelle …");
                        progress?.Report($"Paket ist veraltet ({found}) — alternative Quelle …");
                        continue;
                    }

                    staged = Path.Combine(work, "KlarWin.exe.new");
                    File.Copy(newExe, staged, overwrite: true);

                    // Keep browser extension next to install + in Roaming for Chrome „Entpackt laden“
                    var extSrc = Directory.EnumerateDirectories(extractDir, "browser-extension", SearchOption.AllDirectories)
                        .FirstOrDefault(d => File.Exists(Path.Combine(d, "manifest.json")));
                    if (extSrc is not null)
                    {
                        var extBesideExe = Path.Combine(targetDir, "browser-extension");
                        CopyDirectory(extSrc, extBesideExe);
                        CopyDirectory(extSrc, BrowserDownloadBridge.ExtensionInstallDir);
                    }

                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    AppendLog(logPath, $"Fehler bei {downloadUrl}: {ex.Message}");
                    if (ex.InnerException is not null)
                    {
                        AppendLog(logPath, "Details: " + ex.InnerException.Message);
                    }
                }
            }

            if (staged is null || !File.Exists(staged))
            {
                var hint = lastError is not null
                    ? UserFriendlyErrors.Translate(lastError)
                    : "Keine Quelle lieferte ein gültiges Paket.";
                return "Update fehlgeschlagen: " + hint + "\n\n" +
                       "Setup manuell: https://ganz-soft.de/klarwin/KlarWin-Setup.zip\n" +
                       "Protokoll: " + logPath;
            }

            var pid = Environment.ProcessId;
            var bat = Path.Combine(work, "apply-update.cmd");
            var batContent = BuildUpdateBatch(staged, targetExe, targetDir, work, pid, logPath, info.LatestVersion);
            await File.WriteAllTextAsync(bat, batContent, cancellationToken);

            AppendLog(logPath, $"Update vorbereitet: {info.CurrentVersion} → {info.LatestVersion}");
            AppendLog(logPath, $"Ziel: {targetExe}");

            progress?.Report($"Update wird installiert nach {targetDir} …");
            var started = Process.Start(new ProcessStartInfo
            {
                FileName = bat,
                UseShellExecute = true,
                WorkingDirectory = work,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (started is null)
            {
                return "Update-Hilfsprogramm konnte nicht gestartet werden.\nProtokoll: " + logPath;
            }

            return "UPDATE_OK";
        }
        catch (Exception ex)
        {
            AppendLog(logPath, "Fehler: " + ex.Message);
            try { Directory.Delete(work, true); } catch { /* ignore */ }
            return "Update fehlgeschlagen: " + UserFriendlyErrors.Translate(ex) + "\nProtokoll: " + logPath;
        }
    }

    private static string BuildUpdateBatch(string staged, string targetExe, string targetDir, string work, int pid, string logPath, string version)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal");
        sb.AppendLine($"set \"LOG={logPath}\"");
        sb.AppendLine($"set \"TARGET={targetExe}\"");
        sb.AppendLine($"set \"DIR={targetDir}\"");
        sb.AppendLine($"echo [%date% %time%] Update {version} startet >> \"%LOG%\"");
        sb.AppendLine("timeout /t 2 /nobreak >nul");
        sb.AppendLine(":wait");
        sb.AppendLine($"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul");
        sb.AppendLine("if not errorlevel 1 (timeout /t 1 /nobreak >nul & goto wait)");
        sb.AppendLine("timeout /t 3 /nobreak >nul");
        sb.AppendLine($"taskkill /F /PID {pid} 2>nul");
        sb.AppendLine("taskkill /F /IM KlarWin.exe 2>nul");
        sb.AppendLine("timeout /t 2 /nobreak >nul");
        sb.AppendLine("mkdir \"%DIR%\" 2>nul");
        sb.AppendLine($"copy /Y \"{staged}\" \"%TARGET%\" >> \"%LOG%\" 2>&1");
        sb.AppendLine("if errorlevel 1 (");
        sb.AppendLine("  echo [%date% %time%] FEHLER: copy fehlgeschlagen >> \"%LOG%\"");
        sb.AppendLine("  exit /b 1");
        sb.AppendLine(")");
        sb.AppendLine("echo [%date% %time%] Kopie OK >> \"%LOG%\"");
        // Extension folder is copied by C# before bat runs (Roaming + beside exe if present)
        AppendShortcutRefreshCommands(sb, targetExe, targetDir);
        sb.AppendLine("timeout /t 2 /nobreak >nul");
        sb.AppendLine("start \"\" \"%TARGET%\" --post-update");
        sb.AppendLine("if errorlevel 1 (");
        sb.AppendLine("  echo [%date% %time%] FEHLER: Neustart fehlgeschlagen >> \"%LOG%\"");
        sb.AppendLine("  exit /b 2");
        sb.AppendLine(")");
        sb.AppendLine("echo [%date% %time%] KlarWin neu gestartet >> \"%LOG%\"");
        sb.AppendLine($"rd /s /q \"{work}\" 2>nul");
        sb.AppendLine("endlocal");
        return sb.ToString();
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var target = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void AppendLog(string path, string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch
        {
            // ignore
        }
    }

    private static IEnumerable<string> BuildDownloadCandidates(UpdateInfo info)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in new[]
                 {
                     DownloadUrlPrimary,
                     info.DownloadUrl,
                     DownloadUrlMirror
                 })
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            var withBuster = AppendCacheBuster(url, info.LatestVersion);
            if (seen.Add(withBuster)) yield return withBuster;
        }
    }

    private static string AppendCacheBuster(string url, string version)
    {
        var sep = url.Contains('?') ? '&' : '?';
        return $"{url}{sep}v={Uri.EscapeDataString(version)}";
    }

    private static async Task DownloadZipAsync(string url, string zipPath, CancellationToken cancellationToken)
    {
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var remote = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = File.Create(zipPath);
        await remote.CopyToAsync(file, cancellationToken);
    }

    private static bool ExeMatchesExpectedVersion(string exePath, string expectedVersion)
    {
        var found = ReadExeVersion(exePath);
        if (string.IsNullOrWhiteSpace(found)) return false;
        if (!Version.TryParse(Normalize(expectedVersion), out var expected)) return true;
        if (!Version.TryParse(Normalize(found), out var actual)) return false;
        return actual >= expected;
    }

    public static string ReadExeVersion(string exePath)
    {
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(exePath);
            return $"{vi.FileMajorPart}.{vi.FileMinorPart}.{vi.FileBuildPart}";
        }
        catch
        {
            return "";
        }
    }

    private static void AppendShortcutRefreshCommands(StringBuilder sb, string targetExe, string targetDir)
    {
        var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "KlarWin.lnk");
        var startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs",
            "KlarWin.lnk");
        foreach (var shortcut in new[] { desktop, startMenu })
        {
            sb.AppendLine(DesktopShortcutService.BuildCreateShortcutCommand(shortcut, targetExe, targetDir));
        }
    }

    private static string NormalizeDownloadUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || url.Contains("dg.ganz-soft.de", StringComparison.OrdinalIgnoreCase))
        {
            return DownloadUrlPrimary;
        }

        return url;
    }

    private static async Task<string> GetManifestAsync(CancellationToken cancellationToken)
    {
        Exception? last = null;
        foreach (var url in new[] { ManifestUrlPrimary, ManifestUrlFallback, ManifestUrlGithub })
        {
            try
            {
                using var response = await Http.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode) continue;
                return await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/DimanDimtchik/klarwin/releases/latest");
            request.Headers.TryAddWithoutValidation("User-Agent", "KlarWin");
            using var response = await Http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var apiJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(apiJson);
            var tag = doc.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
            var zip = doc.RootElement.GetProperty("assets").EnumerateArray()
                .Select(a => a.GetProperty("browser_download_url").GetString() ?? "")
                .FirstOrDefault(u => u.EndsWith("KlarWin-Setup.zip", StringComparison.OrdinalIgnoreCase))
                ?? $"https://github.com/DimanDimtchik/klarwin/releases/download/v{tag}/KlarWin-Setup.zip";
            return JsonSerializer.Serialize(new
            {
                version = tag,
                url = zip,
                notes = doc.RootElement.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                published = doc.RootElement.TryGetProperty("published_at", out var p) ? p.GetString() ?? "" : ""
            });
        }
        catch (Exception ex)
        {
            last = ex;
        }

        throw last ?? new InvalidOperationException("version.json nicht erreichbar.");
    }

    private static bool IsNewer(string remote, string local)
    {
        if (!Version.TryParse(Normalize(remote), out var r)) return false;
        if (!Version.TryParse(Normalize(local), out var l)) return true;
        return r > l;
    }

    private static string Normalize(string version)
    {
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        while (parts.Length < 3) parts = parts.Append("0").ToArray();
        return string.Join('.', parts.Take(4));
    }
}
