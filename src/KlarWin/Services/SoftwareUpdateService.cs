using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace KlarWin.Services;

public sealed class SoftwareUpdateItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string CurrentVersion { get; init; } = "";
    public string AvailableVersion { get; init; } = "";
    public string Kind { get; init; } = "Programm"; // Programm | Treiber
    public string Source { get; init; } = "winget";
    public object? NativeHandle { get; init; }
}

public sealed class SoftwareUpdateService
{
    private static readonly TimeSpan WingetListCacheDuration = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan DriverListCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly object CacheLock = new();
    private static DateTime _wingetCacheUtc = DateTime.MinValue;
    private static IReadOnlyList<SoftwareUpdateItem> _wingetCache = [];
    private static DateTime _driverCacheUtc = DateTime.MinValue;
    private static IReadOnlyList<SoftwareUpdateItem> _driverCache = [];

    private const int WingetListTimeoutMs = 90_000;
    private const int DriverSearchTimeoutMs = 45_000;

    public async Task<IReadOnlyList<SoftwareUpdateItem>> ListWingetOnlyAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report("Programme werden geprüft (winget) …");
        return await Task.Run(() => ListWingetUpgradesCached(), cancellationToken);
    }

    public async Task<IReadOnlyList<SoftwareUpdateItem>> ListDriversOnlyAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report("Treiber über Windows Update werden geprüft …");
        try
        {
            return await Task.Run(() => ListDriverUpdatesCached(), cancellationToken);
        }
        catch (Exception ex)
        {
            progress?.Report("Treiber-Prüfung eingeschränkt: " + UserFriendlyErrors.Translate(ex));
            return [];
        }
    }

    public async Task<IReadOnlyList<SoftwareUpdateItem>> ListAvailableAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var wingetTask = ListWingetOnlyAsync(progress, cancellationToken);
        var driverTask = ListDriversOnlyAsync(progress, cancellationToken);
        await Task.WhenAll(wingetTask, driverTask);

        return wingetTask.Result
            .Concat(driverTask.Result)
            .OrderBy(i => i.Kind, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static void InvalidateCache()
    {
        lock (CacheLock)
        {
            _wingetCacheUtc = DateTime.MinValue;
            _driverCacheUtc = DateTime.MinValue;
        }
    }

    public async Task<string> InstallSelectedAsync(
        IReadOnlyList<SoftwareUpdateItem> items,
        IProgress<SoftwareInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0) return "Nichts ausgewählt.";

        var notes = new List<string>();
        var apps = items.Where(i => i.Source == "winget").ToList();
        var drivers = items.Where(i => i.Source == "wu-driver").ToList();
        var total = apps.Count + (drivers.Count > 0 ? 1 : 0);
        var step = 0;

        void ReportStep(string headline, string detail, bool indeterminate = true)
        {
            progress?.Report(new SoftwareInstallProgress
            {
                Step = step,
                Total = total,
                Headline = headline,
                Detail = detail,
                IsIndeterminate = indeterminate
            });
        }

        foreach (var app in apps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportStep(
                $"Installiere {app.Name} …",
                $"{app.CurrentVersion} → {app.AvailableVersion} · winget (kann mehrere Minuten dauern)");
            var detailProgress = new Progress<string>(line =>
            {
                progress?.Report(new SoftwareInstallProgress
                {
                    Step = step,
                    Total = total,
                    Headline = $"Installiere {app.Name} …",
                    Detail = line,
                    IsIndeterminate = true
                });
            });
            notes.Add(await Task.Run(() => InstallWinget(app.Id, app.Name, detailProgress), cancellationToken));
            step++;
            progress?.Report(new SoftwareInstallProgress
            {
                Step = step,
                Total = total,
                Headline = notes[^1].StartsWith('✓') ? $"{app.Name} fertig" : $"{app.Name} — Ergebnis prüfen",
                Detail = notes[^1],
                IsIndeterminate = false
            });
        }

        if (drivers.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportStep(
                $"Installiere {drivers.Count} Treiber-Update(s) …",
                "Windows Update — bitte warten, kein Fenster schließen.");
            notes.Add(await Task.Run(() => InstallDrivers(drivers), cancellationToken));
            step++;
            progress?.Report(new SoftwareInstallProgress
            {
                Step = step,
                Total = total,
                Headline = "Treiber-Updates abgeschlossen",
                Detail = notes[^1],
                IsIndeterminate = false
            });
        }

        InvalidateCache();
        return string.Join("\n", notes.Where(n => !string.IsNullOrWhiteSpace(n)));
    }

    public static bool IsWingetAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            p?.WaitForExit(5000);
            return p?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<SoftwareUpdateItem> ListWingetUpgradesCached()
    {
        lock (CacheLock)
        {
            if (DateTime.UtcNow - _wingetCacheUtc < WingetListCacheDuration)
            {
                return _wingetCache;
            }
        }

        var fresh = ListWingetUpgrades();
        lock (CacheLock)
        {
            _wingetCache = fresh;
            _wingetCacheUtc = DateTime.UtcNow;
            return _wingetCache;
        }
    }

    private static IReadOnlyList<SoftwareUpdateItem> ListDriverUpdatesCached()
    {
        lock (CacheLock)
        {
            if (DateTime.UtcNow - _driverCacheUtc < DriverListCacheDuration)
            {
                return _driverCache;
            }
        }

        var fresh = ListDriverUpdates();
        lock (CacheLock)
        {
            _driverCache = fresh;
            _driverCacheUtc = DateTime.UtcNow;
            return _driverCache;
        }
    }

    private static IReadOnlyList<SoftwareUpdateItem> ListWingetUpgrades()
    {
        if (!IsWingetAvailable()) return [];

        var output = RunCapture(
            "winget",
            "upgrade --source winget --accept-source-agreements --disable-interactivity",
            WingetListTimeoutMs);
        var items = new List<SoftwareUpdateItem>();
        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith('-') || line.StartsWith("Name", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains("Aktualisierungen verfügbar", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains("package", StringComparison.OrdinalIgnoreCase) && line.Contains("Version", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains("Mindestens", StringComparison.OrdinalIgnoreCase)) continue;

            var match = Regex.Match(line,
                @"^(?<name>.+?)\s+(?<id>[A-Za-z0-9][A-Za-z0-9._+-]+)\s+(?<cur>\S+)\s+(?<avail>\S+)\s+(?<src>\S+)\s*$");
            if (!match.Success) continue;
            var id = match.Groups["id"].Value;
            if (id.Equals("winget", StringComparison.OrdinalIgnoreCase)
                || id.Equals("msstore", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            items.Add(new SoftwareUpdateItem
            {
                Id = id,
                Name = match.Groups["name"].Value.Trim(),
                CurrentVersion = match.Groups["cur"].Value,
                AvailableVersion = match.Groups["avail"].Value,
                Kind = "Programm",
                Source = "winget"
            });
        }

        return items;
    }

    private static string InstallWinget(string id, string displayName, IProgress<string>? progress = null)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = $"upgrade --id \"{id}\" --accept-package-agreements --accept-source-agreements --disable-interactivity --silent",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            });
            if (p is null)
            {
                return $"✗ {displayName}: winget konnte nicht gestartet werden.";
            }

            var lastLine = $"{displayName}: wird installiert …";
            p.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                lastLine = e.Data.Trim();
                progress?.Report($"{displayName}: {lastLine}");
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                lastLine = e.Data.Trim();
                progress?.Report($"{displayName}: {lastLine}");
            };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit(600_000);

            return p.ExitCode == 0
                ? $"✓ {displayName} aktualisiert."
                : $"✗ {displayName}: Update fehlgeschlagen (Code {p.ExitCode}). {lastLine}";
        }
        catch (Exception ex)
        {
            return $"✗ {displayName}: {UserFriendlyErrors.Translate(ex)}";
        }
    }

    private static IReadOnlyList<SoftwareUpdateItem> ListDriverUpdates()
    {
        var task = Task.Run(ListDriverUpdatesCore);
        if (!task.Wait(DriverSearchTimeoutMs))
        {
            return [];
        }

        return task.IsFaulted ? [] : task.Result;
    }

    private static List<SoftwareUpdateItem> ListDriverUpdatesCore()
    {
        var items = new List<SoftwareUpdateItem>();
        try
        {
            var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (sessionType is null) return items;
            dynamic session = Activator.CreateInstance(sessionType)!;
            dynamic searcher = session.CreateUpdateSearcher();
            searcher.Online = true;
            dynamic result = searcher.Search("IsInstalled=0 and Type='Driver'");
            var count = (int)result.Updates.Count;
            for (var i = 0; i < count && i < 80; i++)
            {
                dynamic update = result.Updates[i];
                string title = update.Title?.ToString() ?? $"Treiber {i + 1}";
                string identity = "";
                try { identity = update.Identity.UpdateID?.ToString() ?? ""; } catch { /* ignore */ }
                items.Add(new SoftwareUpdateItem
                {
                    Id = string.IsNullOrWhiteSpace(identity) ? $"driver-{i}" : identity,
                    Name = title,
                    CurrentVersion = "—",
                    AvailableVersion = "Windows Update",
                    Kind = "Treiber",
                    Source = "wu-driver",
                    NativeHandle = update
                });
            }
        }
        catch
        {
            // WU COM not available / blocked
        }

        return items;
    }

    private static string InstallDrivers(IReadOnlyList<SoftwareUpdateItem> drivers)
    {
        try
        {
            var wanted = drivers.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
            var collType = Type.GetTypeFromProgID("Microsoft.Update.UpdateColl");
            if (sessionType is null || collType is null)
            {
                return "Windows Update-Schnittstelle nicht verfügbar.";
            }

            dynamic session = Activator.CreateInstance(sessionType)!;
            dynamic searcher = session.CreateUpdateSearcher();
            searcher.Online = true;
            dynamic result = searcher.Search("IsInstalled=0 and Type='Driver'");
            dynamic toInstall = Activator.CreateInstance(collType)!;
            var count = (int)result.Updates.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic update = result.Updates[i];
                string identity = "";
                try { identity = update.Identity.UpdateID?.ToString() ?? ""; } catch { /* ignore */ }
                if (wanted.Contains(identity) || wanted.Contains($"driver-{i}"))
                {
                    toInstall.Add(update);
                }
            }

            if (toInstall.Count == 0)
            {
                return "Keine Treiber zum Installieren (Liste erneut laden und erneut wählen).";
            }

            dynamic downloader = session.CreateUpdateDownloader();
            downloader.Updates = toInstall;
            downloader.Download();

            dynamic installer = session.CreateUpdateInstaller();
            installer.Updates = toInstall;
            dynamic installResult = installer.Install();
            var code = (int)installResult.ResultCode;
            return code == 2
                ? $"✓ {toInstall.Count} Treiber-Update(s) installiert."
                : $"Treiber-Installation beendet (ResultCode {code}). Ein Neustart kann nötig sein.";
        }
        catch (Exception ex)
        {
            return "Treiber: " + UserFriendlyErrors.Translate(ex);
        }
    }

    private static string RunCapture(string file, string args, int timeoutMs = 120_000)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            });
            if (p is null) return "";

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return stdout.ToString();
            }

            return stdout.ToString();
        }
        catch
        {
            return "";
        }
    }
}
