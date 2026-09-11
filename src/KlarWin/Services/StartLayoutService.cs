using System.Diagnostics;
using System.IO;
using System.Text;

namespace KlarWin.Services;

public static class StartLayoutService
{
    public static WorkplaceProfile CreateProfileFromContext(
        string name,
        WorkplaceContext context,
        WorkplaceProfile? previous = null)
    {
        var monitors = context.Monitors.OrderBy(m => m.X).ThenBy(m => m.Y).ToList();
        var roles = BuildMonitorRoles(monitors);

        var triggers = new List<string>();
        if (!string.IsNullOrWhiteSpace(context.WifiSsid))
            triggers.Add($"SSID:{context.WifiSsid}");
        if (!string.IsNullOrWhiteSpace(context.Gateway))
            triggers.Add($"Gateway:{context.Gateway}");

        var apps = previous?.Apps is { Count: > 0 }
            ? RemapAppsToRoles(previous, roles, context)
            : [];

        return new WorkplaceProfile
        {
            Name = name,
            NetworkTriggers = triggers,
            MonitorFingerprints = monitors.Select(m => m.Id).ToList(),
            Roles = roles,
            Apps = apps
        };
    }

    /// <summary>
    /// Roles named after hardware (Dell / Samsung / MiniFire), ordered left→right.
    /// Avoids confusing primary/left/right when the laptop is not in the center.
    /// </summary>
    public static Dictionary<string, WorkplaceRoleRule> BuildMonitorRoles(IReadOnlyList<MonitorInfo> monitors)
    {
        var roles = new Dictionary<string, WorkplaceRoleRule>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in monitors.OrderBy(x => x.X).ThenBy(x => x.Y))
        {
            var key = BuildUniqueRoleKey(m, used);
            used.Add(key);
            roles[key] = new WorkplaceRoleRule { Match = $"id:{m.Id}" };
        }

        if (roles.Count == 0)
            roles["Laptop"] = new WorkplaceRoleRule { Match = "primary" };

