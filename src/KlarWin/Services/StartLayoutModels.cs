namespace KlarWin.Services;

public sealed class MonitorInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string ManufacturerDisplay { get; init; } = "";
    public string ProductCode { get; init; } = "";
    public string Serial { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public bool IsPrimary { get; init; }
    public string DeviceName { get; init; } = "";
    public double DiagonalInches { get; init; }
    public int RefreshRateHz { get; init; }
    public string PositionLabel { get; init; } = "";
}

public sealed class WorkplaceContext
{
    public IReadOnlyList<MonitorInfo> Monitors { get; init; } = [];
    public string Gateway { get; init; } = "";
    public string WifiSsid { get; init; } = "";
    public string AdapterName { get; init; } = "";
}

public sealed class WorkplaceRoleRule
{
    /// <summary>primary | name:SUBSTRING | id:EXACT | index:0</summary>
    public string Match { get; set; } = "primary";
}

public sealed class StartLayoutApp
{
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string Role { get; set; } = "primary";
    public int DelayMs { get; set; } = 800;
    public bool Maximize { get; set; } = true;
}

public sealed class WorkplaceProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Arbeitsplatz";
    public List<string> NetworkTriggers { get; set; } = [];
    public List<string> MonitorFingerprints { get; set; } = [];
    public Dictionary<string, WorkplaceRoleRule> Roles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<StartLayoutApp> Apps { get; set; } = [];
}

public sealed class StartLayoutSettings
{
    /// <summary>User must opt in before Startlayout features run.</summary>
    public bool Enabled { get; set; }
    /// <summary>Always true while Enabled; kept for compatibility.</summary>
    public bool ApplyOnLogin { get; set; }
    public bool KlarWinAutostartManaged { get; set; }
    public string? LastProfileId { get; set; }
    public List<WorkplaceProfile> Profiles { get; set; } = [];
}

public sealed class InstalledAppEntry
{
    public string Name { get; init; } = "";
    public string ExePath { get; init; } = "";
    public string Source { get; init; } = "";
    public string Display => string.IsNullOrWhiteSpace(Source) ? Name : $"{Name}  · {Source}";
}
