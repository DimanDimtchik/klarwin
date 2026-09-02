using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace KlarWin.Services;

public static class WindowsIsoDownloadService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromHours(6)
    };

    public static string FidoScriptPath()
    {
        var baseDir = AppContext.BaseDirectory;
        return Path.Combine(baseDir, "Tools", "Fido.ps1");
    }

    public static async Task<(bool Success, string Message, string? Url)> ResolveDownloadUrlAsync(
        WindowsIsoRecommendation recommendation,
        CancellationToken cancellationToken = default)
    {
        var fidoPath = FidoScriptPath();
        if (!File.Exists(fidoPath))
        {
            return (false, "ISO-Hilfsskript fehlt (Tools/Fido.ps1). Bitte KlarWin neu installieren.", null);
        }

        var args = new StringBuilder();
        args.Append("-NoProfile -ExecutionPolicy Bypass -File ");
        args.Append('"').Append(fidoPath).Append('"');
        args.Append(" -Win \"").Append(recommendation.FidoWindowsArg).Append('"');
        args.Append(" -Rel Latest");
        args.Append(" -Ed \"").Append(recommendation.FidoEditionArg).Append('"');
        args.Append(" -Lang \"").Append(recommendation.FidoLanguageArg).Append('"');
        args.Append(" -Arch \"").Append(recommendation.FidoArchitectureArg).Append('"');
        args.Append(" -PlatformArch \"").Append(recommendation.FidoArchitectureArg).Append('"');
        args.Append(" -GetUrl");

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = args.ToString(),
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();

            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                return (false, string.IsNullOrWhiteSpace(detail)
                    ? "Microsoft-ISO-Link konnte nicht ermittelt werden."
                    : detail, null);
            }

            var url = ExtractUrl(stdout);
            if (string.IsNullOrWhiteSpace(url))
            {
                return (false, "Kein Download-Link von Microsoft erhalten.", null);
            }

            return (true, "Download-Link ermittelt.", url);
        }
        catch (Exception ex)
        {
            return (false, UserFriendlyErrors.Translate(ex), null);
        }
    }

    public static async Task<(bool Success, string Message, string? FilePath)> DownloadIsoAsync(
        WindowsIsoRecommendation recommendation,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report("Passende ISO wird bei Microsoft angefragt …");
        var (resolved, resolveMessage, url) = await ResolveDownloadUrlAsync(recommendation, cancellationToken);
        if (!resolved || string.IsNullOrWhiteSpace(url))
        {
            return (false, resolveMessage, null);
        }

        var downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        downloads = Path.Combine(downloads, "Downloads");
        Directory.CreateDirectory(downloads);
        var targetPath = Path.Combine(downloads, WindowsIsoRecommendationService.SuggestFileName(recommendation));
        if (File.Exists(targetPath))
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
            var name = Path.GetFileNameWithoutExtension(targetPath);
            targetPath = Path.Combine(downloads, $"{name}_{stamp}.iso");
        }

        progress?.Report("ISO wird geladen (mehrere GB, bitte warten) …");

        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[1024 * 256];
            long readTotal = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                readTotal += read;
                if (total > 0 && readTotal % (1024 * 1024 * 50) < read)
                {
                    var pct = readTotal * 100.0 / total;
                    progress?.Report($"Download … {ByteSizeFormatter.Format(readTotal)} von {ByteSizeFormatter.Format(total)} ({pct:0} %)");
                }
            }

            progress?.Report("Download abgeschlossen.");
            return (true, $"ISO gespeichert:\n{targetPath}\n\nNächster Schritt: USB-Stick unter „USB-Stick“ vorbereiten und ISO z. B. mit Rufus auf den Stick schreiben.", targetPath);
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(targetPath)) File.Delete(targetPath);
            }
            catch
            {
                // ignore cleanup errors
            }

            return (false, UserFriendlyErrors.Translate(ex), null);
        }
    }

    private static string ExtractUrl(string stdout)
    {
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return line.Trim();
            }
        }

        var match = Regex.Match(stdout, @"https://[^\s""']+", RegexOptions.IgnoreCase);
        return match.Success ? match.Value : stdout.Trim();
    }
}
