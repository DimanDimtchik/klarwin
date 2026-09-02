using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace KlarWin.Services;

public static class PawnIoInstallService
{
    public const string OfficialDownloadUrl =
        "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";

    public static bool IsInstalled => LibreHardwareTemperatureReader.IsPawnIoInstalled;

    public static async Task<(bool Success, string Message)> InstallAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsInstalled)
        {
            return (true, "PawnIO ist bereits installiert. KlarWin neu starten, falls CPU/Mainboard-Temperatur noch fehlt.");
        }

        progress?.Report("Prüfe winget …");
        var winget = await TryWingetInstallAsync(progress, cancellationToken);
        if (winget.Success)
        {
            return winget;
        }

        progress?.Report("Lade PawnIO-Installer von GitHub …");
        var (downloadOk, downloadPath, downloadError) = await DownloadInstallerAsync(progress, cancellationToken);
        if (!downloadOk || downloadPath is null)
        {
            return (false, downloadError ?? "Download fehlgeschlagen.");
        }

        progress?.Report("Installationsassistent starten …");
        return LaunchInstaller(downloadPath);
    }

    private static async Task<(bool Success, string Message)> TryWingetInstallAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var winget = FindWingetPath();
            if (winget is null)
            {
                return (false, "winget nicht gefunden.");
            }

            progress?.Report("Installiere PawnIO über winget …");
            var psi = new ProcessStartInfo
            {
                FileName = winget,
                Arguments =
                    "install --id namazso.PawnIO --exact --accept-package-agreements --accept-source-agreements --disable-interactivity",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return (false, "winget konnte nicht gestartet werden.");
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0 || IsInstalled)
            {
                return (true,
                    "PawnIO wurde installiert. Bitte KlarWin schließen und neu starten — danach „Temperatur-Diagnose“ erneut prüfen.");
            }

            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            return (false, string.IsNullOrWhiteSpace(detail)
                ? $"winget-Installation fehlgeschlagen (Code {process.ExitCode})."
                : detail.Trim());
        }
        catch (Exception ex)
        {
            return (false, UserFriendlyErrors.Translate(ex));
        }
    }

    private static string? FindWingetPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(localAppData, @"Microsoft\WindowsApps\winget.exe"),
            Path.Combine(localAppData, @"Microsoft\WindowsApps\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\winget.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<(bool Success, string? Path, string? Error)> DownloadInstallerAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "KlarWin", "PawnIO");
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, "PawnIO_setup.exe");

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            await using var remote = await http.GetStreamAsync(OfficialDownloadUrl, cancellationToken);
            await using var file = File.Create(target);
            await remote.CopyToAsync(file, cancellationToken);

            if (new FileInfo(target).Length < 100_000)
            {
                return (false, null, "Installer-Datei wirkt unvollständig.");
            }

            progress?.Report("Download abgeschlossen.");
            return (true, target, null);
        }
        catch (Exception ex)
        {
            return (false, null, UserFriendlyErrors.Translate(ex));
        }
    }

    private static (bool Success, string Message) LaunchInstaller(string installerPath)
    {
        try
        {
            var started = Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "-install",
                UseShellExecute = true,
                Verb = "runas"
            });

            if (started is null)
            {
                return (false, "Installationsassistent konnte nicht gestartet werden.");
            }

            return (true,
                "Der PawnIO-Installationsassistent wurde gestartet (Windows fragt nach Administratorrechten). " +
                "Nach Abschluss KlarWin schließen und neu starten.");
        }
        catch (Exception ex)
        {
            return (false, UserFriendlyErrors.Translate(ex));
        }
    }
}
