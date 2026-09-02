using System.Diagnostics;
using System.Text.RegularExpressions;

namespace KlarWin.Services;

public sealed class PowerPlanInfo
{
    public string Guid { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsActive { get; init; }
}

public static class PowerPlanService
{
    private static readonly Regex PlanLine = new(
        @"Power Scheme GUID:\s*([0-9a-fA-F-]{36})\s+\((.+?)\)(\s+\*)?",
        RegexOptions.Compiled);

    public static IReadOnlyList<PowerPlanInfo> ListPlans()
    {
        var output = RunPowerCfg("/list");
        var list = new List<PowerPlanInfo>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = PlanLine.Match(line);
            if (!match.Success) continue;
            list.Add(new PowerPlanInfo
            {
                Guid = match.Groups[1].Value,
                Name = match.Groups[2].Value.Trim(),
                IsActive = match.Groups[3].Success
            });
        }

        return list;
    }

    public static bool ActivatePlan(string guid)
    {
        if (string.IsNullOrWhiteSpace(guid)) return false;
        return RunPowerCfgExit($"/setactive {guid}") == 0;
    }

    private static string RunPowerCfg(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) return "";
            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit(8000);
            return text;
        }
        catch
        {
            return "";
        }
    }

    private static int RunPowerCfgExit(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(8000);
            return process?.ExitCode ?? 1;
        }
        catch
        {
            return 1;
        }
    }
}
