using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KlarWin.Services;

public sealed class SoftwareUpdateItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string CurrentVersion { get; init; } = "";
    public string AvailableVersion { get; init; } = "";
    public string Kind { get; init; } = "Programm"; // Programm | Treiber
    /// <summary>Kanal in KlarWin: winget | wu-driver</summary>
    public string Source { get; init; } = "winget";
    /// <summary>winget-Quelle: winget | msstore | …</summary>
    public string PackageSource { get; init; } = "winget";
    public object? NativeHandle { get; init; }
}

public sealed class SoftwareInstallBatchResult
{
    public IReadOnlyList<string> Lines { get; init; } = [];
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    /// <summary>Already current / no actionable winget upgrade (not counted as failure).</summary>
    public int Skipped { get; init; }
    public IReadOnlyList<string> AttemptedIds { get; init; } = [];
    /// <summary>Packages that failed specifically because elevation was required.</summary>
    public IReadOnlyList<SoftwareUpdateItem> AdminBlockedItems { get; init; } = [];
    /// <summary>Packages that should be finished via Microsoft Store or Windows Update.</summary>
    public IReadOnlyList<SoftwareUpdateExternalAction> ExternalFollowUps { get; init; } = [];

    public bool NeedsAdmin => AdminBlockedItems.Count > 0;
    public bool HasExternalFollowUps => ExternalFollowUps.Count > 0;

    public string SummaryText => string.Join("\n", Lines.Where(n => !string.IsNullOrWhiteSpace(n)));

    public string ResultHeadline
    {
        get
        {
            var parts = new List<string> { $"{Succeeded} erfolgreich" };
            if (Skipped > 0) parts.Add($"{Skipped} übersprungen");
            if (Failed > 0) parts.Add($"{Failed} fehlgeschlagen");
            return "Ergebnis: " + string.Join(", ", parts) + ".";
        }
    }

    public static bool IsAdminRequiredMessage(string line) =>
        line.Contains("Benötigt Administratorrechte", StringComparison.OrdinalIgnoreCase)
        || line.Contains("requires elevation", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Administratorrechte nötig", StringComparison.OrdinalIgnoreCase)
        || line.Contains("als Administrator starten", StringComparison.OrdinalIgnoreCase)
        || line.Contains("als Administrator ausführen", StringComparison.OrdinalIgnoreCase);

    public static bool IsSkippedMessage(string line) =>
        line.StartsWith('○')
        || line.Contains("kein Upgrade nötig", StringComparison.OrdinalIgnoreCase)
        || line.Contains("bereits aktuell", StringComparison.OrdinalIgnoreCase)
        || line.Contains("nicht über winget aktualisierbar", StringComparison.OrdinalIgnoreCase);
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

    public async Task<SoftwareInstallBatchResult> InstallSelectedAsync(
        IReadOnlyList<SoftwareUpdateItem> items,
        IProgress<SoftwareInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
        {
            return new SoftwareInstallBatchResult
            {
                Lines = ["Nichts ausgewählt."],
                Succeeded = 0,
                Failed = 0
            };
        }

        var notes = new List<string>();
        var apps = items.Where(i => i.Source == "winget").ToList();
        var drivers = items.Where(i => i.Source == "wu-driver").ToList();
        var attemptedIds = items.Select(i => i.Id).ToList();
        var adminBlocked = new List<SoftwareUpdateItem>();
        var externalFollowUps = new List<SoftwareUpdateExternalAction>();
        var total = apps.Count + (drivers.Count > 0 ? 1 : 0);
        var step = 0;
        var succeeded = 0;
        var failed = 0;
        var skipped = 0;

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
            var line = await Task.Run(
                () => InstallWinget(app.Id, app.Name, app.PackageSource, detailProgress),
                cancellationToken);
            notes.Add(line);
            if (line.StartsWith('✓'))
            {
                succeeded++;
            }
            else if (SoftwareInstallBatchResult.IsSkippedMessage(line))
            {
                skipped++;
                TryAddExternalFollowUp(externalFollowUps, app, line);
            }
            else
            {
                failed++;
                if (SoftwareInstallBatchResult.IsAdminRequiredMessage(line))
                    adminBlocked.Add(app);
                else
                    TryAddExternalFollowUp(externalFollowUps, app, line);
            }

            step++;
            var headline = line.StartsWith('✓')
                ? $"{app.Name} fertig"
                : SoftwareInstallBatchResult.IsSkippedMessage(line)
                    ? $"{app.Name} — übersprungen"
                    : $"{app.Name} — fehlgeschlagen";
            progress?.Report(new SoftwareInstallProgress
            {
                Step = step,
                Total = total,
                Headline = headline,
                Detail = line,
                IsIndeterminate = false
            });
        }

        if (drivers.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportStep(
                $"Installiere {drivers.Count} Treiber-Update(s) …",
                "Windows Update — bitte warten, kein Fenster schließen.");
            var line = await Task.Run(() => InstallDrivers(drivers), cancellationToken);
            notes.Add(line);
            if (line.StartsWith('✓'))
            {
                succeeded++;
            }
            else if (SoftwareInstallBatchResult.IsSkippedMessage(line))
            {
                skipped++;
                foreach (var d in drivers)
                    TryAddExternalFollowUp(externalFollowUps, d, line);
            }
            else
            {
                failed++;
                if (SoftwareInstallBatchResult.IsAdminRequiredMessage(line))
                    adminBlocked.AddRange(drivers);
                else
                {
                    foreach (var d in drivers)
                        TryAddExternalFollowUp(externalFollowUps, d, line);
                }
            }

            step++;
            progress?.Report(new SoftwareInstallProgress
            {
                Step = step,
                Total = total,
                Headline = "Treiber-Updates abgeschlossen",
                Detail = line,
                IsIndeterminate = false
            });
        }

        InvalidateCache();
        return new SoftwareInstallBatchResult
        {
            Lines = notes,
            Succeeded = succeeded,
            Failed = failed,
            Skipped = skipped,
            AttemptedIds = attemptedIds,
            AdminBlockedItems = adminBlocked,
            ExternalFollowUps = DeduplicateExternal(externalFollowUps)
        };
    }