        return roles;
    }

    /// <summary>Ensure profile roles match current monitors; remaps existing apps.</summary>
    public static void SyncRolesToMonitors(WorkplaceProfile profile, WorkplaceContext context)
    {
        var fresh = CreateProfileFromContext(profile.Name, context, profile);
        profile.Roles = fresh.Roles;
        profile.Apps = fresh.Apps;
        profile.MonitorFingerprints = fresh.MonitorFingerprints;
        profile.NetworkTriggers = fresh.NetworkTriggers;
    }

    public static string FriendlyRoleLabel(MonitorInfo m)
    {
        var mfg = (m.ManufacturerDisplay ?? "").Trim();
        if (mfg.Contains("MiniFire", StringComparison.OrdinalIgnoreCase))
            return "MiniFire";
        if (mfg.Contains("Laptop", StringComparison.OrdinalIgnoreCase)
            || mfg.StartsWith("BOE", StringComparison.OrdinalIgnoreCase))
            return "Laptop";

        if (!string.IsNullOrWhiteSpace(mfg) && !mfg.Equals("Unbekannt", StringComparison.OrdinalIgnoreCase))
        {
            var shortMfg = mfg;
            if (shortMfg.Contains('('))
                shortMfg = shortMfg.Split('(')[0].Trim();
            if (shortMfg.Contains('/'))
                shortMfg = shortMfg.Split('/')[0].Trim();
            if (!string.IsNullOrWhiteSpace(shortMfg))
                return shortMfg;
        }

        var name = (m.Name ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(name) && name is not ("Hauptanzeige" or "Monitor"))
            return name.Length <= 28 ? name : name[..28].Trim();

        return m.IsPrimary ? "Hauptmonitor" : "Monitor";
    }

    private static string BuildUniqueRoleKey(MonitorInfo m, HashSet<string> used)
    {
        var baseKey = FriendlyRoleLabel(m);
        if (!used.Contains(baseKey)) return baseKey;

        // Two same brands: prefer model / product code
        var model = !string.IsNullOrWhiteSpace(m.Name) && m.Name is not ("Hauptanzeige" or "Monitor")
            ? m.Name.Trim()
            : (!string.IsNullOrWhiteSpace(m.ProductCode) ? m.ProductCode.Trim() : "");
        if (!string.IsNullOrWhiteSpace(model))
        {
            var withModel = $"{baseKey} {model}";
            if (withModel.Length > 36) withModel = withModel[..36].Trim();
            if (!used.Contains(withModel)) return withModel;
        }

        var n = 2;
        while (used.Contains($"{baseKey} {n}")) n++;
        return $"{baseKey} {n}";
    }

    private static List<StartLayoutApp> RemapAppsToRoles(
        WorkplaceProfile previous,
        Dictionary<string, WorkplaceRoleRule> newRoles,
        WorkplaceContext context)
    {
        var idToRole = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (role, rule) in newRoles)
        {
            var match = rule.Match ?? "";
            if (match.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
                idToRole[match[3..].Trim()] = role;
        }

        var result = new List<StartLayoutApp>();
        foreach (var app in previous.Apps)
        {
            var copy = new StartLayoutApp
            {
                Name = app.Name,
                ExePath = app.ExePath,
                Arguments = app.Arguments,
                DelayMs = app.DelayMs,
                Maximize = app.Maximize,
                Role = app.Role
            };

            // Already matches a new role key
            if (newRoles.ContainsKey(app.Role))
            {
                result.Add(copy);
                continue;
            }

            var mon = ResolveRole(previous, app.Role, context);
            if (mon is not null && idToRole.TryGetValue(mon.Id, out var mapped))
                copy.Role = mapped;
            else if (newRoles.ContainsKey("Laptop"))
                copy.Role = "Laptop";
            else
                copy.Role = newRoles.Keys.First();

            result.Add(copy);
        }

        return result;
    }

    public static WorkplaceProfile? SelectBestProfile(IEnumerable<WorkplaceProfile> profiles, WorkplaceContext context)
    {
        var list = profiles.ToList();
        if (list.Count == 0) return null;

        WorkplaceProfile? best = null;
        var bestScore = -1;
        foreach (var profile in list)
        {
            var score = ScoreProfile(profile, context);
            if (score > bestScore)
            {
                bestScore = score;
                best = profile;
            }
        }

        return bestScore > 0 ? best : list.FirstOrDefault();
    }

    public static int ScoreProfile(WorkplaceProfile profile, WorkplaceContext context)
    {
        var score = 0;
        var monitorIds = context.Monitors.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (profile.MonitorFingerprints.Count > 0)
        {
            var hits = profile.MonitorFingerprints.Count(id => monitorIds.Contains(id));
            score += hits * 10;
            if (hits == profile.MonitorFingerprints.Count && hits == monitorIds.Count)
                score += 20;
        }

        foreach (var trigger in profile.NetworkTriggers)
        {
            if (trigger.StartsWith("SSID:", StringComparison.OrdinalIgnoreCase))
            {
                var ssid = trigger["SSID:".Length..].Trim();
                if (!string.IsNullOrEmpty(ssid)
                    && ssid.Equals(context.WifiSsid, StringComparison.OrdinalIgnoreCase))
                    score += 25;
            }
            else if (trigger.StartsWith("Gateway:", StringComparison.OrdinalIgnoreCase))
            {
                var gw = trigger["Gateway:".Length..].Trim();
                if (!string.IsNullOrEmpty(gw)
                    && gw.Equals(context.Gateway, StringComparison.OrdinalIgnoreCase))
                    score += 15;
            }
        }

        return score;
    }

    public static MonitorInfo? ResolveRole(WorkplaceProfile profile, string role, WorkplaceContext context)
    {
        if (!profile.Roles.TryGetValue(role, out var rule))
        {
            if (role.Equals("primary", StringComparison.OrdinalIgnoreCase))
                return context.Monitors.FirstOrDefault(m => m.IsPrimary) ?? context.Monitors.FirstOrDefault();
            return null;
        }

        var match = rule.Match?.Trim() ?? "";
        if (match.Equals("primary", StringComparison.OrdinalIgnoreCase))
            return context.Monitors.FirstOrDefault(m => m.IsPrimary) ?? context.Monitors.FirstOrDefault();

        if (match.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
        {
            var id = match[3..].Trim();
            return context.Monitors.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        if (match.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
        {
            var needle = match[5..].Trim();
            return context.Monitors.FirstOrDefault(m =>
                m.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || m.ManufacturerDisplay.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || m.Id.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || FriendlyRoleLabel(m).Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        if (match.StartsWith("index:", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(match[6..].Trim(), out var index))
        {
            var ordered = context.Monitors.OrderBy(m => m.X).ThenBy(m => m.Y).ToList();
            if (index >= 0 && index < ordered.Count) return ordered[index];
        }

        return null;
    }

    public static string FormatDiscovery(WorkplaceContext context, IReadOnlyList<WorkplaceProfile> profiles)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aktueller Arbeitsplatz:");
        if (!string.IsNullOrWhiteSpace(context.WifiSsid))
            sb.AppendLine($"· WLAN: {context.WifiSsid}");
        if (!string.IsNullOrWhiteSpace(context.Gateway))
            sb.AppendLine($"· Gateway: {context.Gateway}");
        if (!string.IsNullOrWhiteSpace(context.AdapterName))
            sb.AppendLine($"· Adapter: {context.AdapterName}");
        sb.AppendLine($"· Monitore: {context.Monitors.Count}");
        foreach (var m in context.Monitors.OrderBy(x => x.X))
        {
            var size = m.DiagonalInches > 0 ? $" · ca. {m.DiagonalInches:0.#}\"" : "";
            var hz = m.RefreshRateHz > 0 ? $" · {m.RefreshRateHz} Hz" : "";
            var serial = !string.IsNullOrWhiteSpace(m.Serial) && m.Serial != "0"
                ? $" · S/N {m.Serial}"
                : "";
            var product = !string.IsNullOrWhiteSpace(m.ProductCode) ? $" · Produkt {m.ProductCode}" : "";
            sb.AppendLine($"  – [{m.PositionLabel}] {m.ManufacturerDisplay} · {m.Name}");
            sb.AppendLine($"    {m.Width}×{m.Height}{hz}{size} @ ({m.X},{m.Y}){product}{serial}");
            sb.AppendLine($"    ID: {m.Id}");
        }

        sb.AppendLine();
        sb.AppendLine(profiles.Count == 0
            ? "Noch kein Profil gespeichert. „Profil speichern“ legt eines aus dem aktuellen Setup an."
            : $"{profiles.Count} Profil(e). „Layout anwenden“ startet die Apps und platziert die Fenster.");

        var best = SelectBestProfile(profiles, context);
        if (best is not null)
            sb.AppendLine($"Vorschlag: „{best.Name}“ (Score {ScoreProfile(best, context)}).");

        return sb.ToString().TrimEnd();
    }

    public static async Task<string> ApplyAsync(WorkplaceProfile profile, WorkplaceContext? context = null)
    {
        context ??= WorkplaceContextService.Capture();
        var log = new StringBuilder();
        log.AppendLine($"Profil „{profile.Name}“ wird angewendet …");

        foreach (var app in profile.Apps)
        {
            try
            {
                var monitor = ResolveRole(profile, app.Role, context);
                if (monitor is null)
                {
                    log.AppendLine($"· {app.Name}: Rolle „{app.Role}“ nicht gefunden — übersprungen.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(app.ExePath) || !File.Exists(app.ExePath))
                {
                    log.AppendLine($"· {app.Name}: Datei nicht gefunden ({app.ExePath}).");
                    continue;
                }

                var process = FindRunning(app.ExePath) ?? StartProcess(app);
                if (process is null)
                {
                    log.AppendLine($"· {app.Name}: Start fehlgeschlagen.");
                    continue;
                }

                if (app.DelayMs > 0)
                    await Task.Delay(app.DelayMs);

                var ok = WindowPlacementService.TryPlaceMainWindow(process, monitor, app.Maximize);
                if (!ok)
                {
                    await Task.Delay(1500);
                    ok = WindowPlacementService.TryPlaceMainWindow(
                        process, monitor, app.Maximize, TimeSpan.FromSeconds(8), retries: 2);
                }

                log.AppendLine(ok
                    ? $"· {app.Name} → {monitor.Name} ({app.Role})"
                    : $"· {app.Name}: gestartet, Fenster noch nicht gefunden (später erneut anwenden).");
            }
            catch (Exception ex)
            {
                log.AppendLine($"· {app.Name}: {ex.Message}");
            }
        }

        if (profile.Apps.Count == 0)
            log.AppendLine("Keine Apps im Profil — zuerst „App hinzufügen“.");

        return log.ToString().TrimEnd();
    }

    private static Process? FindRunning(string exePath)
    {
        var name = Path.GetFileNameWithoutExtension(exePath);
        try
        {
            var matches = Process.GetProcessesByName(name);
            return matches.FirstOrDefault(p =>
            {
                try
                {
                    return string.Equals(p.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase)
                           || true; // name match is enough for placement
                }
                catch
                {
                    return true;
                }
            });
        }
        catch
        {
            return null;
        }
    }

    private static Process? StartProcess(StartLayoutApp app)
    {
        var psi = new ProcessStartInfo
        {
            FileName = app.ExePath,
            Arguments = app.Arguments ?? "",
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(app.ExePath) ?? ""
        };
        return Process.Start(psi);
    }
}
