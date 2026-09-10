using System.Diagnostics;
using System.IO;
using System.Text;

namespace KlarWin.Services;

public static class StartLayoutService
{
    public static WorkplaceProfile CreateProfileFromContext(string name, WorkplaceContext context)
    {
        var monitors = context.Monitors.OrderBy(m => m.X).ThenBy(m => m.Y).ToList();
        var roles = new Dictionary<string, WorkplaceRoleRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["primary"] = new WorkplaceRoleRule { Match = "primary" }
        };

        var nonPrimary = monitors.Where(m => !m.IsPrimary).OrderBy(m => m.X).ToList();
        if (nonPrimary.Count >= 1)
            roles["left"] = new WorkplaceRoleRule { Match = $"id:{nonPrimary[0].Id}" };
        if (nonPrimary.Count >= 2)
            roles["right"] = new WorkplaceRoleRule { Match = $"id:{nonPrimary[1].Id}" };
        else if (nonPrimary.Count == 1 && monitors.Count == 2)
            roles["right"] = new WorkplaceRoleRule { Match = $"id:{nonPrimary[0].Id}" };

        var triggers = new List<string>();
        if (!string.IsNullOrWhiteSpace(context.WifiSsid))
            triggers.Add($"SSID:{context.WifiSsid}");
        if (!string.IsNullOrWhiteSpace(context.Gateway))
            triggers.Add($"Gateway:{context.Gateway}");

        return new WorkplaceProfile
        {
            Name = name,
            NetworkTriggers = triggers,
            MonitorFingerprints = monitors.Select(m => m.Id).ToList(),
            Roles = roles,
            Apps = []
        };
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
                || m.Id.Contains(needle, StringComparison.OrdinalIgnoreCase));
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
            var tag = m.IsPrimary ? " (Haupt)" : "";
            sb.AppendLine($"  – {m.Name}{tag}: {m.Width}×{m.Height} @ ({m.X},{m.Y})");
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
