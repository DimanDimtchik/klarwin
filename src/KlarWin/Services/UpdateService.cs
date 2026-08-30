using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
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
    public const string ManifestUrlPrimary = "https://dg.ganz-soft.de/klarwin/version.json";
    public const string ManifestUrlFallback = "https://ganz-soft.de/klarwin/version.json";
    public const string ManifestUrlGithub = "https://github.com/DimanDimtchik/klarwin/releases/latest/download/version.json";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

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
            var url = root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
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
                    ? "https://dg.ganz-soft.de/klarwin/KlarWin-Setup.zip"
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

        var work = Path.Combine(Path.GetTempPath(), "KlarWinUpdate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var zipPath = Path.Combine(work, "KlarWin-Setup.zip");
        progress?.Report("Update wird geladen …");

        await using (var remote = await Http.GetStreamAsync(info.DownloadUrl, cancellationToken))
        await using (var file = File.Create(zipPath))
        {
            await remote.CopyToAsync(file, cancellationToken);
        }

        progress?.Report("Paket wird entpackt …");
        ZipFile.ExtractToDirectory(zipPath, work, overwriteFiles: true);
        var newExe = Directory.EnumerateFiles(work, "KlarWin.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (newExe is null)
        {
            return "Im Update-Paket fehlt KlarWin.exe.";
        }

        var targetDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var targetExe = Path.Combine(targetDir, "KlarWin.exe");
        var staged = Path.Combine(work, "KlarWin.exe.new");
        File.Copy(newExe, staged, overwrite: true);

        var bat = Path.Combine(work, "apply-update.cmd");
        var batContent =
            "@echo off\r\n" +
            "timeout /t 2 /nobreak >nul\r\n" +
            $"copy /Y \"{staged}\" \"{targetExe}\" >nul\r\n" +
            $"start \"\" \"{targetExe}\"\r\n" +
            $"rd /s /q \"{work}\"\r\n";
        await File.WriteAllTextAsync(bat, batContent, cancellationToken);

        progress?.Report("KlarWin startet neu mit der neuen Version …");
        Process.Start(new ProcessStartInfo
        {
            FileName = bat,
            UseShellExecute = true,
            WorkingDirectory = work,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        return "UPDATE wird angewendet. KlarWin startet gleich neu.";
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