    private static void TryAddExternalFollowUp(
        List<SoftwareUpdateExternalAction> list,
        SoftwareUpdateItem item,
        string line)
    {
        var action = SoftwareUpdateExternalAction.TryFromInstallHint(item, line)
                     ?? SoftwareUpdateExternalAction.TryForItem(item);
        if (action is null) return;
        list.Add(action);
    }

    private static IReadOnlyList<SoftwareUpdateExternalAction> DeduplicateExternal(
        IEnumerable<SoftwareUpdateExternalAction> actions) =>
        actions
            .GroupBy(a => a.Kind + "|" + a.PackageId + "|" + a.Uri, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

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

        // 1) JSON wenn winget es kann (zuverlässige Ids)
        var json = RunCapture(
            "winget",
            "upgrade --accept-source-agreements --disable-interactivity --format json",
            WingetListTimeoutMs);
        var fromJson = ParseWingetUpgradeJson(json);
        if (fromJson.Count > 0)
        {
            return FilterActionableUpgrades(fromJson);
        }

        // 2) Tabelle über Spaltenköpfe (nicht Regex über Leerzeichen —
        //    sonst werden Namensbruchstücke wie „Microsoft“ fälschlich als Id gelesen).
        var output = RunCapture(
            "winget",
            "upgrade --accept-source-agreements --disable-interactivity",
            WingetListTimeoutMs);

        return FilterActionableUpgrades(ParseWingetUpgradeTable(output));
    }

    /// <summary>
    /// Drop packages where installed and available versions are the same —
    /// winget sometimes still lists them, then fails with „no available upgrade“.
    /// </summary>
    internal static IReadOnlyList<SoftwareUpdateItem> FilterActionableUpgrades(
        IReadOnlyList<SoftwareUpdateItem> items) =>
        items.Where(HasVersionBump).ToList();

    internal static bool HasVersionBump(SoftwareUpdateItem item)
    {
        var cur = NormalizeVersionLabel(item.CurrentVersion);
        var avail = NormalizeVersionLabel(item.AvailableVersion);
        if (string.IsNullOrWhiteSpace(cur) || string.IsNullOrWhiteSpace(avail))
            return true;
        if (cur is "?" or "-" || avail is "?" or "-")
            return true;

        if (string.Equals(cur, avail, StringComparison.OrdinalIgnoreCase))
            return false;

        if (Version.TryParse(PadVersion(cur), out var cv)
            && Version.TryParse(PadVersion(avail), out var av))
        {
            return av > cv;
        }

        // Unknown format but not identical — keep so the user can try.
        return true;
    }

