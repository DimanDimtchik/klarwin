using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace KlarWin.Services;

public sealed class PowerPlanInfo
{
    public string Guid { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsActive { get; init; }
}

public sealed class PowerPlanActionResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
}

public static class PowerPlanService
{
    public const string HighPerformanceGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string BalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string UltimatePerformanceGuid = "e3a42bb0-4c61-4b3d-99b4-7d6c5f0c2a3e";

    // DE: "GUID des Energieschemas: … (Name) *" · EN: "Power Scheme GUID: … (Name) *"
    private static readonly Regex PlanLine = new(
        @"([0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})\s+\(([^)\r\n]+)\)(\s*\*)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<PowerPlanInfo> ListPlans()
    {
        var output = RunPowerCfgCapture("-list");
        if (string.IsNullOrWhiteSpace(output))
        {
            // Fallback: manche Umgebungen liefern nur mit Slash
            output = RunPowerCfgCapture("/list");
        }

        var list = new List<PowerPlanInfo>();
        foreach (Match match in PlanLine.Matches(output))
        {
            list.Add(new PowerPlanInfo
            {
                Guid = match.Groups[1].Value,
                Name = match.Groups[2].Value.Trim(),
                IsActive = match.Groups[3].Success
            });
        }

        return list
            .GroupBy(p => p.Guid, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static string EmptyListHint() =>
        "Keine Energiepläne gelesen. Windows meldet die Pläne oft nur auf Deutsch/Englisch unterschiedlich — KlarWin sollte beides verstehen. " +
        "Falls die Liste leer bleibt: KlarWin neu starten oder als Administrator öffnen. Manuell prüfen: powercfg -list";

    public static bool ActivatePlan(string guid) =>
        ActivatePlanDetailed(guid).Success;

    public static PowerPlanActionResult ActivatePlanDetailed(string guid)
    {
        if (string.IsNullOrWhiteSpace(guid))
        {
            return new PowerPlanActionResult
            {
                Success = false,
                Message = "Energieplan: Keine Auswahl — bitte einen Plan in der Liste anhaken."
            };
        }

        guid = guid.Trim();
        var before = ListPlans();
        var target = before.FirstOrDefault(p =>
            string.Equals(p.Guid, guid, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            // Bekannte Standard-Pläne ggf. nachrüsten (häufig unter Win11 ausgeblendet).
            if (string.Equals(guid, HighPerformanceGuid, StringComparison.OrdinalIgnoreCase)
                || string.Equals(guid, UltimatePerformanceGuid, StringComparison.OrdinalIgnoreCase))
            {
                var installed = TryDuplicateScheme(guid);
                before = ListPlans();
                target = before.FirstOrDefault(p =>
                    string.Equals(p.Guid, guid, StringComparison.OrdinalIgnoreCase));
                if (target is null && installed is not null)
                {
                    target = installed;
                    guid = installed.Guid;
                }
            }
        }

        if (target is null)
        {
            var available = before.Count == 0
                ? "keine Pläne erkannt"
                : string.Join(", ", before.Select(p => p.Name));
            return new PowerPlanActionResult
            {
                Success = false,
                Message =
                    $"Energieplan: Gewählter Plan ({ShortGuid(guid)}) ist auf diesem PC nicht verfügbar. " +
                    $"Vorhanden: {available}. " +
                    "Tipp: Anderen Plan wählen oder als Administrator: powercfg -duplicatescheme 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"
            };
        }

        if (target.IsActive)
        {
            return new PowerPlanActionResult
            {
                Success = true,
                Message = $"Energieplan: „{target.Name}“ war bereits aktiv — nichts zu ändern."
            };
        }

        var (exit, stdout, stderr) = RunPowerCfgExitCapture($"-setactive {guid}");
        if (exit != 0)
        {
            (exit, stdout, stderr) = RunPowerCfgExitCapture($"/setactive {guid}");
        }

        if (exit != 0)
        {
            var detail = FirstUsefulLine(stderr, stdout);
            return new PowerPlanActionResult
            {
                Success = false,
                Message =
                    $"Energieplan: „{target.Name}“ konnte nicht aktiviert werden" +
                    (string.IsNullOrWhiteSpace(detail) ? "." : $": {detail}") +
                    " Oft hilft ein Start von KlarWin als Administrator (Rechtsklick → Als Administrator ausführen)."
            };
        }

        var after = ListPlans().FirstOrDefault(p => p.IsActive);
        if (after is not null &&
            string.Equals(after.Guid, guid, StringComparison.OrdinalIgnoreCase))
        {
            var previous = before.FirstOrDefault(p => p.IsActive)?.Name ?? "unbekannt";
            return new PowerPlanActionResult
            {
                Success = true,
                Message = $"Energieplan auf „{target.Name}“ gestellt (vorher: „{previous}“)."
            };
        }

        return new PowerPlanActionResult
        {
            Success = false,
            Message =
                $"Energieplan: Aktivierung von „{target.Name}“ wurde gemeldet, aktiv ist aber noch „{after?.Name ?? "unbekannt"}“. " +
                "Bitte powercfg -list prüfen oder KlarWin als Administrator starten."
        };
    }

    private static PowerPlanInfo? TryDuplicateScheme(string guid)
    {
        var (exit, stdout, stderr) = RunPowerCfgExitCapture($"-duplicatescheme {guid}");
        if (exit != 0)
        {
            (exit, stdout, stderr) = RunPowerCfgExitCapture($"/duplicatescheme {guid}");
        }

        if (exit != 0)
        {
            return null;
        }

        var match = PlanLine.Match(stdout + "\n" + stderr);
        if (match.Success)
        {
            return new PowerPlanInfo
            {
                Guid = match.Groups[1].Value,
                Name = match.Groups[2].Value.Trim(),
                IsActive = false
            };
        }

        return ListPlans().FirstOrDefault(p =>
            string.Equals(p.Guid, guid, StringComparison.OrdinalIgnoreCase));
    }

    private static string ShortGuid(string guid) =>
        guid.Length >= 8 ? guid[..8] + "…" : guid;

    private static string FirstUsefulLine(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            if (string.IsNullOrWhiteSpace(chunk)) continue;
            foreach (var line in chunk.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                return t.Length > 160 ? t[..160] + "…" : t;
            }
        }

        return "";
    }

    private static string PowerCfgPath() =>
        Path.Combine(Environment.SystemDirectory, "powercfg.exe");

    private static Encoding OemEncoding()
    {
        try
        {
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return Encoding.Default;
        }
    }

    private static string RunPowerCfgCapture(string arguments)
    {
        try
        {
            var oem = OemEncoding();
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = PowerCfgPath(),
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = oem,
                StandardErrorEncoding = oem
            });
            if (process is null) return "";
            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10000);
            return text;
        }
        catch
        {
            return "";
        }
    }

    private static (int ExitCode, string StdOut, string StdErr) RunPowerCfgExitCapture(string arguments)
    {
        try
        {
            var oem = OemEncoding();
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = PowerCfgPath(),
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = oem,
                StandardErrorEncoding = oem
            });
            if (process is null) return (1, "", "powercfg konnte nicht gestartet werden.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(10000);
            return (process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (1, "", ex.Message);
        }
    }
}
