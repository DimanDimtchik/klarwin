using System.Diagnostics.Eventing.Reader;

namespace KlarWin.Services;

public sealed record SystemReliabilityIssue(
    DateTime When,
    string Source,
    string Message);

public static class SystemReliabilityService
{
    private static readonly string[] RelevantProviders =
    [
        "disk", "Disk", "Ntfs", "ntfs", "partmgr", "stornvme", "storahci",
        "Microsoft-Windows-WHEA-Logger", "Microsoft-Windows-Kernel-Power"
    ];

    public static IReadOnlyList<SystemReliabilityIssue> ReadRecentIssues(int maxAgeDays = 7, int maxCount = 5)
    {
        var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays);
        var issues = new List<SystemReliabilityIssue>();
        try
        {
            var query = new EventLogQuery("System", PathType.LogName, "*[System[(Level=1 or Level=2)]]");
            using var reader = new EventLogReader(query);
            for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    if (record.TimeCreated is not DateTime when) continue;
                    if (when.ToUniversalTime() < cutoff) break;

                    var provider = record.ProviderName ?? "";
                    if (!RelevantProviders.Any(p => provider.Contains(p, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var message = record.FormatDescription()?.Trim();
                    if (string.IsNullOrWhiteSpace(message))
                    {
                        message = $"Ereignis-ID {record.Id}";
                    }

                    if (message.Length > 220)
                    {
                        message = message[..217] + "…";
                    }

                    issues.Add(new SystemReliabilityIssue(when, provider, message));
                    if (issues.Count >= maxCount) break;
                }
            }
        }
        catch
        {
            // Event log optional
        }

        return issues;
    }

    public static HealthLevel LevelForIssues(IReadOnlyList<SystemReliabilityIssue> issues) =>
        issues.Count switch
        {
            0 => HealthLevel.Ok,
            <= 2 => HealthLevel.Watch,
            _ => HealthLevel.Critical
        };
}