    private static string NormalizeVersionLabel(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "";
        var v = version.Trim();
        if (v.StartsWith('v') || v.StartsWith('V'))
            v = v[1..].Trim();
        return v;
    }

    private static string PadVersion(string version)
    {
        // Version.TryParse wants at least Major.Minor
        var parts = version.Split('.');
        return parts.Length switch
        {
            1 => version + ".0",
            _ => version
        };
    }

    internal static IReadOnlyList<SoftwareUpdateItem> ParseWingetUpgradeJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.IndexOf('{') < 0)
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(json[json.IndexOf('{')..]);
            var items = new List<SoftwareUpdateItem>();
            CollectWingetJsonPackages(doc.RootElement, items);
            return items
                .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static void CollectWingetJsonPackages(JsonElement el, List<SoftwareUpdateItem> items)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("PackageIdentifier", out var idEl)
                || el.TryGetProperty("Id", out idEl))
            {
                var id = idEl.GetString() ?? "";
                if (LooksLikeWingetId(id))
                {
                    var name = GetJsonString(el, "PackageName", "Name") ?? id;
                    var cur = GetJsonString(el, "InstalledVersion", "Version") ?? "?";
                    var avail = GetJsonString(el, "AvailableVersion")
                                ?? FirstJsonArrayString(el, "AvailableVersions")
                                ?? "?";
                    var src = GetJsonString(el, "Source") ?? "winget";
                    items.Add(new SoftwareUpdateItem
                    {
                        Id = id,
                        Name = name,
                        CurrentVersion = cur,
                        AvailableVersion = avail,
                        Kind = "Programm",
                        Source = "winget",
                        PackageSource = src
                    });
                }
            }

            foreach (var prop in el.EnumerateObject())
            {
                CollectWingetJsonPackages(prop.Value, items);
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in el.EnumerateArray())
            {
                CollectWingetJsonPackages(child, items);
            }
        }
    }

    private static string? GetJsonString(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String)
            {
                return p.GetString();
            }
        }

        return null;
    }

    private static string? FirstJsonArrayString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var x in arr.EnumerateArray())
        {
            if (x.ValueKind == JsonValueKind.String)
            {
                return x.GetString();
            }
        }

        return null;
    }

    internal static IReadOnlyList<SoftwareUpdateItem> ParseWingetUpgradeTable(string output)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd())
            .Where(l => l.Length > 0)
            .ToList();

        var headerIndex = lines.FindIndex(l =>
            l.Contains("Id", StringComparison.OrdinalIgnoreCase)
            && (l.StartsWith("Name", StringComparison.OrdinalIgnoreCase)
                || l.Contains(" Name ", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(l, @"^Name\s+Id\b", RegexOptions.IgnoreCase)));
        if (headerIndex < 0)
        {
            return ParseWingetUpgradeFallback(lines);
        }

        var header = lines[headerIndex];
        var idAt = IndexOfColumn(header, "Id");
        var versionAt = IndexOfColumn(header, "Version");
        var availableAt = IndexOfColumn(header, "Available");
        if (availableAt < 0) availableAt = IndexOfColumn(header, "Verfügbar");
        var sourceAt = IndexOfColumn(header, "Source");
        if (sourceAt < 0) sourceAt = IndexOfColumn(header, "Quelle");

        if (idAt < 0 || versionAt < 0 || availableAt < 0)
        {
            return ParseWingetUpgradeFallback(lines);
        }

        var items = new List<SoftwareUpdateItem>();
        for (var i = headerIndex + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.StartsWith('-') || line.All(c => c is '-' or ' ')) continue;
            if (line.Contains("Aktualisierungen verfügbar", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains("upgrades available", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("Mindestens", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Length <= idAt) continue;

            var id = SliceColumn(line, idAt, versionAt).Trim();
            if (string.IsNullOrWhiteSpace(id)
                || id.Equals("winget", StringComparison.OrdinalIgnoreCase)
                || id.Equals("msstore", StringComparison.OrdinalIgnoreCase)
                || !LooksLikeWingetId(id))
            {
                continue;
            }

            var name = SliceColumn(line, 0, idAt).Trim();
            var cur = SliceColumn(line, versionAt, availableAt).Trim();
            var avail = sourceAt > availableAt
                ? SliceColumn(line, availableAt, sourceAt).Trim()
                : line[Math.Min(availableAt, line.Length)..].Trim().Split(' ', 2)[0];
            var pkgSource = sourceAt >= 0 && sourceAt < line.Length
                ? line[sourceAt..].Trim().Split(' ', 2)[0]
                : "winget";

            if (string.IsNullOrWhiteSpace(name)) name = id;

            items.Add(new SoftwareUpdateItem
            {
                Id = id,
                Name = name,
                CurrentVersion = string.IsNullOrWhiteSpace(cur) ? "?" : cur,
                AvailableVersion = string.IsNullOrWhiteSpace(avail) ? "?" : avail,
                Kind = "Programm",
                Source = "winget",
                PackageSource = string.IsNullOrWhiteSpace(pkgSource) ? "winget" : pkgSource
            });
        }

        return items
            .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<SoftwareUpdateItem> ParseWingetUpgradeFallback(IReadOnlyList<string> lines)
    {
        var items = new List<SoftwareUpdateItem>();
        // Id oft Publisher.Product — am Zeilenende vor Versionen suchen
        var row = new Regex(
            @"^(?<name>.+?)\s+(?<id>[A-Za-z0-9][A-Za-z0-9._+-]*\.[A-Za-z0-9._+-]+)\s+(?<cur>\S+)\s+(?<avail>\S+)(?:\s+(?<src>\S+))?\s*$",
            RegexOptions.Compiled);
        foreach (var line in lines)
        {
            if (line.StartsWith('-') || line.StartsWith("Name", StringComparison.OrdinalIgnoreCase)) continue;
            var match = row.Match(line);
            if (!match.Success) continue;
            var id = match.Groups["id"].Value;
            if (!LooksLikeWingetId(id)) continue;
            items.Add(new SoftwareUpdateItem
            {
                Id = id,
                Name = match.Groups["name"].Value.Trim(),
                CurrentVersion = match.Groups["cur"].Value,
                AvailableVersion = match.Groups["avail"].Value,
                Kind = "Programm",
                Source = "winget",
                PackageSource = match.Groups["src"].Success ? match.Groups["src"].Value : "winget"
            });
        }

        return items;
    }

    private static bool LooksLikeWingetId(string id) =>
        id.Contains('.') && id.Length >= 3 && !id.Contains(' ');

    private static int IndexOfColumn(string header, string name)
    {
        // Prefer whole-word column title at start of a field (preceded by start or 2+ spaces)
        var pattern = $@"(?:^|(?<=\s{{2}})){Regex.Escape(name)}(?=\s{{2,}}|$)";
        var m = Regex.Match(header, pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Index : -1;
    }

    private static string SliceColumn(string line, int start, int endExclusive)
    {
        if (start < 0) return "";
        if (start >= line.Length) return "";
        if (endExclusive <= start || endExclusive > line.Length)
        {
            return line[start..];
        }

        return line[start..endExclusive];
    }

    private static string InstallWinget(
        string id,
        string displayName,
        string packageSource,
        IProgress<string>? progress = null)
    {
        try
        {
            if (!LooksLikeWingetId(id))
            {
                return $"✗ {displayName}: Ungültige Paket-Id „{id}“ (Listenfehler). Bitte Liste aktualisieren.";
            }

            // 1) mit gemeldeter Quelle, 2) ohne Source-Filter
            var attempts = new List<string>();
            if (!string.IsNullOrWhiteSpace(packageSource))
            {
                attempts.Add(BuildWingetUpgradeArgs(id, packageSource));
            }

            attempts.Add(BuildWingetUpgradeArgs(id, source: null));

            string? lastFail = null;
            foreach (var args in attempts.Distinct(StringComparer.Ordinal))
            {
                progress?.Report($"{displayName}: winget {args}");
                var (exit, log, lastLine) = RunWingetUpgrade(args, displayName, progress);
                if (exit == 0)
                {
                    return $"✓ {displayName} aktualisiert.";
                }

                lastFail = WingetExitHint(exit, log, lastLine, id);
                if (IsNoUpgradeHint(lastFail) || IsNonWingetUpgradeableHint(lastFail))
                {
                    return $"○ {displayName}: {lastFail}";
                }

                // Bei „kein Installer“ / falsche Quelle: nächsten Versuch
                if (exit != -1978335212 && !lastFail.Contains("Kein passender Installer", StringComparison.Ordinal))
                {
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(lastFail)
                && (IsNoUpgradeHint(lastFail) || IsNonWingetUpgradeableHint(lastFail)))
            {
                return $"○ {displayName}: {lastFail}";
            }

            return $"✗ {displayName}: {lastFail ?? "Update fehlgeschlagen."}";
        }
        catch (Exception ex)
        {
            return $"✗ {displayName}: {UserFriendlyErrors.Translate(ex)}";
        }
    }

    private static bool IsNoUpgradeHint(string hint) =>
        hint.Contains("kein Upgrade", StringComparison.OrdinalIgnoreCase)
        || hint.Contains("bereits aktuell", StringComparison.OrdinalIgnoreCase);

    private static bool IsNonWingetUpgradeableHint(string hint) =>
        hint.Contains("nicht über winget aktualisierbar", StringComparison.OrdinalIgnoreCase)
        || hint.Contains("Installationstechnologie", StringComparison.OrdinalIgnoreCase);

    private static string BuildWingetUpgradeArgs(string id, string? source)
    {
        var src = string.IsNullOrWhiteSpace(source) ? "" : $" --source {source}";
        return $"upgrade --id \"{id}\" -e{src} " +
               "--accept-package-agreements --accept-source-agreements " +
               "--disable-interactivity --silent";
    }

    private static (int ExitCode, string Log, string LastLine) RunWingetUpgrade(
        string args,
        string displayName,
        IProgress<string>? progress)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "winget",
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        });
        if (p is null)
        {
            return (1, "", "winget konnte nicht gestartet werden.");
        }

        var log = new StringBuilder();
        var lastLine = $"{displayName}: wird installiert …";
        void OnLine(string? data)
        {
            if (string.IsNullOrWhiteSpace(data)) return;
            lastLine = data.Trim();
            log.AppendLine(lastLine);
            progress?.Report($"{displayName}: {lastLine}");
        }

        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(600_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return (1, log.ToString(), "Zeitüberschreitung (winget > 10 Min).");
        }

        return (p.ExitCode, log.ToString(), lastLine);
    }

    private static string WingetExitHint(int exitCode, string log, string lastLine, string id)
    {
        var blob = (log + "\n" + lastLine).ToLowerInvariant();
        if (blob.Contains("requires elevation") || blob.Contains("administrator") || blob.Contains("access is denied")
            || blob.Contains("zugriff verweigert") || blob.Contains("erhöhte rechte")
            || exitCode == -1978335184) // APPINSTALLER_CLI_ERROR_REQUIRE_ADMIN
        {
            return "Benötigt Administratorrechte.";
        }

        if (blob.Contains("no newer package versions") || blob.Contains("keine neueren")
            || blob.Contains("no available upgrade") || exitCode == -1978335189)
        {
            return "bereits aktuell — kein Upgrade nötig.";
        }

        // Edge etc.: newer build exists, but MSI vs Store / different installer family
        if (blob.Contains("install technology") || blob.Contains("installation technology")
            || blob.Contains("installationstechnologie") || blob.Contains("installer type")
            || blob.Contains("unterschiedet sich jedoch"))
        {
            return "nicht über winget aktualisierbar (andere Installationstechnologie) — bitte über die App selbst oder Windows Update.";
        }

        if (blob.Contains("installer hash") || blob.Contains("hash mismatch"))
        {
            return "Installer-Hash stimmt nicht (Paketquelle). Später erneut versuchen.";
        }

        if (exitCode == -1978335212)
        {
            return $"Kein passender Installer (Id „{id}“). Oft falsche Id in der Liste oder Architektur/Store-Paket — Liste aktualisieren, ggf. als Administrator.";
        }

        var shortLast = lastLine.Length > 120 ? lastLine[..120] + "…" : lastLine;
        return $"Update fehlgeschlagen (winget-Code {exitCode}). {shortLast}";
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
